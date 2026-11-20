namespace MeowFilm.AppHost;

public enum StartupOutcome
{
    Started,
    RetryableFailure,
    CleanupFailed,
    Cancelled
}

/// <summary>
/// Serial, explicitly retryable App startup. Owns cancellation, not App resources.
/// Only RetryableFailure certifies that the caller has made a new attempt safe.
/// Callbacks start on the calling thread and must return promptly; synchronous blocking
/// cannot be preempted. No callback or cancellation registration runs under the state lock.
/// </summary>
public sealed class AppStartup : IDisposable
{
    private sealed class Attempt
    {
        public CancellationTokenSource Source { get; } = new();
        public CancellationToken Token { get; }
        public TaskCompletionSource<StartupOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Attempt() => Token = Source.Token;
    }

    private static readonly Task<StartupOutcome> NotStarted = Task.FromResult(StartupOutcome.Cancelled);
    private readonly object gate = new();
    private Attempt? current;
    private StartupOutcome? outcome;
    private bool disposed;

    /// <summary>
    /// Repeated calls share the accepted attempt's Task. A new attempt is allowed only after
    /// an explicit RetryableFailure and before Stop/Dispose. Terminal or stopped controllers
    /// return the existing attempt (or Cancelled if never started), without invoking action.
    /// Unclassified faults/null/invalid results are CleanupFailed, never implicit permission
    /// to retry. Cancellation is terminal and is not a claim that resources were released.
    /// </summary>
    public Task<StartupOutcome> StartAsync(Func<CancellationToken, Task<StartupOutcome>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Attempt attempt;
        Attempt? previous;
        lock (gate)
        {
            if (disposed) return current?.Completion.Task ?? NotStarted;
            if (current is not null &&
                (!current.Completion.Task.IsCompleted || outcome != StartupOutcome.RetryableFailure))
                return current.Completion.Task;
            previous = current;
            current = attempt = new Attempt();
            outcome = null;
        }

        // A predecessor is settled and cannot be selected by Stop after this publication.
        previous?.Source.Dispose();
        Invoke(attempt, action);
        return attempt.Completion.Task;
    }

    private void Invoke(Attempt attempt, Func<CancellationToken, Task<StartupOutcome>> action)
    {
        try
        {
            attempt.Token.ThrowIfCancellationRequested();
            var raw = action(attempt.Token);
            if (raw is null)
            {
                Complete(attempt, StartupOutcome.CleanupFailed);
                return;
            }
            // Observe the original delegate Task directly, not just a dispatcher-bound wrapper.
            Observe(raw);
            _ = FinishAsync(attempt, raw);
        }
        catch (OperationCanceledException) { Complete(attempt, StartupOutcome.Cancelled); }
        catch (Exception) { Complete(attempt, StartupOutcome.CleanupFailed); }
    }

    private async Task FinishAsync(Attempt attempt, Task<StartupOutcome> raw)
    {
        StartupOutcome result;
        try
        {
            result = await raw.ConfigureAwait(false);
            if (!Enum.IsDefined(result)) result = StartupOutcome.CleanupFailed;
        }
        catch (OperationCanceledException) { result = StartupOutcome.Cancelled; }
        catch (Exception) { result = StartupOutcome.CleanupFailed; }
        Complete(attempt, result);
    }

    private void Complete(Attempt attempt, StartupOutcome result)
    {
        lock (gate)
        {
            // Closing wins over a late success/retry request, but never hides cleanup failure.
            if (disposed && result != StartupOutcome.CleanupFailed) result = StartupOutcome.Cancelled;
            if (ReferenceEquals(current, attempt)) outcome = result;
        }
        attempt.Completion.TrySetResult(result);
    }

    /// <summary>
    /// Permanently stops this controller without blocking. Does not dispose App resources.
    /// Its token source is released only after startup and cancellation registrations settle.
    /// </summary>
    public void Dispose()
    {
        Attempt? attempt;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            attempt = current;
        }
        if (attempt is not null) _ = ReleaseTokenAsync(attempt, attempt.Source.CancelAsync());
    }

    private static async Task ReleaseTokenAsync(Attempt attempt, Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch { /* A cancellation callback failure is not a resource-release receipt. */ }
        await attempt.Completion.Task.ConfigureAwait(false);
        attempt.Source.Dispose();
    }

    private static void Observe(Task raw) =>
        _ = raw.ContinueWith(static done => { _ = done.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

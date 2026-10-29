using System.Text.Json;

namespace MeowFilm.Core.Runtime;

/// <summary>Passive script port. Explicit recovery replaces only a failed transport;
/// it never replays a business operation or starts scripts during native navigation.</summary>
public sealed class OnDemandRuntimeClient : IRuntimeClient, IRuntimeRecovery, IAsyncDisposable
{
    private sealed class Epoch(long generation)
    {
        internal readonly long Generation = generation;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<IRuntimeSession> Start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Retirement;
    }
    private readonly Func<CancellationToken, Task<IRuntimeSession>> factory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly List<IRuntimeRecoveryScope> scopes = [];
    private Epoch? current;
    private Task? recovery, disposal;
    private bool closed, recoveryFailed;
    private long generation;

    public OnDemandRuntimeClient(Func<CancellationToken, Task<IRuntimeSession>> factory)
        => this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
    public bool HasStarted { get { lock (gate) return current is not null; } }
    public long Generation { get { lock (gate) return current?.Generation ?? 0; } }
    public RuntimeConnectionState State
    {
        get
        {
            lock (gate)
            {
                if (closed) return RuntimeConnectionState.Closed;
                if (recovery is { IsCompleted: false }) return RuntimeConnectionState.Recovering;
                if (recoveryFailed) return RuntimeConnectionState.Failed;
                if (current is null) return RuntimeConnectionState.Dormant;
                if (!current.Start.Task.IsCompleted) return RuntimeConnectionState.Starting;
                return current.Start.Task.IsCompletedSuccessfully && current.Start.Task.Result.IsConnected
                    ? RuntimeConnectionState.Ready : RuntimeConnectionState.Failed;
            }
        }
    }
    public bool CanRetry => State == RuntimeConnectionState.Failed;
    public void RegisterRecoveryScope(IRuntimeRecoveryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
        {
            if (closed) throw new BridgeException("CLOSED");
            if (current is not null || scopes.Count >= 32 || scopes.Any(item => ReferenceEquals(item, scope)))
                throw new BridgeException("INVALID_MESSAGE");
            scopes.Add(scope);
        }
    }
    public async Task<JsonElement> CallAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!BridgeCodec.ValidMethod(method)) throw new BridgeException("INVALID_MESSAGE");
        Epoch epoch; bool launch = false;
        lock (gate)
        {
            if (closed || recoveryFailed || recovery is { IsCompleted: false }) throw new BridgeException("CLOSED");
            if (current is null) { current = NewEpoch(); launch = true; }
            epoch = current;
        }
        if (launch) _ = StartAsync(epoch);
        // A cancelled caller cannot cancel the common factory for other callers.
        var session = await epoch.Start.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); CheckCurrent(epoch);
        var operation = session.CallAsync(method, parameters, cancellationToken);
        Observe(operation);
        var result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); CheckCurrent(epoch);
        return result;
    }
    private Epoch NewEpoch()
    {
        var epoch = new Epoch(checked(++generation)); Observe(epoch.Start.Task); return epoch;
    }
    private void CheckCurrent(Epoch epoch)
    {
        lock (gate)
            if (closed || recoveryFailed || recovery is { IsCompleted: false } || !ReferenceEquals(current, epoch))
                throw new BridgeException("CLOSED");
    }
    private async Task StartAsync(Epoch epoch)
    {
        try
        {
            var task = factory(epoch.Cancellation.Token) ?? throw new BridgeException("INTERNAL");
            Observe(task);
            var session = await task.ConfigureAwait(false) ?? throw new BridgeException("INTERNAL");
            epoch.Start.TrySetResult(session); // Retirement owns a late factory result.
        }
        catch (OperationCanceledException)
        { epoch.Start.TrySetException(new BridgeException(epoch.Cancellation.IsCancellationRequested ? "CLOSED" : "CANCELLED")); }
        catch (BridgeException error) { epoch.Start.TrySetException(new BridgeException(error.Code)); }
        catch { epoch.Start.TrySetException(new BridgeException("INTERNAL")); }
    }
    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task work; TaskCompletionSource? completion = null; Epoch? previous = null;
        IRuntimeRecoveryScope[] selected = [];
        lock (gate)
        {
            if (closed) throw new BridgeException("CLOSED");
            if (recovery is { IsCompleted: false }) work = recovery;
            else
            {
                if (!CanRetry || current is null) throw new BridgeException("INVALID_MESSAGE");
                previous = current; selected = scopes.ToArray();
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                recovery = work = completion.Task; Observe(work);
            }
        }
        if (completion is not null) _ = RecoverAsync(previous!, selected, completion);
        // Only the caller's wait is cancelled; retiring ownership is never abandoned.
        return cancellationToken.CanBeCanceled ? work.WaitAsync(cancellationToken) : work;
    }
    private async Task RecoverAsync(Epoch previous, IRuntimeRecoveryScope[] selected, TaskCompletionSource completion)
    {
        try
        {
            await RetireAsync(previous).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            foreach (var scope in selected)
                await scope.PrepareRuntimeRecoveryAsync(lifetime.Token).ConfigureAwait(false);
            Epoch next;
            lock (gate)
            {
                if (closed) throw new BridgeException("CLOSED");
                next = NewEpoch(); current = next;
            }
            await StartAsync(next).ConfigureAwait(false);
            var session = await next.Start.Task.ConfigureAwait(false);
            lock (gate)
            {
                if (closed || !session.IsConnected) throw new BridgeException("CLOSED");
                // Admission is reopened atomically with publishing the epoch.
                // Shutdown cannot win between the closed check and Resume.
                foreach (var scope in selected) scope.ResumeAfterRuntimeRecovery();
                recoveryFailed = false;
                completion.TrySetResult();
            }
        }
        catch (Exception error)
        {
            lock (gate) recoveryFailed = true;
            completion.TrySetException(new BridgeException(error is BridgeException known ? known.Code :
                lifetime.IsCancellationRequested ? "CLOSED" : "INTERNAL"));
        }
    }
    private Task RetireAsync(Epoch epoch)
    {
        lock (gate)
        {
            if (epoch.Retirement is not null) return epoch.Retirement;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            epoch.Retirement = completion.Task; Observe(completion.Task);
            _ = RetireCoreAsync(epoch, completion); return completion.Task;
        }
    }
    private static async Task RetireCoreAsync(Epoch epoch, TaskCompletionSource completion)
    {
        bool failed = false;
        try
        {
            try { await epoch.Cancellation.CancelAsync().ConfigureAwait(false); } catch { failed = true; }
            IRuntimeSession? session = null;
            try { session = await epoch.Start.Task.ConfigureAwait(false); }
            catch { /* A failed factory owns its partial initialization cleanup. */ }
            if (session is not null)
            { try { await session.DisposeAsync().ConfigureAwait(false); } catch { failed = true; } }
            if (failed) completion.TrySetException(new BridgeException("INTERNAL"));
            else completion.TrySetResult();
        }
        finally { epoch.Cancellation.Dispose(); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion; Task? recovering;
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true; recovering = recovery;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task; Observe(disposal);
        }
        _ = DisposeCoreAsync(recovering, completion); return new(disposal);
    }
    private async Task DisposeCoreAsync(Task? recovering, TaskCompletionSource completion)
    {
        bool failed = false;
        try
        {
            try { await lifetime.CancelAsync().ConfigureAwait(false); } catch { failed = true; }
            Epoch? epoch; lock (gate) epoch = current;
            Task? retiring = epoch is null ? null : RetireAsync(epoch);
            if (recovering is not null) { try { await recovering.ConfigureAwait(false); } catch { } }
            if (retiring is not null) { try { await retiring.ConfigureAwait(false); } catch { failed = true; } }
            lock (gate) scopes.Clear();
            if (failed) completion.TrySetException(new BridgeException("INTERNAL"));
            else completion.TrySetResult();
        }
        finally { lifetime.Dispose(); }
    }
    private static void Observe(Task task) => _ = task.ContinueWith(done => { _ = done.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}

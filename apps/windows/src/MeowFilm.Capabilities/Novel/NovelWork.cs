namespace MeowFilm.Capabilities;

/// <summary>Admission and physical drain for a domain owner's asynchronous operations.</summary>
internal sealed class NovelWork
{
    private readonly object gate = new();
    private CancellationTokenSource pending = new();
    private readonly HashSet<Task> operations = [];
    private bool paused;
    internal Task<T> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken token = default)
    {
        lock (gate)
        {
            if (paused) throw new InvalidOperationException("SHUTTING_DOWN");
            token.ThrowIfCancellationRequested();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, pending.Token);
            var receipt = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            operations.Add(receipt.Task);
            _ = Execute();
            return receipt.Task;
            async Task Execute()
            {
                T? result = default; Exception? failure = null;
                var cancellation = linked.Token;
                try { result = await action(cancellation); }
                catch (Exception error) { failure = error; }
                finally { linked.Dispose(); }
                // A receipt includes all of the operation's physical cleanup.
                lock (gate)
                {
                    operations.Remove(receipt.Task);
                    if (failure is OperationCanceledException) receipt.TrySetCanceled(cancellation);
                    else if (failure is not null) receipt.TrySetException(failure);
                    else receipt.TrySetResult(result!);
                }
            }
        }
    }
    internal async Task PauseAsync()
    {
        Task[] tasks; Task cancel;
        lock (gate) { paused = true; tasks = operations.ToArray(); cancel = pending.CancelAsync(); }
        try { await cancel; } catch (AggregateException) { }
        try { await Task.WhenAll(tasks); } catch { /* Each owner reports its operation failure. */ }
    }
    internal void Resume()
    {
        lock (gate) { if (!paused) return; pending.Dispose(); pending = new(); paused = false; }
    }
}

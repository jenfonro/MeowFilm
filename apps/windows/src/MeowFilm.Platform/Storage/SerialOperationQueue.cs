using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Storage;

/// <summary>
/// FIFO asynchronous operations. The owner controls admission and cancellation;
/// completion includes the entire operation, not just its caller's wait.
/// </summary>
public sealed class SerialOperationQueue
{
    private readonly object gate = new();
    private Task tail = Task.CompletedTask;

    public Task<T> Enqueue<T>(Func<Task<T>> operation, CancellationToken admission = default)
    {
        Task previous;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { previous = tail; tail = settled.Task; }
        var registration = admission.Register(() => result.TrySetCanceled(admission));
        _ = RunAsync();
        return result.Task;

        async Task RunAsync()
        {
            try
            {
                await previous.ConfigureAwait(false);
                registration.Dispose();
                if (result.Task.IsCanceled) return;
                admission.ThrowIfCancellationRequested();
                result.TrySetResult(await operation().ConfigureAwait(false));
            }
            catch (OperationCanceledException error) { result.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { result.TrySetException(error); }
            finally { registration.Dispose(); settled.TrySetResult(); }
        }
    }

    public Task Enqueue(Func<Task> operation) =>
        Enqueue(async () => { await operation().ConfigureAwait(false); return true; });

    public Task DrainAsync() { lock (gate) return tail; }
}

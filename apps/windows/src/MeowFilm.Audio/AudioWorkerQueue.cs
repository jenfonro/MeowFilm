namespace MeowFilm.Media;

/// <summary>
/// Dedicated bounded worker threads for blocking FFmpeg open/seek/probe/dispose operations,
/// isolating native blocking I/O from the shared .NET ThreadPool.
/// </summary>
internal static class AudioWorkerQueue
{
    private const int WorkerCount = 2;
    private static readonly object Gate = new();
    private static readonly Queue<Action> Queue = new();
    private static bool _started;

    internal static Task RunAsync(Action action) => RunAsync<object?>(() =>
    {
        action();
        return null;
    });

    internal static Task<T> RunAsync<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try { completion.TrySetResult(func()); }
            catch (OperationCanceledException canceled) { completion.TrySetCanceled(canceled.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    private static void Enqueue(Action work)
    {
        lock (Gate)
        {
            EnsureWorkersLocked();
            Queue.Enqueue(work);
            Monitor.Pulse(Gate);
        }
    }

    private static void EnsureWorkersLocked()
    {
        if (_started) return;
        _started = true;
        for (int i = 0; i < WorkerCount; i++)
        {
            var thread = new Thread(WorkerLoop)
            {
                Name = $"MeowFilm.AudioWorker-{i + 1}",
                IsBackground = true
            };
            thread.Start();
        }
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            Action work;
            lock (Gate)
            {
                while (Queue.Count == 0) Monitor.Wait(Gate);
                work = Queue.Dequeue();
            }
            work();
        }
    }
}

using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Threading;

/// <summary>Work admitted to an existing owner thread, including cancellation before execution.</summary>
public sealed class OwnerDispatcher
{
    private static readonly ConditionalWeakTable<DispatcherQueue, OwnerDispatcher> Owners = new();
    private readonly DispatcherQueue queue;
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<Task> running = [];
    private OwnerDispatcher(DispatcherQueue queue)
    {
        this.queue = queue;
        queue.ShutdownStarting += ShutdownStarting;
    }
    public static OwnerDispatcher Current => Owners.GetValue(
        DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("OWNER_QUEUE_UNAVAILABLE"),
        queue => new(queue));
    public bool HasThreadAccess => queue.HasThreadAccess;
    public bool IsClosing => shutdown.IsCancellationRequested;
    public void VerifyAccess()
    {
        if (!HasThreadAccess) throw new InvalidOperationException("OWNER_THREAD_ACCESS");
    }
    public Task EnqueueAsync(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal,
        CancellationToken cancellationToken = default) => EnqueueAsync(() => { action(); return true; }, priority, cancellationToken);

    public Task<T> EnqueueAsync<T>(Func<T> action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal,
        CancellationToken cancellationToken = default)
    {
        if (IsClosing) return Task.FromException<T>(new InvalidOperationException("OWNER_QUEUE_CLOSED"));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        return Run();
        async Task<T> Run()
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            int state = 0;
            using var registration = pending.Token.Register(() =>
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                    completion.TrySetCanceled(pending.Token);
            });
            if (Volatile.Read(ref state) == 0 && !queue.TryEnqueue(priority, () =>
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                try { completion.TrySetResult(action()); }
                catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
                catch (Exception error) { completion.TrySetException(error); }
            }))
                completion.TrySetException(new InvalidOperationException("OWNER_QUEUE_CLOSED"));
            return await completion.Task.ConfigureAwait(false);
        }
    }
    public Task EnqueueAsync(Func<Task> action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync<Task>(() => Track(action()), priority, cancellationToken).Unwrap();
    public Task<T> EnqueueAsync<T>(Func<Task<T>> action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync<Task<T>>(() => Track(action()), priority, cancellationToken).Unwrap();

    private async Task Track(Task task)
    {
        running.Add(task);
        try { await task; }
        finally { running.Remove(task); }
    }
    private async Task<T> Track<T>(Task<T> task)
    {
        running.Add(task);
        try { return await task; }
        finally { running.Remove(task); }
    }
    private async void ShutdownStarting(DispatcherQueue sender, DispatcherQueueShutdownStartingEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            shutdown.Cancel();
            try { await Task.WhenAll(running.ToArray()); }
            catch { /* Each accepted operation reports its own failure to its caller. */ }
        }
        finally { deferral.Complete(); }
    }
    public static Task YieldAsync(DispatcherQueuePriority priority = DispatcherQueuePriority.Low) =>
        Current.EnqueueAsync(() => { }, priority);
    public DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        VerifyAccess();
        var timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}

using MeowFilm.Threading;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Windows.Tests;

public static class OwnerDispatcherTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("owner-dispatcher.STA access async completion and exceptions", Completion);
        yield return new("owner-dispatcher.queued cancellation prevents execution", Cancellation);
        yield return new("owner-dispatcher.shutdown rejects admission and drains accepted async work", Shutdown);
        yield return new("owner-dispatcher.timer ticks on owner and stops", Timer);
    }

    private static async Task Completion()
    {
        await using var host = await OwnerTestQueue.CreateAsync();
        Check(!host.Owner.HasThreadAccess, "caller does not own the dedicated queue");
        try { host.Owner.VerifyAccess(); throw new Exception("wrong-thread access accepted"); }
        catch (InvalidOperationException error) when (error.Message == "OWNER_THREAD_ACCESS") { }
        int thread = await host.Owner.EnqueueAsync(async () =>
        {
            Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "STA owner");
            Check(ReferenceEquals(OwnerDispatcher.Current, host.Owner), "one dispatcher per queue");
            int before = Environment.CurrentManagedThreadId;
            await Task.Delay(10);
            Check(host.Owner.HasThreadAccess && Environment.CurrentManagedThreadId == before,
                "async continuation returns through the queue synchronization context");
            return before;
        });
        Check(thread != Environment.CurrentManagedThreadId, "execution belongs to owner");
        var canary = new InvalidOperationException("dispatch-canary");
        try { await host.Owner.EnqueueAsync(new Action(() => throw canary)); throw new Exception("failure lost"); }
        catch (InvalidOperationException error) when (ReferenceEquals(error, canary)) { }
        try
        {
            await host.Owner.EnqueueAsync(async () => { await Task.Yield(); throw canary; });
            throw new Exception("async failure lost");
        }
        catch (InvalidOperationException error) when (ReferenceEquals(error, canary)) { }
    }

    private static async Task Cancellation()
    {
        await using var host = await OwnerTestQueue.CreateAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = host.Owner.EnqueueAsync(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("dispatch blocker");
        });
        int calls = 0;
        using var cancel = new CancellationTokenSource();
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "owner blocked before admission");
            var queued = host.Owner.EnqueueAsync(() => Interlocked.Increment(ref calls), cancellationToken: cancel.Token);
            cancel.Cancel();
            await Cancelled(queued);
            Check(calls == 0 && !blocker.IsCompleted, "queued cancellation settles without owner execution");
        }
        finally { release.Set(); await blocker; }
        await host.Owner.EnqueueAsync(() => { }, DispatcherQueuePriority.Low);
        Check(calls == 0, "cancelled delegate remains inert after queue drains");
        using var runningCancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = host.Owner.EnqueueAsync(async () => { started.SetResult(); await finish.Task; },
            cancellationToken: runningCancel.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runningCancel.Cancel();
            Check(!operation.IsCompleted, "cancellation after admission cannot hide physical async work");
        }
        finally { finish.SetResult(); await operation; }
    }

    private static async Task Shutdown()
    {
        var host = await OwnerTestQueue.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool retired = false;
        await host.Owner.EnqueueAsync(() => host.Queue.ShutdownStarting += (_, _) => closing.TrySetResult());
        var accepted = host.Owner.EnqueueAsync(async () =>
        {
            started.SetResult();
            try { await release.Task; }
            finally { Check(host.Owner.HasThreadAccess, "retirement stays on owner"); retired = true; }
        });
        Task shutdown = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = host.DisposeAsync().AsTask();
            await closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(host.Owner.IsClosing && !accepted.IsCompleted && !shutdown.IsCompleted,
                "shutdown deferral retains admitted asynchronous work");
            try { await host.Owner.EnqueueAsync(() => { }); throw new Exception("closed queue accepted work"); }
            catch (InvalidOperationException error) when (error.Message == "OWNER_QUEUE_CLOSED") { }
        }
        finally { release.TrySetResult(); await accepted; await shutdown; await host.DisposeAsync(); }
        Check(retired && host.ThreadStopped.IsCompletedSuccessfully, "shutdown drains work and stops the owner thread");
    }

    private static async Task Timer()
    {
        await using var host = await OwnerTestQueue.CreateAsync();
        var ticked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int ticks = 0;
        var timer = await host.Owner.EnqueueAsync(() => host.Owner.CreateTimer(TimeSpan.FromMilliseconds(10), () =>
        {
            Check(host.Owner.HasThreadAccess, "timer owner affinity");
            ticks++;
            ticked.TrySetResult();
        }));
        try
        {
            Check(!timer.IsRunning, "new timer remains stopped until owner starts it");
            await host.Owner.EnqueueAsync(timer.Start);
            await ticked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await host.Owner.EnqueueAsync(timer.Stop); }
        int stoppedAt = await host.Owner.EnqueueAsync(() => ticks);
        await Task.Delay(40);
        Check(await host.Owner.EnqueueAsync(() => ticks) == stoppedAt, "stopped timer cannot tick again");
    }

    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("queued operation did not cancel");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Owner dispatcher: " + message); }
}

internal sealed class OwnerTestQueue : IAsyncDisposable
{
    private readonly DispatcherQueueController controller;
    internal DispatcherQueue Queue => controller.DispatcherQueue;
    internal OwnerDispatcher Owner { get; }
    internal Task ThreadStopped { get; }
    private Task? shutdown;

    private OwnerTestQueue(DispatcherQueueController controller, OwnerDispatcher owner, Task stopped)
    { this.controller = controller; Owner = owner; ThreadStopped = stopped; }

    internal static async Task<OwnerTestQueue> CreateAsync()
    {
        var ready = new TaskCompletionSource<OwnerTestQueue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var controller = DispatcherQueueController.CreateOnCurrentThread();
                var queue = controller.DispatcherQueue;
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                ready.SetResult(new(controller, OwnerDispatcher.Current, stopped.Task));
                queue.RunEventLoop();
                controller.ShutdownQueue();
                stopped.TrySetResult();
            }
            catch (Exception error) { ready.TrySetException(error); stopped.TrySetException(error); }
        }) { IsBackground = true, Name = "MeowFilm test owner" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal static async Task RunAsync(Func<Task> action, TimeSpan timeout)
    {
        await using var host = await CreateAsync();
        await host.Owner.EnqueueAsync(action).WaitAsync(timeout);
    }

    public ValueTask DisposeAsync() => new(shutdown ??= ShutdownAsync());
    private async Task ShutdownAsync()
    {
        if (!Queue.TryEnqueue(() => Queue.EnqueueEventLoopExit()) && !ThreadStopped.IsCompleted)
            throw new InvalidOperationException("TEST_OWNER_QUEUE_CLOSED");
        await ThreadStopped.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

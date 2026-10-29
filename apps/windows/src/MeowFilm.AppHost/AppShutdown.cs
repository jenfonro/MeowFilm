using MeowFilm.Desktop;

namespace MeowFilm.AppHost;

public sealed record ShutdownResource(Func<ValueTask> Release, bool InterruptsPendingOperations = false);

/// <summary>
/// Prepare all owners before releasing anything. A force deadline bounds only the
/// caller's wait: live owners keep their dependencies until actual drain succeeds.
/// Callbacks must return promptly and be safe to retry after a reported failure.
/// </summary>
public sealed class AppShutdown
{
    private sealed class Operation(Func<Task> action)
    {
        private readonly object gate = new();
        public Task? Task { get; private set; }
        public Task Start(bool retryFailure = false)
        {
            TaskCompletionSource completion;
            lock (gate)
            {
                if (Task is { } task && (!retryFailure || !task.IsCompleted || task.IsCompletedSuccessfully)) return task;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task = completion.Task;
                Observe(Task);
            }
            _ = RunAsync();
            return completion.Task;
            async Task RunAsync()
            {
                try
                {
                    var raw = action() ?? throw Failure();
                    Observe(raw);
                    await raw; // Preserve the caller's dispatcher for UI-affine lifecycle callbacks.
                    completion.TrySetResult();
                }
                catch { completion.TrySetException(Failure()); }
            }
        }
    }
    private sealed class Participant
    {
        public IDesktopShutdownParticipant Owner { get; }
        public Operation Prepare { get; private set; }
        public Operation Cancel { get; private set; }
        public Operation Dispose { get; }
        public Operation Abort { get; }
        public bool Released { get; set; }
        public Participant(IDesktopShutdownParticipant owner)
        {
            Owner = owner;
            Prepare = NewPrepare(); Cancel = NewCancel();
            Dispose = new(() => owner.DisposeAsync().AsTask());
            Abort = new(owner.AbortAsync);
        }
        private Operation NewPrepare() => new(Owner.PrepareShutdownAsync);
        private Operation NewCancel() => new(Owner.CancelShutdownPreparationAsync);
        public void ResetPreparation() { Prepare = NewPrepare(); Cancel = NewCancel(); }
    }
    private readonly object gate = new();
    private readonly Participant[] releaseOrder, prepareOrder;
    private readonly (Operation Release, bool Interrupts)[] resources;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan forceTimeout;
    private Task? closeTask, preparationTask, forceWork, forceWait, cancelTask;
    private volatile bool forced, releaseStarted, released;
    public bool IsReleased => released;

    public AppShutdown(IEnumerable<IDesktopShutdownParticipant> modules, IEnumerable<ShutdownResource> resources,
        TimeProvider? timeProvider = null, TimeSpan? forceTimeout = null,
        IEnumerable<IDesktopShutdownParticipant>? sharedParticipants = null)
    {
        var domains = modules?.ToArray() ?? throw new ArgumentNullException(nameof(modules));
        var shared = sharedParticipants?.ToArray() ?? [];
        var all = domains.Concat(shared).ToArray();
        if (all.Any(item => item is null) || all.Distinct(ReferenceEqualityComparer.Instance).Count() != all.Length)
            throw new ArgumentException("Invalid shutdown participants", nameof(modules));
        releaseOrder = all.Select(item => new Participant(item)).ToArray();
        prepareOrder = releaseOrder.Skip(domains.Length).Concat(releaseOrder.Take(domains.Length)).ToArray();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.forceTimeout = forceTimeout ?? TimeSpan.FromSeconds(5);
        if (this.forceTimeout <= TimeSpan.Zero || this.forceTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(forceTimeout));
        this.resources = (resources ?? throw new ArgumentNullException(nameof(resources))).Select(item =>
        {
            if (item?.Release is null) throw new ArgumentException("Invalid release callback", nameof(resources));
            return (new Operation(() => item.Release().AsTask()), item.InterruptsPendingOperations);
        }).ToArray();
    }

    public Task CloseAsync()
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (closeTask is not null) return closeTask;
            if (forced || cancelTask is { IsCompleted: false }) return Task.FromException(Failure());
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            closeTask = completion.Task; Observe(closeTask);
        }
        _ = CloseCoreAsync(completion);
        return completion.Task;
    }
    private async Task CloseCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            preparationTask = PrepareCoreAsync(); Observe(preparationTask);
            await preparationTask;
            if (forced) throw Failure();
            releaseStarted = true;
            foreach (var participant in releaseOrder)
            {
                if (forced) throw Failure();
                await participant.Dispose.Start();
                participant.Released = true;
            }
            foreach (var resource in resources)
            {
                if (forced) throw Failure();
                await resource.Release.Start();
            }
            released = true;
            completion.TrySetResult();
        }
        catch { completion.TrySetException(Failure()); }
    }
    private async Task PrepareCoreAsync()
    {
        try
        {
            foreach (var participant in prepareOrder)
            {
                if (forced) throw Failure();
                await participant.Prepare.Start();
            }
        }
        catch
        {
            foreach (var participant in prepareOrder.Reverse())
            {
                if (forced) break;
                if (participant.Prepare.Task is not null)
                    try { await participant.Cancel.Start(); } catch { /* Explicit cancellation can retry. */ }
            }
            throw Failure();
        }
    }

    /// <summary>Explicitly resumes a failed preparation; never resumes after release or force began.</summary>
    public Task CancelShutdownPreparationAsync()
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (forced || releaseStarted || closeTask is not { IsCompleted: true })
                return Task.FromException(Failure());
            if (cancelTask is { IsCompleted: false }) return cancelTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancelTask = completion.Task; Observe(cancelTask);
        }
        _ = CancelCoreAsync(completion);
        return completion.Task;
    }
    private async Task CancelCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            foreach (var participant in prepareOrder.Reverse())
            {
                if (forced) throw Failure();
                if (participant.Prepare.Task is not null)
                    await participant.Cancel.Start(retryFailure: true);
            }
            lock (gate)
            {
                if (forced) throw Failure();
                foreach (var participant in prepareOrder) participant.ResetPreparation();
                closeTask = preparationTask = null;
                completion.TrySetResult();
            }
        }
        catch { completion.TrySetException(Failure()); }
    }

    /// <summary>Only after explicit force or authorized failed-startup cleanup. Failure retains ownership.</summary>
    public Task ForceReleaseAsync()
    {
        TaskCompletionSource? workCompletion = null;
        TaskCompletionSource waitCompletion;
        lock (gate)
        {
            if (released) return Task.CompletedTask;
            if (forceWait is { IsCompleted: false }) return forceWait;
            forced = releaseStarted = true;
            if (forceWork is null || forceWork.IsFaulted || forceWork.IsCanceled)
            {
                workCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                forceWork = workCompletion.Task; Observe(forceWork);
            }
            waitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            forceWait = waitCompletion.Task; Observe(forceWait);
        }
        if (workCompletion is not null) _ = ForceCoreAsync(workCompletion);
        _ = WaitForForceAsync(forceWork!, waitCompletion);
        return waitCompletion.Task;
    }
    private async Task ForceCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            // Only explicitly interruptible resources (the script runtime) may
            // precede owner drain. Store/bridge/file ownership is never in this set.
            foreach (var resource in resources.Where(item => item.Interrupts))
                await resource.Release.Start(retryFailure: true);
            await SettleAsync(cancelTask);
            foreach (var participant in releaseOrder)
            {
                if (participant.Released) continue;
                await SettleAsync(participant.Cancel.Task);
                var abort = participant.Abort.Start(retryFailure: true);
                // Abort must interrupt, but all pre-existing calls must also
                // actually settle before dependencies can be released.
                await SettleAsync(participant.Prepare.Task);
                await SettleAsync(participant.Dispose.Task);
                await abort;
                participant.Released = true;
            }
            await SettleAsync(preparationTask);
            // A normal close may have been inside one disposal/resource callback.
            // Its forced fence prevents it starting subsequent teardown stages.
            await SettleAsync(closeTask);
            foreach (var resource in resources) await resource.Release.Start(retryFailure: true);
            released = true;
            completion.TrySetResult();
        }
        catch { completion.TrySetException(Failure()); }
    }
    private async Task WaitForForceAsync(Task work, TaskCompletionSource completion)
    {
        try { await work.WaitAsync(forceTimeout, timeProvider); completion.TrySetResult(); }
        catch { completion.TrySetException(Failure()); }
    }
    private static async Task SettleAsync(Task? task)
    {
        if (task is not null) { try { await task; } catch { /* Completion, not success, is the drain fence. */ } }
    }
    private static InvalidOperationException Failure() => new("SHUTDOWN_FAILED");
    private static void Observe(Task task) =>
        _ = task.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

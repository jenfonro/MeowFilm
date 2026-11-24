namespace MeowFilm.AppHost;

public sealed record ShutdownResource(Func<ValueTask> Release, bool InterruptsPendingOperations = false);

/// <summary>One-way retirement of an App generation. Stop executors and owners together;
/// release their borrowed resources only after all owners have stopped using them.</summary>
public sealed class AppShutdown
{
    private sealed class Release(Func<ValueTask> action)
    {
        private Task? work;
        internal Task Run() => work ??= InvokeAsync();
        private async Task InvokeAsync() => await action();
    }

    private readonly Release[] owners;
    private readonly (Release Release, bool Interrupts)[] resources;
    private Task? closing;
    public bool IsReleased { get; private set; }

    public AppShutdown(IEnumerable<IAsyncDisposable> modules, IEnumerable<ShutdownResource> resources,
        IEnumerable<IAsyncDisposable>? sharedParticipants = null)
    {
        var domains = modules?.ToArray() ?? throw new ArgumentNullException(nameof(modules));
        var shared = sharedParticipants?.ToArray() ?? [];
        var all = shared.Concat(domains).ToArray();
        if (all.Any(item => item is null) || all.Distinct(ReferenceEqualityComparer.Instance).Count() != all.Length)
            throw new ArgumentException("Invalid shutdown participants", nameof(modules));
        owners = all.Select(item => new Release(item.DisposeAsync)).ToArray();
        this.resources = (resources ?? throw new ArgumentNullException(nameof(resources))).Select(item =>
        {
            if (item?.Release is null) throw new ArgumentException("Invalid release callback", nameof(resources));
            return (new Release(item.Release), item.InterruptsPendingOperations);
        }).ToArray();
    }

    public Task CloseAsync()
    {
        if (closing is { IsCompletedSuccessfully: true } or { IsCompleted: false }) return closing;
        // Publish before invoking dispatcher-affine owners, which can reenter.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closing = completion.Task;
        _ = CloseCoreAsync(completion);
        return closing;
    }

    private async Task CloseCoreAsync(TaskCompletionSource completion)
    {
        Exception? firstFailure = null;
        try
        {
            // Drain business modules and shared owners FIRST while Node runtime is still alive so
            // playback progress flushes and session/media release RPCs complete cleanly.
            // If any owner stalls beyond 5s, trigger InterruptsPendingOperations resources to unblock it.
            var ownerTasks = owners.Select(owner => owner.Run()).ToArray();
            var allOwners = Task.WhenAll(ownerTasks);
            using var drainTimeout = new CancellationTokenSource();
            var completed = await Task.WhenAny(allOwners, Task.Delay(TimeSpan.FromSeconds(5), drainTimeout.Token));
            drainTimeout.Cancel();
            if (!ReferenceEquals(completed, allOwners))
            {
                var interrupts = resources.Where(item => item.Interrupts).Select(item => item.Release.Run()).ToArray();
                try { await Task.WhenAll(interrupts); } catch (Exception error) { firstFailure ??= error; }
            }
            try { await allOwners; } catch (Exception error) { firstFailure ??= error; }

            // Release all platform/runtime/database resources strictly in topological order.
            foreach (var resource in resources)
            {
                try { await resource.Release.Run(); }
                catch (Exception error) { firstFailure ??= error; }
            }

            IsReleased = true;
            if (firstFailure is not null) completion.TrySetException(firstFailure);
            else completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}

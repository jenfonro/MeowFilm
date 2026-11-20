namespace MeowFilm.AppHost;

public sealed record ShutdownResource(Func<ValueTask> Release, bool InterruptsPendingOperations = false);

/// <summary>One-way retirement of an App generation. Stop executors and owners together;
/// release their borrowed resources only after all owners have stopped using them.</summary>
public sealed class AppShutdown
{
    private sealed class Release(Func<ValueTask> action)
    {
        private Task? work;
        internal Task Run()
        {
            if (work is { IsCompletedSuccessfully: true } or { IsCompleted: false }) return work;
            return work = InvokeAsync();
        }
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
        try
        {
            // Shared input owners seal first, then every domain and executor is
            // stopped before waiting. A slow reader cannot postpone cancellation
            // of the executor that would otherwise keep that reader alive.
            var stopping = owners.Select(owner => owner.Run()).ToList();
            stopping.AddRange(resources.Where(item => item.Interrupts).Select(item => item.Release.Run()));
            await Task.WhenAll(stopping);
            foreach (var resource in resources.Where(item => !item.Interrupts)) await resource.Release.Run();
            IsReleased = true;
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}

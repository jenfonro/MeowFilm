using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class RuntimeShutdownConsumerTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (bool failed in new[] { false, true })
        foreach (bool racing in new[] { false, true })
        {
            bool failure = failed, race = racing;
            string suffix = (race ? "racing " : "") + (failure ? "failed" : "successful") + " physical runtime shutdown";
            yield return new("runtime shutdown consumer: music reset awaits " + suffix, () => Music(failure, race));
            yield return new("runtime shutdown consumer: novel image release awaits " + suffix, () => Novel(failure, race));
        }
        yield return new("runtime shutdown consumer: CLOSED without a physical receipt remains a music cleanup failure", UnacknowledgedMusic);
        yield return new("runtime shutdown consumer: CLOSED without a physical receipt remains a novel cleanup failure", UnacknowledgedNovel);
    }

    private static async Task Music(bool failed, bool racing)
    {
        await using var inner = new MusicPlatformRuntimeFixture
        { OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { })) };
        var runtime = new Runtime(inner);
        var client = new MusicPlatformClient(runtime);
        await client.RequestAsync("wy", "home", new { }, null, default);
        int before = inner.Calls.Count;
        var physical = Pending();
        if (racing) runtime.BeginShutdownOnCall = physical.Task;
        else runtime.ShutdownCompletion = physical.Task;
        var close = client.DisposeAsync().AsTask();
        try
        {
            await runtime.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted && ReferenceEquals(close, client.DisposeAsync().AsTask()));
            if (failed) physical.SetException(new BridgeException("INTERNAL"));
            else physical.SetResult();
            if (failed)
            {
                await BridgeTests.Expect("INTERNAL", () => close);
                await BridgeTests.Expect("INTERNAL", () => client.DisposeAsync().AsTask());
            }
            else await close;
            Check(inner.Calls.Count == before, "a retiring runtime receives no new session reset");
        }
        finally { physical.TrySetResult(); try { await close; } catch { } }
    }

    private static async Task Novel(bool failed, bool racing)
    {
        var inner = new NovelTests.Runtime();
        var runtime = new Runtime(inner);
        var sources = new NovelSourceService(runtime, inner.SourceStore);
        await sources.RefreshAsync();
        await sources.ImageAsync(sources.Sources.Single(), "https://fixture.invalid/cover.png",
            "https://fixture.invalid/book", "cover", default);
        int before = inner.Calls.Count;
        var physical = Pending();
        if (racing) runtime.BeginShutdownOnCall = physical.Task;
        else runtime.ShutdownCompletion = physical.Task;
        var close = sources.PrepareShutdownAsync();
        try
        {
            await runtime.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted);
            if (failed) physical.SetException(new BridgeException("INTERNAL"));
            else physical.SetResult();
            if (failed)
            {
                await BridgeTests.Expect("INTERNAL", () => close);
                await BridgeTests.Expect("INTERNAL", sources.PrepareShutdownAsync);
            }
            else await close;
            Check(inner.Calls.Count == before, "a retiring runtime receives no new image release");
        }
        finally { physical.TrySetResult(); try { await close; } catch { } }
    }

    private static async Task UnacknowledgedMusic()
    {
        await using var inner = new MusicPlatformRuntimeFixture
        { OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { })) };
        var runtime = new Runtime(inner);
        var client = new MusicPlatformClient(runtime);
        await client.RequestAsync("wy", "home", new { }, null, default);
        runtime.Unavailable = true;
        var close = client.DisposeAsync().AsTask();
        try { await close; throw new InvalidOperationException("CLOSED was mistaken for physical cleanup"); }
        catch (MusicPlatformException error) when (error.Code == "CLOSED") { }
        Check(ReferenceEquals(close, client.DisposeAsync().AsTask()) && close.IsFaulted);
    }

    private static async Task UnacknowledgedNovel()
    {
        var inner = new NovelTests.Runtime();
        var runtime = new Runtime(inner);
        var sources = new NovelSourceService(runtime, inner.SourceStore);
        await sources.RefreshAsync();
        await sources.ImageAsync(sources.Sources.Single(), "https://fixture.invalid/cover.png",
            "https://fixture.invalid/book", "cover", default);
        runtime.Unavailable = true;
        await BridgeTests.Expect("CLOSED", sources.PrepareShutdownAsync);
        Check(runtime.Rejected == 1 && inner.ImageReferences.Count == 1);
        await BridgeTests.Expect("CLOSED", sources.PrepareShutdownAsync);
        Check(runtime.Rejected == 2, "failed release retains the native reference for an explicit retry");
        runtime.Unavailable = false;
        await sources.PrepareShutdownAsync();
        Check(inner.ImageReferences.Count == 0, "a later acknowledged release retires the retained reference");
    }

    private sealed class Runtime(IRuntimeClient inner) : IRuntimeClient, IRuntimeLifetime
    {
        private Task? shutdown;
        internal readonly TaskCompletionSource Observed = Pending();
        public Task? ShutdownCompletion
        {
            get { if (shutdown is not null) Observed.TrySetResult(); return shutdown; }
            set => shutdown = value;
        }
        internal Task? BeginShutdownOnCall;
        internal bool Unavailable;
        internal int Rejected;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            if (BeginShutdownOnCall is { } shutdown) ShutdownCompletion = shutdown;
            if (Unavailable || ShutdownCompletion is not null)
            { Rejected++; throw new BridgeException("CLOSED"); }
            return inner.CallAsync(method, parameters, token);
        }
        public Task<JsonElement> UploadSourceAsync(string module, string id, long revision, ReadOnlyMemory<byte> bytes,
            string? name = null, CancellationToken token = default) => throw new InvalidOperationException("No source upload");
    }

    private static TaskCompletionSource Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message = "runtime shutdown consumer assertion")
    { if (!value) throw new InvalidOperationException(message); }
}

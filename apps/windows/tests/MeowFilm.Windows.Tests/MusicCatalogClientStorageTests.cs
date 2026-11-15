using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real native DPAPI/CAS and canonical platform results. Device identity and
// response-cookie persistence belong to the Node lifecycle tests.
public static class MusicCatalogClientStorageTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("catalog current repository and queue references survive DPAPI reopen", Reopen);
        yield return new("catalog cancelled home keeps admitted native metadata CAS owned", CancelHome);
        yield return new("catalog cancellation before admission leaves durable state unchanged", CancelBeforeAdmission);
        yield return new("catalog shutdown drains admitted native metadata CAS", Shutdown);
        yield return new("catalog lost native CAS receipt reconciles without replay", LostReceipt);
        yield return new("catalog departed home cannot publish and fresh read remains usable", () => ApplicationRead(false));
        yield return new("catalog closes and drains a home with no remaining subscribers", () => ApplicationRead(true));
    }
    private static string CreateRoot()
    {
        string root = TestWorkspace.DataPath("catalog-storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static JsonElement Ok(JsonElement value)
    {
        Check(value.GetProperty("ok").GetBoolean(), value.TryGetProperty("code", out var code) ? code.GetString()! : "Missing receipt");
        return value;
    }
    private static void Error(JsonElement value, params string[] codes) =>
        Check(!value.GetProperty("ok").GetBoolean() && codes.Contains(value.GetProperty("code").GetString()), "Expected actual failure receipt");
    private static JsonElement Song => MusicSongFixture.Element(MusicSongFixture.Online("wy", "123", "Synthetic track"));
    private static async Task Reopen()
    {
        string root = CreateRoot(), id;
        await using (var store = new Store(root))
        await using (var runtime = Runtime())
        await using (var app = new MusicNativeApplication(store, runtime))
        {
            var home = Ok(await app.ExecuteAsync("music.catalog.home", new { platform = "wy" }));
            var song = home.GetProperty("sections")[0].GetProperty("items")[0];
            id = song.GetProperty("songId").GetString()!;
            Check(Guid.TryParse(id, out _), "Catalog did not adopt a permanent identity");
            Ok(await app.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }));
            Check(runtime.Requests.Count == 1, "Repository write repeated a provider request");
        }
        await using var coldStore = new Store(root);
        await using var coldRuntime = Runtime();
        await using var cold = new MusicNativeApplication(coldStore, coldRuntime);
        var queue = Ok(await cold.ExecuteAsync("music.queue.get"));
        Check(queue.GetProperty("current").GetProperty("song").GetProperty("songId").GetString() == id &&
            queue.GetProperty("current").GetProperty("song").GetProperty("title").GetString() == "Synthetic track" &&
            coldStore.Writes == 0 && coldRuntime.Requests.Count == 0, "Current references were not restored locally");
    }
    private static async Task CancelHome()
    {
        await using var store = new Store(CreateRoot()) { Hold = true };
        await using var runtime = Runtime();
        await using var app = new MusicNativeApplication(store, runtime);
        using var cancellation = new CancellationTokenSource();
        var home = app.ExecuteAsync("music.catalog.home", new { platform = "wy" }, cancellation.Token);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Check(!home.IsCompleted, "Admitted CAS lost physical ownership");
        store.Release.TrySetResult(); Error(await home, "CANCELLED");
        Check(store.Writes == 1, "Cancellation retried or rolled back metadata");
        Ok(await app.ExecuteAsync("music.catalog.home", new { platform = "wy" }));
        Check(store.Writes == 1, "Readback repeated the durable write");
    }
    private static async Task CancelBeforeAdmission()
    {
        await using var store = new Store(CreateRoot());
        await using var runtime = Runtime();
        await using var app = new MusicNativeApplication(store, runtime);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Error(await app.ExecuteAsync("music.catalog.home", new { platform = "wy" }, cancellation.Token), "CANCELLED");
        var saved = await store.ReadAsync(MusicLibraryOwner.DocumentNamespace);
        try { Check(saved.Data is null && store.Writes == 0 && runtime.Requests.Count == 0, "Pre-admission cancellation changed durable state"); }
        finally { if (saved.Data is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task Shutdown()
    {
        string root = CreateRoot();
        await using (var store = new Store(root) { Hold = true })
        await using (var runtime = Runtime())
        await using (var app = new MusicNativeApplication(store, runtime))
        {
            var home = app.ExecuteAsync("music.catalog.home", new { platform = "wy" });
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = app.PrepareShutdownAsync(); Check(!close.IsCompleted, "Shutdown released admitted persistence");
            store.Release.TrySetResult(); await home; await close.WaitAsync(TimeSpan.FromSeconds(5));
            Check(store.Writes == 1 && store.Disposals == 0 && runtime.Disposals == 0, "Shutdown cancelled CAS or disposed borrowed owners");
        }
        await using var reopened = new Store(root);
        await using var peer = Runtime();
        await using var owner = new MusicNativeApplication(reopened, peer);
        Ok(await owner.ExecuteAsync("music.catalog.home", new { platform = "wy" }));
        Check(reopened.Writes == 0, "Reopen lost or replayed confirmed metadata");
    }
    private static async Task LostReceipt()
    {
        await using var store = new Store(CreateRoot()) { LoseReceipt = true };
        await using var runtime = Runtime();
        await using var library = new MusicLibraryOwner(store);
        await using var catalog = new MusicNativeCatalogOwner(runtime.Catalog, library);
        Error(await catalog.ExecuteAsync("music.catalog.home", new { platform = "wy" }), "COMMIT_UNKNOWN");
        Error(await catalog.ExecuteAsync("music.catalog.home", new { platform = "wy" }), "PENDING_WRITE");
        Check(store.Writes == 1, "Unknown CAS was automatically replayed");
        await library.ReconcileAsync();
        Ok(await catalog.ExecuteAsync("music.catalog.home", new { platform = "wy" }));
        Check(store.Writes == 1, "Authoritative reconciliation rewrote confirmed metadata");
    }
    private static async Task ApplicationRead(bool close)
    {
        await using var store = new Store(CreateRoot());
        await using var runtime = Runtime();
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        runtime.OnRequest = async (call, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await release.Task; return Home();
        };
        await using var app = new MusicNativeApplication(store, runtime);
        using var cancellation = new CancellationTokenSource();
        var home = app.ExecuteAsync("music.catalog.home", new { platform = "wy" }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = close ? app.PrepareShutdownAsync() : Task.CompletedTask;
            if (close) Check(!closing.IsCompleted, "Unsubscribed physical read was not drained");
            release.TrySetResult(); Error(await home, "CANCELLED", "STALE_SESSION", "CLOSED"); await closing;
            Check(store.Writes == 0, "Retired home adopted late metadata");
            if (!close)
            {
                runtime.OnRequest = (_, _) => Task.FromResult(Home());
                Ok(await app.ExecuteAsync("music.catalog.home", new { platform = "wy" }));
                Check(runtime.Requests.Count == 2 && store.Writes == 1, "Fresh home did not use current operation");
            }
        }
        finally { release.TrySetResult(); }
    }
    private static MusicPlatformRuntimeFixture Runtime()
    {
        var runtime = new MusicPlatformRuntimeFixture();
        runtime.OnRequest = (call, _) => call.Operation == "home" ? Task.FromResult(Home()) : throw new InvalidOperationException(call.Operation);
        return runtime;
    }
    private static JsonElement Home() => MusicPlatformRuntimeFixture.Json(new {
        platform = "wy", sections = new[] { new { id = "tracks", title = "Synthetic", type = "songs", items = new[] { Song } } },
        more = false, continuation = (object?)null });
    private sealed class Store(string root) : IDocumentStore
    {
        private readonly LocalDocumentStore inner = new(root, AppIdentity.NativeNamespaces, protectedNamespaces: AppIdentity.ProtectedNamespaces);
        internal int Writes, Disposals;
        internal bool Hold, LoseReceipt;
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) => inner.ReadAsync(ns, token);
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
        {
            Writes++; Entered.TrySetResult(); if (Hold) await Release.Task;
            var receipt = await inner.WriteAsync(ns, data, expected, token);
            if (LoseReceipt) { LoseReceipt = false; throw new DocumentStoreException(DocumentStoreError.Storage); }
            return receipt;
        }
        public async ValueTask DisposeAsync() { Release.TrySetResult(); Disposals++; await inner.DisposeAsync(); }
    }
}

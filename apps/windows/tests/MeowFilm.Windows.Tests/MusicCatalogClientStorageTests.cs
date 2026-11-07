using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real product-configured DPAPI storage with synthetic HTTP, never product data or live accounts.
public static class MusicCatalogClientStorageTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("catalog client home initialization and response update survive DPAPI reopen", Reopen);
        yield return new("catalog client cancelled home leaves owned CAS running and warms the next home", CancelHome);
        yield return new("catalog client cancellation before admission leaves durable state unchanged", CancelBeforeAdmission);
        yield return new("catalog client shutdown drains admitted CAS without an unknown receipt", Shutdown);
        yield return new("catalog client lost receipt still fences home until authoritative read without replay", LostReceipt);
        yield return new("catalog application departed home is shared and cached with real product storage", () => ApplicationRead(false));
        yield return new("catalog application closes and drains a home with no remaining subscribers", () => ApplicationRead(true));
    }

    private static async Task ApplicationRead(bool close)
    {
        await using var store = new Store(CreateRoot());
        var handler = new HomeHandler { Arrived = Signal(), Release = Signal() };
        await using var app = new MusicNativeApplication(store, new NoRuntime(), handler);
        using var cancellation = new CancellationTokenSource();
        var home = app.ExecuteAsync("music.catalog.home", new { platform = "wy" }, cancellation.Token);
        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            cancellation.Cancel();
            var cancelled = await home.WaitAsync(TimeSpan.FromSeconds(2));
            Check(cancelled.GetProperty("code").GetString() == "CANCELLED", "departed page was published");
            Check(!handler.Cancelled, "page token reached the owned transport");
            if (close)
            {
                var closing = app.PrepareShutdownAsync();
                try
                {
                    await Task.Delay(30);
                    Check(!closing.IsCompleted, "app released an unsubscribed physical read");
                }
                finally { handler.Release.TrySetResult(); }
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                var joined = app.ExecuteAsync("music.catalog.home", new { platform = "wy" });
                handler.Release.TrySetResult();
                var result = await joined.WaitAsync(TimeSpan.FromSeconds(5));
                Check(result.GetProperty("ok").GetBoolean(), "current subscriber did not receive the home");
                CheckHome(result);
                CheckHome(await app.ExecuteAsync("music.catalog.home", new { platform = "wy" }));
                Check(handler.Requests == 1, "same home was fetched again after unsubscribe");
            }
        }
        finally { handler.Release.TrySetResult(); }
    }

    private static async Task Reopen()
    {
        string root = CreateRoot();
        string? device = null;
        for (int pass = 0; pass < 2; pass++)
        {
            await using var store = new Store(root);
            await using var clients = new MusicPlatformClientContextOwner(store);
            using var handler = new HomeHandler();
            using var catalog = MusicPlatformCatalog.CreateWithHandler(new Anonymous(), handler, clients);
            CheckHome(await catalog.HomeAsync(new("wy")));
            var current = await clients.AcquireWyAsync();
            Check(current.Nmtid == HomeHandler.ClientCookie && current.Generation == 2, "response update not retained");
            Check(pass == 0 || device == current.DeviceId, "restart replaced the client identity");
            device = current.DeviceId;
            Check(store.Writes == (pass == 0 ? 2 : 0) && handler.Requests == 1, "unexpected persistence or HTTP replay");
            Check(!clients.NeedsReconciliation, "successful home left an unknown receipt");
        }
    }

    private static async Task CancelHome()
    {
        string root = CreateRoot();
        await using var store = new Store(root);
        await using var clients = new MusicPlatformClientContextOwner(store);
        await clients.InitializeAsync();
        using var handler = new HomeHandler();
        using var catalog = MusicPlatformCatalog.CreateWithHandler(new Anonymous(), handler, clients);
        using var cancellation = new CancellationTokenSource();
        store.WriteArrived = Signal();
        Task<JsonElement> home;
        using (HoldRoot(root))
        {
            home = catalog.HomeAsync(new("wy"), cancellation.Token);
            await store.WriteArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Error(() => home.WaitAsync(TimeSpan.FromSeconds(2)), "CANCELLED");
            Check(!catalog.DrainAsync().IsCompleted, "service lost ownership of the pending write");
        }
        await Error(() => home, "CANCELLED");
        await catalog.DrainAsync();
        Check(!clients.NeedsReconciliation, "cancelling a read poisoned later catalog requests");
        Check((await clients.AcquireWyAsync()).Nmtid == HomeHandler.ClientCookie, "admitted update did not settle");
        CheckHome(await catalog.HomeAsync(new("wy")));
        Check(store.Writes == 2 && handler.Requests == 1, "completed retired home was not reused from cache");
    }

    private static async Task CancelBeforeAdmission()
    {
        string root = CreateRoot();
        await using var store = new Store(root);
        await using var clients = new MusicPlatformClientContextOwner(store);
        var before = await clients.AcquireWyAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await clients.AcceptWyAsync(before, null, [HomeHandler.SetCookie], () => { }, cancellation.Token);
            throw new InvalidOperationException("cancelled response was admitted");
        }
        catch (OperationCanceledException) { }
        Check(store.Writes == 1 && !clients.NeedsReconciliation && clients.IsCurrent(before),
            "pre-admission cancellation changed client state");
        await clients.ReloadAsync();
        var current = await clients.AcquireWyAsync();
        Check(current.Generation == before.Generation && current.Nmtid is null && current.DeviceId == before.DeviceId,
            "pre-admission cancellation changed durable state");
    }

    private static async Task Shutdown()
    {
        string root = CreateRoot();
        await using (var store = new Store(root))
        await using (var clients = new MusicPlatformClientContextOwner(store))
        {
            var before = await clients.AcquireWyAsync();
            store.WriteArrived = Signal();
            Task<MusicWyClientContext> update;
            Task preparation;
            using (HoldRoot(root))
            {
                update = clients.AcceptWyAsync(before, null, [HomeHandler.SetCookie], () => { }, default);
                await store.WriteArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                preparation = clients.PrepareShutdownAsync();
                Check(!preparation.IsCompleted, "shutdown did not wait for admitted persistence");
            }
            await Error(() => update, "CLOSED");
            await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!clients.NeedsReconciliation && store.Writes == 2, "shutdown cancelled an admitted client write");
        }
        await using var reopenedStore = new Store(root);
        await using var reopened = new MusicPlatformClientContextOwner(reopenedStore);
        var restored = await reopened.AcquireWyAsync();
        Check(restored.Nmtid == HomeHandler.ClientCookie && restored.Generation == 2 && reopenedStore.Writes == 0,
            "shutdown lost or replayed a confirmed update");
    }

    private static async Task LostReceipt()
    {
        string root = CreateRoot();
        await using var store = new Store(root);
        await using var clients = new MusicPlatformClientContextOwner(store);
        await clients.InitializeAsync();
        using var handler = new HomeHandler();
        using var catalog = MusicPlatformCatalog.CreateWithHandler(new Anonymous(), handler, clients);
        store.LoseReceipt = true;
        await Error(() => catalog.HomeAsync(new("wy")), "COMMIT_UNKNOWN");
        Check(clients.NeedsReconciliation, "genuinely lost receipt was hidden");
        await Error(() => catalog.HomeAsync(new("wy")), "COMMIT_UNKNOWN");
        Check(store.Writes == 2 && handler.Requests == 1, "unknown receipt replayed a request or write");
        await clients.ReloadAsync();
        CheckHome(await catalog.HomeAsync(new("wy")));
        Check(!clients.NeedsReconciliation && store.Writes == 2 && handler.Requests == 2,
            "authority read did not settle the receipt without replay");
    }

    private static string CreateRoot()
    {
        string root = TestWorkspace.DataPath("catalog-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static FileStream HoldRoot(string root) =>
        new(Path.Combine(root, ".documents.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void CheckHome(JsonElement home)
    {
        Check(home.GetProperty("platform").GetString() == "wy", "wrong home source");
        var item = home.GetProperty("sections").EnumerateArray()
            .SelectMany(section => section.GetProperty("items").EnumerateArray()).Single();
        Check(item.GetProperty("id").GetString() == "123" && item.GetProperty("name").GetString() == "Synthetic playlist",
            "home did not publish the response content");
    }
    private static async Task Error(Func<Task> action, string code)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (MusicPlatformException error)
        {
            Check(error.Code == code, "Expected " + code + ", got " + error.Code);
            return;
        }
        throw new InvalidOperationException("Expected " + code);
    }

    private sealed class Anonymous : IMusicPlatformSessionSource
    {
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken) =>
            ValueTask.FromResult<MusicPlatformSession?>(null);
        public bool IsCurrent(MusicPlatformSession session) => false;
    }

    private sealed class HomeHandler : HttpMessageHandler
    {
        internal const string ClientCookie = "SYNTHETIC_HOME";
        internal const string SetCookie = "NMTID=" + ClientCookie + "; Domain=music.163.com";
        internal int Requests;
        internal TaskCompletionSource? Arrived, Release;
        internal bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            using var registration = token.Register(() => Cancelled = true);
            Arrived?.TrySetResult();
            if (Release is not null) await Release.Task;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"code":200,"data":{"hasMore":false,"blocks":[{"positionCode":"PLAYLIST",
                    "dslData":{"blockResource":{"header":{"title":"Synthetic recommendations"},
                    "items":[{"resourceType":"playlist","resourceId":"123","name":"Synthetic playlist"}]}}}]}}
                    """, Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("Set-Cookie", SetCookie);
            return response;
        }
    }

    private sealed class NoRuntime : IRuntimeClient
    {
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        { throw new InvalidOperationException("Native business must not upload script sources"); }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Native home must not start Node");
    }

    private sealed class Store(string root) : IDocumentStore
    {
        private readonly LocalDocumentStore inner = new(root, AppIdentity.NativeNamespaces,
            protectedNamespaces: AppIdentity.ProtectedNamespaces);
        internal int Writes;
        internal bool LoseReceipt;
        internal TaskCompletionSource? WriteArrived;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(ns, cancellationToken);
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            Writes++;
            var write = inner.WriteAsync(ns, data, expected, cancellationToken);
            WriteArrived?.TrySetResult();
            var receipt = await write;
            if (LoseReceipt)
            {
                LoseReceipt = false;
                throw new DocumentStoreException(DocumentStoreError.Storage);
            }
            return receipt;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

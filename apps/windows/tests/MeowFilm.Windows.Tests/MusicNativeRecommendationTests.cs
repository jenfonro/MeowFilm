using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real application/owner/catalog/coordinator/queue lane and temporary DPAPI.
// Only the upstream wire and deliberately uncooperative physical delays are synthetic.
public static class MusicNativeRecommendationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("single-binding-no-unbound-start-or-native-command-bypass", BindingBoundary);
        yield return Case("cold-saved-hydrate-fm-commits-before-one-media-callback-no-node", ColdStart);
        yield return Case("fm-choice-persists-across-restart-by-actual-local-account", FmMemory);
        yield return Case("fm-failed-or-retired-switch-does-not-overwrite-remembered-choice", FmMemoryFailure);
        yield return Case("get-and-events-are-native-safe-and-read-only", NativeState);
        yield return Case("identical-native-start-reuses-account-observation-session-and-provider-flight", () => NativeDuplicateStart(false));
        yield return Case("implicit-fm-start-reuses-the-committed-scene-without-reading-preferences-or-account", () => NativeDuplicateStart(true));
        yield return Case("narrow-prepare-drains-fm-preference-read-without-dispatching-retired-start", FmPreferenceReadDrain);
        yield return Case("narrow-prepare-drains-admitted-fm-preference-cas", FmPreferenceWriteDrain);
        yield return Case("similar-replaces-queue-and-has-no-checkpoint", SimilarReturn);
        yield return Case("heartbeat-has-no-unverified-continuation", Heartbeat);
        yield return Case("heartbeat-current-retains-native-occurrence-and-returns-original-queue", HeartbeatCurrent);
        yield return Case("heartbeat-browser-seed-from-empty-queue-restores-empty-checkpoint", () => HeartbeatBrowser(false));
        yield return Case("heartbeat-browser-seed-from-other-current-restores-original-checkpoint", () => HeartbeatBrowser(true));
        yield return Case("queue-mixed-platform-wy-seed-is-supported", () => QueueMixed(0));
        yield return Case("queue-mixed-platform-other-current-never-dispatches-http", () => QueueMixed(1));
        yield return Case("queue-fixed-seed-preview-choose-tail-and-live-feedback", QueuePreviewChoose);
        yield return Case("queue-disable-after-cas-admission-keeps-committed-facts-without-autoplay", QueueDisableCommit);
        yield return Case("queue-late-account-invalidated-preview-never-writes-or-selects", QueueAccountPreview);
        yield return Case("fm-real-tail-playback-and-natural-next-retain-live-feedback", Playback);
        foreach (string method in new[] { "append", "remove", "reorder", "mode", "select", "next", "previous", "step" })
        {
            string route = method;
            yield return Case("queue-" + route + "-uses-shared-lane-and-fences-old-selection", () => QueueMutation(route));
        }
        yield return Case("queue-replacement-fences-inflight-recommendation-before-final-commit", ReplaceDuringRead);
        yield return Case("queue-clear-and-history-restore-retire-recommendation", ClearAndHistory);
        yield return Case("catalog-prepare-does-not-retire-save-shares-lane-play-fences-final-cas", CatalogCommit);
        yield return Case("catalog-next-preserves-active-queue-and-persistence-fences-late-recommendation", () => NextDuringRecommendation(true));
        yield return Case("ordinary-next-append-preserves-active-queue-and-persistence-fences-late-recommendation", () => NextDuringRecommendation(false));
        foreach (int read in new[] { 1, 2 })
        {
            int number = read;
            yield return Case("narrow-prepare-drains-physical-account-read-" + number + "-then-resumes", () => AccountReadDrain(number));
        }
        yield return Case("cancelled-hydrate-waiter-detaches-from-shared-new-start", CancelHydrate);
        yield return Case("narrow-prepare-http-cancelled-waiter-is-not-physical-drain", HttpDrain);
        yield return Case("narrow-prepare-drains-http-response-async-disposal", ResponseDrain);
        yield return Case("narrow-prepare-drains-media-callback-but-keeps-history-writable", CallbackDrain);
        yield return Case("binding-dispose-immediately-fences-old-media-and-does-not-clear-replacement", BindingDisposal);
        yield return Case("account-notification-fences-media-before-catalog-cancellation", InvalidationOrder);
        yield return Case("auth-revokes-only-the-captured-current-native-session", AuthCurrent);
        yield return Case("late-old-auth-cannot-revoke-new-account-generation", AuthReplacement);
        yield return Case("anonymous-auth-does-not-prove-login-or-dispatch-http", Anonymous);
        yield return Case("admitted-queue-cas-drains-before-application-library-preparation", CasDrain);
        yield return Case("unknown-cas-reconcile-does-not-retry-or-start-late-media", UnknownCommit);
        foreach (int outcome in new[] { 1, 2 })
        {
            int result = outcome;
            yield return Case("unknown-settled-cas-" + result + "-prepare-and-dispose-fail-but-abort-releases", () => UnknownAbort(result));
        }
        foreach (string boundary in new[] { "http", "cas", "media" })
        {
            string owner = boundary;
            yield return Case("abort-waits-for-noncooperative-" + owner, () => AbortDrain(owner));
        }
        yield return Case("unknown-applied-cas-full-close-failure-cancel-reconcile-reopens-controls", () => UnknownCloseRecovery(true, 2));
        yield return Case("unknown-not-applied-cas-narrow-close-failure-cancel-reconcile-reopens-controls", () => UnknownCloseRecovery(false, 1));
        yield return Case("fresh-narrow-close-fences-late-unknown-reconciliation-resume", ReconcileCloseFence);
        yield return Case("full-prepare-resume-and-cold-reopen-do-not-revive-live-session", Reopen);
        yield return Case("immediate-full-prepare-and-resume-settle-without-mutual-wait", ImmediateResume);
        yield return Case("changed-observer-synchronous-close-does-not-wait-own-notification", ObserverClose);
    }

    private static TestCase Case(string name, Func<Task> run) => new("music.native.recommendations." + name, run);
    private static void Check(bool value, string message = "Native recommendation consumer invariant")
    { if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(8));
    private static string S(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static long N(JsonElement value, string name) => value.GetProperty(name).GetInt64();
    private static bool B(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Okay(JsonElement value)
    {
        Check(B(value, "ok"), "Command rejected: " + (value.TryGetProperty("code", out var c) ? c.GetString() : "INVALID_RESPONSE"));
        Check(!value.GetRawText().Contains("synthetic-account-key", StringComparison.Ordinal), "Credential escaped DTO");
        return value;
    }
    private static void Error(JsonElement value, string code) => Check(!B(value, "ok") && S(value, "code") == code, "Expected " + code);
    private static async Task Pending(Task task)
    { await Task.Delay(40); Check(!task.IsCompleted, "Returned before physical settlement"); }
    private static async Task Until(Func<Task<bool>> predicate)
    {
        for (int i = 0; i < 300; i++) { if (await predicate()) return; await Task.Delay(10); }
        throw new InvalidOperationException("Consumer checkpoint not reached");
    }
    private static async Task Cancelled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancelled wait");
    }
    private static JsonElement Song(int id) => MusicSongFixture.Element(MusicSongFixture.Online(
        "wy", id.ToString(), "Fixture " + id, "Synthetic", "Native fixture", 180));
    private static object WireSong(int id) => new
    {
        id, name = "Fixture " + id, ar = new[] { new { id = 8, name = "Synthetic" } },
        al = new { id = 9, name = "Native fixture" }, dt = 180000, alg = "synthetic-alg-" + id
    };

    private sealed class Barrier
    {
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal(), Cancelled = Signal(), Settled = Signal();
        internal Action? OnCancel;
        internal async Task Block(CancellationToken token)
        {
            using var registration = token.Register(() => { OnCancel?.Invoke(); Cancelled.TrySetResult(); });
            Entered.TrySetResult();
            try { await Release.Task.ConfigureAwait(false); }
            finally { Settled.TrySetResult(); }
        }
    }
    private sealed class Runtime : IRuntimeSession
    {
        public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Image requests are not part of this fixture.");

        internal int Calls, Disposals;
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Native business must not upload script sources"); }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Node must not execute native recommendation business"); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Store(LocalDocumentStore inner) : IDocumentStore
    {
        internal readonly ConcurrentDictionary<(string Namespace, int Number), Barrier> Holds = new();
        internal readonly ConcurrentDictionary<string, int> Reads = new(), Writes = new();
        internal readonly ConcurrentBag<Barrier> Barriers = [];
        internal int Disposals, UnknownLibraryWrite;
        internal Barrier HoldRead(string ns, int number)
        {
            var point = new Barrier(); Check(Holds.TryAdd((ns, number), point)); Barriers.Add(point); return point;
        }
        internal Barrier HoldWrite(string ns)
        {
            var point = new Barrier(); Check(Holds.TryAdd(("write:" + ns, Writes.GetValueOrDefault(ns) + 1), point));
            Barriers.Add(point); return point;
        }
        public async Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            int count = Reads.AddOrUpdate(ns, 1, (_, n) => n + 1);
            var document = await inner.ReadAsync(ns, token).ConfigureAwait(false);
            if (Holds.TryRemove((ns, count), out var point)) await point.Block(token).ConfigureAwait(false);
            return document;
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes,
            DocumentFingerprint expected, CancellationToken token = default)
        {
            int count = Writes.AddOrUpdate(ns, 1, (_, n) => n + 1);
            if (Holds.TryRemove(("write:" + ns, count), out var point)) await point.Block(token).ConfigureAwait(false);
            int unknown = ns == MusicLibraryOwner.DocumentNamespace ? Interlocked.Exchange(ref UnknownLibraryWrite, 0) : 0;
            if (unknown == 1) throw new IOException("synthetic lost acknowledgement");
            var value = await inner.WriteAsync(ns, bytes, expected, token).ConfigureAwait(false);
            if (unknown == 2) throw new IOException("synthetic lost acknowledgement");
            return value;
        }
        internal async Task SetAccount(long generation = 1, bool anonymous = false)
        {
            var previous = await inner.ReadAsync(MusicAccountOwner.DocumentNamespace);
            try
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = 1, entries = new[] { new {
                        key = "[\"meowfilm\",\"music\",1,\"platform-account\",\"wy\"]",
                        value = new {
                            version = 1, generation,
                            cookie = anonymous ? null : "MUSIC_U=synthetic-account-key-" + generation,
                            scope = anonymous ? null : "synthetic-scope-" + generation,
                            profile = anonymous ? null : new { platform = "wy", userId = (100 + generation).ToString(), displayName = "Saved fixture" },
                            validatedAt = anonymous ? (long?)null : 1000
                        }
                    } }
                });
                try { await inner.WriteAsync(MusicAccountOwner.DocumentNamespace, data, previous.Fingerprint); }
                finally { CryptographicOperations.ZeroMemory(data); }
            }
            finally { if (previous.Data is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
        }
        internal async Task<string> RawLibrary()
        {
            var value = await inner.ReadAsync(MusicLibraryOwner.DocumentNamespace);
            try { return value.Data is null ? "" : Encoding.UTF8.GetString(value.Data); }
            finally { if (value.Data is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
        }
        internal void ReleaseAll() { foreach (var point in Barriers) point.Release.TrySetResult(); }
        public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
    }
    private sealed class ResponseHold
    {
        internal readonly Barrier Read = new(), Dispose = new();
        internal bool Disposed;
    }
    private sealed class HeldStream(byte[] bytes, ResponseHold hold) : MemoryStream(bytes, writable: false)
    {
        private bool read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!read) { read = true; await hold.Read.Block(token).ConfigureAwait(false); }
            return await base.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask DisposeAsync()
        {
            await hold.Dispose.Block(CancellationToken.None).ConfigureAwait(false);
            hold.Disposed = true; await base.DisposeAsync().ConfigureAwait(false);
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<string> Paths = new();
        internal readonly ConcurrentBag<Barrier> Barriers = [];
        internal readonly ConcurrentBag<ResponseHold> Responses = [];
        internal Barrier? Next;
        internal ResponseHold? NextResponse;
        internal int Code = 200, Batch, Disposals;
        internal string ExpectedAccount = "synthetic-account-key-1";
        internal Barrier Hold() { var value = new Barrier(); Check(Next is null); Next = value; Barriers.Add(value); return value; }
        internal ResponseHold HoldResponse()
        { var value = new ResponseHold(); Check(NextResponse is null); NextResponse = value; Responses.Add(value); return value; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(Disposals == 0 && request.Method == HttpMethod.Post, "Unexpected HTTP method or lifetime");
            var uri = request.RequestUri!;
            Check(uri.Scheme == "https" && uri.IsDefaultPort && uri.Host is "interfacepc.music.163.com" or "music.163.com",
                "Unexpected fixed native upstream");
            string path = uri.AbsolutePath;
            Check(path is "/eapi/v1/radio/get" or "/weapi/v1/radio/get" or "/eapi/playmode/intelligence/list" or
                "/eapi/homepage/scene/more/rcmd/song" or "/eapi/v6/playlist/detail" or "/weapi/v3/song/detail" or "/weapi/user/playlist",
                "Unexpected native operation");
            Check(request.Headers.TryGetValues("Cookie", out var cookie) && string.Join(";", cookie).Contains(ExpectedAccount, StringComparison.Ordinal),
                "Wrong actual account lease");
            Paths.Enqueue(path); var hold = Interlocked.Exchange(ref Next, null);
            int code = Code;
            if (hold is not null) await hold.Block(token).ConfigureAwait(false);
            object result;
            if (code != 200) result = new { code };
            else if (path == "/weapi/user/playlist")
                result = new { code = 200, more = false, playlist = new[] {
                    new { id = 77, name = "Complete fixture", specialType = 5, creator = new { userId = 101 } } } };
            else if (path == "/eapi/v6/playlist/detail")
                result = new { code = 200, playlist = new { id = 77, name = "Complete fixture", trackCount = 3, specialType = 5,
                    creator = new { userId = 101 },
                    trackIds = new[] { new { id = 501 }, new { id = 502 }, new { id = 9 } }, updateTime = 1 } };
            else if (path == "/weapi/v3/song/detail")
                result = new { code = 200, songs = new[] { WireSong(501), WireSong(502), WireSong(9) } };
            else
            {
                int start = 100 * Interlocked.Increment(ref Batch);
                var songs = path == "/eapi/playmode/intelligence/list"
                    ? new[] { WireSong(501), WireSong(start + 2), WireSong(start + 3), WireSong(start + 4), WireSong(start + 5) }
                    : Enumerable.Range(start + 1, 3).Select(WireSong).ToArray();
                result = path switch
                {
                    "/eapi/playmode/intelligence/list" => new { code = 200, data = songs.Select(song => new { songInfo = song, source = "aiMode" }).ToArray() },
                    "/eapi/homepage/scene/more/rcmd/song" => new { code = 200, data = new { songList = songs,
                        sourceMap = Enumerable.Range(start + 1, 3).ToDictionary(id => id.ToString(), id => "native-source-" + id),
                        offset = start / 100 * 15, hasMore = true } },
                    _ => new { code = 200, data = songs }
                };
            }
            var response = Interlocked.Exchange(ref NextResponse, null);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result);
            return new(HttpStatusCode.OK) { Content = response is null ? new ByteArrayContent(bytes) : new StreamContent(new HeldStream(bytes, response)) };
        }
        internal void ReleaseAll()
        {
            foreach (var b in Barriers) b.Release.TrySetResult();
            foreach (var r in Responses) { r.Read.Release.TrySetResult(); r.Dispose.Release.TrySetResult(); }
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root;
        internal readonly Store Store;
        internal readonly Runtime Runtime = new();
        internal readonly Handler Handler = new();
        internal readonly MusicNativeApplication App;
        internal IMusicRecommendationSource Source => App;
        internal readonly ConcurrentQueue<MusicRecommendationSelection> Selections = new();
        internal readonly ConcurrentQueue<JsonElement> Notices = new();
        internal readonly ConcurrentBag<Barrier> Media = [];
        internal IDisposable? Binding;
        private Fixture(string? root)
        {
            string parent = Environment.GetEnvironmentVariable("MEOWFILM_NATIVE_RECOMMENDATIONS_DATA") ??
                throw new InvalidOperationException("Explicit isolated fixture directory required");
            Root = root ?? Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            Store = new(new LocalDocumentStore(Root, AppIdentity.NativeNamespaces, protectedNamespaces: AppIdentity.ProtectedNamespaces));
            App = new(Store, Runtime, Handler);
            Source.Changed += value => Notices.Enqueue(value.Clone());
        }
        internal static async Task<Fixture> Create(bool account = true, bool bind = true, string? root = null)
        {
            var result = new Fixture(root);
            if (account) await result.Store.SetAccount();
            if (bind) result.Bind();
            return result;
        }
        internal void Bind(Func<MusicRecommendationSelection, CancellationToken, Task<bool>>? callback = null)
        {
            Binding = Source.BindSelection(async (selection, token) =>
            {
                Selections.Enqueue(selection);
                if (!selection.IsCurrent()) return false;
                if (selection.Action == MusicRecommendationPlaybackAction.Select)
                {
                    var queue = await Queue();
                    Check(S(queue, "currentId") == selection.EntryId && N(queue, "version") == selection.QueueVersion,
                        "Media callback did not receive the already committed occurrence");
                }
                return callback is null ? true : await callback(selection, token);
            });
        }
        internal async Task<JsonElement> Call(string method, object? input = null) => Okay(await App.ExecuteAsync(method, input));
        internal Task<JsonElement> Queue() => Call("music.queue.get", new { offset = 0, limit = 100 });
        internal Task<JsonElement> State() => Call("music.recommendations.get");
        internal async Task<JsonElement> Start(object? input = null)
        {
            var value = JsonSerializer.SerializeToNode(input ?? new { kind = "fm", platform = "wy" })!.AsObject();
            if (value["kind"]!.GetValue<string>() == "queue" && value["seedEntryId"] is null)
                value["seedEntryId"] = Current(await Queue());
            return await Call("music.recommendations.start", value);
        }
        internal static string Current(JsonElement queue) => S(queue, "currentId");
        internal async Task<string> Tail()
        {
            var q = await Queue(); var tail = S(q.GetProperty("entries").EnumerateArray().Last(), "id");
            await Call("music.queue.select", new { id = tail });
            await Until(async () => S(await State(), "currentId") == tail);
            return tail;
        }
        internal Barrier HoldMedia()
        {
            Binding?.Dispose(); var hold = new Barrier(); Media.Add(hold);
            Bind(async (_, token) => { await hold.Block(token); return true; }); return hold;
        }
        public async ValueTask DisposeAsync()
        {
            Handler.ReleaseAll(); Store.ReleaseAll(); foreach (var point in Media) point.Release.TrySetResult();
            Binding?.Dispose();
            try
            {
                await App.DisposeAsync();
                Check(Store.Disposals == 0 && Runtime.Calls == 0 && Runtime.Disposals == 0,
                    "Native application used or disposed borrowed runtime/store");
            }
            finally { await Store.DisposeAsync(); }
        }
    }

    private static async Task BindingBoundary()
    {
        await using var f = await Fixture.Create(bind: false);
        Error(await f.App.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy" }), "PLAYBACK_UNAVAILABLE");
        Check(f.Store.Reads.Count == 0 && f.Handler.Paths.IsEmpty);
        f.Bind();
        try { f.Source.BindSelection((_, _) => Task.FromResult(true)); throw new InvalidOperationException("Duplicate media binding accepted"); }
        catch (MusicOperationException e) { Check(e.Code == "PLAYBACK_ALREADY_BOUND"); }
        Error(await f.App.ExecuteAsync("music.queue.recommendation.enter", new { }), "CAPABILITY_UNAVAILABLE");
        Check(f.Store.Writes.Count == 0);
    }
    private static async Task ColdStart()
    {
        await using var f = await Fixture.Create();
        var result = await f.Start(); Check(B(result, "active") && N(result, "batch") == 1 && S(result, "error") == "");
        var queue = await f.Queue(); Check(N(queue, "total") == 3 && f.Selections.Count == 1 && f.Handler.Paths.Count == 1);
        var account = (await f.Call("music.accounts.list")).GetProperty("items")[0];
        Check(S(account, "platform") == "wy" && S(account, "state") == "saved", "Disk hydration must not fabricate verified authentication");
        Check(f.Store.Writes.GetValueOrDefault(MusicAccountOwner.DocumentNamespace) == 0);
        Check(!((await f.Store.RawLibrary()).Contains("synthetic-alg-", StringComparison.Ordinal)), "Ephemeral algorithm persisted");
        Check(f.Source.GetFeedback(Fixture.Current(queue)) is { Kind: "fm", Platform: "wy" });
    }
    private static async Task NativeState()
    {
        await using var f = await Fixture.Create(); var started = await f.Start();
        await Until(() => Task.FromResult(f.Notices.Any(n => N(n, "batch") == 1 && !B(n, "loading"))));
        int calls = f.Handler.Paths.Count, writes = f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace);
        for (int i = 0; i < 3; i++) Check(S(await f.State(), "id") == S(started, "id"));
        Check(f.Handler.Paths.Count == calls && f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace) == writes);
        Check(f.Notices.All(n => !n.GetRawText().Contains("synthetic-account-key", StringComparison.Ordinal)));
        var revisions = f.Notices.Select(n => N(n, "revision")).ToArray();
        Check(revisions.Zip(revisions.Skip(1)).All(p => p.First < p.Second), "Changed revisions must be monotonic");
    }
    private static async Task NativeDuplicateStart(bool scene)
    {
        await using var f = await Fixture.Create();
        var first = scene ? await f.Start(new { kind = "fm", platform = "wy", mode = "scene", submode = "ELECTRONIC" }) :
            await f.Start();
        int accountReads = f.Store.Reads.GetValueOrDefault(MusicAccountOwner.DocumentNamespace);
        int preferenceReads = f.Store.Reads.GetValueOrDefault(MusicFmPreferences.DocumentNamespace);
        int preferenceWrites = f.Store.Writes.GetValueOrDefault(MusicFmPreferences.DocumentNamespace);
        int requests = f.Handler.Paths.Count, selections = f.Selections.Count;
        var duplicate = await f.Start();
        Check(S(duplicate, "id") == S(first, "id") && N(duplicate, "batch") == N(first, "batch"));
        Check(S(duplicate, "mode") == S(first, "mode") && S(duplicate, "submode") == S(first, "submode"));
        Check(f.Store.Reads.GetValueOrDefault(MusicAccountOwner.DocumentNamespace) == accountReads,
            "identical start repeated account hydration");
        Check(f.Store.Reads.GetValueOrDefault(MusicFmPreferences.DocumentNamespace) == preferenceReads &&
            f.Store.Writes.GetValueOrDefault(MusicFmPreferences.DocumentNamespace) == preferenceWrites,
            "identical start repeated preference storage work");
        Check(f.Handler.Paths.Count == requests && f.Selections.Count == selections,
            "identical start repeated provider or media work");
    }
    private static async Task FmPreferenceReadDrain()
    {
        await using var f = await Fixture.Create();
        var hold = f.Store.HoldRead(MusicFmPreferences.DocumentNamespace, 1);
        var start = f.App.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy" });
        await Wait(hold.Entered.Task);
        var preparing = f.Source.PrepareRecommendationsAsync();
        await Pending(preparing); await Pending(start);
        Check(!hold.Cancelled.Task.IsCompleted && f.Handler.Paths.IsEmpty);
        await f.Call("music.queue.mode", new { mode = 3 });
        hold.Release.TrySetResult();
        await Wait(preparing); Error(await start, "CANCELLED");
        Check(f.Handler.Paths.IsEmpty && f.Selections.IsEmpty &&
            f.Store.Writes.GetValueOrDefault(MusicFmPreferences.DocumentNamespace) == 0,
            "retired preference read cannot start HTTP, selection or a preference save");
        await f.App.CancelShutdownPreparationAsync();
        Check(B(await f.Start(), "active"));
    }
    private static async Task FmPreferenceWriteDrain()
    {
        await using var f = await Fixture.Create();
        var hold = f.Store.HoldWrite(MusicFmPreferences.DocumentNamespace);
        var start = f.Start(new { kind = "fm", platform = "wy", mode = "scene", submode = "ELECTRONIC" });
        await Wait(hold.Entered.Task);
        int selections = f.Selections.Count;
        var preparing = f.Source.PrepareRecommendationsAsync();
        await Pending(preparing); await Pending(start);
        Check(!hold.Cancelled.Task.IsCompleted);
        await f.Call("music.queue.mode", new { mode = 3 });
        hold.Release.TrySetResult();
        await Wait(preparing); await start;
        Check(f.Selections.Count == selections &&
            f.Store.Writes.GetValueOrDefault(MusicFmPreferences.DocumentNamespace) == 1);
        await f.App.CancelShutdownPreparationAsync();
        var restored = await f.Start();
        Check(S(restored, "mode") == "scene" && S(restored, "submode") == "ELECTRONIC" && N(await f.Queue(), "mode") == 3,
            "an admitted successful preference CAS remains valid after narrow shutdown resumes");
    }
    private static async Task FmMemory()
    {
        string root;
        await using (var first = await Fixture.Create())
        {
            root = first.Root;
            var initial = await first.Start(); Check(S(initial, "mode") == "default");
            await first.Call("music.queue.mode", new { mode = 3 });
            var chosen = await first.Start(new { kind = "fm", platform = "wy", mode = "scene", submode = "ELECTRONIC" });
            Check(S(chosen, "submode") == "ELECTRONIC" && N(await first.Queue(), "mode") == 3);
            await first.Call("music.recommendations.cancel", new { id = S(chosen, "id") });
            Check(S(await first.Start(), "submode") == "ELECTRONIC");
        }
        await using (var reopened = await Fixture.Create(account: false, root: root))
        {
            var restored = await reopened.Start();
            Check(S(restored, "mode") == "scene" && S(restored, "submode") == "ELECTRONIC" && N(await reopened.Queue(), "mode") == 3);
        }
        await using (var other = await Fixture.Create(account: false, root: root))
        {
            await other.Store.SetAccount(2); other.Handler.ExpectedAccount = "synthetic-account-key-2";
            var initial = await other.Start(); Check(S(initial, "mode") == "default" && S(initial, "submode") == "");
            await other.Start(new { kind = "fm", platform = "wy", mode = "scene", submode = "GAMES" });
        }
        await using (var original = await Fixture.Create(account: false, root: root))
        {
            await original.Store.SetAccount(1);
            var restored = await original.Start(); Check(S(restored, "submode") == "ELECTRONIC");
            Check(original.Runtime.Calls == 0);
        }
    }
    private static async Task FmMemoryFailure()
    {
        string root;
        await using (var f = await Fixture.Create())
        {
            root = f.Root;
            await f.Start(new { kind = "fm", platform = "wy", mode = "scene", submode = "ELECTRONIC" });
            f.Handler.Code = 500;
            var failed = await f.Start(new { kind = "fm", platform = "wy", mode = "explore" });
            Check(S(failed, "submode") == "ELECTRONIC" && S(failed, "error").Length > 0);
            f.Handler.Code = 200;
            var held = f.Handler.Hold();
            var switching = f.Start(new { kind = "fm", platform = "wy", mode = "scene", submode = "GAMES" });
            await Wait(held.Entered.Task);
            await f.Call("music.queue.replace", new { songs = new[] { Song(99) }, index = 0 });
            held.Release.TrySetResult(); await switching;
        }
        await using var reopened = await Fixture.Create(account: false, root: root);
        Check(S(await reopened.Start(), "submode") == "ELECTRONIC");
    }
    private static async Task SimilarReturn()
    {
        await using var f = await Fixture.Create();
        await f.Call("music.queue.replace", new { songs = new[] { Song(1), Song(2) }, index = 1 });
        await f.Call("music.queue.mode", new { mode = 3 });
        var original = await f.Queue();
        var started = await f.Start(new { kind = "similar", platform = "wy", sourceIds = new[] { "9" }, sourceType = "artist" });
        Check(B(started, "active") && !B(started, "canReturn") && B(started, "continue"));
        Check(N(await f.Queue(), "mode") == 3 && Fixture.Current(await f.Queue()) != Fixture.Current(original));
        Error(await f.App.ExecuteAsync("music.recommendations.return", new { id = S(started, "id") }), "CHECKPOINT_NOT_FOUND");
        Check(f.Selections.Count == 1 && f.Handler.Paths.Count == 1);
    }

    private static async Task Heartbeat()
    {
        await using var f = await Fixture.Create();
        var start = await f.Start(new { kind = "heartbeat", platform = "wy", entry = "favorites", songId = "501" });
        Check(B(start, "active") && !B(start, "continue") && N(await f.Queue(), "total") == 5);
        Check(MusicSongFixture.NativeId((await f.Queue()).GetProperty("current").GetProperty("song")) == "501");
        Check(f.Source.GetFeedback(Fixture.Current(await f.Queue())) is { FromHome: true, FeedbackSource: "aiMode", SourceId: "77" });
        string tail = await f.Tail(); await f.Source.OnPlaybackAsync(new(tail, true, true, true));
        Error(await f.App.ExecuteAsync("music.recommendations.continue", new { id = S(start, "id"), enabled = true }), "UNSUPPORTED");
        await f.Call("music.queue.mode", new { mode = 2 });
        Check(B(await f.State(), "active") && N(await f.Queue(), "mode") == 2);
        Check(!f.Handler.Paths.Contains("/eapi/homepage/scene/more/rcmd/song") &&
            f.Handler.Paths.Count(p => p == "/eapi/playmode/intelligence/list") == 1);
    }

    private static async Task HeartbeatCurrent()
    {
        await using var f = await Fixture.Create();
        await f.Call("music.queue.replace", new { songs = new[] { Song(9), Song(10) }, index = 0,
            context = new { sourceTitle = "Original", sourceType = "search", searchKeyword = "seed" } });
        var before = await f.Queue(); string seed = Fixture.Current(before);
        var start = await f.Start(new { kind = "heartbeat", platform = "wy", entry = "current", songId = "9", playlistId = "77", seedEntryId = seed });
        Check(N(await f.Queue(), "total") == 6 && Fixture.Current(await f.Queue()) == seed && f.Selections.IsEmpty);
        Check(f.Source.GetFeedback(S((await f.Queue()).GetProperty("entries")[1], "id")) is
            { FromHome: false, FeedbackSource: "aiMode", SourceId: "77" });
        Check(f.Handler.Paths.Count(p => p == "/weapi/user/playlist") == 1 && f.Handler.Paths.Count(p => p == "/eapi/playmode/intelligence/list") == 1);
        await f.Call("music.recommendations.return", new { id = S(start, "id") });
        Check((await f.Queue()).GetProperty("entries").GetRawText() == before.GetProperty("entries").GetRawText() &&
            Fixture.Current(await f.Queue()) == seed && f.Selections.IsEmpty);
    }
    private static async Task HeartbeatBrowser(bool existingQueue)
    {
        await using var f = await Fixture.Create();
        if (existingQueue)
            await f.Call("music.queue.replace", new { songs = new[] { Song(9), Song(10) }, index = 1,
                context = new { sourceTitle = "Original", sourceType = "search", searchKeyword = "before browser seed" } });
        var before = await f.Queue();
        var start = await f.Start(new { kind = "heartbeat", platform = "wy", entry = "current", songId = "501", playlistId = "77" });
        var after = await f.Queue();
        Check(S(start, "error") == "" && B(start, "canReturn") && !B(start, "continue") &&
            N(after, "total") == 5 && Fixture.Current(after) != Fixture.Current(before));
        Check(f.Selections.Single() is { Play: true, Automatic: false } &&
            f.Source.GetFeedback(Fixture.Current(after)) is { FromHome: false, FeedbackSource: "aiMode", SourceId: "77" });
        Check(f.Handler.Paths.Count(p => p == "/weapi/user/playlist") == 1 &&
            f.Handler.Paths.Count(p => p == "/eapi/playmode/intelligence/list") == 1);
        await f.Call("music.recommendations.return", new { id = S(start, "id") });
        var restored = await f.Queue();
        var original = before.GetProperty("entries").EnumerateArray().ToList();
        original.Insert(existingQueue ? 2 : 0, after.GetProperty("current"));
        Check(Fixture.Current(restored) == Fixture.Current(after) &&
            restored.GetProperty("entries").EnumerateArray().Select(e => e.GetRawText()).SequenceEqual(original.Select(e => e.GetRawText())) &&
            restored.GetProperty("source").GetRawText() == before.GetProperty("source").GetRawText());
    }
    private static async Task QueueMixed(int index)
    {
        await using var f = await Fixture.Create();
        await f.Call("music.queue.replace", new { songs = new[] { Song(9),
            MusicSongFixture.Element(MusicSongFixture.Online("tx", "qq123")) }, index });
        var before = await f.Queue(); int writes = f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace);
        var state = await f.Start(new { kind = "queue", platform = "wy" }); var after = await f.Queue();
        Check((index == 1 ? S(state, "error") == "NATIVE_ID_REQUIRED" && f.Handler.Paths.IsEmpty &&
                state.GetProperty("candidates").GetArrayLength() == 0 : S(state, "error") == "" &&
                S(state, "seedSongId") == "9" && state.GetProperty("candidates").GetArrayLength() == 3) && f.Selections.IsEmpty &&
            f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace) == writes);
        Check(N(after, "version") == N(before, "version") && Fixture.Current(after) == Fixture.Current(before) &&
            after.GetProperty("entries").GetRawText() == before.GetProperty("entries").GetRawText() &&
            after.GetProperty("source").GetRawText() == before.GetProperty("source").GetRawText());
    }
    private static async Task QueuePreviewChoose()
    {
        await using var f = await Fixture.Create();
        await f.Call("music.queue.replace", new { songs = new[] { Song(9) }, index = 0,
            context = new { sourceTitle = "Original search", sourceType = "search", searchKeyword = "original",
                queueSource = new { id = "search", title = "Original search", kind = "search", platform = "wy" } } });
        var before = await f.Queue(); int writes = f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace);
        var preview = await f.Start(new { kind = "queue", platform = "wy" });
        Check(preview.GetProperty("candidates").GetArrayLength() == 3 && B(preview, "continue") && N(await f.Queue(), "total") == 1 &&
            f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace) == writes && f.Selections.IsEmpty);
        Check(f.Source.GetFeedback(Fixture.Current(before)) is null);
        await f.Call("music.recommendations.choose", new { id = S(preview, "id"), candidateId = S(preview.GetProperty("candidates")[1], "id") });
        var chosen = await f.Queue();
        Check(N(chosen, "total") == 4 && MusicSongFixture.NativeId(chosen.GetProperty("current").GetProperty("song")) == "102");
        Check(S(chosen.GetProperty("source"), "kind") == "queue" &&
            chosen.GetProperty("entries")[0].GetRawText() == before.GetProperty("entries")[0].GetRawText());
        Check(f.Selections.Single() is { Play: true, Automatic: false } &&
            f.Source.GetFeedback(Fixture.Current(chosen)) is { Kind: "queue", Provider: "queue-recommendations", FeedbackSource: "native-source-102" });
        string tail = await f.Tail(); await f.Source.OnPlaybackAsync(new(tail, true, true, true));
        Check(f.Handler.Paths.Count(p => p == "/eapi/homepage/scene/more/rcmd/song") == 2);
        Check(N(await f.Queue(), "total") == 7 && Fixture.Current(await f.Queue()) == tail && f.Selections.Count == 1 &&
            f.Handler.Paths.Count(p => p == "/eapi/homepage/scene/more/rcmd/song") == 2);
    }
    private static async Task QueueAccountPreview()
    {
        await using var f = await Fixture.Create();
        await f.Call("music.queue.replace", new { songs = new[] { Song(9) }, index = 0 });
        var hold = f.Handler.Hold(); var start = f.Start(new { kind = "queue", platform = "wy" });
        await Wait(hold.Entered.Task);
        int writes = f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace);
        var logout = f.App.ExecuteAsync("music.accounts.logout", new { platform = "wy" });
        await Until(async () => !B(await f.State(), "active"));
        hold.Release.TrySetResult(); await start; await logout;
        Check(N(await f.Queue(), "total") == 1 && f.Selections.IsEmpty && (await f.State()).GetProperty("candidates").GetArrayLength() == 0 &&
            f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace) == writes);
    }
    private static async Task QueueDisableCommit()
    {
        await using var f = await Fixture.Create();
        await f.Call("music.queue.replace", new { songs = new[] { Song(9) }, index = 0 });
        var start = await f.Start(new { kind = "queue", platform = "wy" }); string id = S(start, "id");
        await f.Call("music.recommendations.choose", new { id, candidateId = S(start.GetProperty("candidates")[0], "id") });
        await f.Call("music.recommendations.continue", new { id, enabled = false });
        string tail = await f.Tail(); await f.Source.OnPlaybackAsync(new(tail, true, true, true));
        var hold = f.Store.HoldWrite(MusicLibraryOwner.DocumentNamespace);
        var enable = f.Call("music.recommendations.continue", new { id, enabled = true }); await Wait(hold.Entered.Task);
        var disable = f.Call("music.recommendations.continue", new { id, enabled = false });
        await Until(async () => !B(await f.State(), "continue")); await disable;
        hold.Release.TrySetResult(); await enable; var state = await f.State(); var queue = await f.Queue();
        Check(N(queue, "total") == 7 && Fixture.Current(queue) == tail && N(state, "count") == 7 &&
            N(state, "queueVersion") == N(queue, "version") && f.Selections.Count == 1 && !B(state, "continue"));
        Check(f.Source.GetFeedback(S(queue.GetProperty("entries").EnumerateArray().Last(), "id")) is
            { Kind: "queue", Provider: "queue-recommendations", FeedbackSource: "native-source-203" });
    }
    private static async Task Playback()
    {
        await using var f = await Fixture.Create(); await f.Start();
        for (int i = 0; i < 2; i++)
        {
            Check(!await f.Source.TryAdvanceAsync(Fixture.Current(await f.Queue()), false, true));
            await f.Call("music.queue.next");
        }
        string tail = await f.Tail();
        await f.Source.OnPlaybackAsync(new(tail, false, true, true)); await f.Source.OnPlaybackAsync(new(tail, true, false, true));
        await f.Source.OnPlaybackAsync(new(tail, true, true, false)); Check(f.Handler.Paths.Count == 1);
        await f.Source.OnPlaybackAsync(new(tail, true, true, true)); Check(N(await f.Queue(), "total") == 6);
        Check(!await f.Source.TryAdvanceAsync(tail, true, true));
        var next = await f.Call("music.queue.next", new { reason = "ended" });
        Check(f.Source.GetFeedback(S(next, "targetId"))?.BatchId.EndsWith(":2", StringComparison.Ordinal) == true && f.Selections.Count == 1);
        await f.Source.OnPlaybackAsync(new(S(next, "targetId"), true, true, true, IsLocal: false));
        Check(!B(await f.State(), "active") && f.Source.GetFeedback(S(next, "targetId")) is null);
    }

    private static async Task QueueMutation(string route)
    {
        await using var f = await Fixture.Create(); await f.Start();
        var prior = f.Selections.Last(); var queue = await f.Queue();
        string[] ids = queue.GetProperty("entries").EnumerateArray().Select(r => S(r, "id")).ToArray();
        object input = route switch
        {
            "append" => new { songs = new[] { Song(901) } },
            "remove" => new { id = ids[^1] },
            "reorder" => new { ids = ids.Reverse().ToArray() },
            "mode" => new { mode = 1 },
            "select" => new { id = ids[1] },
            "step" => new { direction = 1, mode = 1 },
            _ => new { }
        };
        await f.Call("music.queue." + route, input);
        Check(prior.IsCurrent() == (route == "mode"), "Loop changes must preserve current media; queue navigation must fence it");
        Check(B(await f.State(), "active"), "Ordinary queue navigation/edit must not discard live session");
    }
    private static async Task ReplaceDuringRead()
    {
        await using var f = await Fixture.Create(); var hold = f.Handler.Hold();
        var starting = f.Start(); await Wait(hold.Entered.Task);
        await f.Call("music.queue.replace", new { songs = new[] { Song(901) }, index = 0 });
        await Pending(starting); Check(!hold.Cancelled.Task.IsCompleted); hold.Release.TrySetResult();
        await starting; Check(N(await f.Queue(), "total") == 1 && f.Selections.IsEmpty && !B(await f.State(), "active"));
    }
    private static async Task ClearAndHistory()
    {
        await using var f = await Fixture.Create();
        await f.Call("music.queue.replace", new { songs = new[] { Song(1), Song(2) }, index = 0 });
        await f.Start(); var old = f.Selections.Last();
        var history = await f.Call("music.history.list", new { kind = "playlists", offset = 0, limit = 10 });
        string id = S(history.GetProperty("items")[0], "id");
        await f.Call("music.history.restorePlaylist", new { id });
        Check(!old.IsCurrent() && !B(await f.State(), "active") && N(await f.Queue(), "total") == 2);
        await f.Start(); old = f.Selections.Last(); await f.Call("music.queue.clear");
        Check(!old.IsCurrent() && !B(await f.State(), "active") && N(await f.Queue(), "total") == 0);
    }
    private static async Task CatalogCommit()
    {
        await using var f = await Fixture.Create(); await f.Start(); var old = f.Selections.Last();
        var stage = await f.Call("music.catalog.playlist.prepare", new { platform = "wy", id = "77" });
        Check(old.IsCurrent() && B(await f.State(), "active"));
        await f.Call("music.catalog.playlist.commit", new { token = S(stage, "token"), action = "save" });
        Check(old.IsCurrent() && B(await f.State(), "active") && N(await f.Queue(), "total") == 3);
        stage = await f.Call("music.catalog.playlist.prepare", new { platform = "wy", id = "77" });
        var hold = f.Store.HoldWrite(MusicLibraryOwner.DocumentNamespace); long version = N(await f.Queue(), "version");
        var commit = f.Call("music.catalog.playlist.commit", new { token = S(stage, "token"), action = "play", index = 1, expectedVersion = version });
        await Wait(hold.Entered.Task); Check(!old.IsCurrent(), "Final catalog CAS bypassed queue lane"); hold.Release.TrySetResult();
        await commit; Check(N(await f.Queue(), "total") == 3 && !B(await f.State(), "active"));
    }
    private static async Task NextDuringRecommendation(bool catalog)
    {
        await using var f = await Fixture.Create();
        var started = await f.Start(); string session = S(started, "id");
        var prior = f.Selections.Last(); var before = await f.Queue();
        string? stage = catalog ? S(await f.Call("music.catalog.playlist.prepare", new { platform = "wy", id = "77" }), "token") : null;
        Check(prior.IsCurrent() && B(await f.State(), "active"));
        var late = f.Handler.Hold();
        var retry = f.Call("music.recommendations.retry", new { id = session });
        await Wait(late.Entered.Task);
        int selections = f.Selections.Count, writes = f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace);
        var durable = f.Store.HoldWrite(MusicLibraryOwner.DocumentNamespace);
        var append = catalog
            ? f.Call("music.catalog.playlist.commit", new { token = stage, action = "next", expectedVersion = N(before, "version") })
            : f.Call("music.queue.append", new { songs = new[] { Song(501), Song(502), Song(9) }, next = true });
        await Wait(durable.Entered.Task);
        Check(!prior.IsCurrent(), "Next edit must fence the old media selection before durable completion");
        durable.Release.TrySetResult(); var result = await append;
        var after = await f.Queue();
        Check(S(result, "currentId") == Fixture.Current(before) && Fixture.Current(after) == Fixture.Current(before));
        Check(N(after, "mode") == N(before, "mode") && N(after, "version") == N(before, "version") + 1);
        Check(N(after, "total") == 6 && f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace) == writes + 1);
        var original = before.GetProperty("entries").EnumerateArray().ToArray();
        var entries = after.GetProperty("entries").EnumerateArray().ToArray();
        Check(entries[0].GetRawText() == original[0].GetRawText() &&
            entries[4].GetRawText() == original[1].GetRawText() && entries[5].GetRawText() == original[2].GetRawText(),
            "Next must preserve original occurrences around one contiguous inserted segment");
        Check(entries.Skip(1).Take(3).Select(e => MusicSongFixture.NativeId(e.GetProperty("song"))).SequenceEqual(new[] { "501", "502", "9" }));
        Check(entries.Select(e => S(e, "id")).Distinct().Count() == 6 && f.Selections.Count == selections);
        await Until(async () => N(await f.State(), "queueVersion") == N(after, "version"));
        var state = await f.State();
        Check(B(state, "active") && S(state, "id") == session && N(state, "batch") == 1,
            "Next append must retain the live recommendation session like an ordinary edit");
        string persisted = await f.Store.RawLibrary();
        using var document = JsonDocument.Parse(persisted);
        var savedQueue = document.RootElement.GetProperty("entries").EnumerateArray()
            .Single(e => S(e, "key") == "[\"meowfilm\",\"music\",1,\"local\",\"queue\"]").GetProperty("value");
        Check(S(savedQueue, "currentId") == Fixture.Current(before) &&
            savedQueue.GetProperty("entries").EnumerateArray().Select(e => S(e, "id")).SequenceEqual(entries.Select(e => S(e, "id"))),
            "Durable queue must retain the same ordered occurrences and current selection");

        late.Release.TrySetResult(); await Wait(retry); await Wait(late.Settled.Task);
        var settled = await f.State(); var queue = await f.Queue();
        Check(B(settled, "active") && !B(settled, "loading") && N(settled, "batch") == 2 && S(settled, "error") == "");
        Check(queue.GetProperty("entries").EnumerateArray().Take(6).Select(e => e.GetRawText()).SequenceEqual(entries.Select(e => e.GetRawText())) &&
            Fixture.Current(queue) == Fixture.Current(before) && N(queue, "total") == 9);
        Check(f.Selections.Count == selections && f.Store.Writes.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace) == writes + 2,
            "Valid late response appends after the user's explicit next entries without restarting media");
        string final = await f.Store.RawLibrary();
        await f.Call("music.recommendations.cancel", new { id = session });
        Check(!B(await f.State(), "active") && await f.Store.RawLibrary() == final && f.Selections.Count == selections);
    }

    private static async Task AccountReadDrain(int number)
    {
        await using var f = await Fixture.Create();
        var hold = f.Store.HoldRead(MusicAccountOwner.DocumentNamespace, number);
        var start = f.App.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy" }); await Wait(hold.Entered.Task);
        var preparing = f.Source.PrepareRecommendationsAsync();
        await Pending(preparing); Check(!hold.Cancelled.Task.IsCompleted && f.Handler.Paths.IsEmpty);
        if (number == 1) Error(await start.WaitAsync(TimeSpan.FromSeconds(8)), "CANCELLED");
        else await Pending(start);
        // Narrow prepare must leave ordinary library reads/writes available.
        await f.Call("music.queue.mode", new { mode = 1 });
        hold.Release.TrySetResult(); await Wait(preparing);
        var result = await start;
        Check(B(result, "ok") ? !B(result, "active") || S(result, "error").Length > 0 : S(result, "code") == "CANCELLED");
        await f.App.CancelShutdownPreparationAsync();
        Check(f.Handler.Paths.IsEmpty && f.Selections.IsEmpty,
            "both initial observation and FM lease are prelude reads; a retired prelude cannot dispatch HTTP");
        var next = await f.Start(); Check(B(next, "active") && f.Handler.Paths.Count == 1);
    }
    private static async Task CancelHydrate()
    {
        await using var f = await Fixture.Create(); var hold = f.Store.HoldRead(MusicAccountOwner.DocumentNamespace, 1);
        using var cancellation = new CancellationTokenSource();
        var first = f.App.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy" }, cancellation.Token);
        await Wait(hold.Entered.Task);
        var second = f.Start(); cancellation.Cancel();
        Error(await first.WaitAsync(TimeSpan.FromSeconds(8)), "CANCELLED");
        await Pending(second); Check(!hold.Cancelled.Task.IsCompleted && f.Handler.Paths.IsEmpty);
        hold.Release.TrySetResult(); Error(await first, "CANCELLED"); Check(B(await second, "active") && f.Handler.Paths.Count == 1);
        Check(f.Store.Reads.GetValueOrDefault(MusicAccountOwner.DocumentNamespace) == 2 &&
            f.Store.Reads.GetValueOrDefault(MusicPlatformClientContextOwner.DocumentNamespace) == 1 &&
            f.Store.Writes.GetValueOrDefault(MusicPlatformClientContextOwner.DocumentNamespace) == 1,
            "two waiters share hydration; the admitted batch reads one exact lease and client initialization never reads accounts");
    }
    private static async Task HttpDrain()
    {
        await using var f = await Fixture.Create(); var hold = f.Handler.Hold();
        var start = f.Start(); await Wait(hold.Entered.Task); using var waiting = new CancellationTokenSource();
        var prepare = f.Source.PrepareRecommendationsAsync(waiting.Token);
        await Pending(prepare); Check(!hold.Cancelled.Task.IsCompleted);
        waiting.Cancel(); await Cancelled(prepare); var physical = f.Source.PrepareRecommendationsAsync();
        await Pending(physical); Check(f.Handler.Disposals == 0 && f.Store.Disposals == 0);
        hold.Release.TrySetResult(); await Wait(physical); await start;
        await f.App.CancelShutdownPreparationAsync(); Check(f.Handler.Paths.Count == 1);
        Check(B(await f.Start(), "active") && f.Handler.Paths.Count == 2);
    }
    private static async Task ResponseDrain()
    {
        await using var f = await Fixture.Create(); var hold = f.Handler.HoldResponse();
        var start = f.Start(); await Wait(hold.Read.Entered.Task); var prepare = f.Source.PrepareRecommendationsAsync();
        await Pending(prepare); Check(!hold.Read.Cancelled.Task.IsCompleted); hold.Read.Release.TrySetResult();
        await Wait(hold.Dispose.Entered.Task); await Pending(prepare); Check(!hold.Disposed);
        hold.Dispose.Release.TrySetResult(); await Wait(prepare); await start; Check(hold.Disposed && f.Selections.IsEmpty);
    }
    private static async Task CallbackDrain()
    {
        await using var f = await Fixture.Create(); var hold = f.HoldMedia();
        var start = f.Start(); await Wait(hold.Entered.Task); var selected = f.Selections.Last();
        var prepare = f.Source.PrepareRecommendationsAsync(); Check(!selected.IsCurrent());
        await Wait(hold.Cancelled.Task); await Pending(prepare);
        await f.Call("music.history.recordPlayed", new { entryId = selected.EntryId, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        hold.Release.TrySetResult(); await Wait(prepare); await start;
        Check(f.Store.Writes.GetValueOrDefault(MusicFmPreferences.DocumentNamespace) == 0,
            "a retired media callback cannot start saving an FM choice");
        await f.App.CancelShutdownPreparationAsync(); Check(!selected.IsCurrent() && f.Handler.Paths.Count == 1);
    }
    private static async Task BindingDisposal()
    {
        await using var f = await Fixture.Create(); var hold = f.HoldMedia(); var oldBinding = f.Binding!;
        var first = f.Start(); await Wait(hold.Entered.Task); var selection = f.Selections.Last();
        oldBinding.Dispose(); Check(!selection.IsCurrent() && !first.IsCompleted && f.Source.GetFeedback(selection.EntryId) is null);
        f.Bind(); oldBinding.Dispose();
        var next = f.Start(); await Pending(next); hold.Release.TrySetResult(); await first;
        Check(B(await next, "active") && f.Selections.Last().IsCurrent());
    }
    private static async Task InvalidationOrder()
    {
        await using var f = await Fixture.Create(); await f.Start(); var selection = f.Selections.Last();
        bool fencedAtCancellation = false;
        var hold = f.Handler.Hold(); hold.OnCancel = () => fencedAtCancellation = !selection.IsCurrent();
        var catalog = f.App.ExecuteAsync("music.catalog.playlist.prepare", new { platform = "wy", id = "77" });
        await Wait(hold.Entered.Task); await f.Store.SetAccount(2); await f.Call("music.accounts.list");
        await Wait(hold.Cancelled.Task); Check(fencedAtCancellation && !selection.IsCurrent(), "Catalog invalidated before recommendation callback fence");
        hold.Release.TrySetResult(); await catalog; Check(!B(await f.State(), "active"));
    }
    private static async Task AuthCurrent()
    {
        await using var f = await Fixture.Create(); f.Handler.Code = 301;
        var state = await f.Start(); Check(!B(state, "active") && f.Selections.IsEmpty);
        var row = (await f.Call("music.accounts.list")).GetProperty("items")[0];
        Check(S(row, "state") == "revoked" && N(row, "generation") == 1 && f.Handler.Paths.Count == 1);
    }
    private static async Task AuthReplacement()
    {
        await using var f = await Fixture.Create(); f.Handler.Code = 301; var hold = f.Handler.Hold();
        var old = f.Start(); await Wait(hold.Entered.Task);
        await f.Store.SetAccount(2); await f.Call("music.accounts.list"); hold.Release.TrySetResult(); await old;
        var row = (await f.Call("music.accounts.list")).GetProperty("items")[0];
        Check(S(row, "state") == "saved" && N(row, "generation") == 2);
        f.Handler.Code = 200; f.Handler.ExpectedAccount = "synthetic-account-key-2";
        Check(B(await f.Start(), "active"));
    }
    private static async Task Anonymous()
    {
        await using var f = await Fixture.Create(account: false);
        var state = await f.Start();
        Check(!B(state, "active") && S(state, "error") == "AUTH_REQUIRED" && f.Handler.Paths.IsEmpty && f.Selections.IsEmpty);
        Check(S((await f.Call("music.accounts.list")).GetProperty("items")[0], "state") == "none");
    }
    private static async Task CasDrain()
    {
        await using var f = await Fixture.Create(); var hold = f.Store.HoldWrite(MusicLibraryOwner.DocumentNamespace);
        var start = f.Start(); await Wait(hold.Entered.Task); var prepare = f.App.PrepareShutdownAsync();
        await Pending(prepare); Check(f.Handler.Disposals == 0);
        hold.Release.TrySetResult(); await Wait(prepare); await start; Check(f.Selections.IsEmpty);
        await f.App.CancelShutdownPreparationAsync(); Check(N(await f.Queue(), "total") == 3 && !B(await f.State(), "active"));
    }
    private static async Task UnknownCommit()
    {
        await using var f = await Fixture.Create(); f.Store.UnknownLibraryWrite = 2;
        var state = await f.Start(); Check(B(state, "requiresReconcile") && f.Selections.IsEmpty);
        int writes = f.Store.Writes[MusicLibraryOwner.DocumentNamespace];
        Error(await f.App.ExecuteAsync("music.recommendations.retry", new { id = S(state, "id") }), "COMMIT_UNKNOWN");
        Check(f.Store.Writes[MusicLibraryOwner.DocumentNamespace] == writes);
        var result = await f.Call("music.recommendations.reconcile", new { id = S(state, "id") });
        Check(!B(result, "requiresReconcile") && f.Selections.IsEmpty && f.Store.Writes[MusicLibraryOwner.DocumentNamespace] == writes);
        Check(N(await f.Queue(), "total") == 3);
    }
    private static async Task UnknownAbort(int outcome)
    {
        var f = await Fixture.Create();
        try
        {
            f.Store.UnknownLibraryWrite = outcome;
            Check(B(await f.Start(), "requiresReconcile"));
            int writes = f.Store.Writes[MusicLibraryOwner.DocumentNamespace];
            string persisted = await f.Store.RawLibrary();
            foreach (var close in new Func<Task>[] { () => f.App.PrepareShutdownAsync(), () => f.App.DisposeAsync().AsTask() })
            {
                try { await Wait(close()); throw new InvalidOperationException("Unknown commit must fail ordinary close"); }
                catch (Exception error) when (error.Message == "COMMIT_UNKNOWN") { }
            }
            await Wait(f.App.AbortAsync());
            await Wait(f.App.AbortAsync());
            Check(f.Handler.Disposals == 1 && f.Store.Disposals == 0 && f.Runtime.Disposals == 0);
            Check(f.Store.Writes[MusicLibraryOwner.DocumentNamespace] == writes &&
                await f.Store.RawLibrary() == persisted && f.Selections.IsEmpty,
                "Abort must neither retry nor reconcile the unknown write or select media");
            Error(await f.App.ExecuteAsync("music.recommendations.get"), "CLOSED");
        }
        finally
        {
            f.Binding?.Dispose();
            await f.App.AbortAsync();
            await f.Store.DisposeAsync();
        }
    }
    private static async Task AbortDrain(string boundary)
    {
        await using var f = await Fixture.Create();
        var hold = boundary switch
        {
            "cas" => f.Store.HoldWrite(MusicLibraryOwner.DocumentNamespace),
            "media" => f.HoldMedia(),
            _ => f.Handler.Hold()
        };
        var start = f.Start();
        try
        {
            await Wait(hold.Entered.Task);
            using var waiter = new CancellationTokenSource();
            var cancelled = f.App.AbortAsync(waiter.Token);
            await Pending(cancelled);
            waiter.Cancel(); await Cancelled(cancelled);
            var abort = f.App.AbortAsync();
            await Pending(abort); await Pending(start);
            Check(f.Handler.Disposals == 0 && f.Store.Disposals == 0,
                "Cancellation of an abort waiter must not release owners before physical settlement");
            hold.Release.TrySetResult();
            await Wait(abort); await Wait(start);
            Check(f.Handler.Disposals == 1 && f.Store.Disposals == 0 && f.Runtime.Disposals == 0);
            Error(await f.App.ExecuteAsync("music.recommendations.get"), "CLOSED");
        }
        finally { hold.Release.TrySetResult(); }
    }
    private static async Task Reopen()
    {
        string root;
        await using (var f = await Fixture.Create())
        {
            root = f.Root; await f.Start(); var old = f.Selections.Last();
            await f.App.PrepareShutdownAsync(); Check(!old.IsCurrent() && f.Source.GetFeedback(old.EntryId) is null);
            Error(await f.App.ExecuteAsync("music.recommendations.get"), "CLOSED");
            await f.App.CancelShutdownPreparationAsync(); Check(!B(await f.State(), "active") && f.Handler.Paths.Count == 1);
            Check(N(await f.Queue(), "total") == 3);
        }
        await using var reopened = await Fixture.Create(account: false, root: root);
        Check(!B(await reopened.State(), "active") && reopened.Handler.Paths.IsEmpty && N(await reopened.Queue(), "total") == 3);
        Check(reopened.Source.GetFeedback(Fixture.Current(await reopened.Queue())) is null);
    }
    private static async Task UnknownCloseRecovery(bool full, int outcome)
    {
        var f = await Fixture.Create(); bool finished = false;
        try
        {
            f.Store.UnknownLibraryWrite = outcome;
            var state = await f.Start(); Check(B(state, "requiresReconcile"));
            int writes = f.Store.Writes[MusicLibraryOwner.DocumentNamespace];
            try
            {
                await (full ? f.App.PrepareShutdownAsync() : f.Source.PrepareRecommendationsAsync());
                throw new InvalidOperationException("Unknown commit must fail save preparation");
            }
            catch (Exception e) when (e.Message == "COMMIT_UNKNOWN") { }
            try { await f.App.CancelShutdownPreparationAsync(); }
            catch (Exception e) { throw new InvalidOperationException("Cannot reopen controls for explicit reconciliation", e); }
            var pending = await f.State();
            Check(B(pending, "requiresReconcile") && !B(pending, "active") && f.Selections.IsEmpty);
            Error(await f.App.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy" }), "COMMIT_UNKNOWN");
            Check(f.Store.Writes[MusicLibraryOwner.DocumentNamespace] == writes && f.Handler.Paths.Count == 1);
            var reconciled = await f.Call("music.recommendations.reconcile", new { id = S(state, "id") });
            Check(!B(reconciled, "requiresReconcile") && f.Selections.IsEmpty);
            Check(N(await f.Queue(), "total") == (outcome == 2 ? 3 : 0) &&
                f.Store.Writes[MusicLibraryOwner.DocumentNamespace] == writes && f.Handler.Paths.Count == 1);
            Check(B(await f.Start(), "active") && f.Handler.Paths.Count == 2);
            finished = true;
        }
        finally
        {
            // Preserve the original failed-recovery assertion, not the expected
            // secondary unknown-state error while cleaning a failing old candidate.
            if (finished) await f.DisposeAsync();
            else { try { await f.DisposeAsync(); } catch { } }
        }
    }
    private static async Task ImmediateResume()
    {
        await using var f = await Fixture.Create(); var hold = f.Handler.Hold();
        var start = f.Start(); await Wait(hold.Entered.Task);
        var prepare = f.App.PrepareShutdownAsync();
        var resume = f.App.CancelShutdownPreparationAsync();
        await Wait(hold.Cancelled.Task); await Pending(prepare); await Pending(resume);
        hold.Release.TrySetResult();
        await Wait(Task.WhenAll(prepare, resume)); await start;
        Check(!B(await f.State(), "active") && f.Handler.Paths.Count == 1);
        Check(B(await f.Start(), "active"));
    }
    private static async Task ReconcileCloseFence()
    {
        await using var f = await Fixture.Create(); f.Store.UnknownLibraryWrite = 2;
        var state = await f.Start();
        try { await f.Source.PrepareRecommendationsAsync(); throw new InvalidOperationException("Expected unknown preparation"); }
        catch (Exception e) when (e.Message == "COMMIT_UNKNOWN") { }
        await f.App.CancelShutdownPreparationAsync();
        var hold = f.Store.HoldRead(MusicLibraryOwner.DocumentNamespace,
            f.Store.Reads.GetValueOrDefault(MusicLibraryOwner.DocumentNamespace) + 1);
        var reconcile = f.Call("music.recommendations.reconcile", new { id = S(state, "id") });
        await Wait(hold.Entered.Task);
        var close = f.Source.PrepareRecommendationsAsync();
        await Pending(close); await Pending(reconcile);
        hold.Release.TrySetResult(); await reconcile; await Wait(close);
        Check(f.Selections.IsEmpty && f.Handler.Paths.Count == 1);
        Error(await f.App.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy" }), "CLOSED");
        await f.App.CancelShutdownPreparationAsync();
        Check(!B(await f.State(), "active") && B(await f.Start(), "active"));
    }
    private static async Task ObserverClose()
    {
        await using var f = await Fixture.Create(); var closed = Signal(); int once = 0;
        Action<JsonElement> observer = state =>
        {
            if (N(state, "batch") != 1 || B(state, "loading") || Interlocked.Exchange(ref once, 1) != 0) return;
            try { f.App.PrepareShutdownAsync().GetAwaiter().GetResult(); closed.TrySetResult(); }
            catch (Exception e) { closed.TrySetException(e); }
        };
        f.Source.Changed += observer;
        try { await f.Start(); await Wait(closed.Task); await f.App.CancelShutdownPreparationAsync(); }
        finally { f.Source.Changed -= observer; }
        Check(!B(await f.State(), "active"));
    }
}

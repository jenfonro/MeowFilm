using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicNativeCatalogTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native catalog public boundary borrows platform and library", Boundary);
        yield return new("Native catalog base typed routes preserve UI DTO and continuation", BaseRoutes);
        foreach (string route in new[] { "personalPlaylists", "daily", "fm", "similar", "heartbeat", "queueRecommendations", "album", "artist" })
            yield return new("Native catalog explicit typed catalog route " + route, () => PersonalRoute(route));
        yield return new("Native catalog artist cancellation remains owned until the typed read settles", ArtistCancellation);
        yield return new("Native catalog rejects implicit platform unknown routes and malformed fields", InvalidRequests);
        yield return new("Native catalog complete multi-page save has a single durable handoff", CompleteSave);
        yield return new("Native catalog complete multi-page queue selection and source context", CompleteQueue);
        yield return new("Native catalog empty playlist saves but cannot replace queue", Empty);
        yield return new("Native catalog duplicate member rejects while equal recording IDs remain legal", Duplicates);
        yield return new("Native catalog failed final page leaves existing queue and library unchanged", TailFailure);
        foreach (string defect in new[] { "identity", "page", "length", "total", "snapshot", "name", "strength", "more", "continuation" })
            yield return new("Native catalog rejects incomplete or changing page " + defect, () => BadPage(defect));
        foreach (string platform in new[] { "tx", "kg" })
        {
            yield return new("Native catalog fresh full second scan succeeds " + platform, () => SecondScan(platform, "none"));
            yield return new("Native catalog second-scan tail failure cannot half-write " + platform, () => SecondScan(platform, "failure"));
            yield return new("Native catalog second-scan changed member cannot half-write " + platform, () => SecondScan(platform, "member"));
        }
        yield return new("Native catalog complete private locator projection and exact 30-member pages", PrivateLocator);
        yield return new("Native catalog private KG sort across pages preserves duplicate hash occurrences", PrivateOrder);
        yield return new("Native catalog private KG duplicate fileid and changed sort are not snapshots", PrivateChanges);
        yield return new("Native catalog private KG exceeds 100 pages without truncation", PrivateLong);
        yield return new("Native catalog stage tokens expire and discard independently", StageSlots);
        yield return new("Native catalog simultaneous preparations own cancellation independently", PreparingSlots);
        yield return new("Native catalog concurrent reads have no shared request quota", RequestSlots);
        yield return new("Native catalog commit drain and cancellation do not block unrelated preparation", CommitSlots);
        yield return new("Native catalog invalid selection retains token and stale queue CAS consumes it", SelectionAndCas);
        yield return new("Native catalog pre-cancel and late page cancellation never publish hidden stage", Cancellation);
        yield return new("Native catalog cancellation at stage publication releases hidden token", PublicationCancellation);
        yield return new("Native catalog invalidation fences late pages and clears staged tokens", Invalidate);
        yield return new("Native catalog invalidation of queued commit prevents storage admission", InvalidateCommit);
        yield return new("Native catalog cancellation after CAS admission reports known durable success", CancelCommit);
        yield return new("Native catalog unknown-before-CAS uses explicit reconciliation not token replay", () => Unknown(false));
        yield return new("Native catalog unknown-after-CAS retains actual persistence without token replay", () => Unknown(true));
        yield return new("Native catalog third-party CAS conflict cannot overwrite durable state", StorageConflict);
        yield return new("Native catalog close drains ignored read cancellation and borrows dependencies", CloseRead);
        yield return new("Native catalog close drains admitted CAS without pretending cancellation rollback", CloseCommit);
        yield return new("Native catalog bounded close fails until actual drain rather than disposing dependencies", CloseTimeout);
        yield return new("Native catalog field and storage capacity checks never admit partial persistence", Budgets);
    }

    private static void Check(bool value, string message = "Assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static JsonElement E(object? value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement J(JsonNode value) => JsonSerializer.SerializeToElement(value);
    private static JsonObject O(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();
    private static string S(JsonElement value, string key) => value.GetProperty(key).GetString()!;
    private static int N(JsonElement value, string key) => value.GetProperty(key).GetInt32();
    private static JsonElement Ok(JsonElement result)
    { Check(result.GetProperty("ok").GetBoolean(), result.ToString()); return result; }
    private static void Error(JsonElement result, string code)
    { Check(!result.GetProperty("ok").GetBoolean() && S(result, "code") == code, $"Expected {code}, got {result}"); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> predicate)
    {
        for (int i = 0; i < 2000 && !predicate(); i++) await Task.Delay(1);
        Check(predicate(), "Expected lifecycle checkpoint was not reached");
    }
    private static string NativeId(string platform, int index) => platform switch
    { "kg" => (index + 1).ToString("x32"), "tx" => "MID" + (index + 1), _ => (index + 1).ToString() };
    private static JsonElement Song(string platform, int index, string? member = null)
    {
        var id = NativeId(platform, index);
        var native = new JsonObject
        {
            ["hash"] = platform == "kg" ? id : "",
            ["fileid"] = member
        };
        return MusicSongFixture.Element(MusicSongFixture.Online(
            platform, id, "Track " + index, "Synthetic", "Fixture", 180, native: native));
    }
    private static void ReplaceNativeIdentity(JsonObject song, JsonElement replacement)
    {
        var next = O(replacement);
        var currentSource = song["sources"]![0]!.AsObject();
        var nextSource = next["sources"]![0]!.AsObject();
        currentSource["sourceKey"] = nextSource["sourceKey"]!.DeepClone();
        currentSource["musicInfo"] = nextSource["musicInfo"]!.DeepClone();
    }
    private static JsonElement Page(MusicPlaylistRequest request, int total = 235, Func<int, JsonElement>? song = null)
    {
        int offset = (request.Page - 1) * request.Limit;
        var indices = Enumerable.Range(offset, Math.Max(0, Math.Min(request.Limit, total - offset))).ToArray();
        bool privateKg = request.Personal?.Kind == MusicPersonalPlaylistKind.KugouOwned;
        var result = O(E(new
        {
            platform = request.Platform, id = request.Id, name = "Complete fixture", page = request.Page,
            limit = request.Limit, total, more = offset + indices.Length < total,
            continuation = offset + indices.Length < total ? new { page = request.Page + 1 } : null,
            snapshot = new string('a', 64), snapshotStrength = request.Platform == "wy" ? "ordered-membership" : "metadata-only",
            items = indices.Select(i => song?.Invoke(i) ?? Song(request.Platform, i, privateKg ? (1000 + i).ToString() : null)).ToArray()
        }));
        if (privateKg)
        {
            result["memberIds"] = JsonSerializer.SerializeToNode(indices.Select(i => (1000 + i).ToString()));
            result["memberSorts"] = JsonSerializer.SerializeToNode(indices.Select(i => (long?)i));
        }
        return J(result);
    }
    private static object Locator(string platform) => platform == "tx"
        ? new { scope = new string('b', 64), kind = "qq-favorites", listId = "201", total = (int?)null,
            version = (string?)null, name = "我喜欢", pageSize = 100, playlistId = "favorites", dirId = "201" }
        : (object)new { scope = new string('b', 64), kind = "kugou-owned", listId = "42", total = 65,
            version = "7", name = "Private fixture", pageSize = 30, listType = 0,
            globalCollectionId = (string?)null, source = 1, listVersion = 7 };

    private sealed record Call(string Method, object? Request, CancellationToken Token);
    private sealed class Platform : IMusicPlatformCatalog, IMusicArtistCatalog
    {
        private readonly object gate = new();
        private readonly List<Call> calls = [];
        internal Call[] Calls { get { lock (gate) return calls.ToArray(); } }
        internal readonly List<string?> Invalidations = [];
        internal bool Disposed;
        internal int Total = 235;
        internal Func<Call, Task<JsonElement>>? Handler;
        internal Func<MusicPlaylistRequest, JsonElement>? Pages;
        private Task<JsonElement> Run(string method, object request, CancellationToken token)
        {
            var call = new Call(method, request, token);
            lock (gate) calls.Add(call);
            if (Handler is not null) return Handler(call);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(request is MusicPlaylistRequest p ? Pages?.Invoke(p) ?? Page(p, Total)
                : E(new { marker = method, items = new[] { "opaque" }, continuation = new { provider = new { cursor = 7 } } }));
        }
        public Task<JsonElement> SearchAsync(MusicSearchRequest r, CancellationToken t = default) => Run("search", r, t);
        public Task<JsonElement> SongAsync(MusicSongRequest r, CancellationToken t = default) => Run("song", r, t);
        public Task<JsonElement> PlaylistAsync(MusicPlaylistRequest r, CancellationToken t = default) => Run("playlist", r, t);
        public Task<JsonElement> HomeAsync(MusicHomeRequest r, CancellationToken t = default) => Run("home", r, t);
        public Task<JsonElement> ChannelsAsync(MusicChannelsRequest r, CancellationToken t = default) => Run("channels", r, t);
        public Task<JsonElement> ChannelAsync(MusicChannelRequest r, CancellationToken t = default) => Run("channel", r, t);
        public Task<JsonElement> PersonalPlaylistsAsync(MusicPersonalPlaylistsRequest r, CancellationToken t = default) => Run("personalPlaylists", r, t);
        public Task<JsonElement> DailyAsync(MusicDailyRequest r, CancellationToken t = default) => Run("daily", r, t);
        public Task<JsonElement> FmAsync(MusicFmRequest r, CancellationToken t = default) => Run("fm", r, t);
        public Task<JsonElement> SimilarAsync(MusicSimilarRequest r, CancellationToken t = default) => Run("similar", r, t);
        public Task<JsonElement> HeartbeatAsync(MusicHeartbeatRequest r, CancellationToken t = default) => Run("heartbeat", r, t);
        public Task<JsonElement> QueueRecommendationsAsync(MusicQueueRecommendationsRequest r, CancellationToken t = default) => Run("queueRecommendations", r, t);
        public Task<JsonElement> AlbumAsync(MusicAlbumRequest r, CancellationToken t = default) => Run("album", r, t);
        public Task<JsonElement> ArtistAsync(MusicArtistRequest r, CancellationToken t = default) => Run("artist", r, t);
        public JsonElement Describe() => E(new { platform = "explicit-fixture", enabled = true });
        public void Invalidate(string? platform = null) { lock (gate) Invalidations.Add(platform); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Store : IProtectedDocumentStore
    {
        private readonly object gate = new();
        internal byte[]? Bytes;
        internal int Writes, Failure;
        internal bool Disposed;
        internal TaskCompletionSource? Entered, Release;
        internal bool WriteWasUncancellable;
        public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken t = default)
        {
            Check(ns == MusicLibraryOwner.DocumentNamespace); t.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(new ProtectedDocument(Bytes?.ToArray(),
                Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes)));
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes,
            DocumentFingerprint expected, CancellationToken t = default)
        {
            Check(ns == MusicLibraryOwner.DocumentNamespace); WriteWasUncancellable = !t.CanBeCanceled;
            if (Release is { } release) { Entered!.TrySetResult(); await release.Task; }
            lock (gate)
            {
                var current = Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes);
                if (current != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
                Writes++;
                int failure = Failure; Failure = 0;
                if (failure == 1) throw new ProtectedDocumentException(DocumentStoreError.Storage);
                Bytes = bytes.ToArray();
                if (failure == 2) throw new ProtectedDocumentException(DocumentStoreError.Storage);
                return DocumentFingerprint.FromBytes(Bytes);
            }
        }
        internal void Block() { Entered = Signal(); Release = Signal(); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private DateTimeOffset now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        internal Action? Read;
        public override DateTimeOffset GetUtcNow() { Read?.Invoke(); lock (gate) return now; }
        public override long GetTimestamp() { lock (gate) return now.Ticks; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state); lock (gate) timers.Add(timer); timer.Change(dueTime, period); return timer; }
        internal bool HasTimer(TimeSpan after) { lock (gate) return timers.Any(t => !t.Dead && t.Due == now + after); }
        internal void Advance(TimeSpan amount)
        {
            Timer[] due;
            lock (gate)
            {
                now += amount; due = timers.Where(t => !t.Dead && t.Due <= now).ToArray();
                foreach (var t in due) t.Due = t.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + t.Period;
            }
            foreach (var t in due) t.Callback(t.State);
        }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal DateTimeOffset Due;
            internal TimeSpan Period;
            internal bool Dead;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                { if (Dead) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime; Period = period; return true; }
            }
            public void Dispose() { lock (owner.gate) Dead = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Platform Platform = new();
        internal readonly Store Store = new();
        internal readonly Clock Clock = new();
        internal readonly MusicLibraryOwner Library;
        internal readonly MusicNativeCatalogOwner Owner;
        internal readonly List<TaskCompletionSource> Releases = [];
        internal bool ExpectedCloseTimeout;
        internal Fixture() { Library = new(Store); Owner = new(Platform, Library, Clock); }
        internal TaskCompletionSource Hold() { var signal = Signal(); Releases.Add(signal); return signal; }
        internal Task<JsonElement> Run(string method, object? input = null, CancellationToken token = default) =>
            Owner.ExecuteAsync("music.catalog." + method, input, token);
        internal async Task<JsonElement> Prepare(string platform = "wy", string id = "42", object? personal = null) =>
            Ok(await Run("playlist.prepare", new { platform, id, personal }));
        internal async Task<JsonElement> Queue() => Ok(await Library.ExecuteAsync("music.queue.get", new { limit = 100 }));
        public async ValueTask DisposeAsync()
        {
            foreach (var release in Releases) release.TrySetResult(); Store.Release?.TrySetResult();
            try { await Owner.DisposeAsync(); } catch (TimeoutException) when (ExpectedCloseTimeout) { }
            await Library.AbortAsync(); Platform.Dispose(); await Store.DisposeAsync();
        }
    }

    private static async Task Boundary()
    {
        await using var f = new Fixture();
        Check(typeof(MusicNativeCatalogOwner).GetInterfaces().All(t => t.Name != "IRuntimeClient"));
        Check(typeof(IMusicPlatformCatalog).GetMethod(nameof(IMusicArtistCatalog.ArtistAsync)) is null,
            "Artist capability must remain optional for unrelated platform substitutes");
        Check(S(Ok(await f.Run("describe")).GetProperty("descriptor"), "platform") == "explicit-fixture");
        Check(f.Store.Bytes is null && f.Platform.Calls.Length == 0);
        await f.Owner.DisposeAsync(); await f.Owner.DisposeAsync();
        Check(!f.Platform.Disposed && !f.Store.Disposed); Ok(await f.Library.ExecuteAsync("music.library.list"));
        Error(await f.Run("describe"), "CLOSED");
    }
    private static async Task BaseRoutes()
    {
        await using var f = new Fixture();
        var cursor = E(new { opaque = new { position = 7, seen = new[] { "x" } } });
        Ok(await f.Run("search", new { platform = "tx", keyword = "Fixture", kind = "playlists", page = 2, limit = 17, refresh = true }));
        Check(f.Platform.Calls[^1].Request is MusicSearchRequest { Platform: "tx", Kind: MusicSearchKind.Playlists, Page: 2, Limit: 17, Refresh: true });
        Check(S(Ok(await f.Run("song", new { platform = "wy", id = "5" })).GetProperty("song"), "marker") == "song");
        Ok(await f.Run("playlist", new { platform = "wy", id = "42", page = 2, limit = 7, snapshot = new string('a', 64) }));
        Check(f.Platform.Calls[^1].Request is MusicPlaylistRequest { Page: 2, Limit: 7, Personal: null });
        Ok(await f.Run("home", new { platform = "kg", continuation = cursor, refresh = true }));
        Check(((MusicHomeRequest)f.Platform.Calls[^1].Request!).Continuation!.Value.GetRawText() == cursor.GetRawText());
        Ok(await f.Run("channels", new { platform = "tx", refresh = true }));
        Ok(await f.Run("channel", new { platform = "wy", id = "top", pageCode = "p", continuation = cursor, tag = "", refresh = true }));
        Check(f.Platform.Calls[^1].Request is MusicChannelRequest { PageCode: "p", Tag: "", Refresh: true });
        Ok(await f.Run("channel", new { platform = "wy", id = "artist", tag = "96:2:65" }));
        Check(f.Platform.Calls[^1].Request is MusicChannelRequest { Id: "artist", Tag: "96:2:65" });
        var personalCursor = E(new { seen = Enumerable.Range(1, 5000).Select(i => i.ToString("D20")).ToArray() });
        Check(Encoding.UTF8.GetByteCount(personalCursor.GetRawText()) > 65536);
        Ok(await f.Run("personalPlaylists", new { platform = "wy", page = 5001, limit = 1, continuation = personalCursor }));
        Check(((MusicPersonalPlaylistsRequest)f.Platform.Calls[^1].Request!).Continuation!.Value.GetRawText() == personalCursor.GetRawText());
        Check(f.Store.Writes == 0);
    }
    private static async Task PersonalRoute(string route)
    {
        await using var f = new Fixture();
        object input = route switch
        {
            "personalPlaylists" => new { platform = "kg", kind = "created", page = 2, limit = 23, continuation = new { a = new { b = 3 } }, refresh = false },
            "daily" => new { platform = "wy", branch = "style", refresh = true },
            "fm" => new { platform = "wy", mode = "scene", submode = "EXERCISE", preview = true },
            "similar" => new { platform = "wy", sourceIds = new[] { "1", "2" } },
            "heartbeat" => new { platform = "wy", playlistId = "42", startMusicId = "1", songId = "2", fromHome = true },
            "queueRecommendations" => new { platform = "wy", songIds = new[] { "1", "2" }, currentPlaySongId = "2" },
            "album" => new { platform = "wy", id = "12", refresh = true },
            _ => new { platform = "wy", id = "13", refresh = true }
        };
        var reply = Ok(await f.Run(route, input));
        Check(S(reply, "marker") == route && reply.GetProperty("continuation").GetProperty("provider").GetProperty("cursor").GetInt32() == 7);
        Check(f.Platform.Calls.Length == 1 && f.Store.Writes == 0);
        var request = f.Platform.Calls.Single().Request;
        Check(route switch
        {
            "personalPlaylists" => request is MusicPersonalPlaylistsRequest { Kind: MusicPersonalListKind.Created, Page: 2, Limit: 23, Refresh: false } p &&
                p.Continuation!.Value.GetProperty("a").GetProperty("b").GetInt32() == 3,
            "daily" => request is MusicDailyRequest { Branch: MusicDailyBranch.Style, Refresh: true },
            "fm" => request is MusicFmRequest { Mode: MusicFmMode.Scene, Submode: "EXERCISE", Preview: true },
            "similar" => request is MusicSimilarRequest p && p.SourceIds.SequenceEqual(["1", "2"]),
            "heartbeat" => request is MusicHeartbeatRequest { PlaylistId: "42", StartMusicId: "1", SongId: "2", FromHome: true },
            "queueRecommendations" => request is MusicQueueRecommendationsRequest { CurrentPlaySongId: "2" } p && p.SongIds.SequenceEqual(["1", "2"]),
            "album" => request is MusicAlbumRequest { Id: "12", Refresh: true },
            _ => request is MusicArtistRequest { Platform: "wy", Id: "13", Refresh: true }
        });
        if (route == "fm")
        {
            Ok(await f.Run("fm", new { platform = "wy", ruleId = "9", preview = false }));
            Check(f.Platform.Calls[^1].Request is MusicFmRequest { Mode: MusicFmMode.Default, RuleId: "9", TabTagId: null });
        }
    }
    private static async Task ArtistCancellation()
    {
        await using var f = new Fixture();
        var entered = Signal();
        var release = f.Hold();
        f.Platform.Handler = async call =>
        {
            Check(call.Method == "artist" && call.Request is MusicArtistRequest { Platform: "wy", Id: "13" });
            entered.TrySetResult();
            await release.Task;
            call.Token.ThrowIfCancellationRequested();
            return E(new { marker = "artist" });
        };
        using var cancellation = new CancellationTokenSource();
        var work = f.Run("artist", new { platform = "wy", id = "13" }, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Until(() => f.Platform.Calls.Single().Token.IsCancellationRequested);
        Check(!work.IsCompleted, "Typed artist read detached before physical settlement");
        release.TrySetResult();
        Error(await work, "CANCELLED");
    }
    private static async Task InvalidRequests()
    {
        await using var f = new Fixture();
        foreach (var (route, input) in new (string, object)[]
        {
            ("daily", new { }), ("daily", new { platform = "all" }), ("daily", new { platform = "wy", branch = "other" }),
            ("fm", new { platform = "wy", mode = "auto" }), ("personalPlaylists", new { platform = "tx", kind = "other" }),
            ("similar", new { platform = "wy", sourceIds = Array.Empty<string>() }),
            ("album", new { platform = "wy", id = "x", cookie = "synthetic-not-forwarded" }),
            ("artist", new { platform = "wy", id = "13", cookie = "synthetic-not-forwarded" }),
            ("search", new { platform = "wy", keyword = "x", kind = "unknown" }),
        }) Error(await f.Run(route, input), "INVALID_REQUEST");
        Error(await f.Run("daily", JsonDocument.Parse("{\"platform\":\"wy\",\"platform\":\"tx\"}").RootElement), "INVALID_REQUEST");
        Error(await f.Run("personal.aggregate", new { platform = "wy" }), "METHOD_UNAVAILABLE");
        Check(f.Platform.Calls.Length == 0 && f.Store.Writes == 0);
    }
    private static async Task CompleteSave()
    {
        await using var f = new Fixture();
        var stage = await f.Prepare(); Check(N(stage, "count") == 235 && f.Store.Writes == 0);
        Check(f.Platform.Calls.Select(c => ((MusicPlaylistRequest)c.Request!).Page).SequenceEqual([1, 2, 3]));
        Check(((MusicPlaylistRequest)f.Platform.Calls[0].Request!).Snapshot is null);
        Check(f.Platform.Calls.Skip(1).All(c => ((MusicPlaylistRequest)c.Request!).Snapshot == S(stage, "snapshot")));
        var saved = Ok(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "save", name = "Saved complete" }));
        Check(f.Store.Writes == 1 && N(saved.GetProperty("collection"), "count") == 235);
        var songs = new List<JsonElement>();
        for (int offset = 0; offset < 235; offset += 100)
        {
            var detail = Ok(await f.Library.ExecuteAsync("music.library.detail",
                new { id = S(saved.GetProperty("collection"), "id"), offset, limit = 100 }));
            Check(N(detail, "total") == 235);
            songs.AddRange(detail.GetProperty("songs").EnumerateArray().Select(song => song.Clone()));
        }
        Check(songs.Count == 235 && MusicSongFixture.NativeId(songs[234]) == NativeId("wy", 234));
        Error(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "save" }), "CATALOG_TOKEN_EXPIRED");
        await using var reader = new MusicLibraryOwner(f.Store);
        Check(N(Ok(await reader.ExecuteAsync("music.library.list")), "total") == 2);
    }
    private static async Task CompleteQueue()
    {
        await using var f = new Fixture();
        var before = await f.Queue(); var stage = await f.Prepare();
        var reply = Ok(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "play", index = 101,
            expectedVersion = N(before, "version"), expectedSongId = S(Song("wy", 101), "songId") }));
        var queue = await f.Queue();
        Check(N(queue, "total") == 235 && S(queue, "currentId") == S(reply, "currentId") && N(reply, "version") == 1 && N(reply, "mode") == 1);
        Check(MusicSongFixture.NativeId(queue.GetProperty("current").GetProperty("song")) == NativeId("wy", 101));
        Check(S(queue.GetProperty("source"), "platform") == "wy" && f.Store.Writes == 1);
    }
    private static async Task Empty()
    {
        await using var f = new Fixture(); f.Platform.Total = 0;
        var stage = await f.Prepare(); Check(N(stage, "count") == 0);
        Error(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "play", expectedVersion = 0 }), "INVALID_REQUEST");
        var saved = Ok(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "save" }));
        Check(N(saved.GetProperty("collection"), "count") == 0 && f.Store.Writes == 1);
    }
    private static async Task Duplicates()
    {
        await using var f = new Fixture();
        f.Platform.Pages = p => Page(p, 101, i => Song("wy", i == 100 ? 0 : i));
        Error(await f.Run("playlist.prepare", new { platform = "wy", id = "42" }), "PAGINATION_STALLED");
        f.Platform.Pages = p => Page(p, 101, i => { var song = O(Song("wy", i)); song["songId"] = "song:shared-recording"; return J(song); });
        var stage = await f.Prepare(); Check(N(stage, "count") == 101 && f.Store.Writes == 0);
    }
    private static async Task TailFailure()
    {
        await using var f = new Fixture();
        Ok(await f.Library.ReplaceQueueAsync([Song("wy", 999)], 0, null, 0));
        var saved = f.Store.Bytes!.ToArray();
        f.Platform.Pages = p => p.Page == 3 ? throw new InvalidOperationException("private-error") : Page(p);
        Error(await f.Run("playlist.prepare", new { platform = "wy", id = "42" }), "INVALID_RESPONSE");
        Check(f.Store.Bytes!.SequenceEqual(saved) && f.Store.Writes == 1 && N(await f.Queue(), "total") == 1);
    }
    private static async Task BadPage(string defect)
    {
        await using var f = new Fixture();
        f.Platform.Pages = p =>
        {
            var result = O(Page(p)); if (p.Page != 2) return J(result);
            switch (defect)
            {
                case "identity": result["platform"] = "tx"; break;
                case "page": result["page"] = 1; break;
                case "length": result["items"]!.AsArray().RemoveAt(0); break;
                case "total": result["total"] = 234; break;
                case "snapshot": result["snapshot"] = new string('b', 64); break;
                case "name": result["name"] = "Changed"; break;
                case "strength": result["snapshotStrength"] = "metadata-only"; break;
                case "more": result["more"] = false; break;
                case "continuation": result["continuation"] = JsonSerializer.SerializeToNode(new { page = 2 }); break;
            }
            return J(result);
        };
        string expected = defect switch { "identity" => "IDENTITY_MISMATCH", "page" => "INVALID_RESPONSE",
            "length" or "more" => "INCOMPLETE_PLAYLIST", "continuation" => "PAGINATION_STALLED", _ => "SNAPSHOT_CHANGED" };
        Error(await f.Run("playlist.prepare", new { platform = "wy", id = "42" }), expected);
        Check(f.Store.Bytes is null && f.Store.Writes == 0);
    }
    private static async Task SecondScan(string platform, string defect)
    {
        await using var f = new Fixture();
        int count = 0;
        f.Platform.Pages = p =>
        {
            int call = ++count; var page = Page(p);
            if (call == 6 && defect == "failure") throw new InvalidOperationException("synthetic-tail");
            if (call == 6 && defect == "member")
            {
                var changed = O(page);
                ReplaceNativeIdentity(changed["items"]![0]!.AsObject(), Song(platform, 999));
                return J(changed); // songId intentionally unchanged: it is NOT member identity.
            }
            return page;
        };
        var reply = await f.Run("playlist.prepare", new { platform, id = "42" });
        if (defect == "none")
        {
            Ok(reply);
            Ok(await f.Run("playlist.commit", new { token = S(reply, "token"), action = "save" }));
            Check(f.Store.Writes == 1);
        }
        else { Error(reply, defect == "failure" ? "INVALID_RESPONSE" : "SNAPSHOT_CHANGED"); Check(f.Store.Writes == 0); }
        Check(count == 6 && f.Platform.Calls.Skip(3).All(c => ((MusicPlaylistRequest)c.Request!).Refresh));
    }
    private static async Task PrivateLocator()
    {
        await using var f = new Fixture(); f.Platform.Total = 65;
        await f.Prepare("kg", "0:42", Locator("kg"));
        var requests = f.Platform.Calls.Select(c => (MusicPlaylistRequest)c.Request!).ToArray();
        Check(requests.Length == 6 && requests.All(r => r.Limit == 30 && r.Personal is
            { Kind: MusicPersonalPlaylistKind.KugouOwned, ListId: "42", Total: 65, Version: "7" }));
        Ok(await f.Run("playlist", new { platform = "tx", id = "favorites", personal = Locator("tx") }));
        Check(f.Platform.Calls[^1].Request is MusicPlaylistRequest { Limit: 100,
            Personal: { Kind: MusicPersonalPlaylistKind.QqFavorites, ListId: "201", Total: null, Version: null } });
        var wrong = O(E(Locator("kg"))); wrong["pageSize"] = 100;
        Error(await f.Run("playlist.prepare", new { platform = "kg", id = "0:42", personal = wrong }), "INVALID_REQUEST");
        wrong["pageSize"] = 30; wrong["scope"] = "default";
        Error(await f.Run("playlist.prepare", new { platform = "kg", id = "0:42", personal = wrong }), "INVALID_REQUEST");
        Error(await f.Run("playlist.prepare", new { platform = "wy", id = "0:42", personal = Locator("kg") }), "IDENTITY_MISMATCH");
    }
    private static long? Sort(int i) => i % 17 == 0 ? null : (64 - i) / 2;
    private static async Task PrivateOrder()
    {
        await using var f = new Fixture();
        f.Platform.Pages = p =>
        {
            var result = O(Page(p, 65, i => Song("kg", i == 31 ? 0 : i, (1000 + i).ToString())));
            result["memberSorts"] = JsonSerializer.SerializeToNode(Enumerable.Range((p.Page - 1) * p.Limit,
                result["items"]!.AsArray().Count).Select(Sort));
            return J(result);
        };
        var stage = await f.Prepare("kg", "0:42", Locator("kg"));
        Ok(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "play", memberId = "1031",
            expectedSongId = S(Song("kg", 0), "songId"), expectedVersion = 0 }));
        var queue = await f.Queue();
        var members = queue.GetProperty("entries").EnumerateArray().Select(e =>
            S(MusicSongFixture.Info(e.GetProperty("song")), "fileid")).ToArray();
        var expected = Enumerable.Range(0, 65).OrderBy(i => Sort(i) is null).ThenBy(Sort).ThenBy(i => i)
            .Select(i => (1000 + i).ToString()).ToArray();
        Check(members.SequenceEqual(expected) && members.Contains("1000") && members.Contains("1031"));
        Check(S(MusicSongFixture.Info(queue.GetProperty("current").GetProperty("song")), "fileid") == "1031");
        Check(N(queue, "total") == 65 && f.Store.Writes == 1);
    }
    private static async Task PrivateChanges()
    {
        foreach (string defect in new[] { "duplicate", "sort", "missing" })
        {
            await using var f = new Fixture(); int count = 0;
            f.Platform.Pages = p =>
            {
                var result = O(Page(p, 65)); int call = ++count;
                if (defect == "duplicate" && call == 2) result["memberIds"]![0] = "1000";
                if (defect == "sort" && call == 6) result["memberSorts"]![0] = 999;
                if (defect == "missing" && call == 1) result.Remove("memberIds");
                return J(result);
            };
            Error(await f.Run("playlist.prepare", new { platform = "kg", id = "0:42", personal = Locator("kg") }),
                defect == "duplicate" ? "PAGINATION_STALLED" : defect == "sort" ? "SNAPSHOT_CHANGED" : "IDENTITY_MISMATCH");
            Check(f.Store.Writes == 0);
        }
    }
    private static async Task PrivateLong()
    {
        await using var f = new Fixture(); f.Platform.Total = 3001;
        var locator = O(E(Locator("kg"))); locator["total"] = 3001;
        var stage = await f.Prepare("kg", "0:42", locator);
        Check(N(stage, "count") == 3001 && f.Platform.Calls.Length == 202 &&
            f.Platform.Calls.Count(c => ((MusicPlaylistRequest)c.Request!).Page == 101) == 2);
        Check(f.Store.Writes == 0);
    }
    private static async Task StageSlots()
    {
        await using var f = new Fixture(); f.Platform.Total = 1;
        var a = await f.Prepare(); var b = await f.Prepare();
        await f.Prepare();
        Check(Ok(await f.Run("playlist.discard", new { token = S(a, "token") })).GetProperty("discarded").GetBoolean());
        Check(!Ok(await f.Run("playlist.discard", new { token = S(a, "token") })).GetProperty("discarded").GetBoolean());
        var c = await f.Prepare(); f.Clock.Advance(TimeSpan.FromMinutes(1));
        Error(await f.Run("playlist.commit", new { token = S(b, "token"), action = "save" }), "CATALOG_TOKEN_EXPIRED");
        Error(await f.Run("playlist.commit", new { token = S(c, "token"), action = "save" }), "CATALOG_TOKEN_EXPIRED");
        await f.Prepare(); await f.Prepare();
    }
    private static async Task PreparingSlots()
    {
        await using var f = new Fixture(); var release = f.Hold();
        f.Platform.Handler = async c => { await release.Task.WaitAsync(c.Token); return Page((MusicPlaylistRequest)c.Request!, 1); };
        using var cancellation = new CancellationTokenSource();
        var a = f.Run("playlist.prepare", new { platform = "wy", id = "42" }, cancellation.Token);
        var b = f.Run("playlist.prepare", new { platform = "wy", id = "43" });
        await Until(() => f.Platform.Calls.Length == 2);
        var c = f.Run("playlist.prepare", new { platform = "wy", id = "44" });
        await Until(() => f.Platform.Calls.Length == 3);
        cancellation.Cancel(); Error(await a, "CANCELLED");
        release.TrySetResult(); Ok(await b); Ok(await c);
    }
    private static async Task RequestSlots()
    {
        await using var f = new Fixture(); var release = f.Hold();
        f.Platform.Handler = async c => { await release.Task.WaitAsync(c.Token); return E(new { marker = "home" }); };
        var tasks = Enumerable.Range(0, 40).Select(_ => f.Run("home", new { platform = "wy" })).ToArray();
        await Until(() => f.Platform.Calls.Length == 40);
        release.TrySetResult();
        foreach (var reply in await Task.WhenAll(tasks)) Ok(reply);
        Ok(await f.Run("describe"));
    }
    private static async Task CommitSlots()
    {
        await using var f = new Fixture(); f.Platform.Total = 1;
        var a = await f.Prepare(); var b = await f.Prepare();
        f.Store.Block();
        var writing = f.Run("playlist.commit", new { token = S(a, "token"), action = "save" });
        await f.Store.Entered!.Task;
        using var cancel = new CancellationTokenSource();
        var queued = f.Run("playlist.commit", new { token = S(b, "token"), action = "save" }, cancel.Token);
        await f.Prepare();
        cancel.Cancel(); Error(await queued, "CANCELLED");
        await f.Prepare();
        Check(!writing.IsCompleted);
        f.Store.Release!.TrySetResult(); Ok(await writing); Check(f.Store.Writes == 1);
    }
    private static async Task SelectionAndCas()
    {
        await using var f = new Fixture(); f.Platform.Total = 2;
        var stage = await f.Prepare(); string token = S(stage, "token");
        Error(await f.Run("playlist.commit", new { token, action = "play", index = 2, expectedVersion = 0 }), "INVALID_REQUEST");
        Error(await f.Run("playlist.commit", new { token, action = "play", index = 0, expectedVersion = 0, expectedSongId = "wrong" }), "SNAPSHOT_CHANGED");
        Error(await f.Run("playlist.commit", new { token, action = "play", index = 0, memberId = "1", expectedVersion = 0 }), "INVALID_REQUEST");
        Ok(await f.Library.ReplaceQueueAsync([Song("wy", 999)], 0, null, 0));
        Error(await f.Run("playlist.commit", new { token, action = "play", index = 0, expectedVersion = 0 }), "REVISION_CONFLICT");
        Error(await f.Run("playlist.commit", new { token, action = "play", index = 0, expectedVersion = 1 }), "CATALOG_TOKEN_EXPIRED");
        Check(f.Store.Writes == 1 && N(await f.Queue(), "total") == 1);
    }
    private static async Task Cancellation()
    {
        await using var f = new Fixture(); using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Error(await f.Run("playlist.prepare", new { platform = "wy", id = "42" }, cancel.Token), "CANCELLED");
        Check(f.Platform.Calls.Length == 0);
        using var during = new CancellationTokenSource(); var entered = Signal(); var release = f.Hold();
        f.Platform.Handler = async c =>
        { var p = (MusicPlaylistRequest)c.Request!; if (p.Page == 2) { entered.TrySetResult(); await release.Task; } return Page(p); };
        var work = f.Run("playlist.prepare", new { platform = "wy", id = "42" }, during.Token);
        await entered.Task; during.Cancel(); release.TrySetResult(); Error(await work, "CANCELLED");
        Check(f.Store.Writes == 0 && f.Platform.Calls.Length == 2);
        f.Platform.Handler = null; f.Platform.Total = 1; await f.Prepare(); await f.Prepare();
    }
    private static async Task PublicationCancellation()
    {
        await using var f = new Fixture(); f.Platform.Total = 1; using var cancel = new CancellationTokenSource();
        int reads = 0; f.Clock.Read = () => { if (++reads == 3) cancel.Cancel(); };
        Error(await f.Run("playlist.prepare", new { platform = "wy", id = "42" }, cancel.Token), "CANCELLED");
        f.Clock.Read = null; await f.Prepare(); await f.Prepare(); Check(f.Store.Writes == 0);
    }
    private static async Task Invalidate()
    {
        await using var f = new Fixture(); f.Platform.Total = 1;
        var stage = await f.Prepare(); var entered = Signal(); var release = f.Hold();
        f.Platform.Handler = async c => { entered.TrySetResult(); await release.Task; return Page((MusicPlaylistRequest)c.Request!, 1); };
        var late = f.Run("playlist.prepare", new { platform = "wy", id = "43" });
        await entered.Task; f.Owner.Invalidate("wy"); release.TrySetResult(); Error(await late, "STALE_SESSION");
        Error(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "save" }), "CATALOG_TOKEN_EXPIRED");
        Check(f.Platform.Invalidations.SequenceEqual(["wy"]) && f.Store.Writes == 0);
        f.Platform.Handler = null; await f.Prepare(); await f.Prepare();
    }
    private static async Task InvalidateCommit()
    {
        await using var f = new Fixture(); f.Platform.Total = 1;
        var stage = await f.Prepare(); f.Store.Block();
        var other = f.Library.CreatePlaylistAsync("Other user action", [Song("wy", 99)]);
        await f.Store.Entered!.Task;
        var queued = f.Run("playlist.commit", new { token = S(stage, "token"), action = "save" });
        f.Owner.Invalidate("wy"); Error(await queued, "STALE_SESSION");
        f.Store.Release!.TrySetResult(); Ok(await other); Check(f.Store.Writes == 1);
    }
    private static async Task CancelCommit()
    {
        await using var f = new Fixture(); f.Platform.Total = 1; var stage = await f.Prepare(); f.Store.Block();
        using var cancel = new CancellationTokenSource();
        var writing = f.Run("playlist.commit", new { token = S(stage, "token"), action = "save" }, cancel.Token);
        await f.Store.Entered!.Task; cancel.Cancel(); f.Store.Release!.TrySetResult();
        Ok(await writing); Check(f.Store.WriteWasUncancellable && f.Store.Writes == 1);
    }
    private static async Task Unknown(bool after)
    {
        await using var f = new Fixture(); f.Platform.Total = 1;
        var stage = await f.Prepare(); f.Store.Failure = after ? 2 : 1;
        Error(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "save" }), "COMMIT_UNKNOWN");
        Error(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "save" }), "CATALOG_TOKEN_EXPIRED");
        var next = await f.Prepare();
        Error(await f.Run("playlist.commit", new { token = S(next, "token"), action = "save" }), "PENDING_WRITE");
        Check(f.Store.Writes == 1 && (f.Store.Bytes is not null) == after);
        await f.Library.ReconcileAsync();
        Check(N(Ok(await f.Library.ExecuteAsync("music.library.list")), "total") == (after ? 2 : 1));
        var explicitRetry = await f.Prepare();
        Ok(await f.Run("playlist.commit", new { token = S(explicitRetry, "token"), action = "save" }));
        Check(f.Store.Writes == 2);
    }
    private static async Task StorageConflict()
    {
        await using var f = new Fixture(); f.Platform.Total = 1;
        Ok(await f.Library.ExecuteAsync("music.library.list")); // Capture missing fingerprint.
        f.Store.Bytes = Encoding.UTF8.GetBytes("{\"version\":1,\"entries\":[]}");
        var external = f.Store.Bytes.ToArray(); var stage = await f.Prepare();
        Error(await f.Run("playlist.commit", new { token = S(stage, "token"), action = "save" }), "STORAGE_CONFLICT");
        Check(f.Store.Bytes.SequenceEqual(external) && f.Store.Writes == 0);
    }
    private static async Task CloseRead()
    {
        await using var f = new Fixture(); var entered = Signal(); var release = f.Hold();
        f.Platform.Handler = async c => { entered.TrySetResult(); await release.Task; return E(new { marker = "late" }); };
        var work = f.Run("home", new { platform = "wy" }); await entered.Task;
        var closing = f.Owner.DisposeAsync().AsTask();
        await Until(() => f.Platform.Calls.Single().Token.IsCancellationRequested);
        Check(!closing.IsCompleted); release.TrySetResult(); Error(await work, "CLOSED"); await closing;
        Check(!f.Platform.Disposed && !f.Store.Disposed); Ok(await f.Library.ExecuteAsync("music.library.list"));
    }
    private static async Task CloseCommit()
    {
        await using var f = new Fixture(); f.Platform.Total = 1; var stage = await f.Prepare(); f.Store.Block();
        var writing = f.Run("playlist.commit", new { token = S(stage, "token"), action = "save" });
        await f.Store.Entered!.Task;
        var closing = f.Owner.DisposeAsync().AsTask(); await Until(() => f.Clock.HasTimer(TimeSpan.FromSeconds(30)));
        Check(!closing.IsCompleted); f.Store.Release!.TrySetResult(); Ok(await writing); await closing;
        Check(f.Store.Writes == 1 && !f.Platform.Disposed); Ok(await f.Library.ExecuteAsync("music.library.list"));
    }
    private static async Task CloseTimeout()
    {
        await using var f = new Fixture(); f.ExpectedCloseTimeout = true;
        var entered = Signal(); var release = f.Hold();
        f.Platform.Handler = async _ => { entered.TrySetResult(); await release.Task; return E(new { marker = "late" }); };
        var work = f.Run("home", new { platform = "wy" }); await entered.Task;
        var closing = f.Owner.DisposeAsync().AsTask(); await Until(() => f.Clock.HasTimer(TimeSpan.FromSeconds(30)));
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        try { await closing; throw new InvalidOperationException("Expected bounded close failure"); }
        catch (TimeoutException) { }
        Check(!f.Platform.Disposed && !f.Store.Disposed);
        release.TrySetResult(); Error(await work, "CLOSED");
        Error(await f.Run("describe"), "CLOSED");
    }
    private static async Task Budgets()
    {
        await using var f = new Fixture();
        using var json = JsonDocument.Parse("{\"platform\":\"wy\",\"keyword\":\"" + new string('猫', 30000) + "\"}");
        Error(await f.Run("search", json.RootElement), "INVALID_REQUEST");
        Check(f.Platform.Calls.Length == 0);
        f.Platform.Handler = _ => Task.FromResult(E(new { data = new string('x', 512 * 1024) }));
        Check(S(Ok(await f.Run("home", new { platform = "wy" })), "data").Length == 512 * 1024);
        f.Platform.Handler = _ => Task.FromResult(E(new { ok = false, code = "AUTH_REQUIRED" }));
        Error(await f.Run("home", new { platform = "wy" }), "INVALID_RESPONSE");
        f.Platform.Handler = null;
        f.Platform.Pages = p => Page(p, 10001);
        Check(N(await f.Prepare(), "count") == 10001);
        var huge = O(Song("wy", 0)); huge["title"] = new string('x', 32 * 1024 * 1024);
        var large = J(huge);
        f.Platform.Pages = p => Page(p, 1, _ => large);
        Error(await f.Run("playlist.prepare", new { platform = "wy", id = "42" }), "LIMIT");
        Check(f.Store.Writes == 0);
        f.Platform.Pages = p => Page(p, 1); await f.Prepare(); await f.Prepare();
    }
}

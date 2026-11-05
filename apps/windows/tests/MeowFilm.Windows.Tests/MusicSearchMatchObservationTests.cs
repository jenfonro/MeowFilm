using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Windows.Tests;

public static class MusicSearchMatchObservationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("first fused batch distinguishes empty success failure and unchecked platforms", FirstBatch);
        yield return Case("later page failure updates already fused songs before other platforms settle", LaterFailure);
        yield return Case("later round source checks wait for their candidate fusion", LaterRoundFusion);
        yield return Case("get cached pages and repeated notices retain physical check times", CacheAge);
        yield return Case("account invalidation excludes late physical results and permits fresh checks", AccountInvalidation);
        yield return Case("cancelled physical results produce no matching observations", Cancellation);
        yield return Case("query replacement excludes the old physical result", Replacement);
        yield return Case("public observer account invalidation fences matching delivery", PublicObserverFence);
        yield return Case("matching observer account invalidation fences later observers", MatchingObserverFence);
        yield return Case("throwing matching observer preserves subsequent delivery and search lifecycle", ObserverFailure);
        yield return Case("synchronous matching observer dispose retains physical and notification barriers", SynchronousDispose);
        yield return Case("playlist results produce no matching observations", Playlists);
    }

    private static TestCase Case(string name, Func<Task> run) => new("Music search matching " + name, run);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Text(JsonElement value, string field) => value.GetProperty(field).GetString()!;
    private static string Id(JsonElement value) => Text(value, "id");
    private static bool Loading(JsonElement value) => value.GetProperty("loading").GetBoolean();
    private static JsonElement State(JsonElement value, string platform) => value.GetProperty("platforms")
        .EnumerateArray().Single(state => Text(state, "platform") == platform);
    private static long CheckedAt(JsonElement value, string platform) => State(value, platform).GetProperty("checkedAt").GetInt64();
    private static bool HasSource(JsonElement value, string platform) => value.GetProperty("items").EnumerateArray()
        .Any(song => song.GetProperty("sources").EnumerateArray().Any(source => Text(source, "source") == platform));
    private static JsonObject Song(string platform, string id = "track") =>
        MusicSongFixture.Online(platform, id, "Synthetic recording");
    private static JsonElement Page(MusicSearchRequest request, IEnumerable<JsonObject> items, bool more = false)
    {
        var page = JsonSerializer.SerializeToNode(new
        {
            platform = request.Platform, page = request.Page, limit = request.Limit * request.Pages,
            items = items.ToArray(), more,
            continuation = more ? new { page = request.Page + request.Pages } : null
        })!.AsObject();
        if (request.Pages > 1) page["offset"] = (request.Page - 1) * request.Limit;
        return JsonSerializer.SerializeToElement(page);
    }

    private static async Task FirstBatch()
    {
        var wy = Signal(); var tx = Signal(); var kw = Signal(); var kg = Signal();
        await using var f = new Fixture();
        f.Catalog.Supported = ["wy", "tx", "kw", "kg"];
        f.Catalog.Handler = async (request, _) =>
        {
            await (request.Platform switch { "wy" => wy.Task, "tx" => tx.Task, "kw" => kw.Task, _ => kg.Task });
            if (request.Platform == "kw") throw new MusicPlatformException("AUTH_EXPIRED", "kw", "search");
            return Page(request, request.Platform == "wy" ? [Song("wy")] : []);
        };
        using var observed = new Observations(f.Search);
        try
        {
            string id = Id(await f.Start(["wy", "tx", "kw", "kg", "mg"]));
            long firstCheck = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            wy.TrySetResult();
            var first = await observed.Until(value => Id(value) == id && HasSource(value, "wy") && !Loading(State(value, "wy")));
            Check(first.GetProperty("items").GetArrayLength() == 1 && CheckedAt(first, "wy") == firstCheck,
                "FIRST_SUCCESS_MISSING_FROM_FUSED_OBSERVATION");
            Check(State(first, "tx").GetProperty("page").GetInt32() == 0 &&
                Text(State(first, "tx"), "error") == "" && CheckedAt(first, "tx") == 0 && Loading(State(first, "tx")),
                "UNCHECKED_PLATFORM_LOOKED_SUCCESSFUL");
            Check(first.GetProperty("items")[0].EnumerateObject().Select(field => field.Name).ToHashSet()
                .SetEquals(["songId", "title", "artist", "album", "duration", "cover_url", "sources"]),
                "OBSERVATION_CHANGED_SONG_CONTRACT");
            f.Clock.Advance(TimeSpan.FromMilliseconds(100));
            long emptyCheck = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            tx.TrySetResult();
            var empty = await observed.Until(value => Id(value) == id && !Loading(State(value, "tx")));
            Check(State(empty, "tx").GetProperty("page").GetInt32() == 1 &&
                State(empty, "tx").GetProperty("count").GetInt32() == 0 &&
                Text(State(empty, "tx"), "error") == "" && CheckedAt(empty, "tx") == emptyCheck,
                "EMPTY_SUCCESS_LOST_PLATFORM_EVIDENCE");
            f.Clock.Advance(TimeSpan.FromMilliseconds(100));
            long failureCheck = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            kw.TrySetResult();
            var failed = await observed.Until(value => Id(value) == id && Text(State(value, "kw"), "error") == "AUTH_EXPIRED");
            Check(State(failed, "kw").GetProperty("page").GetInt32() == 0 && CheckedAt(failed, "kw") == failureCheck &&
                CheckedAt(failed, "kg") == 0 && Text(State(failed, "kg"), "error") == "",
                "FAILED_REQUEST_WAS_CONFUSED_WITH_UNCHECKED_OR_EMPTY_SUCCESS");
            kg.TrySetResult();
            var settled = await observed.Until(value => Id(value) == id && !Loading(value));
            Check(State(settled, "mg").GetProperty("page").GetInt32() == 0 &&
                Text(State(settled, "mg"), "error") == "UNSUPPORTED" && CheckedAt(settled, "mg") == 0,
                "UNSUPPORTED_PLATFORM_WAS_MARKED_PHYSICALLY_CHECKED");
            var published = await f.Call("get", id);
            Check(published.GetProperty("platforms").EnumerateArray().All(state => !state.TryGetProperty("checkedAt", out _)) &&
                JsonElement.DeepEquals(published.GetProperty("items"), settled.GetProperty("items")) &&
                observed.Values.All(value => HasSource(value, "wy")) && f.Catalog.Calls.Count == 4,
                "OBSERVATION_CHANGED_PUBLIC_SEARCH_OR_ADDED_REQUESTS");
        }
        finally { wy.TrySetResult(); tx.TrySetResult(); kw.TrySetResult(); kg.TrySetResult(); }
    }

    private static async Task LaterFailure()
    {
        var failure = Signal(); var other = Signal();
        await using var f = new Fixture();
        f.Catalog.Handler = async (request, _) =>
        {
            if (request.Platform == "wy") { await other.Task; return Page(request, []); }
            if (request.Page == 1) return Page(request, [Song("tx")], more: true);
            await failure.Task;
            throw new MusicPlatformException("AUTH_EXPIRED", "tx", "search");
        };
        using var observed = new Observations(f.Search);
        try
        {
            string id = Id(await f.Start(["wy", "tx"], pages: 2));
            await observed.Until(value => Id(value) == id && HasSource(value, "tx"));
            f.Clock.Advance(TimeSpan.FromMilliseconds(100));
            long checkedAt = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            failure.TrySetResult();
            var failed = await observed.Until(value => Id(value) == id && Text(State(value, "tx"), "error") == "AUTH_EXPIRED" &&
                !Loading(State(value, "tx")));
            Check(HasSource(failed, "tx") && Loading(State(failed, "wy")) && Loading(failed) &&
                State(failed, "tx").GetProperty("page").GetInt32() == 1 && CheckedAt(failed, "tx") == checkedAt,
                "LATER_FAILURE_DID_NOT_UPDATE_EXISTING_FUSED_SONGS");
            other.TrySetResult();
            await observed.Until(value => Id(value) == id && !Loading(value));
            Check(f.Catalog.Calls.Count == 3, "FAILURE_OBSERVATION_ADDED_REQUESTS");
        }
        finally { failure.TrySetResult(); other.TrySetResult(); }
    }

    private static async Task CacheAge()
    {
        await using var f = new Fixture();
        using var observed = new Observations(f.Search);
        long originalCheck = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        string id = Id(await f.Start(["wy", "tx"]));
        var first = await observed.Until(value => Id(value) == id && !Loading(value));
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        for (int read = 0; read < 3; read++) await f.Call("get", id);
        var duplicate = await f.Call("next", id);
        var repeated = await observed.Until(value => Id(value) == id &&
            value.GetProperty("revision").GetInt64() >= duplicate.GetProperty("revision").GetInt64());
        Check(new[] { "wy", "tx" }.All(platform => CheckedAt(first, platform) == originalCheck &&
            CheckedAt(repeated, platform) == originalCheck), "GET_OR_REPEATED_NOTICE_RENEWED_CHECK_TIME");
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        string cachedId = Id(await f.Start(["wy", "tx"]));
        var cached = await observed.Until(value => Id(value) == cachedId && !Loading(value));
        Check(cachedId != id && f.Catalog.Calls.Count == 2 &&
            new[] { "wy", "tx" }.All(platform => CheckedAt(cached, platform) == originalCheck) &&
            observed.Values.SelectMany(value => value.GetProperty("platforms").EnumerateArray())
                .Select(state => state.GetProperty("checkedAt").GetInt64()).All(at => at == 0 || at == originalCheck),
            "CACHED_PAGE_RENEWED_PHYSICAL_CHECK_TIME");
        f.Clock.Advance(TimeSpan.FromMinutes(41));
        long refreshedCheck = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        string expiredId = Id(await f.Start(["wy", "tx"]));
        var expired = await observed.Until(value => Id(value) == expiredId && !Loading(value));
        Check(f.Catalog.Calls.Count == 4 && new[] { "wy", "tx" }.All(platform => CheckedAt(expired, platform) == refreshedCheck),
            "EXPIRED_PAGE_DID_NOT_RECORD_A_NEW_PHYSICAL_CHECK");
    }

    private static async Task LaterRoundFusion()
    {
        var release = Signal();
        await using var f = new Fixture();
        f.Catalog.Handler = async (request, _) =>
        {
            if (request.Page == 1) return Page(request, request.Platform == "tx" ? [Song("tx")] : [], more: true);
            if (request.Platform == "tx") { await release.Task; return Page(request, []); }
            return Page(request, [Song("wy")]);
        };
        using var observed = new Observations(f.Search);
        try
        {
            string id = Id(await f.Start(["tx", "wy"]));
            await observed.Until(value => Id(value) == id && !Loading(value));
            f.Clock.Advance(TimeSpan.FromMilliseconds(100));
            await f.Call("next", id);
            var pending = await f.Until(id, value => State(value, "wy").GetProperty("page").GetInt32() == 2);
            Check(HasSource(pending, "tx") && !HasSource(pending, "wy") && Loading(State(pending, "tx")),
                "OBSERVATION_CHANGED_THE_PUBLIC_ROUND_BARRIER");
            release.TrySetResult();
            var settled = await observed.Until(value => Id(value) == id && !Loading(value) &&
                value.GetProperty("round").GetInt32() == 2);
            await f.Search.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(HasSource(settled, "tx") && HasSource(settled, "wy") &&
                observed.Values.All(value => State(value, "wy").GetProperty("page").GetInt32() < 2 || HasSource(value, "wy")) &&
                f.Catalog.Calls.Count == 4, "UNFUSED_SOURCE_CHECK_WAS_PUBLISHED_AS_NO_MATCH");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task AccountInvalidation()
    {
        var entered = Signal(); var release = Signal(); int wyCalls = 0;
        await using var f = new Fixture();
        f.Catalog.Handler = async (request, _) =>
        {
            if (request.Platform == "wy" && Interlocked.Increment(ref wyCalls) == 1)
            { entered.TrySetResult(); await release.Task; }
            return Page(request, [Song(request.Platform)]);
        };
        using var observed = new Observations(f.Search);
        try
        {
            string id = Id(await f.Start(["wy", "tx"]));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            f.Search.Invalidate("wy");
            release.TrySetResult();
            var invalidated = await observed.Until(value => Id(value) == id && !Loading(value) &&
                Text(State(value, "wy"), "error") == "STALE_SESSION");
            Check(!HasSource(invalidated, "wy") && HasSource(invalidated, "tx") && CheckedAt(invalidated, "wy") == 0 &&
                observed.Values.All(value => !HasSource(value, "wy")), "OLD_ACCOUNT_RESULT_REACHED_MATCHING_OBSERVER");
            f.Clock.Advance(TimeSpan.FromMilliseconds(100));
            long checkedAt = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            await f.Call("next", id);
            var fresh = await observed.Until(value => Id(value) == id && !Loading(value) && HasSource(value, "wy"));
            Check(CheckedAt(fresh, "wy") == checkedAt && Text(State(fresh, "wy"), "error") == "" &&
                f.Catalog.Calls.Count == 3, "FRESH_ACCOUNT_CHECK_WAS_DROPPED_OR_REUSED_INVALID_CACHE");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task Cancellation()
    {
        var entered = Signal(); var release = Signal();
        await using var f = new Fixture();
        f.Catalog.Handler = async (request, _) =>
        { entered.TrySetResult(); await release.Task; return Page(request, [Song("wy")]); };
        using var observed = new Observations(f.Search);
        try
        {
            string id = Id(await f.Start(["wy"]));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancelling = f.Call("cancel", id);
            release.TrySetResult();
            var cancelled = await cancelling.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Search.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(cancelled.GetProperty("cancelled").GetBoolean() && observed.Values.Length == 0,
                "CANCELLED_RESULT_REACHED_MATCHING_OBSERVER");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task Replacement()
    {
        var entered = Signal(); var release = Signal();
        await using var f = new Fixture();
        f.Catalog.Handler = async (request, _) =>
        {
            if (request.Keyword == "old") { entered.TrySetResult(); await release.Task; }
            return Page(request, [Song("wy", request.Keyword)]);
        };
        using var observed = new Observations(f.Search);
        try
        {
            await f.Start(["wy"], keyword: "old");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var replacing = f.Start(["wy"], keyword: "fresh");
            release.TrySetResult();
            string id = Id(await replacing.WaitAsync(TimeSpan.FromSeconds(10)));
            await observed.Until(value => Id(value) == id && !Loading(value));
            await f.Search.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(observed.Values.Length > 0 && observed.Values.All(value => Text(value, "keyword") == "fresh" &&
                !value.GetProperty("cancelled").GetBoolean()), "RETIRED_QUERY_REACHED_MATCHING_OBSERVER");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task PublicObserverFence()
    {
        await using var f = new Fixture(); int invalidated = 0;
        f.Search.Changed += value =>
        {
            if (HasSource(value, "wy") && Interlocked.Exchange(ref invalidated, 1) == 0) f.Search.Invalidate("wy");
        };
        using var observed = new Observations(f.Search);
        string id = Id(await f.Start(["wy"]));
        await observed.Until(value => Id(value) == id && !Loading(value) && Text(State(value, "wy"), "error") == "STALE_SESSION");
        await f.Search.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(invalidated == 1 && observed.Values.All(value => !HasSource(value, "wy") && CheckedAt(value, "wy") == 0),
            "PUBLIC_OBSERVER_INVALIDATION_DID_NOT_FENCE_MATCHING_NOTICE");
    }

    private static async Task MatchingObserverFence()
    {
        await using var f = new Fixture(); int invalidated = 0;
        f.Search.MatchingObserved += value =>
        {
            if (HasSource(value, "wy") && Interlocked.Exchange(ref invalidated, 1) == 0) f.Search.Invalidate("wy");
        };
        using var observed = new Observations(f.Search);
        string id = Id(await f.Start(["wy"]));
        await observed.Until(value => Id(value) == id && !Loading(value) && Text(State(value, "wy"), "error") == "STALE_SESSION");
        await f.Search.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(invalidated == 1 && observed.Values.All(value => !HasSource(value, "wy") && CheckedAt(value, "wy") == 0),
            "MATCHING_OBSERVER_INVALIDATION_DID_NOT_FENCE_LATER_OBSERVER");
    }

    private static async Task ObserverFailure()
    {
        await using var f = new Fixture(); int throws = 0;
        f.Search.MatchingObserved += _ => { Interlocked.Increment(ref throws); throw new InvalidOperationException("SYNTHETIC_OBSERVER_FAILURE"); };
        using var observed = new Observations(f.Search);
        string id = Id(await f.Start(["wy"]));
        var settled = await observed.Until(value => Id(value) == id && !Loading(value));
        var repeated = await f.Call("next", id);
        await observed.Until(value => Id(value) == id &&
            value.GetProperty("revision").GetInt64() >= repeated.GetProperty("revision").GetInt64());
        var cancelled = await f.Call("cancel", id);
        await f.Search.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(throws >= 2 && HasSource(settled, "wy") && cancelled.GetProperty("cancelled").GetBoolean() &&
            observed.Values.All(value => !value.GetProperty("cancelled").GetBoolean()) && f.Catalog.Calls.Count == 1,
            "THROWING_MATCHING_OBSERVER_BROKE_SEARCH_LIFECYCLE");
    }

    private static async Task SynchronousDispose()
    {
        var physicalEntered = Signal(); var physicalReleased = Signal(); var tokenCancelled = Signal();
        await using var f = new Fixture();
        f.Catalog.Handler = async (request, token) =>
        {
            if (request.Platform == "tx")
            {
                token.Register(() => tokenCancelled.TrySetResult());
                physicalEntered.TrySetResult(); await physicalReleased.Task;
            }
            return Page(request, [Song(request.Platform)]);
        };
        var issued = Signal<Task>(); var insideDone = Signal(); var releaseObserver = Signal();
        int once = 0, laterObservers = 0;
        f.Search.MatchingObserved += value =>
        {
            if (!HasSource(value, "wy") || Interlocked.Exchange(ref once, 1) != 0) return;
            try
            {
                physicalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var closing = f.Search.DisposeAsync().AsTask(); issued.TrySetResult(closing);
                closing.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                insideDone.TrySetResult(); releaseObserver.Task.GetAwaiter().GetResult();
            }
            catch (Exception error) { insideDone.TrySetException(error); }
        };
        f.Search.MatchingObserved += _ => Interlocked.Increment(ref laterObservers);
        Task? external = null;
        try
        {
            await f.Start(["wy", "tx"]);
            var inside = await issued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await tokenCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            external = f.Search.DisposeAsync().AsTask();
            Check(!inside.IsCompleted && !external.IsCompleted, "MATCHING_DISPOSE_SKIPPED_PHYSICAL_DRAIN");
            physicalReleased.TrySetResult();
            await insideDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(inside.IsCompletedSuccessfully && !external.IsCompleted, "MATCHING_DISPOSE_SKIPPED_NOTIFICATION_BARRIER");
            releaseObserver.TrySetResult();
            await external.WaitAsync(TimeSpan.FromSeconds(10));
            Check(laterObservers == 0 && f.Catalog.Disposed == 0, "CLOSED_MATCHING_NOTICE_LEAKED_OR_BORROWED_CATALOG_DISPOSED");
        }
        finally
        {
            physicalReleased.TrySetResult(); releaseObserver.TrySetResult();
            await f.Search.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (external is not null) await external.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task Playlists()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (request, _) => Task.FromResult(Page(request,
            [new JsonObject { ["id"] = "playlist", ["name"] = "Synthetic playlist", ["source"] = request.Platform }]));
        using var observed = new Observations(f.Search);
        string id = Id(await f.Start(["wy", "tx"], kind: "playlists"));
        var settled = await f.Idle(id);
        await f.Search.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(settled.GetProperty("items").GetArrayLength() == 2 && observed.Values.Length == 0 && f.Catalog.Calls.Count == 2,
            "PLAYLIST_SEARCH_PRODUCED_MATCHING_OBSERVATIONS");
    }

    private sealed class Observations : IDisposable
    {
        private readonly MusicSearchCoordinator search;
        private readonly ConcurrentQueue<JsonElement> values = new();
        internal Observations(MusicSearchCoordinator search)
        { this.search = search; search.MatchingObserved += values.Enqueue; }
        internal JsonElement[] Values => values.ToArray();
        internal async Task<JsonElement> Until(Func<JsonElement, bool> condition)
        {
            var done = Signal<JsonElement>();
            void Updated(JsonElement value) { if (condition(value)) done.TrySetResult(value); }
            search.MatchingObserved += Updated;
            try
            {
                foreach (var value in values) Updated(value);
                return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { search.MatchingObserved -= Updated; }
        }
        public void Dispose() => search.MatchingObserved -= values.Enqueue;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Clock Clock = new();
        internal readonly Catalog Catalog = new();
        internal readonly MusicSearchCoordinator Search;
        internal Fixture() => Search = new(Catalog, Clock);
        internal Task<JsonElement> Start(string[] platforms, string kind = "songs", string keyword = "matching", int pages = 1) =>
            Search.ExecuteAsync("music.search.start", new { keyword, kind, platforms, pages, concurrency = 1, cacheMinutes = 60 });
        internal Task<JsonElement> Call(string method, string id) => Search.ExecuteAsync("music.search." + method, new { id });
        internal Task<JsonElement> Idle(string id) => Until(id, value => !Loading(value));
        internal async Task<JsonElement> Until(string id, Func<JsonElement, bool> condition)
        {
            var done = Signal<JsonElement>();
            void Updated(JsonElement value) { if (Id(value) == id && condition(value)) done.TrySetResult(value); }
            Search.Changed += Updated;
            try
            {
                Updated(await Call("get", id));
                return await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { Search.Changed -= Updated; }
        }
        public ValueTask DisposeAsync() => Search.DisposeAsync();
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero)
            .AddTicks(Interlocked.Read(ref ticks));
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private sealed class Catalog : IMusicPlatformCatalog
    {
        internal string[] Supported = ["wy", "tx", "kw", "kg", "mg"];
        internal readonly ConcurrentQueue<MusicSearchRequest> Calls = new();
        internal Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> Handler =
            (request, _) => Task.FromResult(Page(request, [Song(request.Platform)]));
        internal int Disposed;
        public Task<JsonElement> SearchAsync(MusicSearchRequest request, CancellationToken token = default)
        { Calls.Enqueue(request); return Handler(request, token); }
        public JsonElement Describe() => JsonSerializer.SerializeToElement(new
        { platforms = Supported.ToDictionary(platform => platform, _ => new { search = new[] { "songs", "playlists" } }) });
        public void Invalidate(string? platform = null) { }
        public void Dispose() => Interlocked.Increment(ref Disposed);
        private static Task<JsonElement> Unused() => throw new InvalidOperationException("UNEXPECTED_NON_SEARCH_CALL");
        public Task<JsonElement> SongAsync(MusicSongRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> PlaylistAsync(MusicPlaylistRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> HomeAsync(MusicHomeRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> ChannelsAsync(MusicChannelsRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> ChannelAsync(MusicChannelRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> PersonalPlaylistsAsync(MusicPersonalPlaylistsRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> DailyAsync(MusicDailyRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> FmAsync(MusicFmRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> SimilarAsync(MusicSimilarRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> HeartbeatAsync(MusicHeartbeatRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> QueueRecommendationsAsync(MusicQueueRecommendationsRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> AlbumAsync(MusicAlbumRequest r, CancellationToken t = default) => Unused();
    }
}

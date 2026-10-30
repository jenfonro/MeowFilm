using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Windows.Tests;

public static class MusicAggregateSearchTests
{
    private static readonly string[] Five = ["wy", "tx", "kw", "kg", "mg"];
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Aggregate search five explicit platforms and genuine missing-provider partial", FivePlatforms);
        yield return new("Aggregate search two kinds retain ids and tab reads never refetch", DualKinds);
        yield return new("Aggregate search changing one kind platform set preserves the other tab", ReplaceOneKind);
        yield return new("Aggregate search later native providers can satisfy existing KW and MG retries", LaterProviders);
        yield return new("Aggregate search two kinds share four transport-compatible physical request slots", SharedSlots);
        yield return new("Aggregate search RRF consensus outranks single-platform first result", Consensus);
        yield return new("Aggregate search same-platform editions count only their best rank", BestRank);
        yield return new("Aggregate search rank ties and representatives ignore platform arrival order", Deterministic);
        yield return new("Aggregate search same title different singer never merges", () => Separate("Singer B", 180, "Song"));
        yield return new("Aggregate search incompatible duration never merges", () => Separate("Singer A", 220, "Song"));
        yield return new("Aggregate search recording version never merges", () => Separate("Singer A", 180, "Song (Live)"));
        yield return new("Aggregate search zero duration is not cross-platform identity evidence", () => Separate("Singer A", 0, "Song"));
        yield return new("Aggregate search duration compatibility does not transitively merge", DurationChain);
        yield return new("Aggregate search KG native discriminators remain separate candidates", KgDiscriminators);
        yield return new("Aggregate search playlists use platform plus native id not names", PlaylistIdentity);
        yield return new("Aggregate search preserves original rank holes after paged dedup", RankHoles);
        yield return new("Aggregate search honors native forward continuation without refetch", Continuation);
        yield return new("Aggregate search failure retry affects only failed platform and uncommitted page", Retry);
        yield return new("Aggregate search retry after page two failure never refetches page one", RetrySecondPage);
        yield return new("Aggregate search concurrent next and retry never double-admit", DuplicateAdmission);
        yield return new("Aggregate search repeated pages stop after four stalled pages", () => Stalled(false));
        yield return new("Aggregate search empty pages stop after four stalled pages", () => Stalled(true));
        yield return new("Aggregate search hundred-page cap cannot be bypassed by retry", PageCap);
        foreach (int count in new[] { 1, 3, 5 })
        {
            int selected = count;
            yield return new($"Aggregate search candidate budget min(2000,5000/{count})", () => CandidateCap(selected));
        }
        yield return new("Aggregate search virtual 25 second deadline retains physical drain", Timeout);
        yield return new("Aggregate search new keyword drains both old kinds, joins its duplicate and rejects a different replacement", ReplaceBoth);
        yield return new("Aggregate search cancelled replacement waiter cannot start its query later", CancelReplacement);
        yield return new("Aggregate search account invalidation clears one platform in both kinds", InvalidateBoth);
        yield return new("Aggregate search account invalidation fences late results and retries from page one", InvalidateLate);
        yield return new("Aggregate search injected per-request delegate isolates auth frames across both kinds", DelegateFrames);
        yield return new("Aggregate search injected auth failure preserves precise platform error", DelegateAuthError);
        yield return new("Aggregate search synchronous account fence inside delegate does not retire other platforms or query ids", DelegateAccountFence);
        yield return new("Aggregate search invalidate and cancelled shutdown waiter retain injected physical drain", DelegateDrain);
        yield return new("Aggregate search observer-triggered account fence suppresses remaining stale deliveries", ObserverFence);
        yield return new("Aggregate search cancelled close resumes admission without replaying old queries", ReversibleClose);
        yield return new("Aggregate search dispose fences concurrent resume and borrows catalog", DisposeDuringResume);
        yield return new("Aggregate search observers cannot corrupt snapshots or break publication", Observers);
        yield return new("Aggregate search malformed page does not commit a partial page", MalformedPage);
        yield return new("Aggregate search foreign platform page is rejected independently", ForeignPage);
        yield return new("Aggregate search long native ids retain exact identity", LongId);
        yield return new("Aggregate search empty selection is explicit and makes no catalog calls", EmptySelection);
        yield return new("Aggregate search rejects malformed and cancelled admission without IO", InvalidInput);
    }

    private static void Check(bool value, string message = "ASSERTION_FAILED")
    { if (!value) throw new InvalidOperationException(message); }
    private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value);
    private static string S(JsonElement value, string field) => value.GetProperty(field).GetString()!;
    private static JsonElement State(JsonElement value, string platform) => value.GetProperty("platforms").EnumerateArray().Single(s => S(s, "platform") == platform);
    private static void Code(JsonElement result, string code) => Check(!result.GetProperty("ok").GetBoolean() && S(result, "code") == code, "expected " + code);
    private static JsonObject Song(string platform, string id, string title = "Song",
        string artist = "Singer A", double duration = 180, string? hash = null)
    {
        var native = new JsonObject();
        if (hash is not null) native["hash"] = hash;
        return MusicSongFixture.Online(platform, id, title, artist, "Album", duration, native: native);
    }
    private static string NativeId(JsonElement song) => MusicSongFixture.NativeId(song);
    private static void CurrentSongSurface(JsonElement song)
    {
        foreach (string field in new[] { "id", "_rank", "_ranks", "_rankScore", "matchedPlatforms",
            "plugin_entry_path", "_isOnlineObj", "dedup_key" })
            Check(!song.TryGetProperty(field, out _), "temporary song field leaked: " + field);
    }
    private static JsonObject Card(string platform, string id, string name = "Same name") =>
        new() { ["id"] = id, ["name"] = name, ["source"] = platform, ["count"] = 1, ["creator"] = "Synthetic" };
    private static JsonElement Page(MusicSearchRequest request, IEnumerable<JsonObject> items, bool more = false, int? next = null) =>
        E(new { platform = request.Platform, page = request.Page, limit = request.Limit, items = items.ToArray(), more,
            continuation = more ? new { page = next ?? request.Page + 1 } : null });
    private static JsonElement Single(MusicSearchRequest r) => Page(r, [r.Kind == MusicSearchKind.Songs
        ? Song(r.Platform, r.Platform + r.Page, r.Platform + " Song " + r.Page) : Card(r.Platform, r.Platform + r.Page)]);

    private sealed class Catalog : IMusicPlatformCatalog
    {
        internal string[] Supported = Five;
        internal Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> Handler = (r, _) => Task.FromResult(Single(r));
        internal readonly ConcurrentQueue<MusicSearchRequest> Calls = new();
        private readonly object gate = new();
        private readonly List<(int Count, TaskCompletionSource Done)> waiting = [];
        internal int Disposed, Active, Maximum;
        private int started;
        public Task<JsonElement> SearchAsync(MusicSearchRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(request);
            lock (gate)
            {
                Active++; Maximum = Math.Max(Maximum, Active);
            }
            return Run();
            async Task<JsonElement> Run()
            {
                try
                {
                    var task = Handler(request, cancellationToken);
                    lock (gate)
                    {
                        started++;
                        foreach (var item in waiting.Where(w => started >= w.Count).ToArray())
                        { item.Done.TrySetResult(); waiting.Remove(item); }
                    }
                    return await task;
                }
                finally { lock (gate) Active--; }
            }
        }
        internal Task CountAsync(int count)
        {
            lock (gate)
            {
                if (started >= count) return Task.CompletedTask;
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting.Add((count, done)); return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        public JsonElement Describe() => E(new { platforms = Supported.ToDictionary(p => p, _ => new { search = new[] { "songs", "playlists" } }) });
        public void Invalidate(string? platform = null) { }
        public void Dispose() => Disposed++;
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
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Catalog Catalog = new();
        internal readonly MusicSearchCoordinator Search;
        internal Fixture(TimeProvider? clock = null,
            Func<Catalog, MusicSearchRequest, CancellationToken, Task<JsonElement>>? searchAsync = null)
            => Search = new(Catalog, clock, searchAsync is null ? null : (r, t) => searchAsync(Catalog, r, t));
        internal Task<JsonElement> Start(string[] platforms, string kind = "songs", string word = "needle", CancellationToken token = default) =>
            Search.ExecuteAsync("music.search.start", new { keyword = word, kind, platforms }, token);
        internal Task<JsonElement> Call(string method, string id, string? platform = null, CancellationToken token = default) =>
            Search.ExecuteAsync("music.search." + method, platform is null ? new { id } : (object)new { id, platform }, token);
        internal Task<JsonElement> Idle(string id) => Until(id, r => !r.GetProperty("loading").GetBoolean());
        internal async Task<JsonElement> Until(string id, Func<JsonElement, bool> condition)
        {
            var done = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Updated(JsonElement value) { if (S(value, "id") == id && condition(value)) done.TrySetResult(value); }
            Search.Changed += Updated;
            try
            {
                var value = await Call("get", id); Updated(value);
                return await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { Search.Changed -= Updated; }
        }
        public ValueTask DisposeAsync() => Search.DisposeAsync();
    }

    private static async Task FivePlatforms()
    {
        await using var f = new Fixture(); f.Catalog.Supported = ["wy", "tx", "kg"];
        var start = await f.Start(Five); var result = await f.Idle(S(start, "id"));
        Check(result.GetProperty("platforms").GetArrayLength() == 5 && f.Catalog.Calls.Count == 3);
        Check(S(State(result, "kw"), "error") == "UNSUPPORTED" && S(State(result, "mg"), "error") == "UNSUPPORTED");
        Check(result.GetProperty("partial").GetBoolean() && S(result, "code") == "PARTIAL");
        await f.Call("retry", S(start, "id"), "kw"); result = await f.Idle(S(start, "id"));
        Check(f.Catalog.Calls.Count == 3 && S(State(result, "kw"), "error") == "UNSUPPORTED", "unsupported is not success or silent filtering");
    }
    private static async Task DualKinds()
    {
        await using var f = new Fixture();
        string songs = S(await f.Start(["wy", "tx"]), "id"), lists = S(await f.Start(["wy", "tx"], "playlists"), "id");
        Check(songs != lists); await f.Idle(songs); await f.Idle(lists);
        Check(S(await f.Start(["tx", "wy"]), "id") == songs);
        Check(S(await f.Start(["wy", "tx"], "playlists"), "id") == lists);
        await f.Call("get", songs); await f.Call("get", lists);
        Check(f.Catalog.Calls.Count == 4);
    }
    private static async Task ReplaceOneKind()
    {
        await using var f = new Fixture();
        string songs = S(await f.Start(["wy"]), "id"), lists = S(await f.Start(["wy"], "playlists"), "id");
        await f.Idle(songs); await f.Idle(lists);
        string changed = S(await f.Start(["tx"]), "id"); await f.Idle(changed);
        Check(changed != songs && S(await f.Call("get", lists), "id") == lists && f.Catalog.Calls.Count == 3);
        Code(await f.Call("get", songs), "SEARCH_NOT_FOUND");
    }
    private static async Task LaterProviders()
    {
        await using var f = new Fixture(); f.Catalog.Supported = ["wy", "tx", "kg"];
        string id = S(await f.Start(Five), "id"); await f.Idle(id);
        f.Catalog.Supported = Five;
        await f.Call("retry", id, "kw"); await f.Idle(id);
        await f.Call("retry", id, "mg"); var result = await f.Idle(id);
        Check(!result.GetProperty("partial").GetBoolean() && f.Catalog.Calls.Count == 5 &&
            State(result, "kw").GetProperty("count").GetInt32() == 1 && State(result, "mg").GetProperty("count").GetInt32() == 1);
    }
    private static async Task SharedSlots()
    {
        await using var f = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Catalog.Handler = async (r, _) => { await release.Task; return Single(r); };
        try
        {
            string songs = S(await f.Start(Five), "id"), lists = S(await f.Start(Five, "playlists"), "id");
            await f.Catalog.CountAsync(4); Check(f.Catalog.Maximum <= 4 && f.Catalog.Calls.Count == 4);
            release.SetResult();
            var songResult = await f.Idle(songs);
            var playlistResult = await f.Idle(lists);
            Check(f.Catalog.Calls.Count == 10 && f.Catalog.Maximum <= 4 &&
                !songResult.GetProperty("partial").GetBoolean() &&
                !playlistResult.GetProperty("partial").GetBoolean() &&
                songResult.GetProperty("items").GetArrayLength() == 5 &&
                playlistResult.GetProperty("items").GetArrayLength() == 5);
        }
        finally { release.TrySetResult(); }
    }
    private static async Task Consensus()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Song(r.Platform, "solo", r.Platform + " solo"), Song(r.Platform, "shared", "Shared")]));
        var result = await f.Idle(S(await f.Start(["wy", "tx"]), "id"));
        var rows = result.GetProperty("items"); Check(rows.GetArrayLength() == 3);
        Check(S(rows[0], "title") == "Shared" && rows[0].GetProperty("sources").GetArrayLength() == 2);
        CurrentSongSurface(rows[0]);
    }
    private static async Task BestRank()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) =>
        {
            if (r.Platform == "wy")
                return Task.FromResult(Page(r, [
                    Song("wy", "shared-a", "Shared"),
                    Song("wy", "filler-a", "Filler A"),
                    Song("wy", "competitor", "Competitor"),
                    Song("wy", "shared-b", "Shared")
                ]));
            return Task.FromResult(Page(r, [
                Song("tx", "filler-b", "Filler B"),
                Song("tx", "filler-c", "Filler C"),
                Song("tx", "competitor", "Competitor"),
                Song("tx", "shared-a", "Shared")
            ]));
        };
        var result = await f.Idle(S(await f.Start(["wy", "tx"]), "id"));
        var row = result.GetProperty("items")[0];
        Check(S(row, "title") == "Shared" && row.GetProperty("sources").GetArrayLength() == 3,
            "best same-platform rank must win fusion without exposing rank metadata");
        CurrentSongSurface(row);
    }
    private static async Task Deterministic()
    {
        async Task<string> Run(string[] order)
        {
            await using var f = new Fixture();
            f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Song(r.Platform, "shared"), Song(r.Platform, "unique", r.Platform + " unique")]));
            return (await f.Idle(S(await f.Start(order), "id"))).GetProperty("items").GetRawText();
        }
        Check(await Run(["wy", "tx", "kg"]) == await Run(["kg", "tx", "wy"]), "hash representatives and RRF ties must be deterministic");
    }
    private static async Task Separate(string artist, double duration, string title)
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [r.Platform == "wy" ? Song("wy", "a") : Song("tx", "b", title, artist, duration)]));
        Check((await f.Idle(S(await f.Start(["wy", "tx"]), "id"))).GetProperty("items").GetArrayLength() == 2);
    }
    private static async Task DurationChain()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Song(r.Platform, "id", duration: r.Platform == "wy" ? 100 : r.Platform == "tx" ? 103 : 106)]));
        Check((await f.Idle(S(await f.Start(["wy", "tx", "kg"]), "id"))).GetProperty("items").GetArrayLength() == 2);
    }
    private static async Task KgDiscriminators()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Song("kg", "same", duration: 0, hash: "aa"), Song("kg", "same", duration: 0, hash: "bb")]));
        var result = await f.Idle(S(await f.Start(["kg"]), "id"));
        Check(result.GetProperty("items").GetArrayLength() == 2 && State(result, "kg").GetProperty("count").GetInt32() == 2);
    }
    private static async Task PlaylistIdentity()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Card(r.Platform, "same"), Card(r.Platform, "other"), Card(r.Platform, "same")]));
        var result = await f.Idle(S(await f.Start(["wy", "tx"], "playlists"), "id"));
        Check(result.GetProperty("items").GetArrayLength() == 4);
        Check(result.GetProperty("items").EnumerateArray()
            .Select(r => S(r, "source") + ":" + S(r, "id"))
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(["wy:same", "wy:other", "tx:same", "tx:other"]));
        Check(result.GetProperty("items").EnumerateArray().All(r =>
            !r.TryGetProperty("matchedPlatforms", out _) && !r.TryGetProperty("_rankScore", out _) &&
            !r.TryGetProperty("_ranks", out _)));
    }
    private static async Task RankHoles()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, r.Page == 1
            ? [Song("wy", "a", "A"), Song("wy", "b", "B")]
            : [Song("wy", "a", "A"), Song("wy", "c", "C")], r.Page == 1));
        string id = S(await f.Start(["wy"]), "id"); await f.Idle(id); await f.Call("next", id);
        var result = await f.Idle(id);
        var third = result.GetProperty("items").EnumerateArray().Single(r => NativeId(r) == "c");
        Check(result.GetProperty("items").GetArrayLength() == 3 &&
            S(result.GetProperty("items").EnumerateArray().Last(), "title") == "C");
        CurrentSongSurface(third);
    }
    private static async Task Continuation()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Song("wy", r.Page.ToString(), "Page " + r.Page)], r.Page == 1, 3));
        string id = S(await f.Start(["wy"]), "id"); await f.Idle(id); await f.Call("next", id); var result = await f.Idle(id);
        Check(f.Catalog.Calls.Select(r => r.Page).SequenceEqual([1, 3]) && State(result, "wy").GetProperty("page").GetInt32() == 3);
    }
    private static async Task Retry()
    {
        await using var f = new Fixture(); int failures = 0;
        f.Catalog.Handler = (r, _) => r.Platform == "tx" && Interlocked.Increment(ref failures) == 1
            ? Task.FromException<JsonElement>(new InvalidOperationException("synthetic-sensitive-message"))
            : Task.FromResult(Single(r));
        string id = S(await f.Start(["wy", "tx"]), "id"); var initial = await f.Idle(id);
        Check(initial.GetProperty("partial").GetBoolean() && !initial.GetRawText().Contains("synthetic-sensitive-message"));
        await f.Call("next", id); Check(f.Catalog.Calls.Count == 2, "next cannot implicitly retry errors");
        await f.Call("retry", id, "tx"); var result = await f.Idle(id);
        Check(!result.GetProperty("partial").GetBoolean() && f.Catalog.Calls.Count == 3);
        Check(f.Catalog.Calls.Last() is { Platform: "tx", Page: 1, Refresh: true } && f.Catalog.Calls.Count(r => r.Platform == "wy") == 1);
    }
    private static async Task RetrySecondPage()
    {
        await using var f = new Fixture(); int failures = 0;
        f.Catalog.Handler = (r, _) => r.Page == 2 && Interlocked.Increment(ref failures) == 1
            ? Task.FromException<JsonElement>(new InvalidOperationException("synthetic-page-two"))
            : Task.FromResult(Page(r, [Song(r.Platform, r.Page.ToString(), "Page " + r.Page)], r.Page == 1));
        string id = S(await f.Start(["wy"]), "id"); await f.Idle(id);
        await f.Call("next", id); var failed = await f.Idle(id);
        Check(State(failed, "wy").GetProperty("page").GetInt32() == 1 && State(failed, "wy").GetProperty("count").GetInt32() == 1);
        await f.Call("retry", id, "wy"); var result = await f.Idle(id);
        Check(f.Catalog.Calls.Select(r => r.Page).SequenceEqual([1, 2, 2]) &&
            f.Catalog.Calls.Last().Refresh && State(result, "wy").GetProperty("count").GetInt32() == 2);
    }
    private static async Task DuplicateAdmission()
    {
        await using var f = new Fixture(); var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Catalog.Handler = (_, _) => response.Task;
        try
        {
            string id = S(await f.Start(["wy"]), "id"); await f.Catalog.CountAsync(1);
            await Task.WhenAll(f.Call("next", id), f.Call("next", id), f.Call("retry", id, "wy"));
            Check(f.Catalog.Calls.Count == 1); response.SetResult(Single(f.Catalog.Calls.Single())); await f.Idle(id);
        }
        finally { response.TrySetResult(E(new { })); }
    }
    private static async Task Stalled(bool empty)
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, empty ? [] : [Song("wy", "same")], true));
        string id = S(await f.Start(["wy"]), "id"); var result = await f.Idle(id);
        for (int i = 0; i < (empty ? 3 : 4); i++) { await f.Call("next", id); result = await f.Idle(id); }
        Check(S(State(result, "wy"), "error") == "SEARCH_LIMIT" && !result.GetProperty("more").GetBoolean());
        int count = f.Catalog.Calls.Count; await f.Call("retry", id, "wy"); await f.Call("next", id);
        Check(f.Catalog.Calls.Count == count && count == (empty ? 4 : 5));
    }
    private static async Task PageCap()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Song("wy", r.Page.ToString(), "Song " + r.Page)], true));
        string id = S(await f.Start(["wy"]), "id"); var result = await f.Idle(id);
        for (int page = 2; page <= 100; page++) { await f.Call("next", id); result = await f.Idle(id); }
        Check(State(result, "wy").GetProperty("page").GetInt32() == 100 && S(State(result, "wy"), "error") == "SEARCH_LIMIT");
        await f.Call("retry", id, "wy"); Check(f.Catalog.Calls.Count == 100);
    }
    private static async Task CandidateCap(int platforms)
    {
        await using var f = new Fixture(); f.Catalog.Supported = ["wy"];
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, Enumerable.Range(0, 30).Select(i =>
            Song("wy", $"{r.Page}-{i}", $"Song {r.Page}-{i}")), true));
        string id = S(await f.Start(Five.Take(platforms).ToArray()), "id"); var result = await f.Idle(id);
        int budget = Math.Min(2000, 5000 / platforms), pages = (budget + 29) / 30;
        for (int page = 2; page <= pages; page++) { await f.Call("next", id); result = await f.Idle(id); }
        Check(State(result, "wy").GetProperty("count").GetInt32() == budget && S(State(result, "wy"), "error") == "SEARCH_LIMIT");
        await f.Call("retry", id, "wy"); Check(f.Catalog.Calls.Count == pages);
    }

    private sealed class Held
    {
        internal readonly ConcurrentQueue<(MusicSearchRequest Request, CancellationToken Token, TaskCompletionSource<JsonElement> Reply)> Calls = new();
        internal Task<JsonElement> Handle(MusicSearchRequest r, CancellationToken token)
        {
            var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Enqueue((r, token, reply)); return reply.Task;
        }
        internal void Release() { foreach (var call in Calls) call.Reply.TrySetResult(Single(call.Request)); }
    }
    private static async Task Timeout()
    {
        var clock = new Clock(); await using var f = new Fixture(clock); var held = new Held(); f.Catalog.Handler = held.Handle;
        try
        {
            string id = S(await f.Start(["wy"]), "id"); await f.Catalog.CountAsync(1);
            clock.Advance(TimeSpan.FromSeconds(24)); Check(!held.Calls.Single().Token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(1));
            var result = await f.Until(id, r => S(State(r, "wy"), "error") == "TIMEOUT");
            Check(result.GetProperty("loading").GetBoolean() && held.Calls.Single().Token.IsCancellationRequested);
            await f.Call("retry", id, "wy"); Check(f.Catalog.Calls.Count == 1);
            var cancel = f.Call("cancel", id); Check(!cancel.IsCompleted, "cancel must wait for physical settlement");
            held.Release(); result = await cancel;
            Check(!result.GetProperty("loading").GetBoolean() && result.GetProperty("items").GetArrayLength() == 0);
        }
        finally { held.Release(); }
    }
    private static async Task ReplaceBoth()
    {
        await using var f = new Fixture(); var held = new Held(); f.Catalog.Handler = held.Handle;
        try
        {
            string oldSongs = S(await f.Start(["wy"]), "id"), oldLists = S(await f.Start(["wy"], "playlists"), "id");
            await f.Catalog.CountAsync(2);
            var next = f.Start(["tx"], word: "replacement"); Check(!next.IsCompleted);
            var same = f.Start(["tx"], word: "replacement"); Check(!same.IsCompleted);
            using var cancelled = new CancellationTokenSource();
            var cancelledWaiter = f.Start(["tx"], word: "replacement", token: cancelled.Token);
            Check(!cancelledWaiter.IsCompleted); cancelled.Cancel();
            Code(await cancelledWaiter, "CANCELLED");
            Code(await f.Start(["kg"], word: "extra"), "SEARCH_BUSY");
            f.Catalog.Handler = (r, _) => Task.FromResult(Single(r));
            held.Release();
            var joined = await Task.WhenAll(next, same);
            string id = S(joined[0], "id");
            Check(S(joined[1], "id") == id, "same replacement must share one admitted session");
            var result = await f.Idle(id);
            Code(await f.Call("get", oldSongs), "SEARCH_NOT_FOUND"); Code(await f.Call("get", oldLists), "SEARCH_NOT_FOUND");
            Check(S(result, "keyword") == "replacement" && f.Catalog.Calls.Count == 3 && held.Calls.All(c => c.Token.IsCancellationRequested));
        }
        finally { held.Release(); }
    }
    private static async Task CancelReplacement()
    {
        await using var f = new Fixture(); var held = new Held(); f.Catalog.Handler = held.Handle;
        using var caller = new CancellationTokenSource();
        try
        {
            string old = S(await f.Start(["wy"]), "id"); await f.Catalog.CountAsync(1);
            var next = f.Start(["tx"], word: "abandoned", token: caller.Token); caller.Cancel(); Code(await next, "CANCELLED");
            held.Release(); await f.Call("cancel", old); await f.Search.PrepareShutdownAsync();
            Check(f.Catalog.Calls.Count == 1); await f.Search.CancelShutdownPreparationAsync();
            f.Catalog.Handler = (r, _) => Task.FromResult(Single(r));
            await f.Idle(S(await f.Start(["tx"], word: "fresh"), "id")); Check(f.Catalog.Calls.Count == 2);
        }
        finally { held.Release(); }
    }
    private static async Task InvalidateBoth()
    {
        await using var f = new Fixture();
        string songs = S(await f.Start(["wy", "tx"]), "id"), lists = S(await f.Start(["wy", "tx"], "playlists"), "id");
        await f.Idle(songs); await f.Idle(lists); f.Search.Invalidate("wy");
        foreach (string id in new[] { songs, lists })
        {
            var result = await f.Idle(id);
            Check(State(result, "wy").GetProperty("count").GetInt32() == 0 && S(State(result, "wy"), "error") == "STALE_SESSION");
            Check(State(result, "tx").GetProperty("count").GetInt32() == 1);
        }
        Check(f.Catalog.Calls.Count == 4);
        await f.Call("retry", songs, "wy"); var retried = await f.Idle(songs);
        Check(!retried.GetProperty("partial").GetBoolean() && f.Catalog.Calls.Last() is { Platform: "wy", Page: 1, Refresh: true });
        Check(S(State(await f.Call("get", lists), "wy"), "error") == "STALE_SESSION");
    }
    private static async Task InvalidateLate()
    {
        await using var f = new Fixture(); var held = new Held(); f.Catalog.Handler = held.Handle;
        try
        {
            string id = S(await f.Start(["wy"]), "id"); await f.Catalog.CountAsync(1); f.Search.Invalidate("wy");
            await f.Call("retry", id, "wy"); Check(f.Catalog.Calls.Count == 1);
            held.Release(); var retired = await f.Idle(id);
            Check(retired.GetProperty("items").GetArrayLength() == 0 && S(State(retired, "wy"), "error") == "STALE_SESSION");
            f.Catalog.Handler = (r, _) => Task.FromResult(Single(r));
            await f.Call("retry", id, "wy"); await f.Idle(id); Check(f.Catalog.Calls.Last() is { Page: 1, Refresh: true });
        }
        finally { held.Release(); }
    }
    private static async Task DelegateFrames()
    {
        var frame = new AsyncLocal<MusicSearchRequest?>();
        int entered = 0, exited = 0;
        await using var f = new Fixture(searchAsync: async (catalog, request, token) =>
        {
            Check(frame.Value is null, "request frame leaked from another physical search");
            frame.Value = request; Interlocked.Increment(ref entered);
            try
            {
                await Task.Yield();
                return await catalog.SearchAsync(request, token);
            }
            finally { frame.Value = null; Interlocked.Increment(ref exited); }
        });
        f.Catalog.Handler = (r, _) =>
        {
            Check(ReferenceEquals(frame.Value, r), "delegate bypassed or frame crossed requests");
            return Task.FromResult(Single(r));
        };
        string songs = S(await f.Start(["wy", "tx"]), "id"), lists = S(await f.Start(["wy", "tx"], "playlists"), "id");
        var a = await f.Idle(songs); var b = await f.Idle(lists);
        Check(!a.GetProperty("partial").GetBoolean() && !b.GetProperty("partial").GetBoolean());
        Check(entered == 4 && exited == 4 && f.Catalog.Calls.Count == 4 && frame.Value is null);
    }
    private static async Task DelegateAuthError()
    {
        await using var f = new Fixture(searchAsync: (catalog, request, token) => request.Platform == "wy"
            ? Task.FromException<JsonElement>(new MusicPlatformException("AUTH_EXPIRED", "wy", "search"))
            : catalog.SearchAsync(request, token));
        var result = await f.Idle(S(await f.Start(["wy", "tx"]), "id"));
        Check(S(State(result, "wy"), "error") == "AUTH_EXPIRED" && State(result, "wy").GetProperty("count").GetInt32() == 0);
        Check(State(result, "tx").GetProperty("count").GetInt32() == 1 && f.Catalog.Calls.Single().Platform == "tx");
    }
    private static async Task DelegateAccountFence()
    {
        Fixture? owner = null; int invalidated = 0;
        await using var f = new Fixture(searchAsync: (catalog, request, token) =>
        {
            if (request.Platform == "wy" && Interlocked.Exchange(ref invalidated, 1) == 0)
                owner!.Search.Invalidate("wy");
            return catalog.SearchAsync(request, token); // Intentionally ignores cancellation, like a late account restore.
        });
        owner = f;
        string songs = S(await f.Start(["wy", "tx"]), "id");
        var result = await f.Idle(songs);
        Check(S(result, "id") == songs && !result.GetProperty("cancelled").GetBoolean());
        Check(S(State(result, "wy"), "error") == "STALE_SESSION" && State(result, "wy").GetProperty("count").GetInt32() == 0);
        Check(State(result, "tx").GetProperty("count").GetInt32() == 1);
        string lists = S(await f.Start(["wy", "tx"], "playlists"), "id");
        Check(!(await f.Idle(lists)).GetProperty("partial").GetBoolean() && S(await f.Call("get", songs), "id") == songs);
        await f.Call("retry", songs, "wy"); var retried = await f.Idle(songs);
        Check(!retried.GetProperty("partial").GetBoolean() && f.Catalog.Calls.Last() is { Platform: "wy", Page: 1, Refresh: true });
    }
    private static async Task DelegateDrain()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken physicalToken = default;
        await using var f = new Fixture(searchAsync: async (_, request, token) =>
        {
            physicalToken = token; started.TrySetResult();
            await release.Task; // Must remain tracked even without catalog calls or token cooperation.
            return Single(request);
        });
        using var caller = new CancellationTokenSource();
        try
        {
            string id = S(await f.Start(["wy"]), "id"); await started.Task;
            f.Search.Invalidate("wy");
            var snapshot = await f.Call("get", id);
            Check(physicalToken.IsCancellationRequested && snapshot.GetProperty("loading").GetBoolean());
            var close = f.Search.PrepareShutdownAsync(caller.Token); caller.Cancel();
            bool cancelled = false;
            try { await close; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled);
            var drain = f.Search.PrepareShutdownAsync();
            Check(!drain.IsCompleted, "cancelled waiter must not erase retained physical work");
            release.TrySetResult(); await drain; await f.Search.CancelShutdownPreparationAsync();
            Check(f.Catalog.Calls.IsEmpty);
        }
        finally { release.TrySetResult(); }
    }
    private static async Task ObserverFence()
    {
        await using var f = new Fixture(); var seen = new ConcurrentQueue<JsonElement>();
        int invalidated = 0;
        var fenced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Search.Changed += value =>
        {
            if (State(value, "wy").GetProperty("count").GetInt32() == 0 ||
                Interlocked.Exchange(ref invalidated, 1) != 0) return;
            f.Search.Invalidate("wy"); fenced.TrySetResult();
        };
        f.Search.Changed += seen.Enqueue;
        string id = S(await f.Start(["wy"]), "id"); await fenced.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var result = await f.Idle(id); await f.Search.PrepareShutdownAsync();
        Check(S(State(result, "wy"), "error") == "STALE_SESSION");
        Check(seen.All(r => State(r, "wy").GetProperty("count").GetInt32() == 0),
            "already-invalidated snapshot reached a later observer");
    }
    private static async Task ReversibleClose()
    {
        await using var f = new Fixture(); var held = new Held(); f.Catalog.Handler = held.Handle;
        try
        {
            string id = S(await f.Start(["wy"]), "id"); await f.Catalog.CountAsync(1);
            var close = f.Search.PrepareShutdownAsync(); Check(!close.IsCompleted);
            Code(await f.Start(["tx"]), "CLOSED"); held.Release(); await close; await f.Search.CancelShutdownPreparationAsync();
            Check(f.Catalog.Calls.Count == 1); Code(await f.Call("get", id), "SEARCH_NOT_FOUND");
            f.Catalog.Handler = (r, _) => Task.FromResult(Single(r));
            await f.Idle(S(await f.Start(["tx"]), "id")); Check(f.Catalog.Disposed == 0);
        }
        finally { held.Release(); }
    }
    private static async Task DisposeDuringResume()
    {
        var f = new Fixture(); var held = new Held(); f.Catalog.Handler = held.Handle;
        try
        {
            await f.Start(["wy"]); await f.Catalog.CountAsync(1);
            var prepare = f.Search.PrepareShutdownAsync(); var resume = f.Search.CancelShutdownPreparationAsync();
            var closing = f.Search.DisposeAsync().AsTask(); Check(!closing.IsCompleted);
            held.Release(); await prepare;
            bool fenced = false; try { await resume; } catch (InvalidOperationException) { fenced = true; }
            await closing; Check(fenced && f.Catalog.Disposed == 0); Code(await f.Start(["wy"]), "CLOSED");
        }
        finally { held.Release(); await f.DisposeAsync(); }
    }
    private static async Task Observers()
    {
        await using var f = new Fixture(); var seen = new ConcurrentQueue<JsonElement>();
        f.Search.Changed += _ => throw new InvalidOperationException("synthetic-observer");
        f.Search.Changed += seen.Enqueue;
        string id = S(await f.Start(["wy", "tx"]), "id"); var first = await f.Idle(id);
        string immutable = first.GetRawText(); f.Search.Invalidate("wy"); await f.Idle(id);
        await f.Search.PrepareShutdownAsync();
        Check(first.GetRawText() == immutable);
        var revisions = seen.Where(r => S(r, "id") == id).Select(r => r.GetProperty("revision").GetInt64()).ToArray();
        Check(revisions.Length > 0 && revisions.Zip(revisions.Skip(1)).All(p => p.First < p.Second));
    }
    private static async Task MalformedPage()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Song("wy", "good"), new JsonObject { ["id"] = "bad" }]));
        var result = await f.Idle(S(await f.Start(["wy"]), "id"));
        Check(result.GetProperty("partial").GetBoolean() && State(result, "wy").GetProperty("count").GetInt32() == 0);
    }
    private static async Task ForeignPage()
    {
        await using var f = new Fixture();
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Song("tx", "foreign")]));
        var result = await f.Idle(S(await f.Start(["wy"]), "id"));
        Check(S(State(result, "wy"), "error") == "IDENTITY_MISMATCH" && result.GetProperty("items").GetArrayLength() == 0);
    }
    private static async Task LongId()
    {
        await using var f = new Fixture(); const string id = "90071992547409931234567890";
        f.Catalog.Handler = (r, _) => Task.FromResult(Page(r, [Song("wy", id)]));
        var result = await f.Idle(S(await f.Start(["wy"]), "id"));
        Check(NativeId(result.GetProperty("items")[0]) == id);
        CurrentSongSurface(result.GetProperty("items")[0]);
    }
    private static async Task EmptySelection()
    {
        await using var f = new Fixture(); var result = await f.Start([]);
        Check(S(result, "code") == "NO_PLATFORMS" && !result.GetProperty("loading").GetBoolean() && f.Catalog.Calls.IsEmpty);
    }
    private static async Task InvalidInput()
    {
        await using var f = new Fixture();
        foreach (var data in new object[] { new { keyword = "", kind = "songs", platforms = Five },
            new { keyword = "x", kind = "other", platforms = Five }, new { keyword = "x", kind = "songs", platforms = new[] { "evil" } },
            new { keyword = "x", kind = "songs", platforms = Five, unexpected = true } })
            Code(await f.Search.ExecuteAsync("music.search.start", data), "INVALID_REQUEST");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Code(await f.Start(["wy"], token: cancelled.Token), "CANCELLED"); Check(f.Catalog.Calls.IsEmpty);
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() { lock (gate) return new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (gate) { timers.Add(timer); timer.Change(dueTime, period); } return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            List<Timer> ready;
            lock (gate)
            {
                ticks += duration.Ticks; ready = timers.Where(t => t.Due <= ticks).ToList();
                foreach (var t in ready) t.Due = t.Period > 0 ? ticks + t.Period : long.MaxValue;
            }
            foreach (var t in ready) t.Callback(t.State);
        }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal long Due = long.MaxValue, Period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    Due = dueTime == global::System.Threading.Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                    Period = period.Ticks; return true;
                }
            }
            public void Dispose() { lock (owner.gate) { owner.timers.Remove(this); Due = long.MaxValue; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

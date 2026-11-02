using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Windows.Tests;

public static class MusicSearchGroupTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("current provider vector keeps 3 items from 2 groups and 4 raw variants", Vector);
        yield return Case("expanded native pages preserve group cursor totals and shared ranks", Pages);
        yield return Case("cross-page copyright dedup never rewrites native resource ids", Copyright);
        yield return Case("other platforms and playlist search cannot opt into group units", OtherKinds);
        yield return Case("ordinary item pages retain their original limits and optional total", ItemPages);
        yield return Case("group count total and variant statistics must be exact bounded integers", Counts);
        yield return Case("native group continuation cannot be guessed from expanded item count", Cursors);
        yield return Case("short group page against a larger total is terminal", ShortTotal);
        yield return Case("group ranks are required and remain within native group positions", Ranks);
        yield return Case("copyright identity is required and page duplicates are rejected", Identities);
        yield return Case("100 groups 1000 expanded variants remain an accepted bounded page", MaximumPage);
        yield return Case("expanded pages are not truncated", Bytes);
        yield return Case("failed group page keeps its cursor and the next round retries the same native page", Retry);
        yield return Case("account fence clears the platform and excludes late group completion", Invalidate);
        yield return Case("complete group pages retain every variant across search rounds", CandidateBudget);
        yield return Case("duplicate copyrights do not stop an advancing group cursor", Stalled);
    }
    private static TestCase Case(string name, Func<Task> run) => new("Music MG group search " + name, run);
    private static void Check(bool condition, string message = "ASSERTION_FAILED")
    { if (!condition) throw new InvalidOperationException(message); }
    private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value);
    private static string S(JsonElement value, string field) => value.GetProperty(field).GetString()!;
    private static JsonElement State(JsonElement value) => value.GetProperty("platforms")[0];
    private static JsonObject Song(string id, long rank, string? copyright = null)
    {
        var native = new JsonObject
        {
            ["source"] = "mg", ["id"] = id, ["songmid"] = id, ["songId"] = id,
            ["copyrightId"] = copyright ?? "C" + id
        };
        return new JsonObject
        {
            ["rank"] = rank,
            ["song"] = MusicSongFixture.Online("mg", id, "Synthetic track " + id,
                "Synthetic artist", "Synthetic album", 180, native: native)
        };
    }
    private static JsonObject CompactSong(int group, int variant)
    {
        string id = Convert.ToString(group * 32 + variant + 1, 16);
        return new JsonObject
        {
            ["rank"] = group + 1,
            ["song"] = MusicSongFixture.Online("mg", id, "t", "a", "", 1,
                native: new JsonObject { ["copyrightId"] = "c" + id })
        };
    }
    private static JsonObject ProviderSong(string id, long rank, string copyright)
    {
        var native = new JsonObject
        {
            ["id"] = id,
            ["songId"] = id,
            ["copyrightId"] = copyright,
            ["albumId"] = "A1",
            ["interval"] = "03:00",
            ["audioFormats"] = new JsonArray(new JsonObject
            {
                ["formatType"] = "HQ", ["asize"] = 1048576, ["hash"] = "fixturehash"
            }),
            ["types"] = new JsonArray(new JsonObject { ["type"] = "320k", ["size"] = "1.00 MB" }),
            ["_types"] = new JsonObject { ["320k"] = new JsonObject { ["size"] = "1.00 MB" } },
            ["qualityKnown"] = true,
            ["contentId"] = "CONTENT" + id[1..]
        };
        return new JsonObject
        {
            ["rank"] = rank,
            ["song"] = MusicSongFixture.Online("mg", id, "Synthetic track " + id[1..],
                "Synthetic artist", "Synthetic album", 180, native: native)
        };
    }
    private static JsonObject Group(int page, int limit, long total, int groups, int raw, params JsonObject[] items) => new()
    {
        ["platform"] = "mg", ["page"] = page, ["limit"] = limit, ["total"] = total, ["totalUnit"] = "groups",
        ["groupCount"] = groups, ["rawItemCount"] = raw, ["items"] = new JsonArray(items.Select(s => (JsonNode)s).ToArray()),
        ["more"] = page * (long)limit < total && groups == limit && page < int.MaxValue,
        ["continuation"] = page * (long)limit < total && groups == limit && page < int.MaxValue
            ? new JsonObject { ["page"] = page + 1 } : null
    };
    private static JsonObject Small() => Group(1, 2, 3, 2, 4,
        Song("S1", 1, "C1"), Song("S2", 1, "C2"), Song("S3", 2, "C3"));
    private static JsonObject WrappedSong(JsonObject page, int index) =>
        page["items"]![index]!["song"]!.AsObject();
    private static void SetCopyright(JsonObject page, int index, string? value)
    {
        var info = MusicSongFixture.Info(WrappedSong(page, index));
        if (value is null) info.Remove("copyrightId"); else info["copyrightId"] = value;
    }
    private static MusicSearchPage Read(JsonObject page, string platform = "mg", MusicSearchKind kind = MusicSearchKind.Songs, int number = 1) =>
        MusicSearchPage.Read(E(page), platform, kind, number);
    private static void Reject(JsonObject page, string platform = "mg", MusicSearchKind kind = MusicSearchKind.Songs, int number = 1)
    {
        try { _ = Read(page, platform, kind, number); }
        catch (MusicLibraryException) { return; }
        throw new InvalidOperationException("invalid group page accepted");
    }
    private static JsonObject ProviderVector() => Group(1, 2, 3, 2, 4,
        ProviderSong("S1", 1, "C1"), ProviderSong("S2", 1, "C2"), ProviderSong("S3", 2, "C3"));
    private static void CurrentSongSurface(JsonElement song)
    {
        foreach (string field in new[] { "id", "_rank", "_ranks", "_rankScore", "matchedPlatforms",
            "plugin_entry_path", "_isOnlineObj", "dedup_key" })
            Check(!song.TryGetProperty(field, out _), "temporary song field leaked: " + field);
    }
    private static async Task Vector()
    {
        var source = ProviderVector();
        var records = source["items"]!.AsArray().Select(item => item!["song"]!.AsObject()).ToArray();
        Check(records.Select(song => MusicSongFixture.Info(song)["songmid"]!.GetValue<string>()).SequenceEqual(["S1", "S2", "S3"]) &&
            records.Select(song => MusicSongFixture.Info(song)["copyrightId"]!.GetValue<string>()).SequenceEqual(["C1", "C2", "C3"]),
            "Provider recording and copyright identities must remain exact");
        foreach (var record in records) CurrentSongSurface(E(record));
        var first = Read(source);
        Check(first.Items.Length == 3 && first.Total == 3 && first.GroupCount == 2 && first.RawItemCount == 4 &&
            first.TotalUnit == "groups" && first.More && first.Next == 2);
        Check(first.Items.Select(i => i.Rank).SequenceEqual([1d, 1d, 2d]));
        var secondPage = Group(2, 2, 3, 1, 2, Song("S5", 3, "C5"), Song("S6", 3, "C6"));
        var second = Read(secondPage, number: 2);
        Check(second.Items.Length == 2 && !second.More && second.Items.All(i => i.Rank == 3) && second.Total == 3);
        await using var f = new Fixture(r => Task.FromResult(E(r.Page == 1 ? source : secondPage)));
        string id = S(await f.Start(), "id"); var snapshot = await f.Idle(id); var state = State(snapshot);
        Check(state.GetProperty("count").GetInt32() == 3 && state.GetProperty("page").GetInt32() == 1 &&
            snapshot.GetProperty("more").GetBoolean());
        var song = snapshot.GetProperty("items").EnumerateArray()
            .Single(s => S(MusicSongFixture.Info(s), "songmid") == "S1");
        Check(S(song, "songId") == records[0]["songId"]!.GetValue<string>(),
            "Search grouping must preserve the supplied song identity");
        CurrentSongSurface(song);
        var native = MusicSongFixture.Info(song);
        Check(new[] { "id", "songId", "songmid" }.All(field => S(native, field) == "S1") &&
            S(native, "copyrightId") == "C1" && S(native, "contentId") == "CONTENT1");
        // Existing NativeMusicSong quality projection keeps hash/size only; native format
        // metadata remains in audioFormats and must still identify the original resource.
        Check(S(native.GetProperty("_types").GetProperty("320k"), "size") == "1.00 MB" &&
            native.GetProperty("audioFormats")[0].GetProperty("asize").GetInt64() == 1048576 &&
            S(native.GetProperty("audioFormats")[0], "formatType") == "HQ");
        await f.Call("next", id); var final = await f.Idle(id);
        Check(State(final).GetProperty("count").GetInt32() == 5 && State(final).GetProperty("page").GetInt32() == 2 &&
            !final.GetProperty("more").GetBoolean() && final.GetProperty("round").GetInt32() == 2);
        Check(f.Catalog.Calls.Select(r => r.Page).SequenceEqual([1, 2]));
    }
    private static JsonObject Page(MusicSearchRequest request, int variants, long total = 31)
    {
        int offset = (request.Page - 1) * request.Limit, groups = (int)Math.Min(request.Limit, Math.Max(0, total - offset));
        var items = Enumerable.Range(0, groups).SelectMany(g => Enumerable.Range(0, variants).Select(v =>
            Song($"S{offset + g + 1}V{v}", offset + g + 1))).ToArray();
        return Group(request.Page, request.Limit, total, groups, items.Length, items);
    }
    private static async Task Pages()
    {
        await using var f = new Fixture(r => Task.FromResult(E(Page(r, 2))));
        string id = S(await f.Start(), "id"); var first = await f.Idle(id); var a = State(first);
        Check(a.GetProperty("count").GetInt32() == 60 && a.GetProperty("page").GetInt32() == 1);
        var rows = first.GetProperty("items").EnumerateArray().ToArray();
        Check(rows.Length == 60 &&
            rows.Take(2).Select(r => S(MusicSongFixture.Info(r), "songmid"))
                .ToHashSet(StringComparer.Ordinal).SetEquals(["S1V0", "S1V1"]));
        Check(rows.All(r => !r.TryGetProperty("_ranks", out _) && !r.TryGetProperty("_rankScore", out _)));
        await f.Call("next", id); var final = await f.Idle(id); var b = State(final);
        Check(b.GetProperty("count").GetInt32() == 62 && b.GetProperty("page").GetInt32() == 2 &&
            !final.GetProperty("more").GetBoolean() && final.GetProperty("items").GetArrayLength() == 62);
        Check(f.Catalog.Calls.Select(r => r.Page).SequenceEqual([1, 2]));
    }
    private static async Task Copyright()
    {
        await using var f = new Fixture(r => Task.FromResult(E(r.Page == 1 ? Page(r, 1) :
            Group(2, 30, 31, 1, 1, Song("differentNative", 31, "CS1V0")))));
        string id = S(await f.Start(), "id"); await f.Idle(id); await f.Call("next", id);
        var result = await f.Idle(id);
        Check(State(result).GetProperty("count").GetInt32() == 30 && State(result).GetProperty("page").GetInt32() == 2);
        var song = result.GetProperty("items").EnumerateArray()
            .Single(s => S(MusicSongFixture.Info(s), "songmid") == "S1V0");
        Check(S(MusicSongFixture.Info(song), "songmid") == "S1V0" &&
            S(MusicSongFixture.Info(song), "copyrightId") == "CS1V0");
        Check(result.GetProperty("items").EnumerateArray()
            .All(s => S(MusicSongFixture.Info(s), "songmid") != "differentNative"));
    }
    private static Task OtherKinds()
    {
        foreach (string platform in new[] { "wy", "tx", "kw", "kg" })
        {
            var page = Small(); page["platform"] = platform; Reject(page, platform);
        }
        Reject(Small(), kind: MusicSearchKind.Playlists); return Task.CompletedTask;
    }
    private static Task ItemPages()
    {
        var page = Small(); page.Remove("totalUnit"); page.Remove("groupCount"); page.Remove("rawItemCount");
        page["items"] = new JsonArray(page["items"]!.AsArray()
            .Select(item => item!["song"]!.DeepClone()).ToArray());
        Reject(page); // Expanded >limit remains invalid without explicit MG group semantics.
        page["limit"] = 3; page["more"] = false; page["continuation"] = null;
        var parsed = Read(page);
        Check(parsed.TotalUnit == "items" && parsed.GroupCount is null && parsed.RawItemCount is null && parsed.Total == 3);
        page["totalUnit"] = "songs"; Reject(page); return Task.CompletedTask;
    }
    private static Task Counts()
    {
        foreach (string field in new[] { "groupCount", "rawItemCount", "total" })
        {
            var p = Small(); p.Remove(field); Reject(p);
            foreach (JsonNode? value in new JsonNode?[] { null, JsonValue.Create("2"), JsonValue.Create(1.5), JsonValue.Create(9007199254740992L) })
            { p = Small(); p[field] = value; Reject(p); }
        }
        foreach (var (field, value) in new[] { ("groupCount", 1), ("groupCount", 3), ("rawItemCount", 2),
            ("rawItemCount", -1), ("total", -1), ("limit", 101) })
        { var p = Small(); p[field] = value; Reject(p); }
        Reject(Group(1, 2, 1, 1, 0));
        Reject(Group(1, 2, 1, 1, 1)); // Nonempty raw groups cannot become an empty deduplicated page.
        var emptyFirst = Read(Group(1, 2, 1, 0, 0));
        Check(emptyFirst.Items.Length == 0 && !emptyFirst.More && emptyFirst.GroupCount == 0);
        var empty = Read(Group(2, 2, 2, 0, 0), number: 2); Check(empty.Items.Length == 0 && !empty.More);
        return Task.CompletedTask;
    }
    private static Task Cursors()
    {
        var p = Small(); p["continuation"] = new JsonObject { ["page"] = 3 }; Reject(p);
        p = Small(); p["continuation"] = null; Reject(p);
        p = Small(); p["more"] = false; p["continuation"] = null;
        Check(Read(p) is { Items.Length: 3, More: false });
        p = Group(1, 2, 2, 2, 2, Song("1", 1), Song("2", 2)); p["more"] = true; p["continuation"] = new JsonObject { ["page"] = 2 }; Reject(p);
        return Task.CompletedTask;
    }
    private static Task ShortTotal()
    {
        var page = Read(Group(1, 30, 100, 1, 1, Song("S1", 1, "C1")));
        Check(page.Items.Length == 1 && !page.More);
        return Task.CompletedTask;
    }
    private static Task Ranks()
    {
        foreach (double value in new[] { 0, 1.5, 3, -1 })
        { var p = Small(); p["items"]![0]!["rank"] = value; Reject(p); }
        var missing = Small(); missing["items"]![0]!.AsObject().Remove("rank"); Reject(missing);
        var crowded = Group(1, 2, 2, 2, 34, Enumerable.Range(0, 33).Select(i => Song("x" + i, 1)).Append(Song("last", 2)).ToArray());
        Check(Read(crowded).Items.Length == 34); return Task.CompletedTask;
    }
    private static Task Identities()
    {
        var p = Small(); SetCopyright(p, 0, null); Reject(p);
        p = Small(); SetCopyright(p, 2, "C1"); Reject(p);
        p = Small(); SetCopyright(p, 0, new string('x', 513)); Reject(p);
        return Task.CompletedTask;
    }
    private static Task MaximumPage()
    {
        var items = Enumerable.Range(0, 100)
            .SelectMany(group => Enumerable.Range(0, 10).Select(variant => CompactSong(group, variant)))
            .ToArray();
        var page = Read(Group(1, 100, 100, 100, 1000, items));
        Check(page.Items.Length == 1000 && page.Total == 100 && page.RawItemCount == 1000 && !page.More);
        return Task.CompletedTask;
    }
    private static Task Bytes()
    {
        var p = Small(); p["padding"] = new string('x', 512 * 1024);
        Check(Read(p).Items.Length == 3);
        return Task.CompletedTask;
    }
    private static async Task Retry()
    {
        int fails = 0;
        await using var f = new Fixture(r => r.Page == 2 && Interlocked.Increment(ref fails) == 1
            ? Task.FromException<JsonElement>(new InvalidOperationException("synthetic page two"))
            : Task.FromResult(E(Page(r, 2))));
        string id = S(await f.Start(), "id"); await f.Idle(id); await f.Call("next", id); var failed = State(await f.Idle(id));
        Check(failed.GetProperty("page").GetInt32() == 1 && failed.GetProperty("count").GetInt32() == 60 &&
            S(failed, "error") == "INVALID_RESPONSE" && failed.GetProperty("more").GetBoolean());
        // The failed cursor did not advance: the next round asks for the same native page.
        await f.Call("next", id); var result = State(await f.Idle(id));
        Check(result.GetProperty("page").GetInt32() == 2 && result.GetProperty("count").GetInt32() == 62 &&
            S(result, "error") == "" && f.Catalog.Calls.Select(r => r.Page).SequenceEqual([1, 2, 2]) &&
            !f.Catalog.Calls.Last().Refresh);
    }
    private static async Task Invalidate()
    {
        var held = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = new Fixture(r =>
        {
            if (r.Page != 2) return Task.FromResult(E(Page(r, 2)));
            entered.TrySetResult(); return held.Task;
        });
        try
        {
            string id = S(await f.Start(), "id"); await f.Idle(id); await f.Call("next", id); await entered.Task;
            f.Search.Invalidate("mg");
            var invalid = State(await f.Call("get", id));
            Check(invalid.GetProperty("count").GetInt32() == 0 && S(invalid, "error") == "STALE_SESSION");
            held.SetResult(E(Page(f.Catalog.Calls.Last(), 2))); var result = State(await f.Idle(id));
            Check(result.GetProperty("page").GetInt32() == 0 && result.GetProperty("count").GetInt32() == 0);
            // The late page 2 is excluded and the platform restarts from its first page.
            await f.Call("next", id); var restarted = State(await f.Idle(id));
            Check(restarted.GetProperty("page").GetInt32() == 1 && restarted.GetProperty("count").GetInt32() == 60 &&
                f.Catalog.Calls.Last().Page == 1);
        }
        finally { held.TrySetResult(E(new { })); }
    }
    private static async Task CandidateBudget()
    {
        await using var f = new Fixture(r =>
        {
            int offset = (r.Page - 1) * r.Limit;
            int groups = Math.Min(r.Limit, 100 - offset);
            var items = Enumerable.Range(0, groups)
                .SelectMany(group => Enumerable.Range(0, 32)
                    .Select(variant => CompactSong(offset + group, variant)))
                .ToArray();
            return Task.FromResult(E(Group(r.Page, r.Limit, 100, groups, items.Length, items)));
        });
        string id = S(await f.Start(), "id"); await f.Idle(id);
        await f.Call("next", id); var second = State(await f.Idle(id)); Check(second.GetProperty("count").GetInt32() == 1920);
        await f.Call("next", id); var third = State(await f.Idle(id));
        Check(third.GetProperty("count").GetInt32() == 2880 && third.GetProperty("page").GetInt32() == 3 &&
            S(third, "error") == "" && third.GetProperty("more").GetBoolean());
        await f.Call("next", id); var last = State(await f.Idle(id));
        Check(last.GetProperty("count").GetInt32() == 3200 && !last.GetProperty("more").GetBoolean());
        Check(f.Catalog.Calls.Count == 4);
    }
    private static async Task Stalled()
    {
        await using var f = new Fixture(r =>
        {
            var p = Page(r, 1, 180);
            for (int i = 0; i < 30; i++) SetCopyright(p, i, "repeat" + i);
            return Task.FromResult(E(p));
        });
        string id = S(await f.Start(), "id"); await f.Idle(id);
        for (int i = 0; i < 4; i++) { await f.Call("next", id); await f.Idle(id); }
        var state = State(await f.Call("get", id));
        Check(state.GetProperty("page").GetInt32() == 5 && state.GetProperty("count").GetInt32() == 30 &&
            S(state, "error") == "" && state.GetProperty("more").GetBoolean());
    }
    private sealed class Fixture(Func<MusicSearchRequest, Task<JsonElement>> handler) : IAsyncDisposable
    {
        internal readonly Catalog Catalog = new(handler);
        private MusicSearchCoordinator? search;
        internal MusicSearchCoordinator Search => search ??= new(Catalog);
        internal Task<JsonElement> Start() => Search.ExecuteAsync("music.search.start", new { keyword = "group", kind = "songs",
            platforms = new[] { "mg" }, pages = 1, concurrency = 1, cacheMinutes = 60 });
        internal Task<JsonElement> Call(string method, string id) => Search.ExecuteAsync("music.search." + method, new { id });
        internal async Task<JsonElement> Idle(string id)
        {
            var done = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Updated(JsonElement r)
            { if (S(r, "id") == id && !r.GetProperty("loading").GetBoolean()) done.TrySetResult(r); }
            Search.Changed += Updated;
            try { Updated(await Call("get", id)); return await done.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { Search.Changed -= Updated; }
        }
        public ValueTask DisposeAsync() => Search.DisposeAsync();
    }
    private sealed class Catalog(Func<MusicSearchRequest, Task<JsonElement>> handler) : IMusicPlatformCatalog
    {
        internal readonly ConcurrentQueue<MusicSearchRequest> Calls = new();
        public Task<JsonElement> SearchAsync(MusicSearchRequest r, CancellationToken t = default) { Calls.Enqueue(r); return handler(r); }
        public JsonElement Describe() => E(new { platforms = new { mg = new { search = new[] { "songs", "playlists" } } } });
        public void Invalidate(string? platform = null) { }
        public void Dispose() { }
        private static Task<JsonElement> Unused() => throw new InvalidOperationException("non-search call");
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

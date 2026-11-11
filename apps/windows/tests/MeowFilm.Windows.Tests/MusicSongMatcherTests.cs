using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.MusicOfficialAudioTests;

namespace MeowFilm.Windows.Tests;

public static class MusicSongMatcherTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Song matching only fetches the required missing platform and reuses accepted recordings", Target);
        yield return new("Song matching negative results retry only on demand after 24 hours or changed conditions", Negative);
        yield return new("Song matching request failure is not a negative result and retains existing mappings", Failure);
        yield return new("Song matching seven days never deletes or refetches a still usable mapping", Freshness);
        yield return new("Song matching content failure rechecks existing mappings only at seven days", ContentFailure);
        yield return new("Song matching content failure may check imported mappings without timestamp evidence", MissingTimestamp);
        yield return new("Song matching failed rechecks retain mapping age and never become not found", RecheckFailure);
        yield return new("Song matching search evidence distinguishes searched empty failed and unsearched platforms", SearchEvidence);
        yield return new("Song matching broad search absence cannot suppress a different focused query", SearchConditions);
        yield return new("Song matching later page absence cannot suppress a focused first-page query", LaterPage);
        yield return new("Song matching a late older physical search cannot overwrite a newer observation", LateObservation);
        yield return new("Song matching a cached empty page never renews its negative check time", CachedNegative);
        yield return new("Song matching persistence starts at local membership and survives restart without searching", Persistence);
        yield return new("Song matching removed membership evicts derived data without changing queue or history", Removal);
        yield return new("Song matching wrong recording exclusion is original-specific and survives subsequent discovery", Exclusion);
        yield return new("Song matching incompatible versions and all-pair durations never become sources", Identity);
        yield return new("Song matching duplicate callers share one physical request and cancellation is waiter-local", Concurrent);
        yield return new("Song matching account invalidation cancels and fences noncooperative old responses", Invalidation);
        yield return new("Song matching local songs never acquire guessed online mappings", Local);
    }
    private static void Check(bool condition, string message = "song matching assertion")
    { if (!condition) throw new InvalidOperationException(message); }
    private static string Context(string platform) => NativeMusicSong.Hash("fixture:" + platform);
    private static JsonObject Song(string platform = "wy", string id = "original", double duration = 180,
        string title = "Track") => MusicSongFixture.Online(platform, id, title, "Artist", "Album", duration);
    private static bool Has(JsonObject song, string platform) => song["sources"]!.AsArray()
        .OfType<JsonObject>().Any(source => LJ.S(source["source"]) == platform);
    private static JsonElement Page(MusicSearchRequest request, params JsonObject[] songs) =>
        JsonSerializer.SerializeToElement(new { platform = request.Platform, page = request.Page,
            limit = request.Limit, items = songs, more = false });
    private static JsonElement Observation(JsonObject song, string query, long at, long order,
        params (string Platform, int Page, string Error)[] states) =>
        JsonSerializer.SerializeToElement(new { kind = "songs", keyword = query, cancelled = false,
            items = new[] { song }, platforms = states.Select(state => new {
                platform = state.Platform, page = state.Page, error = state.Error,
                loading = false, checkedAt = state.Page > 0 || state.Error.Length > 0 ? at : 0
            }).ToArray(),
            observations = states.Where(state => state.Page > 0 || state.Error.Length > 0).Select(state => new {
                platform = state.Platform, page = Math.Max(1, state.Page), pages = 1, limit = 30,
                checkedAt = at, order, generation = 0, cached = false, error = state.Error,
                items = state.Error.Length == 0 && Has(song, state.Platform) ? new[] { song } : Array.Empty<JsonObject>()
            }).ToArray() });
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly MemoryStore Store = new();
        internal readonly CacheDocumentStore CacheStore;
        internal readonly Clock Clock = new();
        internal readonly MusicLibraryOwner Library;
        internal readonly MusicSongMatcher Matcher;
        internal readonly List<MusicSearchRequest> Requests = [];
        internal Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> Reply =
            (request, _) => Task.FromResult(Page(request, Song(request.Platform, "mapped")));
        internal Fixture()
        {
            CacheStore = new(Store);
            Library = new(Store);
            Matcher = new(Library, new(CacheStore), async (request, token) =>
            {
                lock (Requests) Requests.Add(request);
                return await Reply(request, token);
            }, Clock);
        }
        internal Task<MusicSongMatcher.MatchResult> Match(JsonObject song, string platform = "tx",
            CancellationToken token = default, bool contentFailed = false) =>
            Matcher.MatchAsync(song, platform, Context(platform), token, contentFailed);
        internal async Task<JsonElement> Command(string method, object input)
        {
            var reply = await Library.ExecuteAsync(method, input);
            Check(reply.GetProperty("ok").GetBoolean(), method + ": " + reply.GetRawText());
            return reply;
        }
        internal Task<JsonElement> Favorite(JsonObject song) => Command("music.library.addSongs",
            new { id = "favorites", ifRevision = 0, songs = new[] { song } });
        internal JsonArray Saved => CacheStore.ReadMemory(MusicSongMatchStore.Namespace).Data is { } bytes
            ? JsonNode.Parse(bytes)!["entries"]!.AsArray() : [];
        public async ValueTask DisposeAsync() { await Matcher.DrainAsync(); await CacheStore.DisposeAsync(); await Library.DisposeAsync(); }
    }
    private static async Task Target()
    {
        await using var f = new Fixture();
        var original = Song();
        var result = await f.Match(original);
        Check(Has(result.Song, "tx") && f.Requests.Count == 1 && f.Requests.Single().Platform == "tx");
        Check(LJ.S(result.Song["songId"]) == LJ.S(original["songId"]) && !Has(original, "tx"));
        Check(Has(await f.Matcher.KnownAsync(original, default), "tx"));
        await f.Match(original);
        Check(f.Requests.Count == 1, "a queue snapshot must reuse the previously accepted mapping");
    }
    private static async Task Negative()
    {
        await using var f = new Fixture();
        f.Reply = (request, _) => Task.FromResult(Page(request));
        var song = Song();
        await f.Match(song);
        f.Clock.Now += MusicSongMatcher.NotFoundRetry - 1;
        await f.Match(song);
        Check(f.Requests.Count == 1);
        f.Clock.Now++;
        Check(f.Requests.Count == 1, "time passing must not schedule background work");
        await f.Match(song);
        Check(f.Requests.Count == 2);
        await f.Match(song);
        Check(f.Requests.Count == 2, "repeated discovery retains negative cooling");
        await f.Matcher.MatchAsync(song, "tx", NativeMusicSong.Hash("new-account"), default);
        Check(f.Requests.Count == 3, "different search authority is a different matching condition");
    }
    private static async Task Failure()
    {
        await using var f = new Fixture();
        var song = Song();
        await f.Favorite(song);
        f.Reply = (_, _) => throw new MusicPlatformException("NETWORK_ERROR");
        var failed = await f.Match(song);
        Check(failed.Error == "NETWORK_ERROR" && !Has(failed.Song, "tx"));
        Check(LJ.S(f.Saved.Single()!["platforms"]!["tx"]!["state"]) == "failed");
        f.Reply = (request, _) => Task.FromResult(Page(request, Song("tx", "mapped")));
        Check(Has((await f.Match(song)).Song, "tx") && f.Requests.Count == 2);
        f.Reply = (_, _) => throw new MusicPlatformException("NETWORK_ERROR");
        var refresh = await f.Match(song);
        Check(refresh.Error is null && Has(refresh.Song, "tx") && f.Requests.Count == 2,
            "an accepted identity is reused without a new transport request");
    }
    private static async Task Freshness()
    {
        await using var f = new Fixture();
        var song = Song();
        await f.Favorite(song);
        await f.Match(song);
        long checkedAt = LJ.I(f.Saved.Single()!["platforms"]!["tx"]!["checkedAt"]);
        f.Clock.Now += 8L * 86400000;
        await f.Match(song);
        await f.Matcher.PersistAsync(default);
        Check(f.Requests.Count == 1 && Has(await f.Matcher.KnownAsync(song, default), "tx"));
        Check(LJ.I(f.Saved.Single()!["platforms"]!["tx"]!["checkedAt"]) == checkedAt,
            "reading and persisting must not renew freshness");
        await f.Matcher.ExcludeAsync(song, NativeMusicSong.SourceKey(Song("tx", "mapped")), default);
        f.Reply = (request, _) => Task.FromResult(Page(request, Song("tx", "replacement")));
        var rechecked = await f.Matcher.MatchAsync(song, "tx", Context("tx"), default);
        Check(f.Requests.Count == 2 && rechecked.Song["sources"]!.AsArray().OfType<JsonObject>()
            .Any(source => NativeMusicSong.NativeId(source) == "replacement"));
    }
    private static async Task SearchEvidence()
    {
        await using var f = new Fixture();
        var song = Song();
        f.Matcher.ObserveSearch(Observation(song, MusicSongMatcher.Query(song), f.Clock.Now, f.Matcher.NextObservation(),
            ("wy", 1, ""), ("tx", 1, ""), ("kw", 0, "NETWORK_ERROR"), ("kg", 0, "")), Context);
        await f.Match(song);
        Check(f.Requests.Count == 0, "the searched-empty platform must not repeat");
        await f.Match(song, "kw");
        await f.Match(song, "kg");
        Check(f.Requests.Select(request => request.Platform).SequenceEqual(["kw", "kg"]));
        var mapped = Song(); mapped["sources"]!.AsArray().Add(Song("tx", "from-search")["sources"]![0]!.DeepClone());
        f.Matcher.ObserveSearch(Observation(NativeMusicSong.Normalize(mapped), MusicSongMatcher.Query(song),
            f.Clock.Now + 1, f.Matcher.NextObservation(), ("wy", 1, ""), ("tx", 1, "")), Context);
        Check(Has((await f.Match(song)).Song, "tx") && f.Requests.Count == 2);
    }
    private static async Task SearchConditions()
    {
        await using var f = new Fixture();
        var song = Song();
        f.Matcher.ObserveSearch(Observation(song, "broad artist query", f.Clock.Now, f.Matcher.NextObservation(),
            ("wy", 1, ""), ("tx", 1, "")), Context);
        await f.Match(song);
        Check(f.Requests.Count == 1 && f.Requests[0].Keyword == MusicSongMatcher.Query(song),
            "a different bounded query may find a recording outside the broad first page");
    }
    private static async Task ContentFailure()
    {
        await using var f = new Fixture();
        var song = Song();
        await f.Favorite(song);
        await f.Match(song);
        f.Clock.Now += MusicSongMatcher.Freshness - 1;
        await f.Match(song, contentFailed: true);
        Check(f.Requests.Count == 1, "content failure cannot recheck a fresh accepted mapping");
        f.Clock.Now++;
        f.Reply = (request, _) => Task.FromResult(Page(request, Song("tx", "replacement")));
        var rechecked = await f.Match(song, contentFailed: true);
        Check(f.Requests.Count == 2 && rechecked.Song["sources"]!.AsArray().OfType<JsonObject>()
            .Any(source => NativeMusicSong.NativeId(source) == "replacement"));
        f.Clock.Now += MusicSongMatcher.Freshness + 1;
        await f.Match(song);
        Check(f.Requests.Count == 2, "elapsed age alone cannot trigger an existing mapping search");
    }
    private static async Task MissingTimestamp()
    {
        await using var f = new Fixture();
        var song = Song("tx", "imported");
        await f.Match(song);
        Check(f.Requests.Count == 0);
        await f.Match(song, contentFailed: true);
        Check(f.Requests.Count == 1 && f.Requests[0].Platform == "tx");
        f.Clock.Now++;
        await f.Matcher.MatchAsync(song, "tx", NativeMusicSong.Hash("new-account"), default, contentFailed: true);
        Check(f.Requests.Count == 2, "another authority cannot borrow a previous account's time evidence");
    }
    private static async Task RecheckFailure()
    {
        await using var f = new Fixture();
        var song = Song();
        await f.Favorite(song); await f.Match(song);
        long matchedAt = LJ.I(f.Saved.Single()!["platforms"]!["tx"]!["checkedAt"]);
        f.Clock.Now += MusicSongMatcher.Freshness;
        f.Reply = (_, _) => throw new MusicPlatformException("NETWORK_ERROR");
        var failure = await f.Match(song, contentFailed: true);
        Check(failure.Error == "NETWORK_ERROR" && Has(failure.Song, "tx"));
        var state = f.Saved.Single()!["platforms"]!["tx"]!;
        Check(LJ.S(state["state"]) == "failed" && LJ.I(state["checkedAt"]) == f.Clock.Now &&
            LJ.I(state["matchedAt"]) == matchedAt, "a failed request must not renew successful-match age");
        await f.Match(song);
        Check(f.Requests.Count == 2, "network failure alone cannot search an existing mapping");
        f.Reply = (request, _) => Task.FromResult(Page(request));
        await f.Match(song, contentFailed: true);
        await f.Match(song, contentFailed: true);
        Check(f.Requests.Count == 3 && LJ.S(f.Saved.Single()!["platforms"]!["tx"]!["state"]) == "notFound",
            "only a real focused empty response enters the negative retry window");
    }
    private static async Task LaterPage()
    {
        await using var f = new Fixture();
        var song = Song();
        f.Matcher.ObserveSearch(Observation(song, MusicSongMatcher.Query(song), f.Clock.Now,
            f.Matcher.NextObservation(), ("wy", 1, ""), ("tx", 2, "")), Context);
        await f.Match(song);
        Check(f.Requests.Count == 1 && f.Requests[0].Page == 1,
            "page two says nothing about the recording's absence from the focused first page");
    }
    private static async Task LateObservation()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reply = async (request, _) =>
        {
            entered.TrySetResult(); await release.Task;
            return Page(request, Song("tx", "older"));
        };
        var pending = f.Match(Song());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Clock.Now++;
            f.Matcher.ObserveSearch(Observation(Song("tx", "newer"), "Track Artist", f.Clock.Now,
                f.Matcher.NextObservation(), ("tx", 1, "")), Context);
            f.Clock.Now++;
            release.TrySetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            var ids = result.Song["sources"]!.AsArray().OfType<JsonObject>().Select(NativeMusicSong.NativeId).ToArray();
            Check(ids.Contains("newer") && !ids.Contains("older"), "completion time cannot make an earlier request newer");
        }
        finally { release.TrySetResult(); }
    }
    private static async Task CachedNegative()
    {
        await using var f = new Fixture();
        var song = Song();
        long at = f.Clock.Now;
        var observed = Observation(song, MusicSongMatcher.Query(song), at, f.Matcher.NextObservation(),
            ("wy", 1, ""), ("tx", 1, ""));
        f.Matcher.ObserveSearch(observed, Context);
        f.Clock.Now += MusicSongMatcher.NotFoundRetry - 1;
        f.Matcher.ObserveSearch(observed, Context);
        await f.Match(song);
        Check(f.Requests.Count == 0);
        f.Clock.Now++;
        await f.Match(song);
        Check(f.Requests.Count == 1, "replaying cached evidence must not renew a 24-hour negative result");
    }
    private static async Task Persistence()
    {
        await using var f = new Fixture();
        var song = Song();
        await f.Match(song);
        Check(f.Saved.Count == 0, "temporary playback must not create persistent matching rows");
        await f.Favorite(song);
        await f.Matcher.PersistAsync(default);
        Check(f.Saved.Count == 1 && Has(f.Saved.Single()!["song"]!.AsObject(), "tx"));
        int requests = f.Requests.Count;
        await f.CacheStore.DrainAsync();
        await using var restartedStore = new CacheDocumentStore(f.Store);
        var restarted = new MusicSongMatcher(f.Library, new(restartedStore), (_, _) =>
            throw new InvalidOperationException("restart must not search"), f.Clock);
        Check(Has((await restarted.MatchAsync(song, "tx", Context("tx"), default)).Song, "tx"));
        Check(f.Requests.Count == requests && !f.Saved.ToJsonString().Contains("https://media", StringComparison.Ordinal));
    }
    private static async Task Removal()
    {
        await using var f = new Fixture();
        var song = Song();
        await f.Favorite(song);
        await f.Command("music.queue.replace", new { songs = new[] { song }, index = 0 });
        string queue = (await f.Command("music.queue.get", new { })).GetRawText();
        await f.Match(song);
        Check(f.Saved.Count == 1);
        await f.Command("music.library.removeSong", new { id = "favorites", ifRevision = 1, songId = LJ.S(song["songId"]) });
        await f.Matcher.PersistAsync(default);
        Check(f.Saved.Count == 0);
        Check((await f.Command("music.queue.get", new { })).GetRawText() == queue,
            "derived-cache eviction cannot modify a durable queue snapshot or occurrence");
    }
    private static async Task Exclusion()
    {
        await using var f = new Fixture();
        var original = Song();
        string wrong = NativeMusicSong.SourceKey(Song("tx", "mapped"));
        await f.Match(original);
        await f.Matcher.ExcludeAsync(original, wrong, default);
        Check(!Has(await f.Matcher.KnownAsync(original, default), "tx"));
        Check(!Has((await f.Match(original)).Song, "tx"),
            "a subsequent search must not immediately select the same wrong recording");
        await f.Match(original);
        Check(f.Requests.Count == 2, "an actual search which only returned excluded recordings must not repeat on every read");
        var anotherOriginal = Song("kg", "another-original");
        Check(Has((await f.Match(anotherOriginal)).Song, "tx"),
            "exclusion is not a global ban on the target recording");
        Check(!Has(await f.Matcher.KnownAsync(original, default), "tx"));
    }
    private static async Task Identity()
    {
        foreach (var incompatible in new[] { Song("tx", "live", title: "Track (Live)"), Song("tx", "long", 230) })
        {
            await using var f = new Fixture();
            f.Reply = (request, _) => Task.FromResult(Page(request, incompatible));
            Check(!Has((await f.Match(Song())).Song, "tx"));
        }
        await using var pair = new Fixture();
        var song = Song(duration: 103);
        song["sources"]!.AsArray().Add(Song("kw", "short", 100)["sources"]![0]!.DeepClone());
        pair.Reply = (request, _) => Task.FromResult(Page(request, Song("tx", "chain", 106)));
        Check(!Has((await pair.Match(NativeMusicSong.Normalize(song))).Song, "tx"),
            "a candidate must be compatible with every retained recording");
    }
    private static async Task Concurrent()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reply = async (request, token) =>
        {
            entered.TrySetResult(token);
            await release.Task.WaitAsync(token);
            return Page(request, Song("tx", "shared"));
        };
        using var firstCaller = new CancellationTokenSource();
        var first = f.Match(Song(), token: firstCaller.Token);
        var physical = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = f.Match(Song());
        firstCaller.Cancel();
        try { await first; throw new InvalidOperationException("cancelled waiter published"); }
        catch (OperationCanceledException) { }
        Check(!physical.IsCancellationRequested && f.Requests.Count == 1);
        release.TrySetResult();
        Check(Has((await second.WaitAsync(TimeSpan.FromSeconds(3))).Song, "tx") && f.Requests.Count == 1);
    }
    private static async Task Invalidation()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reply = async (request, token) =>
        {
            entered.TrySetResult(token);
            await release.Task; // Synthetic noncooperative transport.
            return Page(request, Song("tx", "stale"));
        };
        var pending = f.Match(Song());
        var physical = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Matcher.Invalidate("tx");
        Check(physical.IsCancellationRequested);
        release.TrySetResult();
        try { await pending; throw new InvalidOperationException("old account response published"); }
        catch (OperationCanceledException) { }
        catch (MusicLibraryException error) when (error.Code == "AUTH_CHANGED") { }
        Check(!Has(await f.Matcher.KnownAsync(Song(), default), "tx"));
    }
    private static async Task Local()
    {
        await using var f = new Fixture();
        var a = MusicSongFixture.Local("local:first", "Track", "Artist", duration: 180);
        var b = MusicSongFixture.Local("local:second", "Track", "Artist", duration: 180);
        Check(!Has((await f.Match(a)).Song, "tx") && f.Requests.Count == 0);
        Check(LJ.S((await f.Matcher.KnownAsync(b, default))["songId"]) == "local:second");
    }
}

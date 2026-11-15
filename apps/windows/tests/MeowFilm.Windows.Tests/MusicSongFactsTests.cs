using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.MusicOfficialAudioTests;

namespace MeowFilm.Windows.Tests;

public static class MusicSongFactsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Song facts share mutable mappings bidirectionally across primary platforms", Bidirectional);
        yield return new("Song facts share same-source metadata updates without changing presentation identity", Metadata);
        yield return new("Song facts old rows cannot replace newer search metadata through collections or catalog", SnapshotAcquisition);
        yield return new("Song facts snapshot acquisition cannot fence an in-flight real search", SnapshotDuringSearch);
        yield return new("Song facts retain local UUIDs full titles and fixed duration baselines", Boundaries);
        yield return new("Song facts library first read loads saved knowledge before any completion", LibraryFirstRead);
        yield return new("Song facts library imports and queue reads contribute known metadata without requests", LibraryAcquisition);
        yield return new("Song facts persist one repository record independently of collection membership", SinglePersistentOwner);
        yield return new("Song facts a real empty search updates an already known recording without displayed hits", EmptySearch);
        yield return new("Song facts an empty platform settled before the first hit keeps its own negative evidence", EmptyBeforeHit);
        yield return new("Song facts broad page absence never creates a negative recording fact", BroadAbsence);
        yield return new("Song facts cache replay cannot replace a newer recording or check time", CacheReplay);
        yield return new("Song facts retired account observations cannot recreate old mappings", RetiredObservation);
        yield return new("Song facts catalog first read projects saved metadata and adds no supplementary request", CatalogFirstRead);
        yield return new("Song facts catalog projection cannot ingest an older rejected search again", CatalogLateSearch);
        yield return new("Song facts complete dispatch preserves its name and accepts optional contentFailed platforms", CompletionContract);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static JsonObject Song(string platform = "wy", string id = "original", string title = "Track", double duration = 180) =>
        MusicSongFixture.Online(platform, id, title, "Artist", "Album", duration);
    private static string Context(string platform) => NativeMusicSong.Hash("facts:" + platform);
    private static bool Has(JsonObject song, string platform) => song["sources"]!.AsArray().OfType<JsonObject>()
        .Any(source => LJ.S(source["source"]) == platform);
    private static bool Has(JsonElement song, string platform) => song.GetProperty("sources").EnumerateArray()
        .Any(source => source.GetProperty("source").GetString() == platform);
    private static JsonElement Page(MusicSearchRequest request, params JsonObject[] songs) => JsonSerializer.SerializeToElement(new
    { platform = request.Platform, page = request.Page, limit = request.Limit, items = songs, more = false });
    private static JsonObject ReadSong(JsonElement reply) => JsonNode.Parse(reply.GetProperty("song").GetRawText())!.AsObject();
    private static JsonArray Saved(MemoryStore store) => store.Data.TryGetValue(MusicSongMatchStore.Namespace, out var bytes)
        ? JsonNode.Parse(bytes)!["entries"]!.AsArray() : [];
    private static async Task<JsonElement> Ok(MusicLibraryOwner owner, string method, object? input = null)
    {
        var reply = await owner.ExecuteAsync(method, input);
        Check(reply.GetProperty("ok").GetBoolean(), method + ": " + reply.GetRawText());
        return reply;
    }
    private static async Task<JsonElement> Ok(MusicNativeApplication app, string method, object? input = null)
    {
        var reply = await app.ExecuteAsync(method, input);
        Check(reply.GetProperty("ok").GetBoolean(), method + ": " + reply.GetRawText());
        return reply;
    }
    private static async Task<JsonElement> Ok(MusicNativeCatalogOwner owner, string method, object? input = null)
    {
        var reply = await owner.ExecuteAsync(method, input);
        Check(reply.GetProperty("ok").GetBoolean(), method + ": " + reply.GetRawText());
        return reply;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly MemoryStore Store = new();
        internal readonly CacheDocumentStore CacheStore;
        internal readonly Clock Clock = new();
        internal readonly MusicLibraryOwner Library;
        internal readonly MusicSongMatcher Facts;
        internal int Searches;
        internal Fixture()
        {
            CacheStore = new(Store);
            Library = new(Store);
            Facts = new(Library, new(CacheStore), async (request, token) =>
            {
                Searches++;
                return await Library.AdoptPlatformResultAsync(Page(request, Song(request.Platform, "matched-" + request.Platform)), token);
            }, Clock);
        }
        internal Task<MusicSongMatcher.MatchResult> Match(JsonObject song, string platform) =>
            Facts.MatchAsync(song, platform, Context(platform), default);
        internal Task Favorite(params JsonObject[] songs) => Ok(Library, "music.library.addSongs",
            new { id = "favorites", ifRevision = 0, songs });
        public async ValueTask DisposeAsync()
        {
            try { await Facts.DrainAsync(); await Library.DisposeAsync(); }
            finally { await CacheStore.DisposeAsync(); await Store.DisposeAsync(); }
        }
    }

    private static async Task Bidirectional()
    {
        await using var f = new Fixture();
        var a = await f.Facts.KnownAsync(Song(), default);
        var b = await f.Facts.KnownAsync(Song("kg", "other-entry"), default);
        await f.Match(a, "tx");
        Check(Has(await f.Facts.KnownAsync(b, default), "tx"), "A's update must be visible to an existing B alias");
        await f.Match(b, "kw");
        var fromA = await f.Facts.KnownAsync(a, default);
        var fromB = await f.Facts.KnownAsync(b, default);
        Check(Has(fromA, "kw") && Has(fromB, "tx") && Has(fromA, "kg") && f.Searches == 2,
            "B's later update must return through A's original alias");
        Check(LJ.S(fromA["songId"]) == LJ.S(a["songId"]) && LJ.S(fromB["songId"]) == LJ.S(a["songId"]) &&
            JsonNode.DeepEquals(fromA, fromB),
            "all entry points project the single repository identity and current metadata");
    }
    private static async Task Metadata()
    {
        await using var f = new Fixture();
        var original = await f.Facts.KnownAsync(Song(), default);
        var matched = await f.Match(original, "tx");
        var tx = matched.Song["sources"]!.AsArray().OfType<JsonObject>().Single(source => LJ.S(source["source"]) == "tx");
        var refreshed = Song("tx", NativeMusicSong.NativeId(tx));
        NativeMusicSong.SourceData(refreshed)["size"] = "4096";
        var request = new MusicSearchRequest("tx", "Track Artist");
        await f.Library.AdoptPlatformResultAsync(Page(request, refreshed), default);
        f.Facts.ObservePage(request, Page(request, refreshed), "", Context("tx"), f.Facts.BeginSearch("tx"));
        var result = await f.Facts.KnownAsync(original, default);
        Check(LJ.S(result["title"]) == LJ.S(original["title"]) && LJ.S(result["songId"]) == LJ.S(original["songId"]) &&
            LJ.S(NativeMusicSong.SourceData(result["sources"]!.AsArray().OfType<JsonObject>()
                .Single(source => LJ.S(source["source"]) == "tx"))["size"]) == "4096" && f.Searches == 1,
            "real metadata acquisition updates the owner's source while display remains stable");
        long checkedAt = f.Clock.Now;
        f.Clock.Now++;
        var detail = refreshed.DeepClone().AsObject(); NativeMusicSong.SourceData(detail)["size"] = "8192";
        f.Facts.ProjectKnownFacts(LJ.Obj(("song", detail)));
        f.Clock.Now++;
        var imported = refreshed.DeepClone().AsObject(); NativeMusicSong.SourceData(imported)["size"] = "16384";
        await f.Favorite(imported);
        await Ok(f.Library, "music.queue.replace", new { songs = new[] { matched.Song }, index = 0 });
        var queued = await Ok(f.Library, "music.queue.get");
        Check(LJ.S(NativeMusicSong.SourceData(ReadSong(queued.GetProperty("current"))["sources"]!.AsArray()
            .OfType<JsonObject>().Single(source => LJ.S(source["source"]) == "tx"))["size"]) == "4096",
            "catalog, library and queue snapshots cannot replace metadata from the real search");
        await f.Facts.PersistAsync(default);
        await f.CacheStore.DrainAsync();
        Check(LJ.I(Saved(f.Store).Single()!["platforms"]!["tx"]!["checkedAt"]) == checkedAt &&
            LJ.I(Saved(f.Store).Single()!["platforms"]!["tx"]!["matchedAt"]) == checkedAt && f.Searches == 1,
            "metadata acquisition and queue reads cannot renew search or match age");
    }
    private static async Task SnapshotAcquisition()
    {
        await using var f = new Fixture();
        var original = await f.Facts.KnownAsync(Song(), default);
        var old = Song("tx", "same-id"); NativeMusicSong.SourceData(old)["size"] = "1024";
        var newer = old.DeepClone().AsObject(); NativeMusicSong.SourceData(newer)["size"] = "4096";
        int searches = 0;
        var catalog = new Catalog
        {
            Search = request => Task.FromResult(Page(request, ++searches == 1 ? old : newer))
        };
        await using var owner = new MusicNativeCatalogOwner(catalog, f.Library)
        {
            SearchSongs = async (request, token) =>
            {
                var observation = f.Facts.BeginSearch(request.Platform);
                long metadataObservation = f.Library.BeginSongObservation();
                var reply = await catalog.SearchAsync(request, token);
                reply = await f.Library.AdoptPlatformResultAsync(reply, token, observation: metadataObservation);
                f.Facts.ObservePage(request, reply, "", Context(request.Platform), observation);
                return reply;
            }
        };
        string Size(JsonObject song) => LJ.S(NativeMusicSong.SourceData(song["sources"]!.AsArray().OfType<JsonObject>()
            .Single(source => LJ.S(source["sourceKey"]) == NativeMusicSong.SourceKey(old)))["size"]);
        var first = await Ok(owner, "music.catalog.search", new { platform = "tx", keyword = "Track Artist" });
        var oldRow = JsonNode.Parse(first.GetProperty("items")[0].GetRawText())!.AsObject();
        Check(Size(oldRow) == "1024", "retain the actual old search row");
        f.Clock.Now++;
        await Ok(owner, "music.catalog.search", new { platform = "tx", keyword = "Track Artist", refresh = true });
        long checkedAt = f.Clock.Now;
        Check(Size(await f.Facts.KnownAsync(original, default)) == "4096" && searches == 2,
            "a later real search replaces same-source metadata before any snapshot is reused");
        async Task Unchanged(string step)
        {
            var known = await f.Facts.KnownAsync(original, default);
            await f.Facts.PersistAsync(default);
            await f.CacheStore.DrainAsync();
            var state = Saved(f.Store).Single()!["platforms"]!["tx"]!;
            Check(Size(known) == "4096" && LJ.I(state["checkedAt"]) == checkedAt &&
                LJ.I(state["matchedAt"]) == checkedAt && searches == 2 && f.Searches == 0,
                step + " must preserve newer metadata and evidence without new searches");
        }
        f.Clock.Now++;
        var created = await Ok(f.Library, "music.library.create", new { name = "Old search row", songs = new[] { oldRow } });
        await Unchanged("library.create");
        var detail = await Ok(f.Library, "music.library.detail", new { id = created.GetProperty("collection").GetProperty("id").GetString() });
        Check(Size(JsonNode.Parse(detail.GetProperty("songs")[0].GetRawText())!.AsObject()) == "4096",
            "collection reads project the newer source rather than their stored membership snapshot");
        f.Clock.Now++;
        await f.Favorite(oldRow);
        await Unchanged("library.addSongs");
        catalog.Detail = _ => f.Library.ProjectRepository(oldRow, MusicLibraryOwner.PlatformSongShape.Song);
        for (int read = 0; read < 2; read++)
        {
            f.Clock.Now++;
            var projected = await Ok(owner, "music.catalog.song", new { platform = "tx", id = "same-id" });
            Check(Size(ReadSong(projected)) == "4096", "repeated cached catalog data projects current shared facts");
            await Unchanged("catalog projection");
        }
        var withNewSource = oldRow.DeepClone().AsObject();
        withNewSource["sources"]!.AsArray().Add(Song("tx", "imported-source")["sources"]![0]!.DeepClone());
        await Ok(f.Library, "music.library.addSongs", new { id = "favorites", ifRevision = 1, songs = new[] { withNewSource } });
        await Unchanged("library new source");
        Check((await f.Facts.KnownAsync(original, default))["sources"]!.AsArray().OfType<JsonObject>()
            .Any(source => NativeMusicSong.NativeId(source) == "imported-source"), "a new sourceKey is still collected");
        catalog.Detail = request => Song(request.Platform, request.Id);
        await Ok(owner, "music.catalog.song", new { platform = "kg", id = "catalog-source" });
        await Unchanged("catalog new source");
        Check(Has(await f.Facts.KnownAsync(original, default), "kg"), "a newly acquired catalog source is visible through the original alias");
    }
    private static async Task SnapshotDuringSearch()
    {
        await using var f = new Fixture();
        var original = await f.Facts.KnownAsync(Song(), default);
        var old = Song("tx", "same-id"); NativeMusicSong.SourceData(old)["size"] = "1024";
        var newer = old.DeepClone().AsObject(); NativeMusicSong.SourceData(newer)["size"] = "4096";
        var request = new MusicSearchRequest("tx", "Track Artist");
        await f.Library.AdoptPlatformResultAsync(Page(request, old), default);
        f.Facts.ObservePage(request, Page(request, old), "", Context("tx"), f.Facts.BeginSearch("tx"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int searches = 0;
        var catalog = new Catalog
        {
            Detail = _ => newer,
            Search = async query =>
            {
                searches++; entered.TrySetResult(); await release.Task;
                return Page(query, newer);
            }
        };
        await using var owner = new MusicNativeCatalogOwner(catalog, f.Library)
        {
            SearchSongs = async (query, token) =>
            {
                var observation = f.Facts.BeginSearch(query.Platform);
                long metadataObservation = f.Library.BeginSongObservation();
                var reply = await catalog.SearchAsync(query, token);
                reply = await f.Library.AdoptPlatformResultAsync(reply, token, observation: metadataObservation);
                f.Facts.ObservePage(query, reply, "", Context(query.Platform), observation);
                return reply;
            }
        };
        var pending = Ok(owner, "music.catalog.search", new { platform = "tx", keyword = "Track Artist", refresh = true });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Clock.Now++;
            await f.Favorite(old);
            await Ok(owner, "music.catalog.song", new { platform = "tx", id = "same-id" });
            f.Clock.Now++;
            long checkedAt = f.Clock.Now;
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            var known = await f.Facts.KnownAsync(original, default);
            await f.Facts.PersistAsync(default);
            await f.CacheStore.DrainAsync();
            var state = Saved(f.Store).Single()!["platforms"]!["tx"]!;
            Check(LJ.S(NativeMusicSong.SourceData(known["sources"]!.AsArray().OfType<JsonObject>()
                .Single(source => LJ.S(source["source"]) == "tx"))["size"]) == "4096" &&
                LJ.I(state["checkedAt"]) == checkedAt && LJ.I(state["matchedAt"]) == checkedAt &&
                searches == 1 && f.Searches == 0,
                "historical imports cannot downgrade current detail metadata or reject an in-flight physical search check");
        }
        finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    private static async Task Boundaries()
    {
        await using var f = new Fixture();
        await f.Match(Song(), "tx");
        foreach (var other in new[] { Song("kg", "live", "Track (Live)"), Song("kg", "long", duration: 230),
            MusicSongFixture.Local("file:one", "Track", "Artist", duration: 180) })
            Check(!Has(await f.Facts.KnownAsync(other, default), "tx"), "incompatible/local recordings must retain independent facts");
        var a = Song("wy", "duration-chain", duration: 103);
        a["sources"]!.AsArray().Add(Song("kw", "short", duration: 100)["sources"]![0]!.DeepClone());
        var b = Song("tx", "long-chain", duration: 106);
        await f.Facts.KnownAsync(NativeMusicSong.Normalize(a), default);
        Check(Has(await f.Facts.KnownAsync(b, default), "kw"), "106 seconds is within the fixed 103-second baseline despite the retained 100-second source");
        var samePrimary = Song("wy", "duration-chain", duration: 103);
        samePrimary["sources"]!.AsArray().Add(b["sources"]![0]!.DeepClone());
        var separate = await f.Facts.KnownAsync(NativeMusicSong.Normalize(samePrimary), default);
        Check(Has(separate, "kw") && Has(separate, "tx"),
            "an already registered primary retains its one repository record");
    }
    private static async Task LibraryFirstRead()
    {
        await using var f = new Fixture();
        var original = Song();
        await f.Favorite(original); await f.Match(original, "tx"); await f.Facts.PersistAsync(default);
        await f.CacheStore.DrainAsync();
        await using var restartedCache = new CacheDocumentStore(f.Store);
        await using var restartedLibrary = new MusicLibraryOwner(f.Store);
        var restartedFacts = new MusicSongMatcher(restartedLibrary, new(restartedCache), (_, _) =>
            throw new InvalidOperationException("first library read cannot search"), f.Clock);
        await restartedFacts.InitializeAsync(default);
        string before = Convert.ToHexString(f.Store.Data[MusicLibraryOwner.DocumentNamespace]);
        int writes = f.Store.Writes;
        var first = await Ok(restartedLibrary, "music.library.detail", new { id = "favorites" });
        Check(Has(first.GetProperty("songs")[0], "tx") && f.Store.Writes == writes &&
            Convert.ToHexString(f.Store.Data[MusicLibraryOwner.DocumentNamespace]) == before,
            "the first read must query saved facts without a prior complete command or library mutation");
    }
    private static async Task LibraryAcquisition()
    {
        await using var f = new Fixture();
        var original = Song();
        await f.Facts.KnownAsync(original, default);
        await f.Favorite(Song("tx", "imported"));
        Check(Has(await f.Facts.KnownAsync(original, default), "tx") && f.Searches == 0,
            "collection import must contribute acquired data without guessing other platform checks");
        await Ok(f.Library, "music.queue.replace", new { songs = new[] { original }, index = 0 });
        var before = await Ok(f.Library, "music.queue.get");
        await f.Match(Song("kg", "second-entry"), "kw");
        var after = await Ok(f.Library, "music.queue.get");
        Check(Has(after.GetProperty("current").GetProperty("song"), "kw") &&
            before.GetProperty("current").GetProperty("id").GetString() == after.GetProperty("current").GetProperty("id").GetString() &&
            before.GetProperty("version").GetInt64() == after.GetProperty("version").GetInt64() && f.Searches == 1,
            "queue reads must observe subsequent alias updates without changing occurrence or version");
        await f.Match(original, "mg");
        Check(f.Searches == 2, "one imported platform is not evidence of searches on other platforms");
    }
    private static async Task SinglePersistentOwner()
    {
        await using var f = new Fixture();
        var a = await f.Facts.KnownAsync(Song(), default); var b = Song("kg", "alias");
        await f.Favorite(a);
        await Ok(f.Library, "music.library.create", new { name = "Other collection", songs = new[] { b } });
        await f.Match(a, "tx"); await f.Match(b, "kw");
        await f.Facts.PersistAsync(default);
        await f.CacheStore.DrainAsync();
        var known = await f.Facts.KnownAsync(a, default);
        Check(Saved(f.Store).Count == 1 && Saved(f.Store)[0]!["song"] is null &&
            LJ.S(Saved(f.Store)[0]!["songId"]) == LJ.S(a["songId"]) && Has(known, "tx") && Has(known, "kw"),
            "discovery persistence only references the one mutable repository record");
        await Ok(f.Library, "music.library.removeSong", new { id = "favorites", ifRevision = 1, songId = LJ.S(a["songId"]) });
        await f.Facts.PersistAsync(default);
        await f.CacheStore.DrainAsync();
        Check(Saved(f.Store).Count == 1, "another collection keeps the same owner eligible");
        await Ok(f.Library, "music.library.remove", new { id = "playlist:1", ifRevision = 1 });
        await f.Facts.PersistAsync(default);
        await f.CacheStore.DrainAsync();
        Check(Saved(f.Store).Count == 1 && Has(await f.Facts.KnownAsync(a, default), "tx"),
            "collection membership does not own or delete the permanent song record");
    }
    private static async Task EmptySearch()
    {
        await using var f = new Fixture();
        var song = await f.Facts.KnownAsync(Song(), default);
        var request = new MusicSearchRequest("tx", "Track Artist");
        f.Facts.ObservePage(request, Page(request), "", Context("tx"), f.Facts.BeginSearch("tx"));
        await f.Match(song, "tx");
        Check(f.Searches == 0, "an empty real focused request applies to a known recording without a displayed row");
        f.Facts.ObservePage(request, null, "NETWORK_ERROR", Context("tx"), f.Facts.BeginSearch("tx"));
        await f.Match(song, "tx");
        Check(f.Searches == 1, "a failed request replaces empty evidence and cannot suppress another attempt");
    }
    private static async Task EmptyBeforeHit()
    {
        await using var f = new Fixture();
        var song = await f.Facts.KnownAsync(Song(), default);
        long emptyAt = f.Clock.Now, emptyOrder = f.Facts.NextObservation(), hitOrder = f.Facts.NextObservation();
        f.Clock.Now++;
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            kind = "songs", keyword = "Track Artist", cancelled = false, items = new[] { song },
            observations = new[]
            {
                new { platform = "tx", page = 1, pages = 1, limit = 30, checkedAt = emptyAt,
                    order = emptyOrder, generation = 0, cached = false, error = "", items = Array.Empty<JsonObject>() },
                new { platform = "wy", page = 1, pages = 1, limit = 30, checkedAt = f.Clock.Now,
                    order = hitOrder, generation = 0, cached = false, error = "", items = new[] { song } }
            }
        });
        f.Facts.ObserveSearch(snapshot, Context);
        await f.Match(song, "tx"); await f.Favorite(song); await f.Facts.PersistAsync(default);
        await f.CacheStore.DrainAsync();
        Check(f.Searches == 0 && LJ.I(Saved(f.Store).Single()!["platforms"]!["tx"]!["checkedAt"]) == emptyAt,
            "a platform which returned empty before discovery still contributes its original focused check");
        f.Clock.Now = emptyAt + MusicSongMatcher.NotFoundRetry;
        await f.Match(song, "tx");
        Check(f.Searches == 1, "another platform's later hit cannot extend the empty platform's retry window");
    }
    private static async Task BroadAbsence()
    {
        await using var f = new Fixture();
        var a = Song(); await f.Favorite(a);
        var request = new MusicSearchRequest("tx", "Artist");
        f.Facts.ObservePage(request, Page(request), "", Context("tx"), f.Facts.BeginSearch("tx"));
        await f.Facts.PersistAsync(default);
        await f.CacheStore.DrainAsync();
        Check(Saved(f.Store).All(entry => entry!["platforms"]!["tx"] is null),
            "a broad page must not label an omitted recording notFound");
        await f.Match(a, "tx");
        Check(f.Searches == 1, "focused supplementary discovery remains eligible");
    }
    private static async Task CacheReplay()
    {
        await using var f = new Fixture();
        var original = Song(); await f.Favorite(original);
        var oldRequest = new MusicSearchRequest("tx", "Track Artist");
        var old = f.Facts.BeginSearch("tx");
        await f.Library.AdoptPlatformResultAsync(Page(oldRequest, Song("tx", "same-id")), default);
        f.Facts.ObservePage(oldRequest, Page(oldRequest, Song("tx", "same-id")), "", Context("tx"), old);
        f.Clock.Now++;
        var newer = Song("tx", "same-id"); NativeMusicSong.SourceData(newer)["size"] = "8192";
        await f.Library.AdoptPlatformResultAsync(Page(oldRequest, newer), default);
        f.Facts.ObservePage(oldRequest, Page(oldRequest, newer), "", Context("tx"), f.Facts.BeginSearch("tx"));
        long checkedAt = f.Clock.Now;
        f.Clock.Now++;
        var cached = JsonSerializer.SerializeToElement(new { kind = "songs", keyword = "Track Artist", cancelled = false,
            items = new[] { Song("tx", "same-id") }, observations = new[] { new {
                platform = "tx", page = 1, pages = 1, limit = 30, checkedAt = checkedAt - 1,
                order = old.Order, generation = 0, cached = true, error = "", items = new[] { Song("tx", "same-id") }
            } } });
        f.Facts.ObserveSearch(cached, Context);
        await f.Facts.PersistAsync(default);
        await f.CacheStore.DrainAsync();
        var known = await f.Facts.KnownAsync(original, default);
        Check(LJ.I(Saved(f.Store).Single()!["platforms"]!["tx"]!["checkedAt"]) == checkedAt &&
            LJ.S(NativeMusicSong.SourceData(known["sources"]!.AsArray().OfType<JsonObject>()
                .Single(source => LJ.S(source["source"]) == "tx"))["size"]) == "8192" && f.Searches == 0,
            "replayed cached rows cannot replace a newer source or renew its age");
    }
    private static async Task RetiredObservation()
    {
        await using var f = new Fixture();
        var song = await f.Facts.KnownAsync(Song(), default);
        var retired = f.Facts.BeginSearch("tx");
        f.Facts.Invalidate("tx");
        var stale = JsonSerializer.SerializeToElement(new
        {
            kind = "songs", keyword = "Track Artist", cancelled = false, items = new[] { Song("tx", "retired") },
            observations = new[] { new {
                platform = "tx", page = 1, pages = 1, limit = 30, checkedAt = f.Clock.Now,
                order = retired.Order, generation = retired.Generation, cached = false, error = "",
                items = new[] { Song("tx", "retired") }
            } }
        });
        f.Facts.ObserveSearch(stale, Context);
        Check(!Has(await f.Facts.KnownAsync(song, default), "tx"), "retired candidates cannot even seed an owner");
        await f.Match(song, "tx");
        Check(f.Searches == 1, "the new account generation remains eligible for a physical request");
    }
    private static async Task CatalogFirstRead()
    {
        await using var f = new Fixture();
        await f.Favorite(Song()); await f.Match(Song(), "tx"); await f.Facts.PersistAsync(default);
        await f.CacheStore.DrainAsync();
        await using var restartedCache = new CacheDocumentStore(f.Store);
        var catalog = new Catalog();
        await using var owner = new MusicNativeCatalogOwner(catalog, f.Library);
        var restarted = new MusicSongMatcher(f.Library, new(restartedCache), (_, _) =>
            throw new InvalidOperationException("catalog projection cannot supplement"), f.Clock);
        await restarted.InitializeAsync(default);
        var result = await owner.ExecuteAsync("music.catalog.song", new { platform = "wy", id = "original" });
        Check(result.GetProperty("ok").GetBoolean() && Has(result.GetProperty("song"), "tx") && catalog.Calls == 1,
            "the first catalog result must query shared facts, performing only the requested acquisition");
    }
    private static async Task CatalogLateSearch()
    {
        await using var f = new Fixture();
        var song = await f.Facts.KnownAsync(Song(), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new Catalog(); int calls = 0;
        catalog.Search = async request =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { entered.TrySetResult(); await release.Task; return Page(request, Song("tx", "older-catalog")); }
            return Page(request, Song("tx", "newer-catalog"));
        };
        await using var owner = new MusicNativeCatalogOwner(catalog, f.Library)
        {
            SearchSongs = async (request, token) =>
            {
                var observation = f.Facts.BeginSearch(request.Platform);
                long metadataObservation = f.Library.BeginSongObservation();
                var reply = await catalog.SearchAsync(request, token);
                reply = await f.Library.AdoptPlatformResultAsync(reply, token, observation: metadataObservation);
                f.Facts.ObservePage(request, reply, "", Context(request.Platform), observation);
                return reply;
            }
        };
        var old = owner.ExecuteAsync("music.catalog.search", new { platform = "tx", keyword = "Track Artist" });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var current = await owner.ExecuteAsync("music.catalog.search", new { platform = "tx", keyword = "Track Artist" });
            Check(current.GetProperty("ok").GetBoolean(), "new catalog search must settle");
            release.TrySetResult();
            Check((await old.WaitAsync(TimeSpan.FromSeconds(3))).GetProperty("ok").GetBoolean(), "old page may still display");
            var known = await f.Facts.KnownAsync(song, default);
            var ids = known["sources"]!.AsArray().OfType<JsonObject>().Select(NativeMusicSong.NativeId).ToArray();
            Check(ids.Contains("newer-catalog") && !ids.Contains("older-catalog") && catalog.Calls == 2 && f.Searches == 0,
                "catalog output projection cannot reaccept a search already rejected by observation order");
        }
        finally { release.TrySetResult(); await old.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    private static async Task CompletionContract()
    {
        await using var store = new MemoryStore();
        var clock = new Clock();
        await using var runtime = new MusicPlatformRuntimeFixture();
        string mid = "FixtureMID";
        runtime.OnRequest = (call, _) =>
        {
            if (call.Operation == "account.verify")
            {
                var verified = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation("tx", "123").GetRawText())!.AsObject();
                verified["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
                return Task.FromResult(MusicPlatformRuntimeFixture.Json(verified));
            }
            return Task.FromResult(call.Operation switch
            {
                "search" => MusicPlatformRuntimeFixture.Json(new { platform = "tx", page = 1, limit = 30,
                    items = new[] { Song("tx", mid, "Same Song", 100) }, more = false }),
                _ => throw new InvalidOperationException(call.Operation)
            });
        };
        runtime.OnOtherCall = (call, _) => call.Method == "music.script.sources.list"
            ? Task.FromResult(MusicPlatformRuntimeFixture.Json(new {
                ok = true, contract = "music.script.v3", value = new { items = Array.Empty<object>() }
            }))
            : throw new InvalidOperationException(call.Method);
        int Searches() => runtime.Requests.Count(call => call.Operation == "search");
        await using var app = new MusicNativeApplication(store, runtime, null, null, clock);
        await Ok(app, "music.accounts.import", new { platform = "tx", cookie = "musicid=123; musickey=synthetic; encrypt_uin=FixtureEUIN" });
        var original = Song(title: "Same Song", duration: 100);
        var first = await Ok(app, "music.songs.complete", new { song = original, platforms = new[] { "tx" } });
        Check(Has(ReadSong(first), "tx") && Searches() == 1, "missing platforms complete through the existing dispatch");
        await Ok(app, "music.songs.complete", new { song = original, platforms = new[] { "tx" }, contentFailed = new[] { "tx" } });
        Check(Searches() == 1, "fresh content failure must not recheck");
        clock.Now += MusicSongMatcher.Freshness;
        await Ok(app, "music.songs.complete", new { song = original, platforms = new[] { "tx" } });
        await Ok(app, "music.songs.complete", new { song = original, platforms = new[] { "tx" }, contentFailed = Array.Empty<string>() });
        Check(Searches() == 1, "omitted or empty contentFailed never searches old existing mappings");
        mid = "ReplacementMID";
        var rechecked = await Ok(app, "music.songs.complete", new { song = original, platforms = new[] { "tx" }, contentFailed = new[] { "tx" } });
        Check(Searches() == 2 && rechecked.GetProperty("song").GetProperty("sources").EnumerateArray()
            .Any(source => source.GetProperty("musicInfo").GetProperty("songmid").GetString() == "ReplacementMID") &&
            runtime.Calls.All(call => call.Method != "music.script.request"),
            "content failure plus seven days must recheck through canonical platform matching");
        var malformed = await app.ExecuteAsync("music.songs.complete", new { song = original, platforms = new[] { "tx" }, contentFailed = new[] { "unknown" } });
        Check(!malformed.GetProperty("ok").GetBoolean() && Searches() == 2, "invalid optional platform data cannot dispatch requests");
        var alias = await app.ExecuteAsync("music.song.complete", new { song = original, platforms = new[] { "tx" } });
        Check(!alias.GetProperty("ok").GetBoolean() && Searches() == 2, "no new completion method alias is introduced");
    }
    private sealed class Catalog : IMusicPlatformCatalog
    {
        internal int Calls;
        internal Func<MusicSearchRequest, Task<JsonElement>> Search = _ => Unused();
        internal Func<MusicSongRequest, JsonObject> Detail = request => Song(request.Platform, request.Id);
        public Task<JsonElement> SongAsync(MusicSongRequest request, CancellationToken token = default)
        { Calls++; return Task.FromResult(LJ.Element(Detail(request))); }
        public JsonElement Describe() => throw new InvalidOperationException("unused");
        public void Invalidate(string? platform = null) { }
        public void Dispose() { }
        private static Task<JsonElement> Unused() => throw new InvalidOperationException("unexpected catalog acquisition");
        public Task<JsonElement> SearchAsync(MusicSearchRequest r, CancellationToken t = default)
        { Calls++; return Search(r); }
        public Task<JsonElement> PlaylistAsync(MusicPlaylistRequest r, CancellationToken t = default) => Unused();
        public Task<JsonElement> CompletePlaylistAsync(MusicPlaylistRequest r, CancellationToken token = default) => throw new NotSupportedException();
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

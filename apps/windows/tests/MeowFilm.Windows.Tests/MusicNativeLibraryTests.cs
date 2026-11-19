using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Lyrics;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeLibraryTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in FullAppendCases()) yield return test;
        foreach (var test in RecordingConsumerCases()) yield return test;
        foreach (var test in OnlineSettingsCases()) yield return test;
        foreach (var test in LyricPersistenceCases()) yield return test;
        yield return new("Native music library contract version and storage identity", Contract);
        yield return new("Native music library initial reads never write control or song records", Initial);
        yield return new("Native music collection member edits exact ordering revisions and tombstones", Collections);
        yield return new("Native music favorites protected and local intents persist", Favorites);
        yield return new("Native music collection paging search and invalid paging preserve state", Paging);
        yield return new("Native music permanent UUID preserves platform locators and current-format reload", Identity);
        yield return new("Native music Unicode metadata is preserved independently of permanent UUID", UnicodeIdentity);
        yield return new("Native music same native identity enrichment and recording conflicts", Recording);
        yield return new("Native music song projection ignores unused fields and omits credentials and audio URLs", RejectTransientSong);
        yield return new("Native music catalog full collection handoff uses expected queue version", FullCollection);
        yield return new("Native music duplicate queue occurrences and next priority", Occurrences);
        yield return new("Native music queue rejects modes outside the three global loop choices", InvalidMode);
        yield return new("Native music queue loop mode boundaries", () => Modes(1));
        yield return new("Native music queue single repeats ended only", () => Modes(3));
        yield return new("Native music queue shuffle previous uses runtime history", Shuffle);
        yield return new("Native music queue remove reorder clear and archive semantics", QueueEdits);
        yield return new("Native music replace retains all archived queues and restores occurrences", Archives);
        yield return new("Native music actual-play history deduplicates and retains all local days", Played);
        yield return new("Native music queue and history restart preserve selection not session clocks", Restart);
        yield return new("Native music SQLite records reopen without Node", PlainFiles);
        yield return new("Native music before commit failure reads the actual durable outcome without replay", () => UnknownCommit(false));
        yield return new("Native music after commit failure reads the actual durable outcome without replay", () => UnknownCommit(true));
        yield return new("Native music third-party revision conflict stays fenced without a discard fallback", Conflict);
        yield return new("Native music explicit null owned areas never become empty state", NullAreas);
        yield return new("Native music SQLite preserves unconsumed extensions and rejects selected damaged sources without writes", UnrelatedMaterials);
        yield return new("Native music read failure does not become empty library", ReadFailure);
        yield return new("Native music cancellation before admission cannot mutate state", CancelAdmission);
        yield return new("Native music cancellation after transaction admission observes real result", CancelCommit);
        yield return new("Native music shutdown pending failure and reversible preparation", Shutdown);
        yield return new("Native music online settings revision and exact fields", Settings);
        yield return new("Native music damaged current references and owned areas fail without writes", DamagedDocuments);
        yield return new("Native music damaged song rows fail on record reads without an all-song load or rewrite", DamagedSongRecord);
        yield return new("Native music plain LRC keeps full text empty words and duplicate timestamps", LyricsLrc);
        yield return new("Native music YRC QRC exact intervals and negative offset", LyricsWords);
        yield return new("Native music canonical lyric text preserves negative-offset words and zero durations", LyricsCanonicalWords);
        yield return new("Native music word LRC absolute relative tags and trailing markers", LyricsMarkers);
        yield return new("Native music word LRC preserves sub-50ms and zero intervals", LyricsShortMarkers);
        yield return new("Native music word LRC negative offsets retain elapsed word progress", LyricsNegativeMarkers);
        yield return new("Native music word LRC uses real next-line boundaries and song length only for rows", LyricsTrailingWords);
        yield return new("Native music word LRC without end boundary keeps timestamps and zero trailing duration", LyricsUnboundedTrailingWords);
        yield return new("Native music QRC XML entity decode never interprets markup", LyricsXml);
        yield return new("Native music translation exact timestamps only no nearest matching", LyricsTranslation);
        yield return new("Native music lyrics local JSON adapter accepts large text and line counts with valid timing", LyricsAdapter);
    }
    private static void Check(bool c, string message = "Assertion failed") { if (!c) throw new InvalidOperationException(message); }
    private static readonly JsonSerializerOptions PersistedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static JsonObject O(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonArray A(IEnumerable<JsonNode?> nodes) => new(nodes.Select(n => n?.DeepClone()).ToArray());
    private static JsonObject Song(string id = "a") =>
        MusicSongFixture.Local("library:local:" + id, "Track " + id, "Test artist", duration: 180);
    private static JsonObject Online(string id, string? title = null, string artist = "Test artist", int duration = 180)
    {
        string name = title ?? "Track " + id;
        return MusicSongFixture.Online("wy", id, name, artist, duration: duration,
            native: O(new { name, singer = artist, duration }));
    }
    private static async Task<JsonElement> Ok(MusicLibraryOwner owner, string method, object? args = null)
    {
        var reply = await owner.ExecuteAsync(method, args);
        Check(reply.GetProperty("ok").GetBoolean(), method + ": " + (reply.TryGetProperty("code", out var code) ? code.GetString() : "missing"));
        return reply;
    }
    private static async Task Error(MusicLibraryOwner owner, string method, object? args, string expected)
    {
        var reply = await owner.ExecuteAsync(method, args);
        Check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("code").GetString() == expected, method + ": " + reply);
    }
    private static async Task Throws(string code, Func<Task> action)
    {
        try { await action(); } catch (MusicLibraryException e) { Check(e.Code == code, $"Expected {code}, actual {e.Code}"); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static JsonObject Parsed(JsonElement e) => JsonNode.Parse(e.GetRawText())!.AsObject();
    private sealed class Store : MusicRecordTestStore
    {
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store = new();
        internal readonly LyricCacheStore Cache = new();
        private readonly List<MusicLibraryOwner> owners = [];
        internal MusicLibraryOwner Owner(IMusicLibraryStore? store = null, string scope = "local")
        { var owner = new MusicLibraryOwner(store ?? Store, scope, Cache); owners.Add(owner); return owner; }
        public async ValueTask DisposeAsync() { foreach (var o in owners) await o.AbortAsync(); await Store.DisposeAsync(); }
    }
    private static Task Contract()
    {
        Check(MusicLibraryOwner.ContractVersion == "0.7.0");
        return Task.CompletedTask;
    }
    private static async Task Initial()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        var list = await Ok(owner, "music.library.list"); Check(list.GetProperty("total").GetInt32() == 1 && list.GetProperty("items")[0].GetProperty("id").GetString() == "favorites");
        var q = await Ok(owner, "music.queue.get"); Check(q.GetProperty("total").GetInt32() == 0 && q.GetProperty("mode").GetInt32() == 1);
        await Ok(owner, "music.history.get"); await Ok(owner, "music.settings.get");
        Check(f.Store.Bytes is null && f.Store.Writes == 0);
    }
    private static async Task Collections()
    {
        await using var f = new Fixture(); var o = f.Owner();
        var c = (await Ok(o, "music.library.create", new { name = "List", songs = new[] { Song("a"), Song("b"), Song("a") } })).GetProperty("collection");
        string id = c.GetProperty("id").GetString()!; Check(c.GetProperty("count").GetInt32() == 2);
        await Error(o, "music.library.edit", new { id, ifRevision = 0, name = "Stale" }, "REVISION_CONFLICT");
        await Ok(o, "music.library.addSongs", new { id, ifRevision = 1, songs = new[] { Song("c") } });
        var songs = (await Ok(o, "music.library.detail", new { id })).GetProperty("songs");
        string a = songs[0].GetProperty("songId").GetString()!, b = songs[1].GetProperty("songId").GetString()!,
            cId = songs[2].GetProperty("songId").GetString()!;
        await Ok(o, "music.library.reorderSong", new
        {
            id,
            ifRevision = 2,
            songId = cId,
            targetId = b,
            after = false
        });
        var detail = await Ok(o, "music.library.detail", new { id }); Check(detail.GetProperty("songs")[1].GetProperty("songId").GetString() == cId);
        await Ok(o, "music.library.removeSong", new { id, ifRevision = 3, songId = b });
        await Ok(o, "music.library.edit", new { id, ifRevision = 4, name = "Renamed", order = new[] { cId, a } });
        var library = f.Store.State("library")["library"]!;
        Check(!library["collections"]![1]!["intents"]!.AsObject().ContainsKey(b));
        await Ok(o, "music.library.remove", new { id, ifRevision = 5 });
        Check((await Ok(o, "music.library.list")).GetProperty("total").GetInt32() == 1);
        Check(f.Store.State("library")["library"]!["collections"]![1]!["deleted"]!.GetValue<bool>());
    }
    private static async Task Favorites()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Error(o, "music.library.remove", new { id = "favorites", ifRevision = 0 }, "FAVORITES_PROTECTED");
        await Error(o, "music.library.edit", new { id = "favorites", ifRevision = 0, name = "Name" }, "INVALID_NAME");
        await Ok(o, "music.library.addSongs", new { id = "favorites", ifRevision = 0, songs = new[] { Song() } });
        string songId = (await Ok(o, "music.library.detail", new { id = "favorites" })).GetProperty("songs")[0].GetProperty("songId").GetString()!;
        var added = f.Store.State("library")["library"]!["collections"]![0]!["intents"]![songId]!;
        Check(added["present"]!.GetValue<bool>() && added["pending"]!.GetValue<bool>());
        await Ok(o, "music.library.removeSong", new { id = "favorites", ifRevision = 1, songId });
        Check(f.Store.State("library")["library"]!["collections"]![0]!["intents"]!.AsObject().Count == 0);
        await o.DisposeAsync();
        var cold = f.Owner();
        var detail = await Ok(cold, "music.library.detail", new { id = "favorites" });
        Check(detail.GetProperty("songs").GetArrayLength() == 0);
        await Ok(cold, "music.library.addSongs", new { id = "favorites", ifRevision = 2, songs = new[] { Song() } });
        var readded = f.Store.State("library")["library"]!["collections"]![0]!["intents"]![songId]!;
        Check(readded["present"]!.GetValue<bool>() && readded["pending"]!.GetValue<bool>());
    }
    private static async Task Paging()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.library.create", new { name = "Page", songs = Enumerable.Range(0, 103).Select(i => Song(i.ToString())).ToArray() });
        var page = await Ok(o, "music.library.detail", new { id = "playlist:1", offset = 100, limit = 3 });
        Check(page.GetProperty("songs").GetArrayLength() == 3 && page.GetProperty("total").GetInt32() == 103);
        var search = await Ok(o, "music.library.search", new { query = "TRACK 102" }); Check(search.GetProperty("total").GetInt32() == 1);
        int writes = f.Store.Writes;
        await Error(o, "music.library.list", new { limit = 101 }, "INVALID_REQUEST");
        await Error(o, "music.library.list", new { offset = -1 }, "INVALID_REQUEST"); Check(f.Store.Writes == writes);
    }
    private static async Task Identity()
    {
        await using var f = new Fixture(); var o = f.Owner();
        var cases = new[]
        {
            MusicSongFixture.Online("wy", "42", "Test (Live)", "B; A", duration: 180),
            MusicSongFixture.Online("kg", "42", "Test", "Tester", duration: 180, native: O(new { hash = "ABC" })),
            MusicSongFixture.Online("tx", "9223372036854775806", "Unknown", "unknown")
        };
        var ids = new HashSet<string>();
        foreach (var song in cases)
        {
            await Ok(o, "music.queue.replace", new { songs = new[] { song }, index = 0 });
            var current = (await Ok(o, "music.queue.get")).GetProperty("current").GetProperty("song");
            string id = current.GetProperty("songId").GetString()!;
            Check(Guid.TryParseExact(id, "D", out _) && ids.Add(id));
            Check(MusicSongFixture.NativeId(current) == NativeMusicSong.NativeId(song));
            await Ok(o, "music.queue.replace", new { songs = new[] { song }, index = 0 });
            Check((await Ok(o, "music.queue.get")).GetProperty("current").GetProperty("song").GetProperty("songId").GetString() == id);
        }
        await o.DisposeAsync();
        var reopened = (await Ok(f.Owner(), "music.queue.get")).GetProperty("current").GetProperty("song");
        Check(ids.Contains(reopened.GetProperty("songId").GetString()!));
    }
    private static async Task UnicodeIdentity()
    {
        var cases = new[]
        {
            ("星😀 (Live)", "Ｂ; A", 185, "emoji"),
            ("歌曲live", "艺人", 185, "unicode"),
            ("A\u2028B\u2029C", "A\u0085B", 185, "whitespace"),
            ("Any", "Artist", 0, "9007199254740993123"),
            ("İSTANBUL", "ΟΣ", 180, "case"),
            ("ΟΣ́", "İ", 180, "case2")
        };
        await using var f = new Fixture(); var o = f.Owner();
        foreach (var (title, artist, duration, id) in cases)
        {
            var song = MusicSongFixture.Online("wy", id, title, artist, duration: duration);
            await Ok(o, "music.queue.replace", new { songs = new[] { song }, index = 0 });
            var current = (await Ok(o, "music.queue.get")).GetProperty("current").GetProperty("song");
            Check(Guid.TryParseExact(current.GetProperty("songId").GetString(), "D", out _) &&
                current.GetProperty("title").GetString() == title && current.GetProperty("artist").GetString() == artist &&
                MusicSongFixture.NativeId(current) == id);
        }
    }
    private static async Task Recording()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.library.create", new { name = "First", songs = new[] { Online("a") } });
        string id = (await Ok(o, "music.library.detail", new { id = "playlist:1" })).GetProperty("songs")[0].GetProperty("songId").GetString()!;
        await Ok(o, "music.library.addSongs", new { id = "playlist:1", ifRevision = 1, songs = new[] { Online("a", "Corrected") } });
        Check((await Ok(o, "music.library.detail", new { id = "playlist:1" })).GetProperty("songs")[0].GetProperty("songId").GetString() == id);
        await Ok(o, "music.library.create", new { name = "Same registered source", songs = new[] { Online("a", "Other recording", "Other singer") } });
        Check((await Ok(o, "music.library.detail", new { id = "playlist:2" })).GetProperty("songs")[0].GetProperty("songId").GetString() == id);
        await Ok(o, "music.library.create", new { name = "Different take", songs = new[] { Online("b", "Corrected", duration: 250) } });
        string other = (await Ok(o, "music.library.detail", new { id = "playlist:3" })).GetProperty("songs")[0].GetProperty("songId").GetString()!;
        Check(id != other);
    }
    private static async Task RejectTransientSong()
    {
        await using var f = new Fixture(); var o = f.Owner(); var song = Online("a");
        song["url"] = "https://media.invalid/temporary"; song["cover_url"] = "https://image.invalid/a?size=large";
        song["cookie"] = "synthetic-private"; MusicSongFixture.Info(song)["headers"] = O(new { Authorization = "synthetic-private" });
        await Ok(o, "music.queue.replace", new { songs = new[] { song }, index = 0 });
        Check(!Encoding.UTF8.GetString(f.Store.Bytes!).Contains("synthetic-private") &&
            !Encoding.UTF8.GetString(f.Store.Bytes!).Contains("https://media.invalid/temporary"));
        var fabricated = Online("b"); fabricated["favoriteCount"] = 250_000;
        await Ok(o, "music.queue.replace", new { songs = new[] { fabricated }, index = 0 });
        Check(!(await Ok(o, "music.queue.get")).GetProperty("current").GetProperty("song").TryGetProperty("favoriteCount", out _));
    }
    private static async Task FullCollection()
    {
        await using var f = new Fixture(); var o = f.Owner(); var songs = Enumerable.Range(0, 1001).Select(i => E(Song(i.ToString()))).ToArray();
        await Ok(o, "music.library.create", new { name = "Complete collection", songs });
        var created = await o.CreatePlaylistAsync("Native full collection", songs); Check(created.GetProperty("ok").GetBoolean());
        var q = await Ok(o, "music.queue.get"); long version = q.GetProperty("version").GetInt64();
        var replaced = await o.ReplaceQueueAsync(songs, 1000, null, version); Check(replaced.GetProperty("ok").GetBoolean());
        var stale = await o.ReplaceQueueAsync(songs, 0, null, version); Check(stale.GetProperty("code").GetString() == "REVISION_CONFLICT");
        Check((await Ok(o, "music.queue.get")).GetProperty("total").GetInt32() == 1001);
    }
    private static async Task Occurrences()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.queue.replace", new { songs = new[] { Song(), Song() }, index = 1 });
        var q = await Ok(o, "music.queue.get"); var ids = q.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        Check(ids.Distinct().Count() == 2);
        var added = await Ok(o, "music.queue.append", new { songs = new[] { Song("next") }, next = true });
        string next = added.GetProperty("entryIds")[0].GetString()!;
        await Ok(o, "music.queue.mode", new { mode = 3 });
        Check((await Ok(o, "music.queue.next", new { reason = "ended" })).GetProperty("targetId").GetString() == next);
    }
    private static async Task InvalidMode()
    {
        await using var f = new Fixture(); var o = f.Owner();
        foreach (int mode in new[] { -1, 0, 4 })
            await Error(o, "music.queue.mode", new { mode }, "INVALID_REQUEST");
        Check((await Ok(o, "music.queue.get")).GetProperty("mode").GetInt32() == 1);
    }
    private static async Task Modes(int mode)
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.queue.replace", new { songs = new[] { Song("a"), Song("b") }, index = 1 });
        var q = await Ok(o, "music.queue.get"); string a = q.GetProperty("entries")[0].GetProperty("id").GetString()!, b = q.GetProperty("currentId").GetString()!;
        await Ok(o, "music.queue.mode", new { mode });
        var result = await Ok(o, "music.queue.next", new { reason = "ended" });
        Check(result.GetProperty("targetId").GetString() == (mode == 1 ? a : b));
        if (mode == 3) Check((await Ok(o, "music.queue.next")).GetProperty("targetId").GetString() == a);
        await Ok(o, "music.queue.select", new { id = a });
        Check((await Ok(o, "music.queue.previous")).GetProperty("targetId").GetString() == b);
    }
    private static async Task Shuffle()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.queue.replace", new { songs = new[] { Song("a"), Song("b"), Song("c") }, index = 0 }); await Ok(o, "music.queue.mode", new { mode = 2 });
        string before = (await Ok(o, "music.queue.get")).GetProperty("currentId").GetString()!;
        var next = await Ok(o, "music.queue.next"); Check(next.GetProperty("currentId").GetString() != before);
        Check((await Ok(o, "music.queue.previous")).GetProperty("targetId").GetString() == before);
        var visited = new List<string> { before };
        for (int i = 0; i < 105; i++)
            visited.Add((await Ok(o, "music.queue.next")).GetProperty("currentId").GetString()!);
        await Ok(o, "music.queue.replace", new { songs = new[] { Song("replacement") }, index = 0 });
        string archive = (await Ok(o, "music.history.list", new { kind = "playlists" }))
            .GetProperty("items")[0].GetProperty("id").GetString()!;
        await Ok(o, "music.history.restorePlaylist", new { id = archive });
        for (int i = visited.Count - 2; i >= 0; i--)
            Check((await Ok(o, "music.queue.previous")).GetProperty("targetId").GetString() == visited[i]);
        await o.DisposeAsync(); var reopened = f.Owner();
        Check((await Ok(reopened, "music.queue.previous")).GetProperty("targetId").ValueKind == JsonValueKind.Null);
    }
    private static async Task QueueEdits()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.queue.replace", new { songs = new[] { Song("a"), Song("b") }, index = 0 });
        var q = await Ok(o, "music.queue.get"); var ids = q.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
        await Error(o, "music.queue.reorder", new { ids = new[] { ids[0], ids[0] } }, "INVALID_ORDER");
        await Ok(o, "music.queue.reorder", new { ids = ids.Reverse().ToArray() });
        var removed = await Ok(o, "music.queue.remove", new { id = ids[0] }); Check(removed.GetProperty("wasCurrent").GetBoolean() && removed.GetProperty("nextId").GetString() == ids[1]);
        await Ok(o, "music.queue.clear");
        Check((await Ok(o, "music.history.get", new { kind = "playlists" })).GetProperty("total").GetInt32() == 0);
    }
    private static async Task Archives()
    {
        await using var f = new Fixture(); var o = f.Owner();
        for (int i = 0; i < 7; i++) await Ok(o, "music.queue.replace", new { songs = new[] { Song("a" + i), Song("b" + i) }, index = 1, context = new { sourceTitle = "Set " + i } });
        await o.DisposeAsync(); o = f.Owner();
        var h = await Ok(o, "music.history.list", new { kind = "playlists" }); Check(h.GetProperty("total").GetInt32() == 6);
        Check(h.GetProperty("items")[5].GetProperty("title").GetString() == "Set 0");
        string id = h.GetProperty("items")[1].GetProperty("id").GetString()!;
        var detail = await Ok(o, "music.history.detail", new { kind = "playlists", id }); string occurrence = detail.GetProperty("currentId").GetString()!;
        await Ok(o, "music.history.restorePlaylist", new { id });
        Check((await Ok(o, "music.queue.get")).GetProperty("currentId").GetString() == occurrence);
        await Ok(o, "music.history.removePlaylist", new { id });
        await Error(o, "music.history.restorePlaylist", new { id }, "HISTORY_NOT_FOUND");
        Check((await Ok(o, "music.history.get")).GetProperty("total").GetInt32() == 0);
    }
    private static async Task Played()
    {
        await using var f = new Fixture(); var o = f.Owner(); await Ok(o, "music.queue.replace", new { songs = new[] { Song() }, index = 0 });
        string id = (await Ok(o, "music.queue.get")).GetProperty("currentId").GetString()!;
        Check((await Ok(o, "music.history.get")).GetProperty("total").GetInt32() == 0);
        await Error(o, "music.history.recordPlayed", new { entryId = "entry-999", at = 1 }, "ENTRY_NOT_FOUND");
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        await Error(o, "music.history.recordPlayed", new { entryId = id, at = now + 86400000 }, "INVALID_REQUEST");
        for (int i = 0; i < 9; i++)
            for (int j = 0; j < 2; j++) await Ok(o, "music.history.recordPlayed", new { entryId = id, at = now - i * 86400000L - j });
        await o.DisposeAsync(); o = f.Owner();
        var days = await Ok(o, "music.history.get"); Check(days.GetProperty("total").GetInt32() == 9 &&
            days.GetProperty("items").EnumerateArray().All(day => day.GetProperty("count").GetInt32() == 1));
    }
    private static async Task Restart()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.queue.replace", new { songs = new[] { Song("a"), Song("b") }, index = 1 }); await Ok(o, "music.queue.mode", new { mode = 2 });
        string id = (await Ok(o, "music.queue.get")).GetProperty("currentId").GetString()!;
        await Ok(o, "music.history.recordPlayed", new { entryId = id, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        await o.DisposeAsync(); Check(!f.Store.Disposed); var restored = f.Owner();
        var q = await Ok(restored, "music.queue.get"); Check(q.GetProperty("mode").GetInt32() == 2 && q.GetProperty("currentId").GetString() == id);
        Check(!f.Store.State("queue").ContainsKey("history") && !Encoding.UTF8.GetString(f.Store.Bytes!).Contains("currentTime"));
    }
    private static async Task PlainFiles()
    {
        string root = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Test data root required");
        Check(Path.IsPathFullyQualified(root) && Directory.Exists(root)); string path = Path.Combine(root, "native-music-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        await using (var store = new MusicRecordTestStore(path))
        await using (var first = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage))
            await Ok(first, "music.library.create", new { name = "Plain files", songs = new[] { Song() } });
        await using var reopened = new MusicRecordTestStore(path);
        await using var second = new MusicLibraryOwner(MusicTestStores.For(reopened), database: MusicTestStores.For(reopened).Storage);
        Check((await Ok(second, "music.library.list")).GetProperty("total").GetInt32() == 2);
    }
    private static async Task UnknownCommit(bool committed)
    {
        await using var f = new Fixture(); var o = f.Owner(); f.Store.Failure = committed ? 2 : 1;
        await Error(o, "music.queue.replace", new { songs = new[] { Song() }, index = 0 }, "COMMIT_UNKNOWN");
        int writes = f.Store.Writes;
        var q = await Ok(o, "music.queue.get");
        Check(f.Store.Writes == writes && q.GetProperty("total").GetInt32() == (committed ? 1 : 0));
    }
    private static async Task Conflict()
    {
        await using var f = new Fixture(); var first = f.Owner(); var second = f.Owner();
        await Ok(first, "music.library.list"); await Ok(second, "music.library.create", new { name = "Other" });
        byte[] saved = f.Store.Bytes!.ToArray();
        await Error(first, "music.library.create", new { name = "Stale" }, "STORAGE_CONFLICT"); Check(saved.SequenceEqual(f.Store.Bytes!));
        await Throws("STORAGE_CONFLICT", () => first.ReconcileAsync());
        int writes = f.Store.Writes;
        await Error(first, "music.library.list", null, "STORAGE_CONFLICT");
        await Error(first, "music.library.create", new { name = "Never" }, "STORAGE_CONFLICT");
        Check(f.Store.Writes == writes && saved.SequenceEqual(f.Store.Bytes!));
        Check((await Ok(second, "music.library.list")).GetProperty("total").GetInt32() == 2);
    }
    private static async Task UnrelatedMaterials()
    {
        var current = await CurrentDocuments();
        string songId = current.Songs.First().Key;
        var extra = JsonNode.Parse("""{"large":900719925474099312345,"precise":0.12345678901234567890123456789,"é":1,"é":2}""")!;
        current.Songs[songId]!["future"] = extra.DeepClone();
        current.Library["future"] = extra.DeepClone();
        current.Library["library"]!["future"] = extra.DeepClone();
        current.Queue["future"] = extra.DeepClone();
        current.Queue["entries"]![0]!["context"]!["future"] = extra.DeepClone();
        current.History["days"]![0]!["songs"]![0]!["future"] = extra.DeepClone();
        await using var f = new Fixture();
        var seed = f.Owner();
        var damaged = await MusicSongFixture.AdoptAsync(seed, Online("damaged"));
        await seed.AbortAsync();
        string damagedId = damaged["songId"]!.GetValue<string>();
        string sourceKey = damaged["sources"]![0]!["sourceKey"]!.GetValue<string>();
        current.Songs[damagedId] = damaged.DeepClone();
        f.Store.Bytes = Document(("library", current.Library), ("queue", current.Queue),
            ("history", current.History), ("songs", current.Songs),
            ("online-settings", JsonValue.Create(false)!), ("future-area", extra));
        var badSource = damaged["sources"]![0]!.DeepClone().AsObject();
        badSource["musicInfo"] = false;
        string rawSource = badSource.ToJsonString(PersistedJson);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = Path.Combine(f.Store.RootPath, "music-library.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=PERSIST";
            Check(journal.ExecuteScalar() as string == "persist");
        }
        using (var damage = connection.CreateCommand())
        {
            damage.CommandText = "UPDATE song_sources SET data=$data WHERE source_key=$key";
            damage.Parameters.AddWithValue("$data", rawSource); damage.Parameters.AddWithValue("$key", sourceKey);
            Check(damage.ExecuteNonQuery() == 1);
        }
        string rawSong = f.Store.RawSong(damagedId);
        var owner = f.Owner();
        await Ok(owner, "music.library.create", new { name = "Unrelated write", extra = new { nested = true } });
        await Ok(owner, "music.queue.get");
        await Ok(owner, "music.history.get");
        await Error(owner, "music.settings.get", null, "CORRUPT_STORAGE");
        await owner.AbortAsync();
        var cold = f.Owner();
        await Error(cold, "music.queue.select", new { id = "entry-missing" }, "ENTRY_NOT_FOUND");
        Check((await Ok(cold, "music.queue.get")).GetProperty("version").GetInt64() == 1);
        await Ok(cold, "music.library.addSongs", new { id = "favorites", ifRevision = 1, songs = new[] { Song() } });
        var beforeReject = f.Store.Snapshot().Revision;
        int writes = f.Store.WriteCalls;
        await Error(cold, "music.library.create", new { name = "Selected damaged source", songs = new[] { Online("damaged") } }, "CORRUPT_STORAGE");
        Check(f.Store.Snapshot().Revision == beforeReject && f.Store.WriteCalls == writes);
        Check(f.Store.FindSource(sourceKey) == damagedId && f.Store.RawSong(damagedId) == rawSong);
        using var source = connection.CreateCommand();
        source.CommandText = "SELECT data FROM song_sources WHERE source_key=$key";
        source.Parameters.AddWithValue("$key", sourceKey);
        Check(source.ExecuteScalar() as string == rawSource, "unselected damaged source was rewritten or rebound");
        Check(JsonNode.DeepEquals(f.Store.ReadSong(songId)!["future"], extra));
        Check(JsonNode.DeepEquals(f.Store.State("library")["future"], extra));
        Check(JsonNode.DeepEquals(f.Store.State("library")["library"]!["future"], extra));
        Check(JsonNode.DeepEquals(f.Store.State("queue")["entries"]![0]!["context"]!["future"], extra));
        Check(JsonNode.DeepEquals(f.Store.State("history")["days"]![0]!["songs"]![0]!["future"], extra));
        var saved = f.Store.Snapshot();
        Check(!E(saved.Areas["online-settings"]!).GetBoolean());
        Check(JsonNode.DeepEquals(JsonNode.Parse(E(saved.Areas["future-area"]!).GetRawText()), extra));
        string raw = f.Store.RawSong(songId);
        Check(raw.Contains("900719925474099312345", StringComparison.Ordinal) &&
            raw.Contains("0.12345678901234567890123456789", StringComparison.Ordinal));
    }
    private static byte[] Document(params (string Area, JsonNode Value)[] values)
    {
        var areas = new JsonObject(); var songs = new JsonObject();
        foreach (var (area, value) in values)
            if (area == "songs") songs = value.DeepClone().AsObject();
            else areas[area] = value.DeepClone();
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            { ["schema"] = 3, ["areas"] = areas, ["songs"] = songs }, PersistedJson);
    }
    private static async Task<(JsonObject Library, JsonObject Queue, JsonObject History, JsonObject Songs)> CurrentDocuments()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        await Ok(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 0, songs = new[] { Song() } });
        await Ok(owner, "music.queue.replace", new { songs = new[] { Song() }, index = 0 });
        string entryId = (await Ok(owner, "music.queue.get")).GetProperty("currentId").GetString()!;
        await Ok(owner, "music.history.recordPlayed", new { entryId, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1 });
        return (f.Store.State("library"), f.Store.State("queue"), f.Store.State("history"), f.Store.Root["songs"]!.AsObject());
    }
    private static async Task NullAreas()
    {
        foreach (string area in new[] { "library", "queue", "history", "playback-settings", "online-settings", "native-source-exclusions" })
        {
            await using var f = new Fixture();
            f.Store.SetArea(area, null);
            byte[] original = (f.Store.Bytes ?? throw new InvalidOperationException("Expected seeded SQLite state")).ToArray(); var o = f.Owner();
            string method = area switch
            {
                "library" => "music.library.list", "queue" or "playback-settings" => "music.queue.get",
                "history" => "music.history.get", "online-settings" => "music.settings.get", _ => "music.sourceExclusions.list"
            };
            for (int i = 0; i < 2; i++)
                Check(!(await o.ExecuteAsync(method)).GetProperty("ok").GetBoolean(), area);
            Check(f.Store.Writes == 0 && original.SequenceEqual(f.Store.Bytes));
        }
    }
    private static async Task ReadFailure()
    {
        await using var f = new Fixture(); f.Store.FailRead = true; var o = f.Owner();
        await Error(o, "music.library.list", null, "STORAGE"); Check(f.Store.Bytes is null && f.Store.Writes == 0);
        f.Store.FailRead = false; await Ok(o, "music.library.list"); Check(f.Store.Writes == 0);
    }
    private static async Task CancelAdmission()
    {
        await using var f = new Fixture(); var o = f.Owner(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await o.ExecuteAsync("music.library.create", new { name = "Cancelled" }, cancel.Token);
        Check(result.GetProperty("code").GetString() == "CANCELLED" && f.Store.Writes == 0);
    }
    private static async Task CancelCommit()
    {
        await using var f = new Fixture(); var o = f.Owner(); f.Store.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); f.Store.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource(); var task = o.ExecuteAsync("music.library.create", new { name = "Admitted" }, cancel.Token);
        await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); f.Store.Release.TrySetResult();
        Check((await task).GetProperty("ok").GetBoolean() && f.Store.Writes == 1);
    }
    private static async Task Shutdown()
    {
        await using var f = new Fixture(); var o = f.Owner(); f.Store.Failure = 2;
        await Error(o, "music.library.create", new { name = "Unknown" }, "COMMIT_UNKNOWN");
        await Throws("COMMIT_UNKNOWN", () => o.PrepareShutdownAsync()); Check(!f.Store.Disposed);
        await o.ReconcileAsync(); await o.PrepareShutdownAsync();
        await Error(o, "music.library.list", null, "CLOSED"); await o.CancelShutdownPreparationAsync();
        Check((await Ok(o, "music.library.list")).GetProperty("total").GetInt32() == 2);
        await o.DisposeAsync(); Check(!f.Store.Disposed); await Error(o, "music.queue.get", null, "CLOSED");
    }
    private static async Task Settings()
    {
        await using var f = new Fixture(); var o = f.Owner();
        var settings = (await Ok(o, "music.settings.get")).GetProperty("settings");
        Check(settings.GetProperty("schema").GetInt32() == 3 &&
            settings.GetProperty("revision").GetInt64() == 0 &&
            settings.GetProperty("lyrics").GetBoolean() &&
            settings.EnumerateObject().Count() == 3);
        int writes = f.Store.Writes;
        await Error(o, "music.settings.save", new { ifRevision = 0 }, "INVALID_REQUEST");
        await Error(o, "music.settings.save", new { lyrics = true }, "INVALID_REQUEST");
        Check(f.Store.Writes == writes);
        var saved = (await Ok(o, "music.settings.save", new { ifRevision = 0, lyrics = false, quality = "flac", target = new { installationId = "missing", platform = "wy" } })).GetProperty("settings");
        Check(saved.GetProperty("schema").GetInt32() == 3 && saved.GetProperty("revision").GetInt64() == 1 &&
            !saved.GetProperty("lyrics").GetBoolean() && saved.EnumerateObject().Count() == 3);
        await Error(o, "music.settings.save", new { ifRevision = 0, lyrics = true }, "REVISION_CONFLICT");
        await o.DisposeAsync();
        Check((await Ok(f.Owner(), "music.settings.get")).GetProperty("settings").GetRawText() == saved.GetRawText());
    }
    private static async Task RejectDocument(byte[] bytes, string expected = "CORRUPT_STORAGE", string? method = null, object? parameters = null)
    {
        await using var f = new Fixture();
        f.Store.Bytes = bytes.ToArray();
        byte[] original = f.Store.Bytes.ToArray();
        var owner = f.Owner();
        if (method is not null) await Error(owner, method, parameters, expected);
        else await Throws(expected, async () => await owner.ReadLibrarySyncAsync());
        Check(f.Store.Writes == 0 && original.SequenceEqual(f.Store.Bytes!), "Rejected selected data changed");
    }
    private static async Task DamagedDocuments()
    {
        static JsonObject Changed(JsonObject value, Action<JsonObject> change)
        {
            var copy = (JsonObject)value.DeepClone();
            change(copy);
            return copy;
        }

        var current = await CurrentDocuments();
        var queue = current.Queue;
        foreach (var invalid in new[]
        {
            Changed(queue, q => q["entries"]![0]!["songId"] = Guid.NewGuid().ToString("D")),
            Changed(queue, q => q.Remove("source")),
            Changed(queue, q => q["currentId"] = "entry-2"),
            Changed(queue, q => q["priority"] = new JsonArray(JsonValue.Create("entry-1")))
        })
            await RejectDocument(Document(("library", current.Library), ("queue", invalid), ("songs", current.Songs)), "CORRUPT_QUEUE", "music.queue.get");

        var history = current.History;
        foreach (var invalid in new[]
        {
            Changed(history, h => h.Remove("days"))
        })
            await RejectDocument(Document(("library", current.Library), ("history", invalid), ("songs", current.Songs)), "CORRUPT_HISTORY", "music.history.get");

        var library = current.Library;

        foreach (var invalid in new[]
        {
            Changed(library, state => state["library"]!.AsObject().Remove("accounts")),
            Changed(library, state => state["revision"] = 2),
            Changed(library, state => state["library"]!["accounts"]!["kw"] = "account-a"),
            Changed(library, state => state["library"]!["accounts"]!["wy"] = new JsonArray()),
            Changed(library, state => state["library"]!["collections"]![0]!["locallyCreated"] = true),
            Changed(library, state =>
            {
                var playlist = (JsonObject)state["library"]!["collections"]![0]!.DeepClone();
                playlist["id"] = "playlist:01";
                playlist["kind"] = "playlist";
                state["library"]!["collections"]!.AsArray().Add(playlist);
                state["library"]!["nextId"] = 2;
            }),
            Changed(library, state =>
            {
                var playlist = (JsonObject)state["library"]!["collections"]![0]!.DeepClone();
                playlist["id"] = "playlist:1";
                playlist["kind"] = "playlist";
                state["library"]!["collections"]!.AsArray().Add(playlist);
                state["library"]!["nextId"] = 1;
            })
        })
            await RejectDocument(Document(("library", invalid), ("songs", current.Songs)), "CORRUPT_LIBRARY");


        await RejectDocument(Document(("online-settings", O(new
        {
            schema = 2,
            revision = 0,
            lyrics = true,
            target = new { installationId = "missing-installation", platform = "wy" }
        }))), "CORRUPT_STORAGE", "music.settings.get");
    }
    private static async Task DamagedSongRecord()
    {
        await using var f = new Fixture(); var current = await CurrentDocuments();
        f.Store.Bytes = Document(("library", current.Library), ("songs", current.Songs));
        string id = current.Songs.First().Key;
        f.Store.CorruptSong(id, song => song["songId"] = Guid.NewGuid().ToString("D"));
        string damaged = f.Store.RawSong(id); var owner = f.Owner();
        await Ok(owner, "music.library.list"); // Opening control state does not scan unrelated song payloads.
        await Error(owner, "music.library.detail", new { id = "favorites" }, "CORRUPT_STORAGE");
        Check(f.Store.WriteCalls == 0 && f.Store.RawSong(id) == damaged, "damaged row must not be normalized or rewritten");
    }
    private static Task LyricsLrc()
    {
        var lines = MusicLyricTiming.Parse("[00:00]你好👨‍👩‍👧‍👦\n[00:06]再见", "", 10);
        Check(lines[0].GetProperty("text").GetString() == "你好👨‍👩‍👧‍👦" && lines[1].GetProperty("text").GetString() == "再见");
        Check(lines[0].GetProperty("end").GetDouble() == 6 && lines[1].GetProperty("end").GetDouble() == 10);
        var duplicates = MusicLyricTiming.Parse("[00:01]A !\n[00:01]B\n[00:08]End");
        Check(duplicates[0].GetProperty("text").GetString() == "A !" && duplicates[1].GetProperty("text").GetString() == "B");
        Check(duplicates[0].GetProperty("end").GetDouble() == 8 && duplicates[1].GetProperty("end").GetDouble() == 8);
        foreach (var row in lines.EnumerateArray().Concat(duplicates.EnumerateArray()))
            Check(row.GetProperty("words").GetArrayLength() == 0 && !row.TryGetProperty("timing", out _));
        Check(MusicLyricTiming.ActiveIndex(duplicates, 1) == 1 && MusicLyricTiming.ActiveIndex(duplicates, .9) == -1);
        return Task.CompletedTask;
    }
    private static Task LyricsWords()
    {
        foreach (string text in new[] { "[offset:-2000]\n[1000,2000](1000,1000,0)A(2000,1000,0)B", "[offset:-2000]\n[1000,2000]A(1000,1000)B(2000,1000)" })
        {
            var line = MusicLyricTiming.Parse(text)[0]; Check(line.GetProperty("time").GetDouble() == 0 && line.GetProperty("end").GetDouble() == 1);
            var words = line.GetProperty("words");
            Check(words.GetArrayLength() == 2 && line.GetProperty("text").GetString() == "AB" && !line.TryGetProperty("timing", out _));
            Check(words[0].GetProperty("time").GetDouble() == -1 && words[1].GetProperty("time").GetDouble() == 0);
            Check(words[0].GetProperty("offset").GetDouble() == -1 && words[1].GetProperty("offset").GetDouble() == 0);
            Check(words.EnumerateArray().All(word => word.GetProperty("duration").GetDouble() == 1));
        }
        foreach (string text in new[] { "[1000,0]Zero(1000,0)\n[1000,20]Tiny(1000,20)", "[1000,0](1000,0,0)Zero\n[1000,20](1000,20,0)Tiny" })
        {
            var shortLines = MusicLyricTiming.Parse(text);
            Check(shortLines[0].GetProperty("end").GetDouble() == 1 && shortLines[1].GetProperty("end").GetDouble() == 1.02);
            Check(shortLines[0].GetProperty("words")[0].GetProperty("duration").GetDouble() == 0 &&
                shortLines[1].GetProperty("words")[0].GetProperty("duration").GetDouble() == .02);
        }
        Check(MusicLyricTiming.Progress(1, 0, .99) == 0 && MusicLyricTiming.Progress(1, 0, 1) == 1);
        return Task.CompletedTask;
    }
    private static Task LyricsMarkers()
    {
        var lines = MusicLyricTiming.Parse("[00:10][00:20][[00:00.000]]X[[00:01.000]]Y[[00:02.000]]");
        Check(lines[0].GetProperty("end").GetDouble() == 12 && lines[1].GetProperty("end").GetDouble() == 22);
        for (int i = 0; i < 2; i++)
        {
            var words = lines[i].GetProperty("words");
            Check(words[0].GetProperty("time").GetDouble() == (i + 1) * 10 && words[1].GetProperty("time").GetDouble() == (i + 1) * 10 + 1);
            Check(words.EnumerateArray().All(word => word.GetProperty("duration").GetDouble() == 1));
            Check(!lines[i].TryGetProperty("timing", out _));
        }
        var absolute = MusicLyricTiming.Parse("[00:10]Lead [[00:10.000]]A[[00:10.000]]B<00:11.000>")[0];
        Check(absolute.GetProperty("text").GetString() == "Lead AB" && absolute.GetProperty("end").GetDouble() == 11);
        Check(absolute.GetProperty("words").EnumerateArray().Select(word => word.GetProperty("duration").GetDouble()).SequenceEqual(new[] { 0d, 0, 1 }));
        return Task.CompletedTask;
    }
    private static Task LyricsShortMarkers()
    {
        var lines = MusicLyricTiming.Parse("[00:01]<00:01.000>A<00:01.020>B<00:01.020>C<00:01.040>\n[00:02]<00:02.000>Z<00:02.000>");
        var words = lines[0].GetProperty("words");
        Check(lines[0].GetProperty("end").GetDouble() == 1.04 && words.GetArrayLength() == 3);
        Check(words[0].GetProperty("time").GetDouble() == 1 && words[1].GetProperty("time").GetDouble() == 1.02 &&
            words[2].GetProperty("time").GetDouble() == 1.02);
        Check(Math.Abs(words[0].GetProperty("duration").GetDouble() - .02) < 1e-12 &&
            words[1].GetProperty("duration").GetDouble() == 0 &&
            Math.Abs(words[2].GetProperty("duration").GetDouble() - .02) < 1e-12);
        Check(lines[1].GetProperty("end").GetDouble() == 2 && lines[1].GetProperty("words")[0].GetProperty("duration").GetDouble() == 0);
        return Task.CompletedTask;
    }
    private static Task LyricsNegativeMarkers()
    {
        foreach (string text in new[] {
            "[offset:-1500]\n[00:01]<00:01.000>A<00:02.000>B<00:03.000>",
            "[offset:-10500]\n[00:10][[00:00.000]]A[[00:01.000]]B[[00:02.000]]",
            "[offset:-1500]\n[00:01]A<00:02.000>B<00:03.000>"
        })
        {
            var line = MusicLyricTiming.Parse(text)[0]; var words = line.GetProperty("words");
            Check(line.GetProperty("time").GetDouble() == 0 && line.GetProperty("end").GetDouble() == 1.5);
            Check(words[0].GetProperty("time").GetDouble() == -.5 && words[0].GetProperty("offset").GetDouble() == -.5 &&
                words[0].GetProperty("duration").GetDouble() == 1);
            Check(words[1].GetProperty("time").GetDouble() == .5 && words[1].GetProperty("duration").GetDouble() == 1);
            Check(MusicLyricTiming.Progress(words[0].GetProperty("time").GetDouble(), words[0].GetProperty("duration").GetDouble(), 0) == .5);
        }
        var expired = MusicLyricTiming.Parse("[offset:-1500]\n[00:01]<00:01.000>A<00:01.020>")[0];
        Check(expired.GetProperty("time").GetDouble() == 0 && expired.GetProperty("end").GetDouble() == 0 &&
            expired.GetProperty("words")[0].GetProperty("time").GetDouble() == -.5);
        Check(Math.Abs(expired.GetProperty("words")[0].GetProperty("duration").GetDouble() - .02) < 1e-12);
        return Task.CompletedTask;
    }
    private static Task LyricsCanonicalWords()
    {
        var lines = MusicLyricTiming.Parse("[offset:-1500]\n[00:01.000]<00:01.000>A<00:01.000>B<00:01.020>\n[00:02.000]<00:02.000>Z<00:02.000>");
        var words = lines[0].GetProperty("words");
        Check(lines[0].GetProperty("text").GetString() == "AB" && lines[0].GetProperty("time").GetDouble() == 0 &&
            lines[0].GetProperty("end").GetDouble() == 0);
        Check(words[0].GetProperty("time").GetDouble() == -.5 && words[0].GetProperty("duration").GetDouble() == 0 &&
            words[1].GetProperty("time").GetDouble() == -.5 &&
            Math.Abs(words[1].GetProperty("duration").GetDouble() - .02) < 1e-12);
        Check(lines[1].GetProperty("time").GetDouble() == .5 && lines[1].GetProperty("end").GetDouble() == .5 &&
            lines[1].GetProperty("words")[0].GetProperty("duration").GetDouble() == 0);
        return Task.CompletedTask;
    }
    private static Task LyricsTrailingWords()
    {
        var lines = MusicLyricTiming.Parse("[00:01]<00:01.000>A<00:01.020>B\n[00:02]整句\n[00:03]<00:03.000>C", duration: 4);
        Check(lines[0].GetProperty("end").GetDouble() == 2 &&
            Math.Abs(lines[0].GetProperty("words")[1].GetProperty("duration").GetDouble() - .98) < 1e-12);
        Check(lines[1].GetProperty("text").GetString() == "整句" && lines[1].GetProperty("words").GetArrayLength() == 0 &&
            lines[1].GetProperty("end").GetDouble() == 3);
        Check(lines[2].GetProperty("end").GetDouble() == 4 && lines[2].GetProperty("words")[0].GetProperty("duration").GetDouble() == 0);
        var shifted = MusicLyricTiming.Parse("[offset:-2500]\n[00:01]<00:01.000>A\n[00:02]整句\n[00:03]<00:03.000>C", duration: 4);
        Check(shifted[0].GetProperty("time").GetDouble() == 0 && shifted[0].GetProperty("end").GetDouble() == 0 &&
            shifted[0].GetProperty("words")[0].GetProperty("time").GetDouble() == -1.5 &&
            shifted[0].GetProperty("words")[0].GetProperty("duration").GetDouble() == 1);
        Check(shifted[1].GetProperty("time").GetDouble() == 0 && shifted[1].GetProperty("end").GetDouble() == .5 &&
            shifted[1].GetProperty("words").GetArrayLength() == 0);
        Check(shifted[2].GetProperty("end").GetDouble() == 4 &&
            shifted[2].GetProperty("words")[0].GetProperty("duration").GetDouble() == 0);
        return Task.CompletedTask;
    }
    private static Task LyricsUnboundedTrailingWords()
    {
        foreach (var (text, time, first, last) in new[] {
            ("[00:01]<00:01.000>A<00:02.000>B", 1d, 1d, 2d),
            ("[00:10][[00:00.000]]A[[00:01.000]]B", 10d, 10d, 11d),
            ("[offset:-2500]\n[00:01]<00:01.000>A<00:02.000>B", 0d, -1.5, -.5)
        })
        {
            var line = MusicLyricTiming.Parse(text)[0]; var words = line.GetProperty("words");
            Check(line.GetProperty("time").GetDouble() == time && line.GetProperty("end").GetDouble() == time &&
                line.GetProperty("text").GetString() == "AB" && words.GetArrayLength() == 2);
            Check(words[0].GetProperty("time").GetDouble() == first && words[0].GetProperty("duration").GetDouble() == 1);
            Check(words[1].GetProperty("time").GetDouble() == last && words[1].GetProperty("duration").GetDouble() == 0);
            Check(MusicLyricTiming.Progress(last, words[1].GetProperty("duration").GetDouble(), last - .01) == 0 &&
                MusicLyricTiming.Progress(last, words[1].GetProperty("duration").GetDouble(), last) == 1);
            var boundedLine = MusicLyricTiming.Parse(text, duration: 180)[0];
            Check(boundedLine.GetProperty("end").GetDouble() == 180 &&
                boundedLine.GetProperty("words")[1].GetProperty("duration").GetDouble() == 0);
            Check(MusicLyricTiming.Progress(last, 0, 179) == 1);
        }
        var single = MusicLyricTiming.Parse("[offset:-1500]\n[00:01]<00:01.000>A")[0];
        Check(single.GetProperty("end").GetDouble() == 0 && single.GetProperty("words")[0].GetProperty("time").GetDouble() == -.5 &&
            single.GetProperty("words")[0].GetProperty("duration").GetDouble() == 0);
        var plain = MusicLyricTiming.Parse("[00:03]整句")[0];
        Check(plain.GetProperty("end").GetDouble() == 3 && plain.GetProperty("words").GetArrayLength() == 0);
        return Task.CompletedTask;
    }
    private static Task LyricsXml()
    {
        string xml = "<QrcInfos><Lyric_1 LyricContent=\"[1000,1000]&lt;literal&gt;(1000,500)&amp;lt; &#x1F3B5;(1500,500)&#10;[2000,1000]Next(2000,1000)\"/></QrcInfos>";
        var lines = MusicLyricTiming.Parse(xml); Check(lines.GetArrayLength() == 2 && lines[0].GetProperty("text").GetString() == "<literal>&lt; 🎵");
        Check(MusicLyricTiming.Parse("<X LyricContent='[00:01]One' LyricContent='[00:02]Two'/>").GetArrayLength() == 0);
        Check(MusicLyricTiming.Parse("<!DOCTYPE x SYSTEM 'file:///no-read'><X/>").GetArrayLength() == 0);
        return Task.CompletedTask;
    }
    private static Task LyricsTranslation()
    {
        var lines = MusicLyricTiming.Parse("[00:01]Original\n[00:02]Next", "[00:01]One\n[00:01]One\n[00:01]Original\n[00:01]Two\n[00:02.001]Wrong");
        Check(lines[0].GetProperty("translation").GetString() == "One\nTwo" && !lines[1].TryGetProperty("translation", out _));
        return Task.CompletedTask;
    }
    private static async Task LyricsAdapter()
    {
        await using var f = new Fixture(); var o = f.Owner();
        var lines = await o.ExecuteAsync("music.lyrics.parse", new { text = "[00:01]Synthetic", translation = "", duration = 4 });
        Check(lines.ValueKind == JsonValueKind.Array && lines[0].GetProperty("words").GetArrayLength() == 0 &&
            !lines[0].TryGetProperty("timing", out _) && f.Store.Writes == 0);
        string text = new('x', 65537);
        var large = await o.ExecuteAsync("music.lyrics.parse", new { text = "[00:01]" + text, translation = "", duration = 4 });
        Check(large.ValueKind == JsonValueKind.Array && large.GetArrayLength() == 1 &&
            large[0].GetProperty("text").GetString() == text && f.Store.Writes == 0);
        string manyLines = string.Join('\n', Enumerable.Range(0, 10001).Select(i => $"[{i * 10},10]line-{i}"));
        var parsed = await o.ExecuteAsync("music.lyrics.parse", new { text = manyLines, translation = "", duration = 101 });
        Check(parsed.ValueKind == JsonValueKind.Array && parsed.GetArrayLength() == 10001 &&
            parsed[10000].GetProperty("text").GetString() == "line-10000" && f.Store.Writes == 0);
        await Error(o, "music.lyrics.parse", new { text = "[00:01]line", translation = "", duration = -1 }, "INVALID_LYRIC_RESPONSE");
    }
}

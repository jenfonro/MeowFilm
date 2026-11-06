using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeLibraryTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in RecordingConsumerCases()) yield return test;
        foreach (var test in OnlineSettingsCases()) yield return test;
        yield return new("Native music library contract version and storage identity", Contract);
        yield return new("Native music library initial reads never manufacture a document", Initial);
        yield return new("Native music collection member edits exact ordering revisions and tombstones", Collections);
        yield return new("Native music favorites protected and local intents persist", Favorites);
        yield return new("Native music collection paging search and invalid paging preserve state", Paging);
        yield return new("Native music song identity matches existing JS stable IDs", Identity);
        yield return new("Native music Unicode identity matches emoji ASCII boundaries and default casing", UnicodeIdentity);
        yield return new("Native music same native identity enrichment and recording conflicts", Recording);
        yield return new("Native music current song contract rejects unowned fields credentials and resource URLs", RejectTransientSong);
        yield return new("Native music catalog full collection handoff uses expected queue version", FullCollection);
        yield return new("Native music duplicate queue occurrences and next priority", Occurrences);
        yield return new("Native music queue sequential mode boundaries", () => Modes(0));
        yield return new("Native music queue loop mode boundaries", () => Modes(1));
        yield return new("Native music queue single repeats ended only", () => Modes(3));
        yield return new("Native music queue shuffle previous uses runtime history", Shuffle);
        yield return new("Native music queue remove reorder clear and archive semantics", QueueEdits);
        yield return new("Native music replace archives five queues and restores occurrences", Archives);
        yield return new("Native music actual-play history deduplicates and retains seven local days", Played);
        yield return new("Native music queue and history restart preserve selection not session clocks", Restart);
        yield return new("Native music plain files reopen without Node", PlainFiles);
        yield return new("Native music before commit failure reads the actual durable outcome without replay", () => UnknownCommit(false));
        yield return new("Native music after commit failure reads the actual durable outcome without replay", () => UnknownCommit(true));
        yield return new("Native music third-party CAS conflict stays fenced without a discard fallback", Conflict);
        yield return new("Native music corrupt envelope and domain records fail closed", Corrupt);
        yield return new("Native music explicit null owned areas never become empty state", NullAreas);
        yield return new("Native music read failure does not become empty library", ReadFailure);
        yield return new("Native music cancellation before admission cannot mutate state", CancelAdmission);
        yield return new("Native music cancellation after CAS admission observes real result", CancelCommit);
        yield return new("Native music shutdown pending failure and reversible preparation", Shutdown);
        yield return new("Native music online settings revision and exact fields", Settings);
        yield return new("Native music retired script areas stay unread, unchanged and never block other owners", RetiredAreas);
        yield return new("Native music non-current envelopes fail every owner entry without writes", NonCurrentDocuments);
        yield return new("Native music LRC graphemes weighting and duplicate timestamps", LyricsLrc);
        yield return new("Native music YRC QRC exact intervals and negative offset", LyricsWords);
        yield return new("Native music word LRC absolute relative tags and trailing markers", LyricsMarkers);
        yield return new("Native music QRC XML entity decode never interprets markup", LyricsXml);
        yield return new("Native music translation exact timestamps only no nearest matching", LyricsTranslation);
        yield return new("Native music lyrics local JSON adapter and bounded bad inputs", LyricsAdapter);
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
    private sealed class Store : IDocumentStore
    {
        private readonly object sync = new();
        private readonly Dictionary<string, byte[]> documents = new(StringComparer.Ordinal);
        internal byte[]? Bytes
        {
            get => documents.TryGetValue(MusicLibraryOwner.DocumentNamespace, out var value) ? value : null;
            set
            {
                if (value is null) documents.Remove(MusicLibraryOwner.DocumentNamespace);
                else documents[MusicLibraryOwner.DocumentNamespace] = value;
            }
        }
        internal int Writes, WriteCalls, Failure;
        internal int FailAtWrite;
        internal bool FailRead, Disposed;
        internal TaskCompletionSource? Entered, Release;
        private static void Namespace(string value) =>
            Check(value == MusicLibraryOwner.DocumentNamespace, "Unexpected document namespace");
        internal void Seed(string documentNamespace, byte[] bytes)
        {
            Namespace(documentNamespace);
            lock (sync) documents[documentNamespace] = bytes.ToArray();
        }
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            Namespace(documentNamespace);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (FailRead) throw new DocumentStoreException(DocumentStoreError.Storage);
                byte[]? bytes = documents.TryGetValue(documentNamespace, out var value) ? value.ToArray() : null;
                var fingerprint = bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes);
                return Task.FromResult(new DocumentSnapshot(bytes, fingerprint));
            }
        }
        public async Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            Namespace(documentNamespace);
            Interlocked.Increment(ref WriteCalls);
            if (Release is { } release) { Entered!.TrySetResult(); await release.Task; }
            lock (sync)
            {
                byte[]? prior = documents.TryGetValue(documentNamespace, out var value) ? value : null;
                var current = prior is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(prior);
                if (current != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
                int attempt = ++Writes;
                int failure = Failure;
                if (FailAtWrite == attempt) { failure = 1; FailAtWrite = 0; }
                Failure = 0;
                if (failure == 1) throw new DocumentStoreException(DocumentStoreError.Storage);
                documents[documentNamespace] = bytes.ToArray();
                if (failure == 2) throw new DocumentStoreException(DocumentStoreError.Storage);
                return DocumentFingerprint.FromBytes(documents[documentNamespace]);
            }
        }
        internal JsonObject Root => JsonNode.Parse(Bytes!)!.AsObject();
        internal JsonObject State(string area, string scope = "local")
        {
            string key = JsonSerializer.Serialize(new object[] { "meowfilm", "music", 1, scope, area });
            return Root["entries"]!.AsArray().First(e => e!["key"]!.GetValue<string>() == key)!["value"]!.AsObject();
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store = new();
        private readonly List<MusicLibraryOwner> owners = [];
        internal MusicLibraryOwner Owner(IDocumentStore? store = null, string scope = "local")
        { var owner = new MusicLibraryOwner(store ?? Store, scope); owners.Add(owner); return owner; }
        public async ValueTask DisposeAsync() { foreach (var o in owners) await o.AbortAsync(); await Store.DisposeAsync(); }
    }
    private static Task Contract()
    {
        Check(MusicLibraryOwner.ContractVersion == "0.6.0" && MusicLibraryOwner.DocumentNamespace == "music.library.v1");
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
        await Ok(o, "music.library.reorderSong", new
        {
            id,
            ifRevision = 2,
            songId = "library:local:c",
            targetId = "library:local:b",
            after = false
        });
        var detail = await Ok(o, "music.library.detail", new { id }); Check(detail.GetProperty("songs")[1].GetProperty("songId").GetString() == "library:local:c");
        await Ok(o, "music.library.removeSong", new { id, ifRevision = 3, songId = "library:local:b" });
        await Ok(o, "music.library.edit", new { id, ifRevision = 4, name = "Renamed", order = new[] { "library:local:c", "library:local:a" } });
        var library = f.Store.State("library")["library"]!;
        Check(library["collections"]![1]!["intents"]!["library:local:b"]!["present"]!.GetValue<bool>() == false);
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
        await Ok(o, "music.library.removeSong", new { id = "favorites", ifRevision = 1, songId = "library:local:a" });
        Check(f.Store.State("library")["library"]!["collections"]![0]!["intents"]!["library:local:a"]!["pending"]!.GetValue<bool>());
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
            (MusicSongFixture.Online("wy", "42", "Test (Live)", "B; A", duration: 180,
                native: O(new { duration = 180 })), "song:18c66609b3825777"),
            (MusicSongFixture.Online("kg", "42", "Test", "Tester", duration: 180,
                native: O(new { hash = "ABC" })), "song:566fe39035a3d57a"),
            (MusicSongFixture.Online("tx", "9223372036854775806", "Unknown", "unknown"),
                "song:d0a0d45c6ae7a9fa")
        };
        foreach (var (song, expected) in cases)
        {
            await Ok(o, "music.queue.replace", new { songs = new[] { song }, index = 0 });
            var current = (await Ok(o, "music.queue.get")).GetProperty("current").GetProperty("song");
            Check(current.GetProperty("songId").GetString() == expected, "JS identity mismatch: " + expected);
        }
    }
    private static async Task UnicodeIdentity()
    {
        // Exact golden outputs.
        var cases = new[]
        {
            ("星😀 (Live)", "Ｂ; A", 185, "42", "song:738530dd4024f5df"),
            ("歌曲live", "艺人", 185, "42", "song:b1d5fc2b8f357441"),
            ("A\u2028B\u2029C", "A\u0085B", 185, "42", "song:aab6a0c14f208ccb"),
            ("Any", "Artist", 0, "9007199254740993123", "song:ee628a1de1e9aa73"),
            ("İSTANBUL", "ΟΣ", 180, "case", "song:b50a579dd05f16a3"),
            ("ΟΣ́", "İ", 180, "case2", "song:d8a203994447b8df")
        };
        await using var f = new Fixture(); var o = f.Owner();
        foreach (var (title, artist, duration, id, expected) in cases)
        {
            var song = MusicSongFixture.Online("wy", id, title, artist, duration: duration);
            await Ok(o, "music.queue.replace", new { songs = new[] { song }, index = 0 });
            Check((await Ok(o, "music.queue.get")).GetProperty("current").GetProperty("song").GetProperty("songId").GetString() == expected, expected);
        }
    }
    private static async Task Recording()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.library.create", new { name = "First", songs = new[] { Online("a") } });
        string id = (await Ok(o, "music.library.detail", new { id = "playlist:1" })).GetProperty("songs")[0].GetProperty("songId").GetString()!;
        await Ok(o, "music.library.addSongs", new { id = "playlist:1", ifRevision = 1, songs = new[] { Online("a", "Corrected") } });
        Check((await Ok(o, "music.library.detail", new { id = "playlist:1" })).GetProperty("songs")[0].GetProperty("songId").GetString() == id);
        await Error(o, "music.library.create", new { name = "Wrong", songs = new[] { Online("a", "Other recording", "Other singer") } }, "NATIVE_ID_CONFLICT");
        await Ok(o, "music.library.create", new { name = "Different take", songs = new[] { Online("b", "Corrected", duration: 250) } });
        string other = (await Ok(o, "music.library.detail", new { id = "playlist:2" })).GetProperty("songs")[0].GetProperty("songId").GetString()!;
        Check(id != other);
    }
    private static async Task RejectTransientSong()
    {
        await using var f = new Fixture(); var o = f.Owner(); var song = Online("a");
        song["url"] = "https://media.invalid/temporary"; song["cover_url"] = "https://image.invalid/a?signature=synthetic-private";
        song["cookie"] = "synthetic-private"; MusicSongFixture.Info(song)["headers"] = O(new { Authorization = "synthetic-private" });
        await Error(o, "music.queue.replace", new { songs = new[] { song }, index = 0 }, "INVALID_REQUEST");
        var fabricated = Online("b"); fabricated["favoriteCount"] = 250_000;
        await Error(o, "music.queue.replace", new { songs = new[] { fabricated }, index = 0 }, "INVALID_REQUEST");
        Check(f.Store.Bytes is null && f.Store.Writes == 0);
    }
    private static async Task FullCollection()
    {
        await using var f = new Fixture(); var o = f.Owner(); var songs = Enumerable.Range(0, 1001).Select(i => E(Song(i.ToString()))).ToArray();
        await Error(o, "music.library.create", new { name = "Too many RPC-shaped items", songs }, "LIMIT");
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
    private static async Task Modes(int mode)
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.queue.replace", new { songs = new[] { Song("a"), Song("b") }, index = 1 });
        var q = await Ok(o, "music.queue.get"); string a = q.GetProperty("entries")[0].GetProperty("id").GetString()!, b = q.GetProperty("currentId").GetString()!;
        await Ok(o, "music.queue.mode", new { mode });
        var result = await Ok(o, "music.queue.next", new { reason = "ended" });
        Check(result.GetProperty("targetId").GetString() == (mode == 0 ? null : mode == 1 ? a : b));
        if (mode == 3) Check((await Ok(o, "music.queue.next")).GetProperty("targetId").GetString() == a);
        await Ok(o, "music.queue.select", new { id = a });
        Check((await Ok(o, "music.queue.previous")).GetProperty("targetId").GetString() == (mode == 0 ? null : b));
    }
    private static async Task Shuffle()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.queue.replace", new { songs = new[] { Song("a"), Song("b"), Song("c") }, index = 0 }); await Ok(o, "music.queue.mode", new { mode = 2 });
        string before = (await Ok(o, "music.queue.get")).GetProperty("currentId").GetString()!;
        var next = await Ok(o, "music.queue.next"); Check(next.GetProperty("currentId").GetString() != before);
        Check((await Ok(o, "music.queue.previous")).GetProperty("targetId").GetString() == before);
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
        var h = await Ok(o, "music.history.list", new { kind = "playlists" }); Check(h.GetProperty("total").GetInt32() == 5);
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
        var days = await Ok(o, "music.history.get"); Check(days.GetProperty("total").GetInt32() == 7 && days.GetProperty("items")[0].GetProperty("count").GetInt32() == 1);
    }
    private static async Task Restart()
    {
        await using var f = new Fixture(); var o = f.Owner();
        await Ok(o, "music.queue.replace", new { songs = new[] { Song("a"), Song("b") }, index = 1 }); await Ok(o, "music.queue.mode", new { mode = 0 });
        string id = (await Ok(o, "music.queue.get")).GetProperty("currentId").GetString()!;
        await Ok(o, "music.history.recordPlayed", new { entryId = id, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        await o.DisposeAsync(); Check(!f.Store.Disposed); var restored = f.Owner();
        var q = await Ok(restored, "music.queue.get"); Check(q.GetProperty("mode").GetInt32() == 0 && q.GetProperty("currentId").GetString() == id);
        Check(!f.Store.State("queue").ContainsKey("history") && !Encoding.UTF8.GetString(f.Store.Bytes!).Contains("currentTime"));
    }
    private static async Task PlainFiles()
    {
        string root = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Test data root required");
        Check(Path.IsPathFullyQualified(root) && Directory.Exists(root)); string path = Path.Combine(root, "native-music-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        await using (var store = new LocalDocumentStore(path, [MusicLibraryOwner.DocumentNamespace], []))
        await using (var first = new MusicLibraryOwner(store))
            await Ok(first, "music.library.create", new { name = "Plain files", songs = new[] { Song() } });
        await using var reopened = new LocalDocumentStore(path, [MusicLibraryOwner.DocumentNamespace], []);
        await using var second = new MusicLibraryOwner(reopened);
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
    private static async Task Corrupt()
    {
        foreach (string raw in new[] { "null", "{}", "{\"version\":2,\"entries\":[]}", "{\"version\":1,\"version\":1,\"entries\":[]}",
            "{\"version\":1,\"entries\":[{\"key\":\"x\",\"value\":{}},{\"key\":\"x\",\"value\":{}}]}",
            "{\"version\":1,\"entries\":[{\"key\":\"[\\\"meowfilm\\\",\\\"music\\\",1,\\\"local\\\",\\\"queue\\\"]\",\"value\":{\"schema\":1,\"entries\":[{\"id\":\"not-an-entry\",\"song\":{}}]}}]}" })
        {
            await using var f = new Fixture(); f.Store.Bytes = Encoding.UTF8.GetBytes(raw); var o = f.Owner();
            var reply = await o.ExecuteAsync("music.library.list"); Check(!reply.GetProperty("ok").GetBoolean() && f.Store.Writes == 0);
        }
    }
    private static string AreaKey(string area) => JsonSerializer.Serialize(new object[] { "meowfilm", "music", 1, "local", area },
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static byte[] Document(params (string Area, JsonNode Value)[] values)
    {
        var entries = new JsonArray();
        foreach (var (area, value) in values)
            entries.Add(new JsonObject { ["key"] = AreaKey(area), ["value"] = value.DeepClone() });
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["version"] = 1, ["entries"] = entries }, PersistedJson);
    }
    private static JsonObject CurrentQueue()
    {
        var song = Song();
        var entry = new JsonObject
        {
            ["id"] = "entry-1",
            ["key"] = "library:local:a",
            ["song"] = song.DeepClone(),
            ["context"] = new JsonObject()
        };
        return new JsonObject
        {
            ["schema"] = 1,
            ["entries"] = new JsonArray(entry),
            ["currentId"] = "entry-1",
            ["priority"] = new JsonArray(),
            ["source"] = null
        };
    }
    private static JsonObject CurrentHistory()
    {
        var played = new JsonObject
        {
            ["song"] = Song(),
            ["context"] = new JsonObject(),
            ["at"] = 1
        };
        var day = new JsonObject
        {
            ["date"] = "2026-10-27",
            ["songs"] = new JsonArray(played)
        };
        return new JsonObject
        {
            ["schema"] = 1,
            ["playlists"] = new JsonArray(),
            ["days"] = new JsonArray(day)
        };
    }
    private static JsonObject CurrentLibraryState()
    {
        const string songId = "library:local:a";
        var favorite = new JsonObject
        {
            ["id"] = "favorites",
            ["name"] = "我的收藏",
            ["kind"] = "favorites",
            ["order"] = new JsonArray(JsonValue.Create(songId)),
            ["revision"] = 0,
            ["manualOrder"] = false,
            ["bindings"] = new JsonArray(),
            ["intents"] = new JsonObject()
        };
        var songs = new JsonObject { [songId] = Song() };
        var library = new JsonObject
        {
            ["version"] = 1,
            ["ownerId"] = "local",
            ["revision"] = 0,
            ["nextId"] = 1,
            ["collections"] = new JsonArray(favorite),
            ["songs"] = songs,
            ["accounts"] = new JsonObject()
        };
        return new JsonObject
        {
            ["schema"] = 1,
            ["scope"] = "local",
            ["revision"] = 0,
            ["library"] = library
        };
    }
    private static async Task NullAreas()
    {
        foreach (string area in new[] { "library", "queue", "history", "playback-settings", "online-settings", "native-source-exclusions" })
        {
            await using var f = new Fixture();
            f.Store.Bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, entries = new[] { new { key = AreaKey(area), value = (object?)null } } });
            byte[] original = f.Store.Bytes.ToArray(); var o = f.Owner();
            for (int i = 0; i < 2; i++)
                Check(!(await o.ExecuteAsync("music.library.create", new { name = "Never overwrite" })).GetProperty("ok").GetBoolean(), area);
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
    /// <summary>Script installations moved to the shared runtime module. Earlier entries are not read,
    /// migrated or deleted: they ride along unchanged, and even malformed ones never fail other owners.</summary>
    private static async Task RetiredAreas()
    {
        await using var f = new Fixture();
        var retired = new (string Area, JsonNode Value)[]
        {
            ("lx-installations", O(new { schema = 4, revision = 7, installations = new[] { new { id = "old-install" } } })),
            ("[\"lx-data\",\"old-install\",\"wy\"]", JsonValue.Create("not a current record")!),
            ("[\"lx-data\",\"missing-installation\",\"tx\"]", O(new { schema = 1, values = Array.Empty<object>() }))
        };
        f.Store.Bytes = Document(retired);
        var o = f.Owner();
        Check((await Ok(o, "music.library.list")).GetProperty("total").GetInt32() == 1);
        await Ok(o, "music.library.create", new { name = "Kept" });
        var entries = JsonNode.Parse(f.Store.Bytes!)!["entries"]!.AsArray();
        foreach (var (area, value) in retired)
            Check(entries.Any(entry => entry!["key"]!.GetValue<string>() == AreaKey(area) &&
                JsonNode.DeepEquals(entry["value"], value)), "retired entry must be carried unchanged: " + area);
        await o.DisposeAsync();
        Check((await Ok(f.Owner(), "music.library.list")).GetProperty("total").GetInt32() == 2);
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
        await Error(o, "music.settings.save", new { ifRevision = 0, quality = "flac", lyrics = true }, "INVALID_REQUEST");
        await Error(o, "music.settings.save", new { ifRevision = 0 }, "INVALID_REQUEST");
        await Error(o, "music.settings.save", new { lyrics = true }, "INVALID_REQUEST");
        await Error(o, "music.settings.save", new { ifRevision = 0, lyrics = true, target = (object?)null }, "INVALID_REQUEST");
        await Error(o, "music.settings.save", new { ifRevision = 0, lyrics = true, target = new { installationId = "missing", platform = "wy" } }, "INVALID_REQUEST");
        Check(f.Store.Writes == writes);
        var saved = (await Ok(o, "music.settings.save", new { ifRevision = 0, lyrics = false })).GetProperty("settings");
        Check(saved.GetProperty("schema").GetInt32() == 3 && saved.GetProperty("revision").GetInt64() == 1 &&
            !saved.GetProperty("lyrics").GetBoolean() && saved.EnumerateObject().Count() == 3);
        await Error(o, "music.settings.save", new { ifRevision = 0, lyrics = true }, "REVISION_CONFLICT");
        await o.DisposeAsync();
        Check((await Ok(f.Owner(), "music.settings.get")).GetProperty("settings").GetRawText() == saved.GetRawText());
    }
    private static async Task RejectDocument(byte[] bytes, string expected = "CORRUPT_STORAGE")
    {
        await using var f = new Fixture();
        f.Store.Bytes = bytes.ToArray();
        byte[] original = f.Store.Bytes.ToArray();
        var owner = f.Owner();
        await Error(owner, "music.library.list", null, expected);
        await Error(owner, "music.library.create", new { name = "Must not write" }, expected);
        await Throws(expected, async () => await owner.ReadLibrarySyncAsync());
        Check(f.Store.Writes == 0 && original.SequenceEqual(f.Store.Bytes!), "Non-current data changed");
    }
    private static async Task NonCurrentDocuments()
    {
        string[] storage =
        [
            """{"version":1,"entries":[{"key":"[\"meowfilm\",\"music\",1,\"local\",\"future\"]","value":{}}]}""",
            """{"version":1,"entries":[{"key":"[\"meowfilm\",\"music\",1,\"other\",\"queue\"]","value":{"schema":1,"entries":[],"currentId":null,"priority":[],"source":null}}]}""",
            """{"version":1,"entries":[{"key":"[\"meowfilm\",\"music\",1.0,\"local\",\"queue\"]","value":{"schema":1,"entries":[],"currentId":null,"priority":[],"source":null}}]}""",
            """{"version":1.0,"entries":[]}""",
            """{"version":1e0,"entries":[]}"""
        ];
        foreach (string value in storage)
            await RejectDocument(Encoding.UTF8.GetBytes(value));

        static JsonObject Changed(JsonObject value, Action<JsonObject> change)
        {
            var copy = (JsonObject)value.DeepClone();
            change(copy);
            return copy;
        }

        var queue = CurrentQueue();
        foreach (var invalid in new[]
        {
            Changed(queue, q => q["similar"] = new JsonArray()),
            Changed(queue, q => q.Remove("source")),
            Changed(queue, q => q["currentId"] = "entry-2"),
            Changed(queue, q => q["priority"] = new JsonArray(JsonValue.Create("entry-1"))),
            Changed(queue, q => q["extra"] = true)
        })
            await RejectDocument(Document(("queue", invalid)), "CORRUPT_QUEUE");

        var history = CurrentHistory();
        foreach (var invalid in new[]
        {
            Changed(history, h => h.Remove("days")),
            Changed(history, h => h["extra"] = true),
            Changed(history, h => h["days"]![0]!["songs"]![0]!["extra"] = true)
        })
            await RejectDocument(Document(("history", invalid)), "CORRUPT_HISTORY");

        var library = CurrentLibraryState();
        foreach (var invalid in new[]
        {
            Changed(library, state => state["library"]!["extra"] = true),
            Changed(library, state => state["library"]!.AsObject().Remove("accounts")),
            Changed(library, state => state["library"]!["songs"]!["library:local:a"]!["extra"] = true),
            Changed(library, state => state["library"]!["songs"]!["library:local:a"]!.AsObject().Remove("sources")),
            Changed(library, state => state["revision"] = 1),
            Changed(library, state => state["library"]!["accounts"]!["kw"] = "account-a"),
            Changed(library, state => state["library"]!["accounts"]!["wy"] = new string('a', 65)),
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
            await RejectDocument(Document(("library", invalid)), "CORRUPT_LIBRARY");


        await RejectDocument(Document(("online-settings", O(new
        {
            schema = 2,
            revision = 0,
            lyrics = true,
            target = new { installationId = "missing-installation", platform = "wy" }
        }))));
    }
    private static Task LyricsLrc()
    {
        var lines = MusicLyricTiming.Parse("[00:00]你好👨‍👩‍👧‍👦\n[00:06]再见", "", 10);
        Check(lines[0].GetProperty("words").GetArrayLength() == 3 && lines[1].GetProperty("end").GetDouble() == 10);
        var weighted = MusicLyricTiming.Parse("[00:01]A !\n[00:01]B\n[00:08]End");
        Check(weighted[0].GetProperty("words").EnumerateArray().Select(w => w.GetProperty("duration").GetDouble()).SequenceEqual(new[] { 4d, 1, 2 }));
        Check(MusicLyricTiming.ActiveIndex(weighted, 1) == 1 && MusicLyricTiming.ActiveIndex(weighted, .9) == -1);
        return Task.CompletedTask;
    }
    private static Task LyricsWords()
    {
        foreach (string text in new[] { "[offset:-2000]\n[1000,2000](1000,1000,0)A(2000,1000,0)B", "[offset:-2000]\n[1000,2000]A(1000,1000)B(2000,1000)" })
        {
            var line = MusicLyricTiming.Parse(text)[0]; Check(line.GetProperty("time").GetDouble() == 0 && line.GetProperty("end").GetDouble() == 1);
            Check(line.GetProperty("words")[0].GetProperty("time").GetDouble() == -1);
        }
        var shortLines = MusicLyricTiming.Parse("[1000,0]Zero(1000,0)\n[1000,20]Tiny(1000,20)");
        Check(shortLines[0].GetProperty("end").GetDouble() == 1 && shortLines[1].GetProperty("end").GetDouble() == 1.02);
        Check(MusicLyricTiming.Progress(1, 0, 1) == 1); return Task.CompletedTask;
    }
    private static Task LyricsMarkers()
    {
        var lines = MusicLyricTiming.Parse("[00:10][00:20][[00:00.000]]X[[00:01.000]]Y[[00:02.000]]");
        Check(lines[0].GetProperty("end").GetDouble() == 12 && lines[1].GetProperty("end").GetDouble() == 22);
        var absolute = MusicLyricTiming.Parse("[00:10]Lead [[00:10.000]]A[[00:10.000]]B<00:11.000>")[0];
        Check(absolute.GetProperty("text").GetString() == "Lead AB" && absolute.GetProperty("words")[0].GetProperty("duration").GetDouble() == .05);
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
        Check(lines.ValueKind == JsonValueKind.Array && f.Store.Writes == 0);
        await Error(o, "music.lyrics.parse", new { text = new string('x', 65537), translation = "", duration = 0 }, "INVALID_LYRIC_RESPONSE");
    }
}

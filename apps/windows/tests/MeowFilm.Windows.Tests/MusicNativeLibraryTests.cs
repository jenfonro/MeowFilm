using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicNativeLibraryTests
{
    public static IEnumerable<TestCase> Cases()
    {
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
        yield return new("Native music DPAPI real restart native only without Node", Dpapi);
        yield return new("Native music before commit failure reads the actual durable outcome without replay", () => UnknownCommit(false));
        yield return new("Native music after commit failure reads the actual durable outcome without replay", () => UnknownCommit(true));
        yield return new("Native music third-party CAS conflict stays fenced without a discard fallback", Conflict);
        yield return new("Native music corrupt envelope and domain records fail closed", Corrupt);
        yield return new("Native music explicit null owned areas never become empty state", NullAreas);
        yield return new("Native music malformed script values never overwrite damaged storage", CorruptScriptStorage);
        yield return new("Native music read failure does not become empty library", ReadFailure);
        yield return new("Native music cancellation before admission cannot mutate state", CancelAdmission);
        yield return new("Native music cancellation after CAS admission observes real result", CancelCommit);
        yield return new("Native music shutdown pending failure and reversible preparation", Shutdown);
        yield return new("Native music source metadata single owner CAS alongside library", SourceMetadata);
        yield return new("Native music source snapshots preserve both versions and never write on reads or restart", SourceSnapshots);
        yield return new("Native music source metadata unknown commit and atomic private storage removal", SourceFailure);
        yield return new("Native music online settings revision and target validation", Settings);
        yield return new("Native music script storage permissions revisions scope and quotas", ScriptStorage);
        yield return new("Native music non-current envelopes fail every owner entry without writes", NonCurrentDocuments);
        yield return new("Native music source metadata malformed input never commits", InvalidSource);
        yield return new("Native music arbitrary bounded script sites retain installation isolation and quotas", DeclaredSiteStorage);
        yield return new("Native music complete bounded script-site storage coexists with ordinary owned areas", CompleteSiteStorage);
        yield return new("Native music source supported large script restores exact bytes", SourceBudget);
        yield return new("Native music candidate overlay new disabled installation atomic restart", Overlay);
        yield return new("Native music candidate overlay detects intervening live storage without metadata change", OverlayConflict);
        yield return new("Native music candidate overlay cannot change another installation", OverlayIsolation);
        yield return new("Native music candidate overlay before commit failure preserves both old halves", () => OverlayUnknown(false));
        yield return new("Native music candidate overlay after commit failure observes both new halves", () => OverlayUnknown(true));
        yield return new("Native music queued stale and cancelled script calls cannot enter CAS", QueuedScript);
        yield return new("Native music script values reject duplicate JSON properties and over-budget sites", OverlayInvalid);
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
    private sealed class Store : IProtectedDocumentStore
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
        internal int Writes, Failure;
        internal int FailAtWrite;
        internal bool FailRead, Disposed;
        internal DocumentFingerprint? ReadFingerprint;
        internal TaskCompletionSource? Entered, Release;
        private static bool ContentNamespace(string value)
        {
            string prefix = MusicLibraryOwner.ScriptContentCollection + ".";
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string[] parts = value[prefix.Length..].Split('.');
            return parts.Length == 2 && parts[0].Length == 64 &&
                parts[0].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                int.TryParse(parts[1], out int index) && index >= 0 &&
                parts[1] == index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        private static void Namespace(string value) =>
            Check(value == MusicLibraryOwner.DocumentNamespace || ContentNamespace(value), "Unexpected document namespace");
        internal void Seed(string documentNamespace, byte[] bytes)
        {
            Namespace(documentNamespace);
            lock (sync) documents[documentNamespace] = bytes.ToArray();
        }
        internal byte[] Document(string documentNamespace)
        {
            Namespace(documentNamespace);
            lock (sync) return documents[documentNamespace].ToArray();
        }
        public Task<ProtectedDocument> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            Namespace(documentNamespace);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (FailRead) throw new ProtectedDocumentException(DocumentStoreError.Storage);
                byte[]? bytes = documents.TryGetValue(documentNamespace, out var value) ? value.ToArray() : null;
                var fingerprint = documentNamespace == MusicLibraryOwner.DocumentNamespace && ReadFingerprint is not null
                    ? ReadFingerprint : bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes);
                return Task.FromResult(new ProtectedDocument(bytes, fingerprint));
            }
        }
        public async Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            Namespace(documentNamespace);
            if (Release is { } release) { Entered!.TrySetResult(); await release.Task; }
            lock (sync)
            {
                byte[]? prior = documents.TryGetValue(documentNamespace, out var value) ? value : null;
                var current = prior is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(prior);
                if (current != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
                int attempt = ++Writes;
                int failure = Failure;
                if (FailAtWrite == attempt) { failure = 1; FailAtWrite = 0; }
                Failure = 0;
                if (failure == 1) throw new ProtectedDocumentException(DocumentStoreError.Storage);
                documents[documentNamespace] = bytes.ToArray();
                if (failure == 2) throw new ProtectedDocumentException(DocumentStoreError.Storage);
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
        internal MusicLibraryOwner Owner(IProtectedDocumentStore? store = null, string scope = "local")
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
        var sources = await owner.ReadSourceMetadataAsync();
        Check(sources.State.GetProperty("schema").GetInt32() == 4 && sources.State.GetProperty("revision").GetInt64() == 0 &&
            sources.State.GetProperty("installations").GetArrayLength() == 0);
        Check(!(await owner.ReadSourceScriptDataAsync("test-install-a", 0)).Sites.EnumerateObject().Any());
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
        song["cookie"] = "synthetic-private"; song["source_data"]!["headers"] = O(new { Authorization = "synthetic-private" });
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
    private static async Task Dpapi()
    {
        string root = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Test data root required");
        Check(Path.IsPathFullyQualified(root) && Directory.Exists(root)); string path = Path.Combine(root, "native-music-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        byte[] source = Encoding.UTF8.GetBytes("// DPAPI source\n" + new string('猫', 600_000));
        MusicScriptContent content;
        await using (var store = new DpapiDocumentStore(path, [MusicLibraryOwner.DocumentNamespace],
            [MusicLibraryOwner.ScriptContentCollection]))
        await using (var first = new MusicLibraryOwner(store))
        {
            await Ok(first, "music.library.create", new { name = "DPAPI", songs = new[] { Song() } });
            content = await first.StoreScriptContentAsync(source);
        }
        await using var reopened = new DpapiDocumentStore(path, [MusicLibraryOwner.DocumentNamespace],
            [MusicLibraryOwner.ScriptContentCollection]);
        await using var second = new MusicLibraryOwner(reopened);
        Check((await Ok(second, "music.library.list")).GetProperty("total").GetInt32() == 2);
        byte[] restored = await second.ReadScriptContentAsync(content.Sha256, content.Length);
        try { Check(source.SequenceEqual(restored)); }
        finally { CryptographicOperations.ZeroMemory(restored); CryptographicOperations.ZeroMemory(source); }
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
    private static JsonObject CurrentSources() => new()
    {
        ["schema"] = 4,
        ["revision"] = 1,
        ["installations"] = new JsonArray(Installation())
    };
    private static async Task SourceSnapshots()
    {
        var sources = CurrentSources(); sources["revision"] = 7;
        byte[] currentBytes = Encoding.UTF8.GetBytes("// current source\n// 中文 header and stable bytes");
        byte[] previousBytes = Encoding.UTF8.GetBytes("// previous source");
        var currentContent = new MusicScriptContent(Convert.ToHexStringLower(SHA256.HashData(currentBytes)), currentBytes.Length);
        var previousContent = new MusicScriptContent(Convert.ToHexStringLower(SHA256.HashData(previousBytes)), previousBytes.Length);
        var item = sources["installations"]![0]!.AsObject();
        item["current"]!["sourceHash"] = currentContent.Sha256;
        item["current"]!["sourceBytes"] = currentContent.Length;
        item["current"]!["version"] = "2.3";
        item["previous"] = item["current"]!.DeepClone();
        item["previous"]!["sourceHash"] = previousContent.Sha256;
        item["previous"]!["sourceBytes"] = previousContent.Length;
        item["previous"]!["version"] = "1.8";
        item["previous"]!["permissions"]!["storage"] = new JsonArray("read");
        var disabled = Installation("test-install-b", false);
        disabled["current"]!["permissions"]!["storage"] = new JsonArray();
        sources["installations"]!.AsArray().Add(disabled);
        await using var f = new Fixture();
        f.Store.Seed(ContentKey(currentContent.Sha256, 0), currentBytes);
        f.Store.Seed(ContentKey(previousContent.Sha256, 0), previousBytes);
        f.Store.Bytes = Document(
            ("lx-installations", sources),
            ("library", CurrentLibraryState()), ("queue", CurrentQueue()), ("history", CurrentHistory()),
            ("online-settings", O(new { schema = 2, revision = 3, lyrics = false, target = new { installationId = "test-install-a", platform = "wy" } })),
            ("[\"lx-data\",\"test-install-a\",\"wy\"]", O(new { schema = 1, values = new[] { new { key = "keep", value = "source-a" } } })),
            ("[\"lx-data\",\"test-install-a\",\"tx\"]", O(new { schema = 1, values = new[] { new { key = "keep", value = "source-a-tx" } } })),
            ("[\"lx-data\",\"test-install-b\",\"wy\"]", O(new { schema = 1, values = new[] { new { key = "keep", value = "source-b" } } })));
        byte[] original = f.Store.Bytes.ToArray(); var o = f.Owner();
        await Ok(o, "music.library.list");
        var before = await o.ReadSourceScriptDataAsync("test-install-a", 7);
        var metadata = await o.ReadSourceMetadataAsync();
        Check(JsonNode.DeepEquals(sources, Parsed(metadata.State)));
        await Ok(o, "music.queue.get"); await Ok(o, "music.history.get"); await Ok(o, "music.settings.get");
        Check((await o.ReadSourceScriptDataAsync("test-install-a", 7)).Sha256 == before.Sha256);
        Check((await o.ReadSourceMetadataAsync()).Sha256 == metadata.Sha256);
        Check(f.Store.Writes == 0 && original.SequenceEqual(f.Store.Bytes!));
        await o.DisposeAsync();
        var restored = f.Owner();
        Check((await restored.ReadSourceMetadataAsync()).Sha256 == metadata.Sha256);
        Check((await restored.ReadScriptValueAsync("test-install-a", "wy", "keep", 7)).GetString() == "source-a");
        Check((await restored.ReadSourceScriptDataAsync("test-install-a", 7)).Sha256 == before.Sha256);
        Check((await restored.ReadSourceScriptDataAsync("test-install-b", 7)).Sites.GetProperty("wy")
            .GetProperty("values")[0].GetProperty("value").GetString() == "source-b");
        byte[] currentSource = await restored.ReadScriptContentAsync(currentContent.Sha256, currentContent.Length);
        byte[] previousSource = await restored.ReadScriptContentAsync(previousContent.Sha256, previousContent.Length);
        try
        {
            Check(currentSource.SequenceEqual(currentBytes));
            Check(previousSource.SequenceEqual(previousBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentSource);
            CryptographicOperations.ZeroMemory(previousSource);
        }
        Check(f.Store.Writes == 0 && original.SequenceEqual(f.Store.Bytes!));
    }
    private static async Task DeclaredSiteStorage()
    {
        await using var f = new Fixture(); var o = f.Owner(); await Install(o, Installation(), Installation("test-install-b"));
        for (int index = 0; index < 32; index++)
            await o.CommitScriptValueAsync("test-install-a", "site-" + index, "same", 1, E(index), false);
        await o.CommitScriptValueAsync("test-install-b", "site-0", "same", 1, E("isolated"), false);
        Check((await o.ReadScriptValueAsync("test-install-a", "site-0", "same", 1)).GetInt32() == 0);
        Check((await o.ReadScriptValueAsync("test-install-b", "site-0", "same", 1)).GetString() == "isolated");
        int writes = f.Store.Writes; byte[] original = f.Store.Bytes!.ToArray();
        await Throws("LIMIT", async () => await o.CommitScriptValueAsync("test-install-a", "site-overflow", "same", 1, E("overflow"), false));
        foreach (string site in new[] { "", " spaced", "control\nsite", "site\n", new string('s', 129) })
            await Throws("INVALID_REQUEST", async () => await o.CommitScriptValueAsync("test-install-b", site, "same", 1, E("invalid"), false));
        Check(f.Store.Writes == writes && original.SequenceEqual(f.Store.Bytes!));
        await o.DisposeAsync();
        Check((await f.Owner().ReadSourceScriptDataAsync("test-install-a", 1)).Sites.EnumerateObject().Count() == 32);
    }
    private static async Task CompleteSiteStorage()
    {
        var sources = CurrentSources();
        sources["installations"] = new JsonArray(Enumerable.Range(0, 32)
            .Select(index => (JsonNode)Installation("test-install-" + index)).ToArray());
        var entries = new List<(string Area, JsonNode Value)>
        {
            ("lx-installations", sources), ("library", CurrentLibraryState()),
            ("queue", CurrentQueue()), ("history", CurrentHistory())
        };
        foreach (var item in sources["installations"]!.AsArray())
            for (int site = 0; site < 32; site++)
            {
                string area = new JsonArray("lx-data", item!["id"]!.GetValue<string>(), "site-" + site).ToJsonString();
                entries.Add((area, O(new { schema = 1, values = Array.Empty<object>() })));
            }
        await using var f = new Fixture(); f.Store.Bytes = Document(entries.ToArray()); var o = f.Owner();
        await Ok(o, "music.library.list");
        Check((await o.ReadSourceScriptDataAsync("test-install-0", 1)).Sites.EnumerateObject().Count() == 32);
        Check(f.Store.Writes == 0);
    }
    private static async Task NullAreas()
    {
        foreach (string area in new[] { "library", "queue", "history", "playback-settings", "online-settings", "lx-installations", "[\"lx-data\",\"test-install-a\",\"wy\"]" })
        {
            await using var f = new Fixture();
            f.Store.Bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, entries = new[] { new { key = AreaKey(area), value = (object?)null } } });
            byte[] original = f.Store.Bytes.ToArray(); var o = f.Owner();
            for (int i = 0; i < 2; i++)
                Check(!(await o.ExecuteAsync("music.library.create", new { name = "Never overwrite" })).GetProperty("ok").GetBoolean(), area);
            Check(f.Store.Writes == 0 && original.SequenceEqual(f.Store.Bytes));
        }
    }
    private static async Task CorruptScriptStorage()
    {
        string[] states = ["{\"schema\":1,\"values\":null}", "{\"schema\":1,\"values\":[null]}",
            "{\"schema\":1,\"values\":[{\"key\":\"x\"}]}", "{\"schema\":1,\"values\":[{\"key\":\"x\",\"value\":1},{\"key\":\"x\",\"value\":2}]}",
            "{\"schema\":2,\"values\":[]}", "{\"schema\":1,\"values\":[{\"key\":\"\",\"value\":1}]}"];
        foreach (string raw in states)
        {
            await using var f = new Fixture();
            f.Store.Bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1,
                entries = new[] { new { key = AreaKey("[\"lx-data\",\"test-install-a\",\"wy\"]"), value = JsonDocument.Parse(raw).RootElement } } });
            Check(!(await f.Owner().ExecuteAsync("music.library.list")).GetProperty("ok").GetBoolean() && f.Store.Writes == 0);
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
    private const string SyntheticSource = "// synthetic inert fixture; never executed";
    private static MusicScriptContent Content(string source)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        try { return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string ContentKey(string hash, int index) =>
        $"{MusicLibraryOwner.ScriptContentCollection}.{hash}.{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    private static JsonObject Installation(string id = "test-install-a", bool enabled = true,
        MusicScriptContent? content = null)
    {
        content ??= Content(SyntheticSource);
        return O(new
        {
            id, name = "Synthetic source", enabled, previous = (object?)null,
            current = new { sourceHash = content.Sha256, sourceBytes = content.Length, version = "1",
                permissions = new { storage = new[] { "read", "write" } } }
        });
    }
    private static async Task<MusicSourceMetadataSnapshot> Install(MusicLibraryOwner o, params JsonObject[] items)
    {
        var current = await o.ReadSourceMetadataAsync(); var state = Parsed(current.State); state["revision"] = state["revision"]!.GetValue<long>() + 1;
        foreach (var item in items) state["installations"]!.AsArray().Add(item.DeepClone());
        return await o.CommitSourceMetadataAsync(E(state), current.Sha256);
    }
    private static async Task SourceMetadata()
    {
        await using var f = new Fixture(); var o = f.Owner(); var before = await o.ReadSourceMetadataAsync();
        await Ok(o, "music.library.create", new { name = "Local" });
        var state = Parsed(before.State); state["revision"] = 1; state["installations"]!.AsArray().Add(Installation());
        var next = await o.CommitSourceMetadataAsync(E(state), before.Sha256); Check(next.State.GetProperty("revision").GetInt64() == 1);
        await Throws("REVISION_CONFLICT", async () => await o.CommitSourceMetadataAsync(E(state), before.Sha256));
        Check((await Ok(o, "music.library.list")).GetProperty("total").GetInt32() == 2);
    }
    private static async Task SourceFailure()
    {
        await using var f = new Fixture(); var o = f.Owner(); var installed = await Install(o, Installation());
        await o.CommitScriptValueAsync("test-install-a", "wy", "value", 1, E("synthetic"), false);
        var state = Parsed(installed.State); state["installations"] = new JsonArray(); state["revision"] = 2;
        f.Store.Failure = 2; await Throws("COMMIT_UNKNOWN", async () => await o.CommitSourceMetadataAsync(E(state), installed.Sha256));
        await Throws("COMMIT_UNKNOWN", async () => await o.ReadSourceMetadataAsync());
        await o.ReconcileAsync(); Check((await o.ReadSourceMetadataAsync()).State.GetProperty("installations").GetArrayLength() == 0);
        Check(f.Store.Root["entries"]!.AsArray().All(e => !e!["key"]!.GetValue<string>().Contains("lx-data")));
    }
    private static async Task Settings()
    {
        await using var f = new Fixture(); var o = f.Owner();
        var settings = (await Ok(o, "music.settings.get")).GetProperty("settings");
        Check(settings.GetProperty("schema").GetInt32() == 2 &&
            settings.GetProperty("revision").GetInt64() == 0 &&
            settings.GetProperty("lyrics").GetBoolean() &&
            settings.GetProperty("target").ValueKind == JsonValueKind.Null &&
            !settings.TryGetProperty("quality", out _));
        int writes = f.Store.Writes;
        await Error(o, "music.settings.save", new { ifRevision = 0, quality = "flac", lyrics = true, target = (object?)null }, "INVALID_REQUEST");
        Check(f.Store.Writes == writes);
        await Error(o, "music.settings.save", new { ifRevision = 0, lyrics = true, target = new { installationId = "missing", platform = "wy" } }, "NOT_FOUND");
        await Install(o, Installation());
        await Ok(o, "music.settings.save", new { ifRevision = 0, lyrics = false, target = new { installationId = "test-install-a", platform = "wy" } });
        await Error(o, "music.settings.save", new { ifRevision = 0, lyrics = true, target = (object?)null }, "REVISION_CONFLICT");
        var metadata = await o.ReadSourceMetadataAsync();
        var disabled = NextMetadata(metadata); disabled["installations"]![0]!["enabled"] = false;
        await o.CommitSourceMetadataAsync(E(disabled), metadata.Sha256);
        var cleared = (await Ok(o, "music.settings.get")).GetProperty("settings");
        Check(cleared.GetProperty("revision").GetInt64() == 2 &&
            cleared.GetProperty("target").ValueKind == JsonValueKind.Null);
        await o.DisposeAsync(); Check(!(await Ok(f.Owner(), "music.settings.get")).GetProperty("settings").GetProperty("lyrics").GetBoolean());
    }
    private static async Task ScriptStorage()
    {
        await using var f = new Fixture(); var o = f.Owner(); await Install(o, Installation(), Installation("test-install-b"));
        await o.CommitScriptValueAsync("test-install-a", "wy", "same", 1, E(new { value = "a" }), false);
        await o.CommitScriptValueAsync("test-install-b", "wy", "same", 1, E(new { value = "b" }), false);
        Check((await o.ReadScriptValueAsync("test-install-a", "wy", "same", 1)).GetProperty("value").GetString() == "a");
        Check((await o.ReadScriptValueAsync("test-install-a", "tx", "same", 1)).ValueKind == JsonValueKind.Null);
        await Throws("STALE_SESSION", async () => await o.CommitScriptValueAsync("test-install-a", "wy", "same", 0, E("stale"), false));
        await Throws("LIMIT", async () => await o.CommitScriptValueAsync("test-install-a", "wy", "large", 1, E(new string('x', 17000)), false));
        var old = await o.ReadSourceMetadataAsync(); var state = Parsed(old.State); state["revision"] = 2; state["installations"]![0]!["enabled"] = false;
        await o.CommitSourceMetadataAsync(E(state), old.Sha256);
        await Throws("PERMISSION_DENIED", async () => await o.ReadScriptValueAsync("test-install-a", "wy", "same", 2));
    }
    private static async Task RejectDocument(byte[] bytes, string expected = "CORRUPT_STORAGE",
        DocumentFingerprint? fingerprint = null)
    {
        await using var f = new Fixture();
        f.Store.Bytes = bytes.ToArray();
        f.Store.ReadFingerprint = fingerprint;
        byte[] original = f.Store.Bytes.ToArray();
        var owner = f.Owner();
        await Error(owner, "music.library.list", null, expected);
        await Error(owner, "music.library.create", new { name = "Must not write" }, expected);
        await Throws(expected, async () => await owner.ReadSourceMetadataAsync());
        await Throws(expected, async () => await owner.ReadSourceScriptDataAsync("test-install-a", 0));
        await Throws(expected, async () => await owner.CommitScriptValueAsync(
            "test-install-a", "wy", "key", 0, E("value"), false));
        await Throws(expected, async () => await owner.CommitSourceMetadataAsync(
            E(new { schema = 4, revision = 0, installations = Array.Empty<object>() }), new string('0', 64)));
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
            """{"version":1,"entries":[{"key":"[\"meowfilm\",\"music\",1,\"local\",\"[\\\"lx-data\\\",\\\"missing-installation\\\",\\\"wy\\\"]\"]","value":{"schema":1,"values":[]}}]}""",
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
            Changed(library, state => state["library"]!["songs"]!["library:local:a"]!.AsObject().Remove("source_data")),
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

        var sources = CurrentSources();
        sources["installations"]![0]!["previous"] = sources["installations"]![0]!["current"]!.DeepClone();
        foreach (var invalid in new[]
        {
            Changed(sources, state => state["schema"] = 1),
            Changed(sources, state => state["schema"] = 2),
            Changed(sources, state => state["schema"] = 3),
            Changed(sources, state => state["schema"] = 5),
            Changed(sources, state => state["schema"] = "4"),
            Changed(sources, state => state["installations"]![0]!["current"]!["config"] = new JsonObject()),
            Changed(sources, state => state["installations"]![0]!["current"]!["sourceHash"] = new string('0', 63)),
            Changed(sources, state => state["installations"]![0]!["current"]!["sourceBytes"] = 0),
            Changed(sources, state => state["installations"]![0]!["current"]!["source"] = SyntheticSource),
            Changed(sources, state => state["installations"]![0]!["current"]!["permissions"]!["unsupported"] = new JsonArray()),
            Changed(sources, state => state["installations"]![0]!["current"]!["permissions"]!.AsObject().Remove("storage")),
            Changed(sources, state => state["installations"]![0]!["previous"]!["permissions"]!["storage"] = new JsonArray("read", "read")),
            Changed(sources, state => state["installations"]![0]!["previous"]!["permissions"]!["storage"] = new JsonArray("unknown")),
            Changed(sources, state => state["installations"]![0]!["previous"]!["permissions"]!["unsupported"] = false),
            Changed(sources, state => state["installations"]![0]!["enabled"] = null)
        })
            await RejectDocument(Document(("lx-installations", invalid)));

        await RejectDocument(Document(("online-settings", O(new
        {
            schema = 2,
            revision = 0,
            lyrics = true,
            target = new { installationId = "missing-installation", platform = "wy" }
        }))));

        byte[] current = Encoding.UTF8.GetBytes("""{"version":1,"entries":[]}""");
        await RejectDocument(current, fingerprint: new DocumentFingerprint(true, current.Length, new string('0', 64)));
    }
    private static async Task InvalidSource()
    {
        await using var f = new Fixture(); var o = f.Owner(); var read = await o.ReadSourceMetadataAsync();
        var bad = Parsed(read.State); bad["revision"] = 1; bad["installations"]!.AsArray().Add(Installation("../escape"));
        try { await o.CommitSourceMetadataAsync(E(bad), read.Sha256); throw new InvalidOperationException("Bad metadata accepted"); }
        catch (MusicLibraryException) { Check(f.Store.Writes == 0); }
    }
    private static async Task SourceBudget()
    {
        await using var f = new Fixture(); var owner = f.Owner();
        byte[] source = Encoding.UTF8.GetBytes("// large source\n" + new string('猫', 900_000));
        var content = await owner.StoreScriptContentAsync(source);
        Check(content.Length == source.Length &&
            content.Sha256 == Convert.ToHexStringLower(SHA256.HashData(source)));
        var metadata = await owner.ReadSourceMetadataAsync();
        var next = Parsed(metadata.State); next["revision"] = 1;
        next["installations"]!.AsArray().Add(Installation(content: content));
        await owner.CommitSourceMetadataAsync(E(next), metadata.Sha256);
        await owner.DisposeAsync();
        var reopened = f.Owner();
        byte[] restored = await reopened.ReadScriptContentAsync(content.Sha256, content.Length);
        try { Check(source.SequenceEqual(restored)); }
        finally { CryptographicOperations.ZeroMemory(restored); }

        int beforeInterrupted = f.Store.Writes;
        byte[] interrupted = Encoding.UTF8.GetBytes("// interrupted\n" + new string('中', 800_000));
        f.Store.FailAtWrite = beforeInterrupted + 2;
        await Throws("STORAGE_ERROR", async () => await reopened.StoreScriptContentAsync(interrupted));
        Check((await reopened.ReadSourceMetadataAsync()).State.GetProperty("revision").GetInt64() == 1);
        var resumed = await reopened.StoreScriptContentAsync(interrupted);
        Check(resumed.Sha256 == Convert.ToHexStringLower(SHA256.HashData(interrupted)));

        byte[] corrupt = f.Store.Document(ContentKey(content.Sha256, 0));
        corrupt[0] ^= 0xff;
        f.Store.Seed(ContentKey(content.Sha256, 0), corrupt);
        await Throws("CORRUPT_STORAGE", async () => await reopened.ReadScriptContentAsync(content.Sha256, content.Length));
        await Throws("CORRUPT_STORAGE", async () => await reopened.StoreScriptContentAsync(source));
        CryptographicOperations.ZeroMemory(corrupt);
        CryptographicOperations.ZeroMemory(interrupted);
        CryptographicOperations.ZeroMemory(source);
    }
    private static JsonObject Sites(string value) => O(new { wy = new { schema = 1, values = new[] { new { key = "shared-key", value } } } });
    private static JsonObject NextMetadata(MusicSourceMetadataSnapshot old)
    { var state = Parsed(old.State); state["revision"] = state["revision"]!.GetValue<long>() + 1; return state; }
    private static async Task Overlay()
    {
        await using var f = new Fixture(); var o = f.Owner(); var metadata = await o.ReadSourceMetadataAsync();
        var snapshot = await o.ReadSourceScriptDataAsync("test-install-a", 0);
        Check(!snapshot.Sites.EnumerateObject().Any() && f.Store.Writes == 0);
        var next = NextMetadata(metadata); next["installations"]!.AsArray().Add(Installation(enabled: false));
        var overlay = Sites("candidate"); int writes = f.Store.Writes;
        var committed = await o.CommitSourceWithScriptDataAsync(E(next), metadata.Sha256, "test-install-a", E(overlay), snapshot.Sha256);
        Check(f.Store.Writes == writes + 1 && committed.State.GetProperty("revision").GetInt64() == 1);
        overlay["wy"]!["values"]![0]!["value"] = "mutated caller";
        await Throws("PERMISSION_DENIED", async () => await o.ReadScriptValueAsync("test-install-a", "wy", "shared-key", 1));
        await o.DisposeAsync(); var restarted = f.Owner();
        var durable = await restarted.ReadSourceScriptDataAsync("test-install-a", 1);
        Check(durable.Sites.GetProperty("wy").GetProperty("values")[0].GetProperty("value").GetString() == "candidate");
        await Throws("STALE_SESSION", async () => await restarted.ReadSourceScriptDataAsync("test-install-a", 0));
    }
    private static async Task OverlayConflict()
    {
        await using var f = new Fixture(); var o = f.Owner(); var metadata = await Install(o, Installation());
        var snapshot = await o.ReadSourceScriptDataAsync("test-install-a", 1);
        await o.CommitScriptValueAsync("test-install-a", "wy", "shared-key", 1, E("live"), false);
        var next = NextMetadata(metadata); next["installations"]![0]!["current"]!["version"] = "2";
        int writes = f.Store.Writes;
        await Throws("SCRIPT_DATA_CONFLICT", async () => await o.CommitSourceWithScriptDataAsync(E(next), metadata.Sha256, "test-install-a", E(Sites("candidate")), snapshot.Sha256));
        Check(writes == f.Store.Writes && (await o.ReadSourceMetadataAsync()).Sha256 == metadata.Sha256);
        Check((await o.ReadScriptValueAsync("test-install-a", "wy", "shared-key", 1)).GetString() == "live");
        var fresh = await o.ReadSourceScriptDataAsync("test-install-a", 1);
        await o.CommitSourceWithScriptDataAsync(E(next), metadata.Sha256, "test-install-a", E(Sites("candidate")), fresh.Sha256);
        await Throws("STALE_SESSION", async () => await o.CommitScriptValueAsync("test-install-a", "wy", "shared-key", 1, E("late"), false));
        Check((await o.ReadScriptValueAsync("test-install-a", "wy", "shared-key", 2)).GetString() == "candidate");
    }
    private static async Task OverlayIsolation()
    {
        await using var f = new Fixture(); var o = f.Owner(); var metadata = await Install(o, Installation(), Installation("test-install-b"));
        await o.CommitScriptValueAsync("test-install-a", "tx", "delete-me", 1, E("a"), false);
        await o.CommitScriptValueAsync("test-install-b", "wy", "shared-key", 1, E("b"), false);
        var a = await o.ReadSourceScriptDataAsync("test-install-a", 1); var b = await o.ReadSourceScriptDataAsync("test-install-b", 1);
        var next = NextMetadata(metadata); next["installations"]![1]!["name"] = "Not the candidate";
        await Throws("REVISION_CONFLICT", async () => await o.CommitSourceWithScriptDataAsync(E(next), metadata.Sha256, "test-install-a", E(Sites("a2")), a.Sha256));
        next = NextMetadata(metadata); next["installations"]![0]!["current"]!["version"] = "2";
        await o.CommitSourceWithScriptDataAsync(E(next), metadata.Sha256, "test-install-a", E(Sites("a2")), a.Sha256);
        Check((await o.ReadSourceScriptDataAsync("test-install-b", 2)).Sha256 == b.Sha256);
        Check((await o.ReadScriptValueAsync("test-install-a", "tx", "delete-me", 2)).ValueKind == JsonValueKind.Null);
    }
    private static async Task OverlayUnknown(bool committed)
    {
        await using var f = new Fixture(); var o = f.Owner(); var metadata = await Install(o, Installation());
        await o.CommitScriptValueAsync("test-install-a", "wy", "shared-key", 1, E("old"), false);
        var snapshot = await o.ReadSourceScriptDataAsync("test-install-a", 1); var next = NextMetadata(metadata);
        next["installations"]![0]!["current"]!["version"] = "2"; f.Store.Failure = committed ? 2 : 1;
        await Throws("COMMIT_UNKNOWN", async () => await o.CommitSourceWithScriptDataAsync(E(next), metadata.Sha256, "test-install-a", E(Sites("new")), snapshot.Sha256));
        await Throws("COMMIT_UNKNOWN", async () => await o.ReadSourceScriptDataAsync("test-install-a", 1));
        int writes = f.Store.Writes; await o.ReconcileAsync(); Check(f.Store.Writes == writes);
        Check((await o.ReadSourceMetadataAsync()).State.GetProperty("revision").GetInt64() == (committed ? 2 : 1));
        Check((await o.ReadScriptValueAsync("test-install-a", "wy", "shared-key", committed ? 2 : 1)).GetString() == (committed ? "new" : "old"));
        await o.DisposeAsync(); Check((await f.Owner().ReadSourceMetadataAsync()).State.GetProperty("revision").GetInt64() == (committed ? 2 : 1));
    }
    private static async Task QueuedScript()
    {
        await using var f = new Fixture(); var o = f.Owner(); var metadata = await Install(o, Installation());
        f.Store.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); f.Store.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = NextMetadata(metadata); next["installations"]![0]!["enabled"] = false;
        var disabling = o.CommitSourceMetadataAsync(E(next), metadata.Sha256);
        await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancel = new CancellationTokenSource();
        var cancelled = o.CommitScriptValueAsync("test-install-a", "wy", "late", 1, E("cancelled"), false, cancel.Token);
        var stale = o.CommitScriptValueAsync("test-install-a", "wy", "late", 1, E("stale"), false);
        cancel.Cancel(); f.Store.Release.TrySetResult(); await disabling;
        bool observed = false;
        try { await cancelled; } catch (OperationCanceledException) { observed = true; }
        Check(observed); await Throws("STALE_SESSION", async () => await stale);
        Check(!(await o.ReadSourceScriptDataAsync("test-install-a", 2)).Sites.EnumerateObject().Any() && f.Store.Writes == 2);
    }
    private static async Task OverlayInvalid()
    {
        await using var f = new Fixture(); var o = f.Owner(); var metadata = await Install(o, Installation());
        using var duplicate = JsonDocument.Parse("{\"name\":1,\"name\":2}");
        await Throws("CORRUPT_STORAGE", async () => await o.CommitScriptValueAsync("test-install-a", "wy", "value", 1, duplicate.RootElement, false));
        var snapshot = await o.ReadSourceScriptDataAsync("test-install-a", 1); var next = NextMetadata(metadata);
        var invalid = O(new { wy = new { schema = 1, values = Enumerable.Range(0, 65).Select(i => new { key = "key-" + i, value = i }).ToArray() } });
        await Throws("LIMIT", async () => await o.CommitSourceWithScriptDataAsync(E(next), metadata.Sha256, "test-install-a", E(invalid), snapshot.Sha256));
        await Throws("INVALID_REQUEST", async () => await o.CommitSourceWithScriptDataAsync(E(next), metadata.Sha256, "test-install-a", E(new Dictionary<string, object> { [""] = new { schema = 1, values = new object[0] } }), snapshot.Sha256));
        Check(f.Store.Writes == 1);
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

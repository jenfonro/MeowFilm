using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicLibraryReceiptIntegrationTests
{
    private const string InitialImage = "https://images.example.test/receipt-initial.jpg";
    private const string NewImage = "https://images.example.test/receipt-new.jpg";
    private const string OldImage = "https://images.example.test/receipt-old.jpg";
    private const string BackupImage = "https://images.example.test/receipt-backup.jpg";

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music library receipt W1 After fences older platform and sync observations", () => Task.Run(ObservationAfter));
        yield return new("Music library receipt W1 Before does not accept newer platform or sync observations", () => Task.Run(ObservationBefore));
        yield return new("Music library receipt W2 Before restores shuffle previous and queue version", () => Task.Run(QueueBefore));
        yield return new("Music library receipt W3 retirement closes record reads and a replacement loads durable songs", () => Task.Run(PreparationReads));
        yield return new("Music library receipt W3 Abort closes record reads without disposing borrowed SQLite", () => Task.Run(AbortReads));
        yield return new("Music library receipt W4 Before permits a fresh failed artwork callback to commit fallback", () => Task.Run(ArtworkBefore));
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static string Text(JsonElement value, string field) => value.GetProperty(field).GetString()!;
    private static string Text(JsonObject value, string field) => value[field]!.GetValue<string>();
    private static JsonElement Element(JsonNode value) => MusicSongFixture.Element(value);
    private static JsonObject Song(string image) => MusicSongFixture.Online("tx", "receipt-mid",
        "Receipt recording", "Receipt artist", cover: image, native: new JsonObject
        {
            ["qqSongId"] = "710001", ["songId"] = "710001", ["songType"] = 0,
            ["albumId"] = "receipt-album-mid", ["albumMid"] = "receipt-album-mid",
            ["strMediaMid"] = "receipt-media-mid"
        });
    private static string SourceKey(JsonObject song) => Text(song["sources"]![0]!.AsObject(), "sourceKey");
    private static JsonElement Snapshot(MusicLibraryOwner owner, string id) =>
        owner.SongSnapshot(id) ?? throw new InvalidOperationException("Registered song disappeared");
    private static JsonElement DurableSong(MusicRecordTestStore store, string id) =>
        Element(store.ReadSong(id) ?? throw new InvalidOperationException("SQLite song disappeared"));
    private static void CheckSong(JsonElement song, string id, string key, string image)
    {
        Check(Text(song, "songId") == id && Text(song, "cover_url") == image &&
            Text(song, "artworkSourceKey") == key && Text(song, "artworkSourceImage") == image,
            "Permanent song or selected artwork changed unexpectedly");
        Check(song.GetProperty("sources").GetArrayLength() == 1, "Source count changed");
        var source = song.GetProperty("sources")[0]; var info = source.GetProperty("musicInfo");
        Check(Text(source, "sourceKey") == key && Text(source, "source") == "tx" &&
            Text(info, "source") == "tx" && Text(info, "songmid") == "receipt-mid" &&
            Text(info, "qqSongId") == "710001" && Text(info, "songId") == "710001" &&
            Text(info, "strMediaMid") == "receipt-media-mid" && Text(info, "img") == image,
            "Source locator, native identity or authoritative source image was lost");
    }
    private static async Task<JsonElement> Ok(MusicLibraryOwner owner, string method, object? parameters = null)
    {
        var result = await owner.ExecuteAsync(method, parameters);
        Check(result.GetProperty("ok").GetBoolean(), method + ": " + result.GetRawText());
        return result;
    }
    private static void Error(JsonElement result, string code)
    {
        Check(!result.GetProperty("ok").GetBoolean() && Text(result, "code") == code,
            "Expected " + code + ": " + result.GetRawText());
    }
    private static async Task Unknown(Func<Task> operation)
    {
        try { await operation(); }
        catch (MusicLibraryException error) when (error.Code == "COMMIT_UNKNOWN") { return; }
        throw new InvalidOperationException("Expected COMMIT_UNKNOWN");
    }

    private static Task ObservationAfter() => ObservationReceipt(committed: true);
    private static Task ObservationBefore() => ObservationReceipt(committed: false);
    private static async Task ObservationReceipt(bool committed)
    {
        foreach (bool sync in new[] { false, true })
        {
            await using var store = new MusicRecordTestStore();
            await using var owner = new MusicLibraryOwner(store, database: store.Storage);
            var registered = await MusicSongFixture.AdoptAsync(owner, Song(InitialImage));
            string id = Text(registered, "songId"), key = SourceKey(registered);
            Check(Guid.TryParseExact(id, "D", out _) &&
                key == new JsonArray("tx", "receipt-mid", "").ToJsonString(),
                "Fixture must retain the platform source key and a native-owned UUID");
            await Ok(owner, "music.library.addSongs", new { id = "favorites", ifRevision = 0, songs = new[] { registered } });
            var before = store.Snapshot().Revision;
            var olderSync = sync ? await owner.ReadLibrarySyncAsync() : null;
            long older = olderSync?.SongObservation ?? owner.BeginSongObservation();
            var newerSync = sync ? await owner.ReadLibrarySyncAsync() : null;
            long newer = newerSync?.SongObservation ?? owner.BeginSongObservation();
            Check(newer > older, "Observation order is not increasing");
            async Task<JsonElement> Adopt(JsonObject song, bool latest)
            {
                if (sync)
                {
                    _ = await owner.AdoptLibrarySyncSongsAsync((latest ? newerSync : olderSync)!,
                        new[] { (song, true) }, default);
                    return Snapshot(owner, id);
                }
                return await owner.AdoptPlatformResultAsync(Element(song), default,
                    observation: latest ? newer : older, shape: MusicLibraryOwner.PlatformSongShape.Song);
            }
            if (committed) store.FailAfterWrite = true;
            else store.FailBeforeWrite = true;
            int calls = store.WriteCalls;
            await Unknown(async () => { _ = await Adopt(Song(NewImage), latest: true); });
            Check(store.WriteCalls == calls + 1, "Fault did not wrap exactly one commit attempt");
            var physical = store.Snapshot().Revision;
            Check(committed ? physical.Generation == before.Generation + 1 && physical.Receipt != before.Receipt :
                physical == before, "Injected fault did not produce the expected physical receipt");
            CheckSong(DurableSong(store, id), id, key, committed ? NewImage : InitialImage);
            CheckSong(Snapshot(owner, id), id, key, InitialImage);
            await owner.ReconcileAsync();
            Check(store.WriteCalls == calls + 1 && store.Snapshot().Revision == physical,
                "Receipt reconciliation replayed the unknown adoption");
            CheckSong(Snapshot(owner, id), id, key, committed ? NewImage : InitialImage);
            var late = await Adopt(Song(OldImage), latest: false);
            string expectedImage = committed ? NewImage : OldImage;
            CheckSong(late, id, key, expectedImage);
            CheckSong(Snapshot(owner, id), id, key, expectedImage);
            CheckSong(DurableSong(store, id), id, key, expectedImage);
            var final = store.Snapshot().Revision;
            Check(committed ? final == physical && store.WriteCalls == calls + 1 :
                final.Generation == before.Generation + 1 && final.Receipt != before.Receipt &&
                    store.WriteCalls == calls + 2,
                "After must reject older metadata without writing; Before must allow the still-valid older observation");
            var favorites = await Ok(owner, "music.library.detail", new { id = "favorites" });
            Check(favorites.GetProperty("songs").GetArrayLength() == 1, "Favorite reference was lost or duplicated");
            CheckSong(favorites.GetProperty("songs")[0], id, key, expectedImage);
            Check(store.FindSource(key) == id && store.SongIds().SequenceEqual([id]),
                "Observation settlement rebound or duplicated a registered source");
        }
    }

    private static async Task QueueBefore()
    {
        await using var store = new MusicRecordTestStore();
        await using var owner = new MusicLibraryOwner(store, database: store.Storage);
        var a = Song(InitialImage);
        var b = MusicSongFixture.Online("wy", "710002", "Other recording", "Other artist");
        await Ok(owner, "music.queue.replace", new { songs = new[] { a, b }, index = 0 });
        var initial = await Ok(owner, "music.queue.get");
        var entries = initial.GetProperty("entries");
        Check(entries.GetArrayLength() == 2, "Queue fixture requires two occurrences");
        string aEntry = Text(entries[0], "id"), bEntry = Text(entries[1], "id");
        string aId = Text(entries[0].GetProperty("song"), "songId"), bId = Text(entries[1].GetProperty("song"), "songId");
        Check(aEntry != bEntry && aId != bId && Text(initial, "currentId") == aEntry,
            "Queue fixture identities or initial selection are invalid");
        await Ok(owner, "music.queue.mode", new { mode = 2 });
        var selected = await Ok(owner, "music.queue.select", new { id = bEntry });
        long version = selected.GetProperty("version").GetInt64();
        Check(Text(selected, "currentId") == bEntry, "A to B selection failed");
        var before = store.Snapshot().Revision;
        var queueBefore = store.State("queue");
        int calls = store.WriteCalls;
        store.FailBeforeWrite = true;
        Error(await owner.ExecuteAsync("music.queue.clear"), "COMMIT_UNKNOWN");
        Check(store.WriteCalls == calls + 1 && store.Snapshot().Revision == before &&
            JsonNode.DeepEquals(store.State("queue"), queueBefore), "Failed clear changed durable queue or receipt");
        await owner.ReconcileAsync();
        var restored = await Ok(owner, "music.queue.get");
        Check(store.WriteCalls == calls + 1 && store.Snapshot().Revision == before &&
            Text(restored, "currentId") == bEntry && restored.GetProperty("version").GetInt64() == version &&
            restored.GetProperty("mode").GetInt32() == 2, "Before did not restore the selected queue and runtime version");
        var restoredEntries = restored.GetProperty("entries");
        Check(restoredEntries.GetArrayLength() == 2 && Text(restoredEntries[0], "id") == aEntry &&
            Text(restoredEntries[1], "id") == bEntry &&
            Text(restoredEntries[0].GetProperty("song"), "songId") == aId &&
            Text(restoredEntries[1].GetProperty("song"), "songId") == bId,
            "Reconciliation changed queue occurrences or song references");
        var previous = await Ok(owner, "music.queue.step", new { direction = -1, mode = 2 });
        Check(Text(previous, "targetId") == aEntry && Text(previous, "currentId") == aEntry &&
            previous.GetProperty("version").GetInt64() == version, "Failed clear erased A from shuffle history");
        Check(Text(store.State("queue"), "currentId") == aEntry && store.WriteCalls == calls + 2,
            "Restored previous selection did not commit exactly once");
    }

    private static void OpenQueries(MusicLibraryOwner owner, RecordReadCounter reads, JsonObject song)
    {
        string id = Text(song, "songId"), key = SourceKey(song);
        int before = reads.RecordReads;
        CheckSong(Snapshot(owner, id), id, key, InitialImage);
        Check(reads.RecordReads > before, "Open SongSnapshot did not read SQLite"); before = reads.RecordReads;
        var matches = owner.SongSnapshots(MusicSongMatcher.Query(song));
        Check(matches.Count == 1 && Text(matches[0], "songId") == id && reads.RecordReads > before,
            "Open SongSnapshots did not query the registered SQLite record"); before = reads.RecordReads;
        CheckSong(Element(owner.ProjectRepository(Song(InitialImage), MusicLibraryOwner.PlatformSongShape.Song)),
            id, key, InitialImage);
        Check(reads.RecordReads > before, "Open projection did not resolve SQLite identity"); before = reads.RecordReads;
        CheckSong(Element(owner.ReadLibrarySyncSong(id)), id, key, InitialImage);
        Check(reads.RecordReads > before, "Open sync song did not read SQLite"); before = reads.RecordReads;
        Check(owner.FindLibrarySyncSource(key) == id && reads.RecordReads > before,
            "Open sync source did not resolve SQLite identity");
    }
    private static void ClosedQueries(MusicLibraryOwner owner, RecordReadCounter reads, JsonObject song)
    {
        string id = Text(song, "songId"), key = SourceKey(song);
        void Reject(string name, Action query)
        {
            bool rejected = false;
            try { query(); }
            catch (MusicLibraryException error) when (error.Code == "CLOSED") { rejected = true; }
            Check(rejected, name + " must reject CLOSED");
            Check(reads.RecordReads == 0, name + " touched SQLite after record-read revocation");
        }
        reads.RecordReads = 0;
        Reject("SongSnapshot", () => { _ = owner.SongSnapshot(id); });
        Reject("SongSnapshots", () => { _ = owner.SongSnapshots(MusicSongMatcher.Query(song)); });
        Reject("ProjectRepository", () => { _ = owner.ProjectRepository(Song(InitialImage), MusicLibraryOwner.PlatformSongShape.Song); });
        Reject("ReadLibrarySyncSong", () => { _ = owner.ReadLibrarySyncSong(id); });
        Reject("FindLibrarySyncSource", () => { _ = owner.FindLibrarySyncSource(key); });
    }
    private static async Task PreparationReads()
    {
        await using var store = new MusicRecordTestStore();
        var reads = new RecordReadCounter(store);
        await using var owner = new MusicLibraryOwner(reads, database: store.Storage);
        var song = await MusicSongFixture.AdoptAsync(owner, Song(InitialImage));
        OpenQueries(owner, reads, song);
        await owner.PrepareShutdownAsync();
        ClosedQueries(owner, reads, song);
        Error(await owner.ExecuteAsync("music.library.detail", new { id = "favorites" }), "CLOSED");
        Check(!store.Disposed && store.ReadSong(Text(song, "songId")) is not null,
            "Preparation must not close the borrowed SQLite store");
        await owner.DisposeAsync();
        await using var reopened = new MusicLibraryOwner(reads, database: store.Storage);
        await Ok(reopened, "music.library.detail", new { id = "favorites" });
        OpenQueries(reopened, reads, song);
    }
    private static async Task AbortReads()
    {
        await using var store = new MusicRecordTestStore();
        var reads = new RecordReadCounter(store);
        await using var owner = new MusicLibraryOwner(reads, database: store.Storage);
        var song = await MusicSongFixture.AdoptAsync(owner, Song(InitialImage));
        OpenQueries(owner, reads, song);
        await owner.AbortAsync();
        ClosedQueries(owner, reads, song);
        Error(await owner.ExecuteAsync("music.queue.get"), "CLOSED");
        Check(!store.Disposed && store.ReadSong(Text(song, "songId")) is not null &&
            store.FindSource(SourceKey(song)) == Text(song, "songId"),
            "Abort closed borrowed SQLite instead of revoking this owner's reads");
        Check(reads.RecordReads == 0, "Closed command read records from the borrowed store");
    }

    private static async Task ArtworkBefore()
    {
        await using var store = new MusicRecordTestStore();
        await using var owner = new MusicLibraryOwner(store, database: store.Storage);
        var song = Song(InitialImage);
        var backup = MusicSongFixture.Online("wy", "710003", "Receipt recording", "Receipt artist", cover: BackupImage,
            native: new JsonObject { ["albumId"] = "710004" });
        song["sources"]!.AsArray().Add(backup["sources"]![0]!.DeepClone());
        var registered = await MusicSongFixture.AdoptAsync(owner, song);
        string id = Text(registered, "songId"), aKey = SourceKey(registered), bKey = SourceKey(backup);
        void Artwork(JsonElement current, string key, string image)
        {
            Check(Text(current, "songId") == id && Text(current, "cover_url") == image &&
                Text(current, "artworkSourceKey") == key && Text(current, "artworkSourceImage") == image,
                "Fallback selection and permanent identity disagree");
            Check(JsonNode.DeepEquals(JsonNode.Parse(current.GetProperty("sources").GetRawText()), registered["sources"]),
                "Fallback must not modify source keys, native identities or source image metadata");
        }
        Artwork(Snapshot(owner, id), aKey, InitialImage);
        var before = store.Snapshot().Revision;
        int calls = store.WriteCalls;
        store.FailBeforeWrite = true;
        await Unknown(() => owner.ArtworkLoadCompletedAsync(InitialImage, false));
        Check(store.WriteCalls == calls + 1 && store.Snapshot().Revision == before,
            "Fallback fault must precede the physical SQLite commit");
        Artwork(DurableSong(store, id), aKey, InitialImage);
        await owner.ReconcileAsync();
        Artwork(Snapshot(owner, id), aKey, InitialImage);
        Check(store.WriteCalls == calls + 1 && store.Snapshot().Revision == before,
            "Before reconciliation replayed the unknown artwork operation");
        await owner.ArtworkLoadCompletedAsync(InitialImage, false);
        Artwork(Snapshot(owner, id), bKey, BackupImage);
        Artwork(DurableSong(store, id), bKey, BackupImage);
        var after = store.Snapshot().Revision;
        Check(after.Generation == before.Generation + 1 && after.Receipt != before.Receipt &&
            store.WriteCalls == calls + 2 && store.FindSource(aKey) == id && store.FindSource(bKey) == id,
            "A fresh failure must commit B exactly once without rebinding either source");
    }

    // Count forwarded record reads; the existing fixture still owns all SQL and fault injection.
    private sealed class RecordReadCounter(MusicRecordTestStore store) : IMusicLibraryStore
    {
        internal int RecordReads;
        public Task<MusicLibrarySnapshot> ReadAsync(string scope, CancellationToken token = default) => store.ReadAsync(scope, token);
        public JsonObject? ReadArea(string scope, MusicLibraryRevision revision, string area, string[]? members = null) => store.ReadArea(scope, revision, area, members);
        public IReadOnlyList<RelationalRecord.SelectedRow> ReadRows(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] prefix, string[] members) => store.ReadRows(scope, revision, area, relations, prefix, members);
        public JsonObject? ReadRow(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key) => store.ReadRow(scope, revision, area, relations, key);
        public bool HasRelation(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, string relation) => store.HasRelation(scope, revision, area, relations, key, relation);
        public bool Matches(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, JsonObject value) => store.Matches(scope, revision, area, relations, key, value);
        public JsonObject? ReadSong(string id) { RecordReads++; return store.ReadSong(id); }
        public bool ContainsSong(string id) { RecordReads++; return store.ContainsSong(id); }
        public string? FindSource(string key) { RecordReads++; return store.FindSource(key); }
        public string? FindLocalFile(string id) { RecordReads++; return store.FindLocalFile(id); }
        public IReadOnlyList<string> FindCandidates(string key, double duration) { RecordReads++; return store.FindCandidates(key, duration); }
        public IReadOnlyList<string> FindArtwork(string address) { RecordReads++; return store.FindArtwork(address); }
        public IReadOnlyList<string> FindQuery(string query) { RecordReads++; return store.FindQuery(query); }
        public IReadOnlyList<string> SongIds() { RecordReads++; return store.SongIds(); }
        public Task<MusicLibraryRevision> CommitAsync(string scope, MusicLibraryRevision expected,
            MusicLibraryRevision next, MusicLibraryChanges changes, CancellationToken token = default) =>
            store.CommitAsync(scope, expected, next, changes, token);
        public ValueTask DisposeAsync() => store.DisposeAsync();
    }
}

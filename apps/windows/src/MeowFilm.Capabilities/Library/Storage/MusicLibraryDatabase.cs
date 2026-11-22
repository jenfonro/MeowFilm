using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.LibraryStorageHelpers;

namespace MeowFilm.Capabilities.Library;

/// <summary>The music owner borrows the App database; fixed rows and its receipt commit together.</summary>
public sealed class MusicLibraryDatabase : IMusicLibraryStore
{
    private static readonly string[] AllAreas =
    [
        "library", "queue", "history",
        "playback-settings", "online-settings", "native-device-settings", "native-appearance",
        "native-catalog-source", "native-desktop-lyrics", "native-quality-policy",
        "native-official-sources", "native-metadata-policy", "native-search-policy",
        "native-search-history", "native-source-exclusions", "native-local-lyrics", "library-sync"
    ];

    private static readonly HashSet<string> ValidAreaSet = new(AllAreas, StringComparer.Ordinal);

    private readonly IStorageDatabase database;
    private readonly bool ownsDatabase;
    internal IStorageDatabase Database => database;
    private readonly object admission = new();
    private string? activeScope;
    private bool closed;
    private Task? disposal;

    public MusicLibraryDatabase(IStorageDatabase database) => this.database = database;
    private MusicLibraryDatabase() { database = NativeDatabase.CreateMemory(); ownsDatabase = true; }
    public static MusicLibraryDatabase CreateMemory() => new();

    private static string OwnerKey(string scope) => "music.library:" + scope;

    private void Check(string? scope = null)
    {
        lock (admission)
        {
            if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
            if (scope is null) return;
            if (!LJ.Id(scope)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            if (activeScope is not null && activeScope != scope) throw new DocumentStoreException(DocumentStoreError.Conflict);
            activeScope = scope;
        }
    }

    private static MusicLibraryRevision Revision(DatabaseSession db, string scope)
    {
        var rev = db.Revision(OwnerKey(scope));
        var revision = new MusicLibraryRevision(rev.Revision, rev.Receipt);
        if (!ValidRevision(revision)) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return revision;
    }

    private static bool ValidRevision(MusicLibraryRevision revision) => revision.Generation >= 0 && revision.Generation <= LJ.MaxInteger &&
        (revision.Generation == 0 ? revision.Receipt == "" : Guid.TryParseExact(revision.Receipt, "D", out _));

    public Task<MusicLibrarySnapshot> ReadAsync(string scope, CancellationToken cancellationToken = default)
    {
        Check(scope);
        return database.ReadAsync(db => new MusicLibrarySnapshot(Revision(db, scope),
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)), cancellationToken);
    }

    // Explicit diagnostic export consumes every control record; ordinary commands do not.
    internal Task<MusicLibrarySnapshot> ReadAllAsync(string scope, CancellationToken cancellationToken = default)
    {
        Check(scope);
        return database.ReadSnapshotAsync(db => new MusicLibrarySnapshot(Revision(db, scope), AllAreas
            .Select(area => (Key: area, Value: ReadAreaCore(db, scope, area, null)))
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => (JsonNode?)pair.Value, StringComparer.Ordinal)), cancellationToken);
    }

    private T AtRevision<T>(string scope, MusicLibraryRevision revision, Func<DatabaseSession, T> read)
    {
        Check(scope);
        return database.ReadSnapshot(db =>
        {
            if (Revision(db, scope) != revision) throw new DocumentStoreException(DocumentStoreError.Conflict);
            return read(db);
        });
    }

    public JsonObject? ReadArea(string scope, MusicLibraryRevision revision, string area, string[]? members = null) =>
        AtRevision(scope, revision, db => ReadAreaCore(db, scope, area, members));

    public IReadOnlyList<MusicLibrarySelectedRow> ReadRows(string scope, MusicLibraryRevision revision, string area,
        string[] relations, object?[] prefix, string[] members) =>
        AtRevision(scope, revision, db => area == "history"
            ? MusicHistoryStore.ReadRows(db, relations, prefix, members)
            : MusicSettingsAreaStore.ReadRows(db, area, relations, prefix, members));

    public JsonObject? ReadRow(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key) =>
        AtRevision(scope, revision, db => ReadRowCore(db, area, relations, key));

    public bool HasRelation(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, string relation) =>
        AtRevision(scope, revision, db => area == "history"
            ? MusicHistoryStore.HasRelation(db, relations, key, relation)
            : MusicSettingsAreaStore.HasRelation(db, area, relations, key, relation));

    public bool Matches(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, JsonObject value) =>
        AtRevision(scope, revision, db =>
        {
            var stored = relations.Length == 0
                ? ReadAreaCore(db, (string)key[0]!, area, null)
                : ReadRowCore(db, area, relations, key);
            return MatchesSubset(stored, value);
        });

    private static JsonObject? ReadAreaCore(DatabaseSession db, string scope, string area, string[]? members) => area switch
    {
        "library" => SelectMembers(MusicCollectionsStore.Read(db, scope), members),
        "queue" => SelectMembers(MusicQueueStore.Read(db, scope), members),
        "history" => MusicHistoryStore.ReadArea(db, scope, members),
        _ when MusicSettingsAreaStore.SupportedAreas.Contains(area) => MusicSettingsAreaStore.ReadArea(db, scope, area, members),
        _ => throw new DocumentStoreException(DocumentStoreError.InvalidRequest)
    };

    private static JsonObject? ReadRowCore(DatabaseSession db, string area, string[] relations, object?[] key) =>
        area == "history"
            ? MusicHistoryStore.ReadRow(db, relations, key)
            : MusicSettingsAreaStore.ReadRow(db, area, relations, key);

    private T Read<T>(Func<DatabaseSession, T> action) { Check(); return database.Read(action); }

    public JsonObject? ReadSong(string id)
    {
        Check();
        return database.ReadSnapshot(db =>
        {
            var song = MusicSongStore.Read(db, id);
            if (song is not null) ValidateSong(id, song, DocumentStoreError.Corrupt);
            return song;
        });
    }

    public bool ContainsSong(string id) => Read(db => db.Scalar<long>("SELECT count(*) FROM music_songs WHERE song_id=$id", ("$id", id)) != 0);

    public string? FindSource(string sourceKey) => Read(db => db.Scalar<string>(
        "SELECT song_id FROM music_song_sources WHERE source_key=$value", ("$value", sourceKey)));

    public string? FindLocalFile(string localFileId) => Read(db => localFileId.Length == 0 ? null : db.Scalar<string>(
        "SELECT song_id FROM music_songs WHERE local_file_id=$value", ("$value", localFileId)));

    public IReadOnlyList<string> FindCandidates(string fingerprint, double baselineDuration) => Read(db =>
        fingerprint.Length == 0 || !double.IsFinite(baselineDuration) || baselineDuration <= 0 ? [] : db.Query("""
            SELECT song_id FROM music_songs WHERE fingerprint=$fingerprint AND baseline_duration BETWEEN $low AND $high
              AND EXISTS (SELECT 1 FROM music_song_sources s WHERE s.song_id=music_songs.song_id) ORDER BY song_id
            """, row => row.GetString(0), ("$fingerprint", fingerprint), ("$low", baselineDuration - 5), ("$high", baselineDuration + 5)));

    public IReadOnlyList<string> FindArtwork(string address) => Read(db => address.Length == 0 ? [] : db.Query(
        "SELECT song_id FROM music_songs WHERE cover_url=$address ORDER BY song_id", row => row.GetString(0), ("$address", address)));

    public IReadOnlyList<string> FindQuery(string query) => Read(db => db.Query(
        "SELECT song_id FROM music_songs WHERE search_query=$query ORDER BY song_id", row => row.GetString(0), ("$query", query)));

    public IReadOnlyList<string> SongIds() => Read(db => db.Query("SELECT song_id FROM music_songs ORDER BY song_id", row => row.GetString(0)));

    public Task<MusicLibraryRevision> CommitAsync(string scope, MusicLibraryRevision expected, MusicLibraryRevision next,
        MusicLibraryChanges changes, CancellationToken cancellationToken = default)
    {
        Check(scope); cancellationToken.ThrowIfCancellationRequested();
        if (!ValidRevision(expected) || !ValidRevision(next) || expected.Generation >= LJ.MaxInteger ||
            next.Generation != expected.Generation + 1 || next.Receipt == expected.Receipt)
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);

        var songs = new List<(string Id, MusicSongRecord Record, string Fingerprint, double? BaselineDuration, string SearchQuery)>(changes.Songs.Count);
        int bytes = 0;
        foreach (var (id, raw) in changes.Songs)
        {
            var copy = raw.DeepClone().AsObject();
            ValidateSong(id, copy, DocumentStoreError.InvalidRequest);
            var record = MusicSongRecord.FromJson(id, copy);
            bytes = checked(bytes + record.EstimateBytes());
            songs.Add((id, record, NativeMusicSong.Fingerprint(copy),
                NativeMusicSong.ValidDuration(copy["baselineDuration"]),
                MusicSongMatcher.Query(copy)));
        }

        var areas = changes.Areas.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone().AsObject(), StringComparer.Ordinal);
        if (areas.Keys.Any(name => !ValidAreaSet.Contains(name)))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        var partialAreas = changes.PartialAreas.ToHashSet(StringComparer.Ordinal);
        var rows = changes.Rows.Select(row => row with { Value = row.Value?.DeepClone().AsObject() }).ToArray();

        foreach (var value in areas.Values)
            bytes = checked(bytes + MusicStorageModelHelpers.EstimateJsonBytes(value));
        foreach (var row in rows)
            if (row.Value is not null)
                bytes = checked(bytes + MusicStorageModelHelpers.EstimateJsonBytes(row.Value));

        var owner = OwnerKey(scope);
        var expectedStorageRev = new StorageRevision(expected.Generation, expected.Receipt);

        return database.WriteAsync(db =>
        {
            if (Revision(db, scope) != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            foreach (var (id, record, fingerprint, baselineDuration, searchQuery) in songs)
            {
                var previous = MusicSongStore.ReadRecord(db, id);
                if (record.ContentEquals(previous)) continue;
                if (previous is not null)
                {
                    if (previous.BaselineDuration is not null &&
                        !MusicStorageModelHelpers.NullableDoubleEqual(previous.BaselineDuration, record.BaselineDuration))
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                    var keys = record.Sources.Select(s => s.SourceKey).ToHashSet(StringComparer.Ordinal);
                    if (previous.Sources.Any(s => !keys.Contains(s.SourceKey)))
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                }
                foreach (var source in record.Sources)
                    if (db.Scalar<string>("SELECT song_id FROM music_song_sources WHERE source_key=$key", ("$key", source.SourceKey)) is { } sourceOwner && sourceOwner != id)
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                MusicSongStore.SaveIncremental(db, record, previous, fingerprint, baselineDuration, searchQuery);
            }

            foreach (var (name, value) in areas)
            {
                bool partial = partialAreas.Contains(name);
                switch (name)
                {
                    case "library": MusicCollectionsStore.SaveIncremental(db, scope, value); break;
                    case "queue": MusicQueueStore.SaveIncremental(db, scope, value); break;
                    case "history": MusicHistoryStore.SaveAreaIncremental(db, scope, value); break;
                    default: MusicSettingsAreaStore.WriteArea(db, scope, name, value, partial); break;
                }
            }

            foreach (var row in rows)
            {
                if (row.Key[0] as string != scope) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
                if (row.Area == "history") MusicHistoryStore.ApplyRowChange(db, row);
                else MusicSettingsAreaStore.ApplyRowChange(db, row);
            }

            var committed = db.CommitRevision(owner, expectedStorageRev, next.Receipt);
            return new MusicLibraryRevision(committed.Revision, committed.Receipt);
        }, cancellationToken, bytes);
    }

    private static void ValidateSong(string id, JsonObject song, DocumentStoreError failure)
    {
        try
        {
            if (!Guid.TryParseExact(id, "D", out var parsed) || id != parsed.ToString("D") ||
                LJ.S(song["songId"]) != id)
                throw new DocumentStoreException(failure);
            _ = NativeMusicSong.Normalize(song);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in LJ.Array(song["sources"]).OfType<JsonObject>())
            {
                var info = LJ.Object(source["musicInfo"]);
                string platform = LJ.S(source["source"]), key = LJ.S(source["sourceKey"]);
                if (!NativeMusicSong.Platforms.Contains(platform) || LJ.S(info["source"]) != platform ||
                    NativeMusicSong.InfoId(info).Length == 0 || key != NativeMusicSong.InfoKey(info) || !keys.Add(key))
                    throw new DocumentStoreException(failure);
            }
        }
        catch (MusicLibraryException) { throw new DocumentStoreException(failure); }
    }

    public ValueTask DisposeAsync()
    {
        lock (admission)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            return new(disposal = CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        try { await database.DrainAsync().ConfigureAwait(false); }
        finally
        {
            if (ownsDatabase) await database.DisposeAsync().ConfigureAwait(false);
        }
    }
}

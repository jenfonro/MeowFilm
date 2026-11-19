using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>The music owner borrows the App database; fixed rows and its receipt commit together.</summary>
public sealed class MusicLibraryDatabase : IMusicLibraryStore
{
    private readonly IStorageDatabase database;
    private readonly bool ownsDatabase;
    internal IStorageDatabase Database => database;
    private readonly object admission = new();
    private string? activeScope;
    private bool closed;
    private MusicLibrarySnapshot? cached;
    private Task? disposal;

    public MusicLibraryDatabase(IStorageDatabase database) => this.database = database;
    private MusicLibraryDatabase() { database = NativeDatabase.CreateMemory(); ownsDatabase = true; }
    public static MusicLibraryDatabase CreateMemory() => new();
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
        var rows = db.Query("SELECT revision,receipt FROM music_owner_state WHERE scope=$scope",
            row => new MusicLibraryRevision(row.GetInt64(0), row.GetString(1)), ("$scope", scope));
        var revision = rows.SingleOrDefault() ?? MusicLibraryRevision.Empty;
        if (!ValidRevision(revision)) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return revision;
    }
    private static bool ValidRevision(MusicLibraryRevision revision) => revision.Generation >= 0 && revision.Generation <= LJ.MaxInteger &&
        (revision.Generation == 0 ? revision.Receipt == "" : Guid.TryParseExact(revision.Receipt, "D", out _));
    public Task<MusicLibrarySnapshot> ReadAsync(string scope, CancellationToken cancellationToken = default)
    {
        Check(scope);
        return database.ReadAsync(db =>
        {
            var revision = Revision(db, scope);
            if (cached?.Revision == revision) return Copy(cached);
            var areas = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (name, record) in MusicStorageRecords.Areas)
                if (record.Read(db, [scope]) is { } value) areas.Add(name, value);
            cached = new(revision, areas);
            return Copy(cached);
        }, cancellationToken);
    }
    private static MusicLibrarySnapshot Copy(MusicLibrarySnapshot snapshot) => new(snapshot.Revision,
        snapshot.Areas.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone(), StringComparer.Ordinal));
    private T Read<T>(Func<DatabaseSession, T> action) { Check(); return database.Read(action); }
    public JsonObject? ReadSong(string id) => Read(db =>
    {
        var song = MusicStorageRecords.Song.Read(db, [id]);
        if (song is not null) ValidateSong(id, song, DocumentStoreError.Corrupt);
        return song;
    });
    public bool ContainsSong(string id) => Read(db => db.Scalar<long>("SELECT count(*) FROM music_songs WHERE song_id=$id", ("$id", id)) != 0);
    public string? FindSource(string sourceKey) => Read(db => db.Scalar<string>(
        "SELECT song_id FROM music_song_sources WHERE sources_key=$value", ("$value", sourceKey)));
    public string? FindLocalFile(string localFileId) => Read(db => localFileId.Length == 0 ? null : db.Scalar<string>(
        "SELECT song_id FROM music_songs WHERE localFileId=$value", ("$value", localFileId)));
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
        var songs = changes.Songs.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone().AsObject(), StringComparer.Ordinal);
        var areas = changes.Areas.ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone().AsObject(), StringComparer.Ordinal);
        foreach (var (id, value) in songs) ValidateSong(id, value, DocumentStoreError.InvalidRequest);
        if (areas.Keys.Any(name => !MusicStorageRecords.Areas.ContainsKey(name)))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        int bytes = 0;
        foreach (var value in songs.Values.Concat(areas.Values))
        {
            bytes = checked(bytes + System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString(LJ.Json)));
        }
        return database.WriteAsync(db =>
        {
            if (Revision(db, scope) != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            foreach (var (id, value) in songs)
            {
                var previous = MusicStorageRecords.Song.Read(db, [id]);
                if (JsonNode.DeepEquals(value, previous)) continue;
                if (previous is not null)
                {
                    if (previous["baselineDuration"] is not null && !JsonNode.DeepEquals(previous["baselineDuration"], value["baselineDuration"]))
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                    var keys = LJ.Array(value["sources"]).OfType<JsonObject>().Select(source => LJ.S(source["sourceKey"])).ToHashSet(StringComparer.Ordinal);
                    if (LJ.Array(previous["sources"]).OfType<JsonObject>().Any(source => !keys.Contains(LJ.S(source["sourceKey"]))))
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                }
                foreach (var source in LJ.Array(value["sources"]).OfType<JsonObject>())
                    if (db.Scalar<string>("SELECT song_id FROM music_song_sources WHERE sources_key=$key", ("$key", LJ.S(source["sourceKey"]))) is { } owner && owner != id)
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                MusicStorageRecords.Song.Save(db, value, previous, [id], indexed: new Dictionary<string, object?>
                {
                    ["fingerprint"] = NativeMusicSong.Fingerprint(value),
                    ["baseline_duration"] = NativeMusicSong.ValidDuration(value["baselineDuration"]),
                    ["search_query"] = MusicSongMatcher.Query(value)
                });
            }
            foreach (var (name, value) in areas)
            {
                var record = MusicStorageRecords.Areas[name];
                var previous = cached?.Revision == expected && cached.Areas.TryGetValue(name, out var memory) ? memory?.AsObject() : record.Read(db, [scope]);
                record.Save(db, value, previous, [scope]);
            }
            db.Execute("INSERT INTO music_owner_state(scope,revision,receipt) VALUES($scope,$revision,$receipt) " +
                "ON CONFLICT(scope) DO UPDATE SET revision=excluded.revision,receipt=excluded.receipt",
                ("$scope", scope), ("$revision", next.Generation), ("$receipt", next.Receipt));
            // A failed COMMIT cannot make this cache appear current; the next read checks the durable revision.
            cached = null;
            return next;
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
            cached = null;
            if (ownsDatabase) await database.DisposeAsync().ConfigureAwait(false);
        }
    }
}

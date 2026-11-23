using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>A durable generation and exact receipt, independent of business revisions.</summary>
public sealed record MusicLibraryRevision(long Generation, string Receipt)
{
    public static MusicLibraryRevision Empty { get; } = new(0, "");
}

/// <summary>Owner receipt and any explicitly supplied controls. Ordinary reads select controls on demand.</summary>
public sealed record MusicLibrarySnapshot(MusicLibraryRevision Revision, IReadOnlyDictionary<string, JsonNode?> Areas);

public sealed record MusicLibraryRowChange(string Area, string[] Relations, object?[] Key, JsonObject? Value, int Ordinal, bool Insert = false);
public sealed record MusicLibraryChanges(IReadOnlyDictionary<string, JsonObject> Areas,
    IReadOnlyDictionary<string, JsonObject> Songs)
{
    public IReadOnlyList<MusicLibraryRowChange> Rows { get; init; } = [];
    public IReadOnlySet<string> PartialAreas { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>The music owner borrows the App database; fixed rows and its receipt commit together.</summary>
public sealed class MusicLibraryDatabase : IAsyncDisposable
{
    private readonly IStorageDatabase database;
    internal IStorageDatabase Database => database;
    private readonly object admission = new();
    private string? activeScope;
    private bool closed;
    private Task? disposal;

    public MusicLibraryDatabase(IStorageDatabase database) => this.database = database;
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
        return database.ReadAsync(db => new MusicLibrarySnapshot(Revision(db, scope),
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)), cancellationToken);
    }
    // Explicit diagnostic export consumes every control record; ordinary commands do not.
    internal Task<MusicLibrarySnapshot> ReadAllAsync(string scope, CancellationToken cancellationToken = default)
    {
        Check(scope);
        return database.ReadAsync(db => new MusicLibrarySnapshot(Revision(db, scope), MusicStorageRecords.Areas
            .Select(pair => (pair.Key, Value: pair.Value.Read(db, [scope])))
            .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => (JsonNode?)pair.Value, StringComparer.Ordinal)), cancellationToken);
    }
    private T AtRevision<T>(string scope, MusicLibraryRevision revision, Func<DatabaseSession, T> read)
    {
        Check(scope);
        return database.Read(db =>
        {
            if (Revision(db, scope) != revision) throw new DocumentStoreException(DocumentStoreError.Conflict);
            return read(db);
        });
    }
    private static RelationalRecord Record(string area, string[] relations)
    {
        var record = MusicStorageRecords.Areas[area];
        foreach (string relation in relations) record = record.Relation(relation);
        return record;
    }
    public JsonObject? ReadArea(string scope, MusicLibraryRevision revision, string area, string[]? members = null) =>
        AtRevision(scope, revision, db => members is null ? Record(area, []).Read(db, [scope]) : Record(area, []).ReadSelected(db, [scope], members));
    public IReadOnlyList<RelationalRecord.SelectedRow> ReadRows(string scope, MusicLibraryRevision revision, string area,
        string[] relations, object?[] prefix, string[] members) => AtRevision(scope, revision,
            db => Record(area, relations).ReadRows(db, prefix, members));
    public JsonObject? ReadRow(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key) =>
        AtRevision(scope, revision, db => Record(area, relations).Read(db, key));
    public bool HasRelation(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, string relation) =>
        AtRevision(scope, revision, db => Record(area, relations).HasRelation(db, key, relation));
    public bool Matches(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, JsonObject value) =>
        AtRevision(scope, revision, db => Record(area, relations).Matches(db, key, value));
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
        var partialAreas = changes.PartialAreas.ToHashSet(StringComparer.Ordinal);
        var rows = changes.Rows.Select(row => row with { Value = row.Value?.DeepClone().AsObject() }).ToArray();
        int bytes = 0;
        foreach (var value in songs.Values.Concat(areas.Values).Concat(rows.Where(row => row.Value is not null).Select(row => row.Value!)))
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
                if (partialAreas.Contains(name)) MusicStorageRecords.Areas[name].Patch(db, value, [scope]);
                else MusicStorageRecords.Areas[name].Replace(db, value, [scope]);
            foreach (var row in rows)
            {
                var record = Record(row.Area, row.Relations);
                if (row.Key[0] as string != scope) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
                if (row.Value is null) record.Delete(db, row.Key);
                else
                {
                    if (row.Insert) record.InsertAt(db, row.Key[..^1], row.Ordinal);
                    record.Replace(db, row.Value, row.Key, row.Ordinal);
                    var parent = Record(row.Area, row.Relations[..^1]);
                    parent.SetRelationPresent(db, row.Key[..parent.Keys.Count], row.Relations[^1]);
                }
            }
            db.Execute("INSERT INTO music_owner_state(scope,revision,receipt) VALUES($scope,$revision,$receipt) " +
                "ON CONFLICT(scope) DO UPDATE SET revision=excluded.revision,receipt=excluded.receipt",
                ("$scope", scope), ("$revision", next.Generation), ("$receipt", next.Receipt));
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
    private Task CloseAsync() => database.DrainAsync();
}

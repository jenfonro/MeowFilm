using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Capabilities.Library;

/// <summary>The native music record store; control records and affected songs commit together.</summary>
public sealed class MusicLibraryDatabase : IMusicLibraryStore
{
    private const int Format = 1;
    private const string FileName = "music-library.sqlite3";
    private const int MaximumRecordBytes = 16 * 1024 * 1024;
    private readonly string? databaseRoot;
    private readonly object admission = new(), databaseGate = new();
    private readonly SerialOperationQueue queue = new();
    private SqliteConnection? connection;
    private DatabaseDirectory? directory;
    private string? activeScope;
    private bool closed;
    private Task? disposal;

    public MusicLibraryDatabase(string databaseRoot) => this.databaseRoot = !string.IsNullOrEmpty(databaseRoot)
        ? databaseRoot : throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    private MusicLibraryDatabase() { }
    public static MusicLibraryDatabase CreateMemory() => new();

    public Task<MusicLibrarySnapshot> ReadAsync(string scope, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        return RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Open(scope);
            using var transaction = connection!.BeginTransaction(deferred: true);
            var revision = Header(scope, transaction);
            var areas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            using var command = Command("SELECT name, data FROM areas ORDER BY name", transaction);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                areas.Add(reader.GetString(0), Decode(reader.GetString(1)));
            }
            reader.Close();
            transaction.Commit();
            return new MusicLibrarySnapshot(revision, areas);
        }, cancellationToken);
    }

    public JsonObject? ReadSong(string id) => Read(() =>
    {
        using var transaction = connection!.BeginTransaction(deferred: true);
        using var command = Command("SELECT data FROM songs WHERE song_id=$id", transaction, ("$id", id));
        if (command.ExecuteScalar() is not string data) return null;
        var song = Decode(data);
        if (song.ContainsKey("sources") || LJ.S(song["songId"]) != id)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        var sources = new JsonArray();
        using var sourceCommand = Command("SELECT data FROM song_sources WHERE song_id=$id ORDER BY position", transaction, ("$id", id));
        using var reader = sourceCommand.ExecuteReader();
        while (reader.Read()) sources.Add(Decode(reader.GetString(0)));
        reader.Close();
        song["sources"] = sources;
        ValidateSong(id, song, DocumentStoreError.Corrupt);
        transaction.Commit();
        return song;
    });

    public bool ContainsSong(string id) => Read(() =>
        Lookup("SELECT song_id FROM songs WHERE song_id=$value", id) is not null);
    public string? FindSource(string sourceKey) => Read(() =>
        Lookup("SELECT song_id FROM song_sources WHERE source_key=$value", sourceKey));
    public string? FindLocalFile(string localFileId) => Read(() => localFileId.Length == 0 ? null :
        Lookup("SELECT song_id FROM songs WHERE local_file_id=$value", localFileId));
    public IReadOnlyList<string> FindCandidates(string fingerprint, double baselineDuration) => Read(() =>
        fingerprint.Length == 0 || !double.IsFinite(baselineDuration) || baselineDuration <= 0 ? [] :
            Ids("""
                SELECT song_id FROM songs
                WHERE fingerprint=$fingerprint AND baseline_duration BETWEEN $low AND $high
                  AND EXISTS (SELECT 1 FROM song_sources WHERE song_sources.song_id=songs.song_id)
                ORDER BY song_id
                """, ("$fingerprint", fingerprint), ("$low", baselineDuration - 5), ("$high", baselineDuration + 5)));
    public IReadOnlyList<string> FindArtwork(string address) => Read(() => address.Length == 0 ? [] :
        Ids("SELECT song_id FROM songs WHERE cover_url=$address ORDER BY song_id", ("$address", address)));
    public IReadOnlyList<string> FindQuery(string query) => Read(() =>
        Ids("SELECT song_id FROM songs WHERE search_query=$query ORDER BY song_id", ("$query", query)));
    public IReadOnlyList<string> SongIds() => Read(() => Ids("SELECT song_id FROM songs ORDER BY song_id"));

    public Task<MusicLibraryRevision> CommitAsync(string scope, MusicLibraryRevision expected, MusicLibraryRevision next,
        MusicLibraryChanges changes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref closed)) throw new DocumentStoreException(DocumentStoreError.Closed);
        ValidateScope(scope);
        if (!ValidRevision(expected) || !ValidRevision(next) || expected.Generation >= LJ.MaxInteger ||
            next.Generation != expected.Generation + 1 || next.Receipt == expected.Receipt || changes is null)
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        // Encode now, not after the caller's mutable JSON has waited in a queue.
        var songs = changes.Songs.Select(pair => EncodeSong(pair.Key, pair.Value)).ToArray();
        var areas = changes.Areas.Select(pair =>
        {
            if (!LJ.Id(pair.Key, 128))
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            return (Name: pair.Key, Data: Encode(pair.Value));
        }).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return RunAsync(() =>
        {
            RequireOpen(scope);
            using var transaction = connection!.BeginTransaction();
            if (Header(scope, transaction) != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            using var songCommand = Command("""
                INSERT INTO songs(song_id,data,fingerprint,baseline_duration,local_file_id,cover_url,search_query)
                VALUES($id,$data,$fingerprint,$duration,$file,$cover,$query)
                ON CONFLICT(song_id) DO UPDATE SET data=excluded.data, fingerprint=excluded.fingerprint,
                    baseline_duration=excluded.baseline_duration, local_file_id=excluded.local_file_id, cover_url=excluded.cover_url,
                    search_query=excluded.search_query
                WHERE (songs.baseline_duration IS NULL OR songs.baseline_duration IS excluded.baseline_duration)
                    AND songs.data<>excluded.data
                """, transaction, ("$id", ""), ("$data", ""), ("$fingerprint", ""), ("$duration", DBNull.Value),
                ("$file", DBNull.Value), ("$cover", ""), ("$query", ""));
            using var sourceCommand = Command("""
                INSERT INTO song_sources(source_key,song_id,position,platform,native_id,variant,data)
                VALUES($key,$id,$position,$platform,$native,$variant,$data)
                ON CONFLICT(source_key) DO UPDATE SET position=excluded.position, data=excluded.data
                WHERE song_sources.song_id=excluded.song_id
                """, transaction, ("$key", ""), ("$id", ""), ("$position", 0), ("$platform", ""),
                ("$native", ""), ("$variant", ""), ("$data", ""));
            foreach (var song in songs)
            {
                // Source ownership is permanent. Removing an existing locator
                // would also permit a later rebind, so it is not a valid update.
                var saved = new Dictionary<string, (int Position, string Data)>(StringComparer.Ordinal);
                using var previous = Command("SELECT source_key,position,data FROM song_sources WHERE song_id=$id", transaction, ("$id", song.Id));
                using (var reader = previous.ExecuteReader())
                    while (reader.Read())
                        saved.Add(reader.GetString(0), (reader.GetInt32(1), reader.GetString(2)));
                if (saved.Keys.Any(key => !song.Sources.Any(source => source.Key == key)))
                    throw new DocumentStoreException(DocumentStoreError.Conflict);
                Set(songCommand, ("$id", song.Id), ("$data", song.Data), ("$fingerprint", song.Fingerprint),
                    ("$duration", (object?)song.Duration ?? DBNull.Value), ("$file", (object?)song.LocalFile ?? DBNull.Value),
                    ("$cover", song.Cover), ("$query", song.Query));
                if (songCommand.ExecuteNonQuery() != 1)
                {
                    using var unchanged = Command("SELECT data FROM songs WHERE song_id=$id", transaction, ("$id", song.Id));
                    if (unchanged.ExecuteScalar() as string != song.Data) throw new DocumentStoreException(DocumentStoreError.Conflict);
                }
                foreach (var source in song.Sources)
                {
                    if (saved.TryGetValue(source.Key, out var current) &&
                        current.Position == source.Position && current.Data == source.Data) continue;
                    Set(sourceCommand, ("$key", source.Key), ("$id", song.Id), ("$position", source.Position),
                        ("$platform", source.Platform), ("$native", source.NativeId), ("$variant", source.Variant), ("$data", source.Data));
                    if (sourceCommand.ExecuteNonQuery() != 1) throw new DocumentStoreException(DocumentStoreError.Conflict);
                }
            }
            using var areaCommand = Command("""
                INSERT INTO areas(name,data) VALUES($name,$data)
                ON CONFLICT(name) DO UPDATE SET data=excluded.data WHERE areas.data<>excluded.data
                """, transaction, ("$name", ""), ("$data", ""));
            foreach (var area in areas)
            {
                Set(areaCommand, ("$name", area.Name), ("$data", area.Data));
                _ = areaCommand.ExecuteNonQuery();
            }
            using var header = Command("UPDATE header SET generation=$generation, receipt=$receipt WHERE singleton=1",
                transaction, ("$generation", next.Generation), ("$receipt", next.Receipt));
            if (header.ExecuteNonQuery() != 1) throw new DocumentStoreException(DocumentStoreError.Corrupt);
            transaction.Commit();
            return next;
        }, CancellationToken.None, cancellationToken);
    }

    private sealed record SourceRow(string Key, int Position, string Platform, string NativeId, string Variant, string Data);
    private sealed record SongRow(string Id, string Data, string Fingerprint, double? Duration, string? LocalFile,
        string Cover, string Query, SourceRow[] Sources);

    private static SongRow EncodeSong(string id, JsonObject song)
    {
        ValidateSong(id, song, DocumentStoreError.InvalidRequest);
        var listed = LJ.Array(song["sources"]);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var sources = listed.Select((value, position) =>
        {
            if (value is not JsonObject source || source["musicInfo"] is not JsonObject info)
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            string key = LJ.S(source["sourceKey"]), platform = LJ.S(source["source"]), nativeId = NativeMusicSong.InfoId(info);
            string variant = platform == "kg" ? LJ.Scalar(info["hash"] ?? info["albumAudioId"]).ToLowerInvariant() : "";
            if (!NativeMusicSong.Platforms.Contains(platform) || LJ.S(info["source"]) != platform || nativeId.Length == 0 ||
                key != NativeMusicSong.InfoKey(info) || !keys.Add(key))
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            return new SourceRow(key, position, platform, nativeId, variant, Encode(source));
        }).ToArray();
        var metadata = new JsonObject(song.Where(pair => pair.Key != "sources")
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
        string file = LJ.S(song["localFileId"]);
        return new(id, Encode(metadata), NativeMusicSong.Fingerprint(song),
            NativeMusicSong.ValidDuration(song["baselineDuration"]), file.Length == 0 ? null : file,
            LJ.S(song["cover_url"]), MusicSongMatcher.Query(song), sources);
    }
    private static void ValidateSong(string id, JsonObject song, DocumentStoreError failure)
    {
        try
        {
            if (!Guid.TryParseExact(id, "D", out var parsed) || id != parsed.ToString("D") ||
                LJ.S(song["songId"]) != id || !JsonNode.DeepEquals(song, NativeMusicSong.Normalize(song)))
                throw new DocumentStoreException(failure);
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

    private static string Encode(JsonObject value)
    {
        try
        {
            string text = value.ToJsonString(LJ.Json);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > MaximumRecordBytes) throw new DocumentStoreException(DocumentStoreError.Limit);
            _ = LJ.Parse(bytes, MaximumRecordBytes);
            return text;
        }
        catch (MusicLibraryException error)
        { throw new DocumentStoreException(error.Code == "LIMIT" ? DocumentStoreError.Limit : DocumentStoreError.InvalidRequest); }
        catch (DocumentStoreException) { throw; }
        catch { throw new DocumentStoreException(DocumentStoreError.InvalidRequest); }
    }
    private static JsonObject Decode(string text)
    {
        try { return LJ.Object(LJ.Parse(Encoding.UTF8.GetBytes(text), MaximumRecordBytes)); }
        catch (MusicLibraryException) { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
    }
    private static void ValidateScope(string scope)
    {
        if (scope is null || !LJ.Id(scope)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }
    private static bool ValidRevision(MusicLibraryRevision? revision) => revision is not null &&
        revision.Generation >= 0 && revision.Generation <= LJ.MaxInteger &&
        (revision.Generation == 0 ? revision.Receipt == "" : revision.Receipt is not null && LJ.Id(revision.Receipt));

    private void Open(string scope)
    {
        if (connection is not null) { RequireOpen(scope); return; }
        try
        {
            if (databaseRoot is not null) directory = new DatabaseDirectory(databaseRoot, FileName);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databaseRoot is null ? ":memory:" : directory!.DatabasePath, Pooling = false,
                Mode = databaseRoot is null ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private
            }.ToString());
            // :memory: is explicit and only reachable through CreateMemory().
            connection.Open();
            // Before the first schema read, native exclusive mode makes hot
            // journal recovery zero the header rather than delete our pinned file.
            // Normal multi-connection locking is restored after initialization.
            using (var locking = Command("PRAGMA locking_mode=EXCLUSIVE"))
                if (locking.ExecuteScalar() as string != "exclusive") throw new DocumentStoreException(DocumentStoreError.Storage);
            using (var setup = Command("PRAGMA synchronous=FULL; PRAGMA trusted_schema=OFF; PRAGMA temp_store=MEMORY; PRAGMA foreign_keys=ON"))
                _ = setup.ExecuteNonQuery();
            using (var journal = Command("PRAGMA journal_mode=PERSIST"))
            {
                string mode = (string)journal.ExecuteScalar()!;
                if (mode != (directory is null ? "memory" : "persist")) throw new DocumentStoreException(DocumentStoreError.Storage);
            }
            bool create = directory is null || directory.Created;
            if (!create)
            {
                using var version = Command("PRAGMA user_version");
                if (Convert.ToInt32(version.ExecuteScalar()) != Format)
                    throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
                _ = Header(scope);
                using var records = Command("""
                    SELECT song_id,data,fingerprint,baseline_duration,local_file_id,cover_url,search_query FROM songs LIMIT 0;
                    SELECT source_key,song_id,position,platform,native_id,variant,data FROM song_sources LIMIT 0;
                    SELECT name,data FROM areas LIMIT 0;
                    """);
                _ = records.ExecuteNonQuery();
            }
            if (create)
            {
                using var transaction = connection.BeginTransaction();
                using var schema = Command(Schema, transaction);
                _ = schema.ExecuteNonQuery();
                using var header = Command("INSERT INTO header VALUES(1,$format,$scope,0,'')", transaction,
                    ("$format", Format), ("$scope", scope));
                _ = header.ExecuteNonQuery();
                transaction.Commit();
            }
            if (directory is not null)
            {
                using var locking = Command("PRAGMA locking_mode=NORMAL");
                if (locking.ExecuteScalar() as string != "normal") throw new DocumentStoreException(DocumentStoreError.Storage);
            }
            activeScope = scope;
        }
        catch
        {
            connection?.Dispose(); connection = null;
            directory?.Dispose(); directory = null;
            throw;
        }
    }

    private MusicLibraryRevision Header(string scope, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT format,scope,generation,receipt FROM header WHERE singleton=1", transaction);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        if (reader.GetInt64(0) != Format) throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
        if (reader.GetString(1) != scope) throw new DocumentStoreException(DocumentStoreError.Conflict);
        var revision = new MusicLibraryRevision(reader.GetInt64(2), reader.GetString(3));
        if (!ValidRevision(revision) || reader.Read()) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return revision;
    }
    private void RequireOpen(string? scope = null)
    {
        if (connection is null) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        if (scope is not null && activeScope != scope) throw new DocumentStoreException(DocumentStoreError.Conflict);
        directory?.Check();
    }
    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        var command = connection!.CreateCommand();
        command.CommandText = sql; command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static void Set(SqliteCommand command, params (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters) command.Parameters[name].Value = value;
    }
    private string? Lookup(string sql, string value)
    {
        using var command = Command(sql, null, ("$value", value));
        return command.ExecuteScalar() as string;
    }
    private IReadOnlyList<string> Ids(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, null, parameters);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }
    private T Read<T>(Func<T> operation)
    {
        lock (databaseGate)
        {
            if (Volatile.Read(ref closed)) throw new DocumentStoreException(DocumentStoreError.Closed);
            RequireOpen();
            return Translate(operation);
        }
    }
    private Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken, CancellationToken admissionToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        admissionToken.ThrowIfCancellationRequested();
        lock (admission)
        {
            cancellationToken.ThrowIfCancellationRequested();
            admissionToken.ThrowIfCancellationRequested();
            if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
            return queue.Enqueue(() => Task.Run(() =>
            {
                lock (databaseGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    directory?.Check();
                    return Translate(operation);
                }
            }, CancellationToken.None), cancellationToken);
        }
    }
    private static T Translate<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (SqliteException error)
        {
            throw new DocumentStoreException(error.SqliteErrorCode switch
            {
                11 or 26 or 1 => DocumentStoreError.Corrupt,
                19 or 5 or 6 => DocumentStoreError.Conflict,
                13 => DocumentStoreError.Limit,
                _ => DocumentStoreError.Storage
            });
        }
        catch (DocumentStoreException) { throw; }
        catch (MusicLibraryException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new DocumentStoreException(DocumentStoreError.Storage); }
    }
    public ValueTask DisposeAsync()
    {
        lock (admission)
        {
            if (disposal is not null) return new(disposal);
            Volatile.Write(ref closed, true);
            return new(disposal = DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        await queue.DrainAsync().ConfigureAwait(false);
        lock (databaseGate)
        {
            try { _ = Translate(() => { connection?.Dispose(); return true; }); }
            finally { connection = null; directory?.Dispose(); directory = null; }
        }
    }

    private const string Schema = """
        PRAGMA user_version=1;
        CREATE TABLE header(singleton INTEGER PRIMARY KEY CHECK(singleton=1),format INTEGER NOT NULL,
            scope TEXT NOT NULL,generation INTEGER NOT NULL CHECK(generation>=0),receipt TEXT NOT NULL);
        CREATE TABLE songs(song_id TEXT PRIMARY KEY NOT NULL,data TEXT NOT NULL CHECK(json_valid(data)),
            fingerprint TEXT NOT NULL,baseline_duration REAL,local_file_id TEXT,cover_url TEXT NOT NULL,search_query TEXT NOT NULL);
        CREATE INDEX songs_recording ON songs(fingerprint,baseline_duration);
        CREATE UNIQUE INDEX songs_local_file ON songs(local_file_id) WHERE local_file_id IS NOT NULL;
        CREATE INDEX songs_artwork ON songs(cover_url);
        CREATE INDEX songs_query ON songs(search_query);
        CREATE TABLE song_sources(source_key TEXT PRIMARY KEY NOT NULL,song_id TEXT NOT NULL REFERENCES songs(song_id),
            position INTEGER NOT NULL CHECK(position>=0),platform TEXT NOT NULL,native_id TEXT NOT NULL,
            variant TEXT NOT NULL,data TEXT NOT NULL CHECK(json_valid(data)));
        CREATE INDEX song_sources_song ON song_sources(song_id,position);
        CREATE TABLE areas(name TEXT PRIMARY KEY NOT NULL,data TEXT NOT NULL CHECK(json_valid(data)));
        """;
}

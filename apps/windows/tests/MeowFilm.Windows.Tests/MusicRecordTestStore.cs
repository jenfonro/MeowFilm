using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Windows.Tests;

/// <summary>Test-owned SQLite companions. Documents remain separate from music records.</summary>
internal static class MusicTestStores
{
    private static ConditionalWeakTable<object, MusicRecordTestStore> companions = new();
    private static readonly List<MusicRecordTestStore> owned = [];
    internal static MusicRecordTestStore For(object fixture)
    {
        if (fixture is MusicRecordTestStore records) return records;
        lock (owned) return companions.GetValue(fixture, _ => new MusicRecordTestStore());
    }
    internal static MusicRecordTestStore Use(object fixture, string root)
    {
        lock (owned)
        {
            var records = new MusicRecordTestStore(root);
            companions.Remove(fixture); companions.Add(fixture, records); return records;
        }
    }
    internal static MusicRecordTestStore Attach(object fixture, MusicRecordTestStore records)
    {
        lock (owned) { companions.Remove(fixture); companions.Add(fixture, records); return records; }
    }
    internal static void Register(MusicRecordTestStore store) { lock (owned) owned.Add(store); }
    internal static async Task ReleaseAsync()
    {
        MusicRecordTestStore[] stores;
        lock (owned) { stores = owned.ToArray(); owned.Clear(); companions = new(); }
        foreach (var store in stores) await store.DisposeAsync();
    }
}

/// <summary>Faults wrap actual SQLite transactions, not a second table implementation.</summary>
internal class MusicRecordTestStore : IMusicLibraryStore
{
    internal const string RecordScope = "local";
    internal readonly string RootPath;
    internal readonly MusicLibraryDatabase Database;
    private string scope = RecordScope;
    private bool initialized;
    internal DocumentStoreError ReadError = DocumentStoreError.Storage;
    internal int Reads, Writes, WriteCalls, Failure, FailAtWrite;
    internal bool FailRead, FailBeforeWrite, FailAfterWrite, FailAfter, Lose, Disposed, Hold, Fault, FaultAfter;
    internal TaskCompletionSource? Entered, Release;
    internal Action? AfterRead, BeforeWrite;
    internal Func<MusicLibrarySnapshot, CancellationToken, Task>? AfterReadAsync;
    internal Func<MusicLibraryChanges, CancellationToken, Task>? BeforeCommit, AfterCommit;
    internal DocumentStoreError? ReadFailure;
    internal MusicLibraryRevision? WriteReceipt;
    internal MusicRecordTestStore(string? root = null)
    {
        RootPath = root ?? TestWorkspace.DataPath("music-records-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        Database = new(RootPath); MusicTestStores.Register(this);
    }
    public async Task<MusicLibrarySnapshot> ReadAsync(string scope, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); Reads++;
        if (FailRead || ReadFailure is not null)
            throw new DocumentStoreException(ReadFailure ?? ReadError);
        this.scope = scope;
        var result = await Database.ReadAsync(scope, token); initialized = true; AfterRead?.Invoke();
        if (AfterReadAsync is not null) await AfterReadAsync(result, token);
        return result;
    }
    public JsonObject? ReadSong(string id) => Database.ReadSong(id);
    public bool ContainsSong(string id) => Database.ContainsSong(id);
    public string? FindSource(string key) => Database.FindSource(key);
    public string? FindLocalFile(string id) => Database.FindLocalFile(id);
    public IReadOnlyList<string> FindCandidates(string key, double duration) => Database.FindCandidates(key, duration);
    public IReadOnlyList<string> FindArtwork(string url) => Database.FindArtwork(url);
    public IReadOnlyList<string> FindQuery(string query) => Database.FindQuery(query);
    public IReadOnlyList<string> SongIds() => Database.SongIds();
    public Task<MusicLibraryRevision> CommitAsync(string scope, MusicLibraryRevision expected,
        MusicLibraryRevision next, MusicLibraryChanges changes, CancellationToken token = default) =>
        Task.Run(() => CommitCoreAsync(scope, expected, next, changes, token));
    private async Task<MusicLibraryRevision> CommitCoreAsync(string scope, MusicLibraryRevision expected,
        MusicLibraryRevision next, MusicLibraryChanges changes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); WriteCalls++; BeforeWrite?.Invoke();
        if (BeforeCommit is not null) await BeforeCommit(changes, token);
        if ((Hold || Release is not null) && Release is { } signal)
        { Hold = false; Entered?.TrySetResult(); await signal.Task.WaitAsync(token); }
        int attempt = Writes + 1;
        int failure = Failure; Failure = 0;
        if (FailAtWrite == attempt) { failure = 1; FailAtWrite = 0; }
        if (Fault) { Fault = false; failure = FaultAfter ? 2 : 1; }
        if (FailBeforeWrite) { FailBeforeWrite = false; failure = 1; }
        if (FailAfterWrite || FailAfter || Lose)
        { FailAfterWrite = FailAfter = Lose = false; failure = 2; }
        if (failure == 1) { Writes++; throw new DocumentStoreException(DocumentStoreError.Storage); }
        var receipt = await Database.CommitAsync(scope, expected, next, changes, token);
        Writes++;
        if (AfterCommit is not null) await AfterCommit(changes, token);
        if (failure == 2) throw new DocumentStoreException(DocumentStoreError.Storage);
        return WriteReceipt ?? receipt;
    }
    internal void HoldCommit()
    {
        Hold = true; Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal JsonObject State(string area, string? requestedScope = null) =>
        Snapshot(requestedScope).Areas[area].DeepClone().AsObject();
    internal MusicLibrarySnapshot Snapshot(string? requestedScope = null) =>
        Database.ReadAsync(requestedScope ?? scope).GetAwaiter().GetResult();
    // A diagnostic export is not a persistence protocol. Control areas contain no songs.
    internal JsonObject Root
    {
        get
        {
            if (!initialized) { _ = Snapshot(); initialized = true; }
            using var connection = RawConnection();
            using var header = connection.CreateCommand();
            header.CommandText = "SELECT generation,receipt FROM header WHERE singleton=1";
            using var reader = header.ExecuteReader(); reader.Read();
            long generation = reader.GetInt64(0); string receipt = reader.GetString(1); reader.Close();
            var areas = new JsonObject(); var songs = new JsonObject();
            using var controls = connection.CreateCommand(); controls.CommandText = "SELECT name,data FROM areas ORDER BY name";
            using var values = controls.ExecuteReader();
            while (values.Read()) areas[values.GetString(0)] = JsonNode.Parse(values.GetString(1));
            values.Close();
            foreach (string id in Database.SongIds()) songs[id] = Database.ReadSong(id);
            return new JsonObject { ["schema"] = 3, ["generation"] = generation,
                ["receipt"] = receipt, ["areas"] = areas, ["songs"] = songs };
        }
    }
    private SqliteConnection RawConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(RootPath, "music-library.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=PERSIST";
        if (!string.Equals(journal.ExecuteScalar() as string, "persist", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fixture must retain the pinned persistent journal.");
        return connection;
    }
    internal byte[]? Data
    {
        get { var root = Root; return root["generation"]!.GetValue<long>() == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(root); }
        set
        {
            if (value is null) throw new InvalidOperationException("Use a fresh SQLite fixture for empty state.");
            Seed(JsonNode.Parse(value)!.AsObject());
        }
    }
    internal byte[]? Bytes { get => Data; set => Data = value; }
    internal void Seed(JsonObject snapshot)
    {
        if (snapshot["schema"]?.GetValue<int>() != 3 || snapshot["areas"] is not JsonObject areas ||
            snapshot["songs"] is not JsonObject songs) throw new InvalidOperationException("New-format diagnostic snapshot required.");
        if (!initialized) { _ = Snapshot(); initialized = true; }
        MusicLibraryRevision beforeRevision;
        using (var raw = RawConnection())
        using (var read = raw.CreateCommand())
        {
            read.CommandText = "SELECT generation,receipt FROM header WHERE singleton=1";
            using var row = read.ExecuteReader(); row.Read();
            beforeRevision = new(row.GetInt64(0), row.GetString(1));
        }
        if (songs.Count > 0)
        {
            var next = new MusicLibraryRevision(beforeRevision.Generation + 1, Guid.NewGuid().ToString("D"));
            Database.CommitAsync(scope, beforeRevision, next, new(new Dictionary<string, JsonObject>(),
                songs.ToDictionary(pair => pair.Key, pair => pair.Value!.AsObject()))).GetAwaiter().GetResult();
        }
        using var connection = RawConnection(); using var transaction = connection.BeginTransaction();
        using var clear = connection.CreateCommand(); clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM areas"; clear.ExecuteNonQuery();
        foreach (var pair in areas)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO areas(name,data) VALUES($name,$data)";
            command.Parameters.AddWithValue("$name", pair.Key);
            command.Parameters.AddWithValue("$data", pair.Value?.ToJsonString() ?? "null"); command.ExecuteNonQuery();
        }
        using var header = connection.CreateCommand(); header.Transaction = transaction;
        header.CommandText = "UPDATE header SET generation=generation+1,receipt=$receipt WHERE singleton=1";
        header.Parameters.AddWithValue("$receipt", Guid.NewGuid().ToString("D")); header.ExecuteNonQuery();
        transaction.Commit();
    }
    internal void SetArea(string name, JsonNode? value)
    {
        var snapshot = Root; snapshot["areas"]![name] = value?.DeepClone(); Seed(snapshot);
    }
    internal string RawSong(string id)
    {
        using var connection = RawConnection(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM songs WHERE song_id=$id";
        command.Parameters.AddWithValue("$id", id); return (string)command.ExecuteScalar()!;
    }
    internal void CorruptSong(string id, Action<JsonObject> change)
    {
        var song = JsonNode.Parse(RawSong(id))!.AsObject(); change(song);
        using var connection = RawConnection(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE songs SET data=$data WHERE song_id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$data", song.ToJsonString());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Expected the actual SQL song row.");
    }
    internal void SetAreaJson(string name, string json)
    {
        if (!initialized) { _ = Snapshot(); initialized = true; }
        using var connection = RawConnection(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO areas(name,data) VALUES($name,$data) ON CONFLICT(name) DO UPDATE SET data=excluded.data";
        command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$data", json); command.ExecuteNonQuery();
        using var header = connection.CreateCommand(); header.Transaction = transaction;
        header.CommandText = "UPDATE header SET generation=generation+1,receipt=$receipt WHERE singleton=1";
        header.Parameters.AddWithValue("$receipt", Guid.NewGuid().ToString("D")); header.ExecuteNonQuery(); transaction.Commit();
    }
    public async ValueTask DisposeAsync()
    {
        if (Disposed) return; Disposed = true; Release?.TrySetResult(); await Database.DisposeAsync();
    }
}

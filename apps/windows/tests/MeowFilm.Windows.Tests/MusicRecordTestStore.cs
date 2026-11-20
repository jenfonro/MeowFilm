using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>All test repositories borrow the same current App database.</summary>
internal static class MusicTestStores
{
    private static ConditionalWeakTable<object, MusicRecordTestStore> companions = new();
    private static readonly List<IAsyncDisposable> owned = [];
    internal static MusicRecordTestStore For(object fixture)
    {
        if (fixture is MusicRecordTestStore records) return records;
        lock (owned) return companions.GetValue(fixture, value =>
            new MusicRecordTestStore(value as IStorageDatabase));
    }
    internal static MusicRecordTestStore Use(object fixture, string root)
    {
        lock (owned)
        {
            if (fixture is not IStorageDatabase database || database.ProductRoot != root)
                throw new InvalidOperationException("The fixture must use one current App database root.");
            var records = new MusicRecordTestStore(database);
            companions.Remove(fixture); companions.Add(fixture, records); return records;
        }
    }
    internal static MusicRecordTestStore Attach(object fixture, MusicRecordTestStore records)
    {
        lock (owned) { companions.Remove(fixture); companions.Add(fixture, records); return records; }
    }
    internal static void Register(IAsyncDisposable store) { lock (owned) owned.Add(store); }
    internal static async ValueTask ReleaseCompanionAsync(object fixture)
    {
        MusicRecordTestStore? store;
        lock (owned)
        {
            companions.TryGetValue(fixture, out store);
            companions.Remove(fixture);
        }
        if (store is not null) await store.DisposeAsync();
    }
    internal static async Task ReleaseAsync()
    {
        IAsyncDisposable[] stores;
        lock (owned) { stores = owned.ToArray(); owned.Clear(); companions = new(); }
        foreach (var store in stores.Reverse()) await store.DisposeAsync();
    }
}

/// <summary>Faults wrap actual SQLite transactions, not a second table implementation.</summary>
internal class MusicRecordTestStore : IMusicLibraryStore
{
    internal const string RecordScope = "local";
    internal readonly string RootPath;
    internal readonly MusicLibraryDatabase Database;
    internal readonly IStorageDatabase Storage;
    private readonly bool ownsStorage;
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
    internal MusicRecordTestStore(IStorageDatabase? storage = null)
    {
        Storage = storage ?? new NativeDatabase(TestWorkspace.DataPath("music-records-" + Guid.NewGuid().ToString("N")));
        ownsStorage = storage is null;
        RootPath = Storage.ProductRoot!;
        Database = new(Storage); MusicTestStores.Register(this);
    }
    internal MusicRecordTestStore(string root) : this(new NativeDatabase(root)) => ownsStorage = true;
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
    public JsonObject? ReadArea(string scope, MusicLibraryRevision revision, string area, string[]? members = null)
    {
        if (FailRead || ReadFailure is not null) throw new DocumentStoreException(ReadFailure ?? ReadError);
        return Database.ReadArea(scope, revision, area, members);
    }
    public IReadOnlyList<RelationalRecord.SelectedRow> ReadRows(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] prefix, string[] members) => Database.ReadRows(scope, revision, area, relations, prefix, members);
    public JsonObject? ReadRow(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key) => Database.ReadRow(scope, revision, area, relations, key);
    public bool HasRelation(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, string relation) => Database.HasRelation(scope, revision, area, relations, key, relation);
    public bool Matches(string scope, MusicLibraryRevision revision, string area, string[] relations, object?[] key, JsonObject value) => Database.Matches(scope, revision, area, relations, key, value);
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
        Snapshot(requestedScope).Areas[area]!.DeepClone().AsObject();
    internal MusicLibrarySnapshot Snapshot(string? requestedScope = null) =>
        Database.ReadAllAsync(requestedScope ?? scope).GetAwaiter().GetResult();
    // A diagnostic export is not a persistence protocol. Control areas contain no songs.
    internal JsonObject Root
    {
        // Diagnostic exports also consume synchronous song indexes. Keep this fixture
        // work off the STA runner without changing how it calls the actual owner.
        get => Task.Run(() =>
        {
            if (!initialized) { _ = Snapshot(); initialized = true; }
            var saved = Snapshot();
            var areas = new JsonObject(); var songs = new JsonObject();
            foreach (var pair in saved.Areas) areas[pair.Key] = pair.Value?.DeepClone();
            foreach (string id in Database.SongIds()) songs[id] = Database.ReadSong(id);
            return new JsonObject { ["schema"] = 1, ["generation"] = saved.Revision.Generation,
                ["receipt"] = saved.Revision.Receipt, ["areas"] = areas, ["songs"] = songs };
        }).GetAwaiter().GetResult();
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
        if (snapshot["schema"]?.GetValue<int>() != 1 || snapshot["areas"] is not JsonObject areas ||
            snapshot["songs"] is not JsonObject songs) throw new InvalidOperationException("New-format diagnostic snapshot required.");
        if (!initialized) { _ = Snapshot(); initialized = true; }
        Storage.WriteAsync(db =>
        {
            foreach (var pair in songs)
                MusicStorageRecords.Song.Save(db, pair.Value!.AsObject(),
                    MusicStorageRecords.Song.Read(db, [pair.Key]), [pair.Key]);
            foreach (var pair in areas)
            {
                var record = MusicStorageRecords.Areas[pair.Key];
                record.Save(db, pair.Value!.AsObject(), record.Read(db, [scope]), [scope]);
            }
            db.Execute("INSERT INTO music_owner_state(scope,revision,receipt) VALUES($scope,1,$receipt) " +
                "ON CONFLICT(scope) DO UPDATE SET revision=revision+1,receipt=excluded.receipt",
                ("$scope", scope), ("$receipt", Guid.NewGuid().ToString("D")));
            return true;
        }).GetAwaiter().GetResult();
    }
    internal void SetArea(string name, JsonNode? value)
    {
        var snapshot = Root; snapshot["areas"]![name] = value?.DeepClone(); Seed(snapshot);
    }
    internal string RawSong(string id)
    {
        return Storage.ReadAsync(db => MusicStorageRecords.Song.Read(db, [id])!.ToJsonString(LJ.Json)).GetAwaiter().GetResult();
    }
    internal void CorruptSong(string id, Action<JsonObject> change)
    {
        var song = JsonNode.Parse(RawSong(id))!.AsObject(); change(song);
        Storage.WriteAsync(db =>
        {
            MusicStorageRecords.Song.Save(db, song, MusicStorageRecords.Song.Read(db, [id]), [id]);
            return true;
        }).GetAwaiter().GetResult();
    }
    internal void SetAreaJson(string name, string json)
    {
        SetArea(name, JsonNode.Parse(json));
    }
    public async ValueTask DisposeAsync()
    {
        if (Disposed) return; Disposed = true; Release?.TrySetResult(); await Database.DisposeAsync();
        if (ownsStorage) await Storage.DisposeAsync();
    }
}

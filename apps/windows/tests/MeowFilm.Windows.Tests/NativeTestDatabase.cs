using MeowFilm.Platform.Storage;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Windows.Tests;

/// <summary>Fault and lifecycle probes around the actual current SQLite database.</summary>
internal class NativeTestDatabase : IStorageDatabase
{
    internal readonly IStorageDatabase Inner;
    private readonly bool owns;
    internal int Reads, Writes, Disposals;
    internal Func<CancellationToken, Task>? BeforeRead, BeforeWrite;
    internal Action? AfterWrite;
    internal NativeTestDatabase(string? root = null)
    {
        Inner = new NativeDatabase(root ?? TestWorkspace.DataPath("native-database-" + Guid.NewGuid().ToString("N")));
        owns = true;
        MusicTestStores.Register(this);
    }
    internal NativeTestDatabase(IStorageDatabase inner, bool owns = false)
    {
        Inner = inner; this.owns = owns;
        if (owns) MusicTestStores.Register(this);
    }
    public Task Ready => Inner.Ready;
    public string? ProductRoot => Inner.ProductRoot;
    public string? DatabasePath => Inner.DatabasePath;
    public virtual async Task<T> ReadAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default)
    {
        Interlocked.Increment(ref Reads);
        if (BeforeRead is { } before) await before(token).ConfigureAwait(false);
        return await Inner.ReadAsync(read, token).ConfigureAwait(false);
    }
    public virtual async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
        int retainedBytes = 0, Action<Action>? commit = null)
    {
        Interlocked.Increment(ref Writes);
        if (BeforeWrite is { } before) await before(token).ConfigureAwait(false);
        var result = await Inner.WriteAsync(write, token, retainedBytes, commit).ConfigureAwait(false);
        AfterWrite?.Invoke();
        return result;
    }
    public Task<StorageRevision> RevisionAsync(string owner, CancellationToken token = default) =>
        ReadAsync(db => db.Revision(owner), token);
    public T Read<T>(Func<DatabaseSession, T> read) =>
        Task.Run(() => ReadAsync(read)).GetAwaiter().GetResult();
    public Task DrainAsync() => Inner.DrainAsync();
    internal Task SetReadOnlyAsync(bool value) =>
        Inner.ReadAsync(db => db.Execute(value ? "PRAGMA query_only=ON" : "PRAGMA query_only=OFF"));
    internal JsonObject? Project(RelationalRecord record, params object?[] key) =>
        Inner.ReadAsync(db => record.Read(db, key)).GetAwaiter().GetResult();
    internal void Seed(RelationalRecord record, JsonObject? value, params object?[] key) =>
        Inner.WriteAsync(db => { record.Save(db, value, record.Read(db, key), key); return true; }).GetAwaiter().GetResult();
    internal static JsonObject Object(object value) => JsonNode.Parse(JsonSerializer.Serialize(value))!.AsObject();
    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref Disposals, 1) != 0) return;
        if (owns)
        {
            try { await MusicTestStores.ReleaseCompanionAsync(this).ConfigureAwait(false); }
            finally { await Inner.DisposeAsync().ConfigureAwait(false); }
        }
    }
}

/// <summary>App controls share the real database; diagnostics project only their declared relations.</summary>
internal class AppSettingsTestDatabase : NativeTestDatabase
{
    internal bool Fault, FaultAfter, HoldWrites;
    internal bool Disposed => Disposals != 0;
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly HashSet<string> WrittenOwners = [];
    private static readonly (string Owner, RelationalRecord Record)[] records =
    [
        (MeowFilm.AppHost.AppAppearance.DocumentNamespace, MeowFilm.AppHost.AppStorageRecords.Appearance),
        (MeowFilm.AppHost.AppClosePreferences.DocumentNamespace, MeowFilm.AppHost.AppStorageRecords.Window),
        (MeowFilm.AppHost.AppMediaControlSettings.DocumentNamespace, MeowFilm.AppHost.AppStorageRecords.MediaControls)
    ];
    internal byte[]? AppearanceData
    {
        get => Data(MeowFilm.AppHost.AppStorageRecords.Appearance);
        set => Set(MeowFilm.AppHost.AppStorageRecords.Appearance, value);
    }
    internal byte[]? WindowData
    {
        get => Data(MeowFilm.AppHost.AppStorageRecords.Window);
        set => Set(MeowFilm.AppHost.AppStorageRecords.Window, value);
    }
    internal byte[]? ControlsData
    {
        get => Data(MeowFilm.AppHost.AppStorageRecords.MediaControls);
        set => Set(MeowFilm.AppHost.AppStorageRecords.MediaControls, value);
    }
    private byte[]? Data(RelationalRecord record) =>
        Project(record, "device") is { } value ? JsonSerializer.SerializeToUtf8Bytes(value) : null;
    private void Set(RelationalRecord record, byte[]? value) =>
        Seed(record, value is null ? null : JsonNode.Parse(value)!.AsObject(), "device");
    public override async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
        int retainedBytes = 0, Action<Action>? commit = null)
    {
        bool fault = Fault, after = FaultAfter;
        Fault = false;
        var changed = new List<string>();
        var result = await base.WriteAsync(db =>
        {
            if (HoldWrites)
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            if (fault && !after) throw new DocumentStoreException(DocumentStoreError.Storage);
            var previous = records.ToDictionary(item => item.Owner, item => db.Revision(item.Owner));
            var value = write(db);
            changed.AddRange(records.Where(item => previous[item.Owner] != db.Revision(item.Owner)).Select(item => item.Owner));
            return value;
        }, token, retainedBytes, commit).ConfigureAwait(false);
        WrittenOwners.UnionWith(changed);
        if (fault) throw new DocumentStoreException(DocumentStoreError.Storage);
        return result;
    }
}

internal static class RecordTestReads
{
    internal static Task<RecordSnapshot> ReadRecordAsync(this IStorageDatabase database, string owner, RelationalRecord record,
        CancellationToken token = default) => new TypedRecordRepository(database, owner, record).ReadAsync(token);
    internal static byte[]? RecordBytes(this IStorageDatabase database, RelationalRecord record) =>
        database.ReadAsync(db => record.Read(db, ["device"]) is { } row ?
            JsonSerializer.SerializeToUtf8Bytes(row) : null).GetAwaiter().GetResult();

    internal static Task<RecordSnapshot> ReadAudioIndexAsync(this IStorageDatabase database) =>
        database.ReadAsync(db =>
        {
            var entries = new JsonObject(MeowFilm.Capabilities.PlaybackCache.AudioStorageRecords.Index.ReadMany(db, [])
                .Select(row => new KeyValuePair<string, JsonNode?>(row["resourceKey"]!.GetValue<string>(), row)));
            return new RecordSnapshot(entries.Count == 0 && db.Revision(MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache.Namespace) == StorageRevision.Empty ? null : JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            { ["version"] = 1, ["entries"] = entries }), db.Revision(MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache.Namespace));
        });
    internal static Task SeedAudioIndexAsync(this IStorageDatabase database, JsonObject root) =>
        database.WriteAsync(db =>
        {
            var record = MeowFilm.Capabilities.PlaybackCache.AudioStorageRecords.Index;
            var entries = root["entries"]!.AsObject();
            foreach (var row in record.ReadMany(db, []))
            {
                string id = row["resourceKey"]!.GetValue<string>();
                if (!entries.ContainsKey(id)) record.Save(db, null, row, [id]);
            }
            foreach (var (id, row) in entries)
                record.Save(db, row!.AsObject(), record.Read(db, [id]), [id]);
            return db.CommitRevision(MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache.Namespace,
                db.Revision(MeowFilm.Capabilities.PlaybackCache.MusicAudioFileCache.Namespace), Guid.NewGuid().ToString("D"));
        });
    internal static Task<JsonArray> ReadMatchRowsAsync(this IStorageDatabase database) => database.ReadAsync(db =>
        new JsonArray(MeowFilm.Capabilities.Library.MatchStorageRecords.Entry.ReadMany(db, []).Select(row =>
        { row.Remove("storedBytes"); return (JsonNode?)row; }).ToArray()));
    internal static Task<JsonObject> ReadPresentationEntriesAsync(this IStorageDatabase database) => database.ReadAsync(db =>
    {
        var entries = new JsonObject();
        foreach (var row in db.Query("SELECT address,bytes,updated_at FROM windows_images",
            row => (Address: row.GetString(0), Bytes: row.GetInt32(1), At: row.GetInt64(2))))
            entries["image:" + row.Address] = new JsonObject { ["kind"] = "image", ["bytes"] = row.Bytes, ["updatedAt"] = row.At };
        foreach (var id in db.Query("SELECT song_id FROM windows_lyrics", row => row.GetString(0)))
            entries["lyrics:" + id] = MeowFilm.Capabilities.PlaybackCache.PlaybackCacheRecords.Lyrics.Read(db, [id]);
        return entries;
    });
}

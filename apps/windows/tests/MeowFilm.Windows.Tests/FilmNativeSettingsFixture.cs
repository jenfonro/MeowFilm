using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>Current typed repositories on real SQLite, with explicit I/O fault/receipt probes.</summary>
internal class FilmNativeSettingsFixture : NativeTestDatabase
{
    internal DocumentStoreError? ReadError, WriteError;
    internal Func<CancellationToken, Task>? Reading, Writing;
    internal CancellationToken WriteToken { get; private set; }
    internal bool FailWrite;
    private readonly Dictionary<string, int> writesByOwner = [];
    internal int WritesFor(string owner) => writesByOwner.GetValueOrDefault(owner);
    private static RelationalRecord Record(string owner) => owner switch
    {
        "film.rules.v1" => MeowFilm.Capabilities.FilmStorageRecords.Rules,
        "film.tmdb.v1" => MeowFilm.Capabilities.FilmStorageRecords.TMDB,
        "film.douban.v1" => MeowFilm.Capabilities.FilmStorageRecords.Douban,
        "film.preferences.v1" => MeowFilm.Capabilities.FilmStorageRecords.Preferences,
        "film.cat-sources.v1" => MeowFilm.Capabilities.FilmStorageRecords.Cat,
        _ => throw new InvalidOperationException("Unknown current test relation")
    };
    internal void Seed(string owner, JsonElement value)
    {
        var record = Record(owner);
        var row = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!.AsObject();
        Inner.WriteAsync(db =>
        {
            record.Save(db, row, record.Read(db, ["device"]), ["device"]);
            db.CommitRevision(owner, db.Revision(owner), Guid.NewGuid().ToString("D"));
            return true;
        }).GetAwaiter().GetResult();
    }
    internal void Seed(string owner, object value) => Seed(owner, JsonSerializer.SerializeToElement(value,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    internal JsonElement Read(string owner) => Value(owner);
    internal JsonElement Value(string owner) => Inner.ReadAsync(db =>
        JsonSerializer.SerializeToElement(Record(owner).Read(db, ["device"])!)).GetAwaiter().GetResult();
    public override async Task<T> ReadAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default)
    {
        Interlocked.Increment(ref Reads);
        if (Reading is { } hook) await hook(token).ConfigureAwait(false);
        if (ReadError is { } error) throw new DocumentStoreException(error);
        return await Inner.ReadAsync(read, token).ConfigureAwait(false);
    }
    public override async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
        int retainedBytes = 0, Action<Action>? commit = null)
    {
        if (BeforeWrite is { } before) await before(token).ConfigureAwait(false);
        var changed = new List<string>();
        var result = await Inner.WriteAsync(db =>
        {
            Interlocked.Increment(ref Writes);
            WriteToken = CancellationToken.None;
            Writing?.Invoke(CancellationToken.None).GetAwaiter().GetResult();
            if (FailWrite) throw new DocumentStoreException(DocumentStoreError.Storage);
            if (WriteError is { } error) throw new DocumentStoreException(error);
            var previous = new Dictionary<string, StorageRevision>();
            foreach (var owner in new[] { "film.rules.v1", "film.tmdb.v1", "film.douban.v1", "film.preferences.v1", "film.cat-sources.v1" })
                previous[owner] = db.Revision(owner);
            var result = write(db);
            foreach (var (owner, revision) in previous)
                if (db.Revision(owner) != revision) changed.Add(owner);
            return result;
        }, token, retainedBytes, commit).ConfigureAwait(false);
        foreach (string owner in changed) writesByOwner[owner] = WritesFor(owner) + 1;
        return result;
    }
}

/// <summary>Held/failing notification transport double for the production App facade.</summary>
internal sealed class FilmSettingsNotificationSession : IRuntimeSession, IRuntimeConnectionSource
{
    private readonly CancellationTokenSource lifetime = new();
    private Task<JsonElement>? pending;
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<JsonElement> Receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal JsonElement Input;
    public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimeConnection(this, new("http://127.0.0.1:1/"), lifetime.Token));
    }
    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (method != "runtime.settings.changed") throw new InvalidOperationException("Only a block notification is expected.");
        Input = JsonSerializer.SerializeToElement(parameters);
        Entered.TrySetResult();
        return pending = ReceiveAsync();
        async Task<JsonElement> ReceiveAsync()
        {
            try { return await Receipt.Task.WaitAsync(cancellationToken); }
            finally { Finished.TrySetResult(); }
        }
    }
    public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        CallAsync(method, parameters, cancellationToken);
    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Settings do not upload source.");
    public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Settings do not read images.");
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (pending is not null) try { await pending; } catch { /* The notification caller owns this failure. */ }
        lifetime.Dispose();
    }
}

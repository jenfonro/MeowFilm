using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>Explicit native document/CAS test double, not a real disk or runtime.</summary>
internal sealed class FilmNativeSettingsFixture(params string[] namespaces) : IDocumentStore
{
    private readonly HashSet<string> allowed = new(namespaces.Length == 0
        ? ["film.rules.v1", "film.tmdb.v1", "film.douban.v1", "film.preferences.v1"] : namespaces);
    private readonly Dictionary<string, byte[]> documents = [];
    private readonly object gate = new();
    private int active;
    private bool closed;
    private TaskCompletionSource? drained;
    internal DocumentStoreError? ReadError, WriteError;
    internal Func<string, CancellationToken, Task>? Reading, Writing;
    internal int Writes { get; private set; }
    internal CancellationToken WriteToken { get; private set; }

    internal void Seed(string space, JsonElement value) => SeedBytes(space, JsonSerializer.SerializeToUtf8Bytes(value));
    internal void SeedBytes(string space, byte[] bytes)
    {
        lock (gate) { Verify(space); documents[space] = bytes.ToArray(); }
    }
    internal JsonElement Value(string space)
    {
        lock (gate) { Verify(space); using var json = JsonDocument.Parse(documents[space]); return json.RootElement.Clone(); }
    }
    private void Verify(string space)
    {
        if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
        if (!allowed.Contains(space)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }
    public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default) =>
        Run(documentNamespace, cancellationToken, async () =>
        {
            if (Reading is { } read) await read(documentNamespace, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (ReadError is { } error) throw new DocumentStoreException(error);
                var bytes = documents.GetValueOrDefault(documentNamespace)?.ToArray();
                return new DocumentSnapshot(bytes, bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes));
            }
        });
    public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
        DocumentFingerprint expected, CancellationToken cancellationToken = default)
    {
        byte[] owned = data.ToArray();
        return Run(documentNamespace, cancellationToken, async () =>
        {
            lock (gate) { Writes++; WriteToken = cancellationToken; }
            if (Writing is { } write) await write(documentNamespace, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (WriteError is { } error) throw new DocumentStoreException(error);
                var before = documents.GetValueOrDefault(documentNamespace);
                if ((before is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(before)) != expected)
                    throw new DocumentStoreException(DocumentStoreError.Conflict);
                documents[documentNamespace] = owned;
                return DocumentFingerprint.FromBytes(owned);
            }
        });
    }
    private Task<T> Run<T>(string space, CancellationToken token, Func<Task<T>> operation)
    {
        lock (gate) { Verify(space); token.ThrowIfCancellationRequested(); active++; }
        return Execute();
        async Task<T> Execute()
        {
            try { return await operation().ConfigureAwait(false); }
            finally { lock (gate) { if (--active == 0) drained?.TrySetResult(); } }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closed = true;
            drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (active == 0) drained.TrySetResult();
            return new(drained.Task);
        }
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
    public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Settings do not read images.");
    public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Settings do not read images.");
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (pending is not null) try { await pending; } catch { /* The notification caller owns this failure. */ }
        lifetime.Dispose();
    }
}

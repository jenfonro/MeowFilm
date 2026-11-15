// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// A peer for the canonical business contract, not an implementation of a provider.
// Responders return the operation value; only this peer adds the runtime envelope.
internal sealed class MusicPlatformRuntimeFixture : IRuntimeClient, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly List<Call> calls = [];
    private int disposals;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal MusicPlatformRuntimeFixture()
    {
        Client = new(this);
        Client.BindSessions(Sessions);
        Catalog = new(Client, Sessions);
    }

    internal MusicPlatformClient Client { get; }
    internal MusicPlatformCatalog Catalog { get; }
    internal SessionSource Sessions { get; } = new();
    internal Func<Call, CancellationToken, Task<JsonElement>> OnRequest { get; set; } =
        (call, _) => throw new InvalidOperationException("Unexpected canonical operation: " + call.Platform + "/" + call.Operation);
    internal Func<Call, CancellationToken, Task<JsonElement>> OnSessionUpdate { get; set; } =
        (_, _) => Task.FromResult(Json(new { published = true }));
    // Script calls return their own envelope; platform calls always use the contract above.
    internal Func<Call, CancellationToken, Task<JsonElement>>? OnOtherCall { get; set; }
    // Optional transport-response probe; operation responders still return only their value.
    internal Func<Call, JsonElement, JsonElement>? OnResponse { get; set; }
    internal IReadOnlyList<Call> Calls { get { lock (gate) return calls.ToArray(); } }
    internal IReadOnlyList<Call> Requests => Calls.Where(call => call.Method == "music.platform.request").ToArray();
    internal int Disposals => Volatile.Read(ref disposals);

    internal sealed record Call(string Method, JsonElement Payload, TimeSpan? Timeout = null)
    {
        internal string Platform => Payload.GetProperty("platform").GetString()!;
        internal string Operation => Payload.GetProperty("operation").GetString()!;
        internal JsonElement Parameters => Payload.GetProperty("params");
    }

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        Respond(new(method, Json(parameters)), cancellationToken);
    public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Respond(new(method, Json(parameters), timeout), cancellationToken);
    private async Task<JsonElement> Respond(Call call, CancellationToken cancellationToken)
    {
        string method = call.Method;
        lock (gate) calls.Add(call);
        JsonElement reply;
        try
        {
            var value = method switch
            {
                "music.platform.request" => await OnRequest(call, cancellationToken).ConfigureAwait(false),
                "music.platform.session.update" => await OnSessionUpdate(call, cancellationToken).ConfigureAwait(false),
                "music.platform.cache.clear" => Json(new { cleared = true }),
                _ when OnOtherCall is not null => await OnOtherCall(call, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unexpected runtime method: " + method)
            };
            reply = method.StartsWith("music.platform.", StringComparison.Ordinal)
                ? Json(new { ok = true, contract = "music.platform.v1", value }) : value;
        }
        catch (MusicPlatformException error)
        {
            reply = Json(new { ok = false, contract = "music.platform.v1", code = error.Code });
        }
        return OnResponse is null ? reply : OnResponse(call, reply);
    }

    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Unexpected script upload");

    internal MusicAccountProtocol CreateAccountProtocol() => new(Client);
    internal MusicAccountOwner CreateAccountOwner(IDocumentStore store, TimeProvider? clock = null,
        IDocumentStore? detailsCacheStore = null)
    {
        var owner = new MusicAccountOwner(store, CreateAccountProtocol(), clock, detailsCacheStore);
        Client.BindSessions(owner);
        return owner;
    }
    internal static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    internal static JsonElement Observation(string platform = "wy", string userId = "42", long? now = null,
        bool? vip = null, string displayName = "Synthetic account")
    {
        long at = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Json(new MusicAccountDetailsObservation(new(platform, userId, displayName), at,
            new(new(vip, vip == true ? "vip" : "unknown", null), at, at + 60_000, ["standard"])));
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposals, 1);
        var closing = Client.DisposeAsync();
        Catalog.Dispose();
        await closing.ConfigureAwait(false);
    }

    internal sealed class SessionSource : IMusicPlatformSessionSource
    {
        private readonly object gate = new();
        private readonly Dictionary<string, MusicPlatformSession> sessions = [];
        internal MusicPlatformSession? Current
        {
            get { lock (gate) return sessions.Values.SingleOrDefault(); }
            set { lock (gate) { sessions.Clear(); if (value is not null) sessions.Add(value.Platform, value); } }
        }
        internal void Set(MusicPlatformSession session) { lock (gate) sessions[session.Platform] = session; }
        internal void Clear(string platform) { lock (gate) sessions.Remove(platform); }
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return ValueTask.FromResult(sessions.GetValueOrDefault(platform));
        }
        public bool IsCurrent(MusicPlatformSession session)
        {
            lock (gate) return sessions.TryGetValue(session.Platform, out var current) && ReferenceEquals(current, session);
        }
    }

    internal sealed class MemoryStore : IDocumentStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, byte[]> documents = [];
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(documents.TryGetValue(ns, out var bytes)
                ? new DocumentSnapshot(bytes.ToArray(), DocumentFingerprint.FromBytes(bytes))
                : new(null, DocumentFingerprint.Missing));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                var before = documents.TryGetValue(ns, out var bytes) ? DocumentFingerprint.FromBytes(bytes) : DocumentFingerprint.Missing;
                if (before != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                documents[ns] = data.ToArray();
                return Task.FromResult(DocumentFingerprint.FromBytes(data.Span));
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (gate) { foreach (var bytes in documents.Values) CryptographicOperations.ZeroMemory(bytes); documents.Clear(); }
            return ValueTask.CompletedTask;
        }
    }
}

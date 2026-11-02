// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Explicit synthetic client scope for isolated consumers. Durable integration
// fixtures pass their own DPAPI store and import the published account document.
internal sealed class MusicClientContextFixture : IAsyncDisposable, IDisposable
{
    internal readonly MusicPlatformClientContextOwner Owner;
    internal readonly MusicPlatformClientProtocol Protocol;
    internal readonly MusicPlatformHttp Http;
    private readonly IProtectedDocumentStore store;
    private readonly bool ownsStore, ownsHttp;
    internal MusicClientContextFixture(MusicPlatformHttp? http = null, IProtectedDocumentStore? store = null,
        bool importAccounts = false, TimeProvider? clock = null, Func<string>? registrationEntropy = null)
    {
        this.store = store ?? new MemoryStore(); ownsStore = store is null;
        Http = http ?? MusicPlatformHttp.CreateWithHandler(new NoNetwork()); ownsHttp = http is null;
        Owner = new(this.store, importAccounts ? token => AccountClientContextImport.ReadAsync(this.store, token) : null);
        Protocol = new(Http, clock, registrationEntropy);
    }
    internal MusicAccountProtocol CreateAccountProtocol(TimeSpan? timeout = null, TimeProvider? clock = null) =>
        new(Http, Owner, timeout: timeout, clock: clock ?? Protocol.Clock, clientProtocol: Protocol);
    internal MusicAccountOwner CreateAccountOwner(IProtectedDocumentStore accountStore, IMusicAccountProtocol? protocol = null,
        TimeSpan? timeout = null, TimeProvider? clock = null) =>
        new(accountStore, protocol ?? CreateAccountProtocol(timeout, clock), Owner, Protocol, clock ?? Protocol.Clock);
    internal async Task SeedQqAsync(TimeProvider? clock = null)
    {
        var now = (clock ?? Protocol.Clock).GetUtcNow();
        var device = await Owner.AcquireQqAsync();
        if (device.SessionCurrent(now)) return;
        var qimei = new MusicClientQimei((device.Qimei?.Generation ?? 0) + 1,
            "SYNTHETIC_Q16", "SYNTHETIC_Q36", now.ToUnixTimeMilliseconds());
        var session = new MusicClientDeviceSession((device.Session?.Generation ?? 0) + 1,
            "SYNTHETIC_UID", "SYNTHETIC_SID", now.ToUnixTimeMilliseconds(), qimei.Generation);
        await Owner.SaveQqAsync(device, qimei, session);
    }
    internal static async Task SeedQqAsync(IProtectedDocumentStore store, TimeProvider? clock = null)
    {
        await using var fixture = new MusicClientContextFixture(store: store, clock: clock);
        await fixture.SeedQqAsync();
    }
    internal static MusicWyClientContext Wy { get; } = new(1, new('A', 52), new('a', 64),
        new string('a', 64) + ",1700000000000", "abcdef.1700000000000.01.0");
    internal static MusicKgClientContext Kg { get; } = new(1, "0123456789abcdef0123456789abcdef");
    internal static void VerifyLocalWyCookie(HttpRequestMessage request, MusicWyClientContext client, string? accountCookie)
    {
        static Dictionary<string, string> Parse(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Split('=', 2)).ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
        var expected = Parse(accountCookie ?? "");
        expected.TryAdd("__remember_me", "true");
        expected["deviceId"] = client.DeviceId;
        expected["_ntes_nuid"] = client.Nuid;
        expected["_ntes_nnid"] = client.Nnid;
        expected["WNMCID"] = client.Wnmcid;
        if (client.Nmtid is null) expected.Remove("NMTID"); else expected["NMTID"] = client.Nmtid;
        if (!request.Headers.TryGetValues("Cookie", out var headers) || headers.Count() != 1)
            throw new InvalidOperationException("Synthetic WY cookie scope mismatch");
        var actual = Parse(headers.Single());
        if (actual.Count != expected.Count || expected.Any(part => actual.GetValueOrDefault(part.Key) != part.Value))
            throw new InvalidOperationException("Synthetic WY cookie scope mismatch");
    }
    internal static async Task<JsonObject> ReadDocument(IProtectedDocumentStore store)
    {
        var document = await store.ReadAsync(MusicPlatformClientContextOwner.DocumentNamespace);
        try { return JsonNode.Parse(document.Data!)!.AsObject(); }
        finally { if (document.Data is not null) CryptographicOperations.ZeroMemory(document.Data); }
    }
    internal static async Task ConcurrentClientWrite(IProtectedDocumentStore store)
    {
        var before = await store.ReadAsync(MusicPlatformClientContextOwner.DocumentNamespace);
        var current = before.Data is null ? MusicClientContextDocument.Create(new()) : MusicClientContextDocument.Decode(before);
        var next = current with { Wy = current.Wy with { Generation = current.Wy.Generation + 1, Nmtid = "SYNTHETIC_CONCURRENT" } };
        var bytes = next.Encode();
        try { await store.WriteAsync(MusicPlatformClientContextOwner.DocumentNamespace, bytes, before.Fingerprint); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (before.Data is not null) CryptographicOperations.ZeroMemory(before.Data);
        }
    }
    internal static async Task MutateDocument(IProtectedDocumentStore store, Action<JsonObject> change)
    {
        var before = await store.ReadAsync(MusicPlatformClientContextOwner.DocumentNamespace);
        try
        {
            var next = JsonNode.Parse(before.Data!)!.AsObject(); change(next);
            byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(next);
            try { await store.WriteAsync(MusicPlatformClientContextOwner.DocumentNamespace, bytes, before.Fingerprint); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { if (before.Data is not null) CryptographicOperations.ZeroMemory(before.Data); }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        await Owner.DisposeAsync();
        if (ownsStore) await store.DisposeAsync();
        if (ownsHttp) Http.Dispose();
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected client network request in isolated fixture");
    }
    internal sealed class MemoryStore : IProtectedDocumentStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, byte[]> documents = [];
        public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(documents.TryGetValue(ns, out var bytes)
                ? new ProtectedDocument(bytes.ToArray(), DocumentFingerprint.FromBytes(bytes))
                : new(null, DocumentFingerprint.Missing));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                var before = documents.TryGetValue(ns, out var bytes) ? DocumentFingerprint.FromBytes(bytes) : DocumentFingerprint.Missing;
                if (before != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
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

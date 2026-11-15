// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

internal sealed class MusicAccountDetailsFixture : IAsyncDisposable
{
    internal const string UserId = "9007199254740997";
    internal const string Euin = "SYNTHETIC_DETAILS_EUIN";
    internal static string Cookie(string platform) => platform switch {
        "wy" => "MUSIC_U=SYNTHETIC_WY_DETAILS_TOKEN; __csrf=SYNTHETIC_CSRF",
        "tx" => "musicid=" + UserId + "; musickey=SYNTHETIC_QQ_DETAILS_KEY; encrypt_uin=" + Euin,
        "kg" => "userid=" + UserId + "; token=SYNTHETIC_KG_DETAILS_TOKEN; KUGOU_API_MID=123456789; dfid=SYNTHETIC_DFID",
        _ => throw new InvalidOperationException()
    };
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly string Root;
    internal readonly string Platform;
    internal readonly Store Wrapper;
    internal readonly Store CacheBacking;
    internal readonly CacheDocumentStore Cache;
    internal readonly MusicPlatformRuntimeFixture Runtime;
    internal readonly MusicAccountOwner Owner;
    internal readonly List<string> Steps = [];
    internal bool HoldResponse, UnknownRights;
    internal string? FailureCode;
    internal readonly TaskCompletionSource Entered = Signal(), Release = Signal(), Cancelled = Signal();
    internal Func<JsonObject, JsonObject>? Change;
    internal MusicAccountDetailsFixture(string platform, string? root = null, TimeProvider? clock = null)
    {
        Platform = platform;
        Root = root ?? TestWorkspace.DataPath("account-details", Guid.NewGuid().ToString("N"));
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root);
        Directory.CreateDirectory(Root);
        Wrapper = new(new LocalDocumentStore(Root, [MusicAccountOwner.DocumentNamespace], protectedNamespaces: [MusicAccountOwner.DocumentNamespace]));
        string cacheRoot = Path.Combine(Root, "cache"); Directory.CreateDirectory(cacheRoot);
        CacheBacking = new(new LocalDocumentStore(cacheRoot,
            [MusicAccountOwner.DetailsCacheNamespace], protectedNamespaces: [MusicAccountOwner.DetailsCacheNamespace]));
        Cache = new(CacheBacking);
        Runtime = new();
        Runtime.OnRequest = async (call, token) => {
            if (call.Operation == "qr.cancel") return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
            if (call.Operation == "song") return MusicPlatformRuntimeFixture.Json(MusicSongFixture.Online(call.Platform, "still-borrowed"));
            if (call.Operation is not ("account.verify" or "account.profile")) throw new InvalidOperationException(call.Operation);
            lock (Steps) Steps.Add(call.Operation);
            using var registration = token.Register(() => Cancelled.TrySetResult());
            if (HoldResponse) { Entered.TrySetResult(); await Release.Task; }
            if (FailureCode is not null) throw new MusicPlatformException(FailureCode);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rights = UnknownRights
                ? new MusicAccountEntitlements(new(null, "unknown", null), now, 0, [], true, "NETWORK_ERROR", platform == "kg" ? now + 60000 : 0)
                : new MusicAccountEntitlements(new(true, platform == "wy" ? "vip" : platform == "tx" ? "huge_vip" : "concept:svip", now + 120000), now, now + 120000,
                    platform == "wy" ? ["standard", "higher", "exhigh", "lossless", "hires", "jyeffect"] : []);
            var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Json(new MusicAccountDetailsObservation(
                new(platform, UserId, "Fresh name", "https://images.invalid/avatar"), now, rights)).GetRawText())!.AsObject();
            if (call.Operation == "account.verify") value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
            return MusicPlatformRuntimeFixture.Json(Change?.Invoke(value) ?? value);
        };
        Owner = Runtime.CreateAccountOwner(Wrapper, clock, detailsCacheStore: Cache);
    }
    internal async Task Seed()
    {
        // Generate the current credential format through the real owner, then cold-read it.
        await using var runtime = new MusicPlatformRuntimeFixture();
        runtime.OnRequest = (call, _) => {
            if (call.Operation == "qr.cancel") return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { cancelled = true }));
            if (call.Operation != "account.verify") throw new InvalidOperationException(call.Operation);
            var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation(Platform, UserId, displayName: "Saved name").GetRawText())!.AsObject();
            value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(value));
        };
        await using var seed = runtime.CreateAccountOwner(Wrapper.Inner);
        var result = await seed.CallAsync("music.accounts.import", new { platform = Platform, cookie = Cookie(Platform) });
        if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Current account seed failed");
    }
    internal Task<MusicAccountDetails> Refresh(CancellationToken token = default) => Owner.RefreshDetailsAsync(Platform, 1, token);
    internal async Task<JsonObject> Document()
    {
        var doc = await Wrapper.Inner.ReadAsync(MusicAccountOwner.DocumentNamespace);
        try { return JsonNode.Parse(doc.Data!)!.AsObject(); }
        finally { CryptographicOperations.ZeroMemory(doc.Data!); }
    }
    internal async Task Mutate(Action<JsonObject> change)
    {
        var doc = await Wrapper.Inner.ReadAsync(MusicAccountOwner.DocumentNamespace);
        try {
            var root = JsonNode.Parse(doc.Data!)!.AsObject(); change(root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
            try { await Wrapper.Inner.WriteAsync(MusicAccountOwner.DocumentNamespace, bytes, doc.Fingerprint); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        } finally { CryptographicOperations.ZeroMemory(doc.Data!); }
    }
    internal async Task<JsonObject> CacheDocument()
    {
        var doc = await Cache.ReadAsync(MusicAccountOwner.DetailsCacheNamespace);
        try { return JsonNode.Parse(doc.Data!)!.AsObject(); }
        finally { CryptographicOperations.ZeroMemory(doc.Data!); }
    }
    internal async Task MutateCache(Action<JsonObject> change)
    {
        var doc = await Cache.ReadAsync(MusicAccountOwner.DetailsCacheNamespace);
        try {
            var root = JsonNode.Parse(doc.Data!)!.AsObject(); change(root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
            try { await Cache.WriteAsync(MusicAccountOwner.DetailsCacheNamespace, bytes, doc.Fingerprint); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        } finally { CryptographicOperations.ZeroMemory(doc.Data!); }
    }
    internal static JsonObject DetailsRow(JsonObject root) => root.First().Value!.AsObject();
    internal static JsonObject AccountRow(JsonObject root) => root["entries"]!.AsArray()
        .Single(r => r!["key"]!.GetValue<string>().Contains("\"platform-account\"", StringComparison.Ordinal))!["value"]!.AsObject();
    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult(); Wrapper.Release.TrySetResult(); CacheBacking.Release.TrySetResult();
        try { await Owner.DisposeAsync(); }
        finally { await Runtime.DisposeAsync(); await Cache.DisposeAsync(); await CacheBacking.DisposeAsync(); await Wrapper.DisposeAsync(); }
    }
    internal sealed class Store(LocalDocumentStore inner) : IDocumentStore
    {
        internal LocalDocumentStore Inner => inner;
        internal int Reads, Writes, Disposals;
        internal bool Hold, HoldRead, LoseReceipt, Conflict, FailRead;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        public async Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            Interlocked.Increment(ref Reads);
            if (FailRead) throw new DocumentStoreException(DocumentStoreError.Storage);
            if (HoldRead) { Entered.TrySetResult(); await Release.Task; token = default; }
            return await inner.ReadAsync(ns, token);
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
        {
            Interlocked.Increment(ref Writes);
            if (Hold) { Entered.TrySetResult(); await Release.Task; token = default; }
            if (Conflict) throw new DocumentStoreException(DocumentStoreError.Conflict);
            var result = await inner.WriteAsync(ns, data, expected, token);
            if (LoseReceipt) throw new DocumentStoreException(DocumentStoreError.Storage);
            return result;
        }
        public async ValueTask DisposeAsync() { Disposals++; await inner.DisposeAsync(); }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net;
using System.Net.Http;
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
    internal readonly MusicPlatformHttp Http;
    internal readonly MusicAccountOwner Owner;
    internal readonly MusicClientContextFixture Clients;
    internal readonly List<string> Steps = [];
    internal string? HoldStep, Fault;
    internal string FaultStep = "rights";
    internal bool HoldDisposal;
    internal readonly TaskCompletionSource Entered = Signal(), Release = Signal(), Cancelled = Signal();
    internal Func<string, JsonObject, JsonObject>? Change;
    internal MusicAccountDetailsFixture(string platform, string? root = null, TimeSpan? timeout = null)
    {
        Platform = platform;
        Root = root ?? TestWorkspace.DataPath("account-details", Guid.NewGuid().ToString("N"));
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root);
        Directory.CreateDirectory(Root);
        Wrapper = new(new LocalDocumentStore(Root, [MusicAccountOwner.DocumentNamespace, MusicPlatformClientContextOwner.DocumentNamespace], protectedNamespaces: [MusicAccountOwner.DocumentNamespace, MusicPlatformClientContextOwner.DocumentNamespace]));
        string cacheRoot = Path.Combine(Root, "cache");
        Directory.CreateDirectory(cacheRoot);
        CacheBacking = new(new LocalDocumentStore(cacheRoot,
            [MusicAccountOwner.DetailsCacheNamespace], protectedNamespaces: [MusicAccountOwner.DetailsCacheNamespace]));
        Cache = new(CacheBacking);
        Http = MusicPlatformHttp.CreateWithHandler(new Handler(this));
        Clients = new(Http, Wrapper.Inner);
        Owner = Clients.CreateAccountOwner(Wrapper, timeout: timeout, detailsCacheStore: Cache);
    }
    internal async Task Seed()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var root = new JsonObject { ["version"] = 1, ["entries"] = new JsonArray(
            new JsonObject { ["key"] = $"[\"meowfilm\",\"music\",1,\"platform-account\",\"{Platform}\"]",
                ["value"] = new JsonObject { ["version"] = 1, ["generation"] = 1, ["cookie"] = Cookie(Platform),
                    ["profile"] = new JsonObject { ["platform"] = Platform, ["userId"] = UserId, ["displayName"] = "Saved name" },
                    ["scope"] = "synthetic-details-scope", ["validatedAt"] = now } }) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
        try { await Wrapper.Inner.WriteAsync(MusicAccountOwner.DocumentNamespace, bytes, DocumentFingerprint.Missing); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
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
        try
        {
            var root = JsonNode.Parse(doc.Data!)!.AsObject(); change(root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
            try { await Wrapper.Inner.WriteAsync(MusicAccountOwner.DocumentNamespace, bytes, doc.Fingerprint); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CryptographicOperations.ZeroMemory(doc.Data!); }
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
        try
        {
            var root = JsonNode.Parse(doc.Data!)!.AsObject(); change(root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(root);
            try { await Cache.WriteAsync(MusicAccountOwner.DetailsCacheNamespace, bytes, doc.Fingerprint); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CryptographicOperations.ZeroMemory(doc.Data!); }
    }
    internal static JsonObject DetailsRow(JsonObject root) => root.First().Value!.AsObject();
    internal static JsonObject AccountRow(JsonObject root) => root["entries"]!.AsArray()
        .Single(r => r!["key"]!.GetValue<string>().Contains("\"platform-account\"", StringComparison.Ordinal))!["value"]!.AsObject();
    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult(); Wrapper.Release.TrySetResult(); CacheBacking.Release.TrySetResult();
        try { await Owner.DisposeAsync(); }
        finally { await Clients.DisposeAsync(); Http.Dispose(); await Cache.DisposeAsync(); await CacheBacking.DisposeAsync(); await Wrapper.DisposeAsync(); }
    }
    private sealed class Handler(MusicAccountDetailsFixture f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath, method = "";
            if (f.Platform == "tx")
                method = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["req_0"]!["method"]!.GetValue<string>();
            string step = method == "GetLoginUserInfo" ? "auth" : method == "GetHomepageHeader" ||
                path is "/weapi/w/nuser/account/get" or "/v3/get_my_info" ? "profile" : "rights";
            lock (f.Steps) f.Steps.Add(step);
            using var registration = token.Register(() => f.Cancelled.TrySetResult());
            if (f.HoldStep == step) { f.Entered.TrySetResult(); await f.Release.Task; }
            if (step == f.FaultStep && f.Fault == "network") throw new HttpRequestException("synthetic-private-error");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            JsonObject data;
            if (f.Platform == "wy")
                data = step == "profile"
                    ? new() { ["code"] = 200, ["account"] = new JsonObject { ["id"] = UserId }, ["profile"] = new JsonObject {
                        ["userId"] = UserId, ["nickname"] = "Fresh name", ["avatarUrl"] = "https://images.invalid/avatar" } }
                    : new() { ["code"] = 200, ["data"] = new JsonObject { ["userId"] = UserId, ["now"] = now,
                        ["associator"] = new JsonObject { ["expireTime"] = now + 120000 },
                        ["musicPackage"] = new JsonObject { ["expireTime"] = 0 }, ["redplus"] = new JsonObject { ["expireTime"] = 0 } } };
            else if (f.Platform == "tx")
            {
                var value = step == "auth" ? new JsonObject { ["code"] = 0 } : step == "profile"
                    ? new JsonObject { ["Info"] = new JsonObject { ["BaseInfo"] = new JsonObject {
                        ["EncryptedUin"] = Euin, ["Name"] = "Fresh name", ["Avatar"] = "https://images.invalid/avatar" } } }
                    : new JsonObject { ["svip"] = 0, ["identity"] = new JsonObject { ["HugeVip"] = 1,
                        ["HugeVipEnd"] = now + 120000, ["HugeVipStart"] = "2020-01-01" } };
                data = new() { ["code"] = 0, ["req_0"] = new JsonObject { ["code"] = 0, ["data"] = value } };
            }
            else data = step == "profile"
                ? new() { ["status"] = 1, ["error_code"] = 0, ["data"] = new JsonObject { ["userid"] = UserId,
                    ["nickname"] = "Fresh name", ["pic"] = "https://images.invalid/avatar" } }
                : new() { ["status"] = 1, ["error_code"] = 0, ["data"] = new JsonObject { ["userid"] = UserId,
                    ["is_vip"] = 0, ["busi_vip"] = new JsonArray(new JsonObject { ["busi_type"] = "concept",
                        ["product_type"] = "svip", ["is_vip"] = 1, ["vip_end_time"] = now + 120000 }) } };
            if (step == f.FaultStep && f.Fault == "auth")
            {
                if (f.Platform == "wy") data["code"] = 301;
                else if (f.Platform == "tx") data["code"] = 1000;
                else data["error_code"] = 20018;
            }
            data = f.Change?.Invoke(step, data) ?? data;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
            var response = new HttpResponseMessage(HttpStatusCode.OK) {
                Content = step == "rights" && f.HoldDisposal ? new StreamContent(new Cleanup(bytes, f)) : new ByteArrayContent(bytes)
            };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "SYNTHETIC_UNRELATED=discard");
            return response;
        }
    }
    private sealed class Cleanup(byte[] data, MusicAccountDetailsFixture f) : MemoryStream(data)
    {
        public override async ValueTask DisposeAsync()
        { f.Entered.TrySetResult(); await f.Release.Task; await base.DisposeAsync(); }
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

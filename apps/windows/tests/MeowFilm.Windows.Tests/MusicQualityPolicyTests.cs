using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicQualityPolicyTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("quality fixed b900 category spatial priority and request vectors", Vectors);
        yield return new("quality fresh current document returns the current default without writes or Node", Defaults);
        yield return new("quality strict writes reject all off spatial empty duplicates and invented LX", Invalid);
        yield return new("quality malformed persisted current policy fails without repair or rewrite", Corrupt);
        yield return new("quality policy CAS leaves the independently owned online settings unchanged", IndependentOwners);
        yield return new("quality cold restart retains policy and rejects stale revision", Restart);
        yield return new("quality unknown before CAS is resolved by the next current read without resend", () => Unknown(false));
        yield return new("quality unknown after CAS is resolved by the next current read without resend", () => Unknown(true));
        yield return new("quality cancellation before CAS never writes", CancelBefore);
        yield return new("quality admitted CAS cancellation drains and returns durable receipt", CancelAdmitted);
        yield return new("quality auto only same installation and exact recording declared intersection", Intersection);
        yield return new("quality no recording types or unavailable spatial never invokes LX", Unavailable);
        yield return new("quality definite miss bounded fallback but manual never changes type", Fallback);
        yield return new("quality unknown script response denial and malformed media never retry", NoRetry);
        yield return new("quality changed policy cancels and physically drains pending LX", PolicyDrain);
        yield return new("quality external cancellation and close physically drain LX", CloseDrain);
        yield return new("quality cache signature stable and refresh pins type policy recording and generation", Signatures);
        yield return new("quality missing recording metadata permits only explicit declared requests and preserves contrary evidence", RecordingEvidence);
        yield return new("quality native WY catalog missing types reaches authored LX for explicit 320k without claiming verification", WyCatalogUnknown);
    }
    private static void Check(bool value, string message = "quality assertion") => QualityFixture.Check(value, message);
    private static Task Vectors()
    {
        var p = MusicQualityPolicy.Default("auto");
        Check(MusicQualityPolicy.Categories.SequenceEqual(["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]));
        string[] input = ["jymaster", "dolby", "sky", "vivid", "jyeffect", "flac24bit", "flac", "192k", "128k"];
        string[] expected = ["master", "spatial", "spatial", "spatial", "enhanced", "hires", "lossless", "high", "standard"];
        Check(input.Select(MusicQualityPolicy.Category).SequenceEqual(expected));
        Check(MusicQualityPolicy.Priority("master", p) == 0 && MusicQualityPolicy.Priority("dolby", p) == 8 &&
            MusicQualityPolicy.Priority("sky", p) == 11 && MusicQualityPolicy.Priority("192k", p) == 40);
        p["spatialEnabled"] = new JsonArray("sky");
        Check(!MusicQualityPolicy.Enabled("dolby", p) && MusicQualityPolicy.Enabled("sky", p));
        var record = QualityFixture.O(new { types = MusicQualityPolicy.LxTypes.Select(type => new { type }) });
        var caps = QualityFixture.O(new { qualitys = MusicQualityPolicy.LxTypes });
        Check(MusicQualityPolicy.Select(p, "auto", record, caps, false)
            .SequenceEqual(["master", "flac24bit", "flac", "320k", "192k", "128k"]));
        Check(MusicQualityPolicy.Category("madeup") == "");
        return Task.CompletedTask;
    }
    private static async Task Defaults()
    {
        await using var f = new QualityFixture();
        var read = await f.Ok("music.quality.settings.get");
        Check(read.GetProperty("settings").GetProperty("request").GetString() == "320k");
        Check(f.Store.Writes == 0 && f.Runtime.Calls.Count == 0);
        await f.Ok("music.settings.save", new { ifRevision = 0, lyrics = false, target = (object?)null });
        read = await f.Ok("music.quality.settings.get");
        Check(read.GetProperty("settings").GetProperty("request").GetString() == "320k" &&
            !read.GetProperty("onlineSettings").GetProperty("lyrics").GetBoolean());
    }
    private static async Task Invalid()
    {
        await using var f = new QualityFixture();
        foreach (string kind in new[] { "off", "spatial", "duplicate", "request" })
        {
            var p = await f.SaveParameters();
            if (kind == "off") p["qualityEnabled"] = new JsonArray();
            if (kind == "spatial") { p["qualityEnabled"] = new JsonArray("spatial"); p["spatialEnabled"] = new JsonArray(); }
            if (kind == "duplicate") p["qualityOrder"]![0] = "high";
            if (kind == "request") p["request"] = "dolby";
            await f.Error("music.quality.settings.save", p, kind is "off" or "spatial" ? "QUALITY_REQUIRED" : "INVALID_REQUEST");
        }
        Check(f.Store.Writes == 0 && f.Runtime.Calls.Count == 0);
    }
    private static async Task Corrupt()
    {
        var store = new QualityFixture.DocumentStore();
        var p = MusicQualityPolicy.Default("master"); p["qualityEnabled"] = new JsonArray();
        p["spatialEnabled"] = new JsonArray(); p["qualityOrder"] = LJ.Arr(MusicQualityPolicy.Categories.Reverse());
        store.Put("music.library.v1", LJ.Obj(("version", 1), ("entries", new JsonArray(LJ.Obj(
            ("key", "[\"meowfilm\",\"music\",1,\"local\",\"native-quality-policy\"]"), ("value", p))))));
        await using var f = new QualityFixture(store);
        await f.Error("music.quality.settings.get", null, "CORRUPT_STORAGE");
        Check(store.Writes == 0, "invalid current data must not be repaired or rewritten");
    }
    private static async Task IndependentOwners()
    {
        await using var f = new QualityFixture();
        var online = await f.Ok("music.settings.save", new
            { ifRevision = 0, lyrics = false, target = (object?)null });
        var request = await f.SaveParameters("master");
        var reply = await f.Ok("music.quality.settings.save", request);
        Check(f.Store.Writes == 2 &&
            reply.GetProperty("onlineSettings").GetRawText() ==
            online.GetProperty("settings").GetRawText() &&
            reply.GetProperty("settings").GetProperty("request").GetString() == "master");
        var stale = await f.SaveParameters();
        stale["ifRevision"] = 0;
        await f.Error("music.quality.settings.save", stale, "REVISION_CONFLICT");
        Check(f.Store.Writes == 2);
        Check(f.Store.Names.All(n => n == "music.library.v1"));
    }
    private static async Task Restart()
    {
        var store = new QualityFixture.DocumentStore(); string signature;
        await using (var f = new QualityFixture(store))
        {
            var p = await f.SaveParameters("auto");
            p["spatialOrder"] = LJ.Arr(MusicQualityPolicy.Spatial.Reverse());
            signature = (await f.Ok("music.quality.settings.save", p)).GetProperty("signature").GetString()!;
            await f.Error("music.quality.settings.save", p, "REVISION_CONFLICT");
        }
        await using var next = new QualityFixture(store);
        var read = await next.Ok("music.quality.settings.get");
        Check(read.GetProperty("signature").GetString() == signature && next.Runtime.Calls.Count == 0);
    }
    private static async Task Unknown(bool after)
    {
        await using var f = new QualityFixture();
        f.Store.FaultNamespace = "music.library.v1"; f.Store.FaultAfter = after; f.Store.Fault = true;
        var p = await f.SaveParameters("auto");
        await f.Error("music.quality.settings.save", p, "COMMIT_UNKNOWN");
        int writes = f.Store.Writes;
        var read = await f.Ok("music.quality.settings.get");
        Check(read.GetProperty("settings").GetProperty("request").GetString() == (after ? "auto" : "320k"));
        Check(f.Store.Writes == writes);
        await f.Error("music.quality.settings.reconcile", null, "CAPABILITY_UNAVAILABLE");
    }
    private static async Task CancelBefore()
    {
        await using var f = new QualityFixture(); var p = await f.SaveParameters("auto");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        QualityFixture.Code(await f.App.ExecuteAsync("music.quality.settings.save", p, cts.Token), "CANCELLED");
        Check(f.Store.Writes == 0);
    }
    private static async Task CancelAdmitted()
    {
        await using var f = new QualityFixture(); var p = await f.SaveParameters("auto");
        f.Store.HoldNamespace = "music.library.v1";
        using var cts = new CancellationTokenSource();
        var saving = f.App.ExecuteAsync("music.quality.settings.save", p, cts.Token);
        await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
        var close = f.App.PrepareShutdownAsync();
        Check(!saving.IsCompleted && !close.IsCompleted);
        f.Store.Release.TrySetResult();
        Check((await saving).GetProperty("ok").GetBoolean());
        await close; Check(f.Store.Writes == 1 && !f.Store.Disposed);
        await f.App.CancelShutdownPreparationAsync();
    }
    private static async Task Intersection()
    {
        await using var f = new QualityFixture();
        string one = await f.Install(), two = await f.Install();
        await f.Ok("music.quality.settings.save", await f.SaveParameters("auto"));
        var song = QualityFixture.Song("11", ["master", "flac", "128k"]);
        var reply = await f.Resolve(two, "auto", song);
        Check(reply.GetProperty("quality").GetString() == "master" &&
            !reply.GetProperty("qualityVerified").GetBoolean() && !reply.GetProperty("playbackVerified").GetBoolean());
        Check(f.Runtime.Invoked.SequenceEqual([two]) && !f.Runtime.Invoked.Contains(one));
        Check(f.Runtime.Qualities.SequenceEqual(["master"]));
        var p = await f.SaveParameters("auto"); p["qualityOrder"] = LJ.Arr(MusicQualityPolicy.Categories.Reverse());
        await f.Ok("music.quality.settings.save", p);
        reply = await f.Resolve(two, "auto", song);
        Check(reply.GetProperty("quality").GetString() == "128k");
    }
    private static async Task Unavailable()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        await f.Error("music.online.resolve", f.Args(id, "320k", QualityFixture.Song("11", [])), "RECORDING_QUALITY_UNAVAILABLE");
        var p = await f.SaveParameters("auto"); p["qualityEnabled"] = new JsonArray("spatial");
        await f.Ok("music.quality.settings.save", p);
        await f.Error("music.online.resolve", f.Args(id, "auto"), "QUALITY_UNAVAILABLE");
        await f.Error("music.online.resolve", f.Args(id, "dolby"), "INVALID_REQUEST");
        Check(f.Runtime.Invoked.Count == 0);
    }
    private static async Task Fallback()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        f.Runtime.Misses.Add("master"); f.Runtime.Misses.Add("flac24bit");
        var result = await f.Resolve(id, "auto");
        Check(result.GetProperty("quality").GetString() == "flac" &&
            f.Runtime.Qualities.SequenceEqual(["master", "flac24bit", "flac"]));
        f.Runtime.Qualities.Clear();
        await f.Error("music.online.resolve", f.Args(id, "master"), "MEDIA_UNAVAILABLE");
        Check(f.Runtime.Qualities.SequenceEqual(["master"]));
    }
    private static async Task NoRetry()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        foreach (string code in new[] { "TIMEOUT", "PERMISSION_DENIED", "SCRIPT_EXCEPTION", "COMMIT_UNKNOWN" })
        {
            f.Runtime.Failure = code; f.Runtime.Qualities.Clear();
            await f.Error("music.online.resolve", f.Args(id, "auto"), code);
            Check(f.Runtime.Qualities.Count == 1);
        }
        f.Runtime.Failure = null; f.Runtime.Value = JsonSerializer.SerializeToNode(new { url = "https://media.invalid/a", drm = true });
        f.Runtime.Qualities.Clear();
        await f.Error("music.online.resolve", f.Args(id, "auto"), "INVALID_MEDIA_CANDIDATE");
        Check(f.Runtime.Qualities.Count == 1);
    }
    private static async Task PolicyDrain()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        f.Runtime.Hold = true;
        var request = f.App.ExecuteAsync("music.online.resolve", f.Args(id, "auto"));
        await f.Runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Ok("music.quality.settings.save", await f.SaveParameters("128k"));
        await f.Runtime.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!request.IsCompleted, "policy cancellation must await actual runtime settle");
        f.Runtime.Release.TrySetResult();
        QualityFixture.Code(await request, "CANCELLED");
        Check(f.Runtime.Qualities.Count == 1);
    }
    private static async Task CloseDrain()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        f.Runtime.Hold = true; using var cts = new CancellationTokenSource();
        var request = f.App.ExecuteAsync("music.online.resolve", f.Args(id, "auto"), cts.Token);
        await f.Runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
        await f.Runtime.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = f.App.PrepareShutdownAsync(); Check(!request.IsCompleted && !close.IsCompleted);
        f.Runtime.Release.TrySetResult(); QualityFixture.Code(await request, "CANCELLED");
        await close; Check(!f.Store.Disposed); await f.App.CancelShutdownPreparationAsync();
    }
    private static async Task Signatures()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        var first = await f.Resolve(id, "auto"); var second = await f.Resolve(id, "auto");
        Check(first.GetProperty("qualityCacheSignature").GetString() == second.GetProperty("qualityCacheSignature").GetString());
        var refresh = f.Args(id, "master");
        refresh["qualityPolicySignature"] = first.GetProperty("qualityPolicySignature").GetString();
        refresh["qualityCacheSignature"] = first.GetProperty("qualityCacheSignature").GetString();
        await f.Ok("music.online.resolve", refresh);
        int calls = f.Runtime.Qualities.Count;
        refresh["song"] = JsonSerializer.SerializeToNode(QualityFixture.Song("11", ["master", "128k"]));
        await f.Error("music.online.resolve", refresh, "QUALITY_CACHE_CHANGED");
        Check(f.Runtime.Qualities.Count == calls);
        await f.Ok("music.quality.settings.save", await f.SaveParameters("master"));
        await f.Error("music.online.resolve", refresh, "QUALITY_POLICY_CHANGED");
        Check(f.Runtime.Qualities.Count == calls);
    }
    private static Task RecordingEvidence()
    {
        var policy = MusicQualityPolicy.Default();
        var caps = QualityFixture.O(new { qualitys = MusicQualityPolicy.LxTypes });
        var missing = new JsonObject();
        void Reject(JsonObject record, string request, string code, JsonObject? declared = null)
        {
            try { MusicQualityPolicy.Select(policy, request, record, declared ?? caps, false); }
            catch (MusicLibraryException error) { Check(error.Code == code, error.Code); return; }
            throw new InvalidOperationException("Expected " + code);
        }
        foreach (string request in MusicQualityPolicy.LxTypes)
            Check(MusicQualityPolicy.Select(policy, request, missing, caps, false).SequenceEqual([request]));
        Check(missing.Count == 0, "selection must not manufacture recording metadata");
        Check(MusicQualityPolicy.Select(policy, "320k", QualityFixture.O(new { qualityKnown = false }), caps, false)
            .SequenceEqual(["320k"]));
        Reject(missing, "auto", "QUALITY_UNAVAILABLE");
        Reject(QualityFixture.O(new { qualityKnown = true }), "320k", "RECORDING_QUALITY_UNAVAILABLE");
        Reject(QualityFixture.O(new { types = Array.Empty<object>() }), "320k", "RECORDING_QUALITY_UNAVAILABLE");
        Reject(QualityFixture.O(new { qualityKnown = false, types = Array.Empty<object>() }),
            "320k", "RECORDING_QUALITY_UNAVAILABLE");
        var known = QualityFixture.O(new { types = new[] { new { type = "128k" } } });
        Reject(known, "320k", "RECORDING_QUALITY_UNAVAILABLE");
        Check(MusicQualityPolicy.Select(policy, "auto", known, caps, false).SequenceEqual(["128k"]));
        Reject(missing, "320k", "CAPABILITY_UNAVAILABLE", QualityFixture.O(new { qualitys = new[] { "128k" } }));
        policy["qualityEnabled"] = new JsonArray("standard");
        Reject(missing, "320k", "QUALITY_DISABLED");
        return Task.CompletedTask;
    }
    private static async Task WyCatalogUnknown()
    {
        var store = new QualityFixture.DocumentStore();
        var wire = new WyCatalogWire();
        await using var runtime = new NativeMusicScriptTestRuntime();
        await using var app = new MusicNativeApplication(store, runtime, wire);
        runtime.Register(app);
        async Task<JsonElement> Call(string method, object? parameters = null)
        {
            var result = await app.ExecuteAsync(method, parameters);
            Check(result.GetProperty("ok").GetBoolean(), method + ":" + result.GetRawText());
            return result;
        }
        var page = await Call("music.catalog.search", new { platform = "wy", keyword = "合成歌曲", limit = 1 });
        var song = page.GetProperty("items")[0].Clone();
        Check(wire.Calls == 1 && !MusicSongFixture.Info(song).TryGetProperty("types", out _));
        Check(!runtime.HasStarted, "ordinary native catalog must not start Node");
        const string source = """
            let calls=0;
            lx.on(lx.EVENT_NAMES.request,async ({info})=>{
              if(info.type!=='320k'||info.musicInfo.songmid!=='11'||'types' in info.musicInfo)
                throw new Error('unexpected recording or type');
              if(++calls>1) return null;
              return 'https://media.invalid/wy/11/320k';
            });
            lx.send(lx.EVENT_NAMES.inited,{status:true,sources:{
              wy:{name:'unknown quality fixture',type:'music',actions:['musicUrl'],qualitys:['320k']}
            }});
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        string token = (await Call("music.transfer.begin", new { kind = "script", basename = "wy-unknown-quality.js",
            length = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) })).GetProperty("token").GetString()!;
        await Call("music.transfer.chunk", new { token, offset = 0, data = Convert.ToBase64String(bytes) });
        string id = (await Call("music.sources.install", new { token, name = "unknown quality fixture", version = "1",
            permissions = new { storage = Array.Empty<string>() } }))
            .GetProperty("item").GetProperty("installationId").GetString()!;
        await Call("music.sources.enable", new { installationId = id });
        var parameters = new { installationId = id, platform = "wy", quality = "320k", song };
        var reply = await Call("music.online.resolve", parameters);
        Check(reply.GetProperty("url").GetString() == "https://media.invalid/wy/11/320k" &&
            reply.GetProperty("quality").GetString() == "320k" &&
            !reply.GetProperty("qualityVerified").GetBoolean() && !reply.GetProperty("playbackVerified").GetBoolean());
        Check(reply.GetProperty("qualityPolicySignature").GetString() ==
            (await Call("music.quality.settings.get")).GetProperty("signature").GetString());
        Check(reply.GetProperty("qualityCacheSignature").GetString()!.Length == 64);
        int invokes = runtime.Calls.Count(method => method == "music.script.invoke");
        Check(invokes == 1);
        QualityFixture.Code(await app.ExecuteAsync("music.online.resolve",
            new { installationId = id, platform = "wy", quality = "auto", song }), "QUALITY_UNAVAILABLE");
        Check(runtime.Calls.Count(method => method == "music.script.invoke") == invokes, "auto must not guess types");
        var known = QualityFixture.O(song);
        foreach (var row in known["sources"]!.AsArray()) row!["musicInfo"]!["qualityKnown"] = true;
        QualityFixture.Code(await app.ExecuteAsync("music.online.resolve",
            new { installationId = id, platform = "wy", quality = "320k", song = known }), "RECORDING_QUALITY_UNAVAILABLE");
        Check(runtime.Calls.Count(method => method == "music.script.invoke") == invokes,
            "qualityKnown:true must survive native normalization and reject before LX");
        QualityFixture.Code(await app.ExecuteAsync("music.online.resolve", parameters), "MEDIA_UNAVAILABLE");
        Check(runtime.Calls.Count(method => method == "music.script.invoke") == invokes + 1,
            "explicit resolver miss must not retry or downgrade");
        Check(wire.Calls == 1 && runtime.Calls.All(method => method.StartsWith("music.script.", StringComparison.Ordinal)));
    }
    private sealed class WyCatalogWire : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/eapi/cloudsearch/pc");
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":200,"result":{"songCount":1,"songs":[{"id":11,"name":"合成歌曲","dt":120000,"ar":[{"name":"合成艺人"}],"al":{"id":1,"name":"合成专辑"}}]}}""",
                    Encoding.UTF8, "application/json")
            });
        }
    }
}

internal sealed class QualityFixture : IAsyncDisposable
{
    internal DocumentStore Store { get; }
    internal ScriptRuntime Runtime { get; } = new();
    internal MusicNativeApplication App { get; }
    internal QualityFixture(DocumentStore? store = null)
    {
        Store = store ?? new(); App = new(Store, Runtime, new RejectHttp());
    }
    internal static void Check(bool value, string message = "quality assertion")
    { if (!value) throw new InvalidOperationException(message); }
    internal static JsonObject O(object value) => JsonNode.Parse(JsonSerializer.Serialize(value))!.AsObject();
    internal static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static void Code(JsonElement value, string code) =>
        Check(!value.GetProperty("ok").GetBoolean() && value.GetProperty("code").GetString() == code, code + ":" + value.GetRawText());
    internal async Task<JsonElement> Ok(string method, object? p = null)
    {
        var result = await App.ExecuteAsync(method, p);
        Check(result.GetProperty("ok").GetBoolean(), method + ":" + result.GetRawText()); return result;
    }
    internal async Task Error(string method, object? p, string code) => Code(await App.ExecuteAsync(method, p), code);
    internal async Task<JsonObject> SaveParameters(string request = "auto")
    {
        var policy = O((await Ok("music.quality.settings.get")).GetProperty("settings"));
        policy["ifRevision"] = policy["revision"]!.DeepClone(); policy.Remove("revision"); policy.Remove("schema");
        policy["request"] = request; return policy;
    }
    internal static JsonElement Song(string id = "11", string[]? types = null)
    {
        var native = O(new {
            name = "合成歌曲", singer = "合成艺人", duration = 120,
            types = (types ?? MusicQualityPolicy.LxTypes).Select(type => new { type })
        });
        return MusicSongFixture.Element(MusicSongFixture.Online("wy", id,
            "合成歌曲", "合成艺人", "", 120, native: native));
    }
    internal JsonObject Args(string id, string quality, JsonElement? song = null) =>
        O(new { installationId = id, platform = "wy", song = song ?? Song(), quality });
    internal Task<JsonElement> Resolve(string id, string request, JsonElement? song = null) =>
        Ok("music.online.resolve", Args(id, request, song));
    internal async Task<string> Install()
    {
        byte[] source = Encoding.UTF8.GetBytes("// authored synthetic script protocol fixture");
        var stage = await Ok("music.transfer.begin", new { kind = "script", basename = "quality.js",
            length = source.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(source)) });
        string token = stage.GetProperty("token").GetString()!;
        await Ok("music.transfer.chunk", new { token, offset = 0, data = Convert.ToBase64String(source) });
        var installed = await Ok("music.sources.install", new { token, name = "合成音源", version = "1",
            permissions = new { storage = Array.Empty<string>() } });
        string id = installed.GetProperty("item").GetProperty("installationId").GetString()!;
        await Ok("music.sources.enable", new { installationId = id }); return id;
    }
    public async ValueTask DisposeAsync()
    {
        Runtime.Release.TrySetResult(); Store.Release.TrySetResult();
        await App.DisposeAsync();
    }
    internal sealed class RejectHttp : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Real upstream is forbidden in quality fixtures");
    }
    internal sealed class DocumentStore : IDocumentStore
    {
        private readonly object gate = new();
        internal readonly Dictionary<string, byte[]> Data = [];
        internal readonly HashSet<string> Names = [];
        internal int Writes;
        internal bool Fault, FaultAfter, Disposed;
        internal string? FaultNamespace, HoldNamespace;
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        internal void Put(string ns, JsonObject value) { lock (gate) Data[ns] = JsonSerializer.SerializeToUtf8Bytes(value); }
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                Names.Add(ns); var bytes = Data.GetValueOrDefault(ns)?.ToArray();
                return Task.FromResult(new DocumentSnapshot(bytes, bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes)));
            }
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken token = default)
        {
            if (ns == HoldNamespace)
            { Check(!token.CanBeCanceled, "CAS must physically settle"); Entered.TrySetResult(); await Release.Task; }
            lock (gate)
            {
                Names.Add(ns); Writes++;
                bool fault = Fault && ns == FaultNamespace;
                if (fault) Fault = false;
                if (fault && !FaultAfter) throw new InvalidOperationException("synthetic before CAS receipt loss");
                var before = Data.GetValueOrDefault(ns);
                if ((before is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(before)) != expected)
                    throw new DocumentStoreException(DocumentStoreError.Conflict);
                Data[ns] = bytes.ToArray();
                if (fault) throw new InvalidOperationException("synthetic after CAS receipt loss");
                return DocumentFingerprint.FromBytes(bytes.Span);
            }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    internal sealed class ScriptRuntime : IRuntimeClient
    {
        internal readonly List<string> Calls = [], Invoked = [], Qualities = [];
        internal readonly HashSet<string> Misses = [];
        internal string? Failure;
        internal bool Hold;
        internal JsonNode? Value = JsonSerializer.SerializeToNode(new { url = "https://media.invalid/quality", headers = new { } });
        internal readonly TaskCompletionSource Entered = Gate(), Cancelled = Gate(), Release = Gate();
        private long generation;
        private readonly Dictionary<string, JsonObject> instances = [];
        private static JsonElement Reply(object value)
        {
            var o = O(value); o["ok"] = true; o["contract"] = "music.script.v2"; return JsonSerializer.SerializeToElement(o);
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(method); var p = O(parameters ?? new { });
            switch (method)
            {
                case "music.script.stage.begin": return Reply(new { token = Guid.NewGuid().ToString(), chunkBytes = 65536 });
                case "music.script.stage.chunk": return Reply(new { next = p["offset"]!.GetValue<int>() + Convert.FromBase64String(p["data"]!.GetValue<string>()).Length });
                case "music.script.stage.abort": return Reply(new { released = true });
                case "music.script.load":
                    Check(p["permissions"]!.AsObject().Select(field => field.Key).SequenceEqual(["storage"]));
                    string id = LJ.S(p["installationId"]);
                    var instance = O(new { installationId = id, revision = LJ.I(p["revision"]), generation = ++generation,
                        grant = LJ.S(p["grant"]), state = "ready",
                        capabilities = new { wy = new { actions = new[] { "musicUrl", "lyric" }, qualitys = MusicQualityPolicy.LxTypes } } });
                    instances[id] = instance; return Reply(new { instance });
                case "music.script.status": return Reply(new { documentAccess = false, instances = instances.Values.ToArray() });
                case "music.script.unload": instances.Remove(LJ.S(p["installationId"])); return Reply(new { cleanupComplete = true });
                case "music.script.invoke":
                    Invoked.Add(LJ.S(p["installationId"])); string quality = LJ.S(p["payload"]!["quality"]); Qualities.Add(quality);
                    if (Hold)
                    {
                        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
                        Entered.TrySetResult(); await Release.Task;
                    }
                    if (Failure is not null) return JsonSerializer.SerializeToElement(new { ok = false, code = Failure, contract = "music.script.v2" });
                    return Reply(new { value = Misses.Contains(quality) ? null : Value });
                default: throw new InvalidOperationException("ordinary business entered Node");
            }
        }
    }
}

using System.Net;
using System.Net.Http;
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
        yield return new("quality exact request only addresses the selected installation and recording", Intersection);
        yield return new("quality no recording types or unavailable spatial never invokes LX", Unavailable);
        yield return new("quality tier ordering skips disabled categories and adapters never downgrade", Fallback);
        yield return new("quality unknown script response denial and malformed media never retry", NoRetry);
        yield return new("quality lyric settings save preserves pending LX and accepted cache signatures", LyricsPreservePlayback);
        yield return new("quality changed policy cancels and physically drains pending LX", PolicyDrain);
        yield return new("quality external cancellation and close physically drain LX", CloseDrain);
        yield return new("quality internal 25s deadline waits for LX drain and returns timeout without retry", InternalDeadline);
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
        Check(MusicQualityPolicy.Tiers(p).SelectMany(tier => tier.Levels)
            .SequenceEqual(["master", "sky", "jyeffect", "flac24bit", "flac", "320k", "192k", "128k"]));
        Check(MusicQualityPolicy.Category("madeup") == "");
        return Task.CompletedTask;
    }
    private static async Task Defaults()
    {
        await using var f = new QualityFixture();
        var read = await f.Ok("music.quality.settings.get");
        Check(read.GetProperty("settings").GetProperty("request").GetString() == "flac");
        Check(f.Store.Writes == 0 && f.Runtime.Calls.Count == 0);
        var before = read;
        await f.Ok("music.settings.save", new { ifRevision = 0, lyrics = false });
        read = await f.Ok("music.quality.settings.get");
        Check(read.GetProperty("settings").GetProperty("request").GetString() == "flac" &&
            !read.GetProperty("onlineSettings").GetProperty("lyrics").GetBoolean());
        Check(read.GetProperty("settings").GetRawText() == before.GetProperty("settings").GetRawText() &&
            read.GetProperty("signature").GetString() == before.GetProperty("signature").GetString(),
            "saving lyrics must leave the complete quality policy and signature unchanged");
        Check(read.GetProperty("onlineSettings").GetProperty("schema").GetInt32() == 3 &&
            read.GetProperty("onlineSettings").GetProperty("revision").GetInt64() == 1);
        Check(f.Store.Writes == 1 && f.Runtime.Calls.Count == 0);
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
            { ifRevision = 0, lyrics = false });
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
        Check(f.Store.WriteNames.SetEquals(["music.library.v1"]), "settings only write the library document");
        Check(f.Store.Names.SetEquals(["music.library.v1", "music.song-matches.v1"]),
            "library access also reads the shared song facts owner without writing its document");
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
        Check(read.GetProperty("settings").GetProperty("request").GetString() == (after ? "auto" : "flac"));
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
        var reply = await f.Resolve(two, "master", QualityFixture.Song("11", ["master", "flac", "128k"]));
        Check(reply.GetProperty("quality").GetString() == "master" &&
            !reply.GetProperty("qualityVerified").GetBoolean() && !reply.GetProperty("playbackVerified").GetBoolean());
        Check(f.Runtime.Invoked.SequenceEqual([two]) && !f.Runtime.Invoked.Contains(one));
        Check(f.Runtime.Qualities.SequenceEqual(["master"]));
    }
    private static async Task Unavailable()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        await f.Error("music.online.resolve", f.Args(id, "320k", QualityFixture.Song("11", [])), "RECORDING_QUALITY_UNAVAILABLE");
        var p = await f.SaveParameters("auto"); p["qualityEnabled"] = new JsonArray("spatial");
        await f.Ok("music.quality.settings.save", p);
        await f.Error("music.online.resolve", f.Args(id, "auto"), "QUALITY_UNAVAILABLE");
        await f.Error("music.online.resolve", f.Args(id, "dolby"), "QUALITY_UNAVAILABLE");
        Check(f.Runtime.Invoked.Count == 0);
    }
    private static async Task Fallback()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        f.Runtime.Misses.Add("master");
        await f.Error("music.online.resolve", f.Args(id, "master"), "MEDIA_UNAVAILABLE");
        Check(f.Runtime.Qualities.SequenceEqual(["master"]), "single-source adapter never downgrades on its own");
        var policy = MusicQualityPolicy.Default();
        Check(MusicQualityPolicy.Tiers(policy).Select(tier => tier.Category).SequenceEqual(["lossless", "high", "standard"]));
        policy["qualityEnabled"] = new JsonArray("lossless", "standard");
        Check(MusicQualityPolicy.Tiers(policy).Select(tier => tier.Category).SequenceEqual(["lossless", "standard"]));
        Check(MusicQualityPolicy.Exact("lossless", policy)!.Levels.SequenceEqual(["flac"]));
        Check(MusicQualityPolicy.Exact("high", policy) is null);
    }
    private static async Task NoRetry()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        foreach (string code in new[] { "TIMEOUT", "PERMISSION_DENIED", "SCRIPT_EXCEPTION", "COMMIT_UNKNOWN" })
        {
            f.Runtime.Failure = code; f.Runtime.Qualities.Clear();
            await f.Error("music.online.resolve", f.Args(id, "master"), code);
            Check(f.Runtime.Qualities.Count == 1);
        }
        f.Runtime.Failure = "INVALID_MEDIA_CANDIDATE"; f.Runtime.Qualities.Clear();
        await f.Error("music.online.resolve", f.Args(id, "master"), "INVALID_MEDIA_CANDIDATE");
        Check(f.Runtime.Qualities.Count == 1);
    }
    private static async Task LyricsPreservePlayback()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        var first = await f.Resolve(id, "320k");
        var refresh = f.Args(id, "320k");
        refresh["qualityPolicySignature"] = first.GetProperty("qualityPolicySignature").GetString();
        refresh["qualityCacheSignature"] = first.GetProperty("qualityCacheSignature").GetString();
        f.Runtime.Hold = true;
        var pending = f.App.ExecuteAsync("music.online.resolve", refresh);
        await f.Runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = await f.Ok("music.settings.save", new { ifRevision = 0, lyrics = false });
        Check(!saved.GetProperty("settings").GetProperty("lyrics").GetBoolean());
        Check(!pending.IsCompleted && !f.Runtime.Cancelled.Task.IsCompleted,
            "lyric settings save must not cancel or complete an in-flight audio resolve");
        f.Runtime.Release.TrySetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.GetProperty("ok").GetBoolean(), result.GetRawText());
        Check(result.GetProperty("qualityPolicySignature").GetString() == first.GetProperty("qualityPolicySignature").GetString() &&
            result.GetProperty("qualityCacheSignature").GetString() == first.GetProperty("qualityCacheSignature").GetString(),
            "accepted policy and audio cache signatures survive a lyric setting change");
        Check(!f.Runtime.Cancelled.Task.IsCompleted && f.Runtime.Invoked.SequenceEqual([id, id]) &&
            f.Runtime.Qualities.SequenceEqual(["320k", "320k"]));
    }
    private static async Task PolicyDrain()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        f.Runtime.Hold = true;
        var request = f.App.ExecuteAsync("music.online.resolve", f.Args(id, "master"));
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
        var request = f.App.ExecuteAsync("music.online.resolve", f.Args(id, "master"), cts.Token);
        await f.Runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
        await f.Runtime.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = f.App.PrepareShutdownAsync(); Check(!request.IsCompleted && !close.IsCompleted);
        f.Runtime.Release.TrySetResult(); QualityFixture.Code(await request, "CANCELLED");
        await close; Check(!f.Store.Disposed); await f.App.CancelShutdownPreparationAsync();
    }
    private static async Task InternalDeadline()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        f.Runtime.Hold = true;
        var request = f.App.ExecuteAsync("music.online.resolve", f.Args(id, "master"));
        await f.Runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Only MusicScriptOwner's real 25s deadline cancels this request.
        await f.Runtime.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Check(!request.IsCompleted, "deadline cancellation must await actual runtime settle");
        f.Runtime.Release.TrySetResult();
        QualityFixture.Code(await request.WaitAsync(TimeSpan.FromSeconds(5)), "TIMEOUT");
        Check(f.Runtime.Invoked.SequenceEqual([id]) && f.Runtime.Qualities.SequenceEqual(["master"]),
            "an internal deadline must not retry another quality or installation");
    }
    private static async Task Signatures()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        var first = await f.Resolve(id, "master"); var second = await f.Resolve(id, "master");
        Check(first.GetProperty("qualityCacheSignature").GetString() == second.GetProperty("qualityCacheSignature").GetString());
        var refresh = f.Args(id, "master");
        refresh["qualityPolicySignature"] = first.GetProperty("qualityPolicySignature").GetString();
        refresh["qualityCacheSignature"] = first.GetProperty("qualityCacheSignature").GetString();
        await f.Ok("music.online.resolve", refresh);
        int calls = f.Runtime.Qualities.Count;
        refresh["song"] = JsonSerializer.SerializeToNode(QualityFixture.Song("11", ["master", "128k"]));
        await f.Error("music.online.resolve", refresh, "QUALITY_CACHE_CHANGED");
        Check(f.Runtime.Qualities.Count == ++calls);
        await f.Ok("music.quality.settings.save", await f.SaveParameters("master"));
        await f.Error("music.online.resolve", refresh, "QUALITY_POLICY_CHANGED");
        Check(f.Runtime.Qualities.Count == calls);
    }
    private static async Task RecordingEvidence()
    {
        await using var f = new QualityFixture(); string id = await f.Install();
        var song = QualityFixture.O(QualityFixture.Song());
        foreach (var row in song["sources"]!.AsArray()) row!["musicInfo"]!.AsObject().Remove("types");
        var missing = JsonSerializer.SerializeToElement(song);
        foreach (string request in MusicQualityPolicy.LxTypes)
            Check((await f.Resolve(id, request, missing)).GetProperty("quality").GetString() == request);
        int calls = f.Runtime.Invoked.Count;
        foreach (var row in song["sources"]!.AsArray()) row!["musicInfo"]!["qualityKnown"] = true;
        await f.Error("music.online.resolve", f.Args(id, "320k", JsonSerializer.SerializeToElement(song)), "RECORDING_QUALITY_UNAVAILABLE");
        await f.Error("music.online.resolve", f.Args(id, "320k", QualityFixture.Song("11", [])), "RECORDING_QUALITY_UNAVAILABLE");
        await f.Error("music.online.resolve", f.Args(id, "320k", QualityFixture.Song("11", ["128k"])), "RECORDING_QUALITY_UNAVAILABLE");
        Check(f.Runtime.Invoked.Count == calls, "contrary recording evidence prevents invocation");
        var policy = await f.SaveParameters(); policy["qualityEnabled"] = new JsonArray("standard");
        await f.Ok("music.quality.settings.save", policy);
        await f.Error("music.online.resolve", f.Args(id, "320k", missing), "QUALITY_DISABLED");
    }
    private static async Task WyCatalogUnknown()
    {
        var store = new QualityFixture.DocumentStore();
        var wire = new WyCatalogWire();
        await using var runtime = new NativeMusicScriptTestRuntime();
        await using var app = new MusicNativeApplication(store, runtime, wire);
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
        var installed = await app.ImportSourceAsync(bytes, "wy-unknown-quality.js");
        Check(installed.GetProperty("ok").GetBoolean(), installed.GetRawText());
        string id = installed.GetProperty("item").GetProperty("installationId").GetString()!;
        await Call("music.sources.enable", new { installationId = id });
        var parameters = new { installationId = id, platform = "wy", quality = "320k", song };
        var reply = await Call("music.online.resolve", parameters);
        Check(reply.GetProperty("url").GetString() == "https://media.invalid/wy/11/320k" &&
            reply.GetProperty("quality").GetString() == "320k" &&
            !reply.GetProperty("qualityVerified").GetBoolean() && !reply.GetProperty("playbackVerified").GetBoolean());
        Check(reply.GetProperty("qualityPolicySignature").GetString() ==
            (await Call("music.quality.settings.get")).GetProperty("signature").GetString());
        Check(reply.GetProperty("qualityCacheSignature").GetString()!.Length == 64);
        int invokes = runtime.Calls.Count(method => method == "music.script.resolve");
        Check(invokes == 1);
        QualityFixture.Code(await app.ExecuteAsync("music.online.resolve",
            new { installationId = id, platform = "wy", quality = "auto", song }), "QUALITY_UNAVAILABLE");
        Check(runtime.Calls.Count(method => method == "music.script.resolve") == invokes, "auto must not guess types");
        var known = QualityFixture.O(song);
        foreach (var row in known["sources"]!.AsArray()) row!["musicInfo"]!["qualityKnown"] = true;
        QualityFixture.Code(await app.ExecuteAsync("music.online.resolve",
            new { installationId = id, platform = "wy", quality = "320k", song = known }), "RECORDING_QUALITY_UNAVAILABLE");
        Check(runtime.Calls.Count(method => method == "music.script.resolve") == invokes,
            "qualityKnown:true must survive native normalization and reject before LX");
        QualityFixture.Code(await app.ExecuteAsync("music.online.resolve", parameters), "MEDIA_UNAVAILABLE");
        Check(runtime.Calls.Count(method => method == "music.script.resolve") == invokes + 1,
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
        var installed = await App.ImportSourceAsync(source, "quality.js");
        if (!installed.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(installed.GetRawText());
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
        internal readonly HashSet<string> WriteNames = [];
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
                Names.Add(ns); WriteNames.Add(ns); Writes++;
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
        private readonly Dictionary<string, bool> installations = [];
        private object Item(string id) => new { installationId = id, enabled = installations[id],
            name = "Quality fixture", version = "1", contentHash = new string('a', 64), executionHash = new string('a', 64),
            origin = new { kind = "file" }, state = installations[id] ? "ready" : "disabled", generation = 1,
            rollbackAvailable = false, check = (object?)null,
            sites = new { wy = new { actions = new[] { "musicUrl" }, qualitys = MusicQualityPolicy.LxTypes } } };
        internal readonly List<string> Calls = [], Invoked = [], Qualities = [];
        internal readonly HashSet<string> Misses = [];
        internal string? Failure;
        internal bool Hold;
        internal readonly TaskCompletionSource Entered = Gate(), Cancelled = Gate(), Release = Gate();
        private static JsonElement Reply(object value) => JsonSerializer.SerializeToElement(new { ok = true, contract = "music.script.v3", value });
        private readonly HashSet<(string Id, long Revision)> candidates = [];
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module != "music" || source.IsEmpty || !Guid.TryParseExact(installationId, "D", out _) || sourceRevision <= 0)
                throw new InvalidOperationException("Invalid source candidate");
            candidates.Add((installationId, sourceRevision));
            return Task.FromResult(Reply(new { installationId, sourceRevision }));
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(method); var p = O(parameters ?? new { });
            switch (method)
            {
                case "music.script.sources.add":
                    string id = LJ.S(p["installationId"]);
                    if (!candidates.Remove((id, LJ.I(p["sourceRevision"])))) throw new InvalidOperationException("Missing exact candidate");
                    installations[id] = false; return Reply(new { item = Item(id) });
                case "music.script.sources.enable":
                    installations[LJ.S(p["installationId"])] = true; return Reply(new { item = Item(LJ.S(p["installationId"])) });
                case "music.script.sources.list": return Reply(new { items = installations.Keys.Select(Item).ToArray() });
                case "music.script.resolve":
                    Invoked.Add(LJ.S(p["installationId"])); string quality = LJ.S(p["quality"]); Qualities.Add(quality);
                    if (Hold)
                    {
                        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
                        Entered.TrySetResult(); await Release.Task;
                    }
                    string? error = Failure ?? (Misses.Contains(quality) ? "MEDIA_UNAVAILABLE" : null);
                    if (error is not null) return JsonSerializer.SerializeToElement(new { ok = false, code = error, contract = "music.script.v3" });
                    return Reply(new { installationId = LJ.S(p["installationId"]), platform = LJ.S(p["platform"]),
                        quality, generation = 1, contentHash = new string('a', 64), url = "https://media.invalid/quality", headers = new { } });
                default: throw new InvalidOperationException("ordinary business entered Node: " + method);
            }
        }
    }
}

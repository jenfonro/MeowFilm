using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Synthetic wire data shared in shape with MusicFeedbackProtocolTests.QqCases().
// No fake account/context/protocol/clock is injected into the application.
internal sealed class MusicQqFeedbackIntegrationFixture : IAsyncDisposable
{
    internal const string UserId = "9007199254740997";
    internal const string Key = "SYNTHETIC_QQ_JOINT_KEY", OtherKey = "SYNTHETIC_QQ_REPLACEMENT_KEY";
    internal const string Euin = "SYNTHETIC_JOINT_EUIN", Mid = "SYNTHETICMID4321";
    internal const string Qimei = "1234567890abcdef1234567890abcdef1234";
    internal const string DeviceUid = "SYNTHETIC_ANONYMOUS_UID", DeviceSid = "SYNTHETIC_ANONYMOUS_SID";
    internal const string DeviceEntry = "[\"meowfilm\",\"music\",1,\"platform-device\",\"tx-feedback\"]";
    internal const string AccountNamespace = "music.accounts.v1";
    internal readonly string Root;
    internal readonly StoreProbe Store;
    internal readonly RefusingRuntime Runtime = new();
    internal readonly WireHandler Handler;
    internal readonly MusicNativeApplication App;
    internal MusicFeedbackController Feedback => App.Feedback;
    private string entry = "entry-1";

    internal MusicQqFeedbackIntegrationFixture(string? root = null)
    {
        Root = root ?? TestWorkspace.DataPath("qq-feedback-integration-fixtures",
            Guid.NewGuid().ToString("N"));
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root);
        Directory.CreateDirectory(Root);
        Store = new(new DpapiDocumentStore(Root,
            ["music.library.v1", "music.files.v1", AccountNamespace, "music.feedback.v1"]));
        Handler = new(Store);
        // The DEFAULT protocol factory must consume this application's real AccountOwner.
        App = new(Store, Runtime, Handler);
    }

    internal static void Check(bool condition, string diagnostic = "Joint consumer invariant")
    { if (!condition) throw new InvalidOperationException(diagnostic); }
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Pending(Task task)
    { await Task.Delay(40); Check(!task.IsCompleted, "Physical work was abandoned"); }
    internal static async Task Cancelled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancelled wait");
    }
    internal static string Cookie(string key = Key) =>
        $"musicid={UserId}; str_musicid={UserId}; musickey={key}; encrypt_uin={Euin}";

    internal async Task<JsonElement> Call(string method, object? parameters = null)
    {
        var result = await App.ExecuteAsync(method, parameters);
        Check(result.GetProperty("ok").GetBoolean(), "Native account/application command failed");
        AssertRedacted(result.GetRawText());
        return result;
    }
    internal Task<JsonElement> Login(string key = Key) =>
        Call("music.accounts.import", new { platform = "tx", cookie = Cookie(key) });
    internal async Task Enable(params string[] platforms)
    {
        var before = await Feedback.ReadSettingsAsync();
        await Feedback.SaveSettingsAsync(before.Revision, platforms);
    }
    internal async Task Ready()
    {
        await App.InitializeAsync();
        await Login(); await Enable("tx");
        Handler.Clear();
    }
    internal void Play(string id = "entry-1")
    {
        entry = id;
        Feedback.Select(new(entry, MusicSongFixture.Element(MusicSongFixture.Online(
            "tx", Mid, "合成录音", "合成歌手", "", 240))));
        Feedback.Started(entry, 0, 240);
    }
    internal Task Drain() => Feedback.DrainAsync().WaitAsync(TimeSpan.FromSeconds(8));
    internal async Task Accepted(string id = "entry-1", string? retainedError = null)
    {
        int before = Handler.Count("track");
        Play(id); await Drain();
        Check(Handler.Count("track") == before + 1 && Feedback.LastError == retainedError,
            "Default application did not accept the QQ recording");
    }
    internal async Task Listen()
    {
        // Default application has a real TimeProvider. Wait real wall time; no
        // reflection, backdating or fabricated elapsed-time injection.
        var at = Stopwatch.GetTimestamp();
        await Task.Delay(1120);
        Check(Stopwatch.GetElapsedTime(at).TotalSeconds >= 1.05, "Wall clock did not advance");
        Feedback.Observe(entry, 1.05, 240, true);
    }
    internal async Task Finish()
    { Feedback.Finish("ui"); await Drain(); }
    internal async Task<string> AccountState()
    {
        var list = await Call("music.accounts.list");
        return list.GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("platform").GetString() == "tx").GetProperty("state").GetString()!;
    }
    internal async Task<JsonObject> Device() =>
        (await Store.AccountRoot())["entries"]!.AsArray()
            .Single(row => row!["key"]!.GetValue<string>() == DeviceEntry)!["value"]!.DeepClone().AsObject();
    internal static void AssertRedacted(string value)
    {
        Check(new[] { Key, OtherKey, Euin, Qimei, DeviceUid, DeviceSid, "synthetic-vkey",
            "musickey=", "api.tencentmusic.com", "stat6.y.qq.com" }.All(secret => !value.Contains(secret, StringComparison.Ordinal)),
            "Sensitive wire context escaped into a public result");
    }
    public async ValueTask DisposeAsync()
    {
        Handler.ReleaseAll();
        try
        {
            await App.DisposeAsync();
            Check(Store.Disposals == 0 && Runtime.Calls == 0 && Runtime.Disposals == 0,
                "Application used Node or released borrowed dependencies");
            Handler.AssertHealthy();
        }
        finally { await Store.DisposeAsync(); }
    }

    internal sealed class RefusingRuntime : IRuntimeSession
    {
        internal int Calls, Disposals;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Task.FromException<JsonElement>(new InvalidOperationException("Node is forbidden")); }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }

    internal sealed class StoreProbe(DpapiDocumentStore inner) : IProtectedDocumentStore
    {
        private readonly ConcurrentDictionary<string, int> reads = new(), writes = new();
        internal int Disposals;
        internal int Reads(string ns) => reads.GetValueOrDefault(ns);
        internal int Writes(string ns) => writes.GetValueOrDefault(ns);
        public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken token = default)
        { reads.AddOrUpdate(ns, 1, (_, count) => count + 1); return inner.ReadAsync(ns, token); }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken token = default)
        {
            var result = await inner.WriteAsync(ns, data, expected, token);
            writes.AddOrUpdate(ns, 1, (_, count) => count + 1);
            return result;
        }
        internal async Task<JsonObject> AccountRoot()
        {
            var saved = await ReadAsync(AccountNamespace);
            try { return saved.Data is null ? new JsonObject { ["entries"] = new JsonArray() } : JsonNode.Parse(saved.Data)!.AsObject(); }
            finally { if (saved.Data is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
        }
        internal async Task<DocumentFingerprint> Fingerprint(string ns)
        {
            var saved = await ReadAsync(ns);
            try { return saved.Fingerprint; }
            finally { if (saved.Data is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
        }
        public async ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); await inner.DisposeAsync(); }
    }

    // A deliberately non-cooperative physical operation. Cancellation is observed
    // but completion needs an explicit release, including asynchronous stream cleanup.
    internal sealed class Barrier
    {
        internal readonly TaskCompletionSource Arrived = Signal(), Release = Signal(), CancelObserved = Signal();
        internal async Task Block(CancellationToken token)
        {
            using var registration = token.Register(() => CancelObserved.TrySetResult());
            Arrived.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
        internal Task Wait() => Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8));
        internal Task Cancelled() => CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }
    internal sealed class ResponseBarrier
    {
        internal readonly Barrier Read = new(), Cleanup = new();
        internal bool Disposed;
        internal void ReleaseAll() { Read.Release.TrySetResult(); Cleanup.Release.TrySetResult(); }
    }
    private sealed class HeldStream(byte[] bytes, ResponseBarrier point) : MemoryStream(bytes, writable: false)
    {
        private bool read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!read) { read = true; await point.Read.Block(token).ConfigureAwait(false); }
            return await base.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask DisposeAsync()
        {
            await point.Cleanup.Block(CancellationToken.None).ConfigureAwait(false);
            point.Disposed = true; await base.DisposeAsync().ConfigureAwait(false);
        }
    }
    internal sealed class WireHandler(StoreProbe store) : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly ConcurrentQueue<string> steps = new(), violations = new();
        private readonly List<Barrier> holds = [];
        private readonly List<ResponseBarrier> responses = [];
        private (string Step, Barrier Point)? next;
        private ResponseBarrier? nextResponse;
        internal readonly ConcurrentQueue<XDocument> Reports = new();
        internal string Fault = "";
        internal int Disposals;
        internal int Count(string step) => steps.Count(item => item == step);
        internal string[] Steps => steps.ToArray();
        internal void Clear() { steps.Clear(); Reports.Clear(); }
        internal Barrier Hold(string step)
        {
            lock (gate)
            {
                Check(next is null, "Only one pending fixture handler barrier");
                var point = new Barrier(); next = (step, point); holds.Add(point); return point;
            }
        }
        internal ResponseBarrier HoldReportResponse()
        {
            lock (gate)
            {
                Check(nextResponse is null, "Only one pending fixture stream barrier");
                var point = new ResponseBarrier(); nextResponse = point; responses.Add(point); return point;
            }
        }
        internal void ReleaseAll()
        {
            lock (gate)
            {
                foreach (var point in holds) point.Release.TrySetResult();
                foreach (var point in responses) point.ReleaseAll();
            }
        }
        internal void AssertHealthy() => Check(violations.IsEmpty, string.Join("; ", violations));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try
            {
                Check(Disposals == 0 && request.Method == HttpMethod.Post, "Unexpected synthetic HTTP method/lifetime");
                var uri = request.RequestUri!;
                Check(uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                    uri.Fragment.Length == 0 && uri.Query.Length == 0, "Unexpected synthetic HTTP origin");
                byte[] bytes = await request.Content!.ReadAsByteArrayAsync();
                Check(bytes.Length is > 0 and <= 65536, "Native request budget");
                JsonObject? body = null;
                string step;
                if (uri.Host == "stat6.y.qq.com" && uri.AbsolutePath == "/android/fcgi-bin/imusic_tj") step = "report";
                else
                {
                    body = JsonNode.Parse(bytes)!.AsObject();
                    if (uri.Host == "api.tencentmusic.com" && uri.AbsolutePath == "/tme/trpc/proxy") step = "qimei";
                    else
                    {
                        Check(uri.Host == "u.y.qq.com" && uri.AbsolutePath == "/cgi-bin/musicu.fcg", "Unexpected synthetic route");
                        Check(body.Count == 2 && body["comm"] is JsonObject && body["req_0"] is JsonObject, "Exact single QQ request");
                        string module = body["req_0"]!["module"]!.GetValue<string>();
                        string method = body["req_0"]!["method"]!.GetValue<string>();
                        step = (module, method) switch
                        {
                            ("music.UserInfo.userInfoServer", "GetLoginUserInfo") => "auth",
                            ("music.UnifiedHomepage.UnifiedHomepageSrv", "GetHomepageHeader") => "profile",
                            ("VipLogin.VipLoginInter", "vip_login_base") => "rights",
                            ("music.getSession.session", "GetSession") => "session",
                            ("music.trackInfo.UniformRuleCtrl", "CgiGetTrackInfo") => "track",
                            ("music.vkey.GetVkey", "UrlGetVkey") => "vkey",
                            _ => throw new InvalidOperationException("Unexpected synthetic QQ RPC")
                        };
                    }
                }
                steps.Enqueue(step);
                await ValidateWire(step, request, body, bytes);
                Barrier? point = null;
                lock (gate) if (next is { } candidate && candidate.Step == step) { point = candidate.Point; next = null; }
                if (point is not null) await point.Block(token).ConfigureAwait(false);
                if (step == "report")
                {
                    byte[] ack = Ack(Fault == "ack" ? """{"code":1}""" : """{"code":0}""");
                    ResponseBarrier? response;
                    lock (gate) { response = nextResponse; nextResponse = null; }
                    return new(HttpStatusCode.OK)
                    { Content = response is null ? new ByteArrayContent(ack) : new StreamContent(new HeldStream(ack, response)) };
                }
                if (step == "qimei")
                    return Raw(JsonSerializer.Serialize(new { data = JsonSerializer.Serialize(new
                        { data = new { q16 = "1234567890abcdef", q36 = Qimei } }) }));
                if (step == "auth") return Envelope(new JsonObject { ["code"] = 0 }, Fault == "auth" ? 1000 : 0);
                if (step == "profile") return Envelope(new JsonObject { ["Info"] = new JsonObject { ["BaseInfo"] = new JsonObject {
                    ["EncryptedUin"] = Fault == "profile" ? "SYNTHETIC_WRONG_EUIN" : Euin,
                    ["Name"] = "合成账号", ["Avatar"] = "https://y.qq.com/synthetic.png" } } });
                if (step == "rights") return Envelope(new JsonObject { ["svip"] = 0, ["identity"] = new JsonObject { ["vip"] = 0 } });
                if (step == "session") return Envelope(new JsonObject { ["session"] = new JsonObject { ["uid"] = DeviceUid, ["sid"] = DeviceSid } });
                if (step == "track") return Envelope(new JsonObject { ["tracks"] = new JsonArray(new JsonObject {
                    ["mid"] = Fault == "track" ? "OTHERRECORDING" : Mid, ["id"] = 4321, ["interval"] = 240,
                    ["singer"] = new JsonArray(new JsonObject { ["id"] = 77 }) }) });
                return Envelope(new JsonObject { ["midurlinfo"] = new JsonArray(new JsonObject {
                    ["songmid"] = Mid, ["filename"] = Filename, ["result"] = 0, ["vkey"] = "synthetic-vkey" }) });
            }
            catch (Exception error)
            {
                // Never dump request contents, cookies, XML or arbitrary exception messages.
                violations.Enqueue(error is InvalidOperationException ? "Synthetic wire invariant failed" : "Synthetic handler failed");
                throw;
            }
        }
        private static string Filename => "M500" + Mid + Mid + ".mp3";
        private async Task ValidateWire(string step, HttpRequestMessage request, JsonObject? body, byte[] bytes)
        {
            if (step is "auth" or "profile" or "rights" or "track" or "vkey")
                Check(body!["comm"]!["qq"]!.GetValue<string>() == UserId &&
                    body["comm"]!["authst"]!.GetValue<string>() is Key or OtherKey, "Exact account wire scope");
            if (step == "profile") Check(body!["req_0"]!["param"]!["uin"]!.GetValue<string>() == Euin, "Exact homepage identity");
            if (step is "qimei" or "session")
            {
                var root = await store.AccountRoot();
                var device = root["entries"]!.AsArray().Single(row => row!["key"]!.GetValue<string>() == DeviceEntry)!["value"]!;
                Check(device["generation"]!.GetValue<long>() >= 1 &&
                    !request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"), "Persisted anonymous device only");
                if (step == "session")
                {
                    Check(device["qimei"]!["q36"]!.GetValue<string>() == Qimei, "QIMEI persisted before GetSession");
                    var comm = body!["comm"]!.AsObject();
                    Check(!comm.ContainsKey("qq") && !comm.ContainsKey("authst") && !comm.ContainsKey("uid") &&
                        !comm.ContainsKey("sid") && !comm.ContainsKey("tmeLoginType"), "GetSession is not login");
                    Check(body["req_0"]!["param"]!["caller"]!.GetValue<int>() == 2 &&
                        body["req_0"]!["param"]!["uid"]!.GetValue<string>() == "", "First anonymous session recipe");
                }
            }
            if (step is "track" or "vkey" or "report")
            {
                var device = (await store.AccountRoot())["entries"]!.AsArray()
                    .Single(row => row!["key"]!.GetValue<string>() == DeviceEntry)!["value"]!;
                Check(device["generation"]!.GetValue<long>() == 3 &&
                    device["session"]!["generation"]!.GetValue<long>() == 1, "Device/session saved before feedback");
                Check(request.Headers.GetValues("User-Agent").Single() == "QQMusic 20090008(android 15)", "Fixed native QQ UA");
                var cookie = request.Headers.GetValues("Cookie").Single().Split("; ", StringSplitOptions.None);
                Check(cookie.Length == 4 && cookie[0] == "uin=" + UserId && cookie[1] == "qqmusic_uin=" + UserId &&
                    cookie[2] is "qm_keyst=" + Key or "qm_keyst=" + OtherKey &&
                    cookie[3] is "qqmusic_key=" + Key or "qqmusic_key=" + OtherKey, "Report cookie scope");
                if (step != "report")
                {
                    var comm = body!["comm"]!;
                    Check(comm["QIMEI36"]!.GetValue<string>() == Qimei && comm["uid"]!.GetValue<string>() == DeviceUid &&
                        comm["sid"]!.GetValue<string>() == DeviceSid &&
                        comm["OpenUDID"]!.GetValue<string>() == device["openUdid"]!.GetValue<string>() &&
                        comm["OpenUDID2"]!.GetValue<string>() == device["openUdid2"]!.GetValue<string>() &&
                        comm["aid"]!.GetValue<string>() == device["androidId"]!.GetValue<string>(), "Same persisted device wire");
                    var p = body["req_0"]!["param"]!;
                    Check(p["ctx"]!.GetValue<int>() == 0, "QQ ctx");
                    if (step == "track")
                        Check(p["mids"]!.ToJsonString() == JsonSerializer.Serialize(new[] { Mid }) &&
                            p["types"]!.ToJsonString() == "[0]" && p["modify_stamp"]!.ToJsonString() == "[0]" &&
                            p["client"]!.GetValue<int>() == 1, "Exact native track");
                    else Check(p["uin"]!.GetValue<string>() == UserId && p["songmid"]!.ToJsonString() == JsonSerializer.Serialize(new[] { Mid }) &&
                        p["filename"]!.ToJsonString() == JsonSerializer.Serialize(new[] { Filename }) &&
                        p["songtype"]!.ToJsonString() == "[0]", "Exact native vkey");
                }
                else
                {
                    Check(request.Content!.Headers.ContentEncoding.SequenceEqual(["gzip"]), "Report is one gzip body");
                    using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip, new UTF8Encoding(false, true));
                    string text = await reader.ReadToEndAsync();
                    Check(Encoding.UTF8.GetByteCount(text) <= 65536, "Report XML budget");
                    var xml = XDocument.Parse(text);
                    var r = xml.Root!; var item = r.Element("item")!;
                    Check(item.Attributes().Count() == 13 && item.Attribute("QQ")!.Value == UserId &&
                        item.Attribute("uid")!.Value == DeviceUid && item.Attribute("songid")!.Value == "4321" &&
                        item.Attribute("singerid")!.Value == "77" && item.Attribute("audiotime")!.Value == "240000" &&
                        item.Attribute("time")!.Value == "1" && item.Attribute("play_duration_mi")!.Value == "1000" &&
                        item.Attribute("vkey")!.Value == "synthetic-vkey" && r.Element("cid")!.Value == "228" &&
                        r.Element("QIMEI36")!.Value == Qimei &&
                        r.Element("OpenUDID")!.Value == device["openUdid"]!.GetValue<string>(), "Exact eligible report");
                    Check(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() -
                        long.Parse(item.Attribute("optime")!.Value, CultureInfo.InvariantCulture)) <= 5, "Live report timestamp");
                    Reports.Enqueue(xml); // In-memory fixture only, never diagnostics or result JSON.
                }
            }
        }
        internal static byte[] Ack(string json)
        {
            using var output = new MemoryStream();
            output.Write(new byte[5]);
            using (var zip = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zip.Write(Encoding.UTF8.GetBytes(json));
            return output.ToArray();
        }
        private static HttpResponseMessage Raw(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Envelope(JsonObject data, int code = 0) => Raw(new JsonObject
            { ["code"] = 0, ["req_0"] = new JsonObject { ["code"] = code, ["data"] = data } }.ToJsonString());
        protected override void Dispose(bool disposing) { Interlocked.Increment(ref Disposals); base.Dispose(disposing); }
    }
}

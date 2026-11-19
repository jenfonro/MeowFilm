using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real application/account/controller/DPAPI with a canonical Node peer only.
internal sealed class MusicQqFeedbackIntegrationFixture : IAsyncDisposable
{
    internal const string UserId = "9007199254740997", Key = "SYNTHETIC_QQ_JOINT_KEY", OtherKey = "SYNTHETIC_QQ_REPLACEMENT_KEY", Mid = "SYNTHETICMID4321";
    internal const string AccountNamespace = MusicAccountOwner.DocumentNamespace;
    internal readonly string Root;
    internal readonly StoreProbe Store;
    internal readonly MusicPlatformRuntimeFixture Runtime = new();
    internal readonly Peer Handler = new();
    internal readonly MusicNativeApplication App;
    internal MusicFeedbackController Feedback => App.Feedback;
    private string entry = "entry-1";
    internal MusicQqFeedbackIntegrationFixture(string? root = null) {
        Root = root ?? TestWorkspace.DataPath("qq-feedback-integration-fixtures", Guid.NewGuid().ToString("N"));
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root); Directory.CreateDirectory(Root);
        Store = new(new NativeDatabase(Root));
        Runtime.OnRequest = Handler.Respond; App = new(Store, MusicTestStores.For(Store), Runtime);
    }
    internal static void Check(bool condition, string reason = "QQ application invariant") { if (!condition) throw new InvalidOperationException(reason); }
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Pending(Task task) { await Task.Delay(40); Check(!task.IsCompleted, "Physical work was abandoned"); }
    internal static async Task Cancelled(Task task) { try { await task; } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Expected cancelled wait"); }
    internal static string Cookie(string key = Key) => $"musicid={UserId}; str_musicid={UserId}; musickey={key}; encrypt_uin=SYNTHETIC_EUIN";
    internal async Task<JsonElement> Call(string method, object? parameters = null) { var result = await App.ExecuteAsync(method, parameters); Check(result.GetProperty("ok").GetBoolean(), "Native command failed"); AssertRedacted(result.GetRawText()); return result; }
    internal Task<JsonElement> Login(string key = Key) => Call("music.accounts.import", new { platform = "tx", cookie = Cookie(key) });
    internal async Task Enable(params string[] platforms) { var before = await Feedback.ReadSettingsAsync(); await Feedback.SaveSettingsAsync(before.Revision, platforms); }
    internal async Task Ready() { await App.InitializeAsync(); await Login(); await Enable("tx"); Handler.Clear(); }
    internal void Play(string id = "entry-1") { entry = id; Feedback.Select(new(entry, MusicSongFixture.Element(MusicSongFixture.Online("tx", Mid, "合成录音", "合成歌手", "", 240)))); Feedback.Started(entry, 0, 240); }
    internal Task Drain() => Feedback.DrainAsync().WaitAsync(TimeSpan.FromSeconds(8));
    internal async Task Accepted(string id = "entry-1", string? expectedError = null) { int before = Handler.Count("feedback.prepare"); Play(id); await Drain(); Check(Handler.Count("feedback.prepare") == before + 1 && Feedback.LastError == expectedError); }
    internal async Task Listen() { var at = Stopwatch.GetTimestamp(); await Task.Delay(1120); Check(Stopwatch.GetElapsedTime(at).TotalSeconds >= 1.05); Feedback.Observe(entry, 1.05, 240, true); }
    internal async Task Finish() { Feedback.Finish("ui"); await Drain(); }
    internal async Task<string> AccountState() { var list = await Call("music.accounts.list"); return list.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("platform").GetString() == "tx").GetProperty("state").GetString()!; }
    internal static void AssertRedacted(string value) => Check(new[] { Key, OtherKey, "SYNTHETIC_EUIN", "musickey=" }.All(secret => !value.Contains(secret, StringComparison.Ordinal)));
    public async ValueTask DisposeAsync() { Handler.ReleaseAll(); try { await App.DisposeAsync(); Check(Store.Disposals == 0, "Borrowed store was released"); } finally { await Runtime.DisposeAsync(); await Store.DisposeAsync(); } }
    internal sealed class StoreProbe(NativeDatabase inner) : NativeTestDatabase(inner, owns: true)
    {
        private readonly ConcurrentDictionary<string,int> writes = new();
        internal int WritesFor(string owner) => writes.GetValueOrDefault(owner);
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession,T> write,CancellationToken token=default,
            int retainedBytes=0,Action<Action>? commit=null)
        {
            string[] changed=[];
            var result=await base.WriteAsync(db=>{
                var owners=new[]{AccountNamespace,MusicFeedbackSettingsOwner.DocumentNamespace};
                var previous=owners.ToDictionary(owner=>owner,db.Revision);
                var value=write(db);changed=owners.Where(owner=>db.Revision(owner)!=previous[owner]).ToArray();return value;
            },token,retainedBytes,commit);
            foreach(var owner in changed)writes.AddOrUpdate(owner,1,(_,count)=>count+1);
            return result;
        }
    }
    internal sealed class Barrier {
        internal readonly TaskCompletionSource Arrived = Signal(), Release = Signal(), CancelObserved = Signal();
        internal async Task Block(CancellationToken token) { using var registration = token.Register(() => CancelObserved.TrySetResult()); Arrived.TrySetResult(); await Release.Task; }
        internal Task Wait() => Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8)); internal Task Cancelled() => CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }
    internal sealed class Peer {
        private readonly object gate = new(); private readonly ConcurrentQueue<string> steps = new(); private readonly List<Barrier> holds = [];
        private (string Operation, Barrier Point)? next;
        internal string? FailureOperation, FailureCode; internal string SendStatus = "confirmed", SendErrorCode = "UPSTREAM_UNCONFIRMED";
        internal readonly ConcurrentQueue<JsonElement> Reports = new(); internal string[] Steps => steps.ToArray(); internal int Count(string op) => steps.Count(v => v == op);
        internal Barrier Hold(string operation) { lock (gate) { Check(next is null); var point = new Barrier(); next = (operation, point); holds.Add(point); return point; } }
        internal void Clear() { steps.Clear(); Reports.Clear(); }
        internal void ReleaseAll() { lock (gate) foreach (var point in holds) point.Release.TrySetResult(); }
        internal async Task<JsonElement> Respond(MusicPlatformRuntimeFixture.Call call, CancellationToken token) {
            steps.Enqueue(call.Operation); Barrier? point = null; lock (gate) if (next is { } item && item.Operation == call.Operation) { point = item.Point; next = null; }
            if (point is not null) await point.Block(token);
            if (call.Operation == FailureOperation) throw new MusicPlatformException(FailureCode ?? "AUTH_EXPIRED");
            if (call.Operation == "feedback.send") Reports.Enqueue(call.Parameters.Clone());
            if (call.Operation == "account.verify") {
                var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation("tx", UserId, vip: false).GetRawText())!.AsObject();
                value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
                return MusicPlatformRuntimeFixture.Json(value);
            }
            return call.Operation switch {
                "account.profile" => MusicPlatformRuntimeFixture.Observation("tx", UserId, vip: false),
                "feedback.prepare" => MusicPlatformRuntimeFixture.Json(new { sessionId = "synthetic-node-session" }),
                "feedback.send" => MusicPlatformRuntimeFixture.Json(new { status = SendStatus, errorCode = SendErrorCode }),
                "feedback.release" => MusicPlatformRuntimeFixture.Json(new { released = true }), _ => throw new InvalidOperationException(call.Operation) };
        }
    }
}

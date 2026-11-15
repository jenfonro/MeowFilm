// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Platforms;
namespace MeowFilm.Windows.Tests;
public static partial class MusicFeedbackProtocolTests
{
    public static IEnumerable<TestCase> Cases() => NeteaseCases();
    public static IEnumerable<TestCase> QqCases()
    {
        TestCase Q(string name, Func<Task> run) => new("feedback-qq: " + name, run);
        yield return Q("passive borrowed runtime and exact semantic prepare/send/release", async () => {
            await using var f = new QqFixture(); Equal(0, f.Runtime.Calls.Count);
            var input = QqStart(f.Attempt); await using var session = await f.Protocol.StartAsync(f.Account, input);
            f.Clock.Advance(10); await session.FinishAsync(QqEnd()); await session.FinishAsync(QqEnd());
            Equal("feedback.prepare,feedback.send,feedback.release", string.Join(',', f.Runtime.Requests.Select(call => call.Operation)));
            var prepare = f.Runtime.Requests[0].Parameters;
            Equal("synthetic", prepare.GetProperty("recording").GetProperty("id").GetString());
            var send = f.Runtime.Requests[1].Parameters; Equal("synthetic-node-session", send.GetProperty("sessionId").GetString());
            Equal(5d, send.GetProperty("listened").GetDouble()); Equal(5d, send.GetProperty("mediaListened").GetDouble());
            Equal("ui", send.GetProperty("reason").GetString());
            True(!send.TryGetProperty("cookie", out _) && !send.TryGetProperty("device", out _));
        });
        yield return Q("unused display provenance does not gate QQ registration or enter its recipe", async () => {
            await using var f = new QqFixture();
            var input = QqStart(f.Attempt) with {
                Source = new string('s', 40), SourceId = new string('i', 600),
                Algorithm = new string('a', 2100), Mode = "display-only-mode"
            };
            await using var session = await f.Protocol.StartAsync(f.Account, input);
            Equal(1, f.Runtime.Requests.Count);
            var parameters = f.Runtime.Requests[0].Parameters;
            Equal("synthetic", parameters.GetProperty("recording").GetProperty("id").GetString());
            foreach (string key in new[] { "source", "sourceId", "algorithm", "mode" })
                True(!parameters.TryGetProperty(key, out _), "Unused presentation facts are not protocol input");
            f.Clock.Advance(10);
            await session.FinishAsync(QqEnd() with { Mode = "display-only-mode" });
            Equal(1, f.Runtime.Requests.Count(call => call.Operation == "feedback.send"));
        });
        yield return Q("concurrent registration shares one exact native attempt and cancelled waiter does not own it", async () => {
            await using var f = new QqFixture(); var entered = Signal(); var release = Signal();
            f.Runtime.OnRequest = async (call, _) => { if (call.Operation == "feedback.prepare") { entered.TrySetResult(); await release.Task; } return f.Reply(call); };
            var start = QqStart(f.Attempt); var first = f.Protocol.StartAsync(f.Account, start);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = new CancellationTokenSource(); var waiter = f.Protocol.StartAsync(f.Account, start, cancel.Token); cancel.Cancel();
            await Cancelled(() => waiter); release.TrySetResult(); await using var session = await first;
            True(ReferenceEquals(session, await f.Protocol.StartAsync(f.Account, start)));
            Equal(1, f.Runtime.Requests.Count(call => call.Operation == "feedback.prepare"));
            await Error(() => f.Protocol.StartAsync(f.Account, start with { Duration = 300 }), "INSTANCE_MISMATCH");
        });
        foreach (string code in new[] { "AUTH_EXPIRED", "IDENTITY_MISMATCH", "VERIFICATION_REQUIRED", "NETWORK_ERROR", "TIMEOUT", "INVALID_RESPONSE" })
        {
            string c = code;
            yield return Q("canonical prepare failure " + c + " is terminal without send or retry", async () => {
                await using var f = new QqFixture(); f.Runtime.OnRequest = (_, _) => throw new MusicPlatformException(c);
                await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)), c);
                await Error(() => f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)), "START_UNCONFIRMED");
                Equal(1, f.Runtime.Requests.Count);
            });
        }
        yield return Q("late prepare cannot accept a replaced account and close retains physical work", async () => {
            await using var f = new QqFixture(); var entered = Signal(); var release = Signal(); var cancelled = Signal();
            f.Runtime.OnRequest = async (call, token) => { using var registration = token.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await release.Task; return f.Reply(call); };
            var work = f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Runtime.Sessions.Clear("tx"); var close = f.Protocol.DisposeAsync().AsTask();
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); True(!close.IsCompleted && !work.IsCompleted);
                release.TrySetResult(); await Error(() => work, "STALE_SESSION"); await close;
            } finally { release.TrySetResult(); }
            Equal(0, f.Runtime.Requests.Count(call => call.Operation == "feedback.send"));
        });
        foreach (bool ambiguous in new[] { false, true })
            yield return Q(ambiguous ? "controller never guesses between all current tail recordings" :
                "controller admits the unique 257th source and pins it across enrichment", async () => {
                await using var f = new QqFixture();
                await using var settings = new MusicFeedbackSettingsOwner(new Store());
                await settings.ReadAsync(); await settings.SaveAsync(0, ["tx"]);
                await using var controller = new MusicFeedbackController(settings, f.Protocol, f.Runtime.Sessions, f.Clock);
                var song = MusicSongFixture.Online("kw", "source-0", "song", "artist", "", 200);
                var sources = song["sources"]!.AsArray();
                for (int i = 1; i < 256; i++)
                    sources.Add(MusicSongFixture.Online("kw", "source-" + i, "song", "artist", "", 200)["sources"]![0]!.DeepClone());
                string recordingId = "current\nrecording/" + new string('i', 80);
                sources.Add(MusicSongFixture.Online("tx", recordingId, "song", "artist", "", 200)["sources"]![0]!.DeepClone());
                if (ambiguous)
                    sources.Add(MusicSongFixture.Online("tx", "another-current-recording", "song", "artist", "", 200)["sources"]![0]!.DeepClone());
                Equal(ambiguous ? 258 : 257, sources.Count);
                controller.Select(new("entry", MusicSongFixture.Element(song))); controller.Started("entry", 0, 200);
                await controller.DrainAsync();
                if (ambiguous) {
                    Equal(0, f.Runtime.Requests.Count); controller.Finish(); await controller.DrainAsync();
                    Equal(0, f.Runtime.Requests.Count);
                } else {
                    Equal(recordingId, f.Runtime.Requests.Single().Parameters.GetProperty("recording").GetProperty("id").GetString());
                    sources.Insert(0, MusicSongFixture.Online("tx", "later-candidate", "song", "artist", "", 200)["sources"]![0]!.DeepClone());
                    controller.UpdateSelection(new("entry", MusicSongFixture.Element(song))); await controller.DrainAsync();
                    Equal(1, f.Runtime.Requests.Count(call => call.Operation == "feedback.prepare"));
                    f.Clock.Advance(5); controller.Observe("entry", 5, 200, true); controller.Finish(); await controller.DrainAsync();
                    Equal(1, f.Runtime.Requests.Count(call => call.Operation == "feedback.send"));
                    Equal(1, f.Runtime.Requests.Count(call => call.Operation == "feedback.release"));
                }
                True(controller.LastError is null);
            });
        foreach (string status in new[] { "unknown", "rejected", "notSent" })
        {
            string s = status;
            yield return Q("concurrent terminal " + s + " receipt single-shot never retries", async () => {
                await using var f = new QqFixture(); await using var session = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)); f.Clock.Advance(10);
                f.SendStatus = s; var first = session.FinishAsync(QqEnd()); var second = session.FinishAsync(QqEnd());
                await Error(() => first, "UPSTREAM_UNCONFIRMED"); await Error(() => second, "UPSTREAM_UNCONFIRMED");
                await Error(() => session.FinishAsync(QqEnd()), "UPSTREAM_UNCONFIRMED");
                Equal(1, f.Runtime.Requests.Count(call => call.Operation == "feedback.send"));
                Equal(1, f.Runtime.Requests.Count(call => call.Operation == "feedback.release"));
            });
        }
        yield return Q("sent feedback confirmed after cancellation retains its receipt and physical release", async () => {
            await using var f = new QqFixture(); await using var session = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt)); f.Clock.Advance(10);
            var entered = Signal(); var release = Signal(); bool cancellable = true;
            f.Runtime.OnRequest = async (call, token) => { if (call.Operation == "feedback.send") { cancellable = token.CanBeCanceled; entered.TrySetResult(); await release.Task; } return f.Reply(call); };
            using var cancel = new CancellationTokenSource(); var end = session.FinishAsync(QqEnd(), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); True(!end.IsCompleted && !cancellable);
            release.TrySetResult(); await end; Equal(1, f.Runtime.Requests.Count(call => call.Operation == "feedback.send"));
        });
        yield return Q("native terminal clocks and immutable end protect real listening facts", async () => {
            await using var f = new QqFixture(); await using var session = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
            await Error(() => session.FinishAsync(QqEnd()), "INVALID_LISTEN_TIME");
            await Error(() => session.FinishAsync(QqEnd() with { Listened = 0 }), "INSTANCE_MISMATCH");
            Equal(0, f.Runtime.Requests.Count(call => call.Operation == "feedback.send"));
        });
        yield return Q("release failure is surfaced and all close waiters share physical cleanup", async () => {
            await using var f = new QqFixture(); _ = await f.Protocol.StartAsync(f.Account, QqStart(f.Attempt));
            var entered = Signal(); var release = Signal();
            f.Runtime.OnRequest = async (call, _) => { if (call.Operation == "feedback.release") { entered.TrySetResult(); await release.Task; throw new MusicPlatformException("NETWORK_ERROR"); } return f.Reply(call); };
            var first = f.Protocol.DisposeAsync().AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = f.Protocol.DisposeAsync().AsTask(); True(!first.IsCompleted && !second.IsCompleted);
            release.TrySetResult(); await Error(() => first, "NETWORK_ERROR"); await Error(() => second, "NETWORK_ERROR"); f.CloseFailure = true;
        });
        yield return Q("KG retains its existing explicit TLS safety boundary", async () => {
            await using var runtime = new MusicPlatformRuntimeFixture(); var account = new MusicPlatformSession("kg", "scope", 1, "42", "userid=42; token=SYNTHETIC");
            runtime.Sessions.Current = account; await using var protocol = new NativeMusicFeedbackProtocol(runtime.Sessions, runtime.Client);
            await Error(() => protocol.StartAsync(account, new(new(), new("kg", new string('A', 32)), 200, 0, 1, false)), "UNVERIFIED_TLS_PROTOCOL");
            Equal(0, runtime.Requests.Count);
        });
    }
    private static MusicFeedbackStart QqStart(MusicFeedbackAttempt attempt) => new(attempt, new("tx", "synthetic"), 240, 0, 1, false);
    private static MusicFeedbackEnd QqEnd() => new(5, 5, 240, 5, 1, "ui");
    private sealed class QqFixture : IAsyncDisposable
    {
        internal readonly MusicPlatformRuntimeFixture Runtime = new();
        internal readonly FactClock Clock = new();
        internal readonly MusicFeedbackAttempt Attempt = new();
        internal readonly MusicPlatformSession Account = new("tx", "synthetic-qq", 1, "42", "musicid=42; musickey=SYNTHETIC_KEY");
        internal readonly NativeMusicFeedbackProtocol Protocol;
        internal string SendStatus = "confirmed"; internal bool CloseFailure;
        internal QqFixture() { Runtime.Sessions.Current = Account; Runtime.OnRequest = (call, _) => Task.FromResult(Reply(call)); Protocol = new(Runtime.Sessions, Runtime.Client, Clock); }
        internal JsonElement Reply(MusicPlatformRuntimeFixture.Call call) => call.Operation switch {
            "feedback.prepare" => MusicPlatformRuntimeFixture.Json(new { sessionId = "synthetic-node-session" }),
            "feedback.send" => MusicPlatformRuntimeFixture.Json(new { status = SendStatus, errorCode = "UPSTREAM_UNCONFIRMED" }),
            "feedback.release" => MusicPlatformRuntimeFixture.Json(new { released = true }),
            _ => throw new InvalidOperationException(call.Operation) };
        public async ValueTask DisposeAsync() { try { await Protocol.DisposeAsync(); } catch (MusicFeedbackException) when (CloseFailure) { } finally { await Runtime.DisposeAsync(); } }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void True(bool value, string reason = "Feedback invariant") { if (!value) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Feedback value mismatch"); }
    private static async Task Error(Func<Task> work, string code)
    {
        try { await work(); }
        catch (MusicFeedbackException error) when (error.Code == code) { True(error.InnerException is null); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task Cancelled(Func<Task> work) { try { await work(); } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Expected cancellation"); }
}

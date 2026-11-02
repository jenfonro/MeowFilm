using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MeowFilm.Core.Runtime;
using MeowFilm.Media;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;
using MeowFilm.SystemMedia;
using static MeowFilm.Windows.Tests.AdaptiveMediaTests;

namespace MeowFilm.Windows.Tests;

// Real native dummy and real domain modules/owners; only upstream resolution and
// origin sockets are synthetic. No Node, external accounts or production endpoints.
public static class PlaybackRecoveryTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("native explicit empty HTTP headers retain controlled failure classification without sending account credentials", async () =>
        {
            await using var origin = WaveOrigin(); await using var native = Player(origin);
            await Reject(() => native.OpenAsync("music", "entry", origin.Uri("old.wav"), "fixture",
                new Dictionary<string, string>()), "anonymous 401");
            Check(native.LastErrorCode == PlaybackError.HttpUnauthorized &&
                origin.Requests.Count == 1 && !origin.Requests.Single().AnyCallerHeader,
                "anonymous direct stream still has typed authorization failure");
        });
        yield return Test("native authorized cache lease can continue online without losing pause position or leaking handle", async () =>
        {
            await using var origin = WaveOrigin(); await using var native = Player(origin);
            var lease = new CachedWave();
            await native.OpenReadAsync("music", "entry", lease, "fixture");
            await Decoded(native); await native.SeekAsync(18);
            await Until(() => native.PositionSeconds >= 17, "cached position");
            await native.PauseAsync();
            var point = native.CaptureContinuation();
            Check(point is not null, "domain may resolve a replacement for cached input");
            await native.ReopenAsync(point!, origin.Uri("new.wav"), origin.Headers);
            Check(lease.Disposals == 1 && !native.IsPlaying && native.PauseRequested, "released lease and retained pause");
            await native.ResumeAsync(); await Decoded(native);
            await Until(() => native.PositionSeconds >= 17, "cache to network position");
        });
        yield return Test("native failed continuation allows a fresh capability without replaying the consumed one", async () =>
        {
            await using var origin = WaveOrigin(); await using var native = Player(origin);
            await native.OpenAsync("music", "entry", origin.Uri("old.wav"), "fixture", origin.Headers);
            await Decoded(native); await native.SeekAsync(25);
            await Until(() => native.PositionSeconds >= 24, "position");
            await native.PauseAsync();
            var first = native.CaptureContinuation()!;
            origin.Route = path => path == "/bad.wav" ? new Reply([]) { Status = 401 } : new Reply(Wave(), "audio/wav");
            await Reject(() => native.ReopenAsync(first, origin.Uri("bad.wav"), origin.Headers), "failed input");
            var second = native.CaptureContinuation();
            Check(second is not null && !ReferenceEquals(first, second) && second.PositionSeconds >= 24, "fresh position authority");
            await Reject(() => native.ReopenAsync(first, origin.Uri("old.wav"), origin.Headers), "used capability");
            await native.ReopenAsync(second!, origin.Uri("new.wav"), origin.Headers);
            Check(native.PauseRequested && !native.IsPlaying, "paused across failed input");
            await native.ResumeAsync(); await Decoded(native);
            await Until(() => native.PositionSeconds >= 24, "position retained");
        });
        yield return Test("official automatic failure exhausts exact refresh then changes recording preserving queue identity", async () =>
        {
            await using var f = await MusicFixture.Create(firstFails: true, official: true);
            f.Origin.Route = path => path == "/other.wav" ? new Reply(Wave(), "audio/wav") : new Reply([]) { Status = 401 };
            await f.Play(); await Decoded(f.Native);
            Check(f.App.Resolutions.Count == 3 && f.Module.CurrentSource == "QQ 音乐", "exact then bounded cross-platform fallback");
            Check(f.App.Resolutions.Take(2).All(r => r.GetProperty("platform").GetString() == "wy") &&
                f.App.Resolutions[2].GetProperty("platform").GetString() == "tx", "failure excludes old source");
            Check(f.App.HistoryWrites == 1 && f.Runtime.Calls == 0, "one occurrence, no Node");
        });
        yield return Test("official automatic all failed sources terminate rather than retrying excluded candidates", async () =>
        {
            await using var f = await MusicFixture.Create(bothFail: true, official: true);
            await f.Play();
            Check(!f.Native.HasMedia && f.App.Resolutions.Count == 3 && f.App.HistoryWrites == 0,
                $"finite failed candidate set: media={f.Native.HasMedia} resolve={f.App.Resolutions.Count} history={f.App.HistoryWrites} status={f.Module.Status}");
        });
        yield return Test("official manual source resolves before interruption and continues paused at same position", async () =>
        {
            await using var f = await MusicFixture.Create(official: true);
            await f.Play(); await Decoded(f.Native);
            await f.Module.SeekAsync(30); await Until(() => f.Native.PositionSeconds >= 29, "manual seek");
            await f.Module.TogglePlaybackAsync();
            await f.Module.RefreshCurrentRecordingsAsync();
            var other = f.Module.CurrentRecordings.Single(row => row.Platform == "tx");
            string identity = f.Native.Identity!;
            await f.Module.SelectCurrentRecordingAsync(other);
            Check(f.Module.CurrentSource == "QQ 音乐" && f.Native.Identity == identity &&
                f.Native.PauseRequested && !f.Native.IsPlaying, "manual source retains queue and pause");
            await f.Module.TogglePlaybackAsync(); await Decoded(f.Native);
            await Until(() => f.Native.PositionSeconds >= 29, "manual continuation");
            Check(f.App.HistoryWrites == 1, "source change not a new play occurrence");
        });
        yield return Test("official rejected manual resolution leaves accepted input and source unchanged", async () =>
        {
            await using var f = await MusicFixture.Create(official: true);
            await f.Play(); await Decoded(f.Native);
            await f.Module.RefreshCurrentRecordingsAsync();
            var old = Session(f.Native);
            f.App.RejectAlternate = true;
            await f.Module.SelectCurrentRecordingAsync(f.Module.CurrentRecordings.Single(row => row.Platform == "tx"));
            Check(ReferenceEquals(old, Session(f.Native)) && f.Module.CurrentSource == "网易云音乐" &&
                f.Native.HasMedia && f.App.HistoryWrites == 1, "no interruption or relabel on rejected request");
        });
        yield return Test("official failed manual input restores last accepted recording without switching song", async () =>
        {
            await using var f = await MusicFixture.Create(official: true);
            f.Origin.Route = path => path == "/other.wav" ? new Reply([]) { Status = 401 } : new Reply(Wave(), "audio/wav");
            await f.Play(); await Decoded(f.Native);
            await f.Module.SeekAsync(20); await Until(() => f.Native.PositionSeconds >= 19, "rollback seek");
            await f.Module.TogglePlaybackAsync();
            await f.Module.RefreshCurrentRecordingsAsync();
            await f.Module.SelectCurrentRecordingAsync(f.Module.CurrentRecordings.Single(row => row.Platform == "tx"));
            Check(f.Module.CurrentSource == "网易云音乐" && f.Native.HasMedia && f.Native.PauseRequested &&
                f.App.Resolutions.Count == 3, "failed manual URL restored exact prior recording once");
            await f.Module.TogglePlaybackAsync(); await Decoded(f.Native);
            await Until(() => f.Native.PositionSeconds >= 19, "rollback position");
        });
        yield return Test("official manual candidate Started before reopen settles remains unpublished until commit", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1 &&
                f.App.FeedbackProbe.Starts == 1, "initial controlled playback");
            await f.Module.RefreshCurrentRecordingsAsync();
            var plan = f.Output.Plan(hold: true, startBeforeCompletion: true);
            var choose = f.Module.SelectCurrentRecordingAsync(
                f.Module.CurrentRecordings.Single(row => row.Platform == "tx"));
            await plan.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Module.CurrentSource == "网易云音乐" && f.App.ConfirmWrites == 1 &&
                f.App.HistoryWrites == 1 && f.App.FeedbackProbe.Starts == 1 &&
                f.App.FeedbackProbe.Finishes.Count == 0,
                "pending native Started cannot publish source confirmation history or feedback");
            plan.Release.TrySetResult();
            await choose.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.App.ConfirmWrites == 2 && f.App.FeedbackProbe.Starts == 2,
                "committed controlled source");
            Check(f.Module.CurrentSource == "QQ 音乐" && f.App.HistoryWrites == 1 &&
                f.App.FeedbackProbe.Finishes.SequenceEqual(new[] { "source-change" }) &&
                f.Output.Reopens == 1, "commit publishes one source transition without a new occurrence");
        });
        yield return Test("official manual candidate commits before late Started but confirms only when playback starts", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1,
                "initial late-start fixture");
            await f.Module.RefreshCurrentRecordingsAsync();
            f.Output.Plan();
            await f.Module.SelectCurrentRecordingAsync(
                f.Module.CurrentRecordings.Single(row => row.Platform == "tx"));
            Check(f.Module.CurrentSource == "QQ 音乐" && f.App.ConfirmWrites == 1 &&
                f.App.HistoryWrites == 1 && f.App.FeedbackProbe.Starts == 1 &&
                f.App.FeedbackProbe.Finishes.SequenceEqual(new[] { "source-change" }),
                "accepted paused/pre-start source is visible but unconfirmed");
            f.Output.Start();
            await Until(() => f.App.ConfirmWrites == 2 && f.App.FeedbackProbe.Starts == 2,
                "late Started acceptance");
            Check(f.App.HistoryWrites == 1, "late Started is not another queue occurrence");
        });
        yield return Test("official failed manual replacement restores the accepted input exactly once", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1,
                "initial rollback fixture");
            await f.Module.RefreshCurrentRecordingsAsync();
            f.Output.Plan(startBeforeCompletion: true, failure: PlaybackError.HttpUnauthorized);
            f.Output.Plan(startBeforeCompletion: true);
            await f.Module.SelectCurrentRecordingAsync(
                f.Module.CurrentRecordings.Single(row => row.Platform == "tx"));
            await Until(() => f.Output.Reopens == 2 && f.App.ConfirmWrites == 2,
                "single accepted-source restore");
            Check(f.Module.CurrentSource == "网易云音乐" && f.Output.HasMedia &&
                f.App.Resolutions.Count == 3 && f.App.HistoryWrites == 1 &&
                f.App.FeedbackProbe.Starts == 1 && f.App.FeedbackProbe.Finishes.Count == 0,
                "failed candidate never becomes a feedback or history identity");
        });
        yield return Test("official automatic fallback keeps the old source published until cross-recording commit", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1,
                "initial automatic fallback fixture");
            var exact = f.Output.Plan(failure: PlaybackError.HttpUnauthorized);
            var alternate = f.Output.Plan(hold: true, startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await exact.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await alternate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Module.CurrentSource == "网易云音乐" && f.App.ConfirmWrites == 1 &&
                f.App.HistoryWrites == 1 && f.App.FeedbackProbe.Starts == 1,
                "failed exact refresh and pending alternate remain unpublished");
            alternate.Release.TrySetResult();
            await Until(() => f.Module.CurrentSource == "QQ 音乐" &&
                f.App.ConfirmWrites == 2 && f.App.FeedbackProbe.Starts == 2,
                "automatic alternate commit");
            Check(f.Output.Reopens == 2 && f.App.Resolutions.Count == 3 &&
                f.App.HistoryWrites == 1 &&
                f.App.FeedbackProbe.Finishes.SequenceEqual(new[] { "source-change" }),
                "bounded fallback publishes once");
        });
        yield return Test("official pending platform revocation cancels candidate without replacing the accepted source", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1,
                "initial revocation fixture");
            await f.Module.RefreshCurrentRecordingsAsync();
            var plan = f.Output.Plan(hold: true);
            var choose = f.Module.SelectCurrentRecordingAsync(
                f.Module.CurrentRecordings.Single(row => row.Platform == "tx"));
            await plan.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.App.InvalidateOfficial("tx");
            await choose.WaitAsync(TimeSpan.FromSeconds(5));
            Check(plan.Token.IsCancellationRequested && f.Output.Reopens == 1 &&
                f.Output.CandidateStarts == 0 && f.Output.HasMedia &&
                f.Module.CurrentSource == "网易云音乐" && f.App.ConfirmWrites == 1 &&
                f.App.HistoryWrites == 1 && f.App.FeedbackProbe.Starts == 1 &&
                f.App.FeedbackProbe.Finishes.Count == 0,
                "revoked pending account cannot start publish or force an unnecessary old-source reopen");
        });
        yield return Test("official next-track preload reauthorizes and consumes prepared online bytes", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true);
            await f.PlayPair();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1,
                "initial preload fixture");
            f.Output.SetPosition(85);
            var prepared = await f.WaitForPrepared();
            Check(await prepared.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null,
                "validated provisional bytes");
            int transfers = f.Origin.Requests.Count, networkOpens = f.Output.NetworkOpens;
            await f.Module.StepAsync(true);
            await Until(() => f.Output.ReadOpens == 1 && f.App.Authorizations == 1 &&
                f.App.HistoryWrites == 2 && f.App.ConfirmWrites == 2,
                "prepared next occurrence");
            await prepared.Work.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.NetworkOpens == networkOpens && f.Origin.Requests.Count == transfers &&
                f.Module.CurrentTitle == "下一首" && f.App.Resolutions.Count == 3,
                $"next occurrence reused bytes after a current exact authorization: opens={f.Output.NetworkOpens}/{networkOpens} " +
                $"transfers={f.Origin.Requests.Count}/{transfers} title={f.Module.CurrentTitle} resolves={f.App.Resolutions.Count}");
            await using var persisted = await f.Cache!.OpenAsync(
                "fixture", f.NextSongId!, new string('a', 64), default);
            Check(persisted is not null, "real Started publishes the prepared file");
        });
        foreach (var action in new[] { "queue", "account", "quality" })
            yield return Test("official preload cancellation follows " + action + " authority", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(playbackCache: true);
                f.HoldValidation = true;
                await f.PlayPair(action == "account" ? "tx" : "wy");
                await Until(() => f.App.HistoryWrites == 1, "initial preload cancellation fixture");
                f.Output.SetPosition(85);
                await f.ValidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var prepared = f.Prepared ?? throw new InvalidOperationException("missing prepared owner");
                if (action == "queue")
                    await f.Module.ChangeModeAsync(2);
                else if (action == "account")
                    f.App.InvalidateOfficial("tx");
                else
                {
                    await f.Module.ActivateQualitySettingsAsync();
                    await f.Module.SetPlaybackQualityRequestAsync("auto");
                }
                await Until(() => f.ValidationToken.IsCancellationRequested,
                    action + " cancels physical prepared owner");
                f.ValidationRelease.TrySetResult();
                await prepared.Work.WaitAsync(TimeSpan.FromSeconds(5));
                await using var absent = await f.Cache!.OpenAsync(
                    "fixture", f.NextSongId!, new string('a', 64), default);
                Check(absent is null && !Directory.EnumerateFiles(f.CacheRoot!, "*.audio").Any(),
                    action + " cannot publish stale prepared bytes");
            });
        yield return Test("official shutdown waits for a noncooperative physical preload to settle", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true);
            f.HoldValidation = true;
            await f.PlayPair();
            await Until(() => f.App.HistoryWrites == 1, "initial preload shutdown fixture");
            f.Output.SetPosition(85);
            await f.ValidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var prepare = f.Module.PrepareShutdownAsync();
            await Until(() => f.ValidationToken.IsCancellationRequested, "shutdown preload cancellation");
            Check(!prepare.IsCompleted, "shutdown cannot detach noncooperative cache validation");
            f.ValidationRelease.TrySetResult();
            await prepare.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!Directory.EnumerateFiles(f.CacheRoot!, "*.audio").Any(),
                "cancelled shutdown preload leaves no playable bytes");
        });
        foreach (var action in new[] { "stop", "close" })
            yield return Test("official pending manual resolution cannot revive after " + action, async () =>
            {
                await using var f = await MusicFixture.Create(official: true);
                await f.Play(); await Decoded(f.Native);
                await f.Module.RefreshCurrentRecordingsAsync();
                var acceptedSession = Session(f.Native)!;
                var acceptedAuthority = f.Native.CaptureContinuation()!;
                f.App.HoldRefresh = true;
                var choose = f.Module.SelectCurrentRecordingAsync(f.Module.CurrentRecordings.Single(row => row.Platform == "tx"));
                await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(((System.Windows.Controls.Button)((MusicPlayerView)f.Module.PlayerView).FindName("PlayButton")).IsEnabled,
                    "real pause button stays enabled during source resolution");
                Task? close = null;
                if (action == "stop") await f.Native.StopAsync(); else close = f.Module.PrepareShutdownAsync();
                Check(acceptedAuthority.Revoked.IsCancellationRequested, "accepted input authority revoked before late response");
                if (close is not null)
                {
                    await Task.Yield();
                    Check(!close.IsCompleted && !choose.IsCompleted, "close preparation still owns the held resolution");
                }
                Check(!f.Origin.Requests.Any(r => r.Path == "/other.wav"), "resolution did not preempt the accepted input");
                f.App.ReleaseRefresh.TrySetResult();
                await choose.WaitAsync(TimeSpan.FromSeconds(5));
                if (close is not null) await close.WaitAsync(TimeSpan.FromSeconds(5));
                // Preparation drains capability work, not the App-owned player.
                // Token revocation posts native retirement to the dispatcher; wait
                // for its observable completion without issuing Stop/Dispose here.
                await Until(() => !f.Native.HasMedia, "cancelled accepted input retirement");
                Check(!f.Native.HasMedia && !f.Origin.Requests.Any(r => r.Path == "/other.wav"), "late selection discarded");
                Check(((MediaPlayer)Field(acceptedSession, "Player")!).NativeReference == IntPtr.Zero,
                    "accepted native player physically released");
                Check(f.App.Resolutions.Count == 2 && f.App.HistoryWrites == 1 && f.Runtime.Calls == 0,
                    "late selection cannot retry, record another play or invoke Node");
            });
        yield return Test("native paused continuation preserves position and releases old player", async () =>
        {
            await using var origin = WaveOrigin();
            await using var native = Player(origin);
            await native.OpenAsync("music", "entry", origin.Uri("old.wav"), "fixture", origin.Headers);
            await Decoded(native);
            await native.SeekAsync(30); await Until(() => native.PositionSeconds >= 29, "old seek");
            var old = Session(native)!;
            var point = native.CaptureContinuation()!;
            await native.PauseAsync();
            await native.ReopenAsync(point, origin.Uri("new.wav"), origin.Headers);
            Check(native.OwnerId == "music" && native.Identity == "entry", "identity retained");
            Check(native.PauseRequested && !native.IsPlaying, "no autoplay while paused");
            Check(point.ReopenedScope?.Generation > point.Scope.Generation, "new fenced native generation");
            Check(((MediaPlayer)Field(old, "Player")!).NativeReference == IntPtr.Zero, "old player really released");
            await native.ResumeAsync(); await Decoded(native);
            await Until(() => native.PositionSeconds >= 29, "continued position");
            await Reject(() => native.ReopenAsync(point, origin.Uri("new.wav"), origin.Headers), "single-use point");
        });
        foreach (var action in new[] { "stop", "replace", "revoke", "dispose" })
            yield return Test("native stale continuation after " + action, async () =>
            {
                await using var origin = WaveOrigin(); await using var native = Player(origin);
                using var access = new CancellationTokenSource();
                await native.OpenAsync("music", "old", origin.Uri("old.wav"), "fixture", origin.Headers, access.Token);
                await Decoded(native);
                var point = native.CaptureContinuation()!;
                switch (action)
                {
                    case "stop": await native.StopAsync(); break;
                    case "replace": await native.OpenAsync("film", "new", origin.Uri("new.wav"), "fixture", origin.Headers); break;
                    case "revoke": access.Cancel(); await Until(() => !native.HasMedia, "revoked player"); break;
                    case "dispose": await native.DisposeAsync(); break;
                }
                var count = origin.Requests.Count;
                await Reject(() => native.ReopenAsync(point, origin.Uri("new.wav"), origin.Headers), "revoked point");
                Check(point.Revoked.IsCancellationRequested && origin.Requests.Count == count, "no stale I/O");
                if (action == "replace") Check(native.OwnerId == "film" && native.Identity == "new", "new owner untouched");
            });
        yield return Test("native pending open pause fence and stop truly drain", async () =>
        {
            await using var origin = WaveOrigin(); await using var native = Player(origin);
            await native.OpenAsync("music", "entry", origin.Uri("old.wav"), "fixture", origin.Headers);
            await Decoded(native);
            var point = native.CaptureContinuation()!;
            origin.Route = _ => new Reply(Wave(), "audio/wav") { BlockBody = true };
            var reopen = native.ReopenAsync(point, origin.Uri("new.wav"), origin.Headers);
            await Until(() => origin.Blocked > 0, "blocked preflight");
            var pause = native.PauseAsync();
            Check(native.PauseRequested, "pause intent synchronous ahead of native gate");
            await native.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
            await Reject(() => reopen, "stopped preflight");
            await pause;
            Check(!native.HasMedia, "no revived input");
        });
        yield return Test("system Pause bypasses held Play outer gate and fences queued Play", async () =>
        {
            var player = new HeldSystemPlayer(); var backend = new Backend();
            await using var system = new SystemMediaSession(player, backend, [new("music")]);
            backend.Send(SystemPlaybackCommand.Play);
            await player.Entered.Task;
            backend.Send(SystemPlaybackCommand.Play);
            backend.Send(SystemPlaybackCommand.Pause);
            await Until(() => player.Paused, "pause reaches native-facing port without Play completion");
            player.Release.TrySetResult(); await Task.Delay(100);
            Check(player.Plays == 1, "queued pre-Pause Play discarded");
            backend.Send(SystemPlaybackCommand.Play); await Until(() => player.Plays == 2, "explicit later Play");
            Check(system.LastErrorCode is null, "system command result");
        });
        yield return Test("music initial signed URL failure re-resolves one exact target and decodes", async () =>
        {
            await using var fixture = await MusicFixture.Create(firstFails: true);
            await fixture.Play();
            await Decoded(fixture.Native);
            Check(fixture.App.Resolutions.Count == 2, "one re-resolution");
            SameMusicTarget(fixture.App.Resolutions);
            Check(fixture.App.HistoryWrites == 1, "one real Started history");
            Check(fixture.Origin.Requests.Any(row => row.Path == "/new.wav" && row.Authorized), "fresh headers native direct");
            Check(fixture.Runtime.Calls == 0, "ordinary domain and media never Node");
        });
        yield return Test("music repeated HTTP failure stops after one re-resolution", async () =>
        {
            await using var fixture = await MusicFixture.Create(firstFails: true, bothFail: true);
            await fixture.Play(); await Task.Delay(200);
            Check(fixture.App.Resolutions.Count == 2 && !fixture.Native.HasMedia, "finite failure");
            Check(fixture.App.HistoryWrites == 0, "failed opens not recorded");
        });
        yield return Test("music renewed expiry stops when exact refresh authority is exhausted", async () =>
        {
            await using var f = await MusicFixture.Create(expiry: true);
            f.App.RepeatExpiry = true;
            await f.Play(); await Decoded(f.Native);
            await Until(() => f.App.Resolutions.Count == 2 && f.Origin.Requests.Any(r => r.Path == "/new.wav"), "exact renewal");
            await Until(() => !f.Native.HasMedia, "renewed deadline still enforced");
            Check(f.App.Resolutions.Count == 2, "no unbounded refresh loop or extension of license");
        });
        yield return Test("music decoder or unknown native failure never refreshes URL", async () =>
        {
            await using var fixture = await MusicFixture.Create();
            var bad = Wave(); bad[20] = 0xff; bad[21] = 0x7f;
            fixture.Origin.Route = _ => new Reply(bad, "audio/wav");
            await fixture.Play(); await Until(() => fixture.Native.LastErrorCode is not null, "actual native codec rejection");
            Check(fixture.Native.LastErrorCode is PlaybackError.PlaybackFailed or PlaybackError.OpenFailed,
                "opaque native failure remains separate from HTTP");
            Check(fixture.App.Resolutions.Count == 1 && fixture.App.HistoryWrites <= 1, "not a URL retry");
        });
        yield return Test("music format policy rejection does not invoke resolver again", async () =>
        {
            await using var fixture = await MusicFixture.Create();
            fixture.Origin.Route = _ => new Reply(Encoding.UTF8.GetBytes("#EXTM3U\nfile:///forbidden\n"), "audio/wav");
            await fixture.Play();
            Check(fixture.App.Resolutions.Count == 1 && !fixture.Native.HasMedia, "no safety bypass retry");
        });
        yield return Test("music expiry held resolver preserves UI and system pause and history", async () =>
        {
            await using var fixture = await MusicFixture.Create(expiry: true);
            fixture.App.HoldRefresh = true;
            await fixture.Play(); await Decoded(fixture.Native);
            await fixture.Module.SeekAsync(30); await Until(() => fixture.Native.PositionSeconds >= 29, "music seek");
            await fixture.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await fixture.Module.TogglePlaybackAsync();
            Check(fixture.Native.PauseRequested, "UI pause admitted while resolver awaits");
            var backend = new Backend();
            await using var system = new SystemMediaSession(fixture.Native, backend, [new("music")]);
            backend.Send(SystemPlaybackCommand.Pause); await Task.Delay(60);
            fixture.App.ReleaseRefresh.TrySetResult();
            await Until(() => fixture.Origin.Requests.Any(row => row.Path == "/new.wav") && !fixture.Native.ContinuationPending,
                "music paused reopen");
            Check(!fixture.Native.IsPlaying && fixture.Native.PauseRequested, "recovery did not Play");
            await fixture.Module.TogglePlaybackAsync(); await Decoded(fixture.Native);
            await Until(() => fixture.Native.PositionSeconds >= 29, "music recovered seek position");
            Check(fixture.App.HistoryWrites == 1, "history not duplicated on replacement Playing");
            SameMusicTarget(fixture.App.Resolutions);
        });
        foreach (var action in new[] { "stop", "replace", "cancel", "close" })
            yield return Test("music held recovery cannot revive after " + action, async () =>
            {
                await using var fixture = await MusicFixture.Create(expiry: true);
                fixture.App.HoldRefresh = true;
                await fixture.Play(); await Decoded(fixture.Native);
                await fixture.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Task? ending = null;
                if (action == "stop") await fixture.Native.StopAsync();
                else if (action == "replace")
                    await fixture.Native.OpenAsync("film", "replacement", fixture.Origin.Uri("other.wav"), "fixture", fixture.Origin.Headers);
                else if (action == "cancel") ending = fixture.Module.CancelOnlineAsync();
                else ending = fixture.Module.PrepareShutdownAsync();
                await Task.Delay(100);
                if (ending is not null) Check(!ending.IsCompleted, "real noncooperative resolver still tracked until settled");
                fixture.App.ReleaseRefresh.TrySetResult();
                if (ending is not null) await ending.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(100);
                Check(!fixture.Origin.Requests.Any(row => row.Path == "/new.wav"), "late response cannot reopen");
                if (action == "replace") Check(fixture.Native.OwnerId == "film", "other domain untouched");
                Check(fixture.App.Resolutions.Count == 2, "no further retries");
            });
        foreach (var poison in new[] { "generation", "sourceKey", "installationId", "quality" })
            yield return Test("music refresh identity rejection " + poison, async () =>
            {
                await using var fixture = await MusicFixture.Create(firstFails: true);
                fixture.App.Poison = poison;
                await fixture.Play(); await Task.Delay(50);
                Check(fixture.App.Resolutions.Count == 2 && !fixture.Native.HasMedia, "mismatched target not opened");
                Check(!fixture.Origin.Requests.Any(row => row.Path == "/new.wav"), "no poisoned media request");
            });
        yield return Test("handoff native nonseekable refresh cannot play from zero", async () =>
        {
            await using var origin = WaveOrigin(); await using var native = Player(origin);
            await native.OpenAsync("music", "entry", origin.Uri("old.wav"), "fixture", origin.Headers);
            await Decoded(native);
            await native.SeekAsync(30); await Until(() => native.PositionSeconds >= 29, "position before nonseekable");
            var point = native.CaptureContinuation()!;
            var old = Session(native)!;
            var starts = 0;
            native.Started += (_, _) => starts++;
            origin.Route = _ => new Reply(Wave(), "audio/wav") { IgnoreRange = true };
            try
            {
                await native.ReopenAsync(point, origin.Uri("new.wav"), origin.Headers);
                throw new InvalidOperationException("Nonseekable continuation unexpectedly succeeded");
            }
            catch (PlaybackException error) { Check(error.Code == PlaybackError.NotSeekable, "exact seek rejection"); }
            Check(starts == 0 && !native.HasMedia && !native.IsPlaying, "never starts at zero");
            Check(((MediaPlayer)Field(old, "Player")!).NativeReference == IntPtr.Zero, "old native retired");
        });
        yield return Test("handoff music fresh decoder failure terminates without third resolution", async () =>
        {
            await using var f = await MusicFixture.Create(firstFails: true);
            var bad = Wave(); bad[20] = 0xff; bad[21] = 0x7f;
            f.Origin.Route = path => path == "/old.wav" ? new Reply([]) { Status = 401 } : new Reply(bad, "audio/wav");
            await f.Play();
            await Until(() => !f.Native.HasMedia && f.Module.Status == "播放失败", "fresh codec terminal");
            Check(f.App.Resolutions.Count == 2 && f.App.HistoryWrites == 0, "no retry or successful history");
            Check(f.Origin.Requests.Any(row => row.Path == "/new.wav"), "real refreshed native input attempted");
        });
        foreach (var mutation in new[] { "rollback", "disable" })
            yield return Test("handoff music source " + mutation + " revokes held refresh and drains", async () =>
            {
                await using var f = await MusicFixture.Create(expiry: true);
                f.App.HoldRefresh = true;
                f.Module.SelectedSource = new("fixture-source", "fixture", "1", "", 7, true, "ready", false, ["wy"], "");
                await f.Play(); await Decoded(f.Native);
                await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                var changed = f.Module.SourceActionAsync(mutation);
                await Until(() => !f.Native.HasMedia, "changed source retires native authorization");
                Check(!changed.IsCompleted, "source action still awaits the actual held resolver");
                f.App.ReleaseRefresh.TrySetResult();
                await changed.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.App.SourceChanges == 1 && f.App.Resolutions.Count == 2, "one admitted mutation and no retry");
                Check(!f.Origin.Requests.Any(row => row.Path == "/new.wav") && f.Runtime.Calls == 0, "no late native or script open");
            });
    }

    private static TestCase Test(string name, Func<Task> body) => new("playback-recovery: " + name, () => Sta(body));
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Recovery assertion: " + message); }
    private static async Task Until(Func<bool> predicate, string phase)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Recovery phase: " + phase); await Task.Delay(15); }
    }
    private static async Task Reject(Func<Task> body, string message)
    {
        try { await body(); }
        catch { return; }
        throw new InvalidOperationException("Expected rejection: " + message);
    }
    private static byte[] Wave()
    {
        const int length = 8000 * 2 * 120;
        var bytes = new byte[length + 44];
        using var memory = new MemoryStream(bytes); using var writer = new BinaryWriter(memory);
        writer.Write("RIFF"u8); writer.Write(length + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length);
        return bytes;
    }
    private sealed class CachedWave : INativeMediaReadLease
    {
        public Stream Content { get; } = new MemoryStream(Wave(), writable: false);
        public long Length => Content.Length;
        public CancellationToken Revoked => CancellationToken.None;
        internal int Disposals;
        public ValueTask DisposeAsync() { Disposals++; Content.Dispose(); return ValueTask.CompletedTask; }
    }
    private static Origin WaveOrigin()
    {
        var origin = new Origin(); var bytes = Wave();
        origin.Route = _ => new Reply(bytes, "audio/wav"); return origin;
    }
    private static NativePlayback Player(Origin origin) => new(Dispatcher.CurrentDispatcher, null,
        NativeAudioOutput.Dummy, NativeHttpMediaOptions.ForLoopbackTests([origin.Base],
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);
    private static void SameMusicTarget(List<JsonElement> resolutions)
    {
        Check(resolutions.Count == 2, "exact resolve budget");
        foreach (var key in new[] { "installationId", "platform", "generation", "quality" })
            Check(resolutions[0].GetProperty(key).GetRawText() == resolutions[1].GetProperty(key).GetRawText(), "same " + key);
        var song = resolutions[1].GetProperty("song");
        var expectedSource = song.GetProperty("sources").EnumerateArray()
            .Single(source => source.GetProperty("source").GetString() == "wy")
            .GetProperty("sourceKey").GetString();
        Check(resolutions[1].GetProperty("sourceKey").GetString() == expectedSource, "resolved recording pinned");
        Check(resolutions[0].GetProperty("song").GetRawText() == resolutions[1].GetProperty("song").GetRawText(), "same song payload");
    }

    private sealed class MemoryStore : IProtectedDocumentStore
    {
        private readonly Dictionary<string, byte[]> data = [];
        internal void SeedMusicSource()
        {
            const string source = "// synthetic inert fixture; never executed";
            byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
            string sourceHash = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
            var document = new
            {
                version = 1,
                entries = new[]
                {
                    new
                    {
                        key = """["meowfilm","music",1,"local","lx-installations"]""",
                        value = new
                        {
                            schema = 4,
                            revision = 1,
                            installations = new[]
                            {
                                new
                                {
                                    id = "fixture-source",
                                    name = "fixture",
                                    enabled = true,
                                    previous = (object?)null,
                                    current = new
                                    {
                                        sourceHash,
                                        sourceBytes = sourceBytes.Length,
                                        version = "1",
                                        permissions = new
                                        {
                                            storage = Array.Empty<string>()
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
            data[MusicLibraryOwner.DocumentNamespace] = LJ.Bytes(
                JsonNode.Parse(JsonSerializer.Serialize(document, Json))!.AsObject());
            data[$"{MusicLibraryOwner.ScriptContentCollection}.{sourceHash}.0"] = sourceBytes;
        }
        public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (data) return Task.FromResult(data.TryGetValue(ns, out var bytes)
                ? new ProtectedDocument(bytes.ToArray(), DocumentFingerprint.FromBytes(bytes)) : new(null, DocumentFingerprint.Missing));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (data)
            {
                var actual = data.TryGetValue(ns, out var old) ? DocumentFingerprint.FromBytes(old) : DocumentFingerprint.Missing;
                if (actual != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
                data[ns] = bytes.ToArray(); return Task.FromResult(DocumentFingerprint.FromBytes(bytes.Span));
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoRuntime : IRuntimeClient
    {
        public int Calls;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("NO_NODE_ALLOWED"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("NO_PUBLIC_HTTP_ALLOWED");
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
    private sealed class MusicFixture : IAsyncDisposable
    {
        internal readonly Origin Origin = WaveOrigin();
        internal readonly MemoryStore Store = new();
        internal readonly NoRuntime Runtime = new();
        internal readonly NativePlayback Native;
        internal readonly MusicApp App;
        internal readonly MusicCapability Module;
        private MusicFixture(bool firstFails, bool bothFail, bool expiry, bool official)
        {
            var bytes = Wave();
            Origin.RequireAuthorization = !official; // Official CDN is anonymous; credentials must not be added to make a fixture pass.
            Origin.Route = path => bothFail || firstFails && path == "/old.wav"
                ? new Reply([]) { Status = 401 } : new Reply(bytes, "audio/wav");
            Native = Player(Origin);
            if (!official) Store.SeedMusicSource();
            App = new(new MusicNativeApplication(Store, Runtime, new NoHttp()), Origin, expiry, official);
            Module = new(App, Store, Native, new Dialogs());
        }
        internal static async Task<MusicFixture> Create(bool firstFails = false, bool bothFail = false, bool expiry = false, bool official = false)
        {
            var f = new MusicFixture(firstFails, bothFail, expiry, official);
            await f.App.InitializeAsync();
            await f.Module.InitializeAsync(default);
            return f;
        }
        internal Task Play() => Module.PlayOnlineSongAsync("wy", "合成歌曲", "fixture", "",
            JsonSerializer.SerializeToElement(new { songmid = "42" }));
        public async ValueTask DisposeAsync()
        {
            App.ReleaseRefresh.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Native.DisposeAsync(); await Origin.DisposeAsync(); }
        }
    }
    private sealed class ControlledMusicFixture : IAsyncDisposable
    {
        internal readonly Origin Origin = WaveOrigin();
        internal readonly MemoryStore Store = new();
        internal readonly NoRuntime Runtime = new();
        internal readonly ControlledPlayback Output = new();
        internal readonly MusicApp App;
        internal readonly MusicCapability Module;
        internal readonly string? CacheRoot;
        internal readonly MusicAudioFileCache? Cache;
        internal readonly TaskCompletionSource ValidationEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ValidationRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldValidation;
        internal CancellationToken ValidationToken;
        internal string? NextSongId;
        internal MusicAudioFileCache.PreparedFile? Prepared =>
            (MusicAudioFileCache.PreparedFile?)Field(Module, "preparedAudio");
        private ControlledMusicFixture(bool playbackCache)
        {
            Origin.RequireAuthorization = false;
            App = new(new MusicNativeApplication(Store, Runtime, new NoHttp()), Origin,
                expiry: false, official: true);
            if (playbackCache)
            {
                CacheRoot = TestWorkspace.DataPath(
                    "meowfilm-preload-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(CacheRoot);
                Cache = new(CacheRoot, Store, Validate,
                    httpOptions: NativeHttpMediaOptions.ForLoopbackTests([Origin.Base]));
                Module = new(App, Store, Output, Cache, new Dialogs());
            }
            else Module = new(App, Store, Output, new Dialogs());
        }
        internal static async Task<ControlledMusicFixture> Create(bool playbackCache = false)
        {
            var f = new ControlledMusicFixture(playbackCache);
            await f.App.InitializeAsync();
            await f.Module.InitializeAsync(default);
            return f;
        }
        internal Task Play() => Module.PlayOnlineSongAsync("wy", "合成歌曲", "fixture", "",
            JsonSerializer.SerializeToElement(new { songmid = "42" }));
        internal async Task PlayPair(string nextPlatform = "wy")
        {
            var first = MusicSongFixture.Online("wy", "First", "当前首", "fixture");
            var next = MusicSongFixture.Online(nextPlatform, "Next", "下一首", "fixture");
            NextSongId = next["songId"]!.GetValue<string>();
            App.CacheableSongId = NextSongId;
            var replaced = await App.ExecuteAsync("music.queue.replace",
                new { songs = new[] { first, next }, index = 0 });
            Check(replaced.GetProperty("ok").GetBoolean(), "controlled pair queue");
            await Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await Module.PlayQueueSurfaceItemAsync(Module.QueueSurfaceRows.First());
        }
        internal async Task<MusicAudioFileCache.PreparedFile> WaitForPrepared()
        {
            await ValidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return Prepared ?? throw new InvalidOperationException("missing prepared owner");
        }
        private async Task<double> Validate(string path, double expected, CancellationToken token)
        {
            ValidationToken = token;
            ValidationEntered.TrySetResult();
            if (HoldValidation) await ValidationRelease.Task;
            return expected;
        }
        public async ValueTask DisposeAsync()
        {
            App.ReleaseRefresh.TrySetResult();
            ValidationRelease.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally
            {
                Output.Close();
                await Origin.DisposeAsync();
                if (CacheRoot is not null)
                {
                    TestWorkspace.ValidatePath(TestWorkspace.DataRoot, CacheRoot);
                    if (!Path.GetFileName(CacheRoot).StartsWith("meowfilm-preload-test-", StringComparison.Ordinal))
                        throw new InvalidOperationException("unsafe preload test root");
                    if (Directory.Exists(CacheRoot)) Directory.Delete(CacheRoot, true);
                }
            }
        }
    }
    private sealed class ControlledPlayback : IMusicPlayback, INativePlaybackContinuation
    {
        internal sealed class ReopenPlan(bool hold, bool startBeforeCompletion, string? failure)
        {
            internal readonly bool Hold = hold, StartBeforeCompletion = startBeforeCompletion;
            internal readonly string? Failure = failure;
            internal readonly TaskCompletionSource Entered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationToken Token;
        }
        private readonly Queue<ReopenPlan> plans = [];
        private CancellationTokenSource authority = new();
        private long generation;
        private bool startedForGeneration;
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying { get; private set; }
        public double PositionSeconds { get; private set; }
        public double DurationSeconds => 100;
        public int Volume { get; set; } = 50;
        public bool PauseRequested { get; private set; }
        public string? LastErrorCode { get; private set; }
        internal int Reopens, CandidateStarts;
        internal int NetworkOpens, ReadOpens;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? Started;
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed;

        internal ReopenPlan Plan(bool hold = false, bool startBeforeCompletion = false,
            string? failure = null)
        {
            var plan = new ReopenPlan(hold, startBeforeCompletion, failure);
            if (!hold) plan.Release.TrySetResult();
            plans.Enqueue(plan);
            return plan;
        }
        internal void Start()
        {
            if (!HasMedia || startedForGeneration) return;
            startedForGeneration = true; IsPlaying = !PauseRequested; CandidateStarts++;
            PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            Started?.Invoke(this, EventArgs.Empty);
        }
        internal void Fail(string code)
        {
            LastErrorCode = code; IsPlaying = false;
            PropertyChanged?.Invoke(this, new(nameof(LastErrorCode)));
            PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            Failed?.Invoke(this, EventArgs.Empty);
        }
        public NativePlaybackContinuation? CaptureContinuation()
        {
            if (OwnerId is null || Identity is null || authority.IsCancellationRequested) return null;
            var constructor = typeof(NativePlaybackContinuation).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                [typeof(NativePlaybackScope), typeof(double), typeof(CancellationToken)],
                modifiers: null) ?? throw new InvalidOperationException("continuation constructor");
            return (NativePlaybackContinuation)constructor.Invoke(
                [new NativePlaybackScope(OwnerId, Identity, generation), PositionSeconds, authority.Token]);
        }
        public async Task ReopenAsync(NativePlaybackContinuation continuation, Uri uri,
            IReadOnlyDictionary<string, string> headers, CancellationToken token = default)
        {
            if (plans.Count == 0) throw new InvalidOperationException("missing controlled reopen plan");
            var plan = plans.Dequeue(); plan.Token = token; Reopens++;
            plan.Entered.TrySetResult();
            if (plan.StartBeforeCompletion)
            {
                generation++; HasMedia = true; LastErrorCode = null;
                startedForGeneration = false; Start();
            }
            if (plan.Hold) await plan.Release.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (plan.Failure is { } failure)
            {
                if (!plan.StartBeforeCompletion) generation++;
                HasMedia = false; IsPlaying = false; LastErrorCode = failure;
                PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
                PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
                PropertyChanged?.Invoke(this, new(nameof(LastErrorCode)));
                Failed?.Invoke(this, EventArgs.Empty);
                throw new MusicOperationException(failure);
            }
            if (!plan.StartBeforeCompletion)
            {
                generation++; HasMedia = true; IsPlaying = false; LastErrorCode = null;
                startedForGeneration = false;
                PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
                PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
                PropertyChanged?.Invoke(this, new(nameof(LastErrorCode)));
            }
        }
        public Task OpenAsync(string owner, string identity, Uri uri, string title,
            CancellationToken token) => OpenAsync(owner, identity, uri, title,
                new Dictionary<string, string>(), token);
        public Task OpenAsync(string owner, string identity, Uri uri, string title,
            IReadOnlyDictionary<string, string> headers, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            NetworkOpens++;
            authority.Cancel(); authority.Dispose(); authority = CancellationTokenSource.CreateLinkedTokenSource(token);
            generation++; OwnerId = owner; Identity = identity; HasMedia = true; IsPlaying = true;
            PositionSeconds = 0; PauseRequested = false; LastErrorCode = null; startedForGeneration = true;
            PropertyChanged?.Invoke(this, new(nameof(OwnerId)));
            PropertyChanged?.Invoke(this, new(nameof(Identity)));
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            PropertyChanged?.Invoke(this, new(nameof(PositionSeconds)));
            Started?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public async Task OpenReadAsync(string owner, string identity, INativeMediaReadLease lease,
            string title, CancellationToken token = default)
        {
            await using (lease)
            {
                token.ThrowIfCancellationRequested();
                if (lease.Length <= 0 || lease.Content.ReadByte() < 0)
                    throw new MusicOperationException(PlaybackError.OpenFailed);
            }
            ReadOpens++;
            await OpenAccepted(owner, identity, token);
        }
        private Task OpenAccepted(string owner, string identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            authority.Cancel(); authority.Dispose(); authority = CancellationTokenSource.CreateLinkedTokenSource(token);
            generation++; OwnerId = owner; Identity = identity; HasMedia = true; IsPlaying = true;
            PositionSeconds = 0; PauseRequested = false; LastErrorCode = null; startedForGeneration = true;
            PropertyChanged?.Invoke(this, new(nameof(OwnerId)));
            PropertyChanged?.Invoke(this, new(nameof(Identity)));
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            PropertyChanged?.Invoke(this, new(nameof(PositionSeconds)));
            Started?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task PauseAsync()
        {
            PauseRequested = true; IsPlaying = false;
            PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            return Task.CompletedTask;
        }
        public Task ResumeAsync()
        {
            PauseRequested = false;
            if (!startedForGeneration) Start();
            else
            {
                IsPlaying = HasMedia;
                PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            }
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            authority.Cancel(); HasMedia = false; IsPlaying = false;
            OwnerId = Identity = null; LastErrorCode = null;
            PropertyChanged?.Invoke(this, new(nameof(OwnerId)));
            PropertyChanged?.Invoke(this, new(nameof(Identity)));
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            return Task.CompletedTask;
        }
        public Task SeekAsync(double seconds)
        {
            PositionSeconds = seconds;
            PropertyChanged?.Invoke(this, new(nameof(PositionSeconds)));
            return Task.CompletedTask;
        }
        internal void SetPosition(double seconds)
        {
            PositionSeconds = seconds;
            PropertyChanged?.Invoke(this, new(nameof(PositionSeconds)));
        }
        internal void Close()
        {
            try { authority.Cancel(); }
            finally { authority.Dispose(); }
        }
    }
    private sealed class MusicApp(MusicNativeApplication inner, Origin origin, bool expiry, bool official) : MusicApplicationForwarder(inner)
    {
        internal readonly List<JsonElement> Resolutions = [];
        internal bool HoldRefresh;
        internal bool RejectAlternate;
        internal bool RepeatExpiry;
        internal string? Poison;
        internal string? CacheableSongId;
        internal int HistoryWrites, SourceChanges, ConfirmWrites, Authorizations;
        internal readonly FeedbackProbe FeedbackProbe = new();
        private readonly HashSet<string> revokedOfficial = [];
        private event Action<string>? accountInvalidated;
        private int candidatePlans;
        internal readonly TaskCompletionSource RefreshEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? initialization;
        public override MeowFilm.Capabilities.Feedback.IMusicPlaybackFeedback Feedback => FeedbackProbe;
        public override event Action<string>? AccountDetailsInvalidated
        {
            add { accountInvalidated += value; Inner.AccountDetailsInvalidated += value; }
            remove { accountInvalidated -= value; Inner.AccountDetailsInvalidated -= value; }
        }
        public override bool IsOfficialPlaybackCurrent(string platform, string scope, long generation) =>
            (official && scope == "fixture" && generation == 7 && !revokedOfficial.Contains(platform)) ||
            base.IsOfficialPlaybackCurrent(platform, scope, generation);
        internal void InvalidateOfficial(string platform)
        {
            revokedOfficial.Add(platform);
            accountInvalidated?.Invoke(platform);
        }
        public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
            initialization ??= InitializeCore(cancellationToken);
        private async Task InitializeCore(CancellationToken token)
        {
            await inner.InitializeAsync(token);
            if (official) return;
            var reply = await inner.ExecuteAsync("music.quality.settings.get", cancellationToken: token);
            Check(reply.GetProperty("ok").GetBoolean(), "native quality fixture read: " + reply.GetRawText());
            var policy = JsonNode.Parse(reply.GetProperty("settings").GetRawText())!.AsObject();
            policy["ifRevision"] = policy["revision"]!.DeepClone();
            policy.Remove("schema"); policy.Remove("revision");
            var saved = await inner.ExecuteAsync("music.quality.settings.save", policy, token);
            Check(saved.GetProperty("ok").GetBoolean(), "native quality fixture setup");
            var online = await inner.ExecuteAsync("music.settings.save", new
            {
                ifRevision = reply.GetProperty("onlineSettings").GetProperty("revision").GetInt64(),
                lyrics = false,
                target = new { installationId = "fixture-source", platform = "wy" }
            }, token);
            Check(online.GetProperty("ok").GetBoolean(), "native online target fixture setup");
        }
        private Task<JsonElement> ReadQualitySettings(CancellationToken token) =>
            inner.ExecuteAsync("music.quality.settings.get", cancellationToken: token);
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var input = Element(parameters ?? new { });
            // The native owner's complete quality reply and all failure fields
            // remain authoritative; only the existing script/media port is fake.
            if (official && method == "music.online.song")
            {
                var request = JsonNode.Parse(input.GetRawText())!.AsObject();
                request["duration"] = 180;
                return await inner.ExecuteAsync(method, request, cancellationToken);
            }
            if (method.StartsWith("music.quality.", StringComparison.Ordinal))
                return await inner.ExecuteAsync(method, parameters, cancellationToken);
            if (official && method == "music.official.cacheContext")
            {
                string platform = input.GetProperty("platform").GetString()!;
                if (revokedOfficial.Contains(platform)) return Element(new { ok = false, code = "AUTH_CHANGED" });
                return Element(new { ok = true, platform,
                    sourceKey = input.GetProperty("sourceKey").GetString(),
                    songId = input.GetProperty("song").GetProperty("songId").GetString(),
                    scope = "fixture", generation = 7,
                    qualityCacheSignature = new string('a', 64),
                    qualityPolicySignature = input.GetProperty("qualityPolicySignature").GetString() });
            }
            if (official && method == "music.official.authorize")
            {
                string platform = input.GetProperty("platform").GetString()!;
                if (revokedOfficial.Contains(platform)) return Element(new { ok = false, code = "AUTH_CHANGED" });
                Authorizations++;
                return Element(new { ok = true });
            }
            if (official && method == "music.official.confirm")
            {
                ConfirmWrites++;
                return Element(new { ok = true });
            }
            if (official && method == "music.official.candidates")
            {
                bool includeAlternate = ++candidatePlans >= 3;
                var raw = JsonNode.Parse(input.GetProperty("song").GetRawText())!.AsObject();
                double duration = LJ.N(raw["duration"]);
                var other = MusicSongFixture.Online("tx", "Other", LJ.S(raw["title"]),
                    LJ.S(raw["artist"]), LJ.S(raw["album"]), duration, LJ.S(raw["cover_url"]));
                var sources = raw["sources"]!.AsArray();
                if (!sources.OfType<JsonObject>().Any(r => LJ.S(r["source"]) == "tx"))
                    sources.Add(other["sources"]![0]!.DeepClone());
                raw = NativeMusicSong.Normalize(raw);
                sources = raw["sources"]!.AsArray();
                var policy = await ReadQualitySettings(cancellationToken);
                return Element(new { ok = true, song = raw, qualityPolicySignature = policy.GetProperty("signature").GetString(),
                    candidates = sources.OfType<JsonObject>().Where(r => includeAlternate || LJ.S(r["source"]) == "wy").Select(r => new { platform = LJ.S(r["source"]),
                        sourceKey = LJ.S(r["sourceKey"]), generation = 7, scope = "fixture" }),
                    failures = Array.Empty<object>(), hasMore = false });
            }
            if (official && method == "music.official.resolve")
            {
                Resolutions.Add(input);
                string platform = input.GetProperty("platform").GetString()!;
                if (Resolutions.Count > 1)
                {
                    RefreshEntered.TrySetResult();
                    if (HoldRefresh) await ReleaseRefresh.Task;
                }
                if (platform == "tx" && RejectAlternate) return Element(new { ok = false, code = "AUTH_REQUIRED" });
                var policy = await ReadQualitySettings(cancellationToken);
                string songId = input.GetProperty("song").GetProperty("songId").GetString()!;
                bool full = songId == CacheableSongId;
                return Element(new { ok = true, kind = "official", platform,
                    songId,
                    sourceKey = input.GetProperty("sourceKey").GetString(), generation = 7, scope = "fixture",
                    quality = "320k", requestedQuality = "320k", actualQuality = "",
                    qualityPolicySignature = policy.GetProperty("signature").GetString(),
                    qualityCacheSignature = new string('a', 64), playbackVerified = false, qualityVerified = false,
                    url = origin.Uri(platform == "tx" ? "other.wav" : Resolutions.Count == 1 ? "old.wav" : "new.wav").AbsoluteUri,
                    headers = new { }, expiresAt = (long?)null, duration = 100d,
                    size = full ? Wave().Length : 0, full, licenseExpiresAt = (long?)null,
                    delivery = platform + "-official", offlineAllowed = false,
                    authorizationExpiresAt = (long?)null });
            }
            if (method == "music.sources.detail") return Element(new { ok = true, item = new { generation = 7 } });
            if (method is "music.sources.rollback" or "music.sources.disable")
            { SourceChanges++; return Element(new { ok = true }); } // Synthetic script owner; real module cancellation/drain.
            if (method == "music.online.resolve")
            {
                Resolutions.Add(input);
                bool fresh = Resolutions.Count > 1;
                var quality = await ReadQualitySettings(cancellationToken);
                if (!quality.GetProperty("ok").GetBoolean()) return quality;
                if (input.GetProperty("quality").GetString() != "320k")
                    return Element(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
                string policy = quality.GetProperty("signature").GetString()!;
                var song = input.GetProperty("song");
                var source = song.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("source").GetString() == "wy");
                string sourceKey = source.GetProperty("sourceKey").GetString()!;
                var songNode = JsonNode.Parse(song.GetRawText())!.AsObject();
                string cache = LJ.Digest(LJ.Obj(("contract", "music.quality.cache.v1"), ("policy", policy),
                    ("installation", "fixture-source"), ("platform", "wy"), ("revision", 1), ("generation", 7),
                    ("sourceKey", sourceKey), ("recording", source.GetProperty("musicInfo")),
                    ("songId", songNode["songId"]),
                    ("duration", songNode["duration"]), ("quality", "320k")));
                if (input.TryGetProperty("qualityPolicySignature", out var expectedPolicy) && expectedPolicy.GetString() != policy)
                    return Element(new { ok = false, code = "QUALITY_POLICY_CHANGED" });
                if (input.TryGetProperty("qualityCacheSignature", out var expectedCache) && expectedCache.GetString() != cache)
                    return Element(new { ok = false, code = "QUALITY_CACHE_CHANGED" });
                if (fresh)
                {
                    RefreshEntered.TrySetResult();
                    if (HoldRefresh) await ReleaseRefresh.Task; // deliberately noncooperative: verify real drain
                }
                var result = JsonSerializer.SerializeToNode(new { ok = true,
                    url = origin.Uri(fresh ? "new.wav" : "old.wav").AbsoluteUri, headers = origin.Headers,
                    installationId = "fixture-source", platform = "wy", generation = 7, quality = "320k",
                    sourceKey, songId = input.GetProperty("song").GetProperty("songId").GetString(),
                    qualityPolicySignature = policy, qualityCacheSignature = cache, qualityVerified = false,
                    expiresAt = expiry && (!fresh || RepeatExpiry) ? (long?)DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeMilliseconds() : null,
                    playbackVerified = false }, Json)!.AsObject();
                if (fresh && Poison is not null) result[Poison] = Poison == "generation" ? JsonValue.Create(8) : JsonValue.Create("other");
                return JsonSerializer.SerializeToElement(result, Json);
            }
            if (method == "music.history.recordPlayed") HistoryWrites++;
            return await inner.ExecuteAsync(method, parameters, cancellationToken);
        }
    }
    private sealed class FeedbackProbe : MeowFilm.Capabilities.Feedback.IMusicPlaybackFeedback
    {
        internal int Starts;
        internal readonly List<string> Finishes = [];
        private bool active;
        public string? LastError => null;
        public Task<MeowFilm.Capabilities.Feedback.MusicFeedbackSettings> ReadSettingsAsync(
            bool refresh = false, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new MeowFilm.Capabilities.Feedback.MusicFeedbackSettings(
                0, Array.Empty<string>()));
        }
        public Task<MeowFilm.Capabilities.Feedback.MusicFeedbackSettings> SaveSettingsAsync(
            long revision, IReadOnlyList<string> enabled, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new MeowFilm.Capabilities.Feedback.MusicFeedbackSettings(
                revision + 1, enabled.ToArray()));
        }
        public void Select(MeowFilm.Capabilities.Feedback.MusicFeedbackSelection value) { }
        public void UpdateSelection(MeowFilm.Capabilities.Feedback.MusicFeedbackSelection value) { }
        public void Started(string entryId, double position, double duration, double rate = 1, int mode = 1)
        {
            Starts++;
            active = true;
        }
        public void Observe(string entryId, double position, double duration, bool isPlaying,
            double rate = 1, int mode = 1) { }
        public void Finish(string reason = "ui")
        {
            if (!active) return;
            Finishes.Add(reason);
            active = false;
        }
    }
    private sealed class Backend : ISystemMediaBackend
    {
        public event EventHandler<SystemPlaybackRequest>? Requested;
        public void Send(SystemPlaybackCommand command) => Requested?.Invoke(this, new(command));
        public void Update(SystemPlaybackState state) { }
        public void Disable() { }
        public void Dispose() { }
    }
    private sealed class HeldSystemPlayer : ISystemPlayback
    {
        public SystemPlaybackState State => new("music", "entry", "fixture", true, !Paused, 0, 100);
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Plays; internal bool Paused;
        public async Task PlayAsync() { Plays++; Entered.TrySetResult(); await Release.Task; }
        public Task PauseAsync() { Paused = true; return Task.CompletedTask; }
        public Task StopAsync() { Paused = true; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
    }
}

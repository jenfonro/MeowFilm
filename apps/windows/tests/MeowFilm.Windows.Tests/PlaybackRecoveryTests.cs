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
        yield return Test("source menu opens cached results without requests and manual unavailable quality retains playback", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            var before = await f.App.ExecuteAsync("music.quality.settings.get");
            int resolutions = f.App.Resolutions.Count, discoveries = f.App.Discoveries;
            f.Module.OpenSourceChoices();
            Check(f.App.Resolutions.Count == resolutions && f.App.Discoveries == discoveries, "view more is local");
            await f.Module.SwitchQualityAsync("lossless");
            Check(f.Output.IsPlaying && f.Output.Commits == 0 && f.Module.CurrentSource == "网易云音乐" &&
                f.Module.Status == "未匹配到对应音质", "manual quality has no downgrade or interruption");
            var after = await f.App.ExecuteAsync("music.quality.settings.get");
            Check(before.GetRawText() == after.GetRawText(), "player quality never saves global policy");
        });
        yield return Test("native handoff prepares a full-file lease without interruption and preserves live pause rate and volume", async () =>
        {
            await using var origin = WaveOrigin(); await using var native = Player(origin);
            var old = new CachedWave(); var next = new CachedWave();
            await native.OpenReadAsync("music", "entry", old, "fixture");
            await Decoded(native);
            await native.SetRateAsync((await native.ReadTracksAsync())!.Scope, 1.6);
            native.Volume = 37;
            await native.SeekAsync(18);
            await native.PrepareReadHandoffAsync(next, default);
            Check(native.IsPlaying && old.Disposals == 0 && next.Disposals == 0 &&
                native.PreparedHandoffDurationSeconds >= 119, "preparation keeps the accepted input and owns the new lease");
            await native.PauseAsync();
            double position = native.PositionSeconds;
            await native.CommitHandoffAsync(default);
            Check(old.Disposals == 1 && next.Disposals == 0 && native.Identity == "entry" &&
                native.PauseRequested && !native.IsPlaying && native.Volume == 37 &&
                Math.Abs(native.PositionSeconds - position) < 1 &&
                Math.Abs((await native.ReadTracksAsync())!.Rate - 1.6) < .01, "handoff preserves playback intent and controls");
            await native.StopAsync();
            Check(next.Disposals == 1, "the committed lease releases once");
        });
        yield return Test("native handoff cancelled after readiness cannot commit and stop releases an uncommitted lease", async () =>
        {
            await using var origin = WaveOrigin(); await using var native = Player(origin);
            await native.OpenAsync("music", "entry", origin.Uri("old.wav"), "fixture", origin.Headers);
            await Decoded(native);
            var accepted = Session(native);
            using var request = new CancellationTokenSource();
            var cancelled = new CachedWave();
            await native.PrepareReadHandoffAsync(cancelled, request.Token);
            request.Cancel();
            await Reject(() => native.CommitHandoffAsync(default), "cancelled preparation");
            Check(cancelled.Disposals == 1 && ReferenceEquals(accepted, Session(native)) && native.IsPlaying,
                "cancellation retires only the prepared input even when Commit has a fresh token");
            var abandoned = new CachedWave();
            await native.PrepareReadHandoffAsync(abandoned, default);
            await native.StopAsync();
            Check(abandoned.Disposals == 1 && !native.HasMedia, "stop retires ready but uncommitted media");
        });
        yield return Test("wrong source persists same-quality replacement and exclusion precedes cached attempts", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await TxChoice(f.Module);
            int requests = f.App.Resolutions.Count;
            string identity = f.Output.Identity!;
            f.Output.Plan();
            await f.Module.ReportWrongSourceAsync();
            Check(f.Output.Commits == 1 && f.Output.Identity == identity && f.Module.CurrentSource == "QQ 音乐" &&
                f.App.Resolutions.Count == requests, "cached same-quality alternative wins with no new resolution");
            await f.Module.ReportWrongSourceAsync();
            Check(!f.Output.IsPlaying && f.Output.Identity == identity && f.Output.Commits == 1,
                "without same-quality alternatives the occurrence pauses instead of skipping");
            await f.Module.RefreshSourceExclusionsAsync();
            Check(f.Module.SourceExclusions.Count == 2, "exclusions accumulate");
            await f.Module.RestoreSourceExclusionAsync(f.Module.SourceExclusions.Single(row => row.Platform == "wy"));
            Check(f.Module.SourceExclusions.Count == 1 && !f.Output.IsPlaying, "restore never plays");
            Check(f.Module.CanLoadMoreSources, "restored source reopens the exhausted tier");
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(f.Module.SourceChoices.Single(row => row.Key == "auto"));
            Check(f.Output.Commits == 2 && f.Module.CurrentSource == "网易云音乐" && !f.Output.IsPlaying &&
                f.Output.Identity == identity && f.App.Resolutions.Count == requests + 1,
                "explicit selection can resolve the restored provider while retaining the pause intent");
        });
        yield return Test("second manual source choice cancels prepared predecessor before commit", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            var choice = await TxChoice(f.Module);
            var first = f.Output.Plan(hold: true);
            var pending = f.Module.SelectSourceChoiceAsync(choice);
            await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Output.Plan();
            var replacement = f.Module.SelectSourceChoiceAsync(choice);
            await Task.WhenAll(pending, replacement).WaitAsync(TimeSpan.FromSeconds(5));
            Check(first.Token.IsCancellationRequested && f.Output.Prepares == 2 && f.Output.Commits == 1 &&
                f.Module.CurrentSource == "QQ 音乐", "only the latest selection can commit");
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
            var other = await TxChoice(f.Module);
            string identity = f.Native.Identity!;
            await f.Module.SelectSourceChoiceAsync(other);
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
            var choice = await TxChoice(f.Module);
            var old = Session(f.Native);
            f.Origin.Route = path => path == "/other.wav" ? new Reply([]) { Status = 401 } : new Reply(Wave(), "audio/wav");
            await f.Module.SelectSourceChoiceAsync(choice);
            Check(ReferenceEquals(old, Session(f.Native)) && f.Module.CurrentSource == "网易云音乐" &&
                f.Native.HasMedia && f.App.HistoryWrites == 1, "no interruption or relabel on a rejected replacement");
        });
        yield return Test("official failed manual input keeps the accepted recording playing without switching song", async () =>
        {
            await using var f = await MusicFixture.Create(official: true);
            f.Origin.Route = path => path == "/other.wav" ? new Reply([]) { Status = 401 } : new Reply(Wave(), "audio/wav");
            await f.Play(); await Decoded(f.Native);
            await f.Module.SeekAsync(20); await Until(() => f.Native.PositionSeconds >= 19, "rollback seek");
            await f.Module.TogglePlaybackAsync();
            await f.Module.SelectSourceChoiceAsync(await TxChoice(f.Module));
            Check(f.Module.CurrentSource == "网易云音乐" && f.Native.HasMedia && f.Native.PauseRequested,
                "a failed prepared input never replaces the playing recording");
            await f.Module.TogglePlaybackAsync(); await Decoded(f.Native);
            await Until(() => f.Native.PositionSeconds >= 19, "rollback position");
        });
        yield return Test("official manual candidate stays unpublished while its replacement is prepared", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1 &&
                f.App.FeedbackProbe.Starts == 1, "initial controlled playback");
            var choice = await TxChoice(f.Module);
            var plan = f.Output.Plan(hold: true);
            var choose = f.Module.SelectSourceChoiceAsync(choice);
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
                f.Output.Prepares == 1 && f.Output.Commits == 1 && f.Output.Reopens == 0,
                "commit publishes one seamless source transition without a new occurrence");
        });
        yield return Test("official manual handoff of an already started playback confirms at commit", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1,
                "initial handoff fixture");
            var choice = await TxChoice(f.Module);
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(choice);
            await Until(() => f.App.ConfirmWrites == 2 && f.App.FeedbackProbe.Starts == 2, "handed-off source acceptance");
            Check(f.Module.CurrentSource == "QQ 音乐" && f.App.HistoryWrites == 1 &&
                f.App.FeedbackProbe.Finishes.SequenceEqual(new[] { "source-change" }) && f.Output.CandidateStarts == 0,
                "a handoff emits no second Started and is not another queue occurrence");
        });
        yield return Test("official failed manual replacement leaves the accepted input untouched", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1,
                "initial failed-handoff fixture");
            var choice = await TxChoice(f.Module);
            f.Output.Plan(failure: PlaybackError.HttpUnauthorized);
            await f.Module.SelectSourceChoiceAsync(choice);
            Check(f.Module.CurrentSource == "网易云音乐" && f.Output.HasMedia && f.Output.Prepares == 1 &&
                f.Output.Commits == 0 && f.Output.Reopens == 0 && f.App.ConfirmWrites == 1 &&
                f.App.HistoryWrites == 1 && f.App.FeedbackProbe.Starts == 1 && f.App.FeedbackProbe.Finishes.Count == 0,
                "a failed candidate is never reopened over, nor becomes a feedback or history identity");
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
            var choice = await TxChoice(f.Module);
            var plan = f.Output.Plan(hold: true);
            var choose = f.Module.SelectSourceChoiceAsync(choice);
            await plan.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.App.InvalidateOfficial("tx");
            await choose.WaitAsync(TimeSpan.FromSeconds(5));
            Check(plan.Token.IsCancellationRequested && f.Output.Reopens == 0 && f.Output.Commits == 0 &&
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
            f.Output.SetPosition(105);
            var prepared = await f.WaitForPrepared();
            Check(await prepared.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null,
                "validated provisional bytes");
            int transfers = f.Origin.Requests.Count, networkOpens = f.Output.NetworkOpens;
            await f.Module.StepAsync(true);
            await Until(() => f.Output.ReadOpens == 1 && f.App.Authorizations == 1 &&
                f.App.HistoryWrites == 2 && f.App.ConfirmWrites == 2,
                "prepared next occurrence");
            await prepared.Work.WaitAsync(TimeSpan.FromSeconds(5));
            // One resolve per occurrence: the next song's existing WY mapping is accepted without discovering QQ.
            Check(f.Output.NetworkOpens == networkOpens && f.Origin.Requests.Count == transfers &&
                f.Module.CurrentTitle == "下一首" && f.App.Resolutions.Count == 2,
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
                f.Output.SetPosition(105);
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
            f.Output.SetPosition(105);
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
                var acceptedSession = Session(f.Native)!;
                var acceptedAuthority = f.Native.CaptureContinuation()!;
                f.App.HoldResolution = input => input.GetProperty("quality").GetString() == "128k";
                var choose = f.Module.SwitchQualityAsync("standard");
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
        yield return Test("music script refresh carries the matched recording without changing the queue song", async () =>
        {
            await using var fixture = await MusicFixture.Create(firstFails: true, platforms: ["kw"]);
            fixture.App.MatchMissing = true;
            var song = MusicSongFixture.Online("wy", "42", "合成歌曲", "fixture", "", 120);
            Check((await fixture.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }))
                .GetProperty("ok").GetBoolean(), "script queue");
            await fixture.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await fixture.Module.PlayQueueSurfaceItemAsync(fixture.Module.QueueSurfaceRows.Single());
            await Decoded(fixture.Native);
            static bool Matched(JsonElement input) => input.GetProperty("song").GetProperty("sources").EnumerateArray()
                .Any(source => source.GetProperty("source").GetString() == "kw");
            Check(fixture.App.Resolutions.Count == 2 && fixture.App.Resolutions.All(Matched),
                "the shared matcher completes before both initial resolve and exact refresh");
            var queue = await fixture.App.ExecuteAsync("music.queue.get");
            Check(queue.GetProperty("current").GetProperty("song").GetProperty("sources").GetArrayLength() == 1,
                "the queue keeps its original song snapshot");
            fixture.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
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
            string? nativeError = null;
            fixture.Native.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(NativePlayback.LastErrorCode) &&
                    fixture.Native.LastErrorCode is { } error) nativeError ??= error;
            };
            await fixture.Play(); await Until(() => nativeError is not null, "actual native codec rejection");
            Check(nativeError is PlaybackError.PlaybackFailed or PlaybackError.OpenFailed,
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
        foreach (var action in new[] { "stop", "replace", "close" })
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
                await f.Module.RefreshSourceManagementAsync();
                var row = f.Module.Sources.Single(item => item.InstallationId == "fixture-source");
                await f.Play(); await Decoded(f.Native);
                await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                var changed = mutation == "rollback" ? f.Module.RollbackSourceAsync(row) : f.Module.SetSourceEnabledAsync(row, false);
                await Until(() => !f.Native.HasMedia, "changed source retires native authorization");
                Check(!changed.IsCompleted, "source action still awaits the actual held resolver");
                f.App.ReleaseRefresh.TrySetResult();
                await changed.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.App.SourceChanges == 1 && f.App.Resolutions.Count == 2, "one admitted mutation and no retry");
                Check(!f.Origin.Requests.Any(row => row.Path == "/new.wav") && f.Runtime.Calls == 0, "no late native or script open");
            });
        foreach (var test in UnifiedSourcesCases()) yield return test;
    }

    private static IEnumerable<TestCase> UnifiedSourcesCases()
    {
        yield return Test("manual quality requires its exact tier even when a provider returns a better one", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(
                scripts: [new("master-script", quality: "master")], qualityRequest: "auto", officialQuality: "flac");
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1, "higher quality accepted");
            var uri = f.Output.OpenedUris.Single();
            await f.Module.SwitchQualityAsync("high");
            Check(f.Module.Status == "未匹配到对应音质" && f.Output.IsPlaying &&
                f.Output.Commits == 0 && f.Output.Prepares == 0 && f.Output.OpenedUris.Single() == uri,
                "automatic accepts at least the requested quality; a manual choice never silently changes tier");
        });
        yield return Test("unified intersection asks A wy kg and B wy but never C without a matching platform", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false, scripts:
                [new("A", platforms: ["wy", "kg"]), new("B", platforms: ["wy", "mg"]), new("C", platforms: ["mg"])]);
            f.App.ResolutionFailure = _ => "MEDIA_UNAVAILABLE";
            var song = MusicSongFixture.Online("wy", "Original", "交集歌曲", "fixture");
            song["sources"]!.AsArray().Add(MusicSongFixture.Online("kg", "Matched", "交集歌曲", "fixture")["sources"]![0]!.DeepClone());
            Check((await f.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }))
                .GetProperty("ok").GetBoolean(), "intersection queue");
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await f.Module.PlayQueueSurfaceItemAsync(f.Module.QueueSurfaceRows.Single());
            Check(f.App.Resolutions.Select(row => Installation(row) + ":" + row.GetProperty("platform").GetString())
                .Order().SequenceEqual(new[] { "A:kg", "A:wy", "B:wy" }) && f.App.Discoveries == 1 &&
                f.Output.NetworkOpens == 0, "only known mappings cross declared capabilities; missing mg is completed once");
        });
        foreach (bool scriptWins in new[] { false, true })
            yield return Test("unified tiers " +
                (scriptWins ? "choose a higher quality script" : "keep a better built-in level over a lower script"), async () =>
            {
                var script = new ScriptSource("enabled-script", quality: scriptWins ? "flac" : "320k");
                await using var f = await ControlledMusicFixture.Create(
                    scripts: [new("disabled-script", enabled: false, quality: "master"), script],
                    qualityRequest: "auto", officialQuality: scriptWins ? "320k" : "flac");
                await f.Module.RefreshSourceManagementAsync();
                script.Generation = 19;
                var list = await f.App.ExecuteAsync("music.sources.list");
                Check(list.GetProperty("revision").GetInt64() == 0 &&
                    list.GetProperty("items").EnumerateArray().Count() == 2 &&
                    list.GetProperty("items").EnumerateArray().Single(row =>
                        row.GetProperty("installationId").GetString() == script.Id).GetProperty("enabled").GetBoolean() &&
                    !list.GetProperty("items").EnumerateArray().Single(row =>
                        row.GetProperty("installationId").GetString() == "disabled-script").GetProperty("enabled").GetBoolean(),
                    "the fixture catalog exposes independent enablement with no source mutation");
                await f.Play();
                await Until(() => f.App.HistoryWrites == 1, "mixed quality playback");
                Check(f.App.Resolutions.All(row => Installation(row) != "disabled-script") &&
                    f.App.Resolutions.Count(row => Installation(row) is null) == 1,
                    "a disabled script is never asked and the built-in provider answers once for its level");
                Check(f.Output.NetworkOpens == 1 && f.Output.ReadOpens == 0 &&
                    f.Output.OpenedUris.Single().AbsolutePath ==
                        (scriptWins ? "/" + script.Id + "-wy-" + script.Quality + ".wav" : "/official-wy-flac.wav") &&
                    f.Runtime.Calls == 0,
                    "the better tier wins across provider types; only the chosen source opens media");
            });
        yield return Test("unified first tier of the saved order asks every source before a lower tier", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(
                scripts: [new("high-script")], qualityRequest: "auto", officialQuality: "flac");
            await f.Module.ActivateQualitySettingsAsync();
            await Until(() => f.Module.CanEditQuality, "editable unified quality policy");
            await f.Module.MoveQualityAsync(
                f.Module.QualityCategories.Single(row => row.Key == "high"),
                f.Module.QualityCategories.Single(row => row.Key == "lossless"), false);
            var saved = await f.App.ExecuteAsync("music.quality.settings.get");
            var order = saved.GetProperty("settings").GetProperty("qualityOrder").EnumerateArray()
                .Select(row => row.GetString()).ToArray();
            Check(Array.IndexOf(order, "high") < Array.IndexOf(order, "lossless"), "user quality order persisted");
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1, "custom unified quality playback");
            Check(f.Output.OpenedUris.Single().AbsolutePath == "/high-script-wy-320k.wav" &&
                f.App.Resolutions.Any(row => Installation(row) is null) &&
                f.App.Resolutions.Any(row => Installation(row) == "high-script"),
                "the user's first tier (high) is exhausted across built-in and script sources before lossless");
        });
        foreach (bool mapped in new[] { false, true })
            yield return Test("unified script target " + (mapped ? "reuses an existing recording" : "matches only its missing platform"), async () =>
            {
                var script = new ScriptSource("target-script", platforms: ["tx"]) { Generation = 19 };
                await using var f = await ControlledMusicFixture.Create(official: false, scripts: [script]);
                f.App.MatchMissing = true;
                var song = MusicSongFixture.Online("wy", "Original", "目标平台", "fixture");
                if (mapped)
                    song["sources"]!.AsArray().Add(MusicSongFixture.Online("tx", "Matched", "目标平台", "fixture")
                        ["sources"]![0]!.DeepClone());
                Check((await f.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }))
                    .GetProperty("ok").GetBoolean(), "target recording queue");
                string original = (await f.App.ExecuteAsync("music.queue.get")).GetProperty("current").GetRawText();
                await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
                await f.Module.PlayQueueSurfaceItemAsync(f.Module.QueueSurfaceRows.Single());
                await Until(() => f.App.HistoryWrites == 1, "target script playback");
                Check(f.App.Resolutions.Count == 1 && Installation(f.App.Resolutions.Single()) == script.Id &&
                    f.App.Resolutions.Single().GetProperty("platform").GetString() == "tx" &&
                    f.App.MatchPlatforms.SequenceEqual(mapped ? Array.Empty<string>() : new[] { "tx" }) &&
                    f.App.Discoveries == (mapped ? 0 : 1),
                    "an existing mapping is reused; a missing target platform is completed once, shared by every provider");
                Check(f.Output.OpenedUris.Single().AbsolutePath == "/target-script-tx-320k.wav" &&
                    (await f.App.ExecuteAsync("music.queue.get")).GetProperty("current").GetRawText() == original,
                    "matched playback does not replace queue identity or metadata");
            });
        foreach (bool scriptFirst in new[] { false, true })
            yield return Test("unified HTTP recovery crosses from " +
                (scriptFirst ? "script to official" : "official to script") + " on the same recording", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(
                    scripts: [new("recovery-script", quality: scriptFirst ? "flac" : "320k")],
                    qualityRequest: "auto", officialQuality: scriptFirst ? "320k" : "flac");
                if (scriptFirst)
                    f.App.ResolutionFailure = row => Installation(row) is null ? "RECORDING_UNAVAILABLE" : null;
                await f.Play();
                f.App.ResolutionFailure = null;
                await Until(() => f.App.HistoryWrites == 1 && f.App.FeedbackProbe.Starts == 1,
                    "mixed accepted source");
                string identity = f.Output.Identity!;
                string title = f.Module.CurrentTitle;
                string firstPath = scriptFirst ? "/recovery-script-wy-flac.wav" : "/official-wy-flac.wav";
                string fallbackPath = scriptFirst ? "/official-wy-320k.wav" : "/recovery-script-wy-320k.wav";
                Check(f.Output.OpenedUris.Single().AbsolutePath == firstPath, "first usable initial source");
                await f.Module.SeekAsync(37);
                await f.Module.TogglePlaybackAsync();
                f.Output.Plan(failure: PlaybackError.HttpUnauthorized);
                f.Output.Plan(startBeforeCompletion: true);
                f.Output.Fail(PlaybackError.HttpUnauthorized);
                await Until(() => f.Output.Reopens == 2 && f.App.FeedbackProbe.Starts == 2,
                    "cross-type recovery committed");
                var exact = f.App.Resolutions.Where(row =>
                    (Installation(row) == "recovery-script") == scriptFirst).ToArray();
                Check(exact.Length == 2 &&
                    exact[0].GetProperty("sourceKey").GetString() == exact[1].GetProperty("sourceKey").GetString(),
                    "one exact refresh precedes excluding only that provider identity");
                Check(f.Output.OpenedUris.Last().AbsolutePath == fallbackPath &&
                    f.App.Resolutions.Select(row => row.GetProperty("platform").GetString()).Distinct().Single() == "wy" &&
                    f.App.Resolutions.Select(row => row.GetProperty("sourceKey").GetString()).Distinct().Count() == 1,
                    "the other provider remains eligible for the identical platform and recording");
                Check(f.Output.Identity == identity && f.Module.CurrentTitle == title &&
                    f.Output.PositionSeconds == 37 && f.Output.PauseRequested && !f.Output.IsPlaying &&
                    f.App.HistoryWrites == 1 &&
                    f.App.FeedbackProbe.Finishes.SequenceEqual(new[] { "source-change" }),
                    "cross-type recovery preserves queue identity position pause and the single play occurrence");
            });
        yield return Test("unified pending script revocation cancels official recovery without trying another source", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(
                scripts: [new("pending-script-b"), new("available-script-c")],
                qualityRequest: "auto", officialQuality: "flac");
            await f.Module.RefreshSourceManagementAsync();
            var source = f.Module.Sources.Single(row => row.InstallationId == "pending-script-b");
            var before = await f.App.ExecuteAsync("music.sources.list");
            Check(source.Enabled && before.GetProperty("items").EnumerateArray().All(row =>
                row.GetProperty("enabled").GetBoolean()), "both fallback installations are really enabled");
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1 &&
                f.App.FeedbackProbe.Starts == 1, "official playback before pending script revocation");
            Check(f.Output.OpenedUris.Single().AbsolutePath == "/official-wy-flac.wav" &&
                f.Module.CurrentSource == "网易云音乐", "official source initially accepted");
            var publishedSources = new List<string>();
            f.Module.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(MusicCapability.CurrentSource))
                    publishedSources.Add(f.Module.CurrentSource);
            };
            var exact = f.Output.Plan(failure: PlaybackError.HttpUnauthorized);
            var pending = f.Output.Plan(hold: true);
            var unexpectedFallback = f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await exact.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                Check(!pending.Token.IsCancellationRequested &&
                    f.Output.OpenedUris.Last().AbsolutePath == "/pending-script-b-wy-320k.wav" &&
                    f.Module.CurrentSource == "网易云音乐" && f.Output.CandidateStarts == 0 &&
                    f.App.HistoryWrites == 1 && f.App.FeedbackProbe.Starts == 1,
                    "script B is only a pending native replacement, never a published source");
                int resolutions = f.App.Resolutions.Count;
                await f.Module.SetSourceEnabledAsync(source, false).WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => pending.Token.IsCancellationRequested &&
                    !f.Output.HasMedia && f.Module.Status == "播放失败",
                    "pending script revocation terminates the failed recovery");
                var after = await f.App.ExecuteAsync("music.sources.list");
                Check(after.GetProperty("revision").GetInt64() == before.GetProperty("revision").GetInt64() + 1 &&
                    !after.GetProperty("items").EnumerateArray().Single(row =>
                        row.GetProperty("installationId").GetString() == "pending-script-b")
                        .GetProperty("enabled").GetBoolean() &&
                    after.GetProperty("items").EnumerateArray().Single(row =>
                        row.GetProperty("installationId").GetString() == "available-script-c")
                        .GetProperty("enabled").GetBoolean() &&
                    !f.Module.Sources.Single(row => row.InstallationId == "pending-script-b").Enabled,
                    "the real installation owner disables B while C remains enabled");
                Check(f.App.SourceChanges == 1 && f.App.Resolutions.Count == resolutions &&
                    f.Output.NetworkOpens == 1 && f.Output.Reopens == 2 &&
                    !unexpectedFallback.Entered.Task.IsCompleted && f.Output.CandidateStarts == 0 &&
                    f.Module.CurrentSource == "" &&
                    publishedSources.All(value => value == "网易云音乐" || value.Length == 0) &&
                    f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1 &&
                    f.App.FeedbackProbe.Starts == 1 &&
                    !f.App.FeedbackProbe.Finishes.Contains("source-change"),
                    "revocation neither publishes B nor falls through to C or another play occurrence");
            }
            finally { pending.Release.TrySetResult(); }
        });
        foreach (bool scriptFails in new[] { false, true })
            yield return Test("unified unavailable " + (scriptFails ? "script" : "official") +
                " resolution leaves the other type playable", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(
                    scripts: [new("available-script")]);
                f.App.ResolutionFailure = row => (Installation(row) is not null) == scriptFails
                    ? scriptFails ? "MEDIA_UNAVAILABLE" : "RECORDING_UNAVAILABLE" : null;
                await f.Play();
                await Until(() => f.App.HistoryWrites == 1, "mixed resolution fallback");
                Check(f.Output.OpenedUris.Single().AbsolutePath ==
                    (scriptFails ? "/official-wy-320k.wav" : "/available-script-wy-320k.wav") &&
                    f.App.Resolutions.Any(row => Installation(row) is null) &&
                    f.App.Resolutions.Any(row => Installation(row) == "available-script") == !scriptFails,
                    "an available built-in result skips scripts; a built-in miss allows script fallback");
            });
        yield return Test("unified scripts can play a compatible recording discovered during the current resolution", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(
                scripts: [new("discovery-script", platforms: ["wy", "tx"])]);
            f.App.OfferAlternate = true;
            f.App.ResolutionFailure = row => Installation(row) == "discovery-script" &&
                row.GetProperty("platform").GetString() == "tx" ? null : "RECORDING_UNAVAILABLE";
            var song = MusicSongFixture.Online("wy", "Original", "本次发现录音", "fixture",
                native: QualityInfo("Original"));
            var queued = await f.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 });
            Check(queued.GetProperty("ok").GetBoolean(), "original single-recording queue");
            var original = (await f.App.ExecuteAsync("music.queue.get")).GetProperty("current");
            string entryId = original.GetProperty("id").GetString()!;
            string songId = original.GetProperty("song").GetProperty("songId").GetString()!;
            Check(original.GetProperty("song").GetProperty("sources").GetArrayLength() == 1 &&
                original.GetProperty("song").GetProperty("sources")[0].GetProperty("source").GetString() == "wy",
                "the queued song initially has no tx recording");
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await f.Module.PlayQueueSurfaceItemAsync(f.Module.QueueSurfaceRows.Single());
            await Until(() => f.App.HistoryWrites == 1, "newly discovered script recording playback");
            Check(f.App.Resolutions.Any(row => Installation(row) is null &&
                    row.GetProperty("platform").GetString() == "wy") &&
                f.App.Resolutions.Any(row => Installation(row) is null &&
                    row.GetProperty("platform").GetString() == "tx") &&
                f.App.Resolutions.Any(row => Installation(row) == "discovery-script" &&
                    row.GetProperty("platform").GetString() == "wy") &&
                f.App.Resolutions.Any(row => Installation(row) == "discovery-script" &&
                    row.GetProperty("platform").GetString() == "tx"),
                "unavailable wy providers and tx official leave the newly discovered tx script eligible");
            var accepted = (await f.App.ExecuteAsync("music.queue.get")).GetProperty("current");
            Check(f.Output.NetworkOpens == 1 &&
                f.Output.OpenedUris.Single().AbsolutePath == "/discovery-script-tx-320k.wav" &&
                f.Output.Identity == entryId && accepted.GetProperty("id").GetString() == entryId &&
                accepted.GetProperty("song").GetProperty("songId").GetString() == songId &&
                accepted.GetProperty("song").GetProperty("sources").GetArrayLength() == 1 &&
                accepted.GetProperty("song").GetProperty("sources")[0].GetProperty("source").GetString() == "wy" &&
                f.Module.CurrentTitle == "本次发现录音",
                "discovery changes the playback recording without replacing the original queue song or occurrence");
        });
        yield return Test("unified recovery excludes script installation identity without excluding its shared recording", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false,
                scripts: [new("first-script", quality: "flac"), new("second-script")],
                qualityRequest: "auto");
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.FeedbackProbe.Starts == 1,
                "first script accepted");
            string identity = f.Output.Identity!;
            Check(f.Output.OpenedUris.Single().AbsolutePath == "/first-script-wy-flac.wav",
                "higher-quality first installation initially selected");
            f.Output.Plan(failure: PlaybackError.HttpUnauthorized);
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => f.Output.Reopens == 2 && f.App.FeedbackProbe.Starts == 2,
                "second installation accepted");
            Check(f.Output.OpenedUris.Last().AbsolutePath == "/second-script-wy-320k.wav" &&
                f.App.Resolutions.Count(row => Installation(row) == "first-script") == 2 &&
                f.App.Resolutions.Any(row => Installation(row) == "second-script") &&
                f.App.Resolutions.All(row => row.GetProperty("platform").GetString() == "wy") &&
                f.App.Resolutions.Select(row => row.GetProperty("sourceKey").GetString()).Distinct().Count() == 1,
                "failure of one installation does not exclude another installation for the same platform and recording");
            Check(f.Output.Identity == identity && f.App.HistoryWrites == 1 &&
                f.App.Resolutions.All(row => Installation(row) is not null),
                "disabled official platforms do not disable scripts or change queue identity");
        });
        yield return Test("unified manual source choice can select a script recording found by load more", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(
                scripts: [new("original-script", quality: "master"),
                    new("selected-script", quality: "flac", platforms: ["tx"])],
                qualityRequest: "auto");
            f.App.OfferAlternate = true;
            await f.Play();
            await Until(() => f.App.HistoryWrites == 1 && f.App.FeedbackProbe.Starts == 1, "manual mixed source");
            f.Module.OpenSourceChoices();
            while (!f.Module.SourceChoices.Any(row => row.Provider == "selected-script" && row.Selectable) && f.Module.CanLoadMoreSources)
                await f.Module.LoadMoreSourcesAsync();
            var choice = f.Module.SourceChoices.First(row => row.Provider == "selected-script" && row.Selectable);
            string identity = f.Output.Identity!, title = f.Module.CurrentTitle;
            int before = f.App.Resolutions.Count;
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(choice);
            await Until(() => f.App.FeedbackProbe.Starts == 2, "manual mixed recording commit");
            Check(f.Output.OpenedUris.Last().AbsolutePath == "/selected-script-tx-flac.wav" &&
                f.App.Resolutions.Count == before, "a listed choice is handed off without resolving again");
            Check(f.Output.Identity == identity && f.Module.CurrentTitle == title && f.Output.Commits == 1 &&
                f.Output.Reopens == 0 && f.App.HistoryWrites == 1, "manual source choice retains the current queue song");
        });
        foreach (string action in new[] { "selection", "close" })
            yield return Test("unified held script resolution cannot open late media after " + action, async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false,
                    scripts: [new("held-script", quality: "flac")], qualityRequest: "auto");
                f.App.HoldResolution = row => Installation(row) == "held-script" &&
                    row.GetProperty("song").GetProperty("title").GetString() == "合成歌曲";
                var playing = f.Play();
                await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Output.NetworkOpens == 0 && f.App.HistoryWrites == 0,
                    "the only available script remains owned while its resolution is pending");
                Task changed = action == "selection"
                    ? f.Module.PlayOnlineSongAsync("wy", "替换歌曲", "fixture", "",
                        Element(QualityInfo("replacement")))
                    : f.Module.PrepareShutdownAsync();
                await Until(() => f.App.HeldResolutionToken.IsCancellationRequested, "mixed resolution revoked");
                Check(!playing.IsCompleted && f.Output.NetworkOpens == 0,
                    "cancelled noncooperative work remains owned until its response settles");
                f.App.ReleaseRefresh.TrySetResult();
                await Task.WhenAll(playing, changed).WaitAsync(TimeSpan.FromSeconds(5));
                if (action == "selection")
                {
                    await Until(() => f.App.HistoryWrites == 1, "new selection playback");
                    Check(f.Output.NetworkOpens == 1 && f.Output.OpenedTitles.SequenceEqual(new[] { "替换歌曲" }) &&
                        f.Module.CurrentTitle == "替换歌曲", "only the replacement song can open");
                }
                else
                    Check(f.Output.NetworkOpens == 0 && !f.Output.HasMedia && f.App.HistoryWrites == 0,
                        "cancellation cannot publish or open either resolved type");
            });
        foreach (string failure in new[] { "CORRUPT_STORAGE", "COMMIT_UNKNOWN", "QUALITY_POLICY_CHANGED", "CANCELLED" })
            yield return Test("unified candidate " + failure + " is terminal despite another available provider", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false,
                    scripts: [new("failed-script"), new("available-script")]);
                f.App.ResolutionFailure = row => Installation(row) == "failed-script" ? failure : null;
                await f.Play();
                Check(f.App.Resolutions.Count == 1 && Installation(f.App.Resolutions.Single()) == "failed-script" &&
                    f.Output.NetworkOpens == 0 && !f.Output.HasMedia && f.App.HistoryWrites == 0,
                    "storage policy and cancellation failures cannot be converted into fallback to the next provider");
            });
        foreach (string failure in new[] { "CORRUPT_STORAGE", "COMMIT_UNKNOWN", "QUALITY_POLICY_CHANGED" })
            yield return Test("unified late " + failure + " is terminal after another provider resolves", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false,
                    scripts: [new("failed-script"), new("available-script")]);
                f.App.HoldResolution = row => Installation(row) == "failed-script";
                f.App.ResolutionFailure = row => Installation(row) == "failed-script" ? failure : null;
                var playing = f.Play();
                await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => f.App.HeldResolutionToken.IsCancellationRequested, "another provider resolved");
                Check(!playing.IsCompleted && f.Output.NetworkOpens == 0,
                    "the winner waits for admitted noncooperative work to settle");
                f.App.ReleaseRefresh.TrySetResult();
                await playing.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.App.Resolutions.Count == 2 && f.Output.NetworkOpens == 0 &&
                    !f.Output.HasMedia && f.App.HistoryWrites == 0,
                    "a real storage or policy failure is not hidden by the successful provider");
            });
        yield return Test("unified winner cancellation receipt drains the other provider without rejecting success", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false,
                scripts: [new("cancelled-script"), new("available-script")]);
            f.App.HoldResolution = row => Installation(row) == "cancelled-script";
            f.App.ResolutionFailure = row => Installation(row) == "cancelled-script" ? "CANCELLED" : null;
            var playing = f.Play();
            await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.App.HeldResolutionToken.IsCancellationRequested, "winner cancels other provider");
            f.App.ReleaseRefresh.TrySetResult();
            await playing.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.NetworkOpens == 1 && f.Output.IsPlaying &&
                f.Output.OpenedUris.Single().AbsolutePath == "/available-script-wy-320k.wav",
                "an acknowledged first-success cancellation is not a new independent failure");
        });
        foreach (bool scriptWins in new[] { false, true })
            yield return Test("unified mixed preload " + (scriptWins ? "cannot skip a higher quality script" : "retains official cache reuse"), async () =>
            {
                await using var f = await ControlledMusicFixture.Create(playbackCache: true,
                    scripts: [new("preload-script", quality: scriptWins ? "master" : "320k")],
                    qualityRequest: "auto", officialQuality: scriptWins ? "320k" : "flac");
                await f.PlayPair();
                await Until(() => f.App.HistoryWrites == 1, "mixed preload first occurrence");
                int discoveries = f.App.Discoveries;
                f.Output.SetPosition(105);
                var prepared = await f.WaitForPrepared();
                Check(await prepared.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null,
                    "enabled scripts do not prevent provisional official byte preparation");
                Check(f.App.Resolutions.Where(row => row.GetProperty("song").GetProperty("songId").GetString() == f.NextSongId)
                    .All(row => Installation(row) is null) && f.App.Discoveries == discoveries,
                    "preload never invokes scripts or supplementary searches");
                int networkOpens = f.Output.NetworkOpens, transfers = f.Origin.Requests.Count;
                await f.Module.StepAsync(true);
                await Until(() => f.App.HistoryWrites == 2, "mixed preload next occurrence");
                await prepared.Work.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Module.CurrentTitle == "下一首" && f.Origin.Requests.Count == transfers &&
                    f.Origin.Requests.All(row => row.Path.StartsWith("/official-", StringComparison.Ordinal)),
                    "only official media participates in protected preload transfers");
                await using var persisted = await f.Cache!.OpenAsync(
                    "fixture", f.NextSongId!, new string('a', 64), default);
                if (scriptWins)
                    Check(f.Output.ReadOpens == 0 && f.Output.NetworkOpens == networkOpens + 1 &&
                        f.App.Authorizations == 0 && persisted is null &&
                        f.Output.OpenedUris.Last().AbsolutePath == "/preload-script-wy-master.wav" &&
                        f.App.Resolutions.Count(row => Installation(row) == "preload-script") == 2,
                        "lower-quality preloaded bytes never bypass the remaining higher-quality source");
                else
                    Check(f.Output.ReadOpens == 1 && f.Output.NetworkOpens == networkOpens &&
                        f.App.Authorizations == 1 && persisted is not null &&
                        f.App.Resolutions.All(row => Installation(row) is null),
                        "playable prepared bytes are authorized and published without requesting lower-quality scripts");
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
        foreach (var key in new[] { "installationId", "platform", "quality", "qualityPolicySignature" })
            Check(resolutions[0].GetProperty(key).GetRawText() == resolutions[1].GetProperty(key).GetRawText(), "same " + key);
        var song = resolutions[1].GetProperty("song");
        var expectedSource = song.GetProperty("sources").EnumerateArray()
            .Single(source => source.GetProperty("source").GetString() == "wy")
            .GetProperty("sourceKey").GetString();
        Check(resolutions[1].GetProperty("sourceKey").GetString() == expectedSource, "resolved recording pinned");
        Check(resolutions[0].GetProperty("song").GetRawText() == resolutions[1].GetProperty("song").GetRawText(), "same song payload");
    }

    /// <summary>Explores the next tier from the source list and returns the loaded tx choice.</summary>
    private static async Task<MusicSourceChoice> TxChoice(MusicCapability module)
    {
        module.OpenSourceChoices();
        await module.LoadMoreSourcesAsync();
        return module.SourceChoices.First(choice => choice.Selectable && choice.Platform == "QQ");
    }
    private static string? Installation(JsonElement request) =>
        request.TryGetProperty("installationId", out var id) ? id.GetString() : null;
    private static JsonObject QualityInfo(string nativeId) => new()
    {
        ["songmid"] = nativeId,
        ["types"] = new JsonArray
        {
            new JsonObject { ["type"] = "320k" },
            new JsonObject { ["type"] = "flac" },
            new JsonObject { ["type"] = "master" }
        }
    };
    private sealed class ScriptSource(string id, bool enabled = true, string quality = "320k",
        string[]? platforms = null)
    {
        internal readonly string Id = id;
        internal bool Enabled = enabled;
        internal readonly string Quality = quality;
        internal readonly string[] Platforms = platforms ?? ["wy"];
        internal long Generation = 7;
    }
    private sealed class MemoryStore : IDocumentStore
    {
        private readonly Dictionary<string, byte[]> data = [];
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (data) return Task.FromResult(data.TryGetValue(ns, out var bytes)
                ? new DocumentSnapshot(bytes.ToArray(), DocumentFingerprint.FromBytes(bytes)) : new(null, DocumentFingerprint.Missing));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (data)
            {
                var actual = data.TryGetValue(ns, out var old) ? DocumentFingerprint.FromBytes(old) : DocumentFingerprint.Missing;
                if (actual != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
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
        private MusicFixture(bool firstFails, bool bothFail, bool expiry, bool official, string[]? platforms)
        {
            var bytes = Wave();
            Origin.RequireAuthorization = !official; // Official CDN is anonymous; credentials must not be added to make a fixture pass.
            Origin.Route = path => bothFail || firstFails && path == "/old.wav"
                ? new Reply([]) { Status = 401 } : new Reply(bytes, "audio/wav");
            Native = Player(Origin);
            ScriptSource[] sources = [new("fixture-source", enabled: !official, platforms: platforms)];
            App = new(new MusicNativeApplication(Store, Runtime, new NoHttp()), Origin, expiry, official, sources);
            Module = new(App, Store, Native, new Dialogs());
        }
        internal static async Task<MusicFixture> Create(bool firstFails = false, bool bothFail = false, bool expiry = false, bool official = false,
            string[]? platforms = null)
        {
            var f = new MusicFixture(firstFails, bothFail, expiry, official, platforms);
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
        private ControlledMusicFixture(bool playbackCache, bool official, ScriptSource[] sources,
            string qualityRequest, string officialQuality)
        {
            Origin.RequireAuthorization = false;
            App = new(new MusicNativeApplication(Store, Runtime, new NoHttp()), Origin,
                expiry: false, official, sources)
            {
                QualityRequest = qualityRequest,
                OfficialQuality = officialQuality,
                DistinctSourceUrls = sources.Any(source => source.Enabled),
                OfferAlternate = !sources.Any(source => source.Enabled)
            };
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
        internal static async Task<ControlledMusicFixture> Create(bool playbackCache = false,
            bool official = true, ScriptSource[]? scripts = null, string qualityRequest = "320k",
            string officialQuality = "320k")
        {
            var f = new ControlledMusicFixture(playbackCache, official,
                scripts ?? [new("fixture-source", enabled: false)], qualityRequest, officialQuality);
            await f.App.InitializeAsync();
            await f.Module.InitializeAsync(default);
            return f;
        }
        internal Task Play() => Module.PlayOnlineSongAsync("wy", "合成歌曲", "fixture", "",
            App.QualityRequest == "auto" ? Element(QualityInfo("42")) : Element(new { songmid = "42" }));
        internal async Task PlayPair(string nextPlatform = "wy")
        {
            var first = MusicSongFixture.Online("wy", "First", "当前首", "fixture",
                duration: 120, native: App.QualityRequest == "auto" ? QualityInfo("First") : null);
            var next = MusicSongFixture.Online(nextPlatform, "Next", "下一首", "fixture",
                duration: 120, native: App.QualityRequest == "auto" ? QualityInfo("Next") : null);
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
    private sealed class ControlledPlayback : IMusicPlayback, INativePlaybackContinuation, INativePlaybackHandoff
    {
        private ReopenPlan? prepared;
        internal int Prepares, Commits, Discards;
        // A prepared input is invisible until commit: the old input keeps playing and publishing.
        public async Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token)
        {
            if (plans.Count == 0) throw new InvalidOperationException("missing controlled handoff plan");
            var plan = plans.Dequeue(); plan.Token = token; Prepares++;
            OpenedUris.Add(uri);
            plan.Entered.TrySetResult();
            if (plan.Hold) await plan.Release.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (plan.Failure is { } failure) throw new MusicOperationException(failure);
            prepared = plan;
        }
        public double PreparedHandoffDurationSeconds => 0;
        public async Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token)
        {
            await using (lease) await PrepareHandoffAsync(new Uri("https://cache.invalid/fixture"), new Dictionary<string, string>(), token);
        }
        public Task CommitHandoffAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (prepared is null || !HasMedia) throw new MusicOperationException(PlaybackError.Superseded);
            prepared = null; Commits++;
            generation++; LastErrorCode = null;
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            return Task.CompletedTask;
        }
        public Task DiscardHandoffAsync() { if (prepared is not null) Discards++; prepared = null; return Task.CompletedTask; }
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
        public double DurationSeconds => 120;
        public int Volume { get; set; } = 50;
        public bool PauseRequested { get; private set; }
        public string? LastErrorCode { get; private set; }
        internal int Reopens, CandidateStarts;
        internal int NetworkOpens, ReadOpens;
        internal readonly List<Uri> OpenedUris = [];
        internal readonly List<string> OpenedTitles = [];
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
            OpenedUris.Add(uri);
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
            OpenedUris.Add(uri);
            OpenedTitles.Add(title);
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
    private sealed class MusicApp(MusicNativeApplication inner, Origin origin, bool expiry, bool official,
        ScriptSource[] sources) : MusicApplicationForwarder(inner)
    {
        internal readonly List<JsonElement> Resolutions = [];
        internal bool HoldRefresh;
        internal bool RepeatExpiry;
        internal bool MatchMissing;
        internal readonly List<string> MatchPlatforms = [];
        internal int Discoveries;
        internal bool DistinctSourceUrls, OfferAlternate = true;
        internal string QualityRequest = "320k", OfficialQuality = "320k";
        internal Func<JsonElement, bool>? HoldResolution;
        internal Func<JsonElement, string?>? ResolutionFailure;
        internal CancellationToken HeldResolutionToken;
        internal string? Poison;
        internal string? CacheableSongId;
        internal int HistoryWrites, SourceChanges, ConfirmWrites, Authorizations;
        internal readonly FeedbackProbe FeedbackProbe = new();
        private readonly HashSet<string> revokedOfficial = [];
        private event Action<string>? accountInvalidated;
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
            var reply = await inner.ExecuteAsync("music.quality.settings.get", cancellationToken: token);
            Check(reply.GetProperty("ok").GetBoolean(), "native quality fixture read: " + reply.GetRawText());
            var policy = JsonNode.Parse(reply.GetProperty("settings").GetRawText())!.AsObject();
            policy["request"] = QualityRequest;
            policy["ifRevision"] = policy["revision"]!.DeepClone();
            policy.Remove("schema"); policy.Remove("revision");
            var saved = await inner.ExecuteAsync("music.quality.settings.save", policy, token);
            Check(saved.GetProperty("ok").GetBoolean(), "native quality fixture setup");
            var online = await inner.ExecuteAsync("music.settings.save", new
            {
                ifRevision = reply.GetProperty("onlineSettings").GetProperty("revision").GetInt64(),
                lyrics = false
            }, token);
            Check(online.GetProperty("ok").GetBoolean() &&
                online.GetProperty("settings").GetProperty("schema").GetInt32() == 3,
                "native schema 3 online settings fixture setup");
            if (!official)
            {
                var current = await inner.ExecuteAsync("music.officialSources.settings.get", cancellationToken: token);
                var disabled = await inner.ExecuteAsync("music.officialSources.settings.save", new
                {
                    ifRevision = current.GetProperty("settings").GetProperty("revision").GetInt64(),
                    enabled = Array.Empty<string>()
                }, token);
                Check(disabled.GetProperty("ok").GetBoolean(), "native official enablement fixture setup");
            }
        }
        private Task<JsonElement> ReadQualitySettings(CancellationToken token) =>
            inner.ExecuteAsync("music.quality.settings.get", cancellationToken: token);
        private async Task<string[]> EnabledOfficial(CancellationToken token)
        {
            var reply = await inner.ExecuteAsync("music.officialSources.settings.get", cancellationToken: token);
            Check(reply.GetProperty("ok").GetBoolean(), "native official enablement fixture read");
            return reply.GetProperty("settings").GetProperty("enabled").EnumerateArray()
                .Select(value => value.GetString()!).ToArray();
        }
        private async Task WaitForResolution(JsonElement input, bool fresh, CancellationToken token)
        {
            bool hold = fresh && HoldRefresh || HoldResolution?.Invoke(input) == true;
            if (!fresh && !hold) return;
            if (hold) HeldResolutionToken = token;
            RefreshEntered.TrySetResult();
            if (hold) await ReleaseRefresh.Task; // Deliberately noncooperative: the owner must drain and reject late results.
        }
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var input = Element(parameters ?? new { });
            // The native owner's complete quality reply and all failure fields
            // remain authoritative; only the existing script/media port is fake.
            if (official && method == "music.online.song")
            {
                var request = JsonNode.Parse(input.GetRawText())!.AsObject();
                request["duration"] = 120;
                return await inner.ExecuteAsync(method, request, cancellationToken);
            }
            if (method.StartsWith("music.quality.", StringComparison.Ordinal))
                return await inner.ExecuteAsync(method, parameters, cancellationToken);
            if (method == "music.official.cacheContext")
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
            if (method == "music.official.authorize")
            {
                string platform = input.GetProperty("platform").GetString()!;
                if (revokedOfficial.Contains(platform)) return Element(new { ok = false, code = "AUTH_CHANGED" });
                Authorizations++;
                return Element(new { ok = true });
            }
            if (method == "music.official.confirm")
            {
                ConfirmWrites++;
                return Element(new { ok = true });
            }
            if (method == "music.official.candidates")
            {
                var enabled = await EnabledOfficial(cancellationToken);
                var raw = NativeMusicSong.Normalize(JsonNode.Parse(input.GetProperty("song").GetRawText()));
                var recordings = raw["sources"]!.AsArray();
                var policy = await ReadQualitySettings(cancellationToken);
                return Element(new { ok = true, song = raw, qualityPolicySignature = policy.GetProperty("signature").GetString(),
                    candidates = recordings.OfType<JsonObject>().Where(r => enabled.Contains(LJ.S(r["source"])))
                        .Select(r => new { platform = LJ.S(r["source"]),
                        sourceKey = LJ.S(r["sourceKey"]), generation = 7, scope = "fixture" }),
                    authorizationRequired = enabled.Length == 0 });
            }
            if (method == "music.songs.complete")
            {
                // The one supplementary search of a selection: an alternate tx recording and,
                // for scripts, a matched recording of each requested missing platform.
                Discoveries++;
                var raw = JsonNode.Parse(input.GetProperty("song").GetRawText())!.AsObject();
                var platforms = input.GetProperty("platforms").EnumerateArray().Select(value => value.GetString()!).ToArray();
                foreach (string platform in platforms)
                {
                    if (raw["sources"]!.AsArray().OfType<JsonObject>().Any(r => LJ.S(r["source"]) == platform)) continue;
                    bool alternate = platform == "tx" && OfferAlternate;
                    if (!alternate && !MatchMissing) continue;
                    if (!alternate) MatchPlatforms.Add(platform);
                    var other = MusicSongFixture.Online(platform, alternate ? "Other" : "Matched", LJ.S(raw["title"]),
                        LJ.S(raw["artist"]), LJ.S(raw["album"]), LJ.N(raw["duration"]), LJ.S(raw["cover_url"]),
                        QualityRequest == "auto" ? QualityInfo(alternate ? "Other" : "Matched") : null);
                    var compatible = new NativeMusicSong.Recordings(raw);
                    if (compatible.TryAddSource(other["sources"]![0]!.AsObject())) raw = compatible.ToSong();
                }
                return Element(new { ok = true, song = NativeMusicSong.Normalize(raw), failures = Array.Empty<object>() });
            }
            if (method == "music.official.resolve")
            {
                if (input.TryGetProperty("cacheOnly", out var cacheOnly) && cacheOnly.GetBoolean())
                    return Element(new { ok = false, code = "CACHE_MISS" });
                Resolutions.Add(input);
                string platform = input.GetProperty("platform").GetString()!;
                await WaitForResolution(input, Resolutions.Count(row => Installation(row) is null) > 1, cancellationToken);
                if (!(await EnabledOfficial(cancellationToken)).Contains(platform))
                    return Element(new { ok = false, code = "OFFICIAL_SOURCE_DISABLED" });
                if (ResolutionFailure?.Invoke(input) is { } failure)
                    return Element(new { ok = false, code = failure });
                var policy = await ReadQualitySettings(cancellationToken);
                string songId = input.GetProperty("song").GetProperty("songId").GetString()!;
                bool full = songId == CacheableSongId;
                return Element(new { ok = true, kind = "official", platform,
                    songId,
                    sourceKey = input.GetProperty("sourceKey").GetString(), generation = 7, scope = "fixture",
                    quality = OfficialQuality, requestedQuality = input.GetProperty("quality").GetString(), actualQuality = OfficialQuality,
                    qualityPolicySignature = policy.GetProperty("signature").GetString(),
                    qualityCacheSignature = new string('a', 64), playbackVerified = false, qualityVerified = true,
                    url = origin.Uri(DistinctSourceUrls ? $"official-{platform}-{OfficialQuality}.wav" :
                        platform == "tx" ? "other.wav" : Resolutions.Count == 1 ? "old.wav" : "new.wav").AbsoluteUri,
                    headers = new { }, expiresAt = (long?)null, duration = 120d,
                    size = full ? Wave().Length : 0, full, licenseExpiresAt = (long?)null,
                    delivery = platform + "-official", offlineAllowed = false,
                    authorizationExpiresAt = (long?)null });
            }
            // The shared module is replaced by these synthetic installations; the native
            // capability still consumes the actual list/item contract.
            if (method == "music.sources.list")
                return Element(new { ok = true, revision = SourceChanges, items = sources.Select(SourceItem) });
            if (method == "music.sources.detail")
                return Element(new { ok = true, item = SourceItem(sources.Single(row => row.Id == input.GetProperty("installationId").GetString())) });
            if (method is "music.sources.rollback" or "music.sources.disable" or "music.sources.enable")
            {
                // Synthetic version change; the real capability owns cancellation and drain.
                var source = sources.Single(row => row.Id == input.GetProperty("installationId").GetString());
                SourceChanges++;
                if (method == "music.sources.disable") source.Enabled = false;
                if (method == "music.sources.enable") source.Enabled = true;
                if (method == "music.sources.rollback") source.Generation++;
                return Element(new { ok = true, item = SourceItem(source) });
            }
            if (method == "music.online.resolve")
            {
                Resolutions.Add(input);
                string id = input.GetProperty("installationId").GetString()!;
                string platform = input.GetProperty("platform").GetString()!;
                var declaration = sources.Single(row => row.Id == id);
                bool fresh = Resolutions.Count(row => Installation(row) == id) > 1;
                var quality = await ReadQualitySettings(cancellationToken);
                if (!quality.GetProperty("ok").GetBoolean()) return quality;
                if (!declaration.Enabled) return Element(new { ok = false, code = "SOURCE_DISABLED" });
                if (!declaration.Platforms.Contains(platform) || input.GetProperty("quality").GetString() != declaration.Quality)
                    return Element(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
                if (input.TryGetProperty("generation", out _)) return Element(new { ok = false, code = "INVALID_REQUEST" });
                string policy = quality.GetProperty("signature").GetString()!;
                var song = input.GetProperty("song");
                if (!song.GetProperty("sources").EnumerateArray().Any(row => row.GetProperty("source").GetString() == platform))
                    return Element(new { ok = false, code = "RECORDING_UNAVAILABLE" });
                var source = song.GetProperty("sources").EnumerateArray().Single(s =>
                    s.GetProperty("source").GetString() == platform &&
                    (!input.TryGetProperty("sourceKey", out var key) || s.GetProperty("sourceKey").GetString() == key.GetString()));
                string sourceKey = source.GetProperty("sourceKey").GetString()!;
                var songNode = JsonNode.Parse(song.GetRawText())!.AsObject();
                string cache = LJ.Digest(LJ.Obj(("contract", "music.quality.cache.v1"), ("policy", policy),
                    ("installation", id), ("platform", platform), ("revision", 1), ("generation", declaration.Generation),
                    ("sourceKey", sourceKey), ("recording", source.GetProperty("musicInfo")),
                    ("songId", songNode["songId"]),
                    ("duration", songNode["duration"]), ("quality", declaration.Quality)));
                if (input.TryGetProperty("qualityPolicySignature", out var expectedPolicy) && expectedPolicy.GetString() != policy)
                    return Element(new { ok = false, code = "QUALITY_POLICY_CHANGED" });
                if (input.TryGetProperty("qualityCacheSignature", out var expectedCache) && expectedCache.GetString() != cache)
                    return Element(new { ok = false, code = "QUALITY_CACHE_CHANGED" });
                await WaitForResolution(input, fresh, cancellationToken);
                if (ResolutionFailure?.Invoke(input) is { } failure)
                    return Element(new { ok = false, code = failure });
                var result = JsonSerializer.SerializeToNode(new { ok = true,
                    url = origin.Uri(DistinctSourceUrls ? $"{id}-{platform}-{declaration.Quality}.wav" :
                        fresh ? "new.wav" : "old.wav").AbsoluteUri, headers = origin.Headers,
                    installationId = id, platform, generation = declaration.Generation, quality = declaration.Quality,
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
    private static object SourceItem(ScriptSource source) => new
    {
        installationId = source.Id, name = source.Id, version = "1", author = "", description = "", homepage = "",
        origin = new { kind = "file" }, enabled = source.Enabled, state = source.Enabled ? "ready" : "disabled",
        generation = source.Enabled ? source.Generation : 0, contentHash = new string('c', 64),
        executionHash = new string('e', 63) + (char)('0' + source.Generation % 10), rollbackAvailable = true,
        sites = source.Platforms.ToDictionary(platform => platform,
            _ => new { actions = new[] { "musicUrl" }, qualitys = new[] { source.Quality } }),
        check = (object?)null, lastFailure = (string?)null
    };
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

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
public static partial class PlaybackRecoveryTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in CatalogNextRecoveryCases()) yield return test;
        foreach (var test in SelectionRegressionCases()) yield return test;
        foreach (var test in SourceLifecycleRegressionCases()) yield return test;
        foreach (var test in PlaybackBoundaryRegressionCases()) yield return test;
        foreach (var test in PlaybackInteractionRegressionCases()) yield return test;
        foreach (var test in SmartPlaybackParityCases()) yield return test;
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
            await native.CommitHandoffAsync(() => { }, default);
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
            await Reject(() => native.CommitHandoffAsync(() => { }, default), "cancelled preparation");
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
            Check(f.App.Resolutions.Count == 4 && f.Module.CurrentSource == "QQ 音乐", "exact and remaining high level before cross-platform fallback");
            Check(f.App.Resolutions.Take(3).All(r => r.GetProperty("platform").GetString() == "wy") &&
                f.App.Resolutions.Take(3).Select(r => r.GetProperty("quality").GetString()).SequenceEqual(new[] { "320k", "320k", "192k" }) &&
                f.App.Resolutions[3].GetProperty("platform").GetString() == "tx", "a representation failure does not exclude the other request level");
            Check(f.App.HistoryWrites == 1 && f.Runtime.Calls == 0, "one occurrence, no Node");
        });
        yield return Test("official automatic all failed sources terminate rather than retrying excluded candidates", async () =>
        {
            await using var f = await MusicFixture.Create(bothFail: true, official: true);
            await f.Play();
            Check(!f.Native.HasMedia && f.App.Resolutions.Count == 7 && f.App.HistoryWrites == 0 &&
                f.App.Resolutions.GroupBy(r => (r.GetProperty("platform").GetString(), r.GetProperty("quality").GetString()))
                    .All(group => group.Count() == (group.Key == ("wy", "320k") ? 2 : 1)),
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
            f.Output.Plan(failure: PlaybackError.HttpUnauthorized); // The remaining 192k request of the same high tier.
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
            Check(f.Output.Reopens == 3 && f.App.Resolutions.Count == 4 &&
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
        foreach (bool holdPeek in new[] { true, false })
            yield return Test(holdPeek
                ? "playlist next cancels an unobserved old peek before queue reconciliation"
                : "playlist next cancels observed physical preparation before queue reconciliation", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(playbackCache: true,
                    catalogHttp: new PlaylistNextHttp());
                await f.Module.NavigateAsync("catalog-home", default);
                await f.Module.HomeInitialization; await f.Module.CatalogEntryInitialization;
                var card = f.Module.CatalogSections.SelectMany(section => section.Items)
                    .Single(item => item.Kind == "playlist");
                await f.PlayPair();
                await Until(() => f.App.HistoryWrites == 1 && f.App.ConfirmWrites == 1, "playlist next current media");
                var probe = f.App.PlaylistNext = new PlaylistNextProbe(holdPeek);
                f.HoldValidation = !holdPeek;
                f.Output.SetPosition(105);
                MusicAudioFileCache.PreparedFile? prepared = null;
                if (holdPeek) await probe.PeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                else prepared = await f.WaitForPrepared();
                var attempt = Field(f.Module, "musicPreload") ?? throw new InvalidOperationException("missing old preload");
                var oldToken = ((CancellationTokenSource)Field(attempt, "Cancellation")!).Token;
                Check((bool)Field(attempt, "QueueObserved")! == !holdPeek && !oldToken.IsCancellationRequested,
                    "fixture stops at the required unobserved peek or observed physical preparation boundary");
                if (holdPeek)
                    Check(f.Prepared is null && !f.ValidationEntered.Task.IsCompleted &&
                        !probe.PeekToken.IsCancellationRequested, "old peek has not reached physical preparation");
                else
                    Check(!prepared!.Work.IsCompleted && !f.ValidationToken.IsCancellationRequested,
                        "observed preparation is physically suspended before validation completes");
                var before = (f.Output.Identity, f.Output.NetworkOpens, f.Output.ReadOpens, f.Output.Reopens,
                    f.Output.Commits, f.Output.CandidateStarts, f.App.HistoryWrites, f.App.ConfirmWrites);
                int resolutions = f.App.Resolutions.Count;
                var action = f.Module.QueueCatalogCardAsync(card);
                try
                {
                    await probe.QueueReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(probe.CommitReceipt is { } receipt && receipt.GetProperty("ok").GetBoolean() &&
                        probe.CatalogMethods.SequenceEqual(new[] { "music.catalog.playlist.prepare", "music.catalog.playlist.commit" }),
                        "public card action completed one real native prepare and next commit before reconciliation");
                    Check(probe.CommitInput is { } input && input.GetProperty("action").GetString() == "next" &&
                        input.EnumerateObject().Select(property => property.Name).OrderBy(name => name)
                            .SequenceEqual(new[] { "action", "expectedVersion", "token" }),
                        "consumer used the actual token-only next commit contract");
                    Check(!action.IsCompleted && !probe.ReleaseQueueRead.Task.IsCompleted && oldToken.IsCancellationRequested,
                        "successful next cancels the old attempt before any post-commit queue snapshot can return");
                    if (holdPeek) Check(probe.PeekToken.IsCancellationRequested, "in-flight old peek loses authority immediately");
                    else Check(f.ValidationToken.IsCancellationRequested && f.Prepared is null,
                        "in-flight physical preparation loses authority and its playable handle immediately");
                    Check(before == (f.Output.Identity, f.Output.NetworkOpens, f.Output.ReadOpens, f.Output.Reopens,
                        f.Output.Commits, f.Output.CandidateStarts, f.App.HistoryWrites, f.App.ConfirmWrites) &&
                        f.Output.HasMedia && f.Output.IsPlaying, "playlist next does not restart or republish current media");

                    // Both old operations deliberately return after cancellation while the new queue read is still held.
                    probe.ReleasePeek.TrySetResult(); f.ValidationRelease.TrySetResult();
                    if (prepared is not null) await prepared.Work.WaitAsync(TimeSpan.FromSeconds(5));
                    await Until(() => Field(f.Module, "musicPreload") is null, "late old preload is drained");
                    await using var absent = await f.Cache!.OpenAsync("fixture", f.NextSongId!, new string('a', 64), default);
                    Check(absent is null && f.Prepared is null && !Directory.EnumerateFiles(f.CacheRoot!, "*.audio").Any() &&
                        f.Output.HasMedia && f.Output.IsPlaying &&
                        f.App.Resolutions.Count == resolutions &&
                        before == (f.Output.Identity, f.Output.NetworkOpens, f.Output.ReadOpens, f.Output.Reopens,
                            f.Output.Commits, f.Output.CandidateStarts, f.App.HistoryWrites, f.App.ConfirmWrites),
                        "late old peek or physical validation cannot resolve again publish cached bytes or reopen current media");
                }
                finally
                {
                    probe.ReleasePeek.TrySetResult(); f.ValidationRelease.TrySetResult(); probe.ReleaseQueueRead.TrySetResult();
                    await action.WaitAsync(TimeSpan.FromSeconds(5));
                }
                Check(probe.CatalogMethods.SequenceEqual(new[] { "music.catalog.playlist.prepare",
                    "music.catalog.playlist.commit", "music.catalog.playlist.discard" }), "committed stage is released once");
                var queue = await f.App.ExecuteAsync("music.queue.get", new { limit = 100 });
                Check(queue.GetProperty("ok").GetBoolean() && queue.GetProperty("currentId").GetString() == before.Identity &&
                    queue.GetProperty("entries").EnumerateArray().Select(entry => MusicSongFixture.NativeId(entry.GetProperty("song")))
                        .SequenceEqual(new[] { "First", "8001", "8002", "Next" }),
                    "complete staged playlist is inserted in order behind the unchanged current occurrence");
                var next = await f.App.ExecuteAsync("music.queue.peekNext");
                Check(next.GetProperty("ok").GetBoolean() &&
                    MusicSongFixture.NativeId(next.GetProperty("next").GetProperty("song")) == "8001",
                    "new next authority points to the first inserted playlist song");
                await f.Module.StepAsync(true);
                await Until(() => f.App.HistoryWrites == before.HistoryWrites + 1 &&
                    f.App.ConfirmWrites == before.ConfirmWrites + 1, "inserted first song starts");
                Check(f.Output.Identity == next.GetProperty("next").GetProperty("id").GetString() &&
                    f.Module.CurrentTitle == "插入首曲" && f.Output.NetworkOpens == before.NetworkOpens + 1 &&
                    f.Output.ReadOpens == before.ReadOpens && f.Output.Reopens == before.Reopens,
                    "actual next playback consumes the inserted first song and never the cancelled old prepared bytes");
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
        foreach (var entry in new (double Expected, double? Receipt, bool Accepted)[]
        {
            (180, 185, true), (180, 190, true), (180, 191, false),
            (180, 170, true), (180, 169, false), (1200, 1211, false),
            (180, 0, true), (180, null, true), (0, 1211, true),
        })
            yield return Test($"script receipt duration {entry.Expected}->{entry.Receipt?.ToString() ?? "unknown"} is admitted before opening", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false, scripts: [new("duration-script")]);
                f.App.ScriptDuration = entry.Receipt;
                f.Output.SetDuration(0);
                await PlayDurationSong(f, entry.Expected);
                if (entry.Accepted) await Until(() => f.App.HistoryWrites == 1, "accepted script receipt");
                Check(f.Output.NetworkOpens == (entry.Accepted ? 1 : 0) &&
                    f.App.HistoryWrites == (entry.Accepted ? 1 : 0), "mismatched receipt must never open or record playback");
                if (entry.Accepted)
                    Check(f.Module.CurrentQualityLabel == "音质未知", "known duration is not proof of the requested quality");
            });
        foreach (var entry in new (double Expected, double Actual, bool Accepted)[]
        {
            (180, 185, true), (180, 190, true), (180, 191, false),
            (180, 170, true), (180, 169, false), (1200, 1211, false),
            (180, 0, true), (0, 1211, true),
        })
        {
            yield return Test($"script prepared duration {entry.Expected}->{entry.Actual} uses ten seconds", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false,
                    scripts: [new("original-script", quality: "master"), new("replacement-script", quality: "flac")],
                    qualityRequest: "auto");
                f.Output.SetDuration(0);
                await PlayDurationSong(f, entry.Expected);
                await Until(() => f.App.HistoryWrites == 1 && f.Module.CanSwitchSource, "healthy original script");
                f.Output.PreparedDuration = entry.Actual;
                f.Output.Plan();
                await f.Module.SwitchQualityAsync("lossless");
                Check(f.Output.Prepares == 1 && f.Output.Commits == (entry.Accepted ? 1 : 0) &&
                    f.Output.Discards == (entry.Accepted ? 0 : 1) && f.Output.IsPlaying &&
                    f.App.HistoryWrites == 1, "ten-second handoff boundary keeps a healthy original on rejection");
                Check(f.Module.CurrentQualityLabel == "音质未知", "neither prepared duration nor requested quality proves quality");
            });
            yield return Test($"script duration event {entry.Expected}->{entry.Actual} uses ten seconds", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false, scripts: [new("duration-script")]);
                f.Output.SetDuration(0);
                await PlayDurationSong(f, entry.Expected);
                await Until(() => f.App.HistoryWrites == 1 && f.Module.CanSwitchSource, "script awaiting duration");
                f.Output.SetDuration(entry.Actual);
                if (!entry.Accepted)
                    await Until(() => !f.Output.HasMedia && f.Module.Status == "播放失败", "wrong script duration retired");
                Check(f.Output.HasMedia == entry.Accepted && f.Output.NetworkOpens == 1 && f.Output.Reopens == 0,
                    "mismatch retires the only source, while allowed and unknown duration never triggers a refresh");
                if (entry.Accepted) Check(f.Module.CanSwitchSource, "allowed duration must not start recovery");
            });
        }
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
                // This is healthy mapping reuse, not duration-mismatch recovery.
                var song = MusicSongFixture.Online("wy", "Original", "目标平台", "fixture", duration: 120);
                if (mapped)
                    song["sources"]!.AsArray().Add(MusicSongFixture.Online("tx", "Matched", "目标平台", "fixture", duration: 120)
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
                // The fixture's official provider returns the same flac media for every
                // request. After its exact refresh fails, explicitly make all its other
                // levels unavailable so this case continues to isolate cross-type commit.
                if (!scriptFirst)
                    f.App.ResolutionFailure = row => Installation(row) is null &&
                        f.App.Resolutions.Count(value => Installation(value) is null) > 2 ? "MEDIA_UNAVAILABLE" : null;
                f.Output.Fail(PlaybackError.HttpUnauthorized);
                await Until(() => f.Output.Reopens == 2 && f.App.FeedbackProbe.Starts == 2,
                    "cross-type recovery committed");
                var exact = f.App.Resolutions.Where(row =>
                    (Installation(row) == "recovery-script") == scriptFirst).ToArray();
                Check((scriptFirst ? exact.Length == 2 : exact.Select(row => row.GetProperty("quality").GetString())
                    .SequenceEqual(new[] { "master", "flac", "dolby", "sky", "vivid", "jyeffect", "flac24bit", "flac", "320k" })) &&
                    exact[0].GetProperty("sourceKey").GetString() == exact[1].GetProperty("sourceKey").GetString(),
                    "one exact refresh precedes exhausting that provider's remaining request levels: " +
                    string.Join(",", exact.Select(row => row.GetProperty("quality").GetString())));
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
            f.App.ResolutionFailure = row => Installation(row) is null &&
                f.App.Resolutions.Count(value => Installation(value) is null) > 2 ? "MEDIA_UNAVAILABLE" : null;
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
            var song = MusicSongFixture.Online("wy", "Original", "本次发现录音", "fixture", duration: 120,
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
                if (scriptWins)
                    await Until(() => f.Store.ReadAsync(MusicAudioFileCache.Namespace).GetAwaiter().GetResult().Data is not null,
                        "current script completes its bounded file producer before jumping to the final twenty seconds");
                int beforePreload = f.Origin.Requests.Count;
                f.Output.SetPosition(105);
                var prepared = await f.WaitForPrepared();
                Check(await prepared.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null,
                    "enabled scripts do not prevent provisional official byte preparation");
                Check(f.App.Resolutions.Where(row => row.GetProperty("song").GetProperty("songId").GetString() == f.NextSongId)
                    .All(row => Installation(row) is null) && f.App.Discoveries == discoveries,
                    "preload never invokes scripts or supplementary searches");
                Check(f.Origin.Requests.Skip(beforePreload).Count() == 1 &&
                    f.Origin.Requests.Skip(beforePreload).All(row => row.Path.StartsWith("/official-", StringComparison.Ordinal)),
                    "only official media participates in provisional preload transfers");
                int networkOpens = f.Output.NetworkOpens, transfers = f.Origin.Requests.Count;
                await f.Module.StepAsync(true);
                await Until(() => f.App.HistoryWrites == 2, "mixed preload next occurrence");
                await prepared.Work.WaitAsync(TimeSpan.FromSeconds(5));
                if (scriptWins)
                    await Until(() => f.Cache!.ReadScriptHistoryAsync(f.NextSongId!, default).GetAwaiter().GetResult().Length == 1,
                        "only actually played next script becomes historical bytes");
                Check(f.Module.CurrentTitle == "下一首" && f.Origin.Requests.Count == transfers + (scriptWins ? 1 : 0) &&
                    f.Origin.Requests.Skip(transfers).All(row => row.Path == "/preload-script-wy-master.wav"),
                    "prepared bytes need no second transfer; an actually played script can create its own history");
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

    private static IEnumerable<TestCase> PlaybackBoundaryRegressionCases()
    {
        foreach (string entry in new[] { "button", "system", "lyric" })
            yield return Test("playback boundary excluded source cannot resume through " + entry, async () =>
            {
                const string id = "90c48b2e-a8ee-430c-8652-43599ce3caf8";
                await using var f = await ControlledMusicFixture.Create(official: false, scripts: [new(id)]);
                await f.Play();
                await f.Module.ReportWrongSourceAsync();
                await f.Module.RefreshSourceExclusionsAsync();
                Check(f.Module.SourceExclusions.Count == 1 && f.Output.PauseRequested && !f.Output.IsPlaying && !f.Output.HasMedia,
                    "durable exclusion succeeded and absence of alternatives retained a paused occurrence with retired wrong audio");
                var lyric = new MusicLyricRow(25, "fixture", "");
                f.Module.Lyrics.Add(lyric);
                int before = f.App.Resolutions.Count;
                string? identity = f.Output.Identity;
                async Task Resume(int expectedSystemPlays)
                {
                    if (entry == "button") await f.Module.TogglePlaybackAsync();
                    else if (entry == "lyric") await f.Module.SeekLyricAsync(lyric);
                    else
                    {
                        var backend = new Backend();
                        int plays = 0;
                        await using var system = new SystemMediaSession(f.Output, backend,
                            [new("music", Play: async _ => { plays++; await f.Module.ResumePlaybackAsync(); })]);
                        backend.Send(SystemPlaybackCommand.Play);
                        await Until(() => (int)Field(system, "pending")! == 0, "system Play command settled");
                        Check(system.LastErrorCode is null && plays == expectedSystemPlays,
                            "system Play calls its owner only when media was explicitly prepared");
                    }
                }
                await Resume(expectedSystemPlays: 0);
                Check(!f.Output.IsPlaying && f.Output.PauseRequested && f.Output.Identity == identity &&
                    f.App.Resolutions.Count == before && f.Module.SourceExclusions.Count == 1,
                    "Play cannot clear the exclusion or resume wrong audio");
                if (entry == "button")
                    Check(f.Module.Status.Contains("已排除", StringComparison.Ordinal), "button resume reports the exclusion");
                else if (entry == "lyric")
                    Check(!f.Module.CanSeek, "a retired input also disables lyric seek");
                await f.Module.RestoreSourceExclusionAsync(f.Module.SourceExclusions.Single());
                Check(!f.Output.IsPlaying && !f.Output.HasMedia && f.Module.SourceExclusions.Count == 0,
                    "restore alone never revives the retired wrong input");
                f.Module.OpenSourceChoices();
                f.Output.Plan();
                await f.Module.SelectSourceChoiceAsync(f.Module.SourceChoices.Single(row => row.Key == "auto"));
                Check(!f.Output.IsPlaying && f.Output.HasMedia && f.Output.Commits == 1 &&
                    f.App.Resolutions.Count == before + 1, "explicitly prepare a fresh restored source while retaining pause");
                await Resume(expectedSystemPlays: 1);
                Check(f.Output.IsPlaying && f.Output.Identity == identity && f.App.Resolutions.Count == before + 1,
                    "a later explicit resume uses the newly prepared restored source");
            });
        foreach (var (action, intent) in new[] { ("enabled", "automatic"), ("disable", "automatic"),
            ("remove", "automatic"), ("disable", "quality"), ("remove", "source") })
            yield return Test("playback boundary network failure uses completed history " + action + " " + intent, async () =>
            {
                const string id = "4d769f77-3063-4fdb-8579-7422a1f31382";
                await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                    scripts: [new(id, quality: "flac")], qualityRequest: "flac");
                await f.Play();
                await CompletedHistory(f);
                if (intent == "quality") await f.Module.SwitchQualityAsync("lossless");
                if (intent == "source")
                {
                    f.Module.OpenSourceChoices();
                    await f.Module.SelectSourceChoiceAsync(f.Module.SourceChoices.Single(row => row.Key != "auto" && row.Selectable));
                }
                await f.Module.RefreshSourceManagementAsync();
                var row = f.Module.Sources.Single(row => row.InstallationId == id);
                if (action == "disable") await f.Module.SetSourceEnabledAsync(row, false);
                if (action == "remove") await f.Module.RemoveSourceAsync(row);
                await f.Output.SeekAsync(37);
                bool paused = intent != "automatic";
                if (paused) await f.Module.TogglePlaybackAsync();
                int before = f.App.Resolutions.Count;
                string? identity = f.Output.Identity;
                f.Output.Plan(startBeforeCompletion: true);
                f.Output.Fail(PlaybackError.HttpUnauthorized);
                await Until(() => f.Module.CanSwitchSource || !f.Output.HasMedia, "local recovery settled");
                Check(f.Output.HasMedia && f.Output.IsPlaying == !paused && f.Output.PauseRequested == paused &&
                    f.Output.ReadOpens == 1 && f.Output.Reopens == 1 && f.App.Resolutions.Count == before &&
                    f.Output.Identity == identity && f.Output.PositionSeconds == 37 && f.Module.CurrentQualityLabel == "无损",
                    "complete independent bytes recover at the same position and intent without parsing a script");
            });
        foreach (bool disabled in new[] { false, true })
            yield return Test("playback boundary corrupt completed bytes fall back only with current script authority " + disabled, async () =>
            {
                const string id = "90193910-c59c-473c-9ab8-c206209daf0a";
                await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                    scripts: [new(id, quality: "flac")], qualityRequest: "flac");
                await f.Play(); await CompletedHistory(f);
                if (disabled)
                {
                    await f.Module.RefreshSourceManagementAsync();
                    await f.Module.SetSourceEnabledAsync(f.Module.Sources.Single(row => row.InstallationId == id), false);
                }
                foreach (string file in Directory.GetFiles(f.CacheRoot!, "*.audio")) await File.WriteAllBytesAsync(file, [1, 2, 3]);
                int before = f.App.Resolutions.Count;
                f.Output.Plan(startBeforeCompletion: true);
                f.Output.Fail(PlaybackError.HttpUnauthorized);
                await Until(() => f.Module.CanSwitchSource || !f.Output.HasMedia, "corrupt file recovery settled");
                Check(f.Output.ReadOpens == 0 && f.App.Resolutions.Count == before + (disabled ? 0 : 1) &&
                    f.Output.IsPlaying == !disabled && f.Output.Reopens == (disabled ? 0 : 1),
                    "bad bytes do not blacklist an enabled source or authorize a disabled parser");
            });
        yield return Test("playback boundary official completed recovery retains online authorization", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true,
                officialQuality: "flac", qualityRequest: "flac");
            var song = await f.App.ExecuteAsync("music.online.song", new { platform = "wy", title = "合成歌曲", artist = "fixture",
                album = "", musicInfo = new { songmid = "42" } });
            f.App.CacheableSongId = song.GetProperty("song").GetProperty("songId").GetString();
            await f.Play();
            await Until(() => Field(Field(f.Module, "musicRecovery")!, "Media") is { } media &&
                media.GetType().GetProperty("File")?.GetValue(media) is not null, "official file published");
            int resolves = f.App.Resolutions.Count, authorizations = f.App.Authorizations;
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => f.Module.CanSwitchSource || !f.Output.HasMedia, "official file recovery settled");
            Check(f.Output.IsPlaying && f.Output.ReadOpens == 1 && f.App.Resolutions.Count == resolves &&
                f.App.Authorizations == authorizations + 1, "a full non-offline official file still needs current online authorization");
        });
        yield return Test("playback boundary completed bytes never override a durable exclusion", async () =>
        {
            const string id = "d41bd273-d19f-4135-a97a-8455ca64533f";
            await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                scripts: [new(id, quality: "flac")], qualityRequest: "flac");
            await f.Play(); await CompletedHistory(f);
            await f.Module.ReportWrongSourceAsync();
            await f.Module.RefreshSourceExclusionsAsync();
            Check(f.Module.SourceExclusions.Count == 1 && f.Output.PauseRequested, "excluded completed provider");
            int resolves = f.App.Resolutions.Count;
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => !f.Output.HasMedia, "excluded input cannot recover");
            Check(f.Output.ReadOpens == 0 && f.Output.Reopens == 0 && f.App.Resolutions.Count == resolves,
                "neither a local file nor new parsing can revive the excluded input");
        });
        foreach (string completion in new[] { "cancelled", "success", "no-match" })
            yield return Test("playback boundary supplemental search completion " + completion, async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false,
                    scripts: [new("boundary-current"), new("boundary-missing", quality: "flac", platforms: ["tx"])]);
                var song = MusicSongFixture.Online("wy", "42", "合成歌曲", "fixture", duration: 120);
                Check((await f.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }))
                    .GetProperty("ok").GetBoolean(), "known-duration recording queue");
                await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
                await f.Module.PlayQueueSurfaceItemAsync(f.Module.QueueSurfaceRows.Single());
                Check(f.Output.IsPlaying && f.App.Discoveries == 0, "mapped high-quality script wins before completion");
                f.Module.OpenSourceChoices();
                f.App.MatchMissing = completion != "no-match";
                f.App.HoldCompletion = completion == "cancelled";
                var loading = f.Module.LoadMoreSourcesAsync();
                if (f.App.HoldCompletion)
                {
                    await f.App.CompletionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    f.Module.CloseSourceChoices();
                }
                await loading.WaitAsync(TimeSpan.FromSeconds(5));
                f.App.HoldCompletion = false;
                f.Output.Plan();
                await f.Module.SwitchQualityAsync("lossless");
                Check(f.App.Discoveries == (completion == "cancelled" ? 2 : 1),
                    "cancelled completion is retryable; a completed positive or negative search remains deduplicated");
                if (completion == "no-match")
                    Check(f.Output.Commits == 0 && f.Output.IsPlaying && f.Module.CurrentSource == "boundary-current",
                        "a genuine no-match keeps the old source");
                else Check(f.Output.Commits == 1 && f.Output.IsPlaying &&
                    f.Output.OpenedUris.Last().AbsolutePath == "/boundary-missing-tx-flac.wav",
                    "a later quality intent can use the successfully completed platform");
            });
        foreach (string prior in new[] { "failure", "success", "listed-success" })
            yield return Test("playback boundary account change retires official " + prior, async () =>
            {
                bool oldSuccess = prior != "failure";
                await using var f = await ControlledMusicFixture.Create(
                    scripts: [new("boundary-account-script", quality: oldSuccess ? "master" : "320k")],
                    qualityRequest: oldSuccess ? "master" : "flac", officialQuality: "flac");
                f.App.PreserveOfficialRequest = true;
                f.App.ResolutionFailure = row => !oldSuccess && Installation(row) is null ? "AUTH_REQUIRED" : null;
                await f.Play();
                Check(f.Output.IsPlaying && f.Module.CurrentSource == "boundary-account-script" &&
                    f.App.Resolutions.Any(row => Installation(row) is null), "old official evidence exists while a script plays");
                f.Module.OpenSourceChoices();
                var listed = prior == "listed-success"
                    ? f.Module.SourceChoices.Single(row => row.Provider == "网易云音乐" && row.Selectable) : null;
                int before = f.App.Resolutions.Count;
                f.App.ResolutionFailure = null;
                f.App.RenewOfficial("wy");
                Check(f.Output.IsPlaying && f.Output.Commits == 0, "account changes never interrupt the current script");
                f.Output.Plan();
                if (listed is not null) await f.Module.SelectSourceChoiceAsync(listed);
                else await f.Module.SwitchQualityAsync("lossless");
                Check(f.Output.Commits == 1 && f.Output.IsPlaying && f.Module.CurrentSource == "网易云音乐" &&
                    f.App.Resolutions.Skip(before).Any(row => Installation(row) is null &&
                        row.GetProperty("generation").GetInt64() == f.App.OfficialGeneration),
                    "new official authority is resolved; neither an obsolete failure nor success may stand in for it");
            });
        yield return Test("playback boundary account refresh preserves permanent official exclusion", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(scripts: [new("boundary-allowed", quality: "flac")],
                qualityRequest: "flac", officialQuality: "flac");
            await f.Play();
            f.Output.Plan();
            await f.Module.ReportWrongSourceAsync();
            await f.Module.RefreshSourceExclusionsAsync();
            Check(f.Output.IsPlaying && f.Module.CurrentSource == "boundary-allowed" && f.Module.SourceExclusions.Count == 1,
                "wrong official source replaced by the same-quality script");
            int before = f.App.Resolutions.Count;
            f.App.RenewOfficial("wy");
            await f.Module.SwitchQualityAsync("lossless");
            Check(f.Output.IsPlaying && f.Module.CurrentSource == "boundary-allowed" &&
                f.Module.SourceExclusions.Count == 1 && f.App.Resolutions.Count == before,
                "new authority cannot lift a user exclusion or interrupt the accepted script");
        });
    }

    private static IEnumerable<TestCase> PlaybackInteractionRegressionCases()
    {
        foreach (bool networkBad in new[] { false, true })
            foreach (string intent in new[] { "source", "quality" })
            yield return Test("playback interaction complete file selected before network " + networkBad + " " + intent, async () =>
            {
                const string a = "e7dbf98b-a457-49b9-b690-b5c61794ff28";
                const string b = "930736f5-8874-41eb-8db5-0d089258574e";
                await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                    scripts: [new(a, quality: "flac"), new(b)], qualityRequest: "flac");
                await f.Play(); await CompletedHistory(f);
                f.Module.OpenSourceChoices();
                var chosen = f.Module.SourceChoices.Single(row => row.Provider == a && row.Selectable);
                f.HoldValidation = true;
                f.Output.Plan();
                await f.Module.SwitchQualityAsync("high");
                Check(f.Output.Commits == 1 && f.Module.CurrentSource == b, "B plays before explicit return to A");
                int resolves = f.App.Resolutions.Count;
                f.Output.FailNetworkHandoff = networkBad;
                await f.Output.SeekAsync(37);
                await f.Module.TogglePlaybackAsync();
                f.Output.Plan();
                if (intent == "source") await f.Module.SelectSourceChoiceAsync(chosen);
                else await f.Module.SwitchQualityAsync("lossless");
                Check(f.Output.Commits == 2 && f.Module.CurrentSource == a && !f.Output.IsPlaying &&
                    f.Output.PauseRequested && f.Output.PositionSeconds == 37 &&
                    f.Output.OpenedUris.Last().Host == "cache.invalid" && f.App.Resolutions.Count == resolves,
                    "complete A bytes must win over its network URL, including when the URL can no longer open");
            });
        foreach (bool unrelatedExclusion in new[] { false, true })
            yield return Test("playback interaction unrelated exclusion preserves completed recovery " + unrelatedExclusion, async () =>
            {
                const string id = "408d4ebc-e3b3-49d1-9205-f76509a3b9aa";
                await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                    scripts: [new(id, quality: "flac")], qualityRequest: "flac");
                await f.Play(); await CompletedHistory(f);
                f.Module.OpenSourceChoices();
                await f.Module.SelectSourceChoiceAsync(f.Module.SourceChoices.Single(row => row.Key != "auto" && row.Selectable));
                if (unrelatedExclusion)
                {
                    await f.Module.PrepareSourceExclusionFormAsync();
                    await f.Module.AddSourceExclusionAsync(f.Module.ExclusionSongs.Single(),
                        f.Module.ExclusionProviders.Single(row => row.Key == MusicLibraryOwner.OfficialProvider),
                        f.Module.ExclusionPlatforms.Single(row => row.Key == "tx"));
                    Check(f.Module.SourceExclusions.Count == 1 && f.Output.IsPlaying, "unrelated exclusion saved without changing A");
                }
                int resolves = f.App.Resolutions.Count;
                f.App.ResolutionFailure = _ => "MEDIA_UNAVAILABLE";
                f.Output.Plan(startBeforeCompletion: true);
                f.Output.Fail(PlaybackError.HttpUnauthorized);
                await Until(() => f.Module.CanSwitchSource || !f.Output.HasMedia, "completed history recovery settled");
                Check(f.Output.IsPlaying && f.Output.ReadOpens == 1 && f.App.Resolutions.Count == resolves,
                    "an unrelated exclusion must not make a complete permitted file depend on successful parsing");
            });
        yield return Test("playback interaction exclusion fences pending recovery", async () =>
        {
            const string id = "c9e5bfa1-0671-430d-a903-aa46c717d38c";
            await using var f = await ControlledMusicFixture.Create(official: false, scripts: [new(id)]);
            await f.Play();
            await f.Module.PrepareSourceExclusionFormAsync();
            f.Output.StartAfterPreparation = true;
            var plan = f.Output.Plan(hold: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            try
            {
                await plan.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!f.Output.IsPlaying, "replacement not started before exclusion");
                await f.Module.AddSourceExclusionAsync(f.Module.ExclusionSongs.Single(),
                    f.Module.ExclusionProviders.Single(row => row.Key == id),
                    f.Module.ExclusionPlatforms.Single(row => row.Key == "wy"));
                Check(f.Module.SourceExclusions.Count == 1, "persistent exclusion accepted during recovery");
            }
            finally { plan.Release.TrySetResult(); }
            await Until(() => f.Module.CanSwitchSource || !f.Output.HasMedia, "excluded pending recovery settled");
            Check(!f.Output.IsPlaying, "the already excluded pending input must not publish and play after its delayed preparation");
        });
        yield return Test("playback interaction recovery preserves incompatible cache diagnosis", async () =>
        {
            const string id = "b27d9368-e01a-40fe-82f7-7328364c0c65";
            await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                scripts: [new(id, quality: "flac")], qualityRequest: "flac");
            await f.Play(); await CompletedHistory(f);
            var snapshot = await f.Store.ReadAsync(MusicAudioFileCache.Namespace);
            var index = JsonNode.Parse(snapshot.Data!)!.AsObject();
            index["schema"] = 999;
            var raw = Encoding.UTF8.GetBytes(index.ToJsonString());
            var receipt = await f.Store.WriteAsync(MusicAudioFileCache.Namespace, raw, snapshot.Fingerprint);
            int resolves = f.App.Resolutions.Count;
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => !f.Output.HasMedia, "incompatible cache recovery settled");
            var after = await f.Store.ReadAsync(MusicAudioFileCache.Namespace);
            Check(f.Module.Status == "播放缓存数据格式不兼容" && after.Fingerprint == receipt &&
                f.App.Resolutions.Count == resolves, "report the cache schema error without overwriting it or disguising it as a source outage");
        });
        foreach (string stage in new[] { "resolve", "network", "file" })
            foreach (bool related in new[] { false, true })
            yield return Test("playback interaction precise exclusion during " + stage + " " + related, async () =>
            {
                const string id = "63304e39-0c18-4d74-98d5-8de049f21790";
                await using var f = await ControlledMusicFixture.Create(playbackCache: stage == "file", official: false,
                    scripts: [new(id, quality: "flac")], qualityRequest: "flac");
                await f.Play();
                if (stage == "file") await CompletedHistory(f);
                await f.Module.PrepareSourceExclusionFormAsync();
                f.Output.StartAfterPreparation = true;
                var plan = f.Output.Plan(hold: stage != "resolve");
                if (stage == "resolve") f.App.HoldResolution = _ => true;
                f.Output.Fail(PlaybackError.HttpUnauthorized);
                try
                {
                    await (stage == "resolve" ? f.App.RefreshEntered.Task : plan.Entered.Task)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    await f.Module.AddSourceExclusionAsync(f.Module.ExclusionSongs.Single(),
                        f.Module.ExclusionProviders.Single(row => row.Key == (related ? id : MusicLibraryOwner.OfficialProvider)),
                        f.Module.ExclusionPlatforms.Single(row => row.Key == (related ? "wy" : "tx")));
                    Check(f.Module.SourceExclusions.Count == 1, "the exact durable exclusion was accepted");
                }
                finally { f.App.ReleaseRefresh.TrySetResult(); plan.Release.TrySetResult(); }
                await Until(() => f.Module.CanSwitchSource || !f.Output.HasMedia, "exclusion recovery settled");
                if (related)
                    Check(!f.Output.IsPlaying && f.Output.CandidateStarts == 0 &&
                        (stage == "resolve" ? f.Output.Reopens == 0 : plan.Token.IsCancellationRequested),
                        "newly excluded media is rejected before it can start or publish");
                else Check(f.Output.IsPlaying && f.Output.Reopens == 1 && f.Module.CurrentSource == id &&
                    !plan.Token.IsCancellationRequested, "unrelated exclusions cannot cancel or stop this allowed provider");
            });
        foreach (string failure in new[] { "corrupt", "decode", "disabled" })
            yield return Test("playback interaction unusable complete handoff file " + failure, async () =>
            {
                const string a = "16403cfe-2d04-4a41-9d9c-0db47b43cc8a";
                const string b = "a5a01546-793f-4d06-ad7c-ea2fbf1acdd5";
                await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                    scripts: [new(a, quality: "flac"), new(b)], qualityRequest: "flac");
                await f.Play(); await CompletedHistory(f);
                var saved = await f.Store.ReadAsync(MusicAudioFileCache.Namespace);
                string file = JsonNode.Parse(saved.Data!)!["entries"]!.AsObject().Single().Value!["file"]!.GetValue<string>();
                f.Module.OpenSourceChoices();
                var chosen = f.Module.SourceChoices.Single(row => row.Provider == a && row.Selectable);
                f.HoldValidation = true;
                f.Output.Plan();
                await f.Module.SwitchQualityAsync("high");
                Check(f.Module.CurrentSource == b && f.Output.Commits == 1, "B is accepted before A's file is damaged");
                if (failure == "disabled")
                {
                    await f.Module.RefreshSourceManagementAsync();
                    await f.Module.SetSourceEnabledAsync(f.Module.Sources.Single(row => row.InstallationId == a), false);
                }
                if (failure != "decode")
                    await File.WriteAllBytesAsync(Path.Combine(f.CacheRoot!, file), [1, 2, 3]);
                else f.Output.Plan(failure: PlaybackError.ReadFailed);
                string previousSource = f.Module.CurrentSource;
                int resolves = f.App.Resolutions.Count;
                f.Output.Plan();
                await f.Module.SelectSourceChoiceAsync(chosen);
                bool enabled = failure != "disabled";
                Check(f.Output.IsPlaying && f.Output.Commits == (enabled ? 2 : 1) &&
                    f.Module.CurrentSource == (enabled ? a : previousSource) &&
                    f.App.Resolutions.Count == resolves + (enabled ? 1 : 0),
                    "only bad file bytes are discarded; an enabled exact source may reparse, a disabled one may not");
            });
        yield return Test("playback interaction official complete handoff retains authorization", async () =>
        {
            const string script = "9cf56bd8-3a13-4113-8e3d-42905398913d";
            await using var f = await ControlledMusicFixture.Create(playbackCache: true,
                scripts: [new(script)], qualityRequest: "flac", officialQuality: "flac");
            var song = await f.App.ExecuteAsync("music.online.song", new { platform = "wy", title = "合成歌曲",
                artist = "fixture", album = "", musicInfo = new { songmid = "42" } });
            f.App.CacheableSongId = song.GetProperty("song").GetProperty("songId").GetString();
            await f.Play();
            await Until(() => Field(Field(f.Module, "musicRecovery")!, "Media") is { } media &&
                media.GetType().GetProperty("File")?.GetValue(media) is not null, "official complete file");
            f.Module.OpenSourceChoices();
            var official = f.Module.SourceChoices.Single(row => row.Provider == "网易云音乐" && row.Selectable);
            f.HoldValidation = true;
            f.Output.Plan();
            await f.Module.SwitchQualityAsync("high");
            Check(f.Module.CurrentSource == script && f.Output.Commits == 1, "script is the accepted high source");
            int resolves = f.App.Resolutions.Count, authorizations = f.App.Authorizations;
            f.Output.FailNetworkHandoff = true;
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(official);
            Check(f.Output.Commits == 2 && f.Module.CurrentSource == "网易云音乐" && f.Output.IsPlaying &&
                f.Output.OpenedUris.Last().Host == "cache.invalid" && f.App.Resolutions.Count == resolves &&
                f.App.Authorizations == authorizations + 1, "offline bytes cannot bypass current official online authorization");
        });
        yield return Test("playback interaction corrupt index during file recovery never reparses", async () =>
        {
            const string id = "97b6e49c-0093-4cd9-8021-267180e1e9ec";
            await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                scripts: [new(id, quality: "flac")], qualityRequest: "flac");
            await f.Play(); await CompletedHistory(f);
            await f.Play();
            Check(f.Output.ReadOpens == 1, "history is the current file input");
            var snapshot = await f.Store.ReadAsync(MusicAudioFileCache.Namespace);
            var corrupt = Encoding.UTF8.GetBytes("{");
            var fingerprint = await f.Store.WriteAsync(MusicAudioFileCache.Namespace, corrupt, snapshot.Fingerprint);
            int resolves = f.App.Resolutions.Count;
            f.Output.Fail(PlaybackError.ReadFailed);
            await Until(() => !f.Output.HasMedia, "cached input error settled");
            Check(f.Module.Status == "播放缓存数据格式不兼容" && f.App.Resolutions.Count == resolves &&
                (await f.Store.ReadAsync(MusicAudioFileCache.Namespace)).Fingerprint == fingerprint,
                "failed cache invalidation must surface its data error before any network fallback");
        });
    }

    private static async Task CompletedHistory(ControlledMusicFixture f)
    {
        await Until(() => Field(Field(f.Module, "musicRecovery")!, "Media") is { } media &&
            media.GetType().GetProperty("File")?.GetValue(media) is not null, "complete file identity published");
        var queue = await f.App.ExecuteAsync("music.queue.get");
        string songId = queue.GetProperty("current").GetProperty("song").GetProperty("songId").GetString()!;
        var history = await f.Cache!.ReadScriptHistoryAsync(songId, default);
        Check(history.Length == 1 && history[0].GetProperty("actualQuality").GetString() == "flac",
            "history contains the same verified lossless representation");
        await using var lease = await f.Cache.OpenAsync(history[0].GetProperty("scope").GetString()!, songId,
            history[0].GetProperty("qualityCacheSignature").GetString()!, default);
        Check(lease is not null && lease.Length > 0 && lease.Content.ReadByte() >= 0,
            "completed bytes are genuinely readable before failure");
    }

    private static IEnumerable<TestCase> SourceLifecycleRegressionCases()
    {
        foreach (bool paused in new[] { false, true })
            yield return Test("source lifecycle completed cache publishes current quality without another playback event " + paused, async () =>
            {
                await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                    scripts: [new("quality-publication", quality: "flac")], qualityRequest: "flac");
                f.HoldValidation = true;
                await f.Play();
                await f.ValidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (paused) await f.Module.TogglePlaybackAsync();
                f.Module.OpenSourceChoices();
                var currentQuality = new System.Windows.Controls.TextBlock();
                currentQuality.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                    new System.Windows.Data.Binding(nameof(MusicCapability.CurrentSourceQuality)) { Source = f.Module });
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                Check(currentQuality.Text == "音质未知", "unverified media is not presented as confirmed quality");
                string? identity = f.Output.Identity;
                int resolves = f.App.Resolutions.Count, opens = f.Output.NetworkOpens, playbackEvents = 0;
                void Observe(object? sender, PropertyChangedEventArgs e) => playbackEvents++;
                f.Output.PropertyChanged += Observe;
                try
                {
                    f.ValidationRelease.TrySetResult();
                    await Until(() => f.Module.CurrentSourceQuality == "无损", "native cache evidence reaches the authority");
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    Check(currentQuality.Text == "无损" && f.Module.CurrentQualityLabel == "无损" &&
                        f.Module.SourceChoices.Any(row => row.Provider == "quality-publication" && row.Quality == "无损"),
                        "the existing source header binding and candidate agree with the accepted cache evidence");
                    Check(playbackEvents == 0 && f.Output.Identity == identity && f.Output.IsPlaying == !paused &&
                        f.App.Resolutions.Count == resolves && f.Output.NetworkOpens == opens,
                        "quality publication needs no later playback event, reopen, new parsing or pause change");
                }
                finally { f.Output.PropertyChanged -= Observe; }
            });
        yield return Test("source lifecycle automatic refresh uses its lower receipt only after preferred tier exhaustion", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(qualityRequest: "flac", officialQuality: "flac");
            f.App.OfferAlternate = false;
            f.App.PreserveOfficialRequest = true;
            await f.Play();
            f.App.OfficialQuality = "320k";
            f.App.OfficialExpiry = DateTimeOffset.UtcNow.AddSeconds(20).ToUnixTimeMilliseconds();
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => f.Output.Reopens == 1 && f.Module.CanSwitchSource, "refreshed lower receipt accepted");
            Check(f.App.Resolutions.Count == 2 && f.Module.CurrentQualityLabel == "高音质" && f.Output.IsPlaying,
                "a valid lower receipt is reused without a redundant lower-tier resolver request");
        });
        yield return Test("source lifecycle failed multi-quality source terminates after each declared tier", async () =>
        {
            var source = new ScriptSource("finite-tiers", quality: "flac") { Qualities = ["flac", "320k", "128k"] };
            await using var f = await ControlledMusicFixture.Create(official: false, scripts: [source], qualityRequest: "flac");
            await f.Play();
            f.App.ResolutionFailure = _ => "MEDIA_UNAVAILABLE";
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => !f.Output.HasMedia, "finite quality attempts");
            Check(f.App.Resolutions.Select(row => row.GetProperty("quality").GetString())
                    .SequenceEqual(new[] { "flac", "flac", "320k", "128k" }) && f.Output.Reopens == 0,
                "only the accepted representation gets one exact refresh; all later qualities are tried once");
        });
        yield return Test("source lifecycle recording mismatch excludes all qualities of that recording", async () =>
        {
            var source = new ScriptSource("mismatched-tiers", quality: "flac") { Qualities = ["flac", "320k"] };
            await using var f = await ControlledMusicFixture.Create(official: false, scripts: [source], qualityRequest: "flac");
            f.Output.ActualDuration = 240;
            var song = MusicSongFixture.Online("wy", "Mismatch", "Mismatch", "fixture", duration: 120);
            Check((await f.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 }))
                .GetProperty("ok").GetBoolean(), "mismatched queue");
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await f.Module.PlayQueueSurfaceItemAsync(f.Module.QueueSurfaceRows.Single());
            await Until(() => !f.Output.HasMedia, "mismatched source exhausted");
            Check(f.App.Resolutions.Count == 1 && f.Output.Reopens == 0,
                "known recording contradiction cannot be bypassed through a lower quality");
        });
        yield return Test("source lifecycle resolver recording mismatch also excludes its other qualities", async () =>
        {
            var source = new ScriptSource("resolver-mismatch", quality: "flac") { Qualities = ["flac", "320k"] };
            await using var f = await ControlledMusicFixture.Create(official: false, scripts: [source], qualityRequest: "flac");
            f.App.ResolutionFailure = _ => "RECORDING_MISMATCH";
            await f.Play();
            Check(f.App.Resolutions.Count == 1 && !f.Output.HasMedia,
                "a classified identity contradiction is not just a quality miss");
        });
        yield return Test("source lifecycle rejected higher candidate leaves a refreshed lower receipt reusable", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(scripts: [new("bad-lossless", quality: "flac")],
                qualityRequest: "flac", officialQuality: "flac");
            f.App.PreserveOfficialRequest = true;
            await f.Play();
            f.App.OfficialQuality = "320k";
            f.Output.Plan(failure: PlaybackError.HttpUnauthorized);
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => f.Output.Reopens == 2 && f.Module.CanSwitchSource, "lower receipt after bad higher media");
            Check(f.Output.OpenedUris[^2].AbsolutePath == "/bad-lossless-wy-flac.wav" &&
                f.Output.OpenedUris[^1].AbsolutePath == "/official-wy-320k.wav" &&
                f.App.Resolutions.Count(row => Installation(row) is null) == 2 &&
                f.Module.CurrentQualityLabel == "高音质" && f.Output.IsPlaying,
                "the failed script cannot discard the independent refreshed official receipt");
        });
        foreach (string action in new[] { "disable", "remove" })
            yield return Test("source lifecycle missing historical file cannot parse a " + action + " script", async () =>
            {
                await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                    scripts: [new("missing-script", quality: "flac")], qualityRequest: "flac");
                await f.Play();
                await Until(() => f.Store.ReadAsync(MusicAudioFileCache.Namespace).GetAwaiter().GetResult().Data is not null,
                    "script file committed");
                await f.Module.RefreshSourceManagementAsync();
                var row = f.Module.Sources.Single(row => row.InstallationId == "missing-script");
                if (action == "disable") await f.Module.SetSourceEnabledAsync(row, false);
                else await f.Module.RemoveSourceAsync(row);
                foreach (var path in Directory.GetFiles(f.CacheRoot!, "*.audio")) File.Delete(path);
                int before = f.App.Resolutions.Count;
                await f.Play();
                Check(f.App.Resolutions.Count == before && !f.Output.HasMedia,
                    "missing bytes do not grant new parsing authority to a retired installation");
            });
        yield return Test("source lifecycle pinned official file miss cannot hand off a lower actual quality", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true, scripts: [new("other-high")]);
            f.App.PreserveOfficialRequest = true;
            var song = await f.App.ExecuteAsync("music.online.song", new { platform = "wy", title = "合成歌曲",
                artist = "fixture", album = "", musicInfo = new { songmid = "42" } });
            f.App.CacheableSongId = song.GetProperty("song").GetProperty("songId").GetString();
            await f.Play();
            await Until(() => f.Store.ReadAsync(MusicAudioFileCache.Namespace).GetAwaiter().GetResult().Data is not null,
                "official file committed");
            await f.Play();
            Check(f.Output.ReadOpens == 1, "official complete-file history");
            f.Module.OpenSourceChoices();
            var original = f.Module.SourceChoices.Single(row => row.Provider == "网易云音乐" && row.Selectable);
            await f.Module.LoadMoreSourcesAsync();
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(f.Module.SourceChoices.Single(row => row.Provider == "other-high" && row.Selectable));
            Check(f.Output.Commits == 1, "alternative accepted before clearing the selected history");
            await f.Module.ClearPlaybackCacheAsync();
            int before = f.App.Resolutions.Count;
            f.App.OfficialQuality = "128k";
            await f.Module.SelectSourceChoiceAsync(original);
            Check(f.App.Resolutions.Count == before + 1 && f.Output.Commits == 1 && f.Output.IsPlaying &&
                f.Module.CurrentSource == "other-high", "exact history refresh cannot lower a pinned result");
        });
        yield return Test("source lifecycle newer successful choice consumes old input failure without reopening it", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            f.App.HoldResolution = row => row.GetProperty("quality").GetString() == "flac";
            var oldChoice = f.Module.SwitchQualityAsync("lossless");
            await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            f.Output.Plan();
            var newer = f.Module.SwitchQualityAsync("high");
            Check(f.App.HeldResolutionToken.IsCancellationRequested, "new choice cancels older resolution immediately");
            f.App.ReleaseRefresh.TrySetResult();
            await Task.WhenAll(oldChoice, newer).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.Commits == 1 && f.Output.Reopens == 0 && f.Output.IsPlaying &&
                f.Module.CurrentQualityLabel == "高音质", "only the latest selection repairs the input; old failure is consumed at commit");
        });
        yield return Test("source lifecycle changing song during a failed switch never recovers the retired occurrence", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            f.App.HoldResolution = row => row.GetProperty("quality").GetString() == "flac";
            var switching = f.Module.SwitchQualityAsync("lossless");
            await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            var next = f.Module.PlayOnlineSongAsync("wy", "新的歌曲", "fixture", "", Element(new { songmid = "43" }));
            f.App.ReleaseRefresh.TrySetResult();
            await Task.WhenAll(switching, next).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.Reopens == 0 && f.Output.Commits == 0 && f.Output.NetworkOpens == 2 &&
                f.Module.CurrentTitle == "新的歌曲" && f.Output.IsPlaying, "a retired failure cannot revive or modify the new song");
        });
        yield return Test("source lifecycle wrong-source search with concurrent old failure still records pause intent", async () =>
        {
            const string id = "8d0e3f46-b6dc-4c92-9b65-86a937a28f00";
            await using var f = await ControlledMusicFixture.Create(scripts: [new(id)]);
            await f.Play();
            f.App.HoldResolution = row => Installation(row) == id;
            f.App.ResolutionFailure = row => Installation(row) == id ? "MEDIA_UNAVAILABLE" : null;
            var wrong = f.Module.ReportWrongSourceAsync();
            await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            f.App.ReleaseRefresh.TrySetResult();
            await wrong.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.RefreshSourceExclusionsAsync();
            Check(f.Output.PauseRequested && !f.Output.IsPlaying && !f.Output.HasMedia &&
                f.Output.Reopens == 0 && f.Output.Commits == 0 && f.Module.SourceExclusions.Count == 1,
                "wrong-source replacement never recovers the excluded old input or forgets the pause intent");
        });
        yield return Test("source lifecycle automatic failure still explores lower quality of the same provider", async () =>
        {
            var script = new ScriptSource("lifecycle-multiple", quality: "flac") { Qualities = ["flac", "320k"] };
            await using var f = await ControlledMusicFixture.Create(official: false, scripts: [script], qualityRequest: "flac");
            await f.Play();
            Check(f.Output.OpenedUris.Single().AbsolutePath.EndsWith("-flac.wav"), "automatic starts lossless");
            f.App.ResolutionFailure = row => row.GetProperty("quality").GetString() == "flac" ? "MEDIA_UNAVAILABLE" : null;
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => f.Module.CanSwitchSource || !f.Output.HasMedia, "automatic lower tier settled");
            bool lower = f.App.Resolutions.Any(row => row.GetProperty("quality").GetString() == "320k");
            Check(lower && f.Output.Reopens == 1 && f.Output.IsPlaying,
                "a failed lossless URL must not exclude another declared quality of the same source");
        });
        yield return Test("source lifecycle automatic refresh downgrade cannot skip an available same-tier script", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(scripts: [new("lifecycle-lossless", quality: "flac")],
                qualityRequest: "flac", officialQuality: "flac");
            await f.Play();
            Check(f.Module.CurrentSource == "网易云音乐", "automatic begins official lossless");
            f.App.OfficialQuality = "320k";
            f.App.PreserveOfficialRequest = true;
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => f.Output.Reopens > 0 && f.Module.CanSwitchSource || !f.Output.HasMedia, "automatic recovery settled");
            Check(f.Output.IsPlaying && f.Output.OpenedUris.Last().AbsolutePath == "/lifecycle-lossless-wy-flac.wav",
                "other lossless sources must be exhausted before accepting the official refreshed lower tier");
        });
        yield return Test("source lifecycle corrupt historical file reacquires the still-enabled exact source", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                scripts: [new("lifecycle-history", quality: "flac")], qualityRequest: "flac");
            await f.Play();
            await Until(() => f.Store.ReadAsync(MusicAudioFileCache.Namespace).GetAwaiter().GetResult().Data is not null,
                "script cache publication");
            await Until(() => Field(Field(f.Module, "musicRecovery")!, "Media") is { } media &&
                media.GetType().GetProperty("File")?.GetValue(media) is not null, "file identity publication");
            foreach (var path in Directory.GetFiles(f.CacheRoot!, "*.audio")) await File.WriteAllBytesAsync(path, [1, 2, 3]);
            int before = f.App.Resolutions.Count;
            await f.Play();
            Check(f.App.Resolutions.Count > before && f.Output.NetworkOpens == 2 && f.Output.IsPlaying,
                "a corrupt local copy must not disqualify the same working online source");
        });
        yield return Test("source lifecycle explicit listed result retains its quality after policy refresh", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            f.Module.OpenSourceChoices();
            var chosen = f.Module.SourceChoices.Single(row => row.Key != "auto" && row.Selectable);
            Check(chosen.Level == "320k" && chosen.Quality == "320 kbps", "listed result identifies its exact quality");
            await f.Module.ActivateQualitySettingsAsync();
            await f.Module.SetPlaybackQualityRequestAsync("128k");
            f.App.OfficialQuality = "128k";
            f.App.PreserveOfficialRequest = true;
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(chosen);
            Check(f.Output.Commits == 0 && f.Module.CurrentSource == "网易云音乐" && f.Output.IsPlaying,
                "refreshing a selected high-quality result must not commit a lower-quality result");
        });
        yield return Test("source lifecycle old input error during an unavailable quality search is not lost", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            f.App.HoldResolution = row => row.GetProperty("quality").GetString() == "flac";
            var operation = f.Module.SwitchQualityAsync("lossless");
            await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            f.App.ReleaseRefresh.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(f.Output.Reopens == 1 && f.Output.IsPlaying,
                "a failed quality search must not consume the accepted stream's pending recovery");
        });
        yield return Test("source lifecycle explicit historical result reacquires after clear cache", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                scripts: [new("lifecycle-cleared", quality: "flac"), new("lifecycle-other", quality: "flac")], qualityRequest: "flac");
            await f.Play();
            await Until(() => f.Store.ReadAsync(MusicAudioFileCache.Namespace).GetAwaiter().GetResult().Data is not null,
                "script cache publication");
            await f.Play();
            Check(f.Output.ReadOpens == 1, "history is loaded");
            f.Module.OpenSourceChoices();
            var choice = f.Module.SourceChoices.Single(row => row.Provider == "lifecycle-cleared" && row.Selectable);
            while (!f.Module.SourceChoices.Any(row => row.Provider == "lifecycle-other" && row.Selectable) &&
                f.Module.CanLoadMoreSources) await f.Module.LoadMoreSourcesAsync();
            var other = f.Module.SourceChoices.Single(row => row.Provider == "lifecycle-other" && row.Selectable);
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(other);
            Check(f.Output.Commits == 1 && f.Module.CurrentSource == "lifecycle-other", "another source now plays");
            await f.Module.ClearPlaybackCacheAsync();
            int before = f.App.Resolutions.Count;
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(choice);
            Check(f.App.Resolutions.Count > before && f.Output.Commits == 2 && f.Output.IsPlaying,
                "an explicitly chosen result must refresh its exact online identity when the cached file is gone");
        });
        yield return Test("source lifecycle selecting current tier after wrong-source pause uses newly enabled alternative", async () =>
        {
            const string wrong = "6d0e3f46-b6dc-4c92-9b65-86a937a28f00", enabled = "7d0e3f46-b6dc-4c92-9b65-86a937a28f00";
            await using var f = await ControlledMusicFixture.Create(official: false,
                scripts: [new(wrong), new(enabled, enabled: false)]);
            await f.Play();
            await f.Module.ReportWrongSourceAsync();
            Check(!f.Output.IsPlaying && !f.Output.HasMedia && f.Output.PauseRequested,
                "wrong source without alternative retires audio and pauses the retained occurrence");
            await f.Module.RefreshSourceExclusionsAsync();
            Check(f.Module.SourceExclusions.Single().Provider == wrong, "wrong source durably excluded");
            await f.Module.RefreshSourceManagementAsync();
            await f.Module.SetSourceEnabledAsync(f.Module.Sources.Single(row => row.InstallationId == enabled), true);
            f.Output.Plan();
            await f.Module.SwitchQualityAsync("high");
            Check(f.Output.Commits == 1 && f.Output.OpenedUris.Last().AbsolutePath == "/" + enabled + "-wy-320k.wav" && !f.Output.IsPlaying,
                "same-tier fast path must not reuse excluded current media instead of the newly eligible source");
        });
        yield return Test("source lifecycle script content change resets transient failed-media rejection", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(scripts: [new("lifecycle-fixed", quality: "flac")]);
            await f.Play();
            f.Output.Plan(failure: PlaybackError.HttpUnauthorized);
            await f.Module.SwitchQualityAsync("lossless");
            Check(f.Output.Commits == 0 && f.Output.IsPlaying, "first bad URL keeps the old source");
            await f.Module.RefreshSourceManagementAsync();
            await f.Module.RollbackSourceAsync(f.Module.Sources.Single(row => row.InstallationId == "lifecycle-fixed"));
            int before = f.App.Resolutions.Count(row => Installation(row) == "lifecycle-fixed");
            f.Output.Plan();
            await f.Module.SwitchQualityAsync("lossless");
            int next = f.App.Resolutions.Count(row => Installation(row) == "lifecycle-fixed") - before;
            Check(next == 1 && f.Output.Commits == 1,
                "temporary failed-media rejection must not permanently hide the updated script from new explicit intent");
        });
        yield return Test("source lifecycle historical script file does not disable official next-track preload", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true,
                scripts: [new("lifecycle-preload", quality: "flac")], qualityRequest: "flac");
            await f.PlayPair();
            await Until(() => f.Store.ReadAsync(MusicAudioFileCache.Namespace).GetAwaiter().GetResult().Data is not null,
                "current script cached");
            await f.Module.PlayQueueSurfaceItemAsync(f.Module.QueueSurfaceRows.First());
            Check(f.Output.ReadOpens == 1, "current song uses persistent history");
            int before = f.App.Resolutions.Count(row => Installation(row) is null &&
                row.GetProperty("song").GetProperty("songId").GetString() == f.NextSongId);
            f.Output.SetPosition(105);
            var prepared = await f.WaitForPrepared();
            Check(await prepared.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null, "next official file ready");
            int next = f.App.Resolutions.Count(row => Installation(row) is null &&
                row.GetProperty("song").GetProperty("songId").GetString() == f.NextSongId) - before;
            Check(next > 0 && f.App.Resolutions.Where(row =>
                row.GetProperty("song").GetProperty("songId").GetString() == f.NextSongId).All(row => Installation(row) is null) &&
                f.Output.ReadOpens == 1 && f.Output.IsPlaying,
                "history playback permits only official next-track preparation without changing current playback");
        });
        yield return Test("source lifecycle newly enabled official platform joins current explicit quality intent", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(scripts: [new("lifecycle-script")]);
            await f.Module.RefreshSourceManagementAsync();
            foreach (string platform in new[] { "wy", "tx", "kg" })
                await f.Module.SetSourceEnabledAsync(f.Module.Sources.Single(row => row.IsBuiltIn && row.Platform == platform), false);
            await f.Play();
            Check(f.Output.OpenedUris.Last().AbsolutePath == "/lifecycle-script-wy-320k.wav", "script accepted with officials disabled");
            await f.Module.SwitchQualityAsync("lossless");
            f.App.OfficialQuality = "flac";
            await f.Module.SetSourceEnabledAsync(f.Module.Sources.Single(row => row.IsBuiltIn && row.Platform == "wy"), true);
            int before = f.App.Resolutions.Count(row => Installation(row) is null);
            f.Output.Plan();
            await f.Module.SwitchQualityAsync("lossless");
            int next = f.App.Resolutions.Count(row => Installation(row) is null) - before;
            Check(next > 0 && f.Output.Commits == 1,
                "a new explicit operation must not keep the old disabled official platform snapshot");
        });
    }


    private static async Task PlayDurationSong(ControlledMusicFixture fixture, double duration)
    {
        var song = MusicSongFixture.Online("wy", "duration", "Duration boundary", "fixture", duration: duration);
        var queued = await fixture.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 });
        Check(queued.GetProperty("ok").GetBoolean(), "duration fixture queue");
        await fixture.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
        await fixture.Module.PlayQueueSurfaceItemAsync(fixture.Module.QueueSurfaceRows.Single());
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
        internal bool Enabled = enabled, Removed;
        internal string[]? Qualities;
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
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Native business must not upload script sources"); }
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
            App.ReleaseExclusion.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Native.DisposeAsync(); await Origin.DisposeAsync(); }
        }
    }
    public static IEnumerable<TestCase> SelectionRegressionCases()
    {
        yield return Test("history URL refresh after script update acquires current generation without stopping valid playback", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false, scripts: [new("updated-script")]);
            await f.Play();
            await f.Module.RefreshSourceManagementAsync();
            await f.Module.RollbackSourceAsync(f.Module.Sources.Single(row => row.InstallationId == "updated-script"));
            Check(f.Output.IsPlaying && f.App.Resolutions.Count == 1, "content change leaves accepted history alive");
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => f.Output.Reopens == 1 && f.Module.CanSwitchSource, "updated exact refresh");
            Check(f.Output.IsPlaying && f.App.Resolutions.Count == 2 &&
                !f.App.Resolutions.Last().TryGetProperty("qualityCacheSignature", out _),
                "fresh parsing obtains new script authority rather than replaying the old content signature");
        });
        yield return Test("cached official result remains explicitly selectable after default policy changes", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true, officialQuality: "flac", qualityRequest: "flac");
            var song = await f.App.ExecuteAsync("music.online.song", new { platform = "wy", title = "合成歌曲", artist = "fixture",
                album = "", musicInfo = new { songmid = "42" } });
            f.App.CacheableSongId = song.GetProperty("song").GetProperty("songId").GetString();
            await f.Play();
            await Until(() => f.Store.ReadAsync(MusicAudioFileCache.Namespace).GetAwaiter().GetResult().Data is not null, "official file");
            await f.Play();
            Check(f.Output.ReadOpens == 1, "second occurrence is a complete official file");
            f.Module.OpenSourceChoices();
            var choice = f.Module.SourceChoices.First(row => row.Key != "auto" && row.Selectable);
            await f.Module.ActivateQualitySettingsAsync();
            await f.Module.SetPlaybackQualityRequestAsync("128k");
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(choice);
            Check(f.Output.Commits == 1 && f.Output.IsPlaying && f.Module.CurrentQualityLabel == "无损",
                "new explicit intent reacquires current official policy authority without inheriting a permanent disable");
        });
        yield return Test("recovering input with contradictory duration cannot commit even without a later duration event", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false,
                scripts: [new("first-duration"), new("second-duration")]);
            var song = MusicSongFixture.Online("wy", "Duration", "RecoveryDuration", "fixture", duration: 120);
            Check((await f.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 })).GetProperty("ok").GetBoolean(), "queue");
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await f.Module.PlayQueueSurfaceItemAsync(f.Module.QueueSurfaceRows.Single());
            f.Output.ActualDuration = 240;
            f.Output.Plan(startBeforeCompletion: true); f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => !f.Output.HasMedia, "all mismatched recovered inputs rejected");
            Check(f.Output.Reopens == 2 && f.App.FeedbackProbe.Starts == 1,
                "mismatched prepared recovery never becomes the accepted source or successful feedback");
        });
        foreach (double duration in new[] { 110d, 130d, 109.99d, 130.01d })
            yield return Test("handoff media duration absolute boundary " + duration, async () =>
            {
                await using var f = await ControlledMusicFixture.Create(scripts: [new("duration-lossless", quality: "flac")]);
                await f.Play(); f.Output.PreparedDuration = duration; f.Output.Plan();
                await f.Module.SwitchQualityAsync("lossless");
                bool accepted = duration is >= 110 and <= 130;
                Check(f.Output.Commits == (accepted ? 1 : 0) && f.Output.IsPlaying &&
                    (accepted || f.Module.CurrentSource == "网易云音乐" && f.Module.Status == "未匹配到对应音质"),
                    "prepared decoded duration is checked before disturbing the old stream");
            });

        yield return Test("native read continuation preserves position pause and releases replaced lease", async () =>
        {
            await using var origin = WaveOrigin(); await using var native = Player(origin);
            await native.OpenAsync("music", "entry", origin.Uri("old.wav"), "fixture", origin.Headers);
            await Decoded(native); await native.SeekAsync(18);
            await Until(() => native.PositionSeconds >= 17, "network position");
            await native.PauseAsync();
            var lease = new CachedWave();
            await native.ReopenReadAsync(native.CaptureContinuation()!, lease);
            Check(!native.IsPlaying && native.PauseRequested && lease.Disposals == 0, "read recovery retains pause and owns lease");
            await native.ResumeAsync(); await Decoded(native);
            await Until(() => native.PositionSeconds >= 17, "history position retained");
            await native.StopAsync();
            Check(lease.Disposals == 1, "read continuation drains exactly once");
        });
        foreach (string action in new[] { "disable", "remove" })
            yield return Test("script played file survives " + action + " and a new player without parsing", async () =>
            {
                const string id = "9d125655-6b31-4bf6-bda8-79a04aa51777";
                await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false,
                    scripts: [new(id, quality: "flac")], qualityRequest: "flac");
                await f.Play();
                await Until(() => f.Store.ReadAsync(MusicAudioFileCache.Namespace).GetAwaiter().GetResult().Data is not null,
                    "played script file committed");
                string songId = f.App.Resolutions[0].GetProperty("song").GetProperty("songId").GetString()!;
                Check((await f.Cache!.ReadScriptHistoryAsync(songId, default)).Length == 1, f.Module.Status);
                await f.Module.RefreshSourceManagementAsync();
                var row = f.Module.Sources.Single(row => row.InstallationId == id);
                if (action == "disable") await f.Module.SetSourceEnabledAsync(row, false);
                else await f.Module.RemoveSourceAsync(row);
                Check(f.Output.HasMedia && f.Output.IsPlaying, "source management never retires a valid historical input");
                await f.Module.DisposeAsync();
                var app = new MusicApp(new MusicNativeApplication(f.Store, new NoRuntime(), new NoHttp()), f.Origin,
                    expiry: false, official: false, sources: []) { QualityRequest = "flac" };
                var output = new ControlledPlayback();
                var cache = new MusicAudioFileCache(f.CacheRoot!, f.Store);
                await using (var module = new MusicCapability(app, f.Store, output, cache, new Dialogs()))
                {
                    await app.InitializeAsync(); await module.InitializeAsync(default);
                    await module.PlayOnlineSongAsync("wy", "合成歌曲", "fixture", "", Element(new { songmid = "42" }));
                    Check(output.ReadOpens == 1 && output.NetworkOpens == 0 && app.Resolutions.Count == 0 &&
                        module.CurrentSource == id && output.IsPlaying, "restart reuses complete historical bytes and saved provenance");
                    await module.ReportWrongSourceAsync();
                    Check(!output.IsPlaying && !output.HasMedia && output.ReadOpens == 1,
                        "persistent wrong-source exclusion wins over historical bytes; no alternative means pause");
                    await module.RefreshSourceExclusionsAsync();
                    await module.RestoreSourceExclusionAsync(module.SourceExclusions.Single());
                    Check(!output.IsPlaying && output.Commits == 0, "restore changes no playback intent");
                    module.OpenSourceChoices(); output.Plan();
                    await module.SelectSourceChoiceAsync(module.SourceChoices.Single(row => row.Key == "auto"));
                    Check(output.Commits == 1 && app.Resolutions.Count == 0 && !output.IsPlaying,
                        "explicit selection can reuse restored historical bytes without re-enabling a removed script or resuming a paused song");
                    await module.ReportWrongSourceAsync();
                    await module.ClearPlaybackCacheAsync();
                    var exclusions = await app.ExecuteAsync("music.sourceExclusions.list", new { songId });
                    Check(exclusions.GetProperty("items").GetArrayLength() == 1 &&
                        (await cache.ReadScriptHistoryAsync(songId, default)).Length == 0,
                        "clearing audio leaves wrong-source exclusions intact");
                }
                output.Close();
            });
        foreach (double actual in new[] { 0d, 110d, 130d, 109.99d, 130.01d })
            yield return Test("opened media duration absolute boundary " + actual, async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false, scripts: [new("duration-script")]);
                f.Output.ActualDuration = actual;
                var song = MusicSongFixture.Online("wy", "Expected120", "DurationBoundary", "fixture", duration: 120);
                var queued = await f.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 });
                Check(queued.GetProperty("ok").GetBoolean(), "duration queue");
                await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
                await f.Module.PlayQueueSurfaceItemAsync(f.Module.QueueSurfaceRows.Single());
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                bool allowed = actual == 0 || actual is >= 110 and <= 130;
                if (!allowed) await Until(() => !f.Output.HasMedia, "mismatched input stopped");
                Check(f.Output.IsPlaying == allowed, "metadata duration is authoritative and unknown durations are allowed");
            });
        yield return Test("manual quality refuses lower actual quality on exact refresh", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play(); f.App.OfficialQuality = "flac";
            f.Output.Plan(); await f.Module.SwitchQualityAsync("lossless");
            int opens = f.Output.OpenedUris.Count;
            f.App.OfficialQuality = "320k";
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => !f.Output.HasMedia, "manual lower actual tier exhausted");
            Check(f.Output.OpenedUris.Count == opens, "a refresh is not permission to silently lower a manual tier");
        });

        foreach (string action in new[] { "disable", "rollback" })
            yield return Test("selection regression stale script cache after " + action, async () =>
            {
                await using var f = await ControlledMusicFixture.Create(
                    scripts: [new("cached-script")], qualityRequest: "auto", officialQuality: "flac");
                await f.Play();
                Check(f.Output.IsPlaying && f.Output.OpenedUris.Last().AbsolutePath.StartsWith("/official-"),
                    "fixture must begin on the official provider");
                f.Module.OpenSourceChoices();
                while (!f.Module.SourceChoices.Any(row => row.Provider == "cached-script" && row.Selectable) &&
                    f.Module.CanLoadMoreSources)
                    await f.Module.LoadMoreSourcesAsync();
                var choice = f.Module.SourceChoices.First(row => row.Provider == "cached-script" && row.Selectable);
                await f.Module.RefreshSourceManagementAsync();
                await Until(() => f.Module.CanEditScripts, "management editable");
                var row = f.Module.Sources.Single(row => row.InstallationId == "cached-script");
                if (action == "disable") await f.Module.SetSourceEnabledAsync(row, false);
                else await f.Module.RollbackSourceAsync(row);
                int before = f.App.Resolutions.Count;
                f.Output.Plan();
                await f.Module.SelectSourceChoiceAsync(choice);
                Check(f.Output.Commits == 1 && f.App.Resolutions.Count == before &&
                    f.Output.OpenedUris.Last().AbsolutePath.StartsWith("/cached-script-"),
                    "valid historical URL stays independent of script enablement and content generation");
            });
        yield return Test("selection regression newly enabled script participates in current explicit quality search", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(
                scripts: [new("new-script", enabled: false, quality: "flac")]);
            await f.Play();
            await f.Module.RefreshSourceManagementAsync();
            await Until(() => f.Module.CanEditScripts, "enable editable");
            await f.Module.SetSourceEnabledAsync(f.Module.Sources.Single(row => row.InstallationId == "new-script"), true);
            var facts = (await f.App.ExecuteAsync("music.sources.list")).GetProperty("items")[0];
            f.Output.Plan();
            await f.Module.SwitchQualityAsync("lossless");
            int scriptRequests = f.App.Resolutions.Count(row => Installation(row) == "new-script");
            Check(scriptRequests > 0 && f.Output.Commits == 1,
                "an enabled lossless provider must be considered for an explicit lossless request");
        });
        yield return Test("selection regression explicitly selected script is not silently replaced after refresh failure", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false,
                scripts: [new("first-script"), new("chosen-script")]);
            await f.Play();
            f.Module.OpenSourceChoices();
            await f.Module.LoadMoreSourcesAsync();
            var choice = f.Module.SourceChoices.Single(row => row.Provider == "chosen-script" && row.Selectable);
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(choice);
            Check(f.Output.OpenedUris.Last().AbsolutePath == "/chosen-script-wy-320k.wav",
                "explicit choice must be accepted before failure");
            f.App.ResolutionFailure = row => Installation(row) == "chosen-script" ? "MEDIA_UNAVAILABLE" : null;
            int before = f.Output.OpenedUris.Count;
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => f.Output.Reopens > 0 || !f.Output.HasMedia, "manual recovery settles");
            Check(f.Output.OpenedUris.Count == before,
                "a pinned provider result must not become a different provider automatically");
        });
        yield return Test("selection regression manual quality remains exact after successive provider failures", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(scripts:
                [new("first-lossless", quality: "flac"), new("second-lossless", quality: "flac")]);
            await f.Play();
            f.Output.Plan();
            await f.Module.SwitchQualityAsync("lossless");
            Check(f.Output.OpenedUris.Last().AbsolutePath == "/first-lossless-wy-flac.wav",
                "explicit lossless request must first succeed");
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpFormatUnsupported);
            await Until(() => f.Output.Reopens == 1 && f.Module.CanSwitchSource, "same-quality fallback");
            Check(f.Output.OpenedUris.Last().AbsolutePath == "/second-lossless-wy-flac.wav",
                "first replacement is still the requested lossless tier");
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpFormatUnsupported);
            await Until(() => f.Output.Reopens == 2 || !f.Output.HasMedia, "second failure settles");
            Check(f.Output.Reopens == 1 && !f.Output.HasMedia,
                "exhausted manual lossless must not silently fall back to automatic high quality");
        });
        yield return Test("selection regression remembered recording does not prevent wrong-source replacement after replay", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            var tx = await TxChoice(f.Module);
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(tx);
            await f.Play();
            Check(f.Module.CurrentSource == "QQ 音乐", "replay must reproduce the remembered recording");
            int commits = f.Output.Commits;
            f.Output.Plan();
            await f.Module.ReportWrongSourceAsync();
            Check(f.Output.Commits == commits + 1 && f.Module.CurrentSource == "网易云音乐" && f.Output.IsPlaying,
                "available same-quality WY must replace the excluded remembered TX recording");
        });
        yield return Test("selection regression selecting current quality cancels an earlier pending quality change", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            f.App.OfficialQuality = "flac";
            f.App.HoldResolution = row => row.GetProperty("quality").GetString() == "flac";
            f.Output.Plan();
            var switching = f.Module.SwitchQualityAsync("lossless");
            await f.App.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool cancelled;
            try
            {
                await f.Module.SwitchQualityAsync("high");
                cancelled = f.App.HeldResolutionToken.IsCancellationRequested;
            }
            finally { f.App.ReleaseRefresh.TrySetResult(); }
            await switching.WaitAsync(TimeSpan.FromSeconds(5));
            Check(cancelled && f.Output.Commits == 0 && f.Module.CurrentQualityLabel == "高音质",
                "the latest high-quality intention must supersede the pending lossless choice");
        });
        yield return Test("selection regression default quality change does not permanently disable explicit source choice", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            var tx = await TxChoice(f.Module);
            await f.Module.ActivateQualitySettingsAsync();
            await Until(() => f.Module.CanEditQuality, "quality editable");
            await f.Module.SetPlaybackQualityRequestAsync("128k");
            Check(f.Module.PlaybackQualityRequest == "128k", "default setting saved");
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(tx);
            Check(f.Output.Commits == 1 && f.Module.CurrentSource == "QQ 音乐",
                "a new explicit choice must be validated under current policy, not permanently disabled");
        });
        yield return Test("selection regression manual quality continues past a prepared-media failure", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(scripts:
                [new("bad-script", quality: "flac"), new("good-script", quality: "flac")]);
            await f.Play();
            f.Output.Plan(failure: PlaybackError.HttpUnauthorized);
            f.Output.Plan();
            await f.Module.SwitchQualityAsync("lossless");
            Check(f.Output.Commits == 1 && f.Output.OpenedUris.Last().AbsolutePath == "/good-script-wy-flac.wav",
                "a bad first media URL must not prevent an available same-tier replacement");
        });
        yield return Test("selection regression duration event during open is not lost", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false, scripts: [new("duration-script")]);
            var song = MusicSongFixture.Online("wy", "Expected60", "DurationFixture", "fixture", duration: 60);
            var queued = await f.App.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 });
            Check(queued.GetProperty("ok").GetBoolean(), "duration queue fixture");
            f.Output.Started += (_, _) =>
            {
                // A media port is allowed to announce its length before OpenAsync returns.
                var field = typeof(ControlledPlayback).GetField("PropertyChanged",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                ((PropertyChangedEventHandler?)field.GetValue(f.Output))?.Invoke(f.Output, new(nameof(IMusicPlayback.DurationSeconds)));
            };
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await f.Module.PlayQueueSurfaceItemAsync(f.Module.QueueSurfaceRows.Single());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var state = Field(f.Module, "musicRecovery")!;
            Check(!f.Output.IsPlaying || f.Output.Reopens > 0,
                "known contradictory duration must not be ignored merely because it arrived during open");
        });
        yield return Test("selection regression cancellation after native commit retains matching business source identity", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            var tx = await TxChoice(f.Module);
            f.Output.HoldCommit = true;
            f.Output.Plan();
            var choose = f.Module.SelectSourceChoiceAsync(tx);
            await f.Output.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task replacement;
            try
            {
                replacement = f.Module.SwitchQualityAsync("lossless");
                Check(f.Output.Commits == 1, "the first source is already committed at the media port");
            }
            finally { f.Output.ReleaseCommit.TrySetResult(); }
            await Task.WhenAll(choose, replacement).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Module.CurrentSource == "QQ 音乐",
                "a later missing choice must leave the actual committed source and its business identity consistent");
        });
    }


    // Only upstream catalogue HTTP and receipt timing are controlled. Prepare,
    // token commit/discard, queue persistence and cache production stay native.
    private sealed class PlaylistNextHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Check(uri.Host is "music.163.com" or "interfacepc.music.163.com", "synthetic playlist origin only");
            object body;
            if (uri.AbsolutePath.EndsWith("/rcmd/resource/show", StringComparison.Ordinal))
                body = new { code = 200, data = new { cursor = "1", hasMore = false,
                    blockCodeOrderList = "[\"PLAYLIST\"]", blocks = new[] {
                        new { positionCode = "PLAYLIST", uiElement = new { subTitle = new { title = "预取竞态歌单" } },
                            creatives = new[] { new { resources = new[] {
                                new { resourceType = "playlist", resourceId = "800", name = "插入歌单" }
                            } } } }
                    } } };
            else if (uri.AbsolutePath.Contains("/playlist/detail", StringComparison.Ordinal))
                body = new { code = 200, playlist = new { id = "800", name = "插入歌单", trackCount = 2,
                    trackIds = new[] { new { id = "8001" }, new { id = "8002" } }, updateTime = 1 } };
            else if (uri.AbsolutePath.Contains("/song/detail", StringComparison.Ordinal))
                body = new { code = 200, songs = new[] {
                    new { id = "8001", name = "插入首曲", ar = new[] { new { name = "fixture" } },
                        al = new { name = "插入专辑" }, dt = 120000 },
                    new { id = "8002", name = "插入次曲", ar = new[] { new { name = "fixture" } },
                        al = new { name = "插入专辑" }, dt = 120000 }
                } };
            else throw new InvalidOperationException("Unexpected playlist race upstream path: " + uri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class PlaylistNextProbe(bool holdPeek)
    {
        internal bool HoldPeek = holdPeek;
        internal bool LoseCommitReceipt;
        internal CancellationToken PeekToken, QueueReadToken;
        internal JsonElement? CommitInput, CommitReceipt;
        internal readonly List<string> CatalogMethods = [];
        internal readonly TaskCompletionSource PeekEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleasePeek = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource QueueReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseQueueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            string qualityRequest, string officialQuality, HttpMessageHandler? catalogHttp)
        {
            Origin.RequireAuthorization = false;
            App = new(new MusicNativeApplication(Store, Runtime, catalogHttp ?? new NoHttp()), Origin,
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
            string officialQuality = "320k", HttpMessageHandler? catalogHttp = null)
        {
            var f = new ControlledMusicFixture(playbackCache, official,
                scripts ?? [new("fixture-source", enabled: false)], qualityRequest, officialQuality, catalogHttp);
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
            await Until(() => Prepared is not null, "next file producer");
            return Prepared!;
        }
        private async Task<double> Validate(string path, double expected, CancellationToken token)
        {
            ValidationToken = token;
            ValidationEntered.TrySetResult();
            if (HoldValidation) await ValidationRelease.Task;
            return expected > 0 ? expected : 120;
        }
        public async ValueTask DisposeAsync()
        {
            App.ReleaseRefresh.TrySetResult();
            App.ReleaseExclusion.TrySetResult();
            App.PlaylistNext?.ReleasePeek.TrySetResult();
            App.PlaylistNext?.ReleaseQueueRead.TrySetResult();
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
    private sealed class ControlledPlayback : IMusicPlayback, INativePlaybackContinuation, INativePlaybackHandoff, ISystemPlayback
    {
        SystemPlaybackState ISystemPlayback.State =>
            new(OwnerId, Identity, "fixture", HasMedia, IsPlaying, PositionSeconds, DurationSeconds);
        Task ISystemPlayback.PlayAsync() => ResumeAsync();
        private ReopenPlan? prepared;
        internal bool HoldCommit;
        internal readonly TaskCompletionSource Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Prepares, Commits, Discards, OutputStops;
        internal bool FailNetworkHandoff, StartAfterPreparation;
        public Task StopOutputForHandoffAsync()
        {
            OutputStops++;
            HasMedia = false; IsPlaying = false;
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
            return Task.CompletedTask;
        }
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
            if (FailNetworkHandoff && uri.Host != "cache.invalid")
                throw new MusicOperationException(PlaybackError.HttpUnauthorized);
            prepared = plan;
        }
        internal double PreparedDuration;
        public double PreparedHandoffDurationSeconds => PreparedDuration;
        public async Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token)
        {
            await using (lease) await PrepareHandoffAsync(new Uri("https://cache.invalid/fixture"), new Dictionary<string, string>(), token);
        }
        public async Task CommitHandoffAsync(Action committed, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (prepared is null || prepared.Token.IsCancellationRequested || OwnerId is null ||
                Identity is null || authority.IsCancellationRequested) throw new MusicOperationException(PlaybackError.Superseded);
            prepared = null; Commits++;
            generation++; HasMedia = true; LastErrorCode = null; IsPlaying = !PauseRequested;
            committed();
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            Committed.TrySetResult();
            if (HoldCommit) await ReleaseCommit.Task; // Publication is irreversible; release is deliberately late.
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
        internal double ActualDuration = 120;
        public double DurationSeconds => ActualDuration;
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
        internal void Fail(string code, bool retire = false)
        {
            LastErrorCode = code; IsPlaying = false;
            if (retire) { HasMedia = false; PropertyChanged?.Invoke(this, new(nameof(HasMedia))); }
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
        public async Task ReopenReadAsync(NativePlaybackContinuation continuation, INativeMediaReadLease lease,
            CancellationToken token = default)
        {
            await using (lease)
            {
                if (lease.Length <= 0 || lease.Content.ReadByte() < 0) throw new MusicOperationException(PlaybackError.ReadFailed);
                ReadOpens++;
                await ReopenAsync(continuation, new Uri("https://cache.invalid/fixture"), new Dictionary<string, string>(), token);
            }
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
                if (StartAfterPreparation) Start();
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
            PropertyChanged?.Invoke(this, new(nameof(DurationSeconds)));
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
            PropertyChanged?.Invoke(this, new(nameof(DurationSeconds)));
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
        internal void SetDuration(double seconds)
        {
            ActualDuration = seconds;
            PropertyChanged?.Invoke(this, new(nameof(DurationSeconds)));
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
        internal PlaylistNextProbe? PlaylistNext;
        internal readonly List<JsonElement> Resolutions = [];
        internal bool PreserveOfficialRequest;
        internal bool HoldRefresh;
        internal bool RepeatExpiry;
        internal bool MatchMissing;
        internal readonly List<string> MatchPlatforms = [];
        internal readonly List<JsonElement> Completions = [];
        internal Func<JsonElement, JsonObject>? CompletionSong;
        internal bool HoldExclusion;
        internal readonly TaskCompletionSource ExclusionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseExclusion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Discoveries;
        internal bool HoldCompletion;
        internal readonly TaskCompletionSource CompletionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long OfficialGeneration = 7;
        internal void RenewOfficial(string platform)
        {
            OfficialGeneration++;
            revokedOfficial.Remove(platform);
            accountInvalidated?.Invoke(platform);
        }
        internal bool DistinctSourceUrls, OfferAlternate = true;
        internal string QualityRequest = "320k", OfficialQuality = "320k";
        internal long? OfficialExpiry;
        internal Func<JsonElement, bool>? HoldResolution;
        internal Func<JsonElement, string?>? ResolutionFailure;
        internal double? ScriptDuration;
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
            (official && scope == "fixture" && generation == OfficialGeneration && !revokedOfficial.Contains(platform)) ||
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
            if (method == "music.sourceExclusions.add" && HoldExclusion)
            {
                ExclusionEntered.TrySetResult();
                await ReleaseExclusion.Task.WaitAsync(cancellationToken);
            }
            if (PlaylistNext is { } probe)
            {
                if (method == "music.queue.peekNext" && probe.HoldPeek)
                {
                    probe.HoldPeek = false;
                    var oldNext = await inner.ExecuteAsync(method, parameters, cancellationToken);
                    Check(oldNext.GetProperty("ok").GetBoolean(), "native old peek fixture");
                    probe.PeekToken = cancellationToken; probe.PeekEntered.TrySetResult();
                    await probe.ReleasePeek.Task; // Deliberately return the old receipt after cancellation.
                    return oldNext;
                }
                if (method.StartsWith("music.catalog.playlist.", StringComparison.Ordinal))
                {
                    probe.CatalogMethods.Add(method);
                    if (method == "music.catalog.playlist.commit")
                    {
                        probe.CommitInput = input.Clone();
                        var committed = await inner.ExecuteAsync(method, parameters, cancellationToken);
                        probe.CommitReceipt = committed.Clone();
                        return probe.LoseCommitReceipt ? Element(new { ok = false, code = "STORAGE_ERROR" }) : committed;
                    }
                }
                if (method == "music.queue.get" && probe.CommitReceipt is { } receipt && receipt.GetProperty("ok").GetBoolean())
                {
                    probe.QueueReadToken = cancellationToken;
                    probe.QueueReadEntered.TrySetResult();
                    await probe.ReleaseQueueRead.Task; // No later snapshot may mask a missing mutation notification.
                }
            }
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
                    scope = "fixture", generation = OfficialGeneration,
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
                        sourceKey = LJ.S(r["sourceKey"]), generation = OfficialGeneration, scope = "fixture" }),
                    authorizationRequired = enabled.Length == 0 });
            }
            if (method == "music.songs.complete")
            {
                // The one supplementary search of a selection: an alternate tx recording and,
                // for scripts, a matched recording of each requested missing platform.
                Discoveries++;
                Completions.Add(input.Clone());
                if (HoldCompletion)
                {
                    CompletionEntered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
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
                return Element(new { ok = true, song = NativeMusicSong.Normalize(CompletionSong?.Invoke(input) ?? raw),
                    failures = Array.Empty<object>() });
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
                    sourceKey = input.GetProperty("sourceKey").GetString(), generation = OfficialGeneration, scope = "fixture",
                    quality = PreserveOfficialRequest ? input.GetProperty("quality").GetString() : OfficialQuality,
                    requestedQuality = input.GetProperty("quality").GetString(), actualQuality = OfficialQuality,
                    qualityPolicySignature = policy.GetProperty("signature").GetString(),
                    qualityCacheSignature = new string('a', 64), playbackVerified = false, qualityVerified = true,
                    url = origin.Uri(DistinctSourceUrls ? $"official-{platform}-{OfficialQuality}.wav" :
                        platform == "tx" ? "other.wav" : Resolutions.Count == 1 ? "old.wav" : "new.wav").AbsoluteUri,
                    headers = new { }, expiresAt = OfficialExpiry, duration = 120d,
                    size = full ? Wave().Length : 0, full, licenseExpiresAt = (long?)null,
                    delivery = platform + "-official", offlineAllowed = false,
                    authorizationExpiresAt = (long?)null });
            }
            // The shared module is replaced by these synthetic installations; the native
            // capability still consumes the actual list/item contract.
            if (method == "music.sources.list")
                return Element(new { ok = true, revision = SourceChanges, items = sources.Where(source => !source.Removed).Select(SourceItem) });
            if (method == "music.sources.detail")
                return Element(new { ok = true, item = SourceItem(sources.Single(row => row.Id == input.GetProperty("installationId").GetString())) });
            if (method is "music.sources.rollback" or "music.sources.disable" or "music.sources.enable" or "music.sources.remove")
            {
                // Synthetic version change; the real capability owns cancellation and drain.
                var source = sources.Single(row => row.Id == input.GetProperty("installationId").GetString());
                SourceChanges++;
                if (method == "music.sources.disable") source.Enabled = false;
                if (method == "music.sources.remove") { source.Removed = true; source.Enabled = false; }
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
                string resolvedQuality = input.GetProperty("quality").GetString()!;
                bool fresh = Resolutions.Count(row => Installation(row) == id) > 1;
                var quality = await ReadQualitySettings(cancellationToken);
                if (!quality.GetProperty("ok").GetBoolean()) return quality;
                if (!declaration.Enabled) return Element(new { ok = false, code = "SOURCE_DISABLED" });
                if (!declaration.Platforms.Contains(platform) || !(declaration.Qualities ?? [declaration.Quality]).Contains(resolvedQuality))
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
                    ("duration", songNode["duration"]), ("quality", resolvedQuality)));
                if (input.TryGetProperty("qualityPolicySignature", out var expectedPolicy) && expectedPolicy.GetString() != policy)
                    return Element(new { ok = false, code = "QUALITY_POLICY_CHANGED" });
                if (input.TryGetProperty("qualityCacheSignature", out var expectedCache) && expectedCache.GetString() != cache)
                    return Element(new { ok = false, code = "QUALITY_CACHE_CHANGED" });
                await WaitForResolution(input, fresh, cancellationToken);
                if (ResolutionFailure?.Invoke(input) is { } failure)
                    return Element(new { ok = false, code = failure });
                var result = JsonSerializer.SerializeToNode(new { ok = true,
                    url = origin.Uri(DistinctSourceUrls ? $"{id}-{platform}-{resolvedQuality}.wav" :
                        fresh ? "new.wav" : "old.wav").AbsoluteUri, headers = origin.Headers,
                    installationId = id, platform, generation = declaration.Generation, quality = resolvedQuality,
                    sourceKey, songId = input.GetProperty("song").GetProperty("songId").GetString(),
                    qualityPolicySignature = policy, qualityCacheSignature = cache, qualityVerified = false,
                    expiresAt = expiry && (!fresh || RepeatExpiry) ? (long?)DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeMilliseconds() : null,
                    playbackVerified = false }, Json)!.AsObject();
                if (ScriptDuration is { } duration) result["duration"] = duration;
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
            _ => new { actions = new[] { "musicUrl" }, qualitys = source.Qualities ?? [source.Quality] }),
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

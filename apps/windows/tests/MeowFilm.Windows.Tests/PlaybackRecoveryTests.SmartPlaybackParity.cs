using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

public static partial class PlaybackRecoveryTests
{
    private static IEnumerable<TestCase> SmartPlaybackParityCases()
    {
        yield return Test("parity manual browser explores all enabled script qualities independently of automatic policy", async () =>
        {
            string[] levels = ["128k", "192k", "320k", "flac", "flac24bit", "master"];
            await using var f = await ControlledMusicFixture.Create(official: false, qualityRequest: "flac",
                scripts: [new("all-levels") { Qualities = levels },
                    new("disabled", enabled: false, quality: "master")]);
            await RestrictAutomaticToLossless(f);
            await f.Play();
            Check(f.App.Resolutions.Select(row => row.GetProperty("quality").GetString()).SequenceEqual(["flac"]),
                "automatic playback still obeys the sole enabled category");
            int before = f.App.Resolutions.Count, completions = f.App.Completions.Count;
            f.Module.OpenSourceChoices();
            Check(f.App.Resolutions.Count == before && f.App.Completions.Count == completions,
                "opening the source browser reads only known results");
            while (f.Module.CanLoadMoreSources) await f.Module.LoadMoreSourcesAsync();
            var rows = f.Module.SourceChoices.Where(row => row.Provider == "all-levels" && row.Selectable).ToArray();
            Check(rows.Select(row => row.Level).Order().SequenceEqual(levels.Order()) &&
                rows.All(row => row.Recording.Length > 0), "all declared qualities and their recording identities are displayed");
            Check(f.App.Resolutions.Where(row => Installation(row) == "all-levels")
                    .GroupBy(row => row.GetProperty("quality").GetString()).All(group => group.Count() == 1) &&
                f.App.Resolutions.All(row => Installation(row) != "disabled"),
                "load more covers untried requests without repeating successes or invoking disabled installations");
            before = f.App.Resolutions.Count;
            await f.Module.LoadMoreSourcesAsync();
            Check(f.App.Resolutions.Count == before && !f.Module.CanLoadMoreSources, "exhausted manual exploration is finite");
            var policy = await f.App.ExecuteAsync("music.quality.settings.get");
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(rows.Single(row => row.Level == "master"));
            var after = await f.App.ExecuteAsync("music.quality.settings.get");
            Check(f.Output.Commits == 1 && f.Output.OpenedUris.Last().AbsolutePath == "/all-levels-wy-master.wav" &&
                f.App.Resolutions.Count == before && policy.GetRawText() == after.GetRawText(),
                "a concrete disabled automatic level commits its existing result without changing global policy");
        });
        yield return Test("parity manual result renews its exact disabled quality after automatic policy changes", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false, qualityRequest: "flac",
                scripts: [new("automatic", quality: "flac"), new("manual-master", quality: "master")]);
            await RestrictAutomaticToLossless(f);
            await f.Play();
            f.Module.OpenSourceChoices();
            while (!f.Module.SourceChoices.Any(row => row.Provider == "manual-master" && row.Selectable) &&
                f.Module.CanLoadMoreSources) await f.Module.LoadMoreSourcesAsync();
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(f.Module.SourceChoices.Single(row => row.Provider == "manual-master" && row.Selectable));
            await f.Module.ActivateQualitySettingsAsync();
            await Until(() => f.Module.CanEditQuality, "manual renewal settings editable");
            await f.Module.SetPlaybackQualityRequestAsync("128k");
            int before = f.App.Resolutions.Count;
            await f.Module.SeekAsync(36);
            await f.Module.TogglePlaybackAsync();
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpExpired);
            await Until(() => f.Output.Reopens == 1 && f.Module.CanSwitchSource, "manual master renewal");
            var request = f.App.Resolutions.Skip(before).Single();
            Check(Installation(request) == "manual-master" && request.GetProperty("quality").GetString() == "master" &&
                request.GetProperty("manualSource").GetBoolean() && f.Output.PositionSeconds == 36 &&
                f.Output.PauseRequested && !f.Output.IsPlaying && f.Module.CurrentSource == "manual-master",
                "renewal retains concrete provider, recording, quality, position and pause without automatic filtering");
        });
        yield return Test("parity manual disabled-quality complete file survives policy changes and renews after file loss", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(playbackCache: true, official: false, qualityRequest: "flac",
                scripts: [new("file-lossless", quality: "flac"), new("file-master", quality: "master")]);
            await RestrictAutomaticToLossless(f);
            await f.Play();
            f.Module.OpenSourceChoices();
            var lossless = f.Module.SourceChoices.Single(row => row.Provider == "file-lossless" && row.Selectable);
            while (!f.Module.SourceChoices.Any(row => row.Provider == "file-master" && row.Selectable) &&
                f.Module.CanLoadMoreSources) await f.Module.LoadMoreSourcesAsync();
            var master = f.Module.SourceChoices.Single(row => row.Provider == "file-master" && row.Selectable);
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(master);
            string songId = f.App.Resolutions[0].GetProperty("song").GetProperty("songId").GetString()!;
            await Until(() => f.Cache!.ReadScriptHistoryAsync(songId, default).GetAwaiter().GetResult()
                .Any(row => row.GetProperty("quality").GetString() == "master"), "played manual master file");
            await Until(() => f.Module.SourceChoices.Any(row =>
                row.Provider == "file-master" && row.Quality == "无损" && row.Level == "flac"),
                "actual WAV evidence reaches the same manually selected attempt");
            Check(f.Module.CurrentQualityLabel == "无损",
                "the requested master label cannot override verified lossless media evidence");
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(lossless);
            await f.Module.ActivateQualitySettingsAsync();
            await Until(() => f.Module.CanEditQuality, "manual cache settings editable");
            await f.Module.SetPlaybackQualityRequestAsync("128k");
            int before = f.App.Resolutions.Count;
            f.Output.Plan();
            await f.Module.SelectSourceChoiceAsync(master);
            Check(f.Output.Commits == 3 && f.Module.CurrentSource == "file-master" &&
                f.Output.OpenedUris.Last().Host == "cache.invalid" && f.App.Resolutions.Count == before,
                "a complete concrete representation bypasses disabled automatic quality and needs no new source request");
            f.Output.Plan(startBeforeCompletion: true);
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => f.Output.Reopens == 1 && f.Module.CanSwitchSource, "manual file renewal with new policy");
            var renewed = f.App.Resolutions.Skip(before).Single();
            Check(f.Output.IsPlaying && f.Module.CurrentSource == "file-master" &&
                Installation(renewed) == "file-master" && renewed.GetProperty("quality").GetString() == "master" &&
                renewed.GetProperty("manualSource").GetBoolean(),
                "lost file bytes reacquire exact parser authority under the current policy instead of reusing an old signature");
        });
        yield return Test("parity concrete source rejects a refreshed known lower actual quality", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(qualityRequest: "flac", officialQuality: "flac");
            f.App.PreserveOfficialRequest = true;
            await f.Play();
            f.Module.OpenSourceChoices();
            await f.Module.SelectSourceChoiceAsync(f.Module.SourceChoices.Single(row => row.Key != "auto" && row.Selectable));
            int opens = f.Output.OpenedUris.Count;
            f.App.OfficialQuality = "320k";
            f.Output.Fail(PlaybackError.HttpUnauthorized);
            await Until(() => !f.Output.HasMedia, "known changed actual quality is rejected");
            Check(f.Output.OpenedUris.Count == opens,
                "concrete selection keeps its proven actual quality even when the original request is unchanged");
        });
        foreach (bool earlierDiscovery in new[] { false, true })
            yield return Test("parity content failure rechecks an existing platform " + (earlierDiscovery ? "after earlier completion" : "without a missing platform"), async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false,
                    qualityRequest: earlierDiscovery ? "auto" : "320k",
                    scripts: [new("mapped", quality: earlierDiscovery ? "flac" : "320k")]);
                f.App.ResolutionFailure = row => row.GetProperty("song").GetProperty("sources").EnumerateArray()
                    .Any(source => source.GetProperty("musicInfo").GetProperty("songmid").GetString() == "Fresh")
                    ? null : "RECORDING_UNAVAILABLE";
                f.App.CompletionSong = input =>
                {
                    var original = JsonNode.Parse(input.GetProperty("song").GetRawText())!.AsObject();
                    if (!input.GetProperty("contentFailed").EnumerateArray().Any(value => value.GetString() == "wy")) return original;
                    var fresh = MusicSongFixture.Online("wy", "Fresh", LJ.S(original["title"]), LJ.S(original["artist"]),
                        LJ.S(original["album"]), LJ.N(original["duration"]) > 0 ? LJ.N(original["duration"]) : 120,
                        native: earlierDiscovery ? QualityInfo("Fresh") : null);
                    original["sources"] = fresh["sources"]!.DeepClone();
                    return original;
                };
                await f.Play();
                var content = f.App.Completions.Where(input =>
                    input.GetProperty("contentFailed").GetArrayLength() > 0).ToArray();
                Check(content.Length == 1 && content[0].GetProperty("contentFailed").EnumerateArray()
                        .Select(value => value.GetString()).SequenceEqual(["wy"]) &&
                    content[0].GetProperty("platforms").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["wy"]),
                    "known mappings are passed to the metadata owner with exact content-failure evidence");
                Check(f.App.Completions.Count == (earlierDiscovery ? 2 : 1) && f.App.Resolutions.Count == 2 &&
                    f.Output.NetworkOpens == 1 && f.Output.IsPlaying,
                    "a changed recording on the same platform is retried once and becomes playable");
            });
        foreach (string failure in new[] { "NETWORK_ERROR", "TIMEOUT", "RECORDING_UNAVAILABLE" })
            yield return Test("parity existing platform failures remain classified and finite " + failure, async () =>
            {
                await using var f = await ControlledMusicFixture.Create(official: false,
                    scripts: [new("classified")]);
                f.App.ResolutionFailure = _ => failure;
                await f.Play();
                Check(f.App.Completions.Count == 1 && f.App.Completions[0].GetProperty("contentFailed").GetArrayLength() ==
                    (failure == "RECORDING_UNAVAILABLE" ? 1 : 0) && f.App.Resolutions.Count == 1 &&
                    f.Output.NetworkOpens == 0, "network failures never become content evidence and unchanged mappings are not retried");
            });
        yield return Test("parity mixed content and transport failures do not force a platform recheck", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false,
                scripts: [new("content"), new("transport")]);
            f.App.ResolutionFailure = row => Installation(row) == "content" ? "MEDIA_UNAVAILABLE" : "TIMEOUT";
            await f.Play();
            Check(f.App.Resolutions.Count == 2 && f.App.Completions.Count == 1 &&
                f.App.Completions[0].GetProperty("contentFailed").GetArrayLength() == 0 && f.Output.NetworkOpens == 0,
                "another provider's transport failure prevents ambiguous content evidence from triggering a recheck");
        });
        yield return Test("parity original transport failure remains classified when the source snapshot is renewed", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false,
                scripts: [new("renewed-source")]);
            await f.Play();
            await f.Module.RefreshSourceManagementAsync();
            await Until(() => f.Module.CanEditScripts, "source snapshot editable");
            await f.Module.RollbackSourceAsync(f.Module.Sources.Single(row => row.InstallationId == "renewed-source"));
            f.App.ResolutionFailure = _ => "RECORDING_UNAVAILABLE";
            f.Output.Fail(PlaybackError.HttpNetworkFailed);
            await Until(() => !f.Output.HasMedia, "renewed snapshot recovery settles");
            Check(f.App.Completions.Count == 1 && f.App.Completions[0].GetProperty("contentFailed").GetArrayLength() == 0 &&
                f.App.Resolutions.Count == 2 && f.Output.Reopens == 0,
                "a later content response cannot turn the original failed transport into stale-recording evidence");
        });
        foreach (bool success in new[] { false, true })
        foreach (bool pause in new[] { false, true })
            yield return Test("parity failed old input waits for pending explicit result " +
                (success ? "success" : "then restores committed intent") + (pause ? " with latest pause" : " playing"), async () =>
            {
                await using var f = await ControlledMusicFixture.Create();
                await f.Play();
                await f.Module.SwitchQualityAsync("high");
                var choice = await TxChoice(f.Module);
                int before = f.App.Resolutions.Count;
                await f.Module.SeekAsync(43);
                var candidate = f.Output.Plan(hold: true, failure: success ? null : PlaybackError.HttpUnauthorized);
                var selection = f.Module.SelectSourceChoiceAsync(choice);
                await candidate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Output.Fail(PlaybackError.HttpUnauthorized, retire: true);
                Check(!f.Output.HasMedia && f.Output.Reopens == 0 && f.App.Resolutions.Count == before,
                    "old failure preserves the pending choice without a second resolver or competing recovery");
                var pauseCommand = pause ? f.Module.TogglePlaybackAsync() : Task.CompletedTask;
                if (pause)
                {
                    Check(f.Output.PauseRequested && !f.Output.IsPlaying && !selection.IsCompleted,
                        "a user pause is admitted while A is retired and B is still preparing");
                }
                if (!success) f.Output.Plan(startBeforeCompletion: true);
                candidate.Release.TrySetResult();
                await Task.WhenAll(selection, pauseCommand).WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Output.PositionSeconds == 43 && f.Output.IsPlaying == !pause && f.Output.PauseRequested == pause &&
                    f.Output.Commits == (success ? 1 : 0) && f.Output.Reopens == (success ? 0 : 1) &&
                    f.Module.CurrentSource == (success ? "QQ 音乐" : "网易云音乐"),
                    "pending success commits B; pending failure recovers A once with the original controls");
                var requests = f.App.Resolutions.Skip(before).ToArray();
                Check(success ? requests.Length == 0 : requests.Length == 1 &&
                    requests[0].GetProperty("platform").GetString() == "wy" &&
                    requests[0].GetProperty("quality").GetString() == "320k",
                    "failed B never becomes the committed recovery target");
            });
        yield return Test("parity wrong source stops output synchronously before exclusion storage and preserves pending controls", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await TxChoice(f.Module);
            await f.Module.SeekAsync(47);
            string identity = f.Output.Identity!;
            f.Output.Volume = 31;
            f.App.HoldExclusion = true;
            var replacement = f.Output.Plan(hold: true);
            var wrong = f.Module.ReportWrongSourceAsync();
            Check(f.Output.OutputStops == 1 && !f.Output.IsPlaying && !f.Output.HasMedia &&
                !f.Output.PauseRequested && f.Output.PositionSeconds == 47 && f.Output.Identity == identity,
                "wrong audio is retired immediately without changing the saved user controls or occurrence");
            await f.App.ExclusionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!wrong.IsCompleted && !replacement.Entered.Task.IsCompleted && f.Output.Commits == 0,
                "durable exclusion precedes replacement resolution and native preparation");
            await f.Module.ResumePlaybackAsync();
            Check(!f.Output.IsPlaying, "resume cannot revive a source already rejected in this session");
            f.App.ReleaseExclusion.TrySetResult();
            await replacement.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!f.Output.IsPlaying && f.Module.CurrentSource == "网易云音乐",
                "a pending same-tier candidate is still silent and uncommitted");
            replacement.Release.TrySetResult();
            await wrong.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.RefreshSourceExclusionsAsync();
            Check(f.Module.SourceExclusions.Single().Platform == "wy" && f.Output.Commits == 1 &&
                f.Module.CurrentSource == "QQ 音乐" && f.Output.IsPlaying && !f.Output.PauseRequested &&
                f.Output.Identity == identity && f.Output.PositionSeconds == 47 && f.Output.Volume == 31,
                "same-quality replacement commits at the retained position and restores the original playing intent");
        });
        yield return Test("parity superseding a wrong-source replacement cannot cancel the durable exclusion", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            var tx = await TxChoice(f.Module);
            f.App.HoldExclusion = true;
            var wrong = f.Module.ReportWrongSourceAsync();
            await f.App.ExclusionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Output.Plan();
            var newer = f.Module.SelectSourceChoiceAsync(tx);
            Check(!f.Output.HasMedia && !f.Output.IsPlaying && f.Output.Prepares == 0,
                "the newer choice still waits for the already-admitted exclusion");
            f.App.ReleaseExclusion.TrySetResult();
            await Task.WhenAll(wrong, newer).WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.RefreshSourceExclusionsAsync();
            Check(f.Module.SourceExclusions.Single().Platform == "wy" && f.Output.Commits == 1 &&
                f.Module.CurrentSource == "QQ 音乐" && f.Output.IsPlaying,
                "changing the pending choice cannot erase the permanent report or revive its wrong audio");
        });
        yield return Test("parity user pause during a silent wrong-source replacement is inherited at commit", async () =>
        {
            await using var f = await ControlledMusicFixture.Create();
            await f.Play();
            await TxChoice(f.Module);
            await f.Module.SeekAsync(41);
            var prepared = f.Output.Plan(hold: true);
            var wrong = f.Module.ReportWrongSourceAsync();
            await prepared.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var paused = f.Module.TogglePlaybackAsync();
            Check(f.Output.PauseRequested && !f.Output.HasMedia && !f.Output.IsPlaying && !wrong.IsCompleted,
                "Toggle must admit Pause while the rejected input is silent and its replacement is pending");
            prepared.Release.TrySetResult();
            await Task.WhenAll(wrong, paused).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Output.Commits == 1 && f.Module.CurrentSource == "QQ 音乐" &&
                f.Output.PauseRequested && !f.Output.IsPlaying && f.Output.PositionSeconds == 41,
                "the replacement inherits the latest pause instead of resuming from the pre-report playing intent");
        });
        foreach (string failure in new[] { "MEDIA_UNAVAILABLE", "NETWORK_ERROR" })
            yield return Test("parity wrong source exhausted replacement remains paused without old audio or queue advance " + failure, async () =>
            {
                await using var f = await ControlledMusicFixture.Create(scripts: [new("replacement")]);
                await f.Play();
                await f.Module.SeekAsync(39);
                string identity = f.Output.Identity!, title = f.Module.CurrentTitle;
                f.App.ResolutionFailure = _ => failure;
                await f.Module.ReportWrongSourceAsync();
                await f.Module.RefreshSourceExclusionsAsync();
                Check(f.Output.OutputStops == 1 && !f.Output.HasMedia && !f.Output.IsPlaying && f.Output.PauseRequested &&
                    f.Output.Identity == identity && f.Output.PositionSeconds == 39 && f.Module.CurrentTitle == title &&
                    f.Output.Commits == 0 && f.Output.Reopens == 0 && f.App.HistoryWrites == 1 &&
                    f.Module.SourceExclusions.Single().Platform == "wy",
                    "every failed replacement keeps the excluded old input retired, the same song selected and the pause explicit");
                int requests = f.App.Resolutions.Count;
                await f.Module.TogglePlaybackAsync();
                Check(!f.Output.HasMedia && !f.Output.IsPlaying && f.Output.PauseRequested &&
                    f.Output.Identity == identity && f.Output.PositionSeconds == 39 && f.Output.NetworkOpens == 1 &&
                    f.Output.Commits == 0 && f.Output.Reopens == 0 && f.App.Resolutions.Count == requests &&
                    f.App.HistoryWrites == 1 && f.Module.Status.Contains("已排除", StringComparison.Ordinal),
                    "ordinary Toggle on a retained paused occurrence cannot restart or re-resolve its excluded audio");
            });
    }
    private static async Task RestrictAutomaticToLossless(ControlledMusicFixture f)
    {
        var current = await f.App.ExecuteAsync("music.quality.settings.get");
        var policy = JsonNode.Parse(current.GetProperty("settings").GetRawText())!.AsObject();
        policy["ifRevision"] = policy["revision"]!.DeepClone();
        policy.Remove("schema"); policy.Remove("revision");
        policy["request"] = "flac";
        policy["qualityEnabled"] = new JsonArray("lossless");
        policy["qualityOrder"] = new JsonArray("standard", "high", "lossless", "hires", "enhanced", "spatial", "master");
        Check((await f.App.ExecuteAsync("music.quality.settings.save", policy)).GetProperty("ok").GetBoolean(),
            "automatic policy fixture keeps only lossless enabled");
    }
}

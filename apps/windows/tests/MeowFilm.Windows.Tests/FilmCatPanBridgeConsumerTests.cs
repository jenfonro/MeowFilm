using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

// The existing reflection inventory registers this group. Requires the same public
// tests/film-cat-pan-bridge-fixture.mjs as iOS; the native sink never opens media.
public static class FilmCatPanBridgeConsumerTests
{
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-cat-pan-consumer: " + name, () => MusicQualityConsumerTests.Sta(run));

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("real HTTP SSE auto playback and directory selection preserve Cat pan file quality and local resource", Directory);
        yield return Test("real HTTP metadata entry auto plays first episode and source menu preserves its identity", MetadataEntry);
        yield return Test("real HTTP descriptor preserves DASH CENC receipt without reading media", () => Descriptor(direct: false));
        yield return Test("real HTTP headerless descriptor selects direct native input without a lease", () => Descriptor(direct: true));
        yield return Test("real HTTP list failure notifies and presentation resume rebuilds the same directory", ListFailure);
        yield return Test("close drains an admitted HTTP attempt and releases it without native publication", LateReceipt);
    }

    private static async Task Directory()
    {
        await using var scene = await Scene.Connect();
        await scene.Open(scene.Target);
        await scene.Ready(() => scene.Page.HasOutput && scene.Page.CurrentCandidateId.Length > 0 &&
            scene.Native.Inputs.Count == 1 && scene.Native.SettledIdentity == scene.Page.CurrentAttemptId &&
            scene.Traffic.EventSnapshots.Any(snapshot =>
                FilmJson.Text(FilmJson.Member(snapshot, "current"), "attemptId") == scene.Page.CurrentAttemptId));
        var page = scene.Page;
        Check(page.EntrySurface == "search" && page.ActiveTarget.Kind == "source" &&
            page.EntryTarget.Id == scene.Target.Id && page.EntryTarget.Source is { } original &&
            JsonElement.DeepEquals(original, scene.Target.Source!.Value), "the real Windows source entry keeps its exact Cat identity");
        var source = page.Sources.Single(row => row.MetadataSourceId is null);
        Check(source.Label == "Bridge site" && page.PlayerPanLabel == "夸克" &&
            page.PlayerQualityLabel == "4K·60帧" && !page.RequiresSourceSearch,
            "Windows automatically plays the real high quality file without a seed search");
        var opened = scene.Traffic.Calls.Single(row => row.Method == "film.session.open").Input;
        Check(FilmJson.Text(opened, "platform") == "windows" && FilmJson.Text(opened, "entrySurface") == "search" &&
            JsonElement.DeepEquals(opened.GetProperty("target"), FilmJson.Encode(scene.Target.Wire())) &&
            FilmJson.Array(opened, "sources").Length == 1 &&
            FilmJson.Text(FilmJson.Array(opened, "sources")[0], "key") == FilmJson.Text(scene.Source, "key"),
            "the consumer opens the fixture target and source, not a synthetic media address");
        await scene.Ready(() => page.SourceId == source.Id && !page.ListLoading && page.Episodes.Count == 3);
        Check(page.SelectedSource?.Label == "Bridge site" && page.SelectedLine is { Label: "夸克", Provider: "quark" } &&
            page.Lines.Count == 1 && page.ListError.Length == 0 &&
            page.Episodes.Select(row => row.Quality).SequenceEqual(new[] { "4k_fps", "1080p", "1080p" }) &&
            page.Episodes.Select(row => row.Number).SequenceEqual(new int?[] { 1, 1, 2 }),
            "the real site menu and separate pan menu display the provider-normalized directory");
        var refresh = scene.Traffic.Commands("browse.refresh").Single();
        Check(FilmJson.Text(refresh.Input, "sessionId") == scene.SessionId.ToString() &&
            FilmJson.Member(refresh.Input, "refresh").ValueKind == JsonValueKind.False &&
            !scene.Traffic.Commands("browse").Any(),
            "entering the direct source refreshes its actual directory without a second source-selection action");
        var high = page.Candidates.Single(row => row.Quality == "4k_fps");
        Check(high.Provider == "quark" && high.SourceKey == FilmJson.Text(scene.Source, "key") &&
            high.DetailId.ValueKind == JsonValueKind.String && high.DetailId.GetString() == "bridge-detail" &&
            high.Resolution.GetString() == "3840x2160" && Math.Abs(high.Fps.GetDouble() - 60_000d / 1_001) < .0001 &&
            high.Codec == "hevc" && high.Season == 1 && high.Episode == 1 &&
            page.Episodes.First().CandidateId == high.Id && page.Episodes.First().Path == high.Path,
            "public candidates and native rows share exact file, episode, quality and media metadata");
        CheckReceipt(scene, scene.Native.Inputs.Single().Receipt, high);
        foreach (double width in new[] { 1150d, 780d })
        {
            scene.Window.Width = width; scene.Window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            scene.Window.UpdateLayout();
            var station = scene.Element<ComboBox>("SourcePicker");
            var pan = scene.Element<ComboBox>("LinePicker");
            var row = station.Parent as Grid;
            var stationPoint = row is null ? new Point(double.NaN, double.NaN) : station.TranslatePoint(new(), row);
            var panPoint = row is null ? new Point(double.NaN, double.NaN) : pan.TranslatePoint(new(), row);
            Check(row is not null && ReferenceEquals(station.Parent, pan.Parent),
                $"source and pan share one horizontal row at width {width}");
            Check(Math.Abs(stationPoint.Y - panPoint.Y) < .5 &&
                Math.Abs(panPoint.X - stationPoint.X - station.ActualWidth - 10) < .5,
                $"source and pan remain adjacent at width {width}: source={stationPoint}, pan={panPoint}, sourceWidth={station.ActualWidth}");
            Check(station.SelectedItem is FilmSelection stationItem && stationItem == page.SelectedSource &&
                pan.SelectedItem is FilmSelection panItem && panItem == page.SelectedLine,
                $"source and pan keep their exact selected identities at width {width}");
            Check(AutomationProperties.GetName(station) == "站点来源" && AutomationProperties.GetName(pan) == "网盘来源",
                $"source and pan keep distinct accessibility names at width {width}");
        }
        var firstEvidence = await scene.InterceptionEvidence(2);
        var first = scene.Records(firstEvidence);
        CheckInterception(first);
        Check(first.Where(row => FilmJson.Text(row, "layer") == "pan-service")
                .Select(row => FilmJson.Text(row, "phase")).SequenceEqual(new[] { "list", "prepare", "play" }),
            "the initial receipt originates from the product list then prepare then play");
        var intercepted = first.Single(row => FilmJson.Text(row, "layer") == "cat" && FilmJson.Text(row, "phase") == "intercepted");
        var resource = FilmJson.Array(intercepted, "resources").Single();
        var normalized = FilmJson.Array(first.Single(row => FilmJson.Text(row, "layer") == "normalization" &&
            FilmJson.Text(row, "phase") == "files"), "files");
        Check(normalized.Select(row => FilmJson.Text(row, "fileId"))
                .SequenceEqual(new[] { "episode-1-high", "episode-1-low", "episode-2" }) &&
            normalized.Select(row => FilmJson.Member(row, "size").GetInt64()).SequenceEqual(new long[] { 123456, 65432, 54321 }) &&
            normalized.Select(row => FilmJson.Text(row, "path")).SequenceEqual(page.Episodes.Select(row => row.Path)) &&
            normalized.All(row => FilmJson.Text(row, "provider") == FilmJson.Text(resource, "provider") &&
                FilmJson.Text(row, "shareId") == FilmJson.Text(resource, "shareId") &&
                FilmJson.Integer(row, "accountRevision") == 1) &&
            FilmJson.Text(resource, "installationId") == FilmJson.Text(scene.Source, "installationId") &&
            FilmJson.Text(resource, "siteId") == FilmJson.Text(scene.Source, "siteId") &&
            FilmJson.Bool(resource, "passwordPresent"), "Cat interception and actual provider normalization retain one share and account identity");
        CheckPanFile(first, "episode-1-high");
        var provider = first.Where(row => FilmJson.Text(row, "layer") == "provider").ToArray();
        Check(provider.Select(row => FilmJson.Text(row, "phase"))
                .SequenceEqual(new[] { "token", "list", "list", "destination", "prepare", "play" }) &&
            provider.Where(row => FilmJson.Text(row, "phase") == "list").Select(row => FilmJson.Text(row, "parentFileId"))
                .SequenceEqual(new[] { "0", "season-one" }),
            "the real provider performs token and recursive list before exact transfer and playback");
        foreach (string id in new[] { page.Candidates.Single(row => row.Quality == "1080p" && row.Episode == 1).Id,
                     page.Candidates.Single(row => row.Episode == 2).Id })
        {
            var episode = page.Episodes.Single(row => row.CandidateId == id);
            var button = Visuals(scene.Element<ItemsControl>("EpisodeCards")).OfType<Button>()
                .Single(row => row.Tag is FilmEpisodeItem item && item.CandidateId == id);
            int before = scene.Native.Inputs.Count;
            Click(button);
            await scene.Ready(() => page.CurrentCandidateId == id && scene.Native.Inputs.Count == before + 1 &&
                scene.Native.SettledIdentity == page.CurrentAttemptId && scene.Traffic.EventSnapshots.Any(snapshot =>
                    FilmJson.Text(FilmJson.Member(snapshot, "current"), "attemptId") == page.CurrentAttemptId));
            Check(page.SelectedEpisode is { } selected && selected.CandidateId == id && selected.Path == episode.Path &&
                page.PlayerQualityLabel == "1080P" && page.ListError.Length == 0,
                "the actual episode control selects its file and quality without title matching");
            var candidate = page.Candidates.Single(row => row.Id == id);
            CheckReceipt(scene, scene.Native.Inputs.Last().Receipt, candidate);
        }
        await scene.Ready(() => scene.Native.Inputs.All(input => scene.Traffic.EventSnapshots.Any(snapshot =>
            FilmJson.Text(FilmJson.Member(snapshot, "current"), "attemptId") == input.Receipt.AttemptId)));
        Check(scene.Traffic.Commands("select.candidate").Select(row => FilmJson.Text(row.Input, "candidateId"))
                .SequenceEqual(scene.Native.Inputs.Skip(1).Select(input => scene.Traffic.Snapshots.First(snapshot =>
                    FilmJson.Text(FilmJson.Member(snapshot, "attempt"), "id") == input.Receipt.AttemptId)
                    .GetProperty("attempt").GetProperty("candidate").GetProperty("id").GetString())) &&
            !scene.Traffic.Commands("search.more").Any() && !scene.Traffic.Commands("select.episode").Any(),
            "file controls send opaque candidate IDs with their current session, not guessed metadata coordinates");
        await scene.Close();
        Check(!scene.Session.HasSession && !scene.Native.HasOutput && scene.Native.Stops == 1 &&
            scene.Traffic.EventStarts > 0 && scene.Traffic.EventDrains == scene.Traffic.EventStarts &&
            scene.Traffic.Commands("media.started").Count() == 3 &&
            scene.Traffic.Calls.Count(row => row.Method == "film.session.release") == 1,
            "native close drains real SSE and the accepted output exactly once");
        foreach (var input in scene.Native.Inputs)
            Check(scene.Traffic.Calls.Count(row => row.Method == "film.session.media.release" &&
                FilmJson.Text(row.Input, "attemptId") == input.Receipt.AttemptId &&
                FilmJson.Text(row.Input, "resourceId") == input.Receipt.ResourceId) == 1,
                "each admitted native input releases its exact Node receipt once");
        var final = await scene.Evidence();
        var records = scene.Records(final);
        CheckPanFile(records, "episode-1-low"); CheckPanFile(records, "episode-2");
        scene.CheckNoMedia(final);
        Console.WriteLine("OBSERVED film-cat-pan windows directory; HTTP/SSE; files=3; nativeReceipts=3; exact-release=3; mediaHead=0; mediaBody=0");
    }

    private static async Task MetadataEntry()
    {
        await using var scene = await Scene.Connect();
        await scene.Open(scene.MetadataTarget);
        await scene.Ready(() => scene.Page.HasOutput && scene.Page.ActiveTarget.Episode == 1 &&
            scene.Native.Inputs.Count == 1 && scene.Native.SettledIdentity == scene.Page.CurrentAttemptId);
        var page = scene.Page;
        var input = scene.Native.Inputs.Single();
        var candidate = FilmCandidateItem.Read(scene.Traffic.Snapshots.Last(snapshot =>
            FilmJson.Text(FilmJson.Member(snapshot, "current"), "attemptId") == input.Receipt.AttemptId)
                .GetProperty("current").GetProperty("candidate"));
        Check(page.ActiveTarget.MetadataProvider == "tmdb" && page.ActiveTarget.MetadataId == scene.MetadataTarget.MetadataId &&
            page.ActiveTarget.Season == 1 && page.ActiveTarget.Episode == 1 && !input.Intent.Paused &&
            page.SelectedSource?.Label == "TMDB" && !page.HasSeasons &&
            page.Episodes.Select(row => row.Number).SequenceEqual(new int?[] { 1, 2 }) &&
            page.Episodes.Select(row => row.Title).SequenceEqual(new[] { "Episode 1", "Episode 2" }) &&
            page.PlayerPanLabel == "夸克" && page.PlayerQualityLabel == "4K·60帧",
            "Windows metadata entry automatically starts the real first episode and retains Node's display metadata");
        CheckReceipt(scene, input.Receipt, candidate);
        var source = page.Sources.Single(row => row.MetadataSourceId is null);
        scene.Element<ComboBox>("SourcePicker").SetCurrentValue(ComboBox.SelectedItemProperty, source);
        await scene.Ready(() => page.SourceId == source.Id && !page.ListLoading && page.Episodes.Count == 3);
        var browse = scene.Traffic.Commands("browse").Single();
        Check(FilmJson.Text(browse.Input, "sourceId") == source.Id &&
            FilmJson.Text(browse.Input, "sessionId") == scene.SessionId.ToString() &&
            page.SelectedSource?.Label == "Bridge site" && page.SelectedLine is { Label: "夸克", Provider: "quark" } &&
            page.ActiveTarget.MetadataProvider == "tmdb" && page.ActiveTarget.Episode == 1 &&
            page.Episodes.First().Title == "Episode 1" && scene.Native.Inputs.Count == 1,
            "the real source control browses that site without changing the metadata target or restarting native playback");
        var evidence = await scene.InterceptionEvidence(2);
        var records = scene.Records(evidence);
        CheckInterception(records);
        Check(records.Where(row => FilmJson.Text(row, "layer") == "metadata")
                .Select(row => FilmJson.Text(row, "path")).Contains("/3/tv/321/season/1") &&
            records.Where(row => FilmJson.Text(row, "layer") == "cat" && FilmJson.Text(row, "phase") == "search").Count() == 1 &&
            !scene.Traffic.Commands("play").Any() && !scene.Traffic.Commands("search.more").Any(),
            "the automatic policy uses Node metadata and one Cat search, not a native search or explicit play command");
        CheckPanFile(records, "episode-1-high");
        await scene.Close();
        Check(scene.Native.Stops == 1 && scene.Traffic.Commands("media.started").Count() == 1 &&
            scene.Traffic.Calls.Count(row => row.Method == "film.session.media.release") == 1,
            "browsing presentation retains one native occurrence and releases it once");
        scene.CheckNoMedia(await scene.Evidence());
        Console.WriteLine("OBSERVED film-cat-pan windows metadata; first-episode=1; auto-play; site/pan-distinct; exact-release=1; mediaHead=0; mediaBody=0");
    }

    private static async Task Descriptor(bool direct)
    {
        await using var scene = await Scene.Connect();
        string detail = direct ? "descriptor-direct" : "descriptor";
        var source = FilmJson.Encode(new { key = FilmJson.Text(scene.Source, "key"), detailId = detail });
        await scene.Open(scene.Target with { Id = "site:" + detail, Source = source });
        await scene.Ready(() => scene.Page.HasOutput && scene.Page.CurrentCandidateId.Length > 0 &&
            scene.Native.Inputs.Count == 1 && scene.Native.SettledIdentity == scene.Page.CurrentAttemptId);
        var input = scene.Native.Inputs.Single();
        Check(input.Receipt.Format.GetString() == "dash" && FilmJson.Text(input.Receipt.Drm, "scheme") == "cenc" &&
            FilmJson.Text(FilmJson.Member(input.Receipt.Drm, "clearKeys"), "00000000000000000000000000000001") ==
                "00000000000000000000000000000002",
            "the actual Cat descriptor reaches the native boundary with its real DASH and CENC fields");
        Check(scene.Page.Candidates.Single(row => row.Id == scene.Page.CurrentCandidateId).DetailId.GetString() == detail &&
            !scene.Records(await scene.Evidence()).Any(row => FilmJson.Text(row, "layer") == "pan-service"),
            "a direct Cat descriptor keeps its actual detail identity and cannot be replaced by a pan file");
        CheckReceipt(scene, input.Receipt, FilmCandidateItem.Read(scene.Traffic.Snapshots.Last(snapshot =>
            FilmJson.Text(FilmJson.Member(snapshot, "current"), "attemptId") == input.Receipt.AttemptId)
                .GetProperty("current").GetProperty("candidate")));
        using (var video = FilmVideoInput.Read(input.Receipt.Format, input.Receipt.Drm))
            Check(video.Format == NativeVideoFormat.Dash && FilmVideoInput.ReadClearKeys(input.Receipt.Drm).Count == 1,
                "the production receipt adapter selects DASH and typed clear keys without starting a decoder");
        Check(direct ? input.Receipt.MediaOrigin is null && input.Lease is null :
            input.Receipt.MediaOrigin is not null && input.Lease is not null,
            "the real headerless descriptor uses the direct branch, while headers select a Node local resource");
        await scene.Close();
        Check(scene.Native.Stops == 1 && scene.Traffic.Commands("media.started").Count() == 1 &&
            scene.Traffic.Calls.Count(row => row.Method == "film.session.media.release" &&
                FilmJson.Text(row.Input, "attemptId") == input.Receipt.AttemptId &&
                FilmJson.Text(row.Input, "resourceId") == input.Receipt.ResourceId) == 1,
            "descriptor playback retains the same native occurrence and exact release");
        scene.CheckNoMedia(await scene.Evidence());
        Console.WriteLine($"OBSERVED film-cat-pan windows descriptor; format=dash; drm=cenc; localLease={!direct}; exact-release=1; mediaHead=0; mediaBody=0");
    }

    private static async Task ListFailure()
    {
        await using var scene = await Scene.Connect();
        await scene.Control("scenario", new { scenario = "list-http" });
        await scene.Open(scene.Target);
        await scene.Ready(() => scene.Traffic.Snapshots.Any(snapshot =>
            FilmJson.Text(snapshot, "stage") is "error" or "unavailable"));
        var source = scene.Page.Sources.Single(row => row.MetadataSourceId is null);
        await scene.Ready(() => scene.Page.ListError == "上游服务请求失败" && !scene.Page.ListLoading &&
            scene.Notices.Contains("上游服务请求失败"));
        Check(scene.Page.Episodes.Count == 0 && scene.Page.Candidates.Count == 0 && !scene.Page.CanBrowseRaw &&
            !scene.Page.ShowListMessage && scene.Native.Inputs.Count == 0 &&
            scene.Traffic.Snapshots.Any(snapshot =>
                FilmJson.Text(FilmJson.Member(snapshot, "browsing"), "error") == "UPSTREAM_HTTP") &&
            scene.Element<ComboBox>("SourcePicker").IsEnabled &&
            !Visuals(scene.View).OfType<TextBlock>().Any(row => row.IsVisible && row.Text == scene.Page.ListError),
            "the classified provider error uses the floating notice and exposes no stale playable file");
        await scene.Control("scenario", new { scenario = "success" });
        int refreshes = scene.Traffic.Commands("browse.refresh").Count();
        await scene.Session.ResumePresentationAsync();
        await scene.Ready(() => !scene.Page.ListLoading && scene.Page.ListError.Length == 0 && scene.Page.Episodes.Count == 3);
        Check(scene.Page.SourceId == source.Id && scene.Page.SelectedSource?.Label == "Bridge site" &&
            scene.Page.SelectedLine is { Label: "夸克", Provider: "quark" } && scene.Native.Inputs.Count == 0 &&
            scene.Traffic.Commands("browse.refresh").Count() == refreshes + 1 &&
            FilmJson.Member(scene.Traffic.Commands("browse.refresh").Last().Input, "refresh").ValueKind == JsonValueKind.False &&
            scene.Traffic.Commands("media.started").Count() == 0,
            "presentation resume revalidates the failed directory without forcing refresh or silently restarting media");
        var evidence = await scene.Evidence();
        Check(scene.Records(evidence).Any(row => FilmJson.Text(row, "layer") == "provider" && FilmJson.Text(row, "phase") == "list") &&
            !scene.Records(evidence).Any(row => FilmJson.Text(row, "layer") == "pan-service" &&
                FilmJson.Text(row, "phase") is "prepare" or "play"), "failure and recovery cannot bypass provider list");
        await scene.Close(); scene.CheckNoMedia(await scene.Evidence());
        Console.WriteLine("OBSERVED film-cat-pan windows list-http; notice=UPSTREAM_HTTP; presentation-resume; nativeReceipts=0; mediaHead=0; mediaBody=0");
    }

    private static async Task LateReceipt()
    {
        await using var scene = await Scene.Connect();
        scene.Traffic.HoldReceipt = true;
        await scene.Open(scene.Target);
        var receipt = await scene.Traffic.ReceiptArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string candidate = scene.Page.CurrentCandidateId, title = scene.Page.Title;
        var close = scene.Session.LeaveAsync(retainedOwner: false);
        try
        {
            Check(!close.IsCompleted && !scene.Page.CanPlay && scene.Native.Inputs.Count == 0,
                "close invalidates publication but still owns the admitted HTTP receipt");
            scene.Traffic.ReleaseReceipt.TrySetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(10));
            Check(scene.Native.Inputs.Count == 0 && scene.Native.Commits == 0 && !scene.Session.HasSession &&
                scene.Page.CurrentCandidateId == candidate && scene.Page.Title == title &&
                scene.Traffic.Commands("media.started").Count() == 0 && scene.Traffic.Commands("media.failed").Count() == 0 &&
                scene.Traffic.Calls.Count(row => row.Method == "film.session.media.release" &&
                    FilmJson.Text(row.Input, "attemptId") == receipt.AttemptId &&
                    FilmJson.Text(row.Input, "resourceId") == receipt.ResourceId) == 1 &&
                scene.Traffic.Calls.Count(row => row.Method == "film.session.release") == 1 &&
                scene.Traffic.EventDrains == scene.Traffic.EventStarts,
                "the real late receipt is released once without old-page publication or a fabricated media failure");
            var evidence = await scene.InterceptionEvidence(2);
            CheckInterception(scene.Records(evidence));
            CheckPanFile(scene.Records(evidence), "episode-1-high");
            scene.CheckNoMedia(evidence);
        }
        finally { scene.Traffic.ReleaseReceipt.TrySetResult(); }
        Console.WriteLine("OBSERVED film-cat-pan windows late-receipt; nativeReceipts=0; exact-release=1; SSE-drained; mediaHead=0; mediaBody=0");
    }

    private static void CheckReceipt(Scene scene, FilmMediaReceipt receipt, FilmCandidateItem candidate)
    {
        var record = scene.Traffic.Calls.Single(row => row.Method == "film.session.attempt" &&
            FilmJson.Text(row.Input, "attemptId") == receipt.AttemptId);
        var wire = record.Reply!.Value.GetProperty("value");
        Check(FilmJson.Text(record.Input, "sessionId") == scene.SessionId.ToString() &&
            receipt.ResourceId == FilmJson.Text(wire, "id") && receipt.Address.AbsoluteUri == FilmJson.Text(wire, "url") &&
            (receipt.MediaOrigin?.AbsoluteUri ?? "") == FilmJson.Text(wire, "mediaOrigin") &&
            (wire.TryGetProperty("format", out var format) ? JsonElement.DeepEquals(receipt.Format, format) :
                receipt.Format.ValueKind == JsonValueKind.Undefined) &&
            (wire.TryGetProperty("drm", out var drm) ? JsonElement.DeepEquals(receipt.Drm, drm) :
                receipt.Drm.ValueKind == JsonValueKind.Undefined) &&
            JsonElement.DeepEquals(receipt.Scope, wire.GetProperty("scope")) &&
            !wire.TryGetProperty("headers", out _) &&
            scene.Native.Inputs.Single(row => row.Receipt.AttemptId == receipt.AttemptId).Intent.Identity == receipt.AttemptId,
            "native preparation consumes the exact private receipt of this session and attempt");
        var input = scene.Native.Inputs.Single(row => row.Receipt.AttemptId == receipt.AttemptId);
        Check(FilmJson.Text(receipt.Scope, "sourceKey") == candidate.SourceKey &&
            FilmJson.Text(receipt.Scope, "sourceEngine") == "cat",
            "the actual native input keeps the selected Cat source scope");
        if (receipt.MediaOrigin is { } origin)
            Check(input.Lease is { } lease && lease.Address == receipt.Address && lease.Headers.Count == 0 &&
                receipt.Address.GetLeftPart(UriPartial.Authority) + "/" == origin.AbsoluteUri &&
                origin.GetLeftPart(UriPartial.Authority) != scene.ControlOrigin.GetLeftPart(UriPartial.Authority),
                "Node local resources select the real lease boundary without copying private playback headers");
        else
            Check(input.Lease is null && receipt.Address.GetLeftPart(UriPartial.Authority) ==
                scene.ControlOrigin.GetLeftPart(UriPartial.Authority),
                "a real headerless fixture URL reaches the direct native boundary without creating a local lease");
        if (candidate.Provider == "quark")
            Check(FilmJson.Text(receipt.Scope, "accountProvider") == "quark" &&
                FilmJson.Integer(receipt.Scope, "accountRevision") == 1 &&
                FilmJson.Text(receipt.Scope, "fileProvider") == "quark" &&
                FilmJson.Integer(receipt.Scope, "fileAccountRevision") == 1,
                "the selected provider account and file revisions reach the native receipt");
        var snapshot = scene.Traffic.Snapshots.First(row =>
            FilmJson.Text(FilmJson.Member(row, "attempt"), "id") == receipt.AttemptId);
        Check(FilmJson.Text(FilmJson.Member(FilmJson.Member(snapshot, "attempt"), "candidate"), "id") == candidate.Id &&
            FilmJson.Text(snapshot, "sessionId") == scene.SessionId.ToString(),
            "a real public snapshot supplies the selected candidate associated with the private receipt");
    }

    private static void CheckPanFile(JsonElement[] records, string id)
    {
        var prepared = records.Single(row => FilmJson.Text(row, "layer") == "pan-service" &&
            FilmJson.Text(row, "phase") == "prepare" && FilmJson.Text(row, "fileId") == id);
        var played = records.Single(row => FilmJson.Text(row, "layer") == "pan-service" &&
            FilmJson.Text(row, "phase") == "play" && FilmJson.Text(row, "fileId") == "stored-" + id);
        Check(FilmJson.Text(prepared, "kind") == "share" && FilmJson.Text(prepared, "shareId") == "bridge-share" &&
            FilmJson.Integer(prepared, "accountRevision") == 1 && FilmJson.Text(played, "kind") == "stored" &&
            FilmJson.Integer(played, "accountRevision") == 1 && FilmJson.Text(played, "mode") == "streaming" &&
            FilmJson.Member(prepared, "sequence").GetInt64() < FilmJson.Member(played, "sequence").GetInt64(),
            "the exact selected share file is prepared before its stored file is played");
        var providerPrepare = records.Single(row => FilmJson.Text(row, "layer") == "provider" &&
            FilmJson.Text(row, "phase") == "prepare" && FilmJson.Array(row, "fileIds").Length == 1 &&
            FilmJson.Array(row, "fileIds")[0].GetString() == id);
        var providerPlay = records.Single(row => FilmJson.Text(row, "layer") == "provider" &&
            FilmJson.Text(row, "phase") == "play" && FilmJson.Text(row, "storedFileId") == "stored-" + id);
        Check(FilmJson.Text(providerPrepare, "provider") == "quark" &&
            FilmJson.Text(providerPrepare, "shareId") == FilmJson.Text(prepared, "shareId") &&
            FilmJson.Text(providerPrepare, "destinationId") == "fixture-destination" &&
            FilmJson.Text(providerPlay, "provider") == "quark" &&
            FilmJson.Member(prepared, "sequence").GetInt64() < FilmJson.Member(providerPrepare, "sequence").GetInt64() &&
            FilmJson.Member(providerPrepare, "sequence").GetInt64() < FilmJson.Member(played, "sequence").GetInt64() &&
            FilmJson.Member(played, "sequence").GetInt64() < FilmJson.Member(providerPlay, "sequence").GetInt64(),
            "service identities continue through the real provider transfer and stored-file request in order");
    }

    private static void CheckInterception(JsonElement[] records)
    {
        var requests = records.Where(row => FilmJson.Text(row, "layer") == "pan-interceptor" &&
            FilmJson.Text(row, "phase") == "request").ToArray();
        Check(requests.Select(row => FilmJson.Text(row, "stage")).SequenceEqual(new[] { "token", "list" }) &&
            requests.Select(row => FilmJson.Text(row, "method")).SequenceEqual(new[] { "POST", "GET" }) &&
            requests.All(row => FilmJson.Bool(row, "handled")) &&
            FilmJson.Integer(requests[0], "networkRequestsBefore") == 0 &&
            !records.Any(row => FilmJson.Text(row, "layer") == "pan-interceptor" && FilmJson.Text(row, "phase") == "forward"),
            "the actual Cat interceptor handles exactly token and list, without invoking the original network");
        Check(FilmJson.Text(requests[0], "url") == "https://drive.quark.cn/1/clouddrive/share/sharepage/token" &&
            FilmJson.Text(requests[1], "url").StartsWith(
                "https://drive.quark.cn/1/clouddrive/share/sharepage/detail?pwd_id=bridge-share&stoken=", StringComparison.Ordinal) &&
            FilmJson.Array(requests[0], "knownShares").Length == 0 &&
            FilmJson.Array(requests[1], "knownShares") is [var known] &&
            FilmJson.Text(known, "provider") == "quark" && FilmJson.Text(known, "shareId") == "bridge-share" &&
            FilmJson.Bool(known, "passwordPresent"),
            "token and list retain the captured share while only a redacted token reaches evidence");
        foreach (var request in requests)
        {
            var observations = records.Where(row => FilmJson.Text(row, "layer") == "pan-interceptor" &&
                FilmJson.Integer(row, "observationId") == FilmJson.Integer(request, "observationId")).ToArray();
            Check(observations.Select(row => FilmJson.Text(row, "phase"))
                    .SequenceEqual(new[] { "request", "body", "response", "end", "release" }),
                "each real intercepted request has one complete response and one physical release");
            var body = observations[1];
            var response = observations[2];
            var capture = FilmJson.Array(response, "captures").Single();
            bool token = FilmJson.Text(request, "stage") == "token";
            Check(FilmJson.Text(body, "shareId") == "bridge-share" && !FilmJson.Bool(body, "bodyMalformed") &&
                FilmJson.Bool(body, "passwordFieldPresent") == token && FilmJson.Bool(body, "passwordPresent") == token &&
                FilmJson.Integer(response, "status") == 200 && FilmJson.Text(capture, "provider") == "quark" &&
                FilmJson.Text(capture, "shareId") == "bridge-share" && FilmJson.Bool(capture, "passwordPresent") &&
                FilmJson.Bool(observations[3], "complete") && FilmJson.Bool(observations[3], "readableEnded") &&
                FilmJson.Bool(observations[4], "requestClosed") && FilmJson.Bool(observations[4], "requestDestroyed"),
                "password capture survives an omitted list password and response completion is not faked");
        }
        Check(FilmJson.Integer(records.Last(row => FilmJson.Text(row, "layer") == "pan-interceptor" &&
                FilmJson.Text(row, "phase") == "release"), "networkRequests") == 0,
            "both intercepted native requests are physically drained");
        var share = records.Single(row => FilmJson.Text(row, "layer") == "pan-service" && FilmJson.Text(row, "phase") == "list");
        var providerToken = records.Single(row => FilmJson.Text(row, "layer") == "provider" && FilmJson.Text(row, "phase") == "token");
        Check(FilmJson.Text(share, "provider") == "quark" && FilmJson.Text(share, "shareUrl") == "https://pan.quark.cn/s/bridge-share" &&
            FilmJson.Bool(share, "passwordPresent") && FilmJson.Text(providerToken, "shareId") == "bridge-share" &&
            FilmJson.Bool(providerToken, "passwordPresent"),
            "the intercepted share and passcode reach the product pan service and its actual provider request");
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed class Scene : IAsyncDisposable
    {
        private readonly HttpClient control = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        private readonly CancellationTokenSource owner = new();
        private readonly FilmLibrary library = new(new FilmMemoryStore());
        private readonly RuntimeHttpClient client;
        internal readonly Traffic Traffic;
        internal readonly FilmNativeProbe Native = new();
        internal readonly FilmPlaybackSession Session;
        private readonly FilmCapability module;
        internal readonly Window Window;
        internal readonly FilmPlaybackView View;
        internal readonly Uri ControlOrigin;
        internal readonly JsonElement Source;
        internal readonly FilmPlaybackTarget Target;
        internal readonly FilmPlaybackTarget MetadataTarget;
        internal readonly List<string> Notices = [];
        private int recordCount;
        internal FilmPlaybackPage Page => module.Playback!;
        internal Guid SessionId => Traffic.Snapshots.Length > 0
            ? Guid.Parse(FilmJson.Text(Traffic.Snapshots[0], "sessionId"))
            : throw new InvalidOperationException("No real fixture snapshot");

        private Scene(Uri origin, JsonElement description)
        {
            ControlOrigin = origin; Source = description.GetProperty("source").Clone();
            Target = FilmJson.Target(description.GetProperty("directTarget"));
            MetadataTarget = FilmJson.Target(description.GetProperty("metadataTarget"));
            client = new(FilmJson.RequiredText(description, "baseUrl"));
            Traffic = new(client);
            var connection = new RuntimeConnection(Traffic, new(FilmJson.RequiredText(description, "baseUrl")), owner.Token);
            Session = new(_ => Task.FromResult(connection), Native);
            module = new(new EmptyHome(), Session, library);
            View = (FilmPlaybackView)((FilmView)module.View).FindName("PlaybackView");
            Window = new() { Content = module.View, Width = 1200, Height = 900, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            Window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, args) =>
            { if (args.Message.Length > 0) Notices.Add(args.Message); }));
        }
        internal static async Task<Scene> Connect()
        {
            string address = Environment.GetEnvironmentVariable("MEOWFILM_CAT_PAN_FIXTURE") ??
                throw new InvalidOperationException("Set MEOWFILM_CAT_PAN_FIXTURE to the shared Cat pan fixture controlUrl");
            var origin = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(new Uri(origin, "fixture"));
            response.EnsureSuccessStatusCode();
            var description = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsByteArrayAsync());
            var result = new Scene(origin, description);
            try
            {
                await result.Control("scenario", new { scenario = "success" });
                await result.Rpc("data.invalidate", new { domain = "quark" });
                await result.Rpc("data.invalidate", new { domain = "source" });
                await result.Rpc("library.remove", new { kind = "history" });
                await result.Rpc("library.remove", new { kind = "blocks" });
                result.recordCount = FilmJson.Array(await result.Evidence(), "records").Length;
                result.Window.Show();
                await result.module.NavigateAsync(FilmCapability.DataRoute, default);
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }
        internal async Task Open(FilmPlaybackTarget target)
        {
            var request = new FilmPlaybackRequest(target, Sources: [Source], EntrySurface: "search");
            await module.OpenPlaybackAsync(new(target.Id, target.Title, "", "") { PlaybackContext = request }, request);
            await Ready(() => Page.HasPlaybackSnapshot && View.IsLoaded);
        }
        internal Task Close() => Session.CloseSessionAsync();
        internal T Element<T>(string name) where T : FrameworkElement => (T)View.FindName(name);
        internal async Task Ready(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("Shared Cat pan consumer did not settle; " +
                        (module.Playback is { } page ? page.StatusText + "; " + page.ListStatus : "opening"));
                await Task.Delay(10);
            }
            Window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        private Task<JsonElement> Rpc(string method, object input) => new FilmPlaybackClient(Traffic).CallAsync(method, input);
        internal Task<JsonElement> Evidence() => Control("evidence");
        internal JsonElement[] Records(JsonElement evidence) => FilmJson.Array(evidence, "records").Skip(recordCount).ToArray();
        internal async Task<JsonElement> InterceptionEvidence(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                var evidence = await Evidence();
                var releases = Records(evidence).Where(row => FilmJson.Text(row, "layer") == "pan-interceptor" &&
                    FilmJson.Text(row, "phase") == "release").ToArray();
                if (releases.Length >= count)
                {
                    Check(releases.Length == count && FilmJson.Integer(releases[^1], "networkRequests") == 0,
                        "the fixture records exactly the admitted interceptor releases and no retained request");
                    return evidence;
                }
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("Real Cat interceptor did not drain");
                await Task.Delay(10);
            }
        }
        internal async Task<JsonElement> Control(string path, object? body = null)
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(ControlOrigin, path));
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await control.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsByteArrayAsync());
        }
        internal void CheckNoMedia(JsonElement evidence)
        {
            var counters = evidence.GetProperty("counters");
            Check(FilmJson.Integer(counters, "mediaBody") == 0 && FilmJson.Integer(counters, "mediaHead") == 0 &&
                FilmJson.Integer(counters, "scriptNetwork") == 0,
                "shared fixture must record zero media GET/HEAD and zero original script network fallback in intercepted scenarios");
        }
        public async ValueTask DisposeAsync()
        {
            Traffic.ReleaseReceipt.TrySetResult();
            try { await module.DisposeAsync(); }
            finally
            {
                Window.Content = null; Window.Close();
                try { await Control("scenario", new { scenario = "success" }); }
                finally { await client.DisposeAsync(); owner.Dispose(); control.Dispose(); }
            }
        }
    }

    private sealed class EmptyHome : IFilmHomeSource
    {
        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(FilmSectionDefinition section, int start, int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FilmCardItem>>([]);
        public void Dispose() { }
    }

    // Records only the product HTTP client's results. Never supplies snapshots,
    // candidates, URLs, parser results or replacement transport responses.
    private sealed class Traffic(RuntimeHttpClient client) : IRuntimeClient
    {
        internal sealed class Call(string method, JsonElement input)
        {
            internal readonly string Method = method;
            internal readonly JsonElement Input = input;
            internal JsonElement? Reply;
        }
        private readonly object gate = new();
        private readonly List<Call> calls = [];
        private readonly List<JsonElement> snapshots = [];
        private readonly List<JsonElement> eventSnapshots = [];
        internal Call[] Calls { get { lock (gate) return calls.ToArray(); } }
        internal JsonElement[] Snapshots { get { lock (gate) return snapshots.ToArray(); } }
        internal JsonElement[] EventSnapshots { get { lock (gate) return eventSnapshots.ToArray(); } }
        internal bool HoldReceipt;
        internal readonly TaskCompletionSource<FilmMediaReceipt> ReceiptArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseReceipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int EventStarts, EventDrains;
        internal IEnumerable<Call> Commands(string action) => Calls.Where(row =>
            row.Method == "film.session.command" && FilmJson.Text(row.Input, "action") == action);
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var call = new Call(method, parameters is JsonElement value ? value.Clone() : FilmJson.Encode(parameters ?? new { }));
            lock (gate) calls.Add(call);
            var reply = await client.CallAsync(method, parameters, cancellationToken);
            lock (gate)
            {
                call.Reply = reply.Clone();
                var state = FilmJson.Member(reply, "value");
                if (FilmJson.Text(state, "sessionId").Length > 0 && FilmJson.Member(state, "sequence").ValueKind == JsonValueKind.Number)
                    snapshots.Add(state.Clone());
            }
            if (HoldReceipt && method == "film.session.attempt" && FilmJson.Bool(reply, "ok"))
            {
                ReceiptArrived.TrySetResult(FilmMediaReceipt.Read(reply.GetProperty("value")));
                await ReleaseReceipt.Task; // The already-admitted real receipt must be drained after close.
            }
            return reply;
        }
        public async IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref EventStarts);
            try
            {
                await foreach (var snapshot in client.EventsAsync(module, sessionId, after, cancellationToken))
                {
                    lock (gate) { snapshots.Add(snapshot.Clone()); eventSnapshots.Add(snapshot.Clone()); }
                    yield return snapshot;
                }
            }
            finally { Interlocked.Increment(ref EventDrains); }
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            client.UploadSourceAsync(module, installationId, sourceRevision, source, basename, cancellationToken);
    }
}

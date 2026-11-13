using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmCacheConsumerTests
{
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-cache-consumer: " + name, () => MusicQualityConsumerTests.Sta(run));

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("supplement fills display gaps without changing Douban identity or episode coordinates", SupplementProjection);
        yield return Test("ordinary presentation restore asks owner without forced refresh or another address read", RestorePresentation);
        yield return Test("refresh acknowledgement updates stale display and never schedules its returned pending stage", RefreshDoesNotPlay);
        yield return Test("same-directory refresh retains exact items while loading and reports failure only through session error", RefreshDirectory);
        yield return Test("cold and replacement directory failures never expose a previous identity or its original files", FailedDirectory);
        yield return Test("rejected address retry preserves the exact accepted output", RetryCurrent);
        yield return Test("accepted address retry reacquires the exact candidate and preserves output until replacement", RetryAccepted);
        yield return Test("rejected pending address retry leaves the admitted native preparation alive", RetryPending);
        yield return Test("retry without an attempt delegates the most recent failed candidate to owner", RetryLastFailed);
        yield return Test("browse refresh and picture settings retry route errors through notices", ActionControls);
        yield return Test("failed media exposes picture recovery without covering healthy output", RecoveryControl);
        yield return Test("runtime-relative Douban image identity uses the existing native artwork cache", ImageCache);
    }

    private static Task SupplementProjection()
    {
        var target = new FilmPlaybackTarget("douban:tv:100", "tv", "豆瓣主资料", "douban", "100",
            Season: 1, Episode: 2, GlobalEpisode: 2);
        var card = new FilmCardItem(target.Id, target.Title, "https://fixture.invalid/old.jpg", "9")
        { PlaybackContext = new(target) };
        var page = new FilmPlaybackPage(card, card.PlaybackContext!, null, null);
        var detail = FilmJson.Encode(new
        {
            title = "豆瓣主资料", intro = "", pic = new { normal = "" },
            seasons = new[] { new { season_number = 1, name = "豆瓣第一季",
                episodes = new[] { new { season = 1, episode = 1 }, new { season = 1, episode = 2 } } } }
        });
        var supplement = FilmJson.Encode(new
        {
            provider = "tmdb", metadataId = 900, kind = "tv", metadata = new
            {
                name = "其他标题", overview = "补充简介", poster_path = "/v1/tmdb/images/original/poster.jpg",
                backdrop_path = "/v1/tmdb/images/original/backdrop.jpg",
                images = new { logos = new[] { new { file_path = "/v1/tmdb/images/original/logo.png" } } },
                credits = new { crew = new[] { new { name = "补充导演", job = "Director" } },
                    cast = new[] { new { name = "补充演员" } } },
                similar = new { results = new[] { new { id = 901, name = "相关推荐", poster_path = "" } } },
                seasons = new[] { new { season_number = 99, episode_count = 999 } }, number_of_episodes = 999
            }
        });
        var root = JsonNode.Parse(Snapshot(Guid.NewGuid(), 1, target, detail).GetRawText())!.AsObject();
        root["metadataSupplement"] = JsonNode.Parse(supplement.GetRawText());
        root["metadataCache"] = Cache("fresh");
        page.Apply(new(JsonSerializer.SerializeToElement(root)));
        Check(page.Title == "豆瓣主资料" && page.ActiveTarget == target &&
            page.Overview == "补充简介" && page.Poster == "/v1/tmdb/images/original/poster.jpg" &&
            page.Backdrop == "/v1/tmdb/images/original/backdrop.jpg" && page.Logo == "/v1/tmdb/images/original/logo.png" &&
            page.Directors == "补充导演" && page.Cast == "补充演员" &&
            page.Recommendations.Single().PlaybackContext?.Target.MetadataId == "901",
            "only allowed display gaps use the explicit supplementary TMDB identity");
        Check(page.Seasons.Single().Season == 1 && page.Episodes.Count == 2 && page.SelectedEpisode?.Number == 2 &&
            page.EpisodeCountLabel == "共2集" && page.CaptureRequest.Target == target &&
            page.CaptureRequest.Metadata?.GetProperty("seasons")[0].GetProperty("season_number").GetInt32() == 1,
            "supplementary seasons and counts never reach selection, display coordinates or playback handoff");
        root["metadataSupplement"]!["metadataId"] = "900";
        page.Apply(new(JsonSerializer.SerializeToElement(root)));
        Check(page.Overview == "补充简介" && page.ActiveTarget == target,
            "both numeric and text supplementary metadata identities preserve the exact main identity");

        root["metadata"]!["intro"] = "主简介";
        root["metadata"]!["pic"]!["normal"] = "/v1/douban/images/main.jpg";
        root["metadata"]!["backdrop_path"] = "/v1/douban/images/backdrop.jpg";
        root["metadata"]!["logo_path"] = "/v1/douban/images/logo.png";
        root["metadata"]!["directors"] = JsonNode.Parse("[{\"name\":\"主导演\"}]");
        root["metadata"]!["actors"] = JsonNode.Parse("[{\"name\":\"主演员\"}]");
        root["metadata"]!["similar"] = JsonNode.Parse("""{"results":[{"id":"101","title":"豆瓣推荐","pic":{"normal":"/v1/douban/images/similar.jpg"}}]}""");
        page.Apply(new(JsonSerializer.SerializeToElement(root)));
        Check(page.Overview == "主简介" && page.Poster == "/v1/douban/images/main.jpg" &&
            page.Backdrop == "/v1/douban/images/backdrop.jpg" && page.Logo == "/v1/douban/images/logo.png" &&
            page.Directors == "主导演" && page.Cast == "主演员" &&
            page.Recommendations.Single().PlaybackContext?.Target is
                { MetadataProvider: "douban", MetadataId: "101", Kind: "tv" },
            "main successful fields remain authoritative");

        root["metadataSupplement"] = null;
        root["metadata"] = JsonNode.Parse("""{"title":"豆瓣主资料","intro":"","pic":{"normal":""},"rating":{"value":0},"seasons":[]}""");
        root["episodes"] = new JsonArray();
        page.Apply(new(JsonSerializer.SerializeToElement(root)));
        Check(page.Poster == "" && page.Backdrop == "" && page.Logo == "" && page.Score == "" &&
            page.Overview == "" && page.Directors == "" && page.Cast == "" && page.Recommendations.Count == 0 &&
            page.Episodes.Count == 0 && page.ActiveTarget == target,
            "a complete empty response clears the old baseline and supplementary display without changing identity");
        root["metadataCache"] = Cache("stale", "UPSTREAM_ERROR", "failed");
        page.Apply(new(JsonSerializer.SerializeToElement(root)));
        Check(page.Poster == "" && page.Score == "",
            "a later failure retains an authoritative empty success instead of resurrecting the original card");
        root["metadataCache"]!["components"]!["detail"]!.AsObject().Remove("fetchedAt");
        page.Apply(new(JsonSerializer.SerializeToElement(root)));
        Check(page.Poster == card.Poster && page.Score == card.Score && page.MetadataCache?.Status == "stale",
            "an unfulfilled detail component may still use the matching original card");
        return Task.CompletedTask;
    }

    private static async Task RestorePresentation()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        await Play(rig);
        int before = rig.Runtime.Calls.Count;
        await rig.Session.ResumePresentationAsync();
        var calls = rig.Runtime.Calls.Skip(before).Where(call => call.Method != "film.session.command" ||
            FilmJson.Text(call.Parameters, "action") != "media.progress").ToArray();
        Check(calls.Length == 1 && calls[0].Method == "film.session.command" &&
            FilmJson.Text(calls[0].Parameters, "action") == "browse.refresh" &&
            FilmJson.Member(calls[0].Parameters, "refresh").ValueKind == JsonValueKind.False &&
            rig.Native.Commits == 1 && rig.Native.Stops == 0 && rig.Session.HasOutput,
            "a live event subscription does not let a display snapshot block owner freshness checks");
    }

    private static async Task RefreshDoesNotPlay()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        await Play(rig);
        var notices = new List<string>();
        rig.Session.OperationFailed += (_, code) => notices.Add(code);
        var response = JsonNode.Parse(rig.Runtime.Value.GetRawText())!.AsObject();
        response["stage"] = "pending";
        response["attempt"] = JsonNode.Parse(FilmJson.Encode(new
        { id = "display-only", candidate = Candidate("display-only") }).GetRawText());
        response["metadataCache"] = Cache("stale", "UPSTREAM_ERROR");
        response["metadataError"] = "UPSTREAM_ERROR";
        response["metadata"]!["overview"] = "旧成功简介";
        rig.Runtime.Intercept = (method, input, _) => Task.FromResult(
            method == "film.session.command" && FilmJson.Text(input, "action") == "browse.refresh"
                ? FilmRuntimeProbe.Ok(JsonSerializer.SerializeToElement(response))
                : rig.Runtime.Handle(method, input));
        int before = rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt");
        await rig.Page.RefreshBrowsingAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var refresh = rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "browse.refresh");
        Check(FilmJson.Member(refresh.Parameters, "refresh").ValueKind == JsonValueKind.Undefined &&
            rig.Page.Overview == "旧成功简介" && rig.Page.MetadataCache?.Status == "stale" &&
            notices.SequenceEqual(["UPSTREAM_ERROR"]) && rig.Native.Commits == 1 && rig.Native.Stops == 0 &&
            rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt") == before && rig.Session.HasOutput,
            "refresh keeps the accepted donor, reports its cache failure and never consumes a pending stage from its acknowledgement");
    }

    private static async Task RetryCurrent()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        await Play(rig);
        rig.Runtime.Intercept = (method, input, _) => Task.FromResult(
            method == "film.session.command" && FilmJson.Text(input, "action") == "media.retry"
                ? FilmRuntimeProbe.Failure("UPSTREAM_ERROR") : rig.Runtime.Handle(method, input));
        await Error("UPSTREAM_ERROR", rig.Page.RetryMediaAsync);
        var retry = rig.Runtime.Calls.Single(call => FilmJson.Text(call.Parameters, "action") == "media.retry");
        Check(FilmJson.Text(retry.Parameters, "attemptId") == "cache-current" && rig.Native.Commits == 1 &&
            rig.Native.Stops == 0 && rig.Session.HasOutput && !rig.Runtime.Released.Contains("cache-current"),
            "retry describes the exact accepted occurrence and a rejection leaves it usable");
    }

    private static async Task RefreshDirectory()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        await Play(rig);
        rig.Runtime.Change(root =>
        {
            root["sources"] = JsonNode.Parse("""[{"id":"site-choice","title":"准确来源"}]""");
            root["browsing"] = DirectoryBrowsing("site-choice", content: true);
        });
        await Until(() => rig.Page.SourceId == "site-choice" && rig.Page.Episodes.Count == 1);
        var directory = new FilmDirectoryDialog(rig.Page);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notices = new List<string>();
        rig.Session.OperationFailed += (_, code) => notices.Add(code);
        rig.Runtime.Intercept = async (method, input, token) =>
        {
            if (method != "film.session.command" || FilmJson.Text(input, "action") != "browse.refresh")
                return rig.Runtime.Handle(method, input);
            rig.Runtime.Change(root =>
            {
                root["browsing"]!["loading"] = true;
                root["browsing"]!["refreshing"] = true;
            });
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            rig.Runtime.Change(root =>
            {
                root["browsing"]!["loading"] = false;
                root["browsing"]!["refreshing"] = false;
                root["error"] = "UPSTREAM_ERROR";
            });
            return FilmRuntimeProbe.Ok(rig.Runtime.Value);
        };
        int reads = rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt");
        try
        {
            var refresh = rig.Page.RefreshBrowsingAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => rig.Page.ListLoading);
            Check(rig.Page.Episodes.Single().CandidateId == "cache-current" &&
                rig.Page.Candidates.Single().Id == "cache-current" && rig.Page.CanBrowseRaw &&
                directory.IsCurrent(rig.Page) && rig.Page.ListError == "" && rig.Page.ListStatus == "加载中…",
                "the owner's refreshing flag retains the exact current source and directory while its request is pending");
            finish.TrySetResult();
            await refresh;
            await Until(() => !rig.Page.ListLoading && notices.Count > 0);
            Check(notices.SequenceEqual(["UPSTREAM_ERROR"]) && rig.Page.ListError == "" && !rig.Page.ShowListStatus &&
                rig.Page.Episodes.Single().CandidateId == "cache-current" && rig.Page.Candidates.Single().Id == "cache-current" &&
                rig.Page.CanBrowseRaw && directory.IsCurrent(rig.Page) && rig.Page.CurrentAttemptId == "cache-current" &&
                rig.Native.Commits == 1 && rig.Native.Stops == 0 && rig.Session.HasOutput &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt") == reads,
                "session.error reports a failed same-identity refresh without clearing its exact list, closing the directory or touching media");
            rig.Runtime.Change(root => root["browsing"]!["originalEpisodes"]![0]!["path"] = "/准确目录/更新.mp4");
            await Until(() => FilmJson.Text(FilmJson.Array(rig.Page.RawBrowsing, "originalEpisodes").Single(), "path") == "/准确目录/更新.mp4");
            Check(!directory.IsCurrent(rig.Page), "a genuinely changed original file list retires the old directory even under the same source and line");
        }
        finally { finish.TrySetResult(); directory.Close(); }
    }

    private static async Task FailedDirectory()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        rig.Runtime.Change(root =>
        {
            root["sources"] = JsonNode.Parse("""[{"id":"cold","title":"冷目录"},{"id":"other","title":"其他来源"}]""");
            root["browsing"] = DirectoryBrowsing("cold", content: false, error: "TIMEOUT");
            root["candidates"] = new JsonArray();
        });
        await Until(() => rig.Page.SourceId == "cold" && rig.Page.ListError.Length > 0);
        Check(rig.Page.Episodes.Count == 0 && rig.Page.Candidates.Count == 0 && !rig.Page.CanBrowseRaw &&
            rig.Page.ListStatus == "请求超时，请重试" && rig.Page.Sources.Any(source => source.Id == "other"),
            "a cold directory failure has no fabricated cached items and leaves source recovery available");
        rig.Runtime.Change(root =>
        {
            root["browsing"] = DirectoryBrowsing("cold", content: true);
            root["candidates"] = JsonNode.Parse(FilmJson.Encode(new[] { Candidate("cache-current") }).GetRawText());
        });
        await Until(() => rig.Page.CanBrowseRaw && rig.Page.Episodes.Count == 1);
        var directory = new FilmDirectoryDialog(rig.Page);
        try
        {
            rig.Runtime.Change(root => root["browsing"]!["error"] = "TIMEOUT");
            await Until(() => rig.Page.ListError.Length > 0);
            Check(rig.Page.Episodes.Count == 0 && rig.Page.Candidates.Count == 0 &&
                !rig.Page.CanBrowseRaw && !directory.IsCurrent(rig.Page),
                "browsing.error invalidates ordinary list and directory consumers even when a response still carries old rows");
            rig.Runtime.Change(root => root["browsing"]!["error"] = null);
            await Until(() => rig.Page.CanBrowseRaw && rig.Page.Episodes.Count == 1);
            rig.Runtime.Change(root =>
            {
                root["browsing"] = DirectoryBrowsing("other", content: false);
                root["browsing"]!["loading"] = true;
            });
            await Until(() => rig.Page.SourceId == "other" && rig.Page.ListLoading);
            Check(rig.Page.Episodes.Count == 0 && rig.Page.Candidates.Count == 0 &&
                !rig.Page.CanBrowseRaw && !directory.IsCurrent(rig.Page),
                "ordinary replacement loading cannot retain the previous source's candidates or directory");
            rig.Runtime.Change(root =>
            {
                root["browsing"]!["loading"] = false;
                root["browsing"]!["error"] = "UPSTREAM_FORMAT";
            });
            await Until(() => rig.Page.ListError.Length > 0);
            Check(rig.Page.Episodes.Count == 0 && rig.Page.Candidates.Count == 0 &&
                !rig.Page.CanBrowseRaw && !directory.IsCurrent(rig.Page) && rig.Page.ListStatus == "上游数据格式错误",
                "a replacement source failure displays only its classified error, never the former identity's items");
        }
        finally { directory.Close(); }
    }

    private static JsonNode DirectoryBrowsing(string sourceId, bool content, string? error = null) => JsonNode.Parse(
        FilmJson.Encode(new
        {
            sourceId, lineId = 0, season = 1, loading = false, error,
            lines = new[] { new { id = 0, label = "准确线路" } },
            episodes = content ? new[] { new { id = "file-current", candidateId = "cache-current", name = "准确文件",
                path = "/准确目录/文件.mp4", season = 1, episode = 1 } } : [],
            originalEpisodes = content ? new[] { new { id = "file-current", candidateId = "cache-current", name = "准确文件",
                path = "/准确目录/文件.mp4", season = 1, episode = 1 } } : []
        }).GetRawText())!;

    private static async Task RetryPending()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Native.Preparing = async token => { entered.TrySetResult(); await finish.Task.WaitAsync(token); };
        try
        {
            rig.Runtime.Attempt("cache-pending", Candidate("cache-pending"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            rig.Runtime.Intercept = (method, input, _) => Task.FromResult(
                method == "film.session.command" && FilmJson.Text(input, "action") == "media.retry"
                    ? FilmRuntimeProbe.Failure("UPSTREAM_ERROR") : rig.Runtime.Handle(method, input));
            await Error("UPSTREAM_ERROR", rig.Page.RetryMediaAsync);
            var retry = rig.Runtime.Calls.Single(call => FilmJson.Text(call.Parameters, "action") == "media.retry");
            Check(FilmJson.Text(retry.Parameters, "attemptId") == "cache-pending" &&
                !rig.Runtime.Released.Contains("cache-pending"), "rejected retry cannot cancel the admitted pending receipt");
            finish.TrySetResult();
            await Until(() => rig.Native.SettledIdentity == "cache-pending" && rig.Session.HasOutput);
            Check(rig.Native.Commits == 1 && rig.Native.Stops == 0, "the original pending input still completes normally");
        }
        finally { finish.TrySetResult(); }
    }

    private static async Task RetryAccepted()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        await Play(rig);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Native.Preparing = async token => { entered.TrySetResult(); await finish.Task.WaitAsync(token); };
        rig.Runtime.Intercept = (method, input, _) =>
        {
            if (method == "film.session.command" && FilmJson.Text(input, "action") == "media.retry")
            {
                Check(FilmJson.Text(input, "attemptId") == "cache-current", "retry names the current exact occurrence");
                rig.Runtime.Attempt("cache-retry", Candidate("cache-current"));
                return Task.FromResult(FilmRuntimeProbe.Ok(rig.Runtime.Value));
            }
            return Task.FromResult(rig.Runtime.Handle(method, input));
        };
        try
        {
            await rig.Page.RetryMediaAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(rig.Session.HasOutput && rig.Native.Commits == 1 && rig.Native.Stops == 0 &&
                !rig.Runtime.Released.Contains("cache-current"), "the accepted donor remains live during address preparation");
            finish.TrySetResult();
            await Until(() => rig.Page.CurrentAttemptId == "cache-retry" && rig.Native.SettledIdentity == "cache-retry");
            await Until(() => rig.Runtime.Released.Contains("cache-current"));
            Check(rig.Native.Commits == 2 && rig.Session.HasOutput &&
                rig.Page.CurrentCandidateId == "cache-current" &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "cache-retry") == 1 &&
                !rig.Runtime.Calls.Any(call => FilmJson.Text(call.Parameters, "action") is "switch" or "search.more"),
                "a fresh receipt replaces only the requested candidate without rematching or cached native address reuse");
        }
        finally { finish.TrySetResult(); }
    }

    private static async Task RetryLastFailed()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        await rig.Page.RetryMediaAsync();
        var call = rig.Runtime.Calls.Single(call => FilmJson.Text(call.Parameters, "action") == "media.retry");
        Check(FilmJson.Member(call.Parameters, "attemptId").ValueKind == JsonValueKind.Undefined &&
            !rig.Runtime.Calls.Any(item => FilmJson.Text(item.Parameters, "action") is "play" or "switch" or "search.more"),
            "owner selects the last exact failed candidate without client fallback matching");
    }

    private static async Task ActionControls()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        await Play(rig);
        var view = new FilmPlaybackView { DataContext = rig.Page };
        var window = new Window { Content = view, Width = 1150, Height = 850, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        var notices = new List<string>();
        window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, args) =>
        { if (args.Message.Length > 0) notices.Add(args.Message); }));
        try
        {
            window.Show(); window.UpdateLayout();
            rig.Runtime.Intercept = (method, input, _) => Task.FromResult(
                method == "film.session.command" && FilmJson.Text(input, "action") is "browse.refresh" or "media.retry"
                    ? FilmRuntimeProbe.Failure("UPSTREAM_ERROR") : rig.Runtime.Handle(method, input));
            var refresh = (Button)view.FindName("RefreshBrowsingButton");
            var retry = (Button)view.FindName("RetryMediaButton");
            var settings = (Border)view.FindName("SettingsPanel");
            Check(refresh.IsEnabled && retry.IsEnabled && !settings.IsVisible,
                "browse refresh remains available while the media retry action belongs to the closed picture settings");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => notices.Count == 1);
            ((NoticeOutlet)view.FindName("Notices")).Message = "";
            ((Button)view.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => settings.IsVisible && retry.IsVisible);
            Check(retry.IsDescendantOf(settings) &&
                !((Button)view.FindName("RetryMediaRecoveryButton")).IsVisible,
                "manual address retry is available inside the picture without covering healthy video");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => notices.Count == 2);
            Check(notices.All(message => message == "上游服务请求失败") &&
                rig.Runtime.Calls.Count(call => FilmJson.Text(call.Parameters, "action") == "browse.refresh") == 1 &&
                rig.Runtime.Calls.Count(call => FilmJson.Text(call.Parameters, "action") == "media.retry") == 1 &&
                rig.Native.Commits == 1 && rig.Native.Stops == 0 && rig.Session.HasOutput,
                "actual button input reports rejection through the existing notice outlet and preserves playback");
        }
        finally { view.DataContext = null; window.Content = null; window.Close(); }
    }

    private static async Task RecoveryControl()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        rig.Runtime.Change(root => root["stage"] = "searching");
        await Until(() => !rig.Page.CanRetryMedia);
        var view = new FilmPlaybackView { DataContext = rig.Page };
        var window = new Window { Content = view, Width = 1150, Height = 850, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show(); window.UpdateLayout();
            var retry = (Button)view.FindName("RetryMediaRecoveryButton");
            Check(!retry.IsVisible && retry.IsDescendantOf((Grid)view.FindName("PlayerOverlay")),
                "picture recovery starts hidden when there is no failed or pending media occurrence");
            rig.Runtime.Change(root => root["stage"] = "error");
            await Until(() => retry.IsVisible);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => rig.Runtime.Calls.Any(call => FilmJson.Text(call.Parameters, "action") == "media.retry"));
            var call = rig.Runtime.Calls.Single(call => FilmJson.Text(call.Parameters, "action") == "media.retry");
            Check(FilmJson.Member(call.Parameters, "attemptId").ValueKind == JsonValueKind.Undefined &&
                !rig.Runtime.Calls.Any(item => FilmJson.Text(item.Parameters, "action") is "play" or "switch" or "search.more"),
                "the visible recovery action delegates the exact last failed candidate without rematching");
            await Play(rig);
            await Until(() => !retry.IsVisible);
            Check(rig.Session.HasOutput && rig.Native.Commits == 1,
                "healthy output removes the central recovery action without replacing or pausing media");
        }
        finally { view.DataContext = null; window.Content = null; window.Close(); }
    }

    private static async Task ImageCache()
    {
        int reads = 0;
        const string address = "/v1/douban/images/view/photo/poster.jpg";
        await using var artwork = new ArtworkService(readTrusted: (reference, budget, token) =>
        {
            token.ThrowIfCancellationRequested();
            Check(reference == address && budget == ArtworkService.MaximumBytes, "the native image transport receives the exact runtime reference");
            reads++;
            return Task.FromResult(RuntimeImageTests.Png.ToArray());
        });
        using var first = await artwork.AcquireAsync(address);
        using var second = await artwork.AcquireAsync(address);
        Check(reads == 1 && ReferenceEquals(first.Image, second.Image),
            "business cache adoption preserves the existing native decoded-image cache");
    }

    private static JsonNode Cache(string status, string? error = null, string detail = "ready") => JsonNode.Parse(
        FilmJson.Encode(new { status, error = error is null ? null : new { code = error },
            components = new { detail = new { status = detail, fetchedAt = 1000 } } }).GetRawText())!;

    private static async Task Play(FilmPlaybackRig rig)
    {
        rig.Runtime.Attempt("cache-current", Candidate("cache-current"));
        await Until(() => rig.Native.SettledIdentity == "cache-current" && rig.Page.CurrentAttemptId == "cache-current" && rig.Session.HasOutput);
    }
}

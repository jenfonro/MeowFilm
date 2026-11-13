using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmPlayerModelTests
{
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-player-model: " + name, () => MusicQualityConsumerTests.Sta(run));

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("TMDB and Douban movies and series expose fixed menus without requests", async () =>
        {
            foreach (string provider in new[] { "tmdb", "douban" })
                foreach (string kind in new[] { "movie", "tv" })
                {
                    await using var rig = new FilmPlaybackRig();
                    var card = Card(kind);
                    var request = card.PlaybackContext!;
                    var target = request.Target with { Id = $"{provider}:{kind}:100", MetadataProvider = provider };
                    await rig.OpenAsync(card, request with { Target = target });
                    int before = rig.Runtime.Calls.Count;
                    var page = rig.Page;
                    Check(!page.ShowPlayerSwitchActions && page.PlayerPanOptions.All(option => !option.IsSelected) &&
                        page.PlayerQualityOptions.All(option => !option.IsSelected), "menus do not manufacture a current candidate");
                    Apply(page, rig.Runtime.Value, candidate: Candidate("current", episode: kind == "movie" ? null : 1, quality: "4k_hdr"));
                    Check(page.PlayerPanOptions.Select(option => option.Value)
                            .SequenceEqual(new[] { "yidong", "tianyi", "quark", "uc", "baidu" }) &&
                        page.PlayerQualityOptions.Select(option => option.Value)
                            .SequenceEqual(new[] { "4k_hdr", "4k_fps", "4k", "1080p", "720p" }) &&
                        page.PlayerPanOptions.Single(option => option.IsSelected).Value == "quark" &&
                        page.PlayerQualityOptions.Single(option => option.IsSelected).Value == "4k_hdr" &&
                        page.ShowPlayerSwitchActions, "both metadata providers include movies without an episode-number gate");
                    var browsing = FilmJson.Encode(new
                    {
                        sourceId = "temporary-site", lineId = 0, loading = false,
                        lines = new[] { new { id = 0, label = "浏览线路", provider = "baidu" } }, episodes = Array.Empty<object>()
                    });
                    Apply(page, rig.Runtime.Value, candidate: Candidate("current", episode: kind == "movie" ? null : 1, quality: "4k_hdr"),
                        browsing: browsing);
                    Check(page.SourceId == "temporary-site" &&
                        page.PlayerPanOptions.Select(option => option.Value)
                            .SequenceEqual(new[] { "yidong", "tianyi", "quark", "uc", "baidu" }) &&
                        page.PlayerQualityOptions.Select(option => option.Value)
                            .SequenceEqual(new[] { "4k_hdr", "4k_fps", "4k", "1080p", "720p" }) &&
                        page.PlayerPanOptions.Single(option => option.IsSelected).Value == "quark" &&
                        page.PlayerQualityLabel == "4K·HDR" && page.ShowPlayerSwitchActions,
                        "temporary site browsing does not replace the active metadata playback context");
                    Check(rig.Runtime.Calls.Count == before, "reading menus and labels never calls the runtime");
                }
        });
        yield return Test("labels use the current candidate and preserve real unknown values", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            var page = rig.Page;
            foreach (var pan in new[]
            {
                ("yidong", "移动云盘"), ("tianyi", "天翼云盘"), ("quark", "夸克"), ("quark_tv", "夸克"),
                ("uc", "UC"), ("uc_tv", "UC"), ("baidu", "百度网盘")
            })
            {
                var candidate = Patch(Candidate("current"), root => root["provider"] = pan.Item1);
                Apply(page, rig.Runtime.Value, candidate: candidate);
                Check(page.PlayerPanLabel == pan.Item2 &&
                    page.PlayerPanOptions.Single(option => option.IsSelected).Label == pan.Item2, "standard provider families share their label and selection");
            }
            foreach (var quality in new[]
            {
                ("4k_hdr", "4K·HDR"), ("4k_fps", "4K·60帧"), ("4k", "4K"), ("1080p", "1080P"), ("720p", "720P")
            })
            {
                Apply(page, rig.Runtime.Value, candidate: Candidate("current", quality: quality.Item1));
                Check(page.PlayerQualityLabel == quality.Item2 &&
                    page.PlayerQualityOptions.Single(option => option.IsSelected).Label == quality.Item2, "standard quality labels match the selected value");
            }
            var unknown = Patch(Candidate("current", quality: "8K REMUX"), root =>
            { root["provider"] = "custom"; root["lineLabel"] = "夸克自定义线路"; });
            var pending = FilmJson.Encode(new { id = "next", candidate = Candidate("next", quality: "720p"), prepareOnly = true });
            Apply(page, rig.Runtime.Value, candidate: unknown, attempt: pending);
            Check(page.PlayerPanLabel == "夸克自定义线路" && page.PlayerQualityLabel == "8K REMUX" &&
                page.PlayerPanOptions.All(option => !option.IsSelected) && page.PlayerQualityOptions.All(option => !option.IsSelected),
                "a pending replacement and familiar line text cannot reinterpret the actual current candidate");
            Apply(page, rig.Runtime.Value, candidate: Patch(unknown, root =>
            { root.Remove("lineLabel"); root.Remove("quality"); }));
            Check(page.PlayerPanLabel == "未知" && page.PlayerQualityLabel == "未知", "missing labels remain unknown");
            Apply(page, rig.Runtime.Value, attempt: pending);
            Check(page.PlayerPanLabel == "夸克" && page.PlayerQualityLabel == "720P" && !page.ShowPlayerSwitchActions,
                "the pending candidate can label initial preparation without becoming a current occurrence");
        });
        yield return Test("ordinary menus contain only observed standard line providers and current quality", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            var browsing = FilmJson.Encode(new
            {
                sourceId = "result", lineId = 0, loading = false,
                lines = new object[]
                {
                    new { id = 0, label = "定制线路", provider = "quark_tv" },
                    new { id = "other", label = "第二线路", provider = "baidu" },
                    new { id = "duplicate", label = "重复线路", provider = "quark" },
                    new { id = "custom", label = "移动云盘 UC", provider = "custom" }
                },
                episodes = Array.Empty<object>()
            });
            var source = new FilmPlaybackTarget("source:1", "source", "普通来源",
                Source: FilmJson.Encode(new { key = "cat:fixture:site:1", detailId = 0 }));
            var ordinary = Patch(rig.Runtime.Value, root => root["target"] = JsonNode.Parse(FilmJson.Encode(source.Wire()).GetRawText()));
            int before = rig.Runtime.Calls.Count;
            var page = rig.Page;
            Apply(page, ordinary, candidate: Candidate("current", quality: "4k_fps"), browsing: browsing);
            Check(page.PlayerPanOptions.Select(option => option.Value).SequenceEqual(new[] { "quark", "baidu" }) &&
                page.PlayerPanOptions.Single(option => option.IsSelected).Value == "quark" &&
                page.PlayerQualityOptions.SequenceEqual(new[] { new FilmPlayerOption("4k_fps", "4K·60帧", true) }) &&
                !page.ShowPlayerSwitchActions, "an ordinary source deduplicates only observed provider families");
            Apply(page, ordinary, candidate: Candidate("current", quality: "8K"), browsing: Patch(browsing, root =>
                root["lines"] = JsonNode.Parse("""[{"id":0,"label":"夸克 / 百度网盘","provider":"custom"}]""")));
            Check(page.PlayerPanOptions.Count == 0 && page.PlayerQualityOptions.Count == 0 &&
                page.PlayerQualityLabel == "8K", "unknown values stay visible without creating unsupported actionable menu values");
            Apply(page, ordinary, candidate: Candidate("current"), browsing: browsing);
            Check(page.PlayerPanOptions.Count == 2 && page.PlayerQualityOptions.Single().Value == "1080p" &&
                !page.ShowPlayerSwitchActions && rig.Runtime.Calls.Count == before, "plain source targets use the same observed menus without any RPC");
            source = source with { MetadataProvider = "tmdb", MetadataId = "100" };
            Apply(page, Patch(rig.Runtime.Value, root => root["target"] = JsonNode.Parse(FilmJson.Encode(source.Wire()).GetRawText())),
                candidate: Candidate("current"));
            Check(page.PlayerPanOptions.Count == 0 && page.PlayerQualityOptions.Single().Value == "1080p" &&
                !page.ShowPlayerSwitchActions, "an associated source target remains ordinary even before browsing any lines");
            var group = new FilmPlaybackTarget("group:1", "group", "聚合来源",
                Members: FilmJson.Encode(new[] { new { sourceKey = "cat:fixture:site:1", detailId = 0 } }));
            var associated = Patch(rig.Runtime.Value, root =>
            {
                root["target"] = JsonNode.Parse(FilmJson.Encode(group.Wire()).GetRawText());
                root["metadataSources"] = JsonNode.Parse("""[{"id":"tmdb:tv:100","provider":"tmdb"}]""");
                root["metadataSourceId"] = "tmdb:tv:100";
            });
            Apply(page, associated, candidate: Candidate("current"), browsing: browsing);
            Check(page.PlayerPanOptions.Count == 2 && page.PlayerQualityOptions.Single().Value == "1080p" &&
                !page.ShowPlayerSwitchActions, "metadata associated with a group does not change its ordinary playback context");
        });
        yield return Test("stages describe real preparation milestones and classified failures without output", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            var page = rig.Page;
            var card = Card();
            var opening = new FilmPlaybackPage(card, card.PlaybackContext!, null, null);
            Check(opening.PlayerBusy && !opening.PlayerBuffering && opening.PlayerStatusVisible && opening.PlayerStatusText == "加载中..." &&
                opening.PlayerStageProgress == 0, "an unopened page starts at loading without a fabricated percentage");
            foreach (var state in new (string Stage, bool Busy, string Text, double Progress)[]
            {
                ("loading", true, "加载中...", 0d),
                ("cached", true, "正在搜索片源...", 1d / 6), ("history", true, "正在搜索片源...", 1d / 6),
                ("manual", true, "正在搜索片源...", 1d / 6), ("searching", true, "正在搜索片源...", 1d / 6),
                ("resolving", true, "正在获取播放地址...", 1d / 2), ("prepared", false, "已准备", 2d / 3),
                ("pending", true, "正在准备媒体...", 5d / 6), ("idle", false, "待播放", 0d),
                ("unavailable", false, "暂无可用片源", 0d), ("ended", false, "已结束", 1d), ("stopped", false, "已停止", 0d)
            })
            {
                Apply(page, rig.Runtime.Value, stage: state.Stage);
                Check(page.PlayerBusy == state.Busy && !page.PlayerBuffering && page.PlayerStatusVisible && page.PlayerStatusText == state.Text &&
                    page.PlayerStageProgress == state.Progress, "stage " + state.Stage + " must report only its actual preparation milestone");
            }
            foreach (string stage in new[] { "error", "unavailable" })
            {
                Apply(page, Patch(rig.Runtime.Value, root => root["error"] = "AUTH_REQUIRED"), stage: stage);
                Check(!page.PlayerBusy && page.PlayerStatusVisible && page.PlayerStatusText == "请先登录账号",
                    "failed preparation preserves the existing error classification");
            }
            Apply(page, rig.Runtime.Value, stage: "searching");
            page.SetConnectionError("NETWORK_ERROR");
            Check(!page.PlayerBusy && page.PlayerStatusVisible && page.PlayerStatusText == "网络连接失败，请重试" &&
                page.PlayerStageProgress == 0, "a failed connection cannot keep advertising stale matching work");
        });
        yield return Test("matching remains busy while a real donor output keeps playing", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Attempt("donor", Candidate("donor"));
            await Until(() => rig.Session.HasOutput && rig.Page.CurrentAttemptId == "donor");
            var playing = rig.Runtime.Value;
            var page = rig.Page;
            foreach (string stage in new[] { "loading", "cached", "history", "manual", "searching", "resolving", "pending" })
            {
                page.Apply(new(Patch(playing, root => root["stage"] = stage)));
                Check(page.HasOutput && page.PlayerBusy && page.PlayerStatusVisible && page.PlayerStatusText == "正在搜索片源...",
                    "an accepted donor does not conceal replacement stage " + stage);
            }
            page.Apply(new(playing));
            Check(page.HasOutput && !page.PlayerBusy && !page.PlayerStatusVisible &&
                page.PlayerStatusText == "播放中" && page.PlayerStageProgress == 1, "only settled playback clears matching status");
            page.Apply(new(Patch(playing, root => root["browsing"] = JsonNode.Parse(
                """{"sourceId":"temporary-site","lineId":0,"loading":false,"lines":[{"id":0,"label":"浏览线路","provider":"baidu"}],"episodes":[]}"""))));
            Check(page.HasOutput && page.IsPlaying && page.PlayerPanOptions.Count == 5 && page.PlayerQualityOptions.Count == 5 &&
                page.PlayerPanLabel == "夸克" && page.ShowPlayerSwitchActions,
                "browsing a different site preserves the real metadata output's pan and switch controls");
            await rig.Session.SetPausedAsync(true);
            Check(page.HasOutput && !page.IsPlaying && !page.PlayerBuffering, "paused output does not advertise playback buffering");
        });
    }

    private static JsonElement Patch(JsonElement value, Action<JsonObject> change)
    {
        var root = JsonNode.Parse(value.GetRawText())!.AsObject();
        change(root);
        return JsonSerializer.SerializeToElement(root);
    }

    private static void Apply(FilmPlaybackPage page, JsonElement snapshot, JsonElement? candidate = null,
        JsonElement? attempt = null, JsonElement? browsing = null, string stage = "prepared") =>
        page.Apply(new(Patch(snapshot, root =>
        {
            root["stage"] = stage;
            root["current"] = candidate is { } current ? JsonNode.Parse(FilmJson.Encode(new { attemptId = "current", candidate = current }).GetRawText()) : null;
            root["attempt"] = attempt is { } pending ? JsonNode.Parse(pending.GetRawText()) : null;
            if (browsing is { } selected) root["browsing"] = JsonNode.Parse(selected.GetRawText());
        })));
}

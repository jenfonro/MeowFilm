using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmPlaybackContractTests
{
    private static TestCase Test(string name, Func<Task> action) =>
        new("film-wire-consumer: " + name, () => MusicQualityConsumerTests.Sta(action));
    private static JsonNode Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!;
    private static JsonElement SourceBrowsing() => FilmJson.Encode(new
    {
        sourceId = "site-choice", lineId = 0, season = 1, loading = false, error = (string?)null,
        lines = new object[] { new { id = (object)0, label = "线路数字0" }, new { id = (object)"0", label = "线路文本0" } },
        episodes = new[] { new { id = "opaque-one", candidateId = "one", name = "同名文件", path = "/one/part.mp4", season = 1, episode = 1 } },
        originalEpisodes = new[]
        {
            new { id = "opaque-one", candidateId = "one", name = "同名文件", path = "/one/part.mp4", season = 1, episode = 1 },
            new { id = "opaque-two", candidateId = "two", name = "同名文件", path = "/two/part.mp4", season = 2, episode = 1 }
        }
    });

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("raw episode with only Node global number displays centrally and selects by candidate identity alone", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root => root["browsing"] = Node(FilmJson.Encode(new
            {
                sourceId = "exact-global-episode-source", loading = false, error = (string?)null,
                lines = Array.Empty<object>(),
                episodes = new[] { new { globalEpisode = 37, candidateId = "opaque-global-episode", name = "原始文件名 1080P" } },
                originalEpisodes = Array.Empty<object>()
            })));
            await Until(() => rig.Page.Episodes.Count == 1);
            var episode = rig.Page.Episodes[0];
            Check(episode.Number is null && episode.GlobalEpisode == 37 && !episode.HasEpisodeTitle &&
                episode.EpisodeNumberLabel == "第37集" && episode.Label == "第37集" &&
                episode.OriginalName == "原始文件名 1080P",
                "the Node global number is display-only, remains centered without a real metadata title, and does not replace the source filename or local coordinate");
            await rig.Page.SelectEpisodeAsync(episode);
            var sent = rig.Runtime.Calls.Last(call => call.Method == "film.session.command" &&
                FilmJson.Text(call.Parameters, "action") == "select.candidate").Parameters;
            Check(FilmJson.Text(sent, "action") == "select.candidate" &&
                FilmJson.Text(sent, "candidateId") == "opaque-global-episode" &&
                !sent.TryGetProperty("season", out _) && !sent.TryGetProperty("episode", out _) &&
                !sent.TryGetProperty("globalEpisode", out _),
                "selecting a global-only source episode sends its opaque candidate identity without inventing local coordinates");
        });
        yield return Test("site and unknown pan labels keep separate identities and typed line selectors", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["sources"] = Node(FilmJson.Encode(new[]
                {
                    new { id = "exact-site-result", title = "影片名不是站点名",
                        source = new { name = "真实站点", key = "cat:fixture:site:1", detailId = 0 } }
                }));
                root["browsing"] = Node(FilmJson.Encode(new
                {
                    sourceId = "exact-site-result", lineId = 0, loading = false,
                    lines = new object[]
                    {
                        new { id = (object)0, label = "原线路", provider = "quark_tv" },
                        new { id = (object)"0", label = "未知网盘原标签", provider = "custom-provider" }
                    }, episodes = Array.Empty<object>()
                }));
            });
            await Until(() => rig.Page.Lines.Count == 2);
            Check(rig.Page.SelectedSource?.Label == "真实站点" &&
                rig.Page.SelectedLine is { Label: "夸克", Provider: "quark_tv" } &&
                rig.Page.Lines[1] is { Label: "未知网盘原标签", Provider: "custom-provider" } &&
                rig.Page.PlayerPanOptions.All(option => option.Value != "custom-provider"),
                "station names never become pan labels and an unknown line retains its own provider and label");
            await rig.Page.BrowseLineAsync(rig.Page.Lines[1]);
            var command = rig.Runtime.Calls.Last(row => FilmJson.Text(row.Parameters, "action") == "browse").Parameters;
            Check(FilmJson.Text(command, "sourceId") == "exact-site-result" &&
                command.GetProperty("lineId").ValueKind == JsonValueKind.String &&
                command.GetProperty("lineId").GetString() == "0",
                "unknown pan browsing still sends the exact typed line of the selected site");
        });
        yield return Test("metadata raw episode views share real still season and cover fallbacks and movies keep Node candidate order", () =>
        {
            foreach (bool raw in new[] { false, true })
                foreach (string kind in new[] { "tv", "source", "group" })
                {
                    var card = Card();
                    var request = card.PlaybackContext!;
                    var target = kind == "tv" ? request.Target with { Season = 1, Episode = 1 } :
                        kind == "source" ? new FilmPlaybackTarget("site:raw", "source", "原始站点",
                            Source: FilmJson.Encode(new { key = "cat:fixture:site:1", detailId = 0 })) :
                        new FilmPlaybackTarget("group:raw", "group", "聚合影片",
                            Members: FilmJson.Encode(new[] { new { sourceKey = "cat:fixture:site:1", detailId = 0 } }));
                    var metadata = FilmJson.Encode(new
                    {
                        title = target.Title, poster_path = "content-poster", overview = "内容简介",
                        seasons = new object[]
                        {
                            new { season_number = 1, name = "原始第一季", poster_path = "season-poster",
                                episodes = new object[]
                                {
                                    new { season_number = 1, episode_number = 1, name = "真实集名", still_path = "episode-still", overview = "真实集简介" },
                                    new { season_number = 1, episode_number = 2, name = "" }
                                } },
                            new { season_number = 2, name = "原始第二季", episodes = Array.Empty<object>() }
                        }
                    });
                    var rows = FilmJson.Encode(new object[]
                    {
                        new { season = 1, episode = 1, candidateId = "one", name = raw ? "实际文件名" : "真实集名",
                            still_path = raw ? "" : "episode-still", overview = "真实集简介" },
                        new { season = 1, episode = 2, candidateId = "two", name = "" },
                        new { season = 2, episode = 1, candidateId = "three", name = "" },
                        new { season = 2, episode = 2, candidateId = "four", name = "", still_path = "raw-still" }
                    });
                    var page = new FilmPlaybackPage(card, request with { Target = target }, null, null);
                    void Apply(JsonElement detail)
                    {
                        var state = Node(Snapshot(Guid.NewGuid(), 1, target, detail,
                            browsing: FilmJson.Encode(new { sourceId = raw ? "site" : null, lineId = 0, season = 1, loading = false,
                                lines = Array.Empty<object>(), episodes = rows }), candidates: FilmJson.Encode(new[]
                            { Candidate("one"), Candidate("two"), Candidate("three"), Candidate("four") })));
                        state["episodes"] = Node(rows);
                        page.Apply(new(FilmJson.Encode(state)));
                    }
                    Apply(metadata);
                    Check(page.HasSeasons && page.Seasons.Select(row => row.Label).SequenceEqual(new[] { "原始第一季", "原始第二季" }) &&
                        page.Episodes.Select(row => row.Image).SequenceEqual(new[] { "episode-still", "season-poster", "content-poster", "raw-still" }) &&
                        page.Episodes[0].HasEpisodeTitle && page.Episodes[0].Label == "第1集 · 真实集名" &&
                        !page.Episodes[1].HasEpisodeTitle && page.Episodes[1].Label == "第2集",
                        "both episode layouts retain actual metadata and the same fallback priority for metadata and source files");
                    page.SetListLayout(true);
                    Check(page.ShowEpisodeList && !page.ShowEpisodeCards && page.Episodes[0].Overview == "真实集简介",
                        "layout selection changes presentation without discarding image or synopsis");
                    var single = JsonNode.Parse(metadata.GetRawText())!.AsObject();
                    single["seasons"] = new JsonArray(JsonNode.Parse(metadata.GetProperty("seasons")[0].GetRawText()));
                    Apply(FilmJson.Encode(single));
                    Check(!page.HasSeasons && page.Seasons.Single().Label == "原始第一季",
                        "a single season keeps its real name in the model and hides the redundant season chooser");
                }
            var movie = Card("movie");
            var moviePage = new FilmPlaybackPage(movie, movie.PlaybackContext!, null, null);
            var movieCandidates = FilmJson.Encode(new[] { Candidate("first", episode: null, quality: "720p"),
                Candidate("second", episode: null, quality: "4k_hdr") });
            moviePage.Apply(new(Snapshot(Guid.NewGuid(), 1, movie.PlaybackContext!.Target,
                movie.PlaybackContext.Metadata!.Value, candidates: movieCandidates)));
            Check(moviePage.Candidates.Select(row => row.Id).SequenceEqual(new[] { "first", "second" }) &&
                moviePage.Candidates.All(row => row.Image == moviePage.Poster && row.Overview == moviePage.Overview) &&
                moviePage.ShowMovieCandidates && !moviePage.HasSeasons,
                "movie cards use their content poster and synopsis while preserving the Node-supplied order");
            return Task.CompletedTask;
        });
        yield return Test("media receipts preserve loopback private intranet and external Node URLs without an origin access policy while retaining data validation", () =>
        {
            string[] addresses =
            [
                "http://127.0.0.1:43001/video.mp4?part=1",
                "http://[::1]:43002/video.mp4",
                "http://192.168.1.20:8080/video.mp4",
                "http://[fd00::20]:8080/video.mp4",
                "http://node:43003/video.mp4",
                "https://external-node.example:8443/media/video.mp4?part=2"
            ];
            foreach (string address in addresses)
            {
                foreach (string? origin in new string?[] { null, "http://node:43003/", "https://external-node.example:8443/" })
                {
                    var payload = FilmJson.Encode(new
                    {
                        attemptId = "exact-attempt", id = "exact-resource", url = address, mediaOrigin = origin,
                        title = "真实回执", position = 12.5, paused = true,
                        format = new { container = "mp4" }, drm = new { scheme = "actual-descriptor" },
                        scope = new { sourceKey = "exact-source", generation = 2 }
                    });
                    var receipt = FilmMediaReceipt.Read(payload);
                    Check(receipt.Address.AbsoluteUri == address && receipt.MediaOrigin?.AbsoluteUri == origin &&
                        receipt.AttemptId == "exact-attempt" && receipt.ResourceId == "exact-resource" &&
                        receipt.Position == 12.5 && receipt.Paused &&
                        JsonElement.DeepEquals(receipt.Format, payload.GetProperty("format")) &&
                        JsonElement.DeepEquals(receipt.Drm, payload.GetProperty("drm")) &&
                        JsonElement.DeepEquals(receipt.Scope, payload.GetProperty("scope")),
                        "the business receipt preserves exact URLs and media fields without a host, address-class or origin restriction");
                }
            }
            foreach (var invalid in new object[]
            {
                new { attemptId = "attempt", id = "resource", url = "./relative.mp4", position = 0, paused = false },
                new { attemptId = "attempt", id = "resource", url = addresses[0], mediaOrigin = "./relative", position = 0, paused = false },
                new { attemptId = "", id = "resource", url = addresses[0], position = 0, paused = false },
                new { attemptId = "attempt", id = "resource", url = addresses[0], position = -1, paused = false },
                new { attemptId = "attempt", id = "resource", url = addresses[0], position = 0, paused = "false" }
            })
            {
                try
                {
                    FilmMediaReceipt.Read(FilmJson.Encode(invalid));
                    throw new InvalidOperationException("invalid receipt data must not become a successful media receipt");
                }
                catch (FilmPlaybackException error) when (error.Code == "INVALID_RESPONSE") { }
            }
            return Task.CompletedTask;
        });
        yield return Test("handoff preserves cache times completeness settings revision query and page without a native TTL", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            var card = Card();
            var source = FilmJson.Encode(new { key = "exact-source", engine = "cat",
                installationId = "original-owner", siteId = "original-site", generation = 1, name = "原始来源" });
            var entries = FilmJson.Encode(new object[]
            {
                new { kind = "metadata", value = new { name = "已得资料" }, completeness = "complete", fetchedAt = 123456L,
                    expiresAt = 125456L, provider = "tmdb", path = "/tv/100", settings = new { revision = "opaque-7", language = "zh-CN" } },
                new { kind = "detail", value = new { name = "未完整详情" }, completeness = "partial", fetchedAt = 10001L,
                    source, detailId = 0 }
            });
            var results = FilmJson.Encode(new[] { new { sourceKey = "exact-source", items = new[] { new { vod_id = 0, vod_name = "准确原备注" } } } });
            var search = FilmJson.Encode(new[] { new { sourceKey = "exact-source", page = 3, completed = false } });
            var request = card.PlaybackContext! with { Sources = [source], Query = "原始 查询", Results = results,
                Search = search, CacheEntries = entries, DataOwner = await rig.Runtime.CaptureConnectionAsync() };
            await rig.OpenAsync(card, request.Own());
            var sent = rig.Runtime.Calls.Single(call => call.Method == "film.session.open").Parameters;
            Check(JsonElement.DeepEquals(sent.GetProperty("cacheEntries"), entries) && JsonElement.DeepEquals(sent.GetProperty("results"), results) &&
                JsonElement.DeepEquals(sent.GetProperty("search"), search) && FilmJson.Text(sent, "query") == "原始 查询",
                "the exact successful owner's timestamps, partial status, opaque configuration and search session are handed off unchanged");
            Check(!rig.Runtime.Calls.Any(call => call.Method.StartsWith("film.data.", StringComparison.Ordinal)),
                "native presentation does not refetch or invent a second success cache");
        });
        yield return Test("source IDs retain JSON number zero versus text zero and metadata special coordinates never gain a global episode", async () =>
        {
            var target = new FilmPlaybackTarget("source:exact", "source", "原始单源",
                Season: 0, Episode: 0, Source: FilmJson.Encode(new { key = "exact", detailId = 0, lineId = 0 }));
            target.Validate();
            var candidate = FilmCandidateItem.Read(Candidate("zero", detail: 0, line: 0));
            Check(candidate.DetailId.ValueKind == JsonValueKind.Number && candidate.DetailId.GetInt32() == 0 &&
                candidate.LineId.ValueKind == JsonValueKind.Number, "source contract does not stringify or drop numeric zero");
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["browsing"] = Node(SourceBrowsing());
                root["candidates"] = Node(FilmJson.Encode(new[] { Candidate("one"), Candidate("two", 2) }));
            });
            await Until(() => rig.Page.Lines.Count == 2);
            Check(rig.Page.SelectedLine?.Label == "线路数字0", "selection compares the typed id, not the shared display string");
            await rig.Page.BrowseLineAsync(rig.Page.Lines[1]);
            var sent = rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "browse").Parameters;
            Check(sent.GetProperty("lineId").ValueKind == JsonValueKind.String && sent.GetProperty("lineId").GetString() == "0",
                "manual line selection forwards its actual original type");
            rig.Runtime.Change(root =>
            {
                root["browsing"] = Node(FilmJson.Encode(new { sourceId = (string?)null, lines = Array.Empty<object>(), episodes = Array.Empty<object>() }));
                root["target"] = Node(FilmJson.Encode((rig.Runtime.Target with { Season = 0, Episode = 0, GlobalEpisode = null }).Wire()));
                root["episodes"] = Node(FilmJson.Encode(new[] { new { season = 0, episode = 0, name = "真实特别集" } }));
            });
            await Until(() => rig.Page.Episodes.Count == 1 && rig.Page.Episodes[0].Season == 0);
            await rig.Page.SelectEpisodeAsync(rig.Page.Episodes[0]);
            sent = rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "select.episode").Parameters.GetProperty("episode");
            Check(sent.GetProperty("season").GetInt32() == 0 && sent.GetProperty("episode").GetInt32() == 0 &&
                !sent.TryGetProperty("globalEpisode", out _), "a real special coordinate is preserved without a fabricated global mapping");
        });
        yield return Test("only proven metadata associations appear and switching uses current provider plus explicit keepSource", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            Check(rig.Page.MetadataSources.Count == 1 && rig.Page.MetadataSources[0].Label == "TMDB",
                "there is no fabricated second metadata provider");
            rig.Runtime.Change(root =>
            {
                root["metadataSources"] = Node(FilmJson.Encode(new[]
                {
                    new { id = "tmdb:100", provider = "tmdb", metadataId = "100", kind = "tv", title = "关联样例" },
                    new { id = "douban:200", provider = "douban", metadataId = "200", kind = "tv", title = "关联样例" }
                }));
                root["browsing"] = Node(SourceBrowsing());
            });
            await Until(() => rig.Page.MetadataSources.Count == 2);
            int before = rig.Runtime.Calls.Count;
            var association = rig.Page.MetadataSources[1];
            Check(rig.Page.HasMetadataMappings && rig.Runtime.Calls.Count == before, "rendering the proven menu performs no business fetch");
            await rig.Page.BrowseMetadataMappingAsync(association);
            var command = rig.Runtime.Calls.Last().Parameters;
            Check(FilmJson.Text(command, "action") == "browse.metadata" && FilmJson.Bool(command, "keepSource") &&
                FilmJson.Text(command, "metadataSourceId") == "douban:200", "only mapping selection keeps the actual station");
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.MetadataSourceId == "douban:200"));
            command = rig.Runtime.Calls.Last().Parameters;
            Check(FilmJson.Text(command, "action") == "browse.metadata" && !FilmJson.Bool(command, "keepSource"),
                "normal metadata selection returns to pure information");
            var douban = rig.Runtime.Target with { Id = "douban:tv:200", MetadataProvider = "douban", MetadataId = "200" };
            rig.Runtime.Change(root =>
            {
                root["target"] = Node(FilmJson.Encode(douban.Wire()));
                root["metadata"] = Node(FilmJson.Encode(new { title = "当前豆瓣资料", rating = new { value = 8.3 } }));
                root["metadataSources"] = Node(FilmJson.Encode(new[]
                {
                    new { id = "tmdb:100", provider = "tmdb", metadataId = "100", kind = "tv", title = "关联样例" },
                    new { id = "douban:200", provider = "douban", metadataId = "200", kind = "tv", title = "关联样例" }
                }));
                root["metadataSourceId"] = "douban:200";
                root["browsing"] = Node(FilmJson.Encode(new { sourceId = (string?)null, lines = Array.Empty<object>(), episodes = Array.Empty<object>() }));
                root["episodes"] = new JsonArray();
            });
            await Until(() => rig.Page.ScoreProvider == "豆瓣");
            Check(rig.Page.ActiveTarget.MetadataProvider == "douban" && !rig.Page.HasImageEpisodes && rig.Page.Poster == "" &&
                rig.Page.Score == "8.3" && rig.Page.Recommendations.Count == 0, "old TMDB provider artwork and similar items do not leak into the new identity");
        });
        yield return Test("season-filtered third row and original directory use different real lists with exact candidate playback", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["browsing"] = Node(SourceBrowsing());
                root["candidates"] = Node(FilmJson.Encode(new[] { Candidate("one"), Candidate("two", 2) }));
            });
            await Until(() => rig.Page.CanBrowseRaw);
            Check(rig.Page.Episodes.Count == 1 && rig.Page.Candidates.Count == 1, "third row stays within the reliable season mapping");
            int before = rig.Runtime.Calls.Count;
            var directory = new FilmDirectory(rig.Page.RawBrowsing, "two");
            Check(directory.Rows.Count == 2 && directory.Rows.All(row => row.IsDirectory), "the directory retains both complete original paths");
            directory.Enter(directory.Rows.Single(row => row.Name == "two"));
            var selected = directory.Rows.Single();
            Check(selected.CandidateId == "two" && selected.IsCurrent && selected.Path == "/two/part.mp4",
                "same-named files keep distinct exact paths and opaque candidate identities");
            directory.Back(); directory.Enter(directory.Rows.Single(row => row.Name == "one")); directory.Back();
            Check(rig.Runtime.Calls.Count == before, "directory traversal never lists transfers or plays a file");
            await rig.Page.SelectOriginalCandidateAsync("two");
            var command = rig.Runtime.Calls.Last().Parameters;
            Check(FilmJson.Text(command, "action") == "select.candidate" && FilmJson.Text(command, "candidateId") == "two",
                "explicit directory playback can select an exact original file outside the third row's season");
            await Error("INVALID_REQUEST", () => rig.Page.SelectOriginalCandidateAsync("opaque-two"));
        });
        yield return Test("original directory current identity refresh preserves navigation and distinguishes equal names across paths", () =>
        {
            var directory = new FilmDirectory(SourceBrowsing(), "one");
            directory.Enter(directory.Rows.Single(row => row.Name == "one"));
            Check(directory.Path == "/one" && directory.Rows.Single().IsCurrent &&
                directory.Rows.Single().CandidateId == "one", "the initial exact acknowledged identity is visible");
            directory.UpdateCurrent("two");
            Check(directory.Path == "/one" && directory.CanBack && directory.Rows.Single().Path == "/one/part.mp4" &&
                !directory.Rows.Single().IsCurrent, "refreshing current neither changes the directory nor marks an equal file name");
            directory.Back(); directory.Enter(directory.Rows.Single(row => row.Name == "two"));
            Check(directory.Path == "/two" && directory.Rows.Single().IsCurrent && directory.Rows.Single().CandidateId == "two" &&
                directory.Rows.Single().Path == "/two/part.mp4", "later traversal uses the latest exact current identity in its original path");
            directory.UpdateCurrent("");
            Check(directory.Path == "/two" && directory.Rows.All(row => !row.IsCurrent),
                "a snapshot with no current clears the derived marker while retaining navigation");
            return Task.CompletedTask;
        });
        yield return Test("movie selection follows confirmed candidate identity rather than pending attempt display label or quality rank", () =>
        {
            var card = Card("movie");
            var request = card.PlaybackContext!;
            var page = new FilmPlaybackPage(card, request, null, null);
            JsonElement Movie(string id, string quality = "1080p")
            {
                var row = JsonNode.Parse(Candidate(id, episode: null, quality: quality).GetRawText())!;
                row["name"] = "同名原文件"; row["path"] = "/电影/" + id + ".mp4";
                return JsonSerializer.SerializeToElement(row);
            }
            var a = Movie("a"); var b = Movie("b");
            // Movie order is the Node snapshot's quality order, not a native sorting policy.
            var candidates = FilmJson.Encode(new[] { Movie("hdr", "4k_hdr"), a, b, Movie("unknown", "") });
            var sessionId = Guid.NewGuid(); long sequence = 0;
            void Apply(string? current, bool pending)
            {
                page.Apply(new FilmPlaybackSnapshot(Snapshot(sessionId, ++sequence, request.Target, request.Metadata!.Value,
                    attempt: pending ? FilmJson.Encode(new { id = "pending-b", candidate = b }) : null,
                    current: current is null ? null : FilmJson.Encode(new
                    { attemptId = "output-" + current, candidate = current == "a" ? a : b }), candidates: candidates)));
                Check(page.Candidates.Select(row => row.Id).SequenceEqual(new[] { "hdr", "a", "b", "unknown" }) &&
                    page.Candidates[1].DisplayLabel == "1080P · 契约来源" && page.Candidates[2].DisplayLabel == "1080P · 契约来源 (2)",
                    "current transitions retain quality ordering stable same-label numbering and all distinct IDs");
                Check(current is null ? page.Candidates.All(row => !row.IsSelected) :
                    page.Candidates.Single(row => row.IsSelected).Id == current,
                    "selection comes only from the confirmed current candidate and never from a pending attempt");
            }
            Apply(null, pending: true);
            Apply("a", pending: false);
            Apply("a", pending: true);
            Apply("a", pending: false);
            Apply("b", pending: false);
            Apply(null, pending: false);
            return Task.CompletedTask;
        });
        yield return Test("errors clear list consumers without claiming empty success and prepared addresses never claim buffered media", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["browsing"] = Node(SourceBrowsing());
                root["prefetch"] = Node(FilmJson.Encode(new[] { new { state = "prepared" } }));
            });
            await Until(() => rig.Page.PrefetchStatus == "相邻集地址已准备");
            Check(!rig.Page.PrefetchStatus.Contains("缓存", StringComparison.Ordinal), "URL preparation is not media-cache coverage");
            rig.Runtime.Change(root =>
            {
                root["prefetch"] = Node(FilmJson.Encode(new[] { new { state = "buffered" } }));
                root["stage"] = "error"; root["error"] = "UPSTREAM_FORMAT";
                root["browsing"]!["error"] = "TIMEOUT";
            });
            await Until(() => rig.Page.ListError.Length > 0);
            Check(rig.Page.Episodes.Count == 0 && rig.Page.Candidates.Count == 0 && !rig.Page.CanBrowseRaw &&
                rig.Page.ShowListStatus && rig.Page.ListStatus == "请求超时，请重试" && rig.Page.StatusText == "上游数据格式错误" &&
                rig.Page.Sources.Count > 0 && rig.Page.PrefetchStatus == "下一集媒体已缓存",
                "third-row failure is explicit while source recovery and actual buffered state remain available");
            rig.Runtime.Change(root => { root["browsing"] = Node(SourceBrowsing()); root["stage"] = "unavailable"; root["error"] = null; });
            await Until(() => rig.Page.Episodes.Count == 1);
            Check(rig.Page.ListError.Length == 0 && rig.Page.StatusText == "暂无可用片源", "a valid source recovers separately from playback unavailability");
        });
        yield return Test("known failures keep their safe classification in list playback and connection presentation", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            (string Code, string Message)[] failures =
            [
                ("TIMEOUT", "请求超时，请重试"),
                ("CLOSED", "服务已关闭，请重连"),
                ("DISCONNECTED", "连接已断开，请重连"),
                ("STALE_SESSION", "会话已失效，请重连"),
                ("INVALID_RESPONSE", "响应无效，请重试"),
                ("UPSTREAM_FORMAT", "上游数据格式错误"),
                ("NO_MATCH", "未找到匹配片源"),
                ("PERMISSION_DENIED", "没有访问权限"),
                ("AUTH_REQUIRED", "请先登录账号"),
                ("AUTH_FAILED", "账号认证失败，请重新登录"),
                ("LOGIN_EXPIRED", "账号登录已失效，请重新登录"),
                ("CHALLENGE_REQUIRED", "需要完成账号验证"),
                ("WEB_INTERACTION_REQUIRED", "需要网页交互"),
                ("STALE_ACCOUNT", "账号已变更，请重试"),
                ("NOT_CONFIGURED", "尚未完成配置"),
                ("NETWORK_ERROR", "网络连接失败，请重试"),
                ("MEDIA_EMPTY_RESPONSE", "媒体响应为空"),
                (MeowFilm.Media.PlaybackError.HttpUnauthorized, "媒体认证失败"),
                (MeowFilm.Media.PlaybackError.HttpDeadline, "请求超时，请重试")
            ];
            foreach (var failure in failures)
            {
                rig.Runtime.Change(root =>
                {
                    root["stage"] = "error"; root["error"] = failure.Code;
                    root["browsing"] = Node(SourceBrowsing());
                    root["browsing"]!["error"] = failure.Code;
                    root["candidates"] = Node(FilmJson.Encode(new[] { Candidate("one") }));
                });
                await Until(() => !rig.Page.ConnectionFailed && rig.Page.ListStatus == failure.Message &&
                    rig.Page.StatusText == failure.Message);
                Check(rig.Page.ListError == failure.Message && rig.Page.ShowListStatus &&
                    rig.Page.Episodes.Count == 0 && rig.Page.Candidates.Count == 0 && !rig.Page.CanBrowseRaw &&
                    rig.Page.Sources.Count > 0, "each classified failure still clears old selections and permits source recovery: " + failure.Code);
                rig.Page.SetConnectionError(failure.Code);
                Check(rig.Page.ConnectionFailed && rig.Page.StatusText == failure.Message,
                    "the connection consumer preserves the same actual code: " + failure.Code);
            }
            const string unknown = "TIMEOUT: https://upstream.invalid/video?token=fixture-secret Cookie=fixture-cookie";
            rig.Runtime.Change(root =>
            {
                root["stage"] = "error"; root["error"] = unknown; root["browsing"]!["error"] = unknown;
            });
            await Until(() => !rig.Page.ConnectionFailed && rig.Page.ListStatus == "加载失败");
            Check(rig.Page.ListError == "加载失败" && rig.Page.StatusText == "播放失败",
                "an unknown code is not prefix-matched and never exposes upstream text or a credential");
            rig.Page.SetConnectionError(unknown);
            Check(rig.Page.ConnectionFailed && rig.Page.StatusText == "连接失败",
                "unknown connection failures use only the fixed safe connection fallback");
        });
        yield return Test("empty successful lists loading and other operation errors never become a list failure", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["browsing"] = Node(SourceBrowsing());
                root["browsing"]!["episodes"] = new JsonArray();
                root["browsing"]!["originalEpisodes"] = new JsonArray();
                root["candidates"] = new JsonArray();
                root["stage"] = "prepared"; root["error"] = null;
                root["reportError"] = "AUTH_FAILED";
                root["prefetch"] = Node(FilmJson.Encode(new[] { new { state = "error", error = "TIMEOUT" } }));
            });
            await Until(() => rig.Page.ListStatus == "暂无内容" && rig.Page.StatusText == "已准备");
            Check(rig.Page.ListError == "" && rig.Page.ShowListStatus && !rig.Page.ConnectionFailed &&
                rig.Page.Episodes.Count == 0 && rig.Page.Candidates.Count == 0 &&
                rig.Page.ReportFailed && rig.Page.PrefetchStatus == "相邻集准备失败",
                "legal empty success and independent reporting or prefetch failure keep separate consumers");
            rig.Runtime.Change(root => { root["browsing"]!["loading"] = true; root["stage"] = "resolving"; });
            await Until(() => rig.Page.ListLoading);
            Check(rig.Page.ListError == "" && rig.Page.ListStatus == "加载中…" && rig.Page.StatusText == "取址中…",
                "in-flight loading is not reported as an empty success or failure");
            rig.Runtime.Change(root =>
            {
                root["browsing"]!["loading"] = false; root["stage"] = "unavailable"; root["error"] = "NO_MATCH";
            });
            await Until(() => !rig.Page.ListLoading && rig.Page.StatusText == "暂无可用片源");
            Check(rig.Page.ListError == "" && rig.Page.ListStatus == "暂无内容",
                "playback unavailability does not forge a failed source list");
            rig.Runtime.Change(root =>
            {
                root["stage"] = "prepared"; root["error"] = null; root["reportError"] = null;
                root["prefetch"] = new JsonArray();
            });
            await Until(() => rig.Page.StatusText == "已准备" && !rig.Page.ReportFailed);
            Check(rig.Page.ListError == "" && rig.Page.PrefetchStatus == "",
                "successful preparation clears only its actual errors without a fabricated failure");
        });
    }
}

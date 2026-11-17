using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using MeowFilm.Capabilities;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmBrowsingConsumerTests
{
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-browsing-consumer: " + name, () => MusicQualityConsumerTests.Sta(run));

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("workInfo accepts explicit null and rejects malformed current-contract projections", async () =>
        {
            var target = Card().PlaybackContext!.Target;
            var original = Node(Snapshot(Guid.NewGuid(), 1, target, Metadata(target, ""))).AsObject();
            original["workInfo"] = null;
            Check(new FilmPlaybackSnapshot(JsonSerializer.SerializeToElement(original)).WorkInfo is null,
                "missing verified work information is explicitly null, not a station fallback");
            var movie = Card("movie").PlaybackContext!.Target;
            var movieInfo = new FilmPlaybackSnapshot(Snapshot(Guid.NewGuid(), 1, movie, Metadata(movie, ""))).WorkInfo;
            Check(movieInfo is { Kind: "movie", SeasonCount: null, EpisodeCount: null },
                "movie fixtures retain the explicit null total fields required by the current contract");
            var source = FilmJson.Target(Candidate("raw").GetProperty("playbackTarget"));
            Check(new FilmPlaybackSnapshot(Snapshot(Guid.NewGuid(), 1, source, Metadata(source, ""))).WorkInfo is null,
                "a source-only fixture publishes explicit null rather than an unverified work projection");
            original.Remove("workInfo");
            await Error("INVALID_RESPONSE", () =>
            {
                _ = new FilmPlaybackSnapshot(JsonSerializer.SerializeToElement(original));
                return Task.CompletedTask;
            });
            foreach (var invalid in new[]
            {
                """{"provider":"site","metadataId":"100","kind":"tv","title":"资料","summary":"","seasonCount":null,"episodeCount":null}""",
                """{"provider":"tmdb","metadataId":"100","kind":"source","title":"资料","summary":"","seasonCount":null,"episodeCount":null}""",
                """{"provider":"tmdb","metadataId":"100","kind":"tv","title":"资料","summary":"","seasonCount":-1,"episodeCount":null}""",
                """{"provider":"tmdb","metadataId":"100","kind":"tv","title":"资料","summary":"","seasonCount":null,"episodeCount":"12"}"""
            })
            {
                original["workInfo"] = JsonNode.Parse(invalid);
                await Error("INVALID_RESPONSE", () =>
                {
                    _ = new FilmPlaybackSnapshot(JsonSerializer.SerializeToElement(original));
                    return Task.CompletedTask;
                });
            }
        });
        yield return Test("initial directory is loading with disabled selectors and no stale cards", () =>
        {
            var card = Card();
            var page = new FilmPlaybackPage(card, card.PlaybackContext!, null, null);
            Check(page.ListLoading && page.ShowListMessage && page.ListStatus == "加载中…" &&
                !page.CanBrowseSources && !page.CanBrowseLines && !page.ShowEpisodeCards &&
                !page.ShowEpisodeList && !page.ShowMovieCandidates, "initial loading is an actual mutually exclusive state");
            return Task.CompletedTask;
        });
        yield return Test("source and typed line returns reuse directories controls and positions without browse RPC", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["sources"] = Node(FilmJson.Encode(new[] { Source("a"), Source("b") }));
                root["browsing"] = Node(Browsing("a", FilmJson.Encode(0)));
            });
            await Until(() => rig.Page.SourceId == "a");
            var view = new FilmPlaybackView { DataContext = rig.Page };
            var a = rig.Page.EpisodeDirectory!;
            var controls = a.Presentation;
            a.HorizontalOffset = 123; a.VerticalOffset = 17;
            rig.Runtime.Intercept = (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "browse")
                {
                    var line = FilmJson.Member(input, "lineId");
                    rig.Runtime.Change(root => root["browsing"] = Node(Browsing(FilmJson.Text(input, "sourceId"),
                        FilmJson.IsIdentity(line) ? line : FilmJson.Encode(0))));
                    return Task.FromResult(FilmRuntimeProbe.Ok(rig.Runtime.Value));
                }
                return Task.FromResult(rig.Runtime.Handle(method, input));
            };
            await rig.Page.BrowseLineAsync(rig.Page.Lines[1]);
            var otherLine = rig.Page.EpisodeDirectory;
            int calls = BrowseCalls(rig);
            await rig.Page.BrowseLineAsync(rig.Page.Lines[0]);
            Check(BrowseCalls(rig) == calls && ReferenceEquals(a, rig.Page.EpisodeDirectory) &&
                ReferenceEquals(controls, a.Presentation) && a.HorizontalOffset == 123 && a.VerticalOffset == 17,
                "line A/B/A retains one exact successful directory and controls");
            await rig.Page.BrowseLineAsync(rig.Page.Lines[1]);
            Check(BrowseCalls(rig) == calls && ReferenceEquals(otherLine, rig.Page.EpisodeDirectory),
                "the second completed line also returns without a browse request");
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "b"));
            var b = rig.Page.EpisodeDirectory;
            calls = BrowseCalls(rig);
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "a"));
            Check(BrowseCalls(rig) == calls && ReferenceEquals(otherLine, rig.Page.EpisodeDirectory),
                "source return restores its last selected line, not the Node cursor");
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "b"));
            Check(BrowseCalls(rig) == calls && ReferenceEquals(b, rig.Page.EpisodeDirectory),
                "both sources preserve completed slices locally");
            GC.KeepAlive(view);
        });
        yield return Test("late source completion cannot overwrite a locally restored selection or native playback", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["sources"] = Node(FilmJson.Encode(new[] { Source("a"), Source("b") }));
                root["browsing"] = Node(Browsing("a", FilmJson.Encode(0)));
            });
            await Until(() => rig.Page.SourceId == "a");
            rig.Runtime.Attempt("donor", Candidate("donor"));
            await Until(() => rig.Session.HasOutput);
            var directory = rig.Page.EpisodeDirectory;
            var target = rig.Page.ActiveTarget;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "browse")
                {
                    started.TrySetResult(); await finish.Task;
                    rig.Runtime.Change(root => root["browsing"] = Node(Browsing("b", FilmJson.Encode(0))));
                    return FilmRuntimeProbe.Ok(rig.Runtime.Value);
                }
                return rig.Runtime.Handle(method, input);
            };
            var read = rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "b"));
            try
            {
                Check(rig.Page.SourceId == "b" && rig.Page.ListLoading && rig.Page.ShowListMessage &&
                    !rig.Page.ShowEpisodeCards && !rig.Page.ShowEpisodeList && !rig.Page.CanBrowseLines,
                    "the new selection publishes loading before the RPC completes");
                await started.Task;
                int calls = BrowseCalls(rig);
                await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "a"));
                Check(!rig.Page.ListLoading && ReferenceEquals(directory, rig.Page.EpisodeDirectory) && BrowseCalls(rig) == calls,
                    "an in-flight source read does not prevent an immediate local return");
            }
            finally { finish.TrySetResult(); }
            try { await read; } catch (OperationCanceledException) { }
            Check(rig.Page.SourceId == "a" && ReferenceEquals(directory, rig.Page.EpisodeDirectory) &&
                rig.Session.HasOutput && rig.Page.ActiveTarget == target && rig.Native.Stops == 0 &&
                rig.Page.CurrentCandidateId == "donor", "late browse completion cannot change selection or accepted output");
        });
        yield return Test("metadata and season returns restore their last successful slices without browse RPC", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                var second = rig.Runtime.Target with { Season = 2, Episode = null, GlobalEpisode = null };
                root["metadata"]!["seasons"]!.AsArray().Add(Node(FilmJson.Encode(new
                {
                    businessId = second.Id + ":season:2", playbackTarget = second.Wire(),
                    season_number = 2, name = "第二季", episodes = Array.Empty<object>()
                })));
                root["metadataSources"]!.AsArray().Add(Node(FilmJson.Encode(new
                    { id = "douban:200", provider = "douban", metadataId = "200", kind = "tv", title = "可靠资料" })));
            });
            await Until(() => rig.Page.Seasons.Count == 2 && rig.Page.MetadataSources.Count == 2);
            var first = rig.Page.EpisodeDirectory;
            await rig.Page.BrowseSeasonAsync(rig.Page.Seasons[1]);
            var secondDirectory = rig.Page.EpisodeDirectory;
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.MetadataSourceId == "douban:200"));
            var douban = rig.Page.EpisodeDirectory;
            int calls = BrowseCalls(rig);
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.MetadataSourceId == "tmdb:100"));
            Check(BrowseCalls(rig) == calls && ReferenceEquals(secondDirectory, rig.Page.EpisodeDirectory) && rig.Page.Season == 2,
                "returning to metadata restores its previous season locally");
            await rig.Page.BrowseSeasonAsync(rig.Page.Seasons[0]);
            Check(BrowseCalls(rig) == calls && ReferenceEquals(first, rig.Page.EpisodeDirectory),
                "the original successful season remains a local slice");
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.MetadataSourceId == "douban:200"));
            Check(BrowseCalls(rig) == calls && ReferenceEquals(douban, rig.Page.EpisodeDirectory) && rig.Page.Season == 2,
                "the other metadata provider preserves its own last season");
        });
        yield return Test("failed and empty source reads are local settled states and never fake files", async () =>
        {
            foreach (bool fails in new[] { true, false })
            {
                await using var rig = new FilmPlaybackRig();
                await rig.OpenAsync();
                rig.Runtime.Change(root => root["sources"] = Node(FilmJson.Encode(new[] { Source("empty") })));
                await Until(() => rig.Page.Sources.Any(row => row.Id == "empty"));
                var target = rig.Page.ActiveTarget;
                rig.Runtime.Intercept = (method, input, _) =>
                {
                    if (method == "film.session.command" && FilmJson.Text(input, "action") == "browse")
                    {
                        rig.Runtime.Change(root => root["browsing"] = Node(FilmJson.Encode(new
                        {
                            sourceId = "empty", season = 1, loading = false, error = fails ? "UPSTREAM_ERROR" : null,
                            lines = Array.Empty<object>(), episodes = Array.Empty<object>(), originalEpisodes = Array.Empty<object>()
                        })));
                        return Task.FromResult(FilmRuntimeProbe.Ok(rig.Runtime.Value));
                    }
                    return Task.FromResult(rig.Runtime.Handle(method, input));
                };
                try { await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "empty")); }
                catch (FilmPlaybackException error) when (fails && error.Code == "UPSTREAM_ERROR") { }
                Check(!rig.Page.ListLoading && rig.Page.ShowListMessage && !rig.Page.CanBrowseLines &&
                    !rig.Page.CanBrowseRaw && rig.Page.Episodes.Count == 0 && rig.Page.ActiveTarget == target &&
                    rig.Page.ListStatus == (fails ? "上游服务请求失败" : "无可用视频源"),
                    "failure and successful empty remain distinct from playback and usable files");
            }
        });
        yield return Test("reselecting a failed source performs a real read rather than caching the failure", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root => root["sources"] = Node(FilmJson.Encode(new[] { Source("failed") })));
            await Until(() => rig.Page.Sources.Any(row => row.Id == "failed"));
            var target = rig.Page.ActiveTarget;
            rig.Runtime.Intercept = (method, input, _) =>
            {
                string action = FilmJson.Text(input, "action");
                if (method == "film.session.command" && action is ("browse" or "browse.refresh"))
                {
                    rig.Runtime.Change(root => root["browsing"] = action == "browse"
                        ? Node(FilmJson.Encode(new { sourceId = "failed", season = 1, loading = false,
                            error = "UPSTREAM_ERROR", lines = Array.Empty<object>(), episodes = Array.Empty<object>() }))
                        : Node(Browsing("failed", FilmJson.Encode(0))));
                    return Task.FromResult(FilmRuntimeProbe.Ok(rig.Runtime.Value));
                }
                return Task.FromResult(rig.Runtime.Handle(method, input));
            };
            var source = rig.Page.Sources.Single(row => row.Id == "failed");
            await Error("UPSTREAM_ERROR", () => rig.Page.BrowseSourceAsync(source));
            await rig.Page.BrowseSourceAsync(source);
            Check(!rig.Page.ListLoading && rig.Page.ListError.Length == 0 && rig.Page.Episodes.Count == 1 &&
                rig.Runtime.Calls.Count(row => FilmJson.Text(row.Parameters, "action") == "browse.refresh") == 1 &&
                rig.Page.ActiveTarget == target, "failed reads are not successful display slices and explicit reselection can recover");
            int calls = BrowseCalls(rig);
            await rig.Page.BrowseSourceAsync(source);
            Check(BrowseCalls(rig) == calls, "the recovered successful slice subsequently reuses its directory locally");
        });
        yield return Test("a superseded browse failure settles as cancellation without changing the restored selection", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["sources"] = Node(FilmJson.Encode(new[] { Source("a"), Source("b") }));
                root["browsing"] = Node(Browsing("a", FilmJson.Encode(0)));
            });
            await Until(() => rig.Page.SourceId == "a");
            var directory = rig.Page.EpisodeDirectory;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "browse")
                {
                    entered.TrySetResult(); await finish.Task;
                    return FilmRuntimeProbe.Failure("UPSTREAM_ERROR");
                }
                return rig.Runtime.Handle(method, input);
            };
            var read = rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "b"));
            try
            {
                await entered.Task;
                await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "a"));
            }
            finally { finish.TrySetResult(); }
            bool cancelled = false;
            try { await read; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && rig.Page.SourceId == "a" && rig.Page.ListError.Length == 0 &&
                ReferenceEquals(directory, rig.Page.EpisodeDirectory), "an old read cannot surface its failure on another restored choice");
        });
        yield return Test("workInfo summary and real totals stay independent from selected metadata and directory counts", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["workInfo"] = Node(FilmJson.Encode(new { provider = "tmdb", metadataId = "100", kind = "tv",
                    title = "主资料", summary = "可靠关联填缺简介", seasonCount = 3, episodeCount = 48 }));
                root["metadataSourceId"] = "douban:200";
                root["metadataSources"] = Node(FilmJson.Encode(new[] { new { id = "douban:200", provider = "douban", metadataId = "200", kind = "tv" } }));
                root["metadata"] = Node(FilmJson.Encode(new { title = "浏览资料", intro = "不是主简介" }));
                root["episodes"] = new JsonArray();
                root["browsing"] = Node(Browsing("a", FilmJson.Encode(0)));
            });
            await Until(() => rig.Page.MetadataSourceId == "douban:200");
            Check(rig.Page.Overview == "可靠关联填缺简介" && rig.Page.Metadata.Contains("共 3 季 48 集", StringComparison.Ordinal),
                "native rendering uses only the public stable work projection for summary and totals");
            rig.Runtime.Change(root => { root["workInfo"]!["seasonCount"] = null; root["workInfo"]!["episodeCount"] = null; });
            await Until(() => !rig.Page.Metadata.Contains("共", StringComparison.Ordinal));
            Check(rig.Page.Episodes.Count == 1 && rig.Page.Overview == "可靠关联填缺简介",
                "a local one-file directory never fabricates missing total counts");
        });
        yield return Test("metadata browsing cannot revoke an admitted native preparation", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["sources"] = Node(FilmJson.Encode(new[] { Source("a") }));
                root["browsing"] = Node(Browsing("a", FilmJson.Encode(0)));
                root["metadataSources"]!.AsArray().Add(Node(FilmJson.Encode(new
                    { id = "douban:200", provider = "douban", metadataId = "200", kind = "tv", title = "可靠关联" })));
            });
            await Until(() => rig.Page.SourceId == "a" && rig.Page.MetadataSources.Count == 2);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken preparation = default;
            rig.Native.Preparing = async token => { preparation = token; entered.TrySetResult(); await finish.Task.WaitAsync(token); };
            rig.Runtime.Attempt("preparing", Candidate("preparing"));
            await entered.Task;
            try
            {
                var target = rig.Page.ActiveTarget;
                await rig.Page.BrowseMetadataMappingAsync(rig.Page.MetadataSources.Single(row => row.MetadataSourceId == "douban:200"));
                Check(!preparation.IsCancellationRequested && rig.Page.ActiveTarget == target &&
                    rig.Session.SnapshotFor(rig.Page)?.AttemptId == "preparing" && !rig.Runtime.Released.Contains("preparing"),
                    "browse.metadata changes only the directory mapping and not the pending media intent");
            }
            finally { finish.TrySetResult(); }
            await Until(() => rig.Session.HasOutput && rig.Native.SettledIdentity == "preparing");
            Check(rig.Native.Commits == 1 && rig.Native.Stops == 0, "the same native preparation can still publish its actual output");
        });
        yield return Test("loaded selection sends the complete runtime target while Node is browsing elsewhere", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["sources"] = Node(FilmJson.Encode(new[] { Source("a"), Source("b") }));
                root["browsing"] = Node(Browsing("a", FilmJson.Encode(0)));
            });
            await Until(() => rig.Page.SourceId == "a");
            var item = rig.Page.Episodes[0];
            rig.Runtime.Intercept = (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "browse")
                {
                    rig.Runtime.Change(root => root["browsing"] = Node(Browsing("b", FilmJson.Encode(0))));
                    return Task.FromResult(FilmRuntimeProbe.Ok(rig.Runtime.Value));
                }
                return Task.FromResult(rig.Runtime.Handle(method, input));
            };
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "b"));
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Id == "a"));
            int calls = BrowseCalls(rig);
            await rig.Page.SelectEpisodeAsync(item);
            var command = rig.Runtime.Calls.Last(row => FilmJson.Text(row.Parameters, "action") == "select.target").Parameters;
            Check(BrowseCalls(rig) == calls && JsonElement.DeepEquals(command.GetProperty("target"), FilmJson.Encode(item.PlaybackTarget!.Wire())) &&
                FilmJson.Text(command, "metadataSourceId") == rig.Page.MetadataSourceId &&
                !command.TryGetProperty("candidateId", out _) && !command.TryGetProperty("episode", out _),
                "selection is exact and does not synchronize the stale Node browse cursor first");
        });
    }

    private static JsonNode Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!;
    private static object Source(string id) => new { id, title = id, source = new { key = "cat:fixture:site:1", detailId = 0, name = id } };
    private static int BrowseCalls(FilmPlaybackRig rig) => rig.Runtime.Calls.Count(row =>
        FilmJson.Text(row.Parameters, "action").StartsWith("browse", StringComparison.Ordinal));
    private static JsonElement Browsing(string source, JsonElement line)
    {
        string id = source + ":" + line.GetRawText();
        var targetNode = Node(Candidate(id).GetProperty("playbackTarget"));
        targetNode["source"]!["lineId"] = Node(line);
        var target = JsonSerializer.SerializeToElement(targetNode);
        var row = new { businessId = "fixture:file:" + id, playbackTarget = target, candidateId = id, name = id, season = 1, episode = 1 };
        return FilmJson.Encode(new { sourceId = source, lineId = line, season = 1, loading = false, error = (string?)null,
            lines = new object[] { new { id = (object)0, label = "线路A", provider = "quark" }, new { id = (object)"other", label = "线路B", provider = "baidu" } },
            episodes = new[] { row }, originalEpisodes = new[] { row } });
    }
}

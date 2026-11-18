using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmBrowsingConsumerTests
{
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-browsing-consumer: " + name, () => TestUiHost.InvokeAsync(run));

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("all work fields and favorite entry identity are independent from browsing and exact playback targets", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            var entry = rig.Page.EntryTarget;
            var work = JsonNode.Parse(rig.Runtime.Metadata.GetRawText())!.AsObject();
            work["title"] = "固定主标题"; work["vote_average"] = 9.1;
            work["backdrop_path"] = "work-backdrop"; work["logo_path"] = "work-logo";
            rig.Runtime.Change(root => root["workInfo"] = Node(WorkInfo(entry with { Title = "固定主标题" }, FilmJson.Encode(work))));
            await Until(() => rig.Page.Title == "固定主标题");
            var expected = new[] { rig.Page.Title, rig.Page.Poster, rig.Page.Backdrop, rig.Page.Logo, rig.Page.Metadata,
                rig.Page.Genres, rig.Page.Score, rig.Page.ScoreProvider, rig.Page.Directors, rig.Page.Cast, rig.Page.Overview };
            var recommendations = rig.Page.Recommendations;
            var changes = new List<string?>();
            rig.Page.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            rig.Runtime.Change(root =>
            {
                root["target"] = Node(Candidate("selected-file").GetProperty("playbackTarget"));
                root["metadata"] = Node(FilmJson.Encode(new { title = "浏览标题", pic = new { normal = "browse-poster" },
                    intro = "浏览简介", rating = new { value = 1.2 }, actors = new[] { new { name = "浏览演员" } } }));
                root["metadataSourceId"] = "douban:200";
                root["metadataSources"] = Node(FilmJson.Encode(new[] { new { id = "douban:200", provider = "douban", metadataId = "200", kind = "tv" } }));
                root["browsing"] = Node(Browsing("other", FilmJson.Encode(0)));
            });
            await Until(() => rig.Page.SourceId == "other");
            Check(expected.SequenceEqual(new[] { rig.Page.Title, rig.Page.Poster, rig.Page.Backdrop, rig.Page.Logo, rig.Page.Metadata,
                    rig.Page.Genres, rig.Page.Score, rig.Page.ScoreProvider, rig.Page.Directors, rig.Page.Cast, rig.Page.Overview }) &&
                ReferenceEquals(recommendations, rig.Page.Recommendations) &&
                !changes.Any(name => name is nameof(FilmPlaybackPage.Title) or nameof(FilmPlaybackPage.Poster) or
                    nameof(FilmPlaybackPage.Cast) or nameof(FilmPlaybackPage.Overview)),
                "unrelated browse snapshots do not republish or replace stable work content");
            await rig.Page.ToggleFavoriteAsync();
            var favorite = (await rig.Library.ReadAsync()).Single();
            Check(favorite.Id == entry.Id && favorite.PlaybackContext!.Target == entry &&
                favorite.Title == rig.Page.Title && favorite.Poster == rig.Page.Poster && favorite.Score == rig.Page.Score,
                "favorite identity is the entry, while all saved visible fields belong to stable workInfo");
        });
        yield return Test("aggregate source choices and cached provider views never update one another or write automatic fallback", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root => root["metadataSources"]!.AsArray().Add(Node(FilmJson.Encode(new
                { id = "douban:200", provider = "douban", metadataId = "200", kind = "tv" }))));
            await Until(() => rig.Page.MetadataSources.Count == 2);
            var tmdb = rig.Page.MetadataSources.Single(row => row.Provider == "tmdb");
            var douban = rig.Page.MetadataSources.Single(row => row.Provider == "douban");
            var original = rig.Page.EpisodeDirectory;
            await rig.Page.BrowseMetadataMappingAsync(douban);
            var other = rig.Page.EpisodeDirectory;
            Check(rig.Page.SelectedSource?.Provider == "tmdb" && rig.Page.MetadataSourceId == douban.MetadataSourceId &&
                (await rig.Preferences.ReadPreferencesAsync(default)).MetadataViewProvider == "douban",
                "the explicit view pick persists its provider without changing the aggregate source selection");
            int calls = BrowseCalls(rig), writes = rig.PreferencesStore.Writes;
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Provider == "douban"));
            await rig.Page.BrowseSourceAsync(rig.Page.Sources.Single(row => row.Provider == "tmdb"));
            Check(rig.Page.MetadataSourceId == douban.MetadataSourceId && BrowseCalls(rig) == calls &&
                rig.PreferencesStore.Writes == writes && ReferenceEquals(other, rig.Page.EpisodeDirectory),
                "aggregate labels change neither the selected directory nor its saved provider preference");
            await rig.Page.BrowseMetadataMappingAsync(tmdb);
            Check(BrowseCalls(rig) == calls && ReferenceEquals(original, rig.Page.EpisodeDirectory) &&
                rig.Page.SelectedSource?.Provider == "tmdb" &&
                (await rig.Preferences.ReadPreferencesAsync(default)).MetadataViewProvider == "tmdb",
                "an explicit cached provider return still persists its preference but makes no browse RPC");
            await rig.Page.BrowseMetadataMappingAsync(douban);
            Check(BrowseCalls(rig) == calls && ReferenceEquals(other, rig.Page.EpisodeDirectory),
                "both successful provider slices are retained");
            writes = rig.PreferencesStore.Writes;
            rig.Runtime.Change(root =>
            {
                root["metadataSources"]!.AsArray().RemoveAt(1);
                root["metadataSourceId"] = tmdb.MetadataSourceId;
            });
            await Until(() => rig.Page.MetadataSourceId == tmdb.MetadataSourceId);
            Check(rig.PreferencesStore.Writes == writes &&
                (await rig.Preferences.ReadPreferencesAsync(default)).MetadataViewProvider == "douban",
                "revoked provider availability replaces the local view without overwriting the user's preference");
        });
        yield return Test("either single provider is displayed as supplied without persisting the temporary fallback", async () =>
        {
            foreach (string preferred in new[] { "tmdb", "douban" })
            {
                await using var rig = new FilmPlaybackRig();
                await rig.Preferences.SavePreferencesAsync(FilmPreferences.Defaults with { MetadataViewProvider = preferred }, "metadataViewProvider");
                int writes = rig.PreferencesStore.Writes;
                string available = preferred == "tmdb" ? "douban" : "tmdb";
                var target = new FilmPlaybackTarget($"{available}:tv:100", "tv", "唯一可靠资料", available, "100");
                await rig.OpenAsync(Card() with { PlaybackContext = new(target, Metadata: Metadata(target, "")) });
                Check(rig.Page.MetadataSources.Single().Provider == available &&
                    rig.Page.MetadataSourceId == available + ":100" && !rig.Page.HasMetadataMappings &&
                    rig.PreferencesStore.Writes == writes &&
                    (await rig.Preferences.ReadPreferencesAsync(default)).MetadataViewProvider == preferred &&
                    BrowseCalls(rig) == 0, "the native consumer accepts the runtime's available provider without an automatic write or RPC");
            }
        });
        yield return Test("a previous unavailable target cannot disable a successful exact directory", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Change(root =>
            {
                root["stage"] = "unavailable"; root["error"] = "NO_MATCH";
                root["browsing"] = Node(Browsing("available", FilmJson.Encode(0)));
            });
            await Until(() => rig.Page.SourceId == "available");
            Check(rig.Page.CanSelectDirectory && !rig.Page.CanPlay, "directory and old formal-target eligibility are independent");
            await rig.Page.SelectEpisodeAsync(rig.Page.Episodes.Single());
            Check(rig.Runtime.Calls.Any(call => FilmJson.Text(call.Parameters, "action") == "select.target"),
                "the exact valid row reaches the authoritative selection owner");
        });
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
                """{"provider":"site","metadataId":"100","kind":"tv","title":"资料","summary":"","seasonCount":null,"episodeCount":null,"metadata":{}}""",
                """{"provider":"tmdb","metadataId":"100","kind":"source","title":"资料","summary":"","seasonCount":null,"episodeCount":null,"metadata":{}}""",
                """{"provider":"tmdb","metadataId":"100","kind":"tv","title":"资料","summary":"","seasonCount":-1,"episodeCount":null,"metadata":{}}""",
                """{"provider":"tmdb","metadataId":"100","kind":"tv","title":"资料","summary":"","seasonCount":null,"episodeCount":"12","metadata":{}}""",
                """{"provider":"tmdb","metadataId":"100","kind":"tv","title":"资料","summary":"","seasonCount":null,"episodeCount":null}""",
                """{"provider":"tmdb","metadataId":"100","kind":"tv","title":"资料","summary":"","seasonCount":null,"episodeCount":null,"metadata":null}""",
                """{"provider":"tmdb","metadataId":"100","kind":"tv","title":"资料","summary":"","seasonCount":null,"episodeCount":null,"metadata":[]}"""
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
            await rig.Page.BrowseMetadataMappingAsync(rig.Page.MetadataSources.Single(row => row.MetadataSourceId == "douban:200"));
            var douban = rig.Page.EpisodeDirectory;
            int calls = BrowseCalls(rig);
            await rig.Page.BrowseMetadataMappingAsync(rig.Page.MetadataSources.Single(row => row.MetadataSourceId == "tmdb:100"));
            Check(BrowseCalls(rig) == calls && ReferenceEquals(secondDirectory, rig.Page.EpisodeDirectory) && rig.Page.Season == 2,
                "returning to metadata restores its previous season locally");
            await rig.Page.BrowseSeasonAsync(rig.Page.Seasons[0]);
            Check(BrowseCalls(rig) == calls && ReferenceEquals(first, rig.Page.EpisodeDirectory),
                "the original successful season remains a local slice");
            await rig.Page.BrowseMetadataMappingAsync(rig.Page.MetadataSources.Single(row => row.MetadataSourceId == "douban:200"));
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
                    title = "主资料", summary = "可靠关联填缺简介", seasonCount = 3, episodeCount = 48,
                    metadata = new { title = "主资料" } }));
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

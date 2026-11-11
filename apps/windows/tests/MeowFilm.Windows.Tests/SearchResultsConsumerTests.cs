using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class SearchResultsConsumerTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.UnifiedSearchCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> UnifiedSearchCases()
    {
        yield return Case("unified search music submissions from library and same-word tabs always open online overview", UnifiedMusic);
        yield return Case("unified search shell target defaults manual draft refresh and result tabs remain synchronized", UnifiedTargets);
        yield return Case("unified search real playback keeps second-round sources and returns without another query", UnifiedPlayback);
        yield return Case("unified search in-flight navigation keeps late TMDB members and full metadata", UnifiedLatePlayback);
        yield return Case("unified search raw playback keeps one source and no TMDB overview", UnifiedRawPlayback);
        yield return Case("unified search source cover options reach Node and its selected aggregate and raw covers render unchanged", UnifiedCovers);
        yield return Case("unified search isolated UTF16 display never rewrites opaque navigation identity or raw data", UnifiedText);
        yield return Case("unified search an older handoff cannot replace the newest selected card", UnifiedHandoffRace);
        yield return Case("unified search unchanged display after generation replacement opens the new identity", UnifiedGenerationPlayback);
        yield return Case("unified search page distinguishes all-source failure from successful empty results", UnifiedFailureStatus);
        yield return Case("unified search unreachable playback releases its cache and session observer", UnifiedPlaybackRelease);
        yield return Case("unified search playback restores retained history origin and current identities", UnifiedPlaybackRetention);
        yield return Case("unified search music card navigation leaves the projected view and back restores it", UnifiedMusicNavigation);
        yield return Case("unified search rapid tab changes and page exit cannot remount stale results", UnifiedRapidTabs);
        yield return Case("unified search pending film stop cannot overwrite a newer film submission", UnifiedStopNewQuery);
        yield return Case("unified search pending film stop keeps later tabs and resumes the same search", UnifiedStopTabs);
        yield return Case("unified search pending film stop cannot remount results after page exit", UnifiedStopExit);
        yield return Case("unified search novel target five result classes route book and preserve original query", UnifiedNovel);
        yield return Case("unified search retired novel preparation cannot start after tab changes or page exit", UnifiedNovelPreparation);
        yield return Case("unified search same-word target change cancels novel work without discarding retained pages", UnifiedNovelRetirement);
    }
    private static async Task UnifiedNovel()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("合成小说", "novel");
        await SearchFixture.Until(() => !f.Novel.Search.Loading && f.Novel.Search.Books.Count == 1);
        True(f.Page.Targets.Select(t => t.Id).SequenceEqual(new[] { "music", "film", "novel" }) &&
            f.Page.Tabs.Select(t => t.Id).SequenceEqual(new[] { "overview", "songs", "playlists", "film", "novel" }),
            "three domain targets and all five result classes are exposed by the real page");
        var filmTools = (FrameworkElement)((SearchResultsView)f.Page.View).FindName("FilmTools");
        // The shell is mounted off-screen, so check layout visibility rather than inherited screen visibility.
        True(filmTools.Visibility == Visibility.Collapsed, "novel results do not show the film raw-list switch or metadata status");
        Equal(0, f.Runtime.SearchOpens);
        int count = f.Novel.Search.Books.Count;
        await f.Page.OpenNovelAsync(f.Novel.Search.Books.Single());
        await SearchFixture.Until(() => f.Novel.Route == NovelCapability.BookRoute && f.Novel.Session?.Chapters.Count > 0);
        True(f.Novel.Library.Entries.Count == 0, "opening metadata does not record reading history");
        f.Click("BackButton");
        await SearchFixture.Until(() => f.SearchHost is not null && f.Page.SelectedTarget == "novel");
        Equal("合成小说", f.Page.Query); Equal(count, f.Novel.Search.Books.Count);
        await f.Page.OpenTabAsync("film"); Equal("film", f.Page.SelectedTarget);
        True(filmTools.Visibility == Visibility.Visible, "film results retain their own metadata status and raw-list switch");
        await f.Page.OpenTabAsync("songs"); Equal("music", f.Page.SelectedTarget);
        True(filmTools.Visibility == Visibility.Collapsed, "music results do not inherit film-only tools");
    }
    private static async Task UnifiedNovelPreparation()
    {
        foreach (bool leaving in new[] { false, true })
        {
            var runtime = new NovelTests.Runtime();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Deliberately non-cooperative preparation also exercises the submission
            // fence after the await, not only cancellation of a well-behaved client.
            runtime.Listing = async _ => { entered.TrySetResult(); await release.Task; };
            await using var f = await UnifiedScene.Create(novelRuntime: runtime);
            int web = 0; f.Novel.Sources.InteractAsync = (_, _) => { web++; return Task.FromResult<object?>(null); };
            await f.Submit("生命周期", "music");
            var pending = f.Page.OpenTabAsync("novel");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (leaving) await f.Navigate(f.Film, FilmCapability.HomeRoute);
                else await f.Page.OpenTabAsync("film");
                release.TrySetResult(); await pending; await Drain();
                True(!runtime.Calls.Any(c => c.Method == "source.runtime.library.invoke"), "retired preparation does not admit a source invocation");
                True(web == 0 && runtime.Calls.All(c => c.Method != "source.runtime.web.respond"), "hidden preparation cannot open or commit Web");
                True(f.Page.Error == "" && (!leaving ? f.Page.SelectedTarget == "film" : f.SearchHost is null), "late completion does not change the current page or error");
            }
            finally { release.TrySetResult(); }
        }
    }
    private static async Task UnifiedNovelRetirement()
    {
        var runtime = new NovelTests.Runtime();
        await using var f = await UnifiedScene.Create(novelRuntime: runtime);
        await f.Submit("同词", "novel"); await SearchFixture.Until(() => !f.Novel.Search.Loading && f.Novel.Search.Books.Count == 1);
        var retained = f.Novel.Search.Books.Single(); int firstPage = f.Novel.Search.States.Single().Page;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Read = async (_, _, input, token) =>
        {
            Equal(firstPage + 1, input.GetProperty("page").GetInt32()); entered.TrySetResult();
            using var registration = token.Register(() => retired.TrySetResult());
            return await release.Task;
        };
        int web = 0; f.Novel.Sources.InteractAsync = (_, _) => { web++; return Task.FromResult<object?>(null); };
        var more = f.Page.LoadMoreNovelAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The first shell Submit has already returned; its PageRequest is gone.
            await f.Submit("同词", "film"); await retired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult(NovelJson.Value(new { books = new[] { new { name = "迟到书", bookUrl = "https://late.invalid/book" } } }));
            await more; await Drain();
            True(f.Page.SelectedTarget == "film" && f.Page.Error == "" && web == 0, "same-word target change retires novel without hidden UI or error");
            True(f.Novel.Search.Books.SequenceEqual(new[] { retained }) && f.Novel.Search.States.Single().Page == firstPage,
                "late page cannot publish, while accepted results and page cursor stay retained");
            True(runtime.Calls.All(c => c.Method != "source.runtime.web.respond"), "retired result cannot commit Web state");
            int calls = runtime.Calls.Count(c => c.Method == "source.runtime.library.invoke");
            runtime.Read = (_, _, input, _) => Task.FromResult(NovelJson.Value(new
                { books = new[] { new { name = "第二页", author = "作者", bookUrl = "https://novel.invalid/second" } } }));
            await f.Page.OpenTabAsync("novel"); await SearchFixture.Until(() => !f.Novel.Search.Loading && f.Novel.Search.Books.Count == 2);
            Equal(firstPage + 1, f.Novel.Search.States.Single().Page);
            True(f.Novel.Search.Books.Any(book => book.SourceIdentity == retained.SourceIdentity) && runtime.Calls.Count(c => c.Method == "source.runtime.library.invoke") == calls + 1,
                "return resumes only the cancelled page, not a fresh page-one search");
        }
        finally { release.TrySetResult(NovelJson.Value(new { books = Array.Empty<object>() })); }
    }
    private static async Task UnifiedMusic()
    {
        await using var music = await SearchScene.Create(scriptRuntime: new QualityFixture.ScriptRuntime());
        await music.Policy(["wy"], ["wy"]);
        await using var search = new SearchFixture();
        await using var film = new FilmCapability(new SearchHome(), search.Service);
        using var page = new SearchResultsPage(music.Module, film);
        await music.Module.NavigateAsync("favorites", default);
        await page.ActivateAsync(); await page.SubmitAsync("合成", "music", default);
        await SearchUntil(() => !music.Module.SearchSongsLoading && !music.Module.SearchPlaylistsLoading && music.Http.SearchRequests == 2);
        True(music.Module.IsSearchPage && music.Module.IsSearchOverview && page.SelectedTab == "overview", "library input uses the native online comprehensive search");
        int requests = music.Http.SearchRequests;
        await page.OpenTabAsync("songs"); await page.SubmitAsync("合成", "music", default);
        True(page.SelectedTab == "overview" && music.Module.IsSearchOverview, "same word previously on songs opens overview");
        await page.OpenTabAsync("playlists"); await page.SubmitAsync("合成", "music", default);
        True(page.SelectedTab == "overview" && music.Module.IsSearchOverview && music.Http.SearchRequests == requests,
            "same word previously on playlists opens overview without rebuilding known results");
        Equal(0, search.Runtime.SearchOpens); True(search.Service.Current is null, "music result kinds never start film");
    }

    private static async Task UnifiedTargets()
    {
        await using var f = await UnifiedScene.Create();
        Equal("music", f.Picker.SelectedValue as string);
        await f.Navigate(f.Film, FilmCapability.HomeRoute);
        Equal("film", f.Picker.SelectedValue as string);
        f.Input.Text = "保留草稿";
        f.Picker.SelectedValue = "music";
        int calls = f.Runtime.SearchOpens;
        await f.Film.NavigateAsync(FilmCapability.HomeRoute, default); await Drain();
        Equal("保留草稿", f.Input.Text); Equal("music", f.Picker.SelectedValue as string);
        Equal(calls, f.Runtime.SearchOpens);
        await f.Navigate(f.Music.Module, "favorites");
        Equal("music", f.Picker.SelectedValue as string);
        await f.Submit("原词", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        Equal("film", f.Page.SelectedTab); Equal("film", f.Picker.SelectedValue as string);
        f.Picker.SelectedValue = "music"; f.Input.Text = "未提交的输入";
        f.Sources.Publish(); await Drain();
        Equal("music", f.Picker.SelectedValue as string); Equal("未提交的输入", f.Input.Text);
        Equal(1, f.Runtime.SearchOpens);
        await f.Page.OpenTabAsync("songs"); Equal("music", f.Picker.SelectedValue as string);
        await f.Page.OpenTabAsync("playlists"); Equal("music", f.Picker.SelectedValue as string);
        await f.Page.OpenTabAsync("film"); await Drain(); Equal("film", f.Picker.SelectedValue as string);
        Equal(1, f.Runtime.SearchOpens);
        f.Click("BackButton"); await SearchFixture.Until(() => f.Music.Module.CaptureNavigationLocation() == "favorites" && f.Status == "");
        True(!f.Music.Module.IsSearchPage, "leaving public search restores the original module location, not its private three-tab projection");
    }

    private static async Task UnifiedPlayback()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("真实原查询", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var session = f.Film.Search.Current!;
        var a = f.Sources.Sources[0]; var b = SearchFixture.Source("b", order: 1);
        var members = new[] { SearchFixture.Member(a, 0, "目标 第一季"), SearchFixture.Member(a, "0", "目标 第一季"), SearchFixture.Member(b, "later", "目标 第二季") };
        f.Runtime.SearchCommand = (_, _) => SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query,
            [SearchFixture.Card("group:default", "site", "目标", members)], members, revision: 2, round: 2);
        f.Runtime.SearchHandoff = input => SearchFixture.Handoff(Guid.Parse(session.Id), FilmJson.Text(input, "selectionId"), session.Query,
            new("group:default", "group", "目标", Members: FilmJson.Encode(new object[]
            { new { sourceKey = a.Key, detailId = (object)0 }, new { sourceKey = a.Key, detailId = (object)"0" }, new { sourceKey = b.Key, detailId = (object)"later" } })),
            [a.Wire, b.Wire]);
        await f.Page.LoadMoreAsync();
        await f.Page.OpenCardAsync(f.Page.Cards.Single()); await Drain();
        var page = f.Film.Playback!;
        True(f.Film.IsPlayback && page.Request.Target.Kind == "group" && page.Request.Target.Members!.Value.GetArrayLength() == 3 &&
            page.Request.Query == "真实原查询" && page.Episodes.Count == 0 && page.Overview.Length == 0 && page.CanPlay,
            "actual receiver keeps all Node members and original query without preview episodes");
        string location = f.Film.CaptureNavigationLocation();
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        await f.Page.OpenCardAsync(f.Page.Cards.Single());
        True(ReferenceEquals(page, f.Film.Playback) && f.Film.CaptureNavigationLocation() == location && f.Runtime.SearchOpens == 1,
            "back and reentry preserve page identity and the same Node search");
    }
    private static async Task UnifiedLatePlayback()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("不同显示名的原词", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.OpenCardAsync(f.Page.Cards.Single());
        var page = f.Film.Playback!; var session = f.Film.Search.Current!;
        string[] originalCards = session.Items.Select(item => item.GetRawText()).ToArray();
        f.Runtime.Respond = (method, input, _) =>
        {
            if (method == "film.session.command")
                return Task.FromResult(FilmSearchConsumerRuntime.Success(FilmPlaybackFixture.Snapshot(f.Runtime.LastSessionId,
                    input.GetProperty("revision").GetInt64() + 2, new("tmdb:tv:7", "tv", "可靠关联", "tmdb", "7"),
                    FilmJson.Encode(new { name = "可靠关联", overview = "迟到完整详情" }))));
            return Task.FromResult(f.Runtime.Handle(method, input));
        };
        f.Runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [], [], revision: 4));
        await SearchFixture.Until(() => page.ActiveTarget.Kind == "tv" && page.Overview == "迟到完整详情");
        True(page.SearchContext!.Query == "不同显示名的原词" && f.Runtime.SearchOpens == 1 &&
            f.Runtime.Calls.Any(call => call.Method == "film.session.command" && call.Input.TryGetProperty("searchReference", out _) &&
                call.Input.GetProperty("revision").GetInt64() == 4) &&
            session.Items.Select(item => item.GetRawText()).SequenceEqual(originalCards),
            "stopped search display stays frozen while the retained first-round navigation forwards only the newer Node reference");
    }
    private static async Task UnifiedRawPlayback()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("原词", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        f.Page.RawListMode = true;
        var context = f.Page.Cards.Single().SearchContext!;
        var source = f.Sources.Sources[0];
        f.Runtime.SearchHandoff = input => SearchFixture.Handoff(Guid.Parse(context.SessionId), FilmJson.Text(input, "selectionId"), context.Query,
            new(context.DisplayKey, "source", "准确单源", Source: FilmJson.Encode(new { key = source.Key, detailId = "same" })), [source.Wire]);
        await f.Page.OpenCardAsync(f.Page.Cards.Single());
        var page = f.Film.Playback!;
        True(page.Request.Target.Kind == "source" && page.Request.Sources!.Count == 1 && page.Request.Metadata is null && page.Overview.Length == 0 &&
            page.SearchContext!.DisplayKey == context.DisplayKey, "raw selection keeps a single exact Node target and no TMDB overview");
    }
    private static async Task UnifiedGenerationPlayback()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("目标", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var old = f.Page.Cards.Single(); await f.Page.OpenCardAsync(old);
        var original = f.Film.Playback!;
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        f.Sources.Sources[0] = SearchFixture.Source("a", generation: 2); f.Sources.Publish();
        Equal(0, f.Page.Cards.Count);
        await f.Page.SubmitAsync("目标", "film", default); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.OpenCardAsync(f.Page.Cards.Single());
        True(!ReferenceEquals(original, f.Film.Playback) && f.Runtime.SearchOpens == 2 && original.SearchContext!.Session.Retired &&
            f.Film.Playback!.Request.Sources![0].GetProperty("generation").GetInt64() == 2,
            "source revocation requires explicit entry and preserves retired navigation identity");
    }
    private static async Task UnifiedCovers()
    {
        var a = SearchFixture.Source("a", order: 17);
        a = a with { Site = a.Site with { Options = new(true, false, true, false) } };
        var b = SearchFixture.Source("b", order: 2);
        await using var f = await UnifiedScene.Create(new(b, a));
        var first = System.Text.Json.Nodes.JsonNode.Parse(SearchFixture.Member(a, 0, "第一源").GetRawText())!.AsObject();
        first["poster"] = "https://fixture.invalid/a.jpg";
        var chosen = System.Text.Json.Nodes.JsonNode.Parse(SearchFixture.Member(b, "0", "封面来源").GetRawText())!.AsObject();
        chosen["poster"] = "https://fixture.invalid/b.jpg";
        var members = new[] { FilmJson.Encode(first), FilmJson.Encode(chosen) };
        var aggregate = System.Text.Json.Nodes.JsonNode.Parse(SearchFixture.Card("opaque:cover", "site", "聚合", members).GetRawText())!.AsObject();
        aggregate["poster"] = "https://fixture.invalid/node-chosen-cover.jpg";
        f.Runtime.SearchOpen = (id, input) =>
        {
            var sources = FilmJson.Array(input, "sources");
            True(sources.Length == 2 && FilmJson.Text(sources[0], "key") == b.Key &&
                sources[0].GetProperty("aggregateImages").GetBoolean() && sources[0].GetProperty("order").GetInt32() == 2 &&
                !sources[1].GetProperty("aggregateImages").GetBoolean() && sources[1].GetProperty("order").GetInt32() == 17 &&
                a.Site.Options == new FilmVideoOptions(true, false, true, false),
                "source order and the existing single cover choice reach Node without changing home or search options");
            return SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [FilmJson.Encode(aggregate)], members);
        };
        await f.Submit("封面", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        Equal("https://fixture.invalid/node-chosen-cover.jpg", f.Page.Cards.Single().Poster);
        f.Page.RawListMode = true;
        True(f.Page.Cards.Select(card => card.Poster).SequenceEqual(["https://fixture.invalid/a.jpg", "https://fixture.invalid/b.jpg"]),
            "raw cards preserve each original source cover rather than reusing the selected aggregate cover");
        f.Page.RawListMode = false;
        Equal("https://fixture.invalid/node-chosen-cover.jpg", f.Page.Cards.Single().Poster);
        Equal(1, f.Runtime.SearchOpens);
        True(!f.Runtime.Calls.Any(call => call.Method.StartsWith("film.data.", StringComparison.Ordinal)),
            "mode changes do not fetch or choose covers on the client");
    }
    private static async Task UnifiedText()
    {
        await using var f = await UnifiedScene.Create();
        const string key = "group:\"opaque\\\\ud800\"";
        var raw = SearchFixture.Member(f.Sources.Sources[0], "0", "原始合法标题🐱");
        var card = System.Text.Json.Nodes.JsonNode.Parse(SearchFixture.Card(key, "site", "替换前", [raw]).GetRawText())!.AsObject();
        card["title"] = new System.Text.Json.Nodes.JsonArray(65, 55296, 66, 56320);
        card["groupKey"] = new System.Text.Json.Nodes.JsonArray(55296);
        card["siteLabel"] = new System.Text.Json.Nodes.JsonArray(32858, 21512, 55296);
        card["textBadge"] = new System.Text.Json.Nodes.JsonArray(55357, 56369);
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [FilmJson.Encode(card)], [raw]);
        await f.Submit("有效原词", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var display = f.Page.Cards.Single(); var context = display.SearchContext!;
        True(display.Title == "A\uFFFDB\uFFFD" && display.Subtitle == "聚合\uFFFD" && display.TextBadge == "🐱" &&
            context.DisplayKey == key && context.Item.GetProperty("title").ValueKind == JsonValueKind.Array &&
            JsonElement.DeepEquals(context.Item.GetProperty("aggregateItems")[0], raw),
            "text projection accepts UInt16 arrays while the opaque identity and complete original item remain untouched");
        f.Runtime.SearchHandoff = input =>
        {
            Equal(key, FilmJson.Text(input, "selectionId"));
            return SearchFixture.Handoff(Guid.Parse(context.SessionId), key, context.Query,
                new(key, "group", "运行器返回目标", Members: FilmJson.Encode(new[] { new { sourceKey = f.Sources.Sources[0].Key, detailId = "0" } })),
                [f.Sources.Sources[0].Wire]);
        };
        await f.Page.OpenCardAsync(display);
        Equal(key, f.Film.Playback!.Request.SearchReference!.Value.GetProperty("selectionId").GetString());
        Equal("有效原词", f.Film.Playback.Request.Query);
    }
    private static async Task UnifiedHandoffRace()
    {
        await using var f = await UnifiedScene.Create();
        var a = f.Sources.Sources[0];
        var members = new[] { SearchFixture.Member(a, "1", "一"), SearchFixture.Member(a, "2", "二") };
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"),
            [SearchFixture.Card("opaque:one", "site", "一", [members[0]]), SearchFixture.Card("opaque:two", "site", "二", [members[1]])], members);
        await f.Submit("竞态", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.Respond = (method, input, token) =>
        {
            if (method == "film.search.handoff")
            {
                var selected = FilmJson.Text(input, "selectionId");
                var handoff = SearchFixture.Handoff(Guid.Parse(FilmJson.Text(input, "sessionId")), selected, "竞态",
                    new(selected, "group", selected, Members: FilmJson.Encode(new[] { new { sourceKey = a.Key, detailId = selected } })), [a.Wire]);
                if (selected == "opaque:one") { entered.TrySetResult(); return release.Task; }
                return Task.FromResult(FilmSearchConsumerRuntime.Success(handoff));
            }
            return Task.FromResult(f.Runtime.Handle(method, input));
        };
        try
        {
            var first = f.Film.ReceiveSearchPlaybackAsync(f.Page.Cards[0]); await entered.Task;
            await f.Film.ReceiveSearchPlaybackAsync(f.Page.Cards[1]);
            release.SetResult(FilmSearchConsumerRuntime.Success(SearchFixture.Handoff(Guid.Parse(f.Film.Search.Current!.Id),
                "opaque:one", "竞态", new("opaque:one", "group", "旧选择", Members: FilmJson.Encode(new[] { new { sourceKey = a.Key, detailId = "1" } })), [a.Wire])));
            try { await first; throw new InvalidOperationException("Superseded handoff published"); }
            catch (OperationCanceledException) { }
            True(f.Film.Playback!.Request.Target.Id == "opaque:two" && f.Film.RetainedSearchPlaybackPages == 1,
                "late first handoff cannot replace the newer selection or retain an unreachable page");
        }
        finally { release.TrySetResult(FilmSearchConsumerRuntime.Failure("CLOSED")); }
    }
    private static async Task UnifiedFailureStatus()
    {
        await using var f = await UnifiedScene.Create();
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [], [], siteState: "failed", hasMore: true);
        await f.Submit("失败", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        Equal("搜索不可用", f.Page.Status); True(f.Page.CanLoadMore && f.Page.HasMore, "Node failed source state remains explicitly retryable");
        var session = f.Film.Search.Current!;
        f.Runtime.SearchCommand = (_, _) => SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [], [], revision: 2);
        await f.Page.LoadMoreAsync(); Equal("无搜索结果", f.Page.Status);
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [], [], noSources: true, metadataState: "unavailable");
        await f.Submit("无源", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        Equal("无可用搜索来源", f.Page.Status); Equal("TMDB 不可用", f.Page.MetadataStatus);
    }
    private static async Task UnifiedPlaybackRelease()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("Q一", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.OpenCardAsync(f.Page.Cards.Single());
        var first = f.Film.Playback!; var session = first.SearchContext!.Session;
        Equal(1, f.Film.RetainedSearchPlaybackPages); Equal(2, session.Subscribers);
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        await f.Submit("Q二", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        Equal(1, session.Subscribers);
        await f.Page.OpenCardAsync(f.Page.Cards.Single());
        await SearchFixture.Until(() => session.Subscribers == 0);
        True(f.Film.RetainedSearchPlaybackPages == 1 && !ReferenceEquals(first, f.Film.Playback), "unreachable page releases its session retainer and observer");
    }
    private static async Task UnifiedPlaybackRetention()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("历史一", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.OpenCardAsync(f.Page.Cards.Single());
        var first = f.Film.Playback!; string location = f.Film.CaptureNavigationLocation();
        await f.Navigate(f.Film, FilmCapability.HomeRoute);
        await f.Submit("历史二", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.OpenCardAsync(f.Page.Cards.Single());
        True(f.Film.RetainedSearchPlaybackPages == 2 && first.SearchContext!.Session.Subscribers == 1, "history keeps exact old page alive");
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        f.Click("BackButton"); await SearchFixture.Until(() => f.Film.IsHome && f.Status == "");
        f.Click("BackButton"); await SearchFixture.Until(() => ReferenceEquals(f.Film.Playback, first) && f.Film.IsPlayback && f.Status == "");
        True(f.Film.CaptureNavigationLocation() == location && first.SearchContext!.Query == "历史一", "back restores opaque navigation identity and the original selection");
    }

    private static async Task UnifiedMusicNavigation()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("合成", "music");
        await SearchUntil(() => !f.Music.Module.SearchPlaylistsLoading && f.Music.Module.CatalogSections.Any(section => section.Id == "catalog-search:playlists"));
        var card = f.Music.Module.CatalogSections.SelectMany(section => section.Items).First(item => item.Kind == "playlist");
        int calls = f.Music.Http.SearchRequests;
        await f.Music.Module.OpenCatalogCardAsync(card);
        await SearchFixture.Until(() => f.Music.Module.IsCatalogPlaylist && f.Status == "");
        True(f.Page.View.Parent is null, "projected owner can navigate to its ordinary dynamic routes");
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        True(f.Music.Module.IsSearchOverview && f.Page.SelectedTab == "overview" && f.Music.Http.SearchRequests == calls,
            "back restores the same comprehensive music snapshot without duplicate search");
    }

    private static async Task UnifiedRapidTabs()
    {
        await using var f = await UnifiedScene.Create();
        f.Music.Http.Hold = true;
        await f.Submit("合成", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var musicTab = f.Page.OpenTabAsync("songs");
        await f.Music.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Page.OpenTabAsync("film");
        f.Music.Http.Release.TrySetResult(); await musicTab;
        Equal("film", f.Page.SelectedTab);
        await Drain();
        Equal("film", f.Picker.SelectedValue as string);
        await f.Navigate(f.Film, FilmCapability.HomeRoute); await Drain();
        True(f.Page.View.Parent is null && f.Music.Module.View.Parent is null,
            "deactivation releases the music visual and stale work cannot reparent it");
        Equal(1, f.Runtime.SearchOpens);
    }

    private static async Task UnifiedStopNewQuery()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("旧词", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var original = f.Film.Search.Current!;
        int requests = f.Music.Http.SearchRequests;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.Respond = async (method, input, _) =>
        {
            if (method == "film.search.command" && FilmJson.Text(input, "action") == "stop" &&
                FilmJson.Text(input, "sessionId") == original.Id)
            { entered.TrySetResult(); await release.Task; }
            return f.Runtime.Handle(method, input);
        };
        try
        {
            var pending = f.Page.OpenTabAsync("songs");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            True(!pending.IsCompleted && f.Page.SelectedTab == "songs" && f.Page.SelectedTarget == "music",
                "the requested tab and target publish before the film stop receipt");
            await Drain();
            Equal("music", f.Picker.SelectedValue as string);
            await f.Submit("新词", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
            var latest = f.Film.Search.Current!;
            release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); await Drain();
            True(f.Page.Query == "新词" && f.Page.SelectedTab == "film" && f.Page.SelectedTarget == "film" &&
                ReferenceEquals(latest, f.Film.Search.Current) && !ReferenceEquals(original, latest) &&
                f.Page.Cards.Single().SearchContext!.SessionId == latest.Id && f.Page.Error.Length == 0,
                "the old stop completion cannot replace the newer query target session cards or error");
            Equal("film", f.Picker.SelectedValue as string);
            Equal(2, f.Runtime.SearchOpens); Equal(requests, f.Music.Http.SearchRequests);
            True(f.Page.View.Parent is not null && f.Music.Module.View.Parent is null,
                "the newer film presentation stays mounted without an obsolete music search");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task UnifiedStopTabs()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("同词", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var original = f.Film.Search.Current!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.Respond = async (method, input, _) =>
        {
            if (method == "film.search.command" && FilmJson.Text(input, "action") == "stop" &&
                FilmJson.Text(input, "sessionId") == original.Id)
            { entered.TrySetResult(); await release.Task; }
            return f.Runtime.Handle(method, input);
        };
        try
        {
            var pending = f.Page.OpenTabAsync("songs");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Page.OpenTabAsync("playlists").WaitAsync(TimeSpan.FromSeconds(10));
            Equal("playlists", f.Page.SelectedTab); Equal("music", f.Picker.SelectedValue as string);
            Equal(1, f.Runtime.Calls.Count(call => call.Method == "film.search.command" && FilmJson.Text(call.Input, "action") == "stop"));
            await SearchUntil(() => !f.Music.Module.SearchSongsLoading && !f.Music.Module.SearchPlaylistsLoading);
            int requests = f.Music.Http.SearchRequests;
            await f.Page.OpenTabAsync("film");
            True(!pending.IsCompleted && f.Page.SelectedTab == "film" && ReferenceEquals(original, f.Film.Search.Current),
                "a later film tab is accepted while the earlier stop still waits");
            release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await SearchFixture.Settle(original); await Drain();
            Equal("film", f.Page.SelectedTab); Equal("film", f.Picker.SelectedValue as string);
            True(f.Page.Query == "同词" && f.Page.Cards.Single().SearchContext!.SessionId == original.Id &&
                ReferenceEquals(original, f.Film.Search.Current) && f.Music.Module.View.Parent is null,
                "late stop completion preserves the latest tab and the retained successful search");
            Equal(1, f.Runtime.SearchOpens); Equal(requests, f.Music.Http.SearchRequests);
            Equal(1, f.Runtime.Calls.Count(call => call.Method == "film.search.command" && FilmJson.Text(call.Input, "action") == "resume"));
        }
        finally { release.TrySetResult(); }
    }

    private static async Task UnifiedStopExit()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("离页", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        var original = f.Film.Search.Current!;
        int requests = f.Music.Http.SearchRequests;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.Respond = async (method, input, _) =>
        {
            if (method == "film.search.command" && FilmJson.Text(input, "action") == "stop" &&
                FilmJson.Text(input, "sessionId") == original.Id)
            { entered.TrySetResult(); await release.Task; }
            return f.Runtime.Handle(method, input);
        };
        try
        {
            var pending = f.Page.OpenTabAsync("songs");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await f.Navigate(f.Film, FilmCapability.HomeRoute);
            string departedTab = f.Page.SelectedTab, departedTarget = f.Page.SelectedTarget;
            release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); await Drain();
            True(f.Film.IsHome && f.Page.View.Parent is null && f.Music.Module.View.Parent is null,
                "the departed search and music visual cannot be remounted by the old stop completion");
            Equal(departedTab, f.Page.SelectedTab); Equal(departedTarget, f.Page.SelectedTarget);
            Equal("film", f.Picker.SelectedValue as string);
            Equal(1, f.Runtime.SearchOpens); Equal(requests, f.Music.Http.SearchRequests);
            True(!f.Runtime.Calls.Any(call => call.Method == "film.search.command" && FilmJson.Text(call.Input, "action") == "resume"),
                "page exit cannot restart the retained film search");
        }
        finally { release.TrySetResult(); }
    }

    private sealed class SearchHome : IFilmHomeSource
    {
        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(FilmSectionDefinition section, int start, int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FilmCardItem>>([]);
        public void Dispose() { }
    }
    private sealed class UnifiedScene : IAsyncDisposable
    {
        internal readonly SearchScene Music;
        internal readonly SearchFixture Search;
        internal SearchSources Sources => Search.Sources;
        internal readonly FilmCapability Film;
        internal readonly SearchResultsPage Page;
        internal readonly NovelCapability Novel;
        private readonly NovelRuleRuntimeFixture novelRules = new();
        internal readonly ShellWindow Shell;
        internal FilmSearchConsumerRuntime Runtime => Search.Runtime;
        internal TextBox Input => (TextBox)Shell.FindName("SearchBox");
        internal ComboBox Picker => (ComboBox)Shell.FindName("SearchTargetPicker");
        internal string Status => ((TextBlock)Shell.FindName("StatusText")).Text;
        internal ContentControl? SearchHost => (Shell.FindName("ContentDeck") as Grid)!.Children.OfType<ContentControl>()
            .SingleOrDefault(host => ReferenceEquals(host.Content, Page.View));
        private UnifiedScene(SearchScene music, SearchFixture search, FilmMetadataService? metadata, NovelTests.Runtime? novelRuntime)
        {
            Music = music; Search = search;
            Film = new(new SearchHome(), search.Service, new FilmPlaybackSession(Runtime.CaptureConnectionAsync, new FilmNativeProbe()),
                new FilmLibrary(new FilmMemoryStore()), metadata);
            Novel = new(new NovelSourceService(novelRuntime ?? new NovelTests.Runtime()), new NovelLibrary(new NovelTests.MemoryStore()),
                new NovelRuleProgram(novelRules));
            Page = new(music.Module, Film, Novel);
            Shell = new ShellWindow([Music.Module, Film, Novel], [Page], async () =>
            { Page.Dispose(); await Novel.DisposeAsync(); await Film.DisposeAsync(); await Music.DisposeAsync(); }, () => Task.CompletedTask);
            Page.NavigationToken = () => Shell.GetPageNavigationToken(Page, Film.Id, FilmCapability.PlaybackRoute);
            Page.NavigateToPlaybackAsync = async (item, token) =>
            {
                token.ThrowIfCancellationRequested(); await Film.ReceiveSearchPlaybackAsync(item, token);
                await Shell.NavigateFromPageAsync(Page, Film.Id, FilmCapability.PlaybackRoute);
            };
            Page.NovelNavigationToken = () => Shell.GetPageNavigationToken(Page, Novel.Id, NovelCapability.BookRoute);
            Page.NavigateToNovelAsync = async (item, token) =>
            {
                await Novel.ReceiveBookAsync(item, token);
                await Shell.NavigateFromPageAsync(Page, Novel.Id, NovelCapability.BookRoute);
            };
        }
        internal static async Task<UnifiedScene> Create(SearchSources? sources = null, FilmMetadataService? metadata = null,
            FilmSearchConsumerRuntime? runtime = null, NovelTests.Runtime? novelRuntime = null)
        {
            var music = await SearchScene.Create(scriptRuntime: new QualityFixture.ScriptRuntime());
            await music.Policy(["wy"], ["wy"]);
            var f = new UnifiedScene(music, new SearchFixture(sources, runtime), metadata, novelRuntime);
            try
            {
                // Mount without a default request, then use the actual native library route.
                f.Shell.PrepareForRender();
                await f.Navigate(music.Module, "favorites");
                f.Shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await SearchFixture.Until(() => music.Module.CaptureNavigationLocation() == "favorites" && f.Status == "");
                await Drain(); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal void Click(string name) => ((Button)Shell.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        internal async Task Navigate(IDesktopModule module, string route)
        {
            var list = new[] { "NavigationList", "CollectionNavigationList" }.Select(name => (ListBox)Shell.FindName(name))
                .Single(list => list.Items.OfType<ShellNavigationEntry>().Any(item => ReferenceEquals(item.Owner.Module, module) && item.Route == route));
            list.SelectedItem = list.Items.OfType<ShellNavigationEntry>().Single(item => ReferenceEquals(item.Owner.Module, module) && item.Route == route);
            await SearchFixture.Until(() => Status == "" && list.SelectedItem is ShellNavigationEntry selected && selected.Route == route);
            await Drain();
        }
        internal async Task Submit(string word, string target)
        {
            Input.Text = word; Picker.SelectedValue = target; Click("SearchButton");
            await SearchFixture.Until(() => Page.Query == word && Page.SelectedTarget == target && Status == "");
            await Drain();
        }
        public async ValueTask DisposeAsync()
        {
            Music.Http.Release.TrySetResult();
            Shell.Close(); await Shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            Page.Dispose(); await Novel.DisposeAsync(); await novelRules.DisposeAsync(); await Film.DisposeAsync();
            await Search.DisposeAsync();
        }
    }
}

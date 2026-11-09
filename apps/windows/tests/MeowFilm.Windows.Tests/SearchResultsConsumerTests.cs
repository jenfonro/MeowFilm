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
        yield return Case("unified search unchanged display after generation replacement opens the new identity", UnifiedGenerationPlayback);
        yield return Case("unified search page distinguishes all-source failure from successful empty results", UnifiedFailureStatus);
        yield return Case("unified search unreachable playback releases its cache and session observer", UnifiedPlaybackRelease);
        yield return Case("unified search playback restores retained history origin and current identities", UnifiedPlaybackRetention);
        yield return Case("unified search music card navigation leaves the projected view and back restores it", UnifiedMusicNavigation);
        yield return Case("unified search rapid tab changes and page exit cannot remount stale results", UnifiedRapidTabs);
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
        Equal(0, search.Sources.Calls.Count); True(search.Service.Current is null, "music result kinds never start film");
    }

    private static async Task UnifiedTargets()
    {
        await using var f = await UnifiedScene.Create();
        Equal("music", f.Picker.SelectedValue as string);
        await f.Navigate(f.Film, FilmCapability.HomeRoute);
        Equal("film", f.Picker.SelectedValue as string);
        f.Input.Text = "保留草稿";
        f.Picker.SelectedValue = "music";
        int calls = f.Sources.Calls.Count;
        await f.Film.NavigateAsync(FilmCapability.HomeRoute, default); await Drain();
        Equal("保留草稿", f.Input.Text); Equal("music", f.Picker.SelectedValue as string);
        Equal(calls, f.Sources.Calls.Count);
        await f.Navigate(f.Music.Module, "favorites");
        Equal("music", f.Picker.SelectedValue as string);
        await f.Submit("原词", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        Equal("film", f.Page.SelectedTab); Equal("film", f.Picker.SelectedValue as string);
        f.Picker.SelectedValue = "music"; f.Input.Text = "未提交的输入";
        f.Sources.Publish(); await Drain();
        Equal("music", f.Picker.SelectedValue as string); Equal("未提交的输入", f.Input.Text);
        Equal(1, f.Sources.Calls.Count);
        await f.Page.OpenTabAsync("songs"); Equal("music", f.Picker.SelectedValue as string);
        await f.Page.OpenTabAsync("playlists"); Equal("music", f.Picker.SelectedValue as string);
        await f.Page.OpenTabAsync("film"); await Drain(); Equal("film", f.Picker.SelectedValue as string);
        Equal(1, f.Sources.Calls.Count);
        f.Click("BackButton"); await SearchFixture.Until(() => f.Music.Module.CaptureNavigationLocation() == "favorites" && f.Status == "");
        True(!f.Music.Module.IsSearchPage, "leaving public search restores the original module location, not its private three-tab projection");
    }

    private static async Task UnifiedPlayback()
    {
        var sources = new SearchSources(SearchFixture.Source("a"), SearchFixture.Source("b", order: 1));
        sources.Read = (source, _, page, _) => Task.FromResult(SearchFixture.Page(source.Cat.InstallationId == "a" ? 2 : 1,
            (page == 1 ? "same" : "later", "目标 第" + page + "季", "更新至" + page + "集")));
        await using var f = await UnifiedScene.Create(sources);
        await f.Submit("真实原查询", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.LoadMoreAsync();
        var card = f.Page.Cards.Single(); var context = card.SearchContext!;
        Equal(3, context.Sources.Count);
        True(context.Sources.Select(item => item.GetProperty("installationId").GetString()).Distinct().Count() == 2,
            "same site and detail IDs in different installations remain distinct members");
        await f.Page.OpenCardAsync(card); await Drain();
        var page = f.Film.Playback!;
        var received = page.SearchContext ?? throw new InvalidOperationException("Missing playback search context");
        True(f.Film.IsPlayback && ReferenceEquals(received, context), "real FilmCapability receiver retains the original search context");
        True(received.Query == "真实原查询" && page.Title != received.Query &&
            page.Episodes.Count == 0 && page.SelectedEpisode is null && page.Recommendations.Count == 0 && page.Overview.Length == 0,
            "no static preview story episodes or recommendations are fabricated for search");
        var playback = (FilmPlaybackView)((FilmView)f.Film.View).FindName("PlaybackView");
        Equal(0, ((ItemsControl)playback.FindName("EpisodeCards")).Items.Count);
        Equal(Visibility.Collapsed, ((Button)playback.FindName("OverviewToggle")).Visibility);
        True(!((Button)playback.FindName("PlayButton")).IsEnabled, "the actual playback surface keeps missing content unavailable");
        string location = f.Film.CaptureNavigationLocation();
        int requests = sources.Calls.Count;
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        Equal("真实原查询", f.Input.Text);
        f.Input.Text = "未提交的下一词";
        await f.Page.OpenCardAsync(f.Page.Cards.Single());
        True(ReferenceEquals(page, f.Film.Playback) && sources.Calls.Count == requests && f.Film.CaptureNavigationLocation() == location,
            "back and reentry preserve page/session identity without another search");
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        Equal("未提交的下一词", f.Input.Text);
        Equal("真实原查询", f.Page.Query);
    }

    private static async Task UnifiedLatePlayback()
    {
        var second = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tmdb = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new SearchSources(SearchFixture.Source("a"), SearchFixture.Source("b", order: 1))
        { Read = (source, _, _, _) => source.Cat.InstallationId == "a" ? Task.FromResult(SearchFixture.Page(1, ("same", "目标 第一季", "更新至3集"))) : second.Task };
        var metadata = new SearchMetadata { ReadSearch = (_, _) => tmdb.Task };
        await using var f = await UnifiedScene.Create(sources, metadata);
        try
        {
            await f.Submit("不同显示名的原词", "film");
            await SearchFixture.Until(() => f.Page.Cards.Count == 1);
            await f.Page.OpenCardAsync(f.Page.Cards.Single());
            var receiver = f.Film.Playback!;
            var context = receiver.SearchContext ?? throw new InvalidOperationException("Missing in-flight search context");
            True(f.Film.IsPlayback && context.Session.Loading, "in-flight click reaches the actual playback route");
            second.TrySetResult(SearchFixture.Page(1, ("same", "目标 第二季", "更新至5集")));
            tmdb.TrySetResult(SearchFixture.Tmdb("tv", 7, "目标", "搜索已得简介"));
            await SearchFixture.Settle(context.Session);
            True(context.Sources.Count == 2 && context.Query == "不同显示名的原词" &&
                context.Metadata!.Value.GetProperty("overview").GetString() == "详情tv" && receiver.Overview == "详情tv",
                "existing playback object follows a late TMDB carrier and receives its complete metadata");
            True(receiver.Episodes.Count == 0 && metadata.Searches.Count == 1 && sources.Calls.Count == 2,
                "late receipt neither fabricates episodes nor searches to reconstruct the group");
        }
        finally { second.TrySetResult(SearchFixture.Page(1)); tmdb.TrySetResult(SearchFixture.Tmdb("tv", 7, "目标", "")); }
    }

    private static async Task UnifiedRawPlayback()
    {
        var sources = new SearchSources(SearchFixture.Source("a"), SearchFixture.Source("b", order: 1))
        { Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(1, ("same", "目标 第一季", "更新至3集"))) };
        var metadata = new SearchMetadata();
        await using var f = await UnifiedScene.Create(sources, metadata);
        await f.Submit("原单源查询", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        True(f.Page.Cards.Single().SearchContext!.Sources.Count == 2 &&
            f.Page.Cards.Single().SearchContext!.Metadata is not null, "aggregate has both sources and real acquired metadata");
        f.Page.RawListMode = true;
        var raw = f.Page.Cards.Single(card => SearchFixture.Text(card.SearchContext!.Item, "installationId") == "a");
        var context = raw.SearchContext!;
        string original = context.Item.GetRawText();
        await f.Page.OpenCardAsync(raw); await Drain();
        var page = f.Film.Playback!;
        True(ReferenceEquals(page.SearchContext, context) && context.Query == "原单源查询" &&
            context.Item.GetRawText() == original && context.CurrentItem.GetRawText() == original &&
            context.Sources.Count == 1 && SearchFixture.Text(context.Sources.Single(), "installationId") == "a" &&
            FilmSearchJson.Number(context.Sources.Single(), "generation") == 1 && context.Metadata is null,
            "real receiver keeps the clicked raw identity without widening to its aggregate");
        True(page.Overview == "" && !page.HasOverview && page.Episodes.Count == 0, "raw playback does not invent a TMDB or preview overview");
        var playback = (FilmPlaybackView)((FilmView)f.Film.View).FindName("PlaybackView");
        Equal(Visibility.Collapsed, ((Button)playback.FindName("OverviewToggle")).Visibility);
        Equal(1, metadata.Searches.Count); Equal(1, metadata.Details.Count); Equal(2, sources.Calls.Count);
    }

    private static async Task UnifiedGenerationPlayback()
    {
        foreach (bool matched in new[] { false, true })
        {
            var sources = new SearchSources(SearchFixture.Source("a"))
            { Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(1, ("same", matched ? "目标 第一季" : "站源影片", "已完结"))) };
            var metadata = new SearchMetadata();
            await using var f = await UnifiedScene.Create(sources, metadata);
            await f.Submit("同词恢复", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
            var first = f.Page.Cards.Single(card => card.SearchContext!.Sources.Count == 1);
            await f.Page.OpenCardAsync(first); await Drain();
            var oldPage = f.Film.Playback!; var oldContext = oldPage.SearchContext!;
            string oldItem = oldContext.Item.GetRawText(), oldLocation = f.Film.CaptureNavigationLocation();
            if (matched) await f.Navigate(f.Film, FilmCapability.HomeRoute);
            sources.Sources[0] = SearchFixture.Source("a", generation: 2); sources.Publish();
            if (matched) await f.Submit("同词恢复", "film");
            else { f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == ""); }
            await SearchFixture.Settle(f.Film.Search.Current!);
            var current = f.Page.Cards.Single(card => card.SearchContext!.Sources.Count == 1);
            True(first.Id == current.Id && first.Title == current.Title && first.Poster == current.Poster &&
                first.TextBadge == current.TextBadge && first.Subtitle == current.Subtitle &&
                first.SearchContext!.DisplayKey == current.SearchContext!.DisplayKey,
                "all display fields and display identity remain unchanged across source generations");
            True(!ReferenceEquals(first, current) && FilmSearchJson.Number(current.SearchContext!.Sources.Single(), "generation") == 2,
                "actual result page replaces an identical-looking card with its new source context");
            await f.Page.OpenCardAsync(current); await Drain();
            var received = f.Film.Playback!;
            True(!ReferenceEquals(oldPage, received) && ReferenceEquals(received.SearchContext, current.SearchContext) &&
                FilmSearchJson.Number(received.SearchContext!.Sources.Single(), "generation") == 2 &&
                f.Film.CaptureNavigationLocation() != oldLocation,
                "the real capability creates a distinct navigation identity for the new selection");
            True(oldContext.Item.GetRawText() == oldItem && oldContext.CurrentItem.GetRawText() == oldItem &&
                FilmSearchJson.Number(oldContext.Sources.Single(), "generation") == 1,
                "the previous site or TMDB detail keeps its original source generation rather than being reidentified");
            if (matched)
            {
                f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
                f.Click("BackButton"); await SearchFixture.Until(() => f.Film.IsHome && f.Status == "");
                f.Click("BackButton"); await SearchFixture.Until(() => ReferenceEquals(f.Film.Playback, oldPage) && f.Film.IsPlayback && f.Status == "");
                True(f.Film.CaptureNavigationLocation() == oldLocation && FilmSearchJson.Number(oldContext.Sources.Single(), "generation") == 1,
                    "actual history restores the old generation page without overwriting it with the new selection");
            }
            Equal(1, metadata.Searches.Count); Equal(1, metadata.Details.Count); Equal(2, sources.Calls.Count);
        }
    }

    private static async Task UnifiedFailureStatus()
    {
        var sources = new SearchSources(SearchFixture.Source("a"), SearchFixture.Source("b", order: 1))
        { Read = (_, _, _, _) => throw new InvalidOperationException("fixture failure") };
        var metadata = new SearchMetadata { ReadSearch = (_, _) => Task.FromResult(SearchFixture.Json(new { results = Array.Empty<object>() })) };
        await using var f = await UnifiedScene.Create(sources, metadata);
        await f.Submit("失败查询", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        Equal("搜索不可用", f.Page.Status); Equal("", f.Page.Error);
        True(f.Page.Cards.Count == 0 && f.Page.CanLoadMore && f.Page.HasMore &&
            f.Film.Search.Current!.MetadataState == FilmSearchChannelState.Completed && sources.Calls.Count == 2,
            "successful empty TMDB does not turn all-source failure into a successful empty page or auto retry");
        sources.Read = (source, _, _, _) => source.Cat.InstallationId == "a"
            ? Task.FromResult(SearchFixture.Page(1, ("ok", "成功影片", "已完结")))
            : throw new InvalidOperationException("fixture failure");
        await f.Page.LoadMoreAsync();
        Equal("", f.Page.Status); Equal("", f.Page.Error); Equal(1, f.Page.Cards.Count); Equal(4, sources.Calls.Count);
        True(f.Page.CanLoadMore, "one failed source remains silently retryable through explicit more");
        sources.Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(1));
        await f.Submit("成功空查询", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        Equal("无搜索结果", f.Page.Status); Equal(0, f.Page.Cards.Count);
    }

    private static async Task UnifiedPlaybackRelease()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("Q一", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.OpenCardAsync(f.Page.Cards.Single()); await Drain();
        var first = f.Film.Playback!; var session = first.SearchContext!.Session;
        Equal(1, f.Film.RetainedSearchPlaybackPages); Equal(2, session.Subscribers);
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        await f.Page.OpenCardAsync(f.Page.Cards.Single()); await Drain();
        True(ReferenceEquals(first, f.Film.Playback), "the selected page remains available for same-result reentry");
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        await f.Submit("Q二", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        True(ReferenceEquals(first, f.Film.Playback) && session.Subscribers == 1,
            "retired service releases its observer while the selected playback still owns its page");
        await f.Page.OpenCardAsync(f.Page.Cards.Single()); await Drain();
        True(!ReferenceEquals(first, f.Film.Playback) && f.Film.Playback!.SearchContext!.Query == "Q二" &&
            f.Film.RetainedSearchPlaybackPages == 1 && session.Subscribers == 0,
            "replacing the current page releases an unreachable old query cache and its session observer");
        Console.WriteLine($"EVIDENCE film-search unreachable playback retained={f.Film.RetainedSearchPlaybackPages} retired-observers={session.Subscribers}");
    }

    private static async Task UnifiedPlaybackRetention()
    {
        await using var f = await UnifiedScene.Create();
        await f.Submit("历史一", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.OpenCardAsync(f.Page.Cards.Single()); await Drain();
        var first = f.Film.Playback!; string firstLocation = f.Film.CaptureNavigationLocation();
        await f.Navigate(f.Film, FilmCapability.HomeRoute);
        await f.Submit("历史二", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.OpenCardAsync(f.Page.Cards.Single()); await Drain();
        var second = f.Film.Playback!;
        True(f.Film.RetainedSearchPlaybackPages == 2 && first.SearchContext!.Session.Subscribers == 1,
            "an old query still reachable through actual shell history retains its page and observer");
        await f.Submit("来源三", "film"); await SearchFixture.Settle(f.Film.Search.Current!);
        await f.Page.OpenCardAsync(f.Page.Cards.Single()); await Drain();
        var third = f.Film.Playback!;
        f.Click("BackButton"); await SearchFixture.Until(() => f.SearchHost is not null && f.Status == "");
        f.Click("BackButton"); await SearchFixture.Until(() => ReferenceEquals(f.Film.Playback, second) && f.Status == "");
        True(second.SearchContext!.Query == "历史二" && f.Film.RetainedSearchPlaybackPages == 2 &&
            third.SearchContext!.Session.Subscribers == 1,
            "exiting search restores the exact origin page and releases its now-unreachable child observer");
        f.Click("BackButton"); await SearchFixture.Until(() => f.Film.IsHome && f.Status == "");
        f.Click("BackButton"); await SearchFixture.Until(() => ReferenceEquals(f.Film.Playback, first) && f.Film.IsPlayback && f.Status == "");
        True(f.Film.CaptureNavigationLocation() == firstLocation && first.SearchContext!.Query == "历史一" &&
            f.Film.RetainedSearchPlaybackPages == 1 && second.SearchContext!.Session.Subscribers == 0,
            "opaque history restores the original query object while an unreferenced popped page is released");
        Console.WriteLine($"EVIDENCE film-search navigation retention retained={f.Film.RetainedSearchPlaybackPages} history-observers={first.SearchContext!.Session.Subscribers} popped-observers={second.SearchContext!.Session.Subscribers}");
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
        Equal(1, f.Sources.Calls.Count);
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
        internal readonly ShellWindow Shell;
        internal TextBox Input => (TextBox)Shell.FindName("SearchBox");
        internal ComboBox Picker => (ComboBox)Shell.FindName("SearchTargetPicker");
        internal string Status => ((TextBlock)Shell.FindName("StatusText")).Text;
        internal ContentControl? SearchHost => (Shell.FindName("ContentDeck") as Grid)!.Children.OfType<ContentControl>()
            .SingleOrDefault(host => ReferenceEquals(host.Content, Page.View));
        private UnifiedScene(SearchScene music, SearchFixture search, FilmMetadataService? metadata)
        {
            Music = music; Search = search; Film = new(new SearchHome(), search.Service, metadata); Page = new(music.Module, Film);
            Shell = new ShellWindow([Music.Module, Film], [Page], async () =>
            { Page.Dispose(); await Film.DisposeAsync(); await Music.DisposeAsync(); }, () => Task.CompletedTask);
            Page.NavigationToken = () => Shell.GetPageNavigationToken(Page, Film.Id, FilmCapability.PlaybackRoute);
            Page.NavigateToPlaybackAsync = async (item, token) =>
            {
                token.ThrowIfCancellationRequested(); Film.ReceiveSearchPlayback(item);
                await Shell.NavigateFromPageAsync(Page, Film.Id, FilmCapability.PlaybackRoute);
            };
        }
        internal static async Task<UnifiedScene> Create(SearchSources? sources = null, IFilmSearchMetadata? metadata = null)
        {
            var music = await SearchScene.Create(scriptRuntime: new QualityFixture.ScriptRuntime());
            await music.Policy(["wy"], ["wy"]);
            var f = new UnifiedScene(music, new SearchFixture(sources ?? new(SearchFixture.Source("a"))
                { Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(1, ("1", "站源影片", "已完结"))) }, metadata),
                metadata as FilmMetadataService);
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
            Page.Dispose(); await Film.DisposeAsync();
        }
    }
}

using Windows.UI;
using Windows.System;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI;
using Windows.UI.Text;
using MeowFilm.Desktop;
using Window = MeowFilm.Desktop.DesktopWindow;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class FilmPlaybackTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-playback: unseeded route is empty and explicit wire cards preserve movie or series identity",
            () => TestUiHost.InvokeAsync(ExplicitData));
        yield return new("film-playback: episode buttons and native layout controls retain one selection",
            () => TestUiHost.InvokeAsync(EpisodeInteraction));
        yield return new("film-playback: unavailable player and responsive page keep scrolling in each region",
            () => TestUiHost.InvokeAsync(ResponsivePlayer));
        yield return new("film-playback: recommendations and navigation restore the original page directory and browsing state",
            () => TestUiHost.InvokeAsync(NavigationRecovery));
        yield return new("film-playback: a captured recommendation opens its exact identity after the displayed recommendation list changes",
            () => TestUiHost.InvokeAsync(StaleRecommendation));
        yield return new("film-playback: flat tiles mark one current episode and keep system outlines in high contrast",
            () => TestUiHost.InvokeAsync(FlatTiles));
        yield return new("film-playback: hover or focus lifts episode cards rows and recommendations around a centered play mark",
            () => TestUiHost.InvokeAsync(HoverLift));
        yield return new("film-playback: normal poster cards retain their crop while metadata uses the shared canvas",
            () => TestUiHost.InvokeAsync(PosterCards));
        yield return new("film-playback: centered hero actions allow retry and persist each film favorite",
            () => TestUiHost.InvokeAsync(HeroActions));
        yield return new("film-playback: text stays readable on the default and custom application canvas",
            () => TestUiHost.InvokeAsync(SharedCanvasReadability));
        yield return new("film-playback: the shared canvas follows global color rather than scrolling or film posters",
            () => TestUiHost.InvokeAsync(SharedCanvasNavigation));
    }

    private static async Task ExplicitData()
    {
        await using var scene = new Scene();
        var module = scene.Module;
        await module.InitializeAsync(default);
        await module.NavigateAsync(FilmCapability.PlaybackRoute, default);
        await Drain();
        Check(FilmCapability.PlaybackRoute == "film-playback" && module.IsPlayback && module.Heading == "播放页" &&
            !module.HasPlayback && module.Playback is null,
            "an unseeded direct route never invents a default film");
        Check(!FilmUiTest.Visible(scene.Playback) && FilmUiTest.Visible(Named<TextBlock>(scene.View, "PlaybackEmpty")),
            "the unseeded route renders its real empty state, not an unbound playback layout");
        Check(scene.Source.Calls == 0 && scene.Runtime.Calls.Count == 0,
            "an unseeded route performs no business request");
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        int calls = scene.Source.Calls;
        Check(calls > 0 && module.Sections.All(section => section.Items.Count == 12),
            "the explicitly isolated home fixture supplies its full requested range");
        var homeCards = module.Sections.Select(section => section.Items[0]).ToArray();
        foreach (string key in new[] { "movie", "tv" })
        {
            calls = scene.Source.Calls;
            var item = module.Sections.Single(section => section.Key == key).Items[0];
            await module.OpenPlaybackAsync(item);
            await Drain();
            var page = scene.Page;
            Check(page.Item.Id == item.Id && page.Request.Target.Id == item.PlaybackContext!.Target.Id &&
                page.Title == item.Title && page.Poster == item.Poster,
                "opening a card preserves exact identity, title and poster");
            CheckData(page);
            Check(key == "movie" ? page.Episodes.Count == 0 && page.Candidates.Count == 1 && page.SelectedEpisode is null :
                page.Episodes.Count == 12 && page.SelectedEpisode is { Number: 1 },
                "a movie uses real file candidates; series use actual wire episodes");
            Check(scene.Source.Calls == calls && !scene.Runtime.Calls.Any(call => call.Method.StartsWith("film.data.", StringComparison.Ordinal)),
                "the native consumer does not repeat metadata or detail loading");
            Check(ReferenceEquals(scene.Playback.DataContext, page) && FilmUiTest.Visible(Named<Grid>(scene.View, "PlaybackPage")),
                "FilmView binds the seeded playback page");
            var playerOverlay = Named<Grid>(scene.Playback, "PlayerOverlay");
            var shortVideo = Named<CheckBox>(scene.Playback, "ShortVideoButton");
            Check(Visuals(scene.Playback).OfType<FrameworkElement>()
                    .Where(element => !DesktopVisual.Contains(playerOverlay, element) && !ReferenceEquals(element, shortVideo))
                    .All(element => ToolTipService.GetToolTip(element) is null),
                "film titles and descriptions remain free of hover hints; picture icon controls and prefetch state have their own labels");
            Check(((IDesktopHeaderSource)scene.View).Header is
                { Title: "播放页", SearchVisible: true, SearchEnabled: true, SearchTargetId: "film" },
                "the shell receives the playback heading and retains the film search entry");
            await module.RestoreNavigationAsync(FilmCapability.HomeRoute, default);
            Check(scene.Source.Calls == calls + 4,
                "home restoration checks every section through its owner rather than treating the display snapshot as fresh");
        }
        Check(module.Sections.Select(section => section.Items[0]).Zip(homeCards)
            .All(pair => ReferenceEquals(pair.First, pair.Second)) && scene.Native.Commits == 0,
            "unchanged owner responses retain the original home cards and fixtures do not pretend to play media");
    }

    private static async Task EpisodeInteraction()
    {
        await using var scene = new Scene();
        await scene.OpenAsync();
        await Drain();
        var page = scene.Page;
        var view = scene.Playback;
        var cards = EpisodeItems(view);
        var list = Named<ItemsControl>(view, "EpisodeList");
        var shelf = Named<Shelf>(view, "EpisodeShelf");
        var rail = shelf.ScrollHost!;
        var directory = page.EpisodeDirectory ?? throw new InvalidOperationException("The episode page has no directory.");
        var presentation = directory.Presentation;
        int shelfUnloads = 0;
        shelf.Unloaded += (_, _) => shelfUnloads++;
        Check(page.Episodes.Count > 1 && page.IsHorizontalLayout && !page.IsListLayout &&
            FilmUiTest.Visible(rail) && !FilmUiTest.Visible(list), "the explicit TMDB snapshot starts with horizontal episode cards");
        await CheckEpisodeContent(cards, page);
        var chosen = page.Episodes[1];
        Click(await EpisodeButton(cards, chosen));
        await Drain();
        CheckSelection(scene, chosen);

        Click(Named<ToggleButton>(view, "ListLayout"));
        await Drain();
        Check(page.IsListLayout && !page.IsHorizontalLayout && FilmUiTest.Visible(list) && !FilmUiTest.Visible(rail),
            "the list button switches the actual episode presentation");
        CheckSelection(scene, chosen);
        await CheckEpisodeContent(list, page);
        var last = page.Episodes[^1];
        Click(await EpisodeButton(list, last));
        await Drain();
        CheckSelection(scene, last);

        InvokeAction(Named<ToggleButton>(view, "HorizontalLayout"));
        await Drain();
        Check(page.IsHorizontalLayout && !page.IsListLayout && FilmUiTest.Visible(rail) && !FilmUiTest.Visible(list),
            "Automation activates the horizontal layout button");
        CheckSelection(scene, last);
        InvokeAction(Named<ToggleButton>(view, "ListLayout"));
        await Drain();
        Check(page.IsListLayout && FilmUiTest.Visible(list), "Automation also activates the list layout button");
        CheckSelection(scene, last);
        Click(Named<ToggleButton>(view, "HorizontalLayout"));
        await Drain();
        Check(page.IsHorizontalLayout && FilmUiTest.Visible(rail), "the mouse action returns to horizontal cards");
        CheckSelection(scene, last);

        Check(ReferenceEquals(directory, page.EpisodeDirectory) && ReferenceEquals(presentation, directory.Presentation) &&
            ReferenceEquals(shelf, Named<Shelf>(view, "EpisodeShelf")) && ReferenceEquals(rail, shelf.ScrollHost) &&
            shelf.IsLoaded && shelfUnloads == 0,
            $"layout choices retain the mounted directory shelf and native scroll host; " +
            $"loaded={shelf.IsLoaded}, unloads={shelfUnloads}, sameRail={ReferenceEquals(rail, shelf.ScrollHost)}, " +
            $"descendantRail={Visuals(shelf).OfType<ScrollViewer>().Any(candidate => ReferenceEquals(rail, candidate))}");
        var currentRail = shelf.ScrollHost!;
        shelf.ScrollToIndex(0);
        await FilmPlaybackFixture.Until(() => currentRail.HorizontalOffset < .5 && shelf.IsSettled && shelf.HorizontalOffset < .5);
        var previous = Named<Button>(shelf, "Previous");
        var next = Named<Button>(shelf, "Next");
        Check(currentRail.ScrollableWidth > 0 && !previous.IsEnabled && next.IsEnabled,
            "only the forward action is available at the start of the episode rail");
        double pageOffset = Math.Min(shelf.MaximumOffset, FilmUiTest.Field<int>(shelf, "visibleCards") * shelf.CardWidth);
        Click(next);
        try
        {
            await FilmPlaybackFixture.Until(() => Math.Abs(currentRail.HorizontalOffset - pageOffset) < .5 && shelf.IsSettled &&
                Math.Abs(shelf.HorizontalOffset - pageOffset) < .5);
        }
        catch (InvalidOperationException error)
        { throw new InvalidOperationException("next pages the actual episode viewport; " + PagingState(), error); }
        Check(previous.IsEnabled, "next pages the actual episode viewport; " + PagingState());
        Click(previous);
        await FilmPlaybackFixture.Until(() => currentRail.HorizontalOffset < .5 && shelf.IsSettled && shelf.HorizontalOffset < .5);
        Check(!previous.IsEnabled, "previous returns to the rail boundary; " + PagingState());
        CheckSelection(scene, last);
        Check(scene.Source.Calls == 0, "episode and layout interactions remain entirely local");

        string PagingState() => $"sameRail={ReferenceEquals(rail, currentRail)}, currentHost={ReferenceEquals(currentRail, shelf.ScrollHost)}, " +
            $"oldOffset={rail.HorizontalOffset}, offset={currentRail.HorizontalOffset}, saved={shelf.HorizontalOffset}, " +
            $"page={pageOffset}, range={currentRail.ScrollableWidth}, viewport={currentRail.ViewportWidth}, " +
            $"loaded={shelf.IsLoaded}, enabled={shelf.IsEnabled}, visible={FilmUiTest.Visible(shelf)}, settled={shelf.IsSettled}, " +
            $"cardWidth={shelf.CardWidth}, maximum={shelf.MaximumOffset}, " +
            $"target={FilmUiTest.Field<double?>(shelf, "targetOffset")}, previousTarget={FilmUiTest.Call(shelf, "PageTarget", -1)}, " +
            $"previousEnabled={previous.IsEnabled}, previousVisibility={previous.Visibility}";
    }

    private static async Task ResponsivePlayer()
    {
        await using var scene = new Scene(840);
        await scene.OpenAsync();
        await Drain();
        scene.Runtime.Change(root => root["stage"] = "unavailable");
        await FilmPlaybackFixture.Until(() => !scene.Page.CanPlay);
        var view = scene.Playback;
        var player = Named<Grid>(view, "PlayerFrame");
        var metadata = Named<Grid>(view, "MetadataPanel");
        var hero = Named<Grid>(view, "HeroGrid");
        var scroll = Named<ScrollViewer>(view, "PageScroll");
        var play = Named<Button>(view, "PlayButton");
        Check(!play.IsEnabled && !Named<Button>(view, "HeroPlayButton").IsEnabled &&
            Named<TextBlock>(view, "PlayerStatus").Text == "无可用视频源" &&
            AutomationProperties.GetName(play) == "播放或暂停" && scene.Native.Commits == 0,
            "an unavailable snapshot disables empty transport and the page does not offer an impossible playback action");

        foreach (double width in new[] { 1160d, 840d, 839d, 700d })
        {
            scene.View.Width = width;
            scene.Window.Resize(width, 850);
            DesktopTestUi.WindowRoot(scene.Window).UpdateLayout();
            await Drain();
            bool stacked = width < 840;
            Check(Math.Abs(view.ActualWidth - width) < .5, "responsive test uses the content width");
            var playerOrigin = player.TransformToVisual(hero).TransformPoint(new Point());
            var metadataOrigin = metadata.TransformToVisual(hero).TransformPoint(new Point());
            Check(player.ActualWidth > 0 && metadata.ActualWidth > 0 &&
                Math.Abs(Named<Grid>(view, "VideoPicture").ActualHeight - player.ActualWidth * 9 / 16) < 1,
                "the player keeps its sixteen-to-nine frame");
            Check(stacked
                ? metadataOrigin.Y >= playerOrigin.Y + player.ActualHeight &&
                    Math.Abs(metadataOrigin.X - playerOrigin.X) < .5
                : metadataOrigin.X >= playerOrigin.X + player.ActualWidth &&
                    Math.Abs(metadataOrigin.Y - playerOrigin.Y) < .5,
                "840 content pixels start the side-by-side layout; narrower content stacks player first");
            if (!stacked)
                Check(Math.Abs(metadata.ActualHeight - player.ActualHeight) < 1,
                    "wide metadata stays the same height as the player");
            if (width == 840)
            {
                Click(Named<Button>(view, "OverviewToggle"));
                await Drain();
                var details = Visuals(metadata).OfType<ScrollViewer>().Single();
                double pageOffset = scroll.VerticalOffset;
                Check(details.ScrollableHeight > 0, "wide metadata has its own vertical scroll range");
                details.ChangeView(null, details.ScrollableHeight, null, true);
                try { await FilmPlaybackFixture.Until(() => details.VerticalOffset > 0); }
                catch (InvalidOperationException error)
                {
                    throw new InvalidOperationException($"metadata scrolling reaches its real content; offset={details.VerticalOffset}, " +
                        $"range={details.ScrollableHeight}, extent={details.ExtentHeight}, viewport={details.ViewportHeight}, " +
                        $"pageBefore={pageOffset}, pageAfter={scroll.VerticalOffset}", error);
                }
                Check(details.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - pageOffset) < .5,
                    $"scrolling metadata leaves the page viewport in place; offset={details.VerticalOffset}, " +
                    $"range={details.ScrollableHeight}, pageBefore={pageOffset}, pageAfter={scroll.VerticalOffset}");
                await CheckSharedCanvas(view);
                Click(Named<Button>(view, "OverviewToggle"));
                await Drain();
            }
            var content = (FrameworkElement)scroll.Content;
            double margin = stacked ? 24 : 32;
            Check(content.Margin.Left == margin && content.Margin.Right == margin,
                "the page owns its wide or narrow horizontal margins");
            CheckInsideViewport(scroll, hero);
            CheckInsideViewport(scroll, metadata);
        }
        DesktopTestUi.WindowRoot(scene.Window).UpdateLayout();
        Check(scroll.ScrollableHeight > 0, "narrow content scrolls vertically");
        scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
        try { await FilmPlaybackFixture.Until(() => scroll.VerticalOffset > 0); }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException($"the page scroll reaches content below the player; offset={scroll.VerticalOffset}, " +
                $"range={scroll.ScrollableHeight}, extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}", error);
        }
        Check(scroll.VerticalOffset > 0, "the page scroll reaches content below the player");
        Click(Named<ToggleButton>(view, "ListLayout"));
        await Drain();
        var list = Named<ItemsControl>(view, "EpisodeList");
        CheckInsideViewport(scroll, list);
        CheckInsideViewport(scroll, Named<Grid>(view, "MetadataPanel"));
        var directory = scene.Page.EpisodeDirectory;
        int requests = scene.Runtime.Calls.Count;
        foreach (double height in new[] { 650d, 950d })
        {
            double previousHeight = scroll.ActualHeight;
            scene.Window.Resize(700, height);
            await FilmPlaybackFixture.Until(() => Math.Abs(scroll.ActualHeight - previousHeight) > 40);
            DesktopTestUi.WindowRoot(scene.Window).UpdateLayout();
            await Drain();
            var listScroll = Visuals(list).OfType<ScrollViewer>().Single();
            Check(Math.Abs(list.MaxHeight - scroll.ActualHeight) < .5 && list.ActualHeight <= scroll.ActualHeight + .5 &&
                listScroll.ViewportHeight > 0 && listScroll.ScrollableHeight > 0,
                $"the retained episode list follows the real page viewport after resize; max={list.MaxHeight}, " +
                $"list={list.ActualHeight}, page={scroll.ActualHeight}, innerRange={listScroll.ScrollableHeight}");
            listScroll.ChangeView(null, 0, null, true);
            await FilmPlaybackFixture.Until(() => listScroll.VerticalOffset == 0);
            double pageOffset = scroll.VerticalOffset;
            var last = await EpisodeButton(list, scene.Page.Episodes[^1]);
            await FilmPlaybackFixture.Until(() =>
            {
                list.UpdateLayout();
                return last.ActualWidth > 0 && last.ActualHeight > 0;
            });
            listScroll.ChangeView(null, listScroll.ScrollableHeight, null, true);
            try
            {
                await FilmPlaybackFixture.Until(() => listScroll.VerticalOffset > 0 &&
                    Math.Abs(listScroll.VerticalOffset - listScroll.ScrollableHeight) < 1 &&
                    Bounds(last, listScroll).Bottom <= listScroll.ViewportHeight + 1);
            }
            catch (InvalidOperationException error)
            {
                var container = ((ListView)list).ContainerFromItem(scene.Page.Episodes[^1]) as FrameworkElement;
                throw new InvalidOperationException($"the resized list reaches its real last row; height={height}, " +
                    $"offset={listScroll.VerticalOffset}, range={listScroll.ScrollableHeight}, extent={listScroll.ExtentHeight}, " +
                    $"viewport={listScroll.ViewportHeight}, last={Bounds(last, listScroll)}, lastMargin={last.Margin}, " +
                    $"container={(container is null ? -1 : ((ListView)list).IndexFromContainer(container))}, " +
                    $"containerBounds={(container is null ? default : Bounds(container, listScroll))}, " +
                    $"items={list.Items.Count}, padding={((ListView)list).Padding}", error);
            }
            Check(Math.Abs(scroll.VerticalOffset - pageOffset) < .5 &&
                ReferenceEquals(list, Named<ItemsControl>(view, "EpisodeList")) &&
                ReferenceEquals(directory, scene.Page.EpisodeDirectory) && scene.Runtime.Calls.Count == requests,
                "resized list scrolling retains its directory and leaves the page position and business reads unchanged");
        }
        Check(!play.IsEnabled && !Named<Button>(view, "HeroPlayButton").IsEnabled &&
            !scene.Page.HasOutput && scene.Native.Commits == 0 && scene.Source.Calls == 0,
            "resizing and scrolling do not manufacture an output or resolve a source");
    }

    private static async Task NavigationRecovery()
    {
        await using var scene = new Scene();
        var module = scene.Module;
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var item = module.Sections.Single(section => section.Key == "tv").Items[0];
        await module.OpenPlaybackAsync(item);
        await Drain();
        var original = scene.Page;
        var chosen = original.Episodes[^1];
        Click(await EpisodeButton(EpisodeItems(scene.Playback), chosen));
        Click(Named<ToggleButton>(scene.Playback, "ListLayout"));
        await Drain();
        string location = module.CaptureNavigationLocation();
        int calls = scene.Source.Calls;
        module.RetainNavigationLocations(new HashSet<string>(StringComparer.Ordinal) { location });
        var directory = original.EpisodeDirectory;
        Check(location == FilmCapability.PlaybackRoute + ":" + original.NavigationIdentity && original.IsListLayout,
            "captured navigation retains the page instance independently of the business and execution identities");
        scene.View.Visibility = Visibility.Collapsed;
        await Drain();
        scene.View.Visibility = Visibility.Visible;
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        Check(ReferenceEquals(scene.Page, original) && scene.Page.IsListLayout &&
            ReferenceEquals(scene.Page.EpisodeDirectory, directory) &&
            scene.Page.Request.Target.Id == original.Request.Target.Id && ReferenceEquals(scene.Playback.DataContext, scene.Page),
            "restoration reuses the actual page, selected layout and directory");
        CheckSelection(scene, chosen);
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var updatedCard = FilmPlaybackFixture.RefreshMetadata(item with { Title = item.Title + " 更新", Score = "8.8" });
        module.Sections.Single(section => section.Key == "tv").Items[0] = updatedCard;
        await module.OpenPlaybackAsync(updatedCard);
        await Drain();
        Check(scene.Page.Item.Id == updatedCard.Id && scene.Page.Title == updatedCard.Title &&
            scene.Page.SelectedEpisode is { Number: 1 } && !scene.Page.IsListLayout,
            "a new card request has its own display metadata and coordinates");
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        CheckSelection(scene, chosen);
        var restored = scene.Page;
        var routes = new List<string>();
        module.NavigationRequested += (_, request) => routes.Add(request.Route);
        bool rejected = false;
        try { await module.OpenRecommendationAsync(new("outside-recommendations", "其他影片", "", "")); }
        catch (FilmPlaybackException error) when (error.Code == "CAPABILITY_UNAVAILABLE") { rejected = true; }
        Check(rejected && routes.Count == 0 && ReferenceEquals(scene.Page, restored) &&
            module.CaptureNavigationLocation() == location,
            "a card without the required structured playback payload cannot navigate or change state");
        var recommendation = restored.Recommendations.First();
        Click(Visuals(Named<ItemsControl>(scene.Playback, "RelatedItems")).OfType<Button>()
            .Single(button => ReferenceEquals(button.Tag, recommendation)));
        await Drain();
        Check(routes.SequenceEqual([FilmCapability.PlaybackRoute]), "recommendations use the same internal route");
        await module.NavigateAsync(routes[0], default);
        await Drain();
        Check(scene.Page.Item.Id == recommendation.Id && scene.Page.Request.Target.Id == recommendation.PlaybackContext!.Target.Id &&
            ReferenceEquals(scene.Playback.DataContext, scene.Page), "the actual recommendation identity is bound");
        Check(module.CaptureNavigationLocation() != location, "different targets have different navigation locations");
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        Check(ReferenceEquals(scene.Page, original) && scene.Page.IsListLayout &&
            ReferenceEquals(scene.Page.EpisodeDirectory, directory),
            "the original history location retains its page, layout and directory after recommendation navigation");
        CheckSelection(scene, chosen);
        Check(scene.Source.Calls == calls + 4,
            "only the actual home restoration revalidates its four sections; playback navigation does not reload the catalog");
    }

    private static async Task StaleRecommendation()
    {
        await using var scene = new Scene();
        await scene.OpenAsync(); await Drain();
        var captured = scene.Page.Recommendations[0];
        var request = captured.PlaybackContext!;
        int homeCalls = scene.Source.Calls;
        scene.Runtime.Change(root => root["workInfo"]!["metadata"]!["similar"]!["results"] = new JsonArray());
        await FilmPlaybackFixture.Until(() => scene.Page.Recommendations.Count == 0);
        await scene.Module.OpenRecommendationAsync(captured); await Drain();
        var opened = scene.Runtime.Calls.Last(call => call.Method == "film.session.open").Parameters;
        Check(scene.Page.Item.Id == captured.Id && scene.Page.Request.Target == request.Target &&
            FilmJson.Text(opened.GetProperty("target"), "id") == request.Target.Id &&
            FilmJson.Text(opened.GetProperty("target"), "metadataProvider") == request.Target.MetadataProvider &&
            FilmJson.Text(opened.GetProperty("target"), "metadataId") == request.Target.MetadataId &&
            scene.Source.Calls == homeCalls &&
            !scene.Runtime.Calls.Any(call => call.Method.StartsWith("film.data.", StringComparison.Ordinal)),
            "a click carries the original structured identity without checking or reloading an old recommendation snapshot");
    }

    private static async Task FlatTiles()
    {
        await using var scene = new Scene();
        await scene.OpenAsync();
        await Drain();
        var view = scene.Playback;
        var page = scene.Page;
        var metadata = Named<Grid>(view, "MetadataPanel");
        var actions = Visuals(metadata).OfType<ButtonBase>().ToArray();
        Check(!Visuals(metadata).OfType<Border>().Any(border => FilmUiTest.Visible(border) &&
                !Visuals(border).OfType<ButtonBase>().Any() &&
                !actions.Any(action => DesktopVisual.Contains(action, border)) &&
                ((border.BorderThickness != default && border.BorderBrush is SolidColorBrush { Color.A: > 0 }) ||
                (border.ActualWidth > metadata.ActualWidth / 2 && border.Background is SolidColorBrush { Color.A: > 0 }))),
            "metadata sits directly on the page without a surface or outlined card");
        CheckFlat(view, page, outline: false);
        CheckCurrentStatus(view, page);
        var chosen = page.Episodes[2];
        Click(await EpisodeButton(EpisodeItems(view), chosen));
        await Drain();
        CheckSelection(scene, chosen);
        CheckCurrentStatus(view, page);
        Click(Named<ToggleButton>(view, "ListLayout"));
        await Drain();
        CheckSelection(scene, chosen);
        CheckCurrentStatus(view, page);

        using var contrast = new FilmUiTest.HighContrastResources(view);
        await Drain();
        Check(Named<Grid>(view, "PageCanvas").Background is SolidColorBrush background && background.Color == FilmUiTest.ResourceColor(view, "Film.PageBackground") &&
            Named<TextBlock>(view, "FilmTitle").Foreground is SolidColorBrush ink && ink.Color == FilmUiTest.ResourceColor(view, "MeowFilm.Brush.Text"),
            "the shipped high-contrast resources pair the page with the system background and text");
        CheckFlat(view, page, outline: true);
        CheckSelection(scene, chosen);
        Click(Named<ToggleButton>(view, "HorizontalLayout"));
        await Drain();
        CheckFlat(view, page, outline: true);
        CheckSelection(scene, chosen);
        Check(scene.Source.Calls == 0, "presentation states remain local");
    }

    private static void CheckFlat(FilmPlaybackView view, FilmPlaybackPage page, bool outline)
    {
        var text = outline ? FilmUiTest.ResourceColor(view, "Film.TileBorder") : default;
        string host = page.IsListLayout ? "EpisodeList" : "EpisodeShelf";
        foreach (var tile in Tiles(view, host).Concat(Tiles(view, "RelatedItems")))
        {
            var frame = Visuals(tile).OfType<ContentPresenter>().First();
            Check(tile.Background is SolidColorBrush { Color.A: 0 } && frame.Background is SolidColorBrush { Color.A: 0 },
                "tiles have no resting surface");
            Check(outline
                ? frame.BorderThickness == new Thickness(2) && frame.BorderBrush is SolidColorBrush brush &&
                    brush.Color == text
                : frame.BorderThickness == default,
                outline ? "high contrast outlines tiles with system text while the current badge remains non-color status"
                    : "tiles have no resting border");
        }
        foreach (var row in Visuals(Named<ItemsControl>(view, "EpisodeList")).OfType<Border>()
            .Where(border => border.Name == "RowHover"))
            Check(row.Opacity == 0, "the list row hover surface rests transparent");
    }

    private static void CheckCurrentStatus(FilmPlaybackView view, FilmPlaybackPage page)
    {
        foreach (var tile in Tiles(view, page.IsListLayout ? "EpisodeList" : "EpisodeShelf"))
            Check(AutomationProperties.GetItemStatus(tile) ==
                (ReferenceEquals(tile.Tag, page.SelectedEpisode) ? "当前" : ""),
                "only the current episode announces its current status");
    }

    private static async Task HoverLift()
    {
        await using var scene = new Scene();
        await scene.OpenAsync();
        await Drain();
        var view = scene.Playback;
        var page = scene.Page;
        var current = page.SelectedEpisode!;
        await CheckLift(Tiles(view, "EpisodeShelf")[1], EpisodeItems(view), 144);
        await CheckLift(Tiles(view, "RelatedItems")[0], Named<ItemsControl>(view, "RelatedItems"), 234);
        Click(Named<ToggleButton>(view, "ListLayout"));
        await Drain();
        var row = Tiles(view, "EpisodeList")[1];
        var rowHover = Visuals(row).OfType<Border>().Single(border => border.Name == "RowHover");
        await CheckLift(row, Named<ItemsControl>(view, "EpisodeList"), 99);
        CardMotion.SetProgress(row, 1);
        await Drain();
        Check(rowHover.Opacity == 1, "a lifted list row fades in its soft hover surface");
        CardMotion.SetProgress(row, 0);
        await Drain();
        Check(rowHover.Opacity == 0, "the row hover surface returns to transparent");
        Check(ReferenceEquals(page.SelectedEpisode, current) && current.IsSelected &&
            !page.HasOutput && scene.Native.Commits == 0 && scene.Source.Calls == 0,
            "hover feedback neither changes the current episode nor starts playback");
    }

    private static async Task CheckLift(Button tile, FrameworkElement host, double frameHeight)
    {
        Check(CardMotion.GetEnabled(tile), "card motion owns tile hover and focus");
        var mark = Visuals(tile).OfType<FilmPlayMark>().Single();
        var frame = (Grid)VisualTreeHelper.GetParent(mark);
        var shade = frame.Children.OfType<FilmHoverShade>().Single();
        var image = frame.Children.OfType<FrameworkElement>().First();
        var rest = Bounds(tile, host);
        Check(Math.Abs(rest.Width / tile.ActualWidth - rest.Height / tile.ActualHeight) < .01 && mark.Opacity == 0 && shade.Opacity == 0,
            "a resting tile has no lift scrim or play mark");
        Check(Math.Abs(frame.ActualHeight - frameHeight) < .5, "tile artwork keeps its frame size");

        CardMotion.SetProgress(tile, 1);
        await Drain();
        var lifted = Bounds(tile, host);
        double scale = lifted.Width / rest.Width;
        Check(scale is > 1.005 and < 1.06 && Math.Abs(lifted.Height / rest.Height - scale) < .001,
            "the whole tile lifts slightly on hover or focus");
        Check(image.RenderTransform is ScaleTransform { ScaleX: > 1.04 and < 1.06 },
            "the artwork zooms inside its clipped frame");
        Check(mark.Opacity == 1 && shade.Opacity == 1, "lifted artwork shows the shared scrim and play mark");
        var center = mark.TransformToVisual(frame).TransformPoint(new Point(mark.ActualWidth / 2, mark.ActualHeight / 2));
        Check(Math.Abs(center.X - frame.ActualWidth / 2) <= .5 && Math.Abs(center.Y - frame.ActualHeight / 2) <= .5,
            "the play mark is centered on the artwork");

        CardMotion.SetProgress(tile, 0);
        await Drain();
        var settled = Bounds(tile, host);
        Check(Math.Abs(settled.Width - rest.Width) < .01 && mark.Opacity == 0 && shade.Opacity == 0,
            "the tile settles back without residue");
    }

    private static async Task PosterCards()
    {
        const string poster = "ms-appx:///MeowFilm.Capabilities/Film/Presentation/Assets/poster-long-season.jpg";
        const string still = "ms-appx:///MeowFilm.Capabilities/Film/Presentation/Assets/episode-01.jpg";
        await CheckPosterCard(poster, 400, 300, 2d / 3);
        await CheckPosterCard(poster, 150, 300, 2d / 3);
        await CheckPosterCard(still, 400, 300, 16d / 9);

        await using var scene = new Scene();
        await scene.OpenAsync();
        await Drain();
        var view = scene.Playback;
        var panel = Named<Grid>(view, "MetadataPanel");
        await CheckSharedCanvas(view);
        scene.View.Width = 700;
        scene.Window.Resize(700, 850);
        DesktopTestUi.WindowRoot(scene.Window).UpdateLayout();
        await Drain();
        var details = Named<ScrollViewer>(view, "MetadataScroll");
        Check(double.IsNaN(panel.Height) && Math.Abs(panel.ActualHeight - details.DesiredSize.Height) < 1,
            "stacked metadata sizes to its content on the shared canvas");
        await CheckSharedCanvas(view);
        double collapsedHeight = panel.ActualHeight;
        Click(Named<Button>(view, "OverviewToggle"));
        await Drain();
        Check(panel.ActualHeight > collapsedHeight && FilmUiTest.Visible(Named<TextBlock>(view, "OverviewText")),
            "the expanded overview grows the narrow information region");
        await CheckSharedCanvas(view);
    }

    private static async Task CheckSharedCanvas(FilmPlaybackView view)
    {
        var panel = Named<Grid>(view, "MetadataPanel");
        var scroll = Named<ScrollViewer>(view, "MetadataScroll");
        Check(Named<Grid>(view, "PageCanvas").Background is SolidColorBrush { Color.A: 0 },
            "the playback page exposes its application's background");
        var visibility = scroll.Visibility;
        scroll.Visibility = Visibility.Collapsed;
        try
        {
            panel.UpdateLayout();
            var pixels = await Pixels(panel);
            Check(Enumerable.Range(0, pixels.Length / 4).All(index => pixels[index * 4 + 3] == 0),
                "the entire metadata region exposes the shared canvas behind its scrolling foreground");
        }
        finally { scroll.Visibility = visibility; panel.UpdateLayout(); }
    }

    private static async Task HeroActions()
    {
        await using var scene = new Scene();
        var module = scene.Module;
        await scene.OpenAsync();
        await Drain();
        var page = scene.Page;
        var view = scene.Playback;
        var content = Named<StackPanel>(view, "MetadataContent");
        var details = Named<ScrollViewer>(view, "MetadataScroll");
        var title = Named<TextBlock>(view, "FilmTitle");
        var metadata = Named<TextBlock>(view, "FilmMetadata");
        var actions = Named<Panel>(view, "HeroActions");
        var favorite = Named<ToggleButton>(view, "FavoriteButton");
        var play = Named<Button>(view, "HeroPlayButton");
        var favoriteSurface = Named<Border>(favorite, "FavoriteSurface");
        var favoriteContent = Named<ContentPresenter>(favorite, "FavoriteContent");
        var favoriteText = new[] { Named<TextBlock>(view, "FavoriteLabel"), Named<TextBlock>(view, "FavoriteIcon") };
        Check(favoriteSurface.CornerRadius == new CornerRadius(12) && favoriteSurface.Background is SolidColorBrush { Color.A: 0 } &&
            favoriteSurface.BorderThickness == default && favorite.Padding == default &&
            favorite.Width == 64 && favorite.Height == 44 && favorite.UseSystemFocusVisuals,
            "favorite keeps its transparent rounded 64 by 44 native toggle without a default button frame");
        foreach (string state in new[] { "PointerOver", "Pressed", "CheckedPointerOver", "CheckedPressed" })
        {
            Check(VisualStateManager.GoToState(favorite, state, false), "favorite retains its native " + state + " feedback");
            Check(favoriteSurface.Background is SolidColorBrush fill && fill.Color == FilmUiTest.ResourceColor(view,
                state.EndsWith("Pressed", StringComparison.Ordinal) ? "MeowFilm.Brush.Pressed" : "MeowFilm.Brush.Hover"),
                "favorite renders the existing hover or pressed palette");
            Check(favoriteText.All(text => text.Foreground is SolidColorBrush textBrush &&
                textBrush.Color == FilmUiTest.ResourceColor(view, "MeowFilm.Brush.Text")),
                "favorite hover and pressed colors apply to its complete label and icon");
        }
        Check(VisualStateManager.GoToState(favorite, "Normal", false), "favorite returns to transparent resting chrome");
        favorite.IsEnabled = false;
        var favoritePeer = new ToggleButtonAutomationPeer(favorite);
        try
        { await FilmPlaybackFixture.Until(() => Math.Abs(favoriteSurface.Opacity - .45) < .001 && !favoritePeer.IsEnabled()); }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException($"favorite reaches its native disabled visual and accessible state; " +
                $"enabled={favorite.IsEnabled}, peerEnabled={favoritePeer.IsEnabled()}, checked={favorite.IsChecked}, " +
                $"opacity={favoriteSurface.Opacity}", error);
        }
        Check(Math.Abs(favoriteSurface.Opacity - .45) < .001 &&
            !favoritePeer.IsEnabled(), "disabled favorite keeps its native accessible disabled state");
        favorite.SetBinding(Control.IsEnabledProperty, new Binding { Path = new PropertyPath(nameof(FilmPlaybackPage.CanFavorite)), Mode = BindingMode.OneWay });
        await FilmPlaybackFixture.Until(() => favoritePeer.IsEnabled() && favoriteSurface.Opacity == 1);
        Check(title.TextAlignment == TextAlignment.Center && metadata.TextAlignment == TextAlignment.Center &&
            content.VerticalAlignment == VerticalAlignment.Top &&
            DesktopVisual.Contains(details, content) && DesktopVisual.Contains(Named<Grid>(view, "MetadataPanel"), details),
            "one top-aligned work tree owns identity and actions inside the metadata region");
        foreach (double width in new[] { 1237d, 907d, 840d })
        {
            scene.View.Width = width; scene.Window.Resize(width, 800);
            DesktopTestUi.WindowRoot(scene.Window).UpdateLayout();
            details.ChangeView(null, 0, null, true);
            await Drain();
            foreach (var element in new FrameworkElement[] { title, metadata, Named<WrapPanel>(view, "FilmFacts"), actions })
            {
                var bounds = element is TextBlock ? LayoutInformation.GetLayoutSlot(element) : Bounds(element, content);
                Check(Math.Abs(bounds.Left + bounds.Width / 2 - content.ActualWidth / 2) <= 1,
                    $"identity facts and the action group share the metadata center; width={width}, " +
                    $"element={element.Name}, bounds={bounds}, content={content.ActualWidth}x{content.ActualHeight}");
            }
            Check(Bounds(title, details).Top >= 0 && Bounds(actions, details).Bottom <= details.ViewportHeight + 1 &&
                Bounds(Named<Button>(view, "OverviewToggle"), details).Bottom <= details.ViewportHeight + 1,
                $"the short title playback favorite and overview action fit the desktop metadata viewport; width={width}, " +
                $"title={Bounds(title, details)}, actions={Bounds(actions, details)}, " +
                $"overview={Bounds(Named<Button>(view, "OverviewToggle"), details)}, " +
                $"viewport={details.ViewportWidth}x{details.ViewportHeight}, offset={details.VerticalOffset}, " +
                $"metadata={Named<Grid>(view, "MetadataPanel").ActualWidth}x{Named<Grid>(view, "MetadataPanel").ActualHeight}, " +
                $"player={Named<Grid>(view, "PlayerFrame").ActualWidth}x{Named<Grid>(view, "PlayerFrame").ActualHeight}");
            foreach (var action in actions.Children.OfType<FrameworkElement>())
            {
                var bounds = Bounds(action, content);
                Check(bounds.Left >= -.5 && bounds.Right <= content.ActualWidth + .5 &&
                    action.ActualWidth > 0 && FilmUiTest.Visible(action),
                    $"play short-video and favorite remain fully inside the available metadata width; width={width}, " +
                    $"action={action.Name}, bounds={bounds}, content={content.ActualWidth}x{content.ActualHeight}");
            }
            var favoriteLabel = Named<TextBlock>(view, "FavoriteLabel");
            var labelBounds = Bounds(favoriteLabel, favorite);
            Check(labelBounds.Top >= 0 && labelBounds.Bottom <= favorite.ActualHeight + .5,
                $"the full favorite label fits its actual control; label={labelBounds}, height={favorite.ActualHeight}");
            await CheckSharedCanvas(view);
        }
        Check(play.IsEnabled && AutomationProperties.GetName(play) == "播放当前选择" &&
            Visuals(play).OfType<TextBlock>().Any(text => text.Text == "播放") && !page.HasOutput,
            "the hero offers explicit playback of the current target without claiming a native media output");
        Check(favorite.IsEnabled && favorite.IsTabStop && favorite.IsTabStop && !page.IsFavorite &&
            favorite.IsChecked == false && Named<TextBlock>(view, "FavoriteLabel").Text == "收藏",
            "favorite starts off with a labelled keyboard-accessible toggle");
        var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(favorite).GetPattern(PatternInterface.Toggle);
        scene.Store.FailWrite = true;
        toggle.Toggle();
        await Drain();
        Check(!page.IsFavorite && page.CanFavorite && toggle.ToggleState == ToggleState.Off &&
            Named<TextBlock>(view, "FavoriteLabel").Text == "收藏" &&
            scene.Store.Data is null && scene.Store.Writes == 1,
            "failed UIA persistence restores the actual favorite and cannot retry through binding updates");
        scene.Store.FailWrite = false;
        toggle.Toggle();
        await Drain();
        Check(page.IsFavorite && toggle.ToggleState == ToggleState.On &&
            Named<TextBlock>(view, "FavoriteLabel").Text == "已收藏" &&
            AutomationProperties.GetName(favorite) == "取消收藏 " + page.Title && scene.Store.Writes == 2,
            "the UIA toggle updates the model, visible favorite label and accessible action");
        string location = module.CaptureNavigationLocation();
        module.RetainNavigationLocations(new HashSet<string>(StringComparer.Ordinal) { location });
        await module.OpenRecommendationAsync(page.Recommendations[0]);
        await Drain();
        Check(!scene.Page.IsFavorite && favorite.IsChecked == false,
            "a different film has independent favorite state");
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        page = scene.Page;
        Check(page.IsFavorite && favorite.IsChecked == true && scene.Store.Writes == 2,
            "return navigation reads the original film favorite from its one durable owner");
        favorite.StartBringIntoView();
        FilmUiTest.Click(favorite);
        await Drain();
        Check(!page.IsFavorite && favorite.IsChecked == false &&
            Named<TextBlock>(view, "FavoriteLabel").Text == "收藏" && scene.Store.Writes == 3,
            "native toggle removes the favorite once and updates its label without a second binding-triggered write");
        var overview = Named<TextBlock>(view, "OverviewText");
        var overviewToggle = Named<Button>(view, "OverviewToggle");
        Check(!page.OverviewExpanded && overview.Visibility == Visibility.Collapsed &&
            overview.DesiredSize == default && (string)overviewToggle.Content == "简介",
            "only the overview action is initially visible; the body takes no layout space");
        double collapsedHeight = content.ActualHeight;
        Click(overviewToggle);
        await Drain();
        Check(page.OverviewExpanded && FilmUiTest.Visible(overview) && overview.Text == page.Overview &&
            overview.TextAlignment == TextAlignment.Left && overview.TextTrimming == TextTrimming.None &&
            (string)overviewToggle.Content == "收起简介" && content.ActualHeight > collapsedHeight,
            "clicking the overview action reveals the complete text and changes its label");
        await module.OpenRecommendationAsync(page.Recommendations[0]);
        await Drain();
        Check(!scene.Page.OverviewExpanded && overview.Visibility == Visibility.Collapsed,
            "another film begins with a collapsed overview");
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        page = scene.Page;
        Check(page.OverviewExpanded && FilmUiTest.Visible(overview), "returning to the retained page preserves its own expanded overview");
        InvokeAction(overviewToggle);
        await Drain();
        Check(!page.OverviewExpanded && !FilmUiTest.Visible(overview), "Automation collapses the restored page's overview");
        InvokeAction(overviewToggle);
        await Drain();
        Check(page.OverviewExpanded && FilmUiTest.Visible(overview), "Automation expands the actual current presentation");
        InvokeAction(overviewToggle);
        try
        {
            await FilmPlaybackFixture.Until(() => !page.OverviewExpanded && overview.Visibility == Visibility.Collapsed &&
                Math.Abs(content.ActualHeight - collapsedHeight) < 1 &&
                AutomationProperties.GetItemStatus(overviewToggle) == "已收起");
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException($"Automation collapses the overview and reclaims its layout space; " +
                $"expanded={page.OverviewExpanded}, visibility={overview.Visibility}, desired={overview.DesiredSize}, " +
                $"height={content.ActualHeight}, collapsedHeight={collapsedHeight}, delta={content.ActualHeight - collapsedHeight}, " +
                $"itemStatus={AutomationProperties.GetItemStatus(overviewToggle)}", error);
        }
        Check(!page.OverviewExpanded && overview.Visibility == Visibility.Collapsed &&
            Math.Abs(content.ActualHeight - collapsedHeight) < 1 &&
            AutomationProperties.GetItemStatus(overviewToggle) == "已收起",
            "Automation collapses the overview and reclaims its layout space");
        using var contrast = new FilmUiTest.HighContrastResources(view);
        details.ChangeView(null, 0, null, true);
        await Drain();
        Check(Named<Grid>(view, "PageCanvas").Background is SolidColorBrush background && background.Color == FilmUiTest.ResourceColor(view, "Film.PageBackground") &&
            Bounds(title, content).Top < 1 &&
            favorite.Foreground is SolidColorBrush ink && ink.Color == FilmUiTest.ResourceColor(view, "MeowFilm.Brush.Text") &&
            play.Foreground is SolidColorBrush actionInk && actionInk.Color == FilmUiTest.ResourceColor(view, "MeowFilm.Brush.OnAccent"),
            "high-contrast resources keep the metadata layout and system canvas and action colors");
        Check(favoriteSurface.Background is SolidColorBrush favoriteGround && favoriteGround.Color == FilmUiTest.ResourceColor(view, "MeowFilm.Brush.Hover") &&
            favoriteSurface.BorderThickness == new Thickness(1) && favoriteSurface.BorderBrush is SolidColorBrush outline &&
            outline.Color == FilmUiTest.ResourceColor(view, "MeowFilm.Brush.Text"),
            "high-contrast favorite pairs the system window background with a visible system text outline");
        Check(VisualStateManager.GoToState(favorite, "PointerOver", false) &&
            favoriteSurface.Background is SolidColorBrush highlight && highlight.Color == FilmUiTest.ResourceColor(view, "MeowFilm.Brush.Accent") &&
            favoriteContent.Foreground is SolidColorBrush selectedInk && selectedInk.Color == FilmUiTest.ResourceColor(view, "MeowFilm.Brush.OnAccent") &&
            favoriteText.All(text => text.Foreground is SolidColorBrush textBrush && textBrush.Color == selectedInk.Color),
            $"high-contrast favorite hover changes the complete label and icon foreground together; " +
            $"background={(favoriteSurface.Background as SolidColorBrush)?.Color}, " +
            $"content={(favoriteContent.Foreground as SolidColorBrush)?.Color}, " +
            $"label={(favoriteText[0].Foreground as SolidColorBrush)?.Color}, icon={(favoriteText[1].Foreground as SolidColorBrush)?.Color}, " +
            $"expectedBackground={FilmUiTest.ResourceColor(view, "MeowFilm.Brush.Accent")}, " +
            $"expectedText={FilmUiTest.ResourceColor(view, "MeowFilm.Brush.OnAccent")}");
        Check(VisualStateManager.GoToState(favorite, "Normal", false), "favorite restores the high-contrast resting state");
        Check(favoriteText.All(text => text.Foreground is SolidColorBrush textBrush &&
            textBrush.Color == FilmUiTest.ResourceColor(view, "MeowFilm.Brush.Text")),
            "high-contrast favorite restores the system resting foreground for its complete label and icon");
        Check(scene.Source.Calls == 0, "hero and favorite interactions issue no source requests");
    }

    private static async Task SharedCanvasReadability()
    {
        await using var scene = new Scene(907);
        await scene.OpenAsync();
        await Drain();
        var view = scene.Playback;
        var panel = Named<Grid>(view, "MetadataPanel");
        var scroll = Named<ScrollViewer>(view, "MetadataScroll");
        var labels = new[] { Named<TextBlock>(view, "FilmMetadata"),
            Visuals(EpisodeItems(view)).OfType<TextBlock>()
                .First(text => text.Text == scene.Page.Episodes[0].Overview) };
        foreach (string selected in new[] { AppearanceColors.DefaultColor, "#FF0000", "#000000" })
        {
            scene.Window.Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.Background(selected);
            await Drain();
            var region = Bounds(panel, scene.Canvas);
            region.Intersect(new Rect(0, 0, scene.Canvas.ActualWidth, scene.Canvas.ActualHeight));
            Check(region.Width > 40 && region.Height > 44, "contrast samples cover the visible metadata region");
            scroll.Visibility = Visibility.Collapsed;
            try
            {
                await Drain();
                var pixels = await Pixels(scene.Canvas);
                int width = (int)scene.Canvas.ActualWidth;
                foreach (var label in labels)
                {
                    var ink = ((SolidColorBrush)label.Foreground).Color;
                    double worst = 99;
                    for (int y = (int)region.Top + 22; y < region.Bottom - 22; y += 4)
                        for (int x = (int)region.Left + 20; x < region.Right - 20; x += 4)
                        {
                            int offset = (y * width + x) * 4;
                            Check(pixels[offset + 3] == 255,
                                "contrast samples contain the actual application's opaque canvas");
                            var color = Color.FromArgb(255, pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                            worst = Math.Min(worst, Contrast(ink, color));
                        }
                    Check(worst >= 4.5, $"actual text stays readable on the shared application color {selected}: {worst}");
                }
            }
            finally { scroll.Visibility = Visibility.Visible; await Drain(); }
        }
    }

    private static async Task CheckPosterCard(string address, int width, int height, double ratio)
    {
        var poster = new FilmPlaybackPoster { Address = address, Width = width, Height = height };
        var host = new Grid { Width = width, Height = height, Children = { poster } };
        var window = FilmUiTest.Window(host, width, height); window.Show();
        try
        {
        await Drain();
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var image = poster.Children.OfType<Image>().Single();
        await FilmPlaybackFixture.Until(() => image.Source is BitmapSource { PixelWidth: > 0 });
        var slot = Bounds(image, poster);
        Check(image.Visibility == Visibility.Visible && image.Source is BitmapSource source &&
            Math.Abs((double)source.PixelWidth / source.PixelHeight - ratio) < .01 &&
            image.Stretch == Stretch.UniformToFill && poster.Clip is RectangleGeometry &&
            slot.Left <= 0 && slot.Top <= 0 && slot.Right >= width - 1 && slot.Bottom >= height - 1,
            "normal portrait and landscape cards retain their source ratio and clipped fill");
        var pixels = await Pixels(host);
        Check(new[] { (1, 1), (width - 2, 1), (1, height / 2), (width - 2, height / 2),
                (width / 2, height / 2), (1, height - 2), (width - 2, height - 2) }
            .All(point => Alpha(pixels, width, point.Item1, point.Item2) > 240),
            "ordinary poster cards fill their whole frame without letterboxing");
        }
        finally { window.Close(); }
    }

    private static Task<byte[]> Pixels(FrameworkElement element) => FilmUiTest.Pixels(element);

    private static byte Alpha(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 3];

    private static async Task SharedCanvasNavigation()
    {
        await using var scene = new Scene();
        var module = scene.Module;
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var section = module.Sections.Single(candidate => candidate.Key == "tv");
        await module.OpenPlaybackAsync(section.Items[0]);
        await Drain();
        var view = scene.Playback;
        string originalPoster = scene.Page.Poster;
        Point[] samples = [new(8, 4), new(8, 200), new(8, scene.Canvas.ActualHeight - 4)];
        var first = (await CanvasColors(scene.Canvas, samples));
        await CheckSharedCanvas(view);

        var scroll = Named<ScrollViewer>(view, "PageScroll");
        scroll.ChangeView(scroll.ScrollableWidth, scroll.ScrollableHeight, null, true);
        await Drain();
        Check(scroll.VerticalOffset > 0 && (await CanvasColors(scene.Canvas, samples)).SequenceEqual(first),
            "the application canvas stays fixed while the page scrolls");

        var recommendation = scene.Page.Recommendations.First(item => item.Poster != scene.Page.Poster);
        // This layout fixture returns a successful detail with its explicit poster.
        // A successful empty poster now intentionally clears the card baseline.
        scene.Runtime.Intercept = (method, input, _) =>
        {
            if (method == "film.session.open" &&
                FilmJson.Target(FilmJson.Member(input, "target")).Id == recommendation.PlaybackContext!.Target.Id)
            {
                var response = System.Text.Json.Nodes.JsonNode.Parse(input.GetRawText())!.AsObject();
                response["metadata"] = System.Text.Json.Nodes.JsonNode.Parse(
                    FilmPlaybackFixture.Metadata(recommendation.PlaybackContext.Target, recommendation.Poster).GetRawText());
                input = System.Text.Json.JsonSerializer.SerializeToElement(response);
            }
            return Task.FromResult(scene.Runtime.Handle(method, input));
        };
        Click(Visuals(Named<ItemsControl>(view, "RelatedItems")).OfType<Button>()
            .Single(button => ReferenceEquals(button.Tag, recommendation)));
        await Drain();
        Check(scene.Page.Item.Id == recommendation.Id && scene.Page.Poster == recommendation.Poster &&
            scene.Page.Poster != originalPoster, "the recommendation opens its own film identity and real poster");
        await CheckSharedCanvas(view);
        Check((await CanvasColors(scene.Canvas, samples)).SequenceEqual(first),
            "opening a different poster does not change the application canvas");

        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var blank = FilmPlaybackFixture.RefreshMetadata(section.Items[1] with { Poster = "" });
        section.Items[1] = blank;
        await module.OpenPlaybackAsync(blank);
        await Drain();
        Check(scene.Page.Item.Id == blank.Id && scene.Page.Poster.Length == 0 &&
            (await CanvasColors(scene.Canvas, samples)).SequenceEqual(first),
            "a film without a poster keeps the same application canvas");
        await CheckSharedCanvas(view);
        scene.Window.Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.Background("#FF0000");
        await Drain();
        Check(!(await CanvasColors(scene.Canvas, samples)).SequenceEqual(first),
            "the selected global application color changes the playback canvas");
        scene.Window.Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.DefaultBackground;
        await Drain();
        Check((await CanvasColors(scene.Canvas, samples)).SequenceEqual(first),
            "restoring the global default restores the same playback canvas");
    }

    private static async Task<Color[]> CanvasColors(FrameworkElement canvas, IEnumerable<Point> samples)
    {
        var pixels = await Pixels(canvas);
        int width = (int)canvas.ActualWidth;
        return samples.Select(point =>
        {
            int offset = ((int)point.Y * width + (int)point.X) * 4;
            Check(pixels[offset + 3] == 255, "canvas samples contain the opaque application background");
            return Color.FromArgb(255, pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }).ToArray();
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            double c = value / 255d;
            return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
        }
        static double Luminance(Color color) =>
            .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        double first = Luminance(a), second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static Button[] Tiles(FilmPlaybackView view, string host) =>
        Visuals(host == "EpisodeShelf" ? EpisodeItems(view) : Named<ItemsControl>(view, host))
            .OfType<Button>().Where(button => button.Tag is not null).ToArray();

    private static Rect Bounds(FrameworkElement element, UIElement root) =>
        element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void CheckData(FilmPlaybackPage page)
    {
        Check(new[] { page.Title, page.Metadata, page.Genres, page.Directors, page.Cast, page.Overview, page.SeasonLabel }
            .All(value => !string.IsNullOrWhiteSpace(value)), "explicit metadata is rendered completely");
        if (page.Request.Target.Kind == "tv")
            Check(page.Episodes.Count == 12 && page.Episodes.All(episode => new[]
            { episode.Title, episode.Overview, episode.Image, episode.Duration, episode.Label }.All(value => value.Length > 0)) &&
                page.Episodes.Count(episode => episode.IsSelected) == 1 && page.SelectedEpisode is { IsSelected: true },
                "the complete supplied season has actual coordinates, images and one selected target");
        Check(page.Recommendations.Count > 0 && !page.HasOutput, "recommendations do not imply media playback");
    }

    private static async Task CheckEpisodeContent(ItemsControl host, FilmPlaybackPage page)
    {
        Check(host.Items.Count == page.Episodes.Count, "the episode presentation binds the complete season");
        foreach (var episode in page.Episodes)
        {
            var button = await EpisodeButton(host, episode);
            var texts = Visuals(button).OfType<TextBlock>().Select(text => text.Text).ToArray();
            Check(texts.Contains(episode.Label) && texts.Contains(episode.Duration) && texts.Contains(episode.Overview) &&
                Visuals(button).OfType<Image>().Any(image => image.Source is not null),
                "each episode button renders its bound identity duration synopsis and still image");
        }
    }

    private static void CheckSelection(Scene scene, FilmEpisodeItem episode)
    {
        var selected = scene.Page.SelectedEpisode;
        Check(selected is { IsSelected: true } && selected.Season == episode.Season && selected.Number == episode.Number &&
            scene.Page.Episodes.Count(candidate => candidate.IsSelected) == 1 &&
            scene.Page.CurrentLabel.Length == 0 && !scene.Page.HasOutput,
            "one exact target is selected, but no player title is invented before actual media output");
        var host = scene.Page.IsListLayout ? Named<ItemsControl>(scene.Playback, "EpisodeList") : EpisodeItems(scene.Playback);
        var badges = Visuals(host).OfType<TextBlock>().Where(text => FilmUiTest.Visible(text) && text.Text == "当前").ToArray();
        var selectedButton = Visuals(host).OfType<Button>().SingleOrDefault(button => ReferenceEquals(button.Tag, selected));
        Check(selectedButton is null ? badges.Length == 0 :
            badges.Length == 1 && Visuals(selectedButton).Contains(badges[0]),
            $"only the realized selected episode owns the current-target badge; layout={scene.Page.IsListLayout}, " +
            $"selected={selected?.Number}, button={(selectedButton is null ? "none" : Describe(selectedButton))}, " +
            $"badges={badges.Length}[{string.Join("; ", badges.Select(Describe))}], " +
            $"rows=[{string.Join("; ", Visuals(host).OfType<Button>().Where(button => button.Tag is FilmEpisodeItem).Select(Describe))}]");

        string Describe(FrameworkElement element)
        {
            Button? button = null;
            ListViewItem? container = null;
            for (DependencyObject? node = element; node is not null && !ReferenceEquals(node, host); node = VisualTreeHelper.GetParent(node))
            {
                if (node is Button owner) button = owner;
                if (node is ListViewItem row) container = row;
            }
            var item = button?.Tag as FilmEpisodeItem;
            return $"episode={item?.Number}, selected={item?.IsSelected}, loaded={element.IsLoaded}, " +
                $"visible={FilmUiTest.Visible(element)}, bounds={Bounds(element, host)}, " +
                $"container={(host is ListView list && container is not null ? list.IndexFromContainer(container) : -1)}";
        }
    }

    private static void CheckInsideViewport(ScrollViewer scroll, FrameworkElement element)
    {
        var origin = element.TransformToVisual(scroll).TransformPoint(new Point());
        Check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
            scroll.ScrollableWidth < .5 && scroll.ExtentWidth <= scroll.ViewportWidth + .5 &&
            origin.X >= -.5 && origin.X + element.ActualWidth <= scroll.ViewportWidth + .5,
            "page content stays within the vertical viewport while rails own horizontal scrolling");
    }

    private static ItemsControl EpisodeItems(FilmPlaybackView view) =>
        Named<ItemsControl>(Named<Shelf>(view, "EpisodeShelf"), "ItemsHost");

    private static async Task<Button> EpisodeButton(ItemsControl host, FilmEpisodeItem episode)
    {
        Button? button = null;
        if (host is ListView list)
        {
            if (ParentOfShelf(host) is { } shelf)
            {
                shelf.ScrollToIndex(list.Items.IndexOf(episode));
                await FilmPlaybackFixture.Until(() => shelf.IsSettled);
            }
            else list.ScrollIntoView(episode, ScrollIntoViewAlignment.Leading);
            await FilmPlaybackFixture.Until(() =>
            {
                list.UpdateLayout();
                if (list.ContainerFromItem(episode) is not FrameworkElement container) return false;
                button = Visuals(container).OfType<Button>().SingleOrDefault(candidate => ReferenceEquals(candidate.Tag, episode));
                return button is { IsLoaded: true };
            });
        }
        return button ?? Visuals(host).OfType<Button>().Single(candidate => ReferenceEquals(candidate.Tag, episode));

        static Shelf? ParentOfShelf(DependencyObject node)
        {
            for (var parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is Shelf shelf) return shelf;
            return null;
        }
    }

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        FilmUiTest.Named<T>(root, name);

    private static void Click(ButtonBase button)
    {
        Check(FilmUiTest.Visible(button) && button.IsEnabled, "the clicked control is available");
        button.StartBringIntoView();
        FilmUiTest.Click(button);
    }

    private static void InvokeAction(ButtonBase button)
    {
        Check(FilmUiTest.Visible(button) && button.IsEnabled && button.IsTabStop, "the action remains accessible");
        FilmUiTest.Click(button);
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++) await FilmUiTest.Pump();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly StaticFilmSource Source = new();
        internal readonly FilmRuntimeProbe Runtime = new();
        internal readonly FilmNativeProbe Native = new();
        internal readonly FilmMemoryStore Store = new();
        internal readonly FilmCapability Module;
        internal readonly FilmView View;
        internal readonly Border Canvas;
        internal readonly Window Window;
        internal FilmPlaybackPage Page => Module.Playback ?? throw new InvalidOperationException("Missing playback model");
        internal FilmPlaybackView Playback => Named<FilmPlaybackView>(View, "PlaybackView");

        internal Scene(double width = 1160)
        {
            Module = new(Source, new FilmPlaybackSession(Runtime.CaptureConnectionAsync, Native), new FilmLibrary(Store));
            View = (FilmView)Module.View;
            // Explicit content width avoids native offscreen window constraints.
            View.Width = width;
            Canvas = new() { Child = View };
            Canvas.SetBinding(FrameworkElement.WidthProperty, new Binding { Path = new PropertyPath(nameof(View.Width)), Source = View });
            Canvas.UseResource(Border.BackgroundProperty, "MeowFilm.Brush.AppBackground");
            Window = FilmUiTest.Window(Canvas, width, 800);
            Window.Resources["MeowFilm.Brush.AppBackground"] = AppearanceColors.DefaultBackground;
            Window.Show();
            DesktopTestUi.WindowRoot(Window).UpdateLayout();
        }

        internal Task OpenAsync()
        {
            var card = FilmPlaybackFixture.Card(title: "显式离线影片");
            return Module.OpenPlaybackAsync(card, card.PlaybackContext!);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Content = null;
            Window.Close();
            await Module.DisposeAsync();
            await Runtime.DisposeAsync();
        }
    }

    private sealed class StaticFilmSource : IFilmHomeSource
    {
        internal int Calls { get; private set; }

        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(
            FilmSectionDefinition section, int start, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            IReadOnlyList<FilmCardItem> items = Enumerable.Range(1, 12)
                .Select(index => FilmPlaybackFixture.Card(section.Kind == "movie" ? "movie" : "tv", 100 + index, $"{section.Title} 本地影片 {index}"))
                .Skip(start).Take(limit).ToArray();
            return Task.FromResult(items);
        }

        public void Dispose() { }
    }
}

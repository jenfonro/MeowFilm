using System.Windows;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class FilmPlaybackTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-playback: unseeded route is empty and explicit wire cards preserve movie or series identity",
            () => MusicQualityConsumerTests.Sta(ExplicitData));
        yield return new("film-playback: episode buttons and mouse or keyboard layout switches retain one selection",
            () => MusicQualityConsumerTests.Sta(EpisodeInteraction));
        yield return new("film-playback: unavailable player and responsive page keep scrolling in each region",
            () => MusicQualityConsumerTests.Sta(ResponsivePlayer));
        yield return new("film-playback: recommendations and navigation restore exact identity and coordinates without old presentation state",
            () => MusicQualityConsumerTests.Sta(NavigationRecovery));
        yield return new("film-playback: a captured recommendation opens its exact identity after the displayed recommendation list changes",
            () => MusicQualityConsumerTests.Sta(StaleRecommendation));
        yield return new("film-playback: flat tiles mark one current episode and keep system outlines in high contrast",
            () => MusicQualityConsumerTests.Sta(FlatTiles));
        yield return new("film-playback: hover or focus lifts episode cards rows and recommendations around a centered play mark",
            () => MusicQualityConsumerTests.Sta(HoverLift));
        yield return new("film-playback: poster covers the whole information region behind its foreground",
            () => MusicQualityConsumerTests.Sta(BackdropPoster));
        yield return new("film-playback: centered hero actions allow retry and persist each film favorite",
            () => MusicQualityConsumerTests.Sta(HeroActions));
        yield return new("film-playback: overlay text stays readable on bright dark and missing posters",
            () => MusicQualityConsumerTests.Sta(OverlayReadability));
        yield return new("film-playback: ambient tint follows the poster stays fixed in the viewport and keeps text readable",
            () => MusicQualityConsumerTests.Sta(AmbientTint));
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
        Check(!scene.Playback.IsVisible && Named<TextBlock>(scene.View, "PlaybackEmpty").IsVisible,
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
            Check(ReferenceEquals(scene.Playback.DataContext, page) && Named<Grid>(scene.View, "PlaybackPage").IsVisible,
                "FilmView binds the seeded playback page");
            var playerOverlay = Named<Grid>(scene.Playback, "PlayerOverlay");
            var shortVideo = Named<CheckBox>(scene.Playback, "ShortVideoButton");
            Check(Visuals(scene.Playback).OfType<FrameworkElement>()
                    .Where(element => !element.IsDescendantOf(playerOverlay) && !ReferenceEquals(element, shortVideo))
                    .All(element => element.ToolTip is null),
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
        var cards = Named<ItemsControl>(view, "EpisodeCards");
        var list = Named<ItemsControl>(view, "EpisodeList");
        var rail = Named<ScrollViewer>(view, "HorizontalEpisodes");
        Check(page.Episodes.Count > 1 && page.IsHorizontalLayout && !page.IsListLayout &&
            rail.IsVisible && !list.IsVisible, "the explicit TMDB snapshot starts with horizontal episode cards");
        CheckEpisodeContent(cards, page);
        var chosen = page.Episodes[1];
        Click(EpisodeButton(cards, chosen));
        await Drain();
        CheckSelection(scene, chosen);

        Click(Named<Button>(view, "ListLayout"));
        await Drain();
        Check(page.IsListLayout && !page.IsHorizontalLayout && list.IsVisible && !rail.IsVisible,
            "the list button switches the actual episode presentation");
        CheckSelection(scene, chosen);
        CheckEpisodeContent(list, page);
        var last = page.Episodes[^1];
        Click(EpisodeButton(list, last));
        await Drain();
        CheckSelection(scene, last);

        PressEnter(Named<Button>(view, "HorizontalLayout"));
        await Drain();
        Check(page.IsHorizontalLayout && !page.IsListLayout && rail.IsVisible && !list.IsVisible,
            "Enter activates the horizontal layout button");
        CheckSelection(scene, last);
        PressEnter(Named<Button>(view, "ListLayout"));
        await Drain();
        Check(page.IsListLayout && list.IsVisible, "Enter also activates the list layout button");
        CheckSelection(scene, last);
        Click(Named<Button>(view, "HorizontalLayout"));
        await Drain();
        Check(page.IsHorizontalLayout && rail.IsVisible, "the mouse action returns to horizontal cards");
        CheckSelection(scene, last);

        rail.ScrollToLeftEnd();
        await Drain();
        var previous = Named<Button>(view, "EpisodePrevious");
        var next = Named<Button>(view, "EpisodeNext");
        Check(rail.ScrollableWidth > 0 && !previous.IsEnabled && next.IsEnabled,
            "only the forward action is available at the start of the episode rail");
        Click(next);
        await Drain();
        Check(rail.HorizontalOffset > 0 && previous.IsEnabled, "next pages the actual episode viewport");
        Click(previous);
        await Drain();
        Check(rail.HorizontalOffset < .5 && !previous.IsEnabled, "previous returns to the rail boundary");
        CheckSelection(scene, last);
        Check(scene.Source.Calls == 0, "episode and layout interactions remain entirely local");
    }

    private static async Task ResponsivePlayer()
    {
        await using var scene = new Scene(840);
        await scene.OpenAsync();
        await Drain();
        var view = scene.Playback;
        var player = Named<Grid>(view, "PlayerFrame");
        var metadata = Named<Grid>(view, "MetadataPanel");
        var hero = Named<Grid>(view, "HeroGrid");
        var scroll = Named<ScrollViewer>(view, "PageScroll");
        var play = Named<Button>(view, "PlayButton");
        Check(play.IsEnabled && Named<TextBlock>(view, "PlayerStatus").Text == "暂无可用片源" &&
            AutomationProperties.GetName(play) == "播放或暂停" && scene.Native.Commits == 0,
            "an unavailable snapshot remains honestly empty while allowing an explicit playback retry");

        foreach (double width in new[] { 1160d, 840d, 839d, 700d })
        {
            scene.View.Width = width;
            scene.Window.Width = width;
            scene.Window.UpdateLayout();
            await Drain();
            bool stacked = width < 840;
            Check(Math.Abs(view.ActualWidth - width) < .5, "responsive test uses the content width");
            var playerOrigin = player.TranslatePoint(new Point(), hero);
            var metadataOrigin = metadata.TranslatePoint(new Point(), hero);
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
                details.ScrollToBottom();
                await Drain();
                Check(details.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - pageOffset) < .5,
                    "scrolling metadata leaves the page viewport in place");
                CheckBackgroundCoverage(view);
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
        Check(scroll.ScrollableHeight > 0, "narrow content scrolls vertically");
        scroll.ScrollToBottom();
        await Drain();
        Check(scroll.VerticalOffset > 0, "the page scroll reaches content below the player");
        Click(Named<Button>(view, "ListLayout"));
        await Drain();
        CheckInsideViewport(scroll, Named<ItemsControl>(view, "EpisodeList"));
        CheckInsideViewport(scroll, Named<Grid>(view, "MetadataPanel"));
        Check(play.IsEnabled && !scene.Page.HasOutput && scene.Native.Commits == 0 && scene.Source.Calls == 0,
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
        Click(EpisodeButton(Named<ItemsControl>(scene.Playback, "EpisodeCards"), chosen));
        Click(Named<Button>(scene.Playback, "ListLayout"));
        await Drain();
        string location = module.CaptureNavigationLocation();
        int calls = scene.Source.Calls;
        Check(location.Contains(item.Id, StringComparison.Ordinal) && original.IsListLayout,
            "captured navigation includes the exact film and selected coordinate");
        scene.View.Visibility = Visibility.Collapsed;
        await Drain();
        scene.View.Visibility = Visibility.Visible;
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        Check(!ReferenceEquals(scene.Page, original) && !scene.Page.IsListLayout &&
            scene.Page.Request.Target.Id == original.Request.Target.Id && ReferenceEquals(scene.Playback.DataContext, scene.Page),
            "restoration creates an accurate request, not an old search or presentation object");
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
        Check(scene.Page.Request.Target.Id == original.Request.Target.Id && !scene.Page.IsListLayout,
            "the original location remains restorable without retaining an old layout");
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
        scene.Runtime.Change(root => root["metadata"]!["similar"]!["results"] = new JsonArray());
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
        var backdropLayer = Named<Grid>(view, "MetadataBackdrop");
        Check(!Visuals(metadata).OfType<Border>().Any(border => border.IsVisible &&
                !border.IsDescendantOf(backdropLayer) && border.TemplatedParent is not ButtonBase &&
                ((border.BorderThickness != default && border.BorderBrush is SolidColorBrush { Color.A: > 0 }) ||
                (border.ActualWidth > metadata.ActualWidth / 2 && border.Background is SolidColorBrush { Color.A: > 0 }))),
            "metadata sits directly on the page without a surface or outlined card");
        CheckFlat(view, page, outline: false);
        CheckCurrentStatus(view, page);
        var chosen = page.Episodes[2];
        Click(EpisodeButton(Named<ItemsControl>(view, "EpisodeCards"), chosen));
        await Drain();
        CheckSelection(scene, chosen);
        CheckCurrentStatus(view, page);
        Click(Named<Button>(view, "ListLayout"));
        await Drain();
        CheckSelection(scene, chosen);
        CheckCurrentStatus(view, page);

        DesktopTheme.SetIsHighContrast(scene.Window, true);
        await Drain();
        Check(!Named<Border>(view, "AmbientLayer").IsVisible && !Named<FilmPlaybackPoster>(view, "Backdrop").IsVisible,
            "high contrast removes the ambient tint and poster backdrop");
        CheckFlat(view, page, outline: true);
        CheckSelection(scene, chosen);
        Click(Named<Button>(view, "HorizontalLayout"));
        await Drain();
        CheckFlat(view, page, outline: true);
        CheckSelection(scene, chosen);
        Check(scene.Source.Calls == 0, "presentation states remain local");
    }

    private static void CheckFlat(FilmPlaybackView view, FilmPlaybackPage page, bool outline)
    {
        var text = SystemColors.WindowTextColor;
        var highlight = SystemColors.HighlightColor;
        string host = page.IsListLayout ? "EpisodeList" : "EpisodeCards";
        foreach (var tile in Tiles(view, host).Concat(Tiles(view, "RelatedItems")))
        {
            var frame = (Border)VisualTreeHelper.GetChild(tile, 0);
            bool current = tile.Tag is FilmEpisodeItem { IsSelected: true };
            Check(tile.Background is SolidColorBrush { Color.A: 0 } && frame.Background is SolidColorBrush { Color.A: 0 },
                "tiles have no resting surface");
            Check(outline
                ? frame.BorderThickness == new Thickness(2) && frame.BorderBrush is SolidColorBrush brush &&
                    brush.Color == (current ? highlight : text)
                : frame.BorderThickness == default,
                outline ? "high contrast outlines tiles with WindowText and the current episode with Highlight"
                    : "tiles have no resting border");
        }
        foreach (var row in Visuals(Named<ItemsControl>(view, "EpisodeList")).OfType<Border>()
            .Where(border => border.Name == "RowHover"))
            Check(outline ? !row.IsVisible : row.Opacity == 0, "the list row hover surface rests transparent");
    }

    private static void CheckCurrentStatus(FilmPlaybackView view, FilmPlaybackPage page)
    {
        foreach (var tile in Tiles(view, page.IsListLayout ? "EpisodeList" : "EpisodeCards"))
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
        await CheckLift(Tiles(view, "EpisodeCards")[1], Named<ItemsControl>(view, "EpisodeCards"), 144);
        await CheckLift(Tiles(view, "RelatedItems")[0], Named<ItemsControl>(view, "RelatedItems"), 234);
        Click(Named<Button>(view, "ListLayout"));
        await Drain();
        var row = Tiles(view, "EpisodeList")[1];
        var rowHover = Visuals(row).OfType<Border>().Single(border => border.Name == "RowHover");
        await CheckLift(row, Named<ItemsControl>(view, "EpisodeList"), 99);
        CardMotion.Transition(row, true, animate: false);
        await Drain();
        Check(rowHover.Opacity == 1, "a lifted list row fades in its soft hover surface");
        CardMotion.Transition(row, false, animate: false);
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
        Check(Math.Abs(rest.Width - tile.ActualWidth) < .5 && mark.Opacity == 0 && shade.Opacity == 0,
            "a resting tile has no lift scrim or play mark");
        Check(Math.Abs(frame.ActualHeight - frameHeight) < .5, "tile artwork keeps its frame size");

        CardMotion.Transition(tile, true, animate: false);
        await Drain();
        var lifted = Bounds(tile, host);
        double scale = lifted.Width / rest.Width;
        Check(scale is > 1.005 and < 1.06 && Math.Abs(lifted.Height / rest.Height - scale) < .001,
            "the whole tile lifts slightly on hover or focus");
        Check(image.RenderTransform is ScaleTransform { ScaleX: > 1.04 and < 1.06 },
            "the artwork zooms inside its clipped frame");
        Check(mark.Opacity == 1 && shade.Opacity == 1, "lifted artwork shows the shared scrim and play mark");
        var center = mark.TranslatePoint(new Point(mark.ActualWidth / 2, mark.ActualHeight / 2), frame);
        Check(Math.Abs(center.X - frame.ActualWidth / 2) <= .5 && Math.Abs(center.Y - frame.ActualHeight / 2) <= .5,
            "the play mark is centered on the artwork");

        CardMotion.Transition(tile, false, animate: false);
        await Drain();
        var settled = Bounds(tile, host);
        Check(Math.Abs(settled.Width - rest.Width) < .01 && mark.Opacity == 0 && shade.Opacity == 0,
            "the tile settles back without residue");
    }

    private static async Task BackdropPoster()
    {
        const string poster = "pack://application:,,,/MeowFilm.Capabilities;component/Film/Presentation/Assets/poster-long-season.jpg";
        const string still = "pack://application:,,,/MeowFilm.Capabilities;component/Film/Presentation/Assets/episode-01.jpg";
        var (backdrop, image) = Backdrop(poster, 400, 300);
        Check(backdrop.DesiredSize == default, "the backdrop never asks its panel for size");
        CheckSlot(backdrop, image, new Rect(0, -150, 400, 600), "a portrait fills the entire frame with a centered crop");
        var pixels = Pixels((FrameworkElement)backdrop.Parent);
        Check(new[] { (1, 1), (399, 1), (1, 140), (399, 140), (200, 140), (200, 225), (1, 299), (399, 299) }
            .All(point => Alpha(pixels, 400, point.Item1, point.Item2) > 240),
            "the image remains present across the full region including behind the lower content");

        (backdrop, image) = Backdrop(poster, 150, 300);
        CheckSlot(backdrop, image, new Rect(-25, 0, 200, 300), "a narrow frame crops the portrait equally on each side");
        (backdrop, image) = Backdrop(still, 400, 300);
        CheckSlot(backdrop, image, new Rect((400 - 300 * 16d / 9) / 2, 0, 300 * 16d / 9, 300),
            "a landscape image covers the frame with a centered crop and no stretching");

        await using var scene = new Scene();
        await scene.OpenAsync();
        await Drain();
        var view = scene.Playback;
        var panel = Named<Grid>(view, "MetadataPanel");
        var real = Named<FilmPlaybackPoster>(view, "Backdrop");
        var preview = real.Children.OfType<Image>().Single();
        var slot = Bounds(preview, real);
        Check(real.IsVisible && real.Opacity == 1 &&
            Math.Abs(preview.ActualWidth / preview.ActualHeight - 2d / 3) < .01 &&
            slot.Left <= 0 && slot.Top <= 0 && slot.Right >= real.ActualWidth && slot.Bottom >= real.ActualHeight &&
            Math.Abs(slot.Left + slot.Width / 2 - real.ActualWidth / 2) < 1,
            "the wide information background keeps the poster ratio");
        CheckBackgroundCoverage(view);
        scene.View.Width = 700;
        scene.Window.Width = 700;
        scene.Window.UpdateLayout();
        await Drain();
        var details = Named<ScrollViewer>(view, "MetadataScroll");
        Check(double.IsNaN(panel.Height) && Math.Abs(panel.ActualHeight - details.DesiredSize.Height) < 1 &&
            Math.Abs(preview.ActualWidth / preview.ActualHeight - 2d / 3) < .01,
            "stacked metadata sizes to its content without stretching the poster");
        CheckBackgroundCoverage(view);
        double collapsedHeight = panel.ActualHeight;
        Click(Named<Button>(view, "OverviewToggle"));
        await Drain();
        Check(panel.ActualHeight > collapsedHeight && Named<TextBlock>(view, "OverviewText").IsVisible,
            "the expanded overview grows the narrow information region");
        CheckBackgroundCoverage(view);
    }

    private static void CheckBackgroundCoverage(FilmPlaybackView view)
    {
        var panel = Named<Grid>(view, "MetadataPanel");
        var backdrop = Named<FilmPlaybackPoster>(view, "Backdrop");
        var bounds = Bounds(backdrop, panel);
        Check(bounds.TopLeft == default && Math.Abs(bounds.Width - panel.ActualWidth) < 1 &&
            Math.Abs(bounds.Height - panel.ActualHeight) < 1,
            "the poster covers the whole information region, not a separate image above it");
        foreach (var element in new FrameworkElement[] { Named<TextBlock>(view, "FilmTitle"),
            Named<TextBlock>(view, "FilmMetadata"), Named<StackPanel>(view, "HeroActions"),
            Named<Button>(view, "OverviewToggle"), Named<TextBlock>(view, "OverviewText") })
        {
            if (!element.IsVisible) continue;
            var visible = Bounds(element, panel);
            visible.Intersect(new Rect(panel.RenderSize));
            if (!visible.IsEmpty)
                Check(bounds.Contains(visible), "every visible foreground element is superimposed on the poster");
        }
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
        var actions = Named<StackPanel>(view, "HeroActions");
        var favorite = Named<ToggleButton>(view, "FavoriteButton");
        var play = Named<Button>(view, "HeroPlayButton");
        Check(title.TextAlignment == TextAlignment.Center && metadata.TextAlignment == TextAlignment.Center &&
            content.IsDescendantOf(details) && !Named<FilmPlaybackPoster>(view, "Backdrop").IsDescendantOf(details),
            "identity and actions scroll in front of a background covering their whole viewport");
        foreach (double width in new[] { 1237d, 907d, 840d })
        {
            scene.Window.Width = scene.View.Width = width;
            scene.Window.UpdateLayout();
            details.ScrollToTop();
            await Drain();
            foreach (var element in new FrameworkElement[] { title, metadata, Named<WrapPanel>(view, "FilmFacts"), actions })
            {
                var bounds = Bounds(element, content);
                Check(Math.Abs(bounds.Left + bounds.Width / 2 - content.ActualWidth / 2) <= 1,
                    "identity facts and the action group share the image center");
            }
            Check(Bounds(title, details).Top >= 0 && Bounds(actions, details).Bottom <= details.ViewportHeight + 1 &&
                Bounds(Named<Button>(view, "OverviewToggle"), details).Bottom <= details.ViewportHeight + 1,
                "the short title playback favorite and overview action fit the desktop metadata viewport");
            CheckBackgroundCoverage(view);
        }
        Check(play.IsEnabled && AutomationProperties.GetName(play) == "播放或暂停" &&
            Visuals(play).OfType<TextBlock>().Any(text => text.Text == "播放") && !page.HasOutput,
            "the hero offers explicit retry without claiming a native media output");
        Check(favorite.IsEnabled && favorite.IsTabStop && favorite.Focusable && !page.IsFavorite &&
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
        await module.OpenRecommendationAsync(page.Recommendations[0]);
        await Drain();
        Check(!scene.Page.IsFavorite && favorite.IsChecked == false,
            "a different film has independent favorite state");
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        page = scene.Page;
        Check(page.IsFavorite && favorite.IsChecked == true && scene.Store.Writes == 2,
            "return navigation reads the original film favorite from its one durable owner");
        favorite.BringIntoView();
        var source = PresentationSource.FromVisual(favorite)!;
        var keyboard = new KeyboardProbe();
        favorite.RaiseEvent(new KeyEventArgs(keyboard, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
        favorite.RaiseEvent(new KeyEventArgs(keyboard, source, 1, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
        await Drain();
        Check(!page.IsFavorite && favorite.IsChecked == false &&
            Named<TextBlock>(view, "FavoriteLabel").Text == "收藏" && scene.Store.Writes == 3,
            "Space removes the favorite once and updates its label without a second binding-triggered write");
        var overview = Named<TextBlock>(view, "OverviewText");
        var overviewToggle = Named<Button>(view, "OverviewToggle");
        Check(!page.OverviewExpanded && overview.Visibility == Visibility.Collapsed &&
            overview.DesiredSize == default && (string)overviewToggle.Content == "简介",
            "only the overview action is initially visible; the body takes no layout space");
        double collapsedHeight = content.ActualHeight;
        Click(overviewToggle);
        await Drain();
        Check(page.OverviewExpanded && overview.IsVisible && overview.Text == page.Overview &&
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
        Check(!page.OverviewExpanded && !overview.IsVisible, "new presentation does not reuse an old expanded overview");
        PressEnter(overviewToggle);
        await Drain();
        Check(page.OverviewExpanded && overview.IsVisible, "Enter expands the actual current presentation");
        PressEnter(overviewToggle);
        await Drain();
        Check(!page.OverviewExpanded && overview.Visibility == Visibility.Collapsed &&
            Math.Abs(content.ActualHeight - collapsedHeight) < 1 &&
            AutomationProperties.GetItemStatus(overviewToggle) == "已收起",
            "Enter collapses the overview and reclaims its layout space");
        DesktopTheme.SetIsHighContrast(scene.Window, true);
        details.ScrollToTop();
        await Drain();
        Check(!Named<FilmPlaybackPoster>(view, "Backdrop").IsVisible && !Named<Border>(view, "MetadataWash").IsVisible &&
            Bounds(title, content).Top < 1 &&
            favorite.Foreground is SolidColorBrush ink && ink.Color == SystemColors.WindowTextColor &&
            play.Foreground is SolidColorBrush actionInk && actionInk.Color == SystemColors.HighlightTextColor,
            "high contrast reclaims the image space and uses system colors for both actions");
        Check(scene.Source.Calls == 0, "hero and favorite interactions issue no source requests");
    }

    private static async Task OverlayReadability()
    {
        await using var scene = new Scene(907);
        await scene.OpenAsync();
        await Drain();
        var view = scene.Playback;
        var panel = Named<Grid>(view, "MetadataPanel");
        var scroll = Named<ScrollViewer>(view, "MetadataScroll");
        var poster = Named<FilmPlaybackPoster>(view, "Backdrop");
        var image = poster.Children.OfType<Image>().Single();
        foreach (bool dark in new[] { false, true })
        {
            var window = dark ? Color.FromRgb(0x19, 0x1B, 0x20) : Color.FromRgb(0xF7, 0xF8, 0xFA);
            var text = dark ? Color.FromRgb(0xF1, 0xF2, 0xF4) : Color.FromRgb(0x30, 0x34, 0x43);
            view.Resources["MeowFilm.Brush.Window"] = new SolidColorBrush(window);
            view.Resources["MeowFilm.Brush.Text"] = new SolidColorBrush(text);
            panel.Background = new SolidColorBrush(window);
            foreach (byte? value in new byte?[] { 0, 255, null })
            {
                image.Source = value is byte channel
                    ? BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { channel, channel, channel, 255 }, 4)
                    : null;
                poster.InvalidateArrange();
                await Drain();
                var ink = ((SolidColorBrush)Named<TextBlock>(view, "FilmMetadata").Foreground).Color;
                Check(ink == text, "the contrast experiment uses the requested theme's actual text color");
                scroll.Visibility = Visibility.Hidden;
                await Drain();
                var pixels = Pixels(panel);
                int width = (int)panel.ActualWidth, height = (int)panel.ActualHeight;
                double worst = 99;
                for (int y = 22; y < height - 22; y += 4)
                    for (int x = 20; x < width - 20; x += 4)
                    {
                        int offset = (y * width + x) * 4;
                        Check(pixels[offset + 3] == 255, "contrast samples must contain the painted information background");
                        var color = Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
                        worst = Math.Min(worst, Contrast(ink, color));
                    }
                scroll.Visibility = Visibility.Visible;
                await Drain();
                Check(worst >= 4.5, $"overlay text stays readable for {(dark ? "dark" : "light")} theme and poster {value}: {worst}");
            }
        }
    }

    private static (FilmPlaybackPoster Poster, Image Image) Backdrop(string address, double width, double height)
    {
        // A host keeps the poster's crop in the rendered visual tree.
        var poster = new FilmPlaybackPoster { IsBackdrop = true, Address = address };
        var host = new Grid { Width = width, Height = height, Children = { poster } };
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        return (poster, poster.Children.OfType<Image>().Single());
    }

    private static void CheckSlot(FilmPlaybackPoster poster, Image image, Rect slot, string message)
    {
        var origin = image.TranslatePoint(new Point(), poster);
        Check(image.Visibility == Visibility.Visible && Math.Abs(origin.X - slot.X) < .5 && Math.Abs(origin.Y - slot.Y) < .5 &&
            Math.Abs(image.ActualWidth - slot.Width) < .5 && Math.Abs(image.ActualHeight - slot.Height) < .5 &&
            poster.ClipToBounds, message);
    }

    private static byte[] Pixels(FrameworkElement element)
    {
        int width = (int)element.ActualWidth, height = (int)element.ActualHeight;
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        // Render into local coordinates; a mounted panel's parent offset is outside this bitmap.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static byte Alpha(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 3];

    private static async Task AmbientTint()
    {
        await using var scene = new Scene();
        var module = scene.Module;
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var section = module.Sections.Single(candidate => candidate.Key == "tv");
        await module.OpenPlaybackAsync(section.Items[0]);
        await Drain();
        var view = scene.Playback;
        var tint = Named<Border>(view, "AmbientTint");
        var window = (SolidColorBrush)view.FindResource("MeowFilm.Brush.Window");
        var first = CheckTint(view, tint, window);

        var scroll = Named<ScrollViewer>(view, "PageScroll");
        var origin = tint.TranslatePoint(new Point(), view);
        Check(Math.Abs(tint.ActualHeight - view.ActualHeight) < .5, "the ambient tint fills the playback viewport");
        scroll.ScrollToEnd();
        await Drain();
        Check(scroll.VerticalOffset > 0 && tint.TranslatePoint(new Point(), view) == origin,
            "the ambient tint stays fixed while the page scrolls");

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
        Check(scene.Page.Item.Id == recommendation.Id, "the recommendation opens its own film identity");
        Check(CheckTint(view, tint, window) != first, "the ambient tint follows the newly opened poster");

        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var blank = FilmPlaybackFixture.RefreshMetadata(section.Items[1] with { Poster = "" });
        section.Items[1] = blank;
        await module.OpenPlaybackAsync(blank);
        await Drain();
        Check(scene.Page.Item.Id == blank.Id && Named<FilmPlaybackPoster>(view, "Backdrop").PaletteBrush is null &&
            tint.Background is SolidColorBrush { Color.A: 0 }, "a film without a poster has no ambient tint");

        var converter = new FilmAmbientTintConverter();
        Check(converter.Convert([null!, window], typeof(Brush), null!, null!) is SolidColorBrush { Color.A: 0 } &&
            converter.Convert([DependencyProperty.UnsetValue, window], typeof(Brush), null!, null!) is SolidColorBrush { Color.A: 0 },
            "a missing palette converts to transparent");
        var themes = new[]
        {
            (Window: window.Color, Text: ((SolidColorBrush)view.FindResource("MeowFilm.Brush.Text")).Color,
                Muted: ((SolidColorBrush)view.FindResource("MeowFilm.Brush.Muted")).Color),
            (Window: Color.FromRgb(0x19, 0x1B, 0x20), Text: Color.FromRgb(0xF1, 0xF2, 0xF4), Muted: Color.FromRgb(0xB3, 0xB8, 0xC5))
        };
        foreach (var theme in themes)
            for (int hue = 0; hue < 360; hue += 3)
                foreach (var saturation in new[] { 0, .08, .25, 1 })
                {
                    var color = ((SolidColorBrush)converter.Convert([new SolidColorBrush(Hsl(hue, saturation, .4)),
                        new SolidColorBrush(theme.Window)], typeof(Brush), null!, null!)).Color;
                    Check(color.A == 255 && color != theme.Window &&
                        Contrast(theme.Text, color) >= 4.5 && Contrast(theme.Muted, color) >= 4.5,
                        "every poster hue keeps text and muted text readable on the ambient tint");
                }
    }

    private static Color CheckTint(FilmPlaybackView view, Border tint, SolidColorBrush window)
    {
        var color = tint.Background is SolidColorBrush brush ? brush.Color : default;
        var text = ((SolidColorBrush)view.FindResource("MeowFilm.Brush.Text")).Color;
        var muted = ((SolidColorBrush)view.FindResource("MeowFilm.Brush.Muted")).Color;
        Check(Named<FilmPlaybackPoster>(view, "Backdrop").PaletteBrush is not null && color.A == 255 &&
            color != window.Color && Contrast(text, color) >= 4.5 && Contrast(muted, color) >= 4.5,
            "the poster tints the page while text and muted text stay readable");
        return color;
    }

    private static Color Hsl(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation, h = hue / 60;
        double x = chroma * (1 - Math.Abs(h % 2 - 1)), m = lightness - chroma / 2;
        var (r, g, b) = h switch
        {
            < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
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
        Visuals(Named<ItemsControl>(view, host)).OfType<Button>().Where(button => button.Tag is not null).ToArray();

    private static Rect Bounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

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

    private static void CheckEpisodeContent(ItemsControl host, FilmPlaybackPage page)
    {
        Check(host.Items.Count == page.Episodes.Count, "the episode presentation binds the complete season");
        foreach (var episode in page.Episodes)
        {
            var button = EpisodeButton(host, episode);
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
        var host = Named<ItemsControl>(scene.Playback, scene.Page.IsListLayout ? "EpisodeList" : "EpisodeCards");
        var badges = Visuals(host).OfType<TextBlock>().Where(text => text.IsVisible && text.Text == "当前").ToArray();
        Check(badges.Length == 1 && Visuals(EpisodeButton(host, selected!)).Contains(badges[0]),
            "the actual selected episode has the sole current-target badge");
    }

    private static void CheckInsideViewport(ScrollViewer scroll, FrameworkElement element)
    {
        var origin = element.TranslatePoint(new Point(), scroll);
        Check(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
            scroll.ScrollableWidth < .5 && scroll.ExtentWidth <= scroll.ViewportWidth + .5 &&
            origin.X >= -.5 && origin.X + element.ActualWidth <= scroll.ViewportWidth + .5,
            "page content stays within the vertical viewport while rails own horizontal scrolling");
    }

    private static Button EpisodeButton(ItemsControl host, FilmEpisodeItem episode) =>
        Visuals(host).OfType<Button>().Single(button => ReferenceEquals(button.Tag, episode));

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException("Missing playback control " + name);

    private static void Click(Button button)
    {
        Check(button.IsVisible && button.IsEnabled, "the clicked control is available");
        button.BringIntoView();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void PressEnter(Button button)
    {
        Check(button.IsVisible && button.IsEnabled && button.Focusable && button.IsTabStop,
            "the layout control supports keyboard activation");
        button.BringIntoView();
        var source = PresentationSource.FromVisual(button) ?? throw new InvalidOperationException("Missing presentation source");
        button.RaiseEvent(new KeyEventArgs(new KeyboardProbe(), source, 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private sealed class KeyboardProbe() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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
        internal readonly Window Window;
        internal FilmPlaybackPage Page => Module.Playback ?? throw new InvalidOperationException("Missing playback model");
        internal FilmPlaybackView Playback => Named<FilmPlaybackView>(View, "PlaybackView");

        internal Scene(double width = 1160)
        {
            Module = new(Source, new FilmPlaybackSession(Runtime.CaptureConnectionAsync, Native), new FilmLibrary(Store));
            View = (FilmView)Module.View;
            // Explicit content width avoids native offscreen window constraints.
            View.Width = width;
            Window = new()
            {
                Content = View, Width = width, Height = 800, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            Window.Show();
            Window.UpdateLayout();
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

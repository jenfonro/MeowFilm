using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        yield return new("film-playback: direct preview and movie or series cards need no detail requests",
            () => MusicQualityConsumerTests.Sta(LocalPreview));
        yield return new("film-playback: episode buttons and mouse or keyboard layout switches retain one selection",
            () => MusicQualityConsumerTests.Sta(EpisodeInteraction));
        yield return new("film-playback: unavailable player and responsive page keep scrolling in each region",
            () => MusicQualityConsumerTests.Sta(ResponsivePlayer));
        yield return new("film-playback: recommendations and navigation restore the cached film episode and layout",
            () => MusicQualityConsumerTests.Sta(NavigationRecovery));
        yield return new("film-playback: flat tiles mark one current episode and keep system outlines in high contrast",
            () => MusicQualityConsumerTests.Sta(FlatTiles));
        yield return new("film-playback: hover or focus lifts episode cards rows and recommendations around a centered play mark",
            () => MusicQualityConsumerTests.Sta(HoverLift));
        yield return new("film-playback: backdrop poster keeps its ratio fades on every side and never sizes metadata",
            () => MusicQualityConsumerTests.Sta(BackdropPoster));
        yield return new("film-playback: ambient tint follows the poster stays fixed in the viewport and keeps text readable",
            () => MusicQualityConsumerTests.Sta(AmbientTint));
    }

    private static async Task LocalPreview()
    {
        await using var scene = new Scene();
        var module = scene.Module;
        await module.InitializeAsync(default);
        await module.NavigateAsync(FilmCapability.PlaybackRoute, default);
        await Drain();
        Check(FilmCapability.PlaybackRoute == "film-playback" &&
            module.IsPlayback && module.Heading == "播放页", "direct route publishes the playback heading");
        Check(scene.Source.Calls == 0, "direct local preview does not load home or film details");
        CheckPreview(scene.Page);
        Check(ReferenceEquals(scene.Playback.DataContext, scene.Page) &&
            Named<Grid>(scene.View, "PlaybackPage").IsVisible,
            "FilmView presents the bound playback page");
        Check(((IDesktopHeaderSource)scene.View).Header is
            { Title: "播放页", SearchVisible: false, SearchEnabled: false },
            "the shell receives the playback heading");

        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        int calls = scene.Source.Calls;
        Check(calls > 0 && module.Sections.All(section => section.Items.Count == 12),
            "home is supplied by the local twelve-card source");
        foreach (string key in new[] { "movie", "tv" })
        {
            var item = module.Sections.Single(section => section.Key == key).Items[0];
            await module.OpenPlaybackAsync(item);
            await Drain();
            var page = scene.Page;
            Check(ReferenceEquals(page.Item, item) && page.Title == item.Title && page.Poster == item.Poster,
                "opening a card retains its identity title and poster");
            CheckPreview(page);
            Check(key == "movie"
                ? page.Episodes.Count == 1 && page.SelectedEpisode.Label.Contains("正片", StringComparison.Ordinal)
                : page.Episodes.Count > 1 && page.Episodes.Select(episode => episode.Number).Distinct().Count() ==
                    page.Episodes.Count,
                "movies offer the feature and series offer distinct episodes with descriptions");
            Check(scene.Source.Calls == calls, "opening playback never asks the source for details");
            await module.RestoreNavigationAsync(FilmCapability.HomeRoute, default);
        }
        Check(scene.Source.Calls == calls, "returning to the loaded home also retains its source results");
    }

    private static async Task EpisodeInteraction()
    {
        await using var scene = new Scene();
        await scene.Module.NavigateAsync(FilmCapability.PlaybackRoute, default);
        await Drain();
        var page = scene.Page;
        var view = scene.Playback;
        var cards = Named<ItemsControl>(view, "EpisodeCards");
        var list = Named<ItemsControl>(view, "EpisodeList");
        var rail = Named<ScrollViewer>(view, "HorizontalEpisodes");
        Check(page.Episodes.Count > 1 && page.IsHorizontalLayout && !page.IsListLayout &&
            rail.IsVisible && !list.IsVisible, "preview starts with the horizontal episode cards");
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
        await scene.Module.NavigateAsync(FilmCapability.PlaybackRoute, default);
        await Drain();
        var view = scene.Playback;
        var player = Named<Grid>(view, "PlayerFrame");
        var metadata = Named<Grid>(view, "MetadataPanel");
        var hero = Named<Grid>(view, "HeroGrid");
        var scroll = Named<ScrollViewer>(view, "PageScroll");
        var play = Named<Button>(view, "PlayButton");
        Check(!play.IsEnabled && Named<TextBlock>(view, "PlayerStatus").Text == "暂无片源" &&
            AutomationProperties.GetName(play).Contains("暂无片源", StringComparison.Ordinal),
            "unavailable playback is disabled with a visible and accessible reason");

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
                Math.Abs(player.ActualHeight - player.ActualWidth * 9 / 16) < 1,
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
                var details = Visuals(metadata).OfType<ScrollViewer>().Single();
                double pageOffset = scroll.VerticalOffset;
                Check(details.ScrollableHeight > 0, "wide metadata has its own vertical scroll range");
                details.ScrollToBottom();
                await Drain();
                Check(details.VerticalOffset > 0 && Math.Abs(scroll.VerticalOffset - pageOffset) < .5,
                    "scrolling metadata leaves the page viewport in place");
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
        Check(!play.IsEnabled && scene.Source.Calls == 0, "resizing and scrolling never enable or resolve playback");
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
        Check(location.StartsWith(FilmCapability.PlaybackRoute, StringComparison.Ordinal) &&
            location.Contains(item.Id, StringComparison.Ordinal), "captured playback location identifies its film");

        scene.View.Visibility = Visibility.Collapsed;
        await Drain();
        scene.View.Visibility = Visibility.Visible;
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        Check(ReferenceEquals(scene.Page, original) && original.IsListLayout &&
            ReferenceEquals(scene.Playback.DataContext, original), "module visibility preserves the same playback model");
        CheckSelection(scene, chosen);
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var updatedCard = item with { Title = item.Title + " 更新", Score = "8.8" };
        var section = module.Sections.Single(section => section.Key == "tv");
        section.Items[0] = updatedCard;
        await module.OpenPlaybackAsync(updatedCard);
        await Drain();
        Check(ReferenceEquals(scene.Page, original) && original.IsListLayout &&
            Named<ItemsControl>(scene.Playback, "EpisodeList").IsVisible &&
            ReferenceEquals(original.Item, updatedCard) &&
            Named<TextBlock>(scene.Playback, "FilmTitle").Text == updatedCard.Title,
            "reopening the same film retains its episode and layout while refreshing the card metadata");
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        Check(ReferenceEquals(scene.Page, original) && original.IsListLayout,
            "return navigation restores the cached model and rendered layout");
        CheckSelection(scene, chosen);

        var routes = new List<string>();
        module.NavigationRequested += (_, request) => routes.Add(request.Route);
        bool rejected = false;
        try { await module.OpenRecommendationAsync(new("outside-recommendations", "其他影片", "", "")); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && routes.Count == 0 && ReferenceEquals(scene.Page, original) &&
            module.CaptureNavigationLocation() == location, "an item outside the current recommendations cannot navigate");

        var recommendation = original.Recommendations.First(candidate => candidate.Id != item.Id);
        var related = Named<ItemsControl>(scene.Playback, "RelatedItems");
        var button = Visuals(related).OfType<Button>()
            .Single(candidate => ReferenceEquals(candidate.Tag, recommendation));
        Click(button);
        await Drain();
        Check(routes.SequenceEqual(["film-playback"]), "a recommendation requests the same internal playback route");
        await module.NavigateAsync(routes[0], default);
        await Drain();
        var recommended = scene.Page;
        Check(!ReferenceEquals(recommended, original) && ReferenceEquals(recommended.Item, recommendation) &&
            recommended.Title == recommendation.Title && recommended.Poster == recommendation.Poster &&
            ReferenceEquals(scene.Playback.DataContext, recommended), "recommendation updates the bound film context");
        string recommendedLocation = module.CaptureNavigationLocation();
        Check(recommendedLocation != location &&
            recommendedLocation.Contains(recommendation.Id, StringComparison.Ordinal),
            "the recommended film has its own restorable location");
        await module.RestoreNavigationAsync(location, default);
        await Drain();
        Check(module.IsPlayback && ReferenceEquals(scene.Page, original) && original.IsListLayout &&
            ReferenceEquals(scene.Playback.DataContext, original) &&
            Named<ItemsControl>(scene.Playback, "EpisodeList").IsVisible,
            "an older location still restores its film and layout after a recommendation");
        CheckSelection(scene, chosen);
        Check(scene.Source.Calls == calls, "navigation and recommendation changes do not reload the source");
    }

    private static async Task FlatTiles()
    {
        await using var scene = new Scene();
        await scene.Module.NavigateAsync(FilmCapability.PlaybackRoute, default);
        await Drain();
        var view = scene.Playback;
        var page = scene.Page;
        var metadata = Named<Grid>(view, "MetadataPanel");
        Check(!Visuals(metadata).OfType<Border>().Any(border => border.IsVisible &&
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
        await scene.Module.NavigateAsync(FilmCapability.PlaybackRoute, default);
        await Drain();
        var view = scene.Playback;
        var page = scene.Page;
        var current = page.SelectedEpisode;
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
            !Named<Button>(view, "PlayButton").IsEnabled && scene.Source.Calls == 0,
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
        CheckSlot(backdrop, image, new Rect(200, 0, 200, 300), "a portrait poster fits the panel height at the right edge");
        var pixels = Pixels((FrameworkElement)backdrop.Parent);
        Check(Alpha(pixels, 400, 300, 150) > 240, "the poster middle stays visible");
        Check(new[] { (201, 150), (399, 150), (300, 1), (300, 298), (100, 150) }
            .All(point => Alpha(pixels, 400, point.Item1, point.Item2) < 10), "every poster edge fades out");

        (backdrop, image) = Backdrop(poster, 150, 300);
        CheckSlot(backdrop, image, new Rect(-50, 0, 200, 300), "a narrow panel is covered at the poster ratio");
        (backdrop, image) = Backdrop(still, 400, 300);
        CheckSlot(backdrop, image, new Rect(400 - 300 * 16d / 9, 0, 300 * 16d / 9, 300),
            "a landscape image covers the panel without stretching");

        await using var scene = new Scene();
        await scene.Module.NavigateAsync(FilmCapability.PlaybackRoute, default);
        await Drain();
        var view = scene.Playback;
        var panel = Named<Grid>(view, "MetadataPanel");
        var real = Named<FilmPlaybackPoster>(view, "Backdrop");
        var preview = real.Children.OfType<Image>().Single();
        var origin = preview.TranslatePoint(new Point(), real);
        Check(real.IsVisible && real.Opacity is > .1 and < .2 &&
            Math.Abs(preview.ActualWidth / preview.ActualHeight - 2d / 3) < .01 &&
            Math.Abs(preview.ActualHeight - panel.ActualHeight) < 1 &&
            Math.Abs(origin.X + preview.ActualWidth - real.ActualWidth) < 1,
            "the wide metadata backdrop keeps the poster ratio at the panel's right edge");
        scene.View.Width = 700;
        scene.Window.Width = 700;
        scene.Window.UpdateLayout();
        await Drain();
        var details = Named<ScrollViewer>(view, "MetadataScroll");
        Check(double.IsNaN(panel.Height) && Math.Abs(panel.ActualHeight - details.DesiredSize.Height) < 1 &&
            Math.Abs(preview.ActualHeight - panel.ActualHeight) < 1,
            "stacked metadata keeps its content height and the poster follows it");
    }

    private static (FilmPlaybackPoster Poster, Image Image) Backdrop(string address, double width, double height)
    {
        // A host keeps the poster's own clip and opacity mask in the rendered visual tree.
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
        bitmap.Render(element);
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
        Click(Visuals(Named<ItemsControl>(view, "RelatedItems")).OfType<Button>()
            .Single(button => ReferenceEquals(button.Tag, recommendation)));
        await Drain();
        Check(ReferenceEquals(scene.Page.Item, recommendation), "the recommendation opens its own film");
        Check(CheckTint(view, tint, window) != first, "the ambient tint follows the newly opened poster");

        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var blank = section.Items[1] with { Poster = "" };
        section.Items[1] = blank;
        await module.OpenPlaybackAsync(blank);
        await Drain();
        Check(ReferenceEquals(scene.Page.Item, blank) && Named<FilmPlaybackPoster>(view, "Backdrop").PaletteBrush is null &&
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

    private static void CheckPreview(FilmPlaybackPage page)
    {
        Check(new[] { page.Title, page.Metadata, page.Genres, page.Directors, page.Cast,
            page.Overview, page.SeasonLabel, page.EpisodeCountLabel }.All(value => !string.IsNullOrWhiteSpace(value)),
            "local preview supplies film metadata and descriptions");
        Check(page.Episodes.Count > 0 && page.Episodes.All(episode =>
            new[] { episode.Title, episode.Overview, episode.Image, episode.Duration, episode.Label }
                .All(value => !string.IsNullOrWhiteSpace(value))),
            "every preview episode supplies a title image duration and synopsis");
        Check(page.Recommendations.Count > 0 && page.Episodes.Count(episode => episode.IsSelected) == 1 &&
            page.SelectedEpisode.IsSelected, "preview provides recommendations and exactly one current episode");
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
        Check(ReferenceEquals(scene.Page.SelectedEpisode, episode) && episode.IsSelected &&
            scene.Page.Episodes.Count(candidate => candidate.IsSelected) == 1 &&
            Named<TextBlock>(scene.Playback, "PlayerEpisode").Text == episode.Label,
            "one current episode owns both selection and the player title");
        var host = Named<ItemsControl>(scene.Playback, scene.Page.IsListLayout ? "EpisodeList" : "EpisodeCards");
        var badges = Visuals(host).OfType<TextBlock>().Where(text => text.IsVisible && text.Text == "当前").ToArray();
        Check(badges.Length == 1 && Visuals(EpisodeButton(host, episode)).Contains(badges[0]),
            "the rendered episode presentation has exactly one current badge on the selected episode");
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
        internal readonly FilmCapability Module;
        internal readonly FilmView View;
        internal readonly Window Window;
        internal FilmPlaybackPage Page => Module.Playback ?? throw new InvalidOperationException("Missing playback model");
        internal FilmPlaybackView Playback => Named<FilmPlaybackView>(View, "PlaybackView");

        internal Scene(double width = 1160)
        {
            Module = new(Source);
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

        public async ValueTask DisposeAsync()
        {
            Window.Content = null;
            Window.Close();
            await Module.DisposeAsync();
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
                .Select(index => new FilmCardItem($"{section.Key}:local:{index}", $"{section.Title} 本地影片 {index}",
                    "pack://application:,,,/MeowFilm.Capabilities;component/Film/Presentation/Assets/poster-long-season.jpg", ""))
                .Skip(start).Take(limit).ToArray();
            return Task.FromResult(items);
        }

        public void Dispose() { }
    }
}

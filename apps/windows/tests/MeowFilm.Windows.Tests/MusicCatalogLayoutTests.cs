using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using Windows.Foundation;

namespace MeowFilm.Windows.Tests;

public static class MusicCatalogLayoutTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.CatalogLayoutCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> CatalogLayoutCases()
    {
        yield return Case("section more retains full bounds at narrow and wide native render scales", CatalogMoreGeometry);
        yield return Case("banner frame stays 2.56 to 1 with wide square and portrait artwork", CatalogBannerAspect);
        yield return Case("two-banner paging retains final resources resize and indicator position", CatalogBannerPaging);
        yield return Case("shelf pages through full-height arrow buttons while native scrolling stays with the page", CatalogShelfPagingHitArea);
        yield return Case("catalog content buttons keep hover feedback inside their card artwork", CatalogCardContentStates);
        yield return Case("native indicator keeps its anchor while neighboring dots slide and scale", CatalogIndicatorDrawing);
        yield return Case("fixed entry labels captions and resting gradient bind without input", CatalogEntryRoles);
        yield return Case("artwork demand visibility respects clipping hiding and reentry without input", CatalogArtworkVisibility);
    }
    private static async Task CatalogMoreGeometry()
    {
        foreach (double width in new[] { 320d, 826d })
        foreach (double scale in new[] { 1d, 1.5d, 2d })
        {
            await using var f = await CreateSearchPlaylistPresentationScene(120);
            await f.Module.OpenSearchTabAsync("overview");
            var root = new Grid { Width = width, Height = 800 / scale, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale } };
            root.Children.Add(f.Module.View);
            using var host = new DesktopTestUi.Host(root, (int)Math.Ceiling(width * scale), 800);
            await TestUiHost.LoadedAsync(root); await Drain(); root.UpdateLayout();
            var section = Visuals(root).OfType<MusicCatalogSectionView>().First(view =>
                Visuals(view).OfType<Button>().Any(button => Equals(button.Content, "查看更多")));
            var button = Visuals(section).OfType<Button>().Single(value => Equals(value.Content, "查看更多"));
            var heading = (Grid)VisualTreeHelper.GetParent(button);
            var bounds = DesktopTestUi.Bounds(button, heading);
            True(DesktopVisual.IsVisible(button) && button.ActualHeight >= 30, "the real more control retains its full minimum height");
            True(bounds.Top >= -.01 && bounds.Bottom <= heading.ActualHeight + .01 &&
                bounds.Left >= -.01 && bounds.Right <= heading.ActualWidth + .01,
                "the complete control fits the real heading at every width and render scale");
            True(button.CornerRadius.TopLeft > 0 && button.CornerRadius.BottomLeft > 0,
                "the native control retains both rounded corners");
            var pixels = await DesktopTestUi.Pixels(section);
            True(pixels.Any(value => value != 0), "the native heading and its lower edge are rendered");
            await NativeDesktopCapture.SaveAsync(section, $"catalog-more-{width:0}-{scale * 100:0}");
        }
    }
    private static DataTemplate CatalogCardTemplate() => MusicTestUi.Template("<local:MusicCatalogCardView Presentation='Banner'/>");
    private static async Task CatalogBannerAspect()
    {
        foreach (var (width, height) in new[] { (343, 133), (400, 100), (400, 400), (100, 400) })
        {
            var card = (FrameworkElement)CatalogCardTemplate().LoadContent();
            card.DataContext = new MusicCatalogCard("banner", "横幅", "", "https://art.example.test/banner", "playlist", "wy", ActionId: "1");
            var loader = new CatalogBannerLoader(); ArtworkScope.SetLoader(card, loader);
            using var host = new DesktopTestUi.Host(card, 826, 900); card.VerticalAlignment = VerticalAlignment.Top;
            await Until(() => loader.Reads == 1);
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 180; pixels[i + 3] = 255; }
            loader.Result.SetResult(new CatalogBannerLease(new ArtworkPixels(width, height, pixels)));
            var artwork = Visuals(card).OfType<ArtworkImage>().Single();
            await Until(() => artwork.AspectRatio > 0);
            foreach (int viewport in new[] { 826, 560 })
            {
                host.Window.Resize(viewport, 900); await Drain(); card.UpdateLayout();
                True(Math.Abs(artwork.AspectRatio - (double)width / height) < .0001, "decoded dimensions retain the intrinsic ratio");
                True(artwork.Stretch == Stretch.Uniform && Math.Abs(artwork.ActualHeight - artwork.ActualWidth / 2.56) < 1,
                    "the complete source fits the fixed frame without cropping or stretching");
            }
            Equal(1, loader.Reads);
        }
    }
    private sealed class CatalogBannerLoader : IArtworkLoader
    {
        internal int Reads;
        internal readonly TaskCompletionSource<IArtworkLease> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        { Reads++; return Result.Task; }
    }
    private static async Task CatalogEntryRoles()
    {
        foreach (bool home in new[] { false, true })
        {
            var fixture = MusicNativeCatalogTests.ContentFixture(home ? "home-page" : "browse-page");
            var sections = MusicCatalogDto.Read(fixture, "wy", home ? "home" : "channel", "");
            var model = sections.First(section => section.IsEntrySection).Items[0] with { Cover = "", Decoration = "" };
            var card = new EntryCard { DataContext = model, Width = 152, VerticalAlignment = VerticalAlignment.Top };
            using var host = new DesktopTestUi.Host(card, 320, 400);
            await TestUiHost.LoadedAsync(card); await Drain(); card.UpdateLayout();
            var header = (Grid)card.FindName("TitleHeader");
            var caption = (TextBlock)card.FindName("Caption");
            var surface = (Grid)card.FindName("Surface");
            var shade = (Border)card.FindName("BottomShade");
            True(header.Visibility == (home ? Visibility.Visible : Visibility.Collapsed),
                "only home labels occupy the upper title slot");
            True(caption.Text == (home ? model.ContextText : model.Title), "the lower caption uses the fixed role");
            if (home) True(Visuals(header).OfType<TextBlock>().Any(text => text.Text == model.Label), "home label is rendered");
            if (!home) True(((TextBlock)card.FindName("PlayCount")).Text == "0" &&
                ((Grid)card.FindName("CoverInfo")).Visibility == Visibility.Visible &&
                ((Border)card.FindName("TitleShade")).Visibility == Visibility.Visible, "zero plays has a protected cover label");
            True(Visuals(card).OfType<TextBlock>().All(text => text.FontWeight == Microsoft.UI.Text.FontWeights.Normal),
                "ordinary card typography remains regular");
            var gradient = (LinearGradientBrush)shade.Background;
            True(CardMotion.GetProgress(surface) == 0 && shade.Visibility == Visibility.Visible &&
                !shade.IsHitTestVisible && ((SolidColorBrush)surface.Background).Color == gradient.GradientStops[^1].Color,
                "the resting gradient is present without hover and shares the footer color");
            var offsets = gradient.GradientStops.Select(stop => stop.Offset).ToArray();
            // WinUI stores gradient stop offsets at single precision.
            True(offsets.SequenceEqual([(double).62f, (double).82f, 1d]), $"resting gradient stop offsets: {string.Join(", ", offsets)}");
            True(gradient.GradientStops.Select(stop => stop.Color.A).SequenceEqual(new byte[] { 0, 112, 255 }),
                "the resting alpha stops are not an animated hover effect");
            if (home)
            {
                model.InitializePreview([new("Broken Ankle", "", "", 1, null), new("Maggie", "", "", 2, null), new("HONEYMOON", "", "", 3, null)]);
                CardMotion.SetPointerActive(surface, true);
                await Until(() => !CardMotion.HasActiveAnimation(surface)); await Drain(); card.UpdateLayout();
                var lines = Visuals((Border)card.FindName("PreviewBlock")).OfType<TextBlock>().ToArray();
                var button = (Button)card.FindName("PlayButton");
                True(lines.Length == 3 && lines[0].ActualWidth > lines[^1].ActualWidth &&
                    DesktopTestUi.Bounds(lines[0], surface).Bottom <= DesktopTestUi.Bounds(button, surface).Top &&
                    DesktopTestUi.Bounds(lines[^1], surface).Right <= DesktopTestUi.Bounds(button, surface).Left,
                    "preview rows use the unobstructed width above the play button");
            }
        }
        var fm = new MusicCatalogCard("fm", "24h不间断欧美频道 一键播放", "", "", "fm", "wy", CoverLabel: "欧美漫游");
        var fmCard = new EntryCard { DataContext = fm, Width = 152 };
        using var fmScene = new DesktopTestUi.Host(fmCard, 320, 400);
        await TestUiHost.LoadedAsync(fmCard); await Drain(); fmCard.UpdateLayout();
        True(((TextBlock)fmCard.FindName("FMLabel")).Text == "欧美漫游" &&
            ((TextBlock)fmCard.FindName("Caption")).Text == fm.Title && !fm.HasLabel,
            "the FM cover marker is independent of the home category and lower caption");
    }

    private static async Task CatalogArtworkVisibility()
    {
        var viewport = new Canvas { Width = 200, Height = 180, Clip = new RectangleGeometry { Rect = new Rect(0, 0, 200, 180) } };
        var slot = new Border { Width = 152, Height = 152, Background = new SolidColorBrush(Microsoft.UI.Colors.Blue) };
        using var observed = new MeowFilm.Desktop.DesktopViewport(slot, () => { });
        viewport.Children.Add(slot);
        using var host = new DesktopTestUi.Host(viewport, 320, 400);
        await TestUiHost.LoadedAsync(slot); await Drain();
        await Until(() => observed.IsInViewport);
        True(observed.IsInViewport, "the loaded visible slot creates demand");
        Canvas.SetTop(slot, 200); viewport.InvalidateArrange(); viewport.UpdateLayout(); await Drain();
        await Until(() => !observed.IsInViewport);
        True(!observed.IsInViewport, "a realized but clipped slot cannot create demand");
        Canvas.SetTop(slot, 160); viewport.InvalidateArrange(); viewport.UpdateLayout(); await Drain();
        await Until(() => observed.IsInViewport);
        True(observed.IsInViewport, "partial visibility creates demand");
        viewport.Visibility = Visibility.Collapsed; viewport.UpdateLayout(); await Drain();
        True(!observed.IsInViewport, "hidden ancestors stop new demand");
        viewport.Visibility = Visibility.Visible; Canvas.SetTop(slot, 0); viewport.UpdateLayout(); await Drain();
        await Until(() => observed.IsInViewport);
        True(observed.IsInViewport, "redisplay restores visibility without reconstructing the slot");
    }

    private sealed class CatalogBannerLease(ArtworkPixels image) : IArtworkLease
    { public ArtworkPixels Image => image; public void Dispose() { } }
    private static async Task CatalogBannerPaging()
    {
        var section = new MusicCatalogSection("", Enumerable.Range(1, 9).Select(index =>
            new MusicCatalogCard(index.ToString(), "横幅" + index, "", "", "playlist", "wy", ActionId: index.ToString())).ToArray(), Layout: "banner");
        var shelf = new Shelf { DataContext = section, CardTemplate = CatalogCardTemplate(), VerticalAlignment = VerticalAlignment.Top };
        using var host = new DesktopTestUi.Host(shelf, 826, 900);
        await Until(() => shelf.ScrollHost is not null && shelf.MaximumOffset > 0);
        var indicator = (ShelfPageIndicator)shelf.FindName("PageIndicator");
        var next = (Button)shelf.FindName("Next"); var previous = (Button)shelf.FindName("Previous");
        var rail = shelf.ScrollHost!; var cards = section.Items;
        var items = (ListView)shelf.FindName("ItemsHost");
        True(Math.Abs(2 * shelf.CardWidth - 20 - items.ActualWidth) < 1 && indicator.PageCount == 5 && indicator.Visibility == Visibility.Visible,
            "two banners form one reachable group, including the final odd banner");
        for (int page = 1; page <= 4; page++)
        {
            Click(next); await Until(() => Math.Abs(indicator.Position - page) < .01 &&
                Math.Abs(section.HorizontalOffset - rail.HorizontalOffset) < .5);
        }
        True(Math.Abs(rail.HorizontalOffset - shelf.MaximumOffset) < .5 && next.Visibility == Visibility.Collapsed,
            "the last resource is reachable without an empty page");
        True(ReferenceEquals(cards, section.Items), "paging retains resource identity");
        double oldWidth = items.ActualWidth;
        host.Window.Resize(560, 900);
        await Until(() => items.ActualWidth < oldWidth && Math.Abs(2 * shelf.CardWidth - 20 - items.ActualWidth) < 1 &&
            Math.Abs(rail.HorizontalOffset - shelf.MaximumOffset) < .5 && previous.IsEnabled);
        True(Math.Abs(indicator.Position - 4) < .01, "resize retains the final group");
        Click(previous); await Until(() => Math.Abs(indicator.Position - 3) < .01 && Math.Abs(section.HorizontalOffset - rail.HorizontalOffset) < .5);
        shelf.Visibility = Visibility.Collapsed; double saved = section.HorizontalOffset; await Drain();
        shelf.Visibility = Visibility.Visible; await Drain();
        await Until(() => Math.Abs(section.HorizontalOffset - saved) < .5 && Math.Abs(indicator.Position - 3) < .01);
        shelf.DataContext = section with { Items = section.Items.Take(2).ToArray(), HorizontalOffset = 0 };
        await Until(() => indicator.Visibility == Visibility.Collapsed);
        Equal(0d, shelf.MaximumOffset);
    }
    private static async Task CatalogIndicatorDrawing()
    {
        var dots = new ShelfPageIndicator { PageCount = 9, Position = 3 };
        var root = new Grid(); root.Children.Add(dots);
        using var host = new DesktopTestUi.Host(root, 320, 180);
        await TestUiHost.LoadedAsync(dots); await Drain();
        async Task<byte[]> Frame() { dots.UpdateLayout(); return await DesktopTestUi.Pixels(dots, 72, 12); }
        static byte Alpha(byte[] pixels, int x, int y) => pixels[(y * 72 + x) * 4 + 3];
        var first = await Frame(); dots.Position = 4; var next = await Frame();
        True(first.SequenceEqual(next), "the active middle dot remains anchored as group identity advances");
        True(Alpha(first, 36, 6) > Alpha(first, 46, 6) && Alpha(first, 36, 3) > Alpha(first, 46, 3), "active dots are darker and larger");
        dots.Position = 3.5; var middle = await Frame();
        True(!first.SequenceEqual(middle) && Alpha(middle, 31, 6) > 0 && Alpha(middle, 41, 6) > 0, "native dots slide between groups");
        dots.Position = 0; var start = await Frame();
        True(Alpha(start, 26, 6) == 0 && Alpha(start, 46, 6) > 0, "there is no nonexistent previous dot");
        dots.Position = 8; var end = await Frame();
        True(Alpha(end, 46, 6) == 0 && Alpha(end, 26, 6) > 0, "there is no nonexistent following dot");
        Equal("第 9 / 9 组", AutomationProperties.GetName(dots));
        DesktopTheme.SetIsHighContrast(dots, true); var contrast = await Frame();
        Equal((byte)255, Alpha(contrast, 36, 6));
    }
    private static async Task CatalogShelfPagingHitArea()
    {
        var section = new MusicCatalogSection("歌单", Enumerable.Range(0, 8).Select(index =>
            new MusicCatalogCard(index.ToString(), "歌单" + index, "", "", "playlist", "wy")).ToArray());
        var shelf = new Shelf { DataContext = section, VerticalAlignment = VerticalAlignment.Top,
            CardTemplate = MusicTestUi.Template("<Border Height='140' Background='Transparent'><TextBlock Text='{Binding Title}'/></Border>") };
        var list = (ListView)shelf.FindName("ItemsHost");
        // Compare container states without an unrelated native item entrance animation.
        list.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        var content = new StackPanel(); content.Children.Add(shelf); content.Children.Add(new Border { Height = 600 });
        var outer = new ScrollViewer { Content = content, VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        using var host = new DesktopTestUi.Host(outer, 560, 360);
        await Until(() => shelf.ScrollHost is not null && shelf.MaximumOffset > 0);
        var rail = shelf.ScrollHost!;
        await Until(() => list.ContainerFromIndex(0) is ListViewItem);
        await MusicTestUi.ContentStatesRetainPixels((ListViewItem)list.ContainerFromIndex(0));
        True(rail.HorizontalScrollMode == ScrollMode.Disabled && rail.VerticalScrollMode == ScrollMode.Disabled &&
            rail.IsHorizontalScrollChainingEnabled && rail.IsVerticalScrollChainingEnabled,
            "the native shelf leaves wheel and touch scrolling to its parent while retaining programmatic paging");
        CardMotion.SetProgress(shelf, 1); shelf.UpdateLayout();
        var next = (Button)shelf.FindName("Next"); var previous = (Button)shelf.FindName("Previous");
        void CheckHitArea(Button button)
        {
            True(button.ActualHeight >= 139, "the arrow button spans the complete card height");
            foreach (double y in new[] { 2d, button.ActualHeight / 2, button.ActualHeight - 2 })
            {
                var point = button.TransformToVisual(shelf).TransformPoint(new Point(button.ActualWidth / 2, y));
                True(DesktopTestUi.Hit(shelf, point) is { } hit && DesktopVisual.Contains(button, hit),
                    "the arrow's upper space, symbol, and lower space all hit the same native button");
            }
        }
        CheckHitArea(next); Click(next);
        await Until(() => shelf.PresentedOffset > .5 && previous.IsEnabled && shelf.IsSettled);
        CardMotion.SetProgress(shelf, 1); CheckHitArea(previous); Click(previous);
        await Until(() => shelf.PresentedOffset < .5 && shelf.IsSettled);
        True(shelf.Navigate(global::Windows.System.VirtualKey.Right), "keyboard paging remains available");
        await Until(() => shelf.PresentedOffset > .5 && shelf.IsSettled);
        double retained = shelf.PresentedOffset;
        outer.ChangeView(null, 80, null, true);
        await Until(() => outer.VerticalOffset > 79);
        True(Math.Abs(shelf.PresentedOffset - retained) < .5, "page scrolling leaves the shelf's selected page unchanged");
    }
    private static async Task CatalogCardContentStates()
    {
        var card = new MusicCatalogCardView { Width = 152, VerticalAlignment = VerticalAlignment.Top,
            DataContext = new MusicCatalogCard("playlist", "歌单名称", "", "", "playlist", "wy", ActionId: "1") };
        using var host = new DesktopTestUi.Host(card, 320, 400);
        await TestUiHost.LoadedAsync(card); await Drain(); card.UpdateLayout();
        var open = Visuals(card).OfType<Button>().Single(button => button.Content is StackPanel);
        await MusicTestUi.ContentStatesRetainPixels(open);
        var motion = (Grid)card.Content;
        CardMotion.SetPointerActive(motion, true);
        await Until(() => !CardMotion.HasActiveAnimation(motion));
        True(CardMotion.GetProgress(motion) == 1 && Visuals(card).OfType<Border>().Any(border => border.Opacity == 1 && !border.IsHitTestVisible),
            "the content owner still reveals its artwork shade and play action");
        await NativeDesktopCapture.SaveAsync(card, "catalog-content-hover");
    }
}

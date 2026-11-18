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
        yield return Case("first-load skeletons match real two-banner and entry geometry without artwork", CatalogSkeletonGeometry);
        yield return Case("banner frame stays 2.56 to 1 with wide square and portrait artwork", CatalogBannerAspect);
        yield return Case("two-banner paging retains final resources resize and indicator position", CatalogBannerPaging);
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
    private static DataTemplate CatalogCardTemplate(bool entry = false) => MusicTestUi.Template(entry
        ? "<local:EntryCard/>" : "<local:MusicCatalogCardView Presentation='Banner'/>");
    private static async Task CatalogSkeletonGeometry()
    {
        foreach (int width in new[] { 560, 826, 1490 })
        foreach (bool home in new[] { false, true })
        {
            var skeleton = new MusicCatalogSkeleton { IsHome = home, Width = width, VerticalAlignment = VerticalAlignment.Top };
            using var host = new DesktopTestUi.Host(skeleton, width, 900);
            await TestUiHost.LoadedAsync(skeleton); await Drain(); skeleton.UpdateLayout();
            True(!Visuals(skeleton).OfType<ArtworkImage>().Any(), "a placeholder cannot request artwork");
            var shapes = skeleton.Children.Cast<LoadingSkeleton>().ToArray();
            var squares = shapes.Where(shape => Math.Abs(shape.ActualWidth - shape.ActualHeight) < .5).ToArray();
            True(squares.Length > 0 && squares.All(shape => shape.ActualWidth is >= 123.5 and <= 168.5),
                "ordinary covers have the same bounded square geometry as native shelves");
            var first = DesktopTestUi.Bounds(shapes[0], skeleton);
            if (!home)
            {
                True(Math.Abs(first.Width - (width - 20) / 2) < .5, "two equal banners fit with the real gap");
                True(Math.Abs(DesktopTestUi.Bounds(shapes[1], skeleton).Left - first.Right - 20) < .5,
                    "the banner gap matches the content");
            }
            var card = (FrameworkElement)CatalogCardTemplate(home).LoadContent();
            card.Width = first.Width;
            card.DataContext = new MusicCatalogCard("1", "测试", "描述", "", "playlist", "wy", ActionId: "1");
            using var cardHost = new DesktopTestUi.Host(card, (int)Math.Round(first.Width), 900);
            card.VerticalAlignment = VerticalAlignment.Top;
            await TestUiHost.LoadedAsync(card); await Drain(); card.UpdateLayout();
            True(Math.Abs(first.Height - card.ActualHeight) < 1, "the skeleton has the current native card height");
            True(shapes.All(shape => DesktopTestUi.Bounds(shape, skeleton).Right <= width + .5),
                "no placeholder extends beyond its content column");
            skeleton.Visibility = Visibility.Collapsed; await Drain();
            True(shapes.All(shape => !shape.IsSweeping), "hidden placeholders retire their native sweep");
            if (!home)
            {
                var section = new MusicCatalogSection("", Enumerable.Range(1, 3).Select(index =>
                    new MusicCatalogCard(index.ToString(), "横幅" + index, "", "", "playlist", "wy", ActionId: index.ToString()))
                    .ToArray(), Layout: "banner");
                var shelf = new Shelf { DataContext = section, Width = width, CardTemplate = CatalogCardTemplate(), VerticalAlignment = VerticalAlignment.Top };
                host.Mount(shelf);
                double pitch = first.Width + 20;
                var items = (ListView)shelf.FindName("ItemsHost");
                await Until(() => shelf.ScrollHost is not null && Math.Abs(shelf.CardWidth - pitch) < .5 && items.ContainerFromIndex(1) is not null);
                Button Banner(int index) => Visuals((DependencyObject)items.ContainerFromIndex(index)).OfType<Button>()
                    .Single(button => ReferenceEquals(button.Tag, section.Items[index]));
                var firstBanner = Banner(0); var secondBanner = Banner(1);
                True(Math.Abs(firstBanner.ActualWidth - first.Width) < .5 && Math.Abs(firstBanner.ActualHeight - first.Height) < 1,
                    "the real virtualized banner matches the shared skeleton width and height");
                True(Math.Abs(DesktopTestUi.Bounds(secondBanner, shelf).Left - DesktopTestUi.Bounds(firstBanner, shelf).Right - 20) < .5,
                    "the real virtualized banners use the same 20 DIP gap as the skeleton");
            }
        }
    }
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
            var card = new EntryCard { DataContext = model, Width = 152 };
            using var host = new DesktopTestUi.Host(card, 320, 400);
            await TestUiHost.LoadedAsync(card); await Drain(); card.UpdateLayout();
            var header = (Grid)card.FindName("TitleHeader");
            var caption = (TextBlock)card.FindName("Caption");
            var surface = (Grid)card.FindName("Surface");
            var shade = (Border)card.FindName("BottomShade");
            True(header.Visibility == (home ? Visibility.Visible : Visibility.Collapsed),
                "only home labels occupy the upper title slot");
            True(caption.Text == (home ? model.Subtitle : model.Title), "the lower caption uses the fixed role");
            if (home) True(Visuals(header).OfType<TextBlock>().Any(text => text.Text == model.Label), "home label is rendered");
            True(Visuals(card).OfType<TextBlock>().All(text => text.FontWeight == Microsoft.UI.Text.FontWeights.Normal),
                "ordinary card typography remains regular");
            var gradient = (LinearGradientBrush)shade.Background;
            True(CardMotion.GetProgress(surface) == 0 && shade.Visibility == Visibility.Visible &&
                !shade.IsHitTestVisible && ((SolidColorBrush)surface.Background).Color == gradient.GradientStops[^1].Color,
                "the resting gradient is present without hover and shares the footer color");
            True(gradient.GradientStops.Select(stop => stop.Offset).SequenceEqual([.62, .82, 1d]) &&
                gradient.GradientStops.Select(stop => stop.Color.A).SequenceEqual(new byte[] { 0, 112, 255 }),
                "the resting alpha stops are not an animated hover effect");
        }
    }

    private static async Task CatalogArtworkVisibility()
    {
        var viewport = new Canvas { Width = 200, Height = 180, Clip = new RectangleGeometry { Rect = new Rect(0, 0, 200, 180) } };
        var slot = new Border { Width = 152, Height = 152 };
        viewport.Children.Add(slot);
        using var host = new DesktopTestUi.Host(viewport, 320, 400);
        await TestUiHost.LoadedAsync(slot); await Drain();
        True(MusicCatalogView.InCatalogViewport(slot), "the loaded visible slot creates demand");
        Canvas.SetTop(slot, 200); viewport.UpdateLayout(); await Drain();
        True(!MusicCatalogView.InCatalogViewport(slot), "a realized but clipped slot cannot create demand");
        Canvas.SetTop(slot, 160); viewport.UpdateLayout(); await Drain();
        True(MusicCatalogView.InCatalogViewport(slot), "partial visibility creates demand");
        viewport.Visibility = Visibility.Collapsed; viewport.UpdateLayout(); await Drain();
        True(!MusicCatalogView.InCatalogViewport(slot), "hidden ancestors stop new demand");
        viewport.Visibility = Visibility.Visible; Canvas.SetTop(slot, 0); viewport.UpdateLayout(); await Drain();
        True(MusicCatalogView.InCatalogViewport(slot), "redisplay restores visibility without reconstructing the slot");
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
        True(Math.Abs(shelf.CardWidth - 423) < .5 && indicator.PageCount == 5 && indicator.Visibility == Visibility.Visible,
            "two banners form one reachable group, including the final odd banner");
        for (int page = 1; page <= 4; page++)
        {
            Click(next); await Until(() => Math.Abs(indicator.Position - page) < .01 &&
                Math.Abs(section.HorizontalOffset - rail.HorizontalOffset) < .5);
        }
        True(Math.Abs(rail.HorizontalOffset - shelf.MaximumOffset) < .5 && next.Visibility == Visibility.Collapsed,
            "the last resource is reachable without an empty page");
        True(ReferenceEquals(cards, section.Items), "paging retains resource identity");
        host.Window.Resize(560, 900);
        await Until(() => Math.Abs(shelf.CardWidth - 290) < .5 && Math.Abs(rail.HorizontalOffset - shelf.MaximumOffset) < .5 && previous.IsEnabled);
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
        using var host = new DesktopTestUi.Host(dots, 72, 12);
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
}

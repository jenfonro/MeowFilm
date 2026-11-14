using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class MusicCatalogLayoutTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("section more retains full bounds at narrow and wide layouts across display scales", SectionMoreGeometry);
        yield return Case("first-load templates match real two-banner and card geometry without artwork", SkeletonGeometry);
        yield return Case("banner artwork dimensions drive full-image aspect ratio after asynchronous arrival", BannerAspect);
        yield return Case("two-banner paging retains final resources resize and indicator position", BannerPaging);
        yield return Case("indicator keeps the active anchor while neighboring dots slide and scale", IndicatorDrawing);
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("music-catalog-layout: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task SectionMoreGeometry()
    {
        var view = new MusicCatalogView();
        foreach (double width in new[] { 320d, 826d })
        foreach (double scale in new[] { 1d, 1.5d, 2d })
        {
            var section = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.SectionsTemplate")).LoadContent();
            section.DataContext = new MusicCatalogSection("单曲", []);
            MusicCatalogSurface.SetIsOverview(section, true);
            section.LayoutTransform = new ScaleTransform(scale, scale);
            using var scene = new Scene(section, width * scale);
            await Drain(); scene.Layout(width * scale);
            var button = Descendants<Button>(section).Single(b => b.Content as string == "查看更多");
            var header = (DockPanel)VisualTreeHelper.GetParent(button);
            var bounds = button.TransformToAncestor(header).TransformBounds(new Rect(button.RenderSize));
            Check(button.IsVisible && button.ActualHeight >= 30, "real more template measures at least 30 DIP");
            Check(bounds.Top >= -.01 && bounds.Bottom <= header.ActualHeight + .01 &&
                bounds.Left >= -.01 && bounds.Right <= header.ActualWidth + .01,
                "the complete button fits inside the real header at every width and scale");
            var border = Descendants<Border>(button).First();
            var edge = border.TransformToAncestor(header).TransformBounds(new Rect(border.RenderSize));
            Check(edge.Top >= -.01 && edge.Bottom <= header.ActualHeight + .01,
                "rounded template edge is not cut by the header allocation");
            Check(border.CornerRadius.TopLeft > 0 && border.CornerRadius.BottomLeft > 0,
                "real template retains both rounded corners");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale),
                (int)Math.Ceiling(section.ActualHeight * scale), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(section);
            Check(bitmap.PixelHeight >= Math.Ceiling(bounds.Bottom * scale), "render target includes the complete lower edge");
            if (Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT") is { } previews)
            {
                TestWorkspace.ValidatePath(TestWorkspace.DataRoot, previews);
                System.IO.Directory.CreateDirectory(previews);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = System.IO.File.Create(System.IO.Path.Combine(previews,
                    $"context-section-more-{width:0}-{scale * 96:0}.png"));
                encoder.Save(file);
            }
        }
    }

    private static async Task SkeletonGeometry()
    {
        var template = new MusicCatalogView();
        foreach (var width in new[] { 560d, 826d, 1490d })
        foreach (var home in new[] { false, true })
        {
            var skeleton = new MusicCatalogSkeleton { IsHome = home };
            using var scene = new Scene(skeleton, width);
            await Drain(); scene.Layout(width);
            Check(!Descendants<ArtworkImage>(skeleton).Any(), "placeholder cannot request artwork");
            var shapes = skeleton.Children.Cast<LoadingSkeleton>().ToArray();
            var squareCovers = shapes.Where(shape => Math.Abs(shape.ActualWidth - shape.ActualHeight) < .5).ToArray();
            Check(squareCovers.Length > 0 && squareCovers.All(shape => shape.ActualWidth is >= 123.5 and <= 168.5),
                "ordinary cover placeholders use the same bounded square geometry as their native shelves");
            var first = shapes[0].TransformToAncestor(skeleton).TransformBounds(new Rect(shapes[0].RenderSize));
            if (!home)
            {
                Check(Math.Abs(first.Width - (width - 20) / 2) < .5, "exactly two equal banners fit with a 20px gap");
                var second = shapes[1].TranslatePoint(new Point(), skeleton);
                Check(Math.Abs(second.X - first.Right - 20) < .5, "banner gap matches content");
            }
            string key = home ? "Catalog.Entry" : "Catalog.Banner";
            var card = (FrameworkElement)((DataTemplate)template.FindResource(key)).LoadContent();
            card.DataContext = new MusicCatalogCard("1", "测试", "描述", "", "playlist", "wy", ActionId: "1");
            double pitch = first.Width + (home ? 16 : 20);
            card.Measure(new(pitch, double.PositiveInfinity));
            card.Arrange(new(0, 0, pitch, card.DesiredSize.Height)); card.UpdateLayout();
            Check(Math.Abs(first.Height - card.ActualHeight) < 1, "skeleton uses the real card's height");
            if (!home)
            {
                var section = new MusicCatalogSection("", Enumerable.Range(1, 3).Select(i =>
                    new MusicCatalogCard(i.ToString(), "横幅" + i, "", "", "playlist", "wy", ActionId: i.ToString()))
                    .ToArray(), Type: "banner");
                var shelf = new Shelf { DataContext = section, CardTemplate = (DataTemplate)template.FindResource(key) };
                using var shelfScene = new Scene(shelf, width);
                await Until(() => shelf.ScrollHost is not null && Math.Abs(shelf.CardWidth - pitch) < .5);
                var items = (ListBox)shelf.FindName("ItemsHost");
                // The card host carries its item; ArtworkImage also owns an internal retry button.
                Button Banner(int index) => Descendants<Button>((ListBoxItem)items.ItemContainerGenerator.ContainerFromIndex(index))
                    .Single(button => ReferenceEquals(button.Tag, section.Items[index]));
                var firstBanner = Banner(0);
                var secondBanner = Banner(1);
                Check(Math.Abs(firstBanner.ActualWidth - first.Width) < .5 &&
                    Math.Abs(firstBanner.ActualHeight - first.Height) < 1,
                    "the real virtualized banner matches the shared skeleton width and height");
                Check(Math.Abs(secondBanner.TranslatePoint(new(), shelf).X -
                    firstBanner.TranslatePoint(new(firstBanner.ActualWidth, 0), shelf).X - 20) < .5,
                    "the real virtualized banners use the same 20px gap as the skeleton");
            }
            Check(shapes.All(s => s.TranslatePoint(new(), skeleton).X + s.ActualWidth <= width + .5),
                "no placeholder extends beyond the content column");
            skeleton.Visibility = Visibility.Collapsed;
            Check(shapes.All(s => !s.IsSweeping), "hidden placeholders stop their sweep");
        }
    }

    private static async Task BannerAspect()
    {
        var template = new MusicCatalogView();
        foreach (var (width, height) in new[] { (1029, 399), (800, 200) })
        {
            var card = (FrameworkElement)((DataTemplate)template.FindResource("Catalog.Banner")).LoadContent();
            card.DataContext = new MusicCatalogCard("banner", "横幅", "", "https://art.example.test/banner", "playlist", "wy", ActionId: "1");
            var loader = new BannerLoader(); ArtworkScope.SetLoader(card, loader);
            using var scene = new Scene(card, 826);
            await Until(() => loader.Reads == 1);
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 180; pixels[i + 3] = 255; }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze();
            loader.Result.SetResult(new BannerLease(bitmap));
            var artwork = Descendants<ArtworkImage>(card).Single();
            await Until(() => artwork.AspectRatio > 0);
            foreach (double viewport in new[] { 826d, 560d })
            {
                scene.Layout(viewport); await Drain(); scene.Layout(viewport);
                Check(Math.Abs(artwork.AspectRatio - (double)width / height) < .0001, "decoded dimensions retain the real source ratio");
                Check(artwork.Stretch == Stretch.Uniform && Math.Abs(artwork.ActualHeight - artwork.ActualWidth * height / width) < 1,
                    "the full source fits the dynamically measured frame without cropping or stretching");
            }
            Check(loader.Reads == 1, "resizing does not reacquire artwork");
        }
    }
    private sealed class BannerLoader : IArtworkLoader
    {
        internal int Reads;
        internal readonly TaskCompletionSource<IArtworkLease> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        { Reads++; return Result.Task; }
    }
    private sealed class BannerLease(BitmapSource image) : IArtworkLease
    { public BitmapSource Image => image; public void Dispose() { } }

    private static async Task BannerPaging()
    {
        var template = new MusicCatalogView();
        var section = new MusicCatalogSection("", Enumerable.Range(1, 9).Select(i =>
            new MusicCatalogCard(i.ToString(), "横幅" + i, "", "", "playlist", "wy", ActionId: i.ToString())).ToArray(), Type: "banner");
        var shelf = new Shelf { DataContext = section, CardTemplate = (DataTemplate)template.FindResource("Catalog.Banner") };
        using var scene = new Scene(shelf, 826);
        await Until(() => shelf.ScrollHost is not null && shelf.MaximumOffset > 0);
        await Drain(); scene.Layout(826);
        var indicator = (ShelfPageIndicator)shelf.FindName("PageIndicator");
        var next = (Button)shelf.FindName("Next");
        var previous = (Button)shelf.FindName("Previous");
        var rail = shelf.ScrollHost!;
        Check(Math.Abs(shelf.CardWidth - 423) < .5, "826px viewport holds two banners, not three");
        Check(indicator.PageCount == 5 && indicator.Visibility == Visibility.Visible, "nine banners have five reachable groups");
        var cards = section.Items;
        for (int page = 1; page <= 4; page++)
        {
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Math.Abs(indicator.Position - page) < .01 &&
                Math.Abs(section.HorizontalOffset - rail.HorizontalOffset) < .5,
                () => $"page={page}, indicator={indicator.Position}, scroll={rail.HorizontalOffset}, saved={section.HorizontalOffset}, max={shelf.MaximumOffset}");
        }
        Check(Math.Abs(rail.HorizontalOffset - shelf.MaximumOffset) < .5 && next.Visibility == Visibility.Collapsed,
            "last odd banner is reachable without an empty page");
        Check(ReferenceEquals(cards, section.Items), "view paging never replaces resources");
        scene.Layout(560);
        await Until(() => Math.Abs(shelf.CardWidth - 290) < .5 && Math.Abs(rail.HorizontalOffset - shelf.MaximumOffset) < .5 &&
            previous.IsEnabled && previous.Visibility == Visibility.Visible,
            () => $"resized pitch={shelf.CardWidth}, scroll={rail.HorizontalOffset}, max={shelf.MaximumOffset}, previous={previous.IsEnabled}");
        Check(Math.Abs(indicator.Position - 4) < .01, "resize keeps the end and active group");
        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Math.Abs(indicator.Position - 3) < .01 && Math.Abs(section.HorizontalOffset - rail.HorizontalOffset) < .5,
            () => $"back indicator={indicator.Position}, scroll={rail.HorizontalOffset}, saved={section.HorizontalOffset}");
        shelf.Visibility = Visibility.Collapsed;
        double saved = section.HorizontalOffset;
        await Drain();
        shelf.Visibility = Visibility.Visible;
        scene.Layout(560);
        await Until(() => Math.Abs(section.HorizontalOffset - saved) < .5 && Math.Abs(indicator.Position - 3) < .01);
        shelf.DataContext = section with { Items = section.Items.Take(2).ToArray(), HorizontalOffset = 0 };
        await Until(() => indicator.Visibility == Visibility.Collapsed);
        Check(shelf.MaximumOffset == 0, "no overflow means no fake pages");
    }

    private static Task IndicatorDrawing()
    {
        var dots = new ShelfPageIndicator { PageCount = 9, Position = 3 };
        dots.Resources["MeowFilm.Brush.Text"] = Brushes.Black;
        dots.Measure(new(72, 12)); dots.Arrange(new(0, 0, 72, 12));
        var first = Pixels(dots);
        dots.Position = 4;
        var next = Pixels(dots);
        Check(first.SequenceEqual(next), "middle active dot stays anchored as page identity advances");
        Check(Alpha(first, 36, 6) > Alpha(first, 46, 6), "active dot is darker than its neighbor");
        Check(Alpha(first, 36, 3) > Alpha(first, 46, 3), "active dot is larger than its neighbor");
        dots.Position = 3.5;
        var middle = Pixels(dots);
        Check(!first.SequenceEqual(middle) && Alpha(middle, 31, 6) > 0 && Alpha(middle, 41, 6) > 0,
            "dots slide between pages rather than swapping only a static highlight");
        dots.Position = 0;
        var start = Pixels(dots);
        Check(Alpha(start, 26, 6) == 0 && Alpha(start, 46, 6) > 0, "first page has no nonexistent preceding dot");
        dots.Position = 8;
        var end = Pixels(dots);
        Check(Alpha(end, 46, 6) == 0 && Alpha(end, 26, 6) > 0, "last page has no nonexistent following dot");
        Check(AutomationProperties.GetName(dots) == "第 9 / 9 组", "current group is available to accessibility");
        DesktopTheme.SetIsHighContrast(dots, true);
        Check(Alpha(Pixels(dots), 36, 6) == 255, "high contrast uses solid indicators");
        return Task.CompletedTask;
    }

    private static byte[] Pixels(FrameworkElement control)
    {
        control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(72, 12, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var pixels = new byte[72 * 12 * 4]; bitmap.CopyPixels(pixels, 72 * 4, 0);
        return pixels;
    }
    private static byte Alpha(byte[] pixels, int x, int y) => pixels[(y * 72 + x) * 4 + 3];
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Drain()
    { for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task Until(Func<bool> done, Func<string>? details = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException($"Catalog layout did not settle at line {line}: {details?.Invoke()}");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!done()) await Task.Delay(1);
        }
        await Drain();
    }
    private sealed class Scene : IDisposable
    {
        private readonly HwndSource source;
        private readonly Grid root = new();
        internal Scene(FrameworkElement content, double width)
        {
            root.Resources.MergedDictionaries.Add(new()
                { Source = new("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
            root.Children.Add(content); content.VerticalAlignment = VerticalAlignment.Top;
            source = new(new HwndSourceParameters("catalog-layout-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                Width = 1600, Height = 900, PositionX = -32000, PositionY = -32000
            });
            source.RootVisual = root; Layout(width);
        }
        internal void Layout(double width)
        {
            root.Width = width; root.Height = 900;
            root.Measure(new(width, 900)); root.Arrange(new(0, 0, width, 900)); root.UpdateLayout();
        }
        public void Dispose() { source.RootVisual = null; source.Dispose(); }
    }
}

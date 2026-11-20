using FontWeights = Microsoft.UI.Text.FontWeights;
using Windows.UI;
using Windows.System;
using Windows.UI.ViewManagement;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI;
using Windows.UI.Text;
using MeowFilm.Desktop;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using MeowFilm.Capabilities;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class FilmCardRailTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-card-rails: content receipts retain unchanged card pixels and show changed metadata",
            () => TestUiHost.InvokeAsync(IncrementalCards));
        yield return new("film-card-rails: home scales complete posters and restores aligned pages after resize and hiding",
            () => TestUiHost.InvokeAsync(Home));
        yield return new("film-card-rails: episodes scale complete cards and page beside the rail without business commands",
            () => TestUiHost.InvokeAsync(Playback));
        yield return new("film-card-rails: native grids bound posters at width transitions and keep short rows small",
            () => TestUiHost.InvokeAsync(Grid));
    }

    private static async Task IncrementalCards()
    {
        var section = new FilmHomeSection(FilmSectionDefinition.Home[0]);
        var first = FilmPlaybackFixture.Card(number: 1, poster: "https://art.invalid/film-1.png");
        var second = FilmPlaybackFixture.Card(number: 2, poster: "https://art.invalid/film-2.png");
        var last = FilmPlaybackFixture.Card(number: 3, poster: "https://art.invalid/film-3.png");
        section.Complete([first, second, last]);
        var view = new FilmShelf { DataContext = section, Margin = new(32, 16, 32, 16) };
        var loader = new RetainedArtworkLoader(); ArtworkScope.SetLoader(view, loader);
        var window = Mount(view);
        try
        {
            await Until(() => Visuals(view).OfType<FilmCard>().Count() == 3);
            var retained = Visuals(view).OfType<FilmCard>().Single(card => ReferenceEquals(card.DataContext, first));
            var poster = (FilmPlaybackPoster)retained.FindName("Poster");
            await Until(() => Visuals(poster).OfType<Image>().Any(image => image.Source is BitmapSource { PixelWidth: > 0 }));
            var pixels = Visuals(poster).OfType<Image>().Single(image => image.Source is BitmapSource { PixelWidth: > 0 }).Source;
            int unloaded = 0; retained.Unloaded += (_, _) => unloaded++;
            section.Begin(); section.Complete([first with { }, second with { }, last with { }]); await Drain();
            Check(unloaded == 0 && Visuals(view).OfType<FilmCard>().Contains(retained) &&
                Visuals(poster).OfType<Image>().Any(image => ReferenceEquals(image.Source, pixels)),
                "loading and equal receipts keep the mounted card and poster pixels");
            var oldSecond = Visuals(view).OfType<FilmCard>().Single(card => ReferenceEquals(card.DataContext, second));
            var oldArtwork = Visuals(oldSecond).OfType<ArtworkImage>().Single();
            await Until(() => oldArtwork.Source is not null);
            var secondPixels = oldArtwork.Source;
            await Until(() => Visuals(view).OfType<ArtworkImage>().Count(image => image.Source is not null) == 3);
            int reads = loader.Reads;
            var updated = second with { Title = "更新影片", Score = "9.8", Subtitle = "新状态" };
            section.Complete([first with { }, updated, last with { }]); await Drain();
            await Until(() => Visuals(view).OfType<FilmCard>().Any(card => ReferenceEquals(card.DataContext, updated) &&
                Visuals(card).OfType<ArtworkImage>().Any(image => image.Source is not null)));
            var changed = Visuals(view).OfType<FilmCard>().Single(card => ReferenceEquals(card.DataContext, updated));
            Check(loader.Reads == reads && Visuals(changed).OfType<ArtworkImage>().Any(image => ReferenceEquals(image.Source, secondPixels)),
                "same-key metadata replacement borrows the existing cover lease and bitmap without another read");
            Check(Visuals(changed).OfType<TextBlock>().Any(text => text.Text == "更新影片") &&
                Visuals(changed).OfType<TextBlock>().Any(text => text.Text == "9.8") &&
                Visuals(changed).OfType<TextBlock>().Any(text => text.Text == "新状态") &&
                unloaded == 0 && Visuals(view).OfType<FilmCard>().Contains(retained) &&
                Visuals(poster).OfType<Image>().Any(image => ReferenceEquals(image.Source, pixels)),
                "changed metadata reaches only its card while the unrelated poster remains mounted");
            section.Complete([first with { }, last with { }]); await Drain();
            Check(unloaded == 0 && Visuals(view).OfType<FilmCard>().Contains(retained),
                "removing another identity does not retire the surviving card");
        }
        finally { window.Content = null; window.Close(); }
    }

    private sealed class RetainedArtworkLoader : IArtworkLoader
    {
        internal int Reads;
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            return Task.FromResult<IArtworkLease>(new RetainedArtworkLease());
        }
    }
    private sealed class RetainedArtworkLease : IArtworkLease
    {
        public ArtworkPixels Image { get; } = new(1, 1, new byte[] { 32, 64, 96, 255 });
        public void Dispose() { }
    }

    private static Task Grid()
    {
        var grid = new AdaptiveCardPanel();
        foreach (int index in Enumerable.Range(1, 12))
            grid.Children.Add(new FilmCard { DataContext = FilmPlaybackFixture.Card(number: index) });
        foreach (var sample in new[] { (120d, 1), (176d, 1), (300d, 1), (304d, 2), (1000d, 6), (1440d, 9) })
        {
            grid.Measure(new(sample.Item1, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, sample.Item1, grid.DesiredSize.Height));
            grid.UpdateLayout();
            var row = grid.Children.Cast<FilmCard>().Where(card => card.TransformToVisual(grid).TransformPoint(new Point()).Y < 1).ToArray();
            Check(row.Length == sample.Item2, "a grid adds another column only within the readable poster range");
            foreach (var card in grid.Children.Cast<FilmCard>())
            {
                var bounds = Bounds(card, grid);
                var poster = (Grid)card.FindName("PosterFrame");
                Check(bounds.Left >= -.5 && bounds.Right <= sample.Item1 + .5 &&
                    card.ActualWidth <= 176.5 && card.ActualWidth >= Math.Min(144, sample.Item1) - .5 &&
                    Math.Abs(poster.ActualHeight - poster.ActualWidth * 1.5) <= 1,
                    "grid artwork preserves its ratio without clipping or exceeding its maximum, including a narrow single column");
            }
        }
        while (grid.Children.Count > 1) grid.Children.RemoveAt(grid.Children.Count - 1);
        grid.Measure(new(1440, double.PositiveInfinity));
        grid.Arrange(new Rect(0, 0, 1440, grid.DesiredSize.Height));
        Check(((FilmCard)grid.Children[0]).ActualWidth <= 176.5, "short data does not enlarge its last row");
        grid.Children.Clear();
        grid.Measure(new(1440, double.PositiveInfinity));
        Check(grid.DesiredSize.Height == 0, "an empty grid has no phantom card rows");
        return Task.CompletedTask;
    }

    private static async Task Home()
    {
        var section = new FilmHomeSection(FilmSectionDefinition.Home[0]);
        section.Complete(Enumerable.Range(1, 1000).Select(index => FilmPlaybackFixture.Card(number: index)).ToArray());
        var view = new FilmShelf { DataContext = section, Margin = new(32, 16, 32, 16) };
        var window = Mount(view);
        try
        {
            var shelf = (Shelf)view.FindName("Cards");
            await Until(() => shelf.ScrollHost is { ViewportWidth: > 0 });
            var counts = new HashSet<int>();
            foreach (double width in new[] { 420d, 780d, 1150d, 1440d })
            {
                window.Resize(width, 850);
                window.Root.UpdateLayout();
                await Drain();
                counts.Add(CheckCards<FilmCard>(shelf, card => (FrameworkElement)card.FindName("PosterFrame"), 176d / 264, 144, 176));
                Check(Visuals(shelf).OfType<FilmCard>().Count() < 40,
                    "home realizes nearby cards rather than the complete source");
                ((Button)shelf.FindName("Next")).Focus(FocusState.Programmatic);
                await Drain();
                await Snapshot(view, $"home-{width:0}");
            }
            Check(counts.Count > 1, "the viewport changes the number of complete posters");
            Click(shelf, "Next");
            await Until(() => shelf.IsSettled && shelf.PresentedOffset > .5);
            double anchor = Math.Round(shelf.PresentedOffset / shelf.CardWidth);
            Check(Math.Abs(section.HorizontalOffset - shelf.PresentedOffset) < .5,
                "the section owns the settled rail offset");
            view.Visibility = Visibility.Collapsed;
            window.Resize(910, 850);
            window.Root.UpdateLayout();
            view.Visibility = Visibility.Visible;
            await Drain();
            Check(Math.Abs(shelf.PresentedOffset / shelf.CardWidth - anchor) < .01,
                "returning after hidden resize retains the same complete first card");
            CheckCards<FilmCard>(shelf, card => (FrameworkElement)card.FindName("PosterFrame"), 176d / 264, 144, 176);
            shelf.ScrollToIndex(999);
            await Drain();
            CheckCards<FilmCard>(shelf, card => (FrameworkElement)card.FindName("PosterFrame"), 176d / 264, 144, 176);
            Check(((Button)shelf.FindName("Next")).Visibility == Visibility.Collapsed,
                "home hides the unavailable forward direction on the final page");
            section.Complete(section.Items.Take(1).ToArray());
            await Drain();
            Check(shelf.PresentedOffset < .5 && BothArrowsHidden(shelf),
                "a shortened source clears the distant offset and both navigation arrows");
            var single = Visuals(shelf).OfType<FilmCard>().Single();
            Check(single.ActualWidth <= 176.5, "one remaining poster never expands beyond the search-card maximum");
        }
        finally { window.Content = null; window.Close(); }
    }

    private static async Task Playback()
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        var view = new FilmPlaybackView { DataContext = rig.Page };
        var window = Mount(view);
        try
        {
            var shelf = FilmUiTest.Named<Shelf>(view, "EpisodeShelf");
            await Until(() => shelf.ScrollHost is { ViewportWidth: > 0 });
            var toolbar = (Grid)view.FindName("EpisodeToolbar");
            int commands = rig.Runtime.Calls.Count;
            var selected = rig.Page.SelectedEpisode;
            var counts = new HashSet<int>();
            foreach (double width in new[] { 780d, 1150d, 1440d })
            {
                window.Resize(width, 850);
                window.Root.UpdateLayout();
                shelf.ScrollToIndex(0);
                await Drain();
                counts.Add(CheckCards<Button>(shelf, EpisodeFrame, 16d / 9, 240, 336,
                    button => button.Tag is FilmEpisodeItem));
                var next = (Button)shelf.FindName("Next");
                Check(next.ActualWidth == 48 && next.ActualHeight == 48 &&
                    next.TransformToVisual(view).TransformPoint(new Point()).Y >=
                    toolbar.TransformToVisual(view).TransformPoint(new Point(0, toolbar.ActualHeight)).Y,
                    "the existing white round navigation lives beside the cards below the toolbar");
                next.Focus(FocusState.Programmatic);
                shelf.StartBringIntoView();
                await Drain();
                await Snapshot(view, $"playback-{width:0}");
                Click(shelf, "Next");
                await Until(() => shelf.IsSettled && shelf.PresentedOffset > .5);
                CheckCards<Button>(shelf, EpisodeFrame, 16d / 9, 240, 336, button => button.Tag is FilmEpisodeItem);
                Click(shelf, "Previous");
                await Until(() => shelf.IsSettled && shelf.PresentedOffset < .5);
            }
            Check(counts.Count > 1 && rig.Runtime.Calls.Count == commands &&
                ReferenceEquals(selected, rig.Page.SelectedEpisode),
                "resizing and paging change complete visible card count without selecting or requesting data");
            shelf.ScrollToIndex(rig.Page.Episodes.Count - 1);
            await Drain();
            CheckCards<Button>(shelf, EpisodeFrame, 16d / 9, 240, 336, button => button.Tag is FilmEpisodeItem);
            Check(((Button)shelf.FindName("Next")).Visibility == Visibility.Collapsed,
                "the short last page finishes with complete cards");
            double savedOffset = shelf.PresentedOffset;
            ClickView(view, "ListLayout");
            await Until(() => rig.Page.IsListLayout && !FilmUiTest.Visible(shelf));
            ClickView(view, "HorizontalLayout");
            await Until(() => rig.Page.IsHorizontalLayout && FilmUiTest.Visible(shelf));
            await Drain();
            CheckCards<Button>(shelf, EpisodeFrame, 16d / 9, 240, 336, button => button.Tag is FilmEpisodeItem);
            Check(Math.Abs(shelf.PresentedOffset - savedOffset) < .5 && ReferenceEquals(selected, rig.Page.SelectedEpisode),
                "returning from the list restores the directory's previous card position without changing the selected episode");
            rig.Runtime.Change(root => root["episodes"] = new System.Text.Json.Nodes.JsonArray());
            await Until(() => rig.Page.Episodes.Count == 0);
            await Drain();
            Check(!FilmUiTest.Visible(shelf), "empty episodes retire the card rail");
        }
        finally { view.DataContext = null; window.Content = null; window.Close(); }
    }

    private static FrameworkElement EpisodeFrame(Button tile) =>
        (FrameworkElement)VisualTreeHelper.GetParent(Visuals(tile).OfType<FilmPlayMark>().Single());

    private static int CheckCards<T>(Shelf shelf, Func<T, FrameworkElement> image, double ratio, double minimum, double maximum,
        Func<T, bool>? include = null) where T : FrameworkElement
    {
        var rail = shelf.ScrollHost!;
        var cards = Visuals(shelf).OfType<T>().Where(card => include?.Invoke(card) ?? true)
            .Select(card => (Card: card, Bounds: Bounds(card, rail)))
            .Where(row => row.Bounds.Right > 1 && row.Bounds.Left < rail.ViewportWidth - 1).ToArray();
        Check(cards.Length > 0 && cards.All(row => row.Bounds.Left >= -1 &&
            row.Bounds.Right <= rail.ViewportWidth + 1), "every visible card fits completely inside its viewport");
        Check(Math.Abs(cards[0].Bounds.Left) <= 1, "the row starts on a complete card");
        Check(Math.Abs(cards[^1].Bounds.Right - rail.ViewportWidth) <= 1 ||
            (rail.ViewportWidth + 16) / (cards.Length + 1) - 16 < minimum,
            "the row fills its width unless another complete card would be smaller than the readable minimum");
        foreach (var row in cards)
        {
            var poster = Bounds(image(row.Card), rail);
            Check(Math.Abs(poster.Width / poster.Height - ratio) < .015,
                "artwork scales with the complete card and keeps its original frame ratio");
            Check(poster.Width >= minimum - 1 && poster.Width <= maximum + 1,
                "artwork stays between the readable minimum and the reference-image maximum");
            foreach (var text in Visuals(row.Card).OfType<TextBlock>())
            {
                var transform = text.TransformToVisual(rail);
                Point origin = transform.TransformPoint(new Point()), unit = transform.TransformPoint(new Point(1, 1));
                Check(Math.Abs(unit.X - origin.X - 1) < .001 && Math.Abs(unit.Y - origin.Y - 1) < .001 &&
                    text.FontFamily.Source == "XamlAutoFontFamily" && text.FontWeight == FontWeights.Normal,
                    "native card text keeps system typography without any viewport-dependent glyph scale");
            }
        }
        Check(Math.Abs(shelf.PresentedOffset / shelf.CardWidth -
            Math.Round(shelf.PresentedOffset / shelf.CardWidth)) < .01,
            "the settled viewport starts on a complete card boundary");
        return cards.Length;
    }

    private static bool BothArrowsHidden(Shelf shelf) =>
        new[] { "Previous", "Next" }.All(name => ((Button)shelf.FindName(name)).Visibility == Visibility.Collapsed);
    private static void Click(Shelf shelf, string name) => ClickButton((Button)shelf.FindName(name));
    private static void ClickView(FilmPlaybackView view, string name) => FilmUiTest.Click(FilmUiTest.Named<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(view, name));
    private static void ClickButton(Button button) => FilmUiTest.Click(button);
    private static Rect Bounds(FrameworkElement element, UIElement ancestor) =>
        element.TransformToVisual(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static Window Mount(FrameworkElement view)
    {
        var window = FilmUiTest.Window(view); window.Show(); return window;
    }
    private static async Task Snapshot(FrameworkElement view, string name)
    {
        if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_DESKTOP_EXPECTED_USER") is not null)
            await NativeDesktopCapture.SaveAsync(view, name);
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static async Task Drain()
    {
        for (int index = 0; index < 3; index++) await FilmUiTest.Pump();
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Film card rail state did not settle");
            await FilmUiTest.Pump();
            if (!condition()) await Task.Delay(1);
        }
        await Drain();
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

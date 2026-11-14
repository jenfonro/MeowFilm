using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class FilmCardRailTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-card-rails: home scales complete posters and restores aligned pages after resize and hiding",
            () => MusicQualityConsumerTests.Sta(Home));
        yield return new("film-card-rails: episodes scale complete cards and page beside the rail without business commands",
            () => MusicQualityConsumerTests.Sta(Playback));
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
            foreach (double width in new[] { 780d, 1150d, 1440d })
            {
                window.Width = width;
                window.UpdateLayout();
                await Drain();
                counts.Add(CheckCards<FilmCard>(shelf, card => (FrameworkElement)card.FindName("PosterFrame"), 176d / 264));
                Check(Visuals(shelf).OfType<FilmCard>().Count() < 40,
                    "home realizes nearby cards rather than the complete source");
                ((Button)shelf.FindName("Next")).Focus();
                await Drain();
                Snapshot(view, $"home-{width:0}");
            }
            Check(counts.Count > 1, "the viewport changes the number of complete posters");
            Click(shelf, "Next");
            await Until(() => shelf.IsSettled && shelf.PresentedOffset > .5);
            double anchor = Math.Round(shelf.PresentedOffset / shelf.CardWidth);
            Check(Math.Abs(section.HorizontalOffset - shelf.PresentedOffset) < .5,
                "the section owns the settled rail offset");
            view.Visibility = Visibility.Collapsed;
            window.Width = 910;
            window.UpdateLayout();
            view.Visibility = Visibility.Visible;
            await Drain();
            Check(Math.Abs(shelf.PresentedOffset / shelf.CardWidth - anchor) < .01,
                "returning after hidden resize retains the same complete first card");
            CheckCards<FilmCard>(shelf, card => (FrameworkElement)card.FindName("PosterFrame"), 176d / 264);
            shelf.ScrollToIndex(999);
            await Drain();
            CheckCards<FilmCard>(shelf, card => (FrameworkElement)card.FindName("PosterFrame"), 176d / 264);
            Check(((Button)shelf.FindName("Next")).Visibility == Visibility.Collapsed,
                "home hides the unavailable forward direction on the final page");
            section.Complete(section.Items.Take(1).ToArray());
            await Drain();
            Check(shelf.PresentedOffset < .5 && BothArrowsHidden(shelf),
                "a shortened source clears the distant offset and both navigation arrows");
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
            var shelf = (Shelf)view.FindName("EpisodeShelf");
            await Until(() => shelf.ScrollHost is { ViewportWidth: > 0 });
            var toolbar = (DockPanel)view.FindName("EpisodeToolbar");
            int commands = rig.Runtime.Calls.Count;
            var selected = rig.Page.SelectedEpisode;
            var counts = new HashSet<int>();
            foreach (double width in new[] { 780d, 1150d, 1440d })
            {
                window.Width = width;
                window.UpdateLayout();
                shelf.ScrollToIndex(0);
                await Drain();
                counts.Add(CheckCards<Button>(shelf, EpisodeFrame, 260d / 144,
                    button => button.Tag is FilmEpisodeItem));
                var next = (Button)shelf.FindName("Next");
                Check(next.ActualWidth == 48 && next.ActualHeight == 48 &&
                    next.TranslatePoint(new Point(), view).Y >=
                    toolbar.TranslatePoint(new Point(0, toolbar.ActualHeight), view).Y,
                    "the existing white round navigation lives beside the cards below the toolbar");
                next.Focus();
                shelf.BringIntoView();
                await Drain();
                Snapshot(view, $"playback-{width:0}");
                Click(shelf, "Next");
                await Until(() => shelf.IsSettled && shelf.PresentedOffset > .5);
                CheckCards<Button>(shelf, EpisodeFrame, 260d / 144, button => button.Tag is FilmEpisodeItem);
                Click(shelf, "Previous");
                await Until(() => shelf.IsSettled && shelf.PresentedOffset < .5);
            }
            Check(counts.Count > 1 && rig.Runtime.Calls.Count == commands &&
                ReferenceEquals(selected, rig.Page.SelectedEpisode),
                "resizing and paging change complete visible card count without selecting or requesting data");
            shelf.ScrollToIndex(rig.Page.Episodes.Count - 1);
            await Drain();
            CheckCards<Button>(shelf, EpisodeFrame, 260d / 144, button => button.Tag is FilmEpisodeItem);
            Check(((Button)shelf.FindName("Next")).Visibility == Visibility.Collapsed,
                "the short last page finishes with complete cards");
            ClickView(view, "ListLayout");
            await Until(() => rig.Page.IsListLayout && !shelf.IsVisible);
            ClickView(view, "HorizontalLayout");
            await Until(() => rig.Page.IsHorizontalLayout && shelf.IsVisible);
            await Drain();
            CheckCards<Button>(shelf, EpisodeFrame, 260d / 144, button => button.Tag is FilmEpisodeItem);
            Check(shelf.PresentedOffset < .5 && ReferenceEquals(selected, rig.Page.SelectedEpisode),
                "returning from the list locates the selected first episode without changing it");
            rig.Runtime.Change(root => root["episodes"] = new System.Text.Json.Nodes.JsonArray());
            await Until(() => rig.Page.Episodes.Count == 0);
            await Drain();
            Check(!shelf.IsVisible, "empty episodes retire the card rail");
        }
        finally { view.DataContext = null; window.Content = null; window.Close(); }
    }

    private static FrameworkElement EpisodeFrame(Button tile) =>
        (FrameworkElement)VisualTreeHelper.GetParent(Visuals(tile).OfType<FilmPlayMark>().Single());

    private static int CheckCards<T>(Shelf shelf, Func<T, FrameworkElement> image, double ratio,
        Func<T, bool>? include = null) where T : FrameworkElement
    {
        var rail = shelf.ScrollHost!;
        var cards = Visuals(shelf).OfType<T>().Where(card => include?.Invoke(card) ?? true)
            .Select(card => (Card: card, Bounds: Bounds(card, rail)))
            .Where(row => row.Bounds.Right > 1 && row.Bounds.Left < rail.ViewportWidth - 1).ToArray();
        Check(cards.Length > 0 && cards.All(row => row.Bounds.Left >= -1 &&
            row.Bounds.Right <= rail.ViewportWidth + 1), "every visible card fits completely inside its viewport");
        Check(Math.Abs(cards[0].Bounds.Left) <= 1 &&
            Math.Abs(cards[^1].Bounds.Right - rail.ViewportWidth) <= 1,
            "complete cards fill the row from the first edge to the last edge");
        foreach (var row in cards)
        {
            var poster = Bounds(image(row.Card), rail);
            Check(Math.Abs(poster.Width / poster.Height - ratio) < .015,
                "artwork scales with the complete card and keeps its original frame ratio");
            double sx = row.Bounds.Width / row.Card.ActualWidth, sy = row.Bounds.Height / row.Card.ActualHeight;
            Check(Math.Abs(sx - sy) < .01, "the whole card uses one proportional scale");
        }
        Check(Math.Abs(shelf.PresentedOffset / shelf.CardWidth -
            Math.Round(shelf.PresentedOffset / shelf.CardWidth)) < .01,
            "the settled viewport starts on a complete card boundary");
        return cards.Length;
    }

    private static bool BothArrowsHidden(Shelf shelf) =>
        new[] { "Previous", "Next" }.All(name => ((Button)shelf.FindName(name)).Visibility == Visibility.Collapsed);
    private static void Click(Shelf shelf, string name) => ClickButton((Button)shelf.FindName(name));
    private static void ClickView(FilmPlaybackView view, string name) => ClickButton((Button)view.FindName(name));
    private static void ClickButton(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static Window Mount(FrameworkElement view)
    {
        var window = new Window { Content = view, Width = 1150, Height = 850, Left = 24, Top = 24,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Background = Brushes.White, SizeToContent = SizeToContent.Manual };
        window.Show();
        window.UpdateLayout();
        return window;
    }
    private static void Snapshot(FrameworkElement view, string name)
    {
        if (Environment.GetEnvironmentVariable("MEOWFILM_FILM_RAIL_PREVIEW_ROOT") is not { } root) return;
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root);
        Directory.CreateDirectory(root);
        var window = Window.GetWindow(view);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, name + ".png"));
        encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static async Task Drain()
    {
        for (int index = 0; index < 3; index++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Film card rail state did not settle");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!condition()) await Task.Delay(1);
        }
        await Drain();
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicShelfInteractionTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("card-to-arrow hit paths remain inside the shelf while arrows are hidden or focused", HitPaths);
        yield return Case("keyboard access and paging retain card selection and overflow boundaries", Paging);
        yield return Case("resize retains logical columns without intermediate half cards", Resize);
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("music-shelf: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task HitPaths()
    {
        using var f = new Fixture();
        await f.Load();
        Check(f.Root.Focus(), "focus can leave the shelf");
        await Drain();
        Check(f.Previous.Visibility == Visibility.Collapsed &&
            f.Next.Visibility == Visibility.Visible &&
            f.Next.Opacity == 0 && !f.Next.IsHitTestVisible,
            "only the executable resting arrow exists and it does not intercept pointer input");
        CheckPaths(f);

        Check(f.Next.Focus(), "the next arrow remains keyboard reachable while hidden");
        await Drain();
        Check(f.Shelf.IsKeyboardFocusWithin && f.Next.Opacity == 1 && f.Next.IsHitTestVisible,
            "keyboard focus exposes the shelf arrows");
        CheckPaths(f);
        var nextBounds = f.Bounds(f.Next);
        Check(f.HitWithin(f.Next, new Point(nextBounds.Left + nextBounds.Width / 2,
            nextBounds.Top + nextBounds.Height / 2)), "the exposed arrow owns its separate click region");

        var previousBounds = f.Bounds(f.Previous);
        Check(!f.HitWithin(f.Shelf, new Point(previousBounds.Left - 1,
            previousBounds.Top + previousBounds.Height / 2)), "the left hover scope ends with the shelf gutter");
        Check(!f.HitWithin(f.Shelf, new Point(nextBounds.Right + 1,
            nextBounds.Top + nextBounds.Height / 2)), "the right hover scope ends with the shelf gutter");
    }

    private static void CheckPaths(Fixture f)
    {
        var viewport = f.Bounds(f.Rail);
        foreach (var arrow in new[] { f.Previous, f.Next }.Where(arrow => arrow.Visibility == Visibility.Visible))
        {
            var bounds = f.Bounds(arrow);
            bool right = ReferenceEquals(arrow, f.Next);
            var start = right ? viewport.Right - 2 : viewport.Left + 2;
            var end = bounds.Left + bounds.Width / 2;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(end - start) * 2));
            foreach (var y in new[] { bounds.Top + 1, bounds.Top + bounds.Height / 2, bounds.Bottom - 1 })
                for (int i = 0; i <= steps; i++)
                {
                    var point = new Point(start + (end - start) * i / steps, y);
                    Check(f.HitWithin(f.Shelf, point),
                        $"card-to-arrow path lost shelf ownership at {point} ({AutomationProperties.GetName(arrow)})");
                }
        }
    }

    private static async Task Paging()
    {
        using var f = new Fixture();
        await f.Load();
        f.Items.SelectedIndex = 0;
        var selected = f.Items.SelectedItem;
        var cards = f.Section.Items;
        int cardClicks = 0;
        f.Items.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, _) => cardClicks++));
        Check(f.Next.Visibility == Visibility.Visible && f.Next.IsEnabled &&
            f.Previous.Visibility == Visibility.Collapsed,
            "the first page exposes only forward paging");
        Check(f.Next.IsTabStop && f.Previous.IsTabStop &&
            AutomationProperties.GetName(f.Next) == "下一组" &&
            AutomationProperties.GetName(f.Previous) == "上一组", "paging controls retain keyboard and accessible names");

        Check(f.Next.Focus(), "the hidden forward arrow can receive keyboard focus");
        await Drain();
        f.Next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Rail.HorizontalOffset > .5 && f.IsSettled, "forward page settles");
        var page = f.Rail.HorizontalOffset;
        var visibleCards = (int)Math.Round((f.Rail.ViewportWidth + 16) / f.Shelf.CardWidth,
            MidpointRounding.AwayFromZero);
        Check(Math.Abs(page - visibleCards * f.Shelf.CardWidth) < .5 &&
            page <= f.Rail.ViewportWidth + 16.5 &&
            Math.Abs(page / f.Shelf.CardWidth - Math.Round(page / f.Shelf.CardWidth)) < .01,
            "paging advances a viewport-sized group of whole cards");
        Check(f.Previous.Visibility == Visibility.Visible && f.Previous.IsEnabled && f.Previous.Focus(),
            "backward paging becomes keyboard reachable");
        CheckPaths(f);
        f.Previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled &&
            f.Previous.Visibility == Visibility.Collapsed, "backward page restores the start");

        foreach (var (forward, backward) in new[] { (Key.Right, Key.Left), (Key.PageDown, Key.PageUp) })
        {
            Check(f.Key(forward), "the shelf handles executable forward paging");
            await Until(() => Math.Abs(f.Rail.HorizontalOffset - page) < .5 && f.IsSettled,
                "keyboard forward paging matches the arrow");
            Check(f.Key(backward), "the shelf handles executable backward paging");
            await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled, "keyboard backward paging restores the start");
        }
        f.Rail.ScrollToHorizontalOffset(f.Shelf.CardWidth * .37);
        f.Next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - page) < .5 && f.IsSettled,
            "paging from an in-between physical frame settles on a whole-column page");
        f.Previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled,
            "whole-column paging returns to the start");
        Check(!f.Key(Key.Left), "a boundary key is left unhandled when no previous page exists");
        Check(f.Key(Key.End), "End is handled while the final boundary is reachable");
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
            f.IsSettled && f.Next.Visibility == Visibility.Collapsed, "End reaches the final boundary");
        Check(f.Rail.ScrollableWidth - f.Shelf.MaximumOffset > 8,
            "the trailing card margin is not exposed as another page");
        var previousPage = Math.Max(0,
            Math.Floor(Math.Max(0, f.Shelf.MaximumOffset - .5) / page) * page);
        f.Previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - previousPage) < .5 && f.IsSettled,
            "the final partial page returns to the preceding page boundary");
        Check(f.Key(Key.End), "End remains available after leaving the final partial page");
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
            f.IsSettled, "End restores the final boundary");
        var end = f.Rail.HorizontalOffset;
        f.Next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Drain();
        Check(Math.Abs(f.Rail.HorizontalOffset - end) < .01 && !f.Key(Key.Right),
            "the final page rejects empty forward actions");
        Check(f.Key(Key.Home), "Home is handled while the first boundary is reachable");
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled &&
            f.Previous.Visibility == Visibility.Collapsed,
            "Home reaches the first boundary");
        Check(ReferenceEquals(cards, f.Items.ItemsSource) && ReferenceEquals(selected, f.Items.SelectedItem) &&
            cardClicks == 0, "shelf paging does not replace content, change selection, or invoke card actions");

        Check(f.Root.Focus(), "keyboard focus can leave the shelf after paging");
        await Drain();
        Check(f.Next.Opacity == 0 && !f.Next.IsHitTestVisible, "arrows hide after keyboard focus leaves");
        f.Shelf.DataContext = new MusicCatalogSection("歌单", cards.Take(visibleCards).ToArray(), Type: "playlists");
        f.Window.UpdateLayout();
        await Until(() => f.Previous.Visibility == Visibility.Collapsed && f.Next.Visibility == Visibility.Collapsed &&
            f.Rail.HorizontalOffset < .5, "arrows collapse when the content no longer overflows");
        Check(f.Rail.ScrollableWidth > .5,
            "a template's trailing margin may remain in the native extent without becoming a page");
    }

    private static async Task Resize()
    {
        using var f = new Fixture();
        await f.Load();
        Check(f.Key(Key.PageDown), "the fixture can establish a nonzero logical anchor");
        await Until(() => f.Rail.HorizontalOffset > .5 && f.IsSettled, "initial anchor settles");
        var anchor = (int)Math.Round(f.Rail.HorizontalOffset / f.Shelf.CardWidth,
            MidpointRounding.AwayFromZero);
        var frames = new List<(double Width, double Offset)>();
        EventHandler rendered = (_, _) =>
        {
            if (f.Shelf.CardWidth > 1)
                frames.Add((f.Shelf.CardWidth, f.Shelf.PresentedOffset));
        };
        CompositionTarget.Rendering += rendered;
        try
        {
            f.Window.Width = 910;
            f.Window.UpdateLayout();
            await Until(() => Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - anchor) < .01 &&
                f.IsSettled, "a wider viewport restores the same logical column");
        }
        finally { CompositionTarget.Rendering -= rendered; }
        var resizedFrames = frames.Where(frame => Math.Abs(frame.Width - f.Shelf.CardWidth) < .01).ToArray();
        var misaligned = resizedFrames
            .Where(frame => Math.Abs(frame.Offset / frame.Width -
                Math.Round(frame.Offset / frame.Width)) >= .01).ToArray();
        Check(resizedFrames.Length > 0 && misaligned.Length == 0,
            "every rendered frame at the new pitch begins on a complete logical column: " +
            string.Join(", ", misaligned.Select(frame => $"{frame.Offset:0.##}/{frame.Width:0.##}")));

        f.Window.Width = 760;
        f.Window.Width = 840;
        f.Window.Width = 680;
        f.Window.UpdateLayout();
        await Until(() => Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - anchor) < .01 &&
            f.IsSettled, "continuous resize keeps one anchor generation");

        Check(f.Key(Key.Home), "the fixture can return before testing animation interruption");
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled, "start boundary settles");
        var oldWidth = f.Shelf.CardWidth;
        var expectedAnimatedAnchor = Math.Max(1,
            (int)Math.Round((f.Rail.ViewportWidth + 16) / oldWidth,
                MidpointRounding.AwayFromZero));
        f.Next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        f.Window.Width = 860;
        f.Window.UpdateLayout();
        await Until(() => Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - expectedAnimatedAnchor) < .01 &&
            f.IsSettled, "resize during paging retains the animation target rather than its partial pixels");

        Check(f.Key(Key.End), "the fixture can establish a final-page anchor");
        await Until(() => Math.Abs(f.Shelf.MaximumOffset - f.Rail.HorizontalOffset) < .5 &&
            f.IsSettled, "final page settles before resize");
        f.Window.Width = 730;
        f.Window.UpdateLayout();
        await Until(() => Math.Abs(f.Shelf.MaximumOffset - f.Rail.HorizontalOffset) < .5 &&
            f.IsSettled && f.Next.Visibility == Visibility.Collapsed,
            "a resized final page remains anchored to the real end");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task Until(Func<bool> done, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!done()) await Task.Delay(1);
        }
        await Drain();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Grid Root = new() { Background = Brushes.Transparent, Focusable = true };
        internal readonly MusicCatalogSection Section = new("歌单", Enumerable.Range(1, 18)
            .Select(i => new MusicCatalogCard(i.ToString(), "歌单" + i, "", "", "playlist", "wy"))
            .ToArray(), Type: "playlists");
        internal readonly Shelf Shelf;
        internal readonly Window Window;
        internal ScrollViewer Rail => Shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host");
        internal ListBox Items => (ListBox)Shelf.FindName("ItemsHost");
        internal Button Previous => (Button)Shelf.FindName("Previous");
        internal Button Next => (Button)Shelf.FindName("Next");
        internal bool IsSettled => Math.Abs(Section.HorizontalOffset - Rail.HorizontalOffset) < .5;

        internal Fixture()
        {
            Shelf = new Shelf
            {
                MinimumCardWidth = 124, Height = 180, Margin = new Thickness(40), DataContext = Section,
                CardTemplate = (DataTemplate)XamlReader.Parse("""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                     <Button Content="{Binding Title}" Height="180" Margin="0,0,16,0"/>
                    </DataTemplate>
                    """)
            };
            Root.Children.Add(Shelf);
            Window = new Window
            {
                Content = Root, Width = 720, Height = 280, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
        }

        internal async Task Load()
        {
            Window.Show();
            Window.UpdateLayout();
            await Drain();
            Check(Rail.ScrollableWidth > .5, "fixture cards overflow the shelf viewport");
        }

        internal Rect Bounds(FrameworkElement element) =>
            new(element.TranslatePoint(new Point(), Root), element.RenderSize);

        internal bool HitWithin(FrameworkElement owner, Point point) =>
            Root.InputHitTest(point) is DependencyObject hit &&
            (ReferenceEquals(owner, hit) || owner.IsAncestorOf(hit));

        internal bool Key(Key key)
        {
            var source = PresentationSource.FromVisual(Shelf) ?? throw new InvalidOperationException("shelf presentation source");
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Shelf.RaiseEvent(e);
            return e.Handled;
        }

        public void Dispose() => Window.Close();
    }
}

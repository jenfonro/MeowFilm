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
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("music-shelf: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task HitPaths()
    {
        using var f = new Fixture();
        await f.Load();
        Check(f.Root.Focus(), "focus can leave the shelf");
        await Drain();
        Check(f.Next.Opacity == 0 && !f.Next.IsHitTestVisible, "resting arrows do not intercept pointer input");
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
        foreach (var arrow in new[] { f.Previous, f.Next })
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
        Check(f.Next.IsEnabled && !f.Previous.IsEnabled, "the first page only allows forward paging");
        Check(f.Next.IsTabStop && f.Previous.IsTabStop &&
            AutomationProperties.GetName(f.Next) == "下一组" &&
            AutomationProperties.GetName(f.Previous) == "上一组", "paging controls retain keyboard and accessible names");

        Check(f.Next.Focus(), "the hidden forward arrow can receive keyboard focus");
        await Drain();
        f.Next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Rail.HorizontalOffset > .5 && f.IsSettled, "forward page settles");
        var page = f.Rail.HorizontalOffset;
        Check(page <= f.Rail.ViewportWidth + .5 &&
            Math.Abs(page / f.Shelf.CardWidth - Math.Round(page / f.Shelf.CardWidth)) < .01,
            "paging advances a viewport-sized group of whole cards");
        Check(f.Previous.IsEnabled && f.Previous.Focus(), "backward paging becomes keyboard reachable");
        f.Previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled, "backward page restores the start");

        foreach (var (forward, backward) in new[] { (Key.Right, Key.Left), (Key.PageDown, Key.PageUp) })
        {
            f.Key(forward);
            await Until(() => Math.Abs(f.Rail.HorizontalOffset - page) < .5 && f.IsSettled,
                "keyboard forward paging matches the arrow");
            f.Key(backward);
            await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled, "keyboard backward paging restores the start");
        }
        f.Key(Key.End);
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - f.Rail.ScrollableWidth) < .5 &&
            f.IsSettled && !f.Next.IsEnabled, "End reaches the final boundary");
        f.Key(Key.Home);
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled && !f.Previous.IsEnabled,
            "Home reaches the first boundary");
        Check(ReferenceEquals(cards, f.Items.ItemsSource) && ReferenceEquals(selected, f.Items.SelectedItem) &&
            cardClicks == 0, "shelf paging does not replace content, change selection, or invoke card actions");

        Check(f.Root.Focus(), "keyboard focus can leave the shelf after paging");
        await Drain();
        Check(f.Next.Opacity == 0 && !f.Next.IsHitTestVisible, "arrows hide after keyboard focus leaves");
        f.Shelf.DataContext = new MusicCatalogSection("歌单", cards.Take(2).ToArray(), Type: "playlists");
        f.Window.UpdateLayout();
        await Until(() => f.Previous.Visibility == Visibility.Collapsed && f.Next.Visibility == Visibility.Collapsed &&
            f.Rail.HorizontalOffset < .5, "arrows collapse when the content no longer overflows");
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

        internal void Key(Key key)
        {
            var source = PresentationSource.FromVisual(Shelf) ?? throw new InvalidOperationException("shelf presentation source");
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Shelf.RaiseEvent(e);
            Check(e.Handled, "the shelf handles " + key);
        }

        public void Dispose() => Window.Close();
    }
}

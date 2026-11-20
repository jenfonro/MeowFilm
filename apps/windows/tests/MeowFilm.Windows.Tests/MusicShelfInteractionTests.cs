using MeowFilm.Desktop;
using Colors = Microsoft.UI.Colors;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicShelfInteractionTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("card-to-arrow hit paths remain inside the shelf while arrows are hidden or focused", HitPaths);
        yield return Case("keyboard access and paging retain card selection and overflow boundaries", Paging);
        yield return Case("resize retains logical columns without intermediate half cards", Resize);
        yield return Case("distant restore and navigation realize only nearby cards in normal layout", DistantRestore);
        yield return Case("continuous resize keeps distant and end anchors including narrow viewports", DistantResize);
        yield return Case("bound sources restore the latest section and preserve the outgoing settled target", BoundSourceRestore);
        yield return Case("collection changes keep the visible card mapping and exact overflow boundary", CollectionChanges);
        yield return Case("hidden shelves retain nearby containers and stop paging until shown", HiddenRetention);
        yield return Case("hidden resize and context changes restore only the latest geometric target", HiddenRestore);
        yield return Case("banners snap by pages and report continuous progress through the short final page", BannerSnapping);
        yield return Case("banner final page rejects forward keyboard navigation", BannerKeyboardBoundary);
        yield return Case("switching shelf types settles the outgoing offset with its own geometry", BannerContextRestore);
        yield return Case("distant banners retain virtualized containers and page targets across hidden resize", BannerDistantRestore);
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("music-shelf: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task HitPaths()
    {
        using var f = new Fixture();
        await f.Load();
        Check(f.Root.Focus(FocusState.Programmatic), "focus can leave the shelf");
        await Drain();
        Check(f.Previous.Visibility == Visibility.Collapsed &&
            f.Next.Visibility == Visibility.Visible &&
            f.Next.Opacity == 0 && !f.Next.IsHitTestVisible,
            "only the executable resting arrow exists and it does not intercept pointer input");
        CheckPaths(f);

        Check(f.Next.Focus(FocusState.Programmatic), "the next arrow remains keyboard reachable while hidden");
        await Drain();
        Check(DesktopTestUi.HasFocus(f.Shelf) && f.Next.Opacity == 1 && f.Next.IsHitTestVisible,
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
        foreach (var button in DesktopTestUi.Visuals(f.Items).OfType<Button>())
            button.Click += (_, _) => cardClicks++;
        Check(f.Next.Visibility == Visibility.Visible && f.Next.IsEnabled &&
            f.Previous.Visibility == Visibility.Collapsed,
            "the first page exposes only forward paging");
        Check(f.Next.IsTabStop && f.Previous.IsTabStop &&
            AutomationProperties.GetName(f.Next) == "下一组" &&
            AutomationProperties.GetName(f.Previous) == "上一组", "paging controls retain keyboard and accessible names");

        Check(f.Next.Focus(FocusState.Programmatic), "the hidden forward arrow can receive keyboard focus");
        await Drain();
        DesktopTestUi.Invoke(f.Next);
        await Until(() => f.Rail.HorizontalOffset > .5 && f.IsSettled, "forward page settles");
        var page = f.Rail.HorizontalOffset;
        var visibleCards = (int)Math.Round((f.Rail.ViewportWidth + 16) / f.Shelf.CardWidth,
            MidpointRounding.AwayFromZero);
        Check(Math.Abs(page - visibleCards * f.Shelf.CardWidth) < .5 &&
            page <= f.Rail.ViewportWidth + 16.5 &&
            Math.Abs(page / f.Shelf.CardWidth - Math.Round(page / f.Shelf.CardWidth)) < .01,
            "paging advances a viewport-sized group of whole cards");
        Check(f.Previous.Visibility == Visibility.Visible && f.Previous.IsEnabled && f.Previous.Focus(FocusState.Programmatic),
            "backward paging becomes keyboard reachable");
        CheckPaths(f);
        DesktopTestUi.Invoke(f.Previous);
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled &&
            f.Previous.Visibility == Visibility.Collapsed, "backward page restores the start");

        foreach (var (forward, backward) in new[] { (VirtualKey.Right, VirtualKey.Left), (VirtualKey.PageDown, VirtualKey.PageUp) })
        {
            Check(f.Key(forward), "the shelf handles executable forward paging");
            await Until(() => Math.Abs(f.Rail.HorizontalOffset - page) < .5 && f.IsSettled,
                "keyboard forward paging matches the arrow");
            Check(f.Key(backward), "the shelf handles executable backward paging");
            await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled, "keyboard backward paging restores the start");
        }
        f.Rail.ChangeView(f.Shelf.CardWidth * .37, null, null, true);
        DesktopTestUi.Invoke(f.Next);
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - page) < .5 && f.IsSettled,
            "paging from an in-between physical frame settles on a whole-column page");
        DesktopTestUi.Invoke(f.Previous);
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled,
            "whole-column paging returns to the start");
        Check(!f.Key(VirtualKey.Left), "a boundary key is left unhandled when no previous page exists");
        Check(f.Key(VirtualKey.End), "End is handled while the final boundary is reachable");
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
            f.IsSettled && f.Next.Visibility == Visibility.Collapsed, "End reaches the final boundary");
        Check(f.Rail.ScrollableWidth - f.Shelf.MaximumOffset > 8,
            "the trailing card margin is not exposed as another page");
        var previousPage = Math.Max(0,
            Math.Floor(Math.Max(0, f.Shelf.MaximumOffset - .5) / page) * page);
        DesktopTestUi.Invoke(f.Previous);
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - previousPage) < .5 && f.IsSettled,
            "the final partial page returns to the preceding page boundary");
        Check(f.Key(VirtualKey.End), "End remains available after leaving the final partial page");
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
            f.IsSettled, "End restores the final boundary");
        var end = f.Rail.HorizontalOffset;
        DesktopTestUi.Invoke(f.Next);
        await Drain();
        Check(Math.Abs(f.Rail.HorizontalOffset - end) < .01 && !f.Key(VirtualKey.Right),
            "the final page rejects empty forward actions");
        Check(f.Key(VirtualKey.Home), "Home is handled while the first boundary is reachable");
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled &&
            f.Previous.Visibility == Visibility.Collapsed,
            "Home reaches the first boundary");
        Check(ReferenceEquals(cards, f.Items.ItemsSource) && ReferenceEquals(selected, f.Items.SelectedItem) &&
            cardClicks == 0, "shelf paging does not replace content, change selection, or invoke card actions");

        Check(f.Root.Focus(FocusState.Programmatic), "keyboard focus can leave the shelf after paging");
        await Drain();
        Check(f.Next.Opacity == 0 && !f.Next.IsHitTestVisible, "arrows hide after keyboard focus leaves");
        f.Shelf.DataContext = new MusicCatalogSection("歌单", cards.Take(visibleCards).ToArray(), Type: "playlists");
        DesktopTestUi.WindowRoot(f.Window).UpdateLayout();
        await Until(() => f.Previous.Visibility == Visibility.Collapsed && f.Next.Visibility == Visibility.Collapsed &&
            f.Rail.HorizontalOffset < .5, "arrows collapse when the content no longer overflows");
        Check(f.Rail.ScrollableWidth > .5,
            "a template's trailing margin may remain in the native extent without becoming a page");
    }

    private static async Task Resize()
    {
        using var f = new Fixture();
        await f.Load();
        Check(f.Key(VirtualKey.PageDown), "the fixture can establish a nonzero logical anchor");
        await Until(() => f.Rail.HorizontalOffset > .5 && f.IsSettled, "initial anchor settles");
        var anchor = (int)Math.Round(f.Rail.HorizontalOffset / f.Shelf.CardWidth,
            MidpointRounding.AwayFromZero);
        var frames = new List<(double Width, double Offset)>();
        EventHandler<object> rendered = (_, _) =>
        {
            if (f.Shelf.CardWidth > 1)
                frames.Add((f.Shelf.CardWidth, f.Shelf.PresentedOffset));
        };
        CompositionTarget.Rendering += rendered;
        try
        {
            f.Window.Resize(910, DesktopTestUi.WindowSize(f.Window).Height);
            DesktopTestUi.WindowRoot(f.Window).UpdateLayout();
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

        f.Window.Resize(760, DesktopTestUi.WindowSize(f.Window).Height);
        f.Window.Resize(840, DesktopTestUi.WindowSize(f.Window).Height);
        f.Window.Resize(680, DesktopTestUi.WindowSize(f.Window).Height);
        DesktopTestUi.WindowRoot(f.Window).UpdateLayout();
        await Until(() => Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - anchor) < .01 &&
            f.IsSettled, "continuous resize keeps one anchor generation");

        Check(f.Key(VirtualKey.Home), "the fixture can return before testing animation interruption");
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled, "start boundary settles");
        var oldWidth = f.Shelf.CardWidth;
        var expectedAnimatedAnchor = Math.Max(1,
            (int)Math.Round((f.Rail.ViewportWidth + 16) / oldWidth,
                MidpointRounding.AwayFromZero));
        DesktopTestUi.Invoke(f.Next);
        f.Window.Resize(860, DesktopTestUi.WindowSize(f.Window).Height);
        DesktopTestUi.WindowRoot(f.Window).UpdateLayout();
        await Until(() => Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - expectedAnimatedAnchor) < .01 &&
            f.IsSettled, "resize during paging retains the animation target rather than its partial pixels");

        Check(f.Key(VirtualKey.End), "the fixture can establish a final-page anchor");
        await Until(() => Math.Abs(f.Shelf.MaximumOffset - f.Rail.HorizontalOffset) < .5 &&
            f.IsSettled, "final page settles before resize");
        f.Window.Resize(730, DesktopTestUi.WindowSize(f.Window).Height);
        DesktopTestUi.WindowRoot(f.Window).UpdateLayout();
        await Until(() => Math.Abs(f.Shelf.MaximumOffset - f.Rail.HorizontalOffset) < .5 &&
            f.IsSettled && f.Next.Visibility == Visibility.Collapsed,
            "a resized final page remains anchored to the real end");
    }

    private static async Task DistantRestore()
    {
        using var f = new Fixture(10000);
        // The existing 640px viewport fits four 124px cards plus their 16px gaps.
        f.Section.HorizontalOffset = 7800 * (656d / 4);
        await f.Load(updateLayout: false);
        Check(Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - 7800) < .01 &&
            f.IsSettled, "the first normal layout restores a distant saved column");
        CheckRealized(f);

        Check(f.Key(VirtualKey.End), "a distant final boundary is keyboard reachable");
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
            f.IsSettled, "End reaches the exact full extent");
        CheckRealized(f);
        Check(f.Next.Visibility == Visibility.Collapsed && !f.Key(VirtualKey.Right),
            "the far end has no extra page for its trailing margin");
        Check(f.Key(VirtualKey.Home), "Home remains reachable after the far end");
        await Until(() => f.Rail.HorizontalOffset < .5 && f.IsSettled, "Home settles after a distant jump");
        CheckRealized(f);

        f.Items.ScrollIntoView(f.Items.Items[7400]);
        await Drain();
        var brought = f.Items.ContainerFromIndex(7400) as FrameworkElement;
        Check(brought is not null && brought.TransformToVisual(f.Rail).TransformPoint(new Point()).X >= -.5 &&
            brought.TransformToVisual(f.Rail).TransformPoint(new Point(brought.ActualWidth - 16, 0)).X <= f.Rail.ViewportWidth + .5,
            "native item navigation realizes and reveals a distant card");
        CheckRealized(f);

        f.Root.Children.Remove(f.Shelf);
        await Drain();
        f.Section.HorizontalOffset = 6300 * f.Shelf.CardWidth;
        f.Root.Children.Add(f.Shelf);
        await Drain();
        Check(Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - 6300) < .01 &&
            f.IsSettled, "reattaching the shelf restores the saved offset in normal layout");
        CheckRealized(f);
    }

    private static async Task DistantResize()
    {
        using var f = new Fixture(10000);
        await f.Load(updateLayout: false);
        var restored = f.Section with { Title = "远端恢复", HorizontalOffset = 6400 * f.Shelf.CardWidth };
        f.Shelf.DataContext = restored;
        f.Window.Resize(910, DesktopTestUi.WindowSize(f.Window).Height);
        await Drain();
        Check(Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - 6400) < .01 &&
            Math.Abs(restored.HorizontalOffset - f.Rail.HorizontalOffset) < .5,
            "a resize concurrent with restore preserves its pending logical column");
        CheckRealized(f);

        var frames = new List<(double Pitch, double Offset, double Left)>();
        EventHandler<object> rendered = (_, _) =>
        {
            if (f.Items.ContainerFromIndex(6400) is FrameworkElement card)
                frames.Add((f.Shelf.CardWidth, f.Shelf.PresentedOffset, card.TransformToVisual(f.Rail).TransformPoint(new Point()).X));
        };
        CompositionTarget.Rendering += rendered;
        try
        {
            foreach (var width in new[] { 760d, 840, 680, 150, 520, 1100, 720 })
            {
                f.Window.Resize(width, DesktopTestUi.WindowSize(f.Window).Height);
                await RenderFrame();
                await Drain();
                Check(Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - 6400) < .01 &&
                    Math.Abs(restored.HorizontalOffset - f.Rail.HorizontalOffset) < .5,
                    $"normal layout preserves the distant column at window width {width}");
                CheckRealized(f);
            }
        }
        finally { CompositionTarget.Rendering -= rendered; }
        Check(frames.Count > 0 && frames.All(frame =>
                Math.Abs(frame.Offset / frame.Pitch - 6400) < .01 && Math.Abs(frame.Left) < 1),
            "each rendered resize frame places the anchored card at the viewport edge: " +
            string.Join(", ", frames.Where(frame =>
                    Math.Abs(frame.Offset / frame.Pitch - 6400) >= .01 || Math.Abs(frame.Left) >= 1)
                .Select(frame => $"{frame.Offset:0.##}/{frame.Pitch:0.##}@{frame.Left:0.##}")));

        Check(f.Key(VirtualKey.End), "the far final page can establish an end anchor");
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
            Math.Abs(restored.HorizontalOffset - f.Rail.HorizontalOffset) < .5, "the final page settles");
        foreach (var width in new[] { 150d, 880, 600 })
        {
            f.Window.Resize(width, DesktopTestUi.WindowSize(f.Window).Height);
            await RenderFrame();
            await Drain();
            Check(Math.Abs(f.Rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
                f.Next.Visibility == Visibility.Collapsed, "resize keeps the final card at the real boundary");
            var last = (FrameworkElement)f.Items.ContainerFromIndex(f.Items.Items.Count - 1);
            Check(Math.Abs(last.TransformToVisual(f.Rail).TransformPoint(new Point(last.ActualWidth - 16, 0)).X -
                f.Rail.ViewportWidth) < 1, "the last card's content, excluding its gap, fills the resized viewport");
            CheckRealized(f);
        }
    }

    private static async Task BoundSourceRestore()
    {
        using var f = new Fixture(10000);
        f.Shelf.SetBinding(Shelf.ItemsSourceProperty, new Binding { Path = new PropertyPath(nameof(MusicCatalogSection.Items)) });
        await f.Load(updateLayout: false);
        var second = f.Section with
        {
            Title = "其他栏目", Items = f.Section.Items.ToArray(), HorizontalOffset = 5100 * f.Shelf.CardWidth
        };
        f.Rail.ChangeView(f.Shelf.CardWidth * 20.37, null, null, true);
        await Drain();
        f.Shelf.DataContext = second;
        await Drain();
        Check(Math.Abs(f.Section.HorizontalOffset - 20 * f.Shelf.CardWidth) < .5,
            "a bound source commits the outgoing legal snap target before its context changes");
        Check(Math.Abs(f.Rail.HorizontalOffset - second.HorizontalOffset) < .5 &&
            Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - 5100) < .01,
            "a bound source restores its incoming distant offset without a retry");
        CheckRealized(f);

        var abandoned = f.Section with
        {
            Title = "过期栏目", Items = f.Section.Items.ToArray(), HorizontalOffset = 3400 * f.Shelf.CardWidth
        };
        f.Shelf.DataContext = abandoned;
        f.Shelf.DataContext = f.Section;
        await Drain();
        Check(Math.Abs(f.Rail.HorizontalOffset - 20 * f.Shelf.CardWidth) < .5 &&
            Math.Abs(abandoned.HorizontalOffset - 3400 * f.Shelf.CardWidth) < .5,
            "only the latest context consumes the pending restore and an unpresented section retains its offset");
        CheckRealized(f);
    }

    private static async Task CollectionChanges()
    {
        using var f = new Fixture(1000);
        var cards = new ObservableCollection<MusicCatalogCard>(f.Section.Items);
        f.Shelf.ItemsSource = cards;
        await f.Load(updateLayout: false);
        f.Rail.ChangeView(400 * f.Shelf.CardWidth, null, null, true);
        await Until(() => Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - 400) < .01 && f.IsSettled,
            "native offset changes settle on the requested distant column");
        CheckRealized(f);
        cards.RemoveAt(400);
        cards.Insert(401, new MusicCatalogCard("inserted", "新卡片", "", "", "playlist", "wy"));
        cards.Move(402, 399);
        cards[403] = new MusicCatalogCard("replaced", "替换卡片", "", "", "playlist", "wy");
        await Drain();
        CheckRealized(f);

        cards.Clear();
        foreach (var card in f.Section.Items.Take(3)) cards.Add(card);
        await Drain();
        Check(f.Rail.HorizontalOffset < .5 && f.Shelf.MaximumOffset < .5 &&
            f.Previous.Visibility == Visibility.Collapsed && f.Next.Visibility == Visibility.Collapsed,
            "resetting to a short source removes overflow without retaining a stale distant offset");
        CheckRealized(f);
    }

    private static async Task HiddenRetention()
    {
        using var f = new Fixture(10000);
        f.Section.HorizontalOffset = 6400 * (656d / 4);
        await f.Load(updateLayout: false);
        var rail = f.Rail;
        var source = f.Items.ItemsSource;
        var container = f.Items.ContainerFromIndex(6400);
        Check(container is not null && f.IsSettled, "a distant visible card is realized before hiding");
        foreach (var visibility in new[] { Visibility.Collapsed })
        {
            var saved = f.Section.HorizontalOffset;
            f.Shelf.Visibility = visibility;
            Check(f.Shelf.IsLoaded && ReferenceEquals(rail, f.Shelf.ScrollHost) && f.Shelf.IsSettled,
                "hiding deactivates presentation without unloading the shelf");
            Check(ReferenceEquals(source, f.Items.ItemsSource) &&
                ReferenceEquals(container, f.Items.ContainerFromIndex(6400)),
                "hiding retains the item source and realized card");
            foreach (var key in new[] { VirtualKey.Home, VirtualKey.End, VirtualKey.PageDown, VirtualKey.Left })
                Check(!f.Key(key), "hidden routed keys cannot consume the saved restore or restart paging");
            DesktopTestUi.Invoke(f.Next);
            DesktopTestUi.Invoke(f.Previous);
            await Drain();
            Check(Math.Abs(f.Section.HorizontalOffset - saved) < .01 &&
                Math.Abs(rail.HorizontalOffset - saved) < .5 && f.Shelf.IsSettled,
                "hidden page commands leave the settled position and animation state unchanged");
            f.Shelf.Visibility = Visibility.Visible;
            await Drain();
            Check(ReferenceEquals(rail, f.Rail) && ReferenceEquals(source, f.Items.ItemsSource) &&
                ReferenceEquals(container, f.Items.ContainerFromIndex(6400)) &&
                Math.Abs(f.Rail.HorizontalOffset - saved) < .5 && f.IsSettled,
                "returning at the same width reuses the scroller and card at the saved offset");
            CheckRealized(f);
        }

        var page = Math.Round((rail.ViewportWidth + 16) / f.Shelf.CardWidth,
            MidpointRounding.AwayFromZero) * f.Shelf.CardWidth;
        var expected = Math.Min(f.Shelf.MaximumOffset, f.Section.HorizontalOffset + page);
        DesktopTestUi.Invoke(f.Next);
        f.Shelf.Visibility = Visibility.Collapsed;
        Check(Math.Abs(f.Section.HorizontalOffset - expected) < .5 && f.Shelf.IsSettled,
            "hiding during paging immediately saves the legal target and removes the animation clock");
        DesktopTestUi.Invoke(f.Next);
        Check(!f.Key(VirtualKey.Home) && Math.Abs(f.Section.HorizontalOffset - expected) < .5,
            "hidden input cannot replace an interrupted paging target");
        f.Shelf.Visibility = Visibility.Visible;
        await Drain();
        Check(ReferenceEquals(rail, f.Rail) && ReferenceEquals(source, f.Items.ItemsSource) &&
            Math.Abs(f.Rail.HorizontalOffset - expected) < .5 && f.IsSettled,
            "returning applies the interrupted paging target in normal layout");
        CheckRealized(f);
    }

    private static async Task HiddenRestore()
    {
        using var f = new Fixture(10000);
        f.Section.HorizontalOffset = 6400 * (656d / 4);
        await f.Load(updateLayout: false);
        var rail = f.Rail;
        var pitch = f.Shelf.CardWidth;
        var saved = f.Section.HorizontalOffset;
        f.Shelf.Visibility = Visibility.Collapsed;
        f.Window.Resize(910, DesktopTestUi.WindowSize(f.Window).Height);
        await Drain();
        Check(f.Shelf.CardWidth == pitch && f.Section.HorizontalOffset == saved &&
            f.Shelf.IsSettled,
            "hidden layout retains the last visible geometry and saved column until return");
        var abandoned = f.Section with
        {
            Title = "隐藏中过期栏目", Items = f.Section.Items.ToArray(), HorizontalOffset = 3400 * pitch
        };
        var latest = f.Section with
        {
            Title = "隐藏中最新栏目", Items = f.Section.Items.ToArray(), HorizontalOffset = 5300 * pitch
        };
        f.Shelf.DataContext = abandoned;
        f.Shelf.DataContext = latest;
        await Drain();
        Check(f.Section.HorizontalOffset == saved && abandoned.HorizontalOffset == 3400 * pitch &&
            latest.HorizontalOffset == 5300 * pitch && f.Shelf.CardWidth == pitch,
            "hidden context changes do not commit stale geometry into any section");
        f.Shelf.Visibility = Visibility.Visible;
        await Drain();
        Check(ReferenceEquals(rail, f.Rail) && ReferenceEquals(latest.Items, f.Items.ItemsSource) &&
            Math.Abs(f.Shelf.CardWidth - pitch) > .01 &&
            Math.Abs(rail.HorizontalOffset / f.Shelf.CardWidth - 5300) < .01 &&
            Math.Abs(latest.HorizontalOffset - rail.HorizontalOffset) < .5,
            "returning restores only the latest context and resizes its logical anchor together");
        CheckRealized(f);

        Check(f.Key(VirtualKey.End), "the latest context can establish its final boundary");
        await Until(() => Math.Abs(rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
            Math.Abs(latest.HorizontalOffset - rail.HorizontalOffset) < .5, "latest final page settles");
        var end = latest.HorizontalOffset;
        f.Shelf.Visibility = Visibility.Collapsed;
        f.Window.Resize(150, DesktopTestUi.WindowSize(f.Window).Height);
        await Drain();
        Check(latest.HorizontalOffset == end && f.Shelf.IsSettled,
            "a collapsed narrow resize does not consume the saved end anchor");
        f.Shelf.Visibility = Visibility.Visible;
        await Drain();
        Check(Math.Abs(rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
            Math.Abs(latest.HorizontalOffset - rail.HorizontalOffset) < .5 &&
            f.Next.Visibility == Visibility.Collapsed,
            "returning in a narrow viewport restores the true end rather than its old pixels");
        CheckRealized(f);
    }

    private static async Task BannerSnapping()
    {
        using var f = new Fixture(9, banner: true);
        await f.Load(updateLayout: false);
        var indicator = (ShelfPageIndicator)f.Shelf.FindName("PageIndicator");
        var pitch = f.Shelf.CardWidth;
        Check(Math.Abs(pitch - (f.Rail.ViewportWidth + 20) / 2) < .01,
            "the banner viewport contains two cards with a 20px trailing gap");
        Check(indicator.PageCount == 5 && indicator.Visibility == Visibility.Visible,
            "nine banners expose five real groups, including the short final interval");
        var observations = new List<(double Offset, double Position)>();
        EventHandler<ScrollViewerViewChangedEventArgs> observe = (_, e) =>
        {
            observations.Add((f.Rail.HorizontalOffset, indicator.Position));
        };
        f.Rail.ViewChanged += observe;
        try
        {
            foreach (var (columns, position, finalColumn, finalPage) in new[]
                { (.8, .4, 0d, 0d), (1.2, .6, 2d, 1d), (6.4, 3.4, 6d, 3d),
                  (6.6, 3.6, 7d, 4d), (6.5, 3.5, 7d, 4d) })
            {
                var start = observations.Count;
                f.Rail.ChangeView(columns * pitch, null, null, true);
                await Until(() =>
                    observations.Skip(start).Any(sample => Math.Abs(sample.Offset - columns * pitch) < .5) &&
                    Math.Abs(f.Rail.HorizontalOffset - finalColumn * pitch) < .5 && f.IsSettled,
                    $"banner offset {columns} settles on page {finalPage}",
                    () => $"pitch={pitch}, actualPitch={f.Shelf.CardWidth}, offset={f.Rail.HorizontalOffset}, " +
                        $"saved={f.Section.HorizontalOffset}, animating={!f.Shelf.IsSettled}, " +
                        $"samples={string.Join(';', observations.Skip(start).Select(sample => $"{sample.Offset:0.###}/{sample.Position:0.###}"))}");
                Check(observations.Skip(start).Any(sample => Math.Abs(sample.Offset - columns * pitch) < .5 &&
                    Math.Abs(sample.Position - position) < .01),
                    "indicator follows the actual offset continuously, including the short last interval, without its own clock");
                Check(Math.Abs(indicator.Position - finalPage) < .01, "settled indicator reports the real destination page");
                CheckRealized(f);
            }
        }
        finally { f.Rail.ViewChanged -= observe; }
        Check(Math.Abs(f.Rail.HorizontalOffset - 7 * pitch) < .5 &&
            f.Next.Visibility == Visibility.Collapsed && !f.Key(VirtualKey.Right),
            "the odd final page stops at the actual end with no empty forward move");
        var last = (ListViewItem)f.Items.ContainerFromIndex(8);
        Check(Math.Abs(last.TransformToVisual(f.Rail).TransformPoint(new Point(last.ActualWidth - 20, 0)).X -
            f.Rail.ViewportWidth) < .5, "the last banner content ends at the viewport, not after an empty gap");
        DesktopTestUi.Invoke(f.Previous);
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - 6 * pitch) < .5 && f.IsSettled,
            "the short final interval returns to the preceding full-page boundary");
        var resizing = false;
        EventHandler<ScrollViewerViewChangedEventArgs> resize = (_, e) =>
        {
            if (resizing || Math.Abs(f.Rail.HorizontalOffset - 5.2 * pitch) >= .5) return;
            resizing = true;
            // ScrollViewer drops its post-layout listener after raising ScrollChanged. A real
            // WM_SIZE is a later message, so resize in the next operation before the snap timer.
            f.Window.DispatcherQueue.TryEnqueue(() => { f.Window.Resize(910, DesktopTestUi.WindowSize(f.Window).Height); });
        };
        f.Rail.ViewChanged += resize;
        try
        {
            f.Rail.ChangeView(5.2 * pitch, null, null, true);
            await Until(() => resizing && Math.Abs(f.Shelf.CardWidth - 425) < .01 &&
                Math.Abs(f.Rail.HorizontalOffset - 6 * 425d) < .5 && f.IsSettled,
                "resize during native scrolling keeps the nearest whole banner page instead of an intermediate column",
                () => $"resizing={resizing}, window={DesktopTestUi.WindowSize(f.Window).Width}/{DesktopTestUi.WindowRoot(f.Window).ActualWidth}, viewport={f.Rail.ViewportWidth}, " +
                    $"pitch={f.Shelf.CardWidth}, offset={f.Rail.HorizontalOffset}, saved={f.Section.HorizontalOffset}");
        }
        finally { f.Rail.ViewChanged -= resize; }
        f.Shelf.DataContext = f.Section with { Items = f.Section.Items.Take(8).ToArray(), HorizontalOffset = 0 };
        f.Window.Resize(723.2, DesktopTestUi.WindowSize(f.Window).Height);
        await Until(() => Math.Abs(f.Shelf.CardWidth - 425) > .01 && f.Rail.HorizontalOffset < .5,
            "an even banner source restores after a fractional requested window width");
        Check(Math.Abs(f.Shelf.CardWidth - (f.Rail.ViewportWidth + 20) / 2) < .01 &&
            indicator.PageCount == 4 && indicator.Position == 0,
            "page count comes from reachable groups, not floating-point extent rounding");
        CheckRealized(f);
    }

    private static async Task BannerKeyboardBoundary()
    {
        using var f = new Fixture(9, banner: true);
        await f.Load();
        f.Rail.ChangeView(f.Rail.ScrollableWidth, null, null, true);
        await Until(() => Math.Abs(f.Rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 && f.IsSettled,
            "the final banner page is settled before checking keyboard access");
        Check(!f.Key(VirtualKey.Right), "the final banner page rejects an empty forward keyboard move");
    }

    private static async Task BannerContextRestore()
    {
        using var f = new Fixture(9, banner: true);
        await f.Load(updateLayout: false);
        var bannerPitch = f.Shelf.CardWidth;
        var normal = f.Section with { Layout = "playlist-rail", HorizontalOffset = 0 };
        await SwitchAt(1.2 * bannerPitch, normal, 720);
        Check(Math.Abs(f.Section.HorizontalOffset - 2 * bannerPitch) < .5,
            "an outgoing banner settles to a whole page even when the incoming context is not a banner");
        var normalPitch = f.Shelf.CardWidth;
        await SwitchAt(1.2 * normalPitch, f.Section, 910);
        Check(Math.Abs(normal.HorizontalOffset - normalPitch) < .5 &&
            Math.Abs(f.Shelf.CardWidth - 425) < .01 && Math.Abs(f.Rail.HorizontalOffset - 2 * 425d) < .5,
            "an outgoing ordinary shelf keeps column snapping and the returning banner restores its own page across resize");
        CheckRealized(f);

        async Task SwitchAt(double offset, MusicCatalogSection incoming, double width)
        {
            var rail = f.Rail;
            var switched = false;
            EventHandler<ScrollViewerViewChangedEventArgs> change = (_, e) =>
            {
                if (switched || Math.Abs(f.Rail.HorizontalOffset - offset) >= .5) return;
                switched = true;
                // Keep the switch and resize together at the unsnapped offset, outside ScrollChanged.
                f.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    f.Shelf.DataContext = incoming;
                    f.Window.Resize(width, DesktopTestUi.WindowSize(f.Window).Height);
                });
            };
            rail.ViewChanged += change;
            try
            {
                rail.ChangeView(offset, null, null, true);
                await Until(() => switched && ReferenceEquals(f.Items.ItemsSource, incoming.Items) &&
                    Math.Abs(rail.HorizontalOffset - incoming.HorizontalOffset) < .5,
                    "the incoming section restores after a context switch during native scrolling",
                    () => $"switched={switched}, context={ReferenceEquals(f.Shelf.DataContext, incoming)}, " +
                        $"source={ReferenceEquals(f.Items.ItemsSource, incoming.Items)}, window={DesktopTestUi.WindowSize(f.Window).Width}/{DesktopTestUi.WindowRoot(f.Window).ActualWidth}, " +
                        $"pitch={f.Shelf.CardWidth}, offset={rail.HorizontalOffset}, saved={incoming.HorizontalOffset}");
            }
            finally { rail.ViewChanged -= change; }
        }
    }

    private static async Task BannerDistantRestore()
    {
        using var f = new Fixture(10001, banner: true);
        f.Section.HorizontalOffset = 7800 * 330d;
        await f.Load(updateLayout: false);
        var rail = f.Rail;
        var source = f.Items.ItemsSource;
        var indicator = (ShelfPageIndicator)f.Shelf.FindName("PageIndicator");
        var card = f.Items.ContainerFromIndex(7800);
        Check(Math.Abs(f.Rail.HorizontalOffset / f.Shelf.CardWidth - 7800) < .01 && f.IsSettled &&
            indicator.PageCount == 5001 && Math.Abs(indicator.Position - 3900) < .01,
            "a distant two-banner page restores from exact geometry with the full reachable page count");
        CheckRealized(f);
        f.Shelf.Visibility = Visibility.Collapsed;
        DesktopTestUi.Invoke(f.Next);
        Check(!f.Key(VirtualKey.End) && f.Shelf.IsSettled,
            "hidden banner input cannot restart paging or indicator animation");
        f.Shelf.Visibility = Visibility.Visible;
        await Drain();
        Check(ReferenceEquals(rail, f.Rail) && ReferenceEquals(source, f.Items.ItemsSource) &&
            ReferenceEquals(card, f.Items.ContainerFromIndex(7800)) &&
            Math.Abs(indicator.Position - 3900) < .01 && f.IsSettled,
            "returning at the same width retains the distant banner container and page");

        DesktopTestUi.Invoke(f.Next);
        f.Shelf.Visibility = Visibility.Collapsed;
        Check(Math.Abs(f.Section.HorizontalOffset - 7802 * 330d) < .5 && f.Shelf.IsSettled,
            "hiding interrupted banner paging saves the full destination page");
        f.Window.Resize(910, DesktopTestUi.WindowSize(f.Window).Height);
        await Drain();
        f.Shelf.Visibility = Visibility.Visible;
        await Drain();
        Check(ReferenceEquals(rail, f.Rail) && Math.Abs(f.Shelf.CardWidth - 425) < .01 &&
            Math.Abs(rail.HorizontalOffset / f.Shelf.CardWidth - 7802) < .01 &&
            Math.Abs(indicator.Position - 3901) < .01 && f.IsSettled,
            "hidden resize restores the interrupted page at the new two-banner pitch");
        CheckRealized(f);
        Check(f.Key(VirtualKey.End), "End reaches the distant odd final page");
        await Until(() => Math.Abs(rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 &&
            Math.Abs(indicator.Position - 5000) < .01 && f.IsSettled, "the distant final page settles");
        f.Window.Resize(150, DesktopTestUi.WindowSize(f.Window).Height);
        await Drain();
        Check(Math.Abs(f.Shelf.CardWidth - 45) < .01 &&
            Math.Abs(rail.HorizontalOffset - f.Shelf.MaximumOffset) < .5 && f.IsSettled &&
            Math.Abs(indicator.Position - 5000) < .01 && f.Next.Visibility == Visibility.Collapsed,
            "a narrow resize still shows two banners and keeps the real end and final indicator");
        CheckRealized(f);
    }

    private static void CheckRealized(Fixture f)
    {
        var pitch = f.Shelf.CardWidth;
        var offset = f.Rail.HorizontalOffset;
        var visible = (int)Math.Ceiling(f.Rail.ViewportWidth / pitch);
        var first = (int)Math.Floor((offset + .01) / pitch);
        var last = Math.Min(f.Items.Items.Count - 1, (int)Math.Ceiling((offset + f.Rail.ViewportWidth) / pitch) - 1);
        var realized = Enumerable.Range(0, f.Items.Items.Count)
            .Where(index => f.Items.ContainerFromIndex(index) is not null).ToArray();
        Check(realized.Length > 0 && realized.Length <= 3 * visible + 2 &&
            realized.All(index => index >= Math.Max(0, first - visible - 1) && index <= last + visible + 1),
            $"only the viewport and adjacent cache are realized ({realized.Length} containers at {first})");
        for (var index = first; index <= last; index++)
            Check(f.Items.ContainerFromIndex(index) is ListViewItem,
                $"visible card {index} has a container after normal layout");
        foreach (var index in realized)
        {
            var card = (ListViewItem)f.Items.ContainerFromIndex(index);
            Check(ReferenceEquals(card.Content, f.Items.Items[index]),
                $"recycled card {index} presents the current item");
            Check(Math.Abs(card.TransformToVisual(f.Rail).TransformPoint(new Point()).X - (index * pitch - offset)) < 1 &&
                Math.Abs(card.ActualWidth - pitch) < 1, $"card {index} follows the actual fixed-pitch geometry");
        }
        Check(Math.Abs(f.Rail.ExtentWidth - f.Items.Items.Count * pitch) < .5,
            "the full extent is exact regardless of which containers are realized");
    }

    private static async Task RenderFrame()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object> rendered = (_, _) => completed.TrySetResult();
        CompositionTarget.Rendering += rendered;
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { CompositionTarget.Rendering -= rendered; }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++) await DesktopTestUi.DrainAsync();
    }

    private static async Task Until(Func<bool> done, string message, Func<string>? details = null)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8))
                throw new TimeoutException(details is null ? message : $"{message}: {details()}");
            await DesktopTestUi.DrainAsync();
            if (!done()) await Task.Delay(1);
        }
        await Drain();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Grid Root = new() { Background = (new SolidColorBrush(Microsoft.UI.Colors.Transparent)), IsTabStop = true };
        internal readonly MusicCatalogSection Section;
        internal readonly Shelf Shelf;
        internal readonly Window Window;
        internal ScrollViewer Rail => Shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host");
        internal ListView Items => (ListView)Shelf.FindName("ItemsHost");
        internal Button Previous => (Button)Shelf.FindName("Previous");
        internal Button Next => (Button)Shelf.FindName("Next");
        internal bool IsSettled => Math.Abs(Section.HorizontalOffset - Rail.HorizontalOffset) < .5;

        internal Fixture(int itemCount = 18, bool banner = false)
        {
            Section = new MusicCatalogSection("歌单", Enumerable.Range(1, itemCount)
                .Select(i => new MusicCatalogCard(i.ToString(), "歌单" + i, "", "", "playlist", "wy"))
                .ToArray(), Layout: banner ? "banner" : "playlist-rail");
            Shelf = new Shelf
            {
                MinimumCardWidth = 124, Height = banner ? 204 : 180, Margin = new Thickness(40), DataContext = Section,
                CardTemplate = (DataTemplate)XamlReader.Load($$"""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                     <Button Content="{Binding Title}" Height="180" Margin="0,0,{{(banner ? 20 : 16)}},0"/>
                    </DataTemplate>
                    """)
            };
            Root.Children.Add(Shelf);
            Window = DesktopTestUi.CreateWindow(Root, 720, banner ? 304 : 280);
        }

        internal async Task Load(bool updateLayout = true)
        {
            Window.Show();
            if (updateLayout) DesktopTestUi.WindowRoot(Window).UpdateLayout();
            await Drain();
            Check(Rail.ScrollableWidth > .5, "fixture cards overflow the shelf viewport");
        }

        internal Rect Bounds(FrameworkElement element) =>
            new(element.TransformToVisual(Root).TransformPoint(new Point()), new Size(element.ActualWidth, element.ActualHeight));

        internal bool HitWithin(FrameworkElement owner, Point point) =>
            DesktopTestUi.Hit(Root, point) is DependencyObject hit &&
            (ReferenceEquals(owner, hit) || MeowFilm.Desktop.DesktopVisual.Contains(owner, hit));

        internal bool Key(VirtualKey key) => Shelf.Navigate(key);

        public void Dispose() => Window.Close();
    }
}

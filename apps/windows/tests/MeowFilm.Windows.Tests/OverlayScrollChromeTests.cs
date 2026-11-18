using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class OverlayScrollChromeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native scroll provider moves the content without changing its layout", async () =>
        {
            using var scene = new Fixture();
            await DesktopTestUi.DrainAsync();
            var provider = Provider(scene.Scroll);
            Check(provider.VerticallyScrollable && !provider.HorizontallyScrollable, "native scroll ranges are exposed");
            double width = scene.Content.ActualWidth;
            provider.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            await DesktopTestUi.DrainAsync();
            Check(scene.Scroll.VerticalOffset > 0, "the actual accessibility scroll action advances the viewer");
            Check(scene.Content.ActualWidth == width, "scrollbar activity does not resize content");
            provider.SetScrollPercent(-1, 100); await DesktopTestUi.DrainAsync();
            Check(Math.Abs(scene.Scroll.VerticalOffset - scene.Scroll.ScrollableHeight) < 1, "end of range is reachable");
            provider.SetScrollPercent(-1, 0); await DesktopTestUi.DrainAsync();
            Check(scene.Scroll.VerticalOffset == 0, "start of range is reachable");
        });
        yield return Case("nested viewers keep independent offsets and bounded accessibility actions", async () =>
        {
            using var scene = new Fixture();
            var inner = new ScrollViewer { Width = 280, Height = 160, Content = new Border { Height = 1200 },
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            scene.Content.Child = inner;
            await DesktopTestUi.DrainAsync();
            Provider(inner).Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            await DesktopTestUi.DrainAsync();
            Check(inner.VerticalOffset > 0 && scene.Scroll.VerticalOffset == 0, "inner scrolling does not move its enclosing viewer");
            Provider(inner).SetScrollPercent(-1, 100); await DesktopTestUi.DrainAsync();
            Provider(inner).Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            await DesktopTestUi.DrainAsync();
            Check(scene.Scroll.VerticalOffset == 0 && Math.Abs(inner.VerticalOffset - inner.ScrollableHeight) < 1,
                "an inner boundary does not invent an outer scrolling command");
        });
        yield return Case("hide and reattach preserve content and offset without custom timers", async () =>
        {
            using var scene = new Fixture(); await DesktopTestUi.DrainAsync();
            scene.Scroll.ChangeView(null, 260, null, true); await DesktopTestUi.DrainAsync();
            double offset = scene.Scroll.VerticalOffset;
            scene.Root.Visibility = Visibility.Collapsed; await DesktopTestUi.DrainAsync();
            Check(scene.Scroll.IsLoaded && !DesktopVisual.IsVisible(scene.Scroll), "ancestor hiding retains the viewer");
            scene.Root.Visibility = Visibility.Visible; await DesktopTestUi.DrainAsync();
            Check(ReferenceEquals(scene.Scroll.Content, scene.Content) && Math.Abs(scene.Scroll.VerticalOffset - offset) < 1,
                "returning reuses content and its settled offset");
            scene.Root.Children.Remove(scene.Scroll); await DesktopTestUi.DrainAsync();
            Check(!scene.Scroll.IsLoaded, "unloading detaches the real viewer");
            scene.Root.Children.Add(scene.Scroll); await DesktopTestUi.DrainAsync();
            Provider(scene.Scroll).Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            await DesktopTestUi.DrainAsync();
            Check(scene.Scroll.VerticalOffset > offset, "reattachment restores native scrolling once");
        });
        yield return Case("attached policy updates the native vertical mode and preserves horizontal settings", async () =>
        {
            using var scene = new Fixture(); await DesktopTestUi.DrainAsync();
            Check(OverlayScrollChrome.GetEnabled(scene.Scroll) && scene.Scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
            OverlayScrollChrome.SetEnabled(scene.Scroll, false);
            Check(scene.Scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled);
            OverlayScrollChrome.SetEnabled(scene.Scroll, true); await DesktopTestUi.DrainAsync();
            Check(scene.Scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
                scene.Scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && Provider(scene.Scroll).VerticallyScrollable,
                "re-enabling does not replace content or change the other axis");
            scene.Content.Height = 20; await DesktopTestUi.DrainAsync();
            Check(!Provider(scene.Scroll).VerticallyScrollable, "no fabricated scroll range for short content");
        });
    }

    private static IScrollProvider Provider(ScrollViewer viewer) =>
        (IScrollProvider)(FrameworkElementAutomationPeer.CreatePeerForElement(viewer) ?? new ScrollViewerAutomationPeer(viewer))
            .GetPattern(PatternInterface.Scroll);
    private static TestCase Case(string name, Func<Task> body) => new("overlay-scroll: " + name, () => TestUiHost.InvokeAsync(body));
    private static void Check(bool condition, string message = "scroll assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Fixture : IDisposable
    {
        internal readonly Border Content = new() { Height = 2200, VerticalAlignment = VerticalAlignment.Top };
        internal readonly ScrollViewer Scroll;
        internal readonly Grid Root = new();
        private readonly DesktopTestUi.Host host;
        internal Fixture()
        {
            Scroll = new() { Content = Content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            OverlayScrollChrome.SetEnabled(Scroll, true); Root.Children.Add(Scroll);
            host = new(Root, 720, 360);
        }
        public void Dispose() => host.Dispose();
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    private static IEnumerable<TestCase> SeekHitAreaCases()
    {
        foreach (bool detail in new[] { false, true })
        {
            string host = detail ? "full player page" : "ordinary shell";
            yield return Case(host + " seek envelope uses native hit geometry and range automation across render scales", () => SeekHitAreaClicks(detail));
            yield return Case(host + " seek owner cancels on disable track replacement hide and unload", () => SeekHitAreaCancellation(detail));
            yield return Case(host + " seek envelope respects high contrast system motion and native focus", () => SeekHitAreaAccessibility(detail));
        }
    }

    private static async Task SeekHitAreaClicks(bool detail)
    {
        var playback = new SeekHitPlayback();
        await using var f = await Fixture.Create(output: playback);
        await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
        await using var host = new SeekHitHost(f); await host.Load(detail);
        int requests = host.SurfaceRequests, opens = playback.Opens.Count;
        foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
        {
            host.SetRenderScale(scale); await Drain();
            var bar = host.Bar; host.SetHover(false);
            True(host.HitsPage(new Point(bar.ActualWidth / 2, -20)), $"{scale}x inactive upper page remains reachable");
            foreach (var sample in new[]
            {
                (Name: "expanded rail", Fraction: .25, Y: -2d),
                (Name: "upper playhead", Fraction: .5, Y: -6d),
                (Name: "shade middle", Fraction: .75, Y: -23d),
                (Name: "shade upper edge", Fraction: .5, Y: -42.75),
                (Name: "shade minimum", Fraction: 0d, Y: -20d),
                (Name: "shade maximum", Fraction: 1d, Y: -20d)
            })
            {
                host.SetHover(false); playback.Tick(90); await Drain(); Equal(90d, bar.Value);
                host.SetHover(true);
                var point = new Point(bar.ActualWidth * sample.Fraction, sample.Y);
                True(host.HitsBar(point), $"{scale}x {sample.Name} is hittable on the first hover frame");
                True(bar.PointerWithin && !bar.IsScrubbing, "hover does not begin a scrub");
                int starts = host.Starts, commits = host.Commits, seeks = playback.Seeks.Count;
                host.Range.SetValue(bar.ValueAt(point.X)); await Until(() => !f.Module.Busy);
                Equal(starts + 1, host.Starts); Equal(commits + 1, host.Commits); Equal(seeks + 1, playback.Seeks.Count);
                True(Math.Abs(playback.Seeks[^1] - 180 * sample.Fraction) < .01, sample.Name + " uses x rather than the envelope's y");
                True(!bar.IsScrubbing, "native range automation completes the sole scrub owner");
                bar.EndScrub(true); await Drain();
                Equal(commits + 1, host.Commits); Equal(seeks + 1, playback.Seeks.Count);
                Equal(requests, host.SurfaceRequests); Equal(opens, playback.Opens.Count); host.AssertSurface(detail);
            }
            host.SetHover(false);
            True(!bar.PointerWithin && host.HitsPage(new Point(bar.ActualWidth / 2, -20)), "exit restores upper-page hit geometry immediately, including during fade");
        }
        host.SetRenderScale(1); await Drain(); host.SetHover(true);
        int before = playback.Seeks.Count, committed = host.Commits;
        host.Bar.BeginScrub(); host.Bar.UpdateScrub(host.Bar.ValueAt(host.Bar.ActualWidth + 24));
        True(host.Bar.IsScrubbing, "out-of-range motion keeps the current scrub transaction");
        host.Bar.EndScrub(true); await Until(() => !f.Module.Busy);
        Equal(before + 1, playback.Seeks.Count); Equal(committed + 1, host.Commits);
        Equal(180d, playback.Seeks[^1]); Equal(requests, host.SurfaceRequests);
    }

    private static async Task SeekHitAreaCancellation(bool detail)
    {
        var playback = new SeekHitPlayback();
        await using var f = await Fixture.Create(output: playback);
        await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
        await using var host = new SeekHitHost(f); await host.Load(detail);
        int requests = host.SurfaceRequests;
        foreach (string reason in new[] { "disable", "track", "hide", "unload" })
        {
            var bar = host.Bar; host.SetHover(true);
            var point = new Point(bar.ActualWidth * .75, -20);
            True(host.HitsBar(point), reason + " starts inside the actual shade envelope");
            int seeks = playback.Seeks.Count, commits = host.Commits, cancelled = host.Cancellations;
            bar.BeginScrub(); bar.UpdateScrub(bar.ValueAt(point.X)); True(bar.IsScrubbing, "owner began the scrub");
            switch (reason)
            {
                case "disable":
                    bar.IsEnabled = false; True(host.HitsPage(point), "disabled control does not intercept the upper page"); bar.IsEnabled = true;
                    break;
                case "track":
                    string identity = f.Module.PlaybackPresentationKey;
                    await f.Module.StepAsync(true); await Until(() => !f.Module.Busy && f.Module.PlaybackPresentationKey != identity);
                    break;
                case "hide":
                    host.Footer.Visibility = Visibility.Collapsed; await Drain();
                    True(host.HitsPage(point), "hidden footer does not intercept the page");
                    host.Footer.Visibility = Visibility.Visible; host.Layout(); await Drain();
                    break;
                case "unload":
                    DependencyObject? node = VisualTreeHelper.GetParent(host.Footer);
                    while (node is not ContentControl) node = VisualTreeHelper.GetParent(node!);
                    var parent = (ContentControl)node;
                    parent.Content = null; await Drain(); True(!bar.IsLoaded, "real unloading detaches the seek control");
                    parent.Content = host.Footer; host.Layout(); await Drain();
                    break;
            }
            True(!bar.IsScrubbing, reason + " retires the scrub"); Equal(cancelled + 1, host.Cancellations);
            bar.EndScrub(true); await Drain(); Equal(commits, host.Commits); Equal(seeks, playback.Seeks.Count);
            Equal(requests, host.SurfaceRequests); host.AssertSurface(detail);
        }
    }

    private static async Task SeekHitAreaAccessibility(bool detail)
    {
        var playback = new SeekHitPlayback();
        await using var f = await Fixture.Create(output: playback);
        await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
        await using var host = new SeekHitHost(f); await host.Load(detail);
        int requests = host.SurfaceRequests;
        foreach (bool highContrast in new[] { true, false })
        {
            DesktopTheme.SetIsHighContrast(host.Root, highContrast); host.Layout(); await Drain();
            var bar = host.Bar; host.SetHover(false); bar.Focus(FocusState.Programmatic); await Drain();
            True(host.HitsPage(new Point(bar.ActualWidth / 2, -20)), "native keyboard focus alone leaves the upper page available");
            playback.Tick(90); await Drain(); host.SetHover(true);
            True(host.HitsBar(new Point(bar.ActualWidth / 4, -2)) && host.HitsBar(new Point(bar.ActualWidth / 2, -5.75)), "accessible active rail and head are not clipped");
            var shade = new Point(bar.ActualWidth * .75, -42.75);
            True(highContrast ? host.HitsPage(shade) : host.HitsBar(shade), "high contrast keeps its shade-free appearance and ordinary mode retains the full shade");
            int seeks = playback.Seeks.Count, commits = host.Commits;
            host.Range.SetValue(highContrast ? 90 : 135); await Until(() => !f.Module.Busy);
            Equal(seeks + 1, playback.Seeks.Count); Equal(commits + 1, host.Commits); Equal(highContrast ? 90d : 135d, playback.Seeks[^1]);
            if (highContrast || !new UISettings().AnimationsEnabled)
            {
                var field = typeof(SeekBar).GetField("hoverAnimation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                True(field.GetValue(bar) is null, "high contrast and the actual reduced-motion preference create no hover clock");
            }
            host.SetHover(false); True(!bar.PointerWithin && host.HitsPage(shade), "exit releases the upper page while native focus remains");
            Equal(requests, host.SurfaceRequests); host.AssertSurface(detail);
        }
    }

    private sealed class SeekHitPlayback : PlaybackProbe, IMusicPlayback
    {
        internal List<double> Seeks { get; } = [];
        Task IMusicPlayback.SeekAsync(double seconds) { Seeks.Add(seconds); return base.SeekAsync(seconds); }
    }

    // Geometry, native range automation and the existing scrub/hover owners are
    // separate evidence. No pointer/key event or OS DPI is fabricated.
    private sealed class SeekHitHost : IAsyncDisposable
    {
        private readonly Fixture fixture;
        private readonly ShellWindow window;
        internal FrameworkElement Root => DesktopTestUi.WindowRoot(window);
        internal MusicPlayerView Footer { get; private set; } = null!;
        internal SeekBar Bar { get; private set; } = null!;
        internal IRangeValueProvider Range => (IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Bar)!.GetPattern(PatternInterface.RangeValue);
        internal int SurfaceRequests, Starts, Commits, Cancellations;
        internal SeekHitHost(Fixture fixture)
        {
            this.fixture = fixture;
            window = new ShellWindow([fixture.Module], () => Task.CompletedTask);
            window.PrepareForRender(); window.Resize(1110, 800); window.Activate();
            fixture.Module.GlobalSurfaceRequested += OnSurfaceRequest;
        }
        private void OnSurfaceRequest(object? sender, DesktopGlobalSurfaceRequestEventArgs e) { if (e.Surface == DesktopGlobalSurface.PlayerDetail) SurfaceRequests++; }
        internal async Task Load(bool detail)
        {
            await TestUiHost.LoadedAsync(Root); Layout(); await Drain();
            var ordinary = (MusicPlayerView)fixture.Module.PlayerView;
            if (detail)
            {
                await fixture.Module.OpenPlayerDetailAsync();
                var page = (MusicPlayerDetailView)fixture.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                await Until(() => DesktopVisual.IsVisible(page));
                Footer = (MusicPlayerView)((ContentControl)page.FindName("DetailPlayerHost")).Content;
                True(!ReferenceEquals(Footer, ordinary) && ReferenceEquals(Footer.DataContext, ordinary.DataContext), "detail has its own footer visual and the sole shared playback owner");
            }
            else Footer = ordinary;
            Layout(); await Drain(); Bar = (SeekBar)Footer.FindName("Progress");
            True(DesktopVisual.IsVisible(Bar) && Bar.IsEnabled && window.IsActive && ReferenceEquals(DesktopWindow.GetWindow(Footer), window), "the real footer belongs to the active Shell");
            Bar.ScrubStarted += (_, _) => Starts++; Bar.ScrubCommitted += (_, _) => Commits++; Bar.ScrubCancelled += (_, _) => Cancellations++;
            AssertSurface(detail);
        }
        internal void Layout() => Root.UpdateLayout();
        internal void SetRenderScale(double scale)
        {
            Root.Width = 1110; Root.Height = 800; Root.HorizontalAlignment = HorizontalAlignment.Left; Root.VerticalAlignment = VerticalAlignment.Top;
            Root.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale }; window.Resize(1110 * scale, 800 * scale); Layout();
        }
        internal void SetHover(bool value) => Bar.SetPointerWithin(value);
        internal void AssertSurface(bool detail) => Equal(detail ? Visibility.Visible : Visibility.Collapsed, ((Grid)DesktopTestUi.WindowRoot(window).FindName("PlayerDetailSurface")).Visibility);
        private DependencyObject? Hit(Point point)
        {
            point.Y += Bar.RailOrigin;
            return VisualTreeHelper.FindElementsInHostCoordinates(Bar.TransformToVisual(null).TransformPoint(point), Root).FirstOrDefault();
        }
        internal bool HitsBar(Point point) => Hit(point) is { } hit && DesktopVisual.Contains(Bar, hit);
        internal bool HitsPage(Point point) => Hit(point) is { } hit && !DesktopVisual.Contains(Bar, hit);
        public async ValueTask DisposeAsync()
        {
            fixture.Module.GlobalSurfaceRequested -= OnSurfaceRequest; Bar?.CancelScrub(); window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

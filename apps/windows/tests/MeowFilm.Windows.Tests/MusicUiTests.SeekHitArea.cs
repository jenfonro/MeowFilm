using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    private static IEnumerable<TestCase> SeekHitAreaCases()
    {
        foreach (bool detail in new[] { false, true })
        {
            string host = detail ? "full player page" : "ordinary shell";
            yield return Case(host + " seek union keeps hover and commits fresh x clicks across DPI",
                () => SeekHitAreaClicks(detail));
            yield return Case(host + " seek union cancels on disable track replacement hide and unload",
                () => SeekHitAreaCancellation(detail));
            yield return Case(host + " seek union preserves high contrast reduced motion and keyboard page input",
                () => SeekHitAreaAccessibility(detail));
        }
    }

    private static async Task SeekHitAreaClicks(bool detail)
    {
        var playback = new SeekHitPlayback();
        await using var f = await Fixture.Create(output: playback);
        await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
        await using var host = new SeekHitHost(f);
        await host.Load(detail);
        int requests = host.SurfaceRequests, opens = playback.Opens.Count;
        foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
        {
            host.SetDpi(scale);
            var bar = host.Bar;
            var outside = new Point(bar.ActualWidth / 2, -48);
            host.Move(outside);
            True(host.HitsPage(new Point(bar.ActualWidth / 2, -20)),
                $"{scale}x inactive upper page remains reachable");
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
                host.Move(outside);
                playback.Tick(90); await Drain();
                Equal(90d, bar.Value);
                host.Move(new Point(bar.ActualWidth / 2, 6));
                // No animation wait: the whole envelope must work on the first hover frame.
                var point = new Point(bar.ActualWidth * sample.Fraction, sample.Y);
                True(ReferenceEquals(host.Hit(point), bar),
                    $"{scale}x {sample.Name} participates in the complete host hit traversal");
                host.Move(point);
                True(bar.IsMouseOver && !bar.IsScrubbing, sample.Name + " keeps hover without beginning a gesture");
                int starts = host.Starts, commits = host.Commits, seeks = playback.Seeks.Count;
                True(host.Button(point, Mouse.PreviewMouseDownEvent), "coordinate-selected down is handled by seek");
                True(bar.IsScrubbing && bar.IsMouseCaptured, "fresh upper-area click owns the existing pointer transaction");
                Equal(starts + 1, host.Starts); Equal(seeks, playback.Seeks.Count);
                True(host.Button(point, Mouse.PreviewMouseUpEvent), "seek handles the matching release");
                await Until(() => !f.Module.Busy);
                Equal(commits + 1, host.Commits); Equal(seeks + 1, playback.Seeks.Count);
                True(Math.Abs(playback.Seeks[^1] - 180 * sample.Fraction) <= 180 / bar.ActualWidth + .01,
                    $"{sample.Name} seeks by x only: {playback.Seeks[^1]}");
                True(!bar.IsScrubbing && !bar.IsMouseCaptured, "commit releases the sole scrub and capture owner");
                host.Button(new Point(bar.ActualWidth / 2, 6), Mouse.PreviewMouseUpEvent);
                await Drain();
                Equal(commits + 1, host.Commits); Equal(seeks + 1, playback.Seeks.Count);
                Equal(requests, host.SurfaceRequests); Equal(opens, playback.Opens.Count);
                host.AssertSurface(detail);
            }
            host.Move(outside);
            True(!bar.IsMouseOver && host.HitsPage(new Point(bar.ActualWidth / 2, -20)),
                "leaving the union restores page input even while the highlight fades or focus remains");
        }
        host.SetDpi(1);
        var dragStart = new Point(host.Bar.ActualWidth / 2, -20);
        host.Move(new Point(host.Bar.ActualWidth / 2, 6)); host.Move(dragStart);
        int beforeDrag = playback.Seeks.Count, beforeCommit = host.Commits;
        host.Button(dragStart, Mouse.PreviewMouseDownEvent);
        var dragEnd = new Point(host.Bar.ActualWidth + 24, -60);
        host.Move(dragEnd);
        True(host.Bar.IsScrubbing && host.Bar.IsMouseCaptured, "dragging outside the union keeps the captured transaction");
        host.Button(dragEnd, Mouse.PreviewMouseUpEvent); await Until(() => !f.Module.Busy);
        Equal(beforeDrag + 1, playback.Seeks.Count); Equal(beforeCommit + 1, host.Commits);
        Equal(180d, playback.Seeks[^1]); Equal(requests, host.SurfaceRequests);
    }

    private static async Task SeekHitAreaCancellation(bool detail)
    {
        var playback = new SeekHitPlayback();
        await using var f = await Fixture.Create(output: playback);
        await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
        await using var host = new SeekHitHost(f);
        await host.Load(detail);
        int requests = host.SurfaceRequests;
        foreach (string reason in new[] { "disable", "track", "hide", "unload" })
        {
            var bar = host.Bar;
            host.Move(new Point(bar.ActualWidth / 2, -48));
            host.Move(new Point(bar.ActualWidth / 2, 6));
            var point = new Point(bar.ActualWidth * .75, -20);
            host.Move(point);
            int seeks = playback.Seeks.Count, commits = host.Commits, cancellations = host.Cancellations;
            host.Button(point, Mouse.PreviewMouseDownEvent);
            True(bar.IsScrubbing && bar.IsMouseCaptured, reason + " begins through the shade hit");
            switch (reason)
            {
                case "disable":
                    bar.SetCurrentValue(UIElement.IsEnabledProperty, false);
                    True(host.HitsPage(point), "disabled union does not intercept page input");
                    bar.SetCurrentValue(UIElement.IsEnabledProperty, true);
                    break;
                case "track":
                    string identity = f.Module.PlaybackPresentationKey;
                    await f.Module.StepAsync(true);
                    await Until(() => !f.Module.Busy && f.Module.PlaybackPresentationKey != identity);
                    break;
                case "hide":
                    host.Footer.Visibility = Visibility.Hidden;
                    True(host.HitsPage(point), "hidden union does not intercept page input");
                    host.Footer.Visibility = Visibility.Visible;
                    host.Layout(); await Drain();
                    break;
                case "unload":
                    host.Source.RootVisual = null; await Drain();
                    True(!bar.IsLoaded && !bar.HasAnimatedProperties, "unload releases all seek presentation clocks");
                    host.Source.RootVisual = host.Root;
                    host.Layout(); await Drain();
                    break;
            }
            True(!bar.IsScrubbing && !bar.IsMouseCaptured, reason + " cancels and releases capture");
            Equal(cancellations + 1, host.Cancellations);
            // Deliver the old Up to the re-enabled/current/remounted bar, not just to empty page space.
            host.Button(new Point(bar.ActualWidth * .75, 6), Mouse.PreviewMouseUpEvent); await Drain();
            Equal(commits, host.Commits); Equal(seeks, playback.Seeks.Count);
            Equal(requests, host.SurfaceRequests); host.AssertSurface(detail);
        }
    }

    private static async Task SeekHitAreaAccessibility(bool detail)
    {
        var playback = new SeekHitPlayback();
        await using var f = await Fixture.Create(output: playback);
        await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
        await using var host = new SeekHitHost(f);
        await host.Load(detail);
        int requests = host.SurfaceRequests;
        foreach (bool highContrast in new[] { true, false })
        {
            // WPF's cached preference only; never changes Windows or another desktop's settings.
            using var motion = new SeekHitReducedMotion();
            DesktopTheme.SetIsHighContrast(host.Root, highContrast);
            host.Layout(); await Drain();
            var bar = host.Bar;
            host.Move(new Point(bar.ActualWidth / 2, -48));
            SimulateFocus(bar, UIElement.IsKeyboardFocusWithinProperty, true);
            True(host.HitsPage(new Point(bar.ActualWidth / 2, -20)),
                "keyboard focus alone leaves the upper page available");
            SimulateFocus(bar, UIElement.IsKeyboardFocusWithinProperty, false);
            playback.Tick(90); await Drain();
            host.Move(new Point(bar.ActualWidth / 2, 6));
            var rail = new Point(bar.ActualWidth / 4, -2);
            var head = new Point(bar.ActualWidth / 2, -5.75);
            True(ReferenceEquals(host.Hit(rail), bar) && ReferenceEquals(host.Hit(head), bar),
                "high contrast and reduced motion include the entire expanded rail and head");
            host.Move(rail); host.Move(head);
            True(bar.IsMouseOver && !bar.HasAnimatedProperties,
                "pointer keeps the expanded surface active without motion clocks");
            var shade = new Point(bar.ActualWidth * .75, -42.75);
            True(highContrast ? host.HitsPage(shade) : ReferenceEquals(host.Hit(shade), bar),
                "the 40 DIP shade is interactive when painted; high contrast keeps its shade-free appearance");
            var target = highContrast ? head : shade;
            host.Move(target);
            int seeks = playback.Seeks.Count, commits = host.Commits;
            host.Button(target, Mouse.PreviewMouseDownEvent);
            True(bar.IsScrubbing, "accessible upper surface uses the same pointer transaction");
            host.Button(target, Mouse.PreviewMouseUpEvent); await Until(() => !f.Module.Busy);
            Equal(seeks + 1, playback.Seeks.Count); Equal(commits + 1, host.Commits);
            True(Math.Abs(playback.Seeks[^1] - (highContrast ? 90d : 135d)) <= 180 / bar.ActualWidth + .01,
                $"accessible upper surface seeks by x at native client-pixel precision: {playback.Seeks[^1]}");
            host.Move(new Point(bar.ActualWidth / 2, -48));
            True(!bar.IsMouseOver && host.HitsPage(shade), "exit releases the upper page immediately");
            Equal(requests, host.SurfaceRequests); host.AssertSurface(detail);
        }
    }

    private sealed class SeekHitPlayback : PlaybackProbe, IMusicPlayback
    {
        internal List<double> Seeks { get; } = [];
        Task IMusicPlayback.SeekAsync(double seconds)
        { Seeks.Add(seconds); return base.SeekAsync(seconds); }
    }

    private sealed class SeekHitReducedMotion : IDisposable
    {
        private readonly FieldInfo preference = typeof(SystemParameters).GetField("_clientAreaAnimation",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly bool previous = SystemParameters.ClientAreaAnimation;
        internal SeekHitReducedMotion()
        {
            preference.SetValue(null, false);
            True(!SystemParameters.ClientAreaAnimation, "synthetic WPF reduced-motion preference is effective");
        }
        public void Dispose() => preference.SetValue(null, previous);
    }

    private sealed class SeekHitHost : IAsyncDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly Fixture fixture;
        private readonly ShellWindow window;
        private readonly DependencyPropertyKey activeKey;
        private readonly FieldInfo inputSource = typeof(MouseDevice).GetField("_inputSource", PrivateInstance)!;
        private readonly FieldInfo lastPosition = typeof(MouseDevice).GetField("_lastPosition", PrivateInstance)!;
        private readonly object? previousSource, previousPosition;
        private readonly object mouseProvider;
        private readonly FieldInfo providerActive;
        private readonly MethodInfo changeMouseOver = typeof(MouseDevice).GetMethod("ChangeMouseOver",
            PrivateInstance, null, [typeof(IInputElement), typeof(int)], null)!;
        internal Border Root { get; }
        internal HwndSource Source { get; }
        internal MusicPlayerView Footer { get; private set; } = null!;
        internal SeekBar Bar { get; private set; } = null!;
        internal int SurfaceRequests, Starts, Commits, Cancellations;
        internal SeekHitHost(Fixture fixture)
        {
            // Capture belongs only to this service session's hidden HWND. Never run this
            // fixture on the interactive desktop, call Show or inject native mouse input.
            True(Process.GetCurrentProcess().SessionId == 0, "seek pointer fixture requires noninteractive Session 0");
            this.fixture = fixture;
            previousSource = inputSource.GetValue(Mouse.PrimaryDevice);
            previousPosition = lastPosition.GetValue(Mouse.PrimaryDevice);
            window = new ShellWindow([fixture.Module], () => Task.CompletedTask);
            window.PrepareForRender();
            Root = (Border)window.Content;
            DesktopTheme.SetIsHighContrast(Root, false);
            activeKey = typeof(Window).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(field => field.FieldType == typeof(DependencyPropertyKey))
                .Select(field => (DependencyPropertyKey)field.GetValue(null)!)
                .Single(key => key.DependencyProperty == Window.IsActiveProperty);
            window.SetValue(activeKey, true);
            Source = new HwndSource(new HwndSourceParameters("seek-hit-complete-shell-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 800
            });
            // Retain the unshown logical Window, so the actual blank-surface input
            // owner participates as well as the complete Shell visual/clip hierarchy.
            Source.RootVisual = Root;
            mouseProvider = typeof(HwndSource).GetMethod("GetInputProvider", PrivateInstance,
                null, [typeof(Type)], null)!.Invoke(Source, [typeof(MouseDevice)])!;
            providerActive = mouseProvider.GetType().GetField("_active", PrivateInstance)!;
            fixture.Module.GlobalSurfaceRequested += OnSurfaceRequest;
        }
        private void OnSurfaceRequest(object? sender, DesktopGlobalSurfaceRequestEventArgs e)
        { if (e.Surface == DesktopGlobalSurface.PlayerDetail) SurfaceRequests++; }
        internal async Task Load(bool detail)
        {
            Layout(); await Drain();
            var ordinary = (MusicPlayerView)fixture.Module.PlayerView;
            if (detail)
            {
                await fixture.Module.ShowLyricsAsync();
                await Until(() => ((Grid)window.FindName("PlayerDetailSurface")).Visibility == Visibility.Visible);
                var page = (MusicPlayerDetailView)((ContentControl)window.FindName("PlayerDetailHost")).Content;
                Footer = (MusicPlayerView)((ContentControl)page.FindName("DetailPlayerHost")).Content;
                True(!ReferenceEquals(Footer, ordinary) && ReferenceEquals(Footer.DataContext, ordinary.DataContext),
                    "the complete player page uses its own footer and the same playback owner");
            }
            else Footer = ordinary;
            Layout(); await Drain();
            Bar = (SeekBar)Footer.FindName("Progress");
            True(Bar.IsLoaded && Bar.IsVisible && Bar.IsEnabled && window.IsActive &&
                ReferenceEquals(Window.GetWindow(Footer), window),
                "the real footer is loaded and retains its logical Shell owner");
            True(typeof(MusicPlayerView).GetField("surfaceInput", PrivateInstance)!.GetValue(Footer) is PlayerSurfaceInput,
                "the production blank-surface input owner is present");
            Bar.ScrubStarted += (_, _) => Starts++;
            Bar.ScrubCommitted += (_, _) => Commits++;
            Bar.ScrubCancelled += (_, _) => Cancellations++;
            AssertSurface(detail);
        }
        internal void Layout() => Render(Root, 1110, 800);
        internal void SetDpi(double scale)
        {
            VisualTreeHelper.SetRootDpi(Root, new DpiScale(scale, scale));
            Layout();
            Equal(scale, VisualTreeHelper.GetDpi(Bar).DpiScaleX);
        }
        internal void AssertSurface(bool detail) =>
            Equal(detail ? Visibility.Visible : Visibility.Collapsed,
                ((Grid)window.FindName("PlayerDetailSurface")).Visibility);
        internal IInputElement? Hit(Point point) => Root.InputHitTest(Bar.TranslatePoint(point, Root));
        internal bool HitsPage(Point point) => Hit(point) is DependencyObject hit &&
            !ReferenceEquals(hit, Bar) && !Bar.IsAncestorOf(hit);
        private void At(Point rootPoint, Action action)
        {
            var mouse = Mouse.PrimaryDevice;
            // Model an active synthetic input stream on both WPF owners. An inactive
            // HWND provider otherwise reads the inaccessible Session 0 system cursor
            // after taking native capture. CaptureMouse still verifies Set/GetCapture.
            providerActive.SetValue(mouseProvider, true);
            inputSource.SetValue(mouse, Source);
            var client = Source.CompositionTarget.TransformToDevice.Transform(rootPoint);
            client = new Point(Math.Round(client.X), Math.Round(client.Y));
            lastPosition.SetValue(mouse, client);
            var expected = Source.CompositionTarget.TransformFromDevice.Transform(client);
            True((mouse.GetPosition(Root) - expected).Length < .01,
                "synthetic pointer has the requested coordinates at native client-pixel precision");
            // Keep source and coordinates until teardown, including asynchronous WPF
            // capture revalidation between this gesture's Down and Up.
            action();
        }
        internal void Move(Point point)
        {
            var rootPoint = Bar.TranslatePoint(point, Root);
            // Hit testing determines enter/leave, never a requested/expected control.
            var hit = Root.InputHitTest(rootPoint);
            At(rootPoint, () =>
            {
                changeMouseOver.Invoke(Mouse.PrimaryDevice, [hit, Environment.TickCount]);
                var move = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.PreviewMouseMoveEvent, Source = Mouse.Captured ?? hit ?? Root };
                InputManager.Current.ProcessInput(move);
            });
        }
        internal bool Button(Point point, RoutedEvent routed)
        {
            var rootPoint = Bar.TranslatePoint(point, Root);
            var target = Mouse.Captured ?? Root.InputHitTest(rootPoint) ?? Root;
            var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = routed, Source = target };
            var promoted = routed == Mouse.PreviewMouseDownEvent ? Mouse.MouseDownEvent : Mouse.MouseUpEvent;
            var stagingKey = new object();
            void TargetPromotion(object sender, PreProcessInputEventArgs e)
            {
                if (ReferenceEquals(e.StagingItem.Input, input)) e.StagingItem.SetData(stagingKey, input);
                else if (ReferenceEquals(e.StagingItem.GetData(stagingKey), input) &&
                    e.StagingItem.Input is MouseButtonEventArgs mouse && mouse.RoutedEvent == promoted)
                    mouse.Source = target;
            }
            var manager = InputManager.Current;
            manager.PreProcessInput += TargetPromotion;
            try { At(rootPoint, () => manager.ProcessInput(input)); }
            finally { manager.PreProcessInput -= TargetPromotion; }
            return input.Handled;
        }
        public async ValueTask DisposeAsync()
        {
            fixture.Module.GlobalSurfaceRequested -= OnSurfaceRequest;
            try
            {
                At(new Point(), () =>
                {
                    if (Bar is not null) Bar.CancelScrub();
                    changeMouseOver.Invoke(Mouse.PrimaryDevice, [null, Environment.TickCount]);
                });
                Source.RootVisual = null; await Drain();
                window.SetValue(activeKey, false);
                window.Content = null; window.Close();
                await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                try { Source.Dispose(); }
                finally
                {
                    inputSource.SetValue(Mouse.PrimaryDevice, previousSource);
                    lastPosition.SetValue(Mouse.PrimaryDevice, previousPosition);
                }
            }
        }
    }
}

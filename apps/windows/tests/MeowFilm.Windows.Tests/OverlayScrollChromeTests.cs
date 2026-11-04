using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class OverlayScrollChromeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("hover, focus and ordinary keys do not reveal an idle scrollbar", async () =>
        {
            using var f = new Fixture(); await DrainAsync();
            SetState(f.Scroll, UIElement.IsMouseOverProperty, true);
            SetState(f.Scroll, UIElement.IsKeyboardFocusWithinProperty, true);
            SetState(f.Bar, UIElement.IsMouseOverProperty, true);
            Equal(0d, f.Bar.Opacity, "page and gutter hover stay hidden");
            f.Key(Key.Tab);
            f.Scroll.ScrollToVerticalOffset(120);
            await DrainAsync();
            True(f.Scroll.VerticalOffset > 0, "focus restoration changes the actual WPF offset");
            False(OverlayScrollChrome.GetActive(f.Scroll), "ordinary focus has no scroll intent");
            Equal(0d, f.Bar.Opacity, "ordinary focus does not reveal chrome");
            False(f.Bar.HasAnimatedProperties, "idle chrome owns no animation clock");
        });
        yield return Case("wheel, handled forwarding, scrolling keys and track commands activate without relayout", async () =>
        {
            using var f = new Fixture(); await DrainAsync();
            var width = f.Content.ActualWidth;
            f.Wheel(f.Scroll, -120, native: true); await DrainAsync();
            if (SystemParameters.WheelScrollLines == 0)
                False(OverlayScrollChrome.GetActive(f.Scroll), "disabled system wheel scrolling does not activate chrome");
            else
            {
                True(f.Scroll.VerticalOffset > 0, "default WPF wheel scrolls");
                True(OverlayScrollChrome.GetActive(f.Scroll) && f.Bar.Opacity > 0, "offset change reveals chrome");
            }
            f.Rest(); f.Scroll.ScrollToTop(); await DrainAsync();
            var offset = f.Scroll.VerticalOffset;
            MouseWheelEventHandler forward = (_, e) =>
            {
                f.Scroll.LineDown();
                e.Handled = true;
            };
            f.Root.PreviewMouseWheel += forward;
            f.Wheel(f.Scroll, -120); await DrainAsync();
            f.Root.PreviewMouseWheel -= forward;
            True(f.Scroll.VerticalOffset > offset, "ancestor-handled forwarding still reaches the shared behavior");
            True(OverlayScrollChrome.GetActive(f.Scroll), "handled forwarding activates idle chrome");
            offset = f.Scroll.VerticalOffset;
            f.Rest();
            f.Key(Key.PageDown); await DrainAsync();
            True(f.Scroll.VerticalOffset > offset, "WPF scrolling key changes the offset");
            True(OverlayScrollChrome.GetActive(f.Scroll), "scrolling keys activate idle chrome");
            offset = f.Scroll.VerticalOffset;
            f.Rest();
            ((RoutedCommand)f.Track.IncreaseRepeatButton.Command).Execute(null, f.Track.IncreaseRepeatButton);
            await DrainAsync();
            True(f.Scroll.VerticalOffset > offset, "WPF scrollbar page command works");
            True(OverlayScrollChrome.GetActive(f.Scroll), "scrollbar commands grant scroll intent");
            Equal(width, f.Content.ActualWidth, "overlay visibility never reserves content width");
            True(f.Thumb.ActualHeight >= 28, "thumb remains draggable");
        });
        yield return Case("idle fades under hover and focus, and renewed scrolling replaces the old fade", async () =>
        {
            using var f = new Fixture(); await DrainAsync();
            SetState(f.Scroll, UIElement.IsMouseOverProperty, true);
            SetState(f.Scroll, UIElement.IsKeyboardFocusWithinProperty, true);
            SetState(f.Bar, UIElement.IsMouseOverProperty, true);
            f.Wheel(f.Scroll, -120); await DrainAsync();
            Equal(1d, f.Bar.Opacity, "active scrollbar hover emphasizes the thumb");
            await UntilAsync(() => !OverlayScrollChrome.GetActive(f.Scroll), "first idle deadline");
            if (SystemParameters.ClientAreaAnimation)
            {
                True(f.Bar.HasAnimatedProperties, "idle begins a real WPF fade");
                await UntilAsync(() => f.Bar.Opacity is > 0 and < 1, "intermediate fade opacity");
            }
            else Equal(0d, f.Bar.Opacity, "reduced motion hides without a fade");
            f.Wheel(f.Scroll, -120); await DrainAsync();
            True(OverlayScrollChrome.GetActive(f.Scroll), "new scrolling reactivates chrome");
            Equal(1d, f.Bar.Opacity, "new scrolling cancels the old fade");
            False(f.Bar.HasAnimatedProperties, "old animation clock is removed");
            await Task.Delay(550); await DrainAsync();
            True(OverlayScrollChrome.GetActive(f.Scroll), "old fade completion cannot retire the renewed idle interval");
            await UntilAsync(() => !OverlayScrollChrome.GetActive(f.Scroll) && f.Bar.Opacity == 0 &&
                !f.Bar.HasAnimatedProperties, "renewed idle fade completes");
            Equal(0d, f.Bar.Opacity, "hover and focus cannot hold idle chrome visible");
        });
        yield return Case("templated control input reaches its viewer without granting intent to an enclosing viewer", async () =>
        {
            using var f = new Fixture();
            var list = new ListBox
            {
                Height = 180, VerticalAlignment = VerticalAlignment.Top,
                ItemsSource = Enumerable.Repeat("item", 60).ToArray()
            };
            list.Resources.Add(typeof(ScrollViewer), f.Scroll.Style);
            f.Content.Child = list; f.Layout(); await DrainAsync();
            var inner = Visuals(list).OfType<ScrollViewer>().Single();
            True(ReferenceEquals(inner.TemplatedParent, list) && OverlayScrollChrome.GetEnabled(inner),
                "real templated control owns the shared viewer");
            KeyEventHandler forward = (_, e) =>
            {
                if (e.Key != Key.End) return;
                inner.ScrollToBottom();
                e.Handled = true;
            };
            list.PreviewKeyDown += forward;
            f.Key(Key.End, list); await DrainAsync();
            list.PreviewKeyDown -= forward;
            True(inner.VerticalOffset > 0 && OverlayScrollChrome.GetActive(inner),
                "keyboard input targeting the template owner activates its actual scroll");
            False(OverlayScrollChrome.GetActive(f.Scroll), "enclosing viewer does not inherit another viewer's activity");
            OverlayScrollChrome.SetEnabled(inner, false); OverlayScrollChrome.SetEnabled(inner, true);
            inner.ScrollToTop(); await DrainAsync();
            Equal(0d, ((ScrollBar)inner.Template.FindName("PART_VerticalScrollBar", inner)).Opacity,
                "programmatic reset after reenabling stays hidden");
            MouseWheelEventHandler wheel = (_, e) =>
            {
                inner.ScrollToBottom();
                e.Handled = true;
            };
            list.PreviewMouseWheel += wheel;
            f.Wheel(list, -120); await DrainAsync();
            list.PreviewMouseWheel -= wheel;
            True(OverlayScrollChrome.GetActive(inner), "handled wheel targeting the template owner grants intent");
            False(OverlayScrollChrome.GetActive(f.Scroll), "forwarded wheel does not reveal the enclosing chrome");
        });
        yield return Case("no-op input, programmatic offsets and content or viewport coercion stay hidden", async () =>
        {
            using var f = new Fixture(); await DrainAsync();
            f.Wheel(f.Scroll, 120); f.Key(Key.Home); await DrainAsync();
            Equal(0d, f.Scroll.VerticalOffset, "upward inputs at the boundary do not scroll");
            False(OverlayScrollChrome.GetActive(f.Scroll), "no-op input is not activity");
            f.Scroll.ScrollToBottom(); await DrainAsync();
            Equal(0d, f.Bar.Opacity, "programmatic restoration stays hidden after input intent expires");
            var offset = f.Scroll.VerticalOffset;
            f.Wheel(f.Scroll, -120);
            f.Content.Height = 420;
            f.Layout(); await DrainAsync();
            True(f.Scroll.VerticalOffset < offset, "shrinking content coerces the actual offset");
            False(OverlayScrollChrome.GetActive(f.Scroll), "content changes do not reuse a no-op wheel intent");
            f.Content.Height = 1400;
            f.Scroll.Height = 140;
            f.Layout(); f.Scroll.ScrollToBottom(); await DrainAsync();
            offset = f.Scroll.VerticalOffset;
            f.Wheel(f.Scroll, -120);
            f.Scroll.Height = 240;
            f.Layout(); await DrainAsync();
            True(f.Scroll.VerticalOffset < offset, "growing viewport coerces the actual offset");
            False(OverlayScrollChrome.GetActive(f.Scroll), "viewport changes do not activate chrome");
            f.Scroll.ClearValue(FrameworkElement.HeightProperty);
            f.Layout(); f.Scroll.ScrollToTop(); await DrainAsync();
            f.Wheel(f.Scroll, 0); await DrainAsync();
            Equal(0d, f.Bar.Opacity, "zero-delta input remains hidden");
        });
        yield return Case("nested scrolling and unrelated drag event lifecycle cannot activate the enclosing viewer", async () =>
        {
            using var f = new Fixture();
            var inner = new ScrollViewer
            {
                Height = 140, VerticalAlignment = VerticalAlignment.Top,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new Border { Height = 600 },
                Style = f.Scroll.Style
            };
            var unrelated = new Thumb { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left };
            var panel = new Grid(); panel.Children.Add(inner); panel.Children.Add(unrelated);
            f.Content.Child = panel; f.Layout(); await DrainAsync();
            f.Wheel(inner, -120); await DrainAsync();
            True(inner.VerticalOffset > 0 && OverlayScrollChrome.GetActive(inner), "nested viewer owns its real scroll");
            Equal(0d, f.Scroll.VerticalOffset, "nested wheel is consumed by its owner");
            False(OverlayScrollChrome.GetActive(f.Scroll), "bubbled nested changes cannot activate the enclosing chrome");
            var captured = Mouse.Captured;
            NotifyCapture(unrelated, Mouse.GotMouseCaptureEvent);
            try
            {
                f.Scroll.ScrollToVerticalOffset(120); await DrainAsync();
                True(f.Scroll.VerticalOffset > 0, "enclosing offset really changes");
                False(OverlayScrollChrome.GetActive(f.Scroll), "another thumb does not grant scrollbar intent");
                Equal(0d, f.Bar.Opacity, "unrelated drag notification stays hidden");
            }
            finally { NotifyCapture(unrelated, Mouse.LostMouseCaptureEvent); }
            True(ReferenceEquals(captured, Mouse.Captured), "unrelated drag notifications leave OS capture unchanged");
        });
        yield return Case("owned drag event lifecycle survives idle and loss starts a fresh idle interval", async () =>
        {
            using var f = new Fixture(); await DrainAsync();
            f.Wheel(f.Scroll, -120); await DrainAsync();
            var captured = Mouse.Captured;
            NotifyCapture(f.Thumb, Mouse.GotMouseCaptureEvent);
            SetState(f.Bar, UIElement.IsMouseCaptureWithinProperty, true);
            await Task.Delay(1100); await DrainAsync();
            True(OverlayScrollChrome.GetActive(f.Scroll), "owned drag notification holds chrome beyond the idle deadline");
            Equal(1d, f.Bar.Opacity, "dragging stays fully visible");
            False(f.Bar.HasAnimatedProperties, "dragging does not leave a hide animation running");
            var offset = f.Scroll.VerticalOffset;
            f.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 30) { RoutedEvent = Thumb.DragDeltaEvent });
            await DrainAsync();
            True(f.Scroll.VerticalOffset > offset, "real scrollbar DragDelta continues to scroll");
            NotifyCapture(f.Thumb, Mouse.LostMouseCaptureEvent);
            SetState(f.Bar, UIElement.IsMouseCaptureWithinProperty, false);
            await DrainAsync();
            await Task.Delay(550); await DrainAsync();
            True(OverlayScrollChrome.GetActive(f.Scroll), "loss notification starts a fresh idle interval");
            await UntilAsync(() => !OverlayScrollChrome.GetActive(f.Scroll) && f.Bar.Opacity == 0 &&
                !f.Bar.HasAnimatedProperties, "ended drag event lifecycle fades out");
            True(ReferenceEquals(captured, Mouse.Captured), "owned drag notifications leave OS capture unchanged");
        });
        yield return Case("unload and disable retire input, timers and clocks, while remount restores only subscriptions", async () =>
        {
            using var f = new Fixture(); await DrainAsync();
            f.Wheel(f.Scroll, -120); await DrainAsync();
            f.Root.Children.Remove(f.Scroll); await DrainAsync();
            False(f.Scroll.IsLoaded, "actual WPF unmount dispatches Unloaded");
            False(OverlayScrollChrome.GetActive(f.Scroll), "unload retires activity");
            False(f.Bar.HasAnimatedProperties, "unload retains no animation");
            f.Wheel(f.Scroll, -120); await DrainAsync();
            False(OverlayScrollChrome.GetActive(f.Scroll), "unloaded input cannot restart timing");
            f.Root.Children.Add(f.Scroll); f.Layout(); await DrainAsync();
            True(f.Scroll.IsLoaded, "same viewer mounts again");
            Equal(0d, f.Bar.Opacity, "remount does not restore old activity");
            f.Wheel(f.Scroll, -120); await DrainAsync();
            True(OverlayScrollChrome.GetActive(f.Scroll), "remount restores input subscriptions");
            await UntilAsync(() => !OverlayScrollChrome.GetActive(f.Scroll), "fade before second unmount");
            if (SystemParameters.ClientAreaAnimation) True(f.Bar.HasAnimatedProperties, "fade clock exists before unmount");
            f.Root.Children.Remove(f.Scroll); await DrainAsync();
            False(f.Bar.HasAnimatedProperties, "unmount removes an in-flight fade clock");
            f.Root.Children.Add(f.Scroll); f.Layout(); await DrainAsync();
            f.Wheel(f.Scroll, -120); await DrainAsync();
            OverlayScrollChrome.SetEnabled(f.Scroll, false);
            False(OverlayScrollChrome.GetActive(f.Scroll), "disable retires the current interval");
            False(f.Bar.HasAnimatedProperties, "disable removes clocks");
            f.Wheel(f.Scroll, -120); await DrainAsync();
            False(OverlayScrollChrome.GetActive(f.Scroll), "disabled input cannot restart timing");
            OverlayScrollChrome.SetEnabled(f.Scroll, true);
            Equal(0d, f.Bar.Opacity, "reenabling starts at rest");
            f.Wheel(f.Scroll, -120); await DrainAsync();
            True(OverlayScrollChrome.GetActive(f.Scroll), "reenabling restores working behavior");
            await UntilAsync(() => !OverlayScrollChrome.GetActive(f.Scroll) && f.Bar.Opacity == 0 &&
                !f.Bar.HasAnimatedProperties, "reenabled behavior settles once");
        });
        yield return Case("owned drag event lifecycle retires on unmount and does not revive on remount", async () =>
        {
            using var f = new Fixture(); await DrainAsync();
            f.Wheel(f.Scroll, -120); await DrainAsync();
            var captured = Mouse.Captured;
            NotifyCapture(f.Thumb, Mouse.GotMouseCaptureEvent);
            True(OverlayScrollChrome.GetActive(f.Scroll), "owned drag event activates before unmount");
            f.Root.Children.Remove(f.Scroll); await DrainAsync();
            True(ReferenceEquals(captured, Mouse.Captured), "unmount does not change OS capture");
            False(OverlayScrollChrome.GetActive(f.Scroll), "unload retires the drag");
            False(f.Bar.HasAnimatedProperties, "unload owns no fade clock");
            f.Root.Children.Add(f.Scroll); f.Layout(); await DrainAsync();
            True(f.Scroll.IsLoaded, "same viewer really remounts");
            Equal(0d, f.Bar.Opacity, "old drag does not survive remount");
            NotifyCapture(f.Thumb, Mouse.LostMouseCaptureEvent);
            await Task.Delay(1100); await DrainAsync();
            False(OverlayScrollChrome.GetActive(f.Scroll), "late loss cannot revive retired activity or timer");
            False(f.Bar.HasAnimatedProperties, "remount retains no retired drag clock");
            f.Wheel(f.Scroll, -120); await DrainAsync();
            True(OverlayScrollChrome.GetActive(f.Scroll), "remount restores actual scrolling intent");
            await UntilAsync(() => !OverlayScrollChrome.GetActive(f.Scroll) && f.Bar.Opacity == 0 &&
                !f.Bar.HasAnimatedProperties, "fresh scrolling settles after drag retirement");
            True(ReferenceEquals(captured, Mouse.Captured), "remount and scrolling leave OS capture unchanged");
        });
        yield return Case("high contrast remains visible and immediately cancels an ordinary fade", async () =>
        {
            using var f = new Fixture(); await DrainAsync();
            f.Wheel(f.Scroll, -120); await DrainAsync();
            await UntilAsync(() => !OverlayScrollChrome.GetActive(f.Scroll), "ordinary idle before contrast change");
            DesktopTheme.SetIsHighContrast(f.Root, true); f.Layout();
            Equal(1d, f.Bar.Opacity, "high contrast overrides even an in-flight fade");
            False(f.Bar.HasAnimatedProperties, "contrast change removes the ordinary fade clock");
            True(Visuals(f.Thumb).OfType<Border>().Any(border => border.Background is SolidColorBrush brush &&
                brush.Color == SystemColors.WindowTextColor), "thumb uses a system contrast color");
            f.Wheel(f.Scroll, -120); await DrainAsync();
            await UntilAsync(() => !OverlayScrollChrome.GetActive(f.Scroll), "high contrast idle");
            Equal(1d, f.Bar.Opacity, "high contrast does not hide at idle");
            False(f.Bar.HasAnimatedProperties, "high contrast does not animate chrome");
            DesktopTheme.SetIsHighContrast(f.Root, false); f.Layout();
            Equal(0d, f.Bar.Opacity, "ordinary mode returns to rest without reviving the old fade");
        });
    }

    private static TestCase Case(string name, Func<Task> body) => new("overlay-scroll: " + name, () => OnStaAsync(body));
    private sealed class Fixture : IDisposable
    {
        public Grid Root { get; } = new() { Background = Brushes.White };
        public Border Content { get; } = new() { Height = 1400 };
        public ScrollViewer Scroll { get; }
        public ScrollBar Bar => (ScrollBar)Scroll.Template.FindName("PART_VerticalScrollBar", Scroll);
        public Track Track => (Track)Bar.Template.FindName("PART_Track", Bar);
        public Thumb Thumb => Track.Thumb;
        private readonly HwndSource source;
        public Fixture()
        {
            Root.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
            Root.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/MeowFilm.Desktop;component/OverlayScrollChrome.xaml", UriKind.Relative) });
            DesktopTheme.SetIsHighContrast(Root, false);
            Scroll = new ScrollViewer
            {
                Content = Content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Style = (Style)Root.FindResource("MeowFilm.OverlayScrollViewer")
            };
            Root.Children.Add(Scroll);
            // Nonactivating offscreen HWND drives real WPF loading, layout and clocks; input uses routed APIs.
            source = new HwndSource(new HwndSourceParameters("overlay-scroll-code-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 320, Height = 240
            });
            source.RootVisual = Root; Layout();
            True(OverlayScrollChrome.GetEnabled(Scroll), "shared overlay behavior is applied");
        }
        public void Layout()
        {
            var viewport = new Size(320, 240);
            Root.Measure(viewport); Root.Arrange(new Rect(viewport)); Root.UpdateLayout();
        }
        public void Rest()
        {
            OverlayScrollChrome.SetEnabled(Scroll, false);
            OverlayScrollChrome.SetEnabled(Scroll, true);
            False(OverlayScrollChrome.GetActive(Scroll), "input activation starts from rest");
        }
        public void Wheel(UIElement target, int delta, bool native = false)
        {
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            target.RaiseEvent(args);
            if (args.Handled) return;
            // Fixed forwarded distance isolates chrome timing from the user's wheel-line preference.
            if (!native && target is ScrollViewer viewer)
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset - delta / 120d * 48);
            else { args.RoutedEvent = Mouse.MouseWheelEvent; target.RaiseEvent(args); }
        }
        public void Key(Key key, UIElement? target = null)
        {
            target ??= Scroll;
            var args = new KeyEventArgs(new KeyboardProbe(), source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(args);
            if (!args.Handled) { args.RoutedEvent = Keyboard.KeyDownEvent; target.RaiseEvent(args); }
        }
        public void Dispose()
        {
            OverlayScrollChrome.SetEnabled(Scroll, false);
            source.RootVisual = null;
            source.Dispose();
        }
    }
    private sealed class KeyboardProbe() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }
    private static void NotifyCapture(Thumb thumb, RoutedEvent routedEvent)
    {
        var captured = Mouse.Captured;
        thumb.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent });
        True(ReferenceEquals(captured, Mouse.Captured), "capture adapter notification cannot change OS capture");
    }
    private static void SetState(UIElement element, DependencyProperty property, bool value)
    {
        // Tests real template triggers without moving the user's pointer or stealing keyboard focus.
        var field = typeof(UIElement).GetField(property.Name + "PropertyKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WPF input-state seam unavailable");
        element.SetValue((DependencyPropertyKey)field.GetValue(null)!, value);
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task DrainAsync()
    { for (var i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task UntilAsync(Func<bool> predicate, string label)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Timed out: " + label);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!predicate()) await Task.Delay(1);
        }
    }
    private static async Task OnStaAsync(Func<Task> body)
    {
        var result = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? owner = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Volatile.Write(ref owner, dispatcher);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var unhandled = new List<Exception>();
            dispatcher.UnhandledException += (_, e) => { unhandled.Add(e.Exception); e.Handled = true; };
            dispatcher.BeginInvoke(new Action(async () =>
            {
                Exception? failure = null;
                try
                {
                    await body(); await DrainAsync();
                    if (unhandled.Count > 0) throw new AggregateException("Unhandled WPF event", unhandled);
                }
                catch (Exception error) { failure = error; }
                finally { result.TrySetResult(failure); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            try { Dispatcher.Run(); }
            catch (Exception error) { result.TrySetResult(error); }
            finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "overlay-scroll-code-test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Exception? failure;
        try { failure = await result.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { Volatile.Read(ref owner)?.BeginInvokeShutdown(DispatcherPriority.Send); throw; }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void True(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void False(bool value, string message) => True(!value, message);
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'");
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Desktop;

public static class OverlayScrollChrome
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool),
        typeof(OverlayScrollChrome), new PropertyMetadata(false, OnEnabled));
    private static readonly DependencyPropertyKey ActiveKey = DependencyProperty.RegisterAttachedReadOnly("Active", typeof(bool),
        typeof(OverlayScrollChrome), new PropertyMetadata(false));
    public static readonly DependencyProperty ActiveProperty = ActiveKey.DependencyProperty;
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State),
        typeof(OverlayScrollChrome));
    private static readonly DependencyPropertyDescriptor ContrastDescriptor =
        DependencyPropertyDescriptor.FromProperty(DesktopTheme.IsHighContrastProperty, typeof(ScrollViewer))!;
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    public static bool GetActive(DependencyObject value) => (bool)value.GetValue(ActiveProperty);
    private static void OnEnabled(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is not ScrollViewer scroll) return;
        (scroll.GetValue(StateProperty) as State)?.Dispose();
        scroll.ClearValue(StateProperty);
        if ((bool)e.NewValue) scroll.SetValue(StateProperty, new State(scroll));
    }

    private sealed class State : IDisposable
    {
        private sealed record Fade(ScrollBar Bar);
        private readonly ScrollViewer scroll;
        private UIElement? inputOwner;
        private DispatcherTimer? timer;
        private DispatcherOperation? clearIntent;
        private Thumb? capturedThumb;
        private Fade? fade;

        public State(ScrollViewer scroll)
        {
            this.scroll = scroll;
            scroll.Loaded += Loaded; scroll.Unloaded += Unloaded;
            scroll.IsVisibleChanged += VisibilityChanged;
            UpdatePresentation();
        }
        private ScrollBar? VerticalBar => scroll.Template?.FindName("PART_VerticalScrollBar", scroll) as ScrollBar;
        private bool Owns(Thumb thumb) => VerticalBar?.IsAncestorOf(thumb) == true;
        private void Attach()
        {
            Detach();
            scroll.ScrollChanged += Changed;
            inputOwner = scroll.TemplatedParent as UIElement ?? scroll;
            inputOwner.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Wheel), true);
            inputOwner.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown), true);
            inputOwner.AddHandler(UIElement.ManipulationDeltaEvent, new EventHandler<ManipulationDeltaEventArgs>(Manipulated), true);
            scroll.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(Scrolled), true);
            scroll.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(Command), true);
            scroll.AddHandler(Mouse.GotMouseCaptureEvent, new MouseEventHandler(CaptureChanged), true);
            scroll.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(CaptureChanged), true);
        }
        private void Detach()
        {
            scroll.ScrollChanged -= Changed;
            inputOwner?.RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Wheel));
            inputOwner?.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown));
            inputOwner?.RemoveHandler(UIElement.ManipulationDeltaEvent, new EventHandler<ManipulationDeltaEventArgs>(Manipulated));
            inputOwner = null;
            scroll.RemoveHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(Scrolled));
            scroll.RemoveHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(Command));
            scroll.RemoveHandler(Mouse.GotMouseCaptureEvent, new MouseEventHandler(CaptureChanged));
            scroll.RemoveHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(CaptureChanged));
        }
        private void Loaded(object sender, RoutedEventArgs e) => UpdatePresentation();
        private void Unloaded(object sender, RoutedEventArgs e) { Detach(); Reset(); }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePresentation();
        private void UpdatePresentation()
        {
            if (scroll.IsLoaded && scroll.IsVisible) Attach();
            else { Detach(); Reset(); }
        }
        private void Wheel(object sender, MouseWheelEventArgs e) { if (e.Delta != 0) RememberIntent(); }
        private void Manipulated(object? sender, ManipulationDeltaEventArgs e)
        { if (e.DeltaManipulation.Translation.Y != 0) RememberIntent(); }
        private void KeyDown(object sender, KeyEventArgs e)
        {
            if ((e.KeyboardDevice.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0 &&
                e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space)
                RememberIntent();
        }
        private void Scrolled(object sender, ScrollEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, VerticalBar) && e.ScrollEventType != ScrollEventType.EndScroll)
                RememberIntent();
        }
        private void Command(object sender, ExecutedRoutedEventArgs e)
        {
            if (VerticalBar is not { } bar || e.OriginalSource is not Visual source ||
                (!ReferenceEquals(source, bar) && !bar.IsAncestorOf(source))) return;
            if (e.Command == ScrollBar.LineUpCommand || e.Command == ScrollBar.LineDownCommand ||
                e.Command == ScrollBar.PageUpCommand || e.Command == ScrollBar.PageDownCommand ||
                e.Command == ScrollBar.ScrollToTopCommand || e.Command == ScrollBar.ScrollToBottomCommand ||
                e.Command == ScrollBar.ScrollToVerticalOffsetCommand || e.Command == ScrollBar.DeferScrollToVerticalOffsetCommand ||
                e.Command == ScrollBar.ScrollHereCommand)
                RememberIntent();
        }
        private void RememberIntent()
        {
            clearIntent?.Abort(); clearIntent = null;
            if (scroll.Dispatcher.HasShutdownStarted) return;
            clearIntent = scroll.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => clearIntent = null));
        }
        private void Changed(object sender, ScrollChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, scroll) || e.VerticalChange == 0 ||
                e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0 ||
                e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0) return;
            if (clearIntent is null && capturedThumb is null) return;
            Activate();
        }
        private void CaptureChanged(object sender, MouseEventArgs e)
        {
            if (e.OriginalSource is not Thumb thumb || (!ReferenceEquals(thumb, capturedThumb) && !Owns(thumb))) return;
            if (e.RoutedEvent == Mouse.GotMouseCaptureEvent)
            {
                capturedThumb = thumb;
                Activate();
            }
            else
            {
                capturedThumb = null;
                if (GetActive(scroll)) RestartTimer();
            }
        }
        private void Activate()
        {
            ClearFade();
            scroll.SetValue(ActiveKey, true);
            if (capturedThumb is not null) timer?.Stop();
            else RestartTimer();
        }
        private void RestartTimer()
        {
            if (timer is null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background, scroll.Dispatcher)
                    { Interval = TimeSpan.FromMilliseconds(900) };
                timer.Tick += Idle;
            }
            timer.Stop(); timer.Start();
        }
        private void Idle(object? sender, EventArgs e)
        {
            timer?.Stop();
            if (capturedThumb is not null) return;
            var bar = VerticalBar;
            var opacity = bar?.Opacity ?? 0;
            scroll.SetValue(ActiveKey, false);
            if (bar is null || opacity == 0 || !SystemParameters.ClientAreaAnimation ||
                DesktopTheme.GetIsHighContrast(scroll)) return;
            var animation = new DoubleAnimation(opacity, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            var current = new Fade(bar);
            fade = current;
            ContrastDescriptor.AddValueChanged(scroll, ContrastChanged);
            animation.Completed += (_, _) => { if (ReferenceEquals(fade, current)) ClearFade(); };
            bar.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        private void ContrastChanged(object? sender, EventArgs e)
        { if (DesktopTheme.GetIsHighContrast(scroll)) ClearFade(); }
        private void ClearFade()
        {
            if (fade is not { } current) return;
            fade = null;
            ContrastDescriptor.RemoveValueChanged(scroll, ContrastChanged);
            current.Bar.BeginAnimation(UIElement.OpacityProperty, null);
        }
        private void Reset()
        {
            if (timer is not null) { timer.Stop(); timer.Tick -= Idle; timer = null; }
            clearIntent?.Abort(); clearIntent = null;
            ClearFade();
            scroll.SetValue(ActiveKey, false);
            capturedThumb?.CancelDrag(); capturedThumb?.ReleaseMouseCapture(); capturedThumb = null;
        }
        public void Dispose()
        {
            scroll.Loaded -= Loaded; scroll.Unloaded -= Unloaded;
            scroll.IsVisibleChanged -= VisibilityChanged;
            Detach(); Reset();
        }
    }
}

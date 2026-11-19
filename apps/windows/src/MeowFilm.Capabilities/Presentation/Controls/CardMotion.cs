using System.Runtime.CompilerServices;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// One local hover/focus progress value; it does not change content or playback state.
public sealed class CardMotion : DependencyObject
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(CardMotion),
        new PropertyMetadata(false, EnabledChanged));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached("Progress", typeof(double), typeof(CardMotion), new PropertyMetadata(0d));
    public static bool GetEnabled(DependencyObject owner) => (bool)owner.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject owner, bool value) => owner.SetValue(EnabledProperty, value);
    public static double GetProgress(DependencyObject owner) => (double)owner.GetValue(ProgressProperty);
    public static void SetProgress(DependencyObject owner, double value) => owner.SetValue(ProgressProperty, value);
    private sealed class Motion
    {
        internal bool Pointer, Focus;
        internal EventHandler<object>? Rendering;
        internal IDisposable? Visibility;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, Motion> Motions = new();
    private static void EnabledChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not FrameworkElement element) return;
        if (Motions.TryGetValue(element, out var existing))
        {
            Refresh(element, existing);
            return;
        }
        if (args.NewValue is not true) return;
        var state = Motions.GetValue(element, _ => new());
        element.PointerEntered += (_, _) => SetPointerActive(element, true);
        element.PointerExited += (_, _) => SetPointerActive(element, false);
        element.GotFocus += (_, _) => { state.Focus = true; Refresh(element, state); };
        element.LostFocus += (_, _) =>
        {
            element.DispatcherQueue.TryEnqueue(() =>
            {
                state.Focus = element.XamlRoot is { } root &&
                    Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject focused &&
                    MusicVisuals.IsWithin(focused, element);
                Refresh(element, state);
            });
        };
        void Loaded()
        {
            state.Visibility ??= DesktopVisual.ObserveVisibility(element, visible =>
            {
                if (visible) Refresh(element, state);
                else { state.Focus = state.Pointer = false; Reset(element, state); }
            });
            Refresh(element, state);
        }
        element.Loaded += (_, _) => Loaded();
        element.DataContextChanged += (_, _) => { Reset(element, state); Refresh(element, state); };
        if (element is Microsoft.UI.Xaml.Controls.Control control)
            control.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.Control.IsEnabledProperty, (_, _) => Refresh(element, state));
        element.Unloaded += (_, _) =>
        {
            state.Visibility?.Dispose(); state.Visibility = null;
            state.Focus = state.Pointer = false; Reset(element, state);
        };
        if (element.IsLoaded) Loaded();
    }
    internal static bool IsPointerActive(FrameworkElement element) =>
        Motions.TryGetValue(element, out var motion) && motion.Pointer;
    internal static bool HasActiveAnimation(FrameworkElement element) =>
        Motions.TryGetValue(element, out var motion) && motion.Rendering is not null;
    internal static void SetPointerActive(FrameworkElement element, bool active)
    {
        if (!Motions.TryGetValue(element, out var motion)) return;
        motion.Pointer = active; Refresh(element, motion);
    }
    private static void Refresh(FrameworkElement element, Motion motion)
    {
        if (!GetEnabled(element) || !element.IsLoaded || !DesktopVisual.IsVisible(element) ||
            element is Microsoft.UI.Xaml.Controls.Control { IsEnabled: false })
        {
            Reset(element, motion);
            return;
        }
        double from = GetProgress(element), to = motion.Pointer || motion.Focus ? 1 : 0;
        Stop(motion);
        if (MusicVisuals.CanAnimate(element) && Math.Abs(from - to) > .001)
        {
            long started = Stopwatch.GetTimestamp();
            double duration = to > 0 ? .22 : .16;
            // Progress drives several layout and brush properties on the UI thread.
            // Use the render clock so no custom attached-property animation path is required.
            motion.Rendering = (_, _) =>
            {
                double time = Math.Min(1, Stopwatch.GetElapsedTime(started).TotalSeconds / duration);
                SetProgress(element, from + (to - from) * (1 - Math.Pow(1 - time, 3)));
                if (time >= 1) Stop(motion);
            };
            CompositionTarget.Rendering += motion.Rendering;
        }
        else SetProgress(element, to);
    }
    private static void Stop(Motion motion)
    {
        if (motion.Rendering is null) return;
        CompositionTarget.Rendering -= motion.Rendering;
        motion.Rendering = null;
    }
    private static void Reset(FrameworkElement element, Motion motion)
    {
        Stop(motion); SetProgress(element, 0);
    }
}

using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// One local hover/focus progress value; it does not change content or playback state.
public sealed class CardMotion : DependencyObject
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(CardMotion),
        new PropertyMetadata(false, EnabledChanged));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached("Progress", typeof(double), typeof(CardMotion), new PropertyMetadata(0d));
    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.RegisterAttached("HoverScale", typeof(double), typeof(CardMotion),
        new PropertyMetadata(0d, TargetChanged));
    public static readonly DependencyProperty HoverOpacityProperty = DependencyProperty.RegisterAttached("HoverOpacity", typeof(bool), typeof(CardMotion),
        new PropertyMetadata(false, TargetChanged));
    public static readonly DependencyProperty AnimationProgressProperty = DependencyProperty.Register(nameof(AnimationProgress), typeof(double), typeof(CardMotion),
        new PropertyMetadata(0d, (owner, args) => { if (((CardMotion)owner).target is { } element) SetProgress(element, (double)args.NewValue); }));
    private readonly FrameworkElement? target;
    public double AnimationProgress { get => (double)GetValue(AnimationProgressProperty); set => SetValue(AnimationProgressProperty, value); }
    public CardMotion() { }
    private CardMotion(FrameworkElement target) => this.target = target;

    public static bool GetEnabled(DependencyObject owner) => (bool)owner.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject owner, bool value) => owner.SetValue(EnabledProperty, value);
    public static double GetProgress(DependencyObject owner) => (double)owner.GetValue(ProgressProperty);
    public static void SetProgress(DependencyObject owner, double value)
    {
        owner.SetValue(ProgressProperty, value);
        if (owner is FrameworkElement element && Motions.TryGetValue(element, out var motion))
            foreach (var target in motion.Targets) ApplyTarget(target, value);
    }
    public static double GetHoverScale(DependencyObject owner) => (double)owner.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject owner, double value) => owner.SetValue(HoverScaleProperty, value);
    public static bool GetHoverOpacity(DependencyObject owner) => (bool)owner.GetValue(HoverOpacityProperty);
    public static void SetHoverOpacity(DependencyObject owner, bool value) => owner.SetValue(HoverOpacityProperty, value);

    private sealed class Motion
    {
        internal bool Pointer, Focus;
        internal Storyboard? Storyboard;
        internal IDisposable? Visibility;
        internal readonly CardMotion AnimationTarget;
        internal readonly List<FrameworkElement> Targets = [];
        internal Motion(FrameworkElement target) => AnimationTarget = new(target);
    }
    private sealed class TargetRegistration
    {
        internal FrameworkElement? Host;
        internal bool Hooked;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, Motion> Motions = new();
    private static readonly ConditionalWeakTable<FrameworkElement, TargetRegistration> Registrations = new();

    private static void TargetChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not FrameworkElement element) return;
        var reg = Registrations.GetOrCreateValue(element);
        if (!reg.Hooked)
        {
            reg.Hooked = true;
            element.Loaded += (_, _) => AttachTarget(element, reg);
            element.Unloaded += (_, _) => DetachTarget(element, reg);
        }
        if (element.IsLoaded) AttachTarget(element, reg);
    }

    private static void AttachTarget(FrameworkElement element, TargetRegistration reg)
    {
        FrameworkElement? host = null;
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement candidate && GetEnabled(candidate))
            {
                host = candidate;
                break;
            }
        }
        if (!ReferenceEquals(reg.Host, host)) DetachTarget(element, reg);
        if (host is null || !Motions.TryGetValue(host, out var motion))
        {
            ApplyTarget(element, 0);
            return;
        }
        reg.Host = host;
        if (!motion.Targets.Contains(element)) motion.Targets.Add(element);
        ApplyTarget(element, GetProgress(host));
    }

    private static void DetachTarget(FrameworkElement element, TargetRegistration reg)
    {
        if (reg.Host is { } host && Motions.TryGetValue(host, out var motion))
            motion.Targets.Remove(element);
        reg.Host = null;
        ApplyTarget(element, 0);
    }

    private static void ApplyTarget(FrameworkElement target, double progress)
    {
        double scale = GetHoverScale(target);
        if (scale > 0)
        {
            if (target.RenderTransform is not ScaleTransform transform)
            {
                transform = new ScaleTransform();
                target.RenderTransform = transform;
            }
            double factor = 1d + progress * scale;
            transform.ScaleX = factor;
            transform.ScaleY = factor;
        }
        if (GetHoverOpacity(target))
            target.Opacity = progress;
    }
    private static void EnabledChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not FrameworkElement element) return;
        if (Motions.TryGetValue(element, out var existing))
        {
            Refresh(element, existing);
            return;
        }
        if (args.NewValue is not true) return;
        var state = Motions.GetValue(element, _ => new Motion(element));
        element.PointerEntered += (_, _) => SetPointerActive(element, true);
        element.PointerExited += (_, _) => SetPointerActive(element, false);
        element.GotFocus += (_, _) => { state.Focus = true; Refresh(element, state); };
        element.LostFocus += (_, _) =>
        {
            element.DispatcherQueue.TryEnqueue(() =>
            {
                state.Focus = element.XamlRoot is { } root &&
                    Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject focused &&
                    DesktopVisual.Contains(element, focused);
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
        Motions.TryGetValue(element, out var motion) && motion.Storyboard?.GetCurrentState() == ClockState.Active;
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
        motion.AnimationTarget.AnimationProgress = from;
        motion.Storyboard?.Stop(); motion.Storyboard = null;
        SetProgress(element, from);
        if (DesktopVisual.CanAnimate(element) && Math.Abs(from - to) > .001)
        {
            Storyboard? animation = null;
            animation = DesktopVisual.AnimateDouble(motion.AnimationTarget, nameof(AnimationProgress), from, to,
                TimeSpan.FromMilliseconds(to > 0 ? 220 : 160), () =>
                {
                    if (!ReferenceEquals(motion.Storyboard, animation)) return;
                    motion.AnimationTarget.AnimationProgress = to;
                    motion.Storyboard?.Stop(); motion.Storyboard = null; SetProgress(element, to);
                });
            motion.Storyboard = animation;
        }
        else SetProgress(element, to);
    }
    private static void Reset(FrameworkElement element, Motion motion)
    {
        motion.AnimationTarget.AnimationProgress = 0;
        motion.Storyboard?.Stop(); motion.Storyboard = null; SetProgress(element, 0);
    }
}

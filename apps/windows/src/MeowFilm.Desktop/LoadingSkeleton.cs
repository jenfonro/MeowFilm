using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace MeowFilm.Desktop;

public sealed class LoadingSkeleton : Grid
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(LoadingSkeleton), new PropertyMetadata(true, (owner, _) => ((LoadingSkeleton)owner).Refresh()));
    private readonly UISettings settings = new();
    private readonly AccessibilitySettings accessibility = new();
    private Storyboard? animation;
    private IDisposable? visibility;
    private bool observing;
    internal bool IsSweeping => animation is not null;
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public LoadingSkeleton()
    {
        IsHitTestVisible = false;
        this.UseResource(BackgroundProperty, "MeowFilm.Brush.Text");
        Loaded += (_, _) =>
        {
            settings.AnimationsEnabledChanged += MotionChanged; settings.ColorValuesChanged += ContrastChanged; observing = true;
            visibility = DesktopVisual.ObserveVisibility(this, _ => Refresh());
        };
        Unloaded += (_, _) =>
        {
            visibility?.Dispose(); visibility = null; Stop();
            if (!observing) return;
            observing = false; settings.AnimationsEnabledChanged -= MotionChanged; settings.ColorValuesChanged -= ContrastChanged;
        };
    }
    private void MotionChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Refresh);
    private void ContrastChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Refresh);
    private void Refresh() => UpdateAnimation(settings.AnimationsEnabled, accessibility.HighContrast);
    internal void UpdateAnimation(bool animationsEnabled, bool highContrast)
    {
        bool run = IsLoaded && DesktopVisual.IsVisible(this) && IsActive && animationsEnabled && !highContrast;
        if (run == IsSweeping) return;
        Stop();
        if (!run) { Opacity = highContrast ? .3 : .08; return; }
        var pulse = new DoubleAnimation { From = .06, To = .12, Duration = new Duration(TimeSpan.FromSeconds(.6)), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(pulse, this); Storyboard.SetTargetProperty(pulse, "Opacity");
        animation = new Storyboard(); animation.Children.Add(pulse); animation.Begin();
    }
    private void Stop() { animation?.Stop(); animation = null; }
}

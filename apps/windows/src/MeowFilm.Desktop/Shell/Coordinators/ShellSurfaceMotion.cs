using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace MeowFilm.Desktop;

internal sealed class ShellSurfaceMotion : IDisposable
{
    private Storyboard? _detailMotion;
    private Storyboard? _queueMotion;

    public static bool ReducedMotion => !SystemAppearance.AnimationsEnabled || SystemAppearance.IsHighContrast;

    public void StopDetail()
    {
        _detailMotion?.Stop();
        _detailMotion = null;
    }

    public void StopQueue()
    {
        _queueMotion?.Stop();
        _queueMotion = null;
    }

    public void StartDetail(DependencyObject target, double from, double to, TimeSpan duration, Action complete)
    {
        StopDetail();
        _detailMotion = Animate(target, "Y", from, to, duration, complete);
    }

    public void StartQueue(DependencyObject target, double from, double to, TimeSpan duration, Action complete)
    {
        StopQueue();
        _queueMotion = Animate(target, "X", from, to, duration, complete);
    }

    private static Storyboard Animate(DependencyObject target, string property, double from, double to, TimeSpan duration, Action complete)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => complete();
        storyboard.Begin();
        return storyboard;
    }

    public void Dispose()
    {
        StopDetail();
        StopQueue();
    }
}

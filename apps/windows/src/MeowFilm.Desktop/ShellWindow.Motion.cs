using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly UISettings motionSettings = new();
    private bool ReducedMotion => !motionSettings.AnimationsEnabled || _accessibility.HighContrast;
    private Storyboard? detailMotion, queueMotion;
    private void OnPresentationSizeChanged(object sender, SizeChangedEventArgs args) =>
        PresentationSurface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
    private static Storyboard Animate(DependencyObject target, string property, double from, double to, TimeSpan duration, Action complete)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard(); storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => complete(); storyboard.Begin(); return storyboard;
    }
    private void DisposePresentationMotion() { detailMotion?.Stop(); queueMotion?.Stop(); }
}

using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class LoadingSkeletonTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("loading-skeleton: visible activity follows system preferences and ends on hide unload or completion",
            () => MusicQualityConsumerTests.Sta(Lifecycle));
    }

    private static async Task Lifecycle()
    {
        var skeleton = new LoadingSkeleton { Width = 176, Height = 264, CornerRadius = new(14) };
        var window = new Window
        {
            Content = skeleton, Width = 220, Height = 320,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            Check(!skeleton.IsSweeping, "unloaded skeleton is idle");
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            skeleton.Resources["MeowFilm.Brush.Text"] = Brushes.White;
            var gradient = (LinearGradientBrush)skeleton.Background;
            if (!SystemParameters.HighContrast)
                Check(gradient.GradientStops[0].Color.R == 255, "skeleton follows the theme text color");
            skeleton.Resources["MeowFilm.Brush.Text"] = Brushes.Black;
            if (!SystemParameters.HighContrast)
                Check(gradient.GradientStops[0].Color.R == 0, "theme changes update the retained brush");
            DesktopTheme.SetIsHighContrast(skeleton, true);
            Check(!skeleton.IsSweeping, "inherited high contrast disables animation");
            DesktopTheme.SetIsHighContrast(skeleton, false);
            skeleton.UpdateAnimation(true, false);
            Check(skeleton.IsSweeping, "visible active skeleton sweeps");
            skeleton.UpdateAnimation(false, false);
            Check(!skeleton.IsSweeping, "reduced animation stops the clock");
            skeleton.UpdateAnimation(true, true);
            Check(!skeleton.IsSweeping, "high contrast stops the clock");
            skeleton.UpdateAnimation(true, false);
            skeleton.Visibility = Visibility.Collapsed;
            Check(!skeleton.IsSweeping, "collapsed skeleton releases its animation");
            skeleton.UpdateAnimation(true, false);
            Check(!skeleton.IsSweeping, "system preference change cannot reactivate hidden skeleton");
            skeleton.Visibility = Visibility.Visible;
            skeleton.UpdateAnimation(true, false);
            Check(skeleton.IsSweeping, "visible retained instance resumes");
            skeleton.IsActive = false;
            Check(!skeleton.IsSweeping, "completion or failure deactivates the clock");
            skeleton.IsActive = true;
            skeleton.UpdateAnimation(true, false);
            window.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!skeleton.IsSweeping, "unloaded skeleton releases its animation");
        }
        finally { window.Content = null; window.Close(); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

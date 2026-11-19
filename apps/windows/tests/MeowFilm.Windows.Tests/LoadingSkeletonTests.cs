using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class LoadingSkeletonTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("loading-skeleton: visible activity follows system preferences and ends on hide unload or completion",
            () => TestUiHost.InvokeAsync(Lifecycle));
    }

    private static async Task Lifecycle()
    {
        var skeleton = new LoadingSkeleton { Width = 176, Height = 264, CornerRadius = new(14) };
        var root = new Microsoft.UI.Xaml.Controls.Grid();
        root.Children.Add(skeleton);
        var window = FilmUiTest.Window(root, 220, 320);
        try
        {
            Check(!skeleton.IsSweeping, "unloaded skeleton is idle");
            window.Show();
            await DesktopTestUi.DrainAsync();
            Check(skeleton.Background is SolidColorBrush { Color.A: > 0 }, "skeleton uses a real theme brush");
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
            root.Visibility = Visibility.Collapsed;
            await DesktopTestUi.DrainAsync();
            Check(skeleton.IsLoaded && !skeleton.IsSweeping, "ancestor hiding retires the animation without unloading");
            root.Visibility = Visibility.Visible;
            await DesktopTestUi.DrainAsync();
            skeleton.UpdateAnimation(true, false);
            Check(skeleton.IsSweeping, "retained skeleton resumes after its ancestor returns");
            skeleton.IsActive = false;
            Check(!skeleton.IsSweeping, "completion or failure deactivates the clock");
            skeleton.IsActive = true;
            skeleton.UpdateAnimation(true, false);
            window.Content = null;
            await DesktopTestUi.DrainAsync();
            Check(!skeleton.IsSweeping, "unloaded skeleton releases its animation");
        }
        finally { window.Content = null; window.Close(); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

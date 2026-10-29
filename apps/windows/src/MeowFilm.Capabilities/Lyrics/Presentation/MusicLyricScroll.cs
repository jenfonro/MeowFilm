using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

// One animation clock per viewer, replaced on seek/manual scrolling, never a
// media clock or polling loop. Offset is physical because lyrics wrap.
internal static class MusicLyricScroll
{
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(MusicLyricScroll),
        new PropertyMetadata(0d, (owner, args) => ((ScrollViewer)owner).ScrollToVerticalOffset((double)args.NewValue)));

    internal static void Stop(ScrollViewer viewer)
    {
        var offset = viewer.VerticalOffset;
        viewer.BeginAnimation(OffsetProperty, null);
        viewer.SetValue(OffsetProperty, offset);
    }

    internal static void Move(ScrollViewer viewer, double target, bool manual = false, Action? settled = null)
    {
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        var from = viewer.VerticalOffset;
        Stop(viewer);
        viewer.SetValue(OffsetProperty, target);
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || Math.Abs(from - target) < .5)
        {
            QueueSettled(viewer, settled);
            return;
        }
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(manual ? 180 : 420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        if (settled is not null)
            animation.Completed += (_, _) => QueueSettled(viewer, settled);
        viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void Settle(ScrollViewer viewer, double target)
    {
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        Stop(viewer);
        viewer.SetValue(OffsetProperty, target);
    }

    private static void QueueSettled(ScrollViewer viewer, Action? settled)
    {
        if (settled is null) return;
        viewer.Dispatcher.BeginInvoke(DispatcherPriority.Render, settled);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

// One animation clock per viewer, replaced on seek/manual scrolling, never a
// media clock or polling loop. Offset is physical because lyrics wrap.
internal static class MusicLyricScroll
{
    private sealed class Motion
    {
        internal double? Target;
        internal long Epoch;
        internal bool Moving;
        internal Action? Settled;
    }

    private static readonly DependencyProperty MotionProperty = DependencyProperty.RegisterAttached(
        "Motion", typeof(Motion), typeof(MusicLyricScroll));
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(MusicLyricScroll),
        new PropertyMetadata(0d, (owner, args) => ((ScrollViewer)owner).ScrollToVerticalOffset((double)args.NewValue)));

    private static Motion State(ScrollViewer viewer)
    {
        if (viewer.GetValue(MotionProperty) is Motion state) return state;
        state = new Motion();
        viewer.SetValue(MotionProperty, state);
        return state;
    }

    internal static void Stop(ScrollViewer viewer)
    {
        var state = State(viewer);
        state.Epoch++;
        state.Target = null;
        state.Moving = false;
        state.Settled = null;
        var offset = viewer.VerticalOffset;
        viewer.BeginAnimation(OffsetProperty, null);
        viewer.SetValue(OffsetProperty, offset);
    }

    internal static void Move(ScrollViewer viewer, double target, bool manual = false, Action? settled = null)
    {
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        var state = State(viewer);
        if (state.Target == target)
        {
            state.Settled = settled;
            if (!state.Moving) QueueSettled(viewer, state);
            return;
        }
        var from = viewer.VerticalOffset;
        Stop(viewer);
        state.Target = target;
        state.Settled = settled;
        viewer.SetValue(OffsetProperty, target);
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || Math.Abs(from - target) < .5)
        {
            QueueSettled(viewer, state);
            return;
        }
        state.Moving = true;
        var epoch = state.Epoch;
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(manual ? 180 : 420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (epoch != state.Epoch) return;
            state.Moving = false;
            viewer.BeginAnimation(OffsetProperty, null);
            QueueSettled(viewer, state);
        };
        viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void Settle(ScrollViewer viewer, double target)
    {
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        Stop(viewer);
        viewer.SetValue(OffsetProperty, target);
    }

    private static void QueueSettled(ScrollViewer viewer, Motion state)
    {
        if (state.Settled is null) return;
        var epoch = state.Epoch;
        viewer.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (epoch != state.Epoch) return;
            var settled = state.Settled;
            state.Settled = null;
            settled?.Invoke();
        }));
    }
}

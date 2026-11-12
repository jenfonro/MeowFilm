using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

// One animation clock per viewer, replaced on seek/manual scrolling, never a
// media clock or polling loop. Manual browsing uses physical offsets for middle-line
// hit testing; automatic following arranges once and animates only the presentation.
internal static class MusicLyricScroll
{
    private sealed class Motion
    {
        internal double? Target;
        internal long Epoch;
        internal bool Moving;
        internal bool Manual;
        internal TranslateTransform? Presentation;
        internal MusicLyricList? List;
        internal Action? Settled;
    }

    // Virtualized reflow can shrink the range while the manual destination is still in flight.
    internal static double ManualTarget(ScrollViewer viewer) =>
        State(viewer) is { Moving: true, Manual: true, Target: { } target }
            ? Math.Clamp(target, 0, viewer.ScrollableHeight) : PresentedOffset(viewer);

    private static double PresentedOffset(ScrollViewer viewer) =>
        viewer.VerticalOffset - (State(viewer).Presentation?.Y ?? 0);

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
        var offset = PresentedOffset(viewer);
        if (state.Presentation is { } presentation)
        {
            presentation.BeginAnimation(TranslateTransform.YProperty, null);
            presentation.Y = 0;
            state.Presentation = null;
            state.List?.EndPresentationMotion(presentation);
            state.List = null;
        }
        viewer.BeginAnimation(OffsetProperty, null);
        viewer.SetValue(OffsetProperty, offset);
    }

    internal static void Move(ScrollViewer viewer, double target, TranslateTransform presentation,
        MusicLyricList list, Action? settled = null) => MoveCore(viewer, target, presentation, list, settled);

    internal static void MoveManual(ScrollViewer viewer, double target, Action? settled = null) =>
        MoveCore(viewer, target, null, null, settled);

    private static void MoveCore(ScrollViewer viewer, double target, TranslateTransform? presentation,
        MusicLyricList? list, Action? settled)
    {
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        var state = State(viewer);
        if (state.Target == target)
        {
            state.Settled = settled;
            if (!state.Moving) QueueSettled(viewer, state);
            return;
        }
        var from = PresentedOffset(viewer);
        Stop(viewer);
        state.Target = target;
        state.Manual = presentation is null;
        state.Settled = settled;
        viewer.SetValue(OffsetProperty, target);
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || Math.Abs(from - target) < .5)
        {
            viewer.UpdateLayout();
            QueueSettled(viewer, state);
            return;
        }
        state.Moving = true;
        var epoch = state.Epoch;
        if (presentation is not null)
        {
            // The list caches one page on each side. No intermediate off-screen
            // rows need to be created just to animate a distant seek/return.
            viewer.UpdateLayout();
            var shift = Math.Clamp(viewer.VerticalOffset - from, -viewer.ViewportHeight, viewer.ViewportHeight);
            state.Presentation = presentation;
            state.List = list;
            list?.BeginPresentationMotion(presentation, shift);
            var translation = new DoubleAnimation(shift, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            translation.Completed += (_, _) =>
            {
                if (epoch != state.Epoch) return;
                state.Moving = false;
                presentation.BeginAnimation(TranslateTransform.YProperty, null);
                state.Presentation = null;
                state.List?.EndPresentationMotion(presentation);
                state.List = null;
                QueueSettled(viewer, state);
            };
            presentation.BeginAnimation(TranslateTransform.YProperty, translation, HandoffBehavior.SnapshotAndReplace);
            return;
        }
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
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

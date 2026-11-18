using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

// ScrollViewer owns physical offsets and compositor scrolling. Completion comes
// from its final ViewChanged event, not a guessed animation duration.
internal static class MusicLyricScroll
{
    private sealed class Motion
    {
        internal double? Target;
        internal Action? Settled;
        internal bool Moving;
        internal long Epoch;
    }
    private static readonly ConditionalWeakTable<ScrollViewer, Motion> Motions = new();
    private static Motion State(ScrollViewer viewer) => Motions.GetValue(viewer, owner =>
    {
        var state = new Motion();
        owner.ViewChanged += (_, args) =>
        {
            if (args.IsIntermediate || !state.Moving || state.Target is not { } target ||
                Math.Abs(owner.VerticalOffset - Math.Clamp(target, 0, owner.ScrollableHeight)) >= .5) return;
            state.Moving = false; state.Target = null;
            var settled = state.Settled; state.Settled = null; settled?.Invoke();
        };
        return state;
    });
    internal static double ManualTarget(ScrollViewer viewer) => State(viewer).Target ?? viewer.VerticalOffset;
    internal static void Stop(ScrollViewer viewer)
    {
        var state = State(viewer); state.Epoch++; state.Moving = false; state.Target = null; state.Settled = null;
        viewer.ChangeView(null, viewer.VerticalOffset, null, disableAnimation: true);
    }
    internal static void Move(ScrollViewer viewer, double target, Action? settled = null) => MoveCore(viewer, target, settled);
    internal static void MoveManual(ScrollViewer viewer, double target, Action? settled = null) => MoveCore(viewer, target, settled);
    private static void MoveCore(ScrollViewer viewer, double target, Action? settled)
    {
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        var state = State(viewer);
        if (state.Moving && state.Target is { } existing && Math.Abs(existing - target) < .5)
        { state.Settled = settled; return; }
        Stop(viewer);
        state.Target = target; state.Settled = settled; state.Moving = true;
        var epoch = state.Epoch;
        if (Math.Abs(viewer.VerticalOffset - target) < .5 || !viewer.ChangeView(null, target, null, !MusicVisuals.CanAnimate(viewer)))
            viewer.DispatcherQueue.TryEnqueue(() =>
            {
                if (epoch != state.Epoch) return;
                state.Moving = false; state.Target = null;
                var callback = state.Settled; state.Settled = null; callback?.Invoke();
            });
    }
    internal static void Settle(ScrollViewer viewer, double target)
    {
        Stop(viewer); viewer.ChangeView(null, Math.Clamp(target, 0, viewer.ScrollableHeight), null, true);
    }
}

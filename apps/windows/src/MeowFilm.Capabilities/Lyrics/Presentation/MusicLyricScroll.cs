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
        internal bool Issued;
        internal double? NextOffset;
        internal long Epoch;
    }
    private static readonly ConditionalWeakTable<ScrollViewer, Motion> Motions = new();
    private static Motion State(ScrollViewer viewer) => Motions.GetValue(viewer, owner =>
    {
        var state = new Motion();
        owner.ViewChanging += (_, args) =>
        {
            if (state.Moving && state.Issued) state.NextOffset = args.NextView.VerticalOffset;
        };
        owner.ViewChanged += (_, args) =>
        {
            if (args.IsIntermediate || !state.Moving || !state.Issued || state.NextOffset is not { } next ||
                Math.Abs(owner.VerticalOffset - next) >= .5) return;
            // Stop/retarget can leave an earlier final event in the native queue.
            // Require a native ViewChanging for the issued successor. Its NextView
            // follows virtualized extent/anchor corrections; the requested absolute
            // target is not a completion condition.
            state.Moving = false; state.Target = state.NextOffset = null;
            var settled = state.Settled; state.Settled = null; settled?.Invoke();
        };
        return state;
    });
    internal static bool IsMoving(ScrollViewer viewer) => State(viewer).Moving;
    internal static double ManualTarget(ScrollViewer viewer) => State(viewer).Target ?? viewer.VerticalOffset;
    internal static void Stop(ScrollViewer viewer)
    {
        var state = State(viewer); state.Epoch++; state.Moving = state.Issued = false;
        state.Target = state.NextOffset = null; state.Settled = null;
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
        viewer.DispatcherQueue.TryEnqueue(() =>
        {
            // Coalesce requests before handing a motion to the native compositor.
            // Stop in this dispatcher turn must also retire a not-yet-started ChangeView.
            if (epoch != state.Epoch) return;
            state.Issued = true;
            if (Math.Abs(viewer.VerticalOffset - target) >= .5 &&
                viewer.ChangeView(null, target, null, !MusicVisuals.CanAnimate(viewer))) return;
            state.Moving = false; state.Target = null;
            var callback = state.Settled; state.Settled = null; callback?.Invoke();
        });
    }
    internal static void Settle(ScrollViewer viewer, double target)
    {
        Stop(viewer); viewer.ChangeView(null, Math.Clamp(target, 0, viewer.ScrollableHeight), null, true);
    }
}

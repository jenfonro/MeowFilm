using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MeowFilm.Capabilities;

internal sealed class SettingsReorderShift
{
    private sealed class Shift(Transform original)
    {
        internal Transform Original { get; } = original;
        internal TranslateTransform Translation { get; } = new();
        internal double Target { get; set; }
        internal int Generation { get; set; }
    }

    private readonly Dictionary<FrameworkElement, Shift> shifts = [];

    internal void Apply(FrameworkElement element, int index, int sourceIndex, int destinationIndex,
        double rowHeight, bool animate)
    {
        double target = destinationIndex < 0 || index == sourceIndex ? 0 :
            index > sourceIndex && index <= destinationIndex ? -rowHeight :
            index < sourceIndex && index >= destinationIndex ? rowHeight : 0;
        if (!shifts.TryGetValue(element, out var shift))
        {
            if (target == 0) return;
            shift = new(element.RenderTransform);
            shifts.Add(element, shift);
            element.SetCurrentValue(UIElement.RenderTransformProperty,
                new TransformGroup { Children = { shift.Original, shift.Translation } });
        }
        if (shift.Target == target && (animate || !shift.Translation.HasAnimatedProperties)) return;
        double from = shift.Translation.Y;
        shift.Translation.BeginAnimation(TranslateTransform.YProperty, null);
        shift.Translation.Y = target;
        shift.Target = target;
        int generation = ++shift.Generation;
        if (!animate || Math.Abs(target - from) < .5) return;
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (shift.Generation == generation) shift.Translation.BeginAnimation(TranslateTransform.YProperty, null);
        };
        shift.Translation.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal void Clear()
    {
        foreach (var (element, shift) in shifts)
        {
            shift.Generation++;
            shift.Translation.BeginAnimation(TranslateTransform.YProperty, null);
            element.SetCurrentValue(UIElement.RenderTransformProperty, shift.Original);
        }
        shifts.Clear();
    }
}

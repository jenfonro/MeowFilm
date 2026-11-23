using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace MeowFilm.Capabilities;

internal sealed class SettingsReorderShift
{
    private sealed class Shift(Transform? original)
    {
        internal Transform? Original { get; } = original;
        internal TransformGroup Group { get; } = new();
        internal TranslateTransform Translation { get; } = new();
        internal Storyboard? Animation { get; set; }
        internal double Target { get; set; }
    }
    private readonly Dictionary<FrameworkElement, Shift> shifts = [];
    internal void Apply(FrameworkElement element, int index, int sourceIndex, int destinationIndex, double rowHeight, bool animate)
    {
        double target = destinationIndex < 0 || index == sourceIndex ? 0 :
            index > sourceIndex && index <= destinationIndex ? -rowHeight :
            index < sourceIndex && index >= destinationIndex ? rowHeight : 0;
        if (!shifts.TryGetValue(element, out var shift))
        {
            if (target == 0) return;
            shift = new(element.RenderTransform); shifts.Add(element, shift);
            if (shift.Original is not null) shift.Group.Children.Add(shift.Original);
            shift.Group.Children.Add(shift.Translation); element.RenderTransform = shift.Group;
        }
        if (shift.Target == target) return;
        double from = shift.Translation.Y;
        shift.Animation?.Stop(); shift.Translation.Y = target; shift.Target = target;
        if (!animate || Math.Abs(target - from) < .5) return;
        var animation = new DoubleAnimation { From = from, To = target, Duration = new(TimeSpan.FromMilliseconds(150)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
        Storyboard.SetTarget(animation, shift.Translation); Storyboard.SetTargetProperty(animation, "Y");
        shift.Animation = new Storyboard(); shift.Animation.Children.Add(animation); shift.Animation.Begin();
    }
    internal void Clear()
    {
        foreach (var (element, shift) in shifts)
        {
            shift.Animation?.Stop();
            shift.Group.Children.Clear();
            element.RenderTransform = shift.Original;
        }
        shifts.Clear();
    }
}

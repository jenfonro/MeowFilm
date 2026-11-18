using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MeowFilm.Capabilities;

internal static class MusicVisuals
{
    internal static bool Animate => new UISettings().AnimationsEnabled && !new AccessibilitySettings().HighContrast;
    internal static bool CanAnimate(DependencyObject view)
    {
        if (!Animate) return false;
        for (DependencyObject? node = view; node is not null; node = VisualTreeHelper.GetParent(node))
            if (MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(node)) return false;
        return true;
    }
    internal static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255) => new(Color.FromArgb(a, r, g, b));
    internal static SolidColorBrush Brush(string value) => (SolidColorBrush)XamlReader.Load(
        $"<SolidColorBrush xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Color='{value}'/>");
    internal static Geometry Geometry(string value) => ((Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
        $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{value}'/>")).Data;
    internal static void Bind(FrameworkElement target, DependencyProperty property, string path, object? source = null,
        BindingMode mode = BindingMode.OneWay) => target.SetBinding(property,
            new Binding { Path = new PropertyPath(path), Source = source, Mode = mode });
    internal static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
    internal static bool IsWithin(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? node = child; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }
    internal static Storyboard AnimateDouble(DependencyObject target, string property, double from, double to,
        TimeSpan duration, Action? completed = null, bool forever = false)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = duration, EnableDependentAnimation = true,
            EasingFunction = forever ? null : new CubicEase { EasingMode = EasingMode.EaseOut },
            RepeatBehavior = forever ? RepeatBehavior.Forever : new RepeatBehavior(1)
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard(); storyboard.Children.Add(animation);
        if (completed is not null) storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
        return storyboard;
    }
}

public sealed class MusicVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool visible = value is true || value is string { Length: > 0 };
        if (parameter as string == "Not") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class MusicMotionHitTestConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is double progress && progress > 0;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

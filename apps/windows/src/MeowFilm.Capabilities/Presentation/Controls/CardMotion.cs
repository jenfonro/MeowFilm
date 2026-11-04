using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Animation;

namespace MeowFilm.Capabilities;

// Presentation only. One replaceable animation clock, no timer, network or playback.
public static class CardMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled",
        typeof(bool), typeof(CardMotion), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached("Progress",
        typeof(double), typeof(CardMotion), new PropertyMetadata(0d));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    public static double GetProgress(DependencyObject value) => (double)value.GetValue(ProgressProperty);
    public static void SetProgress(DependencyObject value, double progress) => value.SetValue(ProgressProperty, progress);
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            element.MouseEnter += Mouse; element.MouseLeave += Mouse;
            element.IsKeyboardFocusWithinChanged += State; element.IsEnabledChanged += State;
            element.IsVisibleChanged += State;
            element.Loaded += Loaded; element.Unloaded += Unloaded; element.DataContextChanged += Context;
            Update(element);
        }
        else
        {
            element.MouseEnter -= Mouse; element.MouseLeave -= Mouse;
            element.IsKeyboardFocusWithinChanged -= State; element.IsEnabledChanged -= State;
            element.IsVisibleChanged -= State;
            element.Loaded -= Loaded; element.Unloaded -= Unloaded; element.DataContextChanged -= Context;
            Reset(element);
        }
    }
    private static void Mouse(object sender, System.Windows.Input.MouseEventArgs _) => Update((FrameworkElement)sender);
    private static void State(object sender, DependencyPropertyChangedEventArgs _) => Update((FrameworkElement)sender);
    private static void Loaded(object sender, RoutedEventArgs _) => Update((FrameworkElement)sender);
    private static void Unloaded(object sender, RoutedEventArgs _) => Reset((FrameworkElement)sender);
    private static void Context(object sender, DependencyPropertyChangedEventArgs _) { Reset((FrameworkElement)sender); Update((FrameworkElement)sender); }
    internal static void Reset(FrameworkElement element)
    { element.BeginAnimation(ProgressProperty, null); SetProgress(element, 0); }
    private static void Update(FrameworkElement element)
    {
        if (!element.IsLoaded || !element.IsVisible) { Reset(element); return; }
        Transition(element, element.IsEnabled && (element.IsMouseOver || element.IsKeyboardFocusWithin),
            SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast);
    }
    internal static void Transition(FrameworkElement element, bool active, bool animate)
    {
        double from = GetProgress(element), to = active ? 1 : 0;
        element.BeginAnimation(ProgressProperty, null);
        SetProgress(element, to);
        if (!animate || Math.Abs(from - to) < .001) return;
        element.BeginAnimation(ProgressProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(active ? 220 : 160))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop },
            HandoffBehavior.SnapshotAndReplace);
    }
}

public sealed class MusicEntryOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type type, object parameter, CultureInfo culture) =>
        values.Length == 3 && values[0] is double height && values[1] is double progress && values[2] is true
            ? -Math.Max(0, height - 80) * progress : 0d;
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

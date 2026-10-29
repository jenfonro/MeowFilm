using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MeowFilm.Desktop;

public static class OverlayScrollChrome
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool),
        typeof(OverlayScrollChrome), new PropertyMetadata(false, OnEnabled));
    private static readonly DependencyPropertyKey ActiveKey = DependencyProperty.RegisterAttachedReadOnly("Active", typeof(bool),
        typeof(OverlayScrollChrome), new PropertyMetadata(false));
    public static readonly DependencyProperty ActiveProperty = ActiveKey.DependencyProperty;
    private static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached("Timer", typeof(DispatcherTimer),
        typeof(OverlayScrollChrome));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    public static bool GetActive(DependencyObject value) => (bool)value.GetValue(ActiveProperty);
    private static void OnEnabled(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is not ScrollViewer scroll) return;
        scroll.ScrollChanged -= Changed; scroll.Unloaded -= Unloaded;
        Stop(scroll);
        if ((bool)e.NewValue) { scroll.ScrollChanged += Changed; scroll.Unloaded += Unloaded; }
    }
    private static void Changed(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || !ReferenceEquals(e.OriginalSource, scroll) || e.VerticalChange == 0) return;
        if (scroll.GetValue(TimerProperty) is not DispatcherTimer timer)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, scroll.Dispatcher) { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) => Stop(scroll);
            scroll.SetValue(TimerProperty, timer);
        }
        scroll.SetValue(ActiveKey, true);
        timer.Stop(); timer.Start();
    }
    private static void Unloaded(object sender, RoutedEventArgs e) => Stop((ScrollViewer)sender);
    private static void Stop(ScrollViewer scroll)
    {
        (scroll.GetValue(TimerProperty) as DispatcherTimer)?.Stop();
        scroll.SetValue(ActiveKey, false);
    }
}

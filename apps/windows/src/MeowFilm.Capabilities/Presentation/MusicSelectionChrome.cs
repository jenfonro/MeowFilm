using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

/// <summary>Projects a login mode's visibility tag; it does not select or change an account method.</summary>
public static class MusicSelectionChrome
{
    public static readonly DependencyProperty ObserveTagProperty = DependencyProperty.RegisterAttached("ObserveTag", typeof(bool), typeof(MusicSelectionChrome), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty SubscriptionProperty = DependencyProperty.RegisterAttached("Subscription", typeof(long), typeof(MusicSelectionChrome), new PropertyMetadata(0L));
    public static bool GetObserveTag(DependencyObject value) => (bool)value.GetValue(ObserveTagProperty);
    public static void SetObserveTag(DependencyObject value, bool enabled) => value.SetValue(ObserveTagProperty, enabled);
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not Button button) return;
        if ((bool)args.NewValue)
        {
            button.SetValue(SubscriptionProperty, button.RegisterPropertyChangedCallback(FrameworkElement.TagProperty, (_, _) => Refresh(button)));
            Refresh(button);
        }
        else
        {
            button.UnregisterPropertyChangedCallback(FrameworkElement.TagProperty, (long)button.GetValue(SubscriptionProperty));
            button.ClearValue(Control.BackgroundProperty);
        }
    }
    private static void Refresh(Button button)
    {
        string key = button.Tag is Visibility.Visible ? "MeowFilm.Brush.Surface" : "MeowFilm.Brush.InputSurface";
        for (DependencyObject? node = button; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement element && element.Resources.TryGetValue(key, out var resource))
            { button.Background = (Microsoft.UI.Xaml.Media.Brush)resource; return; }
        if (Application.Current.Resources.TryGetValue(key, out var fallback)) button.Background = (Microsoft.UI.Xaml.Media.Brush)fallback;
    }
}

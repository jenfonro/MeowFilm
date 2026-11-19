using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

/// <summary>WinUI's native overlay scrollbars own pointer, touch, keyboard, animation and contrast.</summary>
public static class OverlayScrollChrome
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(OverlayScrollChrome), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is ScrollViewer scroll) scroll.VerticalScrollBarVisibility = (bool)args.NewValue ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
}

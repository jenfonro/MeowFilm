using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace MeowFilm.Desktop;

public static class DesktopTheme
{
    public static readonly DependencyProperty IsHighContrastProperty = DependencyProperty.RegisterAttached(
        "IsHighContrast", typeof(bool), typeof(DesktopTheme), new PropertyMetadata(false));
    public static bool GetIsHighContrast(DependencyObject element)
    {
        if (new AccessibilitySettings().HighContrast) return true;
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node.ReadLocalValue(IsHighContrastProperty) is bool value) return value;
        return false;
    }
    public static void SetIsHighContrast(DependencyObject element, bool value) => element.SetValue(IsHighContrastProperty, value);

    public static IDisposable Observe(FrameworkElement element, Action changed) => new ContrastObservation(element, changed);
    private sealed class ContrastObservation : IDisposable
    {
        private readonly FrameworkElement element;
        private readonly Action changed;
        private readonly List<(DependencyObject Owner, long Token)> ancestors = [];
        internal ContrastObservation(FrameworkElement element, Action changed)
        {
            this.element = element; this.changed = changed;
            element.Loaded += Loaded; element.Unloaded += Unloaded; element.ActualThemeChanged += ThemeChanged;
            if (element.IsLoaded) Attach();
        }
        private void Loaded(object sender, RoutedEventArgs args) => Attach();
        private void Unloaded(object sender, RoutedEventArgs args) => Detach();
        private void ThemeChanged(FrameworkElement sender, object args) => changed();
        private void Attach()
        {
            Detach();
            for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
                ancestors.Add((node, node.RegisterPropertyChangedCallback(IsHighContrastProperty, (_, _) => changed())));
            changed();
        }
        private void Detach()
        {
            foreach (var (owner, token) in ancestors) owner.UnregisterPropertyChangedCallback(IsHighContrastProperty, token);
            ancestors.Clear();
        }
        public void Dispose()
        {
            Detach(); element.Loaded -= Loaded; element.Unloaded -= Unloaded; element.ActualThemeChanged -= ThemeChanged;
        }
    }
}

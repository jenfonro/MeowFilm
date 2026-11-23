using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

public static class DesktopTheme
{
    public static readonly DependencyProperty IsHighContrastProperty = DependencyProperty.RegisterAttached(
        "IsHighContrast", typeof(bool), typeof(DesktopTheme), new PropertyMetadata(false));
    public static bool GetIsHighContrast(DependencyObject element)
    {
        if (SystemAppearance.IsHighContrast) return true;
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
        private bool disposed, loaded;
        internal ContrastObservation(FrameworkElement element, Action changed)
        {
            this.element = element; this.changed = changed;
            // Subscribing to Loaded on an already loaded WinUI element resets
            // IsLoaded without guaranteeing a new notification.
            loaded = element.IsLoaded;
            element.Unloaded += Unloaded; element.ActualThemeChanged += ThemeChanged;
            if (loaded) Attach();
            else element.Loaded += Loaded;
        }
        private void Loaded(object sender, RoutedEventArgs args)
        {
            element.Loaded -= Loaded; loaded = true; Attach();
        }
        private void Unloaded(object sender, RoutedEventArgs args)
        {
            if (element.IsLoaded) { loaded = true; Attach(); return; }
            loaded = false; Detach(); element.Loaded -= Loaded; element.Loaded += Loaded;
        }
        private void ThemeChanged(FrameworkElement sender, object args)
        {
            if (!disposed) changed();
        }
        private void Attach()
        {
            if (disposed) return;
            Detach();
            for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
                ancestors.Add((node, node.RegisterPropertyChangedCallback(IsHighContrastProperty, (_, _) => { if (!disposed) changed(); })));
            changed();
        }
        private void Detach()
        {
            foreach (var (owner, token) in ancestors) owner.UnregisterPropertyChangedCallback(IsHighContrastProperty, token);
            ancestors.Clear();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Detach(); element.Loaded -= Loaded; element.Unloaded -= Unloaded; element.ActualThemeChanged -= ThemeChanged;
        }
    }
}

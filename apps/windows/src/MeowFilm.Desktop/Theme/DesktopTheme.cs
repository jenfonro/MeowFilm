using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

public static class DesktopTheme
{
    public static readonly DependencyProperty IsHighContrastProperty = DependencyProperty.RegisterAttached(
        "IsHighContrast", typeof(bool), typeof(DesktopTheme), new PropertyMetadata(false));
    private static int activeOverrides;
    private static readonly List<ContrastObservation> Observations = [];

    public static bool GetIsHighContrast(DependencyObject element)
    {
        if (SystemAppearance.IsHighContrast) return true;
        if (Volatile.Read(ref activeOverrides) == 0) return false;
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node.ReadLocalValue(IsHighContrastProperty) is bool value) return value;
        return false;
    }
    public static void SetIsHighContrast(DependencyObject element, bool value)
    {
        var previous = element.ReadLocalValue(IsHighContrastProperty);
        if (previous is bool existing && existing == value) return;
        bool hadTrue = previous is true;
        if (value && !hadTrue) Interlocked.Increment(ref activeOverrides);
        else if (!value && hadTrue) Interlocked.Decrement(ref activeOverrides);
        element.SetValue(IsHighContrastProperty, value);
        for (int i = Observations.Count - 1; i >= 0; i--)
            if (i < Observations.Count) Observations[i].OnHighContrastChanged(element);
    }

    public static IDisposable Observe(FrameworkElement element, Action changed) => new ContrastObservation(element, changed);
    private sealed class ContrastObservation : IDisposable
    {
        private readonly FrameworkElement element;
        private readonly Action changed;
        private int index = -1;
        private bool disposed, loaded;
        internal ContrastObservation(FrameworkElement element, Action changed)
        {
            this.element = element; this.changed = changed;
            loaded = element.IsLoaded;
            element.Unloaded += Unloaded; element.ActualThemeChanged += ThemeChanged;
            if (loaded) Attach();
            else element.Loaded += Loaded;
        }
        internal void OnHighContrastChanged(DependencyObject owner)
        {
            if (!disposed && loaded && DesktopVisual.Contains(owner, element)) changed();
        }
        private void Loaded(object sender, RoutedEventArgs args) { element.Loaded -= Loaded; loaded = true; Attach(); }
        private void Unloaded(object sender, RoutedEventArgs args)
        {
            if (element.IsLoaded) { loaded = true; Attach(); return; }
            loaded = false; Detach(); element.Loaded -= Loaded; element.Loaded += Loaded;
        }
        private void ThemeChanged(FrameworkElement sender, object args) { if (!disposed) changed(); }
        private void Attach()
        {
            if (disposed) return;
            if (index < 0) { index = Observations.Count; Observations.Add(this); }
            changed();
        }
        private void Detach()
        {
            if (index < 0) return;
            int last = Observations.Count - 1;
            if (index != last) { var moved = Observations[last]; Observations[index] = moved; moved.index = index; }
            Observations.RemoveAt(last);
            index = -1;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Detach(); element.Loaded -= Loaded; element.Unloaded -= Unloaded; element.ActualThemeChanged -= ThemeChanged;
        }
    }
}

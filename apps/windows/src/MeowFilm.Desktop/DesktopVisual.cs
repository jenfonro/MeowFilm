using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace MeowFilm.Desktop;

/// <summary>Tree-local presentation helpers; no view registry or business service lookup.</summary>
public static class DesktopVisual
{
    public static bool Contains(DependencyObject ancestor, DependencyObject? child)
    {
        for (var node = child; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }
    public static bool IsVisible(UIElement element)
    {
        if (element is FrameworkElement { IsLoaded: false }) return false;
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
        return element.XamlRoot?.IsHostVisible == true;
    }
    public static IDisposable ObserveVisibility(FrameworkElement element, Action<bool> changed) =>
        new VisibilityObservation(element, changed);

    private sealed class VisibilityObservation : IDisposable
    {
        private readonly FrameworkElement element;
        private readonly Action<bool> changed;
        private readonly List<(DependencyObject Owner, long Token)> ancestors = [];
        private XamlRoot? root;
        private bool? visible;
        private bool disposed;

        internal VisibilityObservation(FrameworkElement element, Action<bool> changed)
        {
            this.element = element; this.changed = changed;
            element.Loaded += Loaded; element.Unloaded += Unloaded;
            if (element.IsLoaded) Attach();
            else Publish(false);
        }
        private void Loaded(object sender, RoutedEventArgs args) => Attach();
        private void Unloaded(object sender, RoutedEventArgs args) { Detach(); Publish(false); }
        private void Attach()
        {
            if (disposed) return;
            Detach();
            for (DependencyObject? owner = element; owner is not null; owner = VisualTreeHelper.GetParent(owner))
                if (owner is UIElement)
                    ancestors.Add((owner, owner.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Refresh())));
            root = element.XamlRoot;
            if (root is not null) root.Changed += RootChanged;
            Refresh();
        }
        private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Refresh();
        private void Refresh() { if (!disposed) Publish(element.IsLoaded && IsVisible(element)); }
        private void Publish(bool current)
        {
            if (disposed || visible == current) return;
            visible = current; changed(current);
        }
        private void Detach()
        {
            foreach (var (owner, token) in ancestors) owner.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);
            ancestors.Clear();
            if (root is not null) root.Changed -= RootChanged;
            root = null;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Detach();
            element.Loaded -= Loaded; element.Unloaded -= Unloaded;
        }
    }
    public static DependencyObject? Focused(FrameworkElement root) => root.XamlRoot is { } xamlRoot
        ? FocusManager.GetFocusedElement(xamlRoot) as DependencyObject : null;
    public static bool ContainsFocus(FrameworkElement root) => Contains(root, Focused(root));
    public static void FocusFirst(FrameworkElement root)
    {
        if (FocusManager.FindFirstFocusableElement(root) is UIElement element) element.Focus(FocusState.Programmatic);
    }
    public static VirtualKeyModifiers Modifiers
    {
        get
        {
            VirtualKeyModifiers value = VirtualKeyModifiers.None;
            if (Down(VirtualKey.Control)) value |= VirtualKeyModifiers.Control;
            if (Down(VirtualKey.Menu)) value |= VirtualKeyModifiers.Menu;
            if (Down(VirtualKey.Shift)) value |= VirtualKeyModifiers.Shift;
            if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) value |= VirtualKeyModifiers.Windows;
            return value;
        }
    }
    private static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    public static object Resource(FrameworkElement view, string key) =>
        FindResource(view, key) ?? throw new KeyNotFoundException(key);
    private static object? FindResource(FrameworkElement view, string key)
    {
        bool contrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        for (DependencyObject? node = view; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement element && Find(element.Resources, key, contrast) is { } value) return value;
        if (view.XamlRoot?.Content is FrameworkElement root && Find(root.Resources, key, contrast) is { } rooted) return rooted;
        return Find(Application.Current.Resources, key, contrast);
    }
    private static object? Find(ResourceDictionary resources, string key, bool contrast)
    {
        if (contrast && resources.ThemeDictionaries.TryGetValue("HighContrast", out var theme) &&
            theme is ResourceDictionary highContrast && highContrast.TryGetValue(key, out var contrasted)) return contrasted;
        if (contrast)
            for (int index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
                if (resources.MergedDictionaries[index].ThemeDictionaries.TryGetValue("HighContrast", out var mergedTheme) &&
                    mergedTheme is ResourceDictionary merged && merged.TryGetValue(key, out var mergedValue)) return mergedValue;
        return resources.TryGetValue(key, out var value) ? value : null;
    }
    public static void UseResource(this FrameworkElement view, DependencyProperty property, string key)
    {
        // A code-created child acquires its page's resource scope when it is mounted.
        if (view.IsLoaded) view.SetValue(property, Resource(view, key));
        else if (FindResource(view, key) is { } value) view.SetValue(property, value);
        view.Loaded += (_, _) => view.SetValue(property, Resource(view, key));
        view.ActualThemeChanged += (_, _) => view.SetValue(property, Resource(view, key));
    }
}

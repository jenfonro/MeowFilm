using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace MeowFilm.Desktop;

/// <summary>Tree-local presentation helpers; no view registry or business service lookup.</summary>
public static class DesktopVisual
{
    public static bool Animate => SystemAppearance.AnimationsEnabled && !SystemAppearance.IsHighContrast;
    public static bool CanAnimate(DependencyObject view) =>
        Animate && !DesktopTheme.GetIsHighContrast(view);

    [ThreadStatic] private static SolidColorBrush? transparentBrush;
    [ThreadStatic] private static SolidColorBrush? whiteBrush;
    public static SolidColorBrush TransparentBrush => transparentBrush ??= new(Microsoft.UI.Colors.Transparent);
    public static SolidColorBrush WhiteBrush => whiteBrush ??= new(Microsoft.UI.Colors.White);

    public static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255) => new(Color.FromArgb(a, r, g, b));
    public static SolidColorBrush Brush(string value) => new(SystemAppearance.ParseColor(value));
    public static Geometry Geometry(string value) => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), value);

    public static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    public static Storyboard AnimateDouble(DependencyObject target, string property, double from, double to,
        TimeSpan duration, Action? completed = null, bool forever = false)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = forever ? null : new CubicEase { EasingMode = EasingMode.EaseOut },
            RepeatBehavior = forever ? RepeatBehavior.Forever : new RepeatBehavior(1)
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (completed is not null) storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
        return storyboard;
    }

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
        private bool disposed, loaded;

        internal VisibilityObservation(FrameworkElement element, Action<bool> changed)
        {
            this.element = element; this.changed = changed;
            // Subscribing to Loaded on an already loaded WinUI element resets
            // IsLoaded without guaranteeing a new notification.
            loaded = element.IsLoaded;
            element.Unloaded += Unloaded;
            if (loaded) Attach();
            else { element.Loaded += Loaded; Publish(false); }
        }
        private void Loaded(object sender, RoutedEventArgs args)
        {
            element.Loaded -= Loaded; loaded = true; Attach();
        }
        private void Unloaded(object sender, RoutedEventArgs args)
        {
            if (element.IsLoaded) { loaded = true; Attach(); return; }
            loaded = false; Detach(); element.Loaded -= Loaded; element.Loaded += Loaded; Publish(false);
        }
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
        private void Refresh() { if (!disposed) Publish(loaded && IsVisible(element)); }
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
    public static Style? TryAppStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as Style : null;
    public static object Resource(FrameworkElement view, string key) =>
        FindResource(view, key) ?? throw new KeyNotFoundException(key);
    private static object? FindResource(FrameworkElement view, string key)
    {
        bool contrast = SystemAppearance.IsHighContrast;
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
}

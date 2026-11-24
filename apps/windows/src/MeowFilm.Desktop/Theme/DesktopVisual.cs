using System.Runtime.CompilerServices;
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
    private static readonly ConditionalWeakTable<UIElement, VisNode> VisNodes = new();
    private static readonly ConditionalWeakTable<XamlRoot, RootWatch> RootWatches = new();
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

    private sealed class VisNode(UIElement element)
    {
        internal readonly UIElement Element = element;
        internal readonly List<VisNode> Children = [];
        internal readonly List<Action<bool>> Leaves = [];
        internal VisNode? Parent;
        internal RootWatch? Root;
        internal long Token;
        internal bool SelfVisible, EffectiveVisible, Attached;

        internal static VisNode Ensure(UIElement target)
        {
            UIElement? parentUi = null;
            for (var p = VisualTreeHelper.GetParent(target); p is not null; p = VisualTreeHelper.GetParent(p))
                if (p is UIElement ui) { parentUi = ui; break; }
            var parent = parentUi is not null ? Ensure(parentUi) : null;
            if (!VisNodes.TryGetValue(target, out var node)) VisNodes.Add(target, node = new VisNode(target));
            if (!node.Attached || !ReferenceEquals(node.Parent, parent)) node.Bind(parent);
            return node;
        }

        private void Bind(VisNode? nextParent)
        {
            Parent?.Children.Remove(this);
            Root?.Remove(this); Root = null; Parent = nextParent;
            SelfVisible = Element.Visibility == Visibility.Visible;
            if (!Attached) { Attached = true; Token = Element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Refresh()); }
            if (nextParent is not null) nextParent.Children.Add(this);
            else if (Element.XamlRoot is { } xr) (Root = RootWatch.For(xr)).Nodes.Add(this);
            Propagate(SelfVisible && (nextParent is not null ? nextParent.EffectiveVisible : Element.XamlRoot?.IsHostVisible == true));
        }

        internal void Refresh()
        {
            if (!Attached) return;
            SelfVisible = Element.Visibility == Visibility.Visible;
            Propagate(SelfVisible && (Parent is not null ? Parent.EffectiveVisible : Element.XamlRoot?.IsHostVisible == true));
        }

        private void Propagate(bool next)
        {
            if (EffectiveVisible == next) return;
            EffectiveVisible = next;
            if (Leaves.Count == 1) Leaves[0](next);
            else if (Leaves.Count > 1) foreach (var cb in Leaves.ToArray()) if (Leaves.Contains(cb)) cb(next);
            if (Children.Count == 1) { if (Children[0].SelfVisible) Children[0].Propagate(next); }
            else if (Children.Count > 1) foreach (var ch in Children.ToArray()) if (ch.Attached && ch.SelfVisible) ch.Propagate(next);
        }

        internal void Prune()
        {
            for (var cur = this; cur is { Attached: true, Leaves.Count: 0, Children.Count: 0 };)
            {
                cur.Attached = false;
                cur.Element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, cur.Token);
                cur.Root?.Remove(cur); cur.Root = null;
                VisNodes.Remove(cur.Element);
                var p = cur.Parent; cur.Parent = null; p?.Children.Remove(cur); cur = p;
            }
        }
    }

    private sealed class RootWatch
    {
        private readonly XamlRoot root;
        internal readonly List<VisNode> Nodes = [];
        private RootWatch(XamlRoot r) { root = r; root.Changed += OnChanged; }
        internal static RootWatch For(XamlRoot r) => RootWatches.GetValue(r, static k => new RootWatch(k));
        private void OnChanged(XamlRoot s, XamlRootChangedEventArgs e) { foreach (var n in Nodes.ToArray()) if (n.Attached && n.Parent is null) n.Refresh(); }
        internal void Remove(VisNode n) { if (Nodes.Remove(n) && Nodes.Count == 0) { root.Changed -= OnChanged; RootWatches.Remove(root); } }
    }

    private sealed class VisibilityObservation : IDisposable
    {
        private readonly FrameworkElement element;
        private readonly Action<bool> changed, onTree;
        private VisNode? node;
        private bool? visible;
        private bool disposed, loaded;

        internal VisibilityObservation(FrameworkElement element, Action<bool> changed)
        {
            this.element = element; this.changed = changed;
            onTree = v => { if (!disposed) Publish(loaded && v); };
            loaded = element.IsLoaded;
            element.Unloaded += Unloaded;
            if (loaded) Attach();
            else { element.Loaded += Loaded; Publish(false); }
        }
        private void Loaded(object s, RoutedEventArgs e) { element.Loaded -= Loaded; loaded = true; Attach(); }
        private void Unloaded(object s, RoutedEventArgs e)
        {
            if (element.IsLoaded) { loaded = true; Attach(); return; }
            loaded = false; Detach(); element.Loaded -= Loaded; element.Loaded += Loaded; Publish(false);
        }
        private void Attach()
        {
            if (disposed) return;
            Detach();
            node = VisNode.Ensure(element);
            node.Leaves.Add(onTree);
            Publish(loaded && node.EffectiveVisible);
        }
        private void Publish(bool current) { if (!disposed && visible != current) { visible = current; changed(current); } }
        private void Detach() { if (node is { } n) { node = null; n.Leaves.Remove(onTree); n.Prune(); } }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Detach(); element.Loaded -= Loaded; element.Unloaded -= Unloaded;
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

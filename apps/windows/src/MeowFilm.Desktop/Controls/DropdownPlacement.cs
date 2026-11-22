using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Desktop;

/// <summary>
/// Unifies dropdown placement, surface chrome, and active-dropdown tracking across all WinUI ComboBox
/// and ShellSearchScope consumers: opens cleanly below the trigger field and aligns width.
/// </summary>
public static class DropdownPlacement
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(DropdownPlacement),
        new PropertyMetadata(false, OnEnabledChanged));

    private static WeakReference<ComboBox>? activeComboBox;
    private static WeakReference<ShellSearchScope>? activeSearchScope;

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    internal static void NotifySearchScopeOpened(ShellSearchScope scope) =>
        activeSearchScope = new WeakReference<ShellSearchScope>(scope);

    internal static void NotifySearchScopeClosed(ShellSearchScope scope)
    {
        if (activeSearchScope is not null && activeSearchScope.TryGetTarget(out var current) && ReferenceEquals(current, scope))
            activeSearchScope = null;
    }

    internal static ComboBox? ActiveComboBox =>
        activeComboBox is not null && activeComboBox.TryGetTarget(out var combo) && combo.IsDropDownOpen ? combo : null;

    internal static ShellSearchScope? ActiveSearchScope =>
        activeSearchScope is not null && activeSearchScope.TryGetTarget(out var scope) &&
        scope.IsLoaded && scope.IsEnabled && DesktopVisual.IsVisible(scope) && scope.IsDropDownOpen ? scope : null;

    internal static object? DescribeActiveDropdown(FrameworkElement root)
    {
        var combo = ActiveComboBox ?? FindOpenComboBox(root);
        if (combo is not null)
            return DescribeComboBox(combo, root);

        if (ActiveSearchScope is { } scope)
            return DescribeSearchScope(scope, root);

        return null;
    }

    internal static object DescribeSearchScope(ShellSearchScope scope, FrameworkElement root)
    {
        var triggerBounds = TryMeasureBounds(scope, root);
        object? popupBounds = null;
        if (scope.IsDropDownOpen && scope.XamlRoot is { } xamlRoot)
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (popup.Child is FrameworkElement child && child.ActualWidth > 0 && child.ActualHeight > 0)
                {
                    try
                    {
                        var origin = child.TransformToVisual(root).TransformPoint(new Point(0, 0));
                        popupBounds = new
                        {
                            x = Math.Round(origin.X, 1),
                            y = Math.Round(origin.Y, 1),
                            w = Math.Round(child.ActualWidth, 1),
                            h = Math.Round(child.ActualHeight, 1)
                        };
                        break;
                    }
                    catch { }
                }
            }
        }
        return new
        {
            open = scope.IsDropDownOpen,
            kind = "search-scope",
            name = string.IsNullOrWhiteSpace(scope.Name) ? null : scope.Name,
            automationName = NullIfEmpty(AutomationProperties.GetName(scope)),
            selectedValue = scope.SelectedValue,
            selectedLabel = scope.SelectedItem?.Label,
            selectedIndex = scope.ItemsSource.ToList().FindIndex(item => string.Equals(item.Id, scope.SelectedValue, StringComparison.Ordinal)),
            options = scope.ItemsSource.Select((item, index) => new
            {
                index,
                id = item.Id,
                label = item.Label,
                selected = string.Equals(item.Id, scope.SelectedValue, StringComparison.Ordinal)
            }).ToArray(),
            triggerBounds,
            popupBounds
        };
    }

    internal static object DescribeComboBox(ComboBox combo, FrameworkElement root)
    {
        var triggerBounds = TryMeasureBounds(combo, root);
        object? popupBounds = null;
        if (combo.IsDropDownOpen && FindPopup(combo) is { Child: FrameworkElement child } popup)
        {
            double width = child.ActualWidth > 0 ? child.ActualWidth : child.Width;
            double height = child.ActualHeight > 0 ? child.ActualHeight : child.Height;
            try
            {
                var origin = combo.TransformToVisual(root).TransformPoint(new Point(popup.HorizontalOffset, popup.VerticalOffset));
                popupBounds = new
                {
                    x = Math.Round(origin.X, 1),
                    y = Math.Round(origin.Y, 1),
                    w = Math.Round(double.IsNaN(width) ? 0 : width, 1),
                    h = Math.Round(double.IsNaN(height) ? 0 : height, 1)
                };
            }
            catch { }
        }
        var options = new List<object>(combo.Items.Count);
        for (int i = 0; i < combo.Items.Count; i++)
        {
            var item = combo.Items[i];
            options.Add(new
            {
                index = i,
                label = DescribeItemLabel(combo, item),
                value = DescribeItemValue(combo, item),
                selected = i == combo.SelectedIndex
            });
        }
        return new
        {
            open = combo.IsDropDownOpen,
            kind = combo is EqualWidthComboBox ? "equal-width-combobox" : "combobox",
            name = string.IsNullOrWhiteSpace(combo.Name) ? null : combo.Name,
            automationName = NullIfEmpty(AutomationProperties.GetName(combo)),
            selectedIndex = combo.SelectedIndex,
            selectedValue = DescribeItemValue(combo, combo.SelectedItem),
            selectedLabel = DescribeItemLabel(combo, combo.SelectedItem),
            options = options.ToArray(),
            triggerBounds,
            popupBounds
        };
    }

    internal static string? DescribeItemLabel(ComboBox combo, object? item)
    {
        if (item is null) return null;
        if (item is string text) return text;
        if (item is ComboBoxItem cbi) return cbi.Content?.ToString();
        if (!string.IsNullOrWhiteSpace(combo.DisplayMemberPath))
        {
            var prop = item.GetType().GetProperty(combo.DisplayMemberPath);
            if (prop?.GetValue(item)?.ToString() is { } display) return display;
        }
        foreach (string candidate in new[] { "Label", "Title", "Name", "Text", "DisplayName" })
        {
            var prop = item.GetType().GetProperty(candidate);
            if (prop?.GetValue(item)?.ToString() is { Length: > 0 } value) return value;
        }
        return item.ToString();
    }

    internal static string? DescribeItemValue(ComboBox combo, object? item)
    {
        if (item is null) return null;
        if (item is string text) return text;
        if (item is ComboBoxItem cbi) return cbi.Tag?.ToString() ?? cbi.Content?.ToString();
        if (!string.IsNullOrWhiteSpace(combo.SelectedValuePath))
        {
            var prop = item.GetType().GetProperty(combo.SelectedValuePath);
            if (prop?.GetValue(item)?.ToString() is { } val) return val;
        }
        foreach (string candidate in new[] { "Id", "Key", "Value", "Code" })
        {
            var prop = item.GetType().GetProperty(candidate);
            if (prop?.GetValue(item)?.ToString() is { Length: > 0 } value) return value;
        }
        return DescribeItemLabel(combo, item);
    }

    private static ComboBox? FindOpenComboBox(DependencyObject node)
    {
        if (node is ComboBox { IsDropDownOpen: true } combo && DesktopVisual.IsVisible(combo))
            return combo;
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is UIElement { Visibility: Visibility.Collapsed }) continue;
            if (FindOpenComboBox(child) is { } found) return found;
        }
        return null;
    }

    private static object? TryMeasureBounds(FrameworkElement element, FrameworkElement root)
    {
        if (!element.IsLoaded) return null;
        try
        {
            var pt = element.TransformToVisual(root).TransformPoint(new Point(0, 0));
            return new
            {
                x = Math.Round(pt.X, 1),
                y = Math.Round(pt.Y, 1),
                w = Math.Round(element.ActualWidth, 1),
                h = Math.Round(element.ActualHeight, 1)
            };
        }
        catch { return null; }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ComboBox combo) return;
        if ((bool)args.NewValue)
        {
            combo.Loaded -= OnLoaded;
            combo.Loaded += OnLoaded;
            combo.DropDownOpened -= OnDropDownOpened;
            combo.DropDownOpened += OnDropDownOpened;
            combo.DropDownClosed -= OnDropDownClosed;
            combo.DropDownClosed += OnDropDownClosed;
            combo.SizeChanged -= OnSizeChanged;
            combo.SizeChanged += OnSizeChanged;
            combo.Unloaded -= OnUnloaded;
            combo.Unloaded += OnUnloaded;
            if (combo.IsLoaded) ConfigurePopupBeforeOpen(combo);
        }
        else
        {
            combo.Loaded -= OnLoaded;
            combo.DropDownOpened -= OnDropDownOpened;
            combo.DropDownClosed -= OnDropDownClosed;
            combo.SizeChanged -= OnSizeChanged;
            combo.Unloaded -= OnUnloaded;
            ClearActiveCombo(combo);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ComboBox combo) ConfigurePopupBeforeOpen(combo);
    }

    private static void ConfigurePopupBeforeOpen(ComboBox combo)
    {
        if (FindPopup(combo) is { IsOpen: false } popup)
            popup.ShouldConstrainToRootBounds = true;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is ComboBox combo) ClearActiveCombo(combo);
    }

    private static void OnDropDownClosed(object? sender, object args)
    {
        if (sender is ComboBox combo) ClearActiveCombo(combo);
    }

    private static void ClearActiveCombo(ComboBox combo)
    {
        if (activeComboBox is not null && activeComboBox.TryGetTarget(out var current) && ReferenceEquals(current, combo))
            activeComboBox = null;
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is ComboBox { IsDropDownOpen: true } combo)
            PositionPopup(combo);
    }

    private static void OnDropDownOpened(object? sender, object args)
    {
        if (sender is not ComboBox combo) return;
        activeComboBox = new WeakReference<ComboBox>(combo);
        var popup = FindPopup(combo);
        if (popup is not null)
        {
            void Reposition(object? _, object __) { if (combo.IsDropDownOpen) PositionPopup(combo); }
            void ChildSized(object _, SizeChangedEventArgs __) { if (combo.IsDropDownOpen) PositionPopup(combo); }
            void Cleanup(object? _, object __)
            {
                popup.Closed -= Cleanup;
                combo.DropDownClosed -= Cleanup;
                popup.LayoutUpdated -= Reposition;
                if (popup.Child is FrameworkElement child) child.SizeChanged -= ChildSized;
            }
            popup.Closed += Cleanup;
            combo.DropDownClosed += Cleanup;
            popup.LayoutUpdated += Reposition;
            if (popup.Child is FrameworkElement menuChild) menuChild.SizeChanged += ChildSized;
        }
        PositionPopup(combo);
        combo.DispatcherQueue?.TryEnqueue(() =>
        {
            if (combo.IsDropDownOpen) PositionPopup(combo);
        });
    }

    private static void PositionPopup(ComboBox combo)
    {
        var popup = FindPopup(combo);
        if (popup is null || combo.ActualWidth <= 0 || combo.ActualHeight <= 0) return;

        if (!popup.IsOpen) popup.ShouldConstrainToRootBounds = true;
        if (popup.Child is FrameworkElement menu)
        {
            menu.MinWidth = combo.ActualWidth;
            if (combo is EqualWidthComboBox)
                menu.MaxWidth = menu.Width = combo.ActualWidth;
            if (menu is Border border)
            {
                if (DesktopVisual.Resource(combo, "MeowFilm.Brush.FloatingSurface") is Brush surfaceBrush)
                    border.Background = surfaceBrush;
                if (DesktopVisual.Resource(combo, "MeowFilm.Brush.Border") is Brush borderBrush)
                    border.BorderBrush = borderBrush;
                border.CornerRadius = new CornerRadius(8);
            }
            // WinUI ComboBox shifts PopupBorder via RenderTransform / Translation to align selected item over trigger.
            if (menu.RenderTransform is TranslateTransform transform && transform.Y != 0)
                transform.Y = 0;
            else if (menu.RenderTransform is CompositeTransform composite && composite.TranslateY != 0)
                composite.TranslateY = 0;
            if (menu.Translation.Y != 0)
                menu.Translation = new Vector3(menu.Translation.X, 0, menu.Translation.Z);
        }

        double gap = 4;
        double belowOffset = combo.ActualHeight + gap;
        double verticalOffset = belowOffset;

        if (combo.XamlRoot is { } root && popup.Child is FrameworkElement content)
        {
            double menuHeight = content.ActualHeight > 0 ? content.ActualHeight : Math.Min(combo.MaxDropDownHeight, 260);
            var origin = combo.TransformToVisual(root.Content).TransformPoint(new Point(0, 0));
            double availableBelow = root.Size.Height - (origin.Y + combo.ActualHeight);
            if (availableBelow < Math.Min(menuHeight + gap, 140) && origin.Y > availableBelow)
                verticalOffset = -(menuHeight + gap);
        }

        if (Math.Abs(popup.HorizontalOffset) > 0.5) popup.HorizontalOffset = 0;
        if (Math.Abs(popup.VerticalOffset - verticalOffset) > 0.5) popup.VerticalOffset = verticalOffset;
    }

    internal static Popup? FindPopup(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Popup popup) return popup;
            if (FindPopup(child) is { } nested) return nested;
        }
        return null;
    }
}

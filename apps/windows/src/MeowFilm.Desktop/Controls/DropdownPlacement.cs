using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Desktop;

/// <summary>
/// Unifies dropdown placement and surface chrome across all WinUI ComboBox consumers:
/// opens cleanly below the trigger field, aligns width, and matches parent surface tokens.
/// </summary>
public static class DropdownPlacement
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(DropdownPlacement),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ComboBox combo) return;
        if ((bool)args.NewValue)
        {
            combo.DropDownOpened -= OnDropDownOpened;
            combo.DropDownOpened += OnDropDownOpened;
            combo.SizeChanged -= OnSizeChanged;
            combo.SizeChanged += OnSizeChanged;
        }
        else
        {
            combo.DropDownOpened -= OnDropDownOpened;
            combo.SizeChanged -= OnSizeChanged;
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is ComboBox { IsDropDownOpen: true } combo)
            PositionPopup(combo);
    }

    private static void OnDropDownOpened(object? sender, object args)
    {
        if (sender is not ComboBox combo) return;
        var popup = FindPopup(combo);
        if (popup is not null)
        {
            void Reposition(object? _, object __) { if (combo.IsDropDownOpen) PositionPopup(combo); }
            void ChildSized(object _, SizeChangedEventArgs __) { if (combo.IsDropDownOpen) PositionPopup(combo); }
            void Cleanup(object? _, object __)
            {
                popup.Closed -= Cleanup;
                popup.LayoutUpdated -= Reposition;
                if (popup.Child is FrameworkElement child) child.SizeChanged -= ChildSized;
            }
            popup.Closed -= Cleanup;
            popup.Closed += Cleanup;
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

    private static Popup? FindPopup(DependencyObject root)
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

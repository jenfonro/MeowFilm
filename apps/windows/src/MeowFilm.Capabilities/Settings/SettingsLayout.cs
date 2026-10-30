using System.Windows;

namespace MeowFilm.Capabilities;

public static class SettingsLayout
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(SettingsLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty IsNarrowProperty = DependencyProperty.RegisterAttached(
        "IsNarrow", typeof(bool), typeof(SettingsLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.RegisterAttached(
        "IsStacked", typeof(bool), typeof(SettingsLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);
    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);
    public static bool GetIsNarrow(DependencyObject element) => (bool)element.GetValue(IsNarrowProperty);
    public static void SetIsNarrow(DependencyObject element, bool value) => element.SetValue(IsNarrowProperty, value);
    public static bool GetIsStacked(DependencyObject element) => (bool)element.GetValue(IsStackedProperty);
    public static void SetIsStacked(DependencyObject element, bool value) => element.SetValue(IsStackedProperty, value);
}

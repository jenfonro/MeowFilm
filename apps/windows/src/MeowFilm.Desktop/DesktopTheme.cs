using System.Windows;

namespace MeowFilm.Desktop;

/// <summary>Inherited system accessibility state used by the reusable resource styles.</summary>
public static class DesktopTheme
{
    private static readonly DependencyPropertyKey IsHighContrastPropertyKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "IsHighContrast", typeof(bool), typeof(DesktopTheme),
            new FrameworkPropertyMetadata(SystemParameters.HighContrast,
                FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty IsHighContrastProperty = IsHighContrastPropertyKey.DependencyProperty;

    public static bool GetIsHighContrast(DependencyObject element) =>
        (bool)element.GetValue(IsHighContrastProperty);

    public static void SetIsHighContrast(DependencyObject element, bool value) =>
        element.SetValue(IsHighContrastPropertyKey, value);
}

using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MeowFilm.Desktop;

/// <summary>
/// Process-wide cached access to OS visual settings and fast color parsing.
/// Eliminates repeated WinRT COM allocations and XamlReader calls on UI hot paths.
/// </summary>
public static class SystemAppearance
{
    private static readonly Lazy<UISettings> UiSettings = new(() =>
    {
        var settings = new UISettings();
        settings.ColorValuesChanged += (_, _) => RaiseChanged();
        settings.AnimationsEnabledChanged += (_, _) => RaiseChanged();
        return settings;
    });

    private static readonly Lazy<AccessibilitySettings> Accessibility = new(() =>
    {
        // AccessibilitySettings.HighContrastChanged requires a UWP CoreWindow and throws COMException
        // in unpackaged WinUI 3 apps; UISettings.ColorValuesChanged already notifies on high-contrast changes.
        return new AccessibilitySettings();
    });

    private static event EventHandler? changed;
    public static event EventHandler? Changed
    {
        add
        {
            _ = UiSettings.Value;
            changed += value;
        }
        remove => changed -= value;
    }

    public static bool IsHighContrast
    {
        get
        {
            try { return Accessibility.Value.HighContrast; }
            catch { return false; }
        }
    }

    public static bool AnimationsEnabled
    {
        get
        {
            try { return UiSettings.Value.AnimationsEnabled; }
            catch { return true; }
        }
    }

    public static Color GetElementColor(UIElementType element)
    {
        return UiSettings.Value.UIElementColor(element);
    }

    public static SolidColorBrush GetElementBrush(UIElementType element)
    {
        return new SolidColorBrush(GetElementColor(element));
    }

    public static Color GetColorValue(UIColorType colorType)
    {
        return UiSettings.Value.GetColorValue(colorType);
    }

    public static Color ParseColor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ReadOnlySpan<char> hex = value.AsSpan().Trim();
        if (hex.StartsWith("#")) hex = hex[1..];
        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint argb))
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        throw new FormatException($"Unsupported color format: {value}");
    }

    private static void RaiseChanged() => changed?.Invoke(null, EventArgs.Empty);
}

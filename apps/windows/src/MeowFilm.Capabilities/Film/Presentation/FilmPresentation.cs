using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

public sealed class FilmScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double progress = value is double number ? Math.Clamp(number, 0, 1) : 0;
        double factor = parameter is string text &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        return 1 + progress * factor;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class FilmGridColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && double.IsFinite(width)
            ? Math.Clamp((int)((width + 36) / 212), 1, 8)
            : 1;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class FilmPlayerHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && double.IsFinite(width) ? Math.Max(0, width) * 9 / 16 : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Playback ambience: poster hue and saturation at a fixed, text-safe lightness for the current theme.</summary>
public sealed class FilmAmbientTintConverter : IMultiValueConverter
{
    private const double LightLightness = .935, LightSaturation = .5, LightGain = 3;
    private const double DarkLightness = .125, DarkSaturation = .3, DarkGain = 1.5;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2 || values[0] is not SolidColorBrush poster || values[1] is not SolidColorBrush window)
            return Brushes.Transparent;
        var (hue, saturation, _) = Hsl(poster.Color);
        bool light = Hsl(window.Color).Lightness > .5;
        var brush = new SolidColorBrush(light
            ? Rgb(hue, Math.Min(LightSaturation, saturation * LightGain), LightLightness)
            : Rgb(hue, Math.Min(DarkSaturation, saturation * DarkGain), DarkLightness));
        brush.Freeze();
        return brush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static (double Hue, double Saturation, double Lightness) Hsl(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2, delta = max - min;
        if (delta == 0) return (0, 0, lightness);
        double saturation = lightness <= .5 ? delta / (max + min) : delta / (2 - max - min);
        double hue = max == r ? (g - b) / delta : max == g ? 2 + (b - r) / delta : 4 + (r - g) / delta;
        return ((hue * 60 + 360) % 360, saturation, lightness);
    }

    private static Color Rgb(double hue, double saturation, double lightness)
    {
        double q = lightness <= .5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        double p = 2 * lightness - q, h = hue / 360;
        return Color.FromRgb(Channel(p, q, h + 1d / 3), Channel(p, q, h), Channel(p, q, h - 1d / 3));
    }

    private static byte Channel(double p, double q, double t)
    {
        t = (t % 1 + 1) % 1;
        double value = t < 1d / 6 ? p + (q - p) * 6 * t : t < .5 ? q : t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6 : p;
        return (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }
}

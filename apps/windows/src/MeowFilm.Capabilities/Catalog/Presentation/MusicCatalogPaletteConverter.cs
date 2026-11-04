using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

public enum MusicEntryTitlePaletteRole
{
    Shade,
    Foreground
}

public sealed class MusicCatalogPaletteConverter : IMultiValueConverter
{
    private static readonly Color EntryTint = Color.FromRgb(75, 56, 50);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not SolidColorBrush source ||
            values[1] is not MusicCatalogEntryPresentation presentation) return Brushes.DimGray;
        var color = presentation == MusicCatalogEntryPresentation.Preview
            ? LimitLuminance(source.Color, 170)
            : LimitLuminance(Blend(source.Color, EntryTint, .65), 96);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Color Blend(Color source, Color target, double targetWeight) =>
        Color.FromRgb(
            Channel(source.R * (1 - targetWeight) + target.R * targetWeight),
            Channel(source.G * (1 - targetWeight) + target.G * targetWeight),
            Channel(source.B * (1 - targetWeight) + target.B * targetWeight));

    private static Color LimitLuminance(Color color, double maximum)
    {
        double luminance = .2126 * color.R + .7152 * color.G + .0722 * color.B;
        if (luminance <= maximum) return color;
        double scale = maximum / luminance;
        return Color.FromRgb(Channel(color.R * scale), Channel(color.G * scale), Channel(color.B * scale));
    }

    private static byte Channel(double value) =>
        (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}

public sealed class MusicEntryTitlePaletteConverter : IMultiValueConverter
{
    public MusicEntryTitlePaletteRole Role { get; set; }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var cover = values.Length > 2 && values[2] is SolidColorBrush coverBrush
            ? coverBrush.Color : Colors.DimGray;
        var decorated = values.Length > 2 && values[0] is true &&
            values[1] is SolidColorBrush;
        var overlay = decorated ? ((SolidColorBrush)values[1]).Color : Colors.Transparent;
        var source = decorated ? Composite(overlay, cover) : cover;
        var luminance = .2126 * source.R + .7152 * source.G + .0722 * source.B;
        var light = luminance >= 150;
        if (Role == MusicEntryTitlePaletteRole.Foreground)
        {
            var brush = new SolidColorBrush(light ? Color.FromRgb(35, 39, 48) : Colors.White);
            brush.Freeze();
            return brush;
        }
        var tone = light ? Colors.White : Colors.Black;
        var decorationStrength = overlay.A / 255d;
        var startAlpha = Channel((light ? 128 : 154) * (1 - decorationStrength) +
            (light ? 60 : 92) * decorationStrength);
        var middleAlpha = Channel((light ? 88 : 118) * (1 - decorationStrength) +
            (light ? 32 : 64) * decorationStrength);
        var shade = new LinearGradientBrush
        {
            StartPoint = new(0, 0),
            EndPoint = new(0, 1),
            GradientStops =
            {
                new(Color.FromArgb(startAlpha, tone.R, tone.G, tone.B), 0),
                new(Color.FromArgb(middleAlpha, tone.R, tone.G, tone.B), .5),
                new(Color.FromArgb(0, tone.R, tone.G, tone.B), 1)
            }
        };
        shade.Freeze();
        return shade;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Color Composite(Color overlay, Color background)
    {
        var alpha = overlay.A / 255d;
        return Color.FromRgb(
            Channel(overlay.R * alpha + background.R * (1 - alpha)),
            Channel(overlay.G * alpha + background.G * (1 - alpha)),
            Channel(overlay.B * alpha + background.B * (1 - alpha)));
    }

    private static byte Channel(double value) =>
        (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}

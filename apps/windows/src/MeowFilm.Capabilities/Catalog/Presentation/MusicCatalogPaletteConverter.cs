using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

public sealed class MusicCatalogPaletteConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture)
    {
        var color = value is SolidColorBrush source ? source.Color : Colors.DimGray;
        var brush = new SolidColorBrush(Darken(color)); brush.Freeze(); return brush;
    }
    internal static Color Darken(Color color)
    {
        double luminance = .2126 * color.R + .7152 * color.G + .0722 * color.B;
        double scale = Math.Min(1, 82 / Math.Max(1, luminance));
        return Color.FromRgb((byte)(color.R * scale), (byte)(color.G * scale), (byte)(color.B * scale));
    }
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class MusicEntryTitlePaletteConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type type, object parameter, CultureInfo culture)
    {
        var cover = values.Length > 2 && values[2] is SolidColorBrush brush ? brush.Color : Colors.DimGray;
        var source = cover;
        if (values.Length > 1 && values[0] is true && values[1] is SolidColorBrush decoration)
        {
            double a = decoration.Color.A / 255d;
            source = Color.FromRgb((byte)(decoration.Color.R * a + cover.R * (1 - a)),
                (byte)(decoration.Color.G * a + cover.G * (1 - a)), (byte)(decoration.Color.B * a + cover.B * (1 - a)));
        }
        var tone = MusicCatalogPaletteConverter.Darken(source);
        var shade = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1),
            GradientStops = [new(Color.FromArgb(230, tone.R, tone.G, tone.B), 0),
                new(Color.FromArgb(180, tone.R, tone.G, tone.B), .5),
                new(Color.FromArgb(0, tone.R, tone.G, tone.B), 1)]
        };
        shade.Freeze(); return shade;
    }
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

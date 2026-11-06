using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeowFilm.Capabilities;

public sealed class MusicCatalogPaletteConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) =>
        MusicEntryPalette.Sample(value as BitmapSource);
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
        var tone = MusicEntryPalette.TitleShade(source);
        byte strength = (byte)Math.Round(94 + 48 * (source.R + source.G + source.B) / 765d);
        var shade = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1),
            GradientStops = [new(Color.FromArgb(strength, tone.R, tone.G, tone.B), 0),
                new(Color.FromArgb(strength, tone.R, tone.G, tone.B), .45),
                new(Color.FromArgb(0, tone.R, tone.G, tone.B), 1)]
        };
        shade.Freeze(); return shade;
    }
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

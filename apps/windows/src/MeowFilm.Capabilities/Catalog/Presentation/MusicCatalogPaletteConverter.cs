using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

public enum MusicCatalogPaletteRole
{
    Entry,
    CoverFooter
}

public sealed class MusicCatalogPaletteConverter : IMultiValueConverter
{
    private static readonly Color EntryTint = Color.FromRgb(75, 56, 50);

    public MusicCatalogPaletteRole Role { get; set; }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not SolidColorBrush source) return Brushes.DimGray;
        var color = Role switch
        {
            MusicCatalogPaletteRole.Entry when values.Length > 1 &&
                values[1] is MusicCatalogEntryPresentation presentation =>
                presentation == MusicCatalogEntryPresentation.Preview
                    ? LimitLuminance(source.Color, 170)
                    : LimitLuminance(Blend(source.Color, EntryTint, .65), 96),
            MusicCatalogPaletteRole.CoverFooter =>
                LimitLuminance(Blend(source.Color, Colors.Black, .22), 108),
            _ => Colors.DimGray
        };
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

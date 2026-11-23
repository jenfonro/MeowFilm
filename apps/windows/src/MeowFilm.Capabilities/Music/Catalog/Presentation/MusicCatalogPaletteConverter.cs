using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MeowFilm.Capabilities;

internal static class MusicEntryTitlePaletteConverter
{
    internal static Brush Create(bool hasDecoration, Brush? decorationBrush, Brush? coverBrush)
    {
        var cover = coverBrush is SolidColorBrush brush ? brush.Color : Colors.DimGray;
        var source = cover;
        if (hasDecoration && decorationBrush is SolidColorBrush decoration)
        {
            double a = decoration.Color.A / 255d;
            source = Color.FromArgb(255, (byte)(decoration.Color.R * a + cover.R * (1 - a)),
                (byte)(decoration.Color.G * a + cover.G * (1 - a)), (byte)(decoration.Color.B * a + cover.B * (1 - a)));
        }
        var tone = MusicEntryPalette.TitleShade(source);
        byte strength = (byte)Math.Round(94 + 48 * (source.R + source.G + source.B) / 765d);
        var shade = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1),
            GradientStops = [new GradientStop { Color = Color.FromArgb(strength, tone.R, tone.G, tone.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(strength, tone.R, tone.G, tone.B), Offset = .45 },
                new GradientStop { Color = Color.FromArgb(0, tone.R, tone.G, tone.B), Offset = 1 }]
        };
        return shade;
    }
}

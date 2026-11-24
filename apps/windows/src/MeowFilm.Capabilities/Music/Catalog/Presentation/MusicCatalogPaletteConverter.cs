using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MeowFilm.Capabilities;

internal static class MusicEntryTitlePaletteConverter
{
    internal static void Apply(Border target, bool hasDecoration, Brush? decorationBrush, Brush? coverBrush)
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
        var c0 = Color.FromArgb(strength, tone.R, tone.G, tone.B);
        var c2 = Color.FromArgb(0, tone.R, tone.G, tone.B);
        if (target.Background is LinearGradientBrush existing && existing.GradientStops.Count == 3)
        {
            existing.GradientStops[0].Color = c0;
            existing.GradientStops[1].Color = c0;
            existing.GradientStops[2].Color = c2;
            return;
        }
        target.Background = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1),
            GradientStops = [new GradientStop { Color = c0, Offset = 0 },
                new GradientStop { Color = c0, Offset = .45 },
                new GradientStop { Color = c2, Offset = 1 }]
        };
    }
}

using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MeowFilm.Capabilities;

internal static class MusicPlayerArtwork
{
    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    public static Brush Backdrop(Brush palette)
    {
        var color = (palette as SolidColorBrush)?.Color ?? Rgb(80, 77, 76);
        static Color Mix(Color a, byte gray, double weight) => Rgb(
            (byte)(a.R * weight + gray * (1 - weight)),
            (byte)(a.G * weight + gray * (1 - weight)),
            (byte)(a.B * weight + gray * (1 - weight)));
        var brush = new RadialGradientBrush
        {
            Center = new(.54, .39), GradientOrigin = new(.48, .37), RadiusX = .78, RadiusY = .85,
            GradientStops =
            {
                new GradientStop { Color = Mix(color, 48, .58), Offset = 0 },
                new GradientStop { Color = Mix(color, 66, .30), Offset = .55 },
                new GradientStop { Color = Mix(color, 109, .16), Offset = 1 }
            }
        };
        return brush;
    }
    public static Brush Progress(Brush palette)
    {
        var color = (palette as SolidColorBrush)?.Color ?? Rgb(80, 77, 76);
        // Same cover-derived hue as the player, with a subdued lighter value.
        // This is a local visual policy, not a claim about the reference app's algorithm.
        var brush = new SolidColorBrush(Color.FromArgb(175,
            (byte)(color.R * .55 + 180 * .45),
            (byte)(color.G * .55 + 180 * .45),
            (byte)(color.B * .55 + 180 * .45)));
        return brush;
    }
}

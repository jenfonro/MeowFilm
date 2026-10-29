using System.Windows;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

internal static class MusicPlayerArtwork
{
    // Frozen once; rotation uses a retained native animation, not per-frame drawing.
    public static Brush Grooves { get; } = CreateGrooves();
    private static Brush CreateGrooves()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(22, 23, 25)), null, new(150, 150), 150, 150);
            for (double radius = 100; radius < 148; radius += 1.7)
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 68, 69, 71)), .65), new(150, 150), radius, radius);
        }
        var brush = new DrawingBrush(drawing); brush.Freeze(); return brush;
    }
    public static Brush Backdrop(Brush palette)
    {
        var color = (palette as SolidColorBrush)?.Color ?? Color.FromRgb(80, 77, 76);
        static Color Mix(Color a, byte gray, double weight) => Color.FromRgb(
            (byte)(a.R * weight + gray * (1 - weight)),
            (byte)(a.G * weight + gray * (1 - weight)),
            (byte)(a.B * weight + gray * (1 - weight)));
        var brush = new RadialGradientBrush
        {
            Center = new(.54, .39), GradientOrigin = new(.48, .37), RadiusX = .78, RadiusY = .85,
            GradientStops = [
                new(Mix(color, 48, .58), 0),
                new(Mix(color, 66, .30), .55),
                new(Mix(color, 109, .16), 1)]
        };
        brush.Freeze(); return brush;
    }
    public static Brush Progress(Brush palette)
    {
        var color = (palette as SolidColorBrush)?.Color ?? Color.FromRgb(80, 77, 76);
        // Same cover-derived hue as the player, with a subdued lighter value.
        // This is a local visual policy, not a claim about the reference app's algorithm.
        var brush = new SolidColorBrush(Color.FromArgb(175,
            (byte)(color.R * .55 + 180 * .45),
            (byte)(color.G * .55 + 180 * .45),
            (byte)(color.B * .55 + 180 * .45)));
        brush.Freeze(); return brush;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MeowFilm.Desktop;

public static class AppearanceColors
{
    public const string DefaultColor = "#E6F3FB";
    private static readonly string[] Stops = ["#E6F3FB", "#EAF3F7", "#F7F7F3", "#E9ECEF", "#DBE3EA", "#D3DDE6"];
    private static readonly double[] Offsets = [0, .18, .38, .60, .80, 1];
    private static readonly double[] Tint = [.18, .12, .025, .10, .17, .22];
    public static Brush DefaultBackground { get; } = Background(DefaultColor);

    public static bool TryParse(string? value, out Color color)
    {
        color = default;
        if (value is not { Length: 7 } || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Brush Background(string value)
    {
        if (!TryParse(value, out var chosen)) throw new ArgumentException("Invalid color.", nameof(value));
        TryParse(DefaultColor, out var original);
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        for (int i = 0; i < Stops.Length; i++)
        {
            TryParse(Stops[i], out var stop);
            // Keep the reference's light canvas and neutral middle, tinting its
            // existing layers rather than laying a saturated color over the page.
            byte Channel(byte source, byte picked, byte baseline) =>
                (byte)Math.Clamp(Math.Round(source + (picked - baseline) * Tint[i]), 0, 255);
            brush.GradientStops.Add(new(Color.FromRgb(Channel(stop.R, chosen.R, original.R),
                Channel(stop.G, chosen.G, original.G), Channel(stop.B, chosen.B, original.B)), Offsets[i]));
        }
        brush.Freeze();
        return brush;
    }

    internal static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    internal static (double Hue, double Saturation, double Value) Hsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double h = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) :
            max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return ((h + 360) % 360, max == 0 ? 0 : delta / max, max);
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Data;

namespace MeowFilm.Desktop;

public static class AppearanceColors
{
    public const string DefaultColor = "#E6F3FB";
    private static readonly string[] Stops = ["#E4F2FC", "#EBF5FC", "#F5F8FB", "#F7F9FC"];
    private static readonly double[] Offsets = [0, .24, .58, 1];
    private static readonly double[] Tint = [.18, .12, .06, .025];
    public static Brush DefaultBackground { get; } = Background(DefaultColor);
    // Theme.xaml is merged by independent views and detached popup roots. Sharing these
    // brushes keeps the application's appearance consistent across those dictionaries.
    [ThreadStatic] private static PaletteColor? floatingSurface;
    [ThreadStatic] private static PaletteColor? itemHover;
    [ThreadStatic] private static PaletteColor? itemSelected;
    [ThreadStatic] private static PaletteColor? inputSurface;
    private static PaletteColor Floating => floatingSurface ??= new(TintColor(DefaultColor, .10, 255));
    private static PaletteColor Hover => itemHover ??= new(TintColor(DefaultColor, .16, 228));
    private static PaletteColor Selected => itemSelected ??= new(TintColor(DefaultColor, .32, 202));
    private static PaletteColor Input => inputSurface ??= new(TintColor(DefaultColor, .12, 250));
    public static SolidColorBrush FloatingSurface => Floating.Brush;
    public static SolidColorBrush ItemHover => Hover.Brush;
    public static SolidColorBrush ItemSelected => Selected.Brush;
    public static SolidColorBrush InputSurface => Input.Brush;

    internal static void ApplyFloatingPalette(string color)
    {
        Floating.Color = TintColor(color, .10, 255);
        Hover.Color = TintColor(color, .16, 228);
        Selected.Color = TintColor(color, .32, 202);
        Input.Color = TintColor(color, .12, 250);
    }

    private static Color TintColor(string value, double amount, byte neutral)
    {
        if (!TryParse(value, out var color)) throw new ArgumentException("Invalid color.", nameof(value));
        byte Mix(byte channel) => (byte)Math.Round(neutral * (1 - amount) + channel * amount);
        return Color.FromRgb(Mix(color.R), Mix(color.G), Mix(color.B));
    }

    private sealed class PaletteColor : DependencyObject
    {
        public static readonly DependencyProperty ColorProperty =
            DependencyProperty.Register(nameof(Color), typeof(Color), typeof(PaletteColor));
        public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
        public SolidColorBrush Brush { get; } = new();
        public PaletteColor(Color color)
        {
            Color = color;
            // A live binding prevents WPF from freezing the brush when styles/resources seal.
            BindingOperations.SetBinding(Brush, SolidColorBrush.ColorProperty,
                new Binding(nameof(Color)) { Source = this, Mode = BindingMode.OneWay });
        }
    }

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

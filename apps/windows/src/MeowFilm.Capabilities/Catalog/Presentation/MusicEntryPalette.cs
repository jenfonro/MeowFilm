using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeowFilm.Capabilities;

// Palette-style muted roles, evaluated only when an entry receives new artwork.
// Shared artwork/player palettes keep their original semantics.
internal static class MusicEntryPalette
{
    internal static SolidColorBrush Sample(BitmapSource? source)
    {
        if (source is null) return Brushes.Gray;
        var small = new TransformedBitmap(source, new ScaleTransform(
            Math.Min(32, source.PixelWidth) / (double)source.PixelWidth,
            Math.Min(32, source.PixelHeight) / (double)source.PixelHeight));
        var rgba = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[rgba.PixelWidth * rgba.PixelHeight * 4];
        rgba.CopyPixels(bytes, rgba.PixelWidth * 4, 0);
        var bins = new Dictionary<int, Swatch>();
        for (int i = 0; i < bytes.Length; i += 4)
        {
            int b = bytes[i], g = bytes[i + 1], r = bytes[i + 2], alpha = bytes[i + 3];
            if (alpha == 0) continue;
            int key = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
            var prior = bins.GetValueOrDefault(key);
            bins[key] = new(prior.Red + r * alpha, prior.Green + g * alpha,
                prior.Blue + b * alpha, prior.Weight + alpha);
        }
        if (bins.Count == 0) return Brushes.Gray;
        var swatches = bins.OrderBy(pair => pair.Key).Select(pair =>
        {
            var s = pair.Value;
            return (Color: Lab.FromRgb(s.Red / (255d * s.Weight),
                s.Green / (255d * s.Weight), s.Blue / (255d * s.Weight)), s.Weight);
        }).ToArray();
        double total = swatches.Sum(s => s.Weight);
        double lightness = swatches.Sum(s => s.Color.L * s.Weight) / total;
        // White/black padding and tiny accent flecks must not win over a real color region.
        var candidates = swatches.Where(s => s.Weight >= total * .01 &&
            s.Color.L is > .15 and < .96 && s.Color.Chroma >= .018).ToArray();
        if (candidates.Length == 0) candidates = swatches;
        double population = candidates.Max(s => s.Weight);
        var selected = candidates.MaxBy(s => .6 * s.Weight / population +
            .25 * (1 - Math.Min(1, Math.Abs(s.Color.Chroma - .06) / .2)) +
            .15 * (1 - Math.Abs(s.Color.L - lightness))).Color;
        double tone = Math.Clamp(selected.L * .75 + lightness * .25, .42, .76);
        double chroma = Math.Min(selected.Chroma * .65, .07);
        var color = selected.WithTone(tone, chroma);
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }

    internal static Color TitleShade(Color source)
    {
        var color = Lab.FromRgb(source.R / 255d, source.G / 255d, source.B / 255d);
        // Protect the title using its own backdrop, without lifting dark artwork.
        return color.WithTone(Math.Min(color.L * .8, .44), .035);
    }

    private readonly record struct Swatch(int Red, int Green, int Blue, int Weight);

    // Standard Oklab transforms: change lightness/chroma without rotating the source hue.
    private readonly record struct Lab(double L, double A, double B)
    {
        internal double Chroma => Math.Sqrt(A * A + B * B);
        internal static Lab FromRgb(double red, double green, double blue)
        {
            static double Linear(double x) => x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4);
            double r = Linear(red), g = Linear(green), b = Linear(blue);
            double l = Math.Cbrt(.4122214708 * r + .5363325363 * g + .0514459929 * b);
            double m = Math.Cbrt(.2119034982 * r + .6806995451 * g + .1073969566 * b);
            double s = Math.Cbrt(.0883024619 * r + .2817188376 * g + .6299787005 * b);
            return new(.2104542553 * l + .793617785 * m - .0040720468 * s,
                1.9779984951 * l - 2.428592205 * m + .4505937099 * s,
                .0259040371 * l + .7827717662 * m - .808675766 * s);
        }
        internal Color WithTone(double lightness, double chroma)
        {
            double scale = Chroma < .0001 ? 0 : Math.Min(chroma, Chroma) / Chroma;
            double a = A * scale, b = B * scale;
            double l = Math.Pow(lightness + .3963377774 * a + .2158037573 * b, 3);
            double m = Math.Pow(lightness - .1055613458 * a - .0638541728 * b, 3);
            double s = Math.Pow(lightness - .0894841775 * a - 1.291485548 * b, 3);
            static byte Encoded(double x)
            {
                x = Math.Clamp(x, 0, 1);
                return (byte)Math.Round(255 * (x <= .0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - .055));
            }
            return Color.FromRgb(Encoded(4.0767416621 * l - 3.3077115913 * m + .2309699292 * s),
                Encoded(-1.2684380046 * l + 2.6097574011 * m - .3413193965 * s),
                Encoded(-.0041960863 * l - .7034186147 * m + 1.707614701 * s));
        }
    }
}

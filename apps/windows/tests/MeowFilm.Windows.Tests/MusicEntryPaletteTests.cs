using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicEntryPaletteTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("entry palette uses shared sRGB mean and explicit preview or standard role", () =>
        {
            var image = Image((x, y) => Color.FromRgb(200, 160, 120));
            var preview = MusicEntryPalette.Sample(image, preview: true);
            var standard = MusicEntryPalette.Sample(image);
            Check(preview.IsFrozen && preview.Color == Color.FromRgb(200, 160, 120),
                "preview below the luminance ceiling retains its mean RGB");
            Check(standard.Color == Color.FromRgb(118, 92, 74),
                "standard mixes 65 percent RGB 75/56/50 before luminance limiting");
            var white = Image((x, y) => Colors.White);
            Check(MusicEntryPalette.Sample(white, preview: true).Color == Color.FromRgb(170, 170, 170),
                "preview caps sRGB luminance at 170");
            Check(MusicEntryPalette.Sample(white).Color == Color.FromRgb(104, 94, 91),
                "standard caps the mixed color at luminance 96");
        });
        yield return Case("partial alpha contributes proportionally to the shared RGB mean", () =>
        {
            var bytes = new byte[48 * 48 * 4];
            for (int i = 0; i < 48 * 48; i++)
            {
                bool red = i % 48 < 24;
                bytes[i * 4] = red ? (byte)0 : (byte)255;
                bytes[i * 4 + 2] = red ? (byte)255 : (byte)0;
                bytes[i * 4 + 3] = red ? (byte)255 : (byte)85;
            }
            var image = BitmapSource.Create(48, 48, 96, 96, PixelFormats.Bgra32, null, bytes, 192);
            Check(MusicEntryPalette.Sample(image, preview: true).Color == Color.FromRgb(191, 0, 64),
                "straight alpha gives opaque red three times the weight of translucent blue");
        });
        yield return Case("transparent padding and tiny accent pixels cannot replace the cover color", () =>
        {
            var blue = Color.FromRgb(130, 172, 209);
            var opaque = MusicEntryPalette.Sample(Image((x, y) => blue)).Color;
            var transparent = MusicEntryPalette.Sample(Image((x, y) =>
                x < 8 ? blue : Color.FromArgb(0, 255, 0, 0))).Color;
            var accent = MusicEntryPalette.Sample(Image((x, y) => x == 0 && y == 0 ? Colors.Red : blue)).Color;
            Check(opaque == transparent, "invisible pixels have no vote in the representative color");
            Check(Distance(opaque, accent) <= 3, "one bright accent does not recolor the card");
            var empty = MusicEntryPalette.Sample(Image((x, y) => Colors.Transparent)).Color;
            Check(empty.R == empty.G && empty.G == empty.B, "empty artwork has a neutral surface");
        });
        yield return Case("equal artwork is deterministic without order dependent palette selection", () =>
        {
            var forward = Image((x, y) => x < 24 ? Color.FromRgb(180, 80, 70) : Color.FromRgb(75, 125, 190), 48);
            var reverse = Image((x, y) => x < 24 ? Color.FromRgb(75, 125, 190) : Color.FromRgb(180, 80, 70), 48);
            Check(MusicEntryPalette.Sample(forward).Color == MusicEntryPalette.Sample(reverse).Color,
                "reversing color regions does not change the chosen surface");
        });
    }

    private static TestCase Case(string name, Action action) =>
        new("music-entry-palette: " + name, () => MusicQualityConsumerTests.Sta(() =>
        { action(); return Task.CompletedTask; }));
    internal static BitmapSource CoolArtwork() => Image((x, y) =>
        x < 19 ? Color.FromRgb(165, 195, 224) :
        y < 16 ? Color.FromRgb(248, 249, 251) : Color.FromRgb(49, 55, 63));
    internal static BitmapSource Image(Func<int, int, Color> pixel, int size = 32)
    {
        var bytes = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var c = pixel(x, y); int i = (y * size + x) * 4;
            bytes[i] = c.B; bytes[i + 1] = c.G; bytes[i + 2] = c.R; bytes[i + 3] = c.A;
        }
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bytes, size * 4);
        image.Freeze(); return image;
    }
    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

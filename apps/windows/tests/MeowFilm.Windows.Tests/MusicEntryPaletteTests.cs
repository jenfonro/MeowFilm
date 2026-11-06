using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicEntryPaletteTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("pale cover keeps a light muted blue family instead of a dark gray average", () =>
        {
            var brush = MusicEntryPalette.Sample(CoolArtwork());
            var color = brush.Color;
            Check(brush.IsFrozen && color.R > 125 && color.G > 145 && color.B > 160,
                "a pale cover produces a light surface rather than the old dark footer");
            Check(color.B > color.G + 5 && color.G > color.R + 5,
                "the representative blue region is not muddied by white and dark neutral regions");
            Check(color.B - color.R < 75, "the blue surface stays muted instead of saturated");
        });
        yield return Case("warm dark vivid and gray artwork retain their own muted color roles", () =>
        {
            var warm = MusicEntryPalette.Sample(Image((x, y) => x < 24
                ? Color.FromRgb(215, 135, 75) : Color.FromRgb(45, 30, 25))).Color;
            var dark = MusicEntryPalette.Sample(Image((x, y) => Color.FromRgb(48, 32, 26))).Color;
            var green = MusicEntryPalette.Sample(Image((x, y) => Color.FromRgb(0, 230, 60))).Color;
            Check(warm.R > warm.G + 8 && warm.G > warm.B + 8, "a warm cover remains warm, not the daily card's blue");
            Check(dark.R > dark.B && dark.R < warm.R - 25, "a dark warm cover retains its darker role");
            Check(green.G > green.R + 10 && green.G > green.B + 10 && green.G - green.R < 100,
                "a vivid green is softened without turning gray or changing hue");
            foreach (byte gray in new byte[] { 0, 80, 180, 255 })
            {
                var neutral = MusicEntryPalette.Sample(Image((x, y) => Color.FromRgb(gray, gray, gray))).Color;
                Check(Math.Abs(neutral.R - neutral.G) <= 1 && Math.Abs(neutral.G - neutral.B) <= 1,
                    "grayscale has a neutral fallback, not a fabricated accent");
            }
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
            var forward = Image((x, y) => x < 16 ? Color.FromRgb(180, 80, 70) : Color.FromRgb(75, 125, 190));
            var reverse = Image((x, y) => x < 16 ? Color.FromRgb(75, 125, 190) : Color.FromRgb(180, 80, 70));
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
    internal static BitmapSource Image(Func<int, int, Color> pixel)
    {
        var bytes = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            var c = pixel(x, y); int i = (y * 32 + x) * 4;
            bytes[i] = c.B; bytes[i + 1] = c.G; bytes[i + 2] = c.R; bytes[i + 3] = c.A;
        }
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, bytes, 128);
        image.Freeze(); return image;
    }
    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

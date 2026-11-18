using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicEntryPaletteTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("entry palette uses shared sRGB mean and explicit preview or standard role", () =>
        {
            var image = Image((x, y) => Color.FromArgb(255, 200, 160, 120));
            var preview = MusicEntryPalette.Sample(image, preview: true);
            var standard = MusicEntryPalette.Sample(image);
            Check(preview.Color == Color.FromArgb(255, 200, 160, 120),
                "preview below the luminance ceiling retains its mean RGB");
            Check(standard.Color == Color.FromArgb(255, 118, 92, 74),
                "standard mixes 65 percent RGB 75/56/50 before luminance limiting");
            var white = Image((x, y) => Colors.White);
            Check(MusicEntryPalette.Sample(white, preview: true).Color == Color.FromArgb(255, 170, 170, 170),
                "preview caps sRGB luminance at 170");
            Check(MusicEntryPalette.Sample(white).Color == Color.FromArgb(255, 104, 94, 91),
                "standard caps the mixed color at luminance 96");
        });
        yield return Case("partial alpha contributes proportionally to the shared RGB mean", () =>
        {
            var image = Image((x, y) => x < 24 ? Colors.Red : Color.FromArgb(85, 0, 0, 255), 48);
            Check(MusicEntryPalette.Sample(image, preview: true).Color == Color.FromArgb(255, 191, 0, 64),
                "straight alpha gives opaque red three times the weight of translucent blue");
        });
        yield return Case("transparent padding and tiny accent pixels cannot replace the cover color", () =>
        {
            var blue = Color.FromArgb(255, 130, 172, 209);
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
            var forward = Image((x, y) => x < 24 ? Color.FromArgb(255, 180, 80, 70) : Color.FromArgb(255, 75, 125, 190), 48);
            var reverse = Image((x, y) => x < 24 ? Color.FromArgb(255, 75, 125, 190) : Color.FromArgb(255, 180, 80, 70), 48);
            Check(MusicEntryPalette.Sample(forward).Color == MusicEntryPalette.Sample(reverse).Color,
                "reversing color regions does not change the chosen surface");
        });
    }

    private static TestCase Case(string name, Action action) =>
        new("music-entry-palette: " + name, () => MusicQualityConsumerTests.Sta(() =>
        { action(); return Task.CompletedTask; }));
    internal static BitmapSource CoolArtwork() => Image((x, y) =>
        x < 19 ? Color.FromArgb(255, 165, 195, 224) :
        y < 16 ? Color.FromArgb(255, 248, 249, 251) : Color.FromArgb(255, 49, 55, 63));
    internal static BitmapSource Image(Func<int, int, Color> pixel, int size = 32) => MusicTestUi.Bitmap(size, size, pixel);
    private static int Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace MeowFilm.Capabilities;

internal static class NovelTextStyle
{
    internal static string[] FontFamilies() => CanvasTextFormat.GetSystemFontFamilies().Order().ToArray();
    internal static Color Color(string value) => NovelAppearance.TryParseColor(value, out byte a, out byte r, out byte g, out byte b)
        ? Windows.UI.Color.FromArgb(a, r, g, b)
        : throw new FormatException("INVALID_COLOR");
}

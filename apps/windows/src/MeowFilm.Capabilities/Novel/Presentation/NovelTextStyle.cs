using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml.Markup;
using System.Runtime.InteropServices;
using Windows.UI;

namespace MeowFilm.Capabilities;

internal static class NovelTextStyle
{
    internal static bool ValidSize(double value) => value > 0 && float.IsFinite((float)value) && (float)value > 0 && value * 1.15 <= float.MaxValue;
    internal static bool ValidLineHeight(double size, double value) => value > 0 &&
        float.IsFinite((float)(size * value * 1.15)) && (float)(size * value) > 0;
    internal static bool ValidFamily(string value) => !string.IsNullOrWhiteSpace(value) && value.IndexOf('\0') < 0;
    internal static string[] FontFamilies() => CanvasTextFormat.GetSystemFontFamilies().Order().ToArray();
    internal static Color Color(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("INVALID_COLOR");
        try
        {
            return XamlBindingHelper.ConvertValue(typeof(Color), value) is Color color
                ? color : throw new FormatException("INVALID_COLOR");
        }
        catch (Exception error) when (error is ArgumentException or COMException or InvalidCastException)
        { throw new FormatException("INVALID_COLOR", error); }
    }
}

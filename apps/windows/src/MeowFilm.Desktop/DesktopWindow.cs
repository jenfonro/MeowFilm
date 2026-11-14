using System.Windows;
using System.Windows.Media;

namespace MeowFilm.Desktop;

/// <summary>Window root that owns only the shared typography defaults.</summary>
public class DesktopWindow : Window
{
    public DesktopWindow()
    {
        DesktopTypography.EnsureInstalled(Resources);
        SetResourceReference(FontFamilyProperty, SystemFonts.MessageFontFamilyKey);
        SetResourceReference(FontSizeProperty, "MeowFilm.Font.Size.Body");
        SetResourceReference(FontWeightProperty, "MeowFilm.Font.Weight.Normal");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

public static class DesktopTypography
{
    private static readonly Uri Source = new("ms-appx:///MeowFilm.Desktop/Theme/Theme.xaml");
    private static FontFamily? autoFontFamily;

    public static FontFamily AutoFontFamily => autoFontFamily ??= new("XamlAutoFontFamily");

    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!application.Resources.MergedDictionaries.OfType<XamlControlsResources>().Any())
            application.Resources.MergedDictionaries.Insert(0, new XamlControlsResources());
        EnsureInstalled(application.Resources);
    }
    internal static void EnsureInstalled(ResourceDictionary resources)
    {
        if (!resources.MergedDictionaries.Any(dictionary => dictionary.Source == Source))
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = Source });
    }
    internal static void Apply(FrameworkElement root)
    {
        if (root is Control control)
        {
            control.FontFamily = AutoFontFamily;
            control.FontSize = 14;
            control.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        }
    }
}

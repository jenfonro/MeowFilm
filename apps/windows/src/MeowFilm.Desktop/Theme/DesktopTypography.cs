using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

public static class DesktopTypography
{
    private static readonly Uri Source = new("ms-appx:///MeowFilm.Desktop/Theme/Theme.xaml");
    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!application.Resources.MergedDictionaries.OfType<Microsoft.UI.Xaml.Controls.XamlControlsResources>().Any())
            application.Resources.MergedDictionaries.Insert(0, new Microsoft.UI.Xaml.Controls.XamlControlsResources());
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
            control.FontFamily = new FontFamily("XamlAutoFontFamily");
            control.FontSize = 14;
            control.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        }
    }
}

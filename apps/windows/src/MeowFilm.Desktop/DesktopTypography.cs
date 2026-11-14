using System.Windows;

namespace MeowFilm.Desktop;

/// <summary>Installs the shared typography defaults before the first application window exists.</summary>
public static class DesktopTypography
{
    private static readonly Uri Source =
        new("/MeowFilm.Desktop;component/Typography.xaml", UriKind.Relative);

    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        EnsureInstalled(application.Resources);
    }

    internal static void EnsureInstalled(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var dictionaries = resources.MergedDictionaries;
        if (dictionaries.Any(dictionary => dictionary.Source is { } source &&
            source.OriginalString == Source.OriginalString)) return;
        dictionaries.Add(new ResourceDictionary { Source = Source });
    }
}

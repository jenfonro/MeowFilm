using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicCacheSettingsView : UserControl
{
    public MusicSettingsController Settings { get; }

    public MusicCacheSettingsView(MusicSettingsController settings)
    {
        Settings = settings;
        InitializeComponent();
    }

    private async void ClearPlaybackCache_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && DesktopVisual.IsVisible(this) && Settings.CanClearPlaybackCache) await Settings.ClearPlaybackCacheAsync();
    }
}

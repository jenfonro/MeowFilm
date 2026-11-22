using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicCacheSettingsView : UserControl
{
    private readonly MusicCapability module;
    public MusicCacheSettingsView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        DataContext = module.SettingsState;
    }
    private async void ClearPlaybackCache_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && DesktopVisual.IsVisible(this) && module.CanClearPlaybackCache) await module.ClearPlaybackCacheAsync();
    }
}

using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicCacheSettingsView : UserControl
{
    private readonly MusicCapability module;
    public MusicCacheSettingsView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        DataContext = module;
    }
    private async void ClearPlaybackCache_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && IsVisible && module.CanClearPlaybackCache) await module.ClearPlaybackCacheAsync();
    }
}

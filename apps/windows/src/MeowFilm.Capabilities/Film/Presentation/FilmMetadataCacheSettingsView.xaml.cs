using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class FilmMetadataCacheSettingsView : UserControl
{
    private readonly FilmMetadataService service;
    public FilmMetadataCacheSettingsView(FilmMetadataService service)
    {
        this.service = service;
        InitializeComponent();
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || !IsVisible || !Actions.IsEnabled || sender is not Button { Tag: string provider }) return;
        Actions.IsEnabled = false;
        try
        {
            await Task.WhenAll((provider == "both" ? new[] { "douban", "tmdb" } : [provider])
                .Select(value => service.ClearCacheAsync(value)));
            Notices.Message = "缓存已清空";
        }
        catch { Notices.Message = "缓存清理失败"; }
        finally { Actions.IsEnabled = true; }
    }
}

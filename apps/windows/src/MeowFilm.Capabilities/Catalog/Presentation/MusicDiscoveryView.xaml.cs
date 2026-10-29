using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicDiscoveryView : UserControl
{
    private MusicCapability? Model => DataContext as MusicCapability;
    public MusicDiscoveryView() => InitializeComponent();
    private async void Rotate_Click(object sender, RoutedEventArgs e)
    { if (Model is { } m) await m.RotateDiscoveryAsync(); }
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    { if (Model is { } m) await m.CancelDiscoveryAsync(); }
    private async void Keyword_Click(object sender, RoutedEventArgs e)
    { if (Model is { } m && sender is Button { Tag: MusicDiscoveryKeyword word }) await m.SearchDiscoveryKeywordAsync(word); }
    private async void Chart_Click(object sender, RoutedEventArgs e)
    { if (Model is { } m && sender is Button { Tag: MusicDiscoveryChart chart }) await m.OpenDiscoveryChartAsync(chart); }
    private async void Play_Click(object sender, RoutedEventArgs e)
    { if (Model is { } m && sender is Button { Tag: MusicDiscoverySong song }) await m.UseDiscoverySongAsync(song, true); }
    private async void Queue_Click(object sender, RoutedEventArgs e)
    { if (Model is { } m && sender is Button { Tag: MusicDiscoverySong song }) await m.UseDiscoverySongAsync(song, false); }
    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    { if (Model is { } m) await m.ClearSearchHistoryAsync(); }
}

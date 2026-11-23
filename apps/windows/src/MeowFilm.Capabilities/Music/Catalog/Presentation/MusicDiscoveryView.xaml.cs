using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicDiscoveryView : UserControl
{
    public MusicDiscoveryController? Discovery { get; private set; }
    public MusicSettingsController? Settings { get; private set; }

    public MusicDiscoveryView() => InitializeComponent();

    internal void Bind(MusicDiscoveryController discovery, MusicSettingsController settings)
    {
        Discovery = discovery;
        Settings = settings;
        Bindings.Update();
    }

    private async void Rotate_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d) await d.RotateDiscoveryAsync(); }
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d) await d.CancelDiscoveryAsync(); }
    private async void Keyword_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d && sender is Button { Tag: MusicDiscoveryKeyword word }) await d.SearchDiscoveryKeywordAsync(word); }
    private async void Chart_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d && sender is Button { Tag: MusicDiscoveryChart chart }) await d.OpenDiscoveryChartAsync(chart); }
    private async void Play_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d && sender is Button { Tag: MusicDiscoverySong song }) await d.UseDiscoverySongAsync(song, true); }
    private async void Queue_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d && sender is Button { Tag: MusicDiscoverySong song }) await d.UseDiscoverySongAsync(song, false); }
    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    { if (Settings is { } s) await s.ClearSearchHistoryAsync(); }
}

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

    private async void RunUiAction(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) Discovery?.PublishDiscoveryError(error);
        }
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d) RunUiAction(d.RotateDiscoveryAsync); }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d) RunUiAction(d.CancelDiscoveryAsync); }
    private void Keyword_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d && sender is Button { Tag: MusicDiscoveryKeyword word }) RunUiAction(() => d.SearchDiscoveryKeywordAsync(word)); }
    private void Chart_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d && sender is Button { Tag: MusicDiscoveryChart chart }) RunUiAction(() => d.OpenDiscoveryChartAsync(chart)); }
    private void Play_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d && sender is Button { Tag: MusicDiscoverySong song }) RunUiAction(() => d.UseDiscoverySongAsync(song, true)); }
    private void Queue_Click(object sender, RoutedEventArgs e)
    { if (Discovery is { } d && sender is Button { Tag: MusicDiscoverySong song }) RunUiAction(() => d.UseDiscoverySongAsync(song, false)); }
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    { if (Settings is { } s) RunUiAction(s.ClearSearchHistoryAsync); }
}

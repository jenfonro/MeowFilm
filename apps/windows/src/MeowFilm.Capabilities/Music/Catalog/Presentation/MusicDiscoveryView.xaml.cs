using System.Collections.Specialized;
using System.ComponentModel;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicDiscoveryView : UserControl
{
    public MusicDiscoveryController? Discovery { get; private set; }
    public MusicSettingsController? Settings { get; private set; }
    private MusicDiscoveryController? observed;

    public MusicDiscoveryView()
    {
        InitializeComponent();
        Loaded += (_, _) => Observe(Discovery);
        Unloaded += (_, _) => { if (!IsLoaded) Observe(null); };
    }

    internal void Bind(MusicDiscoveryController discovery, MusicSettingsController settings)
    {
        Discovery = discovery;
        Settings = settings;
        if (IsLoaded) Observe(discovery);
        Bindings.Update();
    }

    private void Observe(MusicDiscoveryController? next)
    {
        if (ReferenceEquals(observed, next)) return;
        if (observed is not null)
        {
            observed.PropertyChanged -= DiscoveryChanged;
            observed.DiscoveryFailures.CollectionChanged -= FailuresChanged;
        }
        observed = next;
        if (observed is not null)
        {
            observed.PropertyChanged += DiscoveryChanged;
            observed.DiscoveryFailures.CollectionChanged += FailuresChanged;
        }
    }

    private void DiscoveryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (observed is not null && e.PropertyName == nameof(MusicDiscoveryController.DiscoveryStatus))
            NoticeOutlet.Post(this, observed.DiscoveryStatus, observed);
    }

    private void FailuresChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (MusicDiscoveryFailure failure in e.NewItems)
            NoticeOutlet.Post(this, failure.Text, failure);
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

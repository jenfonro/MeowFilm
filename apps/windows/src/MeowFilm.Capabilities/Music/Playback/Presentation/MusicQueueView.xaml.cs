using Microsoft.UI.Xaml;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

public partial class MusicQueueView : UserControl
{
    public MusicQueueController Queue { get; }
    private readonly UserScrollIntent scrollIntent = new();
    private bool loadingMore;
    private ScrollViewer? queueScroll;
    private IDisposable? visibility, themeObserver;

    public MusicQueueView(MusicQueueController queue)
    {
        Queue = queue;
        InitializeComponent();
        ArtworkItemsScope.SetItemKeySelector(QueueList, item => ((MusicQueueSurfaceRow)item).EntryId);
        ArtworkItemsScope.SetItemResourcesSelector(QueueList, item =>
        {
            var row = (MusicQueueSurfaceRow)item;
            return [new("cover", row.Cover, row.Song.Id)];
        });
        ArtworkItemsScope.SetItemKeySelector(RecommendationList, item =>
        {
            var candidate = (MusicRecommendationCandidate)item;
            return (candidate.SessionId, candidate.Id);
        });
        ArtworkItemsScope.SetItemResourcesSelector(RecommendationList, item =>
        {
            var candidate = (MusicRecommendationCandidate)item;
            return [new("cover", candidate.Cover, candidate.Song.Id)];
        });
        Loaded += (_, _) =>
        {
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                themeObserver?.Dispose(); themeObserver = null;
                Queue.PropertyChanged -= QueueChanged;
                if (queueScroll is not null) queueScroll.ViewChanged -= QueueList_ViewChanged;
                if (!visible) { scrollIntent.Clear(); return; }
                Queue.PropertyChanged += QueueChanged;
                queueScroll ??= DesktopVisual.Descendant<ScrollViewer>(QueueList);
                if (queueScroll is not null) { queueScroll.ViewChanged += QueueList_ViewChanged; scrollIntent.Attach(queueScroll); }
                themeObserver = DesktopTheme.Observe(this, RefreshRows);
            });
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            Queue.PropertyChanged -= QueueChanged;
            visibility?.Dispose(); visibility = null;
            themeObserver?.Dispose(); themeObserver = null;
            scrollIntent.Clear();
            if (queueScroll is not null) queueScroll.ViewChanged -= QueueList_ViewChanged;
            queueScroll = null;
        };
    }

    private void QueueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicQueueController.QueueSurfaceStatus))
            NoticeOutlet.Post(this, Queue.QueueSurfaceStatus, Queue);
    }

    private void RefreshRows()
    {
        for (int i = 0; i < QueueList.Items.Count; i++)
            if (QueueList.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: MusicQueueRowView row }) row.Refresh();
    }

    private void QueueList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        scrollIntent.Set(e.GetCurrentPoint(this).Properties.MouseWheelDelta < 0);
        if (scrollIntent.Active) LoadMoreNearEnd();
    }

    private void QueueList_KeyDown(object sender, KeyRoutedEventArgs e) => BrowseQueue(e.Key);
    internal void BrowseQueue(VirtualKey key)
    {
        scrollIntent.Set(key is VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End);
        if (scrollIntent.Active) LoadMoreNearEnd();
    }

    private void QueueList_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!scrollIntent.Accepts(QueueList, e.IsIntermediate)) return;
        LoadMoreNearEnd();
    }

    private async void LoadMoreNearEnd()
    {
        if (loadingMore || !DesktopVisual.IsVisible(this) || !Queue.CanLoadMoreQueueSurface ||
            queueScroll is not { } scroll || scroll.ExtentHeight <= 0 ||
            scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - 80) return;
        loadingMore = true;
        scrollIntent.Clear();
        try { await Queue.LoadMoreQueueSurfaceAsync(); }
        finally { loadingMore = false; }
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicQueueSurfaceRow row }) await Queue.PlayQueueSurfaceItemAsync(row); }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicQueueSurfaceRow row }) await Queue.RemoveQueueSurfaceItemAsync(row); }
    private async void Clear_Click(object sender, RoutedEventArgs e) => await Queue.ClearQueueSurfaceAsync();
    private async void FavoriteAll_Click(object sender, RoutedEventArgs e) => await Queue.FavoriteQueueSurfaceAsync();
    private async void Recommendation_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicRecommendationCandidate candidate }) await Queue.ChooseRecommendationAsync(candidate); }
    private async void RetryRecommendation_Click(object sender, RoutedEventArgs e) => await Queue.RetryRecommendationAsync();
}

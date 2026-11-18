using Microsoft.UI.Xaml;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public partial class MusicQueueView : UserControl
{
    private readonly MusicCapability module;
    private readonly UserScrollIntent scrollIntent;
    private bool loadingMore;

    public MusicQueueView(MusicCapability module)
    {
        this.module = module;
        scrollIntent = new();
        InitializeComponent();
        DataContext = module;
        Loaded += (_, _) =>
        {
            if (Descendant<ScrollViewer>(QueueList) is not { } scroll) return;
            scroll.ViewChanged -= QueueList_ViewChanged; scroll.ViewChanged += QueueList_ViewChanged;
            scrollIntent.Attach(scroll);
        };
        Unloaded += (_, _) => scrollIntent.Clear();
    }

    private void QueueList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        scrollIntent.Set(e.GetCurrentPoint(this).Properties.MouseWheelDelta < 0);
        if (scrollIntent.Active) LoadMoreNearEnd();
    }

    private void QueueList_KeyDown(object sender, KeyRoutedEventArgs e)
        => BrowseQueue(e.Key);
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
        if (loadingMore || !MeowFilm.Desktop.DesktopVisual.IsVisible(this) || !module.CanLoadMoreQueueSurface ||
            Descendant<ScrollViewer>(QueueList) is not { } scroll ||
            scroll.ExtentHeight <= 0 ||
            scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - 80) return;
        loadingMore = true;
        scrollIntent.Clear();
        try { await module.LoadMoreQueueSurfaceAsync(); }
        finally { loadingMore = false; }
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicQueueSurfaceRow row }) await module.PlayQueueSurfaceItemAsync(row); }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicQueueSurfaceRow row }) await module.RemoveQueueSurfaceItemAsync(row); }
    private async void Clear_Click(object sender, RoutedEventArgs e) => await module.ClearQueueSurfaceAsync();
    private async void FavoriteAll_Click(object sender, RoutedEventArgs e) => await module.FavoriteQueueSurfaceAsync();
    private async void Recommendation_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicRecommendationCandidate candidate }) await module.ChooseRecommendationAsync(candidate); }
    private async void RetryRecommendation_Click(object sender, RoutedEventArgs e) => await module.RetryRecommendationAsync();
    private async void ReconcileRecommendation_Click(object sender, RoutedEventArgs e) => await module.ReconcileRecommendationAsync();
}

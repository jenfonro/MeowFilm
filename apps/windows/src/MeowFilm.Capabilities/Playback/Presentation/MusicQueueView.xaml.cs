using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

public partial class MusicQueueView : UserControl
{
    private readonly MusicCapability module;
    private readonly UserScrollIntent scrollIntent;
    private bool loadingMore;

    public MusicQueueView(MusicCapability module)
    {
        this.module = module;
        scrollIntent = new(Dispatcher);
        InitializeComponent();
        DataContext = module;
        AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, args) =>
        {
            if (IsVisible && Descendant<ScrollViewer>(QueueList) is { } scroll)
                scrollIntent.ObserveScroll(scroll, args);
        }), true);
        Unloaded += (_, _) => scrollIntent.Clear();
    }

    private void QueueList_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        scrollIntent.Set(e.Delta < 0);
        if (scrollIntent.Active) LoadMoreNearEnd();
    }

    private void QueueList_KeyDown(object sender, KeyEventArgs e)
    {
        scrollIntent.Set(e.Key is Key.Down or Key.PageDown or Key.End);
        if (scrollIntent.Active) LoadMoreNearEnd();
    }

    private void QueueList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || !scrollIntent.Accepts(QueueList)) return;
        LoadMoreNearEnd();
    }

    private async void LoadMoreNearEnd()
    {
        if (loadingMore || !IsVisible || !module.CanLoadMoreQueueSurface ||
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

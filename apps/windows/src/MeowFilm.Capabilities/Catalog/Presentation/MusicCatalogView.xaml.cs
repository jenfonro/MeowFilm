using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.Foundation;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicCatalogView : UserControl
{
    private readonly UserScrollIntent scrollIntent;
    private readonly ObservableCollection<MusicCatalogCard> searchPlaylists = [];
    private MusicCapability? observed;
    private bool loadingMore, restoringOffset;
    private double searchPlaylistOffset;
    private long restoreEpoch;
    private Storyboard? homeTransition;
    private IDisposable? visibility;
    private bool observingLayout;
    internal MusicCapability? Owner => DataContext as MusicCapability;
    internal ScrollViewer ActiveScroll => Owner is { IsSearchPlaylists: true } ? MusicVisuals.Descendant<ScrollViewer>(SearchPlaylistList) ?? CatalogScroll :
        Owner is { IsCatalogHome: true } ? HomeScroll : Owner is { IsSearchOverview: true } ? SearchOverviewScroll : Owner is { IsChannelLanding: true } ? CatalogScroll : UtilityScroll;
    public MusicCatalogView()
    {
        InitializeComponent(); scrollIntent = new(); SearchPlaylistList.ItemsSource = searchPlaylists;
        Loaded += (_, _) =>
        {
            scrollIntent.Attach(CatalogScroll); Observe(); Refresh(); RestoreSearchOffset();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                if (observingLayout == visible) return;
                observingLayout = visible;
                if (visible) { LayoutUpdated += ReportVisibleCatalogCards; ReportVisibleCatalogCards(null, null!); }
                else { LayoutUpdated -= ReportVisibleCatalogCards; observed?.UpdateCatalogVisibleCards([]); ChannelMorePopup.Hide(); scrollIntent.Clear(); }
            });
        };
        DataContextChanged += (_, _) => { observed?.UpdateCatalogVisibleCards([]); if (IsLoaded) Observe(); Refresh(); };
        Unloaded += (_, _) =>
        {
            visibility?.Dispose(); visibility = null;
            LayoutUpdated -= ReportVisibleCatalogCards; observingLayout = false;
            observed?.UpdateCatalogVisibleCards([]);
            RememberSearchOffset(); restoreEpoch++; scrollIntent.Clear(); ChannelMorePopup.Hide(); homeTransition?.Stop();
            if (observed is not null) { observed.PropertyChanged -= Changed; observed.HomeContentReplacing -= ReplacingHome; }
            observed = null;
        };
        SearchPlaylistList.SizeChanged += (_, _) => SizeSearchCards();
        SearchPlaylistList.Loaded += (_, _) =>
        {
            if (MusicVisuals.Descendant<ScrollViewer>(SearchPlaylistList) is not { } scroll) return;
            scroll.ViewChanged -= SearchScrolled; scroll.ViewChanged += SearchScrolled;
            scroll.SizeChanged -= SearchViewportChanged; scroll.SizeChanged += SearchViewportChanged;
            scrollIntent.Attach(scroll); SizeSearchCards(); RestoreSearchOffset();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        { if (Visibility != Visibility.Visible) { RememberSearchOffset(); ChannelMorePopup.Hide(); scrollIntent.Clear(); } else { Refresh(); RestoreSearchOffset(); } });
    }
    private void ReportVisibleCatalogCards(object? sender, object e)
    {
        if (!DesktopVisual.IsVisible(this) || Owner is not { } owner) return;
        var cards = new HashSet<MusicCatalogCard>(ReferenceEqualityComparer.Instance);
        Visit(this); owner.UpdateCatalogVisibleCards(cards);
        void Visit(DependencyObject parent)
        {
            if (parent is FrameworkElement element)
            {
                if (element.Visibility != Visibility.Visible) return;
                if (element.DataContext is MusicCatalogCard card && element.ActualWidth > 0 && element.ActualHeight > 0)
                {
                    if (InCatalogViewport(element)) cards.Add(card);
                    return;
                }
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                Visit(VisualTreeHelper.GetChild(parent, index));
        }
    }
    internal static bool InCatalogViewport(FrameworkElement slot)
    {
        if (!slot.IsLoaded || !DesktopVisual.IsVisible(slot) || slot.ActualWidth <= 0 || slot.ActualHeight <= 0) return false;
        try
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(slot); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is FrameworkElement element && (element.Clip is not null || element is ScrollContentPresenter or ScrollViewer))
                {
                    var bounds = slot.TransformToVisual(element).TransformBounds(new Rect(0, 0, slot.ActualWidth, slot.ActualHeight));
                    bounds.Intersect(element.Clip?.Rect ?? new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return false;
                }
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
    private void Observe()
    {
        if (observed is not null) { observed.PropertyChanged -= Changed; observed.HomeContentReplacing -= ReplacingHome; }
        observed = Owner;
        if (observed is not null) { observed.PropertyChanged += Changed; observed.HomeContentReplacing += ReplacingHome; }
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { Refresh(); }
    private void ReplacingHome(object? sender, EventArgs e)
    {
        homeTransition?.Stop();
        if (HomeScroll.Visibility == Visibility.Visible && MusicVisuals.CanAnimate(this))
            homeTransition = MusicVisuals.AnimateDouble(HomeScroll, "Opacity", .4, 1, TimeSpan.FromMilliseconds(220));
    }
    private void Refresh()
    {
        if (Owner is not { } owner) return;
        CatalogScroll.Visibility = owner.IsChannelLanding && !owner.IsChannelDirectory ? Visibility.Visible : Visibility.Collapsed;
        Skeleton.Visibility = owner.ShowCatalogSkeleton || owner.ShowSearchSkeleton && owner.IsSearchPlaylists ? Visibility.Visible : Visibility.Collapsed;
        if (!owner.HasSearchSession) ResetSearchPlaylistProjection();
        if (owner.IsSearchPlaylists)
        {
            bool restore = SearchPlaylistList.Visibility != Visibility.Visible;
            SearchPlaylistList.Visibility = Visibility.Visible;
            var cards = owner.CatalogSections.Count == 1 ? owner.CatalogSections[0].Items : [];
            while (searchPlaylists.Count > cards.Count) searchPlaylists.RemoveAt(searchPlaylists.Count - 1);
            for (int i = 0; i < cards.Count; i++)
                if (i == searchPlaylists.Count) searchPlaylists.Add(cards[i]);
                else if (!ReferenceEquals(searchPlaylists[i], cards[i])) searchPlaylists[i] = cards[i];
            if (restore) RestoreSearchOffset();
        }
        else { RememberSearchOffset(); SearchPlaylistList.Visibility = Visibility.Collapsed; }
    }
    internal void ResetSearchPlaylistProjection() { restoreEpoch++; restoringOffset = false; searchPlaylistOffset = 0; searchPlaylists.Clear(); }
    private void RememberSearchOffset()
    { if (!restoringOffset && SearchPlaylistList.Visibility == Visibility.Visible && MusicVisuals.Descendant<ScrollViewer>(SearchPlaylistList) is { } scroll) searchPlaylistOffset = scroll.VerticalOffset; }
    private void RestoreSearchOffset()
    {
        scrollIntent.Clear(); long epoch = ++restoreEpoch; restoringOffset = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != restoreEpoch) return;
            if (IsLoaded && Owner is { IsSearchPlaylists: true }) MusicVisuals.Descendant<ScrollViewer>(SearchPlaylistList)?.ChangeView(null, searchPlaylistOffset, null, true);
            restoringOffset = false;
        });
    }
    private void SizeSearchCards()
    {
        if (SearchPlaylistList.ItemsPanelRoot is not ItemsWrapGrid panel ||
            MusicVisuals.Descendant<ScrollViewer>(SearchPlaylistList) is not { ViewportWidth: > 0 } scroll) return;
        var (_, pitch) = Shelf.MeasureCards(scroll.ViewportWidth, 124, 16);
        double width = Math.Min(168, pitch - 16);
        panel.ItemWidth = pitch; panel.ItemHeight = width + 72;
    }
    private void SearchViewportChanged(object sender, SizeChangedEventArgs e) => SizeSearchCards();
    private void SearchCard_Changing(ListViewBase sender, ContainerContentChangingEventArgs e)
    { e.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch; e.ItemContainer.Padding = new Thickness(0, 0, 16, 24); }
    private void SearchScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    { SizeSearchCards(); RememberSearchOffset(); if (scrollIntent.Accepts(SearchPlaylistList, e.IsIntermediate)) LoadMoreNearEnd(); }
    private void CatalogScroll_Changed(object? sender, ScrollViewerViewChangedEventArgs e)
    { if (scrollIntent.Accepts(ActiveScroll, e.IsIntermediate)) LoadMoreNearEnd(); }
    private void CatalogScroll_Wheel(object sender, PointerRoutedEventArgs e)
        => BrowseCatalogWheel(e.GetCurrentPoint(this).Properties.MouseWheelDelta);
    private void CatalogScroll_Key(object sender, KeyRoutedEventArgs e)
        => BrowseCatalogKey(e.Key);
    internal void BrowseCatalogWheel(int delta)
    { scrollIntent.Set(delta < 0); if (scrollIntent.Active) LoadMoreNearEnd(); }
    internal void BrowseCatalogKey(VirtualKey key)
    { scrollIntent.Set(key is VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End); if (scrollIntent.Active) LoadMoreNearEnd(); }
    private async void LoadMoreNearEnd()
    {
        if (loadingMore || Visibility != Visibility.Visible || Owner is not { } owner || ActiveScroll.ScrollableHeight - ActiveScroll.VerticalOffset > 280) return;
        bool search = owner.IsSearchPlaylists && owner.CanLoadMoreSearchPlaylists;
        if (!search && (!owner.IsChannelLanding || !owner.CanCatalogNext)) return;
        loadingMore = true; scrollIntent.Clear();
        try { if (search) await owner.MoreSearchAsync(); else await owner.CatalogPageAsync(1); }
        finally { loadingMore = false; scrollIntent.Clear(); }
    }
    private void ChannelMore_Click(object sender, RoutedEventArgs e)
    { ((FrameworkElement)ChannelMorePopup.Content).DataContext = Owner; ChannelMorePopup.ShowAt(ChannelMoreButton); }
    private void Channel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicCatalogCard card } button || Owner is not { } owner) return;
        bool selected = owner.CurrentChannelKey == card.Platform + "/" + Uri.EscapeDataString(card.ActionId);
        button.BorderBrush = selected ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] : MusicVisuals.Brush("#00000000");
        AutomationProperties.SetItemStatus(button, selected ? "已选中" : "");
    }
    private void Tag_Loaded(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicCatalogTag tag } button) { button.BorderBrush = (Brush)Application.Current.Resources[tag.Selected ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush"]; AutomationProperties.SetItemStatus(button, tag.Selected ? "已选中" : ""); } }
    private void SignDay_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;
        // Sign-day values are a presentation DTO; normal WinUI bindings handle the label.
        if (border.DataContext is MusicVipSignDay day && (day.Today || day.Signed))
        { border.BorderBrush = MusicVisuals.Brush("#FF3B4D"); if (border.Child is TextBlock text) text.Foreground = border.BorderBrush; if (day.Signed) border.Background = MusicVisuals.Brush("#18FF3B4D"); }
    }
    private async void Chart_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded && Owner is { } owner) await owner.ChangeCatalogChartAsync(); }
    private async void ChartYear_Changed(object sender, RoutedEventArgs e) { if (IsLoaded && Owner is { } owner) await owner.ChangeCatalogChartAsync(); }
    private async void Tag_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner && sender is Button { Tag: MusicCatalogTag tag }) await owner.CatalogTagAsync(tag); }
    private async void SearchBack_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.ReturnToSearchAsync(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Owner?.CancelCatalog();
    private async void PlayAll_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.PlayCatalogPlaylistAsync(); }
    private async void Save_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.SaveCatalogPlaylistAsync(); }
    private async void Account_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.ReadCatalogAccountAsync(); }
    private async void Options_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded && Owner is { } owner) await owner.CatalogOptionsChangedAsync(); }
    private async void VipSign_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.SignVipPageAsync(); }
    private async void VipBanner_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.OpenVipBannerAsync(); }
    private async void QueueRecommendations_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.CatalogQueueRecommendationsAsync(); }
    private async void Platform_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded && Owner is { } owner) await owner.ChangeCatalogPlatformAsync(); }
    private async void Kind_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded && Owner is { IsSearchPage: false } owner) await owner.CatalogSearchKindAsync(); }
    private async void Page_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner && sender is Button { Tag: string direction }) await owner.CatalogPageAsync(int.Parse(direction, CultureInfo.InvariantCulture)); }
    private async void Card_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner && sender is Button { Tag: MusicCatalogCard card }) { ChannelMorePopup.Hide(); await owner.OpenCatalogCardAsync(card); } }
}

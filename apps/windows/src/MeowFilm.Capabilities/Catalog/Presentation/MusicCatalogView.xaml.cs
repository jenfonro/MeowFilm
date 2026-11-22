using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.Foundation;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicCatalogView : UserControl
{
    private readonly UserScrollIntent scrollIntent;
    private readonly ObservableCollection<MusicCatalogCard> searchPlaylists = [];
    private readonly MusicCatalogSectionDisplayCollection homeDisplay = new(), searchDisplay = new(), utilityDisplay = new();
    private readonly Dictionary<ObservableCollection<MusicCatalogSection>, (MusicCatalogSectionDisplayCollection Display, ItemsControl View)> channelPresentations = [];
    private ItemsControl? activeChannelView;
    private ScrollViewer? searchScroll;
    private MusicCapability? observed;
    private bool loadingMore, restoringOffset;
    private double searchPlaylistOffset;
    private long restoreEpoch;
    private IDisposable? visibility;
    private bool visible, reportQueued;
    private readonly Dictionary<FrameworkElement, MusicCatalogCard> visibleSlots = [];
    internal MusicCapability? Owner => (DataContext as MusicCatalogState)?.Owner;
    internal ScrollViewer ActiveScroll => Owner is { IsSearchPlaylists: true } ? searchScroll ?? CatalogScroll :
        Owner is { IsCatalogHome: true } ? HomeScroll : Owner is { IsSearchOverview: true } ? SearchOverviewScroll : Owner is { IsChannelLanding: true } ? CatalogScroll : UtilityScroll;
    public MusicCatalogView()
    {
        InitializeComponent(); scrollIntent = new(); SearchPlaylistList.ItemsSource = searchPlaylists;
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemKeySelector(SearchPlaylistList, item => ((MusicCatalogCard)item).Key);
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemResourcesSelector(SearchPlaylistList,
            item => MusicCatalogCardView.ArtworkResources((MusicCatalogCard)item));
        HomeItems.ItemsSource = homeDisplay.Items;
        SearchOverviewItems.ItemsSource = searchDisplay.Items; UtilityItems.ItemsSource = utilityDisplay.Items;
        Loaded += (_, _) =>
        {
            scrollIntent.Attach(CatalogScroll); Observe(); Refresh(); RestoreSearchOffset();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                this.visible = visible;
                if (visible) QueueVisibleCards();
                else { observed?.UpdateCatalogVisibleCards([]); ChannelMorePopup.Hide(); scrollIntent.Clear(); }
            });
        };
        DataContextChanged += (_, _) => { observed?.UpdateCatalogVisibleCards([]); if (IsLoaded) Observe(); Refresh(); QueueVisibleCards(); };
        Unloaded += (_, _) =>
        {
            visibility?.Dispose(); visibility = null;
            visible = false; visibleSlots.Clear();
            observed?.UpdateCatalogVisibleCards([]);
            RememberSearchOffset(); restoreEpoch++; scrollIntent.Clear(); ChannelMorePopup.Hide();
            if (observed is not null)
            { observed.CatalogState.PropertyChanged -= Changed; observed.ChannelPageRetired -= RetireChannelPresentation;
                observed.SearchPlaylistSections.CollectionChanged -= SearchPlaylistsChanged; }
            observed = null;
            homeDisplay.Dispose(); searchDisplay.Dispose(); utilityDisplay.Dispose(); ClearChannelPresentations();
        };
        SearchPlaylistList.SizeChanged += (_, _) => SizeSearchCards();
        SearchPlaylistList.Loaded += (_, _) =>
        {
            if (MusicVisuals.Descendant<ScrollViewer>(SearchPlaylistList) is not { } scroll) return;
            searchScroll = scroll;
            scroll.ViewChanged -= SearchScrolled; scroll.ViewChanged += SearchScrolled;
            scroll.SizeChanged -= SearchViewportChanged; scroll.SizeChanged += SearchViewportChanged;
            scrollIntent.Attach(scroll); SizeSearchCards(); RestoreSearchOffset();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        { if (Visibility != Visibility.Visible) { RememberSearchOffset(); ChannelMorePopup.Hide(); scrollIntent.Clear(); } else { Refresh(); RestoreSearchOffset(); } });
    }
    internal void UpdateVisibleSlot(FrameworkElement slot, MusicCatalogCard? card)
    {
        if (card is null)
        {
            if (!visibleSlots.Remove(slot)) return;
        }
        else if (visibleSlots.TryGetValue(slot, out var previous) && ReferenceEquals(previous, card)) return;
        else visibleSlots[slot] = card;
        QueueVisibleCards();
    }
    private void QueueVisibleCards()
    {
        if (reportQueued || !visible) return;
        reportQueued = DispatcherQueue.TryEnqueue(() =>
        {
            reportQueued = false;
            if (visible) Owner?.UpdateCatalogVisibleCards(visibleSlots.Values);
        });
    }
    private void Observe()
    {
        if (observed is not null)
        { observed.CatalogState.PropertyChanged -= Changed; observed.ChannelPageRetired -= RetireChannelPresentation;
            observed.SearchPlaylistSections.CollectionChanged -= SearchPlaylistsChanged; }
        if (!ReferenceEquals(observed, Owner)) ClearChannelPresentations();
        observed = Owner;
        homeDisplay.Observe(observed?.HomeSections);
        searchDisplay.Observe(observed?.SearchOverviewSections); utilityDisplay.Observe(observed?.UtilitySections);
        ShowChannelPresentation(); RefreshSearchCards();
        if (observed is not null)
        { observed.CatalogState.PropertyChanged += Changed; observed.ChannelPageRetired += RetireChannelPresentation;
            observed.SearchPlaylistSections.CollectionChanged += SearchPlaylistsChanged; }
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicCapability.CatalogSections))
            ShowChannelPresentation();
        if (e.PropertyName is nameof(MusicCapability.IsCatalogHome) or nameof(MusicCapability.IsChannelLanding) or
            nameof(MusicCapability.IsChannelDirectory) or nameof(MusicCapability.ShowCatalogLoading) or
            nameof(MusicCapability.ShowSearchLoading) or nameof(MusicCapability.IsSearchPlaylists)) Refresh();
    }
    private void ShowChannelPresentation()
    {
        if (activeChannelView is not null) activeChannelView.Visibility = Visibility.Collapsed;
        activeChannelView = null;
        if (Owner is not { IsChannelLanding: true, IsChannelDirectory: false } owner ||
            !owner.RetainedChannelSections.Contains(owner.ChannelSections)) return;
        var current = owner.ChannelSections;
        if (!channelPresentations.TryGetValue(current, out var presentation))
        {
            var display = new MusicCatalogSectionDisplayCollection(); display.Observe(current);
            var view = new ItemsControl { ItemsSource = display.Items, ItemTemplate = (DataTemplate)Resources["Section"] };
            channelPresentations.Add(current, presentation = (display, view)); ChannelPages.Children.Add(view);
        }
        activeChannelView = presentation.View;
        activeChannelView.Visibility = Visibility.Visible;
    }
    private void RetireChannelPresentation(ObservableCollection<MusicCatalogSection> sections)
    {
        if (!channelPresentations.Remove(sections, out var presentation)) return;
        presentation.Display.Dispose(); ChannelPages.Children.Remove(presentation.View);
        if (ReferenceEquals(activeChannelView, presentation.View)) activeChannelView = null;
    }
    private void ClearChannelPresentations()
    {
        foreach (var presentation in channelPresentations.Values) presentation.Display.Dispose();
        ChannelPages.Children.Clear(); channelPresentations.Clear(); activeChannelView = null;
    }
    private void Refresh()
    {
        if (Owner is not { } owner) return;
        CatalogScroll.Visibility = owner.IsChannelLanding && !owner.IsChannelDirectory && !owner.ShowCatalogLoading ? Visibility.Visible : Visibility.Collapsed;
        CatalogLoading.IsActive = owner.ShowCatalogLoading || owner.ShowSearchLoading && owner.IsSearchPlaylists;
        CatalogLoading.Visibility = CatalogLoading.IsActive ? Visibility.Visible : Visibility.Collapsed;
        if (owner.IsSearchPlaylists)
        {
            bool restore = SearchPlaylistList.Visibility != Visibility.Visible;
            SearchPlaylistList.Visibility = Visibility.Visible;
            if (restore) RestoreSearchOffset();
        }
        else if (SearchPlaylistList.Visibility == Visibility.Visible)
        { RememberSearchOffset(); SearchPlaylistList.Visibility = Visibility.Collapsed; }
    }
    private void SearchPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshSearchCards();
    private void RefreshSearchCards() => MusicCatalogSectionView.ReconcileCards(searchPlaylists,
        Owner is { SearchPlaylistSections.Count: 1 } owner ? owner.SearchPlaylistSections[0].Items : []);
    internal void ResetSearchPlaylistProjection()
    {
        if (searchPlaylists.Count == 0 && searchPlaylistOffset == 0) return;
        restoreEpoch++; restoringOffset = false; searchPlaylistOffset = 0; searchPlaylists.Clear();
    }
    private void RememberSearchOffset()
    { if (!restoringOffset && SearchPlaylistList.Visibility == Visibility.Visible && searchScroll is { } scroll) searchPlaylistOffset = scroll.VerticalOffset; }
    private void RestoreSearchOffset()
    {
        scrollIntent.Clear(); long epoch = ++restoreEpoch; restoringOffset = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != restoreEpoch) return;
            if (IsLoaded && Owner is { IsSearchPlaylists: true }) searchScroll?.ChangeView(null, searchPlaylistOffset, null, true);
            restoringOffset = false;
        });
    }
    private void SizeSearchCards()
    {
        if (SearchPlaylistList.ItemsPanelRoot is not ItemsWrapGrid panel ||
            searchScroll is not { ViewportWidth: > 0 } scroll) return;
        var (_, pitch) = Shelf.MeasureCards(scroll.ViewportWidth, 124, 16);
        double width = Math.Min(168, pitch - 16);
        panel.ItemWidth = pitch; panel.ItemHeight = width + 72;
    }
    private void SearchViewportChanged(object sender, SizeChangedEventArgs e) => SizeSearchCards();
    private void SearchCard_Changing(ListViewBase sender, ContainerContentChangingEventArgs e)
    { e.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch; e.ItemContainer.Padding = new Thickness(0, 0, 16, 24); }
    private void SearchScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    { RememberSearchOffset(); if (scrollIntent.Accepts(SearchPlaylistList, e.IsIntermediate)) LoadMoreNearEnd(); }
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
    private async void Options_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsLoaded: true, SelectedItem: MusicCatalogChoice choice } picker &&
            DesktopVisual.IsVisible(picker) && Owner is { } owner)
            await owner.ChangeCatalogPersonalKindAsync(choice);
    }
    private async void VipSign_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.SignVipPageAsync(); }
    private async void VipBanner_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.OpenVipBannerAsync(); }
    private async void QueueRecommendations_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner) await owner.CatalogQueueRecommendationsAsync(); }
    private async void Platform_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsLoaded: true, SelectedItem: MusicCatalogPlatform platform } picker &&
            DesktopVisual.IsVisible(picker) && Owner is { } owner)
            await owner.ChangeCatalogPlatformAsync(platform);
    }
    private async void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsLoaded: true, SelectedItem: string kind } picker &&
            DesktopVisual.IsVisible(picker) && Owner is { IsSearchPage: false, CanCatalogBrowse: true } owner &&
            kind != owner.CatalogSearchKind)
        {
            owner.CatalogSearchKind = kind;
            await owner.CatalogSearchKindAsync();
        }
    }
    private async void Page_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner && sender is Button { Tag: string direction }) await owner.CatalogPageAsync(int.Parse(direction, CultureInfo.InvariantCulture)); }
    private async void Card_Click(object sender, RoutedEventArgs e) { if (Owner is { } owner && sender is Button { Tag: MusicCatalogCard card }) { ChannelMorePopup.Hide(); await owner.OpenCatalogCardAsync(card); } }
}

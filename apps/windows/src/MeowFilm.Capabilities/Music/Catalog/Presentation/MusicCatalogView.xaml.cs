using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
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
    private MusicCatalogController? observedCatalog;
    private MusicSearchController? observedSearch;
    private MusicLibraryController? observedLibrary;
    private bool loadingMore, restoringOffset;
    private double searchPlaylistOffset;
    private long restoreEpoch;
    private IDisposable? visibility;
    private bool visible, reportQueued;
    private readonly Dictionary<FrameworkElement, MusicCatalogCard> visibleSlots = [];
    private readonly HashSet<MusicCatalogCardView> mountedSearchCards = [];
    private MusicCatalogContext? catalogContext;

    public MusicCatalogController? Catalog { get; private set; }
    public MusicSearchController? Search { get; private set; }
    public MusicLibraryController? Library { get; private set; }
    internal MusicSettingsController? Settings { get; private set; }
    internal MusicPlaybackController? Playback { get; private set; }

    internal ScrollViewer ActiveScroll => Search is { IsSearchPlaylists: true } ? searchScroll ?? CatalogScroll :
        Catalog is { IsCatalogHome: true } ? HomeScroll : Search is { IsSearchOverview: true } ? SearchOverviewScroll : Catalog is { IsChannelLanding: true } ? CatalogScroll : UtilityScroll;

    public MusicCatalogView()
    {
        InitializeComponent();
        scrollIntent = new();
        SearchPlaylistList.ItemsSource = searchPlaylists;
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemKeySelector(SearchPlaylistList, item => ((MusicCatalogCard)item).Key);
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemResourcesSelector(SearchPlaylistList,
            item => MusicCatalogCardView.ArtworkResources((MusicCatalogCard)item));
        HomeItems.ItemsSource = homeDisplay.Items;
        SearchOverviewItems.ItemsSource = searchDisplay.Items;
        UtilityItems.ItemsSource = utilityDisplay.Items;
        Loaded += (_, _) =>
        {
            scrollIntent.Attach(CatalogScroll);
            Observe();
            Bindings.Update();
            if (!Refresh()) RestoreSearchOffset();
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                this.visible = visible;
                if (visible) { if (!Refresh()) RestoreSearchOffset(); QueueVisibleCards(); }
                else { RememberSearchOffset(); observedCatalog?.UpdateCatalogVisibleCards([]); ChannelMorePopup.Hide(); scrollIntent.Clear(); }
            });
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            visibility?.Dispose(); visibility = null;
            visible = false; visibleSlots.Clear();
            observedCatalog?.UpdateCatalogVisibleCards([]);
            RememberSearchOffset(); restoreEpoch++; restoringOffset = false; scrollIntent.Clear(); ChannelMorePopup.Hide();
            DetachObserved();
            mountedSearchCards.Clear();
            homeDisplay.Dispose(); searchDisplay.Dispose(); utilityDisplay.Dispose(); ClearChannelPresentations();
        };
        SearchPlaylistList.SizeChanged += (_, _) => SizeSearchCards();
        SearchPlaylistList.Loaded += (_, _) =>
        {
            if (DesktopVisual.Descendant<ScrollViewer>(SearchPlaylistList) is not { } scroll) return;
            searchScroll = scroll;
            scroll.ViewChanged -= SearchScrolled; scroll.ViewChanged += SearchScrolled;
            scroll.SizeChanged -= SearchViewportChanged; scroll.SizeChanged += SearchViewportChanged;
            scrollIntent.Attach(scroll); SizeSearchCards(); RestoreSearchOffset();
        };
    }

    internal void Bind(MusicCatalogController catalog, MusicSearchController search, MusicLibraryController library,
        MusicSettingsController settings, MusicPlaybackController playback)
    {
        observedCatalog?.UpdateCatalogVisibleCards([]);
        Catalog = catalog;
        Search = search;
        Library = library;
        Settings = settings;
        Playback = playback;
        catalogContext = new MusicCatalogContext(catalog, search, library, settings, playback, UpdateVisibleSlot);
        homeDisplay.SetContext(catalogContext);
        searchDisplay.SetContext(catalogContext);
        utilityDisplay.SetContext(catalogContext);
        foreach (var presentation in channelPresentations.Values) presentation.Display.SetContext(catalogContext);
        foreach (var card in mountedSearchCards) card.AttachContext(catalogContext);
        Bindings.Update();
        if (IsLoaded) Observe();
        Refresh();
        QueueVisibleCards();
    }

    private void SearchCardView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MusicCatalogCardView view) return;
        mountedSearchCards.Add(view);
        view.AttachContext(catalogContext);
        view.Unloaded -= SearchCardView_Unloaded;
        view.Unloaded += SearchCardView_Unloaded;
    }

    private void SearchCardView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MusicCatalogCardView view && !view.IsLoaded)
            mountedSearchCards.Remove(view);
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
            if (visible) Catalog?.UpdateCatalogVisibleCards(visibleSlots.Values);
        });
    }

    private void DetachObserved()
    {
        if (observedCatalog is not null)
        {
            observedCatalog.PropertyChanged -= Changed;
            observedCatalog.ChannelPageRetired -= RetireChannelPresentation;
            observedCatalog = null;
        }
        if (observedSearch is not null)
        {
            observedSearch.PropertyChanged -= Changed;
            observedSearch.PlaylistSections.CollectionChanged -= SearchPlaylistsChanged;
            observedSearch = null;
        }
        if (observedLibrary is not null)
        {
            observedLibrary.PropertyChanged -= Changed;
            observedLibrary = null;
        }
    }

    private void Observe()
    {
        if (!ReferenceEquals(observedCatalog, Catalog)) ClearChannelPresentations();
        DetachObserved();
        observedCatalog = Catalog;
        observedSearch = Search;
        observedLibrary = Library;
        homeDisplay.Observe(observedCatalog?.HomeSections);
        searchDisplay.Observe(observedSearch?.OverviewSections);
        utilityDisplay.Observe(observedCatalog?.UtilitySections);
        ShowChannelPresentation();
        RefreshSearchCards();
        if (observedCatalog is not null)
        {
            observedCatalog.PropertyChanged += Changed;
            observedCatalog.ChannelPageRetired += RetireChannelPresentation;
        }
        if (observedSearch is not null)
        {
            observedSearch.PropertyChanged += Changed;
            observedSearch.PlaylistSections.CollectionChanged += SearchPlaylistsChanged;
        }
        if (observedLibrary is not null) observedLibrary.PropertyChanged += Changed;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicCatalogController.CatalogSections) or nameof(MusicCatalogController.CurrentChannelKey) or nameof(MusicLibraryController.ContentPage))
            ShowChannelPresentation();
        if (e.PropertyName is nameof(MusicCatalogController.IsCatalogHome) or nameof(MusicCatalogController.HasChannelTabs) or
            nameof(MusicCatalogController.CurrentChannelKey) or nameof(MusicCatalogController.ShowCatalogLoading) or
            nameof(MusicSearchController.ShowSearchLoading) or nameof(MusicSearchController.IsSearchPage) or
            nameof(MusicSearchController.SearchTab) or nameof(MusicLibraryController.ContentPage)) Refresh();
    }

    private void ShowChannelPresentation()
    {
        if (activeChannelView is not null) activeChannelView.Visibility = Visibility.Collapsed;
        activeChannelView = null;
        if (Catalog is not { IsChannelLanding: true, IsChannelDirectory: false } catalog ||
            !catalog.RetainedChannelSections.Contains(catalog.ChannelSections)) return;
        var current = catalog.ChannelSections;
        if (!channelPresentations.TryGetValue(current, out var presentation))
        {
            var display = new MusicCatalogSectionDisplayCollection();
            display.SetContext(catalogContext);
            display.Observe(current);
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

    private bool Refresh()
    {
        if (Catalog is not { } catalog || Search is not { } search) return false;
        CatalogScroll.Visibility = catalog.IsChannelLanding && !catalog.IsChannelDirectory && !catalog.ShowCatalogLoading ? Visibility.Visible : Visibility.Collapsed;
        if (catalog.ShowCatalogLoading || search.ShowSearchLoading && search.IsSearchPlaylists) FindName(nameof(CatalogLoading));
        else if (CatalogLoading is not null) UnloadObject(CatalogLoading);
        if (search.IsSearchPlaylists)
        {
            bool restore = SearchPlaylistList.Visibility != Visibility.Visible;
            SearchPlaylistList.Visibility = Visibility.Visible;
            if (restore) { RestoreSearchOffset(); return true; }
        }
        else if (SearchPlaylistList.Visibility == Visibility.Visible)
        { RememberSearchOffset(); SearchPlaylistList.Visibility = Visibility.Collapsed; }
        return false;
    }

    private void SearchPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshSearchCards();

    private void RefreshSearchCards() => MusicCatalogSectionView.ReconcileCards(searchPlaylists,
        Search is { PlaylistSections.Count: 1 } search ? search.PlaylistSections[0].Items : []);

    internal void ResetSearchPlaylistProjection()
    {
        if (searchPlaylists.Count == 0 && searchPlaylistOffset == 0) return;
        restoreEpoch++; restoringOffset = false; searchPlaylistOffset = 0; searchPlaylists.Clear();
    }

    private void RememberSearchOffset()
    { if (!restoringOffset && SearchPlaylistList.Visibility == Visibility.Visible && searchScroll is { } scroll) searchPlaylistOffset = scroll.VerticalOffset; }

    private void RestoreSearchOffset()
    {
        if (!IsLoaded || Search is not { IsSearchPlaylists: true }) return;
        scrollIntent.Clear(); long epoch = ++restoreEpoch; restoringOffset = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != restoreEpoch) return;
            if (IsLoaded && Search is { IsSearchPlaylists: true }) searchScroll?.ChangeView(null, searchPlaylistOffset, null, true);
            restoringOffset = false;
        }))
            restoringOffset = false;
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

    private async void RunUiAction(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) Catalog?.PublishCatalogActionError(error);
        }
    }

    private async void LoadMoreNearEnd()
    {
        if (loadingMore || Visibility != Visibility.Visible || Catalog is not { } catalog || Search is not { } search || ActiveScroll.ScrollableHeight - ActiveScroll.VerticalOffset > 280) return;
        bool searchPlaylistsMode = search.IsSearchPlaylists && search.CanLoadMoreSearchPlaylists;
        if (!searchPlaylistsMode && (!catalog.IsChannelLanding || !catalog.CanCatalogNext)) return;
        loadingMore = true; scrollIntent.Clear();
        try { if (searchPlaylistsMode) await search.MoreSearchAsync(); else await catalog.CatalogPageAsync(1); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsLoaded) catalog.PublishCatalogActionError(error); }
        finally { loadingMore = false; scrollIntent.Clear(); }
    }

    [ThreadStatic] private static SolidColorBrush? signDayAccentBrush, signDaySignedBackgroundBrush;

    private void SignDay_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;
        // Sign-day values are a presentation DTO; normal WinUI bindings handle the label.
        if (border.DataContext is MusicVipSignDay day && (day.Today || day.Signed))
        {
            border.BorderBrush = signDayAccentBrush ??= DesktopVisual.Brush("#FF3B4D");
            if (border.Child is TextBlock text) text.Foreground = border.BorderBrush;
            if (day.Signed) border.Background = signDaySignedBackgroundBrush ??= DesktopVisual.Brush("#18FF3B4D");
        }
    }

    private void Chart_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { IsLoaded: true, SelectedValue: string value } picker ||
            !DesktopVisual.IsVisible(picker) || Catalog is not { } catalog) return;
        if (ReferenceEquals(picker, ChartKindPicker))
            RunUiAction(() => catalog.ChangeCatalogChartKindAsync(value));
        else if (ReferenceEquals(picker, ChartPeriodPicker))
            RunUiAction(() => catalog.ChangeCatalogChartPeriodAsync(value));
    }

    private void ChartYear_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { IsLoaded: true } box || !DesktopVisual.IsVisible(box) ||
            Catalog is not { } catalog) return;
        if (int.TryParse(box.Text.Trim(), CultureInfo.InvariantCulture, out int year))
            RunUiAction(() => catalog.ChangeCatalogChartYearAsync(year));
    }

    private void Tag_Click(object sender, RoutedEventArgs e) { if (Catalog is { } catalog && sender is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase { Tag: MusicCatalogTag tag }) RunUiAction(() => catalog.CatalogTagAsync(tag)); }
    private void SearchBack_Click(object sender, RoutedEventArgs e) { if (Search is { } search) RunUiAction(search.ReturnToSearchAsync); }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Search is { IsSearchPage: true } search) _ = search.CancelSearchAsync();
        else Catalog?.CancelCatalog();
    }
    private void PlayAll_Click(object sender, RoutedEventArgs e) { if (Catalog is { } catalog) RunUiAction(catalog.PlayCatalogPlaylistAsync); }
    private void Save_Click(object sender, RoutedEventArgs e) { if (Catalog is { } catalog) RunUiAction(catalog.SaveCatalogPlaylistAsync); }
    private void Account_Click(object sender, RoutedEventArgs e) { if (Catalog is { } catalog) RunUiAction(catalog.ReadCatalogAccountAsync); }

    private void Options_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsLoaded: true, SelectedItem: MusicCatalogChoice choice } picker &&
            DesktopVisual.IsVisible(picker) && Catalog is { } catalog)
            RunUiAction(() => catalog.ChangeCatalogPersonalKindAsync(choice));
    }

    private void VipSign_Click(object sender, RoutedEventArgs e) { if (Catalog is { } catalog) RunUiAction(catalog.SignVipPageAsync); }
    private void VipBanner_Click(object sender, RoutedEventArgs e) { if (Catalog is { } catalog) RunUiAction(catalog.OpenVipBannerAsync); }
    private void QueueRecommendations_Click(object sender, RoutedEventArgs e) { if (Catalog is { } catalog) RunUiAction(catalog.CatalogQueueRecommendationsAsync); }

    private void Platform_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsLoaded: true, SelectedItem: MusicCatalogPlatform platform } picker &&
            DesktopVisual.IsVisible(picker) && Catalog is { } catalog)
            RunUiAction(() => catalog.ChangeCatalogPlatformAsync(platform));
    }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsLoaded: true, SelectedItem: string kind } picker &&
            DesktopVisual.IsVisible(picker) && Catalog is { CanCatalogBrowse: true } &&
            Search is { IsSearchPage: false } search && kind != search.SearchKindLabel)
        {
            search.SearchKindLabel = kind;
        }
    }

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        if (Catalog is { } catalog && sender is Button { Tag: string direction } &&
            int.TryParse(direction, CultureInfo.InvariantCulture, out int delta))
            RunUiAction(() => catalog.CatalogPageAsync(delta));
    }

    private void Card_Click(object sender, RoutedEventArgs e) { if (Catalog is { } catalog && sender is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase { Tag: MusicCatalogCard card }) { ChannelMorePopup.Hide(); RunUiAction(() => catalog.OpenCatalogCardAsync(card)); } }
}

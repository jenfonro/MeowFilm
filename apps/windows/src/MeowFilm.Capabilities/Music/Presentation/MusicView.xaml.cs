using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MeowFilm.Capabilities;

public partial class MusicView : UserControl, IDesktopHeaderSource
{
    public MusicLibraryController Library { get; }
    public MusicCatalogController Catalog { get; }
    public MusicSearchController Search { get; }
    private readonly MusicDiscoveryController discovery;
    private readonly MusicPlaybackController playback;
    private readonly Action onPageLoaded;
    private readonly DesktopHeaderAction refreshAction, renamePlaylistAction, deletePlaylistAction;
    private readonly UserScrollIntent songScrollIntent;
    private readonly MusicPagePresenter content;
    private readonly ListView[] songLists;
    private readonly Dictionary<ListView, ScrollViewer> songScrolls = [];
    private readonly Dictionary<string, bool> favoriteCache = new(StringComparer.Ordinal);
    private long favoritesEpoch;
    private bool loadingMoreSongs, syncingDate, syncingSelection, projectedSearch;
    private bool playlistDialogOpen;
    private MenuFlyout? songMenu, dailyMoreMenu;
    private bool playlistDialogSubmitting;
    private Task dailyStyleRetired = Task.CompletedTask;
    private Flyout? dailyStyleOpen;
    private long dailyStyleRequest;
    private SongRow? draggingSong;
    private ListViewItem? insertionRow;
    private bool insertAfter;
    private FrameworkElement? hoveredSongRow;
    private IDisposable? themeObserver, visibilityObserver;
    private bool visibilityActive;
    private (bool CollectionActions, bool RefreshAction, bool Editable) publishedHeaderState;
    private ListView ActiveSongsGrid => Search.IsSearchPage ? SearchSongsGrid : SongsGrid;
    private MusicContentPage CurrentContentPage => discovery.IsDiscovery ? MusicContentPage.Discovery :
        Library.IsArchives ? MusicContentPage.Archives :
        Search.IsSearchSongs ? MusicContentPage.SearchSongs :
        (Library.IsSongList || Catalog.IsCatalogDetailPage) ? MusicContentPage.Songs : MusicContentPage.Catalog;
    internal bool IsPlaylistDialogOpen => playlistDialogOpen;
    internal DesktopNavigationGroupAction CreatePlaylistAction { get; }

    public MusicView(
        MusicLibraryController library,
        MusicCatalogController catalog,
        MusicSearchController search,
        MusicDiscoveryController discovery,
        MusicPlaybackController playback,
        MusicSettingsController settings,
        DesktopNavigationGroupAction createPlaylistAction,
        Action onPageLoaded)
    {
        Library = library;
        Catalog = catalog;
        Search = search;
        this.discovery = discovery;
        this.playback = playback;
        this.onPageLoaded = onPageLoaded;
        songScrollIntent = new();
        CreatePlaylistAction = createPlaylistAction;
        refreshAction = new("music.refresh", "刷新", async token => { token.ThrowIfCancellationRequested(); await Library.RefreshAsync(); token.ThrowIfCancellationRequested(); }, () => Library.CanEdit, automationName: "刷新音乐内容");
        renamePlaylistAction = new("music.playlist.rename", "重命名", RenamePlaylistNameAsync, () => Library.CanEdit && Library.CanDeleteCollection);
        deletePlaylistAction = new("music.playlist.delete", "删除", token => { token.ThrowIfCancellationRequested(); return Library.DeletePlaylistAsync(); }, () => Library.CanEdit && Library.CanDeleteCollection);
        InitializeComponent();
        CatalogView.Bind(catalog, search, library, settings, playback);
        DiscoveryView.Bind(discovery, settings);
        BodyPages.Children.Clear();
        content = new(new()
        {
            [MusicContentPage.Catalog] = CatalogView,
            [MusicContentPage.Discovery] = DiscoveryView,
            [MusicContentPage.Archives] = ArchiveScroll,
            [MusicContentPage.Songs] = SongsPage,
            [MusicContentPage.SearchSongs] = SearchSongsPage
        });
        BodyPages.Children.Add(content);
        content.Show(CurrentContentPage);
        songLists = [SongsGrid, SearchSongsGrid];
        foreach (var list in songLists)
        {
            if (ReferenceEquals(list, SearchSongsGrid)) ArtworkItemsScope.SetItemKeySelector(list, item => ((SongRow)item).Id);
            ArtworkItemsScope.SetItemResourcesSelector(list, item =>
            {
                var song = (SongRow)item;
                return [new("cover", song.Cover, song.Id)];
            });
            list.Loaded += (_, _) =>
            {
                if (DesktopVisual.Descendant<ScrollViewer>(list) is not { } scroll) return;
                songScrolls[list] = scroll;
                scroll.ViewChanged -= SongsScrolled; scroll.ViewChanged += SongsScrolled;
                songScrollIntent.Attach(scroll);
            };
            list.Unloaded += (_, _) =>
            {
                if (list.IsLoaded) return;
                if (songScrolls.Remove(list, out var scroll)) scroll.ViewChanged -= SongsScrolled;
            };
            list.ContainerContentChanging += (_, e) =>
            {
                if (e.InRecycleQueue)
                {
                    if (ReferenceEquals(hoveredSongRow, e.ItemContainer.ContentTemplateRoot))
                        hoveredSongRow = null;
                    return;
                }
                RefreshSongRow(e.ItemContainer, syncFavorite: true);
            };
            list.SelectionChanged += (_, e) =>
            {
                if (ReferenceEquals(list, SongsGrid) && !syncingSelection)
                {
                    var selected = SongsGrid.SelectedItem as SongRow;
                    if (Catalog.IsCatalogPage) Catalog.CatalogSelectedSong = selected;
                    else if (Library.IsSongList) Library.SelectedSong = selected;
                }
                foreach (var item in e.RemovedItems)
                    if (list.ContainerFromItem(item) is ListViewItem removed) RefreshSongRow(removed, syncFavorite: false);
                foreach (var item in e.AddedItems)
                    if (list.ContainerFromItem(item) is ListViewItem added) RefreshSongRow(added, syncFavorite: false);
            };
        }
        publishedHeaderState = HeaderState();
        Loaded += (_, _) =>
        {
            Library.PropertyChanged -= LibraryChanged; Library.PropertyChanged += LibraryChanged;
            Catalog.PropertyChanged -= CatalogChanged; Catalog.PropertyChanged += CatalogChanged;
            Search.PropertyChanged -= SearchChanged; Search.PropertyChanged += SearchChanged;
            discovery.PropertyChanged -= DiscoveryChanged; discovery.PropertyChanged += DiscoveryChanged;
            playback.PropertyChanged -= OnPlaybackChanged; playback.PropertyChanged += OnPlaybackChanged;
            Library.SongFavoritesChanged -= OnSongFavoritesChanged; Library.SongFavoritesChanged += OnSongFavoritesChanged;
            Library.Playlists.CollectionChanged -= OnPlaylistsChanged; Library.Playlists.CollectionChanged += OnPlaylistsChanged;
            themeObserver?.Dispose();
            themeObserver = DesktopTheme.Observe(this, () => RefreshAllSongRows(syncFavorite: false));
            Bindings.Update();
            PublishHeaderState();
            if (!DesktopVisual.IsVisible(this))
            {
                content.Show(CurrentContentPage);
                Refresh();
            }
            visibilityObserver?.Dispose();
            visibilityObserver = DesktopVisual.ObserveVisibility(this, UpdateVisibilityActivity);
            RefreshAllSongRows(syncFavorite: true);
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            visibilityObserver?.Dispose(); visibilityObserver = null;
            themeObserver?.Dispose();
            themeObserver = null;
            Library.PropertyChanged -= LibraryChanged;
            Catalog.PropertyChanged -= CatalogChanged;
            Search.PropertyChanged -= SearchChanged;
            discovery.PropertyChanged -= DiscoveryChanged;
            playback.PropertyChanged -= OnPlaybackChanged;
            Library.SongFavoritesChanged -= OnSongFavoritesChanged;
            Library.Playlists.CollectionChanged -= OnPlaylistsChanged;
            hoveredSongRow = null;
            UpdateVisibilityActivity(false);
        };
    }

    internal void ResetSearchPresentation() => CatalogView.ResetSearchPlaylistProjection();
    internal void ProjectSearchResults(bool projected) { projectedSearch = projected; Refresh(); }

    private async void UpdateVisibilityActivity(bool active)
    {
        if (visibilityActive == active) return;
        visibilityActive = active;
        if (active)
        {
            content.Show(CurrentContentPage);
            Refresh();
            onPageLoaded();
        }
        else { Catalog.InvalidateCatalogAction(); if (playlistDialogOpen) DesktopModalHost.CloseFor(this); songMenu?.Hide(); dailyMoreMenu?.Hide(); DailyStylePopup.Hide(); songScrollIntent.Clear(); EndDrag(); }
        try { await Task.WhenAll(active ? discovery.ActivateDiscoveryViewAsync() : discovery.DeactivateDiscoveryViewAsync(), Catalog.UpdateVipPageAsync(active), active ? Catalog.ActivateCatalogLandingAsync() : Task.CompletedTask); }
        catch { /* Navigation and shutdown retain final drain ownership. */ }
    }

    private void DiscoveryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicDiscoveryController.IsDiscovery))
        {
            content.Show(CurrentContentPage);
            Refresh();
            PublishHeaderState();
        }
    }

    private void LibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicLibraryController.IsLocalLibraryPage) or nameof(MusicLibraryController.IsArchives) or
            nameof(MusicLibraryController.IsSongList))
            content.Show(CurrentContentPage);
        if (e.PropertyName == nameof(MusicLibraryController.CanReorderPlaylist) && !Library.CanReorderPlaylist) EndDrag();
        if (playlistDialogOpen && !playlistDialogSubmitting && e.PropertyName is nameof(MusicLibraryController.Heading) or nameof(MusicLibraryController.IsCollectionPage)) DesktopModalHost.CloseFor(this);
        if (e.PropertyName is nameof(MusicLibraryController.IsLocalLibraryPage) or nameof(MusicLibraryController.IsArchives) or
            nameof(MusicLibraryController.IsSongList) or nameof(MusicLibraryController.IsCollectionPage) or
            nameof(MusicLibraryController.HistoryDate) or nameof(MusicLibraryController.CanEdit) or
            nameof(MusicLibraryController.Heading) or nameof(MusicLibraryController.CollectionArtwork) or
            nameof(MusicLibraryController.CanPlayCollection) or nameof(MusicLibraryController.CountText) or
            nameof(MusicLibraryController.SelectedSong)) Refresh();
        if (e.PropertyName is nameof(MusicLibraryController.IsCollectionPage) or nameof(MusicLibraryController.CanDeleteCollection) or
            nameof(MusicLibraryController.IsLocalLibraryPage) or nameof(MusicLibraryController.CanEdit))
            PublishHeaderState();
    }

    private void CatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicCatalogController.IsDailyStyleEditorOpen))
        {
            long request = ++dailyStyleRequest;
            if (Catalog.IsDailyStyleEditorOpen) _ = ShowDailyStylePopupAsync(request);
            else if (dailyStyleOpen is not null) DailyStylePopup.Hide();
        }
        if (e.PropertyName is nameof(MusicCatalogController.IsCatalogPage) or nameof(MusicCatalogController.IsCatalogDetailPage) or
            nameof(MusicCatalogController.IsCatalogContent))
        {
            content.Show(CurrentContentPage);
            PublishHeaderState();
        }
        if (e.PropertyName is nameof(MusicCatalogController.IsCatalogPage) or nameof(MusicCatalogController.IsCatalogDetailPage) or
            nameof(MusicCatalogController.IsCatalogContent) or nameof(MusicCatalogController.IsCatalogHome) or
            nameof(MusicCatalogController.IsCatalogLanding) or nameof(MusicCatalogController.HasChannelTabs) or
            nameof(MusicCatalogController.CurrentChannelKey) or nameof(MusicCatalogController.CanEdit) or
            nameof(MusicCatalogController.IsCatalogArtist) or nameof(MusicCatalogController.CatalogHeading) or
            nameof(MusicCatalogController.DetailSubtitle) or nameof(MusicCatalogController.CatalogDetailArtwork) or
            nameof(MusicCatalogController.CanPlayCatalogPlaylist) or nameof(MusicCatalogController.CatalogDetailSectionTitle) or
            nameof(MusicCatalogController.CatalogDetailCountText) or nameof(MusicCatalogController.CatalogSelectedSong)) Refresh();
    }

    private void SearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicSearchController.IsSearchPage) or nameof(MusicSearchController.IsSearchSongs) or
            nameof(MusicSearchController.SearchTab))
        {
            content.Show(CurrentContentPage);
            PublishHeaderState();
        }
        if (e.PropertyName is nameof(MusicSearchController.IsSearchPage) or nameof(MusicSearchController.ShowSearchLoading) or
            nameof(MusicSearchController.SearchTab)) Refresh();
    }

    private void PublishHeaderState()
    {
        var state = HeaderState();
        if (state != publishedHeaderState) { publishedHeaderState = state; HeaderChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicPlaybackController.CurrentSongId))
            RefreshAllSongRows(syncFavorite: false);
        else if (e.PropertyName == nameof(MusicPlaybackController.IsCurrentFavorite))
            OnSongFavoritesChanged();
    }

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnSongFavoritesChanged();

    private void OnSongFavoritesChanged()
    {
        favoritesEpoch++;
        favoriteCache.Clear();
        RefreshAllSongRows(syncFavorite: true);
    }

    private void RefreshAllSongRows(bool syncFavorite)
    {
        foreach (var list in songLists)
            for (int i = 0; i < list.Items.Count; i++) if (list.ContainerFromIndex(i) is ListViewItem row) RefreshSongRow(row, syncFavorite);
    }

    private void Refresh()
    {
        PageContent.Margin = projectedSearch ? new Thickness(0) : Catalog.IsCatalogLanding ? new Thickness(0, 12, 0, 0) : new Thickness(40, 16, 24, 20);
        PageContent.MaxWidth = Catalog.IsCatalogLanding ? double.PositiveInfinity : 1490;
        SearchHeading.Visibility = !projectedSearch && Search.IsSearchPage ? Visibility.Visible : Visibility.Collapsed;

        bool isCatalogDetail = Catalog.IsCatalogDetailPage;
        bool isDetail = isCatalogDetail || Library.IsCollectionPage;
        DetailHeaderGrid.Visibility = isDetail ? Visibility.Visible : Visibility.Collapsed;
        DetailSectionHeader.Visibility = isDetail ? Visibility.Visible : Visibility.Collapsed;
        string detailArtwork = isCatalogDetail ? Catalog.CatalogDetailArtwork : Library.CollectionArtwork;
        DetailArtworkImage.Address = detailArtwork;
        AutomationProperties.SetName(DetailArtworkImage, isCatalogDetail ? Catalog.CatalogDetailArtworkName : "歌单封面");
        DetailHeadingText.Text = isCatalogDetail ? Catalog.CatalogHeading : Library.Heading;
        DetailSubtitleText.Text = isCatalogDetail ? Catalog.DetailSubtitle : "";
        PlayContentDetailButton.IsEnabled = isCatalogDetail ? Catalog.CanPlayCatalogPlaylist : Library.CanPlayCollection;
        DetailSectionTitleText.Text = isCatalogDetail ? Catalog.CatalogDetailSectionTitle : "歌曲";
        DetailCountTextBlock.Text = isCatalogDetail ? Catalog.CatalogDetailCountText : (Library.IsCollectionPage ? $"{Library.Total}" : "");

        double artworkRadius = Catalog.IsCatalogArtist ? 76 : 18;
        DetailArtworkFrame.CornerRadius = new CornerRadius(artworkRadius);
        CardClip.SetRadius(DetailArtworkFrame, artworkRadius);
        DetailArtworkFallback.Visibility = string.IsNullOrWhiteSpace(detailArtwork) ? Visibility.Visible : Visibility.Collapsed;

        var targetSongs = Catalog.IsCatalogPage ? Catalog.CatalogSongsSource : Library.LibrarySongsSource;
        if (!ReferenceEquals(SongsGrid.ItemsSource, targetSongs)) SongsGrid.ItemsSource = targetSongs;
        var targetSelected = Catalog.IsCatalogPage ? Catalog.CatalogSelectedSong : Library.SelectedSong;
        if (!ReferenceEquals(SongsGrid.SelectedItem, targetSelected))
        {
            syncingSelection = true;
            SongsGrid.SelectedItem = targetSelected;
            syncingSelection = false;
        }

        syncingDate = true; HistoryPicker.SelectedItem = Library.HistoryDate; syncingDate = false;
        CreatePlaylistAction.Refresh();
    }

    private (bool CollectionActions, bool RefreshAction, bool Editable) HeaderState()
    {
        bool collection = Library.IsCollectionPage && Library.CanDeleteCollection, refresh = Library.IsLocalLibraryPage && !Library.IsCollectionPage;
        return (collection, refresh, (collection || refresh) && Library.CanEdit);
    }

    public DesktopHeaderContribution Header => new("", "", SearchVisible: true, SearchEnabled: true,
        Actions: Library.IsCollectionPage && Library.CanDeleteCollection ? [renamePlaylistAction, deletePlaylistAction] : Library.IsCollectionPage ? [] : Library.IsLocalLibraryPage ? [refreshAction] : [])
    { SearchTargetId = "music" };

    public event EventHandler? HeaderChanged;

    private async Task RenamePlaylistNameAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialogView = new MusicPlaylistNameDialogView(initialName: Library.Heading);
        var request = new DesktopDialogRequest
        {
            Title = "重命名歌单",
            Content = dialogView,
            PrimaryButtonText = "重命名",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Primary,
            IsPrimaryEnabled = dialogView.PlaylistName.Trim().Length > 0,
            OnOpened = () => dialogView.FocusInput(selectAll: true)
        };
        dialogView.NameChanged += (_, _) => request.IsPrimaryEnabled = dialogView.PlaylistName.Trim().Length > 0;
        if (await DesktopModalHost.ShowDialogAsync(this, request, token) != DesktopDialogResult.Primary) return;
        token.ThrowIfCancellationRequested();
        await Library.RenamePlaylistAsync(dialogView.PlaylistName);
    }

    internal async void OpenCreatePlaylistDialog()
    {
        if (playlistDialogOpen || !Library.CanEdit) return;
        var dialogView = new MusicPlaylistNameDialogView(placeholderText: "歌单名称", showCharacterCount: true);
        DesktopDialogRequest request = null!;
        request = new DesktopDialogRequest
        {
            Title = "创建歌单",
            Content = dialogView,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Primary,
            IsPrimaryEnabled = false,
            OnOpened = () => dialogView.FocusInput(selectAll: false),
            OnPrimaryAsync = async () =>
            {
                playlistDialogSubmitting = true;
                dialogView.IsInputEnabled = request.IsPrimaryEnabled = false;
                try { return Library.CanEdit && await Library.TryCreatePlaylistAsync(dialogView.PlaylistName); }
                finally
                {
                    playlistDialogSubmitting = false;
                    dialogView.IsInputEnabled = true;
                    request.IsPrimaryEnabled = dialogView.PlaylistName.Trim().Length > 0 && Library.CanEdit;
                }
            }
        };
        playlistDialogOpen = true;
        CreatePlaylistAction.Refresh();
        dialogView.NameChanged += (_, _) =>
            request.IsPrimaryEnabled = dialogView.PlaylistName.Trim().Length > 0 && Library.CanEdit && !playlistDialogSubmitting;
        try { await DesktopModalHost.ShowDialogAsync(this, request); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Library.ReportUiError(error); }
        finally { playlistDialogOpen = false; playlistDialogSubmitting = false; CreatePlaylistAction.Refresh(); }
    }

    private async void RunUiAction(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Library.ReportUiError(error); }
    }

    internal double CaptureScroll() => ActiveScroll()?.VerticalOffset ?? 0;

    internal void RestoreScroll(double offset) { songScrollIntent.Clear(); if (Visibility == Visibility.Visible) UpdateLayout(); ActiveScroll()?.ChangeView(null, offset, null, true); }

    private ScrollViewer? ActiveScroll() => CurrentContentPage switch
    {
        MusicContentPage.Archives => ArchiveScroll,
        MusicContentPage.Discovery => DesktopVisual.Descendant<ScrollViewer>(DiscoveryView),
        MusicContentPage.Catalog => CatalogView.ActiveScroll,
        _ => songScrolls.GetValueOrDefault(ActiveSongsGrid)
    };

    private void NetworkPlaylist_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Catalog.ToggleNetworkPlaylistAsync());

    private void DailyMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement host) return;
        dailyMoreMenu?.Hide();
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var save = new MenuFlyoutItem { Text = "保存到本地歌单", IsEnabled = Catalog.CanCatalogAction };
        save.Click += (_, _) => RunUiAction(() => Catalog.SaveCatalogPlaylistAsync());
        var refresh = new MenuFlyoutItem { Text = "重新读取", IsEnabled = Catalog.CanCatalogBrowse };
        refresh.Click += (_, _) => RunUiAction(() => Catalog.RefreshCatalogAsync());
        menu.Items.Add(save); menu.Items.Add(refresh);
        MusicContentMenus.ApplyStyles(host, menu);
        var visibility = DesktopVisual.ObserveVisibility(host, visible => { if (!visible) menu.Hide(); });
        menu.Closed += (_, _) => { visibility.Dispose(); if (ReferenceEquals(dailyMoreMenu, menu)) dailyMoreMenu = null; };
        dailyMoreMenu = menu;
        menu.ShowAt(host);
    }

    private async Task ShowDailyStylePopupAsync(long request)
    {
        await dailyStyleRetired;
        if (request != dailyStyleRequest || !Catalog.IsDailyStyleEditorOpen || !DesktopVisual.IsVisible(DailyStyleButton)) return;
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Closed(object? sender, object args) { DailyStylePopup.Closed -= Closed; retired.TrySetResult(); }
        DailyStylePopup.Closed += Closed; dailyStyleRetired = retired.Task; dailyStyleOpen = DailyStylePopup;
        try { DailyStylePopup.ShowAt(DailyStyleButton); }
        catch
        {
            DailyStylePopup.Closed -= Closed;
            if (ReferenceEquals(dailyStyleOpen, DailyStylePopup)) dailyStyleOpen = null;
            retired.TrySetResult();
        }
    }

    private void DailyStylePopup_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (dailyStyleOpen is null) return;
        dailyStyleOpen = null;
        if (Catalog.IsDailyStyleEditorOpen) Catalog.CancelDailyStyleEditor();
    }

    private void DailyBranch_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string branch }) RunUiAction(() => Catalog.SwitchDailyBranchAsync(branch)); }
    private void DailyStyleOpen_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Catalog.OpenDailyStyleEditorAsync());
    private void DailyStyleTag_Click(object sender, RoutedEventArgs e) { if (sender is ButtonBase { Tag: MusicCatalogTag tag }) Catalog.ToggleDailyStyleTag(tag); }

    private void DailyStyleCancel_Click(object sender, RoutedEventArgs e) => Catalog.CancelDailyStyleEditor();
    private void DailyStyleConfirm_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Catalog.ConfirmDailyStyleAsync());
    private void PlayContentDetail_Click(object sender, RoutedEventArgs e) =>
        RunUiAction(() => Catalog.IsCatalogDetailPage ? Catalog.PlayCatalogPlaylistAsync() : Library.PlayCollectionAsync());

    private void HistoryDate_Changed(object sender, SelectionChangedEventArgs e)
    { if (!syncingDate && Library.CanEdit && HistoryPicker.SelectedItem is string day && day != Library.HistoryDate) RunUiAction(() => Library.SelectHistoryDateAsync(day)); }

    private void RestoreArchive_Click(object sender, RoutedEventArgs e) { if (Library.CanEdit && sender is Button { Tag: string id }) RunUiAction(() => Library.RestoreArchiveAsync(id)); }
    private void DeleteArchive_Click(object sender, RoutedEventArgs e) { if (Library.CanEdit && sender is Button { Tag: string id }) RunUiAction(() => Library.DeleteArchiveAsync(id)); }

    private void SongRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && FindRow(element) is { } row)
            RefreshSongRow(row, syncFavorite: true);
    }

    private void SongRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || FindRow(element) is not { } row) return;
        var previous = hoveredSongRow;
        hoveredSongRow = element;
        if (previous is not null && !ReferenceEquals(previous, element) && FindRow(previous) is { } oldRow)
            RefreshSongRow(oldRow, syncFavorite: false);
        RefreshSongRow(row, syncFavorite: false);
    }

    private void SongRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (ReferenceEquals(hoveredSongRow, element)) hoveredSongRow = null;
        if (FindRow(element) is { } row) RefreshSongRow(row, syncFavorite: false);
    }

    private void SongRow_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && FindRow(element) is { } row)
            RefreshSongRow(row, syncFavorite: false);
    }

    private void SongRow_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || FindRow(element) is not { } row) return;
        DispatcherQueue.TryEnqueue(() => { if (row.IsLoaded) RefreshSongRow(row, syncFavorite: false); });
    }

    [ThreadStatic] private static Brush? brushAccent;

    private void RefreshSongRow(SelectorItem container, bool syncFavorite = false)
    {
        if (container.Content is not SongRow song) return;
        bool contrast = DesktopTheme.GetIsHighContrast(container);
        bool playing = song.Id == playback.CurrentSongId;
        if (container.ContentTemplateRoot is FrameworkElement rowRoot)
        {
            bool focused = container.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focus && DesktopVisual.Contains(container, focus);
            bool hovered = ReferenceEquals(hoveredSongRow, rowRoot);
            bool showActions = hovered || container.IsSelected || focused;
            if (rowRoot.FindName("SongTitle") is TextBlock title)
            {
                if (!contrast && playing)
                    title.Foreground = brushAccent ??= (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.Accent");
                else
                    title.ClearValue(TextBlock.ForegroundProperty);
            }
            var muted = (Brush)DesktopVisual.Resource(rowRoot, container.IsSelected ? "SongRow.SelectedMutedFg" : "SongRow.MutedFg");
            if (rowRoot.FindName("SongArtist") is TextBlock artist)
                artist.Foreground = muted;
            if (rowRoot.FindName("SongActions") is StackPanel actions)
            {
                actions.Opacity = showActions ? 1 : 0;
                actions.IsHitTestVisible = showActions;
            }
            if (rowRoot.FindName("SongFavoriteButton") is FavoriteButton favorite)
            {
                favorite.Foreground = muted;
                if (syncFavorite) SyncSongRowFavorite(container, favorite, song);
            }
            if (rowRoot.FindName("SongNextButton") is Button nextButton) nextButton.Foreground = muted;
            if (rowRoot.FindName("SongMoreButton") is Button moreButton) moreButton.Foreground = muted;
        }
        AutomationProperties.SetItemStatus(container, playing ? "正在播放" : "");
    }

    private async void SyncSongRowFavorite(SelectorItem container, FavoriteButton button, SongRow song)
    {
        long epoch = favoritesEpoch;
        if (favoriteCache.TryGetValue(song.Id, out var cached))
        {
            button.IsFavorited = cached;
            AutomationProperties.SetName(button, cached ? "取消收藏" : "收藏");
            return;
        }
        bool favorited = await Library.IsSongFavoritedAsync(song);
        if (!IsLoaded || epoch != favoritesEpoch) return;
        favoriteCache[song.Id] = favorited;
        if (!button.IsLoaded || !ReferenceEquals(container.Content, song)) return;
        button.IsFavorited = favorited;
        AutomationProperties.SetName(button, favorited ? "取消收藏" : "收藏");
    }

    private SongRow? ActiveSelectedSong => Search.IsSearchPage ? Search.SearchSelectedSong :
        Catalog.IsCatalogPage ? Catalog.CatalogSelectedSong : Library.SelectedSong;

    private void SetActiveSelectedSong(SongRow? song)
    {
        if (Search.IsSearchPage) Search.SearchSelectedSong = song;
        else if (Catalog.IsCatalogPage) Catalog.CatalogSelectedSong = song;
        else Library.SelectedSong = song;
    }

    private Task PlayActiveSelectedSongAsync()
    {
        if (Search.IsSearchPage)
        {
            if (Search.SearchSelectedSong is not { } s) return Task.CompletedTask;
            var playCtx = Search.SearchKeyword.Length > 0
                ? MeowFilm.Capabilities.Library.MusicPlayContextRecord.Empty with { SourceType = "search", SearchKeyword = Search.SearchKeyword }
                : null;
            var src = Search.SearchKeyword.Length > 0
                ? new MeowFilm.Capabilities.Library.MusicQueueSourceRecord("catalog-search", Search.SearchKeyword, "search", "")
                : null;
            return Library.PlaySingleSongAsync(s, searchDirect: true, playCtx, src);
        }
        if (Catalog.IsCatalogDetailPage) return Catalog.PlaySelectedCatalogSongAsync();
        return Library.PlaySelectionAsync();
    }

    private void Song_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    { if (Library.CanEdit && sender is FrameworkElement { DataContext: SongRow song }) { SetActiveSelectedSong(song); e.Handled = true; RunUiAction(PlayActiveSelectedSongAsync); } }

    private void Song_RightTapped(object sender, RightTappedRoutedEventArgs e)
    { if (sender is FrameworkElement host && OpenSongMenu(host) is not null) e.Handled = true; }

    internal MenuFlyout? OpenSongMenu(FrameworkElement host)
    {
        if (host.DataContext is not SongRow song) { SetActiveSelectedSong(null); return null; }
        SetActiveSelectedSong(song);
        songMenu?.Hide();
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        MusicContentMenus.PopulateSongMenu(menu, Library, Catalog, Search, song, null, () => host.IsLoaded && DesktopVisual.IsVisible(host) && ReferenceEquals(host.DataContext, song));
        MusicContentMenus.ApplyStyles(host, menu);
        var visibility = DesktopVisual.ObserveVisibility(host, visible => { if (!visible) menu.Hide(); });
        menu.Closed += (_, _) => { visibility.Dispose(); if (ReferenceEquals(songMenu, menu)) songMenu = null; };
        songMenu = menu; menu.ShowAt(host); return menu;
    }

    private void Song_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool hasSelection = Library.CanEdit && ActiveSelectedSong is not null;
        if ((e.Key == VirtualKey.Enter && hasSelection) || (e.Key == VirtualKey.Delete && Library.CanRemoveSelection)) e.Handled = true;
        RunUiAction(() => ExecuteSongKeyAsync(e.Key));
    }

    internal async Task<bool> ExecuteSongKeyAsync(VirtualKey key)
    {
        songScrollIntent.Set(key is VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End); if (songScrollIntent.Active) LoadMoreSongsNearEnd();
        if (key == VirtualKey.Enter && Library.CanEdit && ActiveSelectedSong is not null) { await PlayActiveSelectedSongAsync(); return true; }
        if (key == VirtualKey.Delete && Library.CanRemoveSelection) { await Library.RemoveSongAsync(); return true; }
        return false;
    }

    private void SongsGrid_Wheel(object sender, PointerRoutedEventArgs e) { songScrollIntent.Set(e.GetCurrentPoint(this).Properties.MouseWheelDelta < 0); if (songScrollIntent.Active) LoadMoreSongsNearEnd(); }
    private void SongsScrolled(object? sender, ScrollViewerViewChangedEventArgs e) { if (songScrollIntent.Accepts(ActiveSongsGrid, e.IsIntermediate)) LoadMoreSongsNearEnd(); }

    private async void LoadMoreSongsNearEnd()
    {
        if (loadingMoreSongs || !ActiveSongsGrid.IsLoaded || (!Library.CanLoadMoreCollection && !Catalog.CanLoadMoreCatalogPlaylist && !Search.CanLoadMoreSearchSongs) || songScrolls.GetValueOrDefault(ActiveSongsGrid) is not { } scroll || scroll.ExtentHeight <= 0 || scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - 96) return;
        loadingMoreSongs = true; songScrollIntent.Clear();
        try { if (Library.CanLoadMoreCollection) await Library.LoadMoreCollectionAsync(); else if (Catalog.CanLoadMoreCatalogPlaylist) await Catalog.LoadMoreCatalogPlaylistAsync(); else if (Search.CanLoadMoreSearchSongs) await Search.MoreSearchAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Library.ReportUiError(error); }
        finally { loadingMoreSongs = false; songScrollIntent.Clear(); }
    }

    private bool SelectSong(object sender) { if (sender is Button { Tag: SongRow song }) SetActiveSelectedSong(song); return Library.CanEdit && ActiveSelectedSong is not null; }
    private void NextInQueue_Click(object sender, RoutedEventArgs e)
    {
        if (!SelectSong(sender) || ActiveSelectedSong is not { } song) return;
        var queueContext = Search.IsSearchPage && Search.SearchKeyword.Length > 0
            ? MeowFilm.Capabilities.Library.MusicPlayContextRecord.Empty with { SourceType = "search", SearchKeyword = Search.SearchKeyword }
            : Catalog.CaptureCatalogFeedbackContext();
        RunUiAction(() => Library.AppendSongNextAsync(song, queueContext));
    }
    private void Favorite_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: SongRow song } && SelectSong(sender)) RunUiAction(() => Library.ToggleSongFavoriteAsync(song)); }
    private void SongMore_Click(object sender, RoutedEventArgs e) { if (sender is Button button && SelectSong(sender)) OpenSongMenu(button); }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Library.PageAsync(-1));
    private void NextPage_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Library.PageAsync(1));
    private void SearchTab_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string tab }) RunUiAction(() => Search.OpenSearchTabAsync(tab)); }

    private static ListViewItem? FindRow(DependencyObject? node)
    { while (node is not null) { if (node is ListViewItem row) return row; node = VisualTreeHelper.GetParent(node); } return null; }

    private void Songs_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (!Library.CanReorderPlaylist || e.Items.Count != 1 || e.Items[0] is not SongRow song) { e.Cancel = true; return; }
        draggingSong = song; e.Data.RequestedOperation = DataPackageOperation.Move; e.Data.Properties["meowfilm.playlistSong"] = song;
    }

    private void Songs_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => EndDrag();

    private void Songs_DragOver(object sender, DragEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject);
        if (!Library.CanReorderPlaylist || draggingSong is null || row?.Content is not SongRow target || ReferenceEquals(target, draggingSong)) { e.AcceptedOperation = DataPackageOperation.None; ClearInsertion(); return; }
        bool after = e.GetPosition(row).Y >= row.ActualHeight / 2;
        if (!ReferenceEquals(row, insertionRow) || after != insertAfter)
        {
            ClearInsertion(); insertionRow = row; insertAfter = after;
            row.BorderBrush = (Brush)DesktopVisual.Resource(row, "MeowFilm.Brush.Accent");
            row.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
        }
        e.AcceptedOperation = DataPackageOperation.Move; e.Handled = true;
        if (songScrolls.GetValueOrDefault(SongsGrid) is { } scroll)
        {
            double y = e.GetPosition(SongsGrid).Y;
            if (y < 36) scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset - 12), null, true);
            else if (y > SongsGrid.ActualHeight - 36) scroll.ChangeView(null, Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + 12), null, true);
        }
    }

    private void Songs_Drop(object sender, DragEventArgs e)
    {
        var source = draggingSong; var target = insertionRow?.Content as SongRow; bool after = insertAfter;
        bool accepted = Library.CanReorderPlaylist && source is not null && target is not null && !ReferenceEquals(source, target);
        e.AcceptedOperation = accepted ? DataPackageOperation.Move : DataPackageOperation.None; e.Handled = true; EndDrag();
        if (accepted) RunUiAction(() => Library.ReorderPlaylistSongAsync(source!, target!, after));
    }

    private void ClearInsertion()
    {
        var row = insertionRow;
        insertionRow = null;
        if (row is not null)
        {
            row.ClearValue(Control.BorderBrushProperty);
            row.ClearValue(Control.BorderThicknessProperty);
        }
    }
    private void EndDrag() { ClearInsertion(); draggingSong = null; }
}

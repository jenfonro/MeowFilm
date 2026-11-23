using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
using Windows.UI.ViewManagement;

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
    private readonly Dictionary<ListView, ScrollViewer> songScrolls = [];
    private readonly Dictionary<string, bool> favoriteCache = new(StringComparer.Ordinal);
    private long favoritesEpoch;
    private bool loadingMoreSongs, syncingDate, projectedSearch;
    private bool playlistDialogOpen;
    private MenuFlyout? songMenu, dailyMoreMenu;
    private bool playlistDialogSubmitting;
    private Task dailyStyleRetired = Task.CompletedTask;
    private Flyout? dailyStyleOpen;
    private long dailyStyleRequest;
    private SongRow? draggingSong;
    private ListViewItem? insertionRow;
    private bool insertAfter;
    private (bool CollectionActions, bool RefreshAction, bool Editable) publishedHeaderState;
    private ListView ActiveSongsGrid => Search.IsSearchPage ? SearchSongsGrid : SongsGrid;
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
        content.Show(Library.ContentPage);
        foreach (var list in new[] { SongsGrid, SearchSongsGrid })
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
                if (e.InRecycleQueue) return;
                RefreshSongRow(e.ItemContainer, syncFavorite: true);
            };
        }
        publishedHeaderState = HeaderState();
        Loaded += (_, _) =>
        {
            Library.PropertyChanged -= LibraryChanged; Library.PropertyChanged += LibraryChanged;
            Catalog.PropertyChanged -= CatalogChanged; Catalog.PropertyChanged += CatalogChanged;
            Search.PropertyChanged -= SearchChanged; Search.PropertyChanged += SearchChanged;
            playback.PropertyChanged -= OnPlaybackChanged; playback.PropertyChanged += OnPlaybackChanged;
            Library.SongFavoritesChanged -= OnSongFavoritesChanged; Library.SongFavoritesChanged += OnSongFavoritesChanged;
            Library.Playlists.CollectionChanged -= OnPlaylistsChanged; Library.Playlists.CollectionChanged += OnPlaylistsChanged;
            Bindings.Update();
            PublishHeaderState();
            if (Visibility != Visibility.Visible)
            {
                content.Show(Library.ContentPage);
                Refresh();
            }
            UpdateVisibilityActivity();
            RefreshAllSongRows(syncFavorite: true);
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            Library.PropertyChanged -= LibraryChanged;
            Catalog.PropertyChanged -= CatalogChanged;
            Search.PropertyChanged -= SearchChanged;
            playback.PropertyChanged -= OnPlaybackChanged;
            Library.SongFavoritesChanged -= OnSongFavoritesChanged;
            Library.Playlists.CollectionChanged -= OnPlaylistsChanged;
            UpdateVisibilityActivity();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateVisibilityActivity());
    }

    internal void ResetSearchPresentation() => CatalogView.ResetSearchPlaylistProjection();
    internal void ProjectSearchResults(bool projected) { projectedSearch = projected; Refresh(); }

    private async void UpdateVisibilityActivity()
    {
        bool active = IsLoaded && Visibility == Visibility.Visible;
        if (active)
        {
            content.Show(Library.ContentPage);
            Refresh();
            onPageLoaded();
        }
        else { Catalog.InvalidateCatalogAction(); if (playlistDialogOpen) DesktopModalHost.CloseFor(this); songMenu?.Hide(); dailyMoreMenu?.Hide(); DailyStylePopup.Hide(); songScrollIntent.Clear(); EndDrag(); }
        try { await Task.WhenAll(active ? discovery.ActivateDiscoveryViewAsync() : discovery.DeactivateDiscoveryViewAsync(), Catalog.UpdateVipPageAsync(active), active ? Catalog.ActivateCatalogLandingAsync() : Task.CompletedTask); }
        catch { /* Navigation and shutdown retain final drain ownership. */ }
    }

    private void LibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicLibraryController.ContentPage))
        {
            content.Show(Library.ContentPage);
            Refresh();
        }
        if (e.PropertyName == nameof(MusicLibraryController.CanReorderPlaylist) && !Library.CanReorderPlaylist) EndDrag();
        if (playlistDialogOpen && !playlistDialogSubmitting && e.PropertyName is nameof(MusicLibraryController.Heading) or nameof(MusicLibraryController.IsCollectionPage)) DesktopModalHost.CloseFor(this);
        if (e.PropertyName is nameof(MusicLibraryController.HistoryDate) or nameof(MusicLibraryController.CanEdit) or
            nameof(MusicLibraryController.DetailArtwork) or nameof(MusicLibraryController.IsContentDetailPage)) Refresh();
        if (e.PropertyName is nameof(MusicLibraryController.IsCollectionPage) or nameof(MusicLibraryController.CanDeleteCollection) or
            nameof(MusicLibraryController.ContentPage) or nameof(MusicLibraryController.CanEdit))
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
        if (e.PropertyName is nameof(MusicCatalogController.IsCatalogHome) or nameof(MusicCatalogController.HasChannelTabs) or
            nameof(MusicCatalogController.CurrentChannelKey) or nameof(MusicCatalogController.CanEdit) or
            nameof(MusicCatalogController.IsCatalogArtist)) Refresh();
    }

    private void SearchChanged(object? sender, PropertyChangedEventArgs e)
    {
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
        foreach (var list in new[] { SongsGrid, SearchSongsGrid })
            for (int i = 0; i < list.Items.Count; i++) if (list.ContainerFromIndex(i) is ListViewItem row) RefreshSongRow(row, syncFavorite);
    }

    private void Refresh()
    {
        PageContent.Margin = projectedSearch ? new Thickness(0) : Catalog.IsCatalogLanding ? new Thickness(0, 12, 0, 0) : new Thickness(40, 16, 24, 20);
        PageContent.MaxWidth = Catalog.IsCatalogLanding ? double.PositiveInfinity : 1490;
        SearchHeading.Visibility = !projectedSearch && Search.IsSearchPage ? Visibility.Visible : Visibility.Collapsed;
        SearchLoading.IsActive = Search.ShowSearchLoading && Search.IsSearchSongs;
        SearchLoading.Visibility = SearchLoading.IsActive ? Visibility.Visible : Visibility.Collapsed;
        double artworkRadius = Catalog.IsCatalogArtist ? 76 : 18;
        DetailArtworkFrame.CornerRadius = new CornerRadius(artworkRadius);
        CardClip.SetRadius(DetailArtworkFrame, artworkRadius);
        DetailArtworkFallback.Visibility = string.IsNullOrWhiteSpace(Library.DetailArtwork) ? Visibility.Visible : Visibility.Collapsed;
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

    private ScrollViewer? ActiveScroll() => Library.ContentPage switch
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
    private void DailyStyleTag_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: MusicCatalogTag tag }) Catalog.ToggleDailyStyleTag(tag); }

    private static readonly ConditionalWeakTable<Button, DailyTagChrome> DailyTagChromes = new();
    private static readonly ConditionalWeakTable<ListViewItem, SongContainerChrome> SongContainerChromes = new();

    private void DailyTag_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicCatalogTag tag } button) return;
        DailyTagChromes.GetValue(button, static b => new DailyTagChrome(b)).Attach(tag);
    }

    private sealed class DailyTagChrome
    {
        private readonly Button button;
        private MusicCatalogTag? tag;
        private bool pointer, attached;
        private long pressedToken, enabledToken;
        private IDisposable? theme;

        internal DailyTagChrome(Button button)
        {
            this.button = button;
            button.Unloaded += OnUnloaded;
        }

        internal void Attach(MusicCatalogTag next)
        {
            tag = next;
            if (!attached)
            {
                attached = true;
                button.PointerEntered += OnEntered;
                button.PointerExited += OnExited;
                pressedToken = button.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, (_, _) => Refresh());
                enabledToken = button.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => Refresh());
                theme = DesktopTheme.Observe(button, Refresh);
            }
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            if (button.IsLoaded) return;
            pointer = false;
            if (!attached) return;
            attached = false;
            button.PointerEntered -= OnEntered;
            button.PointerExited -= OnExited;
            button.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, pressedToken);
            button.UnregisterPropertyChangedCallback(IsEnabledProperty, enabledToken);
            pressedToken = enabledToken = 0;
            theme?.Dispose();
            theme = null;
        }

        private void OnEntered(object sender, PointerRoutedEventArgs args) { pointer = true; Refresh(); }
        private void OnExited(object sender, PointerRoutedEventArgs args) { pointer = false; Refresh(); }

        private void Refresh()
        {
            if (tag is null) return;
            static Brush SystemBrush(UIElementType type) => SystemAppearance.GetElementBrush(type);
            Brush Resource(string name) => (Brush)DesktopVisual.Resource(button, name);
            if (DesktopTheme.GetIsHighContrast(button))
            {
                button.Background = SystemBrush(tag.Selected ? UIElementType.Highlight : UIElementType.ButtonFace);
                button.Foreground = button.BorderBrush = SystemBrush(!button.IsEnabled ? UIElementType.GrayText : tag.Selected ? UIElementType.HighlightText : UIElementType.ButtonText);
            }
            else
            {
                button.Background = Resource(button.IsPressed ? "MeowFilm.Brush.Pressed" : pointer ? "MeowFilm.Brush.Hover" : "MeowFilm.Brush.InputSurface");
                button.Foreground = Resource(!button.IsEnabled ? "MeowFilm.Brush.Disabled" : tag.Selected ? "MeowFilm.Brush.Accent" : "MeowFilm.Brush.Text");
                button.BorderBrush = Resource(tag.Selected ? "MeowFilm.Brush.Accent" : "MeowFilm.Brush.Border");
            }
            AutomationProperties.SetItemStatus(button, tag.Selected ? "已选中" : "");
        }
    }

    private void DailyStyleCancel_Click(object sender, RoutedEventArgs e) => Catalog.CancelDailyStyleEditor();
    private void DailyStyleConfirm_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Catalog.ConfirmDailyStyleAsync());
    private void PlayContentDetail_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Library.PlayContentDetailAsync());

    private void HistoryDate_Changed(object sender, SelectionChangedEventArgs e)
    { if (!syncingDate && Library.CanEdit && HistoryPicker.SelectedItem is string day && day != Library.HistoryDate) RunUiAction(() => Library.SelectHistoryDateAsync(day)); }

    private void RestoreArchive_Click(object sender, RoutedEventArgs e) { if (Library.CanEdit && sender is Button { Tag: string id }) RunUiAction(() => Library.RestoreArchiveAsync(id)); }
    private void DeleteArchive_Click(object sender, RoutedEventArgs e) { if (Library.CanEdit && sender is Button { Tag: string id }) RunUiAction(() => Library.DeleteArchiveAsync(id)); }

    private void SongRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || FindRow(element) is not { } row) return;
        SongContainerChromes.GetValue(row, static r => new SongContainerChrome(r)).BindRowVisuals(element);
        RefreshSongRow(row, syncFavorite: true);
    }

    private void SongContainer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement template || FindRow(template) is not { } row) return;
        SongContainerChromes.GetValue(row, static r => new SongContainerChrome(r)).AttachTemplate(this, template);
    }

    private sealed class SongContainerChrome
    {
        internal ListViewItem Row { get; }
        private MusicView? owner;
        private FrameworkElement? template;
        private FrameworkElement? rowRoot;
        private VisualStateGroup? commonStates;
        private long selectedToken;
        private IDisposable? theme;

        internal bool HasRowVisuals => rowRoot is not null;
        internal string? CommonStateName => commonStates?.CurrentState?.Name;
        internal TextBlock? TitleText { get; private set; }
        internal TextBlock? ArtistText { get; private set; }
        internal List<TextBlock> OtherTexts { get; } = [];
        internal StackPanel? ActionsPanel { get; private set; }
        internal FavoriteButton? FavoriteButton { get; private set; }
        internal List<Button> ActionButtons { get; } = [];
        internal string? FavoriteSongId;
        internal long FavoriteEpoch = -1;

        internal SongContainerChrome(ListViewItem row)
        {
            Row = row;
        }

        internal void BindRowVisuals(FrameworkElement root)
        {
            if (ReferenceEquals(rowRoot, root)) return;
            rowRoot = root;
            TitleText = null;
            ArtistText = null;
            OtherTexts.Clear();
            ActionsPanel = null;
            FavoriteButton = null;
            ActionButtons.Clear();
            foreach (var node in DesktopVisual.Descendants(root))
            {
                if (node is TextBlock text)
                {
                    if (text.Name == "SongTitle") TitleText = text;
                    else if (text.Name == "SongArtist") ArtistText = text;
                    else OtherTexts.Add(text);
                }
                else if (node is StackPanel { Name: "SongActions" } actions) ActionsPanel = actions;
                else if (node is FavoriteButton favorite) FavoriteButton = favorite;
                else if (node is Button button) ActionButtons.Add(button);
            }
        }

        internal void AttachTemplate(MusicView view, FrameworkElement nextTemplate)
        {
            if (ReferenceEquals(owner, view) && ReferenceEquals(template, nextTemplate) && commonStates is not null)
            {
                view.RefreshSongRow(Row, syncFavorite: false);
                return;
            }
            DetachTemplate();
            owner = view;
            template = nextTemplate;
            template.Unloaded += OnUnloaded;
            commonStates = VisualStateManager.GetVisualStateGroups(template).Single(group => group.Name == "CommonStates");
            selectedToken = Row.RegisterPropertyChangedCallback(SelectorItem.IsSelectedProperty, (_, _) => owner?.RefreshSongRow(Row, syncFavorite: false));
            commonStates.CurrentStateChanged += StateChanged;
            Row.GotFocus += OnGotFocus;
            Row.LostFocus += OnLostFocus;
            theme = DesktopTheme.Observe(Row, () => owner?.RefreshSongRow(Row, syncFavorite: false));
            view.RefreshSongRow(Row, syncFavorite: false);
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            if (template is not { IsLoaded: false }) return;
            DetachTemplate();
        }

        private void DetachTemplate()
        {
            if (template is not null) template.Unloaded -= OnUnloaded;
            template = null;
            if (commonStates is not null) commonStates.CurrentStateChanged -= StateChanged;
            commonStates = null;
            if (selectedToken != 0) Row.UnregisterPropertyChangedCallback(SelectorItem.IsSelectedProperty, selectedToken);
            selectedToken = 0;
            Row.GotFocus -= OnGotFocus;
            Row.LostFocus -= OnLostFocus;
            theme?.Dispose();
            theme = null;
            owner = null;
        }

        private void StateChanged(object sender, VisualStateChangedEventArgs args) => owner?.RefreshSongRow(Row, syncFavorite: false);
        private void OnGotFocus(object sender, RoutedEventArgs args) => owner?.RefreshSongRow(Row, syncFavorite: false);
        private void OnLostFocus(object sender, RoutedEventArgs args)
        {
            var capturedOwner = owner;
            if (capturedOwner is not null)
                capturedOwner.DispatcherQueue.TryEnqueue(() => { if (Row.IsLoaded) capturedOwner.RefreshSongRow(Row, syncFavorite: false); });
        }
    }

    private static readonly SolidColorBrush SelectedSongBackground = DesktopVisual.Brush("#E9F0F6");
    private static readonly SolidColorBrush HoveredSongBackground = DesktopVisual.Brush("#FFFFFF");
    private static readonly SolidColorBrush TransparentSongBackground = new(Microsoft.UI.Colors.Transparent);
    private static readonly SolidColorBrush SelectedSongBorder = DesktopVisual.Brush("#9AAEBF");

    private void RefreshSongRow(SelectorItem container, bool syncFavorite = false)
    {
        if (container.Content is not SongRow song) return;
        SongContainerChrome? chrome = container is ListViewItem listItem && SongContainerChromes.TryGetValue(listItem, out var found) ? found : null;
        bool contrast = DesktopTheme.GetIsHighContrast(container);
        bool focused = container.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focus && DesktopVisual.Contains(container, focus);
        var state = chrome?.CommonStateName;
        bool hovered = state is "PointerOver" or "PointerOverSelected" or "Pressed" or "PressedSelected";
        bool showActions = hovered || container.IsSelected || focused;
        static Brush SystemBrush(UIElementType type) => SystemAppearance.GetElementBrush(type);
        Brush Resource(string name) => (Brush)DesktopVisual.Resource(container, name);
        Brush foreground = contrast ? SystemBrush(container.IsSelected ? UIElementType.HighlightText : UIElementType.WindowText) : Resource("MeowFilm.Brush.Text");
        container.Foreground = foreground;
        container.Background = contrast ? SystemBrush(container.IsSelected ? UIElementType.Highlight : UIElementType.Window) :
            container.IsSelected ? SelectedSongBackground : hovered ? HoveredSongBackground : TransparentSongBackground;
        if (!ReferenceEquals(container, insertionRow))
        {
            container.BorderBrush = contrast ? foreground : focused ? Resource("MeowFilm.Brush.Text") : SelectedSongBorder;
            container.BorderThickness = new Thickness(container.IsSelected || focused ? 1 : 0);
        }
        if (chrome is { HasRowVisuals: true })
        {
            Brush muted = contrast ? foreground : Resource("MeowFilm.Brush.Muted");
            if (chrome.TitleText is { } title)
                title.Foreground = !contrast && song.Id == playback.CurrentSongId ? Resource("MeowFilm.Brush.Accent") : foreground;
            if (chrome.ArtistText is { } artist)
                artist.Foreground = muted;
            foreach (var text in chrome.OtherTexts) text.Foreground = foreground;
            if (chrome.ActionsPanel is { } actions)
            {
                actions.Opacity = showActions ? 1 : 0;
                actions.IsHitTestVisible = showActions;
            }
            if (chrome.FavoriteButton is { } favorite)
            {
                favorite.Foreground = muted;
                if (syncFavorite && (chrome.FavoriteEpoch != favoritesEpoch || chrome.FavoriteSongId != song.Id))
                    SyncSongRowFavorite(chrome, favorite, song);
            }
            foreach (var button in chrome.ActionButtons) button.Foreground = muted;
        }
        AutomationProperties.SetItemStatus(container, song.Id == playback.CurrentSongId ? "正在播放" : "");
    }

    private async void SyncSongRowFavorite(SongContainerChrome chrome, FavoriteButton button, SongRow song)
    {
        long epoch = favoritesEpoch;
        if (favoriteCache.TryGetValue(song.Id, out var cached))
        {
            chrome.FavoriteEpoch = epoch;
            chrome.FavoriteSongId = song.Id;
            button.IsFavorited = cached;
            AutomationProperties.SetName(button, cached ? "取消收藏" : "收藏");
            return;
        }
        bool favorited = await Library.IsSongFavoritedAsync(song);
        if (!IsLoaded || epoch != favoritesEpoch) return;
        favoriteCache[song.Id] = favorited;
        if (!button.IsLoaded || !ReferenceEquals(chrome.Row.Content, song)) return;
        chrome.FavoriteEpoch = epoch;
        chrome.FavoriteSongId = song.Id;
        button.IsFavorited = favorited;
        AutomationProperties.SetName(button, favorited ? "取消收藏" : "收藏");
    }

    private void Song_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    { if (Library.CanEdit && sender is FrameworkElement { DataContext: SongRow song }) { Library.SelectedSong = song; e.Handled = true; RunUiAction(() => Library.PlayContentSelectionAsync()); } }

    private void Song_RightTapped(object sender, RightTappedRoutedEventArgs e)
    { if (sender is FrameworkElement host && OpenSongMenu(host) is not null) e.Handled = true; }

    internal MenuFlyout? OpenSongMenu(FrameworkElement host)
    {
        if (host.DataContext is not SongRow song) { Library.SelectedSong = null; return null; }
        Library.SelectedSong = song;
        songMenu?.Hide();
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        MusicContentMenus.PopulateSongMenu(menu, Library, Catalog, song, null, () => host.IsLoaded && DesktopVisual.IsVisible(host) && ReferenceEquals(host.DataContext, song));
        MusicContentMenus.ApplyStyles(host, menu);
        var visibility = DesktopVisual.ObserveVisibility(host, visible => { if (!visible) menu.Hide(); });
        menu.Closed += (_, _) => { visibility.Dispose(); if (ReferenceEquals(songMenu, menu)) songMenu = null; };
        songMenu = menu; menu.ShowAt(host); return menu;
    }

    private void Song_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((e.Key == VirtualKey.Enter && Library.HasSelection) || (e.Key == VirtualKey.Delete && Library.CanRemoveSelection)) e.Handled = true;
        RunUiAction(() => ExecuteSongKeyAsync(e.Key));
    }

    internal async Task<bool> ExecuteSongKeyAsync(VirtualKey key)
    {
        songScrollIntent.Set(key is VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End); if (songScrollIntent.Active) LoadMoreSongsNearEnd();
        if (key == VirtualKey.Enter && Library.HasSelection) { await Library.PlayContentSelectionAsync(); return true; }
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

    private bool SelectSong(object sender) { if (sender is Button { Tag: SongRow song }) Library.SelectedSong = song; return Library.CanEdit && Library.HasSelection; }
    private void NextInQueue_Click(object sender, RoutedEventArgs e) { if (SelectSong(sender)) RunUiAction(() => Library.AppendNextAsync()); }
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
            row.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
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

    private void ClearInsertion() { var row = insertionRow; insertionRow = null; if (row is not null) RefreshSongRow(row, syncFavorite: false); }
    private void EndDrag() { ClearInsertion(); draggingSong = null; }
}

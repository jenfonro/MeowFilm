using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MeowFilm.Capabilities;

public partial class MusicView : UserControl, IDesktopHeaderSource
{
    private readonly MusicCapability module;
    private readonly DesktopHeaderAction refreshAction, renamePlaylistAction, deletePlaylistAction;
    private readonly UserScrollIntent songScrollIntent;
    private readonly MusicPagePresenter content;
    private bool loadingMoreSongs, syncingDate, projectedSearch;
    private ContentDialog? playlistDialog;
    private MenuFlyout? songMenu;
    private bool playlistDialogSubmitting;
    private SongRow? draggingSong;
    private ListViewItem? insertionRow;
    private bool insertAfter;
    private (bool CollectionActions, bool RefreshAction, bool Editable) publishedHeaderState;
    private ListView ActiveSongsGrid => module.IsSearchPage ? SearchSongsGrid : SongsGrid;
    internal bool IsPlaylistDialogOpen => playlistDialog is not null;
    internal DesktopNavigationGroupAction CreatePlaylistAction { get; }
    public MusicView(MusicCapability module)
    {
        this.module = module; songScrollIntent = new();
        CreatePlaylistAction = module.Presentation.CreatePlaylistAction;
        refreshAction = new("music.refresh", "刷新", async token => { token.ThrowIfCancellationRequested(); await module.RefreshAsync(); token.ThrowIfCancellationRequested(); }, () => module.CanEdit, automationName: "刷新音乐内容");
        renamePlaylistAction = new("music.playlist.rename", "重命名", RenamePlaylistNameAsync, () => module.CanEdit && module.CanDeleteCollection);
        deletePlaylistAction = new("music.playlist.delete", "删除", token => { token.ThrowIfCancellationRequested(); return module.DeletePlaylistAsync(); }, () => module.CanEdit && module.CanDeleteCollection);
        InitializeComponent(); DataContext = module; ((FrameworkElement)DailyStylePopup.Content).DataContext = module;
        foreach (FrameworkElement page in BodyPages.Children) page.DataContext = module;
        BodyPages.Children.Clear();
        content = new(new() { [MusicContentPage.Catalog] = CatalogView, [MusicContentPage.Discovery] = DiscoveryView, [MusicContentPage.Archives] = ArchiveScroll, [MusicContentPage.Songs] = SongsPage, [MusicContentPage.SearchSongs] = SearchSongsPage });
        BodyPages.Children.Add(content); content.Show(module.ContentPage);
        foreach (var list in new[] { SongsGrid, SearchSongsGrid })
        {
            list.Loaded += (_, _) =>
            {
                if (MusicVisuals.Descendant<ScrollViewer>(list) is not { } scroll) return;
                scroll.ViewChanged -= SongsScrolled; scroll.ViewChanged += SongsScrolled;
                songScrollIntent.Attach(scroll);
            };
            list.ContainerContentChanging += (_, e) => RefreshSongRow(e.ItemContainer);
        }
        publishedHeaderState = HeaderState();
        Loaded += (_, _) => { module.PropertyChanged -= ModuleChanged; module.PropertyChanged += ModuleChanged; Refresh(); UpdateVisibilityActivity(); };
        Unloaded += (_, _) => { module.PropertyChanged -= ModuleChanged; UpdateVisibilityActivity(); };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateVisibilityActivity());
    }
    internal void ResetSearchPresentation() => CatalogView.ResetSearchPlaylistProjection();
    internal void ProjectSearchResults(bool projected) { projectedSearch = projected; Refresh(); }
    private async void UpdateVisibilityActivity()
    {
        bool active = IsLoaded && Visibility == Visibility.Visible;
        if (active) module.Presentation.PageLoaded();
        else { module.InvalidateCatalogAction(); playlistDialog?.Hide(); songMenu?.Hide(); DailyStylePopup.Hide(); songScrollIntent.Clear(); EndDrag(); }
        try { await Task.WhenAll(active ? module.ActivateDiscoveryViewAsync() : module.DeactivateDiscoveryViewAsync(), module.UpdateVipPageAsync(active), active ? module.ActivateCatalogLandingAsync() : Task.CompletedTask); }
        catch { /* Navigation and shutdown retain final drain ownership. */ }
    }
    private void ModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicCapability.ContentPage)) content.Show(module.ContentPage);
        if (!module.CanReorderPlaylist) EndDrag();
        if (playlistDialog is not null && !playlistDialogSubmitting && e.PropertyName is nameof(MusicCapability.Heading) or nameof(MusicCapability.IsCollectionPage)) playlistDialog.Hide();
        if (e.PropertyName == nameof(MusicCapability.IsDailyStyleEditorOpen))
        { if (module.IsDailyStyleEditorOpen) DailyStylePopup.ShowAt(DailyStyleButton); else DailyStylePopup.Hide(); }
        Refresh();
        var state = HeaderState(); if (state != publishedHeaderState) { publishedHeaderState = state; HeaderChanged?.Invoke(this, EventArgs.Empty); }
        if (e.PropertyName == nameof(MusicCapability.CurrentSongId))
            foreach (var list in new[] { SongsGrid, SearchSongsGrid })
                for (int i = 0; i < list.Items.Count; i++) if (list.ContainerFromIndex(i) is ListViewItem row) RefreshSongRow(row);
    }
    private void Refresh()
    {
        PageContent.Margin = projectedSearch ? new Thickness(0) : module.IsCatalogLanding ? new Thickness(0, 12, 0, 0) : new Thickness(40, 16, 24, 20);
        PageContent.MaxWidth = module.IsCatalogLanding ? double.PositiveInfinity : 1490;
        SearchHeading.Visibility = !projectedSearch && module.IsSearchPage ? Visibility.Visible : Visibility.Collapsed;
        SearchSkeleton.Visibility = module.ShowSearchSkeleton && module.IsSearchSongs ? Visibility.Visible : Visibility.Collapsed;
        DetailArtworkFrame.CornerRadius = new CornerRadius(module.IsCatalogArtist ? 76 : 18);
        syncingDate = true; HistoryPicker.SelectedItem = module.HistoryDate; syncingDate = false;
        CreatePlaylistAction.Refresh();
    }
    private (bool CollectionActions, bool RefreshAction, bool Editable) HeaderState()
    {
        bool collection = module.IsCollectionPage && module.CanDeleteCollection, refresh = module.IsLocalLibraryPage && !module.IsCollectionPage;
        return (collection, refresh, (collection || refresh) && module.CanEdit);
    }
    public DesktopHeaderContribution Header => new("", "", SearchVisible: true, SearchEnabled: true,
        Actions: module.IsCollectionPage && module.CanDeleteCollection ? [renamePlaylistAction, deletePlaylistAction] : module.IsCollectionPage ? [] : module.IsLocalLibraryPage ? [refreshAction] : []) { SearchTargetId = "music" };
    public event EventHandler? HeaderChanged;
    private async Task RenamePlaylistNameAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var input = new TextBox { Text = module.Heading };
        AutomationProperties.SetName(input, "歌单名称");
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "重命名歌单", Content = input, PrimaryButtonText = "重命名", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = input.Text.Trim().Length > 0 };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = input.Text.Trim().Length > 0;
        dialog.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        using var registration = token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        token.ThrowIfCancellationRequested(); await module.RenamePlaylistAsync(input.Text);
    }
    internal async void OpenCreatePlaylistDialog()
    {
        if (playlistDialog is not null || !module.CanEdit) return;
        var input = new TextBox { PlaceholderText = "歌单名称" };
        var count = new TextBlock { Text = "0", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12 };
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(input); panel.Children.Add(count);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "创建歌单", Content = panel, PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false };
        playlistDialog = dialog; CreatePlaylistAction.Refresh();
        input.TextChanged += (_, _) => { count.Text = $"{input.Text.Length}"; dialog.IsPrimaryButtonEnabled = input.Text.Trim().Length > 0 && module.CanEdit && !playlistDialogSubmitting; };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral(); playlistDialogSubmitting = true; input.IsEnabled = dialog.IsPrimaryButtonEnabled = false;
            try { e.Cancel = !module.CanEdit || !await module.TryCreatePlaylistAsync(input.Text); }
            finally { playlistDialogSubmitting = false; input.IsEnabled = true; dialog.IsPrimaryButtonEnabled = input.Text.Trim().Length > 0 && module.CanEdit; deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); }
        finally { if (ReferenceEquals(playlistDialog, dialog)) playlistDialog = null; playlistDialogSubmitting = false; CreatePlaylistAction.Refresh(); }
    }
    internal double CaptureScroll() => ActiveScroll()?.VerticalOffset ?? 0;
    internal void RestoreScroll(double offset) { songScrollIntent.Clear(); if (Visibility == Visibility.Visible) UpdateLayout(); ActiveScroll()?.ChangeView(null, offset, null, true); }
    private ScrollViewer? ActiveScroll() => module.ContentPage switch
    { MusicContentPage.Archives => ArchiveScroll, MusicContentPage.Discovery => MusicVisuals.Descendant<ScrollViewer>(DiscoveryView), MusicContentPage.Catalog => CatalogView.ActiveScroll, _ => MusicVisuals.Descendant<ScrollViewer>(ActiveSongsGrid) };
    private async void NetworkPlaylist_Click(object sender, RoutedEventArgs e) => await module.ToggleNetworkPlaylistAsync();
    private void DailyMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout(); var save = new MenuFlyoutItem { Text = "保存到本地歌单", IsEnabled = module.CanCatalogAction };
        save.Click += async (_, _) => await module.SaveCatalogPlaylistAsync();
        var refresh = new MenuFlyoutItem { Text = "重新读取", IsEnabled = module.CanCatalogBrowse }; refresh.Click += async (_, _) => await module.RefreshCatalogAsync();
        menu.Items.Add(save); menu.Items.Add(refresh); menu.ShowAt((FrameworkElement)sender);
    }
    private void DailyStylePopup_Closed(object? sender, object e) => module.CancelDailyStyleEditor();
    private async void DailyBranch_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string branch }) await module.SwitchDailyBranchAsync(branch); }
    private async void DailyStyleOpen_Click(object sender, RoutedEventArgs e) => await module.OpenDailyStyleEditorAsync();
    private void DailyStyleTag_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: MusicCatalogTag tag }) module.ToggleDailyStyleTag(tag); }
    private void DailyTag_Loaded(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicCatalogTag tag } button) { button.BorderBrush = (Brush)Application.Current.Resources[tag.Selected ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush"]; AutomationProperties.SetItemStatus(button, tag.Selected ? "已选中" : ""); } }
    private void DailyStyleCancel_Click(object sender, RoutedEventArgs e) => module.CancelDailyStyleEditor();
    private async void DailyStyleConfirm_Click(object sender, RoutedEventArgs e) => await module.ConfirmDailyStyleAsync();
    private async void PlayContentDetail_Click(object sender, RoutedEventArgs e) => await module.PlayContentDetailAsync();
    private async void HistoryDate_Changed(object sender, SelectionChangedEventArgs e)
    { if (!syncingDate && module.CanEdit && HistoryPicker.SelectedItem is string day && day != module.HistoryDate) await module.SelectHistoryDateAsync(day); }
    private async void RestoreArchive_Click(object sender, RoutedEventArgs e) { if (module.CanEdit && sender is Button { Tag: string id }) await module.RestoreArchiveAsync(id); }
    private async void DeleteArchive_Click(object sender, RoutedEventArgs e) { if (module.CanEdit && sender is Button { Tag: string id }) await module.DeleteArchiveAsync(id); }
    private void SongRow_Loaded(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement element && FindRow(element) is { } row) RefreshSongRow(row); }
    private void RefreshSongRow(SelectorItem container)
    {
        if (container.Content is not SongRow song) return;
        var title = MusicVisuals.Descendant<Grid>(container)?.FindName("SongTitle") as TextBlock;
        if (title is not null) title.Foreground = (Brush)Application.Current.Resources[song.Id == module.CurrentSongId ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];
        AutomationProperties.SetItemStatus(container, song.Id == module.CurrentSongId ? "正在播放" : "");
    }
    private async void Song_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    { if (module.CanEdit && sender is FrameworkElement { DataContext: SongRow song }) { module.SelectedSong = song; e.Handled = true; await module.PlayContentSelectionAsync(); } }
    private void Song_RightTapped(object sender, RightTappedRoutedEventArgs e)
    { if (sender is FrameworkElement host && OpenSongMenu(host) is not null) e.Handled = true; }
    internal MenuFlyout? OpenSongMenu(FrameworkElement host)
    {
        if (host.DataContext is not SongRow song) { module.SelectedSong = null; return null; }
        module.SelectedSong = song;
        songMenu?.Hide();
        var menu = new MenuFlyout(); module.PopulateSongMenu(menu, song, null, () => host.IsLoaded && DesktopVisual.IsVisible(host) && ReferenceEquals(host.DataContext, song));
        MusicCapability.ApplyContentMenuStyles(host, menu);
        var visibility = DesktopVisual.ObserveVisibility(host, visible => { if (!visible) menu.Hide(); });
        menu.Closed += (_, _) => { visibility.Dispose(); if (ReferenceEquals(songMenu, menu)) songMenu = null; };
        songMenu = menu; menu.ShowAt(host); return menu;
    }
    private async void Song_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((e.Key == VirtualKey.Enter && module.HasSelection) || (e.Key == VirtualKey.Delete && module.CanRemoveSelection)) e.Handled = true;
        await ExecuteSongKeyAsync(e.Key);
    }
    internal async Task<bool> ExecuteSongKeyAsync(VirtualKey key)
    {
        songScrollIntent.Set(key is VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End); if (songScrollIntent.Active) LoadMoreSongsNearEnd();
        if (key == VirtualKey.Enter && module.HasSelection) { await module.PlayContentSelectionAsync(); return true; }
        if (key == VirtualKey.Delete && module.CanRemoveSelection) { await module.RemoveSongAsync(); return true; }
        return false;
    }
    private void SongsGrid_Wheel(object sender, PointerRoutedEventArgs e) { songScrollIntent.Set(e.GetCurrentPoint(this).Properties.MouseWheelDelta < 0); if (songScrollIntent.Active) LoadMoreSongsNearEnd(); }
    private void SongsScrolled(object? sender, ScrollViewerViewChangedEventArgs e) { if (songScrollIntent.Accepts(ActiveSongsGrid, e.IsIntermediate)) LoadMoreSongsNearEnd(); }
    private async void LoadMoreSongsNearEnd()
    {
        if (loadingMoreSongs || !ActiveSongsGrid.IsLoaded || (!module.CanLoadMoreCollection && !module.CanLoadMoreCatalogPlaylist && !module.CanLoadMoreSearchSongs) || MusicVisuals.Descendant<ScrollViewer>(ActiveSongsGrid) is not { } scroll || scroll.ExtentHeight <= 0 || scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - 96) return;
        loadingMoreSongs = true; songScrollIntent.Clear();
        try { if (module.CanLoadMoreCollection) await module.LoadMoreCollectionAsync(); else if (module.CanLoadMoreCatalogPlaylist) await module.LoadMoreCatalogPlaylistAsync(); else if (module.CanLoadMoreSearchSongs) await module.MoreSearchAsync(); }
        finally { loadingMoreSongs = false; songScrollIntent.Clear(); }
    }
    private bool SelectSong(object sender) { if (sender is Button { Tag: SongRow song }) module.SelectedSong = song; return module.CanEdit && module.HasSelection; }
    private async void NextInQueue_Click(object sender, RoutedEventArgs e) { if (SelectSong(sender)) await module.AppendNextAsync(); }
    private async void Favorite_Click(object sender, RoutedEventArgs e) { if (SelectSong(sender)) await module.FavoriteAsync(); }
    private void SongMore_Click(object sender, RoutedEventArgs e) { if (sender is Button button && SelectSong(sender)) OpenSongMenu(button); }
    private async void PreviousPage_Click(object sender, RoutedEventArgs e) => await module.PageAsync(-1);
    private async void NextPage_Click(object sender, RoutedEventArgs e) => await module.PageAsync(1);
    private async void SearchTab_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string tab }) await module.OpenSearchTabAsync(tab); }
    private static ListViewItem? FindRow(DependencyObject? node)
    { while (node is not null) { if (node is ListViewItem row) return row; node = VisualTreeHelper.GetParent(node); } return null; }
    private void Songs_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (!module.CanReorderPlaylist || e.Items.Count != 1 || e.Items[0] is not SongRow song) { e.Cancel = true; return; }
        draggingSong = song; e.Data.RequestedOperation = DataPackageOperation.Move; e.Data.Properties["meowfilm.playlistSong"] = song;
    }
    private void Songs_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => EndDrag();
    private void Songs_DragOver(object sender, DragEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject);
        if (!module.CanReorderPlaylist || draggingSong is null || row?.Content is not SongRow target || ReferenceEquals(target, draggingSong)) { e.AcceptedOperation = DataPackageOperation.None; ClearInsertion(); return; }
        bool after = e.GetPosition(row).Y >= row.ActualHeight / 2;
        if (!ReferenceEquals(row, insertionRow) || after != insertAfter)
        {
            ClearInsertion(); insertionRow = row; insertAfter = after;
            row.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            row.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
        }
        e.AcceptedOperation = DataPackageOperation.Move; e.Handled = true;
        if (MusicVisuals.Descendant<ScrollViewer>(SongsGrid) is { } scroll)
        {
            double y = e.GetPosition(SongsGrid).Y;
            if (y < 36) scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset - 12), null, true);
            else if (y > SongsGrid.ActualHeight - 36) scroll.ChangeView(null, Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + 12), null, true);
        }
    }
    private async void Songs_Drop(object sender, DragEventArgs e)
    {
        var source = draggingSong; var target = insertionRow?.Content as SongRow; bool after = insertAfter;
        bool accepted = module.CanReorderPlaylist && source is not null && target is not null && !ReferenceEquals(source, target);
        e.AcceptedOperation = accepted ? DataPackageOperation.Move : DataPackageOperation.None; e.Handled = true; EndDrag();
        if (accepted) await module.ReorderPlaylistSongAsync(source!, target!, after);
    }
    private void ClearInsertion() { insertionRow?.ClearValue(Control.BorderBrushProperty); insertionRow?.ClearValue(Control.BorderThicknessProperty); insertionRow = null; }
    private void EndDrag() { ClearInsertion(); draggingSong = null; }
}

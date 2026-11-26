using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal static class MusicContentMenus
{
    internal static void ApplyStyles(FrameworkElement host, MenuFlyout menu)
    {
        menu.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        menu.XamlRoot = host.XamlRoot;
        menu.MenuFlyoutPresenterStyle = (Style)DesktopVisual.Resource(host, "MeowFilm.ContextMenu");
        var itemStyle = (Style)DesktopVisual.Resource(host, "MeowFilm.MenuItem");
        var text = (Brush)DesktopVisual.Resource(host, "MeowFilm.Brush.Text");
        var font = DesktopTypography.AutoFontFamily;
        foreach (var item in menu.Items) Apply(item);
        void Apply(MenuFlyoutItemBase item)
        {
            if (item is MenuFlyoutItem action) action.Style = itemStyle;
            item.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            item.FontFamily = font;
            item.Foreground = text;
            if (item is MenuFlyoutSubItem submenu)
                foreach (var child in submenu.Items) Apply(child);
        }
    }

    private static MenuFlyoutItemBase AddItem(ICollection<MenuFlyoutItemBase> items, string title, bool enabled,
        Func<Task>? action = null, Action<Exception>? onError = null)
    {
        if (action is null)
        {
            var submenu = new MenuFlyoutSubItem { Text = title, IsEnabled = enabled };
            items.Add(submenu);
            return submenu;
        }
        var item = new MenuFlyoutItem { Text = title, IsEnabled = enabled };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { onError?.Invoke(error); }
        };
        items.Add(item);
        return item;
    }

    internal static void PopulatePlaylistMenu(MenuFlyout menu, MusicLibraryController library,
        MusicCatalogController catalog, MusicCatalogCard card, Func<bool> targetCurrent)
    {
        menu.Items.Clear();
        var generation = library.LoadGeneration;
        bool Current() => generation == library.LoadGeneration && targetCurrent() &&
            catalog.CanCatalogBrowse && card.Kind == "playlist" && catalog.ContainsCatalogCard(card);
        Task Run(Func<Task> action) => Current() ? action() : Task.CompletedTask;
        AddItem(menu.Items, "查看歌单", Current() && card.CanOpen, () => Run(() => catalog.OpenCatalogCardAsync(card)), library.ReportUiError);
        AddItem(menu.Items, "播放", Current() && card.CanPlay, () => Run(() => catalog.PlayCatalogCardAsync(card)), library.ReportUiError);
        AddItem(menu.Items, "下一首播放", Current() && library.CanEdit && card.CanPlay, () => Run(() => catalog.QueueCatalogCardAsync(card)), library.ReportUiError);
        if (card.CanSavePlaylist)
        {
            var reference = catalog.NetworkPlaylistForCard(card);
            AddItem(menu.Items, reference is null ? "收藏" : "取消收藏", Current() && library.CanEdit,
                () => Run(() =>
                {
                    var current = catalog.NetworkPlaylistForCard(card);
                    if (!library.CanEdit || current?.Id != reference?.Id || current?.Revision != reference?.Revision)
                        return Task.CompletedTask;
                    return reference is null ? catalog.SaveNetworkPlaylistAsync(card) : catalog.RemoveNetworkPlaylistAsync(reference);
                }), library.ReportUiError);
        }
    }

    internal static void PopulateSongMenu(MenuFlyout menu, MusicLibraryController library,
        MusicCatalogController catalog, MusicSearchController search, SongRow? song, MusicCatalogCard? card, Func<bool> targetCurrent)
    {
        menu.Items.Clear();
        var generation = library.LoadGeneration;
        bool Current() => generation == library.LoadGeneration && targetCurrent() && library.CanEdit && song is not null &&
            (card is null ? library.ActiveSongs.Any(row => ReferenceEquals(row, song)) : card.CanFavorite && catalog.ContainsCatalogCard(card));
        Task Run(Func<Task> action)
        {
            if (!Current()) return Task.CompletedTask;
            if (card is not null)
            {
                try { catalog.RequireCatalogContent(); }
                catch (MusicOperationException error) { return catalog.CatalogBusinessError(error.Code); }
            }
            return action();
        }
        AddItem(menu.Items, "播放", Current(), () => Run(() =>
        {
            if (card is not null) return catalog.OpenCatalogCardAsync(card);
            if (search.IsSearchPage)
            {
                search.SearchSelectedSong = song;
                var playCtx = search.SearchKeyword.Length > 0
                    ? Library.MusicPlayContextRecord.Empty with { SourceType = "search", SearchKeyword = search.SearchKeyword }
                    : null;
                var src = search.SearchKeyword.Length > 0
                    ? new Library.MusicQueueSourceRecord("catalog-search", search.SearchKeyword, "search", "")
                    : null;
                return library.PlaySingleSongAsync(song!, searchDirect: true, playCtx, src);
            }
            if (catalog.IsCatalogDetailPage)
            {
                catalog.CatalogSelectedSong = song;
                return catalog.PlaySelectedCatalogSongAsync();
            }
            library.SelectedSong = song;
            return library.PlaySelectionAsync();
        }), library.ReportUiError);
        AddItem(menu.Items, "下一首播放", Current(), () => Run(() =>
        {
            var queueContext = search.IsSearchPage && search.SearchKeyword.Length > 0
                ? Library.MusicPlayContextRecord.Empty with { SourceType = "search", SearchKeyword = search.SearchKeyword }
                : catalog.CaptureCatalogFeedbackContext();
            return library.AppendSongNextAsync(song, queueContext);
        }), library.ReportUiError);
        AddItem(menu.Items, "收藏", Current(), () => Run(() => library.FavoriteSongAsync(song)), library.ReportUiError);
        var playlists = AddItem(menu.Items, "添加到歌单", Current());
        foreach (var playlist in library.Playlists)
            AddItem(((MenuFlyoutSubItem)playlists).Items, playlist.Name, Current(), () => Run(() => library.AddSongToPlaylistAsync(song, playlist.Id)), library.ReportUiError);
        if (card is null && library.IsCollectionPage)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            AddItem(menu.Items, "移除", Current(), () => Run(() =>
            {
                if (!library.IsCollectionPage) return Task.CompletedTask;
                library.SelectedSong = song;
                return library.RemoveSongAsync();
            }), library.ReportUiError);
        }
        if (card is null) return;
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu.Items, "相似歌曲", Current() && card.CanSimilar, () => Run(() => catalog.SimilarCatalogCardAsync(card)), library.ReportUiError);
        AddItem(menu.Items, "专辑", Current() && card.CanAlbum, () => Run(() => catalog.AlbumCatalogCardAsync(card)), library.ReportUiError);
    }
}

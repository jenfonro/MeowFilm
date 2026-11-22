using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class MusicLibraryController
{
    internal static void ApplyContentMenuStyles(FrameworkElement host, MenuFlyout menu)
    {
        menu.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        menu.XamlRoot = host.XamlRoot;
        menu.MenuFlyoutPresenterStyle = (Style)DesktopVisual.Resource(host, "MeowFilm.ContextMenu");
        var itemStyle = (Style)DesktopVisual.Resource(host, "MeowFilm.MenuItem");
        var text = (Brush)DesktopVisual.Resource(host, "MeowFilm.Brush.Text");
        foreach (var item in menu.Items) Apply(item);
        void Apply(MenuFlyoutItemBase item)
        {
            if (item is MenuFlyoutItem action) action.Style = itemStyle;
            item.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            item.FontFamily = new FontFamily("XamlAutoFontFamily");
            item.Foreground = text;
            if (item is MenuFlyoutSubItem submenu)
                foreach (var child in submenu.Items) Apply(child);
        }
    }

    private static MenuFlyoutItemBase ContentMenuItem(ICollection<MenuFlyoutItemBase> items, string title, bool enabled, Func<Task>? action = null)
    {
        if (action is null)
        {
            var submenu = new MenuFlyoutSubItem { Text = title, IsEnabled = enabled };
            items.Add(submenu); return submenu;
        }
        var item = new MenuFlyoutItem { Text = title, IsEnabled = enabled };
        item.Click += async (_, _) => await action(); items.Add(item); return item;
    }

    internal void PopulatePlaylistMenu(MenuFlyout menu, MusicCatalogCard card, Func<bool> targetCurrent)
    {
        menu.Items.Clear();
        var generation = host.LoadGeneration;
        bool Current() => generation == host.LoadGeneration && targetCurrent() &&
            host.Catalog.CanCatalogBrowse && card.Kind == "playlist" && host.Catalog.ContainsCatalogCard(card);
        Task Run(Func<Task> action) => Current() ? action() : Task.CompletedTask;
        ContentMenuItem(menu.Items, "查看歌单", Current() && card.CanOpen, () => Run(() => host.Catalog.OpenCatalogCardAsync(card)));
        ContentMenuItem(menu.Items, "播放", Current() && card.CanPlay, () => Run(() => host.Catalog.PlayCatalogCardAsync(card)));
        ContentMenuItem(menu.Items, "下一首播放", Current() && CanEdit && card.CanPlay, () => Run(() => host.Catalog.QueueCatalogCardAsync(card)));
        if (card.CanSavePlaylist)
        {
            var reference = host.Catalog.NetworkPlaylistForCard(card);
            ContentMenuItem(menu.Items, reference is null ? "收藏" : "取消收藏", Current() && CanEdit,
                () => Run(() =>
                {
                    var current = host.Catalog.NetworkPlaylistForCard(card);
                    if (!CanEdit || current?.Id != reference?.Id || current?.Revision != reference?.Revision)
                        return Task.CompletedTask;
                    return reference is null ? host.Catalog.SaveNetworkPlaylistAsync(card) : host.Catalog.RemoveNetworkPlaylistAsync(reference);
                }));
        }
    }

    internal void PopulateSongMenu(MenuFlyout menu, SongRow? song, MusicCatalogCard? card, Func<bool> targetCurrent)
    {
        menu.Items.Clear();
        var generation = host.LoadGeneration;
        bool Current() => generation == host.LoadGeneration && targetCurrent() && CanEdit && song is not null &&
            (card is null ? host.ActiveSongs.Any(row => ReferenceEquals(row, song)) : card.CanFavorite && host.Catalog.ContainsCatalogCard(card));
        Task Run(Func<Task> action)
        {
            if (!Current()) return Task.CompletedTask;
            if (card is not null)
            {
                try { host.Catalog.RequireCatalogContent(); }
                catch (MusicOperationException error) { return host.Catalog.CatalogBusinessError(error.Code); }
            }
            return action();
        }
        ContentMenuItem(menu.Items, "播放", Current(), () => Run(() =>
        {
            if (card is not null) return host.Catalog.OpenCatalogCardAsync(card);
            SelectedSong = song;
            return PlayContentSelectionAsync();
        }));
        ContentMenuItem(menu.Items, "下一首播放", Current(), () => Run(() => AppendSongNextAsync(song)));
        ContentMenuItem(menu.Items, "收藏", Current(), () => Run(() => FavoriteSongAsync(song)));
        var playlists = ContentMenuItem(menu.Items, "添加到歌单", Current());
        foreach (var playlist in Playlists)
            ContentMenuItem(((MenuFlyoutSubItem)playlists).Items, playlist.Name, Current(), () => Run(() => AddSongToPlaylistAsync(song, playlist.Id)));
        if (card is null && HasCollection)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            ContentMenuItem(menu.Items, "移除", Current(), () => Run(() =>
            {
                if (!HasCollection) return Task.CompletedTask;
                SelectedSong = song;
                return RemoveSongAsync();
            }));
        }
        if (card is null) return;
        menu.Items.Add(new MenuFlyoutSeparator());
        ContentMenuItem(menu.Items, "相似歌曲", Current() && card.CanSimilar, () => Run(() => host.Catalog.SimilarCatalogCardAsync(card)));
        ContentMenuItem(menu.Items, "专辑", Current() && card.CanAlbum, () => Run(() => host.Catalog.AlbumCatalogCardAsync(card)));
    }
}

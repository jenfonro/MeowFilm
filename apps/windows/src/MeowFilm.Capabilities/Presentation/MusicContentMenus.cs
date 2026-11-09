using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    internal static void ApplyContentMenuStyles(FrameworkElement host, ContextMenu menu)
    {
        menu.Style = (Style)host.FindResource("MeowFilm.ContextMenu");
        foreach (string key in new[] { "MeowFilm.Brush.Text", "MeowFilm.Brush.Border", "MeowFilm.Brush.FloatingSurface",
            "MeowFilm.Brush.ItemHover", "MeowFilm.Brush.Disabled" })
            menu.Resources[key] = host.FindResource(key);
        var itemStyle = (Style)host.FindResource("MeowFilm.MenuItem");
        ApplyItems(menu);
        void ApplyItems(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<MenuItem>())
            {
                item.Style = itemStyle;
                ApplyItems(item);
            }
        }
    }

    private static MenuItem ContentMenuItem(ItemsControl menu, string title, bool enabled, Func<Task>? action = null)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        if (action is not null)
            item.Click += async (_, e) => { e.Handled = true; await action(); };
        menu.Items.Add(item);
        return item;
    }

    internal void PopulatePlaylistMenu(ContextMenu menu, MusicCatalogCard card, Func<bool> targetCurrent)
    {
        menu.Items.Clear();
        menu.SetCurrentValue(FrameworkElement.DataContextProperty, this);
        var generation = loadGeneration;
        bool Current() => generation == loadGeneration && targetCurrent() &&
            CanCatalogBrowse && card.Kind == "playlist" && ContainsCatalogCard(card);
        Task Run(Func<Task> action) => Current() ? action() : Task.CompletedTask;
        ContentMenuItem(menu, "查看歌单", Current() && card.CanOpen, () => Run(() => OpenCatalogCardAsync(card)));
        ContentMenuItem(menu, "播放", Current() && card.CanPlay, () => Run(() => PlayCatalogCardAsync(card)));
        ContentMenuItem(menu, "下一首播放", Current() && CanEdit && card.CanPlay, () => Run(() => QueueCatalogCardAsync(card)));
        ContentMenuItem(menu, "收藏", Current() && CanEdit && card.CanOpen, () => Run(() => FavoriteCatalogCardAsync(card)));
    }

    internal void PopulateSongMenu(ContextMenu menu, SongRow? song, MusicCatalogCard? card, Func<bool> targetCurrent)
    {
        menu.Items.Clear();
        menu.SetCurrentValue(FrameworkElement.DataContextProperty, this);
        var generation = loadGeneration;
        bool Current() => generation == loadGeneration && targetCurrent() && CanEdit && song is not null &&
            (card is null ? Songs.Any(row => ReferenceEquals(row, song)) : card.CanFavorite && ContainsCatalogCard(card));
        Task Run(Func<Task> action)
        {
            if (!Current()) return Task.CompletedTask;
            if (card is not null)
            {
                try { RequireCatalogContent(); }
                catch (MusicOperationException error) { return CatalogBusinessError(error.Code); }
            }
            return action();
        }
        ContentMenuItem(menu, "播放", Current(), () => Run(() =>
        {
            if (card is not null) return OpenCatalogCardAsync(card);
            SelectedSong = song;
            return PlayContentSelectionAsync();
        }));
        ContentMenuItem(menu, "下一首播放", Current(), () => Run(() => AppendSongNextAsync(song)));
        ContentMenuItem(menu, "收藏", Current(), () => Run(() => FavoriteSongAsync(song)));
        var playlists = ContentMenuItem(menu, "添加到歌单", Current());
        foreach (var playlist in Playlists)
            ContentMenuItem(playlists, playlist.Name, Current(), () => Run(() => AddSongToPlaylistAsync(song, playlist.Id)));
        menu.Items.Add(new Separator());
        ContentMenuItem(menu, "移除", Current() && card is null && HasCollection, () => Run(() =>
        {
            if (card is not null || !HasCollection) return Task.CompletedTask;
            SelectedSong = song;
            return RemoveSongAsync();
        }));
        if (card is null) return;
        menu.Items.Add(new Separator());
        ContentMenuItem(menu, "相似歌曲", Current() && card.CanSimilar, () => Run(() => SimilarCatalogCardAsync(card)));
        ContentMenuItem(menu, "专辑", Current() && card.CanAlbum, () => Run(() => AlbumCatalogCardAsync(card)));
    }
}

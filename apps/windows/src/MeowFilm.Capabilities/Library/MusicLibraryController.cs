using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone owner for music local playlists, favorites, history, archives, content-detail
/// presentation, song selection, and library navigation items.
/// </summary>
public sealed partial class MusicLibraryController : MusicDomainController
{
    private PlaylistRow? collection;
    private SongRow? selectedSong;
    private string heading = "推荐";
    private int total;
    private CancellationTokenSource? collectionPageCancellation;
    private long libraryRead, libraryRevision = -1;

    internal MusicLibraryController(MusicCapability host) : base(host) { }

    public ObservableCollection<PlaylistRow> Playlists { get; } = [];
    public ObservableCollection<PlaylistRow> NetworkPlaylists { get; } = [];
    public ObservableCollection<SongRow> CatalogSongsSource { get; } = [];
    public ObservableCollection<string> HistoryDates { get; } = [];
    public ObservableCollection<ArchivedPlaylist> Archives { get; } = [];

    internal event Action? SongFavoritesChanged;
    internal void NotifySongFavoritesChanged() => SongFavoritesChanged?.Invoke();

    public MusicContentPage ContentPage => host.Discovery.IsDiscovery ? MusicContentPage.Discovery :
        IsArchives ? MusicContentPage.Archives : host.Search.IsSearchSongs ? MusicContentPage.SearchSongs :
        ShowsSongTable ? MusicContentPage.Songs : MusicContentPage.Catalog;

    public string Heading => heading;
    public string Status => host.Status;
    public bool IsHistory => host.CurrentRoute == "history";
    public bool IsArchives => host.CurrentRoute == "archives";
    public bool IsSongList => host.CurrentRoute is "favorites" or "history" or "search" ||
        host.CurrentRoute.StartsWith("playlist/", StringComparison.Ordinal);
    public bool IsLocalLibraryPage => IsSongList || IsArchives;
    public bool IsCollectionPage => IsSongList && collection is not null;
    public bool IsContentDetailPage => IsCollectionPage || host.Catalog.IsCatalogDetailPage;
    public bool ShowsSongTable => IsSongList || host.Catalog.IsCatalogDetailPage || host.Search.IsSearchSongs;
    public bool ShowSongPager => IsSongList && !IsCollectionPage;
    public string? HistoryDate => host.CurrentLocation.Date;
    public bool HasCollection => collection is not null;
    internal PlaylistRow? CurrentCollection => collection;
    public bool CanDeleteCollection => HasCollection && collection!.Kind != "favorites";
    public bool CanPreviousPage => CanEdit && IsSongList && !IsCollectionPage && host.CurrentLocation.Offset > 0;
    public bool CanNextPage => CanEdit && IsSongList && !IsCollectionPage && host.CurrentLocation.Offset + host.ActiveSongs.Count < total;
    public string CountText => host.Catalog.IsCatalogPage ? "" : IsArchives ? $"{Archives.Count} 个歌单" : $"{total} 首歌曲";
    public string PageText => !IsSongList || IsCollectionPage || host.ActiveSongs.Count == 0 ? "" :
        $"{host.CurrentLocation.Offset + 1}–{host.CurrentLocation.Offset + host.ActiveSongs.Count} / {total}";
    public string CollectionArtwork => IsCollectionPage ? host.ActiveSongs.FirstOrDefault(row => row.Cover.Length > 0)?.Cover ?? "" : "";
    public bool CanPlayCollection => CanEdit && IsCollectionPage && total > 0;
    public string DetailArtwork => host.Catalog.IsCatalogDetailPage
        ? (host.Catalog.CatalogArtwork.Length > 0 ? host.Catalog.CatalogArtwork : host.ActiveSongs.FirstOrDefault(row => row.Cover.Length > 0)?.Cover ?? "")
        : CollectionArtwork;
    public string DetailCountText => host.Catalog.IsCatalogDetailPage
        ? host.Catalog.CatalogSummaryCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""
        : IsContentDetailPage ? total.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    public string DetailSectionTitle => host.Catalog.IsCatalogArtist ? "热门歌曲" : "歌曲";
    public string DetailArtworkName => host.Catalog.IsCatalogArtist ? "歌手头像" : "歌单封面";
    public string DetailSubtitle => host.Catalog.DetailSubtitle;
    public bool ShowDetailLoading => host.Catalog.ShowDetailLoading;
    public bool CanPlayContentDetail => host.Catalog.IsCatalogDetailPage ? host.Catalog.CanPlayCatalogPlaylist : CanPlayCollection;
    public bool HasSelection => CanEdit && selectedSong is not null;
    public bool CanRemoveSelection => HasSelection && HasCollection;
    public bool CanReorderPlaylist => CanEdit && HasCollection && IsSongList;
    public bool CanLoadMoreCollection => CanEdit && IsCollectionPage && host.ActiveSongs.Count < total &&
        collectionPageCancellation is null;
    internal int Total => total;

    public SongRow? CatalogSelectedSong
    {
        get => host.Search.IsSearchPage ? null : selectedSong;
        set { if (!host.Search.IsSearchPage) SelectedSong = value; }
    }

    public SongRow? SelectedSong
    {
        get => selectedSong;
        set
        {
            if (stopping) return;
            // Ignore a binding/context-menu callback from a retired page.
            if (value is not null && !host.ActiveSongs.Any(row => ReferenceEquals(row, value))) { Changed(); return; }
            if (ReferenceEquals(value, selectedSong)) return;
            if (!host.Publishing && value is not null) host.Catalog.InvalidateCatalogAction();
            selectedSong = value;
            if (host.Search.IsSearchPage) host.Search.SearchSelectedKey = value?.SelectionKey;
            Changed(nameof(CatalogSelectedSong));
            host.Search.NotifySelectionChanged();
            host.Playback.UpdateRecordingCandidates();
            Changed(); Changed(nameof(HasSelection)); Changed(nameof(CanRemoveSelection));
        }
    }

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        foreach (var name in new[]
        {
            nameof(CatalogSelectedSong), nameof(Heading), nameof(Status), nameof(IsCollectionPage),
            nameof(ShowSongPager), nameof(HistoryDate), nameof(HasSelection), nameof(CanRemoveSelection),
            nameof(CanReorderPlaylist), nameof(CanLoadMoreCollection), nameof(HasCollection), nameof(CanDeleteCollection),
            nameof(CanPreviousPage), nameof(CanNextPage), nameof(CountText), nameof(PageText),
            nameof(CollectionArtwork), nameof(DetailArtwork), nameof(CanPlayCollection),
            nameof(IsContentDetailPage), nameof(DetailCountText), nameof(DetailSectionTitle),
            nameof(DetailArtworkName), nameof(DetailSubtitle), nameof(ShowDetailLoading), nameof(CanPlayContentDetail)
        })
            Changed(name);
    }

    internal void NotifyStatusChanged() => Changed(nameof(Status));
    internal void NotifyArtworkChanged() { Changed(nameof(CollectionArtwork)); Changed(nameof(DetailArtwork)); }
    internal void NotifySelectionChanged() => Changed(nameof(CatalogSelectedSong));

    internal void NotifyDetailBindings()
    {
        foreach (var name in new[]
        {
            nameof(IsContentDetailPage), nameof(DetailArtwork), nameof(DetailCountText), nameof(DetailSectionTitle),
            nameof(DetailArtworkName), nameof(DetailSubtitle), nameof(ShowDetailLoading), nameof(CanPlayContentDetail)
        })
            Changed(name);
    }

    internal void NotifyRouteChanged()
    {
        foreach (var name in new[]
        {
            nameof(ContentPage), nameof(IsHistory), nameof(IsArchives), nameof(IsSongList),
            nameof(IsLocalLibraryPage), nameof(ShowsSongTable), nameof(IsCollectionPage),
            nameof(IsContentDetailPage), nameof(ShowSongPager), nameof(HistoryDate),
            nameof(DetailSectionTitle), nameof(DetailArtworkName), nameof(DetailArtwork),
            nameof(DetailCountText), nameof(DetailSubtitle), nameof(ShowDetailLoading), nameof(CanPlayContentDetail)
        })
            Changed(name);
    }

    internal void SetLocationState(string? nextHeading, bool clearLibrary, bool clearCatalogSongs,
        bool clearSelection, bool clearTotal, bool clearCollection)
    {
        if (clearLibrary || clearCollection) collection = null;
        if (nextHeading is not null) heading = nextHeading;
        if (clearLibrary || clearTotal) total = 0;
        if (clearLibrary || clearCatalogSongs) CatalogSongsSource.Clear();
        if (clearLibrary)
        {
            HistoryDates.Clear();
            Archives.Clear();
        }
        if (clearLibrary || clearSelection) ClearSelectionOnly();
    }

    internal void ApplyLoadedLocation(PlaylistRow? detail, string title, int count,
        SongRow[] rows, string[] dates, ArchivedPlaylist[] archives, string? selectedKey)
    {
        collection = detail; heading = title; total = count;
        selectedSong = null; Changed(nameof(SelectedSong)); Changed(nameof(CatalogSelectedSong));
        host.ActiveSongs.Clear(); foreach (var row in rows) host.ActiveSongs.Add(row);
        HistoryDates.Clear(); foreach (var date in dates) HistoryDates.Add(date);
        Archives.Clear(); foreach (var archive in archives) Archives.Add(archive);
        SelectedSong = rows.FirstOrDefault(row => row.SelectionKey == selectedKey);
    }

    internal void ClearSelectionForCatalog(string nextHeading)
    {
        heading = nextHeading;
        total = 0;
        ClearSelectionOnly();
    }

    internal void ClearSelectionOnly()
    {
        selectedSong = null;
        Changed(nameof(SelectedSong));
        Changed(nameof(CatalogSelectedSong));
        host.Search.NotifySelectionChanged();
    }

    internal void ClearTotal() => total = 0;

    internal void PublishSearchSummary(string nextHeading, int nextTotal)
    {
        heading = nextHeading;
        total = nextTotal;
        Changed(nameof(Heading));
        Changed(nameof(CountText));
    }

    internal void PublishCatalogSummary(int nextTotal, bool clearSelection)
    {
        total = nextTotal;
        if (clearSelection) ClearSelectionOnly();
    }

    internal void CancelCollectionPage() => collectionPageCancellation?.Cancel();

    public Task OpenPlaylistAsync(string id) => RequestNavigationAsync(NetworkPlaylists.FirstOrDefault(p => p.Id == id)?.Route
        ?? (id == "favorites" ? "favorites" : "playlist/" + id));

    public Task RefreshAsync()
    {
        if (host.Discovery.IsDiscovery) return host.Discovery.RefreshDiscoveryAsync();
        if (host.Catalog.IsCatalogPage) return host.Catalog.RefreshCatalogAsync();
        var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            await RefreshPlaylists(lifetime.Token);
            if (origin == host.LoadGeneration) await host.LoadAsync(host.CaptureLocation(), lifetime.Token);
        });
    }

    public Task PageAsync(int direction)
    {
        if (direction is not (-1 or 1)) return Task.CompletedTask;
        if (IsCollectionPage) return Task.CompletedTask;
        var origin = host.LoadGeneration;
        var target = host.CaptureLocation() with { Offset = Math.Max(0, host.CurrentLocation.Offset + direction * 100), Selected = null, Scroll = 0 };
        if (!IsSongList || direction > 0 && target.Offset >= total) return Task.CompletedTask;
        return RunAsync(async () => { if (origin == host.LoadGeneration) await host.LoadAsync(target, lifetime.Token); });
    }

    public Task CreatePlaylistAsync(string name) => CreatePlaylistCoreAsync(name);
    internal Task<bool> TryCreatePlaylistAsync(string name) => CreatePlaylistCoreAsync(name);

    private async Task<bool> CreatePlaylistCoreAsync(string name)
    {
        name = name.Trim(); var origin = host.LoadGeneration;
        var created = false;
        await RunAsync(async () =>
        {
            if (name.Length == 0) { host.SetStatusQuiet("请输入歌单名称"); return; }
            var reply = await Call("music.library.create", new { name }, lifetime.Token);
            created = true;
            await RefreshPlaylists(lifetime.Token);
            if (origin == host.LoadGeneration) await RequestNavigationAsync("playlist/" + MusicDto.Text(reply.GetProperty("collection"), "id"));
        });
        return created;
    }

    public Task DeletePlaylistAsync()
    {
        var selected = collection; var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            if (selected is null || selected.Kind == "favorites") return;
            if (!await ConfirmAsync("删除此歌单？", "删除歌单", lifetime.Token)) return;
            await Call("music.library.remove", new { id = selected.Id, ifRevision = selected.Revision }, lifetime.Token);
            await RefreshPlaylists(lifetime.Token);
            if (origin == host.LoadGeneration) await RequestNavigationAsync("favorites");
        });
    }

    public Task RenamePlaylistAsync(string name)
    {
        var selected = collection; name = name.Trim(); var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            if (selected is null || selected.Kind == "favorites") return;
            if (name.Length == 0) { host.SetStatusQuiet("请输入歌单名称"); return; }
            await Call("music.library.edit", new { id = selected.Id, ifRevision = selected.Revision, name }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    internal async Task RefreshAfterEdit(long origin)
    {
        await RefreshPlaylists(lifetime.Token);
        if (origin == host.LoadGeneration && !host.Catalog.IsCatalogPage) await host.LoadAsync(host.CaptureLocation(), lifetime.Token);
    }

    public Task SelectHistoryDateAsync(string date)
    {
        if (stopping || host.Publishing || !IsHistory || !HistoryDates.Contains(date) || date == HistoryDate) return Task.CompletedTask;
        return host.LoadUiAsync(host.CaptureLocation() with { Date = date, Offset = 0, Selected = null, Scroll = 0 });
    }

    public Task RestoreArchiveAsync(string id)
    {
        host.Catalog.InvalidateCatalogAction();
        var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            await host.Playback.QueueCall("music.history.restorePlaylist", new { id });
            await host.Playback.StopOwnedPlayback(); await host.Playback.ReadQueue(lifetime.Token);
            if (origin == host.LoadGeneration) await host.OpenPlayQueueAsync();
        });
    }

    public Task DeleteArchiveAsync(string id)
    {
        var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            await Call("music.history.removePlaylist", new { id }, lifetime.Token);
            if (origin == host.LoadGeneration) await host.LoadAsync(host.CaptureLocation(), lifetime.Token);
        });
    }

    public Task ImportFilesAsync()
    {
        var selected = collection; var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            if (selected is null || await dialogs.SelectAudioFilesAsync(View) is not { Length: > 0 } paths) return;
            if (stopping || origin != host.LoadGeneration || selected != collection) return;
            var grants = await files.GrantAsync(paths, lifetime.Token);
            if (stopping || origin != host.LoadGeneration || selected != collection) return;
            var songs = grants.Select(file => NativeMusicSong.CreateLocal(file.Id, file.Title)).ToArray();
            await Call("music.library.addSongs", new { id = selected.Id, ifRevision = selected.Revision, songs }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    public Task RemoveSongAsync()
    {
        host.Catalog.InvalidateCatalogAction();
        var song = selectedSong; var selected = collection; var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            if (song is null) return;
            if (selected is not null)
                await Call("music.library.removeSong", new { id = selected.Id, ifRevision = selected.Revision, songId = song.Id }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    public Task AddSelectionToPlaylistAsync(string id) => AddSongToPlaylistAsync(selectedSong, id);

    internal Task AddSongToPlaylistAsync(SongRow? song, string id)
    {
        var destination = Playlists.FirstOrDefault(row => row.Id == id); var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            if (song is null || destination is null) return;
            await Call("music.library.addSongs", new
            {
                id = destination.Id, ifRevision = destination.Revision,
                songs = new[] { song.Raw }
            }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    public Task FavoriteAsync() => FavoriteSongAsync(selectedSong);

    internal Task FavoriteSongAsync(SongRow? song)
    {
        var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            if (song is null || host.Catalog.IsDailyStyle && !host.ActiveSongs.Any(row => ReferenceEquals(row, song))) return;
            var favorite = await Call("music.library.detail", new { id = "favorites", limit = 1 }, lifetime.Token);
            await Call("music.library.addSongs", new
            {
                id = "favorites", ifRevision = MusicDto.Integer(favorite.GetProperty("collection"), "revision"),
                songs = new[] { song.Raw }
            }, lifetime.Token);
            await RefreshAfterEdit(origin);
            SongFavoritesChanged?.Invoke();
        });
    }

    public async Task<bool> IsSongFavoritedAsync(SongRow? song, CancellationToken token = default)
    {
        if (song is null || stopping || disposed) return false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            var result = await Call("music.library.membership", new { id = "favorites", song = song.Raw }, linked.Token);
            return result.GetProperty("present").GetBoolean();
        }
        catch { return false; }
    }

    public Task ToggleSongFavoriteAsync(SongRow? song)
    {
        var origin = host.LoadGeneration;
        return RunAsync(async () =>
        {
            if (song is null || host.Catalog.IsDailyStyle && !host.ActiveSongs.Any(row => ReferenceEquals(row, song))) return;
            var membership = await Call("music.library.membership", new { id = "favorites", song = song.Raw }, lifetime.Token);
            var revision = MusicDto.Integer(membership, "revision");
            if (membership.GetProperty("present").GetBoolean())
                await Call("music.library.removeSong", new
                {
                    id = "favorites", ifRevision = revision,
                    songId = membership.GetProperty("songId").GetString()
                }, lifetime.Token);
            else
                await Call("music.library.addSongs", new { id = "favorites", ifRevision = revision, songs = new[] { song.Raw } }, lifetime.Token);
            await RefreshAfterEdit(origin);
            await host.Playback.RefreshCurrentFavoriteAsync();
            SongFavoritesChanged?.Invoke();
        });
    }

    public Task AppendNextAsync() => AppendSongNextAsync(selectedSong);

    internal Task AppendSongNextAsync(SongRow? song)
    {
        host.Catalog.InvalidateCatalogAction();
        var queueContext = host.Playback.CaptureFeedbackContext();
        return RunAsync(async () =>
        {
            if (song is null || host.Catalog.IsDailyStyle && !host.ActiveSongs.Any(row => ReferenceEquals(row, song))) return;
            await host.Playback.QueueCall("music.queue.append", new { songs = new[] { song.Raw }, next = true, context = queueContext });
            await host.Playback.ReadQueue(lifetime.Token);
            host.Queue.InvalidateQueueSurface();
        });
    }

    public Task PlaySelectionAsync()
    {
        host.Playback.TrackMusicRecovery(host.Playback.RetireMusicRecovery());
        host.Catalog.InvalidateCatalogAction();
        host.Playback.CancelOnline();
        var song = selectedSong; var selected = collection;
        bool searchDirect = host.Search.IsSearchPage;
        var queueContext = host.Playback.CaptureFeedbackContext();
        var index = song is null ? -1 : host.CurrentLocation.Offset + host.ActiveSongs.IndexOf(song);
        return RunPlaybackActionAsync(async generation =>
        {
            if (song is null || index < 0) return;
            if (selected is not null) await host.Playback.QueueCall("music.queue.playCollection", new { id = selected.Id, ifRevision = selected.Revision, index });
            else await host.Playback.QueueCall("music.queue.replace", new { songs = new[] { song.Raw }, index = 0, context = queueContext });
            await host.Playback.ReadQueue(lifetime.Token);
            if (generation != host.PlaybackActionGeneration) return;
            long searchPlayback = -1;
            try
            {
                await host.Playback.StartCurrent(lifetime.Token, admission: () => generation == host.PlaybackActionGeneration,
                    ownsEpoch: searchDirect ? epoch =>
                    {
                        searchPlayback = epoch;
                        host.Queue.AwaitSearchRecommendationStart(epoch);
                    } : null);
            }
            catch
            {
                if (searchPlayback == host.Playback.PlaybackEpoch) host.Queue.ClearSearchRecommendationStart();
                throw;
            }
        }, supersede: true);
    }

    public Task PlayCollectionAsync()
    {
        host.Playback.TrackMusicRecovery(host.Playback.RetireMusicRecovery());
        host.Catalog.InvalidateCatalogAction();
        host.Playback.CancelOnline();
        var selected = collection;
        return RunPlaybackActionAsync(async generation =>
        {
            if (selected is null || selected.Count == 0) return;
            await host.Playback.QueueCall("music.queue.playCollection", new
            {
                id = selected.Id,
                ifRevision = selected.Revision,
                index = 0
            });
            await host.Playback.ReadQueue(lifetime.Token);
            if (generation != host.PlaybackActionGeneration) return;
            await host.Playback.StartCurrent(lifetime.Token, admission: () => generation == host.PlaybackActionGeneration);
        }, supersede: true);
    }

    public Task PlayContentDetailAsync() =>
        host.Catalog.IsCatalogDetailPage ? host.Catalog.PlayCatalogPlaylistAsync() : PlayCollectionAsync();

    public Task PlayContentSelectionAsync()
    {
        if (!host.Catalog.IsCatalogDetailPage) return PlaySelectionAsync();
        var song = selectedSong;
        var card = song is null ? null : host.Catalog.CatalogSections.SelectMany(section => section.Items)
            .FirstOrDefault(item => ReferenceEquals(item.Song, song));
        return card is null ? Task.CompletedTask : host.Catalog.OpenCatalogCardAsync(card);
    }

    private async Task<(PlaylistRow[] Rows, long Revision)> FetchPlaylists(CancellationToken token)
    {
        var rows = new List<PlaylistRow>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long revision = -1;
        while (true)
        {
            var reply = await Call("music.library.list", new { offset = rows.Count, limit = 100 }, token);
            var nextRevision = MusicDto.Integer(reply, "revision");
            if (revision >= 0 && revision != nextRevision) throw new MusicOperationException("REVISION_CONFLICT");
            revision = nextRevision;
            var page = reply.GetProperty("items").EnumerateArray().Select(PlaylistRow.Read).ToArray();
            MusicDto.Require(page.All(row => ids.Add(row.Id)));
            rows.AddRange(page);
            if (page.Length < 100) break;
        }
        return (rows.ToArray(), revision);
    }

    internal async Task RefreshPlaylists(CancellationToken token)
    {
        if (stopping) return;
        var serial = ++libraryRead;
        var (rows, revision) = await FetchPlaylists(token);
        if (!stopping && serial == libraryRead) PublishPlaylists(rows, revision);
    }

    private void PublishPlaylists(PlaylistRow[] rows, long revision)
    {
        if (revision < libraryRevision) return;
        libraryRevision = revision;
        Playlists.Clear(); foreach (var row in rows.Where(row => row.Kind != "network")) Playlists.Add(row);
        NetworkPlaylists.Clear(); foreach (var row in rows.Where(row => row.Kind == "network")) NetworkPlaylists.Add(row);
        RebuildNavigation(); Changed(nameof(CountText));
        host.Catalog.NetworkPlaylistBindings();
        _ = host.Playback.RefreshCurrentFavoriteAsync();
    }

    internal void RebuildNavigation()
    {
        NavigationItem[] items = [
            new("catalog-home", "推荐", "\uE80F") { IconData = "M9,18 L9,5 21,3 21,16 M9,8 L21,6 M9,18 A3,2 0 1 1 3,18 A3,2 0 1 1 9,18 M21,16 A3,2 0 1 1 15,16 A3,2 0 1 1 21,16" },
            new("catalog-channels", "频道", "\uE8A9"),
            new("favorites", "我的收藏", "\uEB51", "我的", GroupAction: Presentation.CreatePlaylistAction),
            ..Playlists.Where(item => item.Kind == "playlist").Select(item =>
                new NavigationItem("playlist/" + item.Id, item.Name, "\uE8D6", "我的")),
            ..NetworkPlaylists.Select(item => new NavigationItem(item.Route, item.Name, "\uE8D6", "网络歌单")),
            // Declared routes remain reachable from content/settings and public-page handoff.
            new("catalog-discovery", "搜索发现", "\uE721", ShowInSidebar: false),
            new("catalog-personal", "平台歌单", "\uE8D6", ShowInSidebar: false),
            new("catalog-daily", "每日推荐", "\uE734", ShowInSidebar: false),
            new("catalog-fm", "私人 FM", "\uE93E", ShowInSidebar: false),
            new("history", "最近播放", "\uE81C", ShowInSidebar: false),
            new("archives", "已播队列", "\uE8D6", ShowInSidebar: false)
        ];
        if (host.Navigation.SequenceEqual(items)) return;
        host.Navigation.Clear(); foreach (var item in items) host.Navigation.Add(item);
    }
}

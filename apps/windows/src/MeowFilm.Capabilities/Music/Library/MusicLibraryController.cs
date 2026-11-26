using System.Collections.ObjectModel;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone owner for music local playlists, favorites, history, archives,
/// local song selection, and library navigation items. Zero reference to <see cref="MusicCatalogController"/>.
/// </summary>
public sealed partial class MusicLibraryController : MusicController
{
    private readonly MusicPlaybackController playbackController;
    private readonly MusicQueueController queue;
    private PlaylistRow? collection;
    private SongRow? selectedSong;
    private string heading = "发现音乐";
    private int total;
    private CancellationTokenSource? collectionPageCancellation;
    private long libraryRead, libraryRevision = -1;
    private bool syncRefreshQueued, syncRefreshRequested;

    internal MusicLibraryController(
        MusicRuntimeContext ctx,
        MusicPlaybackController playbackController,
        MusicQueueController queue) : base(ctx)
    {
        this.playbackController = playbackController;
        this.queue = queue;
        application.LibrarySync.StateChanged += OnLibrarySyncChanged;
    }

    internal void Unsubscribe()
    {
        application.LibrarySync.StateChanged -= OnLibrarySyncChanged;
    }

    private void OnLibrarySyncChanged()
    {
        if (ctx.Dispatcher.IsClosing) return;
        _ = ctx.Dispatcher.EnqueueAsync(() =>
        {
            if (stopping || disposed) return;
            syncRefreshRequested = true;
            if (syncRefreshQueued) return;
            syncRefreshQueued = true;
            _ = RunAsync(async () =>
            {
                try
                {
                    while (syncRefreshRequested && !stopping && !disposed)
                    {
                        syncRefreshRequested = false;
                        long origin = ctx.LoadGeneration;
                        await RefreshPlaylists(lifetime.Token);
                        var route = ctx.CurrentRoute;
                        if (origin == ctx.LoadGeneration && ctx.LoadCancellation is null &&
                            (route is "favorites" or "search" || route.StartsWith("playlist/", StringComparison.Ordinal)))
                            await LoadAsync(ctx.CaptureLocation(), lifetime.Token, publishStatus: false);
                    }
                }
                finally { syncRefreshQueued = false; }
            }, publishStatus: false);
        });
    }

    public ObservableCollection<PlaylistRow> Playlists { get; } = [];
    public ObservableCollection<PlaylistRow> NetworkPlaylists { get; } = [];
    public ObservableCollection<SongRow> LibrarySongsSource { get; } = [];
    public ObservableCollection<string> HistoryDates { get; } = [];
    public ObservableCollection<ArchivedPlaylist> Archives { get; } = [];

    internal event Action? SongFavoritesChanged;
    internal long LoadGeneration => ctx.LoadGeneration;
    internal ObservableCollection<SongRow> ActiveSongs => ctx.ActiveSongs;
    internal void ReportUiError(Exception error)
    {
        if (!ctx.Stopping && !ctx.Disposed) ctx.SetStatus(MusicRuntimeContext.ErrorText(error));
    }

    public string Heading => heading;
    public string Status => ctx.Status;
    public bool IsHistory => ctx.CurrentRoute == "history";
    public bool IsArchives => ctx.CurrentRoute == "archives";
    public bool IsSongList => ctx.CurrentRoute is "favorites" or "history" or "search" ||
        ctx.CurrentRoute.StartsWith("playlist/", StringComparison.Ordinal);
    public bool IsLocalLibraryPage => IsSongList || IsArchives;
    public bool IsCollectionPage => IsSongList && collection is not null;
    public bool ShowSongPager => IsSongList && !IsCollectionPage;
    public string? HistoryDate => ctx.CurrentLocation.Date;
    public bool HasCollection => collection is not null;
    internal PlaylistRow? CurrentCollection => collection;
    public bool CanDeleteCollection => HasCollection && collection!.Kind != "favorites";
    public bool CanPreviousPage => CanEdit && IsSongList && !IsCollectionPage && ctx.CurrentLocation.Offset > 0;
    public bool CanNextPage => CanEdit && IsSongList && !IsCollectionPage && ctx.CurrentLocation.Offset + LibrarySongsSource.Count < total;
    public string CountText => IsArchives ? $"{Archives.Count} 个歌单" : IsSongList ? $"{total} 首歌曲" : "";
    public string PageText => !IsSongList || IsCollectionPage || LibrarySongsSource.Count == 0 ? "" :
        $"{ctx.CurrentLocation.Offset + 1}–{ctx.CurrentLocation.Offset + LibrarySongsSource.Count} / {total}";
    public string CollectionArtwork => IsCollectionPage ? LibrarySongsSource.FirstOrDefault(row => row.Cover.Length > 0)?.Cover ?? "" : "";
    public bool CanPlayCollection => CanEdit && IsCollectionPage && total > 0;
    public bool HasSelection => CanEdit && selectedSong is not null;
    public bool CanRemoveSelection => HasSelection && HasCollection;
    public bool CanReorderPlaylist => CanEdit && HasCollection && IsSongList;
    public bool CanLoadMoreCollection => CanEdit && IsCollectionPage && LibrarySongsSource.Count < total &&
        collectionPageCancellation is null;
    internal int Total => total;

    public SongRow? SelectedSong
    {
        get => selectedSong;
        set
        {
            if (value is not null && !LibrarySongsSource.Any(row => ReferenceEquals(row, value)))
            {
                Changed();
                return;
            }
            if (ReferenceEquals(selectedSong, value)) return;
            selectedSong = value;
            Changed();
            Changed(nameof(HasSelection));
            Changed(nameof(CanRemoveSelection));
        }
    }

    internal void ClearSelectionOnly()
    {
        if (selectedSong is null) return;
        selectedSong = null;
        Changed(nameof(SelectedSong));
        Changed(nameof(HasSelection));
        Changed(nameof(CanRemoveSelection));
    }

    private static readonly string[] AvailabilityProperties =
    [
        nameof(SelectedSong), nameof(Heading), nameof(Status), nameof(IsCollectionPage),
        nameof(ShowSongPager), nameof(HistoryDate), nameof(HasSelection), nameof(CanRemoveSelection),
        nameof(CanReorderPlaylist), nameof(CanLoadMoreCollection), nameof(HasCollection), nameof(CanDeleteCollection),
        nameof(CanPreviousPage), nameof(CanNextPage), nameof(CountText), nameof(PageText),
        nameof(CollectionArtwork), nameof(CanPlayCollection)
    ];

    private static readonly string[] RouteBindingProperties =
    [
        nameof(IsHistory), nameof(IsArchives), nameof(IsSongList),
        nameof(IsLocalLibraryPage), nameof(IsCollectionPage),
        nameof(ShowSongPager), nameof(HistoryDate),
        nameof(CollectionArtwork), nameof(CanPlayCollection), nameof(CountText)
    ];

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        foreach (var name in AvailabilityProperties) Changed(name);
    }

    internal void NotifyStatusChanged() => Changed(nameof(Status));
    internal void NotifyArtworkChanged() => Changed(nameof(CollectionArtwork));

    internal void NotifyRouteChanged()
    {
        foreach (var name in RouteBindingProperties) Changed(name);
    }

    private static void ReconcileKeyed<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> next,
        Func<T, TKey> keyOf, IEqualityComparer<TKey> comparer) where TKey : notnull
    {
        var nextKeys = new HashSet<TKey>(next.Select(keyOf), comparer);
        for (int index = target.Count - 1; index >= 0; index--)
            if (!nextKeys.Contains(keyOf(target[index]))) target.RemoveAt(index);
        for (int index = 0; index < next.Count; index++)
        {
            var item = next[index];
            var key = keyOf(item);
            if (index < target.Count && comparer.Equals(keyOf(target[index]), key))
            {
                if (!EqualityComparer<T>.Default.Equals(target[index], item)) target[index] = item;
                continue;
            }
            int existing = -1;
            for (int pos = index + 1; pos < target.Count; pos++)
                if (comparer.Equals(keyOf(target[pos]), key)) { existing = pos; break; }
            if (existing >= 0)
            {
                target.Move(existing, index);
                if (!EqualityComparer<T>.Default.Equals(target[index], item)) target[index] = item;
            }
            else target.Insert(index, item);
        }
    }

    internal void SetLocationState(string? nextHeading, bool clearLibrary, bool clearSelection, bool clearTotal, bool clearCollection)
    {
        if (clearLibrary || clearCollection) collection = null;
        if (nextHeading is not null && heading != nextHeading) { heading = nextHeading; Changed(nameof(Heading)); }
        if ((clearLibrary || clearTotal) && total != 0) { total = 0; Changed(nameof(CountText)); }
        if (clearLibrary)
        {
            LibrarySongsSource.Clear();
            HistoryDates.Clear();
            Archives.Clear();
        }
        if (clearLibrary || clearSelection) ClearSelectionOnly();
    }

    internal void ApplyLoadedLocation(PlaylistRow? detail, string title, int count,
        SongRow[] rows, string[] dates, ArchivedPlaylist[] archives, string? selectedKey)
    {
        collection = detail;
        heading = title;
        total = count;
        ClearSelectionOnly();
        MusicRuntimeContext.ReconcileSongRows(LibrarySongsSource, rows);
        ReconcileKeyed(HistoryDates, dates, static d => d, StringComparer.Ordinal);
        ReconcileKeyed(Archives, archives, static a => a.Id, StringComparer.Ordinal);
        SelectedSong = LibrarySongsSource.FirstOrDefault(row => row.SelectionKey == selectedKey);
        Changed(nameof(Heading));
        Changed(nameof(CountText));
        NotifyArtworkChanged();
    }

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, MusicSongRecord?> read, long publication)
    {
        foreach (var song in LibrarySongsSource.Append(selectedSong).OfType<SongRow>().Where(song => ids.Contains(song.Id)))
            song.ApplyRepository(read(song.Id), publication);
        NotifyArtworkChanged();
    }

    internal void CancelCollectionPage() => collectionPageCancellation?.Cancel();

    public Task OpenPlaylistAsync(string id) => RequestNavigationAsync(NetworkPlaylists.FirstOrDefault(p => p.Id == id)?.Route
        ?? (id == "favorites" ? "favorites" : "playlist/" + id));

    public Task RefreshAsync()
    {
        if (!IsLocalLibraryPage) return ctx.RefreshActiveSurfaceAsync();
        var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            await RefreshPlaylists(lifetime.Token);
            if (origin == ctx.LoadGeneration) await ctx.LoadAsync(ctx.CaptureLocation(), lifetime.Token);
        });
    }

    public Task PageAsync(int direction)
    {
        if (direction is not (-1 or 1) || IsCollectionPage) return Task.CompletedTask;
        var origin = ctx.LoadGeneration;
        var target = ctx.CaptureLocation() with { Offset = Math.Max(0, ctx.CurrentLocation.Offset + direction * 100), Selected = null, Scroll = 0 };
        if (!IsSongList || direction > 0 && target.Offset >= total) return Task.CompletedTask;
        return RunAsync(async () => { if (origin == ctx.LoadGeneration) await ctx.LoadAsync(target, lifetime.Token); });
    }

    public Task CreatePlaylistAsync(string name) => CreatePlaylistCoreAsync(name);
    internal Task<bool> TryCreatePlaylistAsync(string name) => CreatePlaylistCoreAsync(name);

    private async Task<bool> CreatePlaylistCoreAsync(string name)
    {
        name = name.Trim(); var origin = ctx.LoadGeneration;
        var created = false;
        await RunAsync(async () =>
        {
            if (name.Length == 0) { ctx.SetStatusQuiet("请输入歌单名称"); return; }
            var reply = await application.Library.CreatePlaylistAsync(name, cancellationToken: lifetime.Token);
            created = true;
            await RefreshPlaylists(lifetime.Token);
            if (origin == ctx.LoadGeneration) await RequestNavigationAsync("playlist/" + reply.Id);
        });
        return created;
    }

    public Task DeletePlaylistAsync()
    {
        var selected = collection; var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            if (selected is null || selected.Kind == "favorites") return;
            if (!await ConfirmAsync("删除此歌单？", "删除歌单", lifetime.Token)) return;
            await application.Library.RemoveCollectionAsync(selected.Id, selected.Revision, lifetime.Token);
            await RefreshPlaylists(lifetime.Token);
            if (origin == ctx.LoadGeneration) await RequestNavigationAsync("favorites");
        });
    }

    public Task RenamePlaylistAsync(string name)
    {
        var selected = collection; name = name.Trim(); var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            if (selected is null || selected.Kind == "favorites") return;
            if (name.Length == 0) { ctx.SetStatusQuiet("请输入歌单名称"); return; }
            await application.Library.EditCollectionAsync(selected.Id, selected.Revision, name: name, cancellationToken: lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    internal async Task RefreshAfterEdit(long origin, bool refreshFavorite = true)
    {
        await RefreshPlaylists(lifetime.Token, refreshFavorite);
        if (origin == ctx.LoadGeneration && IsLocalLibraryPage) await ctx.LoadAsync(ctx.CaptureLocation(), lifetime.Token);
    }

    public Task SelectHistoryDateAsync(string date)
    {
        if (stopping || ctx.Publishing || !IsHistory || !HistoryDates.Contains(date) || date == HistoryDate) return Task.CompletedTask;
        return ctx.LoadUiAsync(ctx.CaptureLocation() with { Date = date, Offset = 0, Selected = null, Scroll = 0 });
    }

    public Task RestoreArchiveAsync(string id)
    {
        ctx.InvalidateCatalogAction();
        var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            await playbackController.QueueCallAsync(token => application.Library.RestoreHistoryPlaylistAsync(id, token), clearsSearchRecommendation: true);
            await playbackController.StopOwnedPlayback(); await playbackController.ReadQueue(lifetime.Token);
            if (origin == ctx.LoadGeneration) await ctx.OpenPlayQueueAsync();
        });
    }

    public Task DeleteArchiveAsync(string id)
    {
        var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            await application.Library.RemoveHistoryPlaylistAsync(id, lifetime.Token);
            if (origin == ctx.LoadGeneration) await ctx.LoadAsync(ctx.CaptureLocation(), lifetime.Token);
        });
    }

    public Task ImportFilesAsync()
    {
        var selected = collection; var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            if (selected is null || await MusicDialogs.SelectAudioFilesAsync(ctx.ModalOwner(MusicModalAnchor.Page)) is not { Length: > 0 } paths) return;
            if (stopping || origin != ctx.LoadGeneration || selected != collection) return;
            var grants = await files.GrantAsync(paths, lifetime.Token);
            if (stopping || origin != ctx.LoadGeneration || selected != collection) return;
            var songs = grants.Select(file => NativeMusicSong.CreateLocal(file.Id, file.Title)).ToArray();
            await application.Library.AddSongsToCollectionAsync(selected.Id, selected.Revision, songs, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    public Task RemoveSongAsync()
    {
        ctx.InvalidateCatalogAction();
        var song = selectedSong; var selected = collection; var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            if (song is null || selected is null) return;
            await application.Library.RemoveSongFromCollectionAsync(selected.Id, selected.Revision, song.Id, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    internal Task AddSongToPlaylistAsync(SongRow? song, string id)
    {
        var destination = Playlists.FirstOrDefault(row => row.Id == id); var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            if (song is null || destination is null) return;
            await application.Library.AddSongsToCollectionAsync(destination.Id, destination.Revision, [song.Record], lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    public Task FavoriteAsync() => FavoriteSongAsync(selectedSong);

    internal Task FavoriteSongAsync(SongRow? song)
    {
        var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            if (song is null) return;
            var favorite = await application.Library.GetCollectionDetailAsync("favorites", 0, 1, lifetime.Token);
            await application.Library.AddSongsToCollectionAsync(
                "favorites", favorite.Collection.Revision, [song.Record], lifetime.Token);
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
            var result = await application.Library.GetCollectionMembershipAsync("favorites", song.Record, linked.Token);
            return result.Present;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            MeowFilm.Core.Debug.DebugLog.Warn(MeowFilm.Core.Debug.DebugCategories.MusicLibrary,
                "collections", "favorite_membership_check_failed", new { songId = song.Id, error = error.Message });
            return false;
        }
    }

    public Task ToggleSongFavoriteAsync(SongRow? song)
    {
        var origin = ctx.LoadGeneration;
        return RunAsync(async () =>
        {
            if (song is null) return;
            var membership = await application.Library.GetCollectionMembershipAsync("favorites", song.Record, lifetime.Token);
            if (membership.Present)
                await application.Library.RemoveSongFromCollectionAsync("favorites", membership.Revision, membership.SongId!, lifetime.Token);
            else
                await application.Library.AddSongsToCollectionAsync("favorites", membership.Revision, [song.Record], lifetime.Token);
            await RefreshAfterEdit(origin, refreshFavorite: false);
            await playbackController.RefreshCurrentFavoriteAsync();
            SongFavoritesChanged?.Invoke();
        });
    }

    public Task AppendNextAsync() => AppendSongNextAsync(selectedSong);

    internal Task AppendSongNextAsync(SongRow? song, MusicPlayContextRecord? queueContext = null)
    {
        ctx.InvalidateCatalogAction();
        return RunAsync(async () =>
        {
            if (song is null) return;
            await playbackController.QueueCallAsync(token => application.Library.AppendQueueAsync([song.Record], queueContext, next: true, cancellationToken: token));
            await playbackController.ReadQueue(lifetime.Token);
            queue.InvalidateQueueSurface();
        });
    }

    public Task PlaySelectionAsync()
    {
        var song = selectedSong; var selected = collection;
        var index = song is null ? -1 : ctx.CurrentLocation.Offset + LibrarySongsSource.IndexOf(song);
        if (song is null || index < 0) return Task.CompletedTask;
        playbackController.TrackMusicRecovery(playbackController.RetireMusicRecovery());
        ctx.InvalidateCatalogAction();
        playbackController.CancelOnline();
        return RunPlaybackActionAsync(async generation =>
        {
            if (selected is not null)
                await playbackController.QueueCallAsync(token => application.Library.PlayCollectionAsync(selected.Id, selected.Revision, index, token), clearsSearchRecommendation: true);
            else
                await playbackController.QueueCallAsync(token => application.Library.ReplaceQueueAsync([song.Record], 0, cancellationToken: token), clearsSearchRecommendation: true);
            await playbackController.ReadQueue(lifetime.Token);
            if (generation != ctx.PlaybackActionGeneration) return;
            await playbackController.StartCurrent(lifetime.Token, admission: () => generation == ctx.PlaybackActionGeneration);
        }, supersede: true);
    }

    internal Task PlaySingleSongAsync(SongRow song, bool searchDirect, MusicPlayContextRecord? queueContext, MusicQueueSourceRecord? queueSource)
    {
        playbackController.TrackMusicRecovery(playbackController.RetireMusicRecovery());
        ctx.InvalidateCatalogAction();
        playbackController.CancelOnline();
        return RunPlaybackActionAsync(async generation =>
        {
            await playbackController.QueueCallAsync(token => application.Library.ReplaceQueueAsync([song.Record], 0, queueContext, queueSource, cancellationToken: token), clearsSearchRecommendation: true);
            await playbackController.ReadQueue(lifetime.Token);
            if (generation != ctx.PlaybackActionGeneration) return;
            long searchPlayback = -1;
            try
            {
                await playbackController.StartCurrent(lifetime.Token, admission: () => generation == ctx.PlaybackActionGeneration,
                    ownsEpoch: searchDirect ? epoch =>
                    {
                        searchPlayback = epoch;
                        queue.AwaitSearchRecommendationStart(epoch);
                    } : null);
            }
            catch
            {
                if (searchPlayback == playbackController.PlaybackEpoch) queue.ClearSearchRecommendationStart();
                throw;
            }
        }, supersede: true);
    }

    public Task PlayCollectionAsync()
    {
        playbackController.TrackMusicRecovery(playbackController.RetireMusicRecovery());
        ctx.InvalidateCatalogAction();
        playbackController.CancelOnline();
        var selected = collection;
        return RunPlaybackActionAsync(async generation =>
        {
            if (selected is null || selected.Count == 0) return;
            await playbackController.QueueCallAsync(token => application.Library.PlayCollectionAsync(selected.Id, selected.Revision, 0, token), clearsSearchRecommendation: true);
            await playbackController.ReadQueue(lifetime.Token);
            if (generation != ctx.PlaybackActionGeneration) return;
            await playbackController.StartCurrent(lifetime.Token, admission: () => generation == ctx.PlaybackActionGeneration);
        }, supersede: true);
    }

    private async Task<(PlaylistRow[] Rows, long Revision)> FetchPlaylists(CancellationToken token)
    {
        var rows = new List<PlaylistRow>();
        long revision = -1;
        while (true)
        {
            var reply = await application.Library.ListCollectionsAsync(rows.Count, 100, token);
            if (revision >= 0 && revision != reply.Revision) throw new MusicOperationException("REVISION_CONFLICT");
            revision = reply.Revision;
            var page = reply.Items.Select(PlaylistRow.FromSummary).ToArray();
            rows.AddRange(page);
            if (page.Length < 100) break;
        }
        return (rows.ToArray(), revision);
    }

    internal async Task RefreshPlaylists(CancellationToken token, bool refreshFavorite = true)
    {
        if (stopping) return;
        var serial = ++libraryRead;
        var (rows, revision) = await FetchPlaylists(token);
        if (!stopping && serial == libraryRead) PublishPlaylists(rows, revision, refreshFavorite);
    }

    private void PublishPlaylists(PlaylistRow[] rows, long revision, bool refreshFavorite)
    {
        if (revision < libraryRevision) return;
        var local = rows.Where(row => row.Kind != "network").ToArray();
        var network = rows.Where(row => row.Kind == "network").ToArray();
        if (revision == libraryRevision && Playlists.SequenceEqual(local) && NetworkPlaylists.SequenceEqual(network))
            return;
        libraryRevision = revision;
        ReconcileKeyed(Playlists, local, static row => row.Id, StringComparer.Ordinal);
        ReconcileKeyed(NetworkPlaylists, network, static row => row.Id, StringComparer.Ordinal);
        RebuildNavigation(); Changed(nameof(CountText));
        if (refreshFavorite) _ = playbackController.RefreshCurrentFavoriteAsync();
    }

    internal void RebuildNavigation()
    {
        NavigationItem[] items = [
            new("catalog-home", "推荐", "\uE80F") { IconData = "M9,18 L9,5 21,3 21,16 M9,8 L21,6 M9,18 A3,2 0 1 1 3,18 A3,2 0 1 1 9,18 M21,16 A3,2 0 1 1 15,16 A3,2 0 1 1 21,16" },
            new("catalog-channels", "频道", "\uE8A9"),
            new("favorites", "我的收藏", "\uEB51", "我的", GroupAction: ctx.CreatePlaylistAction),
            ..Playlists.Where(item => item.Kind == "playlist").Select(item =>
                new NavigationItem("playlist/" + item.Id, item.Name, "\uE8D6", "我的")),
            ..NetworkPlaylists.Select(item => new NavigationItem(item.Route, item.Name, "\uE8D6", "网络歌单")),
            new("catalog-discovery", "搜索发现", "\uE721", ShowInSidebar: false),
            new("catalog-personal", "平台歌单", "\uE8D6", ShowInSidebar: false),
            new("catalog-daily", "每日推荐", "\uE734", ShowInSidebar: false),
            new("catalog-fm", "私人 FM", "\uE93E", ShowInSidebar: false),
            new("history", "最近播放", "\uE81C", ShowInSidebar: false),
            new("archives", "已播队列", "\uE8D6", ShowInSidebar: false)
        ];
        if (ctx.Navigation.SequenceEqual(items)) return;
        ctx.Navigation.Clear(); foreach (var item in items) ctx.Navigation.Add(item);
    }
}

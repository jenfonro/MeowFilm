using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability : IDesktopModule, IDesktopNavigationSource, IScriptPlatformCapabilities
{
    private readonly IMusicApplication application;
    private readonly IMusicPlayback playback;
    private readonly IMusicDialogs dialogs;
    private readonly GrantedMediaFiles files;
    private readonly PlaybackCache.MusicAudioFileCache? audioFileCache;
    private readonly PlaybackCache.MusicChorusCache persistentChorus;
    private readonly CancellationTokenSource lifetime = new();
    private readonly MusicView page;
    private readonly MusicSettingsView settings;
    private readonly MusicPlayerView player;
    private Task commandTail = Task.CompletedTask;
    private readonly HashSet<Task> eventWrites = [];
    private Task? preparation, resumption, disposal, abortion;
    private string status = "", heading = "推荐";
    private PlaylistRow? collection;
    private SongRow? current, selectedSong;
    private int total, mode = 1, commands;
    private long queueEpoch, queueRead, playbackEpoch, endedEpoch = -1, statusEpoch;
    private bool stopping, disposed, recorded, queueSaved = true, playbackSubscribed;
    private string? openedEntry;

    public MusicCapability(IRuntimeClient runtime, IProtectedDocumentStore store, NativePlayback playback, string? playbackCacheRoot = null)
        : this(new MusicNativeApplication(store, runtime), store, new NativeMusicPlayback(playback), playbackCacheRoot: playbackCacheRoot) { }

    public MusicCapability(IMusicApplication application, IProtectedDocumentStore store, NativePlayback playback,
        IMusicDialogs? dialogs = null, string? playbackCacheRoot = null)
        : this(application, store, new NativeMusicPlayback(playback), dialogs, playbackCacheRoot) { }

    public MusicCapability(IRuntimeClient runtime, IProtectedDocumentStore store, IMusicPlayback playback,
        IMusicDialogs? dialogs = null)
        : this(new MusicNativeApplication(store, runtime), store, playback, dialogs) { }

    public MusicCapability(IMusicApplication application, IProtectedDocumentStore store, IMusicPlayback playback,
        IMusicDialogs? dialogs = null, string? playbackCacheRoot = null)
        : this(application, store, playback,
            playbackCacheRoot is null ? null : new PlaybackCache.MusicAudioFileCache(playbackCacheRoot, store),
            dialogs) { }

    internal MusicCapability(IMusicApplication application, IProtectedDocumentStore store, IMusicPlayback playback,
        PlaybackCache.MusicAudioFileCache? playbackCache, IMusicDialogs? dialogs = null)
    {
        this.application = application ?? throw new ArgumentNullException(nameof(application));
        this.playback = playback; this.dialogs = dialogs ?? new MusicDialogs();
        audioFileCache = playbackCache;
        persistentChorus = new(store);
        files = new(store, "music.files.v1", [".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma"]);
        Lyrics.CollectionChanged += OnLyricRowsChanged;
        InitializeLibrarySync();
        vipPage = new(application, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        InitializePlatformSettings();
        page = new(this); settings = new(this); player = new(this);
        InitializeGlobalSurfaces();
        InitializeAggregateSearch();
        InitializeDiscovery();
        InitializeRecommendations();
        SubscribePlayback();
        RebuildNavigation();
    }

    public IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> ScriptCapabilities
        => application.ScriptCapabilities;

    public string Id => "music";
    public string Title => "音乐";
    public string Version => "0.4.0";
    public ObservableCollection<NavigationItem> Navigation { get; } = [];
    public ObservableCollection<PlaylistRow> Playlists { get; } = [];
    public ObservableCollection<SongRow> Songs { get; } = [];
    public ObservableCollection<string> HistoryDates { get; } = [];
    public ObservableCollection<ArchivedPlaylist> Archives { get; } = [];
    public FrameworkElement View => page;
    public FrameworkElement SettingsView => settings;
    public FrameworkElement PlayerView => player;
    public System.Windows.Media.Brush PlayerBackground => page.PlayerBackground;
    public System.Windows.Media.Brush PlayerProgressBrush => page.PlayerProgress;
    internal void RefreshPlayerBackground()
    { Changed(nameof(PlayerBackground)); Changed(nameof(PlayerProgressBrush)); }
    internal void RefreshPlayerAppearance(System.Windows.Media.Brush sampled, bool animate = true) => page.RefreshAppearance(sampled, animate);
    public string Heading => heading;
    public string Status => status;
    public bool Busy => commands > 0 || loadCancellation is not null;
    public bool CanEdit => !stopping && !Busy;
    public bool IsHistory => location.Route == "history";
    public bool IsArchives => location.Route == "archives";
    public bool IsSongList => location.Route is "favorites" or "history" or "search" ||
        location.Route.StartsWith("playlist/", StringComparison.Ordinal);
    public bool IsCollectionPage => IsSongList && collection is not null;
    public bool IsContentDetailPage => IsCollectionPage || IsCatalogDetailPage;
    public bool ShowsSongTable => IsSongList || IsCatalogDetailPage || IsAggregateSongs;
    public bool ShowSongPager => IsSongList && !IsCollectionPage;
    public string? HistoryDate => location.Date;
    public bool HasCollection => collection is not null;
    public bool CanDeleteCollection => HasCollection && collection!.Kind != "favorites";
    public bool CanPreviousPage => CanEdit && IsSongList && !IsCollectionPage && location.Offset > 0;
    public bool CanNextPage => CanEdit && IsSongList && !IsCollectionPage && location.Offset + Songs.Count < total;
    public string CountText => IsCatalogPage ? "" : IsArchives ? $"{Archives.Count} 个歌单" : $"{total} 首歌曲";
    public string PageText => !IsSongList || IsCollectionPage || Songs.Count == 0 ? "" : $"{location.Offset + 1}–{location.Offset + Songs.Count} / {total}";
    public string CollectionArtwork => IsCollectionPage ? Songs.FirstOrDefault(row => row.Cover.Length > 0)?.Cover ?? "" : "";
    public bool CanPlayCollection => CanEdit && IsCollectionPage && total > 0;
    public string DetailArtwork => IsCatalogDetailPage ? catalogArtwork : CollectionArtwork;
    public string DetailCountText => IsContentDetailPage ? total.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    public string DetailSectionTitle => IsCatalogArtist ? "热门歌曲" : "歌曲";
    public string DetailArtworkName => IsCatalogArtist ? "歌手头像" : "歌单封面";
    public bool CanPlayContentDetail => IsCatalogDetailPage ? CanPlayCatalogPlaylist : CanPlayCollection;
    public SongRow? SelectedSong
    {
        get => selectedSong;
        set
        {
            if (stopping) return;
            // Ignore a binding/context-menu callback from a retired page.
            if (value is not null && !Songs.Any(row => ReferenceEquals(row, value))) { Changed(); return; }
            if (!publishing && value is not null && !ReferenceEquals(value, selectedSong)) InvalidateCatalogAction();
            selectedSong = value;
            UpdateRecordingCandidates();
            Changed(); Changed(nameof(HasSelection)); Changed(nameof(CanRemoveSelection));
        }
    }
    public bool HasSelection => CanEdit && selectedSong is not null;
    public bool CanRemoveSelection => HasSelection && HasCollection;
    public bool CanReorderPlaylist => CanEdit && HasCollection && IsSongList;
    public bool CanLoadMoreCollection => CanEdit && IsCollectionPage && Songs.Count < total &&
        collectionPageCancellation is null;
    public string CurrentTitle => current?.Title ?? "未在播放";
    public string CurrentArtist => current?.Artist ?? "";
    public string CurrentAlbum => current?.Album ?? "";
    public string CurrentSource => musicRecovery?.Media?.Target is { Official: true } official
        ? official.Platform switch { "wy" => "网易云音乐", "tx" => "QQ 音乐", "kg" => "酷狗音乐", _ => "" }
        : playbackInstallation is not null ? Sources.FirstOrDefault(s => s.InstallationId == playbackInstallation)?.Label ?? "JS 音源"
        : current is { IsOnline: false } ? "本地音乐" : "";
    public bool IsPlaying => OwnsCurrent && playback.IsPlaying;
    public bool HasCurrent => current is not null;
    public bool CanPlay => CanEdit && HasCurrent && queueSaved;
    public bool CanTogglePlayback => !stopping && HasCurrent && queueSaved &&
        (CanEdit || OwnsCurrent && (playback.HasMedia || musicRecovery?.Recovering == true));
    private bool OwnsCurrent => current is not null && playback.OwnerId == Id && playback.Identity == current.EntryId;
    public string PlaybackPresentationKey => (current?.EntryId ?? "") + ":" + playbackEpoch;
    public bool CanSeek => CanPlay && OwnsCurrent && playback.HasMedia;
    public string PlayGlyph => OwnsCurrent && playback.IsPlaying ? "Ⅱ" : "▶";
    public double PositionSeconds => OwnsCurrent ? playback.PositionSeconds : 0;
    public double DurationSeconds => OwnsCurrent ? Math.Max(1, playback.DurationSeconds) : 1;
    public string PositionText => Clock(PositionSeconds);
    public string DurationText => Clock(OwnsCurrent ? playback.DurationSeconds : 0);
    private static string Clock(double value) => double.IsFinite(value) && value is >= 0 and < 86400000
        ? TimeSpan.FromSeconds(value).ToString(value >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "0:00";
    public int Volume { get => playback.Volume; set { if (!stopping) { volumeEdit++; playback.Volume = Math.Clamp(value, 0, 100); } Changed(); } }
    public int Mode => mode;
    public string ModeText => new[] { "顺序播放", "列表循环", "随机播放", "单曲循环" }[mode];
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void RefreshBindings()
    {
        foreach (var name in new[] { nameof(Heading), nameof(Status), nameof(Busy), nameof(CanEdit), nameof(CanClearPlaybackCache),
            nameof(IsHistory), nameof(IsArchives), nameof(IsSongList), nameof(IsCollectionPage), nameof(IsContentDetailPage), nameof(ShowsSongTable),
            nameof(ShowSongPager), nameof(HistoryDate),
            nameof(HasSelection), nameof(CanRemoveSelection), nameof(CanReorderPlaylist), nameof(CanLoadMoreCollection), nameof(HasCollection), nameof(CanDeleteCollection), nameof(CanEditMetadata),
            nameof(CanImportLocalLyrics), nameof(CanRemoveLocalLyrics), nameof(CanEditFeedback), nameof(CanEditSearchPreferences),
            nameof(CanPreviousPage), nameof(CanNextPage), nameof(CountText), nameof(PageText), nameof(CollectionArtwork),
            nameof(CanPlayCollection), nameof(DetailArtwork), nameof(DetailCountText),
            nameof(DetailSectionTitle), nameof(DetailArtworkName), nameof(CanPlayContentDetail), nameof(CurrentTitle),
            nameof(CurrentArtist), nameof(CurrentAlbum), nameof(CurrentSource), nameof(IsPlaying), nameof(HasCurrent), nameof(CurrentArtwork), nameof(CanPlay), nameof(CanSeek), nameof(PlayGlyph), nameof(Mode), nameof(ModeText),
            nameof(PlaybackPresentationKey),
            nameof(CanChangePlaybackRate), nameof(PlaybackRateText), nameof(AcceptedPlaybackRate),
            nameof(CanSelectCurrentRecording), nameof(CanTogglePlayback) })
            Changed(name);
        OnlineBindings();
        AccountBindings();
        CatalogBindings();
        DiscoveryBindings();
        RecommendationBindings();
    }
    private async Task<JsonElement> Call(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        var reply = await application.ExecuteAsync(method, parameters ?? new { }, cancellationToken).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateReply(method, reply);
    }
    private async Task<JsonElement> CallSettled(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        // Request owners that promise physical drain must retain the application
        // task after cancellation instead of only cancelling their own wait.
        var reply = await application.ExecuteAsync(method, parameters ?? new { }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateReply(method, reply);
    }
    private async Task<JsonElement> CallSettledReceipt(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        // Two-phase commands must observe a successful token/commit receipt even
        // when caller cancellation raced with its delivery. Only an explicit
        // cancelled receipt means the command was not admitted.
        var reply = await application.ExecuteAsync(method, parameters ?? new { }, cancellationToken);
        try { return ValidateReply(method, reply); }
        catch (MusicOperationException error) when (error.Code == "CANCELLED" &&
            cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
    private static JsonElement ValidateReply(string method, JsonElement reply)
    {
        MusicDto.Require(reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("ok", out _));
        var success = reply.GetProperty("ok");
        if (success.ValueKind == JsonValueKind.False)
        {
            var code = MusicDto.Text(reply, "code");
            // The existing catalog UI uses AUTH_REQUIRED as its canonical expiry
            // state. Do not retain private display when a native port says expired.
            if (code == "AUTH_EXPIRED" && method.StartsWith("music.catalog.", StringComparison.Ordinal))
                code = "AUTH_REQUIRED";
            throw new MusicOperationException(code);
        }
        MusicDto.Require(success.ValueKind == JsonValueKind.True);
        return reply;
    }

    // Commands are never dropped because another action is awaiting the application. Targets
    // are captured by the public methods before admission, not read after waiting.
    public Task RunAsync(Func<Task> action) => RunAsync(action, publishStatus: true);
    private Task RunAsync(Func<Task> action, bool publishStatus)
    {
        if (stopping) return Task.CompletedTask;
        var previous = commandTail;
        // Passive Sync refreshes share admission/drain, not foreground status ownership.
        var stamp = publishStatus ? ++statusEpoch : statusEpoch;
        commands++;
        if (publishStatus) status = "";
        RefreshBindings();
        return commandTail = Execute();
        async Task Execute()
        {
            await Task.Yield(); // Publish commandTail before any event can re-enter.
            await previous;
            try { if (!disposed) await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (publishStatus && !disposed && stamp == statusEpoch) status = ErrorText(error);
            }
            finally { commands--; if (!disposed) RefreshBindings(); }
        }
    }
    public string OperationErrorText(Exception error) => ErrorText(error);
    private static string ErrorText(Exception error) => error switch
    {
        PlaybackException { Code: PlaybackError.InvalidRate } or MusicOperationException { Code: PlaybackError.InvalidRate } => "播放倍速须为 0.5–3，间隔 0.1",
        PlaybackException { Code: PlaybackError.RateUnsupported } or MusicOperationException { Code: PlaybackError.RateUnsupported or "RATE_UNSUPPORTED" } => "当前音频不支持此播放倍速",
        PlaybackException { Code: PlaybackError.Superseded } or MusicOperationException { Code: PlaybackError.Superseded } => "播放内容已变更",
        InvalidDataException => "文件不可用",
        Library.MusicLibraryException native => ErrorText(new MusicOperationException(native.Code)),
        MusicOperationException { Code: "CORRUPT_QUEUE" } => "播放队列数据无效或格式不兼容",
        MusicOperationException { Code: "CORRUPT_HISTORY" } => "播放历史数据无效或格式不兼容",
        MusicOperationException { Code: "CORRUPT_LIBRARY" } => "曲库数据无效或格式不兼容",
        MusicOperationException { Code: "CORRUPT_STORAGE" or "CORRUPT_SYNC" } => "本地数据无效或格式不兼容",
        MusicOperationException { Code: "REVISION_CONFLICT" or "STORAGE_CONFLICT" } => "数据已变更，请刷新",
        MusicOperationException { Code: "COMMIT_UNKNOWN" } => "保存结果未确认",
        MusicOperationException { Code: "LIMIT" } => "操作超过数量限制",
        MusicOperationException { Code: "PENDING_WRITE" } => "保存未完成，请重试保存",
        MusicOperationException { Code: "TARGET_REQUIRED" or "TARGET_CHANGED" } => "未选择匹配的音源、平台或歌曲",
        MusicOperationException { Code: "DISABLED" } => "音源已停用",
        MusicOperationException { Code: "CAPABILITY_UNAVAILABLE" } => "当前音源不支持此操作或音质",
        MusicOperationException { Code: "PERMISSION_DENIED" } => "音源权限未授权",
        MusicOperationException { Code: "AUTH_REQUIRED" } => "当前内容需要平台登录",
        MusicOperationException { Code: "AUTH_CHANGED" or "AUTH_EXPIRED" } => "平台登录状态已失效",
        MusicOperationException { Code: "VIP_REQUIRED" } => "当前官方音源需要有效会员",
        MusicOperationException { Code: "TRIAL_ONLY" or "PLAYBACK_RESTRICTED" } => "当前歌曲暂无完整播放权限",
        MusicOperationException { Code: "PLAYBACK_UNAVAILABLE" } => "未找到可用的完整音频",
        MusicOperationException { Code: "QUALITY_UNAVAILABLE" or "QUALITY_DISABLED" } => "当前音质不可用",
        MusicOperationException { Code: "RECORDING_REQUIRED" } => "请选择歌曲版本",
        MusicOperationException { Code: "RECORDING_UNAVAILABLE" or "IDENTITY_MISMATCH" } => "歌曲版本不匹配",
        MusicOperationException { Code: "UNSUPPORTED" } => "当前平台不提供此内容",
        MusicOperationException { Code: "TIMEOUT" } => "请求超时",
        MusicOperationException { Code: "CATALOG_FAILED" or "UPSTREAM_FORMAT" or "FORMAT" } => "平台内容暂不可用",
        MusicOperationException { Code: "STALE_SESSION" } => "音源已变更，请重新播放",
        MusicOperationException { Code: "MEDIA_HEADERS_UNSUPPORTED" } => "此音频需要尚未支持的请求头",
        MusicOperationException { Code: "SCRIPT_ENCODING" } => "音源脚本编码无效",
        MusicOperationException { Code: "SCRIPT_INTEGRITY" } => "音源脚本内容已变化",
        MusicOperationException { Code: "ROLLBACK_UNAVAILABLE" } => "没有可回退的音源版本",
        MusicOperationException { Code: "SCRIPT_EXCEPTION" or "SCRIPT_PROTOCOL_ERROR" } => "音源执行失败",
        PlaybackCache.MusicPlaybackCacheCommitException => "保存结果未确认",
        MusicOperationException { Code: "CACHE_DATA_INCOMPATIBLE" } or
            PlaybackCache.MusicPlaybackCacheDataException => "播放缓存数据格式不兼容",
        SelectedLyricSnapshotException { Error: SelectedLyricSnapshotError.InvalidEncoding } => "歌词文本编码无效",
        SelectedLyricSnapshotException { Error: SelectedLyricSnapshotError.TooLarge } => "歌词文件超过大小限制",
        SelectedScriptSnapshotException or SelectedLyricSnapshotException => "所选文件不可用",
        MusicOperationException { Code: "INVALID_LYRIC_RESPONSE" } => "歌词格式无效",
        System.Text.Json.JsonException => "JSON 格式错误",
        BridgeException { Code: "CLOSED" } => "业务连接已中断",
        _ => "操作失败，请重试"
    };

    private Task RequestNavigationAsync(string destination)
    {
        if (stopping) return Task.CompletedTask;
        if (NavigationRequested is not { } requested) return NavigateAsync(destination, lifetime.Token);
        requested(this, new(destination)); // Shell owns route history and back.
        return Task.CompletedTask;
    }
    public Task OpenPlaylistAsync(string id) => RequestNavigationAsync(id == "favorites" ? "favorites" : "playlist/" + id);
    public Task RefreshAsync()
    {
        if (IsDiscovery) return RefreshDiscoveryAsync();
        if (IsCatalogPage)
            return RefreshCatalogAsync();
        var origin = loadGeneration;
        return RunAsync(async () =>
        {
            await RefreshPlaylists(lifetime.Token);
            if (origin == loadGeneration) await LoadAsync(CaptureLocation(), lifetime.Token);
        });
    }
    public Task PageAsync(int direction)
    {
        if (direction is not (-1 or 1)) return Task.CompletedTask;
        if (IsCollectionPage) return Task.CompletedTask;
        var origin = loadGeneration;
        var target = CaptureLocation() with { Offset = Math.Max(0, location.Offset + direction * 100), Selected = null, Scroll = 0 };
        if (!IsSongList || direction > 0 && target.Offset >= total) return Task.CompletedTask;
        return RunAsync(async () => { if (origin == loadGeneration) await LoadAsync(target, lifetime.Token); });
    }
    public Task CreatePlaylistAsync(string name)
    {
        name = name.Trim(); var origin = loadGeneration;
        return RunAsync(async () =>
        {
            if (name.Length == 0) { status = "请输入歌单名称"; return; }
            var reply = await Call("music.library.create", new { name }, lifetime.Token);
            await RefreshPlaylists(lifetime.Token);
            if (origin == loadGeneration) await RequestNavigationAsync("playlist/" + MusicDto.Text(reply.GetProperty("collection"), "id"));
        });
    }
    public Task DeletePlaylistAsync()
    {
        var selected = collection; var origin = loadGeneration;
        return RunAsync(async () =>
        {
            if (selected is null || selected.Kind == "favorites") return;
            if (!dialogs.Confirm("删除此歌单？", "删除歌单")) return;
            await Call("music.library.remove", new { id = selected.Id, ifRevision = selected.Revision }, lifetime.Token);
            await RefreshPlaylists(lifetime.Token);
            if (origin == loadGeneration) await RequestNavigationAsync("favorites");
        });
    }
    public Task RenamePlaylistAsync(string name)
    {
        var selected = collection; name = name.Trim(); var origin = loadGeneration;
        return RunAsync(async () =>
        {
            if (selected is null || selected.Kind == "favorites") return;
            if (name.Length == 0) { status = "请输入歌单名称"; return; }
            await Call("music.library.edit", new { id = selected.Id, ifRevision = selected.Revision, name }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }
    private async Task RefreshAfterEdit(long origin)
    {
        await RefreshPlaylists(lifetime.Token);
        if (origin == loadGeneration && !IsCatalogPage) await LoadAsync(CaptureLocation(), lifetime.Token);
    }
    public Task SelectHistoryDateAsync(string date)
    {
        if (stopping || publishing || !IsHistory || !HistoryDates.Contains(date) || date == HistoryDate) return Task.CompletedTask;
        return LoadUiAsync(CaptureLocation() with { Date = date, Offset = 0, Selected = null, Scroll = 0 });
    }
    public Task RestoreArchiveAsync(string id)
    {
        InvalidateCatalogAction();
        var origin = loadGeneration;
        return RunAsync(async () =>
        {
            await QueueCall("music.history.restorePlaylist", new { id });
            await StopOwnedPlayback(); await ReadQueue(lifetime.Token);
            if (origin == loadGeneration) await OpenPlayQueueAsync();
        });
    }
    public Task DeleteArchiveAsync(string id)
    {
        var origin = loadGeneration;
        return RunAsync(async () =>
        {
            await Call("music.history.removePlaylist", new { id }, lifetime.Token);
            if (origin == loadGeneration) await LoadAsync(CaptureLocation(), lifetime.Token);
        });
    }
    public Task ImportFilesAsync()
    {
        var selected = collection; var origin = loadGeneration;
        return RunAsync(async () =>
        {
            if (selected is null || dialogs.SelectAudioFiles() is not { } paths) return;
            var grants = await files.GrantAsync(paths, lifetime.Token);
            var songs = grants.Select(file => NativeMusicSong.CreateLocal(file.Id, file.Title)).ToArray();
            await Call("music.library.addSongs", new { id = selected.Id, ifRevision = selected.Revision, songs }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }
    public Task RemoveSongAsync()
    {
        InvalidateCatalogAction();
        var song = selectedSong; var selected = collection; var origin = loadGeneration;
        return RunAsync(async () =>
        {
            if (song is null) return;
            if (selected is not null)
                await Call("music.library.removeSong", new { id = selected.Id, ifRevision = selected.Revision, songId = song.Id }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    // The footer acts on the captured playing song, not the content-page selection.
    public Task AddCurrentToPlaylistAsync(string id)
    {
        var song = current; var origin = loadGeneration;
        if (song is null || !Playlists.Any(p => p.Id == id)) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var detail = await Call("music.library.detail", new { id, limit = 1 }, lifetime.Token);
            await Call("music.library.addSongs", new { id,
                ifRevision = MusicDto.Integer(detail.GetProperty("collection"), "revision"),
                songs = new[] { song.Raw } }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }

    public Task FavoriteAsync()
    {
        var song = selectedSong; var origin = loadGeneration;
        return RunAsync(async () =>
        {
            if (song is null) return;
            var favorite = await Call("music.library.detail", new { id = "favorites", limit = 1 }, lifetime.Token);
            await Call("music.library.addSongs", new { id = "favorites", ifRevision = MusicDto.Integer(favorite.GetProperty("collection"), "revision"),
                songs = new[] { song.Raw } }, lifetime.Token);
            await RefreshAfterEdit(origin);
        });
    }
    public Task AppendNextAsync()
    {
        InvalidateCatalogAction();
        var song = selectedSong; var origin = loadGeneration;
        var queueContext = CaptureFeedbackContext();
        return RunAsync(async () =>
        {
            if (song is null) return;
            await QueueCall("music.queue.append", new { songs = new[] { song.Raw }, next = true, context = queueContext });
            await ReadQueue(lifetime.Token);
            InvalidateQueueSurface();
        });
    }
    public Task PlaySelectionAsync()
    {
        TrackMusicRecovery(RetireMusicRecovery());
        InvalidateCatalogAction();
        onlineCancellation?.Cancel();
        var song = selectedSong; var selected = collection;
        var queueContext = CaptureFeedbackContext();
        var index = song is null ? -1 : location.Offset + Songs.IndexOf(song);
        return RunAsync(async () =>
        {
            if (song is null || index < 0) return;
            if (selected is not null) await QueueCall("music.queue.playCollection", new { id = selected.Id, ifRevision = selected.Revision, index });
            else await QueueCall("music.queue.replace", new { songs = new[] { song.Raw }, index = 0, context = queueContext });
            await ReadQueue(lifetime.Token); await StartCurrent(lifetime.Token);
        });
    }
    public Task PlayCollectionAsync()
    {
        TrackMusicRecovery(RetireMusicRecovery());
        InvalidateCatalogAction();
        onlineCancellation?.Cancel();
        var selected = collection;
        return RunAsync(async () =>
        {
            if (selected is null || selected.Count == 0) return;
            await QueueCall("music.queue.playCollection", new
            {
                id = selected.Id,
                ifRevision = selected.Revision,
                index = 0
            });
            await ReadQueue(lifetime.Token);
            await StartCurrent(lifetime.Token);
        });
    }
    public Task PlayContentDetailAsync() =>
        IsCatalogDetailPage ? PlayCatalogPlaylistAsync() : PlayCollectionAsync();
    public Task PlayContentSelectionAsync()
    {
        if (!IsCatalogDetailPage) return PlaySelectionAsync();
        var song = selectedSong;
        var card = song is null ? null : CatalogSections.SelectMany(section => section.Items)
            .FirstOrDefault(item => ReferenceEquals(item.Song, song));
        return card is null ? Task.CompletedTask : OpenCatalogCardAsync(card);
    }
    public Task TogglePlaybackAsync()
    {
        InvalidateCatalogAction();
        if (!stopping && current is not null && playback.OwnerId == Id && playback.Identity == current.EntryId &&
            playback is INativePlaybackContinuation intent && (playback.HasMedia || musicRecovery?.Recovering == true))
        {
            // Admission is synchronous: a queued/awaiting resolver cannot delay Pause.
            if (!intent.PauseRequested) ObserveExplicitRecommendationPause();
            var command = intent.PauseRequested ? playback.ResumeAsync() : playback.PauseAsync();
            return RunAsync(() => command);
        }
        onlineCancellation?.Cancel();
        return RunAsync(async () =>
        {
            if (current is null || !queueSaved) return;
            if (playback.OwnerId == Id && playback.Identity == current.EntryId && playback.HasMedia)
            {
                if (playback.IsPlaying) await playback.PauseAsync(); else await playback.ResumeAsync();
            }
            else await StartCurrent(lifetime.Token);
        });
    }
    public Task StepAsync(bool next, string reason = "manual")
    {
        InvalidateCatalogAction();
        TrackMusicRecovery(RetireMusicRecovery());
        onlineCancellation?.Cancel();
        return RunAsync(() => StepCore(next, reason));
    }
    public Task StepFromPresentationAsync(bool next, string presentationKey)
    {
        if (stopping || string.IsNullOrEmpty(presentationKey) || presentationKey != PlaybackPresentationKey)
            return Task.CompletedTask;
        return RunAsync(async () =>
        {
            if (presentationKey != PlaybackPresentationKey || current is null || !queueSaved) return;
            InvalidateCatalogAction();
            TrackMusicRecovery(RetireMusicRecovery());
            onlineCancellation?.Cancel();
            await StepCore(next, "manual");
        });
    }
    private async Task StepCore(bool next, string reason)
    {
        var origin = loadGeneration;
        if (next && await AdvanceRecommendation(reason == "ended"))
        {
            await RefreshRecommendationQueue(lifetime.Token);
            return;
        }
        var reply = await QueueCall(next ? "music.queue.next" : "music.queue.previous", next ? new { reason } : new { });
        var target = reply.GetProperty("targetId");
        if (target.ValueKind == JsonValueKind.Null)
        {
            if (reason == "ended") await StopOwnedPlayback();
            return;
        }
        MusicDto.Require(target.ValueKind == JsonValueKind.String);
        await ReadQueue(lifetime.Token); await StartCurrent(lifetime.Token, reason == "ended");
        InvalidateQueueSurface();
    }
    public Task ChangeModeAsync(int value)
    {
        InvalidateCatalogAction();
        return RunAsync(async () =>
        {
            if (value is < 0 or > 3) throw new MusicOperationException("INVALID_REQUEST");
            var reply = await QueueCall("music.queue.mode", new { mode = value });
            mode = MusicDto.Mode(reply);
        });
    }
    public Task SeekAsync(double seconds)
    {
        var epoch = playbackEpoch;
        return RunAsync(async () =>
        {
            if (epoch != playbackEpoch || playback.OwnerId != Id || !playback.HasMedia) return;
            // Explicit discontinuity: never count a short seek as a listened interval.
            SampleFeedback(false);
            await playback.SeekAsync(seconds);
            SampleFeedback(playback.IsPlaying);
        });
    }
    private async Task<JsonElement> QueueCall(string method, object? parameters = null)
    {
        queueEpoch++;
        try
        {
            var result = await Call(method, parameters, lifetime.Token);
            ObservePreloadQueueMutation(result);
            InvalidateQueueSurface();
            return result;
        }
        catch
        {
            // A failed command may leave a pending candidate in the native owner. Playback
            // stays gated until a later queue snapshot explicitly reports persisted state.
            queueSaved = false; RefreshBindings();
            InvalidateQueueSurface();
            throw;
        }
    }
    private async Task ReadQueue(CancellationToken cancellationToken)
    {
        if (stopping) return;
        var epoch = queueEpoch; var serial = ++queueRead;
        var result = await Call("music.queue.get", new { offset = 0, limit = 1 }, cancellationToken);
        ObservePreloadQueueSnapshot(result);
        var state = ParseQueue(result);
        if (stopping || epoch != queueEpoch || serial != queueRead) return;
        ApplyQueue(state);
    }
    private sealed record QueueState(SongRow? Current, int Mode, bool Saved, JsonElement? FeedbackContext = null);
    private static QueueState ParseQueue(JsonElement reply)
    {
        var mode = MusicDto.Mode(reply);
        var persistence = MusicDto.Text(reply.GetProperty("persistence"), "state");
        MusicDto.Require(persistence is "saved" or "pending");
        var value = reply.GetProperty("current");
        SongRow? current = value.ValueKind == JsonValueKind.Null ? null
            : SongRow.Read(value.GetProperty("song"), MusicDto.Text(value, "id"));
        var id = reply.GetProperty("currentId");
        MusicDto.Require(current is null ? id.ValueKind == JsonValueKind.Null
            : id.ValueKind == JsonValueKind.String && id.GetString() == current.EntryId);
        JsonElement? feedbackContext = value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("context", out var sourceContext) && sourceContext.ValueKind == JsonValueKind.Object
            ? sourceContext.Clone() : null;
        return new(current, mode, persistence == "saved", feedbackContext);
    }
    private void ApplyQueue(QueueState state)
    {
        // Background recommendation append/read must not replace the same live
        // SongRow reference while an unchanged occurrence is resolving media.
        if (current is not null && state.Current is not null && current.EntryId == state.Current.EntryId &&
            current.Raw.GetRawText() == state.Current.Raw.GetRawText())
            state = state with { Current = current };
        var changedCurrent = !ReferenceEquals(current, state.Current);
        current = state.Current; mode = state.Mode; queueSaved = state.Saved;
        currentFeedbackContext = state.FeedbackContext; UpdateFeedbackSelection();
        UpdateRecordingCandidates(); UpdateLyricSources(); RefreshQueueSurfaceCurrent(); RefreshBindings();
        if (changedCurrent) { PublishFavorite(false); _ = RefreshCurrentFavoriteAsync(); }
    }
    private async Task<bool> StopOwnedPlayback(Func<bool>? admission = null)
    {
        if (admission is not null && !admission()) return false;
        var epoch = playbackEpoch; var entry = openedEntry;
        var outputIdentity = playback.OwnerId == Id ? playback.Identity : null;
        await RetireMusicRecovery();
        // Retiring an uncooperative resolver can yield while a newer selection
        // opens media. That newer input and its handles do not belong to this stop.
        if (epoch != playbackEpoch || openedEntry != entry || admission is not null && !admission() ||
            playback.OwnerId == Id && playback.Identity != outputIdentity && playback.Identity != entry)
            return false;
        FinishFeedback("ui");
        ClearChorus();
        RetireLyrics();
        onlineCancellation?.Cancel(); onlineGeneration++;
        playbackEpoch++; openedEntry = null; recorded = false;
        Changed(nameof(PlaybackPresentationKey));
        playbackInstallation = null; Lyrics.Clear(); lyricStatus = ""; OnlineBindings();
        if (playback.OwnerId == Id) await playback.StopAsync();
        return true;
    }
    private async Task StartCurrent(CancellationToken cancellationToken, bool automatic = false,
        Func<bool>? admission = null, Action<long>? ownsEpoch = null,
        Recommendations.MusicRecommendationFeedback? recommendation = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (admission is not null && !admission()) throw new OperationCanceledException();
        if (stopping) throw new MusicOperationException("SHUTTING_DOWN");
        if (current is not { EntryId: not null } selected) throw new MusicOperationException("NO_PLAYBACK_TARGET");
        if (!queueSaved) throw new MusicOperationException("PENDING_WRITE");
        // A newly selected unavailable file must not leave the previous audio
        // running under the new song's metadata or seek controls.
        bool Admitted() => !stopping && !disposed && selected == current && (admission is null || admission());
        if (!await StopOwnedPlayback(Admitted) || !Admitted()) throw new OperationCanceledException();
        SelectFeedback(selected, automatic, recommendation);
        var epoch = playbackEpoch;
        ownsEpoch?.Invoke(epoch);
        if (selected.IsOnline)
        {
            await OpenOnlineCurrent(selected, epoch, cancellationToken, admission);
            await RestorePlaybackRateAsync(selected, epoch, cancellationToken);
            if (IsLyrics && !stopping && epoch == playbackEpoch) _ = RestoreLyricsAsync();
            return;
        }
        using var request = BeginOnline(cancellationToken);
        try
        {
            var media = new ResolvedMusic(await files.ResolveAsync(selected.Id, request.Token), new Dictionary<string, string>());
            request.Token.ThrowIfCancellationRequested();
            if (stopping || epoch != playbackEpoch || selected != current || admission is not null && !admission())
                throw new OperationCanceledException();
            openedEntry = selected.EntryId; recorded = false;
            await playback.OpenAsync(Id, selected.EntryId, media.Uri!, selected.Title, media.Headers, request.Token);
            await RestorePlaybackRateAsync(selected, epoch, request.Token);
            if (IsLyrics && !stopping && epoch == playbackEpoch) _ = RestoreLyricsAsync();
        }
        finally { EndOnline(request); }
    }
    private void PlaybackChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (stopping) return;
        if (!OwnsCurrent || !playback.HasMedia) playbackRateReceipt = null;
        if (args.PropertyName == nameof(IMusicPlayback.OwnerId) && playback.OwnerId != Id &&
            musicRecovery is { Opening: false })
            TrackMusicRecovery(RetireMusicRecovery());
        SampleFeedback();
        SyncChorus();
        ObserveRecommendationPlayback();
        ObserveMusicPreload();
        foreach (var name in new[] { nameof(IsPlaying), nameof(CurrentSource), nameof(CurrentQualityLabel), nameof(PlayGlyph), nameof(PositionSeconds), nameof(DurationSeconds),
            nameof(PositionText), nameof(DurationText), nameof(Volume), nameof(CanSeek), nameof(CanChangePlaybackRate),
            nameof(AcceptedPlaybackRate), nameof(ActiveLyricIndex), nameof(CanTogglePlayback) }) Changed(name);
        LyricTimingBindings();
    }
    private void PlaybackStarted(object? sender, EventArgs args)
    {
        if (stopping || playback.OwnerId != Id || playback.Identity != openedEntry || openedEntry is null) return;
        var state = musicRecovery;
        if (state is not null)
        {
            if (!CurrentRecovery(state)) return;
            if (state.Pending is { } pending)
            {
                if (CurrentPendingMedia(state, pending)) pending.SourceStarted = true;
                return;
            }
        }
        AcceptPlaybackStarted(state);
    }
    private void AcceptPlaybackStarted(MusicRecovery? state)
    {
        if (state is not null && !CurrentRecovery(state)) return;
        if (recorded)
        {
            if (state is { ConfirmSourceOnStart: true })
            {
                state.ConfirmSourceOnStart = false;
                bool restartFeedback = state.RestartFeedbackOnStart;
                state.RestartFeedbackOnStart = false;
                ConfirmOfficialPlayback();
                ConfirmPlaybackFileCache();
                if (restartFeedback) StartFeedback();
            }
            return;
        }
        if (state is not null)
        {
            state.ConfirmSourceOnStart = false;
            state.RestartFeedbackOnStart = false;
        }
        var entry = openedEntry; var epoch = playbackEpoch; var stamp = statusEpoch;
        recorded = true;
        ConfirmOfficialPlayback();
        ConfirmPlaybackFileCache();
        StartFeedback();
        ObserveRecommendationPlayback();
        var task = Record();
        eventWrites.Add(task); _ = Observe();
        async Task Record()
        {
            try { await Call("music.history.recordPlayed", new { entryId = entry, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, lifetime.Token); }
            catch (OperationCanceledException) { }
            catch
            {
                if (!stopping && epoch == playbackEpoch && stamp == statusEpoch)
                { status = "播放记录保存失败"; Changed(nameof(Status)); }
            }
        }
        async Task Observe() { try { await task; } finally { eventWrites.Remove(task); } }
    }
    private void PlaybackEnded(object? sender, EventArgs args)
    {
        if (stopping || playback.OwnerId != Id || openedEntry is null || playback.Identity != openedEntry || endedEpoch == playbackEpoch) return;
        TrackMusicRecovery(RetireMusicRecovery());
        FinishFeedback("end");
        var epoch = endedEpoch = playbackEpoch; var entry = openedEntry;
        _ = RunAsync(async () =>
        {
            // Preserve one terminal event while busy, never advance a replacement session.
            if (!stopping && epoch == playbackEpoch && current?.EntryId == entry && playback.OwnerId == Id && playback.Identity == entry)
                await StepCore(true, "ended");
        });
    }
    private void PlaybackFailed(object? sender, EventArgs args)
    {
        if (TryMusicRecovery()) return;
        if (!stopping && playback.OwnerId == Id && playback.Identity == openedEntry)
        { FinishFeedback("error"); status = "播放失败"; Changed(nameof(Status)); }
    }
    public Task PrepareShutdownAsync()
    {
        if (resumption is { IsCompleted: false } recovering) return PrepareAfterResume(recovering);
        if (preparation is not null) return preparation;
        FinishFeedback("interrupt");
        stopping = true; // Seal admission synchronously, before yielding to any callback.
        RetireQueueSurfaceRequest();
        playerDetail.Deactivate();
        SealRecommendations();
        TrackMusicRecovery(RetireMusicRecovery());
        aggregateEpoch++;
        discoveryEpoch++; discoveryRequest?.Cancel();
        searchHistoryRequest?.Cancel();
        InvalidateCatalogAction();
        ClearAccountQr();
        _ = DeactivateSettingsAsync();
        _ = vipPage.PrepareShutdownAsync();
        return preparation = PrepareCore();
    }
    private async Task PrepareAfterResume(Task recovering)
    { await recovering; await PrepareShutdownAsync(); }
    private void SubscribePlayback()
    {
        if (playbackSubscribed) return;
        application.ChorusInvalidated += ChorusAccountChanged;
        application.AccountDetailsInvalidated += OfficialAccountChanged;
        playback.PropertyChanged += PlaybackChanged;
        playback.Started += PlaybackStarted; playback.Ended += PlaybackEnded; playback.Failed += PlaybackFailed;
        playbackSubscribed = true;
    }
    private void UnsubscribePlayback()
    {
        if (!playbackSubscribed) return;
        application.ChorusInvalidated -= ChorusAccountChanged;
        application.AccountDetailsInvalidated -= OfficialAccountChanged;
        ClearChorus();
        playback.PropertyChanged -= PlaybackChanged;
        playback.Started -= PlaybackStarted; playback.Ended -= PlaybackEnded; playback.Failed -= PlaybackFailed;
        playbackSubscribed = false;
    }
    private async Task PrepareCore()
    {
        // Publish the cached task before bindings/cancellation can re-enter shutdown.
        await Task.Yield();
        AdvanceLoadGeneration(); playbackEpoch++;
        UnsubscribePlayback();
        loadCancellation?.Cancel(); // Only reads: never cancel admitted mutations/history writes.
        collectionPageCancellation?.Cancel();
        // Resolution may be a network read; cancel it before waiting for commandTail.
        onlineCancellation?.Cancel(); RetireLyrics(); CancelMusicPreload();
        RefreshBindings();
        Exception? settingsFailure = null;
        try { await DeactivateSettingsAsync(); }
        catch (Exception error) { settingsFailure = error; }
        try { await vipPage.PrepareShutdownAsync(); }
        catch (Exception error) { settingsFailure ??= error; }
        await Task.WhenAll(commandTail, accountSettlement, Task.WhenAll(eventWrites.ToArray()),
            Task.WhenAll(loadDrains.ToArray()), RetireSearchHistoryAsync());
        // A failure stays cached, sealed and observable. Do not tear down the
        // lifetime or any App-owned runtime/store/player before forced exit.
        Exception? failure = accountSaveUncertain ? new MusicOperationException("COMMIT_UNKNOWN") : settingsFailure;
        try { await SaveVolumeCore(lifetime.Token); }
        catch (Exception error) { failure ??= error; }
        try { await DrainRecommendations(); }
        catch (Exception error) { failure ??= error; }
        try { await RetireDiscoveryAsync(); }
        catch (Exception error) { failure ??= error; }
        try { await application.PrepareShutdownAsync(); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public ValueTask DisposeAsync()
    {
        if (abortion is not null) return new(abortion);
        var prepare = PrepareShutdownAsync();
        return new(disposal ??= DisposeCore(prepare));
    }
    public Task CancelShutdownPreparationAsync()
    {
        if (disposed || disposal is not null || abortion is not null) return Task.FromException(new InvalidOperationException("CLOSED"));
        if (preparation is null && resumption is null) return Task.CompletedTask;
        return resumption ??= ResumeCore(preparation!);
    }
    private async Task ResumeCore(Task prepare)
    {
        await Task.Yield();
        try { await prepare; } catch { /* The owning services retain unresolved receipts. */ }
        if (disposed || disposal is not null || abortion is not null) throw new InvalidOperationException("CLOSED");
        await application.CancelShutdownPreparationAsync();
        if (disposed || disposal is not null || abortion is not null) throw new InvalidOperationException("CLOSED");
        RetireCatalogSession(); // Never revive private visual cache or a prepared playlist token.
        SubscribePlayback();
        preparation = null; resumption = null; stopping = false;
        ResumeRecommendations();
        await RecommendationCommand("music.recommendations.get", new { });
        if (playback is IMusicPlaybackRate && current is { } surviving && OwnsCurrent && playback.HasMedia)
        {
            var epoch = playbackEpoch;
            // Preparation does not own the App's local-file player. A
            // physically settled late rate command may have changed that input;
            // reapply the last accepted intent without opening/resuming media.
            await RunAsync(() => RestorePlaybackRateAsync(surviving, epoch, lifetime.Token));
        }
        LibrarySyncChanged(); // A restarted fast round may have finished while the module was still sealed.
        if (settings.IsLoaded) await ActivateSettingsAsync();
        RefreshBindings();
        if (page.IsLoaded && IsDiscovery) await ActivateDiscoveryViewAsync();
        await vipPage.CancelShutdownPreparationAsync();
    }
    public Task AbortAsync()
    {
        if (abortion is not null) return abortion;
        if (disposal is not null) return disposal;
        var prepare = preparation ?? PrepareShutdownAsync();
        return abortion = AbortCore(prepare);
    }
    private async Task AbortCore(Task prepare)
    {
        await Task.Yield();
        Exception? failure = null;
        stopping = true;
        try { lifetime.Cancel(); } catch (Exception error) { failure = error; }
        try { await prepare; } catch { /* Explicit unsaved exit, not a successful save. */ }
        if (resumption is { } recovering) { try { await recovering; } catch { } }
        try { await DisposeLibrarySyncAsync(); } catch (Exception error) { failure ??= error; }
        try { await DisposeAccountDetailsAsync(); } catch (Exception error) { failure ??= error; }
        try { await vipPage.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        try { await application.AbortAsync(); } catch (Exception error) { failure ??= error; }
        UnsubscribeAggregateSearch();
        UnsubscribeDiscovery();
        ReleaseRecommendations();
        disposed = true; UnsubscribePlayback(); lifetime.Dispose();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private async Task DisposeCore(Task prepare)
    {
        await Task.Yield(); // Cache the finalization task before synchronous completion/re-entry.
        Exception? failure = null;
        try { await prepare; } // Observe the same failure; never repeat preparation.
        catch (Exception error) { failure = error; }
        finally
        {
            disposed = true;
            UnsubscribeAggregateSearch();
            UnsubscribeDiscovery();
            ReleaseRecommendations();
            try { lifetime.Cancel(); }
            catch (Exception error) { failure ??= error; }
            finally { lifetime.Dispose(); }
            if (resumption is { } recovering) { try { await recovering; } catch { } }
            try { await DisposeLibrarySyncAsync(); }
            catch (Exception error) { failure ??= error; }
            try { await DisposeAccountDetailsAsync(); }
            catch (Exception error) { failure ??= error; }
            try { await vipPage.DisposeAsync(); }
            catch (Exception error) { failure ??= error; }
            try { await application.DisposeAsync(); }
            catch (Exception error) { failure ??= error; }
            // The capability owns its application. Runtime, store and playback remain App-owned.
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

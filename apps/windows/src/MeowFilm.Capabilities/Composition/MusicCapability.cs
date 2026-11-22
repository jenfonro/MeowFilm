using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Media;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>
/// Slim composition root and runtime kernel for the music capability.
/// All domain state, bindable properties, and commands live in the 9 standalone domain controllers;
/// views never reference <see cref="MusicCapability"/>.
/// </summary>
public sealed partial class MusicCapability :
    IDesktopModule,
    IDesktopStartupParticipant,
    IDesktopNavigationSource,
    IDesktopGeneralSettingsSource,
    IDesktopGlobalSurfaceSource,
    IDesktopOwnedNoticeSource,
    IDebugModuleAdapterSource
{
    private readonly IMusicApplication application;
    private readonly IMusicPlayback playback;
    private readonly IMusicDialogs dialogs;
    private readonly GrantedMediaFiles files;
    private readonly PlaybackCache.MusicAudioFileCache? audioFileCache;
    private readonly PlaybackCache.MusicChorusCache persistentChorus;
    private readonly IStorageDatabase database;
    private readonly PlaybackCache.MusicPresentationCache presentationCache;
    private readonly bool ownsPresentationCache;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> eventWrites = [];
    private Task commandTail = Task.CompletedTask;
    private Task playbackCommandTail = Task.CompletedTask;
    private long playbackActionGeneration;
    private Task? preparation, abortion;
    private string status = "";
    private int commands;
    private long statusEpoch;
    private bool stopping, disposed;
    private readonly MusicDebugAdapter debugAdapter;

    public MusicCapability(IRuntimeClient runtime, IStorageDatabase store, IMusicLibraryStore libraryStore, NativeAudioPlayback playback, string? playbackCacheRoot = null)
        : this(new MusicNativeApplication(store, libraryStore, runtime),
            store, new NativeMusicPlayback(playback), playbackCacheRoot: playbackCacheRoot) { }

    public MusicCapability(IMusicApplication application, IStorageDatabase store, NativeAudioPlayback playback,
        IMusicDialogs? dialogs = null, string? playbackCacheRoot = null)
        : this(application, store, new NativeMusicPlayback(playback), dialogs, playbackCacheRoot) { }

    public MusicCapability(IRuntimeClient runtime, IStorageDatabase store, IMusicLibraryStore libraryStore, IMusicPlayback playback,
        IMusicDialogs? dialogs = null)
        : this(new MusicNativeApplication(store, libraryStore, runtime), store, playback, dialogs) { }

    public MusicCapability(IMusicApplication application, IStorageDatabase store, IMusicPlayback playback,
        IMusicDialogs? dialogs = null, string? playbackCacheRoot = null)
        : this(application, store, playback, null, dialogs, playbackCacheRoot) { }

    internal MusicCapability(IMusicApplication application, IStorageDatabase store, IMusicPlayback playback,
        PlaybackCache.MusicAudioFileCache? playbackCache, IMusicDialogs? dialogs = null,
        string? playbackCacheRoot = null)
    {
        this.application = application ?? throw new ArgumentNullException(nameof(application));
        this.playback = playback;
        this.dialogs = dialogs ?? new MusicDialogs();
        database = store;
        ownsPresentationCache = application is not MusicNativeApplication;
        presentationCache = application is MusicNativeApplication native ? native.PresentationCache : new(store);
        ArtworkLoader = new(byteCache: presentationCache, loadCompleted:
            application is MusicNativeApplication artworkOwner ? artworkOwner.ArtworkLoadCompletedAsync : null);
        audioFileCache = playbackCache ?? (playbackCacheRoot is null ? null :
            new PlaybackCache.MusicAudioFileCache(playbackCacheRoot, store));
        persistentChorus = new(store);
        files = new(store, [".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma"]);
        Receipts = new();
        Presentation = new(this);

        Settings = new(this);
        Accounts = new(this);
        Discovery = new(this);
        Lyrics = new(this);
        Queue = new(this);
        Search = new(this);
        Catalog = new(this);
        Playback = new(this);
        Library = new(this);
        debugAdapter = new(this);

        Accounts.InitializeAfterSettings();
        if (application is MusicNativeApplication repositoryOwner)
            repositoryOwner.SongMetadataChanged += Playback.RepositorySongsChanged;
        Playback.SubscribePlayback();
        Library.RebuildNavigation();
    }

    public MusicSettingsController Settings { get; }
    public MusicAccountsController Accounts { get; }
    public MusicDiscoveryController Discovery { get; }
    public MusicLyricsController Lyrics { get; }
    public MusicQueueController Queue { get; }
    public MusicSearchController Search { get; }
    public MusicCatalogController Catalog { get; }
    public MusicPlaybackController Playback { get; }
    public MusicLibraryController Library { get; }

    internal MusicSettingReceiptTracker Receipts { get; }
    internal MusicPresentation Presentation { get; }
    internal ArtworkService ArtworkLoader { get; }
    internal MusicDebugAdapter DebugAdapterInstance => debugAdapter;
    public IDebugModuleAdapter DebugAdapter => debugAdapter;

    internal IMusicApplication Application => application;
    internal IMusicPlayback PlaybackDevice => playback;
    internal IMusicDialogs Dialogs => dialogs;
    internal GrantedMediaFiles Files => files;
    internal IStorageDatabase Database => database;
    internal PlaybackCache.MusicAudioFileCache? AudioFileCache => audioFileCache;
    internal PlaybackCache.MusicChorusCache PersistentChorus => persistentChorus;
    internal PlaybackCache.MusicPresentationCache PresentationCache => presentationCache;
    internal CancellationTokenSource Lifetime => lifetime;
    internal HashSet<Task> LoadDrains => loadDrains;
    internal HashSet<Task> EventWrites => eventWrites;
    internal Task CommandTail => commandTail;
    internal long PlaybackActionGeneration => playbackActionGeneration;
    internal bool Stopping => stopping;
    internal bool Disposed => disposed;
    internal int Commands => commands;
    internal bool Busy => commands > 0 || loadCancellation is not null;
    internal bool CanEdit => !stopping && !Busy;
    internal string RawStatus => status;
    internal string Status => Catalog.CatalogAccountNotice.Length > 0 ? Catalog.CatalogAccountNotice : status;
    internal long StatusEpoch => statusEpoch;
    internal ObservableCollection<SongRow> ActiveSongs => Search.IsSearchPage ? Search.SearchSongsSource : Library.CatalogSongsSource;

    public string Id => "music";
    public string Title => "音乐";
    public string Version => "0.4.0";
    public ObservableCollection<NavigationItem> Navigation { get; } = [];
    public FrameworkElement View => Presentation.Page;
    public FrameworkElement SettingsView => Presentation.Settings;
    public FrameworkElement AppearanceSettingsView => Presentation.Appearance;
    public FrameworkElement HomeSourceSettingsView => Presentation.HomeSourceSettings;
    public FrameworkElement CacheSettingsView => Presentation.CacheSettings;
    public FrameworkElement PlayerView => Presentation.Player;

    public IReadOnlyList<DesktopOwnedNotice> OwnedNotices => Receipts.OwnedNotices;
    public event EventHandler? OwnedNoticesChanged
    {
        add => Receipts.OwnedNoticesChanged += value;
        remove => Receipts.OwnedNoticesChanged -= value;
    }

    // MusicCapability exposes immutable module-level properties; all mutable domain state lives on the 9 controllers.
    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged { add { } remove { } }
    public event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested;
    public event EventHandler<DesktopGlobalSurfaceRequestEventArgs>? GlobalSurfaceRequested;

    public Task PrepareStartupAsync(CancellationToken cancellationToken = default) =>
        Catalog.PrepareStartupAsync(cancellationToken);

    public FrameworkElement GetGlobalSurface(DesktopGlobalSurface surface) => surface switch
    {
        DesktopGlobalSurface.PlayerDetail => Presentation.Detail,
        DesktopGlobalSurface.PlayQueue => Presentation.Queue,
        _ => throw new ArgumentOutOfRangeException(nameof(surface))
    };

    public async Task ActivateGlobalSurfaceAsync(DesktopGlobalSurface surface, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (surface)
        {
            case DesktopGlobalSurface.PlayerDetail:
                Presentation.DeactivatePlayer();
                Lyrics.SetPlayerDetailOpen(true);
                await Presentation.Detail.ActivateAsync(cancellationToken);
                break;
            case DesktopGlobalSurface.PlayQueue:
                await Queue.ActivatePlayQueueAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(surface));
        }
    }

    public void DeactivateGlobalSurface(DesktopGlobalSurface surface)
    {
        switch (surface)
        {
            case DesktopGlobalSurface.PlayerDetail:
                Lyrics.SetPlayerDetailOpen(false);
                Presentation.DeactivateDetail();
                if (!Lyrics.NeedsLyrics) Lyrics.CancelLyrics();
                break;
            case DesktopGlobalSurface.PlayQueue:
                Queue.DeactivatePlayQueue();
                break;
        }
    }

    public Task OpenPlayerDetailAsync()
    {
        if (!stopping) GlobalSurfaceRequested?.Invoke(this, new(DesktopGlobalSurface.PlayerDetail));
        return Task.CompletedTask;
    }

    public Task OpenPlayQueueAsync()
    {
        if (!stopping) GlobalSurfaceRequested?.Invoke(this, new(DesktopGlobalSurface.PlayQueue));
        return Task.CompletedTask;
    }

    private FrameworkElement ModalOwner =>
        SettingsView.IsLoaded && DesktopVisual.IsVisible(SettingsView) ? SettingsView
        : PlayerView.IsLoaded ? PlayerView
        : View;

    internal Task<bool> ConfirmAsync(string message, string title, CancellationToken token = default) =>
        dialogs.ConfirmAsync(ModalOwner, message, title, token);

    internal long NextStatusEpoch() => ++statusEpoch;
    internal void SetStatus(string message) { status = message; NotifyStatusChanged(); }
    internal void SetStatusQuiet(string message) => status = message;
    internal void ClearStatusQuiet() => status = "";
    internal void ClearTransientStatus() { status = ""; Catalog.ClearCatalogAccountNotice(); NotifyStatusChanged(); }
    internal Task Notify(string message) { SetStatus(message); return Task.CompletedTask; }

    internal void NotifyStatusChanged()
    {
        Settings.NotifyStatusChanged();
        Catalog.NotifyStatusChanged();
        Library.NotifyStatusChanged();
    }

    internal void RetireOperationStatus()
    {
        ++statusEpoch;
        status = "";
        Catalog.ClearCatalogAccountNotice();
        NotifyStatusChanged();
    }

    internal void NotifyMusicSourcesChanged()
    {
        Playback.IncrementSelectionSourcesRevision();
        Lyrics.NotifySourcesChanged();
    }

    internal void RefreshBindings()
    {
        Settings.RefreshAvailability();
        Accounts.RefreshAvailability();
        Discovery.RefreshAvailability();
        Lyrics.RefreshAvailability();
        Queue.RefreshAvailability();
        Search.RefreshAvailability();
        Catalog.RefreshAvailability();
        Playback.RefreshAvailability();
        Library.RefreshAvailability();
    }

    internal Task RequestNavigationAsync(string destination)
    {
        if (stopping) return Task.CompletedTask;
        if (NavigationRequested is not { } requested) return NavigateAsync(destination, lifetime.Token);
        requested(this, new(destination)); // Shell owns route history and back.
        return Task.CompletedTask;
    }

    internal async Task<JsonElement> Call(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (MusicSettingReceiptTracker.SettingLabel(method) is not null)
            return await CallSettledReceipt(method, parameters, cancellationToken);
        var reply = await application.ExecuteAsync(method, parameters ?? new { }, cancellationToken).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateReply(method, reply);
    }

    internal async Task<JsonElement> CallSettled(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        // Request owners that promise physical drain must retain the application
        // task after cancellation instead of only cancelling their own wait.
        var reply = await application.ExecuteAsync(method, parameters ?? new { }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ValidateReply(method, reply);
    }

    internal async Task<JsonElement> CallSettledReceipt(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        // Mutating commands must observe a successful commit receipt even
        // when caller cancellation raced with its delivery. Only an explicit
        // cancelled receipt means the command was not admitted.
        var label = MusicSettingReceiptTracker.SettingLabel(method);
        var settings = MusicSettingReceiptTracker.SettingKeys(method, parameters);
        long submission = label is null ? 0 : Receipts.NextSubmission();
        try
        {
            var reply = await application.ExecuteAsync(method, parameters ?? new { }, cancellationToken);
            var result = ValidateReply(method, reply);
            if (label is not null)
                foreach (var setting in settings) Receipts.Record(setting, label, submission, confirmed: true);
            return result;
        }
        catch (MusicOperationException error) when (error.Code == "COMMIT_UNKNOWN")
        {
            if (label is not null)
                foreach (var setting in settings) Receipts.Record(setting, label, submission, confirmed: false);
            throw;
        }
        catch (MusicOperationException error) when (error.Code == "CANCELLED" &&
            cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    internal static JsonElement ValidateReply(string method, JsonElement reply)
    {
        MusicDto.Require(reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("ok", out _));
        var success = reply.GetProperty("ok");
        if (success.ValueKind == JsonValueKind.False)
        {
            var code = MusicDto.Text(reply, "code");
            // Catalog uses one login-required notice for missing/expired authority;
            // the account owner, not cached display content, decides revocation.
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

    internal Task RunAsync(Func<Task> action, bool publishStatus)
    {
        if (stopping) return Task.CompletedTask;
        var previous = commandTail;
        // Passive Sync refreshes share admission/drain, not foreground status ownership.
        var stamp = publishStatus ? ++statusEpoch : statusEpoch;
        commands++;
        if (publishStatus) { status = ""; Catalog.ClearCatalogAccountNotice(); }
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

    // Dedicated playback/transition lane: keeps transport, step, seek, mode, and source
    // switches responsive without incrementing global editing commands or greying out controls.
    internal Task RunPlaybackActionAsync(Func<long, Task> action, bool publishStatus = true, bool supersede = true)
    {
        if (stopping) return Task.CompletedTask;
        var previous = playbackCommandTail;
        var generation = supersede ? ++playbackActionGeneration : playbackActionGeneration;
        if (supersede) Playback.CancelOnline();
        var stamp = publishStatus ? ++statusEpoch : statusEpoch;
        if (publishStatus) ClearTransientStatus();
        return playbackCommandTail = Execute();
        async Task Execute()
        {
            await Task.Yield();
            await previous;
            if (disposed || stopping || supersede && generation != playbackActionGeneration) return;
            try { await action(generation); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (publishStatus && !disposed && stamp == statusEpoch) SetStatus(ErrorText(error));
            }
            finally { if (!disposed) Playback.RefreshPresentationAvailability(); }
        }
    }

    public async Task ExecuteMediaShortcutAsync(MediaShortcutAction action)
    {
        if (stopping || disposed) return;
        try
        {
            switch (action)
            {
                case MediaShortcutAction.TogglePlayback:
                    if (Playback.CanTogglePlayback) await Playback.TogglePlaybackAsync();
                    else await Notify("当前无法播放或暂停");
                    break;
                case MediaShortcutAction.Previous:
                case MediaShortcutAction.Next:
                    if (Playback.CanStep) await Playback.StepAsync(action == MediaShortcutAction.Next);
                    else await Notify("当前无法切换歌曲");
                    break;
                case MediaShortcutAction.VolumeUp:
                case MediaShortcutAction.VolumeDown:
                    Playback.Volume += action == MediaShortcutAction.VolumeUp ? 5 : -5;
                    await Settings.SaveVolumeAsync();
                    break;
                case MediaShortcutAction.ToggleFavorite:
                    if (Playback.CanFavoriteCurrent) await Playback.ToggleCurrentFavoriteAsync();
                    else await Notify("当前无法收藏歌曲");
                    break;
                case MediaShortcutAction.ToggleDesktopLyrics:
                    await Lyrics.ToggleDesktopLyricsAsync();
                    break;
                case MediaShortcutAction.ToggleTranslation:
                    Lyrics.ToggleLyricTranslation();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }
        catch (OperationCanceledException) { if (!stopping) await Notify("操作已取消"); }
        catch (Exception error) { if (!stopping) await Notify(ErrorText(error)); }
    }

    internal static void ReconcileSongRows(ObservableCollection<SongRow> target, IReadOnlyList<SongRow> next)
    {
        // A queue occurrence has EntryId; catalog members can repeat the same permanent song.
        // Match each occurrence once, in order, rather than deduplicating songs by their Id.
        var available = target.GroupBy(row => row.SelectionKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<SongRow>(group), StringComparer.Ordinal);
        var rows = next.Select(song =>
        {
            if (!available.TryGetValue(song.SelectionKey, out var occurrences) || occurrences.Count == 0) return song;
            var retained = occurrences.Dequeue();
            if (!ReferenceEquals(retained, song)) retained.ApplyRepository(song.Raw, song.RepositoryPublication);
            return retained;
        }).ToArray();
        var retainedRows = rows.ToHashSet(ReferenceEqualityComparer.Instance);
        for (int index = target.Count - 1; index >= 0; index--)
            if (!retainedRows.Contains(target[index])) target.RemoveAt(index);
        for (int index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (index < target.Count && ReferenceEquals(target[index], row)) continue;
            int previous = -1;
            for (int position = index + 1; position < target.Count; position++)
                if (ReferenceEquals(target[position], row)) { previous = position; break; }
            if (previous >= 0) target.Move(previous, index);
            else target.Insert(index, row);
        }
    }

    public string OperationErrorText(Exception error) => ErrorText(error);

    internal static string ErrorText(Exception error) => error switch
    {
        PlaybackException { Code: PlaybackError.InvalidRate } or MusicOperationException { Code: PlaybackError.InvalidRate } => "播放倍速须为 0.5–3，间隔 0.1",
        PlaybackException { Code: PlaybackError.RateUnsupported } or MusicOperationException { Code: PlaybackError.RateUnsupported or "RATE_UNSUPPORTED" } => "当前音频不支持此播放倍速",
        PlaybackException { Code: PlaybackError.Superseded } or MusicOperationException { Code: PlaybackError.Superseded } => "播放内容已变更",
        InvalidDataException => "文件不可用",
        MusicLibraryException native => ErrorText(new MusicOperationException(native.Code)),
        MusicOperationException { Code: "CORRUPT_QUEUE" } => "播放队列数据无效或格式不兼容",
        MusicOperationException { Code: "CORRUPT_HISTORY" } => "播放历史数据无效或格式不兼容",
        MusicOperationException { Code: "CORRUPT_LIBRARY" } => "曲库数据无效或格式不兼容",
        MusicOperationException { Code: "CORRUPT_STORAGE" or "CORRUPT_SYNC" } => "本地数据无效或格式不兼容",
        MusicOperationException { Code: "REVISION_CONFLICT" or "STORAGE_CONFLICT" } => "数据已变更，请刷新",
        MusicOperationException { Code: "COMMIT_UNKNOWN" } => "保存结果未确认",
        MusicOperationException { Code: "LIMIT" } => "操作超过数量限制",
        MusicOperationException { Code: "INVALID_SOURCE_URL" } => "请输入 HTTPS .js 音源地址",
        MusicOperationException { Code: "SOURCE_DOWNLOAD_FAILED" } => "音源下载失败",
        MusicOperationException { Code: "OFFICIAL_SOURCE_DISABLED" } => "该官方音源已停用",
        MusicOperationException { Code: "OFFICIAL_SOURCES_CHANGED" } => "官方音源设置已变更，请重试",
        MusicOperationException { Code: "SOURCES_CHANGED" } => "音源设置已变更，请重试",
        MusicOperationException { Code: "PENDING_WRITE" } => "保存未完成，请重试保存",
        MusicOperationException { Code: "TARGET_REQUIRED" or "TARGET_CHANGED" } => "未选择匹配的音源、平台或歌曲",
        MusicOperationException { Code: "DISABLED" } => "音源已停用",
        MusicOperationException { Code: "CAPABILITY_UNAVAILABLE" } => "当前音源不支持此操作或音质",
        MusicOperationException { Code: "PERMISSION_DENIED" } => "音源权限未授权",
        MusicOperationException { Code: "AUTH_REQUIRED" } => "当前内容需要平台登录",
        MusicOperationException { Code: "SDK_FAILURE" or "SDK_CLEANUP" } => "网易云订阅验证失败",
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
        SelectedLyricSnapshotException { Error: SelectedLyricSnapshotError.InvalidEncoding } => "歌词文本编码无效",
        SelectedLyricSnapshotException { Error: SelectedLyricSnapshotError.TooLarge } => "歌词文件超过大小限制",
        SelectedScriptSnapshotException or SelectedLyricSnapshotException => "所选文件不可用",
        MusicOperationException { Code: "INVALID_LYRIC_RESPONSE" } => "歌词格式无效",
        MusicOperationException { Code: "SCRIPT_RUNTIME_UNAVAILABLE" } => "脚本音源服务不可用",
        MusicOperationException { Code: "SOURCE_REDIRECT" } => "音源地址发生重定向，请填写最终地址",
        MusicOperationException { Code: "SOURCE_DISABLED" } => "音源已停用",
        MusicOperationException { Code: "SOURCE_NOT_FOUND" } => "音源不存在",
        MusicOperationException { Code: "STALE_SOURCE" } => "音源已变更，请重试",
        MusicOperationException { Code: "SCRIPT_FAILURE" } => "音源执行失败",
        MusicOperationException { Code: "SCRIPT_CLEANUP" } => "音源未能完全停止，请重启后重试",
        MusicOperationException { Code: "MEDIA_UNAVAILABLE" or "INVALID_MEDIA_CANDIDATE" or "EXPIRED_MEDIA_CANDIDATE" } => "音源未返回可用音频",
        MusicOperationException { Code: "RECORDING_MISMATCH" } => "音源返回的歌曲不匹配",
        JsonException => "JSON 格式错误",
        BridgeException { Code: "CLOSED" } => "业务连接已中断",
        _ => "操作失败，请重试"
    };

    public Task PrepareShutdownAsync()
    {
        if (preparation is not null) return preparation;
        Playback.FinishFeedback("interrupt");
        Lyrics.PrepareDesktopLyricsShutdown();
        stopping = true; // Seal admission synchronously, before yielding to any callback.
        Playback.RefreshCurrentArtwork();
        Queue.RetireQueueSurfaceRequest();
        Presentation.DeactivateDetail();
        Queue.SealRecommendations();
        Playback.TrackMusicRecovery(Playback.RetireMusicRecovery());
        Search.CancelForShutdown();
        Discovery.CancelForShutdown();
        Settings.CancelSearchHistoryForShutdown();
        Catalog.InvalidateCatalogAction();
        Accounts.ClearAccountQr();
        _ = Settings.DeactivateSettingsAsync();
        _ = Catalog.VipPage.PrepareShutdownAsync();
        return preparation = PrepareCore();
    }

    private async Task PrepareCore()
    {
        // Publish the cached task before bindings/cancellation can re-enter shutdown.
        await Task.Yield();
        AdvanceLoadGeneration();
        Playback.AdvancePlaybackEpochForShutdown();
        Playback.UnsubscribePlayback();
        Playback.RefreshPresentation();
        loadCancellation?.Cancel(); // Only reads: never cancel admitted mutations/history writes.
        Settings.CancelCatalogSourceReadForShutdown(); // The shared startup source read is independent of its waiters.
        Catalog.CancelStartupForShutdown();
        Accounts.CancelReadForShutdown();
        Library.CancelCollectionPage();
        // Resolution may be a network read; cancel it before waiting for commandTail.
        Playback.CancelOnline(); Lyrics.RetireLyrics(); Playback.CancelMusicPreload();
        RefreshBindings();
        Exception? settingsFailure = null;
        try { await Accounts.RetireAccountTransferAsync(); await Settings.DeactivateSettingsAsync(); }
        catch (Exception error) { settingsFailure = error; }
        try { await Settings.DeactivateCatalogSourceSettingsAsync(); }
        catch (Exception error) { settingsFailure ??= error; }
        try { await Catalog.VipPage.PrepareShutdownAsync(); }
        catch (Exception error) { settingsFailure ??= error; }
        await Task.WhenAll(commandTail, playbackCommandTail, Accounts.AccountReadTask, Accounts.AccountSettlementTask,
            Task.WhenAll(eventWrites.ToArray()), Task.WhenAll(loadDrains.ToArray()), Settings.RetireSearchHistoryAsync());
        // A failure stays cached, sealed and observable. Do not tear down the
        // lifetime or any App-owned runtime/store/player before final release.
        Exception? failure = Accounts.AccountSaveUncertain ? new MusicOperationException("COMMIT_UNKNOWN") : settingsFailure;
        try { await Settings.SaveVolumeCore(lifetime.Token); }
        catch (Exception error) { failure ??= error; }
        try { await Queue.DrainRecommendations(); }
        catch (Exception error) { failure ??= error; }
        try { await Discovery.RetireDiscoveryAsync(); }
        catch (Exception error) { failure ??= error; }
        try { await application.PrepareShutdownAsync(); }
        catch (Exception error) { failure ??= error; }
        await database.DrainAsync();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public ValueTask DisposeAsync() => new(AbortAsync());

    public Task AbortAsync()
    {
        if (abortion is not null) return abortion;
        var prepare = preparation ?? PrepareShutdownAsync();
        return abortion = AbortCore(prepare);
    }

    private async Task AbortCore(Task prepare)
    {
        await Task.Yield();
        Exception? failure = null;
        stopping = true;
        Lyrics.ReleaseDesktopLyrics();
        try { lifetime.Cancel(); } catch (Exception error) { failure = error; }
        try { await prepare; } catch { /* The save receipt retains its failure; release still retires settled owners. */ }
        try { await Accounts.DisposeLibrarySyncAsync(); } catch (Exception error) { failure ??= error; }
        try { await Accounts.DisposeAccountDetailsAsync(); } catch (Exception error) { failure ??= error; }
        try { await Catalog.VipPage.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        try { await application.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        if (application is MusicNativeApplication repositoryOwner)
            repositoryOwner.SongMetadataChanged -= Playback.RepositorySongsChanged;
        try { await ArtworkLoader.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        if (ownsPresentationCache)
            try { await presentationCache.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        Search.Unsubscribe();
        Discovery.Unsubscribe();
        Queue.ReleaseRecommendations();
        disposed = true;
        Playback.UnsubscribePlayback();
        lifetime.Dispose();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

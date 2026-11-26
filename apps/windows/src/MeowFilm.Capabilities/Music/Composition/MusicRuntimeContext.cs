using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Media;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;
using MeowFilm.Threading;

namespace MeowFilm.Capabilities;

internal sealed record MusicLocation(string Route, string Query = "", int Offset = 0,
    string? Date = null, string? Selected = null, double Scroll = 0)
{
    public string Key => Route == "search" ? "search/" + Query : Route;
}

/// <summary>
/// Pure infrastructure and execution kernel for the music capability.
/// Holds zero sibling controller references and zero view properties; cross-domain
/// reactions and presentation callbacks are wired explicitly at <see cref="MusicCapability"/>.
/// </summary>
internal sealed class MusicRuntimeContext
{
    private Task commandTail = Task.CompletedTask;
    private Task playbackCommandTail = Task.CompletedTask;
    private long playbackActionGeneration;
    private string status = "";
    private int commands;
    private long statusEpoch;

    private readonly MusicPresentation presentation;
    private readonly MusicCapability shell;

    internal MusicRuntimeContext(MusicNativeApplication application, IStorageDatabase dataStore,
        IStorageDatabase cacheStore, NativeAudioPlayback playback, string? playbackCacheRoot,
        MusicPresentation presentation, MusicCapability shell)
    {
        Application = application;
        PlaybackDevice = playback;
        DataDatabase = dataStore;
        CacheDatabase = cacheStore;
        PresentationCache = application.PresentationCache;
        ArtworkLoader = new(byteCache: PresentationCache, loadCompleted: application.ArtworkLoadCompletedAsync);
        AudioFileCache = playbackCacheRoot is null ? null : new PlaybackCache.MusicAudioFileCache(playbackCacheRoot, cacheStore);
        PersistentChorus = new(cacheStore);
        Files = new(dataStore, [".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma"]);
        Dispatcher = OwnerDispatcher.Current;
        this.presentation = presentation;
        this.shell = shell;
    }

    internal string Id => "music";
    internal MusicNativeApplication Application { get; }
    internal NativeAudioPlayback PlaybackDevice { get; }
    internal GrantedMediaFiles Files { get; }
    internal IStorageDatabase DataDatabase { get; }
    internal IStorageDatabase CacheDatabase { get; }
    internal PlaybackCache.MusicAudioFileCache? AudioFileCache { get; }
    internal PlaybackCache.MusicChorusCache PersistentChorus { get; }
    internal PlaybackCache.MusicPresentationCache PresentationCache { get; }
    internal CancellationTokenSource Lifetime { get; } = new();
    internal ArtworkService ArtworkLoader { get; }
    internal OwnerDispatcher Dispatcher { get; }
    internal ObservableCollection<NavigationItem> Navigation { get; } = [];
    private readonly object eventWritesGate = new();
    private readonly HashSet<Task> eventWrites = [];
    internal HashSet<Task> LoadDrains { get; } = [];
    internal void AddEventWrite(Task work) { lock (eventWritesGate) eventWrites.Add(work); }
    internal void RemoveEventWrite(Task work) { lock (eventWritesGate) eventWrites.Remove(work); }
    internal Task[] SnapshotEventWrites() { lock (eventWritesGate) return eventWrites.ToArray(); }
    internal Dictionary<string, MusicLocation> Locations { get; } = [];
    internal MusicLocation CurrentLocation { get; set; } = new("catalog-home");
    internal string CurrentRoute => CurrentLocation.Route;
    internal CancellationTokenSource? LoadCancellation { get; set; }
    internal long LoadGeneration { get; set; }
    internal long NavigationSubmission { get; set; }
    internal long SearchSubmission { get; set; }
    internal bool Publishing { get; set; }
    internal Task CommandTail => commandTail;
    internal Task PlaybackCommandTail => playbackCommandTail;
    internal long PlaybackActionGeneration => playbackActionGeneration;
    internal bool Stopping { get; set; }
    internal bool Disposed { get; set; }
    internal int Commands => commands;
    internal bool Busy => commands > 0 || LoadCancellation is not null;
    internal bool CanEdit => !Stopping && !Busy;
    internal string RawStatus => status;
    internal string Status => GetCatalogAccountNotice() is { Length: > 0 } notice ? notice : status;
    internal long StatusEpoch => statusEpoch;

    internal event Action? AvailabilityRefreshRequested;
    internal event Action? StatusChanged;

    internal ObservableCollection<SongRow> ActiveSongs => shell.ActiveSongs;
    internal XamlRoot? XamlRoot => presentation.XamlRoot;
    internal Brush PlayerBackground => presentation.PlayerBackground;
    internal Brush PlayerProgress => presentation.PlayerProgress;
    internal DesktopNavigationGroupAction CreatePlaylistAction => presentation.CreatePlaylistAction;
    internal FrameworkElement ModalOwner(MusicModalAnchor anchor = MusicModalAnchor.Active) => presentation.ResolveModalAnchor(anchor);
    internal double CaptureScroll() => presentation.CaptureScroll();
    internal void RestoreScroll(double value) => presentation.RestoreScroll(value);
    internal void ResetSearchPresentation() => presentation.ResetSearchPresentation();
    internal bool IsSettingsActive() => presentation.IsSettingsActive;
    internal void RefreshAppearance(Brush sampled, bool animate = true) => presentation.RefreshAppearance(sampled, animate);
    internal void ReapplyAppearance() => presentation.ReapplyAppearance();
    internal MusicDesktopLyricsWindow CreateDesktopLyricsWindow(Window owner, MusicDesktopLyricsGeometry? geometry) =>
        presentation.CreateDesktopLyricsWindow(owner, geometry);

    internal Task<bool> ConfirmAsync(string message, string title, CancellationToken token = default) =>
        MusicDialogs.ConfirmAsync(ModalOwner(MusicModalAnchor.Active), message, title, token);

    internal Task RequestNavigationAsync(string destination) => shell.RequestNavigationAsync(destination);
    internal Task OpenPlayerDetailAsync() => shell.OpenPlayerDetailAsync();
    internal Task OpenPlayQueueAsync() => shell.OpenPlayQueueAsync();
    internal Task ExecuteMediaShortcutAsync(MediaShortcutAction action) => shell.ExecuteMediaShortcutAsync(action);
    internal Task SearchAsync(string query, CancellationToken token) => shell.SearchAsync(query, token);
    internal Task LoadAsync(MusicLocation target, CancellationToken token, int retry = 0, bool publishStatus = true) =>
        shell.LoadAsync(target, token, retry, publishStatus);
    internal Task LoadUiAsync(MusicLocation target) => shell.LoadUiAsync(target);
    internal Task ClearAllMusicCachesAsync(CancellationToken cancellationToken) => shell.ClearAllMusicCachesAsync(cancellationToken);
    internal (string Route, string SearchView, ObservableCollection<MusicCatalogSection> Sections) CaptureContentSelection() =>
        shell.CaptureContentSelection();
    internal void SetLocation(MusicLocation next, string? heading = null, bool clearLibrary = false,
        bool clearCatalogSongs = false, bool clearSelection = false, bool clearTotal = false, bool clearCollection = false) =>
        shell.SetLocation(next, heading, clearLibrary, clearCatalogSongs, clearSelection, clearTotal, clearCollection);
    internal bool PublishContentSelection((string Route, string SearchView, ObservableCollection<MusicCatalogSection> Sections) previous,
        bool refreshSearch = true) => shell.PublishContentSelection(previous, refreshSearch);
    internal MusicLocation CaptureLocation() => shell.CaptureLocation();
    internal void RememberLocation() => shell.RememberLocation();
    internal long AdvanceLoadGeneration() => shell.AdvanceLoadGeneration();
    internal string GetCatalogAccountNotice() => shell?.CatalogAccountNotice ?? "";
    internal void ClearCatalogAccountNotice() => shell?.ClearCatalogAccountNotice();
    internal long CatalogActionGeneration => shell?.Catalog?.ActionGeneration ?? 0;
    internal void InvalidateCatalogAction() => shell?.InvalidateCatalogAction();
    internal void SetPreparingCard(MusicCatalogCard? card) => shell?.Catalog?.SetPreparingCard(card);
    internal void ClearPreparingCardIf(Func<MusicCatalogCard, bool> predicate) => shell?.Catalog?.ClearPreparingCardIf(predicate);
    internal void ClearPreparingCardIfEquals(MusicCatalogCard? card, long generation) => shell?.Catalog?.ClearPreparingCardIfEquals(card, generation);
    internal void PublishCatalogActionError(Exception error) => shell?.Catalog?.PublishCatalogActionError(error);
    internal void CommitCatalogPlayback() => shell?.Catalog?.CommitCatalogPlayback();
    internal void BeginHomeAccountRecovery(bool current) => shell?.Catalog?.BeginHomeAccountRecovery(current);
    internal void ClearCatalogSectionsAndTags() => shell?.Catalog?.ClearSectionsAndTags();
    internal Task OpenCatalogTargetAsync(CatalogTarget target, string? id) => shell?.Catalog?.OpenCatalogTargetAsync(target, id) ?? Task.CompletedTask;
    internal void RetireCatalogPreviewSurface() => shell?.Catalog?.RetireCatalogPreviewSurface();
    internal void BeginSearchCatalogState(string keyword, string kindLabel) => shell?.Catalog?.BeginSearchCatalogState(keyword, kindLabel);
    internal void PublishSearchCatalogMetadata(string keyword, string kindLabel) => shell?.Catalog?.PublishSearchCatalogMetadata(keyword, kindLabel);
    internal void ClearCatalogTags() => shell?.Catalog?.ClearCatalogTags();
    internal void RetireCatalogSession() => shell?.Catalog?.RetireCatalogSession();
    internal Task<bool> TryAdvanceRecommendationAsync(bool naturalEnd, CancellationToken token) =>
        shell?.Queue is { } queue ? queue.TryAdvanceRecommendationForStepAsync(naturalEnd, token) : Task.FromResult(false);
    internal void ObserveExplicitRecommendationPause() => shell?.Queue?.ObserveExplicitRecommendationPause();
    internal void ClearSearchRecommendationStart() => shell?.Queue?.ClearSearchRecommendationStart();
    internal void OnPlaybackStartedRecorded()
    {
        if (shell?.Queue is { } queue)
        {
            queue.ObserveRecommendationPlayback();
            queue.StartPendingSearchRecommendationPreview();
        }
    }
    internal void RestoreLyricsIfNeeded()
    {
        if (!Stopping && !Disposed && shell?.Lyrics is { NeedsLyrics: true } lyr)
            _ = lyr.RestoreLyricsAsync();
    }
    internal void RefreshLyricsPosition() => shell?.Lyrics?.RefreshLyricPosition();
    internal void RefreshLyricsPreviewAvailability() => shell?.Lyrics?.RefreshLyricPreviewAvailability();
    internal void UpdateLyricSources() => shell?.Lyrics?.UpdateLyricSources();
    internal void ClearLyricsForPlaybackStop() => shell?.Lyrics?.ClearForPlaybackStop();
    internal void NotifyQueueModeAvailability() => shell?.Queue?.NotifyPlaybackModeAvailability();
    internal Task RefreshLibraryAfterMutationAsync(long origin, bool reloadPage = true) =>
        shell?.Library is not { } lib ? Task.CompletedTask :
        reloadPage ? lib.RefreshAfterEdit(origin) : lib.RefreshPlaylists(Lifetime.Token);
    internal Task RefreshActiveSurfaceAsync() =>
        shell is null ? Task.CompletedTask :
        shell.Discovery.IsDiscovery ? shell.Discovery.RefreshDiscoveryAsync() :
        shell.Search.IsSearchPage ? shell.Search.BeginSearchAsync(shell.Search.SearchKeyword, Lifetime.Token, refresh: true) :
        shell.Catalog.RefreshCatalogAsync();
    internal Task ActivatePlatformSettingsAsync(CancellationToken token) => shell.Accounts.ActivatePlatformSettingsAsync(token);
    internal Task RetireAccountTransferAsync() => shell.Accounts.RetireAccountTransferAsync();
    internal Task DeactivatePlatformSyncAsync() => shell.Accounts.LibrarySync.DeactivateAsync();
    internal Task DeactivateAccountDetailsAsync() => shell.Accounts.DeactivateAccountDetailsAsync();
    internal Task RevokeMusicSourceAsync(string installation) => shell.Playback.RevokeChangedMusicSource(installation);
    internal void AdmitSourceExclusion(string songId, string provider, string platform) => shell.Playback.AdmitSourceExclusion(songId, provider, platform);
    internal void RefreshPlaybackSourceBindings() => shell.Playback.PlaybackSourceBindings();
    internal void CancelOnlinePlayback() => shell.CancelOnlinePlayback();
    internal void RefreshPlaybackPresentationAvailability() => shell.RefreshPlaybackPresentationAvailability();

    internal static void ReconcileSongRows(ObservableCollection<SongRow> target, IReadOnlyList<SongRow> next)
    {
        var available = target.GroupBy(row => row.SelectionKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<SongRow>(group), StringComparer.Ordinal);
        var rows = next.Select(song =>
        {
            if (!available.TryGetValue(song.SelectionKey, out var occurrences) || occurrences.Count == 0) return song;
            var retained = occurrences.Dequeue();
            if (!ReferenceEquals(retained, song)) retained.ApplyRepository(song.Record, song.RepositoryPublication);
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

    internal long NextStatusEpoch() => ++statusEpoch;
    internal void SetStatus(string message) { status = message; NotifyStatusChanged(); }
    internal void SetStatusQuiet(string message) => status = message;
    internal void ClearStatusQuiet() => status = "";
    internal void ClearTransientStatus() { status = ""; ClearCatalogAccountNotice(); NotifyStatusChanged(); }
    internal Task Notify(string message) { SetStatus(message); return Task.CompletedTask; }
    internal void NotifyStatusChanged() => StatusChanged?.Invoke();
    internal void RefreshBindings() => AvailabilityRefreshRequested?.Invoke();

    internal void RetireOperationStatus()
    {
        ++statusEpoch;
        status = "";
        ClearCatalogAccountNotice();
        NotifyStatusChanged();
    }

    internal Task RunAsync(Func<Task> action, bool publishStatus = true)
    {
        if (Stopping) return Task.CompletedTask;
        var previous = commandTail;
        var stamp = publishStatus ? ++statusEpoch : statusEpoch;
        commands++;
        if (publishStatus) { status = ""; ClearCatalogAccountNotice(); }
        RefreshBindings();
        return commandTail = Execute();
        async Task Execute()
        {
            await Task.Yield();
            await previous;
            try { if (!Disposed) await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (publishStatus && !Disposed && stamp == statusEpoch) status = ErrorText(error);
            }
            finally { commands--; if (!Disposed) RefreshBindings(); }
        }
    }

    internal Task RunPlaybackActionAsync(Func<long, Task> action, bool publishStatus = true, bool supersede = true)
    {
        if (Stopping) return Task.CompletedTask;
        var previous = playbackCommandTail;
        var generation = supersede ? ++playbackActionGeneration : playbackActionGeneration;
        if (supersede) CancelOnlinePlayback();
        var stamp = publishStatus ? ++statusEpoch : statusEpoch;
        if (publishStatus) ClearTransientStatus();
        return playbackCommandTail = Execute();
        async Task Execute()
        {
            await Task.Yield();
            await previous;
            if (Disposed || Stopping || supersede && generation != playbackActionGeneration) return;
            try { await action(generation); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (publishStatus && !Disposed && stamp == statusEpoch) SetStatus(ErrorText(error));
            }
            finally { if (!Disposed) RefreshPlaybackPresentationAvailability(); }
        }
    }

    internal static string StorageCode(StorageException error) => error.Error switch
    {
        StorageError.InvalidRequest => "INVALID_REQUEST",
        StorageError.Unavailable => "STORAGE_UNAVAILABLE",
        StorageError.Conflict => "STORAGE_CONFLICT",
        StorageError.Corrupt or StorageError.UnsupportedVersion => "CORRUPT_STORAGE",
        _ => "STORAGE"
    };

    internal static string? ErrorCode(Exception error) => error switch
    {
        MusicOperationException e => e.Code,
        MusicLibraryException e => e.Code,
        MusicAccountException e => e.Code,
        MusicPlatformException e => e.Code,
        RecommendationException e => e.Code,
        MusicFeedbackException e => e.Code,
        BridgeException e => e.Code,
        StorageException e => StorageCode(e),
        OperationCanceledException => "CANCELLED",
        _ => null
    };

    internal static string ErrorText(Exception error) => error switch
    {
        PlaybackException { Code: PlaybackError.InvalidRate } or MusicOperationException { Code: PlaybackError.InvalidRate } => "播放倍速须为 0.5–3，间隔 0.1",
        PlaybackException { Code: PlaybackError.AudioDeviceUnavailable } or MusicOperationException { Code: PlaybackError.AudioDeviceUnavailable } => "当前没有可用的音频输出设备",
        PlaybackException { Code: PlaybackError.Superseded } or MusicOperationException { Code: PlaybackError.Superseded } => "播放内容已变更",
        InvalidDataException => "文件不可用",
        MusicLibraryException native => ErrorText(new MusicOperationException(native.Code)),
        MusicAccountException native => ErrorText(new MusicOperationException(native.Code, native.RetryAfterSeconds)),
        MusicPlatformException native => ErrorText(new MusicOperationException(native.Code)),
        RecommendationException native => ErrorText(new MusicOperationException(native.Code)),
        MusicFeedbackException native => ErrorText(new MusicOperationException(native.Code)),
        StorageException native => ErrorText(new MusicOperationException(StorageCode(native))),
        MusicOperationException { Code: "CORRUPT_QUEUE" } => "播放队列数据无效或格式不兼容",
        MusicOperationException { Code: "CORRUPT_HISTORY" } => "播放历史数据无效或格式不兼容",
        MusicOperationException { Code: "CORRUPT_LIBRARY" } => "曲库数据无效或格式不兼容",
        MusicOperationException { Code: "CORRUPT_STORAGE" or "CORRUPT_SYNC" } => "本地数据无效或格式不兼容",
        MusicOperationException { Code: "REVISION_CONFLICT" or "STORAGE_CONFLICT" } => "数据已变更，请刷新",
        MusicOperationException { Code: "LIMIT" } => "操作超过数量限制",
        MusicOperationException { Code: "INVALID_SOURCE_URL" } => "请输入有效的 .js 音源地址",
        MusicOperationException { Code: "SOURCE_DOWNLOAD_FAILED" } => "音源下载失败",
        MusicOperationException { Code: "OFFICIAL_SOURCE_DISABLED" } => "该官方音源已停用",
        MusicOperationException { Code: "OFFICIAL_SOURCES_CHANGED" } => "官方音源设置已变更，请重试",
        MusicOperationException { Code: "SOURCES_CHANGED" } => "音源设置已变更，请重试",
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
}

/// <summary>
/// Pure infrastructure base for music domain controllers bound to <see cref="MusicRuntimeContext"/>.
/// Exposes zero sibling controllers, zero views, and zero reference to <see cref="MusicCapability"/>.
/// </summary>
public abstract class MusicController : MusicStateObject
{
    private protected readonly MusicRuntimeContext ctx;

    internal MusicController(MusicRuntimeContext ctx) => this.ctx = ctx;

    private protected string Id => ctx.Id;
    private protected MusicNativeApplication application => ctx.Application;
    private protected NativeAudioPlayback playback => ctx.PlaybackDevice;
    private protected GrantedMediaFiles files => ctx.Files;
    private protected IStorageDatabase dataDatabase => ctx.DataDatabase;
    private protected IStorageDatabase cacheDatabase => ctx.CacheDatabase;
    private protected PlaybackCache.MusicAudioFileCache? audioFileCache => ctx.AudioFileCache;
    private protected PlaybackCache.MusicChorusCache persistentChorus => ctx.PersistentChorus;
    private protected PlaybackCache.MusicPresentationCache presentationCache => ctx.PresentationCache;
    private protected CancellationTokenSource lifetime => ctx.Lifetime;
    private protected HashSet<Task> loadDrains => ctx.LoadDrains;
    private protected void AddEventWrite(Task work) => ctx.AddEventWrite(work);
    private protected void RemoveEventWrite(Task work) => ctx.RemoveEventWrite(work);
    private protected bool stopping => ctx.Stopping;
    private protected bool disposed => ctx.Disposed;
    private protected int commands => ctx.Commands;
    private protected bool Busy => ctx.Busy;
    public bool CanEdit => ctx.CanEdit;
    internal ArtworkService ArtworkLoader => ctx.ArtworkLoader;

    private protected Task RunAsync(Func<Task> action, bool publishStatus = true) =>
        ctx.RunAsync(action, publishStatus);
    private protected Task RunPlaybackActionAsync(Func<long, Task> action, bool publishStatus = true, bool supersede = true) =>
        ctx.RunPlaybackActionAsync(action, publishStatus, supersede);
    private protected Task<bool> ConfirmAsync(string message, string title, CancellationToken token = default) =>
        ctx.ConfirmAsync(message, title, token);
    internal Task Notify(string message) => ctx.Notify(message);
    private protected Task RequestNavigationAsync(string destination) => ctx.RequestNavigationAsync(destination);
    private protected static string ErrorText(Exception error) => MusicRuntimeContext.ErrorText(error);
    private protected static string? ErrorCode(Exception error) => MusicRuntimeContext.ErrorCode(error);

    private protected void Changed([CallerMemberName] string? propertyName = null) => RaisePropertyChanged(propertyName);
    internal virtual void RefreshAvailability() => RaisePropertyChanged(nameof(CanEdit));
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>
/// Slim composition root for the music capability.
/// Constructs <see cref="MusicRuntimeContext"/> and the 9 domain controllers in strict
/// topological DAG order and wires all cross-domain events and callbacks in one place.
/// Views bind only to controllers and never reference <see cref="MusicCapability"/>.
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
    private readonly MusicRuntimeContext ctx;
    private readonly MusicDebugAdapter debugAdapter;
    private readonly IDisposable? debugCacheRegistration;
    private readonly object accountCacheCleanupGate = new();
    private readonly List<Task> accountCacheCleanups = [];
    private Task? preparation, abortion;

    public MusicCapability(IRuntimeClient runtime, IStorageDatabase dataStore, IStorageDatabase cacheStore,
        MusicLibraryDatabase libraryStore, NativeAudioPlayback playback, string? playbackCacheRoot = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(dataStore);
        ArgumentNullException.ThrowIfNull(cacheStore);
        ArgumentNullException.ThrowIfNull(libraryStore);
        ArgumentNullException.ThrowIfNull(playback);

        var application = new MusicNativeApplication(dataStore, cacheStore, libraryStore, runtime);
        ctx = new MusicRuntimeContext(application, dataStore, cacheStore, playback, playbackCacheRoot);
        Presentation = new(this);

        // Strict topological DAG construction (Layer 1 -> Layer 7).
        Settings = new(ctx);
        Accounts = new(ctx, Settings);
        Lyrics = new(ctx, Settings);
        Playback = new(ctx, Settings, Lyrics);
        Queue = new(ctx, Playback, Accounts);
        Discovery = new(ctx, Settings, Playback);
        Catalog = new(ctx, Settings, Accounts, Playback, Queue, Discovery);
        Search = new(ctx, Settings, Accounts, Catalog);
        Library = new(ctx, Catalog, Search, Discovery, Playback, Queue);
        debugAdapter = new(this);

        WireControllerGraph();

        Accounts.InitializeAfterSettings();
        ctx.Application.SongMetadataChanged += OnSongMetadataChanged;
        ctx.Application.AccountDetailsInvalidated += Catalog.CatalogAccountChanged;
        ctx.Application.AccountSessionChanged += OnAccountSessionChanged;
        Playback.SubscribePlayback();
        Library.RebuildNavigation();
        if (DebugLog.IsEnabled)
        {
            debugCacheRegistration = DebugCacheRegistry.Register(
                "music",
                GetCacheStatsAsync,
                clearMemory: () =>
                {
                    ctx.ArtworkLoader.ClearMemory();
                    ctx.Application.ClearMemoryCaches();
                });
        }
    }

    private void OnAccountSessionChanged(string platform)
    {
        if (ctx.Stopping || ctx.Disposed) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (accountCacheCleanupGate)
        {
            if (ctx.Stopping || ctx.Disposed) return;
            accountCacheCleanups.Add(done.Task);
        }
        _ = RunAsync();
        async Task RunAsync()
        {
            try { await ClearAccountBoundCachesAsync(platform).ConfigureAwait(false); }
            finally
            {
                lock (accountCacheCleanupGate) accountCacheCleanups.Remove(done.Task);
                done.TrySetResult();
            }
        }
    }

    private async Task ClearAccountBoundCachesAsync(string platform)
    {
        try
        {
            if (ctx.AudioFileCache is not null)
                await ctx.AudioFileCache.ClearOfficialAsync(ctx.Lifetime.Token).ConfigureAwait(false);
        }
        catch { /* Best-effort cleanup on account change. */ }
        try
        {
            await ctx.PersistentChorus.ClearAsync(platform, ctx.Lifetime.Token).ConfigureAwait(false);
        }
        catch { /* Best-effort cleanup on account change. */ }
        try
        {
            await Catalog.ClearHomeSnapshotAsync(ctx.Lifetime.Token).ConfigureAwait(false);
        }
        catch { /* Best-effort cleanup on account change. */ }
    }

    internal async Task ClearAllMusicCachesAsync(CancellationToken cancellationToken = default)
    {
        Playback.CancelMusicPreload();
        ctx.ArtworkLoader.ClearMemory();
        await ctx.PresentationCache.ClearAllAsync(cancellationToken).ConfigureAwait(false);
        if (ctx.AudioFileCache is not null)
            await ctx.AudioFileCache.ClearAsync(cancellationToken).ConfigureAwait(false);
        await ctx.PersistentChorus.ClearAsync(null, cancellationToken).ConfigureAwait(false);
        await Catalog.ClearHomeSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await ctx.Application.ClearCachesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<object?> GetCacheStatsAsync(CancellationToken cancellationToken = default)
    {
        var presentation = await ctx.PresentationCache.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var audioFiles = ctx.AudioFileCache is null
            ? null
            : await ctx.AudioFileCache.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var chorus = await ctx.PersistentChorus.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var homeSnapshot = await Catalog.GetHomeSnapshotStatsAsync(cancellationToken).ConfigureAwait(false);
        var application = await ctx.Application.GetCacheStatsAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            artwork = ctx.ArtworkLoader.GetStats(),
            presentation,
            audioFiles,
            chorus = new { entryCount = chorus.EntryCount, maximumEntries = chorus.MaximumEntries },
            homeSnapshot = new
            {
                hasSnapshot = homeSnapshot.HasSnapshot,
                pageCount = homeSnapshot.PageCount,
                sectionCount = homeSnapshot.SectionCount,
                cardCount = homeSnapshot.CardCount
            },
            application
        };
    }

    private void OnSongMetadataChanged(IReadOnlySet<string> ids)
    {
        if (ctx.Dispatcher.IsClosing) return;
        _ = ctx.Dispatcher.EnqueueAsync(() =>
        {
            if (ctx.Stopping || ctx.Disposed) return;
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.LoadDrains.Add(drained.Task);
            _ = Refresh();
            async Task Refresh()
            {
                try
                {
                    var snapshot = await ctx.Application.SongSnapshotsAsync(ids.ToArray(), ctx.Lifetime.Token);
                    if (ctx.Stopping || ctx.Disposed) return;
                    System.Text.Json.JsonElement? Read(string id) => snapshot.Songs.GetValueOrDefault(id);
                    Playback.RefreshSongsFromRepository(ids, Read, snapshot.Publication);
                    Library.RefreshSongsFromRepository(ids, Read, snapshot.Publication);
                    Search.RefreshSongsFromRepository(ids, Read, snapshot.Publication);
                    Catalog.RefreshSongsFromRepository(ids, Read, snapshot.Publication, Search.CachedSearchCards());
                    Queue.RefreshSongsFromRepository(ids, Read, snapshot.Publication);
                    Discovery.RefreshSongsFromRepository(ids, Read, snapshot.Publication);
                }
                catch (OperationCanceledException) { }
                catch (MusicOperationException error) when (ctx.Stopping && error.Code == "CLOSED") { }
                catch (Exception error) { if (!ctx.Stopping) await ctx.Notify(MusicRuntimeContext.ErrorText(error)); }
                finally { ctx.LoadDrains.Remove(drained.Task); drained.TrySetResult(); }
            }
        });
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

    internal MusicPresentation Presentation { get; }
    internal ArtworkService ArtworkLoader => ctx.ArtworkLoader;
    internal string RawStatus => ctx.RawStatus;
    internal CancellationTokenSource? LoadCancellation => ctx.LoadCancellation;
    internal NativeAudioPlayback PlaybackDevice => ctx.PlaybackDevice;
    internal MusicDebugAdapter DebugAdapterInstance => debugAdapter;
    public IDebugModuleAdapter DebugAdapter => debugAdapter;

    internal bool CanEdit => ctx.CanEdit;
    internal string CurrentRoute => ctx.CurrentRoute;
    internal string Status => ctx.Status;
    internal ObservableCollection<SongRow> ActiveSongs => Search.IsSearchPage ? Search.SearchSongsSource : Library.CatalogSongsSource;

    public string Id => ctx.Id;
    public string Title => "音乐";
    public string Version => "0.4.0";
    public ObservableCollection<NavigationItem> Navigation => ctx.Navigation;
    public FrameworkElement View => Presentation.Page;
    public FrameworkElement SettingsView => Presentation.Settings;
    public FrameworkElement AppearanceSettingsView => Presentation.Appearance;
    public FrameworkElement HomeSourceSettingsView => Presentation.HomeSourceSettings;
    public FrameworkElement CacheSettingsView => Presentation.CacheSettings;
    public FrameworkElement PlayerView => Presentation.Player;

    public IReadOnlyList<DesktopOwnedNotice> OwnedNotices => ctx.Receipts.OwnedNotices;
    public event EventHandler? OwnedNoticesChanged
    {
        add => ctx.Receipts.OwnedNoticesChanged += value;
        remove => ctx.Receipts.OwnedNoticesChanged -= value;
    }

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged { add { } remove { } }
    public event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested;
    public event EventHandler<DesktopGlobalSurfaceRequestEventArgs>? GlobalSurfaceRequested;

    private void WireControllerGraph()
    {
        // 1. Runtime kernel -> presentation & shell hooks
        ctx.ResolveModalAnchor = Presentation.ResolveModalAnchor;
        ctx.GetXamlRoot = () => View.XamlRoot;
        ctx.CaptureScroll = () => Presentation.CaptureScroll();
        ctx.RestoreScroll = value => Presentation.RestoreScroll(value);
        ctx.ResetSearchPresentation = () => Presentation.ResetSearchPresentation();
        ctx.IsSettingsActive = () => Presentation.IsSettingsActive;
        ctx.GetPlayerBackground = () => Presentation.PlayerBackground;
        ctx.GetPlayerProgress = () => Presentation.PlayerProgress;
        ctx.RefreshAppearance = (sampled, animate) => Presentation.RefreshAppearance(sampled, animate);
        ctx.ReapplyAppearance = () => Presentation.ReapplyAppearance();
        ctx.GetCreatePlaylistAction = () => Presentation.CreatePlaylistAction;

        ctx.NavigateRequested = RequestNavigationAsync;
        ctx.OpenPlayerDetailAsync = OpenPlayerDetailAsync;
        ctx.OpenPlayQueueAsync = OpenPlayQueueAsync;
        ctx.ExecuteMediaShortcutAsync = ExecuteMediaShortcutAsync;
        ctx.SearchRequestedAsync = SearchAsync;
        ctx.LoadLocationAsync = LoadAsync;
        ctx.LoadLocationUiAsync = LoadUiAsync;
        ctx.CaptureContentSelection = CaptureContentSelection;
        ctx.SetLocationHandler = SetLocation;
        ctx.PublishContentSelection = PublishContentSelection;
        ctx.CaptureLocation = CaptureLocation;
        ctx.RememberLocation = RememberLocation;
        ctx.AdvanceLoadGeneration = AdvanceLoadGeneration;
        ctx.GetActiveSongs = () => ActiveSongs;
        ctx.GetCatalogAccountNotice = () => Catalog.CatalogAccountNotice;
        ctx.ClearCatalogAccountNotice = () => Catalog.ClearCatalogAccountNotice();
        ctx.CancelOnlinePlayback = () => Playback.CancelOnline();
        ctx.RefreshPlaybackPresentationAvailability = () => Playback.RefreshPresentationAvailability();

        ctx.StatusChanged += () =>
        {
            Settings.NotifyStatusChanged();
            Catalog.NotifyStatusChanged();
            Library.NotifyStatusChanged();
        };
        ctx.MusicSourcesChanged += () =>
        {
            Playback.IncrementSelectionSourcesRevision();
            Lyrics.NotifySourcesChanged();
        };
        ctx.AvailabilityRefreshRequested += () =>
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
        };

        // 2. Settings (Layer 1) -> downstream hooks
        Settings.OnRefreshAccountPlatformSetting = platform => Accounts.RefreshPlatformSetting(platform);
        Settings.OnActivateAccountPlatformSettingsAsync = token => Accounts.ActivatePlatformSettingsAsync(token);
        Settings.OnDeactivateAccountDetailsAsync = () => Accounts.DeactivateAccountDetailsAsync();
        Settings.OnDeactivateLibrarySyncAsync = () => Accounts.LibrarySync.DeactivateAsync();
        Settings.OnRetireAccountTransferAsync = () => Accounts.RetireAccountTransferAsync();
        Settings.GetCatalogSearchKind = () => Catalog.CatalogSearchKind;
        Settings.OnRetireCatalogSourcePages = () => Catalog.RetireCatalogSourcePages();
        Settings.OnDesktopLyricBindings = () => Lyrics.DesktopLyricBindings();
        Settings.GetNeedsLyrics = () => Lyrics.NeedsLyrics;
        Settings.OnPublishOnlineLyricsSettings = settings => Lyrics.PublishOnlineSettings(settings);
        Settings.OnRestoreLyricsAsync = () => Lyrics.RestoreLyricsAsync();
        Settings.OnRetireLyrics = () => Lyrics.RetireLyrics();
        Settings.OnAdmitSourceExclusion = (songId, provider, platform) => Playback.AdmitSourceExclusion(songId, provider, platform);
        Settings.OnCancelMusicPreload = () => Playback.CancelMusicPreload();
        Settings.GetCurrentSong = () => Playback.CurrentSong;
        Settings.OnNotifyVolumeChanged = () => Playback.NotifyVolumeChanged();
        Settings.OnSourceExclusionAdded = (songId, provider, platform) => Playback.OnSourceExclusionAdded(songId, provider, platform);
        Settings.OnSourceExclusionRemovedAsync = (songId, token) => Playback.OnSourceExclusionRemovedAsync(songId, token);
        Settings.OnPlaybackSourceBindings = () => Playback.PlaybackSourceBindings();
        Settings.GetPreparedAudioReceiptSignature = () => Playback.PreparedAudioReceiptSignature;
        Settings.OnQualityPolicyChanged = sig => Playback.QualityPolicyChanged(sig);
        Settings.OnRevokeChangedMusicSourceAsync = installation => Playback.RevokeChangedMusicSource(installation);
        Settings.OnClearSearchVisibleSnapshot = () => Search.ClearSearchVisibleSnapshot();
        Settings.GetIsSearchPage = () => Search.IsSearchPage;
        Settings.OnRetireSearchAsync = () => Search.RetireSearchAsync();
        Settings.OnClearAllMusicCachesAsync = ClearAllMusicCachesAsync;

        // 3. Accounts (Layer 2) -> downstream hooks
        Accounts.OnBeginHomeAccountRecovery = forceRead => Catalog.BeginHomeAccountRecovery(forceRead);
        Accounts.GetHomePlatform = () => Catalog.HomePlatform;
        Accounts.GetIsCatalogPage = () => Catalog.IsCatalogPage;
        Accounts.OnRefreshHomeDisplay = () => Catalog.RefreshHomeDisplay();
        Accounts.OnRetireCatalogSession = () => Catalog.RetireCatalogSession();
        Accounts.OnRefreshLibraryPlaylistsAsync = token => Library.RefreshPlaylists(token);
        Accounts.GetIsLyrics = () => Lyrics.IsLyrics;
        Accounts.GetNeedsLyrics = () => Lyrics.NeedsLyrics;
        Accounts.OnRestoreLyricsAsync = () => Lyrics.RestoreLyricsAsync();
        Accounts.OnRetireLyrics = () => Lyrics.RetireLyrics();
        Accounts.OnRecommendationBindings = () => Queue.RecommendationBindings();

        // 4. Lyrics (Layer 2) -> playback read-only bridge & desktop window factory
        Lyrics.GetCanSeek = () => Playback.CanSeek;
        Lyrics.GetCurrentSong = () => Playback.CurrentSong;
        Lyrics.GetOwnsCurrent = () => Playback.OwnsCurrent;
        Lyrics.GetPlaybackEpoch = () => Playback.PlaybackEpoch;
        Lyrics.GetPlaybackSong = row => Playback.PlaybackSong(row);
        Lyrics.GetPositionSeconds = () => Playback.PositionSeconds;
        Lyrics.GetQueueSaved = () => Playback.QueueSaved;
        Lyrics.OnResumePlaybackAsync = () => Playback.ResumePlaybackAsync();
        Lyrics.CreateDesktopLyricsWindow = (owner, geometry) => new MusicDesktopLyricsWindow(Lyrics, Playback, Settings, owner, geometry);

        // 5. Playback (Layer 3) -> downstream hooks
        Playback.OnNotifyPlaybackModeAvailability = () => Queue.NotifyPlaybackModeAvailability();
        Playback.OnAdvanceRecommendationAsync = naturalEnd => Queue.AdvanceRecommendation(naturalEnd);
        Playback.OnClearSearchRecommendationStart = () => Queue.ClearSearchRecommendationStart();
        Playback.OnInvalidateQueueSurface = () => Queue.InvalidateQueueSurface();
        Playback.OnNotifyQueueModeChanged = () => Queue.NotifyModeChanged();
        Playback.OnNotifyRecommendationPlaybackChanged = () => Queue.NotifyRecommendationPlaybackChanged();
        Playback.OnObserveExplicitRecommendationPause = () => Queue.ObserveExplicitRecommendationPause();
        Playback.OnObserveRecommendationPlayback = () => Queue.ObserveRecommendationPlayback();
        Playback.OnRefreshQueueSurfaceCurrent = () => Queue.RefreshQueueSurfaceCurrent();
        Playback.OnRefreshRecommendationQueueAsync = token => Queue.RefreshRecommendationQueue(token);
        Playback.OnStartPendingSearchRecommendationPreview = () => Queue.StartPendingSearchRecommendationPreview();
        Playback.GetCatalogLoadedPlatform = () => Catalog.CatalogLoadedPlatform;
        Playback.GetCatalogLoadedTarget = () => Catalog.CatalogLoadedTarget;
        Playback.GetCatalogQuery = () => Catalog.CatalogQuery;
        Playback.OnCommitCatalogPlayback = () => Catalog.CommitCatalogPlayback();
        Playback.OnInvalidateCatalogAction = () => Catalog.InvalidateCatalogAction();
        Playback.GetIsCatalogPage = () => Catalog.IsCatalogPage;
        Playback.GetPlaylists = () => Library.Playlists;
        Playback.OnRefreshLibraryAfterEditAsync = origin => Library.RefreshAfterEdit(origin);
        Playback.GetIsSearchPage = () => Search.IsSearchPage;
        Playback.GetSearchKeyword = () => Search.SearchKeyword;

        // 6. Queue (Layer 4) -> downstream hooks
        Queue.GetCatalogAccountObservation = platform => Catalog.CatalogAccountObservation(platform);
        Queue.GetCatalogActionGeneration = () => Catalog.CatalogActionGeneration;
        Queue.GetCatalogFmScenes = () => Catalog.CatalogFmScenes;
        Queue.OnClearPreparingCatalogCard = () => Catalog.ClearPreparingCatalogCard();
        Queue.OnInvalidateCatalogAction = () => Catalog.InvalidateCatalogAction();
        Queue.GetPreparingCatalogCard = () => Catalog.PreparingCatalogCard;
        Queue.OnPublishCatalogActionError = error => Catalog.PublishCatalogActionError(error);
        Queue.OnSetPreparingCatalogCard = card => Catalog.SetPreparingCatalogCard(card);
        Queue.OnRefreshLibraryPlaylistsAsync = token => Library.RefreshPlaylists(token);

        // 7. Discovery (Layer 4) -> downstream hooks
        Discovery.OnClearCatalogSectionsAndTags = () => { Catalog.CatalogSections.Clear(); Catalog.CatalogTags.Clear(); };
        Discovery.OnInvalidateCatalogAction = () => Catalog.InvalidateCatalogAction();
        Discovery.OnOpenCatalogTargetAsync = (target, tab) => Catalog.OpenCatalogTargetAsync(target, tab);
        Discovery.GetIsSearchPage = () => Search.IsSearchPage;
        Discovery.GetSearchKeyword = () => Search.SearchKeyword;

        // 8. Catalog (Layer 5) -> downstream hooks
        Catalog.GetCatalogSongsSource = () => Library.CatalogSongsSource;
        Catalog.OnClearSelectionForCatalog = heading => Library.ClearSelectionForCatalog(heading);
        Catalog.OnClearSelectionOnly = () => Library.ClearSelectionOnly();
        Catalog.OnAppendSongNextAsync = song => Library.AppendSongNextAsync(song);
        Catalog.OnFavoriteSongAsync = song => Library.FavoriteSongAsync(song);
        Catalog.OnToggleSongFavoriteAsync = song => Library.ToggleSongFavoriteAsync(song);
        Catalog.GetLibraryHeading = () => Library.Heading;
        Catalog.GetNetworkPlaylists = () => Library.NetworkPlaylists;
        Catalog.OnNotifyLibraryDetailBindings = () => Library.NotifyDetailBindings();
        Catalog.OnNotifyLibrarySelectionChanged = () => Library.NotifySelectionChanged();
        Catalog.OnPublishCatalogSummary = (total, clearSelection) => Library.PublishCatalogSummary(total, clearSelection);
        Catalog.OnRefreshLibraryPlaylistsAsync = token => Library.RefreshPlaylists(token);
        Catalog.GetLibraryTotal = () => Library.Total;
        Catalog.OnBeginSearchAsync = (query, token, refresh) => Search.BeginSearchAsync(query, token, refresh: refresh);
        Catalog.OnCancelSearchAsync = () => Search.CancelSearchAsync();
        Catalog.GetHasSearchSession = () => Search.HasSearchSession;
        Catalog.GetIsSearchPage = () => Search.IsSearchPage;
        Catalog.GetIsSearchSongs = () => Search.IsSearchSongs;
        Catalog.OnRememberSearch = () => Search.RememberSearch();
        Catalog.OnRestoreSearchAsync = token => Search.RestoreSearchAsync(token);
        Catalog.OnRetireSearchTracked = () => Search.RetireSearchTracked();
        Catalog.GetSearchAccountEpoch = () => Search.SearchAccountEpoch;
        Catalog.OnSearchBindings = () => Search.SearchBindings();
        Catalog.GetSearchKeyword = () => Search.SearchKeyword;
        Catalog.GetSearchSongsSource = () => Search.SearchSongsSource;
        Catalog.GetSearchView = () => Search.SearchView;
        Catalog.OnSwitchSearchKindAsync = () => Search.SwitchSearchKindAsync();

        // 9. Search (Layer 6) -> Library hooks
        Search.OnClearLibraryTotal = () => Library.ClearTotal();
        Search.OnPublishSearchSummary = (heading, count) => Library.PublishSearchSummary(heading, count);
        Search.GetSelectedSong = () => Library.SelectedSong;
        Search.SetSelectedSong = song => Library.SelectedSong = song;
    }

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
        if (!ctx.Stopping) GlobalSurfaceRequested?.Invoke(this, new(DesktopGlobalSurface.PlayerDetail));
        return Task.CompletedTask;
    }

    public Task OpenPlayQueueAsync()
    {
        if (!ctx.Stopping) GlobalSurfaceRequested?.Invoke(this, new(DesktopGlobalSurface.PlayQueue));
        return Task.CompletedTask;
    }

    internal Task RequestNavigationAsync(string destination)
    {
        if (ctx.Stopping) return Task.CompletedTask;
        if (NavigationRequested is not { } requested) return NavigateAsync(destination, ctx.Lifetime.Token);
        requested(this, new(destination));
        return Task.CompletedTask;
    }

    public Task RunAsync(Func<Task> action) => ctx.RunAsync(action, publishStatus: true);

    public async Task ExecuteMediaShortcutAsync(MediaShortcutAction action)
    {
        if (ctx.Stopping || ctx.Disposed) return;
        try
        {
            switch (action)
            {
                case MediaShortcutAction.TogglePlayback:
                    if (Playback.CanTogglePlayback) await Playback.TogglePlaybackAsync();
                    else await ctx.Notify("当前无法播放或暂停");
                    break;
                case MediaShortcutAction.Previous:
                case MediaShortcutAction.Next:
                    if (Playback.CanStep) await Playback.StepAsync(action == MediaShortcutAction.Next);
                    else await ctx.Notify("当前无法切换歌曲");
                    break;
                case MediaShortcutAction.VolumeUp:
                case MediaShortcutAction.VolumeDown:
                    Playback.Volume += action == MediaShortcutAction.VolumeUp ? 5 : -5;
                    await Settings.SaveVolumeAsync();
                    break;
                case MediaShortcutAction.ToggleFavorite:
                    if (Playback.CanFavoriteCurrent) await Playback.ToggleCurrentFavoriteAsync();
                    else await ctx.Notify("当前无法收藏歌曲");
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
        catch (OperationCanceledException) { if (!ctx.Stopping) await ctx.Notify("操作已取消"); }
        catch (Exception error) { if (!ctx.Stopping) await ctx.Notify(MusicRuntimeContext.ErrorText(error)); }
    }

    public string OperationErrorText(Exception error) => MusicRuntimeContext.ErrorText(error);

    public Task PrepareShutdownAsync()
    {
        if (preparation is not null) return preparation;
        Playback.FinishFeedback("interrupt");
        Lyrics.PrepareDesktopLyricsShutdown();
        ctx.Stopping = true;
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
        await Task.Yield();
        AdvanceLoadGeneration();
        Playback.AdvancePlaybackEpochForShutdown();
        Playback.UnsubscribePlayback();
        Playback.RefreshPresentation();
        ctx.LoadCancellation?.Cancel();
        Settings.CancelCatalogSourceReadForShutdown();
        Catalog.CancelStartupForShutdown();
        Accounts.CancelReadForShutdown();
        Library.CancelCollectionPage();
        Playback.CancelOnline(); Lyrics.RetireLyrics(); Playback.CancelMusicPreload();
        ctx.RefreshBindings();
        Exception? settingsFailure = null;
        try { await Accounts.RetireAccountTransferAsync(); await Settings.DeactivateSettingsAsync(); }
        catch (Exception error) { settingsFailure = error; }
        try { await Settings.DeactivateCatalogSourceSettingsAsync(); }
        catch (Exception error) { settingsFailure ??= error; }
        try { await Catalog.VipPage.PrepareShutdownAsync(); }
        catch (Exception error) { settingsFailure ??= error; }
        await Task.WhenAll(ctx.CommandTail, ctx.PlaybackCommandTail, Accounts.AccountReadTask, Accounts.AccountSettlementTask,
            Task.WhenAll(ctx.EventWrites.ToArray()), Task.WhenAll(ctx.LoadDrains.ToArray()), Settings.RetireSearchHistoryAsync());
        Exception? failure = Accounts.AccountSaveUncertain ? new MusicOperationException("COMMIT_UNKNOWN") : settingsFailure;
        try { await Settings.SaveVolumeCore(ctx.Lifetime.Token); }
        catch (Exception error) { failure ??= error; }
        try { await Queue.DrainRecommendations(); }
        catch (Exception error) { failure ??= error; }
        try { await Discovery.RetireDiscoveryAsync(); }
        catch (Exception error) { failure ??= error; }
        Task[] accountCleanups;
        lock (accountCacheCleanupGate) accountCleanups = accountCacheCleanups.ToArray();
        await Task.WhenAll(accountCleanups);
        try { await ctx.Application.PrepareShutdownAsync(); }
        catch (Exception error) { failure ??= error; }
        await Task.WhenAll(ctx.CacheDatabase.DrainAsync(), ctx.DataDatabase.DrainAsync());
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
        ctx.Stopping = true;
        Lyrics.ReleaseDesktopLyrics();
        try { ctx.Lifetime.Cancel(); } catch (Exception error) { failure = error; }
        try { await prepare; } catch { }
        try { await Accounts.DisposeLibrarySyncAsync(); } catch (Exception error) { failure ??= error; }
        try { await Accounts.DisposeAccountDetailsAsync(); } catch (Exception error) { failure ??= error; }
        try { await Catalog.VipPage.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        Catalog.DisposeResources();
        try { await ctx.Application.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        ctx.Application.SongMetadataChanged -= OnSongMetadataChanged;
        ctx.Application.AccountDetailsInvalidated -= Catalog.CatalogAccountChanged;
        ctx.Application.AccountSessionChanged -= OnAccountSessionChanged;
        debugCacheRegistration?.Dispose();
        if (ctx.AudioFileCache is not null)
            try { await ctx.AudioFileCache.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        try { await ctx.ArtworkLoader.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        Search.Unsubscribe();
        Discovery.Unsubscribe();
        Queue.ReleaseRecommendations();
        ctx.Disposed = true;
        Playback.UnsubscribePlayback();
        ctx.Lifetime.Dispose();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

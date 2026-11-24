using System.Collections.ObjectModel;
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
    MusicStateObject,
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

        // Layer 0: core application, presentation shell, runtime kernel, and shared state atoms.
        var application = new MusicNativeApplication(dataStore, cacheStore, libraryStore, runtime);
        Presentation = new(this);
        ctx = new MusicRuntimeContext(application, dataStore, cacheStore, playback, playbackCacheRoot, Presentation, this);
        var catalogActions = new MusicCatalogActionState();
        var contentState = new MusicContentState(ctx, catalogActions);

        // Strict acyclic topological DAG construction (Layer 1 -> Layer 7).
        Settings = new(ctx);
        Accounts = new(ctx, Settings);
        Playback = new(ctx, Settings, catalogActions, contentState);
        Lyrics = new(ctx, Settings, Playback);
        Queue = new(ctx, Playback, Accounts, catalogActions);
        Search = new(ctx, Settings, Accounts, catalogActions, contentState);
        Catalog = new(ctx, Settings, Accounts, Playback, Queue, Search, catalogActions, contentState);
        Discovery = new(ctx, Settings, Playback, Catalog, Search, catalogActions);
        Library = new(ctx, Catalog, Search, Discovery, Playback, Queue, catalogActions, contentState);
        debugAdapter = new(this);

        SubscribeDomainEvents();

        ctx.Application.SongMetadataChanged += OnSongMetadataChanged;
        ctx.Application.AccountDetailsInvalidated += OnAccountDetailsInvalidated;
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

    private void OnAccountDetailsInvalidated(string platform)
    {
        var observation = Accounts.RecordPlatformObservation(platform);
        Catalog.OnCatalogAccountObserved(platform, observation);
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
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Storage, "music.accountCaches", "audioOfficial.clearFailed",
                new { platform }, error: error);
        }
        try
        {
            await ctx.PersistentChorus.ClearAsync(platform, ctx.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Storage, "music.accountCaches", "chorus.clearFailed",
                new { platform }, error: error);
        }
        try
        {
            await Catalog.ClearHomeSnapshotAsync(ctx.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Storage, "music.accountCaches", "homeSnapshot.clearFailed",
                new { platform }, error: error);
        }
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
                    MusicSongRecord? Read(string id) => snapshot.Songs.GetValueOrDefault(id);
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

    public event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested;
    public event EventHandler<DesktopGlobalSurfaceRequestEventArgs>? GlobalSurfaceRequested;

    internal string CatalogAccountNotice => Catalog?.CatalogAccountNotice ?? "";
    internal void ClearCatalogAccountNotice() => Catalog?.ClearCatalogAccountNotice();
    internal void CancelOnlinePlayback() => Playback?.CancelOnline();
    internal void RefreshPlaybackPresentationAvailability() => Playback?.RefreshPresentationAvailability();

    private void SubscribeDomainEvents()
    {
        // Runtime kernel broadcasts
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

        // Layer 1 (Settings) -> upper layers
        Settings.PlatformSettingsActivating += Accounts.ActivatePlatformSettingsAsync;
        Settings.AccountTransferRetiring += Accounts.RetireAccountTransferAsync;
        Settings.LibrarySyncDeactivating += () => Accounts.LibrarySync.DeactivateAsync();
        Settings.AccountDetailsDeactivating += Accounts.DeactivateAccountDetailsAsync;
        Settings.FeedbackPlatformChanged += Accounts.RefreshPlatformSetting;
        Settings.CatalogSourceInvalidated += Catalog.RetireCatalogSourcePages;
        Settings.DesktopLyricLayoutChanged += Lyrics.DesktopLyricBindings;
        Settings.OnlineLyricsEnabledChanged += Lyrics.PublishOnlineSettings;
        Settings.MetadataPolicyChanging += () => Lyrics.RetireLyrics();
        Settings.MetadataPolicyApplied += async () =>
        {
            if (!ctx.Stopping && !ctx.Disposed && Lyrics.NeedsLyrics && Playback.CurrentSong is not null)
                await Lyrics.RestoreLyricsAsync();
        };
        Settings.MetadataArtworkRefreshRequested += Playback.RefreshCurrentSongSnapshotAsync;
        Settings.VolumeSynced += Playback.NotifyVolumeChanged;
        Settings.QualityPolicyAccepted += Playback.OnQualityPolicyAccepted;
        Settings.SourcesListUpdated += Playback.PlaybackSourceBindings;
        Settings.PreloadInvalidated += Playback.CancelMusicPreload;
        Settings.SourceRevoking += Playback.RevokeChangedMusicSource;
        Settings.SourceExclusionAdmitted += Playback.AdmitSourceExclusion;
        Settings.SourceExclusionAdded += Playback.OnSourceExclusionAdded;
        Settings.SourceExclusionRemoved += Playback.OnSourceExclusionRemovedAsync;
        Settings.SearchPolicyChanging += Search.RetireAndClearForPolicyChangeAsync;

        // Layer 2 (Accounts) -> upper layers
        Accounts.AccountUpdated += platform =>
        {
            Queue.RecommendationBindings();
            if (platform == Catalog.HomePlatform) Catalog.RefreshHomeDisplay();
        };
        Accounts.AccountsReloaded += () =>
        {
            Queue.RecommendationBindings();
            Catalog.RefreshHomeDisplay();
        };
        Accounts.AccountSessionChanging += Catalog.RetireCatalogSession;
        Accounts.AccountSessionChanged += () =>
        {
            Catalog.RetireCatalogSession();
            Lyrics.RetireLyrics();
        };
        Accounts.AccountChangeSettled += currentEpoch =>
        {
            Catalog.BeginHomeAccountRecovery(true);
            if (currentEpoch && Lyrics.NeedsLyrics) _ = Lyrics.RestoreLyricsAsync();
        };
        Accounts.AccountCancelStarted += () => Lyrics.RetireLyrics();
        Accounts.AccountCancelSettled += () =>
        {
            if (Lyrics.NeedsLyrics) _ = Lyrics.RestoreLyricsAsync();
        };
        Accounts.LibrarySynced += async (origin, token) =>
        {
            await Library.RefreshPlaylists(token);
            var route = ctx.CurrentRoute;
            if (origin == ctx.LoadGeneration && ctx.LoadCancellation is null &&
                !Catalog.IsCatalogPage && !Lyrics.IsLyrics &&
                (route is "favorites" or "search" || route.StartsWith("playlist/", StringComparison.Ordinal)))
                await ctx.LoadAsync(ctx.CaptureLocation(), token, publishStatus: false);
        };

        // Layer 2 (Playback) -> upper layers
        Playback.PositionRefreshed += Lyrics.RefreshLyricPosition;
        Playback.LyricsPreviewAvailabilityChanged += Lyrics.RefreshLyricPreviewAvailability;
        Playback.SourcesUpdatedForLyrics += Lyrics.UpdateLyricSources;
        Playback.PlaybackStopped += Lyrics.ClearForPlaybackStop;
        Playback.RestoreLyricsRequested += () =>
        {
            if (Lyrics.NeedsLyrics) _ = Lyrics.RestoreLyricsAsync();
        };
        Playback.DesktopLyricsStateChanged += Lyrics.DesktopLyricBindings;
        Playback.PlaybackModeAvailabilityChanged += Queue.NotifyPlaybackModeAvailability;
        Playback.ExplicitPauseRequested += Queue.ObserveExplicitRecommendationPause;
        Playback.SteppingNextOverride += Queue.TryAdvanceRecommendationForStepAsync;
        Playback.QueueMutated += Queue.InvalidateQueueSurface;
        Playback.PlaybackModeChanged += Queue.NotifyModeChanged;
        Playback.SearchRecommendationCleared += Queue.ClearSearchRecommendationStart;
        Playback.QueueApplied += Queue.RefreshQueueSurfaceCurrent;
        Playback.PlaybackStateObserved += Queue.ObserveRecommendationPlayback;
        Playback.PlaybackStateChanged += Queue.NotifyRecommendationPlaybackChanged;
        Playback.PlaybackStartedRecorded += () =>
        {
            Queue.ObserveRecommendationPlayback();
            Queue.StartPendingSearchRecommendationPreview();
        };
        Playback.PlaybackCommitted += Catalog.CommitCatalogPlayback;
        Playback.LibraryCollectionEdited += origin => Library.RefreshAfterEdit(origin);

        // Layer 3 (Queue) -> upper layers
        Queue.PlaylistsChanged += token => Library.RefreshPlaylists(token);
        Queue.RecommendationActionError += Catalog.PublishCatalogActionError;

        // Layer 4 (Search) -> upper layers
        Search.CatalogPreviewSurfaceRetiring += Catalog.RetireCatalogPreviewSurface;
        Search.SearchSessionStarted += Catalog.BeginSearchCatalogState;
        Search.SearchMetadataPublished += Catalog.PublishSearchCatalogMetadata;
        Search.CatalogTagsClearing += Catalog.ClearCatalogTags;

        // Layer 5 (Catalog) -> upper layers
        Catalog.PlaylistsChanged += token => Library.RefreshPlaylists(token);
        Catalog.SongAppendNextRequested += song => Library.AppendSongNextAsync(song);
        Catalog.SongFavoriteRequested += (song, toggle) =>
            toggle ? Library.ToggleSongFavoriteAsync(song) : Library.FavoriteSongAsync(song);
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
            Task.WhenAll(ctx.SnapshotEventWrites()), Task.WhenAll(ctx.LoadDrains.ToArray()), Settings.RetireSearchHistoryAsync());
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
        ctx.Application.SongMetadataChanged -= OnSongMetadataChanged;
        ctx.Application.AccountDetailsInvalidated -= OnAccountDetailsInvalidated;
        ctx.Application.AccountSessionChanged -= OnAccountSessionChanged;
        debugCacheRegistration?.Dispose();
        try { await Task.WhenAll(ctx.SnapshotEventWrites()); } catch { }
        try { await Accounts.DisposeLibrarySyncAsync(); } catch (Exception error) { failure ??= error; }
        try { await Accounts.DisposeAccountDetailsAsync(); } catch (Exception error) { failure ??= error; }
        try { await Catalog.VipPage.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        try { await ctx.ArtworkLoader.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        if (ctx.AudioFileCache is not null)
            try { await ctx.AudioFileCache.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        try { await Catalog.DisposeResourcesAsync(); } catch (Exception error) { failure ??= error; }
        try { await ctx.Application.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        Library.Unsubscribe();
        Search.Unsubscribe();
        Discovery.Unsubscribe();
        Queue.ReleaseRecommendations();
        ctx.Disposed = true;
        Playback.UnsubscribePlayback();
        ctx.Lifetime.Dispose();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

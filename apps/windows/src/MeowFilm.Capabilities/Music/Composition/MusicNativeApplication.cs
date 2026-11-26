using System.Runtime.ExceptionServices;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Capabilities.Search;
using MeowFilm.Capabilities.Sync;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication : IAsyncDisposable
{
    private readonly MusicLibraryOwner library;
    private readonly MusicSongMatcher songMatcher;
    private readonly MusicLibrarySyncService librarySync;
    private readonly MusicAccountOwner accounts;
    private readonly MusicPlatformClient platform;
    private readonly MusicVipPageOwner vipPage;
    private readonly MusicPlatformCatalog catalog;
    private readonly MusicNativeCatalogOwner nativeCatalog;
    private readonly NativeMusicSearchDiscovery discovery;
    private readonly MusicScriptOwner scripts;
    private readonly MusicLyricApplication lyrics;
    private readonly CatalogSessions sessions;
    private readonly IStorageDatabase dataDatabase;
    private readonly IStorageDatabase cacheDatabase;
    internal MusicLibraryOwner Library => library;
    internal MusicAccountOwner Accounts => accounts;
    internal MusicNativeCatalogOwner Catalog => nativeCatalog;
    internal MusicVipPageOwner VipPage => vipPage;
    internal MusicScriptOwner Scripts => scripts;
    internal MusicLyricApplication Lyrics => lyrics;
    internal MusicSearchCoordinator Search => searchCoordinator;
    internal NativeMusicSearchDiscovery Discovery => discovery;
    internal MusicRecommendationCoordinator Recommendations => recommendations;
    internal PlaybackCache.MusicSourceCache SourceCache => sourceCache;
    internal MusicSongMatcher SongMatcher => songMatcher;
    internal PlaybackCache.MusicPresentationCache PresentationCache => library.PresentationCache;
    internal Task ArtworkLoadCompletedAsync(string address, bool succeeded) =>
        library.ArtworkLoadCompletedAsync(address, succeeded);
    public event Action? ChorusInvalidated;
    internal event Action<string>? AccountSessionChanged;
    private void InvalidateChorus(string platform)
    {
        foreach (var handler in ChorusInvalidated?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicPlayback, "chorus", "invalidate.listenerFailed",
                    new { platform }, error: error);
            }
        }
    }
    private void InvalidateAccountCaches(string platform)
    {
        CancellationToken token = default;
        bool run = false;
        lock (gate)
        {
            if (!stopping && !closed)
            {
                if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                token = lifetime.Token;
                run = true;
            }
        }
        if (run) _ = ClearSourceCacheForAccountChangeAsync(platform, token);
        foreach (var handler in AccountSessionChanged?.GetInvocationList() ?? [])
        {
            try { ((Action<string>)handler)(platform); }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicAccount, "session", "accountCacheListener.failed",
                    new { platform }, error: error);
            }
        }
    }
    private async Task ClearSourceCacheForAccountChangeAsync(string platform, CancellationToken token)
    {
        try
        {
            await sourceCache.ClearAsync(platform, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Storage, "music.sourceCache", "accountChange.clearFailed",
                new { platform }, error: error);
        }
        finally
        {
            lock (gate) if (--pending == 0) drained.TrySetResult();
        }
    }
    internal void ClearMemoryCaches() => searchCoordinator.ClearMemoryCache();
    internal Task ClearCachesAsync(CancellationToken cancellationToken = default) =>
        RunAdmittedAsync<bool>(async token =>
        {
            ClearMemoryCaches();
            await sourceCache.ClearAsync(null, token).ConfigureAwait(false);
            await songMatcher.ClearAsync(token).ConfigureAwait(false);
            try { await platform.ClearAsync(null, token).ConfigureAwait(false); }
            catch (MusicPlatformException error) when (error.Code is "CLOSED" or "CAPABILITY_UNAVAILABLE" or "TIMEOUT") { }
            return true;
        }, cancellationToken);
    internal async Task<object> GetCacheStatsAsync(CancellationToken cancellationToken = default)
    {
        var sources = await sourceCache.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var matches = await songMatcher.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            sourceCache = new { entryCount = sources.EntryCount, storedBytes = sources.StoredBytes, maximumBytes = sources.MaximumBytes },
            songMatches = new { songCount = matches.SongCount, storedBytes = matches.StoredBytes, maximumBytes = matches.MaximumBytes },
            lyricFlights = lyrics.GetCacheStats(),
            searchResultsCache = searchCoordinator.GetCacheStats()
        };
    }
    private readonly object gate = new();
    private CancellationTokenSource lifetime = new();
    private TaskCompletionSource drained = Completed();
    private Task? preparation, release;
    private int pending;
    private bool stopping, closed;

    private void OnScriptSourcesChanged() => lyrics.Invalidate();
    private void OnMetadataPolicyChanged(MusicMetadataPolicyRecord _) => lyrics.Invalidate();
    private void OnLocalLyricsChanged() => lyrics.Invalidate();
    private void OnLocalCollectionsChanged() => _ = SaveSongMatchesAsync(CancellationToken.None);

    public MusicNativeApplication(IStorageDatabase dataStore, IStorageDatabase cacheStore, MusicLibraryDatabase libraryStore, IRuntimeClient scriptRuntime)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        ArgumentNullException.ThrowIfNull(cacheStore);
        ArgumentNullException.ThrowIfNull(libraryStore);
        ArgumentNullException.ThrowIfNull(scriptRuntime);
        dataDatabase = dataStore;
        cacheDatabase = cacheStore;
        if (!ReferenceEquals(libraryStore.Database, dataStore))
            throw new ArgumentException("SHARED_DATABASE_REQUIRED", nameof(libraryStore));
        platform = new(scriptRuntime);
        library = new(libraryStore, cacheStore);
        accounts = new(dataStore, cacheStore, new MusicAccountProtocol(platform));
        vipPage = new(accounts, platform);
        sourceCache = new(cacheStore);
        sessions = new(accounts);
        platform.BindSessions(sessions);
        catalog = new(platform, sessions);
        songMatcher = new(library, new(cacheStore), SearchCatalogAsync);
        nativeCatalog = new(catalog, library, accounts, sessions, () => SaveSongMatchesAsync(CancellationToken.None));
        fmPreferences = new(dataStore);
        recommendations = new(catalog, library, SelectRecommendationAsync, readAsync: async (request, token) =>
        {
            await PrepareSongFactsAsync(token).ConfigureAwait(false);
            long observation = library.BeginSongObservation();
            var result = await ReadRecommendationsAsync(request, token).ConfigureAwait(false);
            return await library.AdoptPlatformResultAsync(result, token, observation: observation).ConfigureAwait(false);
        });
        searchCoordinator = new(catalog, SearchCatalogAsync, songMatcher.NextObservation);
        searchCoordinator.SongFactsProjection = songMatcher.ProjectKnownFacts;
        searchCoordinator.MatchingObserved += ObserveSongMatches;
        discovery = new NativeMusicSearchDiscovery(accounts, platform, library);
        scripts = new(library, scriptRuntime);
        lyrics = new(library, platform, scripts);
        librarySync = new(library, accounts, platform);
        var feedbackSettings = new MusicFeedbackSettingsOwner(dataStore);
        Feedback = new(feedbackSettings,
            new NativeMusicFeedbackProtocol(accounts, platform,
                store: dataStore, settings: feedbackSettings, authenticationRejected: session => accounts.Revoke(session)), accounts);
        accounts.SessionChanged += RecommendationAccountChanged;
        accounts.SessionChanged += nativeCatalog.Invalidate;
        accounts.SessionChanged += searchCoordinator.Invalidate;
        accounts.SessionChanged += InvalidateSongMatches;
        accounts.SessionChanged += lyrics.Invalidate;
        accounts.SessionChanged += Feedback.AccountChanged;
        accounts.SessionChanged += librarySync.Invalidate;
        accounts.SessionChanged += discovery.Invalidate;
        accounts.SessionChanged += InvalidateChorus;
        accounts.SessionChanged += InvalidateAccountCaches;
        scripts.SourcesChanged += OnScriptSourcesChanged;
        library.MetadataPolicyChanged += OnMetadataPolicyChanged;
        library.LocalLyricsChanged += OnLocalLyricsChanged;
        library.LocalCollectionsChanged += OnLocalCollectionsChanged;
    }

    public MusicFeedbackController Feedback { get; }
    public MusicLibrarySyncService LibrarySync => librarySync;

    public Task<MusicLyricLookupResult> LookupLyricsAsync(
        MusicSongRecord song,
        string? sourceKey = null,
        CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            using var frame = sessions.Begin();
            try
            {
                return await lyrics.LookupAsync(song, sourceKey, token).ConfigureAwait(false);
            }
            catch (MusicPlatformException error) when (error.Code is "AUTH_REQUIRED" or "AUTH_EXPIRED")
            {
                if (frame.Session is { } failed) accounts.Revoke(failed);
                throw;
            }
        }, cancellationToken);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping || closed) return Task.FromException(new InvalidOperationException("CLOSED"));
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        return InitializeOwnersAsync(generation, cancellationToken);
    }
    private async Task InitializeOwnersAsync(CancellationToken generation, CancellationToken caller)
    {
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(generation, caller);
            await Feedback.InitializeAsync(request.Token).ConfigureAwait(false);
            await PrepareSongFactsAsync(request.Token).ConfigureAwait(false);
            await recommendations.RestorePersistedSessionAsync(request.Token).ConfigureAwait(false);
            await librarySync.StartAsync(request.Token).ConfigureAwait(false);
        }
        finally { lock (gate) if (--pending == 0) drained.TrySetResult(); }
    }

    private async Task<T> RunAdmittedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping) throw new MusicOperationException("CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        try
        {
            // Cached owners can complete every await synchronously. Keep document
            // cloning, validation and serialization off the caller's UI dispatcher.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation);
            request.Token.ThrowIfCancellationRequested();
            return await operation(request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new MusicOperationException("CANCELLED"); }
        catch (MusicOperationException) { throw; }
        catch (MusicLibraryException error) { throw new MusicOperationException(error.Code); }
        catch (MusicAccountException error) { throw new MusicOperationException(error.Code, error.RetryAfterSeconds); }
        catch (MusicPlatformException error) { throw new MusicOperationException(error.Code); }
        catch (RecommendationException error) { throw new MusicOperationException(error.Code); }
        catch (StorageException error)
        {
            throw new MusicOperationException(error.Error switch
            {
                StorageError.InvalidRequest => "INVALID_REQUEST",
                StorageError.Unavailable => "STORAGE_UNAVAILABLE",
                StorageError.Conflict => "STORAGE_CONFLICT",
                StorageError.Corrupt or StorageError.UnsupportedVersion => "CORRUPT_STORAGE",
                _ => "STORAGE"
            });
        }
        finally { lock (gate) if (--pending == 0) drained.TrySetResult(); }
    }

    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (gate)
        {
            stopping = true;
            task = preparation ??= PrepareCoreAsync(drained.Task);
        }
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task PrepareCoreAsync(Task pendingOperations)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Exception? failure = null;
        try { lifetime.Cancel(); } catch (Exception e) { failure = e; }
        async Task Attempt(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); } catch (Exception e) { failure ??= e; }
        }
        var recommendationDrain = PrepareRecommendationCoreAsync();
        var accountReadDrain = accounts.PrepareReadShutdownAsync();
        try { nativeCatalog.Invalidate(); } catch (Exception e) { failure ??= e; }
        try { lyrics.Invalidate(); } catch (Exception e) { failure ??= e; }
        try { discovery.Invalidate(); } catch (Exception e) { failure ??= e; }
        try { songMatcher.CancelFlights(); } catch (Exception e) { failure ??= e; }
        await Task.WhenAll(
            Attempt(() => recommendationDrain),
            Attempt(vipPage.PrepareShutdownAsync),
            Attempt(() => accountReadDrain),
            Attempt(lyrics.DrainAsync),
            Attempt(discovery.CancelAndDrainAsync),
            Attempt(() => searchCoordinator.PrepareShutdownAsync()),
            Attempt(catalog.DrainAsync),
            Attempt(() => librarySync.PrepareShutdownAsync()),
            Attempt(() => Feedback.PrepareShutdownAsync()),
            Attempt(() => scripts.PrepareShutdownAsync())).ConfigureAwait(false);
        await pendingOperations.ConfigureAwait(false);
        await Attempt(() => SaveSongMatchesAsync(CancellationToken.None)).ConfigureAwait(false);
        await Attempt(songMatcher.DrainAsync).ConfigureAwait(false);
        await Attempt(() => library.PrepareShutdownAsync()).ConfigureAwait(false);
        await Attempt(platform.DrainAsync).ConfigureAwait(false);
        await Attempt(cacheDatabase.DrainAsync).ConfigureAwait(false);
        await Attempt(dataDatabase.DrainAsync).ConfigureAwait(false);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public ValueTask DisposeAsync() => new(AbortAsync());

    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            _ = BeginFinalization();
            return cancellationToken.CanBeCanceled ? release!.WaitAsync(cancellationToken) : release!;
        }
    }

    private Task BeginFinalization()
    {
        closed = true; stopping = true;
        var prepare = preparation ??= PrepareCoreAsync(drained.Task);
        release ??= ReleaseCoreAsync(prepare);
        return prepare;
    }

    private async Task ReleaseCoreAsync(Task prepare)
    {
        await Task.Yield();
        Exception? failure = null;
        async Task Attempt(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); } catch (Exception e) { failure ??= e; }
        }
        try { await prepare.ConfigureAwait(false); } catch { /* Business failure does not prevent releasing settled owners. */ }
        await Attempt(recommendations.AbortAsync).ConfigureAwait(false);
        lock (gate)
        {
            if (recommendationBinding is { } binding) Interlocked.Exchange(ref binding.Revoked, 1);
            recommendationBinding = null; recommendationBindingEpoch++;
        }
        await Attempt(() => scripts.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => lyrics.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => nativeCatalog.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => searchCoordinator.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => discovery.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => Feedback.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => librarySync.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => vipPage.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(() => accounts.PrepareCloseAsync()).ConfigureAwait(false);
        await Attempt(() => accounts.DisposeAsync().AsTask()).ConfigureAwait(false);
        lock (gate) officialAccepted.Clear();
        accounts.SessionChanged -= RecommendationAccountChanged;
        accounts.SessionChanged -= nativeCatalog.Invalidate;
        accounts.SessionChanged -= searchCoordinator.Invalidate;
        accounts.SessionChanged -= InvalidateSongMatches;
        accounts.SessionChanged -= lyrics.Invalidate;
        accounts.SessionChanged -= Feedback.AccountChanged;
        accounts.SessionChanged -= librarySync.Invalidate;
        accounts.SessionChanged -= discovery.Invalidate;
        accounts.SessionChanged -= InvalidateChorus;
        accounts.SessionChanged -= InvalidateAccountCaches;
        scripts.SourcesChanged -= OnScriptSourcesChanged;
        library.MetadataPolicyChanged -= OnMetadataPolicyChanged;
        library.LocalLyricsChanged -= OnLocalLyricsChanged;
        library.LocalCollectionsChanged -= OnLocalCollectionsChanged;
        await Attempt(() => library.AbortAsync()).ConfigureAwait(false);
        try { songMatcher.Dispose(); } catch (Exception e) { failure ??= e; }
        try { catalog.Dispose(); } catch (Exception e) { failure ??= e; }
        await Attempt(catalog.DrainAsync).ConfigureAwait(false);
        await Attempt(() => platform.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Attempt(cacheDatabase.DrainAsync).ConfigureAwait(false);
        await Attempt(dataDatabase.DrainAsync).ConfigureAwait(false);
        try { recommendationLifetime.Dispose(); } catch (Exception e) { failure ??= e; }
        try { lifetime.Dispose(); } catch (Exception e) { failure ??= e; }
        searchPolicyLane.Dispose();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult(); return result;
    }

    /// <summary>Native-only operation context; credentials never enter result DTOs or diagnostics.</summary>
    internal sealed class CatalogSessions(MusicAccountOwner owner) : IMusicPlatformSessionSource
    {
        private readonly AsyncLocal<Frame?> current = new();
        internal Frame Begin(bool awaitPhysical = false, bool inheritLease = false)
        {
            var previous = current.Value;
            var frame = new Frame(this, previous, awaitPhysical);
            if (inheritLease) previous?.TransferLeaseTo(frame);
            current.Value = frame; return frame;
        }
        public async ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken)
        {
            if (platform is "kw" or "mg") return null;
            var frame = current.Value;
            if (frame?.Captured == true)
                return frame.Reuse(platform, cancellationToken);
            var session = frame?.AwaitPhysical == true
                ? await owner.AcquireSettledAsync(platform, cancellationToken).ConfigureAwait(false)
                : await owner.AcquireAsync(platform, cancellationToken).ConfigureAwait(false);
            frame?.Capture(platform, session);
            return session;
        }
        public bool IsCurrent(MusicPlatformSession session) => owner.IsCurrent(session);
        internal sealed class Frame(CatalogSessions source, Frame? previous, bool awaitPhysical) : IDisposable
        {
            internal bool AwaitPhysical { get; } = awaitPhysical;
            private bool captured;
            private int transferred;
            internal bool Captured => captured;
            internal MusicPlatformSession? Session { get; private set; }
            internal string? Platform { get; private set; }
            internal MusicPlatformSession? Reuse(string platform, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Platform != platform) throw new MusicAccountException("INVALID_REQUEST");
                return Session;
            }
            internal void Capture(string platform, MusicPlatformSession? session)
            {
                if (captured) return;
                Platform = platform; Session = session; captured = true;
            }
            internal void TransferLeaseTo(Frame next)
            {
                if (captured && Interlocked.Exchange(ref transferred, 1) == 0)
                    next.Capture(Platform!, Session);
            }
            public void Dispose() { source.current.Value = previous; }
            public override string ToString() => "Catalog session context (redacted)";
        }
    }
}

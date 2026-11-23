using System.Runtime.ExceptionServices;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Lyrics;
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
    private readonly MusicScriptOwner scripts;
    private readonly MusicLyricApplication lyrics;
    private readonly CatalogSessions sessions;
    private readonly IStorageDatabase dataDatabase;
    private readonly IStorageDatabase cacheDatabase;
    internal PlaybackCache.MusicPresentationCache PresentationCache => library.PresentationCache;
    internal Task ArtworkLoadCompletedAsync(string address, bool succeeded) =>
        library.ArtworkLoadCompletedAsync(address, succeeded);
    public event Action? ChorusInvalidated;
    internal event Action<string>? AccountSessionChanged;
    private void InvalidateChorus(string platform)
    {
        foreach (var handler in ChorusInvalidated?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch { /* Display cannot block credential retirement. */ }
    }
    private void InvalidateAccountCaches(string platform)
    {
        _ = ClearSourceCacheForAccountChangeAsync(platform);
        foreach (var handler in AccountSessionChanged?.GetInvocationList() ?? [])
            try { ((Action<string>)handler)(platform); } catch { /* Cache listeners cannot block credential retirement. */ }
    }
    private async Task ClearSourceCacheForAccountChangeAsync(string platform)
    {
        try
        {
            await sourceCache.ClearAsync(platform, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* Best-effort cleanup on account change; cache entries are also scope-keyed. */ }
    }
    internal void ClearMemoryCaches()
    {
        lyrics.ClearMemoryCache();
        searchCoordinator.ClearMemoryCache();
    }
    internal async Task ClearCachesAsync(CancellationToken cancellationToken = default)
    {
        ClearMemoryCaches();
        await sourceCache.ClearAsync(null, cancellationToken).ConfigureAwait(false);
        await songMatcher.ClearAsync(cancellationToken).ConfigureAwait(false);
        try { await platform.ClearAsync(null, cancellationToken).ConfigureAwait(false); }
        catch (MusicPlatformException error) when (error.Code is "CLOSED" or "CAPABILITY_UNAVAILABLE" or "TIMEOUT") { }
    }
    internal async Task<object> GetCacheStatsAsync(CancellationToken cancellationToken = default)
    {
        var sources = await sourceCache.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        var matches = await songMatcher.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            sourceCache = new { entryCount = sources.EntryCount, storedBytes = sources.StoredBytes, maximumBytes = sources.MaximumBytes },
            songMatches = new { songCount = matches.SongCount, storedBytes = matches.StoredBytes, maximumBytes = matches.MaximumBytes },
            lyricMemoryCache = lyrics.GetCacheStats(),
            searchResultsCache = searchCoordinator.GetCacheStats()
        };
    }
    internal async Task<MusicLibraryOwner.SongSnapshotBatch> SongSnapshotsAsync(IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping || closed) throw new MusicOperationException("CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation);
            return await library.SongSnapshotsAsync(ids, request.Token).ConfigureAwait(false);
        }
        catch (MusicLibraryException error) { throw new MusicOperationException(error.Code); }
        catch (StorageException error)
        {
            throw new MusicOperationException(error.Error switch
            {
                StorageError.Unavailable => "STORAGE_UNAVAILABLE",
                StorageError.Corrupt or StorageError.UnsupportedVersion => "CORRUPT_STORAGE",
                _ => "STORAGE"
            });
        }
        finally { lock (gate) if (--pending == 0) drained.TrySetResult(); }
    }
    internal JsonElement? SongSnapshot(string id) => library.SongSnapshot(id);
    internal event Action<IReadOnlySet<string>>? SongMetadataChanged
    {
        add => library.SongMetadataChanged += value;
        remove => library.SongMetadataChanged -= value;
    }
    private readonly object gate = new();
    private CancellationTokenSource lifetime = new();
    private TaskCompletionSource drained = Completed();
    private Task? preparation, release;
    private int pending;
    private bool stopping, closed, accountReceiptUnknown;
    private long accountReceiptEpoch;

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
        vipPage = new(accounts, new MusicVipPageProtocol(platform));
        officialAudio = new(platform);
        sourceCache = new(cacheStore);
        sessions = new(accounts);
        platform.BindSessions(sessions);
        catalog = new(platform, sessions);
        songMatcher = new(library, new(cacheStore), SearchCatalogAsync);
        nativeCatalog = new(catalog, library)
        {
            SearchSongs = SearchAndObserveCatalogAsync
        };
        recommendationQueue = new(library);
        fmPreferences = new(dataStore);
        recommendations = new(catalog, recommendationQueue, SelectRecommendationAsync, readAsync: async (request, token) =>
        {
            await PrepareSongFactsAsync(token).ConfigureAwait(false);
            long observation = library.BeginSongObservation();
            var result = await ReadRecommendationsAsync(request, token).ConfigureAwait(false);
            return await library.AdoptPlatformResultAsync(result, token, observation: observation).ConfigureAwait(false);
        });
        searchCoordinator = new(catalog, SearchCatalogAsync, songMatcher.NextObservation);
        searchCoordinator.SongFactsProjection = songMatcher.ProjectKnownFacts;
        searchCoordinator.MatchingObserved += ObserveSongMatches;
        var discoverySessions = new DiscoverySessions(accounts);
        discovery = new NativeMusicSearchDiscovery(discoverySessions, platform, library);
        scripts = new(library, scriptRuntime);
        lyrics = new(library, new NativeMusicLyrics(platform), scripts.ExecuteAsync);
        librarySync = new(library, accounts, platform);
        var feedbackSettings = new MusicFeedbackSettingsOwner(dataStore);
        Feedback = new(feedbackSettings,
            new NativeMusicFeedbackProtocol(accounts, platform,
                store: dataStore, settings: feedbackSettings, authenticationRejected: session => accounts.Revoke(session)), accounts);
        // Fence recommendation callbacks synchronously before any catalog cleanup.
        accounts.SessionChanged += RecommendationAccountChanged;
        accounts.SessionChanged += nativeCatalog.Invalidate;
        accounts.SessionChanged += searchCoordinator.Invalidate;
        accounts.SessionChanged += InvalidateSongMatches;
        accounts.SessionChanged += lyrics.Invalidate;
        accounts.SessionChanged += Feedback.AccountChanged;
        accounts.SessionChanged += librarySync.Invalidate;
        accounts.SessionChanged += AccountDetailsChanged;
        accounts.SessionChanged += DiscoveryAccountChanged;
        accounts.SessionChanged += InvalidateChorus;
        accounts.SessionChanged += InvalidateAccountCaches;
    }

    public MusicFeedbackController Feedback { get; }
    public event Action? LibrarySyncChanged
    {
        add => librarySync.StateChanged += value;
        remove => librarySync.StateChanged -= value;
    }

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
            // Explicit local startup/retry can reconcile a receipt; ordinary request
            // acquisition never replays or automatically clears an unknown write.
            // This restores local policy/account-bound unsent work, not a remote
            // startup write. Network drain is owned asynchronously by the adapter.
            await Feedback.InitializeAsync(request.Token).ConfigureAwait(false);
            await PrepareSongFactsAsync(request.Token).ConfigureAwait(false);
            await librarySync.StartAsync(request.Token).ConfigureAwait(false);
        }
        finally { lock (gate) if (--pending == 0) drained.TrySetResult(); }
    }

    public async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping) return Failed("CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        try
        {
            // Cached owners can complete every await synchronously. Keep document
            // cloning, validation and serialization off the caller's UI dispatcher.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation);
            var token = request.Token;
            token.ThrowIfCancellationRequested();
            if (method is null) return Failed("INVALID_REQUEST");
            if (method == "music.official.candidates")
                return await ExecuteOfficialCandidatesAsync(parameters, token).ConfigureAwait(false);
            if (method == "music.official.check")
                return await CheckOfficialSourceAsync(parameters, token).ConfigureAwait(false);
            if (method == "music.songs.complete")
                return await CompleteSongAsync(parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.official.", StringComparison.Ordinal))
                return await ExecuteOfficialAudioAsync(method, parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.recommendations.", StringComparison.Ordinal))
                return await ExecuteRecommendationsAsync(method, parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.discovery.", StringComparison.Ordinal))
                return await ExecuteDiscoveryAsync(method, parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.search.", StringComparison.Ordinal))
                return await ExecuteSearchAsync(method, parameters, token).ConfigureAwait(false);
            if (method.StartsWith("music.librarySync.", StringComparison.Ordinal))
                return await librarySync.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
            if (method == "music.lyrics.lookup")
            {
                using var frame = sessions.Begin();
                var result = await lyrics.ExecuteAsync(parameters, token).ConfigureAwait(false);
                if (result.TryGetProperty("code", out var error) && error.GetString() is "AUTH_EXPIRED" or "AUTH_REQUIRED" &&
                    frame.Session is { } failed) accounts.Revoke(failed);
                return result;
            }
            if (method.StartsWith("music.accounts.", StringComparison.Ordinal))
            {
                long receiptEpoch;
                lock (gate) receiptEpoch = accountReceiptEpoch;
                var receipt = await accounts.CallAsync(method, parameters, token).ConfigureAwait(false);
                lock (gate)
                {
                    if (receipt.TryGetProperty("code", out var error) &&
                        error.GetString() is "COMMIT_UNKNOWN" or "HANDOFF_UNCONFIRMED")
                    { accountReceiptUnknown = true; accountReceiptEpoch++; }
                    else if (method == "music.accounts.list" && receipt.TryGetProperty("ok", out var read) && read.ValueKind == JsonValueKind.True)
                    {
                        if (receiptEpoch == accountReceiptEpoch) accountReceiptUnknown = false;
                        // Explicit reconciliation only; an older concurrent read
                        // cannot clear a newer unknown receipt.
                    }
                }
                return receipt;
            }
            if (method.StartsWith("music.catalog.", StringComparison.Ordinal))
            {
                // Capture the exact issued lease used by the actual catalog request,
                // not a later Acquire after a failed response. Concurrent calls have
                // independent frames; an old auth failure cannot revoke a replacement.
                using var frame = sessions.Begin();
                var result = await ExecuteCatalogWithRecommendationLaneAsync(method, parameters, token).ConfigureAwait(false);
                if (result.TryGetProperty("code", out var error) && error.GetString() is "AUTH_EXPIRED" or "AUTH_REQUIRED" &&
                    frame.Session is { } failed) accounts.Revoke(failed);
                if (method == "music.catalog.playlist.commit" && Okay(result))
                    await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
                return result;
            }
            if (method.StartsWith("music.sources.", StringComparison.Ordinal) ||
                method.StartsWith("music.online.", StringComparison.Ordinal))
            {
                bool changesSource = method.StartsWith("music.sources.", StringComparison.Ordinal) &&
                    method is not ("music.sources.list" or "music.sources.detail" or "music.sources.check" or "music.sources.edit");
                if (changesSource) lyrics.Invalidate();
                try { return await scripts.ExecuteAsync(method, parameters, token).ConfigureAwait(false); }
                finally { if (changesSource) lyrics.Invalidate(); }
            }

            bool changesLyrics = method is "music.settings.save" or "music.metadata.settings.save" or
                "music.lyrics.local.save" or "music.lyrics.local.remove";
            if (changesLyrics) lyrics.Invalidate();
            // All final queue mutations share the coordinator's one lane.
            // Native recommendation commands stay private.
            var reply = await recommendationQueue.ExecuteAsync(method, parameters, token).ConfigureAwait(false);
            // Local membership changes persist earlier matches or evict derived rows for removed members.
            if (method is ("music.library.create" or "music.library.edit" or "music.library.remove" or
                "music.library.addSongs" or "music.library.removeSong") && Okay(reply))
                await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            if (changesLyrics) lyrics.Invalidate();
            return reply;
        }
        catch (OperationCanceledException) { return Failed("CANCELLED"); }
        catch (MusicPlatformException error) { return Failed(error.Code); }
        finally { lock (gate) if (--pending == 0) drained.TrySetResult(); }
    }

    public async Task<JsonElement> ImportSourceAsync(ReadOnlyMemory<byte> source, string basename,
        string? installationId = null, CancellationToken cancellationToken = default)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping) return Failed("CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation);
            request.Token.ThrowIfCancellationRequested();
            lyrics.Invalidate();
            try { return await scripts.ImportSourceAsync(source, basename, installationId, request.Token).ConfigureAwait(false); }
            finally { lyrics.Invalidate(); }
        }
        catch (OperationCanceledException) { return Failed("CANCELLED"); }
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
        // Caller cancellation only stops waiting, never abandons an admitted write
        // or restarts preparation. The App retains borrowed dependencies.
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
        // Fence recommendation callbacks first, then abort service-owned reads.
        // Waiting for narrow preparation before invalidation would leave an
        // already admitted HTTP/account read running until its normal deadline.
        var recommendationDrain = PrepareRecommendationCoreAsync();
        var accountReadDrain = accounts.PrepareReadShutdownAsync();
        try { nativeCatalog.Invalidate(); } catch (Exception e) { failure ??= e; }
        try { lyrics.Invalidate(); } catch (Exception e) { failure ??= e; }
        lock (gate) issuedDiscoveryGeneration = 0;
        try { discovery.Invalidate(); } catch (Exception e) { failure ??= e; }
        await Task.WhenAll(
            Attempt(() => recommendationDrain),
            Attempt(vipPage.PrepareShutdownAsync),
            Attempt(() => accountReadDrain),
            Attempt(lyrics.DrainAsync),
            Attempt(discovery.CancelAndDrainAsync),
            Attempt(() => searchCoordinator.PrepareShutdownAsync()),
            Attempt(songMatcher.DrainAsync),
            Attempt(catalog.DrainAsync),
            Attempt(() => librarySync.PrepareShutdownAsync()),
            Attempt(() => Feedback.PrepareShutdownAsync()),
            Attempt(() => scripts.PrepareShutdownAsync())).ConfigureAwait(false);
        // Native mutations ignore cancellation after CAS admission. Their receipts
        // must settle before the final library preparation, even on earlier failure.
        await pendingOperations.ConfigureAwait(false);
        await Attempt(() => SaveSongMatchesAsync(CancellationToken.None)).ConfigureAwait(false);
        await Attempt(() => library.PrepareShutdownAsync()).ConfigureAwait(false);
        await Attempt(platform.DrainAsync).ConfigureAwait(false);
        await Attempt(cacheDatabase.DrainAsync).ConfigureAwait(false);
        await Attempt(dataDatabase.DrainAsync).ConfigureAwait(false);
        // The account port may have returned an unknown receipt while its physical
        // CAS is still running. Without an explicit list/reconciliation, preparation
        // must fail rather than authorize the capability to dispose the shared App store.
        lock (gate) if (accountReceiptUnknown) failure ??= new MusicAccountException("COMMIT_UNKNOWN");
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

    // Called under gate. Business failures remain on their operation receipts.
    // Release reports only failures that prevent ownership from being relinquished.
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
        accounts.SessionChanged -= AccountDetailsChanged;
        accounts.SessionChanged -= DiscoveryAccountChanged;
        accounts.SessionChanged -= InvalidateChorus;
        accounts.SessionChanged -= InvalidateAccountCaches;
        // Explicit finalization may follow failed preparation. Abort fences memory;
        // it neither retries a commit nor declares an unknown receipt rolled back.
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

    private static JsonElement Failed(string code) => JsonSerializer.SerializeToElement(new { ok = false, code });
    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult(); return result;
    }

    /// <summary>Native-only operation context; credentials never enter result DTOs or diagnostics.</summary>
    private sealed class CatalogSessions(MusicAccountOwner owner) : IMusicPlatformSessionSource
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
            // KW/MG public catalog calls do not create fictitious account owners.
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
                // Only the initial read consumes a startup lease. Async callbacks
                // inheriting the old execution context must acquire afresh.
                if (captured && Interlocked.Exchange(ref transferred, 1) == 0)
                    next.Capture(Platform!, Session);
            }
            // Retiring a subscription must not clear the lease of a still-running child read.
            public void Dispose() { source.current.Value = previous; }
            public override string ToString() => "Catalog session context (redacted)";
        }
    }
}

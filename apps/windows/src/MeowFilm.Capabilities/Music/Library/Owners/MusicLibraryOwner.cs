using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Core.Debug;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Native authority for local music state and its one transactional record store.</summary>
public sealed partial class MusicLibraryOwner : IAsyncDisposable
{
    public const string ContractVersion = "0.7.0";
    internal event Action? LocalCollectionsChanged;
    private readonly MusicLibraryDatabase store;
    private readonly string scope;
    internal MusicPresentationCache PresentationCache { get; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private MusicLibrarySession? session;
    private MusicLibraryRevision revision = MusicLibraryRevision.Empty;
    private MusicSongSession? visibleSongs;
    private Pending? pending;
    private Task? abortion;
    private bool preparing, closed;
    private long queueVersion, serial;
    private List<string> shuffleHistory = [];

    private enum PendingOwner
    {
        Appearance,
        CatalogSource,
        DeviceSettings,
        DesktopLyrics,
        History,
        Library,
        LocalLyrics,
        Metadata,
        NetworkPlaylists,
        OfficialSources,
        Quality,
        Queue,
        Recommendation,
        SearchHistory,
        SearchPolicy,
        SourceExclusions,
        SongMetadata,
        Sync
    }

    private sealed record QueueNavigation(long Version, string[] History, string[] Failed, bool Loaded);

    private sealed record Pending(
        MusicLibrarySession Candidate,
        MusicLibraryRevision Before,
        MusicLibraryRevision After,
        string Code,
        PendingOwner Owner,
        RecommendationCommit? Recommendation = null,
        IReadOnlyDictionary<(string SongId, string Platform), long>? Observations = null,
        QueueNavigation? BeforeQueue = null,
        QueueNavigation? AfterQueue = null);

    public MusicLibraryOwner(MusicLibraryDatabase store, IStorageDatabase cacheDatabase)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cacheDatabase);
        this.store = store;
        scope = "local";
        PresentationCache = new(cacheDatabase);
    }

    private async Task EnterAsync(CancellationToken token) =>
        MusicRules.Require(await gate.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false), "TIMEOUT");

    private async Task LoadAsync(CancellationToken token, bool force = false)
    {
        if (session is null || force)
        {
            var savedRevision = await store.ReadRevisionAsync(scope, token).ConfigureAwait(false);
            var candidate = session is not null && savedRevision == revision
                ? session
                : new MusicLibrarySession(store, scope, savedRevision);
            if (force && (pending ?? recommendationUnknown) is { } attempt &&
                !(attempt.Owner == PendingOwner.SongMetadata && attempt.Code == "STORAGE_CONFLICT"))
                MusicRules.Require(savedRevision == attempt.Before || savedRevision == attempt.After, "STORAGE_CONFLICT");
            if (!ReferenceEquals(session, candidate))
            {
                if (session?.IsQueueLoaded == true && queueLoaded)
                    queueLoaded = session.ReadQueue() is { } prevQueue && prevQueue.ContentEquals(candidate.ReadQueue());
                recommendationCheckpointsLoaded = false;
            }
            lock (repositoryGate)
            {
                session = candidate;
                revision = savedRevision;
                visibleSongs = new MusicSongSession(store, cacheReads: false);
                songPublication++;
            }
            if (officialSourcesLease is not null)
                try { RefreshOfficialSources(candidate); }
                catch (MusicLibraryException) { InvalidateOfficialSources(); }
        }
    }

    private bool IsAutoReconcilablePending(Pending attempt) =>
        recommendationUnknown is null && attempt.Code != "STORAGE_CONFLICT" &&
        (attempt.Owner == PendingOwner.SongMetadata ||
         attempt.Owner == PendingOwner.Recommendation && attempt.Recommendation is null);

    private async Task AdmitReadLockedAsync(PendingOwner? authoritativeFor, CancellationToken token)
    {
        var attempt = pending ?? recommendationUnknown;
        if (attempt is null) return;
        if (recommendationUnknown is null && attempt.Owner == PendingOwner.SongMetadata)
        {
            await ReconcileLockedAsync(token).ConfigureAwait(false);
            return;
        }
        if (attempt.Code == "STORAGE_CONFLICT") throw new MusicLibraryException("STORAGE_CONFLICT");
        if (IsAutoReconcilablePending(attempt) ||
            recommendationUnknown is null && authoritativeFor is { } expected && attempt.Owner == expected)
        {
            await ReconcileLockedAsync(token).ConfigureAwait(false);
            return;
        }
        throw new MusicLibraryException("PENDING_WRITE");
    }

    private async Task AdmitWriteLockedAsync(CancellationToken token)
    {
        var attempt = pending ?? recommendationUnknown;
        if (attempt is null) return;
        if (attempt.Code == "STORAGE_CONFLICT") throw new MusicLibraryException("STORAGE_CONFLICT");
        if (IsAutoReconcilablePending(attempt))
        {
            await ReconcileLockedAsync(token).ConfigureAwait(false);
            return;
        }
        throw new MusicLibraryException("PENDING_WRITE");
    }

    private async Task<T> RunLibraryReadAsync<T>(
        Func<MusicLibrarySession, T> read,
        CancellationToken token,
        PendingOwner? authoritativeFor = null)
    {
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            recommendationStage = null;
            MusicRules.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitReadLockedAsync(authoritativeFor, token).ConfigureAwait(false);
            var candidate = session!.Candidate(false);
            var result = read(candidate);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            recommendationStage = null;
            gate.Release();
        }
    }

    private async Task<T> RunLibraryWriteAsync<T>(
        PendingOwner owner,
        Func<MusicLibrarySession, (T Result, bool Mutate)> write,
        CancellationToken token,
        bool mutatesQueue = false,
        Action? onCommitted = null)
    {
        bool entered = false, captured = false, success = false, previousQueueLoaded = false;
        long previousVersion = 0; List<string>? previousHistory = null; string[] previousFailures = [];
        try
        {
            await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
            recommendationStage = null;
            MusicRules.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitWriteLockedAsync(token).ConfigureAwait(false);
            captured = true; previousQueueLoaded = queueLoaded; previousVersion = queueVersion;
            previousHistory = shuffleHistory.ToList(); previousFailures = failedEntries.ToArray();
            var candidate = session!.Candidate(true);
            var (result, mutate) = write(candidate);
            if (mutate)
            {
                await CommitAsync(candidate, owner, token, beforeQueue: mutatesQueue
                    ? new QueueNavigation(previousVersion, previousHistory!.ToArray(), previousFailures, previousQueueLoaded)
                    : null).ConfigureAwait(false);
                onCommitted?.Invoke();
            }
            if (mutate && recommendationStage is { } recommendation)
                AcceptRecommendation(recommendation, true);
            // Do not turn a known successful durable mutation into an ambiguous cancellation.
            if (!mutate) token.ThrowIfCancellationRequested();
            success = true;
            return result;
        }
        finally
        {
            // A rejected command cannot change shuffle navigation/version. An admitted
            // unknown CAS deliberately retains its candidate until explicit reconciliation.
            if (captured && !success && pending is null && previousHistory is not null)
            {
                queueLoaded = previousQueueLoaded; queueVersion = previousVersion;
                shuffleHistory = previousHistory; failedEntries.Clear(); failedEntries.UnionWith(previousFailures);
            }
            if (entered) { recommendationStage = null; gate.Release(); }
        }
    }

    private async Task CommitAsync(MusicLibrarySession candidate, PendingOwner owner, CancellationToken token,
        IReadOnlyDictionary<(string SongId, string Platform), long>? observations = null, QueueNavigation? beforeQueue = null)
    {
        token.ThrowIfCancellationRequested();
        MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        var commit = candidate.BuildCommit();
        if (commit.IsEmpty) { AcceptSongObservations(observations); return; }
        var expected = new MusicLibraryRevision(checked(revision.Generation + 1));
        bool artworkPolicyChanged = commit.MetadataPolicy is not null;
        // A physical commit may precede its receipt. Until it settles, readers
        // retain only the previous versions of the affected records.
        lock (repositoryGate) visibleSongs = new MusicSongSession(store, candidate.Songs.BeforeChanges, cacheReads: false);
        try
        {
            // Transaction admission is final. Cancellation cannot imply rollback.
            var result = await store.CommitAsync(scope, revision, commit, CancellationToken.None).ConfigureAwait(false);
            MusicRules.Require(result == expected, "COMMIT_UNKNOWN");
            lock (repositoryGate)
            {
                session = candidate.Publish(result, commit);
                revision = result;
                visibleSongs = new MusicSongSession(store, cacheReads: false);
                songPublication++;
            }
            if (owner == PendingOwner.OfficialSources) RefreshOfficialSources(candidate);
        }
        catch (Exception error) when (error is not StorageException { Error: StorageError.Conflict } &&
            error is not TransactionCommitUnknownException &&
            error is not MusicLibraryException { Code: "COMMIT_UNKNOWN" })
        {
            // Failed before SQLite Commit() was invoked: transaction rolled back deterministically.
            lock (repositoryGate) visibleSongs = new MusicSongSession(store, cacheReads: false);
            throw;
        }
        catch (Exception error)
        {
            string code = error is StorageException { Error: StorageError.Conflict } ? "STORAGE_CONFLICT" : "COMMIT_UNKNOWN";
            pending = new Pending(candidate, revision, expected, code, owner, recommendationStage, observations, beforeQueue,
                beforeQueue is null ? null : new QueueNavigation(queueVersion, shuffleHistory.ToArray(), failedEntries.ToArray(), queueLoaded));
            if (recommendationStage is not null) recommendationUnknown = pending;
            InvalidateOfficialSources();
            throw new MusicLibraryException(code);
        }
        AcceptSongObservations(observations);
        if (artworkPolicyChanged) { failedArtwork.Clear(); Volatile.Write(ref failedArtworkCount, 0); }
        if (commit.Songs.Count > 0) NotifySongMetadataChanged(commit.Songs.Select(s => s.Record.SongId).ToHashSet(StringComparer.Ordinal));
        if (owner == PendingOwner.Library && commit.Library is not null) NotifyLocalCollectionsChanged();
    }

    private void NotifyLocalCollectionsChanged()
    {
        foreach (Action listener in LocalCollectionsChanged?.GetInvocationList() ?? [])
        {
            try { listener(); }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicLibrary, "collections", "local_collections_listener_failed",
                    new { error = error.Message });
            }
        }
    }

    private async Task ReconcileLockedAsync(CancellationToken token)
    {
        var previous = session; var previousRevision = revision; var previousSongs = visibleSongs;
        var attempt = pending ?? recommendationUnknown;
        bool previousQueueLoaded = queueLoaded;
        long previousVersion = queueVersion; var previousHistory = shuffleHistory.ToList(); var failures = failedEntries.ToArray();
        try
        {
            await LoadAsync(token, true).ConfigureAwait(false);
            if (attempt is { } p && !(p.Owner == PendingOwner.SongMetadata && p.Code == "STORAGE_CONFLICT"))
                MusicRules.Require(revision == p.Before || revision == p.After, "STORAGE_CONFLICT");
            if (attempt is { BeforeQueue: not null, AfterQueue: not null })
            {
                var navigation = revision == attempt.After ? attempt.AfterQueue : attempt.BeforeQueue;
                queueLoaded = navigation.Loaded; queueVersion = navigation.Version; shuffleHistory = navigation.History.ToList();
                failedEntries.Clear(); failedEntries.UnionWith(navigation.Failed);
            }
            SettleRecommendation(attempt);
            pending = null;
        }
        catch
        {
            lock (repositoryGate) { session = previous; revision = previousRevision; visibleSongs = previousSongs; }
            queueLoaded = previousQueueLoaded; queueVersion = previousVersion; shuffleHistory = previousHistory;
            failedEntries.Clear(); failedEntries.UnionWith(failures);
            InvalidateOfficialSources();
            throw;
        }
        if (attempt is not null && revision == attempt.After) AcceptSongObservations(attempt.Observations);
        if (revision != previousRevision && (attempt is null || attempt.Owner == PendingOwner.Metadata))
        {
            failedArtwork.Clear();
            Volatile.Write(ref failedArtworkCount, 0);
        }
        if (revision != previousRevision)
        {
            var ids = attempt is null ? store.SongIds().ToHashSet(StringComparer.Ordinal) :
                attempt.Candidate.Songs.Changes.Keys.ToHashSet(StringComparer.Ordinal);
            if (ids.Count > 0) NotifySongMetadataChanged(ids);
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { MusicRules.Require(!closed, "CLOSED"); await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed, "CLOSED");
            if (pending is { } attempt && IsAutoReconcilablePending(attempt))
                await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
            MusicRules.Require(pending is null && recommendationUnknown is null, pending?.Code ?? "PENDING_WRITE");
            await SealSongSnapshots().ConfigureAwait(false);
            lock (repositoryGate) preparing = true;
            ClearRecommendations(); InvalidateOfficialSources(); RevokeQualityLeases();
        }
        finally { gate.Release(); }
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (repositoryGate) task = abortion ??= AbortCoreAsync();
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task AbortCoreAsync()
    {
        await EnterAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await SealSongSnapshots().ConfigureAwait(false);
            lock (repositoryGate) closed = true;
            ClearRecommendations(); InvalidateOfficialSources(); RevokeQualityLeases();
        }
        finally { gate.Release(); }
        await PresentationCache.DisposeAsync().ConfigureAwait(false);
        gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!closed) await PrepareShutdownAsync().ConfigureAwait(false); }
        finally { await AbortAsync().ConfigureAwait(false); }
    }
}

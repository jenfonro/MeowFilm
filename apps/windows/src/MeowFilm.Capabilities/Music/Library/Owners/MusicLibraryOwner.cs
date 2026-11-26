using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Core.Debug;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Native authority for local music state and its one transactional record store.</summary>
public sealed partial class MusicLibraryOwner : IAsyncDisposable
{
    public const string ContractVersion = "0.7.0";
    internal event Action? LocalCollectionsChanged;
    internal event Action<MusicAppearanceRecord>? AppearanceChanged;
    internal event Action<MusicCatalogSourceRecord>? CatalogSourceChanged;
    internal event Action<MusicDesktopLyricsRecord>? DesktopLyricsChanged;
    internal event Action<MusicMetadataPolicyRecord>? MetadataPolicyChanged;
    internal event Action<MusicOfficialSourcesRecord>? OfficialSourcesChanged;
    internal event Action<MusicQualitySettingsResult>? QualityPolicyChanged;
    internal event Action<MusicSearchPolicyRecord>? SearchPolicyChanged;
    internal event Action? SourceExclusionsChanged;
    internal event Action? LocalLyricsChanged;

    private readonly MusicLibraryDatabase store;
    private readonly string scope;
    internal MusicPresentationCache PresentationCache { get; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private MusicLibrarySession? session;
    private MusicLibraryRevision revision = MusicLibraryRevision.Empty;
    private MusicSongSession? visibleSongs;
    private Task? abortion;
    private bool preparing, closed;
    private long queueVersion, serial;
    private List<string> shuffleHistory = [];

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

    private async Task LoadAsync(CancellationToken token)
    {
        if (session is null)
        {
            var savedRevision = await store.ReadRevisionAsync(scope, token).ConfigureAwait(false);
            var candidate = new MusicLibrarySession(store, scope, savedRevision);
            recommendationCheckpointsLoaded = false;
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

    private async Task<T> RunLibraryReadAsync<T>(Func<MusicLibrarySession, T> read, CancellationToken token)
    {
        await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            var result = read(session!.Candidate(false));
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<T> RunLibraryWriteAsync<T>(
        Func<MusicLibrarySession, (T Result, bool Mutate)> write,
        CancellationToken token,
        bool mutatesQueue = false,
        Action? onCommitted = null,
        bool refreshOfficialSources = false,
        bool notifyCollections = false)
    {
        bool entered = false, captured = false, success = false, previousQueueLoaded = false;
        long previousVersion = 0; List<string>? previousHistory = null; string[] previousFailures = [];
        bool committedCollections = false;
        try
        {
            await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
            MusicRules.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            if (mutatesQueue)
            {
                captured = true; previousQueueLoaded = queueLoaded; previousVersion = queueVersion;
                previousHistory = shuffleHistory.ToList(); previousFailures = failedEntries.ToArray();
            }
            var candidate = session!.Candidate(true);
            var (result, mutate) = write(candidate);
            if (mutate)
            {
                await CommitAsync(candidate, refreshOfficialSources: refreshOfficialSources).ConfigureAwait(false);
                onCommitted?.Invoke();
                committedCollections = notifyCollections;
            }
            else
            {
                token.ThrowIfCancellationRequested();
            }
            success = true;
            return result;
        }
        finally
        {
            if (captured && !success && previousHistory is not null)
            {
                queueLoaded = previousQueueLoaded; queueVersion = previousVersion;
                shuffleHistory = previousHistory; failedEntries.Clear(); failedEntries.UnionWith(previousFailures);
            }
            if (entered) gate.Release();
            if (committedCollections) NotifyLocalCollectionsChanged();
        }
    }

    private async Task CommitAsync(MusicLibrarySession candidate,
        IReadOnlyDictionary<(string SongId, string Platform), long>? observations = null,
        bool refreshOfficialSources = false)
    {
        var commit = candidate.BuildCommit();
        if (commit.IsEmpty) { AcceptSongObservations(observations); return; }
        bool artworkPolicyChanged = commit.MetadataPolicy is not null;
        lock (repositoryGate) visibleSongs = new MusicSongSession(store, candidate.Songs.BeforeChanges, cacheReads: false);
        try
        {
            var result = await store.CommitAsync(scope, revision, commit, CancellationToken.None).ConfigureAwait(false);
            lock (repositoryGate)
            {
                session = candidate.Publish(result, commit);
                revision = result;
                visibleSongs = new MusicSongSession(store, cacheReads: false);
                songPublication++;
            }
            if (refreshOfficialSources) RefreshOfficialSources(candidate);
        }
        catch (StorageException error) when (error.Error == StorageError.Conflict)
        {
            lock (repositoryGate)
            {
                session = null;
                visibleSongs = new MusicSongSession(store, cacheReads: false);
            }
            InvalidateOfficialSources();
            throw new MusicLibraryException("STORAGE_CONFLICT");
        }
        catch
        {
            lock (repositoryGate)
            {
                session = null;
                visibleSongs = new MusicSongSession(store, cacheReads: false);
            }
            throw;
        }
        AcceptSongObservations(observations);
        if (artworkPolicyChanged) { failedArtwork.Clear(); Volatile.Write(ref failedArtworkCount, 0); }
        if (commit.Songs.Count > 0) NotifySongMetadataChanged(commit.Songs.Select(s => s.Record.SongId).ToHashSet(StringComparer.Ordinal));
    }

    private void NotifyLocalCollectionsChanged() => InvokeSafe(LocalCollectionsChanged);

    private static void InvokeSafe(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action listener in handlers.GetInvocationList())
        {
            try { listener(); }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicLibrary, "owner", "listener_failed", new { error = error.Message });
            }
        }
    }

    private static void InvokeSafe<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> listener in handlers.GetInvocationList())
        {
            try { listener(value); }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicLibrary, "owner", "listener_failed", new { error = error.Message });
            }
        }
    }

    public async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed, "CLOSED");
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

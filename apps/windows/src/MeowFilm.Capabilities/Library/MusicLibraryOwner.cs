using System.Text.Json;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Native authority for local music state and its one transactional record store.</summary>
public sealed partial class MusicLibraryOwner : IAsyncDisposable
{
    public const string ContractVersion = "0.7.0";
    private readonly IMusicLibraryStore store;
    private readonly string scope;
    internal MusicPresentationCache PresentationCache { get; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private MusicLibrarySession? session;
    private MusicLibraryRevision revision = MusicLibraryRevision.Empty;
    private MusicSongSession? visibleSongs;
    private Pending? pending;
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
        OnlineSettings,
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

    public MusicLibraryOwner(IMusicLibraryStore store, IStorageDatabase cacheDatabase) : this(store, "local", cacheDatabase) { }

    public MusicLibraryOwner(IMusicLibraryStore store, string scope, IStorageDatabase cacheDatabase)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cacheDatabase);
        MusicRules.Require(MusicRules.IsValidId(scope));
        this.store = store;
        this.scope = scope;
        PresentationCache = new(cacheDatabase);
    }

    public Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        method.StartsWith("music.lyrics.display.", StringComparison.Ordinal)
            ? ExecuteLyricDisplayAsync(method, parameters, cancellationToken)
            : method is "music.library.network.save" or "music.library.network.remove"
            ? ExecuteNetworkPlaylistMutationAsync(method, parameters, cancellationToken)
            : ExecuteCoreAsync(method, parameters, cancellationToken, method == "music.queue.recommendation.enter");

    public Task<JsonElement> CreatePlaylistAsync(string name, IReadOnlyList<JsonElement> songs, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync("music.library.create", new { name, songs }, cancellationToken, true);

    public Task<JsonElement> ReplaceQueueAsync(IReadOnlyList<JsonElement> songs, int index, JsonElement? context,
        long expectedVersion, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync("music.queue.replace", new { songs, index, context, expectedVersion }, cancellationToken, true);

    public Task<JsonElement> AppendQueueAsync(IReadOnlyList<JsonElement> songs, JsonElement? context,
        long expectedVersion, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync("music.queue.append", new { songs, context, next = true, expectedVersion }, cancellationToken, true);

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
            if (force && (pending ?? recommendationUnknown) is { } attempt)
                MusicRules.Require(savedRevision == attempt.Before || savedRevision == attempt.After, "STORAGE_CONFLICT");
            if (!ReferenceEquals(session, candidate) && session?.IsQueueLoaded == true && queueLoaded)
                queueLoaded = session.ReadQueue() is { } prevQueue && prevQueue.ContentEquals(candidate.ReadQueue());
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

    private static PendingOwner CommandOwner(string method)
    {
        if (method.StartsWith("music.queue.recommendation.", StringComparison.Ordinal)) return PendingOwner.Recommendation;
        if (method.StartsWith("music.library.", StringComparison.Ordinal)) return PendingOwner.Library;
        if (method.StartsWith("music.queue.", StringComparison.Ordinal)) return PendingOwner.Queue;
        if (method.StartsWith("music.history.", StringComparison.Ordinal)) return PendingOwner.History;
        if (method is "music.settings.save") return PendingOwner.OnlineSettings;
        if (method is "music.catalogSource.settings.save") return PendingOwner.CatalogSource;
        if (method is "music.officialSources.settings.save") return PendingOwner.OfficialSources;
        if (method is "music.quality.settings.save") return PendingOwner.Quality;
        if (method is "music.metadata.settings.save") return PendingOwner.Metadata;
        if (method is "music.appearance.save") return PendingOwner.Appearance;
        if (method is "music.device.settings.save") return PendingOwner.DeviceSettings;
        if (method is "music.lyrics.desktop.settings.save") return PendingOwner.DesktopLyrics;
        if (method is "music.sourceExclusions.add" or "music.sourceExclusions.remove") return PendingOwner.SourceExclusions;
        if (method is "music.search.settings.save") return PendingOwner.SearchPolicy;
        if (method is "music.search.history.record" or "music.search.history.clear") return PendingOwner.SearchHistory;
        if (method is "music.lyrics.local.save" or "music.lyrics.local.remove") return PendingOwner.LocalLyrics;
        throw new MusicLibraryException("INVALID_REQUEST");
    }

    private static bool IsAuthoritativeRead(string method, PendingOwner owner) => owner switch
    {
        PendingOwner.Appearance => method == "music.appearance.get",
        PendingOwner.CatalogSource => method == "music.catalogSource.settings.get",
        PendingOwner.DesktopLyrics => method == "music.lyrics.desktop.settings.get",
        PendingOwner.Metadata => method == "music.metadata.settings.get",
        PendingOwner.Quality => method == "music.quality.settings.get",
        PendingOwner.OfficialSources => method == "music.officialSources.settings.get",
        PendingOwner.Queue => method == "music.queue.get",
        PendingOwner.SearchPolicy => method == "music.search.settings.get",
        _ => false
    };

    private async Task AdmitCommandLockedAsync(string method, CancellationToken token)
    {
        var attempt = pending ?? recommendationUnknown;
        if (attempt is null) return;
        if (method == "music.queue.recommendation.reconcile" && recommendationUnknown is not null) return;
        if (attempt.Code == "STORAGE_CONFLICT") throw new MusicLibraryException("STORAGE_CONFLICT");
        if (recommendationUnknown is null && IsAuthoritativeRead(method, attempt.Owner))
        {
            await ReconcileLockedAsync(token).ConfigureAwait(false);
            return;
        }
        throw new MusicLibraryException("PENDING_WRITE");
    }

    private async Task CommitAsync(MusicLibrarySession candidate, PendingOwner owner, CancellationToken token,
        IReadOnlyDictionary<(string SongId, string Platform), long>? observations = null, QueueNavigation? beforeQueue = null)
    {
        token.ThrowIfCancellationRequested();
        MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        var commit = candidate.BuildCommit();
        if (commit.IsEmpty) { AcceptSongObservations(observations); return; }
        ValidateChangedSongs(candidate);
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
        if (artworkPolicyChanged) failedArtwork.Clear();
        if (commit.Songs.Count > 0) NotifySongMetadataChanged(commit.Songs.Select(s => s.Record.SongId).ToHashSet(StringComparer.Ordinal));
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
            if (attempt is { } p)
                MusicRules.Require(revision == p.Before || revision == p.After, "STORAGE_CONFLICT");
            SettleRecommendation(attempt);
            if (attempt is { BeforeQueue: not null, AfterQueue: not null })
            {
                var navigation = revision == attempt.After ? attempt.AfterQueue : attempt.BeforeQueue;
                queueLoaded = navigation.Loaded; queueVersion = navigation.Version; shuffleHistory = navigation.History.ToList();
                failedEntries.Clear(); failedEntries.UnionWith(navigation.Failed);
            }
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
        if (previous is not null && previous.IsMetadataPolicyLoaded && revision != previousRevision)
            failedArtwork.Clear();
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

    private async Task<JsonElement> ExecuteCoreAsync(string method, object? parameters, CancellationToken token, bool full)
    {
        bool entered = false, captured = false, success = false, previousQueueLoaded = false;
        long previousVersion = 0; List<string>? previousHistory = null; string[] previousFailures = [];
        try
        {
            JsonElement p;
            try { p = MusicWireCodec.ParseParameters(parameters); }
            catch (MusicLibraryException error) when (error.Code == "CORRUPT_STORAGE" &&
                (method is "music.officialSources.settings.get" or "music.officialSources.settings.save" or
                    "music.catalogSource.settings.get" or "music.catalogSource.settings.save"))
            { throw new MusicLibraryException("INVALID_REQUEST"); }

            await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
            recommendationStage = null;
            MusicRules.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            if (method == "music.lyrics.parse")
            {
                MusicWireCodec.RequireFields(p, "text translation duration");
                return MusicLyricTiming.Parse(
                    MusicWireCodec.Str(p, "text"),
                    MusicWireCodec.Str(p, "translation"),
                    MusicWireCodec.NumProp(p, "duration"));
            }
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitCommandLockedAsync(method, token).ConfigureAwait(false);
            captured = true; previousQueueLoaded = queueLoaded; previousVersion = queueVersion;
            previousHistory = shuffleHistory.ToList(); previousFailures = failedEntries.ToArray();
            var candidate = session!.Candidate(!IsReadCommand(method));
            var (response, mutate) = await DispatchAsync(method, p, candidate, token, full).ConfigureAwait(false);
            if (mutate)
            {
                var owner = CommandOwner(method);
                await CommitAsync(candidate, owner, token, beforeQueue: owner == PendingOwner.Queue
                    ? new QueueNavigation(previousVersion, previousHistory!.ToArray(), previousFailures, previousQueueLoaded) : null).ConfigureAwait(false);
            }
            if (mutate && recommendationStage is { } recommendation)
                AcceptRecommendation(recommendation, true);
            // Do not turn a known successful durable mutation into an ambiguous cancellation.
            if (!mutate) token.ThrowIfCancellationRequested();
            success = true;
            return response;
        }
        catch (OperationCanceledException) { return MusicWireCodec.Fail("CANCELLED"); }
        catch (MusicLibraryException error) { return MusicWireCodec.Fail(error.Code); }
        catch (StorageException error)
        {
            string code = error.Error switch
            {
                StorageError.Unavailable => "STORAGE_UNAVAILABLE",
                StorageError.Conflict => "STORAGE_CONFLICT",
                StorageError.Corrupt or StorageError.UnsupportedVersion => "CORRUPT_STORAGE",
                _ => "STORAGE"
            };
            return MusicWireCodec.Fail(code);
        }
        catch { return MusicWireCodec.Fail("INVALID_REQUEST"); }
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

    private static bool IsReadCommand(string method) => method.EndsWith(".get", StringComparison.Ordinal) ||
        method.EndsWith(".list", StringComparison.Ordinal) || method.EndsWith(".detail", StringComparison.Ordinal) ||
        method is "music.library.membership" or "music.library.search" or "music.queue.peekNext";

    public async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed, "CLOSED");
            MusicRules.Require(pending is null && recommendationUnknown is null, pending?.Code ?? "PENDING_WRITE");
            await SealSongSnapshots().ConfigureAwait(false);
            lock (repositoryGate) preparing = true;
            ClearRecommendations(); InvalidateOfficialSources();
        }
        finally { gate.Release(); }
    }

    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await SealSongSnapshots().ConfigureAwait(false);
            lock (repositoryGate) closed = true;
            ClearRecommendations(); InvalidateOfficialSources();
        }
        finally { gate.Release(); }
        await PresentationCache.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    { if (!closed) await PrepareShutdownAsync().ConfigureAwait(false); await AbortAsync().ConfigureAwait(false); }
}

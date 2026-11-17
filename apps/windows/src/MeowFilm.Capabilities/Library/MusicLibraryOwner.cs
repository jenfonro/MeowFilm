using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities.Library;

/// <summary>Native authority for local music state and its one transactional record store.</summary>
public sealed partial class MusicLibraryOwner : IAsyncDisposable
{
    public const string ContractVersion = "0.7.0";
    private readonly IMusicLibraryStore store;
    private readonly string scope;
    private readonly CacheDocumentStore displayCache;
    internal MusicPresentationCache PresentationCache { get; }
    private readonly bool ownsDisplayCache;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MusicLibraryData? document;
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
    private sealed record QueueNavigation(long Version, string[] History, string[] Failed);
    private sealed record Pending(MusicLibraryData Candidate, MusicLibraryRevision Before, MusicLibraryRevision After, string Code,
        PendingOwner Owner, RecommendationCommit? Recommendation = null,
        IReadOnlyDictionary<(string SongId, string Platform), long>? Observations = null,
        QueueNavigation? BeforeQueue = null, QueueNavigation? AfterQueue = null);

    public MusicLibraryOwner(IMusicLibraryStore store, IDocumentStore? cacheStore = null) : this(store, "local", cacheStore) { }
    public MusicLibraryOwner(IMusicLibraryStore store, string scope, IDocumentStore? cacheStore = null)
    {
        ArgumentNullException.ThrowIfNull(store); LJ.Require(LJ.Id(scope)); this.store = store; this.scope = scope;
        ownsDisplayCache = cacheStore is not CacheDocumentStore;
        displayCache = cacheStore as CacheDocumentStore ?? (cacheStore is null ? new() : new(cacheStore));
        PresentationCache = new(displayCache);
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
    private static JsonNode? Value(MusicLibraryData doc, string area) => doc.Value(area);
    private static bool HasValue(MusicLibraryData doc, string area) => doc.HasValue(area);
    private static void Set(MusicLibraryData doc, string area, JsonNode value) => doc.Set(area, LJ.Object(value));
    private async Task EnterAsync(CancellationToken token) =>
        LJ.Require(await gate.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false), "TIMEOUT");
    private async Task LoadAsync(CancellationToken token, bool force = false)
    {
        if (document is null || force)
        {
            var saved = await store.ReadAsync(scope, token).ConfigureAwait(false);
            var candidate = new MusicLibraryData(store, saved.Areas);
            ValidateDocument(candidate, all: true);
            if (force && (pending ?? recommendationUnknown) is { } attempt)
                LJ.Require(saved.Revision == attempt.Before || saved.Revision == attempt.After, "STORAGE_CONFLICT");
            if (document is null)
            {
                if (Value(candidate, "queue") is JsonObject q) RestoreRuntime(q, false);
            }
            else if (!JsonNode.DeepEquals(Queue(document), Queue(candidate)))
                RestoreRuntime(Queue(candidate), false);
            lock (repositoryGate)
            {
                document = candidate; revision = saved.Revision;
                visibleSongs = new(store, cacheReads: false);
            }
            if (pending is null && recommendationUnknown is null) RefreshOfficialSources(candidate);
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
    private async Task CommitAsync(MusicLibraryData candidate, PendingOwner owner, CancellationToken token,
        IReadOnlyDictionary<(string SongId, string Platform), long>? observations = null, QueueNavigation? beforeQueue = null)
    {
        token.ThrowIfCancellationRequested();
        LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        var changes = candidate.Changes();
        if (changes.Areas.Count == 0 && changes.Songs.Count == 0) { AcceptSongObservations(observations); return; }
        ValidateDocument(candidate);
        foreach (var area in changes.Areas.Values)
            LJ.Require(LJ.Bytes(area).Length <= 8 * 1024 * 1024, "LIMIT");
        var expected = new MusicLibraryRevision(checked(revision.Generation + 1), Guid.NewGuid().ToString("D"));
        bool artworkPolicyChanged = document is not null && !JsonNode.DeepEquals(MetadataPolicy(document), MetadataPolicy(candidate));
        // A physical commit may precede its receipt. Until it settles, readers
        // retain only the previous versions of the affected records.
        lock (repositoryGate) visibleSongs = new(store, candidate.Songs.BeforeChanges, cacheReads: false);
        try
        {
            // Transaction admission is final. Cancellation cannot imply rollback.
            var result = await store.CommitAsync(scope, revision, expected, changes, CancellationToken.None).ConfigureAwait(false);
            LJ.Require(result == expected, "COMMIT_UNKNOWN");
            lock (repositoryGate)
            {
                document = new(store, candidate.PublishedAreas()); revision = result;
                visibleSongs = new(store, cacheReads: false);
            }
            if (owner == PendingOwner.OfficialSources) RefreshOfficialSources(candidate);
        }
        catch (Exception error)
        {
            string code = error is DocumentStoreException { Error: DocumentStoreError.Conflict } ? "STORAGE_CONFLICT" : "COMMIT_UNKNOWN";
            pending = new(candidate, revision, expected, code, owner, recommendationStage, observations, beforeQueue,
                beforeQueue is null ? null : new(queueVersion, shuffleHistory.ToArray(), failedEntries.ToArray()));
            if (recommendationStage is not null) recommendationUnknown = pending;
            InvalidateOfficialSources();
            throw new MusicLibraryException(code);
        }
        AcceptSongObservations(observations);
        if (artworkPolicyChanged) failedArtwork.Clear();
        if (changes.Songs.Count > 0) NotifySongMetadataChanged(changes.Songs.Keys.ToHashSet(StringComparer.Ordinal));
    }
    private async Task ReconcileLockedAsync(CancellationToken token)
    {
        var previous = document; var previousRevision = revision; var previousSongs = visibleSongs;
        var attempt = pending ?? recommendationUnknown;
        long previousVersion = queueVersion; var previousHistory = shuffleHistory.ToList(); var failures = failedEntries.ToArray();
        try
        {
            await LoadAsync(token, true).ConfigureAwait(false);
            if (attempt is { } p)
                LJ.Require(revision == p.Before || revision == p.After, "STORAGE_CONFLICT");
            SettleRecommendation(attempt);
            if (attempt is { BeforeQueue: not null, AfterQueue: not null })
            {
                var navigation = revision == attempt.After ? attempt.AfterQueue : attempt.BeforeQueue;
                queueVersion = navigation.Version; shuffleHistory = navigation.History.ToList();
                failedEntries.Clear(); failedEntries.UnionWith(navigation.Failed);
            }
            pending = null;
            RefreshOfficialSources(document!);
        }
        catch
        {
            lock (repositoryGate) { document = previous; revision = previousRevision; visibleSongs = previousSongs; }
            queueVersion = previousVersion; shuffleHistory = previousHistory;
            failedEntries.Clear(); failedEntries.UnionWith(failures);
            InvalidateOfficialSources();
            throw;
        }
        if (attempt is not null && revision == attempt.After) AcceptSongObservations(attempt.Observations);
        if (previous is not null && !JsonNode.DeepEquals(MetadataPolicy(previous), MetadataPolicy(document!)))
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
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { LJ.Require(!closed, "CLOSED"); await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task<JsonElement> ExecuteCoreAsync(string method, object? parameters, CancellationToken token, bool full)
    {
        bool entered = false, captured = false, success = false; long previousVersion = 0; List<string>? previousHistory = null; string[] previousFailures = [];
        try
        {
            JsonObject p;
            try { p = LJ.Parameters(parameters, full ? 16 * 1024 * 1024 : 512 * 1024); }
            catch (MusicLibraryException error) when (error.Code == "CORRUPT_STORAGE" &&
                (method is "music.officialSources.settings.get" or "music.officialSources.settings.save" or
                    "music.catalogSource.settings.get" or "music.catalogSource.settings.save"))
            { throw new MusicLibraryException("INVALID_REQUEST"); }
            // Load shared facts before entering the library gate: persistence acquires them
            // in the other direction while reading local membership.
            await EnterAsync(token).ConfigureAwait(false); entered = true;
            recommendationStage = null;
            LJ.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            if (method == "music.lyrics.parse")
            {
                LJ.Fields(p, "text translation duration", "text translation duration");
                return MusicLyricTiming.Parse(LJ.S(p["text"]), LJ.S(p["translation"]), LJ.N(p["duration"]));
            }
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitCommandLockedAsync(method, token).ConfigureAwait(false);
            captured = true; previousVersion = queueVersion; previousHistory = shuffleHistory.ToList(); previousFailures = failedEntries.ToArray();
            var candidate = document!.Candidate(!IsReadCommand(method));
            var outcome = await DispatchAsync(method, p, candidate, token, full).ConfigureAwait(false);
            var response = LJ.Merge(LJ.Obj(("ok", true)), outcome.Result);
            LJ.Require(LJ.Bytes(response).Length <= 512 * 1024, "LIMIT");
            if (outcome.Mutate)
            {
                var owner = CommandOwner(method);
                await CommitAsync(candidate, owner, token, beforeQueue: owner == PendingOwner.Queue
                    ? new(previousVersion, previousHistory!.ToArray(), previousFailures) : null).ConfigureAwait(false);
            }
            if (outcome.Mutate && recommendationStage is { } recommendation)
                AcceptRecommendation(recommendation, true);
            // Do not turn a known successful durable mutation into an ambiguous cancellation.
            if (!outcome.Mutate) token.ThrowIfCancellationRequested();
            success = true;
            return LJ.Element(response);
        }
        catch (OperationCanceledException) { return LJ.Element(LJ.Obj(("ok", false), ("code", "CANCELLED"))); }
        catch (MusicLibraryException error) { return LJ.Element(LJ.Obj(("ok", false), ("code", error.Code))); }
        catch (DocumentStoreException error)
        {
            string code = error.Error switch
            {
                DocumentStoreError.Unavailable => "STORAGE_UNAVAILABLE",
                DocumentStoreError.Corrupt or DocumentStoreError.UnsupportedVersion => "CORRUPT_STORAGE",
                _ => "STORAGE"
            };
            return LJ.Element(LJ.Obj(("ok", false), ("code", code)));
        }
        catch { return LJ.Element(LJ.Obj(("ok", false), ("code", "INVALID_REQUEST"))); }
        finally
        {
            // A rejected command cannot change shuffle navigation/version. An admitted
            // unknown CAS deliberately retains its candidate until explicit reconciliation.
            if (captured && !success && pending is null && previousHistory is not null)
            { queueVersion = previousVersion; shuffleHistory = previousHistory; failedEntries.Clear(); failedEntries.UnionWith(previousFailures); }
            if (entered) { recommendationStage = null; gate.Release(); }
        }
    }
    private static bool IsReadCommand(string method) => method.EndsWith(".get", StringComparison.Ordinal) ||
        method.EndsWith(".list", StringComparison.Ordinal) || method.EndsWith(".detail", StringComparison.Ordinal) ||
        method is "music.library.membership" or "music.library.search" or "music.queue.peekNext";
    public async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { LJ.Require(!closed, "CLOSED"); LJ.Require(pending is null && recommendationUnknown is null, pending?.Code ?? "PENDING_WRITE"); lock (repositoryGate) preparing = true; ClearRecommendations(); InvalidateOfficialSources(); }
        finally { gate.Release(); }
    }
    public async Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { LJ.Require(!closed, "CLOSED"); lock (repositoryGate) preparing = false; } finally { gate.Release(); }
    }
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { lock (repositoryGate) closed = true; ClearRecommendations(); InvalidateOfficialSources(); } finally { gate.Release(); }
        await displayCache.DrainAsync().ConfigureAwait(false);
        if (ownsDisplayCache) await displayCache.DisposeAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    { if (!closed) await PrepareShutdownAsync().ConfigureAwait(false); await AbortAsync().ConfigureAwait(false); }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities.Library;

/// <summary>Native authority for local music state and its one transactional record store.</summary>
public sealed partial class MusicLibraryOwner : IAsyncDisposable
{
    public const string ContractVersion = "0.7.0";
    private readonly MusicLibraryDatabase store;
    private readonly string scope;
    internal MusicPresentationCache PresentationCache { get; }
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
    private sealed record QueueNavigation(long Version, string[] History, string[] Failed, bool Loaded);
    private sealed record Pending(MusicLibraryData Candidate, MusicLibraryRevision Before, MusicLibraryRevision After, string Code,
        PendingOwner Owner, RecommendationCommit? Recommendation = null,
        IReadOnlyDictionary<(string SongId, string Platform), long>? Observations = null,
        QueueNavigation? BeforeQueue = null, QueueNavigation? AfterQueue = null);

    public MusicLibraryOwner(MusicLibraryDatabase store, IStorageDatabase? database = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        scope = "local";
        var shared = database ?? store.Database;
        PresentationCache = new(shared);
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
    private static JsonObject? Area(MusicLibraryData doc, string area) =>
        doc.HasValue(area) ? LJ.Object(doc.Value(area), "CORRUPT_STORAGE") : null;
    private static bool HasValue(MusicLibraryData doc, string area) => doc.HasValue(area);
    private static void Set(MusicLibraryData doc, string area, JsonNode value, bool partial = false) => doc.Set(area, LJ.Object(value), partial);
    private static JsonObject OverwriteSettings(MusicLibraryData doc, string area, long schema, JsonObject defaults)
    {
        var saved = doc.Select(area, "schema", "revision") ?? defaults;
        try { LJ.Require(LJ.I(saved["schema"]) == schema); _ = Revision(saved); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return saved;
    }
    private async Task EnterAsync(CancellationToken token) =>
        LJ.Require(await gate.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false), "TIMEOUT");
    private async Task LoadAsync(CancellationToken token, bool force = false)
    {
        if (document is null || force)
        {
            var saved = await store.ReadAsync(scope, token).ConfigureAwait(false);
            var candidate = document is not null && saved.Revision == revision ? document :
                new MusicLibraryData(store, saved.Areas, scope: scope, revision: saved.Revision);
            if (force && (pending ?? recommendationUnknown) is { } attempt &&
                !(attempt.Owner == PendingOwner.SongMetadata && attempt.Code == "STORAGE_CONFLICT"))
                LJ.Require(saved.Revision == attempt.Before || saved.Revision == attempt.After, "STORAGE_CONFLICT");
            if (!ReferenceEquals(document, candidate) && document?.IsLoaded("queue") == true && queueLoaded)
                queueLoaded = document.Value("queue") is JsonObject queue && candidate.Matches("queue", [], [scope], queue) && queueLoaded;
            lock (repositoryGate)
            {
                document = candidate; revision = saved.Revision;
                visibleSongs = new(store, cacheReads: false);
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
        PendingOwner.SongMetadata => IsReadCommand(method),
        _ => false
    };
    private async Task AdmitCommandLockedAsync(string method, CancellationToken token)
    {
        var attempt = pending ?? recommendationUnknown;
        if (attempt is null) return;
        if (method == "music.queue.recommendation.reconcile" && recommendationUnknown is not null) return;
        if (recommendationUnknown is null && attempt.Owner == PendingOwner.SongMetadata &&
            IsAuthoritativeRead(method, attempt.Owner))
        {
            await ReconcileLockedAsync(token).ConfigureAwait(false);
            return;
        }
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
        if (changes.Areas.Count == 0 && changes.Songs.Count == 0 && changes.Rows.Count == 0) { AcceptSongObservations(observations); return; }
        var expected = new MusicLibraryRevision(checked(revision.Generation + 1), Guid.NewGuid().ToString("D"));
        bool artworkPolicyChanged = changes.Areas.ContainsKey(MetadataPolicyArea);
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
                document = candidate.Publish(result); revision = result;
                visibleSongs = new(store, cacheReads: false);
                songPublication++;
            }
            if (owner == PendingOwner.OfficialSources) RefreshOfficialSources(candidate);
        }
        catch (Exception error) when (error is not DocumentStoreException { Error: DocumentStoreError.Conflict } &&
            error is not TransactionCommitUnknownException &&
            error is not MusicLibraryException { Code: "COMMIT_UNKNOWN" })
        {
            // Failed before SQLite Commit() was invoked: transaction rolled back deterministically.
            lock (repositoryGate) visibleSongs = new(store, cacheReads: false);
            throw;
        }
        catch (Exception error)
        {
            string code = error is DocumentStoreException { Error: DocumentStoreError.Conflict } ? "STORAGE_CONFLICT" : "COMMIT_UNKNOWN";
            pending = new(candidate, revision, expected, code, owner, recommendationStage, observations, beforeQueue,
                beforeQueue is null ? null : new(queueVersion, shuffleHistory.ToArray(), failedEntries.ToArray(), queueLoaded));
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
        bool previousQueueLoaded = queueLoaded;
        long previousVersion = queueVersion; var previousHistory = shuffleHistory.ToList(); var failures = failedEntries.ToArray();
        try
        {
            await LoadAsync(token, true).ConfigureAwait(false);
            if (attempt is { } p && !(p.Owner == PendingOwner.SongMetadata && p.Code == "STORAGE_CONFLICT"))
                LJ.Require(revision == p.Before || revision == p.After, "STORAGE_CONFLICT");
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
            lock (repositoryGate) { document = previous; revision = previousRevision; visibleSongs = previousSongs; }
            queueLoaded = previousQueueLoaded; queueVersion = previousVersion; shuffleHistory = previousHistory;
            failedEntries.Clear(); failedEntries.UnionWith(failures);
            InvalidateOfficialSources();
            throw;
        }
        if (attempt is not null && revision == attempt.After) AcceptSongObservations(attempt.Observations);
        if (revision != previousRevision && (attempt is null || attempt.Owner == PendingOwner.Metadata))
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
        try { LJ.Require(!closed, "CLOSED"); await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task<JsonElement> ExecuteCoreAsync(string method, object? parameters, CancellationToken token, bool full)
    {
        bool entered = false, captured = false, success = false, previousQueueLoaded = false; long previousVersion = 0; List<string>? previousHistory = null; string[] previousFailures = [];
        try
        {
            var p = LJ.Parameters(parameters);
            // Load shared facts before entering the library gate: persistence acquires them
            // in the other direction while reading local membership.
            // A free gate and cached document may complete synchronously. The selected
            // SQLite reads below belong to the command lane, never the caller's UI context.
            await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
            recommendationStage = null;
            LJ.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            if (method == "music.lyrics.parse")
            {
                LJ.Fields(p, "text translation duration");
                return MusicLyricTiming.Parse(LJ.S(p["text"]), LJ.S(p["translation"]), LJ.N(p["duration"]));
            }
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitCommandLockedAsync(method, token).ConfigureAwait(false);
            captured = true; previousQueueLoaded = queueLoaded; previousVersion = queueVersion; previousHistory = shuffleHistory.ToList(); previousFailures = failedEntries.ToArray();
            var candidate = document!.Candidate(!IsReadCommand(method));
            var outcome = await DispatchAsync(method, p, candidate, token, full).ConfigureAwait(false);
            var response = LJ.Merge(LJ.Obj(("ok", true)), outcome.Result);
            if (outcome.Mutate)
            {
                var owner = CommandOwner(method);
                bool mutatesQueue = owner is PendingOwner.Queue or PendingOwner.Recommendation || method == "music.history.restorePlaylist";
                await CommitAsync(candidate, owner, token, beforeQueue: mutatesQueue
                    ? new(previousVersion, previousHistory!.ToArray(), previousFailures, previousQueueLoaded) : null).ConfigureAwait(false);
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
                DocumentStoreError.InvalidRequest => "INVALID_REQUEST",
                DocumentStoreError.Unavailable => "STORAGE_UNAVAILABLE",
                DocumentStoreError.Conflict => "STORAGE_CONFLICT",
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
            { queueLoaded = previousQueueLoaded; queueVersion = previousVersion; shuffleHistory = previousHistory; failedEntries.Clear(); failedEntries.UnionWith(previousFailures); }
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
            LJ.Require(!closed, "CLOSED");
            LJ.Require(pending is null && recommendationUnknown is null, pending?.Code ?? "PENDING_WRITE");
            await SealSongSnapshots().ConfigureAwait(false);
            lock (repositoryGate) preparing = true;
            ClearRecommendations(); InvalidateOfficialSources(); RevokeQualityLeases();
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
            ClearRecommendations(); InvalidateOfficialSources(); RevokeQualityLeases();
        }
        finally { gate.Release(); }
        await PresentationCache.DisposeAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        try { if (!closed) await PrepareShutdownAsync().ConfigureAwait(false); }
        finally { await AbortAsync().ConfigureAwait(false); }
    }
}

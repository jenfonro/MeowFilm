using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Native authority for local music state. Never install beside a writing Node owner of the same namespace.</summary>
public sealed partial class MusicLibraryOwner : IAsyncDisposable
{
    public const string ContractVersion = "0.7.0";
    public const string DocumentNamespace = "music.library.v1";
    private readonly IDocumentStore store;
    private readonly string scope;
    private readonly CacheDocumentStore displayCache;
    private readonly bool ownsDisplayCache;
    private readonly SemaphoreSlim gate = new(1, 1);
    private JsonObject? document;
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private Pending? pending;
    private bool preparing, closed;
    private long queueVersion, serial;
    private List<string> shuffleHistory = [];
    private enum PendingOwner
    {
        Appearance,
        CatalogSource,
        DeviceSettings,
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
        Sync
    }
    private sealed record Pending(JsonObject Candidate, DocumentFingerprint Before, DocumentFingerprint After, string Code,
        PendingOwner Owner, RecommendationCommit? Recommendation = null);

    public MusicLibraryOwner(IDocumentStore store, IDocumentStore? cacheStore = null) : this(store, "local", cacheStore) { }
    public MusicLibraryOwner(IDocumentStore store, string scope, IDocumentStore? cacheStore = null)
    {
        ArgumentNullException.ThrowIfNull(store); LJ.Require(LJ.Id(scope)); this.store = store; this.scope = scope;
        ownsDisplayCache = cacheStore is not CacheDocumentStore;
        displayCache = cacheStore as CacheDocumentStore ?? (cacheStore is null ? new() : new(cacheStore));
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
    private string Key(string area) => LJ.Stringify(new JsonArray("meowfilm", "music", 1, scope, area));
    private JsonNode? Value(JsonObject doc, string area) =>
        LJ.Array(doc["entries"]).OfType<JsonObject>().FirstOrDefault(e => LJ.S(e["key"]) == Key(area))?["value"];
    private bool HasValue(JsonObject doc, string area) =>
        LJ.Array(doc["entries"]).OfType<JsonObject>().Any(e => LJ.S(e["key"]) == Key(area));
    private void Set(JsonObject doc, string area, JsonNode value)
    {
        var entries = LJ.Array(doc["entries"]);
        var entry = entries.OfType<JsonObject>().FirstOrDefault(e => LJ.S(e["key"]) == Key(area));
        if (entry is null) entries.Add(LJ.Obj(("key", Key(area)), ("value", value)));
        else entry["value"] = value.DeepClone();
    }
    private async Task EnterAsync(CancellationToken token) =>
        LJ.Require(await gate.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false), "TIMEOUT");
    private async Task LoadAsync(CancellationToken token, bool force = false)
    {
        if (document is null || force)
        {
            var saved = await store.ReadAsync(DocumentNamespace, token).ConfigureAwait(false);
            JsonObject candidate;
            if (saved.Data is null) { LJ.Require(saved.Fingerprint == DocumentFingerprint.Missing, "CORRUPT_STORAGE"); candidate = LJ.Obj(("version", 1), ("entries", new JsonArray())); }
            else candidate = LJ.PersistedDocument(saved.Data);
            ValidateDocument(candidate);
            if (Value(candidate, "queue") is JsonObject q) RestoreRuntime(q, false);
            else if (force) RestoreRuntime(Queue(candidate), false);
            document = candidate; fingerprint = saved.Fingerprint;
            ObserveStoredSongs(candidate);
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
    private async Task CommitAsync(JsonObject candidate, PendingOwner owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        ValidateDocument(candidate);
        byte[] bytes = LJ.Bytes(candidate);
        LJ.Require(bytes.Length <= 8 * 1024 * 1024, "LIMIT");
        var expected = DocumentFingerprint.FromBytes(bytes);
        try
        {
            // CAS admission is final. Cancellation cannot mean the write was rolled back.
            var result = await store.WriteAsync(DocumentNamespace, bytes, fingerprint, CancellationToken.None).ConfigureAwait(false);
            LJ.Require(result == expected, "COMMIT_UNKNOWN");
            document = candidate; fingerprint = result;
            if (owner == PendingOwner.Sync) ObserveStoredSongs(candidate);
            if (owner == PendingOwner.OfficialSources) RefreshOfficialSources(candidate);
        }
        catch (Exception error)
        {
            string code = error is DocumentStoreException { Error: DocumentStoreError.Conflict } ? "STORAGE_CONFLICT" : "COMMIT_UNKNOWN";
            pending = new(candidate, fingerprint, expected, code, owner, recommendationStage);
            if (recommendationStage is not null) recommendationUnknown = pending;
            InvalidateOfficialSources();
            throw new MusicLibraryException(code);
        }
    }
    private async Task ReconcileLockedAsync(CancellationToken token)
    {
        var previous = document; var previousFingerprint = fingerprint;
        long previousVersion = queueVersion; var previousHistory = shuffleHistory.ToList(); var failures = failedEntries.ToArray();
        try
        {
            var attempt = pending ?? recommendationUnknown;
            await LoadAsync(token, true).ConfigureAwait(false);
            if (attempt is { } p)
                LJ.Require(fingerprint == p.Before || fingerprint == p.After, "STORAGE_CONFLICT");
            SettleRecommendation(attempt);
            pending = null;
            RefreshOfficialSources(document!);
        }
        catch
        {
            document = previous; fingerprint = previousFingerprint; queueVersion = previousVersion; shuffleHistory = previousHistory;
            failedEntries.Clear(); failedEntries.UnionWith(failures);
            InvalidateOfficialSources();
            throw;
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
            if (PrepareSongFacts is { } prepareFacts) await prepareFacts(token).ConfigureAwait(false);
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
            var candidate = (JsonObject)document!.DeepClone();
            var outcome = await DispatchAsync(method, p, candidate, token, full).ConfigureAwait(false);
            var response = LJ.Merge(LJ.Obj(("ok", true)), SongFactsProjection?.Invoke(outcome.Result) ?? outcome.Result);
            LJ.Require(LJ.Bytes(response).Length <= 512 * 1024, "LIMIT");
            if (outcome.Mutate) await CommitAsync(candidate, CommandOwner(method), token).ConfigureAwait(false);
            if (outcome.Mutate && recommendationStage is { } recommendation)
                AcceptRecommendation(recommendation, true);
            // Do not turn a known successful durable mutation into an ambiguous cancellation.
            if (!outcome.Mutate) token.ThrowIfCancellationRequested();
            success = true;
            return LJ.Element(response);
        }
        catch (OperationCanceledException) { return LJ.Element(LJ.Obj(("ok", false), ("code", "CANCELLED"))); }
        catch (MusicLibraryException error) { return LJ.Element(LJ.Obj(("ok", false), ("code", error.Code))); }
        catch (DocumentStoreException error) { return LJ.Element(LJ.Obj(("ok", false), ("code", error.Error == DocumentStoreError.Unavailable ? "STORAGE_UNAVAILABLE" : "STORAGE"))); }
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
    public async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { LJ.Require(!closed, "CLOSED"); LJ.Require(pending is null && recommendationUnknown is null, pending?.Code ?? "PENDING_WRITE"); preparing = true; ClearRecommendations(); InvalidateOfficialSources(); }
        finally { gate.Release(); }
    }
    public async Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { LJ.Require(!closed, "CLOSED"); preparing = false; } finally { gate.Release(); }
    }
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { closed = true; ClearRecommendations(); InvalidateOfficialSources(); } finally { gate.Release(); }
        await displayCache.DrainAsync().ConfigureAwait(false);
        if (ownsDisplayCache) await displayCache.DisposeAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    { if (!closed) await PrepareShutdownAsync().ConfigureAwait(false); await AbortAsync().ConfigureAwait(false); }
}

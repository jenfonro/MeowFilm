using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

/// <summary>
/// Borrowed native catalog; one keyword, at most one songs and one playlists session.
/// A session advances in rounds: the submitted search is round 1, every "next" is one more round.
/// Each round asks every eligible platform for the configured number of pages, limited per
/// platform by the configured concurrency. Round 1 is fused and published as results arrive;
/// later rounds are fused once every platform of the round has settled and are appended.
/// Parsed platform pages are cached in memory for the configured time.
/// No timers except per-request deadlines, no storage, implicit provider fallback or script calls.
/// </summary>
public sealed partial class MusicSearchCoordinator : IAsyncDisposable
{
    public static TimeSpan RequestTimeout => TimeSpan.FromSeconds(25);
    private const long CacheBudgetBytes = 32L * 1024 * 1024;
    private static readonly string[] Platforms = ["wy", "tx", "kw", "kg", "mg"];
    private readonly MusicPlatformCatalog catalog;
    private readonly Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> searchAsync;
    private readonly Func<long> nextObservation;
    private static TimeProvider clock => TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<MusicSearchKind, Query> queries = [];
    private readonly Dictionary<MusicSearchKind, Notice> notices = [];
    private readonly Dictionary<CacheKey, CacheEntry> cache = [];
    private readonly Dictionary<string, long> identityEpochs = [];
    private readonly AsyncLocal<ObserverFrame?> observerFrame = new();
    private long cacheBytes, cacheUse, observationOrder;
    private Replacement? replacement;
    private Task publisher = Task.CompletedTask;
    private Task? preparation, resumption, disposal;
    private bool publishing, stopping, closed;
    private string keyword = "";

    private sealed record CacheKey(MusicSearchKind Kind, string Platform, string Keyword, int Page, int Pages, int Limit);
    private sealed class CacheEntry(MusicSearchPage page, DateTimeOffset expires, long bytes, long checkedAt, long order, long generation)
    {
        internal readonly MusicSearchPage Page = page;
        internal readonly DateTimeOffset Expires = expires;
        internal readonly long Bytes = bytes;
        internal readonly long CheckedAt = checkedAt;
        internal readonly long Order = order;
        internal readonly long Generation = generation;
        internal long Used;
    }
    internal sealed record SearchOptions(int Pages, int Concurrency, TimeSpan CacheTime, bool Refresh);

    private sealed class PlatformState(string platform, int concurrency)
    {
        internal readonly string Platform = platform;
        internal readonly MusicSearchPaging Paging = MusicSearchPaging.For(platform);
        internal readonly List<SearchCandidate> Items = [], RoundItems = [];
        internal readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        internal readonly Dictionary<int, (Chunk Chunk, ChunkResult Result)> Checks = [];
        // Per kind x platform concurrency: this query is the only owner of the kind.
        internal readonly SemaphoreSlim Slots = new(concurrency, concurrency);
        internal int Page, NextPage = 1, Generation;
        internal long CheckedAt;
        internal bool More = true, Loading;
        internal string Error = "";
        internal CancellationTokenSource? Cancellation;
        internal Task Flight = Task.CompletedTask;
    }

    private enum FusionPlan { None, Full, Merge }

    private sealed class Query(string keyword, MusicSearchKind kind, string[] platforms, SearchOptions options)
    {
        internal readonly string Id = Guid.NewGuid().ToString("N"), Keyword = keyword;
        internal readonly MusicSearchKind Kind = kind;
        internal readonly string[] Order = platforms;
        internal readonly SearchOptions Options = options;
        internal readonly PlatformState[] States = platforms.Select(p => new PlatformState(p, options.Concurrency)).ToArray();
        internal readonly CancellationTokenSource Lifetime = new();
        internal MusicSongRecord[] Songs = [];
        internal MusicSearchPlaylistRecord[] Playlists = [];
        internal long Revision, DataVersion;
        internal long FusedVersion = -1;
        internal int Round;
        internal bool RoundOpen, Stopped, Computing, FusionRunning, Released, SettlementNoticePending;
        internal bool InitialIdentityCurrent = true;
        internal FusionPlan Plan;
        internal Task<MusicSearchSnapshot>? CancellationDrain;
        internal string FusionError = "";
        internal Task Fusion = Task.CompletedTask;
        internal bool Matches(string word, MusicSearchKind requestedKind, string[] requestedPlatforms, SearchOptions requested) =>
            Keyword == word && Kind == requestedKind && Order.SequenceEqual(requestedPlatforms) &&
            Options with { Refresh = false } == requested with { Refresh = false };
    }
    private sealed record Notice(Query Query, long Revision, MusicSearchSnapshot Value);
    private sealed class ObserverFrame { internal int Active = 1; }
    private sealed class Replacement(string keyword, MusicSearchKind kind, string[] platforms, SearchOptions options)
    {
        internal string Keyword { get; } = keyword;
        internal MusicSearchKind Kind { get; } = kind;
        internal string[] Platforms { get; } = platforms;
        internal SearchOptions Options { get; } = options;
        internal Task<MusicSearchSnapshot> Work { get; set; } = null!;
        internal bool Matches(string word, MusicSearchKind requestedKind, string[] requestedPlatforms, SearchOptions requested) =>
            Keyword == word && Kind == requestedKind && Platforms.SequenceEqual(requestedPlatforms) && Options == requested;
    }
    private sealed record Chunk(int Page, int Pages);
    private sealed record ChunkResult(MusicSearchPage? Page, string Error, long Bytes, bool Cached, long CheckedAt, long Order, long Generation);
    /// <summary>A chunk's outcome is published at once; Settled ends with its physical request and slot.</summary>
    private sealed record ChunkFetch(int Index, ChunkResult Result, Task Settled);

    internal MusicSearchCoordinator(MusicPlatformCatalog catalog,
        Func<MusicSearchRequest, CancellationToken, Task<JsonElement>>? searchAsync = null, Func<long>? nextObservation = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.searchAsync = searchAsync ?? catalog.SearchAsync;
        this.nextObservation = nextObservation ?? (() => Interlocked.Increment(ref observationOrder));
    }

    public event Action<MusicSearchSnapshot>? Changed;
    internal event Action<MusicSearchSnapshot>? MatchingObserved;
    internal Func<IReadOnlyList<MusicSongRecord>, MusicSongRecord[]>? SongFactsProjection { get; set; }

    public Task<MusicSearchSnapshot> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string validId = MusicRules.RequireId(id, 32);
        lock (gate)
        {
            Guard();
            var query = queries.Values.FirstOrDefault(q => q.Id == validId) ?? throw new MusicLibraryException("SEARCH_NOT_FOUND");
            return Task.FromResult(Snapshot(query));
        }
    }

    public Task<MusicSearchSnapshot> NextAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string validId = MusicRules.RequireId(id, 32);
        lock (gate)
        {
            Guard();
            var query = queries.Values.FirstOrDefault(q => q.Id == validId) ?? throw new MusicLibraryException("SEARCH_NOT_FOUND");
            // A new round starts only after the previous round and its fusion have settled.
            if (!query.Stopped && !query.RoundOpen && !query.Computing) BeginRoundLocked(query);
            return Task.FromResult(Snapshot(query));
        }
    }

    public async Task<MusicSearchSnapshot> CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string validId = MusicRules.RequireId(id, 32);
        Task<MusicSearchSnapshot> cancellation;
        lock (gate)
        {
            Guard();
            var query = queries.Values.FirstOrDefault(q => q.Id == validId) ?? throw new MusicLibraryException("SEARCH_NOT_FOUND");
            StopLocked(query);
            cancellation = query.CancellationDrain ??= Task.Run(() => CancelQueryAsync(query));
        }
        // Cancellation of the waiter does not turn physical requests into completed work.
        return await cancellation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Admits a start synchronously and returns the task that settles it. The
    /// caller may release its policy lane before waiting for an old query drain.
    /// </summary>
    internal Task<MusicSearchSnapshot> StartAdmittedAsync(string keyword, string kindName, string[] platforms,
        int pages, int concurrency, int cacheMinutes, bool refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string word = keyword.Trim();
        MusicRules.Require(MusicRules.IsValidId(word, null));
        MusicRules.Require(kindName is "songs" or "playlists");
        var kind = kindName == "songs" ? MusicSearchKind.Songs : MusicSearchKind.Playlists;
        MusicRules.Require(platforms.Length <= 5 && platforms.All(Platforms.Contains) &&
            platforms.Distinct(StringComparer.Ordinal).Count() == platforms.Length);
        MusicRules.Require(pages >= 1 && concurrency >= 1 && cacheMinutes is >= 1 and <= MusicSearchSettings.MaxCacheMinutes);
        var options = new SearchOptions(pages, concurrency, TimeSpan.FromMinutes(cacheMinutes), refresh);
        Task<MusicSearchSnapshot> start;
        lock (gate)
        {
            Guard();
            if (replacement is { } pending)
            {
                if (!pending.Matches(word, kind, platforms, options)) throw new MusicLibraryException("SEARCH_BUSY");
                start = pending.Work;
            }
            else
            {
                if (!options.Refresh && word == this.keyword && queries.TryGetValue(kind, out var existing) &&
                    !existing.Stopped && existing.InitialIdentityCurrent && existing.Round == 1 &&
                    (existing.RoundOpen || existing.Computing) &&
                    existing.Matches(word, kind, platforms, options))
                    return Task.FromResult(Snapshot(existing));
                var retired = queries.Values.Where(q => word != this.keyword || q.Kind == kind).ToArray();
                if (retired.Length == 0)
                    return Task.FromResult(BeginLocked(word, kind, platforms, options));
                foreach (var old in retired) StopLocked(old);
                var admitted = new Replacement(word, kind, platforms, options);
                replacement = admitted;
                start = admitted.Work = ReplaceAsync(admitted, retired, cancellationToken);
            }
        }
        return start.WaitAsync(cancellationToken);
    }

    private MusicSearchSnapshot BeginLocked(string word, MusicSearchKind kind, string[] platforms, SearchOptions options)
    {
        keyword = word;
        var query = new Query(word, kind, platforms, options); queries[kind] = query;
        BeginRoundLocked(query);
        return Snapshot(query);
    }

    private async Task<MusicSearchSnapshot> ReplaceAsync(Replacement admitted, Query[] retired, CancellationToken caller)
    {
        await Task.Yield(); // Publish the admission task before cancellation/re-entrant callbacks.
        try
        {
            foreach (var old in retired) Cancel(old.Lifetime);
            foreach (var old in retired) await DrainAsync(old).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            lock (gate)
            {
                Guard();
                foreach (var old in retired)
                {
                    if (queries.TryGetValue(old.Kind, out var current) && ReferenceEquals(old, current)) queries.Remove(old.Kind);
                    ReleaseLocked(old);
                }
                return BeginLocked(admitted.Keyword, admitted.Kind, admitted.Platforms, admitted.Options);
            }
        }
        finally
        {
            lock (gate)
                if (ReferenceEquals(replacement, admitted))
                    replacement = null;
        }
    }

    // A failed platform keeps its cursor and stays eligible: the next round retries the same pages.
    private static bool Eligible(PlatformState state) => state.More && !state.Loading;

    private void BeginRoundLocked(Query query)
    {
        var eligible = query.States.Where(Eligible).ToArray();
        if (eligible.Length == 0) { PublishLocked(query); return; }
        query.Round++; query.RoundOpen = true;
        foreach (var state in query.States) state.RoundItems.Clear();
        foreach (var state in eligible)
        {
            state.Error = ""; state.Loading = true; state.Cancellation = new();
            int generation = ++state.Generation;
            int start = state.NextPage, pages = query.Options.Pages;
            var cancellation = state.Cancellation;
            int round = query.Round;
            state.Flight = Task.Run(() => FetchRoundAsync(query, state, generation, round, start, pages, cancellation));
        }
        PublishLocked(query);
    }

    private static Chunk? ChunkAt(int index, int start, int pages, int per)
    {
        long offset = (long)index * per;
        long page = start + offset;
        if (offset >= pages || page > int.MaxValue) return null;
        return new((int)page, (int)Math.Min(Math.Min(per, pages - offset), int.MaxValue - page + 1));
    }

    private async Task FetchRoundAsync(Query query, PlatformState state, int generation, int round, int start, int pages,
        CancellationTokenSource cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(query.Lifetime.Token, cancellation.Token);
        int per = state.Paging.PagesPerRequest, concurrency = query.Options.Concurrency;
        var chunks = new Dictionary<int, Chunk>();
        var results = new Dictionary<int, ChunkResult>();
        var pending = new List<Task<ChunkFetch>>();
        var settled = new List<Task>();
        int committed = 0, next = 0;
        bool halted = false;
        try
        {
            while (true)
            {
                while (!halted && next - committed < concurrency)
                {
                    if (linked.IsCancellationRequested || ChunkAt(next, start, pages, per) is not { } chunk) break;
                    chunks[next] = chunk;
                    pending.Add(FetchChunkAsync(query, state, chunk, next++, linked.Token));
                }
                if (pending.Count == 0) break;
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                var fetch = await done.ConfigureAwait(false);
                settled.Add(fetch.Settled);
                results[fetch.Index] = fetch.Result;
                lock (gate)
                {
                    if (!Current(query, state, generation)) { halted = true; Cancel(linked); continue; }
                    // Commit the contiguous prefix only. A failure, an end of results or a
                    // cursor the platform moved elsewhere halts the round; the cursor stays for the next round.
                    int before = committed;
                    while (!halted && results.TryGetValue(committed, out var result))
                    {
                        var chunk = chunks[committed];
                        if (result.Page is null)
                        {
                            if (result.Error != "CANCELLED")
                            {
                                state.Error = result.Error; state.CheckedAt = result.CheckedAt;
                                state.Checks[chunk.Page] = (chunk, result);
                            }
                            if (result.Error == "UNSUPPORTED") state.More = false;
                            halted = true; break;
                        }
                        if (chunk.Page != state.NextPage) { halted = true; break; }
                        state.CheckedAt = result.CheckedAt;
                        state.Checks[chunk.Page] = (chunk, result);
                        halted = !CommitLocked(query, state, chunk, result.Page);
                        committed++;
                        results.Remove(committed - 1);
                        chunks.Remove(committed - 1);
                    }
                    if (round == 1 && committed > before) QueueFusionLocked(query, FusionPlan.Full);
                    PublishLocked(query);
                }
                if (halted || committed == next && ChunkAt(next, start, pages, per) is null) Cancel(linked);
            }
        }
        finally
        {
            // The platform stays loading, and its slots stay taken, until physical work has settled.
            await Task.WhenAll(settled).ConfigureAwait(false);
            lock (gate)
            {
                if (ReferenceEquals(state.Cancellation, cancellation))
                {
                    state.Cancellation = null; state.Loading = false;
                    if (Current(query)) SettleRoundLocked(query, round);
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task<ChunkFetch> FetchChunkAsync(Query query, PlatformState state, Chunk chunk, int index, CancellationToken token)
    {
        var key = new CacheKey(query.Kind, state.Platform, query.Keyword, chunk.Page, chunk.Pages, state.Paging.PageSize);
        if (!query.Options.Refresh)
            lock (gate)
                if (cache.TryGetValue(key, out var entry))
                {
                    if (entry.Expires > clock.GetUtcNow())
                    { entry.Used = ++cacheUse; return new(index, new(entry.Page, "", entry.Bytes, true, entry.CheckedAt, entry.Order, entry.Generation), Task.CompletedTask); }
                    EraseLocked(key);
                }
        long identity; lock (gate) identity = identityEpochs.GetValueOrDefault(state.Platform);
        bool admitted = false, requested = false;
        long order = 0;
        Task<JsonElement>? physical = null;
        CancellationTokenSource? deadline = null, linked = null;
        ChunkResult result;
        try
        {
            await state.Slots.WaitAsync(token).ConfigureAwait(false); admitted = true;
            // The request deadline starts when the request is admitted, not while it waits for a slot.
            deadline = new CancellationTokenSource(RequestTimeout, clock);
            token.ThrowIfCancellationRequested();
            // Query retirement stops scheduling, not already dispatched pages. Only
            // account invalidation, shutdown and the deadline own physical cancellation.
            linked = CancellationTokenSource.CreateLinkedTokenSource(state.Cancellation!.Token, deadline.Token);
            if (!Supports(state.Platform, query.Kind)) throw new MusicLibraryException("UNSUPPORTED");
            requested = true;
            order = nextObservation();
            physical = searchAsync(new(state.Platform, query.Keyword, query.Kind, chunk.Page, state.Paging.PageSize,
                query.Options.Refresh, chunk.Pages), linked.Token);
            var response = await physical.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var parsed = MusicSearchPage.Read(response, state.Platform, query.Kind, chunk.Page);
            result = new(parsed, "", response.GetRawText().Length * 2L, false, clock.GetUtcNow().ToUnixTimeMilliseconds(), order, identity);
            lock (gate)
                if (!closed && !stopping && identity == identityEpochs.GetValueOrDefault(state.Platform))
                    Store(query, state.Platform, chunk, result);
        }
        catch (Exception error)
        {
            result = new(null, token.IsCancellationRequested ? "CANCELLED" :
                deadline?.IsCancellationRequested == true ? "TIMEOUT" : ErrorCode(error), 0, false,
                requested ? clock.GetUtcNow().ToUnixTimeMilliseconds() : 0, order, identity);
        }
        return new(index, result, SettleAsync(state, physical, admitted, linked, deadline));
    }

    // A provider that ignores cancellation keeps its slot until its physical operation has settled.
    private static async Task SettleAsync(PlatformState state, Task<JsonElement>? physical, bool admitted,
        CancellationTokenSource? linked, CancellationTokenSource? deadline)
    {
        try { if (physical is not null) { try { await physical.ConfigureAwait(false); } catch { } } }
        finally
        {
            if (admitted) state.Slots.Release();
            linked?.Dispose(); deadline?.Dispose();
        }
    }

    /// <summary>Returns false when this platform cannot continue in the current round.</summary>
    private static bool CommitLocked(Query query, PlatformState state, Chunk chunk, MusicSearchPage parsed)
    {
        int added = 0;
        foreach (var item in parsed.Items)
            if (state.Seen.Add(item.Key))
            { state.Items.Add(item); state.RoundItems.Add(item); added++; }
        state.Page = chunk.Page + chunk.Pages - 1; state.NextPage = parsed.Next; state.More = parsed.More;
        if (added > 0) query.DataVersion++;
        return state.More;
    }

    private void SettleRoundLocked(Query query, int round)
    {
        if (round != query.Round || !query.RoundOpen || query.States.Any(s => s.Loading)) { PublishLocked(query); return; }
        query.RoundOpen = false;
        // Later rounds become visible together, after every platform of the round has settled.
        QueueFusionLocked(query, round == 1 ? FusionPlan.Full : FusionPlan.Merge);
        PublishLocked(query);
    }

    private void Store(Query query, string platform, Chunk chunk, ChunkResult result)
    {
        var key = new CacheKey(query.Kind, platform, query.Keyword, chunk.Page, chunk.Pages, MusicSearchPaging.For(platform).PageSize);
        EraseLocked(key);
        if (result.Bytes > CacheBudgetBytes / 4) return;
        var now = clock.GetUtcNow();
        if (cacheBytes + result.Bytes > CacheBudgetBytes)
        {
            foreach (var expired in cache.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                EraseLocked(expired);
        }
        while (cacheBytes + result.Bytes > CacheBudgetBytes && cache.Count > 0)
            EraseLocked(cache.MinBy(pair => pair.Value.Used).Key);
        cache[key] = new(result.Page!, now + query.Options.CacheTime, result.Bytes, result.CheckedAt, result.Order, result.Generation) { Used = ++cacheUse };
        cacheBytes += result.Bytes;
    }

    internal void ClearMemoryCache()
    {
        lock (gate)
        {
            cache.Clear();
            cacheBytes = 0;
        }
    }

    internal object GetCacheStats()
    {
        lock (gate)
        {
            return new
            {
                entryCount = cache.Count,
                bytes = cacheBytes,
                maximumBytes = CacheBudgetBytes,
                activeQueries = queries.Count,
                closed
            };
        }
    }

    private void EraseLocked(CacheKey key) { if (cache.Remove(key, out var entry)) cacheBytes -= entry.Bytes; }

    private static bool Supports(string platform, MusicSearchKind _) =>
        Platforms.Contains(platform);

    private void QueueFusionLocked(Query query, FusionPlan plan)
    {
        // A merge waits for the round barrier; a full fusion supersedes a pending merge.
        query.Plan = plan == FusionPlan.Full || query.Plan == FusionPlan.None ? plan : query.Plan;
        query.DataVersion++; query.Computing = true;
        if (query.FusionRunning) return;
        query.FusionRunning = true;
        query.Fusion = Task.Run(() => Fuse(query));
    }

    private void Fuse(Query query)
    {
        while (true)
        {
            SearchCandidate[] candidates; long version; FusionPlan plan;
            MusicSongRecord[] displayedSongs; MusicSearchPlaylistRecord[] displayedPlaylists;
            lock (gate)
            {
                if (!Current(query)) { query.Computing = query.FusionRunning = false; return; }
                version = query.DataVersion; plan = query.Plan;
                candidates = plan == FusionPlan.Merge
                    ? query.States.SelectMany(s => s.RoundItems).ToArray()
                    : query.States.SelectMany(s => s.Items).ToArray();
                displayedSongs = query.Songs;
                displayedPlaylists = query.Playlists;
            }
            MusicSongRecord[]? fusedSongs = null;
            MusicSearchPlaylistRecord[]? fusedPlaylists = null;
            string error = "";
            try
            {
                if (query.Kind == MusicSearchKind.Songs)
                {
                    if (SongFactsProjection is { } project && candidates.Length > 0)
                    {
                        var projected = project(candidates.Select(c => c.Song!).ToArray());
                        var updated = new SearchCandidate[candidates.Length];
                        for (int i = 0; i < candidates.Length; i++)
                            updated[i] = candidates[i] with { Song = projected[i] };
                        candidates = updated;
                    }
                    fusedSongs = MusicRankFusion.FuseSongs(candidates, query.Order, query.Lifetime.Token,
                        plan == FusionPlan.Merge ? displayedSongs : null);
                }
                else
                {
                    fusedPlaylists = MusicRankFusion.FusePlaylists(candidates, query.Order, query.Lifetime.Token,
                        plan == FusionPlan.Merge ? displayedPlaylists : null);
                }
            }
            catch (OperationCanceledException) { }
            catch { error = "FUSION_FAILED"; }
            lock (gate)
            {
                if (!Current(query)) { query.Computing = query.FusionRunning = false; return; }
                if (version != query.DataVersion) continue;
                if (fusedSongs is not null) { query.Songs = fusedSongs; query.FusedVersion = version; }
                if (fusedPlaylists is not null) { query.Playlists = fusedPlaylists; query.FusedVersion = version; }
                query.Plan = FusionPlan.None;
                query.FusionError = error; query.Computing = query.FusionRunning = false;
                PublishLocked(query); return;
            }
        }
    }

    /// <summary>
    /// Retire affected platform generations in both kinds synchronously, without retiring the query ids.
    /// Physical flights remain tracked (and loading) until settled. Cached pages of the platform are
    /// dropped. This means genuine account change/revocation, not an unclassified initial-restore notification.
    /// The account owner separately invalidates its borrowed catalog.
    /// </summary>
    public void Invalidate(string? platform = null)
    {
        if (platform is not null && !Platforms.Contains(platform)) throw new ArgumentException("INVALID_PLATFORM", nameof(platform));
        List<CancellationTokenSource> cancelled = [];
        lock (gate)
        {
            if (closed) return;
            foreach (var provider in platform is null ? Platforms : [platform])
                identityEpochs[provider] = identityEpochs.GetValueOrDefault(provider) + 1;
            foreach (var key in cache.Keys.Where(k => platform is null || k.Platform == platform).ToArray()) EraseLocked(key);
            if (stopping) return;
            foreach (var query in queries.Values.Where(q => !q.Stopped))
            {
                var affected = query.States.Where(s => platform is null || s.Platform == platform).ToArray();
                if (affected.Length == 0) continue;
                query.InitialIdentityCurrent = false;
                foreach (var state in affected)
                {
                    state.Generation++;
                    if (state.Cancellation is { } pending) cancelled.Add(pending);
                    state.Items.Clear(); state.RoundItems.Clear(); state.Seen.Clear();
                    state.Checks.Clear();
                    state.Page = 0; state.NextPage = 1; state.CheckedAt = 0;
                    state.More = true; state.Error = "STALE_SESSION";
                }
                // Remaining platforms' candidates are fused again from scratch.
                query.Songs = []; query.Playlists = []; QueueFusionLocked(query, FusionPlan.Full); PublishLocked(query);
            }
        }
        foreach (var pending in cancelled) Cancel(pending);
    }

    private bool Current(Query query) => !closed && !stopping && !query.Stopped &&
        queries.TryGetValue(query.Kind, out var value) && ReferenceEquals(value, query);
    private bool Current(Query query, PlatformState state, int generation) => Current(query) && state.Generation == generation;
    private void Guard() { if (stopping || closed) throw new MusicLibraryException("CLOSED"); }
    private static string Kind(MusicSearchKind kind) => kind == MusicSearchKind.Songs ? "songs" : "playlists";
    private static string ErrorCode(Exception error) => error switch
    {
        MusicPlatformException e when e.Code.Length is > 0 and <= 64 && e.Code.All(c => c is >= 'A' and <= 'Z' or '_' or >= '0' and <= '9') => e.Code,
        MusicLibraryException { Code: "UNSUPPORTED" or "IDENTITY_MISMATCH" or "LIMIT" } e => e.Code,
        OperationCanceledException => "CANCELLED",
        _ => "INVALID_RESPONSE"
    };
    private static void Cancel(CancellationTokenSource source) { try { source.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { } }

    private static MusicSearchSnapshot Snapshot(Query query, bool includeCheckTimes = false)
    {
        bool partial = query.FusionError.Length > 0 || query.States.Any(s => s.Error.Length > 0);
        string code = query.Stopped ? "CANCELLED" : query.States.Length == 0 ? "NO_PLATFORMS" :
            query.FusionError.Length > 0 ? query.FusionError : partial ? "PARTIAL" : "";
        var platforms = query.States.Select(s => new MusicSearchPlatformSnapshot(
            s.Platform, s.Page, s.Items.Count, !query.Stopped && s.More, s.Loading, s.Error,
            includeCheckTimes ? s.CheckedAt : 0)).ToArray();
        MusicSearchObservation[] observations = includeCheckTimes
            ? query.States.SelectMany(state => state.Checks.Values.Select(check => (State: state, Check: check)))
                .OrderBy(pair => pair.Check.Result.Order)
                .Select(entry => new MusicSearchObservation(
                    entry.State.Platform,
                    entry.Check.Chunk.Page,
                    entry.Check.Chunk.Pages,
                    entry.State.Paging.PageSize,
                    entry.Check.Result.CheckedAt,
                    entry.Check.Result.Order,
                    entry.Check.Result.Generation,
                    entry.Check.Result.Cached,
                    entry.Check.Result.Error,
                    entry.Check.Result.Page is { } pg
                        ? pg.Items.Where(item => item.Song is not null).Select(item => item.Song!).ToArray()
                        : []))
                .ToArray()
            : [];
        return new MusicSearchSnapshot(
            Id: query.Id,
            Revision: query.Revision,
            Keyword: query.Keyword,
            Kind: Kind(query.Kind),
            Round: query.Round,
            Songs: query.Songs,
            Playlists: query.Playlists,
            More: !query.Stopped && query.States.Any(s => s.More),
            // A cancelled round never settles; it is loading only while its physical work drains.
            Loading: query.Computing || query.RoundOpen && !query.Stopped || query.States.Any(s => s.Loading),
            Partial: partial,
            Code: code,
            Cancelled: query.Stopped,
            Platforms: platforms,
            Observations: observations);
    }

    private void PublishLocked(Query query)
    {
        query.Revision++;
        notices[query.Kind] = new(query, query.Revision, Snapshot(query));
        if (publishing) return;
        publishing = true; publisher = Task.Run(Publish);
    }

    private void Publish()
    {
        while (true)
        {
            Notice notice; MusicSearchSnapshot? matching = null;
            lock (gate)
            {
                if (notices.Count == 0) { publishing = false; return; }
                var first = notices.First(); notice = first.Value; notices.Remove(first.Key);
                if (closed || stopping || !queries.TryGetValue(notice.Query.Kind, out var current) ||
                    !ReferenceEquals(current, notice.Query) || current.Revision != notice.Revision) continue;
                if (MatchingObserved is not null && !current.Stopped && current.Kind == MusicSearchKind.Songs &&
                    !current.Computing && current.FusedVersion == current.DataVersion && current.FusionError.Length == 0)
                    matching = Snapshot(current, includeCheckTimes: true);
            }
            Notify(notice, Changed, notice.Value);
            if (matching is { } value) Notify(notice, MatchingObserved, value);
        }
    }

    private void Notify(Notice notice, Action<MusicSearchSnapshot>? observers, MusicSearchSnapshot value)
    {
        foreach (var observer in observers?.GetInvocationList() ?? [])
        {
            // An earlier observer can synchronously invalidate an account or close the owner.
            // Do not deliver that now-retired notice to the remaining observers.
            lock (gate)
                if (closed || stopping || !queries.TryGetValue(notice.Query.Kind, out var current) ||
                    !ReferenceEquals(current, notice.Query) || current.Revision != notice.Revision) break;
            var previous = observerFrame.Value;
            var frame = new ObserverFrame(); observerFrame.Value = frame;
            try { ((Action<MusicSearchSnapshot>)observer)(value); } catch { /* An observer cannot break request retirement. */ }
            finally
            {
                // Child tasks may have captured this frame. Once the callback returns,
                // they are ordinary external callers, not an excuse to skip a barrier.
                Volatile.Write(ref frame.Active, 0); observerFrame.Value = previous;
            }
        }
    }
}

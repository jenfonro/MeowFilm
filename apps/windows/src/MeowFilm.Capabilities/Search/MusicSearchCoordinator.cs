using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly IMusicPlatformCatalog catalog;
    private readonly Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> searchAsync;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<MusicSearchKind, Query> queries = [];
    private readonly Dictionary<MusicSearchKind, Notice> notices = [];
    private readonly Dictionary<CacheKey, CacheEntry> cache = [];
    private readonly Dictionary<string, long> identityEpochs = [];
    private readonly AsyncLocal<ObserverFrame?> observerFrame = new();
    private long cacheBytes, cacheUse;
    private Replacement? replacement;
    private Task publisher = Task.CompletedTask;
    private Task? preparation, resumption, disposal;
    private bool publishing, stopping, closed;
    private string keyword = "";

    private sealed record CacheKey(MusicSearchKind Kind, string Platform, string Keyword, int Page, int Pages, int Limit);
    private sealed class CacheEntry(MusicSearchPage page, DateTimeOffset expires, long bytes, long checkedAt)
    {
        internal readonly MusicSearchPage Page = page;
        internal readonly DateTimeOffset Expires = expires;
        internal readonly long Bytes = bytes;
        internal readonly long CheckedAt = checkedAt;
        internal long Used;
    }
    internal sealed record SearchOptions(int Pages, int Concurrency, TimeSpan CacheTime, bool Refresh);

    private sealed class PlatformState(string platform, int concurrency)
    {
        internal readonly string Platform = platform;
        internal readonly MusicSearchPaging Paging = MusicSearchPaging.For(platform);
        internal readonly List<SearchCandidate> Items = [], RoundItems = [];
        internal readonly HashSet<string> Seen = new(StringComparer.Ordinal);
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
        internal JsonArray Items = [];
        internal long Revision, DataVersion;
        internal long FusedVersion = -1;
        internal int Round;
        internal bool RoundOpen, Stopped, Computing, FusionRunning, Released, SettlementNoticePending;
        internal bool InitialIdentityCurrent = true;
        internal FusionPlan Plan;
        internal Task<JsonElement>? CancellationDrain;
        internal string FusionError = "";
        internal Task Fusion = Task.CompletedTask;
        internal bool Matches(string word, MusicSearchKind requestedKind, string[] requestedPlatforms, SearchOptions requested) =>
            Keyword == word && Kind == requestedKind && Order.SequenceEqual(requestedPlatforms) &&
            Options with { Refresh = false } == requested with { Refresh = false };
    }
    private sealed record Notice(Query Query, long Revision, JsonElement Value);
    private sealed class ObserverFrame { internal int Active = 1; }
    private sealed class Replacement(string keyword, MusicSearchKind kind, string[] platforms, SearchOptions options)
    {
        internal string Keyword { get; } = keyword;
        internal MusicSearchKind Kind { get; } = kind;
        internal string[] Platforms { get; } = platforms;
        internal SearchOptions Options { get; } = options;
        internal Task<JsonElement> Work { get; set; } = Task.FromResult(Failed("SEARCH_BUSY"));
        internal bool Matches(string word, MusicSearchKind requestedKind, string[] requestedPlatforms, SearchOptions requested) =>
            Keyword == word && Kind == requestedKind && Platforms.SequenceEqual(requestedPlatforms) && Options == requested;
    }
    private sealed record Chunk(int Page, int Pages);
    private sealed record ChunkResult(MusicSearchPage? Page, string Error, long Bytes, bool Cached, long CheckedAt);
    /// <summary>A chunk's outcome is published at once; Settled ends with its physical request and slot.</summary>
    private sealed record ChunkFetch(int Index, ChunkResult Result, Task Settled);

    public MusicSearchCoordinator(IMusicPlatformCatalog catalog, TimeProvider? timeProvider = null,
        Func<MusicSearchRequest, CancellationToken, Task<JsonElement>>? searchAsync = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        clock = timeProvider ?? TimeProvider.System;
        this.searchAsync = searchAsync ?? catalog.SearchAsync;
    }
    public event Action<JsonElement>? Changed;
    internal event Action<JsonElement>? MatchingObserved;

    public async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method == "music.search.start")
                return await ExecuteStartAsync(parameters, cancellationToken).ConfigureAwait(false);
            var input = LJ.Parameters(parameters, 4096);
            LJ.Fields(input, "id", "id");
            string id = LJ.Text(input["id"], 32);
            Task<JsonElement>? cancellation = null;
            lock (gate)
            {
                Guard();
                var query = queries.Values.FirstOrDefault(q => q.Id == id) ?? throw new MusicLibraryException("SEARCH_NOT_FOUND");
                switch (method)
                {
                    case "music.search.get": return Snapshot(query);
                    case "music.search.next":
                        // A new round starts only after the previous round and its fusion have settled.
                        if (!query.Stopped && !query.RoundOpen && !query.Computing) BeginRoundLocked(query);
                        return Snapshot(query);
                    case "music.search.cancel":
                        StopLocked(query);
                        cancellation = query.CancellationDrain ??= Task.Run(() => CancelQueryAsync(query));
                        break;
                    default: return Failed("METHOD_NOT_FOUND");
                }
            }
            // Cancellation of the waiter does not turn physical requests into completed work.
            return await cancellation!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Failed("CANCELLED"); }
        catch (MusicLibraryException error) { return Failed(error.Code); }
        catch { return Failed("INVALID_REQUEST"); }
    }

    /// <summary>
    /// Admits a start synchronously and returns the task that settles it. The
    /// caller may release its policy lane before waiting for an old query drain.
    /// </summary>
    internal Task<JsonElement> ExecuteStartAsync(object? parameters, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = LJ.Parameters(parameters, 4096);
            LJ.Fields(input, "keyword kind platforms pages concurrency cacheMinutes refresh",
                "keyword kind platforms pages concurrency cacheMinutes");
            string word = LJ.S(input["keyword"]).Trim();
            LJ.Require(LJ.Id(word, 200));
            string kindName = LJ.Text(input["kind"], 16);
            LJ.Require(kindName is "songs" or "playlists");
            var kind = kindName == "songs" ? MusicSearchKind.Songs : MusicSearchKind.Playlists;
            var selected = LJ.Array(input["platforms"]);
            LJ.Require(selected.Count <= 5);
            var platforms = selected.Select(p => LJ.Text(p, 2)).ToArray();
            LJ.Require(platforms.All(Platforms.Contains) && platforms.Distinct(StringComparer.Ordinal).Count() == platforms.Length);
            var options = new SearchOptions(
                MusicSearchSettings.Pages(input["pages"]),
                MusicSearchSettings.Concurrency(input["concurrency"]),
                TimeSpan.FromMinutes(MusicSearchSettings.CacheMinutes(input["cacheMinutes"])),
                input["refresh"] is not null && LJ.B(input["refresh"]));
            Task<JsonElement> start;
            lock (gate)
            {
                Guard();
                if (replacement is { } pending)
                {
                    if (!pending.Matches(word, kind, platforms, options)) return Task.FromResult(Failed("SEARCH_BUSY"));
                    start = pending.Work;
                }
                else
                {
                    if (!options.Refresh && word == keyword && queries.TryGetValue(kind, out var existing) &&
                        !existing.Stopped && existing.InitialIdentityCurrent && existing.Round == 1 &&
                        (existing.RoundOpen || existing.Computing) &&
                        existing.Matches(word, kind, platforms, options))
                        return Task.FromResult(Snapshot(existing));
                    var retired = queries.Values.Where(q => word != keyword || q.Kind == kind).ToArray();
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
        catch (OperationCanceledException) { return Task.FromResult(Failed("CANCELLED")); }
        catch (MusicLibraryException error) { return Task.FromResult(Failed(error.Code)); }
        catch { return Task.FromResult(Failed("INVALID_REQUEST")); }
    }

    private JsonElement BeginLocked(string word, MusicSearchKind kind, string[] platforms, SearchOptions options)
    {
        keyword = word;
        var query = new Query(word, kind, platforms, options); queries[kind] = query;
        BeginRoundLocked(query);
        return Snapshot(query);
    }

    private async Task<JsonElement> ReplaceAsync(Replacement admitted, Query[] retired, CancellationToken caller)
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
        catch (OperationCanceledException) { return Failed("CANCELLED"); }
        catch (MusicLibraryException error) { return Failed(error.Code); }
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
                            { state.Error = result.Error; state.CheckedAt = result.CheckedAt; }
                            if (result.Error == "UNSUPPORTED") state.More = false;
                            halted = true; break;
                        }
                        if (chunk.Page != state.NextPage) { halted = true; break; }
                        state.CheckedAt = result.CheckedAt;
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
                    { entry.Used = ++cacheUse; return new(index, new(entry.Page, "", entry.Bytes, true, entry.CheckedAt), Task.CompletedTask); }
                    EraseLocked(key);
                }
        long identity; lock (gate) identity = identityEpochs.GetValueOrDefault(state.Platform);
        bool admitted = false, requested = false;
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
            physical = searchAsync(new(state.Platform, query.Keyword, query.Kind, chunk.Page, state.Paging.PageSize,
                query.Options.Refresh, chunk.Pages), linked.Token);
            var response = await physical.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var parsed = MusicSearchPage.Read(response, state.Platform, query.Kind, chunk.Page);
            result = new(parsed, "", response.GetRawText().Length * 2L, false, clock.GetUtcNow().ToUnixTimeMilliseconds());
            lock (gate)
                if (!closed && !stopping && identity == identityEpochs.GetValueOrDefault(state.Platform))
                    Store(query, state.Platform, chunk, result);
        }
        catch (Exception error)
        {
            result = new(null, token.IsCancellationRequested ? "CANCELLED" :
                deadline?.IsCancellationRequested == true ? "TIMEOUT" : ErrorCode(error), 0, false,
                requested ? clock.GetUtcNow().ToUnixTimeMilliseconds() : 0);
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
        while (cacheBytes + result.Bytes > CacheBudgetBytes && cache.Count > 0)
            EraseLocked(cache.MinBy(pair => pair.Value.Used).Key);
        cache[key] = new(result.Page!, clock.GetUtcNow() + query.Options.CacheTime, result.Bytes, result.CheckedAt) { Used = ++cacheUse };
        cacheBytes += result.Bytes;
    }
    private void EraseLocked(CacheKey key) { if (cache.Remove(key, out var entry)) cacheBytes -= entry.Bytes; }

    private bool Supports(string platform, MusicSearchKind kind)
    {
        var described = catalog.Describe();
        return described.TryGetProperty("platforms", out var providers) && providers.ValueKind == JsonValueKind.Object &&
            providers.TryGetProperty(platform, out var provider) && provider.ValueKind == JsonValueKind.Object &&
            provider.TryGetProperty("search", out var kinds) && kinds.ValueKind == JsonValueKind.Array &&
            kinds.EnumerateArray().Any(k => k.ValueKind == JsonValueKind.String && k.GetString() == Kind(kind));
    }

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
            SearchCandidate[] candidates; long version; FusionPlan plan; JsonArray displayed;
            lock (gate)
            {
                if (!Current(query)) { query.Computing = query.FusionRunning = false; return; }
                version = query.DataVersion; plan = query.Plan;
                candidates = plan == FusionPlan.Merge
                    ? query.States.SelectMany(s => s.RoundItems).ToArray()
                    : query.States.SelectMany(s => s.Items).ToArray();
                displayed = query.Items;
            }
            JsonArray? items = null; string error = "";
            try
            {
                items = MusicRankFusion.Fuse(candidates, query.Kind, query.Order, query.Lifetime.Token,
                    plan == FusionPlan.Merge ? displayed : null);
            }
            catch (OperationCanceledException) { }
            catch { error = "FUSION_FAILED"; }
            lock (gate)
            {
                if (!Current(query)) { query.Computing = query.FusionRunning = false; return; }
                if (version != query.DataVersion) continue;
                if (items is not null) { query.Items = items; query.FusedVersion = version; }
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
                    state.Page = 0; state.NextPage = 1; state.CheckedAt = 0;
                    state.More = true; state.Error = "STALE_SESSION";
                }
                // Remaining platforms' candidates are fused again from scratch.
                query.Items = []; QueueFusionLocked(query, FusionPlan.Full); PublishLocked(query);
            }
        }
        foreach (var pending in cancelled) Cancel(pending);
    }

    private bool Current(Query query) => !closed && !stopping && !query.Stopped &&
        queries.TryGetValue(query.Kind, out var value) && ReferenceEquals(value, query);
    private bool Current(Query query, PlatformState state, int generation) => Current(query) && state.Generation == generation;
    private void Guard() { if (stopping || closed) throw new MusicLibraryException("CLOSED"); }
    private static string Kind(MusicSearchKind kind) => kind == MusicSearchKind.Songs ? "songs" : "playlists";
    private static JsonElement Failed(string code) => JsonSerializer.SerializeToElement(new { ok = false, code });
    private static string ErrorCode(Exception error) => error switch
    {
        MusicPlatformException e when e.Code.Length is > 0 and <= 64 && e.Code.All(c => c is >= 'A' and <= 'Z' or '_' or >= '0' and <= '9') => e.Code,
        MusicLibraryException { Code: "UNSUPPORTED" or "IDENTITY_MISMATCH" or "LIMIT" } e => e.Code,
        OperationCanceledException => "CANCELLED",
        _ => "INVALID_RESPONSE"
    };
    private static void Cancel(CancellationTokenSource source) { try { source.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { } }

    private JsonElement Snapshot(Query query, bool includeCheckTimes = false)
    {
        bool partial = query.FusionError.Length > 0 || query.States.Any(s => s.Error.Length > 0);
        string code = query.Stopped ? "CANCELLED" : query.States.Length == 0 ? "NO_PLATFORMS" :
            query.FusionError.Length > 0 ? query.FusionError : partial ? "PARTIAL" : "";
        return LJ.Element(LJ.Obj(("ok", true), ("id", query.Id), ("revision", query.Revision),
            ("keyword", query.Keyword), ("kind", Kind(query.Kind)), ("round", query.Round), ("items", query.Items),
            ("more", !query.Stopped && query.States.Any(s => s.More)),
            // A cancelled round never settles; it is loading only while its physical work drains.
            ("loading", query.Computing || query.RoundOpen && !query.Stopped || query.States.Any(s => s.Loading)),
            ("partial", partial), ("code", code), ("cancelled", query.Stopped),
            ("platforms", LJ.Arr(query.States.Select(s =>
            {
                var value = LJ.Obj(("platform", s.Platform), ("page", s.Page),
                    ("count", s.Items.Count), ("more", !query.Stopped && s.More), ("loading", s.Loading),
                    ("error", s.Error));
                if (includeCheckTimes) value["checkedAt"] = s.CheckedAt;
                return value;
            })))));
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
            Notice notice; JsonElement? matching = null;
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

    private void Notify(Notice notice, Action<JsonElement>? observers, JsonElement value)
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
            try { ((Action<JsonElement>)observer)(value); } catch { /* An observer cannot break request retirement. */ }
            finally
            {
                // Child tasks may have captured this frame. Once the callback returns,
                // they are ordinary external callers, not an excuse to skip a barrier.
                Volatile.Write(ref frame.Active, 0); observerFrame.Value = previous;
            }
        }
    }
}

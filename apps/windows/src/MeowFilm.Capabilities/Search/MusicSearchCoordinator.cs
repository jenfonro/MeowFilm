using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

/// <summary>
/// Borrowed native catalog; one keyword, at most one songs and one playlists session.
/// No timers except per-request deadlines, no storage, implicit provider fallback or script calls.
/// </summary>
public sealed partial class MusicSearchCoordinator : IAsyncDisposable
{
    public static TimeSpan RequestTimeout => TimeSpan.FromSeconds(25);
    private static readonly string[] Platforms = ["wy", "tx", "kw", "kg", "mg"];
    private readonly IMusicPlatformCatalog catalog;
    private readonly Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> searchAsync;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim slots = new(5, 5);
    private readonly Dictionary<MusicSearchKind, Query> queries = [];
    private readonly Dictionary<MusicSearchKind, Notice> notices = [];
    private readonly AsyncLocal<ObserverFrame?> observerFrame = new();
    private Replacement? replacement;
    private Task publisher = Task.CompletedTask;
    private Task? preparation, resumption, disposal;
    private bool publishing, stopping, closed;
    private string keyword = "";

    private sealed class PlatformState(string platform)
    {
        internal readonly string Platform = platform;
        internal readonly List<SearchCandidate> Items = [];
        internal readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        internal int Page, NextPage = 1, Stalled, Generation;
        internal bool More = true, Loading;
        internal string Error = "";
        internal string TotalUnit = "items";
        internal long? Total;
        internal int? GroupCount, RawItemCount;
        internal CancellationTokenSource? Cancellation;
        internal Task Flight = Task.CompletedTask;
    }

    private sealed class Query(string keyword, MusicSearchKind kind, string[] platforms)
    {
        internal readonly string Id = Guid.NewGuid().ToString("N"), Keyword = keyword;
        internal readonly MusicSearchKind Kind = kind;
        internal readonly PlatformState[] States = platforms.Select(p => new PlatformState(p)).ToArray();
        internal readonly int Budget = Math.Min(2000, 5000 / Math.Max(1, platforms.Length));
        internal readonly CancellationTokenSource Lifetime = new();
        internal JsonArray Items = [];
        internal long Revision, DataVersion;
        internal bool Stopped, Computing, FusionRunning, Released, SettlementNoticePending;
        internal Task<JsonElement>? CancellationDrain;
        internal string FusionError = "";
        internal Task Fusion = Task.CompletedTask;
    }
    private sealed record Notice(Query Query, long Revision, JsonElement Value);
    private sealed class ObserverFrame { internal int Active = 1; }
    private sealed class Replacement(string keyword, MusicSearchKind kind, string[] platforms)
    {
        internal string Keyword { get; } = keyword;
        internal MusicSearchKind Kind { get; } = kind;
        internal string[] Platforms { get; } = platforms;
        internal Task<JsonElement> Work { get; set; } = Task.FromResult(Failed("SEARCH_BUSY"));
        internal bool Matches(string word, MusicSearchKind requestedKind, string[] requestedPlatforms) =>
            Keyword == word && Kind == requestedKind &&
            Platforms.ToHashSet(StringComparer.Ordinal).SetEquals(requestedPlatforms);
    }

    public MusicSearchCoordinator(IMusicPlatformCatalog catalog, TimeProvider? timeProvider = null,
        Func<MusicSearchRequest, CancellationToken, Task<JsonElement>>? searchAsync = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        clock = timeProvider ?? TimeProvider.System;
        this.searchAsync = searchAsync ?? catalog.SearchAsync;
    }
    public event Action<JsonElement>? Changed;

    public async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = LJ.Parameters(parameters, 4096);
            if (method == "music.search.start")
            {
                LJ.Fields(input, "keyword kind platforms", "keyword kind platforms");
                string word = LJ.S(input["keyword"]).Trim();
                LJ.Require(LJ.Id(word, 200));
                string kindName = LJ.Text(input["kind"], 16);
                LJ.Require(kindName is "songs" or "playlists");
                var kind = kindName == "songs" ? MusicSearchKind.Songs : MusicSearchKind.Playlists;
                var selected = LJ.Array(input["platforms"]);
                LJ.Require(selected.Count <= 5);
                var platforms = selected.Select(p => LJ.Text(p, 2)).Distinct(StringComparer.Ordinal).ToArray();
                LJ.Require(platforms.All(Platforms.Contains));
                Task<JsonElement> start;
                lock (gate)
                {
                    Guard();
                    if (replacement is { } pending)
                    {
                        if (!pending.Matches(word, kind, platforms)) return Failed("SEARCH_BUSY");
                        start = pending.Work;
                    }
                    else
                    {
                        if (word == keyword && queries.TryGetValue(kind, out var existing) && !existing.Stopped &&
                            existing.States.Select(s => s.Platform).ToHashSet(StringComparer.Ordinal).SetEquals(platforms))
                            return Snapshot(existing);
                        var retired = queries.Values.Where(q => word != keyword || q.Kind == kind).ToArray();
                        if (retired.Length == 0) return BeginLocked(word, kind, platforms);
                        foreach (var old in retired) StopLocked(old);
                        var admitted = new Replacement(word, kind, platforms);
                        replacement = admitted;
                        start = admitted.Work = ReplaceAsync(admitted, retired, cancellationToken);
                    }
                }
                return await start.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            LJ.Fields(input, method == "music.search.retry" ? "id platform" : "id",
                method == "music.search.retry" ? "id platform" : "id");
            string id = LJ.Text(input["id"], 32);
            Query query;
            Task<JsonElement>? cancellation = null;
            lock (gate)
            {
                Guard();
                query = queries.Values.FirstOrDefault(q => q.Id == id) ?? throw new MusicLibraryException("SEARCH_NOT_FOUND");
                switch (method)
                {
                    case "music.search.get": return Snapshot(query);
                    case "music.search.next":
                        if (!query.Stopped)
                            foreach (var state in query.States.Where(s => Eligible(query, s) && s.Error.Length == 0))
                                BeginFetchLocked(query, state, false);
                        PublishLocked(query); return Snapshot(query);
                    case "music.search.retry":
                        string platform = LJ.Text(input["platform"], 2);
                        var target = query.States.FirstOrDefault(s => s.Platform == platform) ??
                            throw new MusicLibraryException("INVALID_REQUEST");
                        if (!query.Stopped && !target.Loading && target.Error.Length > 0 && Eligible(query, target))
                            BeginFetchLocked(query, target, true);
                        PublishLocked(query); return Snapshot(query);
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

    private JsonElement BeginLocked(string word, MusicSearchKind kind, string[] platforms)
    {
        keyword = word;
        var query = new Query(word, kind, platforms); queries[kind] = query;
        foreach (var state in query.States) BeginFetchLocked(query, state, false);
        PublishLocked(query);
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
                return BeginLocked(admitted.Keyword, admitted.Kind, admitted.Platforms);
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

    private static bool Eligible(Query query, PlatformState state) => state.More && !state.Loading &&
        state.NextPage <= 100 && state.Page < 100 && state.Stalled < 4 && state.Items.Count < query.Budget;

    private void BeginFetchLocked(Query query, PlatformState state, bool refresh)
    {
        if (query.Stopped || !Eligible(query, state)) return;
        state.Loading = true; state.Error = ""; state.Cancellation = new();
        int generation = ++state.Generation, page = state.NextPage;
        var cancellation = state.Cancellation;
        state.Flight = Task.Run(() => FetchAsync(query, state, generation, page, refresh, cancellation));
    }

    private async Task FetchAsync(Query query, PlatformState state, int generation, int page, bool refresh,
        CancellationTokenSource cancellation)
    {
        using var deadline = new CancellationTokenSource(RequestTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(query.Lifetime.Token, cancellation.Token, deadline.Token);
        bool admitted = false;
        Task<JsonElement>? physical = null;
        try
        {
            await slots.WaitAsync(linked.Token).ConfigureAwait(false); admitted = true;
            if (!Supports(state.Platform, query.Kind)) throw new MusicLibraryException("UNSUPPORTED");
            physical = searchAsync(new(state.Platform, query.Keyword, query.Kind, page, 30, refresh), linked.Token);
            var response = await physical.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var parsed = MusicSearchPage.Read(response, state.Platform, query.Kind, page);
            lock (gate)
            {
                if (!Current(query, state, generation)) return;
                if (parsed.TotalUnit == "groups" &&
                    state.Items.Count + parsed.Items.Count(item => !state.Seen.Contains(item.Key)) > query.Budget)
                {
                    // The source budget is still hard, but never silently crop an expanded group page.
                    // Keep the last committed native cursor/statistics and report the explicit stop.
                    state.More = false; state.Error = "SEARCH_LIMIT"; PublishLocked(query); return;
                }
                int added = 0;
                foreach (var item in parsed.Items)
                    if (state.Items.Count < query.Budget && state.Seen.Add(item.Key)) { state.Items.Add(item); added++; }
                state.Page = page; state.NextPage = parsed.Next; state.More = parsed.More;
                state.TotalUnit = parsed.TotalUnit; state.Total = parsed.Total;
                state.GroupCount = parsed.GroupCount; state.RawItemCount = parsed.RawItemCount;
                state.Stalled = added == 0 ? state.Stalled + 1 : 0;
                if (state.More && (state.Page >= 100 || state.NextPage > 100 || state.Stalled >= 4 || state.Items.Count >= query.Budget))
                { state.More = false; state.Error = "SEARCH_LIMIT"; }
                if (added > 0 || query.Items.Count == 0) QueueFusionLocked(query);
                PublishLocked(query);
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (Current(query, state, generation))
                {
                    state.Error = deadline.IsCancellationRequested ? "TIMEOUT" : ErrorCode(error);
                    if (state.Error is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "STALE_SESSION" or "FORBIDDEN")
                    {
                        state.Items.Clear(); state.Seen.Clear(); state.Page = 0; state.NextPage = 1; state.Stalled = 0;
                        ClearPageStatistics(state);
                        query.Items = []; QueueFusionLocked(query);
                    }
                    PublishLocked(query);
                }
            }
        }
        finally
        {
            // A provider that ignores cancellation keeps its slot and loading state
            // until its physical operation has really settled.
            if (physical is not null) { try { await physical.ConfigureAwait(false); } catch { } }
            if (admitted) slots.Release();
            lock (gate)
            {
                if (ReferenceEquals(state.Cancellation, cancellation))
                {
                    state.Cancellation = null; state.Loading = false;
                    if (Current(query)) PublishLocked(query);
                }
            }
            cancellation.Dispose();
        }
    }

    private bool Supports(string platform, MusicSearchKind kind)
    {
        var described = catalog.Describe();
        return described.TryGetProperty("platforms", out var providers) && providers.ValueKind == JsonValueKind.Object &&
            providers.TryGetProperty(platform, out var provider) && provider.ValueKind == JsonValueKind.Object &&
            provider.TryGetProperty("search", out var kinds) && kinds.ValueKind == JsonValueKind.Array &&
            kinds.EnumerateArray().Any(k => k.ValueKind == JsonValueKind.String && k.GetString() == Kind(kind));
    }

    private void QueueFusionLocked(Query query)
    {
        query.DataVersion++; query.Computing = true;
        if (query.FusionRunning) return;
        query.FusionRunning = true;
        query.Fusion = Task.Run(() => Fuse(query));
    }

    private void Fuse(Query query)
    {
        while (true)
        {
            SearchCandidate[] candidates; long version;
            lock (gate)
            {
                if (!Current(query)) { query.Computing = query.FusionRunning = false; return; }
                version = query.DataVersion; candidates = query.States.SelectMany(s => s.Items).ToArray();
            }
            JsonArray? items = null; string error = "";
            try { items = MusicRankFusion.Fuse(candidates, query.Kind, query.Lifetime.Token); }
            catch (OperationCanceledException) { }
            catch { error = "FUSION_FAILED"; }
            lock (gate)
            {
                if (!Current(query)) { query.Computing = query.FusionRunning = false; return; }
                if (version != query.DataVersion) continue;
                if (items is not null) query.Items = items;
                query.FusionError = error; query.Computing = query.FusionRunning = false;
                PublishLocked(query); return;
            }
        }
    }

    /// <summary>
    /// Retire affected platform generations in both kinds synchronously, without retiring the query ids.
    /// Physical flights remain tracked (and loading) until settled; retry/close cannot bypass them.
    /// This means genuine account change/revocation, not an unclassified initial-restore notification.
    /// The account owner separately invalidates its borrowed catalog.
    /// </summary>
    public void Invalidate(string? platform = null)
    {
        if (platform is not null && !Platforms.Contains(platform)) throw new ArgumentException("INVALID_PLATFORM", nameof(platform));
        List<CancellationTokenSource> cancelled = [];
        lock (gate)
        {
            if (stopping || closed) return;
            foreach (var query in queries.Values.Where(q => !q.Stopped))
            {
                var affected = query.States.Where(s => platform is null || s.Platform == platform).ToArray();
                if (affected.Length == 0) continue;
                foreach (var state in affected)
                {
                    state.Generation++;
                    if (state.Cancellation is { } pending) cancelled.Add(pending);
                    state.Items.Clear(); state.Seen.Clear(); state.Page = 0; state.NextPage = 1; state.Stalled = 0;
                    ClearPageStatistics(state);
                    state.More = true; state.Error = "STALE_SESSION";
                }
                query.Items = []; QueueFusionLocked(query); PublishLocked(query);
            }
        }
        foreach (var pending in cancelled) Cancel(pending);
    }

    private bool Current(Query query) => !closed && !stopping && !query.Stopped &&
        queries.TryGetValue(query.Kind, out var value) && ReferenceEquals(value, query);
    private static void ClearPageStatistics(PlatformState state)
    { state.TotalUnit = "items"; state.Total = null; state.GroupCount = state.RawItemCount = null; }
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

    private JsonElement Snapshot(Query query)
    {
        bool partial = query.FusionError.Length > 0 || query.States.Any(s => s.Error.Length > 0);
        string code = query.Stopped ? "CANCELLED" : query.States.Length == 0 ? "NO_PLATFORMS" :
            query.FusionError.Length > 0 ? query.FusionError : partial ? "PARTIAL" : "";
        return LJ.Element(LJ.Obj(("ok", true), ("id", query.Id), ("revision", query.Revision),
            ("keyword", query.Keyword), ("kind", Kind(query.Kind)), ("items", query.Items),
            ("more", !query.Stopped && query.States.Any(s => s.Error.Length == 0 && s.More && s.Page < 100 &&
                s.NextPage <= 100 && s.Stalled < 4 && s.Items.Count < query.Budget)),
            ("loading", query.Computing || query.States.Any(s => s.Loading)),
            ("partial", partial), ("code", code), ("cancelled", query.Stopped),
            ("platforms", LJ.Arr(query.States.Select(s => LJ.Obj(("platform", s.Platform), ("page", s.Page),
                ("count", s.Items.Count), ("more", !query.Stopped && s.More), ("loading", s.Loading), ("error", s.Error),
                ("totalUnit", s.TotalUnit), ("total", s.Total), ("groupCount", s.GroupCount), ("rawItemCount", s.RawItemCount)))))));
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
            Notice notice;
            lock (gate)
            {
                if (notices.Count == 0) { publishing = false; return; }
                var first = notices.First(); notice = first.Value; notices.Remove(first.Key);
                if (closed || stopping || !queries.TryGetValue(notice.Query.Kind, out var current) ||
                    !ReferenceEquals(current, notice.Query) || current.Revision != notice.Revision) continue;
            }
            foreach (var observer in Changed?.GetInvocationList() ?? [])
            {
                // An earlier observer can synchronously invalidate an account or close the owner.
                // Do not deliver that now-retired notice to the remaining observers.
                lock (gate)
                    if (closed || stopping || !queries.TryGetValue(notice.Query.Kind, out var current) ||
                        !ReferenceEquals(current, notice.Query) || current.Revision != notice.Revision) break;
                var previous = observerFrame.Value;
                var frame = new ObserverFrame(); observerFrame.Value = frame;
                try { ((Action<JsonElement>)observer)(notice.Value); } catch { /* An observer cannot break request retirement. */ }
                finally
                {
                    // Child tasks may have captured this frame. Once the callback returns,
                    // they are ordinary external callers, not an excuse to skip a barrier.
                    Volatile.Write(ref frame.Active, 0); observerFrame.Value = previous;
                }
            }
        }
    }
}

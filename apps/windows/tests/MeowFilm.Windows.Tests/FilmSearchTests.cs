using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class FilmSearchTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("complete full projections preserve numeric zero string zero raw fields and same-source aggregate members", CompletePages);
        yield return Case("first-round stream and later appended cards keep the exact Node order without native sorting", RoundOrder);
        yield return Case("failed empty unavailable and no-source channels stay distinct; more is explicit", ChannelStates);
        yield return Case("new query and source configuration changes revoke old UI publication", Invalidation);
        yield return Case("stop same-word resume and disconnect reuse Node session with monotonic complete snapshots", Resume);
        yield return Case("stop fences late SSE and stop receipts update status without publishing new cards", StopPublication);
        yield return Case("accepted open stopped or retired before receipt keeps only its ID until explicit resume or release", OpenPublication);
        yield return Case("close waits for accepted open then stops releases and drains observation", Close);
        yield return Case("runtime unavailable and malformed snapshots remain real errors", Errors);
        yield return Case("Node source snapshots preserve enabled search scope order and cover option", FilmSourcesTests.SearchWire);
    }
    private static TestCase Case(string name, Func<Task> action) => new("film-search: " + name, () => MusicQualityConsumerTests.Sta(action));
    private static void Check(bool value, string reason) => SearchFixture.Check(value, reason);
    private static async Task CompletePages()
    {
        await using var f = new SearchFixture();
        var raw = Enumerable.Range(0, 151).Select(index => SearchFixture.Member(f.Sources.Sources[0], index, "原名" + index)).ToArray();
        var zero = SearchFixture.Member(f.Sources.Sources[0], "0", "原名0");
        raw = raw.Append(zero).ToArray();
        var cards = new[] { SearchFixture.Card("group:original", "site", "Node代表", raw) };
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), cards, raw, hasMore: true);
        var session = f.Service.Start("原查询2"); await SearchFixture.Settle(session);
        Check(session.RawItems.Count == 152 && session.Items.Count == 1 && session.CanLoadMore, "full page and same-source aggregation are consumed without limits");
        Check(JsonElement.DeepEquals(session.Items[0], cards[0]) && session.Items[0].GetProperty("aggregateItems").GetArrayLength() == 152, "all members and original fields survive");
        Check(session.RawItems[0].GetProperty("detailId").ValueKind == JsonValueKind.Number &&
            session.RawItems[^1].GetProperty("detailId").ValueKind == JsonValueKind.String &&
            session.RawItems[0].GetProperty("raw").GetProperty("unknown").GetString() == "  原始字段  ", "0 and string zero are different intact identities");
        var open = f.Runtime.Calls.Single(call => call.Method == "film.search.open").Input;
        Check(open.GetProperty("query").GetString() == "原查询2" && !open.TryGetProperty("rules", out _) &&
            open.GetProperty("sources")[0].GetProperty("aggregateImages").GetBoolean() &&
            open.GetProperty("sources")[0].GetProperty("order").GetInt32() == 0, "only source configuration and query go to Node");
        Check(ReferenceEquals(session, f.Service.Start("原查询2")) && f.Runtime.SearchOpens == 1, "same-word reentry reuses the session");
    }
    private static async Task RoundOrder()
    {
        await using var f = new SearchFixture();
        var member = SearchFixture.Member(f.Sources.Sources[0], "1", "无关");
        var first = SearchFixture.Card("group:low", "site", "无关", [member]);
        var high = SearchFixture.Card("group:high", "site", "目标", [SearchFixture.Member(f.Sources.Sources[0], "2", "目标")]);
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [first], [member], loading: true);
        var session = f.Service.Start("目标"); await session.Work;
        await f.Service.LoadMoreAsync();
        Check(!f.Runtime.Calls.Any(call => FilmJson.Text(call.Input, "action") == "more"), "first round cannot load more");
        f.Runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [high, first], [member], revision: 2, hasMore: true));
        await SearchFixture.Until(() => session.Revision == 2);
        var upgraded = SearchFixture.Card("group:low", "site", "新代表", [member, SearchFixture.Member(f.Sources.Sources[0], "3", "无关 第二季")]);
        var appended = SearchFixture.Card("group:new", "site", "新增", [SearchFixture.Member(f.Sources.Sources[0], "4", "新增")]);
        f.Runtime.SearchCommand = (_, _) => SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query,
            [high, upgraded, appended], [member], revision: 3, round: 2);
        await f.Service.LoadMoreAsync();
        Check(session.Items.Select(item => FilmJson.Text(item, "displayKey")).SequenceEqual(["group:high", "group:low", "group:new"]) &&
            FilmJson.Text(session.Items[1], "title") == "新代表" && session.Items[1].GetProperty("aggregateItems").GetArrayLength() == 2,
            "Node's first-round ranking later positions and in-place member/representative update remain exact");
        Check(f.Runtime.Calls.Count(call => call.Method == "film.search.command" && FilmJson.Text(call.Input, "action") == "more") == 1, "one user action sends one business command");
    }
    private static async Task ChannelStates()
    {
        await using var f = new SearchFixture();
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [], [], siteState: "failed", metadataState: "unavailable", hasMore: true);
        var failed = f.Service.Start("失败"); await SearchFixture.Settle(failed);
        Check(failed.SitesState == FilmSearchChannelState.Error && failed.MetadataState == FilmSearchChannelState.Unavailable && failed.CanLoadMore,
            "source failures remain retryable and metadata unavailable remains distinct");
        await Task.Yield(); Check(f.Runtime.Calls.Count(call => call.Method == "film.search.command") == 0, "no automatic retry");
        await f.Service.LoadMoreAsync();
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [], []);
        var empty = f.Service.Start("空页"); await SearchFixture.Settle(empty);
        Check(empty.SitesState == FilmSearchChannelState.Completed && !empty.CanLoadMore, "successful empty result is complete");
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [], [], noSources: true);
        var none = f.Service.Start("无源"); await SearchFixture.Settle(none);
        Check(none.SitesState == FilmSearchChannelState.NoSources, "no sources is not failure or successful empty search");
    }
    private static async Task Invalidation()
    {
        await using var f = new SearchFixture();
        var old = f.Service.Start("旧词"); await SearchFixture.Settle(old);
        var latest = f.Service.Start("新词"); await SearchFixture.Settle(latest);
        f.Runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(old.Id), old.Query, [], [], revision: 99));
        await Task.Yield();
        Check(ReferenceEquals(f.Service.Current, latest) && old.Revision < 99, "late retired snapshots cannot publish");
        f.Sources.Sources[0] = SearchFixture.Source("a", generation: 2); f.Sources.Publish();
        Check(f.Service.Current is null && latest.Retired && f.Runtime.SearchOpens == 2, "generation changes revoke without implicitly searching");
        var next = f.Service.Start("新词"); await SearchFixture.Settle(next);
        Check(next != latest && f.Runtime.SearchOpens == 3, "explicit reentry opens with the new source snapshot");
        f.Sources.Sources[0] = f.Sources.Sources[0] with { Site = f.Sources.Sources[0].Site with { Order = 2 } };
        f.Sources.Publish(); Check(f.Service.Current is null, "source ordering changes also revoke derived display");
    }
    private static async Task Resume()
    {
        await using var f = new SearchFixture();
        var session = f.Service.Start("保留"); await SearchFixture.Settle(session);
        var original = session.Items[0].GetRawText();
        await f.Service.StopAsync();
        Check(!session.CanLoadMore, "stopped session cannot schedule more");
        Check(ReferenceEquals(session, f.Service.Start("保留")), "same-word return uses the same Node identity");
        await session.Work;
        Check(f.Runtime.SearchOpens == 1 && f.Runtime.Calls.Any(call => call.Method == "film.search.snapshot") &&
            f.Runtime.Calls.Any(call => FilmJson.Text(call.Input, "action") == "resume"), "return snapshots and resumes without another open");
        f.Runtime.DisconnectSearch(Guid.Parse(session.Id));
        await SearchFixture.Until(() => session.Error.Length > 0);
        await session.Work;
        Check(session.Error == "DISCONNECTED" && session.Items[0].GetRawText() == original && !session.CanLoadMore, "disconnect retains display and reports a real error");
        Check(f.Runtime.Calls.Count(call => call.Method == "film.search.command" && FilmJson.Text(call.Input, "action") == "stop") == 2 &&
            !f.Runtime.Revoked.IsCancellationRequested, "event disconnect explicitly stops new scheduling without revoking the data producer");
        await session.Observing;
        // A fresh event subscription gets Node's latest full snapshot.
        f.Runtime.ReconnectSearch(Guid.Parse(session.Id));
        f.Service.Start("保留"); await session.Work;
        var newest = SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [], [], revision: 8);
        f.Runtime.PublishSearch(newest); await SearchFixture.Until(() => session.Revision == 8);
        f.Runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [SearchFixture.Card("old", "site", "旧", [])], [], revision: 7));
        await Task.Yield(); Check(session.Revision == 8 && session.Items.Count == 0 && f.Runtime.SearchOpens == 1, "out-of-order snapshots cannot roll back current state");
    }
    private static async Task Close()
    {
        var receipt = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = new SearchFixture();
        f.Runtime.Respond = async (method, input, _) =>
        {
            if (method != "film.search.open") return f.Runtime.Handle(method, input);
            entered.SetResult(); return await receipt.Task;
        };
        var session = f.Service.Start("关闭"); await entered.Task;
        var close = f.Service.PrepareShutdownAsync(); Check(!close.IsCompleted, "accepted open is drained before close");
        f.Runtime.Respond = null;
        var envelope = f.Runtime.Handle("film.search.open", FilmJson.Encode(new { query = session.Query }));
        receipt.SetResult(envelope); await close;
        f.Service.Resume(); await session.Work;
        Check(f.Runtime.Calls.Any(call => FilmJson.Text(call.Input, "action") == "stop") &&
            f.Runtime.Calls.Any(call => FilmJson.Text(call.Input, "action") == "resume"), "shutdown preparation can resume the same session");
        await f.Service.DisposeAsync();
        Check(f.Service.TrackedSessions == 0 && f.Runtime.Calls.Count(call => call.Method == "film.search.release") == 1 && !f.Runtime.Revoked.IsCancellationRequested,
            "search release drains native observation without closing the borrowed runtime");

        await using var retired = new SearchFixture();
        var old = retired.Service.Start("退役关闭"); await SearchFixture.Settle(old);
        var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        retired.Runtime.Respond = (method, input, _) =>
        {
            if (method != "film.search.release") return Task.FromResult(retired.Runtime.Handle(method, input));
            releasing.TrySetResult(); return released.Task;
        };
        try
        {
            retired.Service.InvalidateQuery("新词"); await releasing.Task;
            var drain = retired.Service.PrepareShutdownAsync();
            Check(!drain.IsCompleted, "shutdown drains an already accepted retired release");
            released.SetResult(FilmSearchConsumerRuntime.Success(new { released = true })); await drain;
            Check(retired.Runtime.Calls.Count(call => call.Method == "film.search.command" && FilmJson.Text(call.Input, "action") == "stop") == 1 &&
                retired.Runtime.Calls.Count(call => call.Method == "film.search.release") == 1,
                "shutdown never schedules another stop behind the retired session's release");
        }
        finally { released.TrySetResult(FilmSearchConsumerRuntime.Failure("CLOSED")); }
    }
    private static async Task StopPublication()
    {
        await using var f = new SearchFixture();
        f.Runtime.SearchOpen = (id, input) => SearchFixture.Snapshot(id, FilmJson.Text(input, "query"),
            [SearchFixture.Card("original", "site", "已显示", [])], [], loading: true);
        var session = f.Service.Start("停止原词"); await session.Work;
        var original = session.Items[0].GetRawText();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken accepted = default;
        f.Runtime.Respond = (method, input, token) =>
        {
            if (method != "film.search.command" || FilmJson.Text(input, "action") != "stop")
                return Task.FromResult(f.Runtime.Handle(method, input));
            accepted = token; entered.TrySetResult(); return receipt.Task;
        };
        try
        {
            var stopped = f.Service.StopAsync(); await entered.Task;
            int deliveries = f.Runtime.SearchDeliveries;
            var late = SearchFixture.Card("late", "site", "不得发布", []);
            f.Runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [late], [late], revision: 2));
            await SearchFixture.Until(() => f.Runtime.SearchDeliveries > deliveries);
            Check(session.Items[0].GetRawText() == original && session.RawItems.Count == 0 && session.Loading && !stopped.IsCompleted,
                "late SSE cannot change the stopped display while the accepted stop RPC is pending");
            var ack = SearchFixture.Snapshot(Guid.Parse(session.Id), session.Query, [late], [late], revision: 3,
                siteState: "cancelled", metadataState: "cancelled");
            f.Runtime.PublishSearch(ack);
            receipt.SetResult(FilmSearchConsumerRuntime.Success(ack)); await stopped;
            Check(!session.Loading && session.SitesState == FilmSearchChannelState.Cancelled &&
                session.MetadataState == FilmSearchChannelState.Cancelled && session.Items[0].GetRawText() == original &&
                session.RawItems.Count == 0 && !accepted.IsCancellationRequested && !f.Runtime.Revoked.IsCancellationRequested,
                "stop receipt updates cancellation status but not cards and never aborts the borrowed producer");
            f.Runtime.Respond = null;
            f.Service.Start(session.Query); await session.Work;
            Check(f.Runtime.SearchOpens == 1 && session.Items.Single().GetProperty("title").GetString() == "不得发布",
                "only explicit resume reacquires the latest full Node projection without reopening");
        }
        finally { receipt.TrySetResult(FilmSearchConsumerRuntime.Failure("CLOSED")); }
    }
    private static async Task OpenPublication()
    {
        foreach (string reason in new[] { "stop", "query", "sources" })
        {
            bool retire = reason != "stop";
            await using var f = new SearchFixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var receipt = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken accepted = default;
            f.Runtime.Respond = (method, input, token) =>
            {
                if (method != "film.search.open") return Task.FromResult(f.Runtime.Handle(method, input));
                accepted = token; entered.TrySetResult(); return receipt.Task;
            };
            try
            {
                var session = f.Service.Start("open竞态"); await entered.Task;
                var publications = new List<int>(); session.Changed += (_, _) => publications.Add(session.Items.Count);
                Task stopped;
                if (reason == "query") { f.Service.InvalidateQuery("另一个词"); stopped = session.Work; }
                else if (reason == "sources")
                {
                    f.Sources.Sources[0] = SearchFixture.Source("a", generation: 2); f.Sources.Publish();
                    stopped = session.Work;
                }
                else stopped = f.Service.StopAsync();
                f.Runtime.Respond = null;
                var allocated = f.Runtime.Handle("film.search.open", FilmJson.Encode(new { query = session.Query }));
                receipt.SetResult(allocated); await stopped;
                Check(session.Id == FilmJson.Text(allocated.GetProperty("value"), "sessionId") && session.Items.Count == 0 &&
                    session.RawItems.Count == 0 && publications.All(count => count == 0) &&
                    !accepted.IsCancellationRequested && !f.Runtime.Revoked.IsCancellationRequested,
                    "an accepted late open binds only its ID; stopped and retired presentations never receive its cards");
                if (retire)
                {
                    await SearchFixture.Until(() => f.Service.TrackedSessions == 0);
                    Check(f.Service.Current is null && f.Runtime.Calls.Count(call => call.Method == "film.search.release") == 1,
                        "retired open is stopped and released after its real allocation receipt");
                }
                else
                {
                    Check(f.Runtime.Calls.Any(call => FilmJson.Text(call.Input, "action") == "stop"), "late open receives stop after allocation");
                    f.Service.Start(session.Query); await session.Work;
                    Check(session.Items.Count == 1 && f.Runtime.SearchOpens == 1, "explicit resume restores late Node cards without duplicate open");
                }
            }
            finally { receipt.TrySetResult(FilmSearchConsumerRuntime.Failure("CLOSED")); }
        }
        await using var unopened = new SearchFixture();
        var pending = unopened.Service.Start("未接纳");
        await unopened.Service.StopAsync();
        Check(unopened.Runtime.SearchOpens == 0 && pending.Items.Count == 0 && !pending.Loading &&
            pending.SitesState == FilmSearchChannelState.Cancelled && pending.MetadataState == FilmSearchChannelState.Cancelled,
            "stop before admission schedules no open and settles the presentation as cancelled");
        unopened.Service.Start(pending.Query); await pending.Work;
        Check(unopened.Runtime.SearchOpens == 1 && pending.Items.Count == 1, "explicit return can perform the first unaccepted open once");
    }
    private static async Task Errors()
    {
        await using var f = new SearchFixture();
        f.Runtime.Respond = (_, _, _) => Task.FromResult(FilmSearchConsumerRuntime.Failure("CAPABILITY_UNAVAILABLE"));
        var failed = f.Service.Start("不可用"); await failed.Work;
        Check(failed.Error == "CAPABILITY_UNAVAILABLE" && failed.Items.Count == 0 && !failed.Loading && f.Runtime.SearchOpens == 1, "no fake success or old execution fallback");
        f.Runtime.Respond = null;
        f.Runtime.SearchOpen = (_, _) => FilmJson.Encode(new { sessionId = "bad", query = "无效" });
        var malformed = f.Service.Start("无效"); await malformed.Work;
        Check(malformed.SitesState == FilmSearchChannelState.Error && malformed.Error.Length > 0, "invalid snapshots surface failure");
    }
}

internal sealed class SearchFixture : IAsyncDisposable
{
    private readonly bool ownsRuntime;
    internal SearchFixture(SearchSources? sources = null, FilmSearchConsumerRuntime? runtime = null)
    {
        ownsRuntime = runtime is null;
        Sources = sources ?? new(Source("a")); Runtime = runtime ?? new();
        Runtime.SearchOpen ??= (id, input) => Snapshot(id, FilmJson.Text(input, "query"),
            [Card("group:default", "site", "目标", [Member(Source("a"), "same", "目标")])], [Member(Source("a"), "same", "目标")], hasMore: true);
        Runtime.SearchHandoff ??= input => Handoff(Guid.Parse(FilmJson.Text(input, "sessionId")), FilmJson.Text(input, "selectionId"),
            FilmJson.Text(Runtime.SearchValue(Guid.Parse(FilmJson.Text(input, "sessionId"))), "query"), new("group:default", "group", "目标", Members: FilmJson.Encode(new[] { new { sourceKey = Source("a").Key, detailId = "same" } })),
            Sources.CurrentSearchSources.Select(source => source.Wire).ToArray());
        Service = new(Runtime.CaptureConnectionAsync, Sources);
    }
    internal SearchSources Sources { get; }
    internal FilmSearchConsumerRuntime Runtime { get; }
    internal FilmSearchService Service { get; }
    internal static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    internal static string Text(JsonElement value, string key) => FilmSearchJson.Text(value, key);
    internal static JsonElement Page(int pages, params (string Id, string Title, string Remark)[] rows) => Json(new
    { pagecount = pages, list = rows.Select(row => new { vod_id = row.Id, vod_name = row.Title, vod_remarks = row.Remark }).ToArray() });
    internal static JsonElement Tmdb(string type, long id, string title, string overview) => Json(new
    { results = new[] { new { media_type = type, id, name = title, title, overview, first_air_date = "2023-01-01", release_date = "2023-01-01", poster_path = "/images/tmdb/poster.jpg" } } });
    internal static FilmSearchSource Source(string installation, string site = "same", int order = 0, long generation = 1)
    {
        string[] actions = ["search"]; var methods = new Dictionary<string, string>();
        return new(new(installation, installation, "local", null, true, 1, null, "idle", null, generation, "ready", "fixture", null,
            [new(site, installation, "fixture", actions, methods)]),
            new(new(installation, site), installation, "fixture", actions, methods, new(AggregateImages: true), order));
    }
    internal static JsonElement Member(FilmSearchSource source, object detailId, string title) => FilmJson.Encode(new
    {
        id = "raw:" + JsonSerializer.Serialize(detailId), displayKey = "site:" + source.Key + ":" + JsonSerializer.Serialize(detailId), sourceKind = "site",
        siteKey = source.Key, installationId = source.Cat.InstallationId, generation = source.Cat.Generation, siteId = source.Site.Key.SiteKey,
        detailId, title, siteLabel = source.Site.Name, textBadge = "高清", poster = "", raw = new { vod_id = detailId, vod_name = title, vod_remarks = "  原备注  ", unknown = "  原始字段  " }
    });
    internal static JsonElement Card(string key, string kind, string title, JsonElement[] members, string? label = null) => FilmJson.Encode(new
    {
        id = key, displayKey = key, sourceKind = kind, title, poster = "", textBadge = "2023", siteLabel = label ?? (kind == "tmdb" ? "剧集" : "聚合"),
        aggregateItems = members, tmdbType = kind == "tmdb" ? "tv" : "", tmdbId = kind == "tmdb" ? 7 : 0
    });
    internal static JsonElement Snapshot(Guid id, string query, JsonElement[] cards, JsonElement[] rawCards,
        long revision = 1, int round = 1, bool loading = false, bool hasMore = false, string siteState = "completed",
        string metadataState = "completed", bool noSources = false, JsonElement? metadataSearch = null) => FilmJson.Encode(new
    {
        sessionId = id, query, revision, sequence = revision, round, loading, hasMore, noSources, siteState, metadataState,
        cards, rawCards, siteItems = rawCards, tmdbItems = Array.Empty<object>(), metadataSearch, metadataQuery = query, metadataDetails = new { }
    });
    internal static JsonElement Handoff(Guid id, string selectionId, string query, FilmPlaybackTarget target,
        IReadOnlyList<JsonElement> sources, JsonElement? results = null, JsonElement? search = null, JsonElement? metadata = null,
        JsonElement? cacheEntries = null, JsonElement? resultHints = null, long revision = 1) => FilmJson.Encode(new
    {
        target = target.Wire(), query, sources, results, search, metadata, cacheEntries, resultHints, selectionId, revision,
        searchReference = new { sessionId = id, selectionId }
    });
    internal static async Task Settle(FilmSearchSession session)
    {
        await session.Work.WaitAsync(TimeSpan.FromSeconds(20));
        await Until(() => !session.Loading);
    }
    internal static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!done()) await Task.Delay(5, timeout.Token);
    }
    public async ValueTask DisposeAsync() { await Service.DisposeAsync(); if (ownsRuntime) Runtime.Dispose(); }
}
internal sealed class SearchSources(params FilmSearchSource[] sources) : IFilmSearchSources
{
    internal List<FilmSearchSource> Sources { get; } = sources.ToList();
    public event EventHandler? Changed;
    public IReadOnlyList<FilmSearchSource> CurrentSearchSources => Sources.ToArray();
    public Task<IReadOnlyList<FilmSearchSource>> ReadSearchSourcesAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(CurrentSearchSources); }
    internal void Publish() => Changed?.Invoke(this, EventArgs.Empty);
}

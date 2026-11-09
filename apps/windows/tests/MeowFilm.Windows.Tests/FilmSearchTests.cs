using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Scripting;
using MeowFilm.Platform.Scripting;

namespace MeowFilm.Windows.Tests;

public static class FilmSearchTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("complete pages exceed twelve and one hundred with independent cursors", CompletePages);
        yield return Case("missing page counts repeated identities and silent explicit same-page retry", PageBoundaries);
        yield return Case("first-round late ranking and later-round aggregate upgrades freeze both orders", RoundOrder);
        yield return Case("TMDB raw fallback boundaries preserve original payload and effective query", MetadataFallback);
        yield return Case("TMDB details cache by type and ID and more never repeats metadata", MetadataCache);
        yield return Case("new-query cancellation fences noncooperative old results", QueryCancellation);
        yield return Case("retired physical work releases owner tracking while playback retains its snapshot", RetiredSnapshot);
        yield return Case("all source failures remain retryable and distinct from successful empty results", AllSourceFailures);
        yield return Case("source generation replacement recovers the same query without refetching retained pages", GenerationRecovery);
        yield return Case("prepare resume retains completed pages and metadata with a fresh lease", PrepareResume);
        yield return Case("prepare resume recovers only interrupted source pages and missing metadata details", PrepareResumeInFlight);
        yield return Case("hanging detail receives cancellation and physically drains before engine close", DetailShutdown);
        yield return Case("cross-query detail flights cannot inherit another query cancellation or cache late data", DetailOwnership);
        yield return Case("in-flight playback follows original members into a late TMDB carrier", LateCarrier);
        yield return Case("real Cat transport selects enabled searchable sources and sends original query and page", FilmSourcesTests.SearchWire);
    }
    private static TestCase Case(string name, Func<Task> action) =>
        new("film-search: " + name, () => MusicQualityConsumerTests.Sta(action));

    private static async Task CompletePages()
    {
        var sources = new SearchSources(SearchFixture.Source("a", order: 0), SearchFixture.Source("b", order: 1));
        sources.Read = (source, _, page, _) => Task.FromResult(SearchFixture.Json(new
        {
            pagecount = source.Cat.InstallationId == "a" ? 2 : 1,
            list = Enumerable.Range(page == 1 ? 0 : 151, source.Cat.InstallationId == "a" ? 151 : 17)
                .Select(id => new { vod_id = id.ToString(), vod_name = source.Cat.InstallationId + id + "独立影片" }).ToArray()
        }));
        await using var f = new SearchFixture(sources);
        var session = f.Service.Start("独立影片");
        await SearchFixture.Settle(session);
        SearchFixture.Check(session.RawItems.Count == 168 && session.Items.Count == 168, "both full first pages survive native mapping");
        SearchFixture.Check(session.MetadataState == FilmSearchChannelState.Unavailable && session.CanLoadMore,
            "production-shaped missing metadata is unavailable, not a fake empty success");
        await f.Service.LoadMoreAsync();
        SearchFixture.Check(session.RawItems.Count == 319 && !session.CanLoadMore, "full second page has no client-side limit");
        SearchFixture.Check(sources.Calls.Select(call => (call.Source.Cat.InstallationId, call.Page))
            .SequenceEqual(new[] { ("a", 1), ("b", 1), ("a", 2) }), "each source advances independently");
        SearchFixture.Check(ReferenceEquals(session, f.Service.Start("独立影片")) && sources.Calls.Count == 3,
            "same word reuses the settled snapshot");
    }

    private static async Task PageBoundaries()
    {
        var sources = new SearchSources(SearchFixture.Source("a"), SearchFixture.Source("b", order: 1));
        int failures = 0;
        sources.Read = (source, _, page, _) =>
        {
            if (source.Cat.InstallationId == "b" && failures++ == 0) throw new InvalidOperationException("fixture source failure");
            return Task.FromResult(SearchFixture.Json(new { list = source.Cat.InstallationId == "a" || page == 1
                ? new[] { new { vod_id = "same", vod_name = source.Cat.InstallationId + "影片" } } : [] }));
        };
        await using var f = new SearchFixture(sources);
        var session = f.Service.Start("影片"); await SearchFixture.Settle(session);
        SearchFixture.Check(session.Items.Count == 1 && session.SitesState == FilmSearchChannelState.Completed && session.CanLoadMore,
            "single source failure is silent and does not prevent round settlement");
        await Task.Yield(); SearchFixture.Check(sources.Calls.Count == 2, "no automatic source retry");
        await f.Service.LoadMoreAsync();
        SearchFixture.Check(session.RawItems.Count == 2 && session.CanLoadMore, "repeated page deduplicates and the failed source explicitly retries page one");
        await f.Service.LoadMoreAsync();
        SearchFixture.Check(!session.CanLoadMore && sources.Calls.Select(call => (call.Source.Cat.InstallationId, call.Page))
            .SequenceEqual(new[] { ("a", 1), ("b", 1), ("a", 2), ("b", 1), ("b", 2) }),
            "without metadata duplicate/empty pages terminate; successful source never restarts");
    }

    private static async Task RoundOrder()
    {
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new SearchSources(SearchFixture.Source("a"), SearchFixture.Source("b", order: 1));
        sources.Read = (source, _, page, _) => source.Cat.InstallationId == "b" ? release.Task : Task.FromResult(page == 1
            ? SearchFixture.Page(2, ("low", "无关", ""))
            : SearchFixture.Page(2, ("upgrade", "无关", ""), ("new", "新增", "")));
        await using var f = new SearchFixture(sources);
        var session = f.Service.Start("目标");
        try
        {
            await SearchFixture.Until(() => session.Items.Count == 1);
            SearchFixture.Check(!session.CanLoadMore && SearchFixture.Text(session.Items[0], "title") == "无关", "first source streams while more remains closed");
            await f.Service.LoadMoreAsync(); SearchFixture.Check(sources.Calls.Count == 2, "first round is still in progress");
            release.TrySetResult(SearchFixture.Page(1, ("high", "目标", "")));
            await SearchFixture.Settle(session);
            SearchFixture.Check(SearchFixture.Text(session.Items[0], "title") == "目标", "late high-relevance first-round card precedes earlier low hit");
            var order = session.Items.Select(item => SearchFixture.Text(item, "displayKey")).ToArray();
            var rawOrder = session.RawItems.Select(item => SearchFixture.Text(item, "displayKey")).ToArray();
            var original = new FilmSearchContext(session, session.Items[1]);
            await f.Service.LoadMoreAsync();
            SearchFixture.Check(order.SequenceEqual(session.Items.Take(order.Length).Select(item => SearchFixture.Text(item, "displayKey"))), "old aggregate positions remain frozen");
            SearchFixture.Check(rawOrder.SequenceEqual(session.RawItems.Take(rawOrder.Length).Select(item => SearchFixture.Text(item, "displayKey"))), "raw positions are independently frozen");
            SearchFixture.Check(original.Sources.Count == 2 && SearchFixture.Text(original.CurrentItem, "siteLabel") == "聚合",
                "ordinary selection upgrades in place with all members including the new page");
        }
        finally { release.TrySetResult(SearchFixture.Page(1)); }
    }

    private static async Task MetadataFallback()
    {
        foreach (string mode in new[] { "success", "person", "empty", "failure" })
        {
            var metadata = new SearchMetadata
            {
                ReadSearch = (word, _) => mode == "failure" ? Task.FromException<JsonElement>(new InvalidOperationException("search failed")) :
                    Task.FromResult(mode == "person" ? SearchFixture.Json(new { results = new[] { new { media_type = "person", id = 9, name = "目标" } } }) :
                    word == "目标2" ? SearchFixture.Json(new { marker = "original", results = Array.Empty<object>() }) :
                    mode == "empty" ? SearchFixture.Json(new { marker = "fallback", results = Array.Empty<object>() }) : SearchFixture.Tmdb("tv", 7, "目标", "已取得简介")),
                ReadDetail = (_, _, _) => Task.FromException<JsonElement>(new InvalidOperationException("single detail failed"))
            };
            await using var f = new SearchFixture(new SearchSources(), metadata);
            var session = f.Service.Start("目标2"); await SearchFixture.Settle(session);
            SearchFixture.Check(metadata.Searches.SequenceEqual(mode is "person" or "failure" ? new[] { "目标2" } : ["目标2", "目标"]),
                mode + ": fallback is driven by successful raw results emptiness, never mapping emptiness or failure");
            if (mode == "success")
            {
                var context = new FilmSearchContext(session, session.Items.Single());
                SearchFixture.Check(context.Query == "目标2" && context.MetadataQuery == "目标" &&
                    context.MetadataSearchPayload!.Value.GetProperty("results")[0].GetProperty("overview").GetString() == "已取得简介" &&
                    context.Metadata!.Value.GetProperty("overview").GetString() == "已取得简介", "failed detail preserves complete original search metadata and the actual fallback query");
                SearchFixture.Check(session.MetadataState == FilmSearchChannelState.Completed, "one detail failure does not fail the search channel");
            }
            else if (mode == "empty") SearchFixture.Check(session.MetadataQuery == "目标2" &&
                session.MetadataSearchPayload!.Value.GetProperty("marker").GetString() == "original", "empty fallback retains original response and effective word");
            else if (mode == "failure") SearchFixture.Check(session.MetadataState == FilmSearchChannelState.Error, "original failure is explicit metadata error");
        }
    }

    private static async Task MetadataCache()
    {
        var metadata = new SearchMetadata { ReadSearch = (word, _) => Task.FromResult(SearchFixture.Tmdb(word == "电影" ? "movie" : "tv", 7, word, "搜索简介")) };
        var sources = new SearchSources(SearchFixture.Source("a")) { Read = (_, _, page, _) => Task.FromResult(SearchFixture.Page(2, (page.ToString(), "站源", ""))) };
        await using var f = new SearchFixture(sources, metadata);
        var first = f.Service.Start("剧集"); await SearchFixture.Settle(first);
        SearchFixture.Check(new FilmSearchContext(first, first.Items.Single(item => SearchFixture.Text(item, "sourceKind") == "tmdb"))
            .Metadata!.Value.GetProperty("overview").GetString() == "详情tv", "full detail reaches context");
        await f.Service.LoadMoreAsync();
        SearchFixture.Check(metadata.Searches.Count == 1 && metadata.Details.Count == 1, "more never repeats TMDB search/detail");
        await SearchFixture.Settle(f.Service.Start("别名"));
        SearchFixture.Check(metadata.Details.Count == 1, "same type and ID reuse successful detail across queries");
        await SearchFixture.Settle(f.Service.Start("电影"));
        SearchFixture.Check(metadata.Details.SequenceEqual(new[] { ("tv", 7L), ("movie", 7L) }), "equal numeric IDs do not cross movie/tv cache identity");
    }

    private static async Task QueryCancellation()
    {
        var late = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new SearchSources(SearchFixture.Source("a"))
        { Read = (_, query, _, _) => query == "旧词" ? late.Task : Task.FromResult(SearchFixture.Page(1, ("new", "新词", ""))) };
        await using var f = new SearchFixture(sources);
        var old = f.Service.Start("旧词");
        try
        {
            await SearchFixture.Until(() => sources.Calls.Count == 1);
            var current = f.Service.Start("新词"); await SearchFixture.Settle(current);
            late.TrySetResult(SearchFixture.Page(1, ("late", "旧词", ""))); await SearchFixture.Settle(old);
            await SearchFixture.Until(() => f.Service.TrackedSessions == 1);
            SearchFixture.Check(ReferenceEquals(f.Service.Current, current) && old.Items.Count == 0 &&
                SearchFixture.Text(current.Items.Single(), "title") == "新词", "noncooperative old delivery never publishes into either new or cancelled state");
        }
        finally { late.TrySetResult(SearchFixture.Page(1)); }
    }

    private static async Task RetiredSnapshot()
    {
        var sources = new SearchSources(SearchFixture.Source("a"))
        { Read = (_, query, _, _) => Task.FromResult(SearchFixture.Page(1, ("same", query, "原始备注"))) };
        await using var f = new SearchFixture(sources);
        var old = f.Service.Start("旧影片"); await SearchFixture.Settle(old);
        var context = new FilmSearchContext(old, old.Items.Single());
        var current = f.Service.Start("新影片");
        await SearchFixture.Settle(current);
        await SearchFixture.Until(() => f.Service.TrackedSessions == 1);
        SearchFixture.Check(context.Query == "旧影片" && context.Sources.Count == 1 &&
            SearchFixture.Text(context.Sources[0], "rawTitle") == "旧影片" && !old.CanLoadMore,
            "retirement releases only owner resources, not the playback-owned data snapshot");
        try { _ = old.Cancellation.Token; throw new InvalidOperationException("retired cancellation source was not disposed"); }
        catch (ObjectDisposedException) { }
        f.Service.InvalidateQuery("新的音乐词");
        await SearchFixture.Until(() => f.Service.TrackedSessions == 0);
        SearchFixture.Check(f.Service.Current is null && context.Sources.Count == 1 && context.Query == "旧影片",
            "a different music query also retires service tracking without discarding a playback snapshot");
        try { _ = current.Cancellation.Token; throw new InvalidOperationException("invalidated cancellation source was not disposed"); }
        catch (ObjectDisposedException) { }
    }

    private static async Task AllSourceFailures()
    {
        var sources = new SearchSources(SearchFixture.Source("a"))
        { Read = (_, _, _, _) => Task.FromException<JsonElement>(new InvalidOperationException("fixture source error")) };
        await using var f = new SearchFixture(sources);
        var session = f.Service.Start("影片"); await SearchFixture.Settle(session);
        SearchFixture.Check(session.SitesState == FilmSearchChannelState.Error && session.CanLoadMore && sources.Calls.Count == 1,
            "all failures retain a generic channel error and explicit same-page retry");
        sources.Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(1));
        await f.Service.LoadMoreAsync();
        SearchFixture.Check(session.SitesState == FilmSearchChannelState.Completed && !session.CanLoadMore &&
            sources.Calls.Select(call => call.Page).SequenceEqual(new[] { 1, 1 }), "successful empty page is a completed result, not an error");
    }

    private static async Task GenerationRecovery()
    {
        var sources = new SearchSources(SearchFixture.Source("stable"), SearchFixture.Source("replaced", order: 1));
        sources.Read = (source, _, _, _) => Task.FromResult(SearchFixture.Page(2,
            (source.Cat.Generation.ToString(), source.Cat.InstallationId + source.Cat.Generation + "影片", "")));
        await using var f = new SearchFixture(sources);
        var session = f.Service.Start("影片"); await SearchFixture.Settle(session);
        sources.Sources[1] = SearchFixture.Source("replaced", order: 1, generation: 2); sources.Publish();
        await SearchFixture.Until(() => session.SitesState == FilmSearchChannelState.Cancelled);
        SearchFixture.Check(ReferenceEquals(session, f.Service.Start("影片")), "same query retains its context identity across source replacement");
        await SearchFixture.Settle(session);
        SearchFixture.Check(sources.Calls.Count(call => call.Source.Cat.InstallationId == "stable") == 1 && sources.Calls.Count == 3,
            "valid completed source pages are not refetched");
        SearchFixture.Check(session.CanLoadMore && session.RawItems.All(item => SearchFixture.Text(item, "installationId") != "replaced" ||
            item.GetProperty("generation").GetInt64() == 2), "retired-generation members are removed and current source restarts with its own page one");
        sources.Sources.Add(SearchFixture.Source("newly-enabled", order: 2)); sources.Publish();
        SearchFixture.Check(session.SitesState == FilmSearchChannelState.Cancelled, "a newly enabled source also invalidates the captured source set");
        f.Service.Start("影片"); await SearchFixture.Settle(session);
        SearchFixture.Check(sources.Calls.Count == 4 && sources.Calls[^1].Source.Cat.InstallationId == "newly-enabled" && sources.Calls[^1].Page == 1,
            "same-query recovery admits the new source without reloading either retained source");
    }

    private static async Task PrepareResume()
    {
        var metadata = new SearchMetadata();
        var sources = new SearchSources(SearchFixture.Source("a")) { Read = (_, _, _, _) => Task.FromResult(SearchFixture.Page(2, ("1", "站源", ""))) };
        await using var f = new SearchFixture(sources, metadata);
        var session = f.Service.Start("目标"); await SearchFixture.Settle(session);
        string raw = session.RawItems[0].GetRawText();
        await f.Service.PrepareShutdownAsync();
        SearchFixture.Check(f.Engine.Disposals == 0, "preparation borrows the engine without closing it");
        f.Service.Resume();
        SearchFixture.Check(ReferenceEquals(session, f.Service.Start("目标")), "resume and same-query entry use the retained snapshot");
        await SearchFixture.Settle(session);
        SearchFixture.Check(session.CanLoadMore && sources.Calls.Count == 1 && metadata.Searches.Count == 1 && metadata.Details.Count == 1 &&
            session.RawItems[0].GetRawText() == raw, "resume renews cancellation without refetching completed pages or metadata");
    }

    private static async Task DetailShutdown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new SearchMetadata { ReadDetail = async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
            return SearchFixture.Json(new { overview = "unreachable" });
        } };
        var f = new SearchFixture(new SearchSources(), metadata);
        try
        {
            var session = f.Service.Start("目标"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var closing = f.Service.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            SearchFixture.Check(!closing.IsCompleted && f.Engine.Disposals == 0, "close cancels physical detail but does not release engine before actual drain");
            release.TrySetResult(); await closing.WaitAsync(TimeSpan.FromSeconds(10));
            SearchFixture.Check(f.Engine.Disposals == 1 && f.Service.TrackedSessions == 0 && !session.Loading && f.Sources.Subscribers == 0, "close drains hung-until-cancel detail and releases engine/observers once");
        }
        finally { release.TrySetResult(); await f.DisposeAsync(); }
    }

    private static async Task PrepareResumeInFlight()
    {
        int interruptedReads = 0, detailReads = 0;
        var sources = new SearchSources(SearchFixture.Source("stable"), SearchFixture.Source("interrupted", order: 1));
        sources.Read = async (source, _, _, token) =>
        {
            if (source.Cat.InstallationId == "interrupted" && ++interruptedReads == 1)
                await Task.Delay(Timeout.Infinite, token);
            return SearchFixture.Page(2, (source.Cat.InstallationId, "目标 第一季", "更新至3集"));
        };
        var metadata = new SearchMetadata { ReadDetail = async (_, _, token) =>
        {
            if (++detailReads == 1) await Task.Delay(Timeout.Infinite, token);
            return SearchFixture.Json(new { name = "目标", overview = "恢复所得详情", seasons = Array.Empty<object>() });
        } };
        await using var f = new SearchFixture(sources, metadata);
        var session = f.Service.Start("目标");
        await SearchFixture.Until(() => session.SiteItems.Count == 1 && metadata.Details.Count == 1 && session.Items.Count > 0);
        var context = new FilmSearchContext(session, session.Items.First());
        await f.Service.PrepareShutdownAsync();
        SearchFixture.Check(!session.Loading && f.Engine.Disposals == 0, "preparation physically settles interrupted work and retains the native engine");
        f.Service.Resume();
        await SearchFixture.Settle(session);
        SearchFixture.Check(ReferenceEquals(session, f.Service.Current) && session.CanLoadMore &&
            session.RawItems.Count(item => SearchFixture.Text(item, "sourceKind") == "site") == 2 &&
            session.RawItems.Count(item => SearchFixture.Text(item, "sourceKind") == "tmdb") == 1 &&
            sources.Calls.Count(call => call.Source.Cat.InstallationId == "stable") == 1 &&
            sources.Calls.Where(call => call.Source.Cat.InstallationId == "interrupted").Select(call => call.Page).SequenceEqual(new[] { 1, 1 }),
            "the retained successful source is untouched and only the unacknowledged page resumes");
        SearchFixture.Check(metadata.Searches.Count == 1 && metadata.Details.Count == 2 && context.Sources.Count == 2 &&
            context.MetadataSearchPayload!.Value.GetProperty("results")[0].GetProperty("overview").GetString() == "搜索简介" &&
            context.Metadata!.Value.GetProperty("overview").GetString() == "恢复所得详情",
            "resume reuses the original search payload and obtains only the cancelled missing detail");
    }

    private static async Task DetailOwnership()
    {
        var oldRelease = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new SearchMetadata();
        int calls = 0;
        metadata.ReadDetail = async (_, _, token) =>
        {
            if (++calls != 1) return SearchFixture.Json(new { name = "目标", overview = "current" });
            using var registration = token.Register(() => oldCancelled.TrySetResult());
            return await oldRelease.Task; // A late noncooperative adapter receipt.
        };
        await using var f = new SearchFixture(new SearchSources(), metadata);
        var old = f.Service.Start("旧查询");
        try
        {
            await SearchFixture.Until(() => metadata.Details.Count == 1);
            f.Service.InvalidateQuery("新查询");
            await oldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var current = f.Service.Start("新查询"); await SearchFixture.Settle(current);
            SearchFixture.Check(metadata.Details.Count == 2 && new FilmSearchContext(current, current.Items.Single()).Metadata!.Value
                .GetProperty("overview").GetString() == "current", "new query owns an independent flight after the last old reader cancelled");
            oldRelease.TrySetResult(SearchFixture.Json(new { name = "目标", overview = "late-old" }));
            await SearchFixture.Settle(old);
            var cached = f.Service.Start("第三查询"); await SearchFixture.Settle(cached);
            SearchFixture.Check(metadata.Details.Count == 2 && new FilmSearchContext(cached, cached.Items.Single()).Metadata!.Value
                .GetProperty("overview").GetString() == "current", "cancelled old flight cannot overwrite the successful new type/ID cache");
        }
        finally { oldRelease.TrySetResult(SearchFixture.Json(new { })); }
    }

    private static async Task LateCarrier()
    {
        var second = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tmdb = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sources = new SearchSources(SearchFixture.Source("a"), SearchFixture.Source("b", order: 1))
        { Read = (source, _, _, _) => source.Cat.InstallationId == "a" ? Task.FromResult(SearchFixture.Page(1, ("same", "目标 第一季", "更新至3集"))) : second.Task };
        var metadata = new SearchMetadata { ReadSearch = (_, _) => tmdb.Task };
        await using var f = new SearchFixture(sources, metadata);
        var session = f.Service.Start("原始查询");
        try
        {
            await SearchFixture.Until(() => session.Items.Count == 1);
            var selected = new FilmSearchContext(session, session.Items.Single());
            var raw = new FilmSearchContext(session, session.RawItems.Single());
            string rawIdentity = raw.Item.GetRawText();
            second.TrySetResult(SearchFixture.Page(1, ("same", "目标 第二季", "更新至5集")));
            tmdb.TrySetResult(SearchFixture.Json(new { results = new object[]
            {
                new { media_type = "movie", id = 7, title = "目标", overview = "电影简介" },
                new { media_type = "tv", id = 7, name = "目标", overview = "剧集简介" }
            } }));
            await SearchFixture.Settle(session);
            SearchFixture.Check(selected.Query == "原始查询" && SearchFixture.Text(selected.Item, "sourceKind") == "site" &&
                SearchFixture.Text(selected.CurrentItem, "sourceKind") == "tmdb" && SearchFixture.Text(selected.CarrierItem, "tmdbType") == "tv" &&
                selected.Sources.Count == 2 && selected.Metadata!.Value.GetProperty("overview").GetString() == "详情tv",
                "original selected identity has an explicit late TMDB carrier with all members and metadata");
            SearchFixture.Check(SearchFixture.Text(raw.CurrentItem, "sourceKind") == "site" && SearchFixture.Text(raw.CurrentItem, "installationId") == "a" &&
                SearchFixture.Text(raw.CurrentItem, "siteDetail") == "same" && FilmSearchJson.Number(raw.CurrentItem, "generation") == 1 &&
                raw.Item.GetRawText() == rawIdentity && SearchFixture.Text(raw.CurrentItem, "id") == SearchFixture.Text(raw.Item, "id") &&
                SearchFixture.Text(raw.CurrentItem, "displayKey") == raw.DisplayKey &&
                SearchFixture.Text(raw.CurrentItem, "siteKey") == SearchFixture.Text(raw.Item, "siteKey") &&
                raw.Sources.Count == 1 && raw.Metadata is null &&
                SearchFixture.Text(raw.CarrierItem, "sourceKind") == "site", "raw selection preserves only its exact original source without TMDB enrichment");
            var movie = new FilmSearchContext(session, session.Items.Single(item => SearchFixture.Text(item, "tmdbType") == "movie"));
            SearchFixture.Check(SearchFixture.Text(movie.CurrentItem, "tmdbType") == "movie" && movie.Sources.Count == 0 &&
                movie.Metadata!.Value.GetProperty("overview").GetString() == "详情movie", "identical TMDB numeric IDs and titles never cross movie/tv identity");
            SearchFixture.Check(metadata.Searches.Count == 1 && sources.Calls.Count == 2, "following late membership never searches again");
        }
        finally { second.TrySetResult(SearchFixture.Page(1)); tmdb.TrySetResult(SearchFixture.Tmdb("tv", 7, "目标", "")); }
    }
}

internal sealed class SearchFixture : IAsyncDisposable
{
    internal SearchFixture(SearchSources? sources = null, IFilmSearchMetadata? metadata = null)
    {
        Sources = sources ?? new(); Engine = new();
        Service = new(Engine, Sources, _ => Task.FromResult<IReadOnlyList<string>>([]), metadata);
    }
    internal SearchSources Sources { get; }
    internal SearchEngine Engine { get; }
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
            new(new(installation, site), installation, "fixture", actions, methods, new(), order));
    }
    internal static async Task Settle(FilmSearchSession session) => await session.Work.WaitAsync(TimeSpan.FromSeconds(20));
    internal static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!done()) await Task.Delay(5, timeout.Token);
    }
    public ValueTask DisposeAsync() => Service.DisposeAsync();
}
internal sealed class SearchEngine : IJavaScriptJsonEngine
{
    private readonly NativeJavaScriptJsonEngine engine = new(FilmSearchService.LoadProgramSource());
    internal int Disposals;
    public Task<JsonElement> InvokeAsync(object request, CancellationToken cancellationToken = default) => engine.InvokeAsync(request, cancellationToken);
    public ValueTask DisposeAsync() { Disposals++; return engine.DisposeAsync(); }
}
internal sealed class SearchSources(params FilmSearchSource[] sources) : IFilmSearchSources
{
    private event EventHandler? changed;
    public event EventHandler? Changed { add => changed += value; remove => changed -= value; }
    internal int Subscribers => changed?.GetInvocationList().Length ?? 0;
    internal List<FilmSearchSource> Sources { get; } = [.. sources];
    internal List<(FilmSearchSource Source, string Query, int Page)> Calls { get; } = [];
    internal Func<FilmSearchSource, string, int, CancellationToken, Task<JsonElement>> Read =
        (_, _, _, _) => Task.FromResult(SearchFixture.Page(1));
    public IReadOnlyList<FilmSearchSource> CurrentSearchSources => Sources.ToArray();
    public Task<IReadOnlyList<FilmSearchSource>> ReadSearchSourcesAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<FilmSearchSource>>(Sources.ToArray()); }
    public Task<JsonElement> SearchAsync(FilmSearchSource source, string query, int page, CancellationToken token)
    { Calls.Add((source, query, page)); return Read(source, query, page, token); }
    public bool IsSearchSourceCurrent(FilmSearchSource source) => Sources.Any(current => current.Key == source.Key && current.Cat.Generation == source.Cat.Generation);
    internal void Publish() => changed?.Invoke(this, EventArgs.Empty);
}
internal sealed class SearchMetadata : IFilmSearchMetadata
{
    internal List<string> Searches { get; } = [];
    internal List<(string Type, long Id)> Details { get; } = [];
    internal Func<string, CancellationToken, Task<JsonElement>> ReadSearch = (_, _) => Task.FromResult(SearchFixture.Tmdb("tv", 7, "目标", "搜索简介"));
    internal Func<string, long, CancellationToken, Task<JsonElement>> ReadDetail = (type, _, _) => Task.FromResult(SearchFixture.Json(new
    { name = "目标", title = "目标", overview = "详情" + type, first_air_date = "2023-01-01", release_date = "2023-01-01", seasons = Array.Empty<object>() }));
    public Task<JsonElement> SearchAsync(string query, CancellationToken token) { Searches.Add(query); return ReadSearch(query, token); }
    public Task<JsonElement> DetailAsync(string type, long id, CancellationToken token) { Details.Add((type, id)); return ReadDetail(type, id, token); }
    public string ResolveImage(string path) => path.Length == 0 ? "" : "https://fixture.invalid" + path;
}

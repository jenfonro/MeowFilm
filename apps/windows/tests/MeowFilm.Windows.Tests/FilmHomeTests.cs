using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class FilmHomeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-home: content receipts publish only affected cards and retain unchanged identities",
            IncrementalCards);
        yield return new("film-home: skeletons end on empty failure cancellation and never cover pagination",
            () => MusicQualityConsumerTests.Sta(SkeletonStates));
        yield return new("film-home: runtime entry reads preserve request range cache state and original image identity",
            RequestContract);
        yield return new("film-home: real Douban runtime entries isolate unidentifiable rows without losing healthy sections",
            () => MusicQualityConsumerTests.Sta(RuntimeRows));
        yield return new("film-home: independent section failure preserves snapshots and retries only that section",
            () => MusicQualityConsumerTests.Sta(IndependentSections));
        yield return new("film-home: explicit refresh retains successful home and category snapshots on stale errors",
            () => MusicQualityConsumerTests.Sta(RefreshSnapshots));
        yield return new("film-home: data routes preserve four contexts and page from 12 to 24 to 48",
            () => MusicQualityConsumerTests.Sta(DataPagination));
        yield return new("film-home: server-compatible filters reset requests and keep Douban anime scope",
            () => MusicQualityConsumerTests.Sta(DataFiltering));
        yield return new("film-home: rapid filter replacement fences non-cooperative older results",
            () => MusicQualityConsumerTests.Sta(FilterReplacement));
        yield return new("film-home: route replacement and shutdown fence late home and data publication",
            () => MusicQualityConsumerTests.Sta(CancellationAndDrain));
    }

    private static Task IncrementalCards()
    {
        var home = new FilmHomeSection(FilmSectionDefinition.Home[0]);
        var data = new FilmDataSection(FilmSectionDefinition.Home[0]);
        Verify(home.Items, items => home.Complete(items), () => home.Begin());
        Verify(data.Items, items => data.Replace(items, items.Length, true, null), () => data.Begin(true));
        return Task.CompletedTask;

        static void Verify(System.Collections.ObjectModel.ObservableCollection<FilmCardItem> items,
            Action<FilmCardItem[]> present, Action begin)
        {
            var first = Card("a"); var removed = Card("b"); var last = Card("c");
            present([first, removed, last]);
            var events = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
            items.CollectionChanged += (_, args) => events.Add(args);
            begin();
            present([first with { }, removed with { }, last with { }]);
            Check(events.Count == 0 && ReferenceEquals(first, items[0]) && ReferenceEquals(last, items[2]),
                "loading and equal receipts do not mutate the display collection");
            var appended = Card("d");
            present([first with { }, last with { }, appended]);
            Check(events.Count == 2 && events[0].Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
                ReferenceEquals(events[0].OldItems![0], removed) && events[1].Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add &&
                ReferenceEquals(first, items[0]) && ReferenceEquals(last, items[1]),
                "removing and appending publish only affected identities, without moving survivors");
            events.Clear();
            var updated = last with { Title = "更新标题", Score = "9.8", Subtitle = "新状态" };
            present([first with { }, updated, appended with { }]);
            Check(events.Count == 1 && events[0].Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace &&
                ReferenceEquals(items[1], updated) && ReferenceEquals(items[0], first) && ReferenceEquals(items[2], appended),
                "a metadata receipt replaces only its changed item");
            events.Clear();
            present([appended with { }, first with { }, updated with { }]);
            Check(events.Count == 1 && events[0].Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move &&
                ReferenceEquals(items[0], appended) && ReferenceEquals(items[1], first) && ReferenceEquals(items[2], updated),
                "reordering retains item identities and publishes the actual move without reset");
        }
    }

    private static Task SkeletonStates()
    {
        var home = new FilmHomeSection(FilmSectionDefinition.Home[0]);
        Check(home.ShowSkeleton && !home.ShowStatus, "pending home has a skeleton");
        home.Begin();
        home.Complete([]);
        Check(!home.ShowSkeleton && home.ShowStatus && !home.CanRetry, "empty result ends skeleton");
        home.Begin(); home.Fail();
        Check(!home.ShowSkeleton && home.CanRetry, "failure ends skeleton and retains retry");
        home.Begin(); home.Cancel();
        Check(!home.ShowSkeleton && home.CanRetry, "cancellation ends skeleton and retains retry");
        var data = new FilmDataSection(FilmSectionDefinition.Home[0]);
        Check(data.ShowSkeleton, "pending data has a skeleton");
        data.Begin(); data.Complete([Card("movie")], 0, 1);
        var card = data.Items[0];
        data.Begin();
        Check(!data.ShowSkeleton && data.HasItems && ReferenceEquals(card, data.Items[0]),
            "pagination keeps existing content visible");
        data.Fail();
        Check(!data.ShowSkeleton && data.HasItems && data.CanPage, "failed page retains content and retry");
        return Task.CompletedTask;
    }

    private static async Task RequestContract()
    {
        await using var runtime = new FilmRuntimeProbe();
        JsonElement raw = JsonSerializer.Deserialize<JsonElement>(HomePayload("movie"));
        JsonElement Entry(string status = "fresh", string? code = null) => FilmJson.Encode(new
        {
            value = raw, completeness = "partial", fetchedAt = 1000, expiresAt = 2000,
            cache = new { status, error = code is null ? null : new { code },
                components = new { baseData = new { status = "ready", fetchedAt = 1000 } } }
        });
        runtime.Intercept = (method, input, _) =>
        {
            var wire = BridgeCodec.DecodeJSONValue(BridgeCodec.EncodeJSONValue(input));
            var refresh = FilmJson.Member(wire, "refresh");
            Check(method == "film.data.metadata.entry" && FilmJson.Text(wire, "provider") == "douban" &&
                FilmJson.Member(wire, "query").ValueKind == JsonValueKind.Object &&
                refresh.ValueKind is JsonValueKind.Undefined or JsonValueKind.False or JsonValueKind.True,
                "the encoded runtime request has the metadata entry shape and never sends refresh:null");
            raw = JsonSerializer.Deserialize<JsonElement>(HomePayload(
                FilmJson.Text(wire, "path").EndsWith("/tv", StringComparison.Ordinal) ? "tv" : "movie"));
            return Task.FromResult(FilmRuntimeProbe.Ok(Entry()));
        };
        using var source = new DoubanFilmHomeSource(runtime);
        var section = FilmSectionDefinition.Home[0];
        var items = await source.LoadAsync(section, 24, 24, default);

        var request = runtime.Calls.Single();
        var query = request.Parameters.GetProperty("query");
        Check(request.Method == "film.data.metadata.entry" &&
            FilmJson.Text(request.Parameters, "provider") == "douban" &&
            FilmJson.Text(request.Parameters, "path") == "/rexxar/api/v2/subject/recent_hot/movie" &&
            FilmJson.Integer(query, "start") == 24 && FilmJson.Integer(query, "limit") == 24 &&
            FilmJson.Text(query, "category") == "热门" && FilmJson.Text(query, "type") == "全部" &&
            FilmJson.Member(request.Parameters, "refresh").ValueKind == JsonValueKind.Undefined,
            "ordinary ranged reads let the runtime check freshness");

        Check(items.Count == 2, "blank titles and unidentifiable rows are isolated");
        Check(items[0].Id == "douban:movie:100" && items[0].Title == "第一部" &&
            items[0].Poster == "/v1/douban/images/view/photo/s_ratio_poster/public/p100.jpg" &&
            items[0].Score == "8.6" && items[0].PlaybackContext?.Target is
                { Id: "douban:movie:100", Kind: "movie", MetadataProvider: "douban", MetadataId: "100" },
            "runtime image reference, numeric score and independent Douban identity");
        Check(items[1].Poster.EndsWith("/large.jpg", StringComparison.Ordinal) &&
            items[1].Score.Length == 0, "large fallback and zero score");
        var complete = await source.LoadAsync(section, 10024, 1, default);
        Check(complete.Count == 2 && complete[1].Id == "douban:movie:200",
            "runtime results are not truncated, identities do not depend on offsets and loaded ranges have no additional client offset ceiling");

        foreach (var definition in FilmSectionDefinition.Home)
        {
            var home = await source.LoadAsync(definition, 0, 12, default);
            var input = runtime.Calls.Last().Parameters;
            Check(FilmJson.Text(input, "path") == "/rexxar/api/v2/subject/recent_hot/" + definition.Kind &&
                FilmJson.Integer(input.GetProperty("query"), "start") == 0 &&
                FilmJson.Integer(input.GetProperty("query"), "limit") == 12 &&
                home[0].PlaybackContext?.Target.Kind == definition.Kind,
                "each home section preserves its movie or TV resource identity and range");
        }
        var filtered = section with { Category = "最新", Type = "日本", Refresh = true };
        await source.LoadAsync(filtered, 0, 24, default);
        request = runtime.Calls.Last();
        Check(FilmJson.Text(request.Parameters.GetProperty("query"), "category") == "最新" &&
            FilmJson.Text(request.Parameters.GetProperty("query"), "type") == "日本" &&
            FilmJson.Bool(request.Parameters, "refresh"), "explicit refresh sends the selected filter and force flag");
        await source.LoadAsync(section with { Key = "future-display", Title = "不同展示标题", Category = "任意", Type = "新筛选" }, 0, 12, default);
        var extended = runtime.Calls.Last().Parameters;
        Check(FilmJson.Text(extended, "path") == "/rexxar/api/v2/subject/recent_hot/movie" &&
            FilmJson.Text(extended.GetProperty("query"), "category") == "任意" &&
            FilmJson.Text(extended.GetProperty("query"), "type") == "新筛选" &&
            !extended.TryGetProperty("title", out _) && !extended.TryGetProperty("key", out _),
            "request materials are independent of the local menu and display title; display fields are not forwarded");

        runtime.Intercept = (_, _, _) => Task.FromResult(FilmRuntimeProbe.Ok(Entry("stale", "UPSTREAM_ERROR")));
        var stale = (FilmHomePage)await source.LoadAsync(filtered, 0, 24, default);
        Check(stale.Count == 2 && stale.Cache is { Status: "stale", ErrorCode: "UPSTREAM_ERROR" } cache &&
            cache.Components.GetProperty("baseData").GetProperty("status").GetString() == "ready",
            "a failed refresh returns old success together with its actual cache error and component state");
        foreach (string auxiliary in new[] { "null", "17", """{"status":"future-state","components":[],"error":{"code":"UPSTREAM_ERROR"}}""" })
        {
            var entry = JsonSerializer.Deserialize<JsonElement>($$$"""
                {"value":{{{raw.GetRawText()}}},"cache":{{{auxiliary}}},"count":{},"start":[],"extra":{"nested":true}}
                """);
            runtime.Intercept = (_, _, _) => Task.FromResult(FilmRuntimeProbe.Ok(entry));
            var page = (FilmHomePage)await source.LoadAsync(section, 0, 12, default);
            Check(page.Count == 2 && page.Cache?.Status != "fresh",
                "unconsumed auxiliary fields do not gate the actual list or turn an unknown cache state into fresh");
            if (page.Cache is { } unknown)
                Check(unknown.Status == "future-state" && unknown.ErrorCode == "UPSTREAM_ERROR",
                    "raw diagnostic state and real refresh failure remain available");
        }
        runtime.Intercept = (_, _, _) => Task.FromResult(FilmRuntimeProbe.Failure("UPSTREAM_ERROR"));
        await Failure(() => source.LoadAsync(section, 0, 12, default), "UPSTREAM_ERROR");
        runtime.Intercept = (_, _, _) => Task.FromResult(FilmRuntimeProbe.Ok(FilmJson.Encode(new
        {
            value = new { items = "invalid" }, cache = new { status = "fresh", components = new { } }
        })));
        await Failure(() => source.LoadAsync(section, 0, 12, default), "INVALID_RESPONSE");
        runtime.Intercept = (_, _, _) => Task.FromResult(FilmRuntimeProbe.Ok(
            JsonSerializer.Deserialize<JsonElement>("""{"value":{"items":[null,{"title":[]}]},"cache":null}""")));
        await Failure(() => source.LoadAsync(section, 0, 12, default), "INVALID_RESPONSE");
        foreach (string invalid in new[]
        {
            """{"title":"缺少身份"}""",
            """{"title":"缺少目标","businessId":"douban:movie:300"}""",
            """{"title":"非法目标","businessId":"douban:movie:300","playbackTarget":{"id":"douban:movie:300","kind":"invalid","title":"非法目标"}}"""
        })
        {
            var healthy = raw.GetProperty("items")[0];
            runtime.Intercept = (_, _, _) => Task.FromResult(FilmRuntimeProbe.Ok(
                JsonSerializer.Deserialize<JsonElement>($$$"""{"value":{"items":[{{{invalid}}},{{{healthy.GetRawText()}}}]}}""")));
            var isolated = await source.LoadAsync(section, 0, 12, default);
            Check(isolated.Count == 1 && isolated[0].Id == "douban:movie:100",
                "bad business identity or playback target is isolated without fabricating a fallback");
            runtime.Intercept = (_, _, _) => Task.FromResult(FilmRuntimeProbe.Ok(
                JsonSerializer.Deserialize<JsonElement>($$$"""{"value":{"items":[{{{invalid}}}]}}""")));
            await Failure(() => source.LoadAsync(section, 0, 12, default), "INVALID_RESPONSE");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        int before = runtime.Calls.Count;
        try { await source.LoadAsync(section, 0, 12, cancelled.Token); throw new InvalidOperationException("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(runtime.Calls.Count == before, "cancelled consumers do not admit another runtime read");
    }

    private static async Task RuntimeRows()
    {
        await using var upstream = new RuntimeHttpClientTests.Server();
        await using var host = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(
            TestWorkspace.DataPath("film-home", Guid.NewGuid().ToString("N")));
        await host.Metadata.SaveDoubanSettingsAsync(new Dictionary<string, object?> { ["apiBase"] = upstream.BaseUrl });
        await host.WaitSettingsAsync("douban");
        await using var module = new FilmCapability(new DoubanFilmHomeSource(host.Client));
        var errors = new List<string>();
        module.OperationFailed += (_, message) => errors.Add(message);
        var navigation = module.NavigateAsync(FilmCapability.HomeRoute, default);
        for (int index = 0; index < FilmSectionDefinition.Home.Count; index++)
        {
            await using var exchange = await upstream.AcceptAsync();
            var address = new Uri("http://fixture.invalid" + exchange.Path);
            var query = address.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                    pair => Uri.UnescapeDataString(pair[1].Replace("+", " ", StringComparison.Ordinal)));
            var definition = FilmSectionDefinition.Home.Single(section =>
                address.AbsolutePath == "/rexxar/api/v2/subject/recent_hot/" + section.Kind &&
                query["category"] == section.Category && query["type"] == section.Type);
            Check(exchange.Method == "GET" && query.Count == 4 && query["start"] == "0" && query["limit"] == "12",
                "all four home sections use the actual Douban recent_hot contract");
            await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(new
            {
                items = new object[]
                {
                    new { title = "缺少作品身份" },
                    new { id = 100, type = definition.Kind, title = "第一部", marker = "来源原字段",
                        pic = new { normal = "https://img3.doubanio.com/view/photo/s_ratio_poster/public/p100.jpg" },
                        rating = new { value = 8.6 } },
                    new { id = "not-a-metadata-id", type = definition.Kind, title = "非法作品身份" },
                    new { id = "200", type = definition.Kind, title = "第二部", rating = new { value = "9.0" } }
                }
            }));
        }
        await navigation;
        foreach (var section in module.Sections)
        {
            string kind = section.Definition.Kind;
            var entry = await host.FilmAsync("data.metadata.entry", new
            {
                provider = "douban", path = "/rexxar/api/v2/subject/recent_hot/" + kind,
                query = new { start = 0, limit = 12, category = section.Definition.Category, type = section.Definition.Type }
            });
            var rows = entry.GetProperty("value").GetProperty("items");
            Check(rows.GetArrayLength() == 4 && !rows[0].TryGetProperty("businessId", out _) &&
                !rows[2].TryGetProperty("playbackTarget", out _) &&
                rows[1].GetProperty("id").ValueKind == JsonValueKind.Number &&
                rows[3].GetProperty("id").ValueKind == JsonValueKind.String,
                "the real Node owner keeps original unidentifiable rows and projects numeric and string metadata identities");
            Check(section.State == FilmHomeSectionState.Ready && section.Items.Count == 2 &&
                section.Items[0].Id == $"douban:{kind}:100" && section.Items[1].Id == $"douban:{kind}:200" &&
                section.Items[0].PlaybackContext?.Target is { MetadataProvider: "douban", MetadataId: "100" } target &&
                target.Kind == kind && section.Items[0].PlaybackContext!.Metadata!.Value.GetProperty("marker").GetString() == "来源原字段" &&
                section.Items[0].Poster == "/v1/douban/images/view/photo/s_ratio_poster/public/p100.jpg" &&
                section.Items[0].Score == "8.6" && section.Items[1].Score == "9",
                "native home sections retain only real targets, original metadata, rewritten images and both score formats");
        }
        Check(errors.Count == 0 && !upstream.HasConnection, "healthy sections finish without errors and repeated reads use the Node cache");
        var movie = module.Sections[0];
        var retained = movie.Items[0];
        var refresh = module.RefreshSectionAsync(movie);
        await using (var exchange = await upstream.AcceptAsync())
            await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(new { items = new[] { new { title = "缺少作品身份" } } }));
        await refresh;
        Check(movie.CanRetry && ReferenceEquals(retained, movie.Items[0]) && errors.SequenceEqual(["栏目加载失败"]),
            "a nonempty all-invalid runtime list still fails and preserves the successful snapshot");
        var retry = module.RetrySectionAsync(movie);
        await using (var exchange = await upstream.AcceptAsync())
            await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(new { items = Array.Empty<object>() }));
        await retry;
        Check(movie.State == FilmHomeSectionState.Ready && movie.Items.Count == 0 && !movie.CanRetry && errors.Count == 1,
            "a real empty list is distinct from a malformed nonempty list");
    }

    private static async Task IndependentSections()
    {
        var source = new ControlledFilmSource();
        await using var runtime = new FilmRuntimeProbe();
        await using var module = new FilmCapability(source, new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeProbe()),
            new FilmLibrary(new FilmMemoryStore()));
        await module.InitializeAsync(default);
        Check(source.Calls.Count == 0 && module.Sections.All(section => section.ShowSkeleton),
            "constructing and initializing film does not read business data");
        var navigation = module.NavigateAsync(FilmCapability.HomeRoute, default);
        await source.WaitForCalls(4);
        Check(module.Sections.All(section => section.StatusText == "加载中…" && section.ShowSkeleton && !section.ShowStatus),
            "all fixed sections begin together");
        Check(source.Calls.All(call => call.Start == 0 && call.Limit == 12),
            "home uses one fixed twelve-card request per section");

        foreach (var section in FilmSectionDefinition.Home)
        {
            if (section.Key == "anime") source.Fail(section.Key);
            else source.Complete(section.Key, Card(section.Key));
        }
        await navigation;

        Check(module.Sections.Single(section => section.Key == "anime").StatusText == "栏目加载失败",
            "one failure stays local");
        Check(module.Sections.Where(section => section.Key != "anime")
            .All(section => section.Items.Count == 1), "successful peers remain visible");
        Check(source.Calls.Count == 4, "one request per fixed section");
        Check(module.Sections.All(section => !section.ShowSkeleton),
            "both successful and failed sections finish their skeletons");
        var retainedItems = module.Sections[0].Items;
        var retainedCard = retainedItems[0];
        module.Sections[0].HorizontalOffset = 192;
        string homeLocation = module.CaptureNavigationLocation();
        Check(homeLocation == FilmCapability.HomeRoute, "capture retains the current film route");
        await module.RestoreNavigationAsync(FilmCapability.PlaybackRoute, default);
        Check(module.CaptureNavigationLocation() == FilmCapability.PlaybackRoute && !module.HasPlayback && runtime.Calls.Count == 0,
            "a bare route has no film identity and does not invent a preview or session");
        var restored = module.RestoreNavigationAsync(homeLocation, default);
        await source.WaitForCalls(8);
        Check(ReferenceEquals(retainedCard, retainedItems[0]) && !module.Sections[0].ShowSkeleton,
            "restoration shows the snapshot while the owner checks freshness");
        foreach (var section in FilmSectionDefinition.Home)
        {
            if (section.Key == "anime") source.Fail(section.Key);
            else source.Complete(section.Key, Card(section.Key));
        }
        await restored;
        Check(source.Calls.Count == 8 && ReferenceEquals(retainedItems, module.Sections[0].Items) &&
            ReferenceEquals(retainedCard, retainedItems[0]) && module.Sections[0].HorizontalOffset == 192,
            "route restoration revalidates through the owner and preserves equal cards and offset");

        var anime = module.Sections.Single(section => section.Key == "anime");
        var retry = module.RetrySectionAsync(anime);
        await source.WaitForCalls(9);
        Check(source.Calls.Last() is { Key: "anime", Start: 0, Limit: 12, Section.Refresh: true },
            "retry forces only the failed home section");
        source.Complete("anime", Card("anime"));
        await retry;
        Check(anime.Items.Single().Title == "影片 anime" && !anime.CanRetry,
            "successful retry replaces the local failure");

        var routes = new List<string>();
        module.NavigationRequested += (_, args) => routes.Add(args.Route);
        var show = module.Sections.Single(section => section.Key == "show");
        await module.OpenDataAsync(show);
        Check(routes.SequenceEqual([FilmCapability.DataRoute]), "content requests the hidden data route");
        Check(module.ActiveData.Key == "show" && module.ActiveData.Title == "热门综艺",
            "the selected section context is retained for shell navigation");
        var card = show.Items.Single();
        await module.OpenPlaybackAsync(card);
        Check(routes.SequenceEqual([FilmCapability.DataRoute, FilmCapability.PlaybackRoute]) &&
            module.Playback?.Item.Id == card.Id && module.Playback.Request.Target.Id == card.PlaybackContext!.Target.Id,
            "film cards retain their catalog identity and request the hidden playback route");
        Check(module.Navigation.Count == 3 &&
            module.Navigation.Single(item => item.Route == FilmCapability.HomeRoute).ShowInSidebar &&
            module.Navigation.Where(item => item.Route != FilmCapability.HomeRoute)
                .All(item => !item.ShowInSidebar),
            "only the film home is exposed in the sidebar");
        int calls = source.Calls.Count;
        await module.NavigateAsync(FilmCapability.PlaybackRoute, default);
        Check(module.IsPlayback && !module.IsHome && !module.IsData &&
            module.Heading == "播放页" && source.Calls.Count == calls,
            "the playback route publishes its explicit identity without another catalog request");
    }

    private static async Task RefreshSnapshots()
    {
        await using var runtime = new FilmRuntimeProbe();
        bool stale = false, fail = false, empty = false;
        runtime.Intercept = (_, input, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (fail) return Task.FromResult(FilmRuntimeProbe.Failure("UPSTREAM_ERROR"));
            var query = input.GetProperty("query");
            int start = FilmJson.Integer(query, "start")!.Value;
            int limit = FilmJson.Integer(query, "limit")!.Value;
            string kind = FilmJson.Text(input, "path").EndsWith("/tv", StringComparison.Ordinal) ? "tv" : "movie";
            return Task.FromResult(FilmRuntimeProbe.Ok(FilmJson.Encode(new
            {
                value = new { items = Enumerable.Range(start + 1, empty ? 0 : limit).Select(id => new
                {
                    id, title = "影片 " + id, pic = new { normal = "/v1/douban/images/" + id + ".jpg" },
                    businessId = $"douban:{kind}:{id}", playbackTarget = new
                    { id = $"douban:{kind}:{id}", kind, title = "影片 " + id, metadataProvider = "douban", metadataId = id.ToString() }
                }) },
                completeness = "partial", fetchedAt = 1000, expiresAt = 2000,
                cache = new { status = stale ? "stale" : "fresh", error = stale && start == 0 ? new { code = "UPSTREAM_ERROR" } : null,
                    components = new { } }
            })));
        };
        await using var module = new FilmCapability(new DoubanFilmHomeSource(runtime));
        var errors = new List<string>();
        module.OperationFailed += (_, message) => errors.Add(message);
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        var home = module.Sections[0];
        var card = home.Items[0];
        stale = true;
        await module.RefreshSectionAsync(home);
        Check(home.Items.Count == 12 && ReferenceEquals(home.Items[0], card) && home.Cache?.Status == "stale" &&
            home.Cache.ErrorCode == "UPSTREAM_ERROR" && errors.Count == 1 &&
            FilmJson.Bool(runtime.Calls.Last().Parameters, "refresh"),
            "explicit home refresh exposes the runtime error and keeps the same successful cards");
        fail = true;
        await module.RefreshSectionAsync(home);
        Check(home.Items.Count == 12 && ReferenceEquals(home.Items[0], card) && errors.Count == 2,
            "a transport rejection also retains the successful home snapshot");
        stale = false; fail = false;
        await module.OpenDataAsync(home);
        await module.LoadMoreDataAsync();
        var cards = module.ActiveData.Items.ToArray();
        Check(cards.Length == 48, "category keeps normal first and subsequent page sizes");
        stale = true;
        int before = runtime.Calls.Count;
        await module.RefreshDataAsync();
        var calls = runtime.Calls.Skip(before).ToArray();
        Check(module.ActiveData.Items.SequenceEqual(cards) && module.ActiveData.Cache is { Status: "stale", ErrorCode: "UPSTREAM_ERROR" } &&
            errors.Count == 3 && calls.Length == 2 && calls.All(call => FilmJson.Bool(call.Parameters, "refresh")) &&
            calls.Select(call => FilmJson.Integer(call.Parameters.GetProperty("query"), "start")).SequenceEqual(new int?[] { 0, 24 }),
            "refresh revalidates only loaded ranges and preserves all old successful content on stale responses");
        fail = true;
        await module.RefreshDataAsync();
        Check(module.ActiveData.Items.SequenceEqual(cards) && module.ActiveData.PageActionText == "重试" && errors.Count == 4,
            "rejected category refresh keeps loaded content and its retry action");
        fail = false; stale = false;
        await module.LoadMoreDataAsync();
        Check(module.ActiveData.Items.Count == 48 && module.ActiveData.Cache?.Status == "fresh" &&
            runtime.Calls.TakeLast(2).All(call => FilmJson.Bool(call.Parameters, "refresh")),
            "retry refreshes the rejected loaded ranges instead of silently appending a new page");
        empty = true;
        await module.RefreshDataAsync();
        Check(module.ActiveData.Items.Count == 0 && !module.ActiveData.CanPage && module.ActiveData.Cache?.Status == "fresh",
            "a successful empty category response removes previous cards and paging state");
        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        Check(module.Sections.All(section => section.Items.Count == 0 && !section.ShowSkeleton && !section.CanRetry),
            "successful empty owner responses replace all old home snapshots");
    }

    private static async Task DataPagination()
    {
        var source = new PagedFilmSource();
        await using var module = new FilmCapability(source);
        await module.NavigateAsync(FilmCapability.HomeRoute, default);

        Check(module.Sections.All(section => section.Items.Count == 12),
            "home provides the twelve-card seed for every section");
        var tv = module.Sections.Single(section => section.Key == "tv");
        await module.OpenDataAsync(tv);
        Check(module.IsData && !module.IsHome && module.Heading == "影片数据" &&
            module.ActiveData.Key == "tv" && module.ActiveData.Title == "热门剧集",
            "data route publishes the selected context");
        Check(module.ActiveData.Items.Count == 24, "the seed is revalidated and completed to the first 24 cards");
        Check(source.Calls.Where(call => call.Key == "tv")
            .Select(call => (call.Start, call.Limit))
            .SequenceEqual([(0, 12), (0, 24)]), "the first category range is checked by the owner despite a home snapshot");

        await module.LoadMoreDataAsync();
        Check(module.ActiveData.Items.Count == 48 &&
            source.Calls.Last() is { Key: "tv", Start: 24, Limit: 24 },
            "load more appends the next 24 cards");

        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        int cachedCalls = source.Calls.Count;
        await module.OpenDataAsync(tv);
        Check(module.ActiveData.Items.Count == 48 && source.Calls.Count == cachedCalls + 2 &&
            source.Calls.TakeLast(2).Select(call => (call.Start, call.Limit)).SequenceEqual([(0, 24), (24, 24)]),
            "reopening a populated context checks each loaded range without losing the full display");

        foreach (var home in module.Sections.Where(section => section.Key != "tv"))
        {
            await module.NavigateAsync(FilmCapability.HomeRoute, default);
            await module.OpenDataAsync(home);
            Check(module.ActiveData.Key == home.Key && module.ActiveData.Title == home.Title &&
                module.ActiveData.Items.Count == 24,
                $"data context {home.Key} uses its matching title and cache");
            Check(source.Calls.Last() is { Start: 0, Limit: 24 } call && call.Key == home.Key,
                $"data context {home.Key} revalidates its own first range");
        }

        var retrySource = new PagedFilmSource();
        await using var retryModule = new FilmCapability(retrySource);
        await retryModule.NavigateAsync(FilmCapability.HomeRoute, default);
        retrySource.FailOnce("movie", 0);
        await retryModule.OpenDataAsync(retryModule.Sections[0]);
        Check(retryModule.ActiveData.Items.Count == 12 &&
            retryModule.ActiveData.PageActionText == "重试" && retryModule.ActiveData.CanPage,
            "a failed first-range check retains the seed and exposes retry");
        await retryModule.LoadMoreDataAsync();
        Check(retryModule.ActiveData.Items.Count == 24 &&
            retrySource.Calls.Last() is { Key: "movie", Start: 0, Limit: 24, Section.Refresh: true },
            "retry forces and completes the original first-page range");

        retrySource.FailOnce("movie", 24);
        await retryModule.LoadMoreDataAsync();
        Check(retryModule.ActiveData.Items.Count == 24 &&
            retryModule.ActiveData.PageActionText == "重试",
            "a failed later page retains all existing cards");
        await retryModule.LoadMoreDataAsync();
        Check(retryModule.ActiveData.Items.Count == 48 &&
            retrySource.Calls.TakeLast(2).All(call =>
                call is { Key: "movie", Start: 24, Limit: 24 }),
            "later-page retry repeats the same 24-card range");
    }

    private static async Task DataFiltering()
    {
        var source = new PagedFilmSource();
        await using var module = new FilmCapability(source);
        await module.NavigateAsync(FilmCapability.HomeRoute, default);

        var movie = module.DataSections.Single(section => section.Key == "movie");
        Check(movie.FilterRows.Select(row => row.Label).SequenceEqual(["分类", "地区"]) &&
            movie.FilterRows[0].Options.Select(option => option.Label)
                .SequenceEqual(["热门电影", "最新电影", "豆瓣高分", "冷门佳片"]) &&
            movie.FilterRows[1].Options.Select(option => option.Label)
                .SequenceEqual(["全部", "华语", "欧美", "韩国", "日本"]),
            "movie filters match the server category page");
        var tv = module.DataSections.Single(section => section.Key == "tv");
        Check(tv.FilterRows.Select(row => row.Label).SequenceEqual(["分类", "类型"]) &&
            tv.FilterRows[0].Options.Select(option => option.Label)
                .SequenceEqual(["全部", "动漫", "纪录片"]) &&
            tv.FilterRows[1].Options.Select(option => option.Label)
                .SequenceEqual(["全部", "国产", "欧美", "日本", "韩国"]),
            "series filters match the server category page");
        var show = module.DataSections.Single(section => section.Key == "show");
        Check(show.FilterRows.Select(row => row.Label).SequenceEqual(["分类", "类型"]) &&
            show.FilterRows[0].Options.Select(option => option.Label).SequenceEqual(["全部"]) &&
            show.FilterRows[1].Options.Select(option => option.Label)
                .SequenceEqual(["全部", "国内", "国外"]),
            "show filters match the server category page");
        var anime = module.DataSections.Single(section => section.Key == "anime");
        Check(anime.FilterRows.Count == 1 &&
            anime.FilterRows[0].Options.Select(option => option.Label).SequenceEqual(["动漫"]) &&
            anime.FilterRows[0].Options.Single().IsSelected,
            "anime exposes only the filter supported by the approved Douban scope");

        await module.OpenDataAsync(module.Sections.Single(section => section.Key == "movie"));
        await module.SelectDataFilterAsync(Option(movie, "category", "最新电影"));
        Check(movie.Items.Count == 24 && movie.NextStart == 24 &&
            source.Calls.Last() is
            {
                Section.Category: "最新",
                Section.Type: "全部",
                Start: 0,
                Limit: 24
            }, "movie category selection clears data and restarts at zero");
        await module.SelectDataFilterAsync(Option(movie, "area", "日本"));
        Check(source.Calls.Last() is
            {
                Section.Category: "最新",
                Section.Type: "日本",
                Start: 0,
                Limit: 24
            }, "movie area selection maps to the recent_hot type");

        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        await module.OpenDataAsync(module.Sections.Single(section => section.Key == "tv"));
        await module.SelectDataFilterAsync(Option(tv, "category", "纪录片"));
        Check(source.Calls.Last() is
            {
                Section.Category: "tv",
                Section.Type: "tv_documentary",
                Start: 0,
                Limit: 24
            }, "series category overrides its type exactly as the server page");
        await module.SelectDataFilterAsync(Option(tv, "area", "日本"));
        Check(source.Calls.Last().Section.Type == "tv_documentary",
            "series area remains selected while a specific category owns the request");
        await module.SelectDataFilterAsync(Option(tv, "category", "全部"));
        Check(source.Calls.Last() is
            {
                Section.Category: "tv",
                Section.Type: "tv_japanese",
                Start: 0,
                Limit: 24
            }, "series all category restores the selected area request");

        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        await module.OpenDataAsync(module.Sections.Single(section => section.Key == "show"));
        await module.SelectDataFilterAsync(Option(show, "area", "国外"));
        Check(source.Calls.Last() is
            {
                Section.Category: "show",
                Section.Type: "show_foreign",
                Start: 0,
                Limit: 24
            }, "show type selection matches the server request");
    }

    private static async Task FilterReplacement()
    {
        var source = new NonCooperativeFilmSource();
        await using var module = new FilmCapability(source);
        var home = module.NavigateAsync(FilmCapability.HomeRoute, default);
        await source.WaitForCalls(4);
        source.CompleteRange(0, 4, "seed");
        await home;

        var movieHome = module.Sections.Single(section => section.Key == "movie");
        var data = module.OpenDataAsync(movieHome);
        await source.WaitForCalls(5);
        var movie = module.ActiveData;
        var latest = module.SelectDataFilterAsync(Option(movie, "category", "最新电影"));
        await source.WaitForCalls(6);
        var japan = module.SelectDataFilterAsync(Option(movie, "area", "日本"));
        await source.WaitForCalls(7);
        Check(source.Calls[5] is
            {
                Section.Category: "最新",
                Section.Type: "全部",
                Start: 0,
                Limit: 24
            } && source.Calls[6] is
            {
                Section.Category: "最新",
                Section.Type: "日本",
                Start: 0,
                Limit: 24
            }, "each replacement captures its own filter definition");

        source.CompleteRange(6, 1, "fresh");
        await japan;
        source.CompleteRange(4, 2, "late");
        await Task.WhenAll(data, latest);
        Check(movie.Items.Count == 24 &&
            movie.Items.All(item => item.Title.EndsWith("fresh", StringComparison.Ordinal)) &&
            movie.FilterRows.SelectMany(row => row.Options)
                .Where(option => option.IsSelected)
                .Select(option => option.Label)
                .SequenceEqual(["最新电影", "日本"]),
            "late filter results cannot overwrite the newest selection");
    }

    private static async Task CancellationAndDrain()
    {
        var source = new NonCooperativeFilmSource();
        await using var module = new FilmCapability(source);
        var oldHome = module.NavigateAsync(FilmCapability.HomeRoute, default);
        await source.WaitForCalls(4);
        var oldData = module.NavigateAsync(FilmCapability.DataRoute, default);
        await source.WaitForCalls(5);
        Check(source.Calls[4] is { Section.Key: "movie", Start: 0, Limit: 24 },
            "data route without a settled seed requests the full first page");

        var replacement = module.NavigateAsync(FilmCapability.HomeRoute, default);
        await source.WaitForCalls(9);
        source.CompleteRange(5, 4, "fresh");
        await replacement;
        Check(module.Sections.All(section =>
            section.Items.Count == 12 &&
            section.Items[0].Title.EndsWith("fresh", StringComparison.Ordinal)),
            "returning home starts a fresh generation before retired requests settle");
        source.CompleteRange(0, 5, "old");
        await Task.WhenAll(oldHome, oldData);
        Check(module.Sections.All(section =>
            section.Items[0].Title.EndsWith("fresh", StringComparison.Ordinal)),
            "late home and data results cannot replace the admitted generation");

        var drainingSource = new NonCooperativeFilmSource();
        await using var draining = new FilmCapability(drainingSource);
        var home = draining.NavigateAsync(FilmCapability.HomeRoute, default);
        await drainingSource.WaitForCalls(4);
        drainingSource.CompleteRange(0, 4, "seed");
        await home;
        var data = draining.OpenDataAsync();
        await drainingSource.WaitForCalls(5);
        Check(drainingSource.Calls[4] is { Section.Key: "movie", Start: 0, Limit: 24 },
            "settled home data is shown while its category range is checked");
        var preparation = draining.PrepareShutdownAsync();
        Check(!preparation.IsCompleted, "shutdown waits for physical data-source settlement");
        drainingSource.CompleteRange(4, 1, "late");
        await Task.WhenAll(data, preparation);
        Check(draining.ActiveData.Items.Count == 12,
            "shutdown cancellation fences the settling data response");
        await draining.CancelShutdownPreparationAsync();
        Check(draining.ActiveData.PageActionText == "重试" && draining.ActiveData.CanPage,
            "cancel preparation reopens the retired data range as retryable");
    }

    private static FilmCardItem Card(string key) =>
        FilmPlaybackFixture.Card(key == "movie" ? "movie" : "tv",
            key == "movie" ? 100 : key == "tv" ? 101 : key == "anime" ? 102 : 103, "影片 " + key, "") with { Id = key };

    private static FilmFilterOption Option(FilmDataSection section, string row, string label) =>
        section.FilterRows.Single(filter => filter.Key == row)
            .Options.Single(option => option.Label == label);

    private static IReadOnlyList<FilmCardItem> Page(
        FilmSectionDefinition section,
        int start,
        int limit,
        string suffix = "") =>
        Enumerable.Range(start + 1, limit)
            .Select(index => new FilmCardItem(
                $"{section.Key}:{index}",
                $"{section.Title} {index}{(suffix.Length == 0 ? "" : " " + suffix)}",
                "",
                index == 1 ? "8.8" : ""))
            .ToArray();

    private static async Task Failure(Func<Task> action, string code)
    {
        try
        {
            await action();
            throw new InvalidOperationException("Expected film source failure");
        }
        catch (FilmHomeException error)
        {
            Check(error.Code == code, $"expected {code}, got {error.Code}");
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static string HomePayload(string kind) => $$"""
                {
                  "items": [
                    {
                      "id": 100,
                      "businessId": "douban:{{kind}}:100",
                      "playbackTarget": {
                        "id": "douban:{{kind}}:100", "kind": "{{kind}}", "title": "第一部",
                        "metadataProvider": "douban", "metadataId": "100"
                      },
                      "title": "第一部",
                      "pic": {
                        "normal": "/v1/douban/images/view/photo/s_ratio_poster/public/p100.jpg",
                        "large": "https://img2.doubanio.com/ignored.jpg"
                      },
                      "rating": { "value": 8.6 }
                    },
                    { "id": "skip", "title": "   ", "pic": { "normal": "https://img1.doubanio.com/skip.jpg" } },
                    {
                      "id": "200",
                      "businessId": "douban:{{kind}}:200",
                      "playbackTarget": {
                        "id": "douban:{{kind}}:200", "kind": "{{kind}}", "title": "第二部",
                        "metadataProvider": "douban", "metadataId": "200"
                      },
                      "title": "第二部",
                      "pic": { "large": "https://img2.doubanio.com/large.jpg" },
                      "rating": { "value": 0 }
                    },
                    {
                      "title": "第三部",
                      "cover": { "url": "https://img3.doubanio.com/cover.jpg" },
                      "rating": { "value": "9.0" }
                    }
                  ]
                }
                """;

    private sealed record FilmCall(FilmSectionDefinition Section, int Start, int Limit)
    {
        internal string Key => Section.Key;
    }

    private sealed class ControlledFilmSource : IFilmHomeSource
    {
        private readonly Dictionary<string, Queue<TaskCompletionSource<IReadOnlyList<FilmCardItem>>>> pending = [];
        private TaskCompletionSource changed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<FilmCall> Calls { get; } = [];

        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(
            FilmSectionDefinition section,
            int start,
            int limit,
            CancellationToken cancellationToken)
        {
            Calls.Add(new(section, start, limit));
            var completion = new TaskCompletionSource<IReadOnlyList<FilmCardItem>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryGetValue(section.Key, out var queue))
                pending.Add(section.Key, queue = new());
            queue.Enqueue(completion);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            SignalChanged();
            return completion.Task;
        }

        internal async Task WaitForCalls(int count)
        {
            while (Calls.Count < count)
            {
                var wait = changed.Task;
                if (Calls.Count >= count) break;
                await wait;
                await OwnerDispatcher.YieldAsync();
            }
        }

        private void SignalChanged()
        {
            var signal = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult();
        }

        internal void Complete(string key, params FilmCardItem[] items) =>
            pending[key].Dequeue().TrySetResult(items);

        internal void Fail(string key) =>
            pending[key].Dequeue().TrySetException(new FilmHomeException("UPSTREAM_ERROR"));

        public void Dispose()
        {
        }
    }

    private sealed class PagedFilmSource : IFilmHomeSource
    {
        private readonly HashSet<(string Key, int Start)> failures = [];
        internal List<FilmCall> Calls { get; } = [];

        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(
            FilmSectionDefinition section,
            int start,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new(section, start, limit));
            if (failures.Remove((section.Key, start)))
                return Task.FromException<IReadOnlyList<FilmCardItem>>(
                    new FilmHomeException("UPSTREAM_ERROR"));
            return Task.FromResult(Page(section, start, limit));
        }

        internal void FailOnce(string key, int start) => failures.Add((key, start));

        public void Dispose()
        {
        }
    }

    private sealed class NonCooperativeFilmSource : IFilmHomeSource
    {
        internal sealed record PendingCall(
            FilmSectionDefinition Section,
            int Start,
            int Limit,
            TaskCompletionSource<IReadOnlyList<FilmCardItem>> Completion);

        private TaskCompletionSource changed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<PendingCall> Calls { get; } = [];

        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(
            FilmSectionDefinition section,
            int start,
            int limit,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<IReadOnlyList<FilmCardItem>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add(new(section, start, limit, completion));
            SignalChanged();
            return completion.Task;
        }

        internal async Task WaitForCalls(int count)
        {
            while (Calls.Count < count)
            {
                var wait = changed.Task;
                if (Calls.Count >= count) break;
                await wait;
                await OwnerDispatcher.YieldAsync();
            }
        }

        private void SignalChanged()
        {
            var signal = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult();
        }

        internal void CompleteRange(int start, int count, string suffix)
        {
            foreach (var call in Calls.Skip(start).Take(count))
                call.Completion.TrySetResult(Page(call.Section, call.Start, call.Limit, suffix));
        }

        public void Dispose()
        {
        }
    }
}

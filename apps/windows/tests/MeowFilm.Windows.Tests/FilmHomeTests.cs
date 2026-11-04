using System.Net;
using System.Net.Http;
using System.Text;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class FilmHomeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-home: skeletons end on empty failure cancellation and never cover pagination",
            () => MusicQualityConsumerTests.Sta(SkeletonStates));
        yield return new("film-home: ranged Douban requests parse bounded cards and rewrite poster hosts",
            RequestContract);
        yield return new("film-home: independent section failure caches results and retries only that section",
            () => MusicQualityConsumerTests.Sta(IndependentSections));
        yield return new("film-home: data routes preserve four contexts and page from 12 to 24 to 48",
            () => MusicQualityConsumerTests.Sta(DataPagination));
        yield return new("film-home: server-compatible filters reset requests and keep Douban anime scope",
            () => MusicQualityConsumerTests.Sta(DataFiltering));
        yield return new("film-home: rapid filter replacement fences non-cooperative older results",
            () => MusicQualityConsumerTests.Sta(FilterReplacement));
        yield return new("film-home: route replacement and shutdown fence late home and data publication",
            () => MusicQualityConsumerTests.Sta(CancellationAndDrain));
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
        var handler = new RecordingHandler();
        using var source = new DoubanFilmHomeSource(handler);
        var section = FilmSectionDefinition.Home[0];
        var items = await source.LoadAsync(section, 24, 24, default);

        Check(handler.Request is not null, "request captured");
        var request = handler.Request!;
        Check(request.Method == HttpMethod.Get, "GET only");
        Check(request.RequestUri == DoubanFilmHomeSource.BuildAddress(section, 24, 24),
            "fixed ranged recent_hot address");
        string query = Uri.UnescapeDataString(request.RequestUri!.Query);
        Check(query.Contains("start=24", StringComparison.Ordinal) &&
            query.Contains("limit=24", StringComparison.Ordinal) &&
            query.Contains("category=热门", StringComparison.Ordinal) &&
            query.Contains("type=全部", StringComparison.Ordinal), "fixed ranged request query");
        Check(request.Headers.UserAgent.ToString() == "Mozilla/5.0", "fixed user agent");
        Check(request.Headers.Accept.ToString() == "application/json, text/plain, */*", "fixed accept");
        Check(request.Headers.Referrer == new Uri("https://m.douban.com/"), "fixed referrer");
        Check(request.Headers.TryGetValues("Origin", out var origins) &&
            origins.SequenceEqual(["https://m.douban.com"]), "fixed origin");

        Check(items.Count == 3, "blank titles are filtered");
        Check(items[0] == new FilmCardItem(
            "100",
            "第一部",
            "https://img.doubanio.cmliussss.net/view/photo/s_ratio_poster/public/p100.jpg",
            "8.6"), "normal poster and numeric score");
        Check(items[1].Poster.EndsWith("/large.jpg", StringComparison.Ordinal) &&
            items[1].Score.Length == 0, "large fallback and zero score");
        Check(items[2].Id.StartsWith("movie:27:", StringComparison.Ordinal) &&
            items[2].Poster.EndsWith("/cover.jpg", StringComparison.Ordinal) &&
            items[2].Score == "9", "ranged fallback identity cover and string score");

        foreach (var definition in FilmSectionDefinition.Home)
        {
            var address = DoubanFilmHomeSource.BuildAddress(definition);
            Check(address.Scheme == "https" && address.Host == "m.douban.com" &&
                address.AbsolutePath.EndsWith("/" + definition.Kind, StringComparison.Ordinal) &&
                address.Query.Contains("start=0", StringComparison.Ordinal) &&
                address.Query.Contains("limit=12", StringComparison.Ordinal),
                "all home sections stay on the fixed origin and range");
        }
        var filtered = section with { Category = "最新", Type = "日本" };
        Check(Uri.UnescapeDataString(DoubanFilmHomeSource.BuildAddress(filtered).Query)
            .Contains("category=最新&type=日本", StringComparison.Ordinal),
            "known filter combinations extend the fixed request whitelist");
        try
        {
            _ = DoubanFilmHomeSource.BuildAddress(section with { Category = "任意" });
            throw new InvalidOperationException("Expected unknown film filter rejection");
        }
        catch (ArgumentException)
        {
        }

        using var redirect = new DoubanFilmHomeSource(new StaticHandler(new(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://example.com/") }
        }));
        await Failure(() => redirect.LoadAsync(section, 0, 12, default), "REDIRECT_DENIED");

        var oversizedContent = new ByteArrayContent([1]);
        oversizedContent.Headers.ContentLength = DoubanFilmHomeSource.MaximumResponseBytes + 1;
        using var oversized = new DoubanFilmHomeSource(new StaticHandler(new(HttpStatusCode.OK)
        {
            Content = oversizedContent
        }));
        await Failure(() => oversized.LoadAsync(section, 0, 12, default), "LIMIT");
    }

    private static async Task IndependentSections()
    {
        var source = new ControlledFilmSource();
        await using var module = new FilmCapability(source);
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
        await module.RestoreNavigationAsync(FilmCapability.DoubanRoute, default);
        Check(module.CaptureNavigationLocation() == FilmCapability.DoubanRoute,
            "capture follows the restored destination");
        await module.RestoreNavigationAsync(homeLocation, default);
        Check(source.Calls.Count == 4 && ReferenceEquals(retainedItems, module.Sections[0].Items) &&
            ReferenceEquals(retainedCard, retainedItems[0]) && module.Sections[0].HorizontalOffset == 192,
            "route restoration preserves ready collection, cards and offset without requests");

        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        Check(source.Calls.Count == 4, "resolved success and failure states are reused");

        var anime = module.Sections.Single(section => section.Key == "anime");
        var retry = module.RetrySectionAsync(anime);
        await source.WaitForCalls(5);
        Check(source.Calls.Last() is { Key: "anime", Start: 0, Limit: 12 },
            "retry requests only the failed home section");
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
        await module.OpenDoubanAsync(card);
        Check(routes.SequenceEqual([FilmCapability.DataRoute, FilmCapability.DoubanRoute]) &&
            ReferenceEquals(module.ActiveDoubanItem, card),
            "film cards retain their Douban identity and request the hidden detail route");
        Check(module.Navigation.Count == 3 &&
            module.Navigation.Single(item => item.Route == FilmCapability.HomeRoute).ShowInSidebar &&
            module.Navigation.Where(item => item.Route != FilmCapability.HomeRoute)
                .All(item => !item.ShowInSidebar),
            "only the film home is exposed in the sidebar");
        int calls = source.Calls.Count;
        await module.NavigateAsync(FilmCapability.DoubanRoute, default);
        Check(module.IsDouban && !module.IsHome && !module.IsData &&
            module.Heading == "豆瓣" && source.Calls.Count == calls,
            "the empty Douban route publishes context without another data request");
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
        Check(module.ActiveData.Items.Count == 24, "the seed is supplemented to the first 24 cards");
        Check(source.Calls.Where(call => call.Key == "tv")
            .Select(call => (call.Start, call.Limit))
            .SequenceEqual([(0, 12), (12, 12)]), "first page reuses the seed without refetching it");

        await module.LoadMoreDataAsync();
        Check(module.ActiveData.Items.Count == 48 &&
            source.Calls.Last() is { Key: "tv", Start: 24, Limit: 24 },
            "load more appends the next 24 cards");

        await module.NavigateAsync(FilmCapability.HomeRoute, default);
        int cachedCalls = source.Calls.Count;
        await module.OpenDataAsync(tv);
        Check(module.ActiveData.Items.Count == 48 && source.Calls.Count == cachedCalls,
            "reopening a populated context reuses its cached data");

        foreach (var home in module.Sections.Where(section => section.Key != "tv"))
        {
            await module.NavigateAsync(FilmCapability.HomeRoute, default);
            await module.OpenDataAsync(home);
            Check(module.ActiveData.Key == home.Key && module.ActiveData.Title == home.Title &&
                module.ActiveData.Items.Count == 24,
                $"data context {home.Key} uses its matching title and cache");
            Check(source.Calls.Last() is { Start: 12, Limit: 12 } call && call.Key == home.Key,
                $"data context {home.Key} supplements its own seed");
        }

        var retrySource = new PagedFilmSource();
        await using var retryModule = new FilmCapability(retrySource);
        await retryModule.NavigateAsync(FilmCapability.HomeRoute, default);
        retrySource.FailOnce("movie", 12);
        await retryModule.OpenDataAsync(retryModule.Sections[0]);
        Check(retryModule.ActiveData.Items.Count == 12 &&
            retryModule.ActiveData.PageActionText == "重试" && retryModule.ActiveData.CanPage,
            "a failed supplement retains the seed and exposes retry");
        await retryModule.LoadMoreDataAsync();
        Check(retryModule.ActiveData.Items.Count == 24 &&
            retrySource.Calls.Last() is { Key: "movie", Start: 12, Limit: 12 },
            "retry completes the original first-page range");

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
        Check(drainingSource.Calls[4] is { Section.Key: "movie", Start: 12, Limit: 12 },
            "settled home data seeds the pending data request");
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
        new(key, "影片 " + key, "", key == "movie" ? "8.8" : "");

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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            const string payload = """
                {
                  "items": [
                    {
                      "id": 100,
                      "title": "第一部",
                      "pic": {
                        "normal": "http://img1.doubanio.com/view/photo/s_ratio_poster/public/p100.jpg",
                        "large": "https://img2.doubanio.com/ignored.jpg"
                      },
                      "rating": { "value": 8.6 }
                    },
                    { "id": "skip", "title": "   ", "pic": { "normal": "https://img1.doubanio.com/skip.jpg" } },
                    {
                      "id": "200",
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response);
        }
    }

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
                await System.Windows.Threading.Dispatcher.Yield();
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
                await System.Windows.Threading.Dispatcher.Yield();
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

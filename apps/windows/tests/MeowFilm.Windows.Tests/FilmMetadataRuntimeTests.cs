using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>Actual Windows entry, App session, native adapter and renderer against synthetic HTTP only.</summary>
public static class FilmMetadataRuntimeTests
{
    private const string Poster = "/v1/tmdb/images/w500/poster.png";
    private const string OriginalPoster = "/v1/tmdb/images/original/poster.png";
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native settings preserve replace clear failed drafts and restore after restart", Settings);
        yield return Case("ordinary metadata cancellation leaves a late complete response in the Node cache", Cancellation);
        yield return Case("concurrent complete details share the Node producer and exact request keys remain isolated", MetadataKeys);
        yield return Case("settings revocation physically drains old metadata before the new configuration reads", SettingsInvalidation);
        yield return Case("host close physically disconnects metadata and waits for the real Node process", HostClose);
        yield return Case("artwork shutdown physically drains the real Node upstream", ArtworkShutdown);
    }
    private static TestCase Case(string name, Func<Task> action) =>
        new("film-tmdb-runtime: " + name, () => MusicQualityConsumerTests.Sta(action));
    private static void Check(bool value, string message) => SearchFixture.Check(value, message);
    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static object Payload(string type = "tv") => new
    {
        marker = "完整原响应", raw = new { unknown = new { text = "  搜索原字段  ", nullable = (string?)null } }, results = new[]
    {
        new { media_type = type, id = 7, name = "目标", title = "目标", overview = "已得搜索简介",
            poster_path = "/poster.png", first_air_date = "2023-01-01", release_date = "2023-01-01" }
    } };
    private static object DetailPayload(string overview, string type = "tv")
    {
        var detail = new Dictionary<string, object?>
        {
            ["id"] = 7, ["name"] = "目标", ["title"] = "目标", ["overview"] = overview,
            ["marker"] = "完整详情原响应", ["raw"] = new { unknown = new { text = "  详情原字段  ", nullable = (string?)null } },
            ["poster_path"] = "/poster.png", ["genres"] = new[] { new { id = 18, name = "剧情" } },
            ["credits"] = new { cast = new[] { new { id = 19, name = "演员", profile_path = "/cast.png" } }, crew = Array.Empty<object>() },
            ["similar"] = new { results = new[] { new { id = 8, title = "相关作品", poster_path = "/similar.png" } } },
            ["images"] = new { posters = new[] { new { file_path = "/poster.png", width = 600 } } }
        };
        if (type == "tv") detail["seasons"] = new[] { new { season_number = 1, episode_count = 3, air_date = "2023-01-01" } };
        else detail["runtime"] = 105;
        return detail;
    }
    private static void CompleteDetail(JsonElement value, string overview, string size = "original", string type = "tv")
    {
        string prefix = "/v1/tmdb/images/" + size;
        Check(value.GetProperty("id").GetInt32() == 7 && value.GetProperty("overview").GetString() == overview &&
            value.GetProperty("marker").GetString() == "完整详情原响应" &&
            value.GetProperty("raw").GetProperty("unknown").GetProperty("text").GetString() == "  详情原字段  " &&
            value.GetProperty("raw").GetProperty("unknown").GetProperty("nullable").ValueKind == JsonValueKind.Null,
            "the complete original detail keeps its identity, unknown fields, whitespace and nulls");
        Check(value.GetProperty("genres")[0].GetProperty("id").GetInt32() == 18 &&
            (type == "tv" ? value.GetProperty("seasons")[0].GetProperty("episode_count").GetInt32() == 3 :
                value.GetProperty("runtime").GetInt32() == 105), "full detail satisfies the actual genre and tv/movie completeness contract");
        Check(value.GetProperty("poster_path").GetString() == prefix + "/poster.png" &&
            value.GetProperty("credits").GetProperty("cast")[0].GetProperty("profile_path").GetString() == prefix + "/cast.png" &&
            value.GetProperty("credits").GetProperty("crew").GetArrayLength() == 0 &&
            value.GetProperty("similar").GetProperty("results")[0].GetProperty("poster_path").GetString() == prefix + "/similar.png" &&
            value.GetProperty("images").GetProperty("posters")[0].GetProperty("file_path").GetString() == prefix + "/poster.png",
            "credits, similar and images arrive intact with the requested stable image size");
    }
    private static object SearchIdentity(string query) =>
        new { kind = "metadata", provider = "tmdb", path = "/3/search/multi",
            query = new { query, page = 1, language = "zh-CN" }, imageSize = "w500" };
    private static object DetailIdentity(string type = "tv", string size = "original") =>
        new { kind = "metadata", provider = "tmdb", path = "/3/" + type + "/7",
            query = new { language = "zh-CN", append_to_response = "credits,similar,images" }, imageSize = size };
    private static Dictionary<string, string> Query(RuntimeHttpClientTests.Exchange exchange) =>
        new Uri("http://fixture.invalid" + exchange.Path).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair.Length == 2 ? pair[1].Replace("+", " ", StringComparison.Ordinal) : ""));
    private static void Request(RuntimeHttpClientTests.Exchange exchange, string path)
    {
        Check(exchange.Method == "GET" && exchange.Path.Split('?')[0] == path, "the actual upstream uses the requested read-only GET route");
        Check(!exchange.Headers.ContainsKey("X-Meow-Session") && !exchange.Headers.ContainsKey("X-Meow-Request-Id"),
            "native runtime authentication is never forwarded to an upstream provider");
    }
    private static void DetailRequest(RuntimeHttpClientTests.Exchange exchange, string path)
    {
        Request(exchange, path);
        var query = Query(exchange);
        Check(query.Count == 2 && query["language"] == "zh-CN" && query["append_to_response"] == "credits,similar,images",
            "complete detail uses the exact identity, language and full append set");
    }

    internal static async Task SearchArtwork()
    {
        await using var api = new RuntimeHttpClientTests.Server();
        await using var images = new RuntimeHttpClientTests.Server();
        string data = NewData();
        await using var first = await RuntimeHost.OpenAsync(data);
        await first.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        { ["apiBase"] = api.BaseUrl + "/api/3", ["imageBase"] = api.BaseUrl + "/img" });
        await using var search = new FilmSearchService(first.Client.CaptureConnectionAsync, new SearchSources(), first.Metadata.ResolveImage);
        var session = search.Start("原查询2");
        await using (var exchange = await api.AcceptAsync())
        {
            Request(exchange, "/api/3/search/multi");
            var query = Query(exchange);
            Check(query["query"] == "原查询2" && query["page"] == "1" && query["language"] == "zh-CN" &&
                !query.ContainsKey("api_key") && !exchange.Headers.ContainsKey("Authorization"),
                "the real adapter passes the original query, page and language even with no client credentials");
            await exchange.ReplyAsync(Json(Payload()));
        }
        await using (var exchange = await api.AcceptAsync())
        {
            DetailRequest(exchange, "/api/3/tv/7");
            await exchange.ReplyAsync(Json(DetailPayload("真实详情回执")));
        }
        await SearchFixture.Settle(session);
        var item = session.Items.Single(value => value.GetProperty("sourceKind").GetString() == "tmdb");
        var context = new FilmSearchContext(session, item);
        var handoff = await context.HandoffAsync(default);
        Check(context.Query == "原查询2" && context.MetadataSearchPayload!.Value.GetProperty("marker").GetString() == "完整原响应" &&
            handoff.Metadata!.Value.GetProperty("overview").GetString() == "真实详情回执" &&
            handoff.Metadata!.Value.GetProperty("poster_path").GetString() == OriginalPoster, "full search and detail JSON reach the native context");
        string searchPoster = context.MetadataSearchPayload!.Value.GetProperty("results")[0].GetProperty("poster_path").GetString()!;
        Check(searchPoster == Poster &&
            context.MetadataSearchPayload!.Value.GetProperty("raw").GetProperty("unknown").GetProperty("text").GetString() == "  搜索原字段  " &&
            context.MetadataSearchPayload!.Value.GetProperty("raw").GetProperty("unknown").GetProperty("nullable").ValueKind == JsonValueKind.Null,
            "search keeps w500 image references and complete unknown raw fields independently of full detail");
        CompleteDetail(handoff.Metadata!.Value, "真实详情回执");
        Check(item.GetProperty("poster").GetString() == OriginalPoster && first.Metadata.ResolveImage(searchPoster) == searchPoster,
            "the enhanced card uses original detail while stable search image references stay unchanged");
        await search.LoadMoreAsync();
        Check(!api.HasConnection && ReferenceEquals(search.Start(session.Query), session), "more and same-word reuse do not request metadata again");
        await first.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        { ["apiKey"] = "synthetic-image-isolation-key", ["accessToken"] = "synthetic-image-isolation-token" });

        IRuntimeImageClient current = first.Client;
        await using var artwork = new ArtworkService(readTrusted: (reference, budget, token) => current.ReadImageAsync(reference, budget, token));
        // Exercise the retained list's w500 reference, not the detail-enriched card's original poster.
        var pixels = artwork.AcquireAsync(searchPoster);
        await using (var exchange = await api.AcceptAsync())
        {
            Request(exchange, "/img/t/p/w500/poster.png");
            Check(!exchange.Headers.ContainsKey("Authorization") && Query(exchange).Count == 0,
                "image requests never carry upstream API credentials");
            await exchange.ReplyAsync(RuntimeImageTests.Png, contentType: "image/png");
        }
        using var lease = await pixels;
        Check(lease.Image.IsFrozen && lease.Image.PixelWidth > 0 && lease.Image.PixelHeight > 0, "the actual native decoder returns detached pixels");
        var image = new ArtworkImage { Address = searchPoster, Width = 180, Height = 240 };
        var root = new Grid { Width = 300, Height = 300, Background = Brushes.White };
        ArtworkScope.SetLoader(root, artwork); root.Children.Add(image);
        using (var surface = Mount(root))
        {
            await SearchFixture.Until(() => ((Image)image.Children[0]).Source is not null);
            Capture(root, "tmdb-artwork.png");
        }
        await first.Metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["imageBase"] = images.BaseUrl + "/pictures" });
        using (var cached = await artwork.AcquireAsync(searchPoster))
            Check(ReferenceEquals(cached.Image, lease.Image) && !api.HasConnection && !images.HasConnection,
                "changing the proxy base does not rename or bypass a cached relative resource");
        var uncached = artwork.AcquireAsync("/v1/tmdb/images/w300/poster.png");
        await using (var exchange = await images.AcceptAsync())
        {
            Request(exchange, "/pictures/t/p/w300/poster.png");
            await exchange.ReplyAsync(RuntimeImageTests.Png, contentType: "image/png");
        }
        using var resized = await uncached;
        int oldProcess = first.ProcessId;
        await first.DisposeAsync();
        await using var second = await RuntimeHost.OpenAsync(data);
        current = second.Client;
        Check(second.Metadata.Settings!.ImageBase == images.BaseUrl + "/pictures", "a new real Node process restores the runtime-owned settings");
        using (var cached = await artwork.AcquireAsync(searchPoster))
            Check(ReferenceEquals(cached.Image, lease.Image), "a new session endpoint cannot alter the stable cache identity");
        var restarted = artwork.AcquireAsync("/v1/tmdb/images/w500/restarted.png");
        await using (var exchange = await images.AcceptAsync())
        {
            Request(exchange, "/pictures/t/p/w500/restarted.png");
            await exchange.ReplyAsync(RuntimeImageTests.Png, contentType: "image/png");
        }
        using var restored = await restarted;
        Console.WriteLine($"TMDB_RUNTIME restart=true; oldProcess={oldProcess}; currentProcess={second.ProcessId}; stableCache=true; publicNetwork=false");
    }

    internal static async Task Failures()
    {
        await using var upstream = new RuntimeHttpClientTests.Server();
        await using var host = await RuntimeHost.OpenAsync(NewData());
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        { ["apiBase"] = upstream.BaseUrl, ["imageBase"] = upstream.BaseUrl + "/img" });
        await using var search = new FilmSearchService(host.Client.CaptureConnectionAsync, new SearchSources(), host.Metadata.ResolveImage);
        var failed = search.Start("失败2");
        await using (var exchange = await upstream.AcceptAsync())
            await exchange.ReplyAsync(Json(new { error = "synthetic upstream failure" }), 503);
        await SearchFixture.Settle(failed);
        Check(failed.MetadataState == FilmSearchChannelState.Error && failed.MetadataSearchPayload is null && !upstream.HasConnection,
            "upstream failure is not a successful empty response and never causes query fallback");
        var session = search.Start("目标2");
        await using (var exchange = await upstream.AcceptAsync())
        {
            Check(Query(exchange)["query"] == "目标2", "fallback starts with the actual original query");
            await exchange.ReplyAsync(Json(new { marker = "empty original", results = Array.Empty<object>() }));
        }
        await using (var exchange = await upstream.AcceptAsync())
        {
            Check(Query(exchange)["query"] == "目标", "only the shared search decision requests the suffix fallback");
            await exchange.ReplyAsync(Json(Payload("movie")));
        }
        await using (var exchange = await upstream.AcceptAsync())
        {
            DetailRequest(exchange, "/3/movie/7");
            await exchange.ReplyAsync(Json(new { error = "synthetic detail failure" }), 404);
        }
        await SearchFixture.Settle(session);
        var context = new FilmSearchContext(session, session.Items.Single());
        var handoff = await context.HandoffAsync(default);
        Check(session.MetadataState == FilmSearchChannelState.Completed && context.Query == "目标2" && context.MetadataQuery == "目标" &&
            handoff.Metadata!.Value.GetProperty("overview").GetString() == "已得搜索简介" &&
            context.MetadataSearchPayload!.Value.GetProperty("marker").GetString() == "完整原响应",
            "the failed real movie detail cannot discard the effective fallback query or full original search metadata");
        await using var artwork = new ArtworkService(readTrusted: host.Client.ReadImageAsync);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var missing = artwork.AcquireAsync(Poster);
            await using var exchange = await upstream.AcceptAsync();
            await exchange.ReplyAsync(Json(new { error = "synthetic image failure" }), 404);
            try { using var unexpected = await missing; throw new InvalidOperationException("Failed image became pixels"); }
            catch (ArtworkException) { }
        }
        var incomplete = artwork.AcquireAsync(Poster);
        await using (var exchange = await upstream.AcceptAsync())
            await exchange.ReplyAsync(RuntimeImageTests.Png[..^12], contentType: "image/png");
        try { using var ignored = await incomplete; throw new InvalidOperationException("Missing IEND became cached pixels"); }
        catch (ArtworkException error) { Check(error.Code == "ARTWORK_FORMAT", "a complete HTTP response still requires a complete PNG container"); }
        Check(!upstream.HasConnection, "rejecting partial pixels never retries automatically");
        var retry = artwork.AcquireAsync(Poster);
        await using (var exchange = await upstream.AcceptAsync())
        {
            Request(exchange, "/img/t/p/w500/poster.png");
            await exchange.ReplyAsync(RuntimeImageTests.Png, contentType: "image/png");
        }
        using var recovered = await retry;
        using var cached = await artwork.AcquireAsync(Poster);
        Check(recovered.Image.IsFrozen && ReferenceEquals(recovered.Image, cached.Image) && !upstream.HasConnection,
            "the exact stable reference succeeds on explicit retry and only the complete pixels enter cache");
    }

    private static async Task Settings()
    {
        await using var upstream = new RuntimeHttpClientTests.Server();
        string data = NewData();
        await using var host = await RuntimeHost.OpenAsync(data);
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        {
            ["apiBase"] = upstream.BaseUrl, ["imageBase"] = upstream.BaseUrl + "/img",
            ["apiKey"] = "synthetic-api-key", ["accessToken"] = "synthetic.access.token"
        });
        string documents = Path.Combine(data, "documents");
        Directory.CreateDirectory(documents);
        await using (var store = new LocalDocumentStore(documents,
            [FilmSourceService.DocumentNamespace]))
        {
            var rules = new FilmRuleService(host.Client);
            var sources = new FilmSourceService(host.Client, store);
            await using var film = new FilmCapability(rules, sources, host.Client, host.Metadata);
            var view = (FilmSettingsView)film.SettingsView;
            view.Width = 900; view.Height = 650;
            using var surface = Mount(view);
            var navigation = (StackPanel)view.FindName("SettingsNavigation");
            navigation.Children.OfType<RadioButton>().Single(button => Equals(button.Tag, "metadata")).IsChecked = true;
            await SearchFixture.Until(() => view.MetadataLoaded && !view.MetadataBusy);
            var token = (TextBox)view.FindName("MetadataApiToken");
            var apiBase = (TextBox)view.FindName("MetadataApiBase");
            Check(token.Text == "synthetic.access.token" && AutomationProperties.GetName(token) == "TMDB API TOKEN",
                "the actual settings form exposes one editable plain token");
            Capture(view, "tmdb-settings-wide.png");
            view.Width = 420; Layout(view);
            Check(apiBase.ActualWidth > 100 && apiBase.ActualWidth < 420 && token.ActualWidth > 80 &&
                token.ActualWidth < 420, "native fields retain usable widths inside the narrow settings viewport");
            Capture(view, "tmdb-settings-narrow.png");
            await SaveView(view);
            await Credentials(host.Metadata, upstream, "初始保留", expectedToken: "synthetic.access.token");
            await SaveView(view);
            Check(host.Metadata.Settings is { ApiKey: "synthetic.access.token", AccessToken: "" } &&
                token.Text == "synthetic.access.token", "an unedited plain token survives another save");
            await Credentials(host.Metadata, upstream, "未修改", expectedToken: "synthetic.access.token");
            token.Text = "synthetic.replacement.token";
            await SaveView(view);
            Check(token.Text == "synthetic.replacement.token", "successful replacement remains visible and editable");
            await Credentials(host.Metadata, upstream, "替换", expectedToken: "synthetic.replacement.token");
            token.Text = "synthetic-replacement-key"; await SaveView(view);
            Check(host.Metadata.Settings is { ApiKey: "synthetic-replacement-key", AccessToken: "" },
                "the same input replaces v4 authentication with a v3 key");
            await Credentials(host.Metadata, upstream, "改用key", expectedKey: "synthetic-replacement-key");
            token.Text = "   "; await SaveView(view);
            Check(host.Metadata.Settings is { ApiKey: "", AccessToken: "" } && token.Text == "",
                "an empty save clears credentials without a separate clear action");
            await Credentials(host.Metadata, upstream, "清空");
            string savedBase = host.Metadata.Settings!.ApiBase;
            apiBase.Text = "https://invalid.example/?query=not-a-base"; token.Text = "synthetic-retry-key";
            await SaveView(view);
            Check(apiBase.Text.Contains("?query=", StringComparison.Ordinal) && token.Text == "synthetic-retry-key" &&
                host.Metadata.Settings!.ApiBase == savedBase && host.Metadata.Settings!.ApiToken == "",
                "failed validation retains drafts and cannot publish unsaved settings");
            apiBase.Text = savedBase; await SaveView(view);
            Check(host.Metadata.Settings!.ApiKey == "synthetic-retry-key" && token.Text == "synthetic-retry-key",
                "the unchanged failure draft can be retried explicitly");
            surface.RootVisual = null;
        }
        await host.DisposeAsync();
        await using var restored = await RuntimeHost.OpenAsync(data);
        Check(restored.Metadata.Settings is { ApiKey: "synthetic-retry-key", AccessToken: "" },
            "runtime-owned plain and cleared credentials restore in a new process");
        await Credentials(restored.Metadata, upstream, "重启恢复", expectedKey: "synthetic-retry-key");
    }

    private static async Task Credentials(FilmMetadataService metadata, RuntimeHttpClientTests.Server upstream, string probe,
        string? expectedToken = null, string? expectedKey = null)
    {
        // Each probe is a distinct content key: a no-op settings save must not be
        // mistaken for revocation merely to force another credential observation.
        string word = "配置验证:" + probe;
        var search = metadata.SearchAsync(word, default);
        await using var exchange = await upstream.AcceptAsync();
        Request(exchange, "/3/search/multi");
        var query = Query(exchange);
        Check(query["query"] == word && query["page"] == "1" && query["language"] == "zh-CN" &&
            query["region"] == "CN" && query["include_adult"] == "false",
            "each settings credential probe uses a distinct exact metadata search key and configured defaults");
        Check(expectedToken is null ? !exchange.Headers.ContainsKey("Authorization") :
                exchange.Headers.GetValueOrDefault("Authorization") == "Bearer " + expectedToken,
            "only the configured upstream token is used");
        Check(expectedKey is null ? !query.ContainsKey("api_key") : query.GetValueOrDefault("api_key") == expectedKey,
            "API key is preserved, replaced or cleared by saving an empty input");
        await exchange.ReplyAsync(Json(new { results = Array.Empty<object>() }));
        await search;
    }
    private static async Task SaveView(FilmSettingsView view)
    {
        ((Button)view.FindName("MetadataSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SearchFixture.Until(() => !view.MetadataBusy);
    }
    private static async Task Cancellation()
    {
        await using var upstream = new RuntimeHttpClientTests.Server();
        await using var host = await RuntimeHost.OpenAsync(NewData());
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        { ["apiBase"] = upstream.BaseUrl, ["imageBase"] = upstream.BaseUrl + "/img" });
        using var cancel = new CancellationTokenSource();
        var query = host.Metadata.SearchAsync("挂起", cancel.Token);
        await using var pending = await upstream.AcceptAsync();
        Request(pending, "/3/search/multi");
        cancel.Cancel();
        await Cancelled(query);
        Check((await host.FilmAsync("data.cache.entry", SearchIdentity("挂起"))).ValueKind == JsonValueKind.Null,
            "a cancelled waiter cannot manufacture a cache receipt before the complete body arrives");
        await pending.ReplyAsync(Json(Payload()));
        var receipt = await CachedAsync(host, SearchIdentity("挂起"));
        Check(receipt.GetProperty("completeness").GetString() == "complete" &&
            receipt.GetProperty("imageSize").GetString() == "w500" &&
            receipt.GetProperty("settings").GetProperty("revision").GetString() == await host.SettingsRevisionAsync() &&
            receipt.GetProperty("fetchedAt").GetInt64() > 0 &&
            receipt.GetProperty("expiresAt").GetInt64() > receipt.GetProperty("fetchedAt").GetInt64(),
            "only the real Node producer supplies complete content and original configuration/freshness evidence after UI cancellation");
        var repeated = await host.Metadata.SearchAsync("挂起", default).WaitAsync(TimeSpan.FromSeconds(5));
        var retained = await host.FilmAsync("data.cache.entry", SearchIdentity("挂起"));
        Check(JsonElement.DeepEquals(repeated, receipt.GetProperty("value")) &&
            retained.GetProperty("fetchedAt").GetInt64() == receipt.GetProperty("fetchedAt").GetInt64() && !upstream.HasConnection,
            "a later same-key read returns the late original body without a second download or a new native freshness clock");
        Console.WriteLine("TMDB_RUNTIME ordinaryCancel=waiterOnly; lateCache=complete; sameKeyUpstreamReads=1; publicNetwork=false");
    }

    private static async Task MetadataKeys()
    {
        await using var upstream = new RuntimeHttpClientTests.Server();
        await using var host = await RuntimeHost.OpenAsync(NewData());
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["apiBase"] = upstream.BaseUrl });
        var first = host.Metadata.DetailAsync("tv", 7, default);
        await using (var pending = await upstream.AcceptAsync())
        {
            DetailRequest(pending, "/3/tv/7");
            var second = host.Metadata.DetailAsync("tv", 7, default);
            Check((await host.FilmAsync("data.cache.entry", DetailIdentity())).ValueKind == JsonValueKind.Null &&
                !first.IsCompleted && !second.IsCompleted, "concurrent full-detail waiters cannot complete from a partial or invented cache entry");
            await pending.ReplyAsync(Json(DetailPayload("并发详情")));
            var values = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            CompleteDetail(values[0], "并发详情");
            Check(JsonElement.DeepEquals(values[0], values[1]) && !upstream.HasConnection,
                "two concurrent complete-detail reads share one actual upstream body");
        }
        var receipt = await CachedAsync(host, DetailIdentity());
        var repeat = await host.Metadata.DetailAsync("tv", 7, default).WaitAsync(TimeSpan.FromSeconds(5));
        Check(JsonElement.DeepEquals(repeat, receipt.GetProperty("value")) && !upstream.HasConnection,
            "a new native detail call uses the unique Node complete-detail cache");
        Check((await host.FilmAsync("data.cache.entry", DetailIdentity(size: "w500"))).ValueKind == JsonValueKind.Null,
            "the original detail is not a freshness receipt for a different image size");
        var resized = host.FilmAsync("data.metadata", new
        {
            provider = "tmdb", path = "/3/tv/7", query = new { language = "zh-CN", append_to_response = "credits,similar,images" },
            imageSize = "w500"
        });
        await using (var pending = await upstream.AcceptAsync())
        {
            DetailRequest(pending, "/3/tv/7");
            await pending.ReplyAsync(Json(DetailPayload("不同图片尺寸")));
        }
        CompleteDetail(await resized, "不同图片尺寸", "w500");
        var differentQuery = host.FilmAsync("data.metadata", new
        {
            provider = "tmdb", path = "/3/tv/7", query = new { language = "en-US", append_to_response = "credits,similar,images" },
            imageSize = "original"
        });
        await using (var pending = await upstream.AcceptAsync())
        {
            Request(pending, "/3/tv/7");
            var query = Query(pending);
            Check(query.Count == 2 && query["language"] == "en-US" && query["append_to_response"] == "credits,similar,images",
                "a different query is its own Node metadata identity, not the existing zh-CN detail");
            await pending.ReplyAsync(Json(DetailPayload("不同查询")));
        }
        CompleteDetail(await differentQuery, "不同查询");
        var movie = host.Metadata.DetailAsync("movie", 7, default);
        await using (var pending = await upstream.AcceptAsync())
        {
            DetailRequest(pending, "/3/movie/7");
            await pending.ReplyAsync(Json(DetailPayload("真实电影详情", "movie")));
        }
        CompleteDetail(await movie, "真实电影详情", type: "movie");
        Check((await host.FilmAsync("data.cache.entry", DetailIdentity())).GetProperty("fetchedAt").GetInt64() ==
            receipt.GetProperty("fetchedAt").GetInt64() && !upstream.HasConnection,
            "movie/type, query and size variants do not replace the original exact-key receipt");
    }

    private static async Task SettingsInvalidation()
    {
        await using var before = new RuntimeHttpClientTests.Server();
        await using var after = new RuntimeHttpClientTests.Server();
        await using var host = await RuntimeHost.OpenAsync(NewData());
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["apiBase"] = before.BaseUrl });
        string oldRevision = await host.SettingsRevisionAsync();
        var ready = host.Metadata.DetailAsync("tv", 7, default);
        await using (var exchange = await before.AcceptAsync())
        {
            DetailRequest(exchange, "/3/tv/7");
            await exchange.ReplyAsync(Json(DetailPayload("旧配置详情")));
        }
        CompleteDetail(await ready, "旧配置详情");
        var oldEntry = await CachedAsync(host, DetailIdentity());
        Check(oldEntry.GetProperty("settings").GetProperty("revision").GetString() == oldRevision,
            "the old ready entry carries its actual metadata settings revision");
        var query = host.Metadata.SearchAsync("撤权", default);
        await using var pending = await before.AcceptAsync();
        Request(pending, "/3/search/multi");
        var save = host.Metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["apiBase"] = after.BaseUrl });
        await pending.AssertDisconnectedAsync();
        await save.WaitAsync(TimeSpan.FromSeconds(10));
        await Cancelled(query);
        Check(await host.SettingsRevisionAsync() != oldRevision &&
            (await host.FilmAsync("data.cache.entry", SearchIdentity("撤权"))).ValueKind == JsonValueKind.Null &&
            (await host.FilmAsync("data.cache.entry", DetailIdentity())).ValueKind == JsonValueKind.Null &&
            !before.HasConnection && !after.HasConnection,
            "the completed settings mutation has a disconnected old socket and neither pending nor ready old content is authorized by the new revision");
        var current = host.Metadata.DetailAsync("tv", 7, default);
        await using (var exchange = await after.AcceptAsync())
        {
            DetailRequest(exchange, "/3/tv/7");
            await exchange.ReplyAsync(Json(DetailPayload("新配置详情")));
        }
        CompleteDetail(await current, "新配置详情");
        var nextEntry = await CachedAsync(host, DetailIdentity());
        Check(nextEntry.GetProperty("settings").GetProperty("revision").GetString() == await host.SettingsRevisionAsync() &&
            nextEntry.GetProperty("settings").GetProperty("revision").GetString() != oldRevision &&
            !before.HasConnection && !after.HasConnection,
            "the same content identity reads the new endpoint once and retains only the new revision");
        Console.WriteLine("TMDB_RUNTIME settingsRevoked=true; oldSocket=disconnected; oldCache=revoked; newEndpointReads=1; publicNetwork=false");
    }

    private static async Task HostClose()
    {
        await using var upstream = new RuntimeHttpClientTests.Server();
        await using var host = await RuntimeHost.OpenAsync(NewData());
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["apiBase"] = upstream.BaseUrl });
        var query = host.Metadata.SearchAsync("关闭", default);
        await using var pending = await upstream.AcceptAsync();
        Request(pending, "/3/search/multi");
        int process = host.ProcessId;
        var close = host.DisposeAsync().AsTask();
        await pending.AssertDisconnectedAsync();
        await Cancelled(query);
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Check(host.Client.State == RuntimeConnectionState.Closed && !upstream.HasConnection,
            "host close waits for the real metadata socket and child-process drain, not just an abandoned native waiter");
        Console.WriteLine($"TMDB_RUNTIME hostClosed=true; process={process}; physicalProcessExit=true; metadataSocket=disconnected; publicNetwork=false");
    }

    private static async Task ArtworkShutdown()
    {
        await using var upstream = new RuntimeHttpClientTests.Server();
        await using var host = await RuntimeHost.OpenAsync(NewData());
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        { ["apiBase"] = upstream.BaseUrl, ["imageBase"] = upstream.BaseUrl + "/img" });
        await using var artwork = new ArtworkService(readTrusted: host.Client.ReadImageAsync);
        var lease = artwork.AcquireAsync(Poster);
        await using var image = await upstream.AcceptAsync();
        await image.BeginImageAsync(100);
        var closeArtwork = artwork.DisposeAsync().AsTask();
        try { using var ignored = await lease; throw new InvalidOperationException("Closed artwork returned pixels"); }
        catch (OperationCanceledException) { }
        catch (ArtworkException) { }
        await closeArtwork.WaitAsync(TimeSpan.FromSeconds(10));
        await image.AssertDisconnectedAsync();
        await host.DisposeAsync();
        Check(!upstream.HasConnection, "real artwork shutdown and host close do not replay any upstream operation");
    }

    private static async Task Cancelled(Task operation)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Expected metadata cancellation"); }
        catch (OperationCanceledException) { }
        catch (FilmPlaybackException error) when (error.Code == "CANCELLED") { }
    }
    private static async Task<JsonElement> CachedAsync(RuntimeHost host, object identity)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var entry = await host.FilmAsync("data.cache.entry", identity, deadline.Token);
            if (entry.ValueKind == JsonValueKind.Object) return entry;
            Check(entry.ValueKind == JsonValueKind.Null, "a cache miss is null, never a native manufactured receipt");
            await Task.Delay(5, deadline.Token);
        }
    }

    internal sealed class RuntimeHost : IAsyncDisposable
    {
        internal readonly AppRuntimeConnectionHost Client;
        internal readonly FilmMetadataService Metadata;
        private NodeRuntime? session;
        internal int ProcessId => session!.ProcessId;
        private Task? disposal;
        private RuntimeHost(string root, RuntimeBundle bundle)
        {
            Client = new(async token => session = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle,
                AppIdentity.ScriptModules, Path.Combine(root, "script-runtime"), token, ownerRevoked: token));
            Metadata = new(Client); Client.Start();
        }
        internal static async Task<RuntimeHost> OpenAsync(string root)
        {
            string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
                ?? throw new InvalidOperationException("A real eight-module Windows runtime bundle is required.");
            var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), AppIdentity.RuntimeId, AppIdentity.ScriptModules);
            var host = new RuntimeHost(root, bundle);
            try
            {
                await host.Metadata.GetSettingsAsync();
                var connection = await host.Client.CaptureConnectionAsync();
                Check(ReferenceEquals(connection.Client, host.session) && connection.Revoked.CanBeCanceled &&
                    !connection.Revoked.IsCancellationRequested,
                    "the production App connection adapter captures the actual Node producer and revocable owner");
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }
        internal async Task<JsonElement> FilmAsync(string operation, object input, CancellationToken token = default)
        {
            var owner = await Client.CaptureConnectionAsync(token);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Revoked);
            return await new FilmPlaybackClient(owner.Client).CallAsync(operation, input, wait.Token);
        }
        internal async Task<string> SettingsRevisionAsync()
        {
            var envelope = await Client.CallAsync("tmdb.data.settings.get", new { });
            Check(envelope.GetProperty("ok").GetBoolean() && envelope.GetProperty("contract").GetString() == "tmdb.data.v1",
                "metadata configuration retains its own formal settings envelope");
            string revision = envelope.GetProperty("value").GetProperty("revision").GetString()!;
            Check(!string.IsNullOrWhiteSpace(revision), "the runtime alone supplies the actual settings revision");
            return revision;
        }
        public ValueTask DisposeAsync() => new(disposal ??= CloseAsync());
        private async Task CloseAsync()
        {
            await Metadata.DisposeAsync();
            using var child = session is { } active ? Process.GetProcessById(active.ProcessId) : null;
            if (child is not null) _ = child.SafeHandle;
            await Client.DisposeAsync();
            if (session is not null) await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            if (child is not null)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(child.HasExited, "the owned real Node process exits after drain");
            }
        }
    }
    private static string NewData()
    {
        string root = TestWorkspace.DataPath("tmdb", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject value)
    {
        yield return value;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(value); index++)
            foreach (var descendant in Visuals(VisualTreeHelper.GetChild(value, index))) yield return descendant;
    }
    private static void Layout(FrameworkElement view)
    { view.Measure(new Size(view.Width, view.Height)); view.Arrange(new Rect(0, 0, view.Width, view.Height)); view.UpdateLayout(); }
    private static HwndSource Mount(FrameworkElement view)
    {
        view.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
        var surface = new HwndSource(new HwndSourceParameters("tmdb-native-offscreen")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = (int)view.Width, Height = (int)view.Height
        });
        surface.RootVisual = view; Layout(view); return surface;
    }
    private static void Capture(FrameworkElement view, string name)
    {
        Layout(view);
        var bitmap = new RenderTargetBitmap((int)view.Width, (int)view.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        string path = TestWorkspace.DataPath("film-search", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var output = File.Create(path)) png.Save(output);
        Check(new FileInfo(path).Length < 700_000, "native preview must remain below the user image-read boundary");
        Console.WriteLine("TMDB_PREVIEW " + path);
    }
}

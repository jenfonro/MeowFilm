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
using MeowFilm.Platform.Scripting;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>Actual Windows entry, App session, native adapter and renderer against synthetic HTTP only.</summary>
public static class FilmMetadataRuntimeTests
{
    private const string Poster = "/v1/tmdb/images/w500/poster.png";
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("real App adapter search detail artwork and restart preserve stable identities", SearchArtwork);
        yield return Case("source failures stay errors and failed detail preserves complete original search metadata", Failures);
        yield return Case("native settings preserve replace clear failed drafts and restore after restart", Settings);
        yield return Case("cancelled metadata and artwork shutdown drain the real Node upstream", Cancellation);
    }
    private static TestCase Case(string name, Func<Task> action) =>
        new("film-tmdb-runtime: " + name, () => MusicQualityConsumerTests.Sta(action));
    private static void Check(bool value, string message) => SearchFixture.Check(value, message);
    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static object Payload(string type = "tv") => new { marker = "完整原响应", results = new[]
    {
        new { media_type = type, id = 7, name = "目标", title = "目标", overview = "已得搜索简介",
            poster_path = "/poster.png", first_air_date = "2023-01-01", release_date = "2023-01-01" }
    } };
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

    private static async Task SearchArtwork()
    {
        await using var api = new RuntimeHttpClientTests.Server();
        await using var images = new RuntimeHttpClientTests.Server();
        string data = NewData();
        await using var first = await RuntimeHost.OpenAsync(data);
        await first.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        { ["apiBase"] = api.BaseUrl + "/api", ["imageBase"] = api.BaseUrl + "/img" });
        var sources = new SearchSources(SearchFixture.Source("a"))
        { Read = (_, _, page, _) => Task.FromResult(SearchFixture.Page(2, (page.ToString(), "站源", ""))) };
        await using var search = new FilmSearchService(new SearchEngine(), sources,
            _ => Task.FromResult<IReadOnlyList<string>>([]), first.Metadata);
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
            Request(exchange, "/api/3/tv/7");
            Check(Query(exchange)["language"] == "zh-CN", "detail retains the exact type and ID with the required language");
            await exchange.ReplyAsync(Json(new { id = 7, name = "目标", overview = "真实详情回执", poster_path = "/poster.png",
                seasons = new[] { new { season_number = 1, episode_count = 3, air_date = "2023-01-01" } } }));
        }
        await SearchFixture.Settle(session);
        var item = session.Items.Single(value => value.GetProperty("sourceKind").GetString() == "tmdb");
        var context = new FilmSearchContext(session, item);
        Check(context.Query == "原查询2" && context.MetadataSearchPayload!.Value.GetProperty("marker").GetString() == "完整原响应" &&
            context.Metadata!.Value.GetProperty("overview").GetString() == "真实详情回执" &&
            context.Metadata!.Value.GetProperty("poster_path").GetString() == Poster, "full search and detail JSON reach the native context");
        Check(item.GetProperty("poster").GetString() == Poster && first.Metadata.ResolveImage(Poster) == Poster,
            "w500 imageSize is observable as the stable relative image reference");
        await search.LoadMoreAsync();
        Check(!api.HasConnection && ReferenceEquals(search.Start(session.Query), session), "more and same-word reuse do not request metadata again");
        await first.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        { ["apiKey"] = "synthetic-image-isolation-key", ["accessToken"] = "synthetic-image-isolation-token" });

        IRuntimeImageClient current = first.Client;
        await using var artwork = new ArtworkService(readTrusted: (reference, budget, token) => current.ReadImageAsync(reference, budget, token));
        var pixels = artwork.AcquireAsync(Poster);
        await using (var exchange = await api.AcceptAsync())
        {
            Request(exchange, "/img/w500/poster.png");
            Check(!exchange.Headers.ContainsKey("Authorization") && Query(exchange).Count == 0,
                "image requests never carry upstream API credentials");
            await exchange.ReplyAsync(RuntimeImageTests.Png, contentType: "image/png");
        }
        using var lease = await pixels;
        Check(lease.Image.IsFrozen && lease.Image.PixelWidth > 0 && lease.Image.PixelHeight > 0, "the actual native decoder returns detached pixels");
        var image = new ArtworkImage { Address = Poster, Width = 180, Height = 240 };
        var root = new Grid { Width = 300, Height = 300, Background = Brushes.White };
        ArtworkScope.SetLoader(root, artwork); root.Children.Add(image);
        using (var surface = Mount(root))
        {
            await SearchFixture.Until(() => ((Image)image.Children[0]).Source is not null);
            Capture(root, "tmdb-artwork.png");
        }
        await first.Metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["imageBase"] = images.BaseUrl + "/pictures" });
        using (var cached = await artwork.AcquireAsync(Poster))
            Check(ReferenceEquals(cached.Image, lease.Image) && !api.HasConnection && !images.HasConnection,
                "changing the proxy base does not rename or bypass a cached relative resource");
        var uncached = artwork.AcquireAsync("/v1/tmdb/images/w300/poster.png");
        await using (var exchange = await images.AcceptAsync())
        {
            Request(exchange, "/pictures/w300/poster.png");
            await exchange.ReplyAsync(RuntimeImageTests.Png, contentType: "image/png");
        }
        using var resized = await uncached;
        int oldProcess = first.ProcessId;
        await first.DisposeAsync();
        await using var second = await RuntimeHost.OpenAsync(data);
        current = second.Client;
        Check(second.Metadata.Settings!.ImageBase == images.BaseUrl + "/pictures", "a new real Node process restores the runtime-owned settings");
        using (var cached = await artwork.AcquireAsync(Poster))
            Check(ReferenceEquals(cached.Image, lease.Image), "a new session endpoint cannot alter the stable cache identity");
        var restarted = artwork.AcquireAsync("/v1/tmdb/images/w500/restarted.png");
        await using (var exchange = await images.AcceptAsync())
        {
            Request(exchange, "/pictures/w500/restarted.png");
            await exchange.ReplyAsync(RuntimeImageTests.Png, contentType: "image/png");
        }
        using var restored = await restarted;
        Console.WriteLine($"TMDB_RUNTIME restart=true; oldProcess={oldProcess}; currentProcess={second.ProcessId}; stableCache=true; publicNetwork=false");
    }

    private static async Task Failures()
    {
        await using var upstream = new RuntimeHttpClientTests.Server();
        await using var host = await RuntimeHost.OpenAsync(NewData());
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        { ["apiBase"] = upstream.BaseUrl, ["imageBase"] = upstream.BaseUrl + "/img" });
        await using var search = new FilmSearchService(new SearchEngine(), new SearchSources(),
            _ => Task.FromResult<IReadOnlyList<string>>([]), host.Metadata);
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
            Request(exchange, "/3/movie/7");
            await exchange.ReplyAsync(Json(new { error = "synthetic detail failure" }), 404);
        }
        await SearchFixture.Settle(session);
        var context = new FilmSearchContext(session, session.Items.Single());
        Check(session.MetadataState == FilmSearchChannelState.Completed && context.Query == "目标2" && context.MetadataQuery == "目标" &&
            context.Metadata!.Value.GetProperty("overview").GetString() == "已得搜索简介" &&
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
            Request(exchange, "/img/w500/poster.png");
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
            ["apiKey"] = "synthetic-api-key", ["accessToken"] = "synthetic-access-token"
        });
        string documents = Path.Combine(data, "documents");
        Directory.CreateDirectory(documents);
        await using (var store = new LocalDocumentStore(documents,
            [FilmRuleService.DocumentNamespace, FilmSourceService.DocumentNamespace]))
        {
            var rules = new FilmRuleService(new NativeJavaScriptJsonEngine(FilmRuleService.LoadProgramSource()), store);
            var sources = new FilmSourceService(host.Client, store);
            await using var film = new FilmCapability(rules, sources, host.Metadata);
            var view = (FilmSettingsView)film.SettingsView;
            view.Width = 900; view.Height = 650;
            using var surface = Mount(view);
            var navigation = (StackPanel)view.FindName("SettingsNavigation");
            navigation.Children.OfType<RadioButton>().Single(button => Equals(button.Tag, "tmdb")).IsChecked = true;
            await SearchFixture.Until(() => view.MetadataLoaded && !view.MetadataBusy);
            var key = (PasswordBox)view.FindName("MetadataApiKey");
            var token = (PasswordBox)view.FindName("MetadataAccessToken");
            var apiBase = (TextBox)view.FindName("MetadataApiBase");
            Check(key.Password.Length == 0 && token.Password.Length == 0 && view.ApiKeyState == "已配置" &&
                view.AccessTokenState == "已配置" && AutomationProperties.GetName(key) == "TMDB API key",
                "the actual settings form uses password fields and displays only configured state");
            Capture(view, "tmdb-settings-wide.png");
            view.Width = 420; Layout(view);
            Check(apiBase.ActualWidth > 100 && apiBase.ActualWidth < 420 && key.ActualWidth > 80 &&
                key.ActualWidth < 420, "native fields retain usable widths inside the narrow settings viewport");
            Capture(view, "tmdb-settings-narrow.png");
            await SaveView(view);
            await Credentials(host.Metadata, upstream, expectedToken: "synthetic-access-token");
            key.Password = "   "; token.Password = "   ";
            await SaveView(view);
            Check(host.Metadata.Settings is { ApiKeyConfigured: true, AccessTokenConfigured: true } &&
                view.ApiKeyState == "已配置" && view.AccessTokenState == "已配置" &&
                key.Password.Length == 0 && token.Password.Length == 0,
                "whitespace-only drafts preserve configured credentials and clear only transient password fields");
            await Credentials(host.Metadata, upstream, expectedToken: "synthetic-access-token");
            key.Password = "synthetic-replacement-key"; token.Password = "synthetic-replacement-token";
            await SaveView(view);
            Check(key.Password.Length == 0 && token.Password.Length == 0, "success clears transient secret drafts without echo");
            await Credentials(host.Metadata, upstream, expectedToken: "synthetic-replacement-token");
            Clear(view, "accessToken"); await SaveView(view);
            Check(view.AccessTokenState == "未配置" && view.ApiKeyState == "已配置", "the explicit token clear does not clear the API key");
            await Credentials(host.Metadata, upstream, expectedKey: "synthetic-replacement-key");
            Clear(view, "apiKey"); await SaveView(view);
            await Credentials(host.Metadata, upstream);
            string savedBase = host.Metadata.Settings!.ApiBase;
            apiBase.Text = "https://invalid.example/?query=not-a-base"; key.Password = "synthetic-retry-key";
            await SaveView(view);
            Check(apiBase.Text.Contains("?query=", StringComparison.Ordinal) && key.Password == "synthetic-retry-key" &&
                host.Metadata.Settings!.ApiBase == savedBase && !host.Metadata.Settings!.ApiKeyConfigured,
                "failed validation retains drafts and cannot publish unsaved settings");
            apiBase.Text = savedBase; await SaveView(view);
            Check(host.Metadata.Settings!.ApiKeyConfigured && key.Password.Length == 0, "the unchanged failure draft can be retried explicitly");
            surface.RootVisual = null;
        }
        await host.DisposeAsync();
        await using var restored = await RuntimeHost.OpenAsync(data);
        Check(restored.Metadata.Settings is { ApiKeyConfigured: true, AccessTokenConfigured: false },
            "runtime-owned configured and cleared credentials restore in a new process");
        await Credentials(restored.Metadata, upstream, expectedKey: "synthetic-retry-key");
    }

    private static async Task Credentials(FilmMetadataService metadata, RuntimeHttpClientTests.Server upstream,
        string? expectedToken = null, string? expectedKey = null)
    {
        var search = metadata.SearchAsync("配置验证", default);
        await using var exchange = await upstream.AcceptAsync();
        var query = Query(exchange);
        Check(expectedToken is null ? !exchange.Headers.ContainsKey("Authorization") :
                exchange.Headers.GetValueOrDefault("Authorization") == "Bearer " + expectedToken,
            "only the configured upstream token is used; credentials are not returned by get");
        Check(expectedKey is null ? !query.ContainsKey("api_key") : query.GetValueOrDefault("api_key") == expectedKey,
            "API key is preserved, replaced or explicitly cleared according to the actual settings action");
        await exchange.ReplyAsync(Json(new { results = Array.Empty<object>() }));
        await search;
    }
    private static async Task SaveView(FilmSettingsView view)
    {
        ((Button)view.FindName("MetadataSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SearchFixture.Until(() => !view.MetadataBusy);
    }
    private static void Clear(FilmSettingsView view, string key) =>
        Visuals(view).OfType<Button>().Single(button => Equals(button.Tag, key))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Cancellation()
    {
        await using var upstream = new RuntimeHttpClientTests.Server();
        await using var host = await RuntimeHost.OpenAsync(NewData());
        await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
        { ["apiBase"] = upstream.BaseUrl, ["imageBase"] = upstream.BaseUrl + "/img" });
        using var cancel = new CancellationTokenSource();
        var query = host.Metadata.SearchAsync("挂起", cancel.Token);
        await using var pending = await upstream.AcceptAsync();
        cancel.Cancel();
        try { await query; throw new InvalidOperationException("Expected cancellation"); }
        catch (OperationCanceledException) { }
        await pending.AssertDisconnectedAsync();
        var artwork = new ArtworkService(readTrusted: host.Client.ReadImageAsync);
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
        Check(!upstream.HasConnection, "real cancellation and close do not replay any upstream operation");
    }

    private sealed class RuntimeHost : IAsyncDisposable
    {
        internal readonly AppRuntimeClient Client;
        internal readonly FilmMetadataService Metadata;
        private NodeRuntime? session;
        internal int ProcessId => session!.ProcessId;
        private Task? disposal;
        private RuntimeHost(string root, RuntimeBundle bundle)
        {
            Client = new(async token => session = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle,
                AppIdentity.ScriptModules, Path.Combine(root, "script-runtime"), token));
            Metadata = new(Client); Client.Start();
        }
        internal static async Task<RuntimeHost> OpenAsync(string root)
        {
            string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
                ?? throw new InvalidOperationException("A real four-module Windows runtime bundle is required.");
            var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), AppIdentity.RuntimeId, AppIdentity.ScriptModules);
            var host = new RuntimeHost(root, bundle);
            try { await host.Metadata.GetSettingsAsync(); return host; }
            catch { await host.DisposeAsync(); throw; }
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

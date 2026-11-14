using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

/// <summary>Real Node data and playback consumers, real WPF controls, and the App artwork owner; synthetic HTTP only.</summary>
public static class FilmMetadataPlaybackIntegrationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("TMDB complete detail hands off once and real controls preserve season preview and recommendation identities", Tmdb);
        yield return Case("Douban relative artwork allows external reads and retries through the real view owner without invented episodes", Douban);
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("film-metadata-playback-integration: " + name, () => MusicQualityConsumerTests.Sta(run));
    private static void Check(bool value, string message) => SearchFixture.Check(value, message);

    private static async Task Tmdb()
    {
        await using var upstream = new MetadataUpstream();
        await using var host = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(NewData());
        await upstream.ConfigureAsync(host);
        var read = await ((IFilmSearchMetadata)host.Metadata).DetailAsync("tv", 7, default);
        CompleteTmdb(read.Value, 7);
        Check(read.Owner is not null && read.CacheEntry is { ValueKind: JsonValueKind.Object },
            "the production data consumer supplies its real producer and original Node cache receipt");

        var direct = new RejectPublicImages();
        var trusted = new ConcurrentQueue<string>();
        await using var artwork = new ArtworkService(direct, (reference, budget, token) =>
        {
            trusted.Enqueue(reference);
            return ((IRuntimeImageClient)host.Client).ReadImageAsync(reference, budget, token);
        });
        var native = new FilmNativeProbe();
        var session = new FilmPlaybackSession(host.Client.CaptureConnectionAsync, native);
        await using var module = new FilmCapability(new NoHomeReads(), session, new FilmLibrary(new FilmMemoryStore()));
        var target = new FilmPlaybackTarget("tmdb:tv:7", "tv", MetadataUpstream.TmdbTitle, "tmdb", "7");
        var request = Handoff(target, read);
        await module.OpenPlaybackAsync(new(target.Id, target.Title, "", ""), request);
        var page = module.Playback!;
        var root = PlaybackWithPosterCard(module, out var poster);
        ArtworkScope.SetLoader(root, artwork);
        using var surface = Mount(root);
        var view = (FilmPlaybackView)((FilmView)module.View).FindName("PlaybackView");
        await Until(upstream, () => page.Episodes.Count == 1 && page.Episodes[0].Title == "已播首集" &&
            page.Season == 1 && upstream.Count("/douban/movie/subject_search") == 1);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CompleteTmdb(page.CaptureRequest.Metadata!.Value, 7);
        Check(ReferenceEquals(view.DataContext, page) && page.Cast == "完整演员" && page.Directors == "完整导演" &&
            ((TextBlock)view.FindName("CastNames")).Text == "完整演员" &&
            Visuals(view).OfType<TextBlock>().Any(text => text.Text == "完整导演"),
            "append-loaded cast and director reach the actual playback page and visible text, not a search-only projection");
        using var presentation = await poster.PrepareForPresentationAsync();
        Check(poster.Source is { IsFrozen: true, PixelWidth: > 0 } &&
            trusted.Contains("/v1/tmdb/images/original/poster.png") && direct.Reads == 0,
            "a normal poster card bound to the real playback metadata decodes the original reference through the App's trusted artwork owner");
        CompleteTmdb(await host.Metadata.DetailAsync("tv", 7, default), 7);
        Check(upstream.Count("/tmdb/3/tv/7") == 1 && upstream.Count("/tmdb/3/tv/7/season/1") == 1 &&
            upstream.Count("/tmdb/3/tv/7/season/2") == 0 && upstream.Count("/tmdb/3/tv/7/season/0") == 0,
            "the original complete receipt, playback initialization and a repeated consumer read share one detail and only the selected season");
        DetailRequest(upstream.Single("/tmdb/3/tv/7"));

        Click(SeasonButton(view, 2));
        await Until(upstream, () => page.Season == 2 && !page.ListLoading && page.Episodes.Count == 1 &&
            page.Episodes[0].Season == 2 && page.Episodes[0].Number == 1 && page.Episodes[0].Title == "第二季已播");
        Check(upstream.Count("/tmdb/3/tv/7/season/2") == 1 && upstream.Count("/tmdb/3/tv/7/season/0") == 0 &&
            upstream.Count("/tmdb/3/tv/7/season/1") == 1 && page.ActiveTarget.Season == 1 && page.ActiveTarget.Episode == 1,
            "the real season button fetches only season two and browsing never retargets the accepted episode");
        var preview = Visuals(view).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "显示点映剧集");
        int beforePreview = upstream.MetadataRequests;
        Toggle(preview, true);
        await Until(upstream, () => page.PreviewEpisodes && page.Episodes.Select(item => item.Number).SequenceEqual(new int?[] { 1, 4 }));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var future = Visuals((Shelf)view.FindName("EpisodeShelf")).OfType<Button>()
            .Single(button => button.Tag is FilmEpisodeItem { Season: 2, Number: 4 });
        Check(future.IsEnabled && page.Episodes.All(item => item.CanSelect) &&
            page.Episodes.All(item => item.Number is not (2 or 3)),
            "the actual preview checkbox exposes the existing future episode four, never fills the missing two or three");
        Toggle(preview, false);
        await Until(upstream, () => !page.PreviewEpisodes && page.Episodes.Count == 1 && page.Episodes[0].Number == 1);
        Check(upstream.MetadataRequests == beforePreview && native.Intents.Count == 0,
            "preview only changes the existing display/selectable range; it neither re-fetches metadata nor prepares media");

        Click(SeasonButton(view, 0));
        await Until(upstream, () => page.Season == 0 && !page.ListLoading && page.Episodes.Count == 1 &&
            page.Episodes[0].Season == 0 && page.Episodes[0].Number == 0);
        var special = page.Episodes.Single();
        var specialMetadata = Season(page, 0).GetProperty("episodes").EnumerateArray().Single();
        Check(special.GlobalEpisode is null && special.CanSelect &&
            specialMetadata.GetProperty("air_date").ValueKind == JsonValueKind.Null &&
            specialMetadata.GetProperty("episode_number").GetInt32() == 0 &&
            upstream.Count("/tmdb/3/tv/7/season/0") == 1,
            "the actual special remains season zero/episode zero with its unknown date and no invented global episode or broadcast date");
        Check(Season(page, 2).GetProperty("episodes").EnumerateArray().Select(row => row.GetProperty("episode_number").GetInt32())
            .SequenceEqual(new[] { 1, 4 }), "season browsing retains only the actual upstream episode rows");

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var recommendation = page.Recommendations.Single();
        Check(recommendation.Title == page.Title && recommendation.PlaybackContext?.Target is
            { MetadataProvider: "tmdb", MetadataId: "8", Season: null, Episode: null },
            "a same-title recommendation still has its own exact typed provider/id and no inherited season selection");
        var related = (ItemsControl)view.FindName("RelatedItems");
        Click(Visuals(related).OfType<Button>().Single(button => ReferenceEquals(button.Tag, recommendation)));
        await Until(upstream, () => module.Playback is { ActiveTarget.MetadataProvider: "tmdb", ActiveTarget.MetadataId: "8" } next &&
            !ReferenceEquals(next, page) && next.CaptureRequest.Metadata is { ValueKind: JsonValueKind.Object } detail &&
            detail.GetProperty("id").GetInt32() == 8 && ReferenceEquals(view.DataContext, next));
        var selected = module.Playback!;
        CompleteTmdb(selected.CaptureRequest.Metadata!.Value, 8);
        Check(selected.ActiveTarget.Id == "tmdb:tv:8" && selected.ActiveTarget.Title == MetadataUpstream.TmdbTitle &&
            selected.ActiveTarget.Season is null && selected.ActiveTarget.Episode is null &&
            selected.Sources.All(value => value.MetadataSourceId is not null) && upstream.Count("/tmdb/3/tv/8") == 1 &&
            upstream.Count("/tmdb/3/tv/7") == 1 && native.Intents.Count == 0,
            "the actual recommendation button goes through FilmView and Capability to a new exact Node target, without old episode or source authority");
        DetailRequest(upstream.Single("/tmdb/3/tv/8"));
        Check(!upstream.Requests.Any(item => item.Path.StartsWith("/tmdb/3/search/", StringComparison.Ordinal) ||
            item.Path.Contains("/person/", StringComparison.Ordinal) || item.Path.EndsWith("/credits", StringComparison.Ordinal) ||
            item.Path.EndsWith("/similar", StringComparison.Ordinal) || item.Path.EndsWith("/images", StringComparison.Ordinal)) &&
            direct.Reads == 0, "same-title navigation never re-searches TMDB or invents separate actor/append requests or an image downloader");
        surface.RootVisual = null;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        upstream.AssertHealthy();
        upstream.PrintCounts("TMDB");
    }

    private static async Task Douban()
    {
        await using var upstream = new MetadataUpstream();
        await using var host = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(NewData());
        await upstream.ConfigureAsync(host);
        var input = new { provider = "douban", path = "/rexxar/api/v2/tv/77", query = new { } };
        var identity = new { kind = "metadata", input.provider, input.path, input.query };
        var read = await FilmSearchDataClient.ReadAsync(host.Client, "metadata", input, identity, default);
        string reference = read.Value.GetProperty("pic").GetProperty("normal").GetString()!;
        Check(reference == "/v1/douban/images/poster.png" && read.Owner is not null &&
            read.CacheEntry is { ValueKind: JsonValueKind.Object } &&
            read.Value.GetProperty("raw").GetProperty("original").GetString() == "  豆瓣原资料  ",
            "the public production data consumer keeps the real complete Douban receipt and relative image reference");
        var connection = await host.Client.CaptureConnectionAsync();
        upstream.AllowDoubanImages = true;
        try
        {
            using var anonymous = new HttpClient(new SocketsHttpHandler
                { UseProxy = false, UseCookies = false, AllowAutoRedirect = false });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.BusinessOrigin, reference));
            request.Headers.Host = "external.example";
            request.Headers.Add("Origin", "https://external.example");
            using var response = await anonymous.SendAsync(request, deadline.Token);
            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            Check(response.StatusCode == HttpStatusCode.OK &&
                response.Content.Headers.ContentType?.MediaType == "image/png" &&
                bytes.SequenceEqual(RuntimeImageTests.Png) &&
                response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins) && origins.SequenceEqual(["*"]) &&
                upstream.DoubanImageRequests == 1 && upstream.SuccessfulDoubanImages == 1,
                "the real runtime image route serves the exact PNG with open CORS to an external Host/Origin without credentials");
        }
        finally
        {
            upstream.AllowDoubanImages = false;
        }

        var trusted = new ConcurrentQueue<string>();
        var direct = new RejectPublicImages();
        await using var artwork = new ArtworkService(direct, (address, budget, token) =>
        {
            trusted.Enqueue(address);
            return ((IRuntimeImageClient)host.Client).ReadImageAsync(address, budget, token);
        });
        var native = new FilmNativeProbe();
        var session = new FilmPlaybackSession(host.Client.CaptureConnectionAsync, native);
        await using var module = new FilmCapability(new NoHomeReads(), session, new FilmLibrary(new FilmMemoryStore()));
        var target = new FilmPlaybackTarget("douban:tv:77", "tv", MetadataUpstream.DoubanTitle, "douban", "77");
        await module.OpenPlaybackAsync(new(target.Id, target.Title, "", ""), Handoff(target, read));
        var page = module.Playback!;
        var root = PlaybackWithPosterCard(module, out var poster);
        ArtworkScope.SetLoader(root, artwork);
        using var surface = Mount(root);
        var view = (FilmPlaybackView)((FilmView)module.View).FindName("PlaybackView");
        using var failedPresentation = await poster.PrepareForPresentationAsync();
        await Until(upstream, () => poster.Source is null && AutomationProperties.GetHelpText(poster) == "图片加载失败" &&
            upstream.DoubanImageRequests > 1 && upstream.SuccessfulDoubanImages == 1 &&
            upstream.Count("/tmdb/3/search/multi") == 1);
        Check(page.Title == MetadataUpstream.DoubanTitle && page.Overview == "来自真实公开资料接口的简介" &&
            page.Cast == "豆瓣演员" && page.Directors == "豆瓣导演" &&
            ((TextBlock)view.FindName("CastNames")).Text == "豆瓣演员" && page.Poster == reference &&
            !poster.ShowRetry && poster.ToolTip as string == "图片加载失败",
            "the actual Douban playback view displays provider facts while its normal poster card exposes safe image failure without an image retry button");
        Check(page.ActiveTarget.Kind == "group" && page.EntryTarget.MetadataProvider == "douban" &&
            page.EntryResolution == "unmatched" && page.RequiresSourceSearch &&
            page.ActiveTarget.Members!.Value.GetArrayLength() == 0 && page.Sources.Count == 0 &&
            page.ActiveTarget.Season is null && page.ActiveTarget.Episode is null && page.Episodes.Count == 0 &&
            page.Seasons.Count == 0 && upstream.Count("/tmdb/3/search/multi") == 1,
            "the Douban seed becomes Node's empty unmatched group and retains its display metadata without invented episodes or source searches");
        var preview = Visuals(view).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "显示点映剧集");
        int beforePreview = upstream.MetadataRequests;
        Toggle(preview, true);
        await Until(upstream, () => page.PreviewEpisodes);
        Check(page.Episodes.Count == 0 && page.ActiveTarget.Season is null && page.ActiveTarget.Episode is null &&
            upstream.MetadataRequests == beforePreview && native.Intents.Count == 0,
            "the real preview checkbox cannot turn absent Douban episode data into a playable coordinate or a guessed request");

        int failures = upstream.DoubanImageRequests;
        surface.RootVisual = null;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(poster.Source is null, "unmounting the real view and normal card retires its failed artwork presentation");
        upstream.AllowDoubanImages = true;
        surface.RootVisual = root;
        Layout(root);
        using var recoveredPresentation = await poster.PrepareForPresentationAsync();
        await Until(upstream, () => poster.Source is { IsFrozen: true, PixelWidth: > 0 });
        Check(AutomationProperties.GetHelpText(poster).Length == 0 && poster.ToolTip is null &&
            trusted.All(address => address == reference) && trusted.Count >= 2 &&
            upstream.DoubanImageRequests > failures && upstream.SuccessfulDoubanImages == 2 && direct.Reads == 0 &&
            upstream.Count("/douban/rexxar/api/v2/tv/77") == 1 && ReferenceEquals(view.DataContext, page),
            "remounting the same real view and normal card retries the failed relative image through the original App artwork owner, clears failure and shares decoded pixels without reloading detail");
        var imageReads = upstream.DoubanImageRequests;
        using (var borrowed = await artwork.AcquireAsync(reference))
            Check(ReferenceEquals(borrowed.Image, poster.Source), "the real image consumer borrows the same decoded artwork, not a second image cache");
        Check(upstream.DoubanImageRequests == imageReads && upstream.Requests.All(item =>
            !item.Path.Contains("/season/", StringComparison.Ordinal) && !item.Path.EndsWith("/episodes", StringComparison.Ordinal) &&
            !item.Path.Contains("/celebrity/", StringComparison.Ordinal)),
            "successful image reuse and an incomplete episode list do not invent new downloads, seasons, episodes or actor navigation");
        var detail = upstream.Single("/douban/rexxar/api/v2/tv/77");
        Check(detail.Query.Count == 0 && detail.Headers.GetValueOrDefault("Cookie") == MetadataUpstream.DoubanCookie &&
            page.CaptureRequest.Metadata!.Value.GetProperty("raw").GetProperty("original").GetString() == "  豆瓣原资料  ",
            "only the real metadata API uses its configured synthetic cookie; the original raw record survives playback");
        surface.RootVisual = null;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        upstream.AssertHealthy();
        upstream.PrintCounts("Douban");
    }

    private static FilmPlaybackRequest Handoff(FilmPlaybackTarget target, FilmSearchData read) =>
        new(target, Sources: [], Metadata: read.Value,
            CacheEntries: FilmJson.Encode(new[] { read.CacheEntry!.Value })) { DataOwner = read.Owner };
    private static JsonElement Season(FilmPlaybackPage page, int number) =>
        page.CaptureRequest.Metadata!.Value.GetProperty("seasons").EnumerateArray()
            .Single(row => row.GetProperty("season_number").GetInt32() == number);
    private static void CompleteTmdb(JsonElement value, int id)
    {
        const string images = "/v1/tmdb/images/original/";
        Check(value.GetProperty("id").GetInt32() == id && value.GetProperty("genres")[0].GetProperty("name").GetString() == "剧情" &&
            value.GetProperty("raw").GetProperty("original").GetString() == "  完整原字段  " &&
            value.GetProperty("raw").GetProperty("unknown").ValueKind == JsonValueKind.Null &&
            value.GetProperty("poster_path").GetString() == images + "poster.png" &&
            value.GetProperty("credits").GetProperty("cast")[0].GetProperty("profile_path").GetString() == images + "cast.png" &&
            value.GetProperty("credits").GetProperty("crew")[0].GetProperty("name").GetString() == "完整导演" &&
            value.GetProperty("similar").GetProperty("results")[0].GetProperty("id").GetInt32() == 8 &&
            value.GetProperty("similar").GetProperty("results")[0].GetProperty("poster_path").GetString() == images + "similar.png" &&
            value.GetProperty("images").GetProperty("posters")[0].GetProperty("file_path").GetString() == images + "poster.png",
            "the exact complete detail keeps credits, similar, images, original-size references and all raw fields");
    }
    private static void DetailRequest(ObservedRequest request) =>
        Check(request.Method == "GET" && request.Query.Count == 2 && request.Query["language"] == "zh-CN" &&
            request.Query["append_to_response"] == "credits,similar,images", "full detail has the exact production append and language key");
    private static Button SeasonButton(FilmPlaybackView view, int number) =>
        Visuals(view).OfType<Button>().Single(button => button.Tag is FilmSelection choice && choice.Season == number);
    private static Grid PlaybackWithPosterCard(FilmCapability module, out ArtworkImage image)
    {
        // Keep the real metadata consumer and a normal poster card, without restoring a page backdrop
        // or inventing episodes/recommendations just to host an image for incomplete metadata.
        var poster = new FilmPlaybackPoster
            { Width = 156, Height = 234, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        poster.SetBinding(FilmPlaybackPoster.AddressProperty, new Binding("Playback.Poster") { Source = module });
        module.View.Width = 1180; module.View.Height = 1300;
        var root = new Grid { Width = 1360, Height = 1300 };
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1180) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(180) });
        root.Children.Add(module.View);
        Grid.SetColumn(poster, 1);
        root.Children.Add(poster);
        image = Visuals(poster).OfType<ArtworkImage>().Single();
        return root;
    }
    private static void Click(Button button)
    {
        Check(button.IsEnabled, "the actual requested button is enabled");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static void Toggle(CheckBox box, bool enabled)
    {
        Check(box.IsEnabled, "the actual preview checkbox is enabled");
        box.SetCurrentValue(ToggleButton.IsCheckedProperty, enabled);
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static async Task Until(MetadataUpstream upstream, Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            upstream.AssertHealthy();
            if (condition()) return;
            await Task.Delay(10, deadline.Token);
        }
    }
    private static string NewData()
    {
        string root = TestWorkspace.DataPath("film-metadata-playback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject value)
    {
        yield return value;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(value); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(value, index))) yield return child;
    }
    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(view.Width, view.Height));
        view.Arrange(new Rect(0, 0, view.Width, view.Height));
        view.UpdateLayout();
    }
    private static HwndSource Mount(FrameworkElement view)
    {
        view.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
        var surface = new HwndSource(new HwndSourceParameters("film-metadata-playback-offscreen")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = (int)view.Width, Height = (int)view.Height
        });
        surface.RootVisual = view;
        Layout(view);
        return surface;
    }

    private sealed class NoHomeReads : IFilmHomeSource
    {
        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(FilmSectionDefinition section, int start, int limit,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Playback cannot turn a metadata identity into a home/title search.");
        public void Dispose() { }
    }
    private sealed class RejectPublicImages : HttpMessageHandler
    {
        internal int Reads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            throw new InvalidOperationException("Runtime-relative playback artwork must use the original trusted reader.");
        }
    }
    private sealed record ObservedRequest(string Method, string Path,
        Dictionary<string, string> Query, Dictionary<string, string> Headers);

    private sealed class MetadataUpstream : IAsyncDisposable
    {
        internal const string TmdbTitle = "同名但身份不同的作品";
        internal const string DoubanTitle = "没有季集事实的豆瓣作品";
        internal const string DoubanCookie = "g02_synthetic=metadata-only";
        private readonly RuntimeHttpClientTests.Server server = new();
        private readonly ConcurrentQueue<ObservedRequest> requests = new();
        private readonly ConcurrentQueue<string> failures = new();
        private readonly Task serving;
        private volatile bool closed;
        internal volatile bool AllowDoubanImages;
        private int successfulDoubanImages;
        internal ObservedRequest[] Requests => requests.ToArray();
        internal int MetadataRequests => requests.Count(item => item.Path.StartsWith("/tmdb/", StringComparison.Ordinal) ||
            item.Path.StartsWith("/douban/", StringComparison.Ordinal));
        internal int DoubanImageRequests => Count("/douban-images/poster.png");
        internal int SuccessfulDoubanImages => Volatile.Read(ref successfulDoubanImages);
        internal int Count(string path) => requests.Count(item => item.Path == path);
        internal ObservedRequest Single(string path) => requests.Single(item => item.Path == path);

        internal MetadataUpstream() => serving = ServeAsync();
        internal async Task ConfigureAsync(FilmMetadataRuntimeTests.RuntimeHost host)
        {
            await host.Metadata.SaveSettingsAsync(new Dictionary<string, object?>
            { ["apiBase"] = server.BaseUrl + "/tmdb/3", ["imageBase"] = server.BaseUrl + "/tmdb-images/t/p" });
            var reply = await host.Client.CallAsync("douban.data.settings.update", new
            {
                apiBase = server.BaseUrl + "/douban", searchBase = server.BaseUrl + "/douban",
                imageBase = server.BaseUrl + "/douban-images", cookie = DoubanCookie
            });
            Check(reply.GetProperty("ok").GetBoolean() && reply.GetProperty("contract").GetString() == "douban.data.v1",
                "all real metadata and image traffic is configured to this one synthetic loopback upstream");
        }
        private async Task ServeAsync()
        {
            while (!closed)
            {
                RuntimeHttpClientTests.Exchange exchange;
                try { exchange = await server.AcceptAsync().ConfigureAwait(false); }
                // AcceptAsync's own five-second idle deadline is not a business failure.
                catch (OperationCanceledException) when (!closed) { continue; }
                catch (Exception error) when (closed && error is
                    (SocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)) { return; }
                await using (exchange)
                {
                    var url = new Uri(server.BaseUrl + exchange.Path);
                    var query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(part => part.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                            pair => Uri.UnescapeDataString(pair.Length == 2 ? pair[1].Replace("+", " ", StringComparison.Ordinal) : ""));
                    var observed = new ObservedRequest(exchange.Method, url.AbsolutePath, query, exchange.Headers);
                    requests.Enqueue(observed);
                    if (exchange.Method != "GET" || exchange.Headers.ContainsKey("X-Meow-Session") ||
                        exchange.Headers.ContainsKey("X-Meow-Request-Id") || exchange.Headers.ContainsKey("Authorization"))
                        failures.Enqueue("Unexpected method or leaked runtime authentication at " + observed.Path);
                    await RespondAsync(exchange, observed.Path).ConfigureAwait(false);
                }
            }
        }
        private async Task RespondAsync(RuntimeHttpClientTests.Exchange exchange, string path)
        {
            object? value = path switch
            {
                "/tmdb/3/tv/7" => TmdbDetail(7),
                "/tmdb/3/tv/8" => TmdbDetail(8),
                "/tmdb/3/tv/7/season/1" => new { season_number = 1, episode_count = 1,
                    episodes = new[] { Episode(1, 1, "已播首集", "2000-01-01") } },
                "/tmdb/3/tv/7/season/2" => new { season_number = 2, episode_count = 4,
                    episodes = new[] { Episode(2, 1, "第二季已播", "2000-01-01"), Episode(2, 4, "第二季未来", "2999-01-01") } },
                "/tmdb/3/tv/7/season/0" => new { season_number = 0, episode_count = 1,
                    episodes = new[] { Episode(0, 0, "未知日期特别篇", null) } },
                "/tmdb/3/search/multi" => new { results = Array.Empty<object>(), page = 1, total_pages = 0 },
                "/douban/rexxar/api/v2/tv/77" => new
                {
                    id = 77, title = DoubanTitle, intro = "来自真实公开资料接口的简介",
                    genres = new[] { "剧情" }, directors = new[] { new { name = "豆瓣导演" } },
                    actors = new[] { new { name = "豆瓣演员" } },
                    pic = new { normal = server.BaseUrl + "/douban-images/poster.png" },
                    raw = new { original = "  豆瓣原资料  " }
                },
                _ => null
            };
            if (value is not null)
            {
                await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(value)).ConfigureAwait(false);
                return;
            }
            if (path == "/douban/movie/subject_search")
            {
                var body = "<html><script>window.__DATA__ = " +
                    JsonSerializer.Serialize(new { total = 0, start = 0, count = 0, items = Array.Empty<object>() }) +
                    "; window.__USER__ = {};</script></html>";
                await exchange.ReplyAsync(Encoding.UTF8.GetBytes(body), contentType: "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }
            if (path is "/tmdb-images/t/p/original/poster.png" or "/tmdb-images/t/p/original/similar.png" or
                "/tmdb-images/t/p/original/episode.png" or "/douban-images/poster.png")
            {
                if (exchange.Headers.ContainsKey("Cookie") || new Uri(server.BaseUrl + exchange.Path).Query.Length != 0)
                    failures.Enqueue("Metadata credentials or unexpected query reached image upstream: " + path);
                if (path == "/douban-images/poster.png")
                {
                    if (exchange.Headers.GetValueOrDefault("Referer") != "https://movie.douban.com/")
                        failures.Enqueue("Douban image lost its protocol Referer.");
                    if (!AllowDoubanImages)
                    {
                        await exchange.ReplyAsync("{}"u8.ToArray(), status: 503).ConfigureAwait(false);
                        return;
                    }
                    Interlocked.Increment(ref successfulDoubanImages);
                }
                await exchange.ReplyAsync(RuntimeImageTests.Png, contentType: "image/png").ConfigureAwait(false);
                return;
            }
            failures.Enqueue("Unexpected metadata/image request: " + path);
            await exchange.ReplyAsync("{}"u8.ToArray(), status: 404).ConfigureAwait(false);
        }
        private static object TmdbDetail(int id) => new
        {
            id, name = TmdbTitle, overview = "完整主资料", poster_path = "/poster.png",
            genres = new[] { new { id = 18, name = "剧情" } },
            credits = new { cast = new[] { new { id = 19, name = "完整演员", profile_path = "/cast.png" } },
                crew = new[] { new { id = 20, name = "完整导演", job = "Director" } } },
            similar = new { results = new[] { new { id = 8, name = TmdbTitle, poster_path = "/similar.png", vote_average = 8.2 } } },
            images = new { posters = new[] { new { file_path = "/poster.png", width = 600, height = 900 } } },
            raw = new { original = "  完整原字段  ", unknown = (string?)null },
            seasons = id == 7 ? new[]
            {
                new { season_number = 0, name = "特别篇", episode_count = 1 },
                new { season_number = 1, name = "第一季", episode_count = 1 },
                new { season_number = 2, name = "第二季", episode_count = 4 }
            } : []
        };
        private static object Episode(int season, int episode, string name, string? airDate) => new
        {
            season_number = season, episode_number = episode, name, air_date = airDate,
            still_path = "/episode.png", overview = name + "的真实简介", runtime = 45
        };
        internal void AssertHealthy()
        {
            if (serving.IsFaulted) serving.GetAwaiter().GetResult();
            Check(failures.IsEmpty, string.Join("; ", failures));
        }
        internal void PrintCounts(string label)
        {
            foreach (var group in requests.GroupBy(item => item.Path).OrderBy(group => group.Key, StringComparer.Ordinal))
                Console.WriteLine($"G02_REQUEST_COUNT {label} {group.Key} {group.Count()}");
        }
        public async ValueTask DisposeAsync()
        {
            closed = true;
            await server.DisposeAsync();
            await serving;
            AssertHealthy();
        }
    }
}

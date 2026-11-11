using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Platform.Runtime;

namespace MeowFilm.Windows.Tests;

public static partial class NovelTests
{
    private static async Task Fixtures()
    {
        using var data = typeof(NovelTests).Assembly.GetManifestResourceStream("NovelFixtures.json") ?? throw new InvalidOperationException("Shared fixtures missing");
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        using var fixtures = await JsonDocument.ParseAsync(data); await using var rules = new NovelRuleProgram(ruleRuntime);
        Check(fixtures.RootElement.GetArrayLength() == 3);
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var input = fixture.GetProperty("input"); var book = input.GetProperty("book");
            var source = Source() with { SourceUrl = input.GetProperty("source").GetProperty("sourceUrl").GetString()! };
            var actual = await rules.TransformAsync(input.GetProperty("title").GetString()!,
                input.GetProperty("blocks").Deserialize<NovelBlock[]>(NovelJson.Options)!,
                new("a", 1, "https://fixture.test/book", book.GetProperty("name").GetString()!, NovelJson.Text(book, "author")), source,
                input.GetProperty("rules").Deserialize<NovelRule[]>(NovelJson.Options)!, input.GetProperty("conversion").GetString()!);
            Check(JsonElement.DeepEquals(actual, fixture.GetProperty("expected")), fixture.GetProperty("name").GetString()!);
            Console.WriteLine("FIXTURE " + fixture.GetProperty("name").GetString());
        }
    }
    private static async Task RuntimeLibrary()
    {
        string bundles = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT") ?? throw new InvalidOperationException("Runtime bundles required");
        RuntimeModule[] modules = RuntimeHttpClientTests.Modules;
        var bundle = RuntimeBundle.Verify(Path.Combine(bundles, "aggregate"), "aggregate", modules);
        string data = TestWorkspace.DataPath("novel-runtime-library"); string id; long revision;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using (var runtime = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, modules, data, deadline.Token))
        {
            var source = new NovelSourceService(runtime);
            var imported = await source.ImportAsync(fileName: "novel.html", text: """
                <script>async function search(keyword) {
                  const bridge = window.flutter_inappwebview;
                  if (keyword === 'write') await bridge.callHandler('cacheSet', 'native-probe', 'persisted');
                  const value = await bridge.callHandler('cacheGet', 'native-probe');
                  return [{name:String(value),bookUrl:'https://books.invalid/1',author:'fixture'}];
                }</script>
                """, token: deadline.Token);
            Check(imported.GetProperty("failures").GetArrayLength() == 0);
            var item = source.Sources.Single(); id = item.InstallationId;
            await source.UpdateAsync(item, new { group = "持久化", order = -4, enabledExplore = false }, deadline.Token);
            item = source.Sources.Single(); revision = item.Revision;
            var result = await source.InvokeAsync(item, "search", new { keyword = "write", page = 1 }, deadline.Token);
            Check(result.GetProperty("books")[0].GetProperty("name").GetString() == "persisted");
            await source.PrepareShutdownAsync();
        }
        await using (var runtime = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, modules, data, deadline.Token))
        {
            var source = new NovelSourceService(runtime); await source.RefreshAsync(deadline.Token);
            var item = source.Sources.Single(); Check(item.InstallationId == id && item.Revision == revision && item.Group == "持久化" && item.Order == -4 && !item.EnabledExplore);
            var result = await source.InvokeAsync(item, "search", new { keyword = "read", page = 1 }, deadline.Token);
            Check(result.GetProperty("books")[0].GetProperty("name").GetString() == "persisted");
            await source.PrepareShutdownAsync();
        }
        Check(File.Exists(Path.Combine(data, "source", "library.json")));
    }
    private static async Task WebHost()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var rules = new NovelRuleProgram(ruleRuntime);
        var owner = new StackPanel(); var host = GeneralAppearanceTests.Window(owner); NovelUi.Theme(host);
        host.Show();
        try
        {
            await using var server = new WebFixture();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var operation = NovelWebInteraction.ShowAsync(owner, rules, Json(new { mode = "rule", request = new
            { url = server.BaseUrl + "/dynamic", cookies = new[] { new NovelWebCookie("seed", "correct", "127.0.0.1", true, "/", false, false, "Lax", null) },
                script = "new Promise(resolve => setTimeout(() => resolve(document.cookie), 40))" } }), cancellation.Token);
            // Dynamic modes must complete from load/script, without a click on 完成.
            var result = Json(await operation ?? throw new InvalidOperationException("Web result missing"));
            Check(result.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "cookieChanges", "headers", "url", "value" }));
            Check(result.GetProperty("value").GetString()!.Contains("page=present"));
            Check(result.GetProperty("cookieChanges").EnumerateArray().Any(c => c.GetProperty("setCookie").GetString()!.StartsWith("page=present;")));
            Check(result.GetProperty("url").GetString() == server.BaseUrl + "/dynamic");
            Check(server.Requests.Single(r => r.Path == "/dynamic").Cookie.Contains("seed=correct"));
            var requestResult = Json((await NovelWebInteraction.ShowAsync(owner, rules,
                Json(new { mode = "request", request = new { url = server.BaseUrl + "/request" } }), cancellation.Token))!);
            Check(requestResult.GetProperty("value").GetProperty("data").GetString()!.Contains("dynamic fixture"));
            var html = Json((await NovelWebInteraction.ShowAsync(owner, rules,
                Json(new { mode = "webview", request = new { url = server.BaseUrl + "/provided", html = "<html><body>provided fixture</body></html>",
                    script = "document.body.textContent", delayTime = 10 } }), cancellation.Token))!);
            Check(html.GetProperty("value").GetString() == "provided fixture" && !server.Requests.Any(r => r.Path == "/provided"));
            var selected = Json((await NovelWebInteraction.ShowAsync(owner, rules,
                Json(new { mode = "webview", request = new { url = server.BaseUrl + "/filtered",
                    html = "<html><body>filtered fixture</body></html>", sourceRegex = server.BaseUrl + "/capture",
                    script = "fetch('/capture').then(response => response.text())" } }), cancellation.Token))!);
            Check(selected.GetProperty("value").GetString() == server.BaseUrl + "/capture");
            var browser = NovelWebInteraction.ShowAsync(owner, rules, Json(new { mode = "browser", request = new { url = server.BaseUrl + "/redirect" } }), cancellation.Token);
            await Until(() => server.Requests.Any(r => r.Path == "/other"));
            Check(!browser.IsCompleted); ClickPopup("完成");
            var response = BridgeCodec.Value((await browser)!).GetProperty("value");
            Check(response.GetProperty("url").GetString()!.Contains("localhost") && response.GetProperty("statusCode").GetInt32() == 200 &&
                response.GetProperty("data").GetString()!.Contains("dynamic fixture"));
            // A different source interaction gets a fresh browser storage partition.
            var next = NovelWebInteraction.ShowAsync(owner, rules, Json(new { mode = "verificationCode", request = new { url = server.BaseUrl + "/code" } }), cancellation.Token);
            await Until(() => server.Requests.Any(r => r.Path == "/code"));
            await Until(() => PopupItems<TextBox>().Any(t => AutomationProperties.GetName(t) == "验证码"));
            Check(!next.IsCompleted && server.Requests.Single(r => r.Path == "/code").Cookie.Length == 0);
            PopupItems<TextBox>().Single(t => AutomationProperties.GetName(t) == "验证码").Text = "synthetic-code";
            ClickPopup("完成"); var code = Json((await next)!); Check(code.GetProperty("value").GetString() == "synthetic-code");
            var login = NovelWebInteraction.ShowAsync(owner, rules, Json(new { mode = "login", request = new { url = server.BaseUrl + "/login" } }), cancellation.Token);
            await Until(() => server.Requests.Any(r => r.Path == "/login"));
            Check(!login.IsCompleted); ClickPopup("完成"); await login;
            using var retire = new CancellationTokenSource();
            var closing = NovelWebInteraction.ShowAsync(owner, rules, Json(new { mode = "request", request = new { url = server.BaseUrl + "/hold" } }), retire.Token);
            await Until(() => server.Requests.Any(r => r.Path == "/hold"));
            Check(!closing.IsCompleted); retire.Cancel();
            try { await closing; throw new InvalidOperationException("Cancelled Web result returned"); } catch (OperationCanceledException) { }
            Check(!PopupItems<Microsoft.Web.WebView2.Wpf.WebView2>().Any());
        }
        finally { host.Close(); }
    }
    private static async Task WebRetirement()
    {
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var rules = new NovelRuleProgram(ruleRuntime);
        var owner = new StackPanel(); var host = GeneralAppearanceTests.Window(owner); NovelUi.Theme(host); host.Show();
        var runtime = new Runtime(); var sources = new NovelSourceService(runtime); await sources.RefreshAsync();
        await using var server = new WebFixture();
        bool physicallyClosed = false; bool entered = false;
        sources.InteractAsync = async (pending, token) =>
        {
            entered = true;
            try { return await NovelWebInteraction.ShowAsync(owner, rules, pending, token); }
            finally { physicallyClosed = true; }
        };
        runtime.Read = async (_, _, _, token) =>
        {
            runtime.PendingWeb = [Json(new { id = "retire", installationId = "a", mode = "request", request = new { url = server.BaseUrl + "/hold" } })];
            await Task.Delay(Timeout.Infinite, token); return Json(new { });
        };
        var pending = sources.InvokeAsync(sources.Sources[0], "content", new { });
        try
        {
            await Until(() => entered && server.Requests.Any(r => r.Path == "/hold"));
            await sources.UpdateAsync(sources.Sources[0], new { enabled = false });
            try { await pending; throw new InvalidOperationException("Retired browser result committed"); } catch (OperationCanceledException) { }
            Check(physicallyClosed && !PopupItems<Microsoft.Web.WebView2.Wpf.WebView2>().Any());
            Check(runtime.Calls.Where(c => c.Method == "source.runtime.web.respond").All(c =>
                c.Payload.TryGetProperty("cancelled", out var cancelled) && cancelled.GetBoolean() && !c.Payload.TryGetProperty("result", out _)));
        }
        finally { await sources.PrepareShutdownAsync(); host.Close(); }
    }
    private static async Task ConcurrentWebCancellation()
    {
        var runtime = new Runtime { Records = [Source("a"), Source("b")] };
        var sources = new NovelSourceService(runtime); await sources.RefreshAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool closed = false;
        sources.InteractAsync = async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return null; }
            finally { retiring.TrySetResult(); await physical.Task; closed = true; }
        };
        runtime.Read = async (source, _, _, ct) =>
        {
            if (source.InstallationId == "b") { await sibling.Task.WaitAsync(ct); return Json(new { }); }
            runtime.PendingWeb = [Json(new { id = "cancelled-web", installationId = "a", mode = "rule", request = new { } })];
            try { await Task.Delay(Timeout.Infinite, ct); return Json(new { }); }
            finally { runtime.PendingWeb = []; }
        };
        using var cancel = new CancellationTokenSource();
        var first = sources.InvokeAsync(sources.Sources[0], "content", new { }, cancel.Token);
        var other = sources.InvokeAsync(sources.Sources[1], "search", new { });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            await retiring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!first.IsCompleted && !other.IsCompleted && !closed);
            physical.TrySetResult();
            try { await first; throw new InvalidOperationException("Cancelled invocation returned a result"); } catch (OperationCanceledException) { }
            Check(closed && !other.IsCompleted); sibling.TrySetResult(); await other;
        }
        finally { physical.TrySetResult(); sibling.TrySetResult(); await sources.PrepareShutdownAsync(); }
    }
    private sealed class WebFixture : IAsyncDisposable
    {
        private readonly RuntimeHttpClientTests.Server server = new();
        private readonly Task running;
        private bool disposed;
        internal string BaseUrl => server.BaseUrl;
        internal List<(string Path, string Cookie, string Host)> Requests { get; } = [];
        internal WebFixture() => running = RunAsync();
        private async Task RunAsync()
        {
            while (!disposed)
            {
                RuntimeHttpClientTests.Exchange exchange;
                try { exchange = await server.AcceptAsync(); }
                catch (OperationCanceledException) { continue; }
                // Chromium may close a speculative connection without sending a request.
                catch (EndOfStreamException) { continue; }
                catch (System.Net.Sockets.SocketException) when (disposed) { break; }
                catch (ObjectDisposedException) when (disposed) { break; }
                await using var request = exchange;
                Requests.Add((request.Path, request.Headers.GetValueOrDefault("Cookie", ""), request.Headers.GetValueOrDefault("Host", "")));
                if (request.Path == "/hold") { await request.AssertDisconnectedAsync(); continue; }
                string html = "<html><head><link rel='icon' href='data:,'></head><body>dynamic fixture</body></html>";
                try
                {
                    if (request.Path == "/redirect")
                    {
                        await request.ReplyAsync([], status: 302, contentType: "text/html",
                            extraHeaders: "Location: " + BaseUrl.Replace("127.0.0.1", "localhost") + "/other\r\nSet-Cookie: hop=updated-a; Path=/; SameSite=Lax\r\n");
                        continue;
                    }
                    await request.ReplyAsync(Encoding.UTF8.GetBytes(html), contentType: "text/html",
                        extraHeaders: request.Path == "/dynamic" ? "Set-Cookie: page=present; Path=/\r\n" :
                            request.Path == "/other" ? "Set-Cookie: other=updated-b; Path=/; SameSite=Lax\r\n" : "");
                }
                catch (IOException error) when (error.InnerException is System.Net.Sockets.SocketException
                    { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionAborted or System.Net.Sockets.SocketError.ConnectionReset })
                {
                    // Navigation or interaction retirement may abort optional resource responses.
                }
            }
        }
        public async ValueTask DisposeAsync() { disposed = true; await server.DisposeAsync(); await running; }
    }
    private static async Task SourceImages()
    {
        await using var server = new RuntimeHttpClientTests.Server();
        await using var runtime = new RuntimeHttpClient(server.BaseUrl);
        var sources = new NovelSourceService(runtime);
        const string reference = "/v1/source/images/novel-full-size";
        byte[] pixels = new byte[4 * 1024 * 1024 + 1]; new Random(31).NextBytes(pixels);
        foreach (bool chunked in new[] { false, true })
        {
            var read = sources.ReadImageAsync(reference, default);
            await using (var exchange = await server.AcceptAsync())
            {
                Check(exchange.Method == "GET" && exchange.Path == reference);
                if (chunked) await exchange.ReplyChunkedAsync(pixels);
                else await exchange.ReplyAsync(pixels, contentType: "image/png");
            }
            var received = await read;
            Check(pixels.SequenceEqual(received));
        }
        using var cancellation = new CancellationTokenSource();
        var cancelled = sources.ReadImageAsync(reference, cancellation.Token);
        await using var pending = await server.AcceptAsync();
        await pending.BeginImageAsync(100); cancellation.Cancel();
        try { await cancelled; throw new InvalidOperationException("Expected image cancellation"); }
        catch (OperationCanceledException) { }
        await pending.AssertDisconnectedAsync();
    }
    private static async Task CoverViewport()
    {
        var runtime = new Runtime(); var sources = new NovelSourceService(runtime); await sources.RefreshAsync();
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPixels = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownPixels = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] currentPixels = Pixels(32), retiredPixels = Pixels(224);
        string? oldReference = null;
        runtime.ReadImage = async (reference, token) =>
        {
            if (oldReference is not null) return currentPixels;
            oldReference = reference;
            using var registration = token.Register(() => oldCancelled.TrySetResult());
            oldStarted.TrySetResult();
            return await oldPixels.Task; // Deliberately finish after cancellation.
        };
        var image = new NovelImage(sources, Book() with { CoverUrl = "https://a.invalid/cover.png" });
        var spacer = new Border { Height = 600 };
        var cards = new StackPanel(); cards.Children.Add(spacer); cards.Children.Add(image);
        var scroll = new ScrollViewer { Content = cards, Width = 220, Height = 180, CanContentScroll = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var host = GeneralAppearanceTests.Window(scroll); host.Width = 350; host.Height = 280;
        try
        {
            host.Show(); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(image.IsVisible && image.ActualWidth > 0 && image.ActualHeight > 0,
                "A cover without pixels lost its arranged viewport bounds.");
            Check(image.Source is null && runtime.ImageReads == 0 &&
                !runtime.Calls.Any(call => call.Method == "source.runtime.library.image"),
                "A clipped offscreen cover acquired a source image.");
            spacer.Height = scroll.ViewportHeight; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(image.Source is null && runtime.ImageReads == 0, "Touching the viewport edge created cover demand without visible pixels.");
            spacer.Height = 600; host.UpdateLayout();
            scroll.ScrollToEnd(); host.UpdateLayout(); await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(runtime.ImageReads == 1 && runtime.ImageReferences.Contains(oldReference!) && image.Source is null);
            scroll.ScrollToTop(); host.UpdateLayout(); await oldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(image.IsVisible && image.Source is null && runtime.Released == 0 &&
                runtime.ImageReferences.Contains(oldReference!), "Scrolling out did not cancel or prematurely acknowledged the held image read.");
            scroll.ScrollToEnd(); host.UpdateLayout();
            await Until(() => runtime.ImageReads == 2 && image.Source is not null);
            var current = image.Source as BitmapSource ?? throw new InvalidOperationException("Visible cover has no decoded bitmap.");
            Check(runtime.ImageReferences.Count == 2, "The new viewport request waited for a retired read.");
            oldPixels.TrySetResult(retiredPixels);
            await Until(() => runtime.Released == 1 && !runtime.ImageReferences.Contains(oldReference!));
            Check(ReferenceEquals(image.Source, current), "Late retired cover pixels replaced the current request.");
            byte[] pixel = new byte[4]; current.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Check(pixel[0] == 32 && pixel[3] == 255);
            scroll.ScrollToTop(); host.UpdateLayout();
            await Until(() => image.Source is null && runtime.ImageReferences.Count == 0);
            Check(runtime.Released == 2);
            scroll.ScrollToEnd(); host.UpdateLayout();
            await Until(() => runtime.ImageReads == 3 && image.Source is not null);
            cards.Children.Remove(image); host.UpdateLayout();
            await Until(() => image.Source is null && runtime.ImageReferences.Count == 0);
            Check(runtime.Released == 3 && runtime.Calls.Where(call => call.Method == "source.runtime.library.image")
                .All(call => call.Payload.GetProperty("kind").GetString() == "cover" &&
                    call.Payload.GetProperty("installationId").GetString() == "a"));
            var outer = new Canvas { Width = 220, Height = 100, ClipToBounds = true };
            var inner = new Canvas { Width = 220, Height = 100, ClipToBounds = true };
            Canvas.SetTop(inner, 150); Canvas.SetTop(image, -100); inner.Children.Add(image); outer.Children.Add(inner);
            host.Content = outer; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(image.IsVisible && image.Source is null && runtime.ImageReads == 3,
                "Separate ancestor intersections created demand although their accumulated clip was empty.");
            Canvas.SetTop(inner, 0); host.UpdateLayout(); await Until(() => runtime.ImageReads == 4 && image.Source is not null);
            Canvas.SetTop(inner, 150); host.UpdateLayout(); await Until(() => image.Source is null && runtime.ImageReferences.Count == 0);
            Check(runtime.Released == 4);

            Canvas.SetTop(inner, 0); host.UpdateLayout(); await Until(() => runtime.ImageReads == 5 && image.Source is not null);
            string visibleReference = runtime.ImageReferences.Single();
            await sources.PrepareShutdownAsync();
            await Until(() => image.Source is null && runtime.ImageReferences.Count == 0);
            Check(runtime.Released == 5 && image.IsLoaded && image.IsVisible);
            sources.Resume();
            await Until(() => runtime.ImageReads == 6 && image.Source is not null);
            Check(runtime.ImageReferences.Count == 1 && !runtime.ImageReferences.Contains(visibleReference),
                "Resuming did not reacquire a fresh lease for the same visible cover without moving or rebuilding it.");
            await sources.PrepareShutdownAsync();
            await Until(() => image.Source is null && runtime.ImageReferences.Count == 0);

            string? shutdownReference = null;
            runtime.ReadImage = async (reference, token) =>
            {
                shutdownReference = reference;
                using var registration = token.Register(() => shutdownCancelled.TrySetResult());
                shutdownStarted.TrySetResult();
                return await shutdownPixels.Task;
            };
            sources.Resume(); await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var drain = sources.PrepareShutdownAsync();
            await shutdownCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!drain.IsCompleted && runtime.ImageReads == 7 && runtime.Released == 6 &&
                runtime.ImageReferences.Contains(shutdownReference!), "Shutdown acknowledged a cover read before its physical completion.");
            shutdownPixels.TrySetResult(retiredPixels); await drain;
            await Until(() => image.Source is null && runtime.ImageReferences.Count == 0);
            Check(runtime.Released == 7, "Retired shutdown pixels were displayed or their lease was retained.");
            runtime.ReadImage = (_, _) => Task.FromResult(currentPixels);
            sources.Resume(); await Until(() => runtime.ImageReads == 8 && image.Source is not null);
            Check(runtime.ImageReferences.Count == 1 && !runtime.ImageReferences.Contains(shutdownReference!));
            current = image.Source as BitmapSource ?? throw new InvalidOperationException("Resumed cover has no decoded bitmap.");
            current.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Check(pixel[0] == 32 && pixel[3] == 255, "A retired shutdown read replaced the resumed cover pixels.");

            await sources.PrepareShutdownAsync();
            await Until(() => image.Source is null && runtime.ImageReferences.Count == 0);
            runtime.ReadImage = (_, _) => throw new BridgeException("NETWORK_ERROR");
            sources.Resume(); await Until(() => runtime.ImageReads == 9 && runtime.Released == 9 && runtime.ImageReferences.Count == 0);
            int imageCalls = runtime.Calls.Count(call => call.Method == "source.runtime.library.image");
            for (int i = 0; i < 4; i++)
            {
                inner.InvalidateArrange(); outer.InvalidateMeasure(); host.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            Check(image.Source is null && runtime.ImageReads == 9 &&
                runtime.Calls.Count(call => call.Method == "source.runtime.library.image") == imageCalls,
                "Ordinary layout retried a failed cover request without a new demand or explicit resume.");

            runtime.ReadImage = (_, _) => Task.FromResult(currentPixels);
            image.Visibility = Visibility.Hidden; host.UpdateLayout();
            await sources.PrepareShutdownAsync(); sources.Resume();
            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(image.Source is null && runtime.ImageReads == 9 && runtime.ImageReferences.Count == 0,
                "Resume acquired a hidden cover.");
            image.Visibility = Visibility.Visible; host.UpdateLayout(); await Until(() => runtime.ImageReads == 10 && image.Source is not null);
            inner.Children.Remove(image); host.UpdateLayout();
            await Until(() => !image.IsLoaded && image.Source is null && runtime.ImageReferences.Count == 0);
            await sources.PrepareShutdownAsync(); sources.Resume();
            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(runtime.ImageReads == 10 && runtime.Released == 10 && runtime.ImageReferences.Count == 0,
                "Resume acquired an unloaded cover.");
        }
        finally { oldPixels.TrySetResult(retiredPixels); shutdownPixels.TrySetResult(retiredPixels); host.Close(); await sources.PrepareShutdownAsync(); }
        static byte[] Pixels(byte shade)
        {
            byte[] pixels = [shade, shade, shade, 255, shade, shade, shade, 255,
                shade, shade, shade, 255, shade, shade, shade, 255];
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
        }
    }
    private static async Task ImageLifecycle()
    {
        await using var store = new MemoryStore(); var library = new NovelLibrary(store); await library.InitializeAsync();
        var runtime = new Runtime { IncludeImages = true, ImageReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously), ImageReadHold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        var sources = new NovelSourceService(runtime); await sources.RefreshAsync(); await using var rules = new NovelRuleProgram(ruleRuntime);
        var session = new NovelBookSession(sources, library, rules, new("image", "key", Book())); await session.ContinueAsync();
        var surface = new NovelReadingSurface(sources); var host = GeneralAppearanceTests.Window(surface); host.Show();
        try
        {
            surface.Present(session, new() { Mode = "scroll" }); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            await runtime.ImageReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            surface.Visibility = Visibility.Collapsed; await surface.DrainImagesAsync();
            Check(runtime.Released == 1 && runtime.ImageReferences.Count == 0);
            runtime.ImageReadHold = null; surface.Visibility = Visibility.Visible; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            await Until(() => runtime.ImageReads >= 2); await Dispatcher.Yield(DispatcherPriority.Render);
            var anchor = surface.CaptureAnchor(); host.Width = 700; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            Check(surface.CaptureAnchor().BlockIndex == anchor.BlockIndex && surface.CaptureAnchor().Utf16Offset == anchor.Utf16Offset);
            await surface.DrainImagesAsync(); Check(runtime.ImageReferences.Count == 0 && runtime.Released >= 2);
        }
        finally { await surface.DrainImagesAsync(); surface.Dispose(); host.Close(); await sources.PrepareShutdownAsync(); }
    }
    private static async Task Purchase()
    {
        await using var store = new MemoryStore(); var runtime = new Runtime { Locked = true };
        var sources = new NovelSourceService(runtime); await sources.RefreshAsync();
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
        await module.InitializeAsync(default); await module.ReceiveBookAsync(Book(), default); await module.Session!.ContinueAsync();
        await module.NavigateAsync(NovelCapability.ReaderRoute, default);
        var host = GeneralAppearanceTests.Window(module.View); host.Show();
        try
        {
            await ClickPurchase("initial cancel"); var cancelledPopup = await Confirmation("initial cancel");
            Check(PopupItems<TextBlock>().Any(t => t.Text.Contains("书源a") && t.Text.Contains("合成小说") && t.Text.Contains("第一章")));
            Preview((FrameworkElement)cancelledPopup.Child, "novel-purchase-confirmation", 640, 320);
            var cancelledClosed = DialogClosed(cancelledPopup);
            ClickPopup("取消"); await cancelledClosed.WaitAsync(TimeSpan.FromSeconds(5)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!runtime.Calls.Any(c => IsAction(c, "payAction")));
            await ClickPurchase("navigation cancel"); var retiredPopup = await Confirmation("navigation cancel"); var retiredClosed = DialogClosed(retiredPopup);
            await module.NavigateAsync(NovelCapability.BookRoute, default); await retiredClosed.WaitAsync(TimeSpan.FromSeconds(5)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!PopupItems<Button>().Any(b => b.Content as string == "确认购买") && !runtime.Calls.Any(c => IsAction(c, "payAction")));
            await module.NavigateAsync(NovelCapability.ReaderRoute, default);
            await ClickPurchase("return to reader"); var acceptedPopup = await Confirmation("return to reader"); var acceptedClosed = DialogClosed(acceptedPopup);
            ClickPopup("确认购买"); await acceptedClosed.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => module.Session.Chapter?.IsPay == true);
            Check(runtime.Calls.Count(c => IsAction(c, "payAction")) == 1 && runtime.Calls.Count(c => IsAction(c, "content")) >= 2);
            await Dispatcher.Yield(DispatcherPriority.Render);
            Check(!Find<Button>(module.View).Single(b => b.Content as string == "购买本章").IsVisible);
            Preview(module.View, "novel-reader-purchased", 1000, 700);
        }
        finally { host.Close(); await module.PrepareShutdownAsync(); }
        async Task ClickPurchase(string stage)
        {
            try { await Until(() => Find<Button>(module.View).Any(b => b.Content as string == "购买本章" && b.IsVisible)); }
            catch (OperationCanceledException error) { throw new TimeoutException(stage + ": purchase button not visible on " + module.Route, error); }
            var buttons = Find<Button>(module.View).Where(b => b.Content as string == "购买本章" && b.IsVisible).ToArray();
            Check(buttons.Length == 1, stage + ": expected one visible purchase button on " + module.Route);
            buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        async Task<Popup> Confirmation(string stage)
        {
            try { await Until(() => PopupItems<Button>().Any(b => b.Content as string == "确认购买" && b.IsVisible)); }
            catch (OperationCanceledException error) { throw new TimeoutException(stage + ": purchase confirmation not visible", error); }
            var buttons = PopupItems<Button>().Where(b => b.Content as string == "确认购买" && b.IsVisible).ToArray();
            Check(buttons.Length == 1, stage + ": expected one current purchase confirmation.");
            return DialogPopup(buttons[0]);
        }
    }
    private static async Task Shell()
    {
        await using var store = new MemoryStore(); var sources = new NovelSourceService(new Runtime()); await sources.RefreshAsync();
        await using var ruleRuntime = new NovelRuleRuntimeFixture();
        await using var module = new NovelCapability(sources, new NovelLibrary(store), new NovelRuleProgram(ruleRuntime));
        var shell = new ShellWindow([module], [], () => module.PrepareShutdownAsync(), () => module.AbortAsync(), module.CancelShutdownPreparationAsync)
        { Width = 1050, Height = 760, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            shell.Show(); await Until(() => module.ExploreSource is not null && !module.LoadingDiscovery);
            await module.ReceiveBookAsync(Book(), default); module.Request(NovelCapability.BookRoute);
            await Until(() => module.Route == NovelCapability.BookRoute && module.Session!.Chapters.Count > 0);
            var retained = module.CaptureNavigationLocation(); await module.StartReadingAsync();
            await Until(() => module.Route == NovelCapability.ReaderRoute && ((Grid)shell.FindName("RootLayout")).ColumnDefinitions[0].Width.Value == 0);
            Check(((FrameworkElement)shell.FindName("PageHeaderHost")).Visibility == Visibility.Collapsed && ((FrameworkElement)shell.FindName("PlayerFrame")).Visibility == Visibility.Collapsed);
            Preview((FrameworkElement)shell.Content, "novel-shell-reader", 1050, 760);
            module.Request(NovelCapability.BookRoute);
            await Until(() => module.Route == NovelCapability.BookRoute && ((Grid)shell.FindName("RootLayout")).ColumnDefinitions[0].Width.Value == 203);
            Check(module.CaptureNavigationLocation() == retained && ((FrameworkElement)shell.FindName("PageHeaderHost")).Visibility == Visibility.Visible);
        }
        finally { shell.Close(); await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    private static bool IsAction((string Method, JsonElement Payload) call, string action) => call.Method == "source.runtime.library.invoke" && call.Payload.GetProperty("action").GetString() == action;
    private static IEnumerable<T> PopupItems<T>() where T : DependencyObject => PresentationSource.CurrentSources.Cast<PresentationSource>()
        .Where(s => s.RootVisual is not null).SelectMany(s => Find<T>(s.RootVisual)).Distinct();
    private static void ClickPopup(string label) => PopupItems<Button>().Last(b => b.Content as string == label && b.IsVisible).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(20, deadline.Token); }
    }
    private static void Preview(FrameworkElement view, string name, int width, int height)
    {
        string? previous = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT", previous ?? TestWorkspace.DataPath("novel-previews"));
            GeneralAppearanceTests.SavePreview(view, name, width, height);
        }
        finally { Environment.SetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT", previous); }
    }
}

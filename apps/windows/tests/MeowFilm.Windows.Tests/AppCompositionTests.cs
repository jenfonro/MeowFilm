using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.AppHost;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real WPF, DPAPI and an explicit script process; no product Main or user profile.
public static class AppCompositionTests
{
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Explicit Windows test environment required");
    private static string DataRoot()
    {
        string root = Path.Combine(Required("MEOWFILM_TEST_DATA_ROOT"), "app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("App components native startup explicit script UI", () => Sta(Components));
        yield return new("App native durable library and accounts without Node", Durable);
        yield return new("App uses its single current storage identity without product selection", Identity);
        yield return new("App shell cold startup and current queue history reopen without Node", () => Sta(CurrentShellStartup));
        yield return new("App shell non-current queue fails once without rewriting data", () => Sta(() =>
            RejectedShellStartup("queue", """{"schema":1,"entries":[],"currentId":null,"priority":[],"source":null,"similar":[]}""",
                "播放队列数据无效或格式不兼容")));
        yield return new("App shell non-current history fails once without rewriting data", () => Sta(() =>
            RejectedShellStartup("history", """{"schema":1,"playlists":[],"days":[],"extra":true}""",
                "播放历史数据无效或格式不兼容")));
    }
    private static Task Identity()
    {
        Check(AppIdentity.Name == "meowfilm" && AppIdentity.StorageId == "aggregate" && AppIdentity.RuntimeId == "aggregate");
        Check(AppDataPaths.Current == AppDataPaths.ForIdentity("aggregate"));
        Check(AppDataPaths.Current.Documents == Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "meowfilm", "aggregate"));
        Check(AppIdentity.ScriptModules.Select(m => (m.Id, m.Version)).SequenceEqual(new[]
        {
            ("music", "0.8.0"),
            ("cat", "0.1.0"),
            ("source", "0.1.0")
        }));
        Check(AppIdentity.NativeCollections.Count == 0, "music sources are stored by the shared runtime module");
        Check(AppIdentity.NativeNamespaces.Contains("music.accounts.v1"));
        Check(AppIdentity.NativeNamespaces.Contains("music.platform-context.v1"));
        Check(AppIdentity.ProtectedNamespaces.SequenceEqual(new[] { "music.accounts.v1", "music.platform-context.v1" }));
        return Task.CompletedTask;
    }
    private static async Task Components()
    {
        string data = DataRoot();
        await using var store = new LocalDocumentStore(data, AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
        await using var documents = new ProtectedDocumentBridge(store, []);
        IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>? methods = null;
        await using var runtime = new AppRuntimeClient(async token =>
            await NodeRuntime.StartAsync(Required("MEOWFILM_TEST_NODE"),
                RuntimeBundle.Verify(Path.Combine(Required("MEOWFILM_TEST_RUNTIME_ROOT"), "aggregate"),
                    AppIdentity.RuntimeId, AppIdentity.ScriptModules), AppIdentity.ScriptModules,
                methods ?? throw new InvalidOperationException("Capabilities not frozen"),
                Path.Combine(data, "script-runtime"), token));
        await using var playback = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
        await using var music = new MusicCapability(runtime, store, playback);
        var lifecycle = new AppShutdown([music], []);
        try
        {
            methods = AppLauncher.ComposeScriptCapabilities(documents.Methods, []);
            runtime.Start();
            await music.InitializeAsync(default);
            Check(music.Status.Length == 0);
            Check(music.CatalogSourcePlatform == "wy" && music.CanEditCatalogSource && !music.HasCatalogSourceError,
                "native startup restores the source preference without starting scripts");
            await music.NavigateAsync("favorites", default);
            Check(music.Status.Length == 0 && music.View != music.SettingsView);
            Check(music.Navigation.Count > 0 && music.Navigation.Select(n => n.Route).Distinct().Count() == music.Navigation.Count);
            RenderComponent(music.View, 1050, 650);
            RenderComponent(music.SettingsView, 860, 650);
            Check(!playback.HasMedia, "native browsing does not start playback");
            foreach (string ns in AppIdentity.NativeNamespaces.Concat(new[] { "unknown.namespace" }))
                await DeniedDocument(documents, ns);
            var status = await runtime.CallAsync("music.script.sources.list", new { });
            Check(status.GetProperty("ok").GetBoolean() && status.GetProperty("items").GetArrayLength() == 0 &&
                runtime.State == RuntimeConnectionState.Ready, "the App's runtime started with the App and runs no script");
            foreach (string method in new[] { "music.library.list", "missing.method" })
                await BridgeTests.Expect("METHOD_UNAVAILABLE", () => runtime.CallAsync(method, new { }));
            await lifecycle.CloseAsync();
        }
        finally { await lifecycle.ForceReleaseAsync(); }
    }
    private static async Task Durable()
    {
        string data = DataRoot(); string? playlistId = null;
        for (int pass = 0; pass < 2; pass++)
        {
            await using var store = new LocalDocumentStore(data, AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
            await using var documents = new ProtectedDocumentBridge(store, []);
            await using var runtime = new AppRuntimeClient(_ => throw new InvalidOperationException("Node unavailable"));
            runtime.Start();
            await using var owner = new MusicNativeApplication(store, runtime);
            if (pass == 0)
            {
                var playlist = await owner.ExecuteAsync("music.library.create", new { name = "合成歌单" });
                Check(playlist.GetProperty("ok").GetBoolean());
                playlistId = playlist.GetProperty("collection").GetProperty("id").GetString();
            }
            var library = await owner.ExecuteAsync("music.library.detail", new { id = playlistId, offset = 0, limit = 10 });
            Check(library.GetProperty("ok").GetBoolean() && library.GetProperty("collection").GetProperty("name").GetString() == "合成歌单");
            var accounts = await owner.ExecuteAsync("music.accounts.list", new { });
            Check(accounts.GetProperty("ok").GetBoolean() && accounts.GetProperty("items").GetArrayLength() == 3);
            Check(accounts.GetProperty("items").EnumerateArray().All(a => a.GetProperty("state").GetString() == "none"));
            var sources = await owner.ExecuteAsync("music.sources.list", new { });
            Check(!sources.GetProperty("ok").GetBoolean() &&
                sources.GetProperty("code").GetString() == "SCRIPT_RUNTIME_UNAVAILABLE",
                "a failed runtime is reported as an unavailable script service, never as no scripts");
            foreach (string ns in AppIdentity.NativeNamespaces) await DeniedDocument(documents, ns);
        }
    }
    private static async Task CurrentShellStartup()
    {
        string data = DataRoot();
        for (int pass = 0; pass < 2; pass++)
        {
            await using var store = new LocalDocumentStore(data, AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
            await using var runtime = new AppRuntimeClient(_ => throw new InvalidOperationException("Node unavailable"));
            runtime.Start();
            await using var playback = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var transport = new StartupTransport();
            var application = new MusicNativeApplication(store, runtime, transport);
            await using var music = new MusicCapability(application, store, playback);
            var lifecycle = new AppShutdown([music], []);
            var shell = new ShellWindow([music], [], lifecycle.CloseAsync, lifecycle.ForceReleaseAsync);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Closed += (_, _) => closed.TrySetResult();
            try
            {
                CheckRecommendationPage(music);
                shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Until(() => music.IsCatalogHome && !music.Busy && ShellStatus(shell).Length == 0 &&
                    ((ListBox)shell.FindName("NavigationList")).SelectedItem is ShellNavigationEntry { Route: "catalog-home" });
                Check(music.Status.Length == 0 && music.CatalogSections.Sum(section => section.Items.Count) > 0,
                    "default recommendation must contain native platform data");
                Check(music.CatalogSourcePlatform == "wy" && !music.HasCatalogSourceError,
                    "cold shell startup consumes the source settings get contract before home");
                Check(((ListBox)shell.FindName("NavigationList")).SelectedItem is ShellNavigationEntry { Route: "catalog-home" },
                    "Shell must commit its actual default route");
                RenderComponent((FrameworkElement)shell.Content, 1244, 848);
                shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Dispatcher.Yield(DispatcherPriority.Background);
                Check(transport.Calls == 1 && !playback.HasMedia,
                    "startup must load one visible page without automatic playback, even when Node fails");
                if (pass == 0)
                {
                    Check(!music.HasCurrent, "fresh storage must not manufacture a current song");
                    var created = await application.ExecuteAsync("music.library.create", new { name = "合成歌单" });
                    Check(created.GetProperty("ok").GetBoolean());
                    var queue = await application.ExecuteAsync("music.queue.replace", new
                    {
                        songs = new[] { MusicSongFixture.Element(MusicSongFixture.Local("library:local:startup", "合成歌曲")) },
                        index = 0
                    });
                    Check(queue.GetProperty("ok").GetBoolean());
                    var played = await application.ExecuteAsync("music.history.recordPlayed", new
                    {
                        entryId = queue.GetProperty("currentId").GetString(),
                        at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    Check(played.GetProperty("ok").GetBoolean());
                }
                else
                {
                    Check(music.HasCurrent && music.CurrentTitle == "合成歌曲" &&
                        music.Playlists.Any(row => row.Name == "合成歌单"), "current data must survive cold reopen");
                    await music.NavigateAsync("history", default);
                    Check(music.Status.Length == 0 && music.HistoryDates.Count == 1 && music.Songs.Count == 1,
                        "current history must remain readable after reopening");
                    Check(!playback.HasMedia, "restored selection is not playback");
                }
            }
            finally
            {
                shell.Close();
                await Task.WhenAll(shell.ShutdownCompletion, closed.Task).WaitAsync(TimeSpan.FromSeconds(8));
                Check(shell.ShutdownSucceeded, "startup fixture must close through the actual App lifecycle");
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
    }
    private static async Task RejectedShellStartup(string area, string value, string expected)
    {
        string data = DataRoot();
        await using var store = new LocalDocumentStore(data, AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
        var document = LJ.Obj(("version", 1), ("entries", new JsonArray(LJ.Obj(
            ("key", LJ.Stringify(new JsonArray("meowfilm", "music", 1, "local", area))),
            ("value", JsonNode.Parse(value))))));
        var fingerprint = await store.WriteAsync(MusicLibraryOwner.DocumentNamespace, LJ.Bytes(document), DocumentFingerprint.Missing);
        await using var runtime = new AppRuntimeClient(_ => throw new InvalidOperationException("Node unavailable"));
        runtime.Start();
        await using var playback = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
        var transport = new StartupTransport();
        var application = new MusicNativeApplication(store, runtime, transport);
        await using var music = new MusicCapability(application, store, playback);
        var messages = new List<string>();
        music.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MusicCapability.Status) && music.Status.Length > 0) messages.Add(music.Status);
        };
        var lifecycle = new AppShutdown([music], []);
        var shell = new ShellWindow([music], [], lifecycle.CloseAsync, lifecycle.ForceReleaseAsync);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.Closed += (_, _) => closed.TrySetResult();
        try
        {
            CheckRecommendationPage(music);
            shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await Until(() => ShellStatus(shell) == expected);
            Check(((ShellState)shell.DataContext).CanRetry, "explicit retry must remain available");
            CheckRecommendationPage(music);
            RenderComponent((FrameworkElement)shell.Content, 1244, 848);
            CheckRecommendationPage(music);
            Check(messages.Count == 0 && music.Status.Length == 0 &&
                ((NoticeStack)shell.FindName("Notices")).Children.Count == 0,
                "Shell-owned failure must not publish a second module notification");
            Check(transport.Calls == 0 && !playback.HasMedia,
                "rejected startup must not start unrelated work");
            var saved = await store.ReadAsync(MusicLibraryOwner.DocumentNamespace);
            try { Check(saved.Fingerprint == fingerprint, "non-current user data must remain unchanged"); }
            finally { if (saved.Data is not null) CryptographicOperations.ZeroMemory(saved.Data); }
        }
        finally
        {
            shell.Close();
            await Task.WhenAll(shell.ShutdownCompletion, closed.Task).WaitAsync(TimeSpan.FromSeconds(8));
            Check(shell.ShutdownSucceeded, "failed startup must still release the actual App");
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }
    private static string ShellStatus(ShellWindow shell) => ((TextBlock)shell.FindName("StatusText")).Text;
    private static void CheckRecommendationPage(MusicCapability music)
    {
        Check(music.IsCatalogHome,
            "startup selects the recommendation page");
        var header = ((IDesktopHeaderSource)music.View).Header;
        Check(header.Slot is null && header.HeaderActions.Count == 0,
            "startup must not expose unrelated collection controls");
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Check(DateTime.UtcNow < deadline, "App shell startup did not settle");
            await Task.Delay(5);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }
    private sealed class StartupTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(request.RequestUri!.AbsolutePath.EndsWith("/link/page/rcmd/resource/show", StringComparison.Ordinal),
                "startup may request only its visible recommendation page");
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"code":200,"data":{"cursor":"1","hasMore":false,"blocks":[{"positionCode":"PLAYLIST","dslData":{"blockResource":{"header":{"title":"推荐"},"items":[{"resourceType":"playlist","resourceId":"500","name":"合成推荐歌单"}]}}}]}}
                    """)
            });
        }
    }
    private static async Task DeniedDocument(ProtectedDocumentBridge bridge, string ns)
    {
        var value = await bridge.Methods["platform.documents.read.begin"](
            BridgeCodec.Value(new { version = 1, @namespace = ns, token = Guid.NewGuid().ToString("D") }), default);
        Check(!BridgeCodec.Value(value).GetProperty("ok").GetBoolean());
    }
    private static void RenderComponent(FrameworkElement view, int width, int height)
    {
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);

    }
    private static void Check(bool condition, string message = "App composition assertion")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60)); Check(thread.Join(TimeSpan.FromSeconds(5)));
    }
}

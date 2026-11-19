using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.AppHost;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real WinUI, current SQLite and an explicit script process; no product Main or user profile.
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
        yield return new("App shell canonical startup and current queue history reopen", () => Sta(CurrentShellStartup));
        yield return new("App shell corrupt current queue fails once without rewriting data", () => Sta(() =>
            RejectedShellStartup("queue", """{"schema":1,"entries":[],"currentId":"entry-1","priority":[],"source":null}""",
                "播放队列数据无效或格式不兼容")));
        yield return new("App corrupt current history fails on explicit read without rewriting data", () => Sta(RejectedHistoryRead));
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
            ("cat", "0.2.0"),
            ("source", "0.3.0"),
            ("pan", "0.1.0"),
            ("tmdb", "0.1.0"),
            ("douban", "0.1.0"),
            ("film", "0.1.0"),
            ("netease", "0.1.0"),
            ("music-platform", "0.1.0")
        }));
        Check(AppDataPaths.Current.ScriptRuntime == AppDataPaths.Current.Documents);
        Check(AppDataPaths.Current.Caches == Path.Combine(AppDataPaths.Current.Documents, "resources-v1"));
        Check(NativeDatabase.RelativePath == "storage-v1/meowfilm.sqlite3");
        return Task.CompletedTask;
    }
    private static async Task Components()
    {
        string data = DataRoot();
        await using var store = new NativeTestDatabase(data);
        await store.Ready;
        await using var runtime = new AppRuntimeClient(async token =>
            await NodeRuntime.StartAsync(Required("MEOWFILM_TEST_NODE"),
                RuntimeBundle.Verify(Path.Combine(Required("MEOWFILM_TEST_RUNTIME_ROOT"), "aggregate"),
                    AppIdentity.RuntimeId), AppIdentity.ScriptModules,
                data, token, settingsStore: new FilmRuntimeSettings(store)));
        await using var playback = new NativeAudioPlayback(audioOutput: NativeAudioOutput.Dummy);
        await using var music = new MusicCapability(runtime, store, MusicTestStores.For(store), playback);
        var lifecycle = new AppShutdown([music], []);
        try
        {
            runtime.Start();
            await music.InitializeAsync(default);
            Check(music.Status.Length == 0);
            Check(music.CatalogSourcePlatform == "wy" && music.CanEditCatalogSource && !music.HasCatalogSourceError,
                "native startup restores the source preference without starting scripts");
            await music.NavigateAsync("favorites", default);
            Check(music.Status.Length == 0 && music.View != music.SettingsView);
            Check(music.Navigation.Count > 0 && music.Navigation.Select(n => n.Route).Distinct().Count() == music.Navigation.Count);
            await RenderComponent(music.View, 1050, 650);
            await RenderComponent(music.SettingsView, 860, 650);
            Check(!playback.HasMedia, "native browsing does not start playback");
            var status = await runtime.CallAsync("music.script.sources.list", new { });
            Check(status.GetProperty("ok").GetBoolean() && status.GetProperty("value").GetProperty("items").GetArrayLength() == 0 &&
                runtime.State == RuntimeConnectionState.Ready, "the App's runtime started with the App and runs no script");
            foreach (string method in new[] { "music.library.list", "platform.documents.read.begin", "missing.method" })
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
            await using var store = new NativeTestDatabase(data);
            await using var runtime = new AppRuntimeClient(_ => throw new InvalidOperationException("Node unavailable"));
            runtime.Start();
            await using var owner = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
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
        }
    }
    private static async Task CurrentShellStartup()
    {
        string data = DataRoot();
        for (int pass = 0; pass < 2; pass++)
        {
            await using var store = new NativeTestDatabase(data);
            await using var runtime = StartupRuntime();
            await using var playback = new NativeAudioPlayback(audioOutput: NativeAudioOutput.Dummy);
            var application = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
            await using var music = new MusicCapability(application, store, playback);
            var lifecycle = new AppShutdown([music], []);
            var shell = new ShellWindow([music], [], lifecycle.CloseAsync, lifecycle.ForceReleaseAsync);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Closed += (_, _) => closed.TrySetResult();
            try
            {
                CheckRecommendationPage(music);
                shell.Show();
                await Until(() => music.IsCatalogHome && !music.Busy && ShellStatus(shell).Length == 0 &&
                    ((ListView)DesktopTestUi.WindowRoot(shell).FindName("ModuleNavigationList")).SelectedItem is ModuleSession session &&
                    ReferenceEquals(session.Module, music));
                Check(music.Status.Length == 0 && music.CatalogSections.Sum(section => section.Items.Count) > 0,
                    "default recommendation must contain native platform data");
                Check(music.CatalogSourcePlatform == "wy" && !music.HasCatalogSourceError,
                    "cold shell startup consumes the source settings get contract before home");
                Check(((ListView)DesktopTestUi.WindowRoot(shell).FindName("ModuleNavigationList")).SelectedItem is ModuleSession selected &&
                    ReferenceEquals(selected.Module, music) && music.CaptureNavigationLocation() == "catalog-home",
                    "Shell commits its actual home through the selected media item");
                var destinations = new[] { "NavigationList", "CollectionNavigationList" }
                    .SelectMany(name => ((ListView)DesktopTestUi.WindowRoot(shell).FindName(name)).Items.OfType<ShellNavigationEntry>()).ToArray();
                Check(destinations.Length > 0 && destinations.All(item =>
                    ReferenceEquals(item.Owner.Module, music) && item.Route != "catalog-home"),
                    "the current owner keeps its content navigation without a second home entry");
                await RenderComponent((FrameworkElement)shell.Content, 1244, 848);
                shell.Show();
                await DesktopTestUi.DrainAsync();
                Check(runtime.Requests.Count == 1 && runtime.Requests[0].Operation == "home" && !playback.HasMedia,
                    "startup consumes one canonical page without automatic playback");
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
                await DesktopTestUi.DrainAsync();
            }
        }
    }
    private static async Task RejectedHistoryRead()
    {
        await using var store = new NativeTestDatabase(DataRoot());
        var records = MusicTestStores.For(store);
        records.SetArea("history", JsonNode.Parse("""{"schema":1,"playlists":[]}"""));
        await using var runtime = StartupRuntime();
        await using var playback = new NativeAudioPlayback(audioOutput: NativeAudioOutput.Dummy);
        await using var music = new MusicCapability(runtime, store, records, playback);
        await music.InitializeAsync(default);
        var before = records.Root.ToJsonString();
        try
        {
            await music.NavigateAsync("history", default);
            throw new InvalidOperationException("Corrupt history must fail its explicit consumer read.");
        }
        catch (MusicOperationException error) when (error.Code == "CORRUPT_HISTORY")
        {
            Check(music.OperationErrorText(error) == "播放历史数据无效或格式不兼容");
        }
        Check(records.Root.ToJsonString() == before && music.IsCatalogHome,
            "rejected history remains unchanged and cannot replace the current page");
        Check(runtime.Requests.Count == 0 && !playback.HasMedia,
            "local history failure starts no platform request or playback");
    }
    private static async Task RejectedShellStartup(string area, string value, string expected)
    {
        string data = DataRoot();
        await using var store = new NativeTestDatabase(data);
        var records = MusicTestStores.For(store);
        records.SetArea(area, JsonNode.Parse(value));
        var before = records.Root.ToJsonString();
        await using var runtime = StartupRuntime();
        await using var playback = new NativeAudioPlayback(audioOutput: NativeAudioOutput.Dummy);
        var application = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
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
            shell.Show();
            await Until(() => ShellStatus(shell) == expected);
            Check(((ShellState)DesktopTestUi.WindowRoot(shell).DataContext).CanRetry, "explicit retry must remain available");
            CheckRecommendationPage(music);
            await RenderComponent((FrameworkElement)shell.Content, 1244, 848);
            CheckRecommendationPage(music);
            Check(messages.Count == 0 && music.Status.Length == 0 &&
                ((NoticeStack)DesktopTestUi.WindowRoot(shell).FindName("Notices")).Children.Count == 0,
                "Shell-owned failure must not publish a second module notification");
            Check(runtime.Requests.Count == 0 && !playback.HasMedia,
                "rejected startup must not start unrelated work");
            Check(records.Root.ToJsonString() == before, "corrupt current records must remain unchanged");
        }
        finally
        {
            shell.Close();
            await Task.WhenAll(shell.ShutdownCompletion, closed.Task).WaitAsync(TimeSpan.FromSeconds(8));
            Check(shell.ShutdownSucceeded, "failed startup must still release the actual App");
            await DesktopTestUi.DrainAsync();
        }
    }
    private static string ShellStatus(ShellWindow shell) => ((TextBlock)DesktopTestUi.WindowRoot(shell).FindName("StatusText")).Text;
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
            await DesktopTestUi.DrainAsync();
        }
    }
    private static MusicPlatformRuntimeFixture StartupRuntime() => new()
    {
        OnRequest = (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            Check(request.Platform == "wy" && request.Operation == "home",
                "startup may request only its visible canonical recommendation page");
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(new
            {
                platform = "wy", more = false, sections = new[]
                {
                    new { id = "PLAYLIST", title = "推荐", type = "playlists", layout = "list", items = new[]
                    {
                        new { id = "500", name = "合成推荐歌单", kind = "playlist", action = new { type = "playlist", id = "500" } }
                    } }
                }
            }));
        }
    };
    private static async Task RenderComponent(FrameworkElement view, int width, int height)
    {
        using var host = view.XamlRoot is null ? new DesktopTestUi.Host(view, width, height) : null;
        await DesktopTestUi.DrainAsync();
        var pixels = await DesktopTestUi.Pixels(view, width, height);
        Check(pixels.Length == width * height * 4, "real WinUI rendering has the requested pixel extent");
    }
    private static void Check(bool condition, string message = "App composition assertion")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Sta(Func<Task> action)
    { await TestUiHost.InvokeAsync(action); }
}

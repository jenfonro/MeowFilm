using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmPlaybackSettingsTests
{
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-playback-settings: " + name, () => MusicQualityConsumerTests.Sta(run));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("full preferences preserve empty keywords and restore only the chosen group across a new view", async () =>
        {
            await using var scene = await Scene.Create();
            scene.Element<TextBox>("Keywords").Text = "";
            Click(scene.Button("保存智能播放偏好"));
            await Until(() => scene.View.Idle);
            Check(FilmPreferences.Read(scene.Preferences).Keywords.Length == 0 &&
                FilmPreferences.Read(scene.Preferences).PanOrder[0] == "baidu", "empty is legal and other groups survive the full save");
            var restore = Visuals(scene.View).OfType<Button>().Single(button => Equals(button.Tag, "rule"));
            Click(restore); await Until(() => scene.View.Idle);
            var value = FilmPreferences.Read(scene.Preferences);
            Check(value.RuleOrder.SequenceEqual(FilmPreferences.Defaults.RuleOrder) && value.PanOrder[0] == "baidu" &&
                value.Keywords.Length == 0 && value.SiteCleanKeywords.SequenceEqual(["fixture-clean"]) && !value.IosAutoPrepare,
                "restore does not reset unrelated groups or the platform prepare switch");
            scene.Window.Content = null;
            var next = new FilmPlaybackSettingsView(scene.Service, scene.Favorites, () => Card().PlaybackContext!.Target);
            scene.Window.Content = next; await next.ShowAsync("preferences");
            await Until(() => next.Idle && ((ListBox)next.FindName("RuleOrder")).Items.Count == 3);
            Check(((TextBox)next.FindName("Keywords")).Text == "" &&
                ((CheckBox)next.FindName("AutoPrepare")).IsChecked == false, "a new presentation reads the same durable owner response");
            await next.PrepareShutdownAsync(); scene.Window.Content = scene.View;
        });
        yield return Test("a retired section's late read cannot overwrite the next collection", async () =>
        {
            await using var scene = await Scene.Create();
            var entered = Gate(); var finish = Gate();
            int reads = 0; CancellationToken oldToken = default;
            scene.Runtime.Intercept = async (method, input, token) =>
            {
                if (method == "film.library.get" && ++reads == 1)
                {
                    oldToken = token; entered.TrySetResult(); await finish.Task;
                    return FilmRuntimeProbe.Ok(FilmJson.Encode(new { history = new[] { new { title = "旧历史" } } }));
                }
                return scene.Response(method, input);
            };
            try
            {
                var old = scene.View.ShowAsync("history"); await entered.Task;
                var next = scene.View.ShowAsync("blocks");
                Check(oldToken.IsCancellationRequested && !next.IsCompleted, "the old consumer retires but its actual read is still drained");
                finish.TrySetResult(); await Task.WhenAll(old, next);
                await Until(() => scene.View.Idle && scene.View.Rows.Count == 1);
                Check(scene.View.Rows[0].Title == "当前屏蔽", "late history never becomes the blocks collection");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("admitted mutations outlive section changes and shutdown waits without cancelling the original payload", async () =>
        {
            await using var scene = await Scene.Create();
            var entered = Gate(); var finish = Gate(); CancellationToken writeToken = default;
            scene.Runtime.Intercept = async (method, input, token) =>
            {
                if (method == "film.settings.set") { writeToken = token; entered.TrySetResult(); await finish.Task; }
                return scene.Response(method, input);
            };
            try
            {
                scene.Element<TextBox>("Keywords").Text = "first\nsecond";
                Click(scene.Button("保存智能播放偏好")); await entered.Task;
                var switching = scene.View.ShowAsync("history");
                var close = scene.View.PrepareShutdownAsync();
                Check(!close.IsCompleted && !writeToken.IsCancellationRequested && !scene.View.Idle,
                    "UI deactivation revokes publication, not an accepted durable mutation");
                finish.TrySetResult(); await Task.WhenAll(switching, close);
                Check(FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["first", "second"]) &&
                    !scene.Notices.Contains("设置已保存"), "the original save settles without a success notification in a retired section");
                scene.View.Resume();
                await Until(() => scene.View.Idle && scene.View.Rows.Count == 1);
                Check(scene.View.Rows[0].Title == "当前历史", "a cancelled shutdown can read the current section again");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("a failed write is notified without publishing success or permanently blocking shutdown", async () =>
        {
            await using var scene = await Scene.Create();
            scene.FailWrite = true;
            scene.Element<TextBox>("Keywords").Text = "unsaved";
            Click(scene.Button("保存智能播放偏好")); await Until(() => scene.View.Idle);
            Check(scene.Notices.Contains("保存失败") && !scene.Notices.Contains("设置已保存") &&
                !FilmPreferences.Read(scene.Preferences).Keywords.Contains("unsaved"),
                "failed storage never becomes a successful preference snapshot");
            await scene.View.PrepareShutdownAsync(); await scene.View.PrepareShutdownAsync();
            scene.FailWrite = false; scene.View.Resume();
            await Until(() => scene.View.Idle && scene.Element<TextBox>("Keywords").Text == "fixture-keyword");
        });
        yield return Test("clear collections and typed block identities reach the one business owner", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Service.RemoveHistoryAsync(null); await scene.Service.RemoveBlockAsync(null);
            var calls = scene.Runtime.Calls.Where(call => call.Method == "film.library.remove").ToArray();
            Check(calls.Length == 2 && FilmJson.Text(calls[0].Parameters, "kind") == "history" &&
                FilmJson.Member(calls[0].Parameters, "id").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined &&
                FilmJson.Text(calls[1].Parameters, "kind") == "blocks" &&
                FilmJson.Member(calls[1].Parameters, "block").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
                "clear is the official omitted/null identity operation, not repeated guessed deletions");
            var block = FilmJson.Encode(new { targetId = "tmdb:tv:100", sourceKey = "source:fixture", detailId = 0, lineId = 0 });
            await scene.Service.RemoveBlockAsync(block);
            var sent = scene.Runtime.Calls.Last().Parameters.GetProperty("block");
            Check(JsonElement.DeepEquals(sent, block), "numeric zero and the whole exact block identity survive the consumer");
            await scene.Service.RemoveHistoryAsync("tmdb:tv:100");
            Check(FilmJson.Text(scene.Runtime.Calls.Last().Parameters, "id") == "tmdb:tv:100", "one history removal is exact");
        });
        yield return Test("manual editing preserves numeric zero special season and private share controls", async () =>
        {
            await using var scene = await Scene.Create();
            scene.LibraryValue = FilmJson.Encode(new { manual = new[] { new
            { id = "fixture-manual", targetId = "tmdb:tv:100", title = "人工样例", kind = "site",
                sourceKey = "source:fixture", detailId = 0, seasonHint = 0, enabled = true, autoDisable = true, failureCount = 2 } } });
            await scene.View.ShowAsync("manual"); scene.View.UpdateLayout();
            Click(Visuals(scene.View).OfType<Button>().Single(button => Equals(button.Content, "编辑")));
            var dialog = scene.Window.OwnedWindows.Cast<Window>().Single();
            Check(Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "详情标识").Text == "0" &&
                Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "季线索").Text == "0",
                "editing does not treat zero as a missing identity or season");
            Check(Visuals(dialog).OfType<PasswordBox>().Select(AutomationProperties.GetName)
                .Order().SequenceEqual(new[] { "分享提取码", "分享链接" }.Order()) &&
                !Visuals(dialog).OfType<TextBox>().Any(box => AutomationProperties.GetName(box).Contains("分享", StringComparison.Ordinal)),
                "both sensitive share fields use private controls");
            Click(Visuals(dialog).OfType<Button>().Single(button => Equals(button.Content, "保存")));
            await Until(() => !dialog.IsVisible && scene.View.Idle);
            var saved = scene.Runtime.Calls.Last(call => call.Method == "film.manual.update").Parameters.GetProperty("item");
            Check(saved.GetProperty("detailId").ValueKind == JsonValueKind.Number && saved.GetProperty("detailId").GetInt32() == 0 &&
                saved.GetProperty("seasonHint").GetInt32() == 0 && saved.GetProperty("failureCount").GetInt32() == 2 &&
                saved.GetProperty("autoDisable").GetBoolean(), "the exact typed edit reaches the business owner without resetting failure state");
        });
        yield return Test("a late manual save cannot reopen or populate a retired editor", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.View.ShowAsync("manual");
            Click((Button)scene.View.FindName("AddManual"));
            var dialog = scene.Window.OwnedWindows.Cast<Window>().Single();
            Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "来源标识").Text = "source:fixture";
            Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "详情标识").Text = "fixture-detail";
            var entered = Gate(); var finish = Gate();
            scene.Runtime.Intercept = async (method, input, token) =>
            {
                if (method == "film.manual.update") { entered.TrySetResult(); await finish.Task; }
                return scene.Response(method, input);
            };
            try
            {
                Click(Visuals(dialog).OfType<Button>().Single(button => Equals(button.Content, "保存")));
                await entered.Task;
                var next = scene.View.ShowAsync("blocks");
                Check(!dialog.IsVisible, "section retirement closes the editor immediately, not only after its save finishes");
                finish.TrySetResult(); await next;
                Check(!dialog.IsVisible && scene.Window.OwnedWindows.Count == 0 && scene.View.Rows.Single().Title == "当前屏蔽",
                    "late durable success does not reopen the editor or overwrite the next collection");
            }
            finally { finish.TrySetResult(); }
        });
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly FilmRuntimeProbe Runtime = new();
        internal readonly FilmLibrary Favorites = new(new FilmMemoryStore());
        internal readonly FilmPlaybackSettings Service;
        internal readonly FilmPlaybackSettingsView View;
        internal readonly Window Window;
        internal readonly List<string> Notices = [];
        internal bool FailWrite;
        internal JsonElement Preferences = FilmJson.Encode(new FilmPreferences(["keyword", "pan", "quality"],
            ["baidu", "uc", "quark", "tianyi", "yidong"], ["fixture-keyword"], ["fixture-clean"], false));
        internal JsonElement LibraryValue = FilmJson.Encode(new
        {
            history = new[] { new { title = "当前历史", targetId = "tmdb:tv:100", position = 10d, duration = 600d } },
            blocks = new[] { new { title = "当前屏蔽", targetId = "tmdb:tv:100", sourceKey = "fixture", detailId = 0, lineId = 0 } },
            manual = Array.Empty<object>()
        });
        private Scene()
        {
            Service = new(Runtime); View = new(Service, Favorites, () => Card().PlaybackContext!.Target);
            Runtime.Intercept = (method, input, _) => Task.FromResult(Response(method, input));
            Window = new() { Content = View, Width = 1000, Height = 800, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            Window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, args) =>
            { if (args.Message.Length > 0) Notices.Add(args.Message); }));
        }
        internal JsonElement Response(string method, JsonElement input)
        {
            if (method == "film.settings.get") return FilmRuntimeProbe.Ok(Preferences);
            if (method == "film.settings.set")
            {
                if (FailWrite) return FilmRuntimeProbe.Failure("STORAGE_FAILED");
                Preferences = input.Clone(); return FilmRuntimeProbe.Ok(Preferences);
            }
            if (method == "film.library.get") return FilmRuntimeProbe.Ok(LibraryValue);
            return Runtime.Handle(method, input);
        }
        internal static async Task<Scene> Create()
        {
            var scene = new Scene();
            try
            {
                await scene.View.ShowAsync("preferences"); scene.Window.Show();
                await Until(() => scene.View.Idle && scene.Element<ListBox>("RuleOrder").Items.Count == 3);
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        internal T Element<T>(string name) where T : FrameworkElement => (T)View.FindName(name);
        internal Button Button(string name) => Visuals(View).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
        public async ValueTask DisposeAsync()
        {
            await View.PrepareShutdownAsync();
            foreach (Window owned in Window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            Window.Content = null; Window.Close(); await Favorites.DisposeAsync(); await Runtime.DisposeAsync();
        }
    }
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        await FilmPlaybackFixture.Until(condition);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}

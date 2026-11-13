using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
            Click(scene.Button("保存关键字匹配规则"));
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
            var next = new FilmPlaybackSettingsView(scene.Service, () => Card().PlaybackContext!.Target);
            scene.Window.Content = next; await next.ShowAsync("preferences");
            await Until(() => next.Idle && ((SettingsPriorityList)next.FindName("RuleOrder")).ItemsSource?.Cast<object>().Count() == 3);
            Check(((TextBox)next.FindName("Keywords")).Text == "" &&
                !FilmPreferences.Read(scene.Preferences).IosAutoPrepare, "a new presentation reads the same durable owner response");
            await next.PrepareShutdownAsync(); scene.Window.Content = scene.View;
        });
        yield return Test("a retired section's late read cannot overwrite the next collection", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.View.ShowAsync("clean");
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
                var old = scene.View.ShowAsync("preferences"); await entered.Task;
                var next = scene.View.ShowAsync("clean");
                Check(oldToken.IsCancellationRequested && !next.IsCompleted, "the old consumer retires but its actual read is still drained");
                finish.TrySetResult(); await Task.WhenAll(old, next);
                await Until(() => scene.View.Idle && scene.Element<TextBox>("CleanKeywords").Text == "fixture-clean");
                Check(scene.View.Rows.Count == 0 && scene.View.ManualGroups.Count == 0 && scene.View.BlockGroups.Count == 0,
                    "late library data never becomes the site-clean collection");
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
                scene.Element<TextBox>("Keywords").Text = " first,，second, ";
                Click(scene.Button("保存关键字匹配规则")); await entered.Task;
                var switching = scene.View.ShowAsync("clean");
                var close = scene.View.PrepareShutdownAsync();
                Check(!close.IsCompleted && !writeToken.IsCancellationRequested && !scene.View.Idle,
                    "UI deactivation revokes publication, not an accepted durable mutation");
                finish.TrySetResult(); await Task.WhenAll(switching, close);
                Check(FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["first", "second"]) &&
                    !scene.Notices.Contains("设置已保存"), "the original save settles without a success notification in a retired section");
                scene.View.Resume();
                await Until(() => scene.View.Idle && scene.Element<TextBox>("CleanKeywords").Text == "fixture-clean");
                Check(scene.Element<Border>("CleanForm").IsVisible && scene.View.Rows.Count == 0,
                    "a cancelled shutdown can read the current section again");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("a failed write is notified without publishing success or permanently blocking shutdown", async () =>
        {
            await using var scene = await Scene.Create();
            scene.FailWrite = true;
            scene.Element<TextBox>("Keywords").Text = "unsaved";
            Click(scene.Button("保存关键字匹配规则")); await Until(() => scene.View.Idle);
            Check(scene.Notices.Contains("保存失败") && !scene.Notices.Contains("设置已保存") &&
                !FilmPreferences.Read(scene.Preferences).Keywords.Contains("unsaved"),
                "failed storage never becomes a successful preference snapshot");
            await scene.View.PrepareShutdownAsync(); await scene.View.PrepareShutdownAsync();
            scene.FailWrite = false; scene.View.Resume();
            await Until(() => scene.View.Idle && scene.Element<TextBox>("Keywords").Text == "fixture-keyword");
        });
        yield return Test("ordering saves exactly once per real move without saving or discarding keyword drafts", async () =>
        {
            await using var scene = await Scene.Create();
            scene.Element<TextBox>("Keywords").Text = "not-saved-yet";
            int writes = scene.Writes;
            foreach (var name in new[] { "RuleOrder", "PanOrder" })
            {
                var priority = scene.Element<SettingsPriorityList>(name);
                priority.BringIntoView(); await Until(() => priority.CanEdit);
                var list = (ListBox)priority.FindName("PriorityItems");
                var original = list.Items.Cast<object>().ToArray();
                var first = Grip(list, 0);
                ComponentCommands.MoveUp.Execute(null, first);
                Check(scene.Writes == writes, "moving the first row up is a no-op, not a save");
                ComponentCommands.MoveDown.Execute(null, first); await Until(() => scene.View.Idle);
                Check(scene.Writes == ++writes && ReferenceEquals(list.Items[1], original[0]),
                    "one keyboard move saves once and retains the moved item identity");
                Check(scene.Element<TextBox>("Keywords").Text == "not-saved-yet" &&
                    FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["fixture-keyword"]),
                    "ordering neither persists nor clears a separate keyword draft");
            }
            var saved = FilmPreferences.Read(scene.Preferences);
            Check(saved.RuleOrder.SequenceEqual(["pan", "keyword", "quality"]) &&
                saved.PanOrder.SequenceEqual(["uc", "baidu", "quark", "tianyi", "yidong"]),
                "both order controls save the exact chosen position without resetting each other");
            Click(scene.Button("恢复优先片源默认规则")); await Until(() => scene.View.Idle);
            Check(scene.Element<TextBox>("Keywords").Text == "not-saved-yet" &&
                FilmPreferences.Read(scene.Preferences).Keywords.SequenceEqual(["fixture-keyword"]) &&
                FilmPreferences.Read(scene.Preferences).PanOrder.SequenceEqual(saved.PanOrder),
                "restoring an order also leaves every unrelated draft and saved group alone");
            scene.Element<TextBox>("Keywords").Text = "";
            await Until(() => scene.Element<TextBlock>("KeywordsPlaceholder").IsVisible);
            Check(scene.Element<TextBlock>("KeywordsPlaceholder").Text == "DDP,H.265",
                "the server keyword example is visible only for an empty field");
            Click(scene.Button("保存关键字匹配规则")); await Until(() => scene.View.Idle);
            Check(FilmPreferences.Read(scene.Preferences).Keywords.Length == 0, "the placeholder is never submitted as data");
        });
        yield return Test("drag cancellation and same-position release do not save but an actual drop does", async () =>
        {
            await using var scene = await Scene.Create();
            var priority = scene.Element<SettingsPriorityList>("RuleOrder");
            var list = (ListBox)priority.FindName("PriorityItems");
            var first = list.Items[0]; int writes = scene.Writes;
            Point Begin(int destination)
            {
                var grip = Grip(list, 0);
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(destination);
                var drop = row.TranslatePoint(new Point(60, row.ActualHeight - 2), priority.DragViewport);
                priority.Drag.Begin(grip, list, grip.TranslatePoint(new Point(18, 18), priority.DragViewport));
                priority.Drag.Update(drop);
                Check(priority.IsDragging, "the real shared control owns a captured gesture");
                return drop;
            }
            priority.CompleteDrag(Begin(2), true);
            priority.CompleteDrag(Begin(0), false);
            Check(scene.Writes == writes && ReferenceEquals(list.Items[0], first), "cancel and in-place release do not mutate");
            var point = Begin(2);
            priority.CompleteDrag(point, false); await Until(() => scene.View.Idle);
            priority.CompleteDrag(point, false);
            Check(scene.Writes == writes + 1 && ReferenceEquals(list.Items[2], first) &&
                FilmPreferences.Read(scene.Preferences).RuleOrder.SequenceEqual(["pan", "quality", "keyword"]),
                "one accepted drop saves exactly its final order, duplicate release does nothing");
        });
        yield return Test("failed ordering restores the authoritative sequence and preserves the keyword draft", async () =>
        {
            await using var scene = await Scene.Create();
            var priority = scene.Element<SettingsPriorityList>("RuleOrder");
            var list = (ListBox)priority.FindName("PriorityItems");
            var original = list.Items.Cast<object>().ToArray();
            scene.Element<TextBox>("Keywords").Text = "keep-draft";
            scene.FailWrite = true; int writes = scene.Writes;
            ComponentCommands.MoveDown.Execute(null, Grip(list, 0)); await Until(() => scene.View.Idle);
            Check(scene.Writes == writes + 1 && list.Items.Cast<object>().SequenceEqual(original) &&
                FilmPreferences.Read(scene.Preferences).RuleOrder.SequenceEqual(["keyword", "pan", "quality"]),
                "failure rolls back displayed order using the same row identities");
            Check(scene.Notices.Contains("保存失败") && !scene.Notices.Contains("设置已保存") &&
                scene.Element<TextBox>("Keywords").Text == "keep-draft", "failure is visible and does not discard the text draft");
        });
        yield return Test("an admitted order keeps keyboard ownership and drains on section retirement without stale success", async () =>
        {
            await using var scene = await Scene.Create();
            var priority = scene.Element<SettingsPriorityList>("RuleOrder");
            var list = (ListBox)priority.FindName("PriorityItems");
            var moved = list.Items[0];
            var entered = Gate(); var finish = Gate(); CancellationToken writeToken = default;
            scene.Runtime.Intercept = async (method, input, token) =>
            {
                if (method == "film.settings.set") { writeToken = token; entered.TrySetResult(); await finish.Task; }
                return scene.Response(method, input);
            };
            try
            {
                scene.Window.Focus(); var handle = Grip(list, 0); handle.Focus();
                Check(ReferenceEquals(Keyboard.FocusedElement, handle), "the actual handle initially owns keyboard focus");
                ComponentCommands.MoveDown.Execute(null, handle); await entered.Task;
                Check(!priority.CanEdit && priority.IsEnabled && ReferenceEquals(Keyboard.FocusedElement, priority),
                    "only list editing pauses while the shared control retains its focus-restoration owner");
                finish.TrySetResult(); await Until(() => scene.View.Idle);
                Check(ReferenceEquals(list.Items[1], moved) && ReferenceEquals(Keyboard.FocusedElement, Grip(list, 1)),
                    "the original row regains its handle after the automatic save");
                var retired = Gate(); var settle = Gate();
                scene.Runtime.Intercept = async (method, input, token) =>
                {
                    if (method == "film.settings.set") { writeToken = token; retired.TrySetResult(); await settle.Task; }
                    return scene.Response(method, input);
                };
                try
                {
                    int notices = scene.Notices.Count;
                    ComponentCommands.MoveDown.Execute(null, Grip(list, 1)); await retired.Task;
                    var switching = scene.View.ShowAsync("clean");
                    var closing = scene.View.PrepareShutdownAsync();
                    Check(!closing.IsCompleted && !writeToken.IsCancellationRequested,
                        "a durable order write is drained, not cancelled with its page");
                    settle.TrySetResult(); await Task.WhenAll(switching, closing); await Until(() => !priority.CanEdit);
                    Check(FilmPreferences.Read(scene.Preferences).RuleOrder.SequenceEqual(["pan", "quality", "keyword"]) &&
                        scene.Notices.Count == notices, "the exact order settles without publishing success to a retired section");
                }
                finally { settle.TrySetResult(); }
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("read failure exposes only a retry action and successful retry never writes preferences", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.View.ShowAsync("clean");
            bool fail = true; int writes = scene.Writes;
            scene.Runtime.Intercept = (method, input, _) => Task.FromResult(method == "film.settings.get" && fail
                ? FilmRuntimeProbe.Failure("STORAGE_FAILED") : scene.Response(method, input));
            await scene.View.ShowAsync("preferences");
            var retry = scene.Button("重新读取智能播放设置");
            await Until(() => scene.View.ReadFailed && retry.IsVisible);
            Check(scene.View.ReadFailed && retry.IsVisible && scene.Notices.Contains("影视设置读取失败"),
                "failed reads have an explicit visible retry");
            fail = false; Click(retry);
            await Until(() => scene.View.Idle && !scene.View.ReadFailed);
            Check(!retry.IsVisible && scene.Writes == writes &&
                scene.Element<TextBox>("Keywords").Text == "fixture-keyword", "recovery only reads the authoritative settings");
        });
        yield return Test("clear collections and typed block identities reach the one business owner", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Service.RemoveBlockAsync(null);
            var calls = scene.Runtime.Calls.Where(call => call.Method == "film.library.remove").ToArray();
            Check(calls.Length == 1 && FilmJson.Text(calls[0].Parameters, "kind") == "blocks" &&
                FilmJson.Member(calls[0].Parameters, "block").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
                "clear is the official omitted/null identity operation, not repeated guessed deletions");
            var block = FilmJson.Encode(new { targetId = "tmdb:tv:100", sourceKey = "source:fixture", detailId = 0, lineId = 0 });
            await scene.Service.RemoveBlockAsync(block);
            var sent = scene.Runtime.Calls.Last().Parameters.GetProperty("block");
            Check(JsonElement.DeepEquals(sent, block), "numeric zero and the whole exact block identity survive the consumer");
        });
        yield return Test("manual editing preserves numeric zero special season and plain editable share controls", async () =>
        {
            await using var scene = await Scene.Create();
            scene.LibraryValue = FilmJson.Encode(new { manual = new[] { new
            { id = "fixture-manual", targetId = "tmdb:tv:100", title = "人工样例", kind = "site",
                sourceKey = "source:fixture", detailId = 0, seasonHint = 0, enabled = true, autoDisable = true, failureCount = 2 } }, blocks = Array.Empty<object>() });
            await scene.View.ShowAsync("clean"); await scene.View.ShowAsync("preferences"); scene.View.UpdateLayout();
            Click(Visuals(scene.View).OfType<Button>().Single(button => Equals(button.Content, "编辑")));
            var dialog = scene.Window.OwnedWindows.Cast<Window>().Single();
            Check(Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "详情标识").Text == "0" &&
                Visuals(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "季线索").Text == "0",
                "editing does not treat zero as a missing identity or season");
            Check(!Visuals(dialog).OfType<PasswordBox>().Any() &&
                Visuals(dialog).OfType<TextBox>().Count(box => AutomationProperties.GetName(box) is "分享提取码" or "分享链接") == 2,
                "share fields use the same plain text editing model as data source credentials");
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
                var next = scene.View.ShowAsync("clean");
                Check(!dialog.IsVisible, "section retirement closes the editor immediately, not only after its save finishes");
                finish.TrySetResult(); await next;
                Check(!dialog.IsVisible && scene.Window.OwnedWindows.Count == 0 && scene.View.Rows.Count == 0 && scene.Element<Border>("CleanForm").IsVisible,
                    "late durable success does not reopen the editor or overwrite the next collection");
            }
            finally { finish.TrySetResult(); }
        });
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly FilmRuntimeProbe Runtime = new();
        internal readonly FilmPlaybackSettings Service;
        internal readonly FilmPlaybackSettingsView View;
        internal readonly Window Window;
        internal readonly List<string> Notices = [];
        internal bool FailWrite;
        internal int Writes => Runtime.Calls.Count(call => call.Method == "film.settings.set");
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
            Service = new(Runtime); View = new(Service, () => Card().PlaybackContext!.Target);
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
                await Until(() => scene.View.Idle && scene.Element<SettingsPriorityList>("RuleOrder").ItemsSource?.Cast<object>().Count() == 3);
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
            Window.Content = null; Window.Close(); await Runtime.DisposeAsync();
        }
    }
    private static Thumb Grip(ListBox list, int index)
    {
        list.UpdateLayout();
        return Visuals((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)).OfType<Thumb>().Single();
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

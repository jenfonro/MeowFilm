using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class NovelSettingsPersistenceTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("novel settings persistence: offline rule editing and import save without JavaScript validation", () => NovelTests.Sta(Offline));
        yield return new("novel settings persistence: failed rule editing and import retain the committed snapshot", () => NovelTests.Sta(Failure));
        yield return new("novel settings persistence: local rule fields types and positive timeout are checked before saving", () => NovelTests.Sta(Structure));
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Offline()
    {
        await using var scene = new Scene(); await scene.Ready();
        int changes = 0; scene.Module.Library.Changed += (_, _) => changes++;
        var editing = scene.Edit(new NovelRule { Id = "edited", Pattern = "初始" });
        await Until(() => scene.Host.IsOpen && NovelTests.Find<NovelRuleEditor>(scene.Host).Any(editor => editor.IsLoaded));
        scene.Field("pattern").Text = "["; scene.Field("replacement").Text = "已编辑";
        scene.Field("timeoutMillisecond").Text = "42";
        Click(scene.Host, "保存"); await editing.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!scene.Host.IsOpen && scene.Store.Writes == 1 && changes == 1,
            "Offline editing did not acknowledge the native write.");
        var edited = scene.Module.Library.Rules.Single();
        Check(edited is { Id: "edited", Pattern: "[", Replacement: "已编辑", IsRegex: true, TimeoutMillisecond: 42 },
            "The saved rule was rejected or its JavaScript expression was altered.");
        await scene.Settings.ImportRulesAsync(
            """{"rules":[{"id":"imported","pattern":"(?i)a","replacement":"导入","isRegex":true},{"id":"empty","pattern":""}]}""", default);
        Check(scene.Store.Writes == 2 && changes == 2 && scene.Module.Notice == "净化规则已导入" &&
            scene.Module.Library.Rules.Count == 3 && scene.Runtime.Calls == 0,
            "Import required the runtime, rejected JavaScript data, or failed to report the committed write.");
        var reopened = new NovelLibrary(scene.Store); await reopened.InitializeAsync();
        Check(reopened.Rules.Single(rule => rule.Id == "edited") == edited &&
            reopened.Rules.Single(rule => rule.Id == "imported").Pattern == "(?i)a" &&
            reopened.Rules.Single(rule => rule.Id == "empty").Pattern.Length == 0,
            "Reopening did not read the saved native rules verbatim.");
        await reopened.PrepareShutdownAsync();
    }
    private static async Task Failure()
    {
        await using var scene = new Scene(); await scene.Ready();
        await scene.Settings.ImportRulesAsync("""{"id":"saved","pattern":"原值"}""", default);
        var saved = scene.Module.Library.Rules.Single(); var bytes = scene.Store.Bytes!.ToArray();
        string notice = scene.Module.Notice; int changes = 0;
        scene.Module.Library.Changed += (_, _) => changes++;
        scene.Store.FailWrite = true;
        var editing = scene.Edit(saved);
        await Until(() => scene.Host.IsOpen && NovelTests.Find<NovelRuleEditor>(scene.Host).Any(editor => editor.IsLoaded));
        scene.Field("pattern").Text = "未保存";
        Click(scene.Host, "保存");
        await Until(() => NovelTests.Find<TextBlock>(scene.Host).Any(text => text.Text == "操作失败，请检查输入或重试"));
        Check(!editing.IsCompleted && scene.Host.IsOpen && scene.Field("pattern").Text == "未保存" &&
            scene.Module.Library.Rules.Single() == saved && scene.Store.Bytes!.SequenceEqual(bytes) && changes == 0,
            "A failed editor write closed the editor, lost input, or published an unsaved snapshot.");
        Click(scene.Host, "取消"); await editing.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await scene.Settings.ImportRulesAsync("""{"id":"saved","pattern":"失败导入"}""", default);
            throw new InvalidOperationException("A failed import was reported as saved.");
        }
        catch (IOException) { }
        Check(scene.Store.Writes == 3 && changes == 0 && scene.Module.Notice == notice &&
            scene.Module.Library.Rules.Single() == saved && scene.Store.Bytes!.SequenceEqual(bytes) &&
            scene.Runtime.Calls == 0, "A failed import published success or changed committed data.");
    }
    private static async Task Structure()
    {
        await using var scene = new Scene(); await scene.Ready();
        foreach (var rule in new[]
        {
            new NovelRule { Id = "", Pattern = "a" }, new NovelRule { Pattern = null! },
            new NovelRule { Pattern = "a", Name = null! }, new NovelRule { Pattern = "a", TimeoutMillisecond = 0 },
            new NovelRule { Pattern = "a", TimeoutMillisecond = -1 }
        })
        {
            try
            {
                await scene.Save(rule);
                throw new InvalidOperationException("Invalid native rule fields reached persistence.");
            }
            catch (InvalidDataException) { }
        }
        Check(scene.Store.Writes == 0 && scene.Module.Library.Rules.Count == 0,
            "Local rule validation wrote or published invalid data.");
        try
        {
            await scene.Settings.ImportRulesAsync("{", default);
            throw new InvalidOperationException("Malformed JSON reached persistence.");
        }
        catch (JsonException) { }
        await scene.Settings.ImportRulesAsync(
            """[{"id":"valid","pattern":"[","timeoutMillisecond":1},{"pattern":4},{"name":"缺少规则"},{"pattern":"a","isRegex":"true"},{"pattern":"a","order":"1"},{"pattern":"a","timeoutMillisecond":1.5},{"pattern":"a","timeoutMillisecond":0},{"id":"","pattern":"a"}]""", default);
        Check(scene.Store.Writes == 1 && scene.Module.Library.Rules.Single() is { Id: "valid", Pattern: "[", TimeoutMillisecond: 1 } &&
            scene.Module.Notice.Split('\n').Length == 7 && scene.Runtime.Calls == 0,
            "Import lost valid rule data or accepted invalid field types, required fields, or timeouts.");
    }
    private static void Click(DependencyObject root, string label) => NovelTests.Find<Button>(root)
        .Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Rule persistence operation did not finish.");
            await Task.Delay(10); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly NovelTests.MemoryStore Store = new();
        private readonly NovelTests.MemoryStore sources = new();
        internal readonly OfflineRuntime Runtime = new();
        internal readonly NovelCapability Module;
        internal NovelSettingsView Settings => (NovelSettingsView)Module.SettingsView;
        internal readonly PlayerDialogHost Host = new();
        private readonly Window window;
        internal Scene()
        {
            Module = new(new NovelSourceService(Runtime, sources), new NovelLibrary(Store), new NovelRuleProgram(Runtime));
            var root = new Grid(); root.Children.Add(Settings); root.Children.Add(Host);
            window = GeneralAppearanceTests.Window(root); NovelUi.Theme(window); Host.Attach(window);
        }
        internal async Task Ready()
        {
            await Module.Library.InitializeAsync(); await Module.Sources.RefreshAsync();
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Render);
        }
        internal TextBox Field(string key) => NovelTests.Find<TextBox>(Host).Single(input =>
            AutomationProperties.GetAutomationId(input) == "novel.rule.field." + key);
        internal Task Edit(NovelRule rule) => Invoke("EditRuleAsync", rule);
        internal Task Save(NovelRule rule) => Invoke("SaveRuleAsync", rule);
        private Task Invoke(string method, NovelRule rule) => (Task)typeof(NovelSettingsView)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Settings, [rule, CancellationToken.None])!;
        public async ValueTask DisposeAsync()
        {
            Host.Close(); window.Close(); await Module.DisposeAsync();
            await Store.DisposeAsync(); await sources.DisposeAsync();
        }
    }
    private sealed class OfflineRuntime : IRuntimeClient
    {
        internal int Calls;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            Calls++; return Task.FromException<JsonElement>(new BridgeException("CLOSED"));
        }
        public Task<JsonElement> UploadSourceAsync(string module, string id, long revision, ReadOnlyMemory<byte> source,
            string? basename = null, CancellationToken token = default) => throw new InvalidOperationException("Rules never upload source.");
    }
}

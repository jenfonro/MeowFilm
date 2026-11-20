using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using FontWeights = Microsoft.UI.Text.FontWeights;
using Color = global::Windows.UI.Color;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class NovelEditorTests
{
    private const string Definition = """{"bookSourceName":"原书源","bookSourceUrl":"https://source.invalid/","ruleSearch":{},"extra":{"keep":true}}""";
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("novel editor input: initial load reverted input and successful save reset", Input);
        yield return new("novel editor confirmation: cancel close dismiss and protocol use only yes or no",
            () => NovelTests.Sta(() => Confirmation(["cancel", "close", "dismiss", "protocol"])));
        yield return new("novel editor close request: save confirmation preserves only yes or no", () => NovelTests.Sta(() => Confirmation(["host-close"])));
        yield return new("novel editor save: failure retains input and cancelled accepted write drains before continuing", () => NovelTests.Sta(SaveFailure));
        yield return new("novel editor viewport: fixed footer optimized internal horizontal and vertical scrolling", () => NovelTests.Sta(Viewport));
        yield return new("novel settings navigation: shared areas direct new source and ordered protocol actions", () => NovelTests.Sta(Settings));
        yield return new("novel editor native save: offline protocol drafts preserve the original source and reopen", () => NovelTests.Sta(ProtocolSave));
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Task Input()
    {
        var draft = new NovelSourceDraft("legado", Definition);
        var name = new NovelSourceField("bookSourceName", "名称");
        Check(!draft.HasInput, "Initial definitions must not count as user input.");
        draft.Set(name, "原书源"); Check(!draft.HasInput, "An unchanged initial value marked the draft edited.");
        draft.Set(name, "修改"); draft.Set(name, "原书源");
        Check(draft.HasInput, "Reverting or clearing input must not suppress the save question.");
        draft.MarkSaved(draft.Definition()); Check(!draft.HasInput, "A successful save did not reset input tracking.");
        draft.Set(name, "新名称"); string saved = draft.Definition(); draft.MarkSaved(saved);
        Check(!draft.HasInput && draft.Text(name) == "新名称", "The saved definition was not made the editing baseline.");
        draft.Set(name, "再修改");
        Check(draft.HasInput && JsonNode.Parse(draft.Definition())!["extra"]!["keep"]!.GetValue<bool>(),
            "Later input lost its marker or an unrelated extension.");
        return Task.CompletedTask;
    }
    private static async Task Confirmation(string[] requests)
    {
        foreach (string request in requests)
        foreach (string answer in new[] { "是", "否" })
        {
            await using var scene = new Scene(); await scene.Ready();
            Check(!scene.Editor.Draft.HasInput, "Real control initialization counted as input.");
            scene.Name.Text = "修改后的书源";
            bool switched = false;
            Task? change = null;
            switch (request)
            {
                case "cancel": Click(scene.Surface, "取消"); break;
                case "close": NovelTestUi.Invoke(NovelTests.Find<Button>(scene.Surface).Single(button => AutomationProperties.GetAutomationId(button) == "novel.dialog.close")); break;
                case "dismiss": scene.Host.RequestClose(); break;
                case "host-close":
                    scene.Host.RequestClose();
                    break;
                case "protocol": change = scene.Editing.ContinueAsync(() => { scene.Editor.ChangeProtocol("qysg"); switched = true; }); break;
            }
            await Until(() => scene.Question is not null);
            await scene.Layout();
            var question = scene.Question!;
            Check(NovelTests.Find<TextBlock>(question).Any(text => text.Text == "当前更改未保存，是否保存"),
                "The save question changed its specified text.");
            Check(NovelTests.Find<Button>(question).Select(button => button.Content as string).SequenceEqual(["是", "否"]),
                "The save question must have exactly two choices.");
            Check(scene.Host.IsOpen && !scene.Dialog.IsCompleted && scene.Editor.Draft.HasInput &&
                NovelTests.Find<NovelSourceEditor>(scene.Host).Single() == scene.Editor,
                "Opening the question retired or replaced the editor.");
            Click(question, answer);
            if (change is not null)
            {
                await change;
                Check(switched && scene.Editor.Draft.Protocol == "qysg" && !scene.Editor.Draft.HasInput &&
                    scene.Editor.Draft.Text(new("bookSourceName", "名称")).Length == 0,
                    "Confirmed protocol changes must create a blank draft without converting the old definition.");
                Click(scene.Surface, "取消");
            }
            await scene.Dialog;
            Check(scene.Saves == (answer == "是" ? 1 : 0), request + ": the choice saved the wrong number of times.");
            if (answer == "是") Check(JsonNode.Parse(scene.Saved!)!["bookSourceName"]!.GetValue<string>() == "修改后的书源",
                "The save question did not save the current input before continuing.");
        }
        await using (var initial = new Scene())
        {
            await initial.Ready(); Click(initial.Surface, "取消"); await initial.Dialog;
            Check(initial.Saves == 0 && initial.Question is null, "An unedited loaded definition prompted or saved.");
        }
        await using (var saved = new Scene())
        {
            await saved.Ready(); saved.Name.Text = "保存后的书源"; Click(saved.Surface, "保存");
            await Until(() => saved.Saves == 1 && !saved.Editor.Draft.HasInput);
            Check(saved.Host.IsOpen, "Saving must retain the source editor.");
            Click(saved.Surface, "取消"); await saved.Dialog;
            Check(saved.Question is null && saved.Saves == 1, "Cancelling after saving asked or saved again.");
        }
    }
    private static async Task SaveFailure()
    {
        await using (var failed = new Scene { FailWrite = true })
        {
            await failed.Ready(); failed.Name.Text = "未保存的输入";
            bool switched = false;
            var change = failed.Editing.ContinueAsync(() => { failed.Editor.ChangeProtocol("qysg"); switched = true; });
            await Until(() => failed.Question is not null); Click(failed.Question!, "是"); await change;
            Check(!switched && failed.Editor.Draft.Protocol == "legado" && failed.Editor.Draft.HasInput &&
                failed.Name.Text == "未保存的输入" && failed.Host.IsOpen && failed.Saved is null,
                "A failed write discarded input, changed protocols, or reported a save.");
            Check(NovelTests.Find<TextBlock>(failed.Surface).Any(text => text.Text == "操作失败，请检查输入或重试"),
                "Save failure did not use the existing notification channel.");
            failed.FailWrite = false; Click(failed.Surface, "取消");
            await Until(() => failed.Question is not null); Click(failed.Question!, "是"); await failed.Dialog;
            Check(failed.Saves == 1, "Retry did not save the retained input.");
        }
        await using var held = new Scene { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await held.Ready(); held.Name.Text = "已接纳的输入";
        bool continued = false;
        var pendingChange = held.Editing.ContinueAsync(() => continued = true);
        await Until(() => held.Question is not null); Click(held.Question!, "是");
        await held.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        held.Cancellation.Cancel(); await Until(() => !held.Host.IsOpen);
        Check(!held.Dialog.IsCompleted && !pendingChange.IsCompleted, "Cancellation acknowledged before the accepted write drained.");
        held.Hold!.TrySetResult(); await pendingChange; await held.Dialog;
        Check(!continued && held.Saves == 1, "A late accepted save continued a cancelled protocol change.");
    }
    private static async Task Viewport()
    {
        await using var scene = new Scene(); scene.Window.Resize(500, 640);
        await scene.Ready();
        var tabs = NovelTests.Find<TabView>(scene.Editor).Single();
        Check(!tabs.IsAddTabButtonVisible && !tabs.CanReorderTabs && tabs.TabItems.Count >= 6,
            "The source field groups must remain fixed native tabs.");
        Check(tabs.TabItems.OfType<TabViewItem>().All(tab => tab.FontWeight == Microsoft.UI.Text.FontWeights.Normal &&
            tab.FontStyle == global::Windows.UI.Text.FontStyle.Normal), "Source field group labels must retain normal system typography.");
        tabs.SelectedIndex = tabs.TabItems.Count - 1; await scene.Layout();
        Check(((TabViewItem)tabs.SelectedItem).IsSelected && ((TabViewItem)tabs.SelectedItem).IsLoaded,
            "The final field group must be reachable in a narrow window.");
        tabs.SelectedIndex = 0; await scene.Layout();
        Check(!NovelTests.Find<ScrollViewer>(scene.Surface).Any(scroll =>
            scroll.Content is DependencyObject content && DesktopVisual.Contains(content, scene.Editor)),
            "The editor must not acquire a scrolling outer wrapper.");
        var footer = NovelTests.Find<Border>(scene.Surface).Single(border => AutomationProperties.GetAutomationId(border) == "novel.dialog.footer");
        double before = NovelTestUi.Bounds(footer, scene.Surface).Y;
        Check(footer.BorderThickness.Top == 1 && ReferenceEquals(footer.BorderBrush, NovelTestUi.Resource(scene.Surface, "MeowFilm.Brush.Border")),
            "The fixed action area lost its themed separator.");
        var basic = (ScrollViewer)((TabViewItem)tabs.TabItems[0]).Content;
        Check(basic.ScrollableHeight > 0 && basic.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "All fields must contribute to the form's scroll extent.");
        var comment = NovelTests.Find<TextBox>(scene.Editor).Single(input => AutomationProperties.GetAutomationId(input) == "novel.source.field.bookSourceComment");
        comment.Text = string.Join('\n', Enumerable.Repeat("长内容", 50)); await scene.Layout();
        var inner = NovelTests.Find<ScrollViewer>(comment).Single();
        inner.ChangeView(null, inner.ScrollableHeight, null, true); await scene.Layout();
        Check(inner.VerticalOffset > 0, "Long field input must remain reachable.");
        basic.ChangeView(null, basic.ScrollableHeight, null, true); await scene.Layout();
        Check(basic.VerticalOffset > 0, "The form must scroll to its final fields.");
        var last = NovelTests.Find<TextBox>(scene.Editor).Single(input => AutomationProperties.GetAutomationId(input) == "novel.source.field.jsLib");
        Check(NovelTestUi.Bounds(last, basic).Bottom <= basic.ActualHeight + 1,
            "The final source field must remain reachable without moving the footer.");
        Check(Math.Abs(before - NovelTestUi.Bounds(footer, scene.Surface).Y) < 1 &&
            scene.Surface.ActualHeight <= scene.Host.ActualHeight - 31,
            "Internal scrolling moved the footer or overflowed the host.");
        NovelTestUi.CheckContrastResources(scene.Surface);
        await NovelTestUi.Preview(scene.Surface, "reader-page-source-editor", (int)Math.Ceiling(scene.Surface.ActualWidth), (int)Math.Ceiling(scene.Surface.ActualHeight));
        scene.Host.Close(); await scene.Dialog;
        var list = new ManagementList { Height = 280 }; scene.Owner.Children.Add(list);
        var rows = Enumerable.Range(0, 100).Select(index => new ManagementRow(index.ToString(), "书源 " + index, "阅读源", true, () => { }, _ => { }, new MenuFlyout())).ToArray();
        list.SetRows(rows, rows.Length, rows.Select(row => row.Id), "无书源"); await scene.Layout();
        var native = NovelTests.Find<ListView>(list).Single();
        Check(ReferenceEquals(native.Style, NovelTestUi.Resource(list, "MeowFilm.ManagementList")) &&
            ReferenceEquals(native.ItemContainerStyle, NovelTestUi.Resource(list, "MeowFilm.ManagementListItem")),
            "The native management list lost its theme.");
        Check(NovelTests.Find<ManagementRowView>(list).Count() < rows.Length,
            "The native management list must retain viewport virtualization.");
    }
    private static async Task Settings()
    {
        await using var store = new NovelTests.MemoryStore();
        var runtime = new NovelTests.Runtime();
        await using var module = new NovelCapability(new NovelSourceService(runtime, store), new NovelLibrary(store), new NovelRuleProgram(runtime));
        var settings = (NovelSettingsView)module.SettingsView;
        var root = new Grid(); root.Children.Add(settings);
        var dialogs = new PlayerDialogHost(); root.Children.Add(dialogs);
        var window = NovelTestUi.Window(root); NovelUi.Theme(window.Root); dialogs.Attach(window); window.Activate();
        try
        {
            await DesktopTestUi.DrainAsync();
            var areas = NovelTests.Find<RadioButton>(settings).Where(area => area.GroupName == "novel-settings-area").ToArray();
            Check(areas.Select(area => area.Content as string).SequenceEqual(["书源管理", "替换净化"]) &&
                areas.All(area => ReferenceEquals(area.Style, NovelTestUi.Resource(area, "MeowFilm.SettingsArea"))),
                "Settings areas do not reuse the shared navigation.");
            Check(!NovelTests.Find<Button>(settings).Any(button => Equals(button.Content, "取消操作")), "The ambiguous settings cancel operation remains.");
            var add = NovelTests.Find<Button>(settings).Single(button => AutomationProperties.GetName(button) == "添加书源");
            Check(Equals(add.Content, "\uE710") && add.ContextFlyout is MenuFlyout menu && menu.Items.OfType<MenuFlyoutItem>().Count(item => item.Text == "新建书源") == 1 && !menu.Items.OfType<MenuFlyoutSubItem>().Any(item => item.Text == "新建书源"),
                "New sources still require a protocol submenu.");
            var edit = settings.EditSourceAsync(null, "legado", default);
            await Until(() => dialogs.IsOpen);
            await DesktopTestUi.DrainAsync();
            var type = NovelTests.Find<ComboBox>(dialogs).Single(input => AutomationProperties.GetAutomationId(input) == "novel.source.protocol");
            var tools = (StackPanel)type.Parent;
            Check(tools.Children.Cast<FrameworkElement>().Select(AutomationProperties.GetName)
                .SequenceEqual(["书源协议", "更多书源操作", "导入书源", "导出书源", "关闭"]),
                "Source editor title actions are not in the requested order.");
            var editor = NovelTests.Find<NovelSourceEditor>(dialogs).Single();
            Check(NovelTests.Find<ComboBox>(editor).Single().SelectedIndex == 0, "Content type was not retained in basic information.");
            type.SelectedValue = "qysg"; await Until(() => editor.Draft.Protocol == "qysg");
            Check(!editor.Draft.HasInput, "Selecting a blank protocol marked its initial controls edited.");
            Click(dialogs, "取消"); await edit;
            Check(!runtime.Calls.Any(call => call.Method is "source.runtime.library.save" or "source.runtime.library.import"),
                "Opening or switching a blank editor performed an installation.");
        }
        finally { dialogs.Close(); window.Close(); }
    }
    private static async Task ProtocolSave()
    {
        foreach (string answer in new[] { "是", "否" })
        {
            var runtime = new OfflineRuntime();
            await using var sourceStore = new NovelSourceMemoryStore();
            await using var libraryStore = new NovelTests.MemoryStore();
            var service = new NovelSourceService(runtime, sourceStore);
            await using var module = new NovelCapability(service, new NovelLibrary(libraryStore), new NovelRuleProgram(runtime));
            await service.ImportAsync(text: Definition);
            var original = service.Sources.Single();
            var settings = (NovelSettingsView)module.SettingsView;
            var root = new Grid(); root.Children.Add(settings);
            var host = new PlayerDialogHost(); root.Children.Add(host);
            var window = NovelTestUi.Window(root); NovelUi.Theme(window.Root); host.Attach(window); window.Activate();
            try
            {
                await DesktopTestUi.DrainAsync();
                var edit = settings.EditSourceAsync(original, "legado", default);
                await Until(() => host.IsOpen && NovelTests.Find<NovelSourceEditor>(host).Any(editor => editor.IsLoaded));
                var editor = NovelTests.Find<NovelSourceEditor>(host).Single();
                TextBox Field(string key) => NovelTests.Find<TextBox>(editor).Single(input =>
                    AutomationProperties.GetAutomationId(input) == "novel.source.field." + key);
                Field("bookSourceName").Text = "原协议修改";
                var protocol = NovelTests.Find<ComboBox>(host).Single(input =>
                    AutomationProperties.GetAutomationId(input) == "novel.source.protocol");
                protocol.SelectedValue = "qysg";
                await Until(() => NovelTests.Find<Border>(host).Any(border => AutomationProperties.GetName(border) == "保存更改"));
                var question = NovelTests.Find<Border>(host).Single(border => AutomationProperties.GetName(border) == "保存更改");
                Click(question, answer); await Until(() => editor.Draft.Protocol == "qysg");
                Check(service.Sources.Single().InstallationId == original.InstallationId &&
                    service.Sources.Single().Name == (answer == "是" ? "原协议修改" : "原书源") &&
                    !editor.Draft.HasInput && Field("bookSourceName").Text.Length == 0,
                    "The protocol guard lost the original source or converted its fields.");
                Field("bookSourceName").Text = "独立新协议";
                Field("bookSourceUrl").Text = "https://new-source.invalid/";
                var tabs = NovelTests.Find<TabView>(editor).Single(); tabs.SelectedIndex = 1;
                await DesktopTestUi.DrainAsync();
                Field("html").Text = "<html><body>新定义</body></html>";
                Click(host, "保存");
                await Until(() => service.Sources.Count == 2 && !editor.Draft.HasInput);
                Check(host.IsOpen && service.Sources.Any(source => source.InstallationId == original.InstallationId) &&
                    service.Sources.Any(source => source.InstallationId != original.InstallationId &&
                        source.Protocol == "qysg" && source.Name == "独立新协议"),
                    "Saving a new protocol replaced the original source or failed while the runtime was unavailable.");
                Click(host, "取消"); await edit;
                var reopened = new NovelSourceService(runtime, sourceStore);
                try
                {
                    await reopened.RefreshAsync();
                    Check(reopened.Sources.Count == 2 && reopened.Sources.Any(source => source.Name == "独立新协议"),
                        "A saved protocol draft was not durable.");
                }
                finally { await reopened.PrepareShutdownAsync(); }
                Check(runtime.Calls.All(method => method == "source.runtime.library.apply"),
                    "Configuration editing used a runtime-owned write path.");
            }
            finally { host.Close(); window.Close(); }
        }
    }
    private static void Click(DependencyObject root, string label) =>
        NovelTestUi.Invoke(NovelTests.Find<Button>(root).Single(button => Equals(button.Content, label)));
    private static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Novel editor state did not complete.");
            await DesktopTestUi.DrainAsync();
        }
    }
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly StackPanel Owner = new() { IsTabStop = true };
        internal readonly PlayerDialogHost Host = new();
        internal readonly NovelSourceEditor Editor = new("legado", Definition);
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource SaveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Window Window;
        internal readonly NovelDialogEditing Editing;
        internal readonly Task<bool> Dialog;
        internal bool FailWrite;
        internal TaskCompletionSource? Hold;
        internal int Saves;
        internal string? Saved;
        internal Scene()
        {
            var root = new Grid(); root.Children.Add(Owner); root.Children.Add(Host);
            Window = NovelTestUi.Window(root); NovelUi.Theme(DesktopTestUi.WindowRoot(Window)); Host.Attach(Window); Window.Activate();
            Editing = new(new StackPanel { Orientation = Orientation.Horizontal }, () => Editor.Draft.HasInput);
            Dialog = OpenAsync();
        }
        private async Task<bool> OpenAsync()
        {
            await NovelTestUi.LoadedAsync(Owner);
            return await NovelUi.DialogAsync(Owner, "编辑书源", Editor, "保存", Save, Cancellation.Token, applicationOverlay: true, editing: Editing);
        }
        internal Border Surface => NovelTests.Find<Border>(Host).Single(border => AutomationProperties.GetName(border) == "编辑书源");
        internal Border? Question => NovelTests.Find<Border>(Host).SingleOrDefault(border => AutomationProperties.GetName(border) == "保存更改");
        internal TextBox Name => NovelTests.Find<TextBox>(Editor).Single(input => AutomationProperties.GetAutomationId(input) == "novel.source.field.bookSourceName");
        internal async Task Ready() { await Until(() => Host.IsOpen && Editor.IsLoaded); await Layout(); }
        internal async Task Layout() { DesktopTestUi.WindowRoot(Window).UpdateLayout(); await DesktopTestUi.DrainAsync(); }
        private async Task<bool> Save(CancellationToken token)
        {
            SaveStarted.TrySetResult();
            if (Hold is not null) await Hold.Task;
            if (FailWrite) throw new IOException("Synthetic write failure.");
            // Model an already accepted local write: cancellation cannot undo its receipt.
            string text = Editor.Draft.Definition(); Saved = text; Editor.Draft.MarkSaved(text); Saves++; return false;
        }
        public async ValueTask DisposeAsync()
        {
            Hold?.TrySetResult(); Cancellation.Cancel(); Host.Close(); Window.Close();
            await Dialog.WaitAsync(TimeSpan.FromSeconds(5)); Cancellation.Dispose();
        }
    }
    private sealed class OfflineRuntime : IRuntimeClient
    {
        internal readonly List<string> Calls = [];
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken token = default)
        {
            Calls.Add(method); return Task.FromException<JsonElement>(new BridgeException("CLOSED"));
        }
        public Task<JsonElement> UploadSourceAsync(string module, string id, long revision, ReadOnlyMemory<byte> source,
            string? basename = null, CancellationToken token = default) => throw new InvalidOperationException();
    }
}

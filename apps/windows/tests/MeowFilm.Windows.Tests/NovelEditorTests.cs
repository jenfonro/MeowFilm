using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
        yield return new("novel editor confirmation: cancel close dismiss Escape and protocol use only yes or no", () => NovelTests.Sta(Confirmation));
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
    private static async Task Confirmation()
    {
        foreach (string request in new[] { "cancel", "close", "dismiss", "escape", "protocol" })
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
                case "close": NovelTests.Find<Button>(scene.Surface).Single(button => AutomationProperties.GetAutomationId(button) == "novel.dialog.close").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
                case "dismiss": scene.Host.RequestClose(); break;
                case "escape":
                    scene.Host.HandleEscape(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(scene.Window), 0, Key.Escape));
                    break;
                case "protocol": change = scene.Editing.ContinueAsync(() => { scene.Editor.ChangeProtocol("qysg"); switched = true; }); break;
            }
            await Until(() => scene.Question is not null);
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
        await using var scene = new Scene(); scene.Window.Width = 500; scene.Window.Height = 640;
        DesktopTheme.SetIsHighContrast(scene.Window, false);
        await scene.Ready();
        var tabs = NovelTests.Find<TabControl>(scene.Editor).Single();
        var header = (ScrollViewer)tabs.Template.FindName("HeaderScroll", tabs);
        Check(header.Style == tabs.FindResource("MeowFilm.SettingsScrollViewer") && header.ScrollableWidth > 0,
            "Overflowing tabs did not retain the existing horizontal scroll style.");
        var horizontal = (ScrollBar)header.Template.FindName("PART_HorizontalScrollBar", header);
        Check(horizontal.Orientation == Orientation.Horizontal && horizontal.Style == header.FindResource("MeowFilm.SettingsScrollBar"),
            "The tab bar used a default or vertical-only scrollbar.");
        header.ScrollToRightEnd(); await scene.Layout();
        Check(header.HorizontalOffset > 0, "The final tabs cannot be reached by horizontal scrolling.");
        Check(!NovelTests.Find<ScrollViewer>(scene.Surface).Any(scroll => ReferenceEquals(scroll.Content, scene.Editor)),
            "The editor still has a scrolling outer wrapper.");
        var footer = NovelTests.Find<Border>(scene.Surface).Single(border => AutomationProperties.GetAutomationId(border) == "novel.dialog.footer");
        double before = footer.TransformToAncestor(scene.Surface).Transform(new Point()).Y;
        Check(footer.BorderThickness.Top == 1 && footer.BorderBrush == scene.Surface.FindResource("MeowFilm.Brush.Border"),
            "The fixed action area lost its themed top separator.");
        var basic = (ScrollViewer)((TabItem)tabs.Items[0]).Content;
        var vertical = (ScrollBar)basic.Template.FindName("PART_VerticalScrollBar", basic);
        var track = (Track)vertical.Template.FindName("PART_Track", vertical);
        Check(OverlayScrollChrome.GetEnabled(basic) && vertical.Orientation == Orientation.Vertical &&
            track.Orientation == Orientation.Vertical && vertical.Opacity == 0,
            "The selected form lost its optimized vertical scrollbar or idle visibility.");
        Check(basic.ScrollableHeight > 0,
            $"The selected form cannot scroll: editor={scene.Editor.ActualHeight}, tabs={tabs.ActualHeight}, " +
            $"form={basic.ActualHeight}, extent={basic.ExtentHeight}, viewport={basic.ViewportHeight}, " +
            $"surface={scene.Surface.ActualHeight}, host={scene.Host.ActualHeight}.");
        var comment = NovelTests.Find<TextBox>(scene.Editor).Single(input => AutomationProperties.GetAutomationId(input) == "novel.source.field.bookSourceComment");
        comment.Text = string.Join('\n', Enumerable.Repeat("长内容", 50)); await scene.Layout();
        var inner = (ScrollViewer)comment.Template.FindName("PART_ContentHost", comment);
        Check(inner.Style == comment.FindResource("MeowFilm.SettingsScrollViewer"), "The multiline input retained a default scrollbar.");
        comment.ScrollToEnd(); await scene.Layout();
        Check(comment.VerticalOffset > 0, "Long field input is not reachable.");
        basic.ScrollToEnd(); await scene.Layout();
        Check(basic.VerticalOffset > 0 && Math.Abs(vertical.Value - basic.VerticalOffset) < 1 &&
            Math.Abs(vertical.Maximum - basic.ScrollableHeight) < 1 &&
            Math.Abs(vertical.ViewportSize - basic.ViewportHeight) < 1 &&
            !OverlayScrollChrome.GetActive(basic) && vertical.Opacity == 0,
            "The form scrollbar lost its viewport bindings or programmatic scrolling activated idle chrome.");
        var last = NovelTests.Find<TextBox>(scene.Editor).Single(input => AutomationProperties.GetAutomationId(input) == "novel.source.field.jsLib");
        Check(last.TransformToAncestor(basic).TransformBounds(new Rect(last.RenderSize)).Bottom <= basic.ActualHeight + 1,
            "Removing outer scroll made the last field unreachable.");
        Check(Math.Abs(before - footer.TransformToAncestor(scene.Surface).Transform(new Point()).Y) < 1 &&
            scene.Surface.ActualHeight <= scene.Host.ActualHeight - 31,
            "Internal scrolling moved the fixed footer or overflowed the host.");
        DesktopTheme.SetIsHighContrast(scene.Window, true); await scene.Layout();
        Check(scene.Host.Background == SystemColors.WindowBrush && footer.BorderBrush == SystemColors.WindowTextBrush &&
            vertical.Opacity == 1 && !vertical.HasAnimatedProperties &&
            NovelTests.Find<Border>(track.Thumb).Any(border => border.Background is SolidColorBrush brush &&
                brush.Color == SystemColors.WindowTextColor),
            "The shared scrim, footer or form scrollbar lost high-contrast semantics.");
        DesktopTheme.SetIsHighContrast(scene.Window, false); await scene.Layout();
        GeneralAppearanceTests.SavePreview(scene.Surface, "reader-page-source-editor",
            (int)Math.Ceiling(scene.Surface.ActualWidth), (int)Math.Ceiling(scene.Surface.ActualHeight));
        scene.Host.Close(); await scene.Dialog;
        var list = new ManagementList { Height = 280 }; scene.Owner.Children.Add(list);
        var rows = Enumerable.Range(0, 100).Select(index => new ManagementRow(index.ToString(), "书源 " + index, "阅读源", true, () => { }, _ => { }, new ContextMenu())).ToArray();
        list.SetRows(rows, rows.Length, rows.Select(row => row.Id), "无书源"); await scene.Layout();
        var native = NovelTests.Find<ListBox>(list).Single();
        Check(native.Style == list.FindResource("MeowFilm.ManagementList") &&
            native.ItemContainerStyle == list.FindResource("MeowFilm.ManagementListItem") &&
            ((Grid)native.Parent).Clip is RectangleGeometry { RadiusX: 14, RadiusY: 14 },
            "Management lists lost their themed rounded content boundary.");
        Check((bool)native.GetValue(VirtualizingPanel.IsVirtualizingProperty) &&
            NovelTests.Find<ManagementRowView>(list).Count() < rows.Length,
            "Optimized list scroll disabled virtualization.");
    }
    private static async Task Settings()
    {
        await using var store = new NovelTests.MemoryStore();
        var runtime = new NovelTests.Runtime();
        await using var module = new NovelCapability(new NovelSourceService(runtime, store), new NovelLibrary(store), new NovelRuleProgram(runtime));
        var settings = (NovelSettingsView)module.SettingsView;
        var root = new Grid(); root.Children.Add(settings);
        var dialogs = new PlayerDialogHost(); root.Children.Add(dialogs);
        var window = GeneralAppearanceTests.Window(root); NovelUi.Theme(window); dialogs.Attach(window); window.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            var areas = NovelTests.Find<RadioButton>(settings).Where(area => area.GroupName == "novel-settings-area").ToArray();
            Check(areas.Select(area => area.Content as string).SequenceEqual(["书源管理", "替换净化"]) &&
                areas.All(area => area.Style == area.FindResource("MeowFilm.SettingsArea")),
                "Settings areas do not reuse the shared navigation.");
            Check(!NovelTests.Find<Button>(settings).Any(button => Equals(button.Content, "取消操作")), "The ambiguous settings cancel operation remains.");
            var add = NovelTests.Find<Button>(settings).Single(button => AutomationProperties.GetName(button) == "添加书源");
            Check(Equals(add.Content, "\uE710") && add.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "新建书源")).Items.Count == 0,
                "New sources still require a protocol submenu.");
            var edit = settings.EditSourceAsync(null, "legado", default);
            await Until(() => dialogs.IsOpen);
            await Dispatcher.Yield(DispatcherPriority.Render);
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
            var window = GeneralAppearanceTests.Window(root); NovelUi.Theme(window); host.Attach(window); window.Show();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Render);
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
                var tabs = NovelTests.Find<TabControl>(editor).Single(); tabs.SelectedIndex = 1;
                await Dispatcher.Yield(DispatcherPriority.Render);
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
        NovelTests.Find<Button>(root).Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Novel editor state did not complete.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly StackPanel Owner = new() { Focusable = true };
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
            Window = GeneralAppearanceTests.Window(root); NovelUi.Theme(Window); Host.Attach(Window); Window.Show();
            Editing = new(new StackPanel { Orientation = Orientation.Horizontal }, () => Editor.Draft.HasInput);
            Dialog = NovelUi.DialogAsync(Owner, "编辑书源", Editor, "保存", Save, Cancellation.Token, applicationOverlay: true, editing: Editing);
        }
        internal Border Surface => NovelTests.Find<Border>(Host).Single(border => AutomationProperties.GetName(border) == "编辑书源");
        internal Border? Question => NovelTests.Find<Border>(Host).SingleOrDefault(border => AutomationProperties.GetName(border) == "保存更改");
        internal TextBox Name => NovelTests.Find<TextBox>(Editor).Single(input => AutomationProperties.GetAutomationId(input) == "novel.source.field.bookSourceName");
        internal async Task Ready() { await Until(() => Host.IsOpen && Editor.IsLoaded); await Layout(); }
        internal async Task Layout() { Window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render); }
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

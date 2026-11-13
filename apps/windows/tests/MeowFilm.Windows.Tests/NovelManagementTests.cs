using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class NovelManagementTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("novel management form: three protocols nested mapping and unedited JSON types", Drafts);
        yield return new("novel management import: external rule identity nullable fields and tag-only group changes", Imports);
        yield return new("novel management theme: inputs and titles follow resources and window high contrast", () => NovelTests.Sta(ThemeResources));
        yield return new("novel management components: visible selection inverse native tabs fields and previews", () => NovelTests.Sta(Components));
        yield return new("novel management QR: local image decodes original rule text and rejects invalid images", () => NovelTests.Sta(Qr));
        yield return new("novel management save: accepted revisions remain editable without another list request", () => NovelTests.Sta(SavedRevision));
    }
    private static void Check(bool condition, string message = "Novel management assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    private static async Task SavedRevision()
    {
        var runtime = new NovelTests.Runtime(); var sources = new NovelSourceService(runtime); await sources.RefreshAsync();
        var original = sources.Sources.Single(); runtime.Listing = _ => throw new IOException("list unavailable");
        try
        {
            var first = await sources.SaveAsync(original, "{}");
            Check(first.GetProperty("source").GetProperty("revision").GetInt32() == original.Revision + 1);
            Check(sources.Sources.Single().Revision == original.Revision + 1);
            await sources.SaveAsync(sources.Sources.Single(), "{}");
            Check(sources.Sources.Single().Revision == original.Revision + 2);
            Check(runtime.Calls.Count(c => c.Method.EndsWith("library.list", StringComparison.Ordinal)) == 1);
        }
        finally { await sources.PrepareShutdownAsync(); }
    }
    private static Task Drafts()
    {
        var draft = new NovelSourceDraft("legado", """{"bookSourceName":"书源","bookSourceUrl":"https://source.invalid/","ruleSearch":"{\"bookList\":\".book\",\"extension.value\":[1,false]}","ruleBookInfo":{"canReName":true},"extension.value":{"keep":null},"enabled":false}""");
        Check(draft.Tabs.Select(t => t.Title).SequenceEqual(new[] { "基本", "搜索", "发现", "详情", "目录", "正文" })); Check(draft.Tabs.Sum(t => t.Fields.Length) == 64);
        Check(JsonNode.Parse(draft.Definition())!["ruleSearch"]!.GetValueKind() == System.Text.Json.JsonValueKind.String);
        draft.Set(new("ruleSearch.bookList", "列表"), ".changed"); draft.Set(new("ruleToc.updateTime", "更新"), "time@text");
        draft.Set(draft.ExtensionFields.Single(f => f.Parts.SequenceEqual(new[] { "extension.value" })), """{"keep":null,"new":false}""");
        var result = JsonNode.Parse(draft.Definition())!;
        Check(result["ruleSearch"]!["bookList"]!.GetValue<string>() == ".changed"); Check(JsonNode.DeepEquals(result["ruleSearch"]!["extension.value"], JsonNode.Parse("[1,false]")));
        Check(result["ruleToc"]!["updateTime"]!.GetValue<string>() == "time@text"); Check(result["ruleBookInfo"]!["canReName"]!.GetValue<bool>());
        Check(!result["extension.value"]!["new"]!.GetValue<bool>() && !result["enabled"]!.GetValue<bool>());
        foreach (string protocol in new[] { "legado", "yiciyuan", "qysg" })
        {
            var item = new NovelSourceDraft(protocol); item.Set(new("bookSourceName", "名称"), "测试"); item.Set(new("bookSourceUrl", "地址"), "https://new.invalid/");
            item.Set(new("bookSourceType", "类型", Kind: NovelSourceFieldKind.Number), "2"); var value = JsonNode.Parse(item.Definition())!.AsObject();
            Check(value["bookSourceType"]!.GetValue<double>() == 2); Check(value.ContainsKey(protocol switch { "legado" => "ruleSearch", "yiciyuan" => "ruleSearchList", _ => "html" }));
            Check(new[] { "ruleSearch", "ruleSearchList", "html" }.Count(value.ContainsKey) == 1, "Saving must not change protocols");
        }
        var yy = new NovelSourceDraft("yiciyuan", """{"bookSourceName":"异次元","bookSourceUrl":"https://yy.invalid/","ruleSearchList":".book","enabled":true,"enable":true,"customOrder":9,"serialNumber":9,"unknown":[null,3]}""");
        yy.SetBool(yy.EnabledKey, false); yy.Set(new("serialNumber", "排序", Kind: NovelSourceFieldKind.Number), "-2");
        result = JsonNode.Parse(yy.Definition())!; Check(!result["enabled"]!.GetValue<bool>() && !result["enable"]!.GetValue<bool>());
        Check(result["customOrder"]!.GetValue<double>() == -2 && result["serialNumber"]!.GetValue<double>() == -2); Check(JsonNode.DeepEquals(result["unknown"], JsonNode.Parse("[null,3]")));
        return Task.CompletedTask;
    }
    private static Task Imports()
    {
        var rules = NovelRuleImport.Parse("""[{"id":42,"name":"","group":null,"pattern":"广告","replacement":"","scope":null,"excludeScope":null},{"id":43,"regex":"literal[","enable":false,"sortOrder":-2}]""");
        Check(rules.Select(r => r.Id).SequenceEqual(new[] { "42", "43" })); Check(rules[0].Name == "广告" && rules[0].IsRegex && rules[0].Group == "");
        Check(!rules[1].IsRegex && !rules[1].Enabled && rules[1].Order == -2);
        Check(NovelGroups.Split(" 甲，乙;丙；甲\n丁 ").ToHashSet().SetEquals(["甲", "乙", "丙", "丁"]));
        Check(NovelGroups.Split(NovelGroups.Replacing("甲,乙", "甲", "丙")).ToHashSet().SetEquals(["乙", "丙"])); Check(NovelGroups.Replacing("甲,乙", "甲", null) == "乙");
        return Task.CompletedTask;
    }
    private static async Task ThemeResources()
    {
        var root = new StackPanel(); NovelUi.Theme(root);
        var form = new ManagementForm(); var plain = form.Field("普通字段", "保留输入");
        var boxed = new ManagementForm(true); var input = boxed.Field("浮窗字段", "保留输入");
        root.Children.Add(form.Card()); root.Children.Add(boxed.Card());
        var preview = new ManagementImportSelection([new("导入标题", "导入说明", "新增"), new("已存在标题", "已存在说明", "已存在")]);
        root.Children.Add(preview);
        var list = new ManagementList { Height = 300 }; root.Children.Add(list);
        var host = GeneralAppearanceTests.Window(root); DesktopTheme.SetIsHighContrast(host, false);
        try
        {
            host.Show();
            list.SetRows([new("1", "书源标题", "列表说明", true, () => { }, _ => { }, new ContextMenu()),
                new("2", "失效书源", "失败说明", true, () => { }, _ => { }, new ContextMenu(), Failed: true)],
                2, ["1", "2"], "无书源");
            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            var titles = new[] { NovelTests.Find<TextBlock>(list).Single(text => text.Text == "书源标题"),
                NovelTests.Find<TextBlock>(list).Single(text => text.Text == "失效书源"),
                NovelTests.Find<TextBlock>(preview).Single(text => text.Text == "导入标题") };
            var labels = new[] { NovelTests.Find<TextBlock>(form).Single(text => text.Text == "普通字段"),
                NovelTests.Find<TextBlock>(boxed).Single(text => text.Text == "浮窗字段"),
                NovelTests.Find<TextBlock>(list).Single(text => text.Text == "列表说明"),
                NovelTests.Find<TextBlock>(preview).Single(text => text.Text == "导入说明") };
            var failure = NovelTests.Find<TextBlock>(list).Single(text => text.Text == "失败说明");
            var newStatus = NovelTests.Find<TextBlock>(preview).Single(text => text.Text == "新增");
            var existingStatus = NovelTests.Find<TextBlock>(preview).Single(text => text.Text == "已存在");
            var newBadge = newStatus.Parent as Border ?? throw new InvalidOperationException("New import status badge is missing.");
            var existingBadge = existingStatus.Parent as Border ?? throw new InvalidOperationException("Existing import status badge is missing.");
            Foreground(false);
            root.Resources["MeowFilm.Brush.Text"] = new SolidColorBrush(Color.FromRgb(30, 40, 50));
            root.Resources["MeowFilm.Brush.Muted"] = new SolidColorBrush(Color.FromRgb(80, 90, 100));
            root.Resources["MeowFilm.Brush.AccentText"] = new SolidColorBrush(Color.FromRgb(100, 110, 120));
            root.Resources["MeowFilm.Brush.AccentSoft"] = new SolidColorBrush(Color.FromArgb(40, 100, 110, 120));
            root.Resources["MeowFilm.Brush.InputSurface"] = new SolidColorBrush(Color.FromRgb(130, 140, 150));
            root.Resources["MeowFilm.Brush.Accent"] = new SolidColorBrush(Color.FromRgb(70, 100, 150));
            root.Resources["MeowFilm.Brush.Focus"] = new SolidColorBrush(Color.FromRgb(80, 110, 160));
            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            Foreground(false);
            DesktopTheme.SetIsHighContrast(host, true); host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            Foreground(true);
            DesktopTheme.SetIsHighContrast(host, false); host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            Foreground(false);
            Check(plain.Text == "保留输入" && input.Text == "保留输入", "Theme changes must preserve field values.");
            Check(input.Focus(), "The management input could not receive focus.");
            await Dispatcher.Yield(DispatcherPriority.Render);
            Check(input.BorderBrush == (Brush)root.FindResource("MeowFilm.Brush.Focus"),
                "Management input focus must follow the application focus resource.");
            DesktopTheme.SetIsHighContrast(host, true); host.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);
            Check(input.BorderBrush == SystemColors.HighlightBrush,
                "Focused management input must use the system highlight in high contrast.");

            void Foreground(bool contrast)
            {
                var text = contrast ? SystemColors.WindowTextBrush : (Brush)root.FindResource("MeowFilm.Brush.Text");
                var muted = contrast ? SystemColors.WindowTextBrush : (Brush)root.FindResource("MeowFilm.Brush.Muted");
                var error = contrast ? SystemColors.WindowTextBrush : (Brush)root.FindResource("MeowFilm.Brush.Error");
                Check(plain.Foreground == text && input.Foreground == text && titles.All(title => title.Foreground == text),
                    "Management inputs and titles must follow theme resources or the window's high-contrast foreground.");
                Check(labels.All(label => label.Foreground == muted) && failure.Foreground == error,
                    "Management labels and details must preserve theme semantics and the system high-contrast foreground.");
                var accent = contrast ? SystemColors.WindowTextBrush : (Brush)root.FindResource("MeowFilm.Brush.AccentText");
                var existing = contrast ? SystemColors.WindowTextBrush : (Brush)root.FindResource("MeowFilm.Brush.Muted");
                var badgeBackground = contrast ? SystemColors.WindowBrush : (Brush)root.FindResource("MeowFilm.Brush.AccentSoft");
                var existingBackground = contrast ? SystemColors.WindowBrush : (Brush)root.FindResource("MeowFilm.Brush.InputSurface");
                var inputBackground = contrast ? SystemColors.WindowBrush : (Brush)root.FindResource("MeowFilm.Brush.InputSurface");
                var inputBorder = contrast ? SystemColors.WindowTextBrush : (Brush)root.FindResource("MeowFilm.Brush.Border");
                Check(newStatus.Foreground == accent && existingStatus.Foreground == existing,
                    "Import status labels must preserve semantic foreground resources and use the system foreground in high contrast.");
                Check(newBadge.Background == badgeBackground && existingBadge.Background == existingBackground,
                    "Import status badges must follow semantic backgrounds and the system window background in high contrast.");
                Check(input.Background == inputBackground && input.BorderBrush == inputBorder,
                    "Boxed management inputs must use theme surfaces or system high-contrast colors.");
                var selection = contrast ? SystemColors.HighlightBrush : (Brush)root.FindResource("MeowFilm.Brush.Accent");
                Check(input.SelectionBrush == selection,
                    "Management text selection must use the application accent or system highlight.");
            }
        }
        finally { host.Close(); }
    }
    private static async Task Components()
    {
        var list = new ManagementList();
        var surface = new Border { Background = AppearanceColors.DefaultBackground, Padding = new Thickness(24), Child = list, Width = 880, Height = 700 };
        NovelUi.Theme(surface);
        var root = new Grid(); root.Children.Add(surface);
        var dialogHost = new PlayerDialogHost(); root.Children.Add(dialogHost);
        var host = GeneralAppearanceTests.Window(root); host.SizeToContent = SizeToContent.WidthAndHeight;
        DesktopTheme.SetIsHighContrast(host, false); dialogHost.Attach(host); host.Show();
        string? previous = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT");
        Environment.SetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT", TestWorkspace.DataPath("novel-management-previews"));
        try
        {
            var rows = Enumerable.Range(1, 3).Select(i => new ManagementRow(i.ToString(), "书源 " + i, "阅读源 · 精品", i != 2, () => { }, _ => { }, new ContextMenu())).ToArray();
            list.SetRows(rows, 3, rows.Select(r => r.Id), "无书源"); host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            var all = NovelTests.Find<CheckBox>(list).Single(b => AutomationProperties.GetAutomationId(b) == "management.selectAll"); all.IsChecked = true; all.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Check(list.Selected.SetEquals(["1", "2", "3"]));
            list.SetRows(rows.Take(1).ToArray(), 3, rows.Select(r => r.Id), "无书源");
            NovelTests.Find<Button>(list).Single(b => b.Content as string == "反选").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Check(list.Selected.SetEquals(["2", "3"]), "Inverse preserves hidden selections");
            all.IsChecked = true; all.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Check(list.Selected.SetEquals(["1"]), "Select-all replaces selection with visible IDs");
            all.IsChecked = false; all.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Check(list.Selected.Count == 0);
            list.SetRows([], 0, [], "暂无书源，请先导入"); await Preview("source-list-empty", 880, 700);
            list.SetRows(rows, 3, rows.Select(r => r.Id), "无书源"); await Preview("source-list", 880, 700);
            surface.Height = 900; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            var editor = new NovelSourceEditor("legado", """{"bookSourceName":"全本同人小说网","bookSourceUrl":"https://source.invalid/","bookSourceGroup":"同人","enabledCookieJar":false,"ruleSearch":{"bookList":".book"}}""");
            var sourcePanel = new DockPanel { Height = Math.Max(300, host.ActualHeight - 220) };
            var sourceActions = NovelUi.Bar(NovelUi.Button("复制书源", () => { }), NovelUi.Button("导出书源", () => { }));
            DockPanel.SetDock(sourceActions, Dock.Top); sourcePanel.Children.Add(sourceActions); sourcePanel.Children.Add(editor);
            var tabs = NovelTests.Find<TabControl>(editor).Single(); Check(tabs.Items.Count == 6);
            await DialogPreview(surface, sourcePanel, "编辑书源", "保存", "source-basic", async dialog =>
            {
                await CaptureDialog(dialog, "source-basic");
                var basicScroll = (ScrollViewer)((TabItem)tabs.Items[0]).Content;
                basicScroll.ScrollToEnd(); dialog.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
                await CaptureDialog(dialog, "source-basic-bottom");
                basicScroll.ScrollToTop();
                tabs.SelectedIndex = 1; dialog.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
                var input = NovelTests.Find<TextBox>(editor).Single(b => AutomationProperties.GetAutomationId(b) == "novel.source.field.ruleSearch.bookList");
                input.Text = ".edited";
                Check(JsonNode.Parse(editor.Draft.Definition())!["ruleSearch"]!["bookList"]!.GetValue<string>() == ".edited");
                await CaptureDialog(dialog, "source-search");
                ((ScrollViewer)((TabItem)tabs.Items[1]).Content).ScrollToEnd(); dialog.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
                await CaptureDialog(dialog, "source-search-bottom");
            });
            var ruleEditor = new NovelRuleEditor(new() { Name = "净化广告", Pattern = "广告", Replacement = "" }, _ => { });
            await DialogPreview(surface, ruleEditor, "替换规则编辑", "保存", "rule-editor", async dialog =>
            {
                Check(NovelTests.Find<TextBox>(ruleEditor).Select(b => AutomationProperties.GetAutomationId(b)).Count(id => id.StartsWith("novel.rule.field.")) == 7);
                Check(ruleEditor.Read().Pattern == "广告");
                await CaptureDialog(dialog, "rule-editor");
                await ReachRuleEditorBottom(dialog, ruleEditor);
                await CaptureDialog(dialog, "rule-editor-bottom");
            });
            var preview = new ManagementImportSelection([new("书源一", "阅读源 · 精品", "新增"), new("书源二", "异次元", "已存在")]);
            Check(preview.Selected.SetEquals([0, 1])); await DialogPreview(surface, preview, "导入书源", "确定导入", "source-import");
            var address = new ManagementForm(true); address.Field("书源地址", "", id: "management.import.url", placeholder: "请输入网络书源地址");
            await DialogPreview(surface, address, "网络导入", "确定", "source-network-import");
            var rulePreview = new ManagementImportSelection([new("净化一", "正文", "新增"), new("净化二", "正文", "已有")], [0]); Check(rulePreview.Selected.SetEquals([0]));

            var runtime = new NovelTests.Runtime(); var sources = new NovelSourceService(runtime); await sources.RefreshAsync();
            await using var store = new NovelTests.MemoryStore();
            var library = new NovelLibrary(store); await library.InitializeAsync();
            await library.SaveRulesAsync([new NovelRule { Id = "visual-rule", Name = "净化广告", Group = "正文", Pattern = "广告" }]);
            await using var ruleRuntime = new NovelRuleRuntimeFixture();
            await using var module = new NovelCapability(sources, library, new NovelRuleProgram(ruleRuntime));
            await module.InitializeAsync(default); await sources.RefreshAsync();
            surface.Child = module.SettingsView; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var settings = (NovelSettingsView)module.SettingsView;
            settings.Refresh();
            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var priorBackground = host.Background;
            var themedBackground = new SolidColorBrush(Color.FromRgb(30, 40, 50));
            host.Background = themedBackground; host.UpdateLayout();
            Check(ReferenceEquals(settings.Background, themedBackground) &&
                  ReferenceEquals(((Grid)settings.Content).Background, themedBackground),
                "Novel settings canvas did not follow its application background.");
            host.Background = priorBackground; host.UpdateLayout();
            Check(NovelTests.Find<TextBlock>(module.SettingsView).Any(text => text.Text == "书源a"));
            await Preview("source-manager-product", 880, 900);
            NovelTests.Find<Button>(module.SettingsView).Single(button => button.Content as string == "替换净化")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.Render);
            Check(NovelTests.Find<TextBlock>(module.SettingsView).Any(text => text.Text == "净化广告"));
            await Preview("rule-manager-product", 880, 900);
            NovelTests.Find<Button>(module.SettingsView).Single(button =>
                AutomationProperties.GetName(button) == "编辑 净化广告")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var ruleDialog = NovelTests.Find<Border>(dialogHost).Single(item =>
                AutomationProperties.GetName(item) == "替换规则编辑");
            try
            {
                await ReachRuleEditorBottom(ruleDialog, NovelTests.Find<NovelRuleEditor>(ruleDialog).Single());
                await CaptureDialog(ruleDialog, "rule-editor-product-bottom");
            }
            finally
            {
                NovelTests.Find<Button>(ruleDialog).Single(button => Equals(button.Content, "取消"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!dialogHost.IsOpen, "Cancelling the rule editor must restore the settings page.");
            }
        }
        finally { Environment.SetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT", previous); host.Close(); }
        async Task Preview(string name, int width, int height)
        {
            // The hosted viewport and capture must agree.  A WPF window clips a child visual
            // to its client area, so a tall editor would otherwise produce a transparent
            // RenderTargetBitmap tail even after the root had been arranged to the requested
            // size.  Render the arranged surface detached from that host clip, then restore
            // the live host for the rest of this component scenario.
            surface.Width = width; surface.Height = height; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
            Check(Math.Abs(surface.ActualWidth - width) < 1 && Math.Abs(surface.ActualHeight - height) < 1);
            var hostedContent = host.Content;
            host.Content = null;
            root.Children.Remove(surface);
            var composition = new Border { Background = host.Background, Child = surface };
            try
            {
                host.UpdateLayout();
                Console.WriteLine($"PREVIEW {name}: before={surface.ActualWidth}x{surface.ActualHeight} render={surface.RenderSize.Width}x{surface.RenderSize.Height}");
                GeneralAppearanceTests.SavePreview(composition, name, width, height);
                Console.WriteLine($"PREVIEW {name}: after={surface.ActualWidth}x{surface.ActualHeight} render={surface.RenderSize.Width}x{surface.RenderSize.Height}");
            }
            finally
            {
                composition.Child = null;
                root.Children.Insert(0, surface);
                host.Content = hostedContent;
                host.UpdateLayout();
            }
        }
    }
    private static async Task ReachRuleEditorBottom(FrameworkElement dialog, NovelRuleEditor editor)
    {
        var scroll = NovelTests.Find<ScrollViewer>(dialog).Single(item => ReferenceEquals(item.Content, editor));
        Check(scroll.ScrollableHeight > 0, "All rule fields must contribute to the dialog's scroll extent.");
        scroll.ScrollToEnd(); dialog.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Render);
        var viewport = scroll.Template.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter
            ?? throw new InvalidOperationException("The rule dialog viewport is missing.");
        var timeout = NovelTests.Find<TextBox>(editor).Single(input =>
            AutomationProperties.GetAutomationId(input) == "novel.rule.field.timeoutMillisecond");
        var bounds = timeout.TransformToAncestor(viewport).TransformBounds(new Rect(timeout.RenderSize));
        Check(bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1 &&
              bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1,
            "The last rule field must be fully reachable inside the dialog viewport.");
        timeout.Text = "2345";
        Check(editor.Read().TimeoutMillisecond == 2345, "Editing the last visible field must preserve its value.");
    }
    private static async Task DialogPreview(FrameworkElement owner, FrameworkElement body, string title, string action, string name,
        Func<FrameworkElement, Task>? capture = null)
    {
        using var cancellation = new CancellationTokenSource();
        var dialogHost = PlayerDialogHost.Find(owner)
            ?? throw new InvalidOperationException("The component preview requires the application dialog host.");
        var pending = NovelUi.DialogAsync(owner, title, body, action, _ => Task.FromResult(true), cancellation.Token,
            applicationOverlay: true);
        Button? cancel = null;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            var panel = NovelTests.Find<Border>(dialogHost).Single(item =>
                AutomationProperties.GetName(item) == title);
            cancel = NovelTests.Find<Button>(dialogHost).Single(button => Equals(button.Content, "取消"));
            Check(dialogHost.IsOpen && panel.IsVisible && panel.ActualWidth > 0 && panel.ActualHeight > 0 && cancel.IsVisible);
            if (capture is null) await CaptureDialog(panel, name); else await capture(panel);
        }
        finally
        {
            if (dialogHost.IsOpen && cancel is not null)
            {
                // Dispatch the WPF control event directly; this does not simulate physical input.
                cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            else cancellation.Cancel();
            Check(!await pending, "Cancelling the preview dialog must not accept the import.");
        }
    }
    private static async Task CaptureDialog(FrameworkElement panel, string name)
    {
        await Dispatcher.Yield(DispatcherPriority.Render);
        int width = (int)Math.Ceiling(panel.ActualWidth), height = (int)Math.Ceiling(panel.ActualHeight);
        Check(width > 0 && height > 0 && Math.Abs(panel.RenderSize.Width - width) < 1 && Math.Abs(panel.RenderSize.Height - height) < 1,
            "The captured native dialog must be fully arranged at its visible size");
        // This is a WPF RenderTargetBitmap visual snapshot, not a desktop pixel capture.
        GeneralAppearanceTests.SavePreview(panel, name, width, height);
    }
    private static async Task Qr()
    {
        const string text = """{"id":42,"pattern":"advertisement","replacement":""}""";
        var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(text, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium);
        const int scale = 5, border = 4; int width = (qr.Size + border * 2) * scale; var pixels = new byte[width * width]; Array.Fill(pixels, (byte)255);
        for (int y = 0; y < qr.Size; y++) for (int x = 0; x < qr.Size; x++) if (qr.GetModule(x, y))
            for (int dy = 0; dy < scale; dy++) Array.Fill(pixels, (byte)0, ((y + border) * scale + dy) * width + (x + border) * scale, scale);
        var bitmap = BitmapSource.Create(width, width, 96, 96, PixelFormats.Gray8, null, pixels, width); bitmap.Freeze();
        string path = TestWorkspace.DataPath("novel-management-qr.png");
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(path)) encoder.Save(file);
            Check(await NovelSettingsView.ReadRuleQrAsync(path, default) == text);
            File.WriteAllText(path, "not an image");
            try { await NovelSettingsView.ReadRuleQrAsync(path, default); throw new InvalidOperationException("Invalid QR image accepted"); }
            catch (Exception error) when (error is NotSupportedException or FileFormatException) { }
        }
        finally { File.Delete(path); }
    }
}

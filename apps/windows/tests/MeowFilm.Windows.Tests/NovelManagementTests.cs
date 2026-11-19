using System.IO;
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
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class NovelManagementTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("novel management form: current protocols nested mapping and unedited JSON types", Drafts);
        yield return new("novel management import: external rule identity nullable fields and tag-only group changes", Imports);
        yield return new("novel management theme: live resources input semantics and high-contrast resource mappings", () => NovelTests.Sta(ThemeResources));
        yield return new("novel management components: visible selection inverse native tabs fields and previews", () => NovelTests.Sta(Components));
        yield return new("novel management layout: shared columns virtualized cells selection and reorder reports", () => NovelTests.Sta(ListLayout));
        yield return new("novel management native reorder: virtualized scroll reaches distant rows and reports stable order", () => NovelTests.Sta(DragAutoScroll));
        yield return new("novel management native reorder: disabled filtered invalid and detached lists cannot persist changes", () => NovelTests.Sta(DragLifetime));
        yield return new("novel management QR: local image decodes original rule text and rejects invalid images", () => NovelTests.Sta(Qr));
        yield return new("novel management save: accepted revisions remain editable without another list request", () => NovelTests.Sta(SavedRevision));
    }
    private static void Check(bool condition, string message = "Novel management assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    private static async Task SavedRevision()
    {
        var runtime = new NovelTests.Runtime(); var sources = NovelTestSources.Create(runtime); await sources.RefreshAsync();
        var original = sources.Sources.Single(); runtime.Listing = _ => throw new IOException("list unavailable");
        try
        {
            string definition = (await sources.GetAsync(original)).GetProperty("definition").GetString()!;
            var first = await sources.SaveAsync(original, definition);
            Check(first.GetProperty("source").GetProperty("revision").GetInt32() == original.Revision + 1);
            Check(sources.Sources.Single().Revision == original.Revision + 1);
            await sources.SaveAsync(sources.Sources.Single(), definition);
            Check(sources.Sources.Single().Revision == original.Revision + 2);
            Check(runtime.SourceStore.Reads == 1 && !runtime.Calls.Any(c => c.Method.EndsWith("library.save", StringComparison.Ordinal)));
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
        foreach (string protocol in new[] { "legado", "qysg" })
        {
            var item = new NovelSourceDraft(protocol); item.Set(new("bookSourceName", "名称"), "测试"); item.Set(new("bookSourceUrl", "地址"), "https://new.invalid/");
            item.Set(new("bookSourceType", "类型", Kind: NovelSourceFieldKind.Number), "2"); var value = JsonNode.Parse(item.Definition())!.AsObject();
            Check(value["bookSourceType"]!.GetValue<double>() == 2); Check(value.ContainsKey(protocol == "legado" ? "ruleSearch" : "html"));
            Check(new[] { "ruleSearch", "html" }.Count(value.ContainsKey) == 1, "Saving must not change protocols");
        }
        var current = new NovelSourceDraft("legado", """{"bookSourceName":"当前源","bookSourceUrl":"https://current.invalid/","ruleSearch":{},"enabled":true,"enable":false,"customOrder":9,"serialNumber":-2,"ruleFindUrl":{"extra":true}}""");
        current.SetBool("enabled", false);
        result = JsonNode.Parse(current.Definition())!;
        Check(!result["enabled"]!.GetValue<bool>() && !result["enable"]!.GetValue<bool>());
        Check(result["customOrder"]!.GetValue<int>() == 9 && result["serialNumber"]!.GetValue<int>() == -2);
        Check(result["ruleFindUrl"]!["extra"]!.GetValue<bool>());
        var qysg = new NovelSourceDraft("qysg", "\"https://source.invalid/reader.html\"");
        qysg.Set(new("bookSourceName", "名称"), "轻悦"); qysg.Set(new("bookSourceUrl", "地址"), "https://source.invalid/");
        Check(JsonNode.Parse(qysg.Definition())!["html"]!.GetValue<string>() == "https://source.invalid/reader.html");
        return Task.CompletedTask;
    }
    private static Task Imports()
    {
        var rules = NovelRuleImport.Parse("""[{"id":42,"name":"","group":null,"pattern":"广告","replacement":"","scope":null,"excludeScope":null},{"id":43,"pattern":"literal[","regex":{"unused":true},"enable":false,"sortOrder":-2,"serialNumber":7,"replaceSummary":"旧名称","useTo":"旧范围","Pattern":false}]""");
        Check(rules.Select(r => r.Id).SequenceEqual(new[] { "42", "43" })); Check(rules[0].Name == "广告" && rules[0].IsRegex && rules[0].Group == "");
        Check(rules[1].Pattern == "literal[" && rules[1].IsRegex && rules[1].Enabled && rules[1].Order == 0 &&
            rules[1].Name == "literal[" && rules[1].Scope == "");
        var exported = NovelJson.Value(rules[1]);
        Check(!exported.GetProperty("enable").GetBoolean() && exported.GetProperty("regex").GetProperty("unused").GetBoolean() &&
            exported.GetProperty("sortOrder").GetInt32() == -2 && !exported.GetProperty("Pattern").GetBoolean());
        var defaults = NovelRuleImport.Parse("""{"id":"default","pattern":"当前","regex":false,"enable":{"extra":true},"sortOrder":[null]}""").Single();
        Check(defaults.Enabled && defaults.IsRegex && defaults.Order == 0);
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
        var statuses = new[] { new ManagementStatus("未检测"), new ManagementStatus("通过", ManagementStatusTone.Success), new ManagementStatus("失效", ManagementStatusTone.Failure) };
        foreach (var status in statuses) root.Children.Add(status);
        var delete = ManagementList.Button("删除所选", () => { }); delete.UseResource(FrameworkElement.StyleProperty, "MeowFilm.ManagementDeleteSelected"); root.Children.Add(delete);
        var host = NovelTestUi.Window(root);
        try
        {
            host.Activate();
            list.SetRows([new("1", "书源标题", "列表说明", true, () => { }, _ => { }, new MenuFlyout()),
                new("2", "失效书源", "失败说明", true, () => { }, _ => { }, new MenuFlyout(), Failed: true)], 2, ["1", "2"], "无书源");
            await DesktopTestUi.DrainAsync();
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
            var textBrush = (SolidColorBrush)NovelTestUi.Resource(root, "MeowFilm.Brush.Text");
            var mutedBrush = (SolidColorBrush)NovelTestUi.Resource(root, "MeowFilm.Brush.Muted");
            var accentBrush = (SolidColorBrush)NovelTestUi.Resource(root, "MeowFilm.Brush.AccentText");
            var originalText = textBrush.Color; var originalMuted = mutedBrush.Color; var originalAccent = accentBrush.Color;
            try
            {
                textBrush.Color = Color.FromArgb(255, 30, 40, 50);
                mutedBrush.Color = Color.FromArgb(255, 80, 90, 100);
                accentBrush.Color = Color.FromArgb(255, 100, 110, 120);
                await DesktopTestUi.DrainAsync();
                Check(titles.All(title => title.Foreground is SolidColorBrush brush && brush.Color == textBrush.Color) &&
                    labels.All(label => label.Foreground is SolidColorBrush brush && brush.Color == mutedBrush.Color) &&
                    (newStatus.Foreground as SolidColorBrush)?.Color == accentBrush.Color,
                    "Changing the active theme brushes must update existing content without rebuilding its fields.");
            }
            finally { textBrush.Color = originalText; mutedBrush.Color = originalMuted; accentBrush.Color = originalAccent; }
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                root.RequestedTheme = theme; await DesktopTestUi.DrainAsync();
                var text = NovelTestUi.Resource(root, "MeowFilm.Brush.Text");
                var muted = NovelTestUi.Resource(root, "MeowFilm.Brush.Muted");
                Check(ReferenceEquals(plain.Foreground, text) && ReferenceEquals(input.Foreground, text) && titles.All(title => ReferenceEquals(title.Foreground, text)));
                Check(labels.All(label => ReferenceEquals(label.Foreground, muted)));
                Check(ReferenceEquals(failure.Foreground, NovelTestUi.Resource(root, "MeowFilm.Brush.Error")),
                    "Failed source details must retain their error semantics.");
                Check(ReferenceEquals(newStatus.Foreground, NovelTestUi.Resource(root, "MeowFilm.Brush.AccentText")) &&
                    ReferenceEquals(existingStatus.Foreground, muted), "Import status labels must preserve their semantic colors.");
                Check(ReferenceEquals(newBadge.Background, NovelTestUi.Resource(root, "MeowFilm.Brush.AccentSoft")) &&
                    ReferenceEquals(existingBadge.Background, NovelTestUi.Resource(root, "MeowFilm.Brush.InputSurface")),
                    "New and existing import statuses must retain their distinct themed surfaces.");
                Check(ReferenceEquals(input.Background, NovelTestUi.Resource(root, "MeowFilm.Brush.InputSurface")) &&
                    ReferenceEquals(input.BorderBrush, NovelTestUi.Resource(root, "MeowFilm.Brush.Border")));
                Check(ReferenceEquals(list.Search.Background, NovelTestUi.Resource(root, "MeowFilm.Brush.Surface")));
                Check(statuses.All(status => status.FontWeight == FontWeights.Normal) && delete.FontWeight == FontWeights.Normal);
                Check(statuses.All(status => NovelTests.Find<Microsoft.UI.Xaml.Shapes.Ellipse>(status).Count() == 2),
                    "Status capsules must retain their ring and center dot.");
                Check(ReferenceEquals(statuses[1].Foreground, NovelTestUi.Resource(root, "MeowFilm.Brush.AccentText")) &&
                    ReferenceEquals(statuses[2].Foreground, NovelTestUi.Resource(root, "MeowFilm.Brush.Error")));
                Check((delete.Background as SolidColorBrush)?.Color.A == 0 && ReferenceEquals(delete.BorderBrush, NovelTestUi.Resource(root, "MeowFilm.Brush.Error")));
                Check(plain.Text == "保留输入" && input.Text == "保留输入", "Theme changes must preserve input.");
                Check(ReferenceEquals(input.SelectionHighlightColor, NovelTestUi.Resource(root, "MeowFilm.Brush.Accent")),
                    "Text selection must use the active application accent.");
            }
            Check(input.Focus(FocusState.Programmatic)); await DesktopTestUi.DrainAsync();
            Check(DesktopVisual.ContainsFocus(input), "The field must remain focusable after theme changes.");
            NovelTestUi.CheckContrastResources(root);
        }
        finally { host.Close(); }
    }
    private static async Task ListLayout()
    {
        int created = 0;
        var list = new ManagementList { ShowToolbar = false, ShowBulkActions = false, EnableRowDrag = true };
        list.SetColumns([new("名称", new(1, GridUnitType.Star)), new("状态", new(100)), new("操作", new(90))]);
        var rows = Enumerable.Range(1, 5000).Select(index => new ManagementRow(index.ToString(), "来源 " + index, "", true,
            () => { }, _ => { }, new MenuFlyout(), Cells: () =>
            {
                created++;
                var title = new TextBlock { Text = "来源 " + index, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                title.UseResource(FrameworkElement.StyleProperty, "MeowFilm.Text");
                var edit = new Button { Content = "编辑" }; edit.UseResource(FrameworkElement.StyleProperty, "MeowFilm.ManagementRowButton");
                return [title, new ManagementStatus("通过", ManagementStatusTone.Success), edit];
            })).ToArray();
        var root = new Border { Background = AppearanceColors.DefaultBackground, Child = list, Width = 880, Height = 600 }; NovelUi.Theme(root);
        var host = NovelTestUi.Window(root, 940, 1000);
        try
        {
            host.Activate(); list.SetRows(rows, rows.Length, rows.Select(row => row.Id), "暂无来源");
            host.Root.UpdateLayout(); await DesktopTestUi.DrainAsync();
            var native = NovelTests.Find<ListView>(list).Single();
            Check(native.ItemsPanelRoot is ItemsStackPanel, "The native list must use its virtualizing panel.");
            Check(created > 0 && created < 100, "Custom controls must only be created for realized rows.");
            Check(!DesktopVisual.IsVisible(list.Search) && !DesktopVisual.IsVisible(list.More), "Hidden book-source tools must not reserve visible chrome.");
            foreach (double width in new[] { 880d, 620d })
            {
                root.Width = width; host.Root.UpdateLayout(); await DesktopTestUi.DrainAsync();
                var header = (Grid)NovelTests.Find<TextBlock>(list).Single(text => text.Text == "名称").Parent;
                foreach (var row in NovelTests.Find<ManagementRowView>(list))
                {
                    Check(row.ColumnDefinitions.Count == header.ColumnDefinitions.Count);
                    for (int column = 0; column < header.ColumnDefinitions.Count; column++)
                        Check(Math.Abs(row.ColumnDefinitions[column].ActualWidth - header.ColumnDefinitions[column].ActualWidth) < 1,
                            "Header and realized row columns must share available width.");
                    Check(row.Children[1] is TextBlock && native.CanDragItems, "Plain row text must remain in the native drag item.");
                    var edit = (Button)row.Children[3];
                    Check(edit.IsEnabled && row.Children[0] is CheckBox,
                        "Selection and row actions must not begin a row drag.");
                }
                Check(NovelTests.Find<ScrollViewer>(native).Single().ScrollableWidth == 0, "Management columns must not cause horizontal scrolling.");
                await NovelTestUi.Preview(root, "management-columns-" + (int)width, (int)width, 600);
            }
            var firstCheck = NovelTests.Find<CheckBox>(list).Single(check => AutomationProperties.GetName(check) == "选择 来源 1");
            NovelTestUi.Check(firstCheck, true);
            native.ScrollIntoView(native.Items[^1]); host.Root.UpdateLayout(); await DesktopTestUi.DrainAsync();
            Check(NovelTests.Find<TextBlock>(list).Any(text => text.Text == "来源 5000") && created < 200,
                "Scrolling must recycle containers without constructing every item.");
            Check(list.Selected.SetEquals(["1"]), "Selection must survive row recycling.");
            IReadOnlyList<string>? reported = null; list.OrderChanged += order => reported = order;
            list.ReportOrder("1", "3", true);
            Check(reported is not null && reported.Take(3).SequenceEqual(["2", "3", "1"]) && reported.Count == rows.Length);
            Check(native.Items[0] is ManagementRowState { Row.Id: "1" }, "The public list must report order, not own persistence.");
            reported = null; list.ReportOrder("1", "1", false); Check(reported is null);
            list.EnableRowDrag = false; list.ReportOrder("1", "3", true); Check(reported is null);
            list.SetRows(rows, rows.Length, rows.Select(row => row.Id), "暂无来源");
            var all = NovelTests.Find<CheckBox>(list).Single(check => AutomationProperties.GetName(check) == "全选");
            NovelTestUi.Check(all, true); Check(list.Selected.Count == rows.Length);
        }
        finally { host.Close(); }
    }
    // Exercise the native list's virtualization and order admission without injecting pointer input.
    private static async Task DragAutoScroll()
    {
        int created = 0, reports = 0;
        IReadOnlyList<string>? reported = null;
        var list = new ManagementList { ShowToolbar = false, ShowBulkActions = false, EnableRowDrag = true };
        list.SetColumns([new("名称", new(1, GridUnitType.Star))]);
        var rows = Enumerable.Range(1, 5000).Select(index => new ManagementRow(index.ToString(), "来源 " + index, "", true,
            () => { }, _ => { }, new MenuFlyout(), Cells: () => { created++; return [new TextBlock { Text = "来源 " + index }]; })).ToArray();
        var root = new Border { Child = list, Width = 880, Height = 600 }; NovelUi.Theme(root);
        var host = NovelTestUi.Window(root);
        list.OrderChanged += order => { reports++; reported = order; };
        try
        {
            host.Activate(); list.SetRows(rows, rows.Length, rows.Select(row => row.Id), "暂无来源");
            await DesktopTestUi.DrainAsync();
            var native = NovelTests.Find<ListView>(list).Single();
            Check(native.CanDragItems && native.CanReorderItems && native.AllowDrop);
            var first = NovelTests.Find<CheckBox>(list).Single(check => AutomationProperties.GetName(check) == "选择 来源 1");
            NovelTestUi.Check(first, true);
            Check(native.ContainerFromIndex(4999) is null, "The target must initially be outside realized containers.");
            native.ScrollIntoView(native.Items[4999]); await DesktopTestUi.DrainAsync();
            Check(native.ContainerFromIndex(4999) is ListViewItem && created < 200,
                "Native scrolling must realize the distant target without building the whole list.");
            Check(reports == 0 && list.Selected.SetEquals(["1"]), "Viewport changes cannot persist order or selection.");
            list.ReportOrder("1", "5000", true);
            Check(reports == 1 && reported is not null && reported.SequenceEqual(rows.Skip(1).Select(row => row.Id).Append("1")));
            Check(native.Items[0] is ManagementRowState { Row.Id: "1" }, "Persistence remains owned by the caller.");
            native.ScrollIntoView(native.Items[0]); await DesktopTestUi.DrainAsync();
            Check(native.ContainerFromIndex(0) is ListViewItem && list.Selected.SetEquals(["1"]));
            list.ReportOrder("1", "1", true); Check(reports == 1, "No-op reordering cannot save.");
        }
        finally { host.Close(); }
    }
    private static async Task DragLifetime()
    {
        var list = new ManagementList { ShowToolbar = false, ShowBulkActions = false, EnableRowDrag = true };
        var rows = Enumerable.Range(1, 500).Select(index => new ManagementRow(index.ToString(), "来源 " + index, "", true,
            () => { }, _ => { }, new MenuFlyout())).ToArray();
        var root = new Border { Child = list, Width = 880, Height = 600 }; NovelUi.Theme(root);
        var host = NovelTestUi.Window(root); int reports = 0; list.OrderChanged += _ => reports++;
        try
        {
            host.Activate(); list.SetRows(rows, rows.Length, rows.Select(row => row.Id), "暂无来源"); await DesktopTestUi.DrainAsync();
            var native = NovelTests.Find<ListView>(list).Single();
            void Disabled() => Check(!native.CanDragItems && !native.CanReorderItems, "Retired input cannot retain native reorder admission.");
            Check(native.CanDragItems && native.CanReorderItems);
            list.EnableRowDrag = false; Disabled(); list.ReportOrder("1", "3", true); Check(reports == 0);
            list.EnableRowDrag = true; list.IsEnabled = false; Disabled(); list.IsEnabled = true;
            list.Visibility = Visibility.Collapsed; Disabled(); list.Visibility = Visibility.Visible; await DesktopTestUi.DrainAsync();
            list.SetRows(rows.Take(10).ToArray(), rows.Length, rows.Select(row => row.Id), "暂无来源"); Disabled();
            list.ReportOrder("1", "3", true); Check(reports == 0, "A filtered list cannot persist a partial order.");
            list.SetRows(rows, rows.Length, rows.Select(row => row.Id), "暂无来源"); await DesktopTestUi.DrainAsync();
            list.ReportOrder("invalid", "3", true); list.ReportOrder("1", "invalid", true); Check(reports == 0);
            root.Child = null; await DesktopTestUi.DrainAsync(); Disabled();
            root.Child = list; await DesktopTestUi.DrainAsync(); Check(native.CanDragItems && native.CanReorderItems);
            host.Close(); await DesktopTestUi.DrainAsync(); Disabled(); Check(reports == 0);
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
        var host = NovelTestUi.Window(root, 940, 1000);
        DesktopTheme.SetIsHighContrast(host.Root, false); dialogHost.Attach(host); host.Activate();
        string? previous = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT");
        Environment.SetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT", TestWorkspace.DataPath("novel-management-previews"));
        try
        {
            var rows = Enumerable.Range(1, 3).Select(i => new ManagementRow(i.ToString(), "书源 " + i, "阅读源 · 精品", i != 2, () => { }, _ => { }, new MenuFlyout())).ToArray();
            list.SetRows(rows, 3, rows.Select(r => r.Id), "无书源"); host.Root.UpdateLayout(); await DesktopTestUi.DrainAsync();
            var all = NovelTests.Find<CheckBox>(list).Single(b => AutomationProperties.GetAutomationId(b) == "management.selectAll"); NovelTestUi.Check(all, true); Check(list.Selected.SetEquals(["1", "2", "3"]));
            list.SetRows(rows.Take(1).ToArray(), 3, rows.Select(r => r.Id), "无书源");
            NovelTestUi.Invoke(NovelTests.Find<Button>(list).Single(b => b.Content as string == "反选")); await DesktopTestUi.DrainAsync(); Check(list.Selected.SetEquals(["2", "3"]), "Inverse preserves hidden selections");
            NovelTestUi.Check(all, true); Check(list.Selected.SetEquals(["1"]), "Select-all replaces selection with visible IDs");
            NovelTestUi.Check(all, false); Check(list.Selected.Count == 0);
            list.SetRows([], 0, [], "暂无书源，请先导入"); await Preview("source-list-empty", 880, 700);
            list.SetRows(rows, 3, rows.Select(r => r.Id), "无书源"); await Preview("source-list", 880, 700);
            surface.Height = 900; host.Root.UpdateLayout(); await DesktopTestUi.DrainAsync();
            var editor = new NovelSourceEditor("legado", """{"bookSourceName":"全本同人小说网","bookSourceUrl":"https://source.invalid/","bookSourceGroup":"同人","enabledCookieJar":false,"ruleSearch":{"bookList":".book"}}""");
            var sourcePanel = new DockPanel { Height = Math.Max(300, host.Root.ActualHeight - 220) };
            var sourceActions = NovelUi.Bar(NovelUi.Button("复制书源", () => { }), NovelUi.Button("导出书源", () => { }));
            DockPanel.SetDock(sourceActions, Dock.Top); sourcePanel.Children.Add(sourceActions); sourcePanel.Children.Add(editor);
            var tabs = NovelTests.Find<TabView>(editor).Single(); Check(tabs.TabItems.Count == 6);
            await DialogPreview(surface, sourcePanel, "编辑书源", "保存", "source-basic", async dialog =>
            {
                await CaptureDialog(dialog, "source-basic");
                var basicScroll = (ScrollViewer)((TabViewItem)tabs.TabItems[0]).Content;
                basicScroll.ChangeView(null, basicScroll.ScrollableHeight, null, true); dialog.UpdateLayout(); await DesktopTestUi.DrainAsync();
                await CaptureDialog(dialog, "source-basic-bottom");
                basicScroll.ChangeView(null, 0, null, true);
                tabs.SelectedIndex = 1; dialog.UpdateLayout(); await DesktopTestUi.DrainAsync();
                var input = NovelTests.Find<TextBox>(editor).Single(b => AutomationProperties.GetAutomationId(b) == "novel.source.field.ruleSearch.bookList");
                input.Text = ".edited";
                Check(JsonNode.Parse(editor.Draft.Definition())!["ruleSearch"]!["bookList"]!.GetValue<string>() == ".edited");
                await CaptureDialog(dialog, "source-search");
                var searchScroll = (ScrollViewer)((TabViewItem)tabs.TabItems[1]).Content; searchScroll.ChangeView(null, searchScroll.ScrollableHeight, null, true); dialog.UpdateLayout(); await DesktopTestUi.DrainAsync();
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
            var preview = new ManagementImportSelection([new("书源一", "阅读源 · 精品", "新增"), new("书源二", "轻悦时光", "已存在")]);
            Check(preview.Selected.SetEquals([0, 1])); await DialogPreview(surface, preview, "导入书源", "确定导入", "source-import");
            var address = new ManagementForm(true); address.Field("书源地址", "", id: "management.import.url", placeholder: "请输入网络书源地址");
            await DialogPreview(surface, address, "网络导入", "确定", "source-network-import");
            var rulePreview = new ManagementImportSelection([new("净化一", "正文", "新增"), new("净化二", "正文", "已有")], [0]); Check(rulePreview.Selected.SetEquals([0]));

            var runtime = new NovelTests.Runtime(); var sources = NovelTestSources.Create(runtime); await sources.RefreshAsync();
            await using var store = new NovelTests.MemoryStore();
            var library = new NovelLibrary(store); await library.InitializeAsync();
            await library.SaveRulesAsync([new NovelRule { Id = "visual-rule", Name = "净化广告", Group = "正文", Pattern = "广告" }]);
            await using var ruleRuntime = new NovelRuleRuntimeFixture();
            await using var module = new NovelCapability(sources, library, new NovelRuleProgram(ruleRuntime));
            await module.InitializeAsync(default); await sources.RefreshAsync();
            surface.Child = module.SettingsView; host.Root.UpdateLayout(); await DesktopTestUi.DrainAsync();
            var settings = (NovelSettingsView)module.SettingsView;
            settings.Refresh();
            host.Root.UpdateLayout(); await DesktopTestUi.DrainAsync();
            var priorBackground = surface.Background;
            var themedBackground = new SolidColorBrush(Color.FromArgb(255, 30, 40, 50));
            surface.Background = themedBackground; await DesktopTestUi.DrainAsync();
            Check(settings.Background is null && ((Grid)settings.Content).Background is null,
                "The settings canvas must expose the application's background instead of owning a stale color copy.");
            surface.Background = priorBackground;
            Check(NovelTests.Find<TextBlock>(module.SettingsView).Any(text => text.Text == "书源a"));
            await Preview("source-manager-product", 880, 900);
            NovelTests.Find<RadioButton>(module.SettingsView).Single(button => button.Content as string == "替换净化")
                .IsChecked = true;
            await DesktopTestUi.DrainAsync();
            Check(NovelTests.Find<TextBlock>(module.SettingsView).Any(text => text.Text == "净化广告"));
            await Preview("rule-manager-product", 880, 900);
            NovelTestUi.Invoke(NovelTests.Find<Button>(module.SettingsView).Single(button =>
                AutomationProperties.GetName(button) == "编辑 净化广告"));
            await DesktopTestUi.DrainAsync();
            var ruleDialog = NovelTests.Find<Border>(dialogHost).Single(item =>
                AutomationProperties.GetName(item) == "替换规则编辑");
            try
            {
                var title = NovelTests.Find<TextBlock>(ruleDialog).Single(text => text.Text == "替换规则编辑");
                Check(title.IsLoaded && DesktopVisual.IsVisible(title) && title.FontSize == 18 && title.FontWeight == FontWeights.Normal,
                    "the actual rule editor dialog retains its title size and uses regular text");
                await ReachRuleEditorBottom(ruleDialog, NovelTests.Find<NovelRuleEditor>(ruleDialog).Single());
                await CaptureDialog(ruleDialog, "rule-editor-product-bottom");
            }
            finally
            {
                NovelTestUi.Invoke(NovelTests.Find<Button>(ruleDialog).Single(button => Equals(button.Content, "取消")));
                await DesktopTestUi.DrainAsync();
                Check(!dialogHost.IsOpen, "Cancelling the rule editor must restore the settings page.");
            }
        }
        finally { Environment.SetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT", previous); host.Close(); }
        async Task Preview(string name, int width, int height)
        {
            // Capture the actual WinUI tree; do not detach it or create a WPF visual island.
            surface.Width = width; surface.Height = height; host.Resize(width + 60, height + 80);
            host.Root.UpdateLayout(); await DesktopTestUi.DrainAsync();
            Check(Math.Abs(surface.ActualWidth - width) < 1 && Math.Abs(surface.ActualHeight - height) < 1);
            await NovelTestUi.Preview(surface, name, width, height);
        }
    }
    private static async Task ReachRuleEditorBottom(FrameworkElement dialog, NovelRuleEditor editor)
    {
        var scroll = NovelTests.Find<ScrollViewer>(dialog).Single(item => item.Content is DependencyObject content && DesktopVisual.Contains(content, editor));
        Check(scroll.ScrollableHeight > 0, "All rule fields must contribute to the dialog's scroll extent.");
        scroll.ChangeView(null, scroll.ScrollableHeight, null, true); dialog.UpdateLayout(); await DesktopTestUi.DrainAsync();
        var viewport = scroll;
        var timeout = NovelTests.Find<TextBox>(editor).Single(input =>
            AutomationProperties.GetAutomationId(input) == "novel.rule.field.timeoutMillisecond");
        var bounds = timeout.TransformToVisual(viewport).TransformBounds(new Rect(0, 0, timeout.ActualWidth, timeout.ActualHeight));
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
            await NovelTestUi.LoadedAsync(body);
            var panel = NovelTests.Find<Border>(dialogHost).Single(item =>
                AutomationProperties.GetName(item) == title);
            cancel = NovelTests.Find<Button>(dialogHost).Single(button => Equals(button.Content, "取消"));
            Check(dialogHost.IsOpen && DesktopVisual.IsVisible(panel) && panel.ActualWidth > 0 && panel.ActualHeight > 0 && DesktopVisual.IsVisible(cancel));
            if (capture is null) await CaptureDialog(panel, name); else await capture(panel);
        }
        finally
        {
            if (dialogHost.IsOpen && cancel is not null)
            {
                // Invoke the control automation pattern, without physical input.
                NovelTestUi.Invoke(cancel);
            }
            else cancellation.Cancel();
            Check(!await pending, "Cancelling the preview dialog must not accept the import.");
        }
    }
    private static async Task CaptureDialog(FrameworkElement panel, string name)
    {
        await DesktopTestUi.DrainAsync();
        int width = (int)Math.Ceiling(panel.ActualWidth), height = (int)Math.Ceiling(panel.ActualHeight);
        Check(width > 0 && height > 0 && Math.Abs(panel.ActualWidth - width) < 1 && Math.Abs(panel.ActualHeight - height) < 1,
            "The captured native dialog must be fully arranged at its visible size");
        // This is a WinUI tree snapshot, not a desktop pixel capture.
        await NovelTestUi.Preview(panel, name, width, height);
    }
    private static async Task Qr()
    {
        const string text = """{"id":42,"pattern":"advertisement","replacement":""}""";
        var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(text, Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium);
        const int scale = 5, border = 4; int width = (qr.Size + border * 2) * scale; var pixels = new byte[width * width]; Array.Fill(pixels, (byte)255);
        for (int y = 0; y < qr.Size; y++) for (int x = 0; x < qr.Size; x++) if (qr.GetModule(x, y))
            for (int dy = 0; dy < scale; dy++) Array.Fill(pixels, (byte)0, ((y + border) * scale + dy) * width + (x + border) * scale, scale);
        var bgra = pixels.SelectMany(value => new byte[] { value, value, value, 255 }).ToArray();
        string path = TestWorkspace.DataPath("novel-management-qr.png");
        try
        {
            File.WriteAllBytes(path, FixtureImages.Encode(width, width, bgra));
            Check(await NovelSettingsView.ReadRuleQrAsync(path, default) == text);
            File.WriteAllText(path, "not an image");
            try { await NovelSettingsView.ReadRuleQrAsync(path, default); throw new InvalidOperationException("Invalid QR image accepted"); }
            catch (Exception error) when (error is InvalidDataException) { }
        }
        finally { File.Delete(path); }
    }
}

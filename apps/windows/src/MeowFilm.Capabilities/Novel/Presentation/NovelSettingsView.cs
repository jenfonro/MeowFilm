using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeowFilm.Desktop;
using Microsoft.Win32;

namespace MeowFilm.Capabilities;

public sealed partial class NovelSettingsView : UserControl, IDisposable
{
    private readonly NovelCapability module;
    private readonly NovelWork work = new();
    private readonly Grid root = new();
    private readonly ContentControl body = new();
    private readonly NoticeOutlet notice = new();
    private readonly ManagementList sourceList = new(), ruleList = new();
    private readonly Button sourceSort, sourceGroups, sourceMore, ruleGroups, ruleMore;
    private string tab = "sources", sourceQuery = "", ruleQuery = "";
    private string? sourceGroup, sourceProtocol, ruleGroup;
    private int sourceStatus, sort;
    private bool reverse, ungroupedRules, disposed, loading;
    private CancellationTokenSource pending = new();
    internal NovelSettingsView(NovelCapability module)
    {
        this.module = module; NovelUi.Theme(this);
        SetBinding(Control.BackgroundProperty, new Binding(nameof(Window.Background))
        { RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(Window), 1) });
        Content = root;
        root.SetBinding(Panel.BackgroundProperty, new Binding(nameof(Window.Background))
        { RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(Window), 1) });
        root.Children.Add(body); root.Children.Add(notice);
        sourceSort = MenuButton("\uE8CB", "排序"); sourceGroups = MenuButton("\uE71C", "分组与协议"); sourceMore = MenuButton("\uE712", "更多");
        ruleGroups = MenuButton("\uE71C", "分组"); ruleMore = MenuButton("\uE712", "更多");
        foreach (var button in new[] { sourceSort, sourceGroups, sourceMore }) sourceList.Tools.Children.Add(button);
        foreach (var button in new[] { ruleGroups, ruleMore }) ruleList.Tools.Children.Add(button);
        sourceList.Search.Tag = "搜索书源"; ruleList.Search.Tag = "搜索替换规则";
        sourceList.Search.TextChanged += (_, _) => { sourceQuery = sourceList.Search.Text; RefreshSources(); };
        ruleList.Search.TextChanged += (_, _) => { ruleQuery = ruleList.Search.Text; RefreshRules(); };
        sourceList.Delete += () => Run(ct => RemoveSourcesAsync(module.Sources.Sources.Where(s => sourceList.Selected.Contains(s.InstallationId)).ToArray(), ct));
        ruleList.Delete += () => Run(ct => RemoveRulesAsync(module.Library.Rules.Where(r => ruleList.Selected.Contains(r.Id)).ToArray(), ct));
        sourceList.SelectionChanged += SourceMenus; ruleList.SelectionChanged += RuleMenus;
        IsVisibleChanged += (_, _) => { if (disposed) return; if (IsVisible) { Resume(); Run(async ct => { await module.Library.InitializeAsync(ct); await module.Sources.RefreshAsync(ct); Refresh(); }); } else Cancel(); };
        Loaded += (_, _) => { if (disposed) return; if (pending.IsCancellationRequested) Resume(); Refresh(); };
        module.PropertyChanged += ModuleChanged; BuildLayout(); Refresh();
    }
    private void ModuleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { notice.Message = module.Notice; }
    private void BuildLayout()
    {
        var layout = new DockPanel { Margin = new(32, 20, 24, 20) };
        var header = NovelUi.Bar(NovelUi.Button("书源管理", () => { tab = "sources"; BuildLayout(); Refresh(); }),
            NovelUi.Button("替换净化", () => { tab = "rules"; BuildLayout(); Refresh(); }),
            NovelUi.Button("阅读排版", () => Run(EditAppearanceAsync)), NovelUi.Button("取消操作", Cancel));
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        // A manager belongs to one tree at a time, including when switching tabs.
        if (sourceList.Parent is Panel sourceParent) sourceParent.Children.Remove(sourceList);
        if (ruleList.Parent is Panel ruleParent) ruleParent.Children.Remove(ruleList);
        layout.Children.Add(tab == "sources" ? sourceList : ruleList); body.Content = layout;
    }
    public void Refresh() { if (disposed) return; RefreshSources(); RefreshRules(); }
    private IReadOnlyList<NovelSource> VisibleSources()
    {
        string query = sourceQuery.Trim();
        var values = module.Sources.Sources.Where(s => (sourceProtocol is null || s.Protocol == sourceProtocol) &&
            (sourceGroup is null || NovelGroups.Split(s.Group).Contains(sourceGroup)) &&
            (sourceStatus == 0 || sourceStatus == 1 && s.Enabled || sourceStatus == 2 && !s.Enabled || sourceStatus == 3 && s.EnabledExplore || sourceStatus == 4 && !s.EnabledExplore) &&
            (query.Length == 0 || new[] { s.Name, s.SourceUrl, s.Group }.Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase))));
        IOrderedEnumerable<NovelSource> ordered = sort switch
        {
            1 => values.OrderByDescending(s => s.Weight).ThenBy(s => s.Name, StringComparer.CurrentCulture),
            2 => values.OrderBy(s => s.Name, StringComparer.CurrentCulture),
            3 => values.OrderBy(s => s.SourceUrl, StringComparer.CurrentCulture).ThenBy(s => s.Name, StringComparer.CurrentCulture),
            4 => values.OrderByDescending(s => s.LastUpdateTime).ThenBy(s => s.Name, StringComparer.CurrentCulture),
            5 => values.OrderBy(s => s.RespondTime).ThenBy(s => s.Name, StringComparer.CurrentCulture),
            6 => values.OrderByDescending(s => s.Enabled).ThenBy(s => s.Name, StringComparer.CurrentCulture),
            _ => values.OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.CurrentCulture)
        };
        var result = ordered.ThenBy(s => s.InstallationId, StringComparer.Ordinal); return (reverse ? result.Reverse() : result).ToArray();
    }
    private void RefreshSources()
    {
        sourceList.SetRows(VisibleSources().Select(source => new ManagementRow(source.InstallationId, source.Name,
            string.Join(" · ", new[] { NovelSourceFields.Title(source.Protocol), source.Group, source.ErrorCode }.Where(value => !string.IsNullOrEmpty(value))), source.Enabled,
            () => Run(ct => EditSourceAsync(source, source.Protocol, ct)), enabled => Run(ct => module.Sources.UpdateAsync(source, new { enabled }, ct)), SourceRowMenu(source), source.ErrorCode is not null)).ToArray(),
            module.Sources.Sources.Count, module.Sources.Sources.Select(s => s.InstallationId), module.Sources.Sources.Count == 0 ? "暂无书源，请先导入" : "没有匹配的书源"); SourceMenus();
    }
    private void SourceMenus()
    {
        sourceSort.ContextMenu = Menu(Item("反序", () => { reverse = !reverse; RefreshSources(); }, reverse));
        string[] sorts = ["手动", "智能", "名称", "地址", "更新时间", "响应时间", "启用"];
        foreach (var (name, index) in sorts.Select((name, index) => (name, index))) sourceSort.ContextMenu.Items.Add(Item(name, () => { sort = index; RefreshSources(); }, sort == index));
        var protocols = new MenuItem { Header = "书源协议" }; protocols.Items.Add(Item("全部协议", () => { sourceProtocol = null; RefreshSources(); }, sourceProtocol is null));
        foreach (string protocol in new[] { "legado", "yiciyuan", "qysg" }) protocols.Items.Add(Item(NovelSourceFields.Title(protocol), () => { sourceProtocol = protocol; RefreshSources(); }, sourceProtocol == protocol));
        var menu = Menu(Item("分组管理", () => Run(ct => ManageGroupsAsync(true, ct))), protocols,
            Item("全部分组", () => { sourceGroup = null; sourceStatus = 0; RefreshSources(); }, sourceGroup is null && sourceStatus == 0));
        foreach (var (name, index) in new[] { "已启用", "已禁用", "已启用发现", "已禁用发现" }.Select((name, index) => (name, index))) menu.Items.Add(Item(name, () => { sourceGroup = null; sourceStatus = index + 1; RefreshSources(); }, sourceStatus == index + 1));
        foreach (string group in module.Sources.Sources.SelectMany(s => NovelGroups.Split(s.Group)).Distinct().Order(StringComparer.CurrentCulture)) menu.Items.Add(Item(group, () => { sourceGroup = group; sourceStatus = 0; RefreshSources(); }, sourceGroup == group));
        sourceGroups.ContextMenu = menu;
        var create = new MenuItem { Header = "新建书源" }; foreach (string protocol in new[] { "legado", "yiciyuan", "qysg" }) create.Items.Add(Item(NovelSourceFields.Title(protocol), () => Run(ct => EditSourceAsync(null, protocol, ct))));
        sourceMore.ContextMenu = Menu(create, Item("本地导入", () => Run(ImportSourceFileAsync)), Item("网络导入", () => Run(ImportSourceUrlAsync)),
            Item("粘贴导入", () => Run(async ct => await PreviewSourcesAsync(this, await module.Sources.PreviewImportAsync(text: Clipboard.GetText(), token: ct), ct))),
            Item("导出书源", () => Run(ct => ExportSourcesAsync(module.Sources.Sources.Select(s => s.InstallationId).ToArray(), ct))));
        sourceList.More.ContextMenu = Menu(Item("导出选中书源", () => Run(ct => ExportSourcesAsync(sourceList.Selected.ToArray(), ct))), Item("启用所选", () => Run(ct => BatchSourcesAsync(true, ct))), Item("禁用所选", () => Run(ct => BatchSourcesAsync(false, ct))));
    }
    private ContextMenu SourceRowMenu(NovelSource source)
    {
        var menu = Menu(Item("置顶", () => Run(ct => MoveSourceAsync(source, true, ct))), Item("置底", () => Run(ct => MoveSourceAsync(source, false, ct))));
        if (source.HasExplore) menu.Items.Add(Item(source.EnabledExplore ? "禁用发现" : "启用发现", () => Run(ct => module.Sources.UpdateAsync(source, new { enabledExplore = !source.EnabledExplore }, ct))));
        if (source.Enabled && source.Actions.Contains("login")) menu.Items.Add(Item("登录", () => Run(async ct => { await module.Sources.InvokeAsync(source, "login", new { }, ct); module.Notify("书源登录已完成"); })));
        menu.Items.Add(Item("导出", () => Run(ct => ExportSourcesAsync([source.InstallationId], ct)))); menu.Items.Add(Item("删除", () => Run(ct => RemoveSourcesAsync([source], ct)))); return menu;
    }
    private async Task MoveSourceAsync(NovelSource source, bool top, CancellationToken token)
    {
        long edge = top ? module.Sources.Sources.Min(s => s.Order) : module.Sources.Sources.Max(s => s.Order);
        if (edge is > -9007199254740991 and < 9007199254740991) { await module.Sources.UpdateAsync(source, new { order = edge + (top ? -1 : 1) }, token); return; }
        var others = module.Sources.Sources.Where(s => s.InstallationId != source.InstallationId).OrderBy(s => s.Order).ToList(); if (top) others.Insert(0, source); else others.Add(source);
        for (int index = 0; index < others.Count; index++) await module.Sources.UpdateAsync(others[index], new { order = index }, token);
    }
    private async Task BatchSourcesAsync(bool enabled, CancellationToken token)
    { foreach (var source in module.Sources.Sources.Where(s => sourceList.Selected.Contains(s.InstallationId)).ToArray()) await module.Sources.UpdateAsync(source, new { enabled }, token); }
    private async Task RemoveSourcesAsync(NovelSource[] sources, CancellationToken token)
    {
        if (sources.Length == 0 || !await NovelUi.ConfirmAsync(this, "删除书源", $"删除所选 {sources.Length} 个书源及其私有数据？", "删除", token, applicationOverlay: true)) return;
        foreach (var source in sources) await module.Sources.RemoveAsync(source, token);
    }
    private async Task EditSourceAsync(NovelSource? source, string protocol, CancellationToken token)
    {
        string? definition = null;
        if (source is not null)
        {
            var current = await module.Sources.GetAsync(source, token);
            source = NovelJson.Decode<NovelSource>(current.GetProperty("source")); definition = NovelJson.Text(current, "definition");
        }
        var editor = new NovelSourceEditor(protocol, definition);
        // Keep the controls/tabs fixed while only the selected form scrolls inside the dialog viewport.
        var panel = new DockPanel { Height = Math.Max(300, Window.GetWindow(this)?.ActualHeight - 220 ?? 520) };
        var actions = NovelUi.Bar(NovelUi.Button("复制书源", () => { try { Clipboard.SetText(editor.Draft.Definition()); module.Notify("书源已复制"); } catch (Exception error) { module.Notify(error.Message); } }),
            NovelUi.Button("导出书源", () => { try { var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "book-source.json" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllText(dialog.FileName, editor.Draft.Definition()); } catch (Exception error) { module.Notify(error.Message); } }));
        DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions); panel.Children.Add(editor);
        await NovelUi.DialogAsync(this, "编辑书源", panel, "保存", async ct =>
        {
            string text = editor.Draft.Definition();
            if (source is null)
            {
                var result = await module.Sources.ImportAsync(text: text, token: ct);
                var errors = ImportErrors(result).ToArray(); if (errors.Length > 0) throw new InvalidDataException(string.Join("\n", errors));
                source = NovelJson.Decode<NovelSource[]>(result.GetProperty("sources")).Single();
            }
            else source = NovelJson.Decode<NovelSource>((await module.Sources.SaveAsync(source, text, ct)).GetProperty("source"));
            module.Notify("书源已保存"); return false;
        }, token, applicationOverlay: true);
    }
    private async Task ImportSourceFileAsync(CancellationToken token)
    {
        var dialog = new OpenFileDialog { Filter = "书源文件|*.json;*.txt;*.html|所有文件|*.*" }; if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await PreviewSourcesAsync(this, await module.Sources.PreviewImportAsync(text: await File.ReadAllTextAsync(dialog.FileName, token), fileName: Path.GetFileName(dialog.FileName), token: token), token);
    }
    private async Task ImportSourceUrlAsync(CancellationToken token)
    {
        var panel = new ManagementForm(true); var input = panel.Field("书源地址", "", id: "management.import.url", placeholder: "请输入网络书源地址"); JsonElement preview = default;
        bool accepted = await NovelUi.DialogAsync(this, "网络导入", panel, "确定", async ct => { preview = await module.Sources.PreviewImportAsync(url: input.Text.Trim(), token: ct); return true; }, token, applicationOverlay: true);
        if (accepted) await PreviewSourcesAsync(this, preview, token);
    }
    internal async Task ImportFromAsync(FrameworkElement owner, CancellationToken token)
    {
        var panel = new ManagementForm(); var input = panel.Field("书源内容", "", 12); JsonElement preview = default;
        bool accepted = await NovelUi.DialogAsync(owner, "导入书源", panel, "预览", async ct => { preview = await module.Sources.PreviewImportAsync(text: input.Text, token: ct); return true; }, token, applicationOverlay: true);
        if (accepted) await PreviewSourcesAsync(owner, preview, token);
    }
    private async Task PreviewSourcesAsync(FrameworkElement owner, JsonElement preview, CancellationToken token)
    {
        var items = preview.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
        var selection = new ManagementImportSelection(items.Select(item => new ManagementImportItem(NovelJson.Text(item, "name"), NovelSourceFields.Title(NovelJson.Text(item, "protocol")) + " · " + NovelJson.Text(item, "group"), item.GetProperty("exists").GetBoolean() ? "已存在" : "新增")).ToArray());
        var panel = new StackPanel(); panel.Children.Add(selection); var errors = ImportErrors(preview).ToArray(); if (errors.Length > 0) panel.Children.Add(NovelUi.Text(string.Join("\n", errors), 12));
        await NovelUi.DialogAsync(owner, "导入书源", panel, "确定导入", async ct =>
        {
            if (selection.Selected.Count == 0) throw new InvalidDataException("请选择要导入的书源");
            string text = "[" + string.Join(',', items.Where((_, index) => selection.Selected.Contains(index)).Select(item => NovelJson.Text(item, "definition"))) + "]";
            var result = await module.Sources.ImportAsync(text: text, token: ct); var failures = ImportErrors(result).ToArray();
            module.Notify($"已导入 {result.GetProperty("sources").GetArrayLength()} 个书源" + (failures.Length == 0 ? "" : "\n" + string.Join("\n", failures))); return true;
        }, token, applicationOverlay: true);
    }
    private static IEnumerable<string> ImportErrors(JsonElement value) => value.GetProperty("failures").EnumerateArray().Select(item => $"条目 {item.GetProperty("index").GetInt32() + 1} · {NovelJson.Text(item, "name")} · {NovelJson.Text(item, "code")}");
    private async Task ExportSourcesAsync(string[] ids, CancellationToken token)
    {
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "book-sources.json" }; if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var result = await module.Sources.ExportAsync(ids, token); await File.WriteAllTextAsync(dialog.FileName, NovelJson.Text(result, "text"), token); module.Notify("书源已导出");
    }
    private static Button MenuButton(string title, string name) { Button? button = null; button = ManagementList.Button(title, () => ManagementList.OpenMenu(button!)); button.SetResourceReference(StyleProperty, "MeowFilm.IconButton"); button.Padding = new(0); button.Width = button.Height = 40; button.ToolTip = name; System.Windows.Automation.AutomationProperties.SetName(button, name); return button; }
    private static ContextMenu Menu(params object[] items) { var menu = new ContextMenu(); menu.SetResourceReference(StyleProperty, "MeowFilm.ContextMenu"); foreach (var item in items) menu.Items.Add(item); return menu; }
    private static MenuItem Item(string title, Action action, bool selected = false) { var item = new MenuItem { Header = title, IsChecked = selected }; item.SetResourceReference(StyleProperty, "MeowFilm.MenuItem"); item.Click += (_, e) => { e.Handled = true; action(); }; return item; }
    private async Task EditAppearanceAsync(CancellationToken token)
    {
        var editor = new NovelAppearanceEditor(module.Library.Appearance);
        await NovelUi.DialogAsync(this, "阅读排版", editor, "保存", async ct =>
        {
            var prior = module.Library.Appearance; var next = editor.Read(); await module.Library.SaveAppearanceAsync(next, ct);
            if (prior.Conversion != next.Conversion) await module.ReprocessCurrentAsync(ct);
            return true;
        }, token, applicationOverlay: true);
    }
    private void Run(Func<CancellationToken, Task> action)
    {
        if (disposed || loading) return;
        if (pending.IsCancellationRequested) Resume();
        _ = Execute();
        async Task Execute()
        {
            loading = true; Refresh();
            try { await work.Run(async ct => { await action(ct); return true; }, pending.Token); }
            catch (OperationCanceledException) { }
            catch { module.Notify("小说设置操作失败，请重试"); }
            finally { loading = false; Refresh(); }
        }
    }
    public void Cancel() => pending.Cancel();
    public Task DrainAsync() => work.PauseAsync();
    public void Resume() { if (pending.IsCancellationRequested) { pending.Dispose(); pending = new(); } work.Resume(); }
    public void Dispose() { disposed = true; Cancel(); module.PropertyChanged -= ModuleChanged; pending.Dispose(); }
}

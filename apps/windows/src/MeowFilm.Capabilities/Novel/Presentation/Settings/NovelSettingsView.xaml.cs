using System.IO;
using System.Text.Json;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeowFilm.Capabilities;

public sealed partial class NovelSettingsView : UserControl, IDisposable
{
    private readonly NovelCapability module;
    private readonly NovelWork work = new();
    private readonly ManagementList sourceList = new(), ruleList = new();
    private readonly Button sourceSort, sourceGroups, sourceMore, ruleGroups, ruleMore;
    private readonly Dictionary<string, MenuFlyout> sourceRowMenus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MenuFlyout> ruleRowMenus = new(StringComparer.Ordinal);
    private string tab = "sources", sourceQuery = "", ruleQuery = "";
    private string? sourceGroup, sourceProtocol, ruleGroup;
    private int sourceStatus, sort;
    private bool reverse, ungroupedRules, disposed, shuttingDown, switchingTab;
    private readonly NovelPresentation.VisibilityWatch visibility;
    private CancellationTokenSource pending = new();
    private Task? activeRun;

    internal string CurrentDebugTab => tab;

    internal bool SelectDebugTab(string nextTab)
    {
        var normalized = nextTab.Trim().ToLowerInvariant();
        if (normalized is not ("sources" or "rules")) return false;
        if (tab == normalized) return true;
        SetTab(normalized);
        return true;
    }

    internal object BuildDebugSnapshot() => new
    {
        domain = "novel",
        section = tab,
        tabs = new object[]
        {
            new { group = "settings-section", index = 0, id = "sources", title = "书源管理", selected = tab == "sources", enabled = true },
            new { group = "settings-section", index = 1, id = "rules", title = "替换净化", selected = tab == "rules", enabled = true }
        },
        sourceCount = module.Sources.Sources.Count,
        enabledSourceCount = module.Sources.Sources.Count(s => s.Enabled),
        ruleCount = module.Library.Rules.Count,
        appearance = module.Library.Appearance,
        sources = module.Sources.Sources.Take(100).Select((s, i) => new
        {
            index = i,
            installationId = s.InstallationId,
            name = s.Name,
            url = s.SourceUrl,
            group = s.Group,
            protocol = s.Protocol,
            enabled = s.Enabled,
            enabledExplore = s.EnabledExplore,
            errorCode = s.ErrorCode
        }).ToArray()
    };

    internal NovelSettingsView(NovelCapability module)
    {
        this.module = module;
        InitializeComponent();
        SourcesHost.Content = sourceList;
        RulesHost.Content = ruleList;
        sourceSort = MenuButton("\uE8CB", "排序"); sourceGroups = MenuButton("\uE71C", "分组与协议"); sourceMore = MenuButton("\uE710", "添加书源");
        ruleGroups = MenuButton("\uE71C", "分组"); ruleMore = MenuButton("\uE712", "更多");
        foreach (var button in new[] { sourceSort, sourceGroups, sourceMore }) sourceList.Tools.Children.Add(button);
        foreach (var button in new[] { ruleGroups, ruleMore }) ruleList.Tools.Children.Add(button);
        sourceList.Search.PlaceholderText = "搜索书源"; AutomationProperties.SetName(sourceList.Search, "搜索书源");
        ruleList.Search.PlaceholderText = "搜索替换规则"; AutomationProperties.SetName(ruleList.Search, "搜索替换规则");
        sourceList.Search.TextChanged += (_, _) => { sourceQuery = sourceList.Search.Text; RefreshSources(); };
        ruleList.Search.TextChanged += (_, _) => { ruleQuery = ruleList.Search.Text; RefreshRules(); };
        sourceList.Delete += () => Run(ct => RemoveSourcesAsync(module.Sources.Sources.Where(s => sourceList.Selected.Contains(s.InstallationId)).ToArray(), ct));
        ruleList.Delete += () => Run(ct => RemoveRulesAsync(module.Library.Rules.Where(r => ruleList.Selected.Contains(r.Id)).ToArray(), ct));
        InitSourceMenus();
        InitRuleMenus();
        visibility = new(this, visible =>
        {
            if (disposed || shuttingDown) return;
            if (visible)
            {
                Resume();
                RefreshActiveTab();
                Run(async ct => { await module.Library.InitializeAsync(ct); await module.Sources.RefreshAsync(ct); }, waitPrevious: true);
            }
            else Cancel();
        });
        Loaded += (_, _) =>
        {
            if (disposed || shuttingDown) return;
            if (pending.IsCancellationRequested) Resume();
            RefreshActiveTab();
        };
        ApplyTabVisuals();
    }

    internal void PostNotice(string text) => NoticeHost.Post(text);

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (switchingTab || sender is not RadioButton { Tag: string id } || tab == id) return;
        SetTab(id);
    }

    private void Appearance_Click(object sender, RoutedEventArgs e) => Run(EditAppearanceAsync);

    private void SetTab(string nextTab)
    {
        tab = nextTab;
        ApplyTabVisuals();
        RefreshActiveTab();
    }

    private void ApplyTabVisuals()
    {
        switchingTab = true;
        try
        {
            SourcesTab.IsChecked = tab == "sources";
            RulesTab.IsChecked = tab == "rules";
            SourcesHost.Visibility = tab == "sources" ? Visibility.Visible : Visibility.Collapsed;
            RulesHost.Visibility = tab == "rules" ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { switchingTab = false; }
    }

    public void Refresh() { if (disposed) return; RefreshSources(); RefreshRules(); }
    private void RefreshActiveTab() { if (disposed) return; if (tab == "sources") RefreshSources(); else RefreshRules(); }
    internal void RefreshSourcesIfVisible() { if (!disposed && DesktopVisual.IsVisible(this) && tab == "sources") RefreshSources(); }
    internal void RefreshRulesIfVisible() { if (!disposed && DesktopVisual.IsVisible(this) && tab == "rules") RefreshRules(); }

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
        var result = ordered.ThenBy(s => s.InstallationId, StringComparer.Ordinal);
        return (reverse ? result.Reverse() : result).ToArray();
    }

    private void RefreshSources()
    {
        var allSources = module.Sources.Sources;
        var validIds = allSources.Select(s => s.InstallationId).ToHashSet(StringComparer.Ordinal);
        foreach (string stale in sourceRowMenus.Keys.Where(id => !validIds.Contains(id)).ToArray())
            sourceRowMenus.Remove(stale);
        sourceList.SetRows(VisibleSources().Select(source => new ManagementRow(source.InstallationId, source.Name,
            string.Join(" · ", new[] { NovelSourceFields.Title(source.Protocol), source.Group, source.ErrorCode }.Where(value => !string.IsNullOrEmpty(value))), source.Enabled,
            () => Run(ct => EditSourceAsync(source, source.Protocol, ct)), enabled => Run(ct => module.Sources.UpdateAsync(source, new { enabled }, ct)), GetSourceRowMenu(source.InstallationId), source.ErrorCode is not null)).ToArray(),
            allSources.Count, validIds, allSources.Count == 0 ? "暂无书源，请先导入" : "没有匹配的书源");
    }

    private void InitSourceMenus()
    {
        var sortMenu = Menu();
        sortMenu.Opening += (_, _) => PopulateSourceSortMenu(sortMenu);
        sourceSort.ContextFlyout = sortMenu;

        var groupMenu = Menu();
        groupMenu.Opening += (_, _) => PopulateSourceGroupsMenu(groupMenu);
        sourceGroups.ContextFlyout = groupMenu;

        sourceMore.ContextFlyout = Menu(
            Item("新建书源", () => Run(ct => EditSourceAsync(null, "legado", ct))),
            Item("本地导入", () => Run(ImportSourceFileAsync)),
            Item("网络导入", () => Run(ImportSourceUrlAsync)),
            Item("粘贴导入", () => Run(async ct => await PreviewSourcesAsync(this, await module.Sources.PreviewImportAsync(text: await NovelPresentation.ClipboardTextAsync(), token: ct), ct))),
            Item("导出书源", () => Run(ct => ExportSourcesAsync(module.Sources.Sources.Select(s => s.InstallationId).ToArray(), ct))));

        sourceList.More.ContextFlyout = Menu(
            Item("导出选中书源", () => Run(ct => ExportSourcesAsync(sourceList.Selected.ToArray(), ct))),
            Item("启用所选", () => Run(ct => BatchSourcesAsync(true, ct))),
            Item("禁用所选", () => Run(ct => BatchSourcesAsync(false, ct))));
    }

    private void PopulateSourceSortMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Item("反序", () => { reverse = !reverse; RefreshSources(); }, reverse));
        string[] sorts = ["手动", "智能", "名称", "地址", "更新时间", "响应时间", "启用"];
        for (int index = 0; index < sorts.Length; index++)
        {
            int captured = index;
            menu.Items.Add(Item(sorts[index], () => { sort = captured; RefreshSources(); }, sort == captured));
        }
    }

    private void PopulateSourceGroupsMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        var protocols = new MenuFlyoutSubItem { Text = "书源协议" };
        protocols.Items.Add(Item("全部协议", () => { sourceProtocol = null; RefreshSources(); }, sourceProtocol is null));
        foreach (string protocol in NovelSourceEditor.Protocols)
            protocols.Items.Add(Item(NovelSourceFields.Title(protocol), () => { sourceProtocol = protocol; RefreshSources(); }, sourceProtocol == protocol));
        menu.Items.Add(Item("分组管理", () => Run(ct => ManageGroupsAsync(true, ct))));
        menu.Items.Add(protocols);
        menu.Items.Add(Item("全部分组", () => { sourceGroup = null; sourceStatus = 0; RefreshSources(); }, sourceGroup is null && sourceStatus == 0));
        string[] statuses = ["已启用", "已禁用", "已启用发现", "已禁用发现"];
        for (int index = 0; index < statuses.Length; index++)
        {
            int status = index + 1;
            menu.Items.Add(Item(statuses[index], () => { sourceGroup = null; sourceStatus = status; RefreshSources(); }, sourceStatus == status));
        }
        foreach (string group in module.Sources.Sources.SelectMany(s => NovelGroups.Split(s.Group)).Distinct().Order(StringComparer.CurrentCulture))
            menu.Items.Add(Item(group, () => { sourceGroup = group; sourceStatus = 0; RefreshSources(); }, sourceGroup == group));
    }

    private MenuFlyout GetSourceRowMenu(string installationId)
    {
        if (sourceRowMenus.TryGetValue(installationId, out var cached)) return cached;
        var menu = Menu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var source = module.Sources.Sources.FirstOrDefault(s => s.InstallationId == installationId);
            if (source is null) return;
            menu.Items.Add(Item("置顶", () => Run(ct => MoveSourceAsync(source, true, ct))));
            menu.Items.Add(Item("置底", () => Run(ct => MoveSourceAsync(source, false, ct))));
            if (source.HasExplore) menu.Items.Add(Item(source.EnabledExplore ? "禁用发现" : "启用发现", () => Run(ct => module.Sources.UpdateAsync(source, new { enabledExplore = !source.EnabledExplore }, ct))));
            if (source.Enabled && source.Actions.Contains("login")) menu.Items.Add(Item("登录", () => Run(async ct => { await module.Sources.InvokeAsync(source, "login", new { }, ct); module.Notify("书源登录已完成"); })));
            menu.Items.Add(Item("导出", () => Run(ct => ExportSourcesAsync([source.InstallationId], ct))));
            menu.Items.Add(Item("删除", () => Run(ct => RemoveSourcesAsync([source], ct))));
        };
        sourceRowMenus[installationId] = menu;
        return menu;
    }

    private async Task MoveSourceAsync(NovelSource source, bool top, CancellationToken token)
    {
        long edge = top ? module.Sources.Sources.Min(s => s.Order) : module.Sources.Sources.Max(s => s.Order);
        if (edge is > -9007199254740991 and < 9007199254740991) { await module.Sources.UpdateAsync(source, new { order = edge + (top ? -1 : 1) }, token); return; }
        var others = module.Sources.Sources.Where(s => s.InstallationId != source.InstallationId).OrderBy(s => s.Order).ToList();
        if (top) others.Insert(0, source); else others.Add(source);
        for (int index = 0; index < others.Count; index++) await module.Sources.UpdateAsync(others[index], new { order = index }, token);
    }

    private async Task BatchSourcesAsync(bool enabled, CancellationToken token)
    {
        foreach (var source in module.Sources.Sources.Where(s => sourceList.Selected.Contains(s.InstallationId)).ToArray())
            await module.Sources.UpdateAsync(source, new { enabled }, token);
    }

    private async Task RemoveSourcesAsync(NovelSource[] sources, CancellationToken token)
    {
        if (sources.Length == 0) return;
        var request = new DesktopDialogRequest
        {
            Title = "删除书源",
            Message = $"删除所选 {sources.Length} 个书源及其私有数据？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Close
        };
        if (await DesktopModalHost.ShowDialogAsync(this, request, token) != DesktopDialogResult.Primary) return;
        foreach (var source in sources) await module.Sources.RemoveAsync(source, token);
    }

    private async Task EditSourceAsync(NovelSource? source, string protocol, CancellationToken token)
    {
        string? definition = null;
        if (source is not null)
        {
            var current = await module.Sources.GetAsync(source, token);
            source = NovelJson.Decode<NovelSource>(current.GetProperty("source"));
            definition = NovelJson.Text(current, "definition");
        }

        var importAfterClose = await NovelSourceEditorDialogView.ShowAsync(
            this,
            module,
            source,
            protocol,
            definition,
            ImportSourceFileAsync,
            ImportSourceUrlAsync,
            async ct => await PreviewSourcesAsync(this, await module.Sources.PreviewImportAsync(text: await NovelPresentation.ClipboardTextAsync(), token: ct), ct),
            token);
        if (importAfterClose is not null && !token.IsCancellationRequested)
            await importAfterClose(token);
    }

    private async Task ImportSourceFileAsync(CancellationToken token)
    {
        string? path = await NovelPresentation.OpenFileAsync(this, ".json", ".txt", ".html", "*");
        token.ThrowIfCancellationRequested();
        if (path is null) return;
        await PreviewSourcesAsync(this, await module.Sources.PreviewImportAsync(text: await File.ReadAllTextAsync(path, token), fileName: Path.GetFileName(path), token: token), token);
    }

    private Task ImportSourceUrlAsync(CancellationToken token) => ImportFromAsync(this, token);

    internal async Task ImportFromAsync(FrameworkElement owner, CancellationToken token)
    {
        var inputView = new NovelImportInputDialogView("书源地址或 JSON 内容", "", rows: 4, automationId: "novel.source.import.text");
        JsonElement preview = default;
        var request = new DesktopDialogRequest
        {
            Title = "导入书源",
            Content = inputView,
            PrimaryButtonText = "解析",
            CloseButtonText = "取消",
            MaxWidth = 520,
            DefaultButton = DesktopDialogDefaultButton.Primary,
            OnOpened = () => inputView.FocusInput(),
            OnPrimaryAsync = async () =>
            {
                try
                {
                    string value = inputView.InputText.Trim();
                    if (value.Length == 0) throw new InvalidDataException("请输入书源地址或 JSON 内容");
                    preview = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                        ? await module.Sources.PreviewImportAsync(url: value, token: token)
                        : await module.Sources.PreviewImportAsync(text: value, token: token);
                    return true;
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception error)
                {
                    inputView.ShowError(error is InvalidDataException ? error.Message : "解析失败，请检查输入或重试");
                    return false;
                }
            }
        };
        if (await DesktopModalHost.ShowDialogAsync(owner, request, token) == DesktopDialogResult.Primary)
            await PreviewSourcesAsync(owner, preview, token);
    }

    private async Task PreviewSourcesAsync(FrameworkElement owner, JsonElement preview, CancellationToken token)
    {
        var items = preview.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
        var entries = items.Select(item => (
            Title: NovelJson.Text(item, "name"),
            Detail: NovelSourceFields.Title(NovelJson.Text(item, "protocol")) + " · " + NovelJson.Text(item, "group"),
            Status: item.GetProperty("exists").GetBoolean() ? "已存在" : "新增")).ToArray();
        var errors = ImportErrors(preview).ToArray();
        var previewView = new NovelImportPreviewDialogView(entries, errors: errors);
        var request = new DesktopDialogRequest
        {
            Title = "导入书源",
            Content = previewView,
            PrimaryButtonText = "确定导入",
            CloseButtonText = "取消",
            MaxWidth = 560,
            DefaultButton = DesktopDialogDefaultButton.Primary,
            OnPrimaryAsync = async () =>
            {
                try
                {
                    var selected = previewView.SelectedIndices;
                    if (selected.Count == 0) throw new InvalidDataException("请选择要导入的书源");
                    string text = "[" + string.Join(',', items.Where((_, index) => selected.Contains(index)).Select(item => NovelJson.Text(item, "definition"))) + "]";
                    var result = await module.Sources.ImportAsync(text: text, token: token);
                    var failures = ImportErrors(result).ToArray();
                    module.Notify($"已导入 {result.GetProperty("sources").GetArrayLength()} 个书源" + (failures.Length == 0 ? "" : "\n" + string.Join("\n", failures)));
                    return true;
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception error)
                {
                    previewView.ShowError(error is InvalidDataException ? error.Message : "导入书源失败，请重试");
                    return false;
                }
            }
        };
        await DesktopModalHost.ShowDialogAsync(owner, request, token);
    }

    private static IEnumerable<string> ImportErrors(JsonElement value) =>
        value.GetProperty("failures").EnumerateArray().Select(item => $"条目 {item.GetProperty("index").GetInt32() + 1} · {NovelJson.Text(item, "name")} · {NovelJson.Text(item, "code")}");

    private async Task ExportSourcesAsync(string[] ids, CancellationToken token)
    {
        string? path = await NovelPresentation.SaveJsonAsync(this, "book-sources.json");
        token.ThrowIfCancellationRequested();
        if (path is null) return;
        var result = await module.Sources.ExportAsync(ids, token);
        await File.WriteAllTextAsync(path, NovelJson.Text(result, "text"), token);
        module.Notify("书源已导出");
    }

    private static Button MenuButton(string title, string name)
    {
        Button? button = null;
        button = ManagementList.Button(title, () =>
            (button!.ContextFlyout as MenuFlyout)?.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft }),
            "MeowFilm.IconButton");
        button.Padding = new(0);
        button.Width = button.Height = 40;
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static MenuFlyout Menu(params MenuFlyoutItemBase[] items)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    private static MenuFlyoutItemBase Item(string title, Action action, bool? selected = null)
    {
        if (selected is { } check)
        {
            var toggle = new ToggleMenuFlyoutItem { Text = title, IsChecked = check };
            toggle.Click += (_, _) => action();
            return toggle;
        }
        var item = new MenuFlyoutItem { Text = title };
        item.Click += (_, _) => action();
        return item;
    }

    private async Task EditAppearanceAsync(CancellationToken token)
    {
        var editor = new NovelAppearanceEditor(module.Library.Appearance);
        var request = new DesktopDialogRequest
        {
            Title = "阅读排版",
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            MaxWidth = 520,
            DefaultButton = DesktopDialogDefaultButton.Primary,
            OnPrimaryAsync = async () =>
            {
                try
                {
                    await module.Library.SaveAppearanceAsync(editor.Read(), token);
                    return true;
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception error)
                {
                    editor.ShowError(error is InvalidDataException ? error.Message : "操作失败，请检查输入或重试");
                    return false;
                }
            }
        };
        await DesktopModalHost.ShowDialogAsync(this, request, token);
    }

    private void Run(Func<CancellationToken, Task> action, bool waitPrevious = false)
    {
        if (disposed || shuttingDown) return;
        if (!waitPrevious && activeRun is { IsCompleted: false }) return;
        if (pending.IsCancellationRequested) Resume();
        var previous = waitPrevious ? activeRun : null;
        var token = pending.Token;
        activeRun = Execute();
        async Task Execute()
        {
            if (previous is not null) { try { await previous; } catch { } }
            if (disposed || shuttingDown || token.IsCancellationRequested) return;
            try { await work.Run(async ct => { await action(ct); return true; }, token); }
            catch (OperationCanceledException) { }
            catch { if (!disposed && !shuttingDown) module.Notify("小说设置操作失败，请重试"); }
        }
    }

    public void Cancel()
    {
        if (disposed) return;
        if (!pending.IsCancellationRequested) pending.Cancel();
    }
    public Task DrainAsync()
    {
        shuttingDown = true;
        Cancel();
        return work.PauseAsync();
    }
    public void Resume()
    {
        if (disposed || shuttingDown) return;
        if (pending.IsCancellationRequested) { pending.Dispose(); pending = new(); }
        work.Resume();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shuttingDown = true;
        visibility.Dispose();
        if (!pending.IsCancellationRequested) pending.Cancel();
        pending.Dispose();
    }
}

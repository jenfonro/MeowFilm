using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.Desktop;
using Microsoft.Win32;

namespace MeowFilm.Capabilities;

public sealed class NovelSettingsView : UserControl, IDisposable
{
    private readonly NovelCapability module;
    private readonly NovelWork work = new();
    private readonly Grid root = new();
    private readonly ContentControl body = new();
    private readonly NoticeOutlet notice = new();
    private readonly HashSet<string> selected = [];
    private string tab = "sources", filter = "", group = "", sourceFilter = "", sourceGroup = "";
    private bool disposed, loading;
    private CancellationTokenSource pending = new();
    internal NovelSettingsView(NovelCapability module)
    {
        this.module = module; NovelUi.Theme(this); Content = root;
        root.Children.Add(body); root.Children.Add(notice);
        IsVisibleChanged += (_, _) => { if (disposed) return; if (IsVisible) { Resume(); Run(async ct => { await module.Library.InitializeAsync(ct); await module.Sources.RefreshAsync(ct); Refresh(); }); } else Cancel(); };
        Loaded += (_, _) => { if (disposed) return; if (pending.IsCancellationRequested) Resume(); Refresh(); };
        module.PropertyChanged += ModuleChanged;
        Refresh();
    }
    private void ModuleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { notice.Message = module.Notice; }
    public void Refresh()
    {
        if (disposed) return;
        var layout = new DockPanel { Margin = new(40, 20, 24, 20) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        header.Children.Add(NovelUi.Bar(NovelUi.Button(tab == "sources" ? "书源 ✓" : "书源", () => { tab = "sources"; Refresh(); }),
            NovelUi.Button(tab == "rules" ? "替换净化 ✓" : "替换净化", () => { tab = "rules"; Refresh(); }),
            NovelUi.Button("阅读排版", () => Run(EditAppearanceAsync)),
            NovelUi.Button("取消操作", Cancel, loading)));
        if (tab == "sources") layout.Children.Add(BuildSources(header)); else layout.Children.Add(BuildRules(header));
        body.Content = layout;
    }
    private FrameworkElement BuildSources(StackPanel header)
    {
        header.Children.Add(NovelUi.Bar(NovelUi.Button("文件导入", () => Run(ImportSourcesAsync)),
            NovelUi.Button("URL 导入", () => Run(ImportSourceUrlsAsync)),
            NovelUi.Button("文本导入", () => Run(ImportSourceTextAsync)),
            NovelUi.Button("导出", () => Run(ExportSourcesAsync)),
            NovelUi.Button("刷新", () => Run(ct => module.Sources.RefreshAsync(ct)))));
        selected.IntersectWith(module.Sources.Sources.Select(s => s.InstallationId));
        var search = NovelUi.Input("筛选书源", sourceFilter);
        var groups = new ComboBox { Width = 180, ItemsSource = module.Sources.Sources.Select(s => s.Group).Where(g => g.Length > 0).Prepend("全部分组").Distinct().ToArray(),
            SelectedItem = sourceGroup.Length == 0 ? "全部分组" : sourceGroup };
        header.Children.Add(NovelUi.Bar(search, groups));
        var rows = new StackPanel();
        search.TextChanged += (_, _) => { sourceFilter = search.Text; Fill(); };
        groups.SelectionChanged += (_, _) => { sourceGroup = groups.SelectedIndex <= 0 ? "" : groups.SelectedItem as string ?? ""; Fill(); };
        Fill(); return NovelUi.Scroll(rows);
        void Fill()
        {
        rows.Children.Clear();
        foreach (var source in module.Sources.Sources.Where(s => (sourceGroup.Length == 0 || s.Group == sourceGroup) &&
            (s.Name + " " + s.Group + " " + s.SourceUrl).Contains(sourceFilter, StringComparison.CurrentCultureIgnoreCase)))
        {
            var row = new DockPanel { Margin = new(0, 0, 0, 12) };
            var checkbox = new CheckBox { IsChecked = selected.Contains(source.InstallationId), Margin = new(0, 10, 12, 0) };
            System.Windows.Automation.AutomationProperties.SetName(checkbox, "选择" + source.Name);
            checkbox.Checked += (_, _) => selected.Add(source.InstallationId); checkbox.Unchecked += (_, _) => selected.Remove(source.InstallationId);
            DockPanel.SetDock(checkbox, Dock.Left); row.Children.Add(checkbox);
            var actions = NovelUi.Bar(
                NovelUi.Button(source.Enabled ? "停用" : "启用", () => Run(ct => module.Sources.UpdateAsync(source, new { enabled = !source.Enabled }, ct))),
                NovelUi.Button(source.EnabledExplore ? "关闭发现" : "启用发现", () => Run(ct => module.Sources.UpdateAsync(source, new { enabledExplore = !source.EnabledExplore }, ct))),
                NovelUi.Button("编辑", () => Run(ct => EditSourceAsync(source, ct))),
                NovelUi.Button("属性", () => Run(ct => EditSourcePropertiesAsync(source, ct))),
                NovelUi.Button("登录", () => Run(async ct => { await module.Sources.InvokeAsync(source, "login", new { }, ct); module.Notify("书源登录已完成"); }), source.Enabled && source.Actions.Contains("login")),
                NovelUi.Button("删除", () => Run(async ct =>
                { if (await NovelUi.ConfirmAsync(this, "删除书源", $"删除“{source.Name}”及其来源私有数据？", "删除", ct)) await module.Sources.RemoveAsync(source, ct); })));
            var text = new StackPanel(); text.Children.Add(NovelUi.Text(source.Name, 16)); text.Children.Add(NovelUi.Text(source.Group + " · " + source.Protocol + (source.ErrorCode is null ? "" : " · " + source.ErrorCode), 12, true));
            text.Children.Add(actions); row.Children.Add(text); rows.Children.Add(row);
        }
        if (rows.Children.Count == 0) rows.Children.Add(NovelUi.Text(module.Sources.Sources.Count == 0 ? "暂无书源" : "无匹配书源", muted: true));
        }
    }
    private async Task ImportSourcesAsync(CancellationToken token)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "书源文件|*.json;*.txt;*.js|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var errors = new List<string>(); int count = 0;
        foreach (string file in dialog.FileNames)
        {
            token.ThrowIfCancellationRequested();
            try { var value = await module.Sources.ImportAsync(text: await File.ReadAllTextAsync(file, token), fileName: Path.GetFileName(file), token: token); count += value.GetProperty("sources").GetArrayLength(); errors.AddRange(ImportErrors(value)); }
            catch (OperationCanceledException) { throw; }
            catch { errors.Add(Path.GetFileName(file) + "：导入失败"); }
        }
        module.Notify($"已导入 {count} 个书源" + (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors)));
    }
    private static IEnumerable<string> ImportErrors(JsonElement value) => value.GetProperty("failures").EnumerateArray()
        .Select(item => $"条目 {item.GetProperty("index").GetInt32() + 1} · {NovelJson.Text(item, "name")} · {NovelJson.Text(item, "code")}");
    private async Task ImportSourceUrlsAsync(CancellationToken token)
    {
        var input = MultiLine("书源 URL（每行一个）");
        await NovelUi.DialogAsync(this, "URL 导入", input, "导入", async ct =>
        {
            var errors = new List<string>(); int count = 0;
            foreach (string url in input.Text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                ct.ThrowIfCancellationRequested();
                try { var result = await module.Sources.ImportAsync(url: url, token: ct); count += result.GetProperty("sources").GetArrayLength(); errors.AddRange(ImportErrors(result)); }
                catch (OperationCanceledException) { throw; }
                catch { errors.Add("URL 导入失败"); }
            }
            module.Notify($"已导入 {count} 个书源" + (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors)));
            return errors.Count == 0;
        }, token);
    }
    private Task ImportSourceTextAsync(CancellationToken token) => ImportFromAsync(this, token);
    internal async Task ImportFromAsync(FrameworkElement owner, CancellationToken token)
    {
        var input = MultiLine("书源 JSON");
        await NovelUi.DialogAsync(owner, "导入书源", input, "导入", async ct =>
        {
            var result = await module.Sources.ImportAsync(text: input.Text, token: ct); var errors = ImportErrors(result).ToArray();
            module.Notify($"已导入 {result.GetProperty("sources").GetArrayLength()} 个书源" + (errors.Length == 0 ? "" : "\n" + string.Join("\n", errors))); return errors.Length == 0;
        }, token);
    }
    private async Task ExportSourcesAsync(CancellationToken token)
    {
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "book-sources.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string[] ids = selected.Count > 0 ? selected.Intersect(module.Sources.Sources.Select(s => s.InstallationId)).ToArray() : module.Sources.Sources.Select(s => s.InstallationId).ToArray();
        var result = await module.Sources.ExportAsync(ids, token);
        await File.WriteAllTextAsync(dialog.FileName, NovelJson.Text(result, "text"), token); module.Notify("书源已导出");
    }
    private async Task EditSourceAsync(NovelSource source, CancellationToken token)
    {
        var result = await module.Sources.GetAsync(source, token);
        var input = MultiLine("完整书源定义", NovelJson.Text(result, "definition"));
        await NovelUi.DialogAsync(this, "编辑“" + source.Name + "”", input, "保存", async ct =>
        { await module.Sources.SaveAsync(source, input.Text, ct); return true; }, token);
    }
    private async Task EditSourcePropertiesAsync(NovelSource source, CancellationToken token)
    {
        var panel = new StackPanel();
        panel.Children.Add(NovelUi.Text("名称")); var name = NovelUi.Input("名称", source.Name); panel.Children.Add(name);
        panel.Children.Add(NovelUi.Text("分组")); var groupInput = NovelUi.Input("分组", source.Group); panel.Children.Add(groupInput);
        panel.Children.Add(NovelUi.Text("排序")); var order = NovelUi.Input("排序", source.Order.ToString()); panel.Children.Add(order);
        await NovelUi.DialogAsync(this, "书源属性", panel, "保存", async ct =>
        {
            if (!long.TryParse(order.Text, out long value) || value is < -9007199254740991 or > 9007199254740991 || string.IsNullOrWhiteSpace(name.Text)) throw new InvalidDataException();
            await module.Sources.UpdateAsync(source, new { name = name.Text.Trim(), group = groupInput.Text.Trim(), order = value }, ct); return true;
        }, token);
    }
    private FrameworkElement BuildRules(StackPanel header)
    {
        header.Children.Add(NovelUi.Bar(NovelUi.Button("新增", () => Run(ct => EditRuleAsync(new NovelRule { Order = module.Library.Rules.Count }, ct))),
            NovelUi.Button("文件导入", () => Run(ImportRuleFileAsync)), NovelUi.Button("URL 导入", () => Run(ImportRuleUrlAsync)),
            NovelUi.Button("导出", () => Run(ExportRulesAsync))));
        var search = NovelUi.Input("筛选规则", filter); var groups = new ComboBox { Width = 180,
            ItemsSource = module.Library.Rules.Select(r => r.Group).Where(g => g.Length > 0).Prepend("全部分组").Distinct().ToArray(), SelectedItem = group.Length == 0 ? "全部分组" : group };
        header.Children.Add(NovelUi.Bar(search, groups));
        var rows = new StackPanel();
        void Fill()
        {
            rows.Children.Clear();
            foreach (var rule in module.Library.Rules.OrderBy(r => r.Order).Where(r => (group.Length == 0 || r.Group == group) &&
                (r.Name + " " + r.Pattern + " " + r.Group).Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
            {
                var line = new DockPanel { Margin = new(0, 0, 0, 12) };
                var actions = NovelUi.Bar(NovelUi.Button(rule.Enabled ? "停用" : "启用", () => Run(ct => SaveRuleAsync(rule with { Enabled = !rule.Enabled }, ct))),
                    NovelUi.Button("上移", () => Run(ct => MoveRuleAsync(rule, -1, ct))), NovelUi.Button("下移", () => Run(ct => MoveRuleAsync(rule, 1, ct))),
                    NovelUi.Button("编辑", () => Run(ct => EditRuleAsync(rule, ct))), NovelUi.Button("删除", () => Run(async ct =>
                    { if (await NovelUi.ConfirmAsync(this, "删除规则", $"删除“{rule.Name}”？", "删除", ct)) await SaveRulesAsync(module.Library.Rules.Where(r => r.Id != rule.Id).ToArray(), ct); })));
                DockPanel.SetDock(actions, Dock.Bottom); line.Children.Add(actions);
                var text = new StackPanel(); text.Children.Add(NovelUi.Text(rule.Name.Length > 0 ? rule.Name : rule.Pattern, 16));
                text.Children.Add(NovelUi.Text(rule.Group + " · " + rule.Pattern + " → " + rule.Replacement, 12, true)); line.Children.Add(text); rows.Children.Add(line);
            }
            if (rows.Children.Count == 0) rows.Children.Add(NovelUi.Text("暂无净化规则", muted: true));
        }
        search.TextChanged += (_, _) => { filter = search.Text; Fill(); };
        groups.SelectionChanged += (_, _) => { group = groups.SelectedIndex <= 0 ? "" : groups.SelectedItem as string ?? ""; Fill(); }; Fill();
        return NovelUi.Scroll(rows);
    }
    private async Task EditRuleAsync(NovelRule rule, CancellationToken token)
    {
        var panel = new StackPanel(); var fields = new Dictionary<string, TextBox>();
        void Field(string name, string value) { panel.Children.Add(NovelUi.Text(name)); var input = NovelUi.Input(name, value, 440); fields.Add(name, input); panel.Children.Add(input); }
        Field("名称", rule.Name); Field("分组", rule.Group); Field("匹配内容", rule.Pattern); Field("替换内容", rule.Replacement);
        Field("作用范围（书名或源 URL）", rule.Scope); Field("排除范围", rule.ExcludeScope); Field("超时（毫秒）", rule.TimeoutMillisecond.ToString()); Field("排序", rule.Order.ToString());
        CheckBox Check(string label, bool value) { var box = new CheckBox { Content = label, IsChecked = value, Margin = new(0, 0, 12, 12) }; panel.Children.Add(box); return box; }
        var enabled = Check("启用", rule.Enabled); var regex = Check("正则表达式", rule.IsRegex); var title = Check("标题", rule.ScopeTitle); var content = Check("正文", rule.ScopeContent);
        await NovelUi.DialogAsync(this, "替换净化", NovelUi.Scroll(panel), "保存", async ct =>
        {
            if (!int.TryParse(fields["超时（毫秒）"].Text, out int timeout) || timeout <= 0 || !long.TryParse(fields["排序"].Text, out long order) || order is < -9007199254740991 or > 9007199254740991) throw new InvalidDataException();
            var next = rule with { Name = fields["名称"].Text, Group = fields["分组"].Text, Pattern = fields["匹配内容"].Text,
                Replacement = fields["替换内容"].Text, Scope = fields["作用范围（书名或源 URL）"].Text, ExcludeScope = fields["排除范围"].Text,
                TimeoutMillisecond = timeout, Order = order, Enabled = enabled.IsChecked == true, IsRegex = regex.IsChecked == true,
                ScopeTitle = title.IsChecked == true, ScopeContent = content.IsChecked == true };
            await SaveRuleAsync(next, ct); return true;
        }, token);
    }
    private async Task SaveRuleAsync(NovelRule rule, CancellationToken token)
    {
        await module.Rules.ValidateAsync(rule, token);
        var current = module.Library.Rules.ToList(); int at = current.FindIndex(r => r.Id == rule.Id);
        if (at < 0) current.Add(rule); else current[at] = rule;
        await SaveRulesAsync(current, token);
    }
    private async Task SaveRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token)
    {
        await module.Library.SaveRulesAsync(rules, token);
        await module.ReprocessCurrentAsync(token);
        Refresh();
    }
    private Task MoveRuleAsync(NovelRule rule, int delta, CancellationToken token)
    {
        var current = module.Library.Rules.OrderBy(r => r.Order).ToArray(); int at = Array.FindIndex(current, r => r.Id == rule.Id);
        int next = at + delta; if (next < 0 || next >= current.Length) return Task.CompletedTask;
        (current[at], current[next]) = (current[next], current[at]);
        return SaveRulesAsync(current.Select((r, i) => r with { Order = i }).ToArray(), token);
    }
    private async Task ImportRuleFileAsync(CancellationToken token)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "规则 JSON|*.json|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var file in dialog.FileNames) await ImportRulesAsync(await File.ReadAllTextAsync(file, token), token);
    }
    private async Task ImportRuleUrlAsync(CancellationToken token)
    {
        var input = NovelUi.Input("规则 URL");
        await NovelUi.DialogAsync(this, "导入净化规则", input, "导入", async ct =>
        {
            if (!Uri.TryCreate(input.Text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) throw new InvalidDataException();
            using var http = new HttpClient(new HttpClientHandler { UseCookies = false });
            await ImportRulesAsync(await http.GetStringAsync(url, ct), ct); return true;
        }, token);
    }
    internal async Task ImportRulesAsync(string text, CancellationToken token)
    {
        using var json = JsonDocument.Parse(text);
        var input = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement : json.RootElement.GetProperty("rules");
        var next = module.Library.Rules.ToList(); var failures = new List<string>();
        int index = 0;
        foreach (var item in input.EnumerateArray())
        {
            index++;
            try
            {
                // Reader exports numeric IDs. Normalize only this import boundary; local
                // storage and CRUD retain one stable string identity.
                var value = System.Text.Json.Nodes.JsonNode.Parse(item.GetRawText())!.AsObject();
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                    value["id"] = id.GetRawText();
                foreach (string field in new[] { "name", "group", "scope", "excludeScope" })
                    if (item.TryGetProperty(field, out var optional) && optional.ValueKind == JsonValueKind.Null) value.Remove(field);
                var rule = NovelJson.Decode<NovelRule>(JsonSerializer.SerializeToElement(value, NovelJson.Options));
                await module.Rules.ValidateAsync(rule, token);
                int at = next.FindIndex(r => r.Id == rule.Id); if (at < 0) next.Add(rule); else next[at] = rule;
            }
            catch (OperationCanceledException) { throw; }
            catch { failures.Add($"规则 {index} 无效"); }
        }
        await SaveRulesAsync(next, token); module.Notify(failures.Count == 0 ? "净化规则已导入" : string.Join("\n", failures));
    }
    private async Task ExportRulesAsync(CancellationToken token)
    {
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "replace-rules.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(module.Library.Rules, NovelJson.Options), token); module.Notify("净化规则已导出");
    }
    private async Task EditAppearanceAsync(CancellationToken token)
    {
        var editor = new NovelAppearanceEditor(module.Library.Appearance);
        await NovelUi.DialogAsync(this, "阅读排版", NovelUi.Scroll(editor), "保存", async ct =>
        {
            var prior = module.Library.Appearance; var next = editor.Read(); await module.Library.SaveAppearanceAsync(next, ct);
            if (prior.Conversion != next.Conversion) await module.ReprocessCurrentAsync(ct);
            return true;
        }, token);
    }
    private static TextBox MultiLine(string name, string text = "")
    {
        var input = new TextBox { Text = text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(input, name); return input;
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

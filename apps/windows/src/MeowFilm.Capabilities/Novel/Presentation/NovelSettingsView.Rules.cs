using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Desktop;
using Microsoft.Win32;

namespace MeowFilm.Capabilities;

public sealed partial class NovelSettingsView
{
    private IReadOnlyList<NovelRule> VisibleRules() => module.Library.Rules.OrderBy(r => r.Order).ThenBy(r => r.Name, StringComparer.CurrentCulture).Where(rule =>
        (!ungroupedRules || rule.Group.Length == 0) && (ruleGroup is null || NovelGroups.Split(rule.Group).Contains(ruleGroup)) &&
        (ruleQuery.Trim().Length == 0 || new[] { rule.Name, rule.Group, rule.Pattern, rule.Replacement, rule.Scope }.Any(value => value.Contains(ruleQuery.Trim(), StringComparison.CurrentCultureIgnoreCase)))).ToArray();
    private void RefreshRules()
    {
        ruleList.SetRows(VisibleRules().Select(rule => new ManagementRow(rule.Id, rule.Name.Length == 0 ? rule.Pattern : rule.Name, rule.Group, rule.Enabled,
            () => Run(ct => EditRuleAsync(rule, ct)), enabled => Run(ct => SaveRuleAsync(rule with { Enabled = enabled }, ct)),
            Menu(Item("置顶", () => Run(ct => MoveRuleAsync(rule, true, ct))), Item("置底", () => Run(ct => MoveRuleAsync(rule, false, ct))), Item("删除", () => Run(ct => RemoveRulesAsync([rule], ct)))))).ToArray(),
            module.Library.Rules.Count, module.Library.Rules.Select(r => r.Id), module.Library.Rules.Count == 0 ? "暂无替换规则，请先新建" : "没有匹配的替换规则"); RuleMenus();
    }
    private void RuleMenus()
    {
        var menu = Menu(Item("分组管理", () => Run(ct => ManageGroupsAsync(false, ct))), Item("全部分组", () => { ruleGroup = null; ungroupedRules = false; RefreshRules(); }, ruleGroup is null && !ungroupedRules), Item("无分组", () => { ruleGroup = null; ungroupedRules = true; RefreshRules(); }, ungroupedRules));
        foreach (string group in module.Library.Rules.SelectMany(r => NovelGroups.Split(r.Group)).Distinct().Order(StringComparer.CurrentCulture)) menu.Items.Add(Item(group, () => { ruleGroup = group; ungroupedRules = false; RefreshRules(); }, ruleGroup == group)); ruleGroups.ContextMenu = menu;
        ruleMore.ContextMenu = Menu(Item("新建替换", () => Run(ct => EditRuleAsync(new NovelRule { Order = module.Library.Rules.Count }, ct))),
            Item("本地导入", () => Run(ImportRuleFileAsync)), Item("网络导入", () => Run(ImportRuleUrlAsync)), Item("二维码导入", () => Run(ImportRuleQrAsync)), Item("导出替换规则", () => Run(ct => ExportRulesAsync(module.Library.Rules, ct))));
        ruleList.More.ContextMenu = Menu(Item("导出选中规则", () => Run(ct => ExportRulesAsync(module.Library.Rules.Where(r => ruleList.Selected.Contains(r.Id)).ToArray(), ct))));
    }
    private async Task EditRuleAsync(NovelRule rule, CancellationToken token)
    {
        var editor = new NovelRuleEditor(rule, module.Notify) { Height = Math.Max(300, Window.GetWindow(this)?.ActualHeight - 220 ?? 520) };
        await NovelUi.DialogAsync(this, "替换规则编辑", editor, "保存", async ct => { await SaveRuleAsync(editor.Read(), ct); return true; }, token);
    }
    private async Task SaveRuleAsync(NovelRule rule, CancellationToken token)
    {
        await module.Rules.ValidateAsync(rule, token); var next = module.Library.Rules.ToList(); int at = next.FindIndex(r => r.Id == rule.Id);
        if (at < 0) next.Add(rule); else next[at] = rule; await SaveRulesAsync(next, token);
    }
    private async Task SaveRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token)
    {
        await module.Library.SaveRulesAsync(rules, token); await module.ReprocessCurrentAsync(token); Refresh();
    }
    private Task MoveRuleAsync(NovelRule rule, bool top, CancellationToken token)
    {
        var next = module.Library.Rules.OrderBy(r => r.Order).Where(r => r.Id != rule.Id).ToList(); if (top) next.Insert(0, rule); else next.Add(rule);
        return SaveRulesAsync(next.Select((r, i) => r with { Order = i }).ToArray(), token);
    }
    private async Task RemoveRulesAsync(NovelRule[] rules, CancellationToken token)
    {
        if (rules.Length == 0 || !await NovelUi.ConfirmAsync(this, "删除替换规则", $"删除所选 {rules.Length} 条规则？", "删除", token)) return;
        var ids = rules.Select(r => r.Id).ToHashSet(); await SaveRulesAsync(module.Library.Rules.Where(r => !ids.Contains(r.Id)).ToArray(), token);
    }
    private async Task ImportRuleFileAsync(CancellationToken token)
    {
        var dialog = new OpenFileDialog { Filter = "规则文件|*.json;*.txt|所有文件|*.*" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) await PreviewRulesAsync(await File.ReadAllTextAsync(dialog.FileName, token), token);
    }
    private async Task ImportRuleQrAsync(CancellationToken token)
    {
        var dialog = new OpenFileDialog { Filter = "二维码图片|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*" }; if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string text = await ReadRuleQrAsync(dialog.FileName, token);
        await PreviewRulesAsync(text, token);
    }
    internal static async Task<string> ReadRuleQrAsync(string path, CancellationToken token)
    {
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        var image = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); image.Freeze();
        int stride = checked(image.PixelWidth * 4); var pixels = new byte[checked(stride * image.PixelHeight)]; image.CopyPixels(pixels, stride, 0);
        string text = await Task.Run(() =>
        {
            var reader = new ZXing.BarcodeReaderGeneric { Options = new ZXing.Common.DecodingOptions { PossibleFormats = [ZXing.BarcodeFormat.QR_CODE], TryHarder = true } };
            return reader.Decode(pixels, image.PixelWidth, image.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32)?.Text ?? throw new InvalidDataException("未识别到二维码内容");
        }, token);
        token.ThrowIfCancellationRequested(); return text;
    }
    private async Task ImportRuleUrlAsync(CancellationToken token)
    {
        var panel = new ManagementForm(true); var input = panel.Field("替换规则地址", "", id: "management.import.url", placeholder: "请输入网络替换规则地址"); string text = "";
        bool accepted = await NovelUi.DialogAsync(this, "网络导入", panel, "确定", async ct =>
        {
            if (!Uri.TryCreate(input.Text.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0) throw new InvalidDataException("网络地址无效");
            using var http = new HttpClient(new HttpClientHandler { UseCookies = false }); text = await http.GetStringAsync(url, ct); return true;
        }, token);
        if (accepted) await PreviewRulesAsync(text, token);
    }
    private async Task<(NovelRule[] Rules, string[] Failures)> ParseRulesAsync(string text, CancellationToken token)
    {
        using var document = JsonDocument.Parse(text.TrimStart('\uFEFF'));
        var root = document.RootElement;
        var entries = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rules", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array ? wrapped.EnumerateArray().ToArray() : [root];
        var rules = new List<NovelRule>(); var failures = new List<string>();
        for (int index = 0; index < entries.Length; index++)
        {
            try { var rule = NovelRuleImport.Parse(entries[index].GetRawText()).Single(); await module.Rules.ValidateAsync(rule, token); rules.Add(rule); }
            catch (OperationCanceledException) { throw; }
            catch { failures.Add($"规则 {index + 1} 无效"); }
        }
        return (rules.ToArray(), failures.ToArray());
    }
    private async Task PreviewRulesAsync(string text, CancellationToken token)
    {
        var (rules, failures) = await ParseRulesAsync(text, token);
        var current = module.Library.Rules.ToDictionary(r => r.Id); var statuses = rules.Select(r => NovelRuleImport.Status(r, current.GetValueOrDefault(r.Id))).ToArray();
        var selection = new ManagementImportSelection(rules.Select((rule, index) => new ManagementImportItem(rule.Name.Length == 0 ? rule.Pattern : rule.Name, rule.Group, statuses[index])).ToArray(), Enumerable.Range(0, rules.Length).Where(index => statuses[index] != "已有"));
        var panel = new StackPanel(); panel.Children.Add(selection); if (failures.Length > 0) panel.Children.Add(NovelUi.Text(string.Join("\n", failures), 12));
        await NovelUi.DialogAsync(this, "导入替换规则", panel, "确定导入", async ct =>
        {
            if (selection.Selected.Count == 0) throw new InvalidDataException("请选择要导入的替换规则");
            await MergeRulesAsync(rules.Where((_, index) => selection.Selected.Contains(index)).ToArray(), ct); return true;
        }, token);
    }
    // The accepted import operation is separate from the preview UI and retains
    // stable external IDs. It is also used by the rule-consumer integration tests.
    internal async Task ImportRulesAsync(string text, CancellationToken token)
    {
        var (rules, failures) = await ParseRulesAsync(text, token); await MergeRulesAsync(rules, token);
        module.Notify(failures.Length == 0 ? "净化规则已导入" : string.Join("\n", failures));
    }
    private Task MergeRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token)
    {
        var next = module.Library.Rules.ToList(); foreach (var rule in rules) { int at = next.FindIndex(r => r.Id == rule.Id); if (at < 0) next.Add(rule); else next[at] = rule; } return SaveRulesAsync(next, token);
    }
    private async Task ExportRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token)
    {
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "replace-rules.json" }; if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(rules, NovelJson.Options), token); module.Notify("净化规则已导出");
    }
    private async Task ManageGroupsAsync(bool sources, CancellationToken token)
    {
        string[] ids = (sources ? sourceList.Selected : ruleList.Selected).ToArray();
        string[] Groups() => (sources ? module.Sources.Sources.Select(s => s.Group) : module.Library.Rules.Select(r => r.Group)).SelectMany(NovelGroups.Split).Distinct().Order(StringComparer.CurrentCulture).ToArray();
        while (!token.IsCancellationRequested)
        {
            using var choice = CancellationTokenSource.CreateLinkedTokenSource(token);
            (string? Group, bool Remove)? selected = null;
            var panel = new StackPanel(); var list = new StackPanel();
            void Choose(string? group, bool remove) { selected = (group, remove); choice.Cancel(); }
            var add = NovelUi.Button("新增分组", () => Choose(null, false)); add.IsEnabled = ids.Length > 0; add.ToolTip = "为所选项目新增分组"; panel.Children.Add(add); panel.Children.Add(list);
            foreach (string group in Groups())
            {
                var row = new DockPanel(); var actions = NovelUi.Bar(NovelUi.Button("编辑", () => Choose(group, false)), NovelUi.Button("删除", () => Choose(group, true))); DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions); row.Children.Add(NovelUi.Text(group)); list.Children.Add(row);
            }
            if (list.Children.Count == 0) list.Children.Add(NovelUi.Text("暂无分组", muted: true));
            // Close and await one modal before opening the next. Every edit and
            // its physical drain remains owned by this settings operation.
            await NovelUi.DialogAsync(this, "分组管理", panel, "完成", _ => Task.FromResult(true), choice.Token);
            if (selected is not { } next || token.IsCancellationRequested) return;
            if (next.Remove) await RemoveAsync(next.Group!); else await EditAsync(next.Group);
        }
        async Task EditAsync(string? old)
        {
            var form = new ManagementForm(); var input = form.Field("分组名称", old ?? "");
            await NovelUi.DialogAsync(this, old is null ? "新增分组" : "编辑分组", form, "保存", async ct =>
            {
                string name = input.Text.Trim(); if (NovelGroups.Split(name).Length != 1) throw new InvalidDataException("请填写单个分组名称");
                await ChangeAsync(old, name, ct); return true;
            }, token);
        }
        async Task RemoveAsync(string group)
        {
            await NovelUi.DialogAsync(this, "删除分组", NovelUi.Text($"移除分组“{group}”？"), "删除", async ct => { await ChangeAsync(group, null, ct); return true; }, token);
        }
        async Task ChangeAsync(string? old, string? next, CancellationToken ct)
        {
            if (sources)
            {
                foreach (var source in module.Sources.Sources.Where(s => old is null ? ids.Contains(s.InstallationId) : NovelGroups.Split(s.Group).Contains(old)).ToArray())
                    await module.Sources.UpdateAsync(source, new { group = NovelGroups.Replacing(source.Group, old ?? "", next) }, ct);
            }
            else await SaveRulesAsync(module.Library.Rules.Select(rule => (old is null ? ids.Contains(rule.Id) : NovelGroups.Split(rule.Group).Contains(old)) ? rule with { Group = NovelGroups.Replacing(rule.Group, old ?? "", next) } : rule).ToArray(), ct);
            Refresh();
        }
    }
}

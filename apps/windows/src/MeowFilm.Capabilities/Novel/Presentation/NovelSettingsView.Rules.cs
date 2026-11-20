using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

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
        foreach (string group in module.Library.Rules.SelectMany(r => NovelGroups.Split(r.Group)).Distinct().Order(StringComparer.CurrentCulture)) menu.Items.Add(Item(group, () => { ruleGroup = group; ungroupedRules = false; RefreshRules(); }, ruleGroup == group)); ruleGroups.ContextFlyout = menu;
        ruleMore.ContextFlyout = Menu(Item("新建替换", () => Run(ct => EditRuleAsync(new NovelRule { Order = module.Library.Rules.Count }, ct))),
            Item("本地导入", () => Run(ImportRuleFileAsync)), Item("网络导入", () => Run(ImportRuleUrlAsync)), Item("二维码导入", () => Run(ImportRuleQrAsync)), Item("导出替换规则", () => Run(ct => ExportRulesAsync(module.Library.Rules, ct))));
        ruleList.More.ContextFlyout = Menu(Item("导出选中规则", () => Run(ct => ExportRulesAsync(module.Library.Rules.Where(r => ruleList.Selected.Contains(r.Id)).ToArray(), ct))));
    }
    private async Task EditRuleAsync(NovelRule rule, CancellationToken token)
    {
        var editor = new NovelRuleEditor(rule, module.Notify);
        await NovelUi.DialogAsync(this, "替换规则编辑", editor, "保存", async ct => { await SaveRuleAsync(editor.Read(), ct); return true; }, token, applicationOverlay: true);
    }
    private async Task SaveRuleAsync(NovelRule rule, CancellationToken token)
    {
        ValidateRuleData(rule);
        var next = module.Library.Rules.ToList(); int at = next.FindIndex(r => r.Id == rule.Id);
        if (at < 0) next.Add(rule); else next[at] = rule; await SaveRulesAsync(next, token);
    }
    private static void ValidateRuleData(NovelRule rule)
    {
        if (string.IsNullOrEmpty(rule.Id) || rule.Pattern is null || rule.Name is null || rule.Group is null ||
            rule.Replacement is null || rule.Scope is null || rule.ExcludeScope is null)
            throw new InvalidDataException("替换规则格式不对");
        if (rule.TimeoutMillisecond <= 0) throw new InvalidDataException("超时毫秒数无效");
    }
    private async Task SaveRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token)
    {
        await module.Library.SaveRulesAsync(rules, token); Refresh();
    }
    private Task MoveRuleAsync(NovelRule rule, bool top, CancellationToken token)
    {
        var next = module.Library.Rules.OrderBy(r => r.Order).Where(r => r.Id != rule.Id).ToList(); if (top) next.Insert(0, rule); else next.Add(rule);
        return SaveRulesAsync(next.Select((r, i) => r with { Order = i }).ToArray(), token);
    }
    private async Task RemoveRulesAsync(NovelRule[] rules, CancellationToken token)
    {
        if (rules.Length == 0 || !await NovelUi.ConfirmAsync(this, "删除替换规则", $"删除所选 {rules.Length} 条规则？", "删除", token, applicationOverlay: true)) return;
        var ids = rules.Select(r => r.Id).ToHashSet(); await SaveRulesAsync(module.Library.Rules.Where(r => !ids.Contains(r.Id)).ToArray(), token);
    }
    private async Task ImportRuleFileAsync(CancellationToken token)
    {
        string? path = await NovelPresentation.OpenFileAsync(this, ".json", ".txt", "*");
        token.ThrowIfCancellationRequested(); if (path is not null) await PreviewRulesAsync(await File.ReadAllTextAsync(path, token), token);
    }
    private async Task ImportRuleQrAsync(CancellationToken token)
    {
        string? path = await NovelPresentation.OpenFileAsync(this, ".png", ".jpg", ".jpeg", ".bmp", "*");
        token.ThrowIfCancellationRequested(); if (path is null) return;
        string text = await ReadRuleQrAsync(path, token);
        await PreviewRulesAsync(text, token);
    }
    internal static async Task<string> ReadRuleQrAsync(string path, CancellationToken token)
    {
        var image = await Task.Run(() => NovelImagePixels.Decode(File.ReadAllBytes(path), 0), token);
        var pixels = image.Pixels;
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
        }, token, applicationOverlay: true);
        if (accepted) await PreviewRulesAsync(text, token);
    }
    private static (NovelRule[] Rules, string[] Failures) ParseRules(string text, CancellationToken token)
    {
        using var document = JsonDocument.Parse(text.TrimStart('\uFEFF'));
        var root = document.RootElement;
        var entries = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rules", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array ? wrapped.EnumerateArray().ToArray() : [root];
        var rules = new List<NovelRule>(); var failures = new List<string>();
        for (int index = 0; index < entries.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var rule = NovelRuleImport.Parse(entries[index].GetRawText()).Single();
                ValidateRuleData(rule); rules.Add(rule);
            }
            catch (OperationCanceledException) { throw; }
            catch { failures.Add($"规则 {index + 1} 无效"); }
        }
        return (rules.ToArray(), failures.ToArray());
    }
    private async Task PreviewRulesAsync(string text, CancellationToken token)
    {
        var (rules, failures) = ParseRules(text, token);
        var current = module.Library.Rules.ToDictionary(r => r.Id); var statuses = rules.Select(r => NovelRuleImport.Status(r, current.GetValueOrDefault(r.Id))).ToArray();
        var selection = new ManagementImportSelection(rules.Select((rule, index) => new ManagementImportItem(rule.Name.Length == 0 ? rule.Pattern : rule.Name, rule.Group, statuses[index])).ToArray(), Enumerable.Range(0, rules.Length).Where(index => statuses[index] != "已有"));
        var panel = new StackPanel(); panel.Children.Add(selection); if (failures.Length > 0) panel.Children.Add(NovelUi.Text(string.Join("\n", failures), 12));
        await NovelUi.DialogAsync(this, "导入替换规则", panel, "确定导入", async ct =>
        {
            if (selection.Selected.Count == 0) throw new InvalidDataException("请选择要导入的替换规则");
            await MergeRulesAsync(rules.Where((_, index) => selection.Selected.Contains(index)).ToArray(), ct); return true;
        }, token, applicationOverlay: true);
    }
    // The accepted import operation is separate from the preview UI and retains
    // stable external IDs. It is also used by the rule-consumer integration tests.
    internal async Task ImportRulesAsync(string text, CancellationToken token)
    {
        var (rules, failures) = ParseRules(text, token); await MergeRulesAsync(rules, token);
        module.Notify(failures.Length == 0 ? "净化规则已导入" : string.Join("\n", failures));
    }
    private Task MergeRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token)
    {
        var next = module.Library.Rules.ToList(); foreach (var rule in rules) { int at = next.FindIndex(r => r.Id == rule.Id); if (at < 0) next.Add(rule); else next[at] = rule; } return SaveRulesAsync(next, token);
    }
    private async Task ExportRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token)
    {
        string? path = await NovelPresentation.SaveJsonAsync(this, "replace-rules.json");
        token.ThrowIfCancellationRequested(); if (path is null) return;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rules, NovelJson.Options), token); module.Notify("净化规则已导出");
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
            var add = NovelUi.Button("新增分组", () => Choose(null, false)); add.IsEnabled = ids.Length > 0; ToolTipService.SetToolTip(add, "为所选项目新增分组"); panel.Children.Add(add); panel.Children.Add(list);
            foreach (string group in Groups())
            {
                var row = new DockPanel(); var actions = NovelUi.Bar(NovelUi.Button("编辑", () => Choose(group, false)), NovelUi.Button("删除", () => Choose(group, true))); DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions); row.Children.Add(NovelUi.Text(group)); list.Children.Add(row);
            }
            if (list.Children.Count == 0) list.Children.Add(NovelUi.Text("暂无分组", muted: true));
            // Close and await one modal before opening the next. Every edit and
            // its physical drain remains owned by this settings operation.
            await NovelUi.DialogAsync(this, "分组管理", panel, "完成", _ => Task.FromResult(true), choice.Token, applicationOverlay: true);
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
            }, token, applicationOverlay: true);
        }
        async Task RemoveAsync(string group)
        {
            await NovelUi.DialogAsync(this, "删除分组", NovelUi.Text($"移除分组“{group}”？"), "删除", async ct => { await ChangeAsync(group, null, ct); return true; }, token, applicationOverlay: true);
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

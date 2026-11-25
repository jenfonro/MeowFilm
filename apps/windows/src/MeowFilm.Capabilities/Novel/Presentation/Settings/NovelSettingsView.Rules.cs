using System.IO;
using System.Text.Json;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class NovelSettingsView
{
    private IReadOnlyList<NovelRule> VisibleRules() => module.Library.Rules.OrderBy(r => r.Order).ThenBy(r => r.Name, StringComparer.CurrentCulture).Where(rule =>
        (!ungroupedRules || rule.Group.Length == 0) && (ruleGroup is null || NovelGroups.Split(rule.Group).Contains(ruleGroup)) &&
        (ruleQuery.Trim().Length == 0 || new[] { rule.Name, rule.Group, rule.Pattern, rule.Replacement, rule.Scope }.Any(value => value.Contains(ruleQuery.Trim(), StringComparison.CurrentCultureIgnoreCase)))).ToArray();

    private void RefreshRules()
    {
        var allRules = module.Library.Rules;
        var validIds = allRules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string stale in ruleRowMenus.Keys.Where(id => !validIds.Contains(id)).ToArray())
            ruleRowMenus.Remove(stale);
        RuleList.SetRows(VisibleRules().Select(rule => new ManagementRow(rule.Id, rule.Name.Length == 0 ? rule.Pattern : rule.Name, rule.Group, rule.Enabled,
            () => Run(ct => EditRuleAsync(rule, ct)), enabled => Run(ct => SaveRuleAsync(rule with { Enabled = enabled }, ct)),
            GetRuleRowMenu(rule.Id))).ToArray(),
            allRules.Count, validIds, allRules.Count == 0 ? "暂无替换规则，请先新建" : "没有匹配的替换规则");
    }

    private void InitRuleMenus()
    {
        var groupMenu = Menu();
        groupMenu.Opening += (_, _) => PopulateRuleGroupsMenu(groupMenu);
        ruleGroups.ContextFlyout = groupMenu;

        ruleMore.ContextFlyout = Menu(
            Item("新建替换", () => Run(ct => EditRuleAsync(new NovelRule { Order = module.Library.Rules.Count }, ct))),
            Item("本地导入", () => Run(ImportRuleFileAsync)),
            Item("网络导入", () => Run(ImportRuleUrlAsync)),
            Item("二维码导入", () => Run(ImportRuleQrAsync)),
            Item("导出替换规则", () => Run(ct => ExportRulesAsync(module.Library.Rules, ct))));

        RuleList.More.ContextFlyout = Menu(
            Item("导出选中规则", () => Run(ct => ExportRulesAsync(module.Library.Rules.Where(r => RuleList.Selected.Contains(r.Id)).ToArray(), ct))));
    }

    private void PopulateRuleGroupsMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Item("分组管理", () => Run(ct => ManageGroupsAsync(false, ct))));
        menu.Items.Add(Item("全部分组", () => { ruleGroup = null; ungroupedRules = false; RefreshRules(); }, ruleGroup is null && !ungroupedRules));
        menu.Items.Add(Item("无分组", () => { ruleGroup = null; ungroupedRules = true; RefreshRules(); }, ungroupedRules));
        foreach (string group in module.Library.Rules.SelectMany(r => NovelGroups.Split(r.Group)).Distinct().Order(StringComparer.CurrentCulture))
            menu.Items.Add(Item(group, () => { ruleGroup = group; ungroupedRules = false; RefreshRules(); }, ruleGroup == group));
    }

    private MenuFlyout GetRuleRowMenu(string ruleId)
    {
        if (ruleRowMenus.TryGetValue(ruleId, out var cached)) return cached;
        var menu = Menu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var rule = module.Library.Rules.FirstOrDefault(r => r.Id == ruleId);
            if (rule is null) return;
            menu.Items.Add(Item("置顶", () => Run(ct => MoveRuleAsync(rule, true, ct))));
            menu.Items.Add(Item("置底", () => Run(ct => MoveRuleAsync(rule, false, ct))));
            menu.Items.Add(Item("删除", () => Run(ct => RemoveRulesAsync([rule], ct))));
        };
        ruleRowMenus[ruleId] = menu;
        return menu;
    }

    private async Task EditRuleAsync(NovelRule rule, CancellationToken token)
    {
        var editor = new NovelRuleEditor(rule, module.Notify);
        var request = new DesktopDialogRequest
        {
            Title = "替换规则编辑",
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            MaxWidth = 560,
            DefaultButton = DesktopDialogDefaultButton.Primary,
            OnPrimaryAsync = async () =>
            {
                try
                {
                    await SaveRuleAsync(editor.Read(), token);
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

    private async Task SaveRuleAsync(NovelRule rule, CancellationToken token)
    {
        NovelRuleImport.Validate(rule);
        var next = module.Library.Rules.ToList();
        int at = next.FindIndex(r => r.Id == rule.Id);
        if (at < 0) next.Add(rule); else next[at] = rule;
        await SaveRulesAsync(next, token);
    }

    private Task SaveRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token) =>
        module.Library.SaveRulesAsync(rules, token);

    private Task MoveRuleAsync(NovelRule rule, bool top, CancellationToken token)
    {
        var next = module.Library.Rules.OrderBy(r => r.Order).Where(r => r.Id != rule.Id).ToList();
        if (top) next.Insert(0, rule); else next.Add(rule);
        return SaveRulesAsync(next.Select((r, i) => r with { Order = i }).ToArray(), token);
    }

    private async Task RemoveRulesAsync(NovelRule[] rules, CancellationToken token)
    {
        if (rules.Length == 0) return;
        var request = new DesktopDialogRequest
        {
            Title = "删除替换规则",
            Message = $"删除所选 {rules.Length} 条规则？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Close
        };
        if (await DesktopModalHost.ShowDialogAsync(this, request, token) != DesktopDialogResult.Primary) return;
        var ids = rules.Select(r => r.Id).ToHashSet();
        await SaveRulesAsync(module.Library.Rules.Where(r => !ids.Contains(r.Id)).ToArray(), token);
    }

    private async Task ImportRuleFileAsync(CancellationToken token)
    {
        string? path = await NovelPresentation.OpenFileAsync(this, ".json", ".txt", "*");
        token.ThrowIfCancellationRequested();
        if (path is not null) await PreviewRulesAsync(await File.ReadAllTextAsync(path, token), token);
    }

    private async Task ImportRuleQrAsync(CancellationToken token)
    {
        string? path = await NovelPresentation.OpenFileAsync(this, ".png", ".jpg", ".jpeg", ".bmp", "*");
        token.ThrowIfCancellationRequested();
        if (path is null) return;
        string text = await ReadRuleQrAsync(path, token);
        await PreviewRulesAsync(text, token);
    }

    private static Task<string> ReadRuleQrAsync(string path, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var image = NovelImagePixels.Decode(File.ReadAllBytes(path), 0);
        token.ThrowIfCancellationRequested();
        var reader = new ZXing.BarcodeReaderGeneric { Options = new ZXing.Common.DecodingOptions { PossibleFormats = [ZXing.BarcodeFormat.QR_CODE], TryHarder = true } };
        return reader.Decode(image.Pixels, image.PixelWidth, image.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32)?.Text
            ?? throw new InvalidDataException("未识别到二维码内容");
    }, token);

    private async Task ImportRuleUrlAsync(CancellationToken token)
    {
        var inputView = new NovelImportInputDialogView("替换规则地址", "", rows: 1, automationId: "management.import.url", placeholder: "请输入网络替换规则地址");
        string text = "";
        var request = new DesktopDialogRequest
        {
            Title = "网络导入",
            Content = inputView,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            MaxWidth = 520,
            DefaultButton = DesktopDialogDefaultButton.Primary,
            OnOpened = () => inputView.FocusInput(),
            OnPrimaryAsync = async () =>
            {
                try
                {
                    text = await NovelRuleImport.FetchUrlAsync(inputView.InputText.Trim(), token);
                    return true;
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception error)
                {
                    inputView.ShowError(error is InvalidDataException ? error.Message : "网络导入失败，请重试");
                    return false;
                }
            }
        };
        if (await DesktopModalHost.ShowDialogAsync(this, request, token) == DesktopDialogResult.Primary)
            await PreviewRulesAsync(text, token);
    }

    private async Task PreviewRulesAsync(string text, CancellationToken token)
    {
        var (rules, failures) = NovelRuleImport.ParseBatch(text, token);
        var current = module.Library.Rules.ToDictionary(r => r.Id);
        var statuses = rules.Select(r => NovelRuleImport.Status(r, current.GetValueOrDefault(r.Id))).ToArray();
        var entries = rules.Select((rule, index) => (
            Title: rule.Name.Length == 0 ? rule.Pattern : rule.Name,
            Detail: rule.Group,
            Status: statuses[index])).ToArray();
        var initial = Enumerable.Range(0, rules.Length).Where(index => statuses[index] != "已有");
        var previewView = new NovelImportPreviewDialogView(entries, initial, failures);
        var request = new DesktopDialogRequest
        {
            Title = "导入替换规则",
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
                    if (selected.Count == 0) throw new InvalidDataException("请选择要导入的替换规则");
                    await MergeRulesAsync(rules.Where((_, index) => selected.Contains(index)).ToArray(), token);
                    return true;
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception error)
                {
                    previewView.ShowError(error is InvalidDataException ? error.Message : "导入替换规则失败，请重试");
                    return false;
                }
            }
        };
        await DesktopModalHost.ShowDialogAsync(this, request, token);
    }

    private Task MergeRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token)
    {
        var next = module.Library.Rules.ToList();
        foreach (var rule in rules)
        {
            int at = next.FindIndex(r => r.Id == rule.Id);
            if (at < 0) next.Add(rule); else next[at] = rule;
        }
        return SaveRulesAsync(next, token);
    }

    private async Task ExportRulesAsync(IReadOnlyList<NovelRule> rules, CancellationToken token)
    {
        string? path = await NovelPresentation.SaveJsonAsync(this, "replace-rules.json");
        token.ThrowIfCancellationRequested();
        if (path is null) return;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rules, NovelJson.Options), token);
        module.Notify("净化规则已导出");
    }

    private Task ManageGroupsAsync(bool sources, CancellationToken token)
    {
        string[] ids = (sources ? SourceList.Selected : RuleList.Selected).ToArray();
        string[] ReadGroups() => (sources ? module.Sources.Sources.Select(s => s.Group) : module.Library.Rules.Select(r => r.Group))
            .SelectMany(NovelGroups.Split)
            .Distinct()
            .Order(StringComparer.CurrentCulture)
            .ToArray();

        async Task ChangeAsync(string? old, string? next, CancellationToken ct)
        {
            if (sources)
            {
                foreach (var source in module.Sources.Sources.Where(s => old is null ? ids.Contains(s.InstallationId) : NovelGroups.Split(s.Group).Contains(old)).ToArray())
                    await module.Sources.UpdateAsync(source, new { group = NovelGroups.Replacing(source.Group, old ?? "", next) }, ct);
            }
            else
            {
                await SaveRulesAsync(module.Library.Rules.Select(rule => (old is null ? ids.Contains(rule.Id) : NovelGroups.Split(rule.Group).Contains(old))
                    ? rule with { Group = NovelGroups.Replacing(rule.Group, old ?? "", next) }
                    : rule).ToArray(), ct);
            }
        }

        return NovelGroupManagerDialogView.ShowAsync(this, ids.Length > 0, ReadGroups, ChangeAsync, token);
    }
}

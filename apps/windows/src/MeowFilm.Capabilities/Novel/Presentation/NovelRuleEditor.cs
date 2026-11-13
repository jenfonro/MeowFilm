using System.IO;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal sealed class NovelRuleEditor : DockPanel
{
    private readonly NovelRule original;
    private readonly ManagementForm form = new(true);
    private TextBox name = null!, group = null!, pattern = null!, replacement = null!, scope = null!, exclude = null!, timeout = null!;
    private CheckBox regex = null!, title = null!, content = null!;
    internal NovelRuleEditor(NovelRule rule, Action<string> notice)
    {
        original = rule;
        var actions = new WrapPanel();
        actions.Children.Add(NovelUi.Button("复制规则", () => { try { Clipboard.SetText(System.Text.Json.JsonSerializer.Serialize(Read(), NovelJson.Options)); notice("规则已复制"); } catch (Exception error) { notice(error.Message); } }));
        actions.Children.Add(NovelUi.Button("粘贴规则", () => { try { var pasted = NovelRuleImport.Parse(Clipboard.GetText()).FirstOrDefault() ?? throw new InvalidDataException(); Render(pasted); } catch { notice("剪贴板内容格式不对"); } }));
        SetDock(actions, Dock.Top); Children.Add(actions); Children.Add(form.Card()); Render(rule);
    }
    private void Render(NovelRule rule)
    {
        form.Children.Clear();
        name = form.Field("替换规则名称（name）", rule.Name, id: "novel.rule.field.name"); group = form.Field("分组（group）", rule.Group, id: "novel.rule.field.group");
        pattern = form.Field("替换规则（pattern）", rule.Pattern, 5, "novel.rule.field.pattern"); regex = form.Check("使用正则表达式", rule.IsRegex);
        replacement = form.Field("替换为（replacement）", rule.Replacement, 4, "novel.rule.field.replacement");
        var checks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 14) };
        title = new CheckBox { Content = "作用于标题", IsChecked = rule.ScopeTitle, Tag = "round", Margin = new(0, 0, 14, 0), MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        content = new CheckBox { Content = "作用于正文", IsChecked = rule.ScopeContent, Tag = "round", MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        title.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck"); content.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck");
        checks.Children.Add(title); checks.Children.Add(content); form.Section(checks);
        scope = form.Field("替换范围，选填书名或者书源 URL（scope）", rule.Scope, id: "novel.rule.field.scope");
        exclude = form.Field("排除范围，选填书名或者书源 URL（excludeScope）", rule.ExcludeScope, id: "novel.rule.field.excludeScope");
        timeout = form.Field("超时毫秒数（timeoutMillisecond）", rule.TimeoutMillisecond.ToString(), id: "novel.rule.field.timeoutMillisecond");
    }
    internal NovelRule Read()
    {
        if (!int.TryParse(timeout.Text, out int value) || value <= 0) throw new InvalidDataException("超时毫秒数无效");
        return original with { Name = name.Text.Trim(), Group = group.Text.Trim(), Pattern = pattern.Text, Replacement = replacement.Text,
            Scope = scope.Text.Trim(), ExcludeScope = exclude.Text.Trim(), TimeoutMillisecond = value,
            IsRegex = regex.IsChecked == true, ScopeTitle = title.IsChecked == true, ScopeContent = content.IsChecked == true };
    }
}

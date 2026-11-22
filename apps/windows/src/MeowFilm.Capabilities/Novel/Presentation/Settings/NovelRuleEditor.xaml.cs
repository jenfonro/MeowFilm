using System.IO;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Capabilities;

public sealed partial class NovelRuleEditor : UserControl
{
    private readonly NovelRule original;
    private readonly Action<string> notice;

    internal NovelRuleEditor(NovelRule rule, Action<string> notice)
    {
        original = rule;
        this.notice = notice;
        InitializeComponent();
        Apply(rule);
    }

    internal void ShowError(string message) => Notices.Show(this, message);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { NovelPresentation.CopyText(System.Text.Json.JsonSerializer.Serialize(Read(), NovelJson.Options)); notice("规则已复制"); }
        catch (Exception error) { notice(error.Message); }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { var pasted = NovelRuleImport.Parse(await NovelPresentation.ClipboardTextAsync()).FirstOrDefault() ?? throw new InvalidDataException(); Apply(pasted); }
        catch { notice("剪贴板内容格式不对"); }
    }

    private void Apply(NovelRule rule)
    {
        NameBox.Text = rule.Name;
        GroupBox.Text = rule.Group;
        PatternBox.Text = rule.Pattern;
        RegexCheck.IsChecked = rule.IsRegex;
        ReplacementBox.Text = rule.Replacement;
        TitleCheck.IsChecked = rule.ScopeTitle;
        ContentCheck.IsChecked = rule.ScopeContent;
        ScopeBox.Text = rule.Scope;
        ExcludeBox.Text = rule.ExcludeScope;
        TimeoutBox.Text = rule.TimeoutMillisecond.ToString();
    }

    private void Multiline_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox input || e.Key != VirtualKey.Tab || DesktopVisual.Modifiers != VirtualKeyModifiers.None) return;
        int start = input.SelectionStart;
        input.SelectedText = "\t";
        input.SelectionStart = start + 1;
        input.SelectionLength = 0;
        e.Handled = true;
    }

    internal NovelRule Read()
    {
        if (!int.TryParse(TimeoutBox.Text, out int value)) throw new InvalidDataException("超时毫秒数无效");
        return original with
        {
            Name = NameBox.Text.Trim(),
            Group = GroupBox.Text.Trim(),
            Pattern = PatternBox.Text,
            Replacement = ReplacementBox.Text,
            Scope = ScopeBox.Text.Trim(),
            ExcludeScope = ExcludeBox.Text.Trim(),
            TimeoutMillisecond = value,
            IsRegex = RegexCheck.IsChecked == true,
            ScopeTitle = TitleCheck.IsChecked == true,
            ScopeContent = ContentCheck.IsChecked == true
        };
    }
}

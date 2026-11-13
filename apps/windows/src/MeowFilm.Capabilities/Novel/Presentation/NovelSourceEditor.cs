using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal sealed class NovelSourceEditor : DockPanel
{
    internal NovelSourceDraft Draft { get; }
    internal NovelSourceEditor(string protocol, string? definition)
    {
        Draft = new(protocol, definition);
        var panel = new DockPanel();
        var card = new Border { CornerRadius = new(22), BorderThickness = new(1), Child = panel, ClipToBounds = true };
        card.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.FloatingSurface");
        Children.Add(card);
        var controls = new WrapPanel { Margin = new(16, 8, 16, 8), MinHeight = 48 };
        var sourceType = new TextBlock { Text = NovelSourceFields.Title(protocol), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
        sourceType.SetResourceReference(FrameworkElement.StyleProperty, "Capability.Muted"); controls.Children.Add(sourceType);
        var typeLabel = new TextBlock { Text = "类型", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        typeLabel.SetResourceReference(FrameworkElement.StyleProperty, "Capability.Muted"); controls.Children.Add(typeLabel);
        var type = new ComboBox { ItemsSource = new[] { "文本", "音频", "图片", "文件" }, MinWidth = 80, Height = 30, MinHeight = 30, Margin = new(8, 0, 16, 0) };
        type.SetResourceReference(StyleProperty, "Novel.ComboBox");
        type.SelectedIndex = int.TryParse(Draft.Text(new("bookSourceType", "内容类型")), out int current) && current is >= 0 and < 4 ? current : 0;
        AutomationProperties.SetName(type, "内容类型"); type.SelectionChanged += (_, _) => Draft.Set(new("bookSourceType", "内容类型", Kind: NovelSourceFieldKind.Number), type.SelectedIndex.ToString()); controls.Children.Add(type);
        foreach (var (key, title) in new[] { (Draft.EnabledKey, "启用"), ("enabledExplore", "发现"), ("enabledCookieJar", "CookieJar") })
        {
            var check = new CheckBox { Content = title, IsChecked = Draft.Bool(key), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
            check.SetResourceReference(StyleProperty, "MeowFilm.ManagementCheck");
            check.Click += (_, _) => Draft.SetBool(key, check.IsChecked == true); controls.Children.Add(check);
        }
        var top = new Border { Child = controls, BorderThickness = new(0, 0, 0, 1) }; top.SetResourceReference(Border.BorderBrushProperty, "MeowFilm.Brush.Border"); SetDock(top, Dock.Top); panel.Children.Add(top);
        var tabs = new TabControl { MinHeight = 300, BorderThickness = new(0) }; tabs.SetResourceReference(StyleProperty, "MeowFilm.ManagementTabs");
        foreach (var tab in Draft.Tabs.Concat(Draft.ExtensionFields.Length == 0 ? [] : new[] { new NovelSourceTab("扩展字段", Draft.ExtensionFields) }))
        {
            var form = new ManagementForm();
            foreach (var field in tab.Fields)
            {
                if (field.Kind == NovelSourceFieldKind.Bool) { var check = form.Check(field.Label, Draft.Text(field) == "true"); check.Click += (_, _) => Draft.Set(field, check.IsChecked == true ? "true" : "false"); }
                else { var input = form.Field(field.Label, Draft.Text(field), field.Rows, "novel.source.field." + field.Path); input.TextChanged += (_, _) => Draft.Set(field, input.Text); }
            }
            tabs.Items.Add(new TabItem { Header = tab.Title, Content = NovelUi.Scroll(form.Card()) });
        }
        panel.Children.Add(tabs); AutomationProperties.SetAutomationId(this, "novel.source.editor");
    }
}

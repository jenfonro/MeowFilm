using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class NovelSourceEditor : UserControl
{
    internal static readonly string[] Protocols = ["legado", "qysg"];
    internal NovelSourceDraft Draft { get; private set; }

    internal NovelSourceEditor(string protocol, string? definition)
    {
        Draft = new(protocol, definition);
        InitializeComponent();
        Render();
    }

    internal void ChangeProtocol(string protocol)
    {
        Draft = new(protocol);
        Render();
    }

    private void Render()
    {
        Tabs.TabItems.Clear();
        foreach (var tab in Draft.Tabs.Concat(Draft.ExtensionFields.Length == 0 ? [] : new[] { new NovelSourceTab("扩展字段", Draft.ExtensionFields) }))
        {
            var form = new ManagementForm();
            if (tab.Title == "基本")
            {
                var contentType = new StackPanel { Margin = new(14, 11, 14, 10) };
                contentType.Children.Add(NovelUi.Text("内容类型", 13, muted: true));
                var type = new ComboBox { ItemsSource = new[] { "文本", "音频", "图片", "文件" }, MinWidth = 100, Height = 32, MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Left };
                type.UseResource(StyleProperty, "Novel.ComboBox");
                type.SelectedIndex = int.TryParse(Draft.Text(new("bookSourceType", "内容类型")), out int current) && current is >= 0 and < 4 ? current : 0;
                AutomationProperties.SetName(type, "内容类型"); AutomationProperties.SetAutomationId(type, "novel.source.contentType");
                type.SelectionChanged += (_, _) => Draft.Set(new("bookSourceType", "内容类型", Kind: NovelSourceFieldKind.Number), type.SelectedIndex.ToString());
                contentType.Children.Add(type); form.Section(contentType);
                foreach (var (key, title) in new[] { ("enabled", "启用"), ("enabledExplore", "发现"), ("enabledCookieJar", "CookieJar") })
                {
                    var check = form.Check(title, Draft.Bool(key));
                    check.Click += (_, _) => Draft.SetBool(key, check.IsChecked == true);
                }
            }
            foreach (var field in tab.Fields)
            {
                if (field.Kind == NovelSourceFieldKind.Bool) { var check = form.Check(field.Label, Draft.Text(field) == "true"); check.Click += (_, _) => Draft.Set(field, check.IsChecked == true ? "true" : "false"); }
                else { var input = form.Field(field.Label, Draft.Text(field), field.Rows, "novel.source.field." + field.Path); input.TextChanged += (_, _) => Draft.Set(field, input.Text); }
            }
            Tabs.TabItems.Add(new TabViewItem
            {
                Header = tab.Title,
                IsClosable = false,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = Windows.UI.Text.FontStyle.Normal,
                Content = NovelUi.Scroll(form.Card())
            });
        }
    }
}

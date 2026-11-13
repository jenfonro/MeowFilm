using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace MeowFilm.Capabilities;

internal sealed class NovelAppearanceEditor : StackPanel
{
    private readonly NovelAppearance initial;
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly ComboBox mode, conversion, fonts, theme;
    private readonly CheckBox bold;
    internal NovelAppearanceEditor(NovelAppearance value)
    {
        initial = value;
        mode = Select("阅读模式", new[] { "覆盖", "平移", "无动画分页", "上下连续滚动" }, Array.IndexOf(new[] { "cover", "slide", "none", "scroll" }, value.Mode));
        conversion = Select("简繁转换", new[] { "原文", "简体", "繁体" }, Array.IndexOf(new[] { "none", "simplified", "traditional" }, value.Conversion));
        fonts = new ComboBox { IsEditable = true, Text = value.FontFamily, ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToArray() };
        fonts.SetResourceReference(FrameworkElement.StyleProperty, "Novel.ComboBox");
        Children.Add(NovelUi.Field("字体", fonts));
        bold = new CheckBox { Content = "加粗", IsChecked = value.Bold, Margin = new(0, 0, 0, 0) };
        bold.SetResourceReference(FrameworkElement.StyleProperty, "Novel.CheckBox");
        Children.Add(NovelUi.Row(bold));
        Number("字号", value.FontSize); Number("行距倍数", value.LineHeight); Number("段距", value.ParagraphSpacing);
        Number("页边距", value.Margin); Number("正文宽度", value.ContentWidth); Number("自动阅读速度（1–10）", value.AutoSpeed);
        theme = Select("主题", NovelAppearance.Themes.Select(t => t.Name).ToArray(), value.Theme);
        Field("背景颜色", value.Background); Field("文字颜色", value.Foreground);
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex >= 0) { fields["背景颜色"].Text = NovelAppearance.Themes[theme.SelectedIndex].Background; fields["文字颜色"].Text = NovelAppearance.Themes[theme.SelectedIndex].Foreground; }
        };
        Field("背景图片", value.BackgroundImage);
        Children.Add(NovelUi.Bar(NovelUi.Button("选择背景图", () =>
        {
            var file = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*" };
            if (file.ShowDialog(Window.GetWindow(this)) == true) fields["背景图片"].Text = file.FileName;
        }), NovelUi.Button("移除背景图", () => fields["背景图片"].Text = "")));
    }
    private ComboBox Select(string label, string[] values, int selected)
    {
        var box = new ComboBox { ItemsSource = values, SelectedIndex = Math.Max(0, selected) };
        box.SetResourceReference(FrameworkElement.StyleProperty, "Novel.ComboBox");
        System.Windows.Automation.AutomationProperties.SetName(box, label); Children.Add(NovelUi.Field(label, box)); return box;
    }
    private void Number(string label, double value) => Field(label, value.ToString(CultureInfo.CurrentCulture));
    private void Field(string label, string value) { var box = NovelUi.Input(label, value, double.NaN); fields.Add(label, box); Children.Add(NovelUi.Field(label, box)); }
    internal NovelAppearance Read()
    {
        double Get(string name, bool positive = true)
        {
            if (!double.TryParse(fields[name].Text, out double value) || !double.IsFinite(value) || value < 0 || positive && value == 0) throw new InvalidDataException("INVALID_APPEARANCE");
            return value;
        }
        string ReadColor(string name) { var value = fields[name].Text; _ = (Color)ColorConverter.ConvertFromString(value); return value; }
        double speed = Get("自动阅读速度（1–10）"); if (speed is < 1 or > 10) throw new InvalidDataException("INVALID_SPEED");
        _ = new FontFamily(fonts.Text);
        var next = initial with
        {
            Mode = new[] { "cover", "slide", "none", "scroll" }[mode.SelectedIndex],
            Conversion = new[] { "none", "simplified", "traditional" }[conversion.SelectedIndex], FontFamily = fonts.Text,
            FontSize = Get("字号"), Bold = bold.IsChecked == true, LineHeight = Get("行距倍数"), ParagraphSpacing = Get("段距", false),
            Margin = Get("页边距", false), ContentWidth = Get("正文宽度"), AutoSpeed = speed, Theme = theme.SelectedIndex,
            Background = ReadColor("背景颜色"), Foreground = ReadColor("文字颜色"), BackgroundImage = fields["背景图片"].Text
        };
        NovelLibrary.ValidateAppearance(next); return next;
    }
}

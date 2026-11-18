using MeowFilm.Desktop;
using System.IO;
using System.Globalization;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

internal sealed class NovelAppearanceEditor : StackPanel
{
    private readonly NovelAppearance initial;
    private readonly NoticeStack notices = new();
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly Dictionary<string, string> initialText = [];
    private readonly ComboBox mode, conversion, fonts, theme;
    private readonly CheckBox bold;
    private bool modeChanged, conversionChanged, themeChanged;
    private readonly string initialFont;
    internal NovelAppearanceEditor(NovelAppearance value)
    {
        initial = value; Children.Add(notices);
        var display = value.ForReading();
        mode = Select("阅读模式", new[] { "覆盖", "平移", "无动画分页", "上下连续滚动" }, Array.IndexOf(new[] { "cover", "slide", "none", "scroll" }, display.Mode));
        conversion = Select("简繁转换", new[] { "原文", "简体", "繁体" }, Array.IndexOf(new[] { "none", "simplified", "traditional" }, display.Conversion));
        mode.SelectionChanged += (_, _) => modeChanged = true;
        conversion.SelectionChanged += (_, _) => conversionChanged = true;
        fonts = new ComboBox { IsEditable = true, Text = display.FontFamily, ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToArray() };
        initialFont = display.FontFamily;
        fonts.UseResource(StyleProperty, "Novel.ComboBox");
        Children.Add(NovelUi.Field("字体", fonts));
        bold = new CheckBox { Content = "加粗", IsChecked = value.Bold, Margin = new(0, 0, 0, 0) };
        bold.UseResource(StyleProperty, "Novel.CheckBox");
        Children.Add(NovelUi.Row(bold));
        Number("字号", display.FontSize); Number("行距倍数", display.LineHeight); Number("段距", display.ParagraphSpacing);
        Number("页边距", display.Margin); Number("正文宽度", display.ContentWidth); Number("自动阅读速度（1–10）", display.AutoSpeed);
        theme = Select("主题", NovelAppearance.Themes.Select(t => t.Name).ToArray(), display.Theme);
        Field("背景颜色", display.Background); Field("文字颜色", display.Foreground);
        theme.SelectionChanged += (_, _) =>
        {
            themeChanged = true;
            if (theme.SelectedIndex >= 0) { fields["背景颜色"].Text = NovelAppearance.Themes[theme.SelectedIndex].Background; fields["文字颜色"].Text = NovelAppearance.Themes[theme.SelectedIndex].Foreground; }
        };
        Field("背景图片", display.BackgroundImage);
        Children.Add(NovelUi.Bar(NovelUi.Button("选择背景图", async () =>
        {
            try
            {
                string? path = await NovelPresentation.OpenFileAsync(this, ".png", ".jpg", ".jpeg", ".bmp", ".webp", "*");
                if (path is not null && IsLoaded) fields["背景图片"].Text = path;
            }
            catch { if (IsLoaded) notices.Show(this, "背景图片选择失败，请重试"); }
        }), NovelUi.Button("移除背景图", () => fields["背景图片"].Text = "")));
    }
    private ComboBox Select(string label, string[] values, int selected)
    {
        var box = new ComboBox { ItemsSource = values, SelectedIndex = selected };
        box.UseResource(StyleProperty, "Novel.ComboBox");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label); Children.Add(NovelUi.Field(label, box)); return box;
    }
    private void Number(string label, double value) => Field(label, value.ToString(CultureInfo.CurrentCulture));
    private void Field(string label, string value) { var box = NovelUi.Input(label, value, double.NaN); fields.Add(label, box); initialText.Add(label, value); Children.Add(NovelUi.Field(label, box)); }
    internal NovelAppearance Read()
    {
        bool Edited(string name) => fields[name].Text != initialText[name];
        double Get(string name, double original, bool positive = true)
        {
            if (!Edited(name)) return original;
            if (!double.TryParse(fields[name].Text, out double value) || !double.IsFinite(value) || value < 0 || positive && value == 0) throw new InvalidDataException("INVALID_APPEARANCE");
            if (name == "字号" && (!System.Windows.Documents.TextElement.FontSizeProperty.IsValidValue(value) ||
                !System.Windows.Documents.TextElement.FontSizeProperty.IsValidValue(value * 1.15))) throw new InvalidDataException("INVALID_APPEARANCE");
            return value;
        }
        string ReadColor(string name, string original) { if (!Edited(name)) return original; var value = fields[name].Text; _ = (Color)ColorConverter.ConvertFromString(value); return value; }
        double speed = Get("自动阅读速度（1–10）", initial.AutoSpeed);
        if (Edited("自动阅读速度（1–10）") && speed is < 1 or > 10) throw new InvalidDataException("INVALID_SPEED");
        string font = fonts.Text == initialFont ? initial.FontFamily : fonts.Text;
        if (fonts.Text != initialFont) _ = new FontFamily(font);
        return initial with
        {
            Mode = modeChanged ? new[] { "cover", "slide", "none", "scroll" }[mode.SelectedIndex] : initial.Mode,
            Conversion = conversionChanged ? new[] { "none", "simplified", "traditional" }[conversion.SelectedIndex] : initial.Conversion, FontFamily = font,
            FontSize = Get("字号", initial.FontSize), Bold = bold.IsChecked == true, LineHeight = Get("行距倍数", initial.LineHeight), ParagraphSpacing = Get("段距", initial.ParagraphSpacing, false),
            Margin = Get("页边距", initial.Margin, false), ContentWidth = Get("正文宽度", initial.ContentWidth), AutoSpeed = speed, Theme = themeChanged ? theme.SelectedIndex : initial.Theme,
            Background = ReadColor("背景颜色", initial.Background), Foreground = ReadColor("文字颜色", initial.Foreground), BackgroundImage = fields["背景图片"].Text
        };
    }
}

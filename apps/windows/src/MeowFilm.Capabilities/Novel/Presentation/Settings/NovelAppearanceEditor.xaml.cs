using System.Globalization;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class NovelAppearanceEditor : UserControl
{
    private static readonly string[] Modes = ["cover", "slide", "none", "scroll"];
    private static readonly string[] Conversions = ["none", "simplified", "traditional"];
    private readonly NovelAppearance initial;
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly Dictionary<string, string> initialText = [];
    private readonly string initialFont;
    private bool initializing = true;
    private bool modeChanged, conversionChanged, themeChanged;

    internal NovelAppearanceEditor(NovelAppearance value)
    {
        initial = value;
        InitializeComponent();
        var display = value.ForReading();
        ModeBox.ItemsSource = new[] { "覆盖", "平移", "无动画分页", "上下连续滚动" };
        ModeBox.SelectedIndex = Array.IndexOf(Modes, display.Mode);
        ConversionBox.ItemsSource = new[] { "原文", "简体", "繁体" };
        ConversionBox.SelectedIndex = Array.IndexOf(Conversions, display.Conversion);
        FontsBox.ItemsSource = NovelTextStyle.FontFamilies();
        FontsBox.Text = display.FontFamily;
        initialFont = display.FontFamily;
        BoldCheck.IsChecked = value.Bold;
        ThemeBox.ItemsSource = NovelAppearance.Themes.Select(t => t.Name).ToArray();
        ThemeBox.SelectedIndex = display.Theme;

        Register("字号", FontSizeBox, display.FontSize.ToString(CultureInfo.CurrentCulture));
        Register("行距倍数", LineHeightBox, display.LineHeight.ToString(CultureInfo.CurrentCulture));
        Register("段距", ParagraphSpacingBox, display.ParagraphSpacing.ToString(CultureInfo.CurrentCulture));
        Register("页边距", MarginBox, display.Margin.ToString(CultureInfo.CurrentCulture));
        Register("正文宽度", ContentWidthBox, display.ContentWidth.ToString(CultureInfo.CurrentCulture));
        Register("自动阅读速度（1–10）", AutoSpeedBox, display.AutoSpeed.ToString(CultureInfo.CurrentCulture));
        Register("背景颜色", BackgroundBox, display.Background);
        Register("文字颜色", ForegroundBox, display.Foreground);
        Register("背景图片", BackgroundImageBox, display.BackgroundImage);
        initializing = false;
    }

    private void Register(string label, TextBox box, string value)
    {
        box.Text = value;
        fields[label] = box;
        initialText[label] = value;
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing) modeChanged = true;
    }

    private void Conversion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing) conversionChanged = true;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        themeChanged = true;
        if (ThemeBox.SelectedIndex >= 0 && ThemeBox.SelectedIndex < NovelAppearance.Themes.Length)
        {
            BackgroundBox.Text = NovelAppearance.Themes[ThemeBox.SelectedIndex].Background;
            ForegroundBox.Text = NovelAppearance.Themes[ThemeBox.SelectedIndex].Foreground;
        }
    }

    private async void PickBackground_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await NovelPresentation.OpenFileAsync(this, ".png", ".jpg", ".jpeg", ".bmp", ".webp", "*");
            if (path is not null && IsLoaded) BackgroundImageBox.Text = path;
        }
        catch { if (IsLoaded) Notices.Show(this, "背景图片选择失败，请重试"); }
    }

    private void RemoveBackground_Click(object sender, RoutedEventArgs e) => BackgroundImageBox.Text = "";

    internal void ShowError(string message) => Notices.Show(this, message);

    internal NovelAppearance Read()
    {
        bool Edited(string name) => fields[name].Text != initialText[name];
        double Get(string name, double original, bool positive = true)
        {
            if (!Edited(name)) return original;
            if (!double.TryParse(fields[name].Text, out double value) || !double.IsFinite(value) || value < 0 || positive && value == 0)
                throw new InvalidDataException("INVALID_APPEARANCE");
            if (name == "字号" && !NovelTextStyle.ValidSize(value)) throw new InvalidDataException("INVALID_APPEARANCE");
            return value;
        }
        string ReadColor(string name, string original)
        {
            if (!Edited(name)) return original;
            var value = fields[name].Text;
            _ = NovelTextStyle.Color(value);
            return value;
        }
        double speed = Get("自动阅读速度（1–10）", initial.AutoSpeed);
        if (Edited("自动阅读速度（1–10）") && speed is < 1 or > 10) throw new InvalidDataException("INVALID_SPEED");
        string font = FontsBox.Text == initialFont ? initial.FontFamily : FontsBox.Text;
        if (FontsBox.Text != initialFont && !NovelTextStyle.ValidFamily(font)) throw new InvalidDataException("INVALID_FONT");
        double fontSize = Get("字号", initial.FontSize), lineHeight = Get("行距倍数", initial.LineHeight);
        if ((Edited("字号") || Edited("行距倍数")) && !NovelTextStyle.ValidLineHeight(fontSize, lineHeight))
            throw new InvalidDataException("INVALID_APPEARANCE");
        return initial with
        {
            Mode = modeChanged ? Modes[ModeBox.SelectedIndex] : initial.Mode,
            Conversion = conversionChanged ? Conversions[ConversionBox.SelectedIndex] : initial.Conversion,
            FontFamily = font,
            FontSize = fontSize,
            Bold = BoldCheck.IsChecked == true,
            LineHeight = lineHeight,
            ParagraphSpacing = Get("段距", initial.ParagraphSpacing, false),
            Margin = Get("页边距", initial.Margin, false),
            ContentWidth = Get("正文宽度", initial.ContentWidth),
            AutoSpeed = speed,
            Theme = themeChanged ? ThemeBox.SelectedIndex : initial.Theme,
            Background = ReadColor("背景颜色", initial.Background),
            Foreground = ReadColor("文字颜色", initial.Foreground),
            BackgroundImage = BackgroundImageBox.Text
        };
    }
}

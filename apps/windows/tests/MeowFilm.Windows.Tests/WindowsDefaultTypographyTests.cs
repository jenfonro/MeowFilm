using FontWeights = Microsoft.UI.Text.FontWeights;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class WindowsDefaultTypographyTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("application installs one shared theme without replacing the running application", async () =>
        {
            var app = Application.Current;
            DesktopTypography.Install(app); DesktopTypography.Install(app);
            Check(app.Resources.MergedDictionaries.Count(dictionary => dictionary.Source?.OriginalString ==
                "ms-appx:///MeowFilm.Desktop/Theme.xaml") == 1, "theme installation is idempotent");
            Check(app.Resources.MergedDictionaries.OfType<XamlControlsResources>().Count() == 1, "one native control resource dictionary");
            await VerifyNewRoots();
            Check(ReferenceEquals(app, Application.Current), "tests never create a second application");
        });
        yield return Case("new and derived roots retain regular system body typography", VerifyNewRoots);
        yield return Case("live family resources reach retained content and recover after removal", VerifyDynamicFamily);
        yield return Case("shared text roles retain regular fonts and size hierarchy", VerifyRoles);
        yield return Case("desktop lyrics preserve user colors and explicit sizes", VerifyDesktopLyrics);
        yield return Case("popup menu tooltip and derived windows retain their native text roles", VerifyOverlays);
    }

    private static async Task VerifyNewRoots()
    {
        var text = new TextBlock { Text = "正文" };
        var button = new Button { Content = "按钮" };
        var input = new TextBox { Text = "输入" };
        var item = new ListViewItem { Content = "列表" };
        var root = new UserControl { Content = new StackPanel { Children = { text, button, input, item } } };
        using var host = new DesktopTestUi.Host(root, 640, 400);
        var derivedText = new TextBlock { Text = "派生窗口正文" };
        var derived = new DerivedDesktopWindow { Content = new UserControl { Content = derivedText } };
        derived.AttachRoot(); derived.Resize(640, 400);
        try
        {
            derived.Activate(); await DesktopTestUi.DrainAsync();
            foreach (var element in new FrameworkElement[] { text, button, input, item, derivedText })
                Typography(element, 14, SystemFamily);
            Check(root.UseLayoutRounding && derived.Root.UseLayoutRounding, "both roots retain native pixel layout");
        }
        finally { derived.Content = null; derived.Close(); }
    }

    private static async Task VerifyDynamicFamily()
    {
        var text = new TextBlock { Text = "正文" };
        var button = new Button { Content = "按钮" };
        var input = new TextBox { Text = "输入" };
        var word = new MusicLyricWordView { Word = new("自绘", 0, 1), FontSize = 20 };
        var lyric = new MusicDesktopLyricTextView { Text = "歌词", FontSize = 20 };
        var popupText = new TextBlock { Text = "浮层" };
        var popup = new Popup { Child = popupText };
        var root = new UserControl { Content = new StackPanel { Children = { text, button, input, word, lyric, popup } } };
        using var host = new DesktopTestUi.Host(root, 640, 400);
        await DesktopTestUi.DrainAsync();
        popup.XamlRoot = root.XamlRoot; popup.IsOpen = true;
        string[] keys = ["ContentControlThemeFontFamily", "TextControlThemeFontFamily"];
        var resources = Application.Current.Resources;
        var prior = keys.ToDictionary(key => key, key => resources.TryGetValue(key, out var value) ? value : null);
        try
        {
            var alternate = new FontFamily("Arial");
            foreach (var key in keys) resources[key] = alternate;
            root.RequestedTheme = ElementTheme.Dark; await DesktopTestUi.DrainAsync();
            root.RequestedTheme = ElementTheme.Light; await DesktopTestUi.DrainAsync();
            foreach (var element in new FrameworkElement[] { text, button, input, word, lyric, popupText })
                Check(Family(element).Source == alternate.Source, "a retained text consumer follows the current native family resources");
            Restore(); root.RequestedTheme = ElementTheme.Default; await DesktopTestUi.DrainAsync();
            foreach (var element in new FrameworkElement[] { text, button, input, word, lyric, popupText })
                Check(Family(element).Source == SystemFamily, "removing the override restores the native system family");
        }
        finally { Restore(); popup.IsOpen = false; }
        void Restore()
        {
            foreach (var key in keys)
                if (prior[key] is { } value) resources[key] = value;
                else resources.Remove(key);
        }
    }

    private static async Task VerifyRoles()
    {
        var root = new StackPanel();
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("ms-appx:///MeowFilm.Capabilities/Lyrics/Presentation/LyricTypography.xaml") });
        var roles = new[] { ("MeowFilm.Heading", 26d), ("MeowFilm.SectionHeading", 18d),
            ("MeowFilm.SettingsSectionTitle", 16d), ("MeowFilm.Title", 20d) };
        var texts = roles.Select(role => new TextBlock { Text = role.Item1,
            Style = (Style)DesktopVisual.Resource(root, role.Item1) }).ToArray();
        foreach (var text in texts) root.Children.Add(text);
        var lyric = new MusicDesktopLyricTextView { Text = "歌词", Style = (Style)DesktopVisual.Resource(root, "MeowFilm.Lyric") };
        var tab = new RadioButton { Content = "设置分类", Style = (Style)DesktopVisual.Resource(root, "MeowFilm.SettingsArea") };
        root.Children.Add(lyric); root.Children.Add(tab);
        using var host = new DesktopTestUi.Host(root, 640, 500); await DesktopTestUi.DrainAsync();
        for (int index = 0; index < texts.Length; index++) Typography(texts[index], roles[index].Item2, SystemFamily);
        Typography(lyric, 14, SystemFamily);
        foreach (bool selected in new[] { false, true, false })
        {
            tab.IsChecked = selected; await DesktopTestUi.DrainAsync(); Typography(tab, 14, SystemFamily);
            foreach (var label in DesktopTestUi.Visuals(tab).OfType<TextBlock>())
                Check(label.FontWeight == FontWeights.Normal, "selection never bolds rendered labels");
        }
    }

    private static async Task VerifyDesktopLyrics()
    {
        var row = new MusicLyricRow(0, "用户原文 🙂", "用户译文");
        var normal = new SolidColorBrush(Microsoft.UI.Colors.LightPink);
        var highlight = new SolidColorBrush(Microsoft.UI.Colors.Gold);
        var line = new MusicDesktopLyricLineView { Width = 700, Height = 120, Row = row,
            Normal = normal, Highlight = highlight, ShowTranslation = true };
        using var host = new DesktopTestUi.Host(line, 740, 200); await DesktopTestUi.DrainAsync();
        var plain = (MusicDesktopLyricTextView)line.FindName("PlainText");
        var translation = (MusicDesktopLyricTextView)line.FindName("TranslationText");
        Typography(plain, 32, SystemFamily); Typography(translation, 17, SystemFamily);
        Check(ReferenceEquals(plain.Foreground, normal) && ReferenceEquals(translation.Foreground, normal), "user lyric colors remain authoritative");
        Check(DesktopTestUi.Visuals(line).OfType<MusicLyricWordView>().Count() == 0, "line-timed lyrics stay plain");
        Check(AutomationProperties.GetName(plain) == row.Text, "lyric keeps accessible text");
        line.Row = row with { Words = [new("用户", 0, 1), new("原文", 1, 1)] }; await DesktopTestUi.DrainAsync();
        var words = DesktopTestUi.Visuals(line).OfType<MusicLyricWordView>().ToArray();
        Check(words.Length == 2, "both real timed words are retained");
        foreach (var word in words)
        {
            Typography(word, 32, SystemFamily);
            Check(ReferenceEquals(word.Foreground, normal) && ReferenceEquals(word.Highlight, highlight) &&
                word.Stroke is not null && word.StrokeThickness > 0, "timed words retain user colors and outline");
        }
    }

    private static async Task VerifyOverlays()
    {
        var anchor = new Button { Content = "菜单" };
        var root = new StackPanel { Children = { anchor } };
        using var host = new DesktopTestUi.Host(root, 640, 400); await DesktopTestUi.DrainAsync();
        var popupText = new TextBlock { Text = "浮层正文" };
        var popup = new Popup { Child = popupText, XamlRoot = root.XamlRoot };
        var item = new MenuFlyoutItem { Text = "菜单项", Style = (Style)DesktopVisual.Resource(root, "MeowFilm.MenuItem") };
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)DesktopVisual.Resource(root, "MeowFilm.ContextMenu") };
        menu.Items.Add(item);
        var tooltipText = new TextBlock { Text = "提示内容" };
        var tooltip = new ToolTip { Content = tooltipText, Style = (Style)DesktopVisual.Resource(root, "MeowFilm.ToolTip") };
        ToolTipService.SetToolTip(anchor, tooltip);
        try
        {
            popup.IsOpen = true; menu.ShowAt(anchor); tooltip.IsOpen = true; await DesktopTestUi.DrainAsync();
            Check(popup.IsOpen && tooltip.IsOpen && item.IsLoaded, "the consumers are actual opened native layers");
            Typography(popupText, 14, SystemFamily); Typography(item, 13, SystemFamily);
            Typography(tooltipText, 12, SystemFamily);
        }
        finally { tooltip.IsOpen = false; menu.Hide(); popup.IsOpen = false; }
    }

    private const string SystemFamily = "XamlAutoFontFamily";
    private static FontFamily Family(FrameworkElement element) => element switch
    { TextBlock text => text.FontFamily, Control control => control.FontFamily, _ => throw new InvalidOperationException("not a text consumer") };
    private static void Typography(FrameworkElement element, double size, string family)
    {
        var actual = element switch
        {
            TextBlock text => (text.FontSize, text.FontWeight, text.FontStyle),
            Control control => (control.FontSize, control.FontWeight, control.FontStyle),
            _ => throw new InvalidOperationException("not a text consumer")
        };
        Check(actual.Item1 == size && actual.Item2 == FontWeights.Normal && actual.Item3 == FontStyle.Normal &&
            Family(element).Source == family, $"{element.GetType().Name} retains regular system typography at {size}");
    }
    private static TestCase Case(string name, Func<Task> body) => new("desktop typography: " + name, () => TestUiHost.InvokeAsync(body));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class DerivedDesktopWindow : DesktopWindow { }
}

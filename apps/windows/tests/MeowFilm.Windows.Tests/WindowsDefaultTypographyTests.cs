using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class WindowsDefaultTypographyTests
{
    private const string ApplicationCaseName =
        "Windows default typography: application install reaches new roots and dynamic family";
    private const string ApplicationChildVariable = "MEOWFILM_TYPOGRAPHY_CHILD";

    public static IEnumerable<TestCase> Cases()
    {
        yield return new(ApplicationCaseName, RunApplicationInstall);
        yield return Case("new window roots and user controls inherit body typography without local setup",
            VerifyNewRoots);
        yield return Case("dynamic system family override and recovery reaches roots popup and self-drawn text",
            VerifyDynamicFamily);
        yield return Case("shared text roles keep regular fonts and their size hierarchy", VerifyRoles);
        yield return Case("desktop lyric user colors and sizes remain outside the body default",
            VerifyDesktopLyricExceptions);
        yield return Case("popup menu tooltip and derived window content use the shared family and keep their own roles",
            VerifyOverlayAndWindowConsumers);
    }

    private static async Task RunApplicationInstall()
    {
        if (Environment.GetEnvironmentVariable(ApplicationChildVariable) != "1")
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = TestWorkspace.SourceRoot
            };
            if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            info.ArgumentList.Add("--case");
            info.ArgumentList.Add(ApplicationCaseName);
            info.Environment[ApplicationChildVariable] = "1";
            using var child = Process.Start(info) ?? throw new InvalidOperationException("Typography child missing");
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            catch { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
            var text = await output;
            var diagnostics = await errors;
            if (child.ExitCode != 0 ||
                !text.Contains("TOTAL 1; PASSED 1; FAILED 0; SKIPPED 0", StringComparison.Ordinal))
                throw new InvalidOperationException("Typography child failed: " + text + diagnostics);
            return;
        }

        await MusicQualityConsumerTests.Sta(VerifyApplicationInstall);
    }

    private static async Task VerifyApplicationInstall()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        DesktopTypography.Install(application);
        var first = new DesktopWindow
        {
            Width = 640, Height = 400, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        var second = new DesktopWindow
        {
            Width = 640, Height = 400, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        try
        {
            var installed = application.Resources.MergedDictionaries
                .Where(dictionary => dictionary.Source?.OriginalString.Contains("Typography.xaml", StringComparison.Ordinal) == true)
                .ToArray();
            Equal(1, installed.Length, "application installs one shared typography dictionary");
            DesktopTypography.Install(application);
            Equal(1, application.Resources.MergedDictionaries
                .Count(dictionary => dictionary.Source?.OriginalString.Contains("Typography.xaml", StringComparison.Ordinal) == true),
                "repeated application install is idempotent");

            var popupText = new TextBlock { Text = "浮层" };
            var popup = new Popup { Child = popupText, PlacementTarget = first };
            var word = new MusicLyricWordView { Word = new("自绘", 0, 1), FontSize = 20 };
            var selfDrawn = new MusicDesktopLyricTextView { Text = "自绘", FontSize = 20 };
            var list = new ListBox();
            list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "列表" } });
            var text = new TextBlock { Text = "正文" };
            var button = new Button { Content = "按钮" };
            var textBox = new TextBox { Text = "输入" };
            first.Content = new StackPanel
            {
                Children = { text, button, textBox, list, word, selfDrawn, popup }
            };
            second.Content = new TextBlock { Text = "重建" };
            first.Show();
            second.Show();
            first.UpdateLayout();
            second.UpdateLayout();
            popup.IsOpen = true;
            first.UpdateLayout();

            var listItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            foreach (var control in new DependencyObject[] { text, button, textBox, listItem, word, selfDrawn })
            {
                Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(control), "application root family");
                Equal(FontWeights.Normal, TextElement.GetFontWeight(control), "application body weight");
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(control), "application display metrics");
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(control), "application native rendering");
            }
            foreach (var control in new DependencyObject[] { text, button, textBox, listItem })
                Equal(14d, TextElement.GetFontSize(control), "application body size");
            Equal(20d, word.FontSize, "timed lyric keeps its explicit size");
            Equal(20d, selfDrawn.FontSize, "self-drawn lyric keeps its explicit size");
            Equal(SystemFonts.MessageFontFamily, ((TextBlock)second.Content).FontFamily, "rebuilt root family");
            Equal(14d, ((TextBlock)second.Content).FontSize, "rebuilt root size");
            Equal(SystemFonts.MessageFontFamily, popupText.FontFamily, "popup content family");
            Equal(14d, popupText.FontSize, "popup body size");

            var alternate = new FontFamily("Arial");
            application.Resources[SystemFonts.MessageFontFamilyKey] = alternate;
            first.UpdateLayout();
            second.UpdateLayout();
            await Drain();
            foreach (var control in new DependencyObject[] { text, button, textBox, listItem, word, selfDrawn })
                Equal(alternate, TextElement.GetFontFamily(control), "application dynamic family reaches root controls");
            Equal(alternate, ((TextBlock)second.Content).FontFamily, "application dynamic family reaches rebuilt root");
            Equal(alternate, popupText.FontFamily, "application dynamic family reaches popup content");

            application.Resources.Remove(SystemFonts.MessageFontFamilyKey);
            first.UpdateLayout();
            second.UpdateLayout();
            await Drain();
            foreach (var control in new DependencyObject[] { text, button, textBox, listItem, word, selfDrawn })
                Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(control), "removing the override restores system family");
            Equal(SystemFonts.MessageFontFamily, popupText.FontFamily, "removing the override restores popup family");
        }
        finally
        {
            first.Content = null;
            second.Content = null;
            first.Close();
            second.Close();
            application.Shutdown();
        }
    }

    private static async Task VerifyNewRoots()
    {
        var text = new TextBlock { Text = "无配置正文" };
        var button = new Button { Content = "无配置按钮" };
        var textBox = new TextBox { Text = "无配置输入" };
        var listItem = new ListBoxItem { Content = new TextBlock { Text = "无配置列表" } };
        var content = new UserControl
        {
            Content = new StackPanel { Children = { text, button, textBox, listItem } }
        };
        var window = new DesktopWindow { Content = content };
        var derived = new DerivedDesktopWindow
        {
            Content = new UserControl { Content = new TextBlock { Text = "派生窗口正文" } }
        };

        try
        {
            window.Show();
            derived.Show();
            window.UpdateLayout();
            derived.UpdateLayout();
            await Drain();
            foreach (var control in new DependencyObject[] { text, button, textBox, listItem })
            {
                Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(control), "new root inherits the system family");
                Equal(14d, TextElement.GetFontSize(control), "new root inherits body size");
                Equal(FontWeights.Normal, TextElement.GetFontWeight(control), "new root inherits normal weight");
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(control), "new root inherits Display");
                Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(control), "new root inherits Auto");
            }
            Equal(SystemFonts.MessageFontFamily,
                ((TextBlock)((UserControl)derived.Content).Content).FontFamily, "derived root inherits the system family");
            True(window.UseLayoutRounding && window.SnapsToDevicePixels, "new root keeps display pixel layout");
            True(derived.UseLayoutRounding && derived.SnapsToDevicePixels, "derived root keeps display pixel layout");
        }
        finally
        {
            window.Content = null;
            derived.Content = null;
            window.Close();
            derived.Close();
        }
    }

    private static async Task VerifyDynamicFamily()
    {
        var text = new TextBlock { Text = "正文" };
        var button = new Button { Content = "按钮" };
        var textBox = new TextBox { Text = "输入" };
        var listItem = new ListBoxItem { Content = new TextBlock { Text = "列表" } };
        var word = new MusicLyricWordView { Word = new("自绘", 0, 1), FontSize = 20 };
        var selfDrawn = new MusicDesktopLyricTextView { Text = "自绘", FontSize = 20 };
        var popupText = new TextBlock { Text = "浮层" };
        var popup = new Popup { Child = popupText };
        var window = new DesktopWindow
        {
            Width = 640, Height = 400, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Content = new StackPanel { Children = { text, button, textBox, listItem, word, selfDrawn, popup } }
        };
        popup.PlacementTarget = window;
        try
        {
            window.Show();
            window.UpdateLayout();
            popup.IsOpen = true;
            await Drain();
            var alternate = new FontFamily("Arial");
            window.Resources[SystemFonts.MessageFontFamilyKey] = alternate;
            await Drain();
            foreach (var control in new DependencyObject[] { text, button, textBox, listItem, word, selfDrawn })
                Equal(alternate, TextElement.GetFontFamily(control), "window override reaches text and self-drawn controls");
            Equal(alternate, popupText.FontFamily, "window override reaches popup content");

            window.Resources.Remove(SystemFonts.MessageFontFamilyKey);
            await Drain();
            foreach (var control in new DependencyObject[] { text, button, textBox, listItem, word, selfDrawn })
                Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(control), "removing the override restores root controls");
            Equal(SystemFonts.MessageFontFamily, popupText.FontFamily, "removing the override restores popup content");
        }
        finally
        {
            popup.IsOpen = false;
            window.Content = null;
            window.Close();
        }
    }

    private static async Task VerifyRoles()
    {
        var window = new DesktopWindow();
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative)
        });
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/MeowFilm.Capabilities;component/Lyrics/Presentation/LyricTypography.xaml", UriKind.Relative)
        });
        var heading = new TextBlock { Text = "标题", Style = (Style)window.FindResource("MeowFilm.Heading") };
        var section = new TextBlock { Text = "分组", Style = (Style)window.FindResource("MeowFilm.SectionHeading") };
        var settings = new TextBlock { Text = "设置", Style = (Style)window.FindResource("MeowFilm.SettingsSectionTitle") };
        var title = new TextBlock { Text = "页标题", Style = (Style)window.FindResource("MeowFilm.Title") };
        var lyric = new MusicDesktopLyricTextView { Text = "歌词", Style = (Style)window.FindResource("MeowFilm.Lyric") };
        var tab = new RadioButton { Content = "设置分类", Style = (Style)window.FindResource("MeowFilm.SettingsArea") };
        window.Width = 640;
        window.Height = 400;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStyle = WindowStyle.None;
        window.Content = new StackPanel { Children = { heading, section, settings, title, lyric, tab } };
        try
        {
            window.Show();
            window.UpdateLayout();
            await Drain();

            Equal(FontWeights.Normal, (FontWeight)window.FindResource("MeowFilm.Font.Weight.Normal"),
                "body role is regular");
            Equal(26d, heading.FontSize, "heading role size");
            Equal(FontWeights.Normal, heading.FontWeight, "heading role is regular");
            Equal(18d, section.FontSize, "section heading role size");
            Equal(FontWeights.Normal, section.FontWeight, "section heading role is regular");
            Equal(16d, settings.FontSize, "settings section title role size");
            Equal(FontWeights.Normal, settings.FontWeight, "settings section title role is regular");
            Equal(20d, title.FontSize, "title role size");
            Equal(FontWeights.Normal, title.FontWeight, "title role is regular");
            Equal(14d, lyric.FontSize, "body default size reaches lyric role");
            Equal(FontWeights.Normal, lyric.FontWeight, "lyric role is regular");
            Equal(SystemFonts.MessageFontFamily, lyric.FontFamily, "lyric role follows the system family");
            foreach (var selected in new[] { false, true, false })
            {
                tab.IsChecked = selected;
                window.UpdateLayout();
                await Drain();
                Equal(FontWeights.Normal, tab.FontWeight, "selection keeps regular text");
                Equal(14d, tab.FontSize, "selection preserves the tab size");
            }
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static async Task VerifyDesktopLyricExceptions()
    {
        var row = new MusicLyricRow(0, "用户原文 🙂", "用户译文");
        var line = new MusicDesktopLyricLineView
        {
            Width = 700, Height = 120, Row = row,
            Normal = Brushes.LightPink, Highlight = Brushes.Gold, ShowTranslation = true
        };
        var window = new DesktopWindow
        {
            Width = 720, Height = 160, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Content = line
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            await Drain();

            var plain = (MusicDesktopLyricTextView)line.FindName("PlainText");
            var translation = (MusicDesktopLyricTextView)line.FindName("TranslationText");
            var words = Visuals(line).OfType<MusicLyricWordView>().ToArray();
            True(plain.Visibility == Visibility.Visible && words.Length == 0, "line-timed lyric stays ordinary text");
            Equal(32d, plain.FontSize, "desktop lyric keeps its source size");
            Equal(FontWeights.Normal, plain.FontWeight, "desktop lyric uses the normal role");
            Equal(SystemFonts.MessageFontFamily, plain.FontFamily, "desktop lyric follows the system family");
            Equal(Brushes.LightPink, plain.Foreground, "desktop lyric keeps the user normal color");
            Equal(17d, translation.FontSize, "desktop translation keeps its source size");
            Equal(Brushes.LightPink, translation.Foreground, "desktop translation keeps the user color");

            line.Row = row with { Words = [new("用户", 0, 1), new("原文", 1, 1)] };
            window.UpdateLayout();
            await Drain();
            words = Visuals(line).OfType<MusicLyricWordView>().ToArray();
            Equal(2, words.Length, "real timed words remain in the desktop lyric");
            True(words.All(word => word.FontSize == 32 && word.FontWeight == FontWeights.Normal &&
                word.FontFamily.Equals(SystemFonts.MessageFontFamily)), "timed words keep the lyric role and size");
            True(words.All(word => Equals(word.Foreground, line.Normal) && Equals(word.Highlight, line.Highlight) &&
                word.Stroke is not null && word.StrokeThickness > 0), "user colors and outline remain separate roles");
            Equal("用户原文 🙂", UIElementAutomationPeer.CreatePeerForElement(plain)!.GetName(), "lyric retains accessible text");
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static async Task VerifyOverlayAndWindowConsumers()
    {
        var popupText = new TextBlock { Text = "浮层正文" };
        var popup = new Popup { Child = popupText };
        var menuText = new TextBlock { Text = "菜单项" };
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = menuText });
        var tooltipText = new TextBlock { Text = "提示内容" };
        var tooltip = new ToolTip { Content = tooltipText };
        var windowText = new TextBlock { Text = "独立窗口" };
        var directory = new DerivedDesktopWindow
        {
            Content = new StackPanel { Children = { windowText } }
        };
        var window = new DesktopWindow
        {
            Width = 640, Height = 400, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Content = new StackPanel { Children = { popup } },
            ContextMenu = menu,
            ToolTip = tooltip
        };
        popup.PlacementTarget = window;
        menu.PlacementTarget = window;
        tooltip.PlacementTarget = window;
        try
        {
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative)
            });
            tooltip.Style = (Style)window.FindResource("MeowFilm.ToolTip");
            window.Show();
            directory.Show();
            window.UpdateLayout();
            directory.UpdateLayout();
            popup.IsOpen = true;
            menu.IsOpen = true;
            tooltip.IsOpen = true;
            await Drain();

            Equal(SystemFonts.MessageFontFamily, popupText.FontFamily, "popup actual content uses the shared family");
            Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(menu), "menu uses the shared family");
            Equal(13d, menu.FontSize, "menu keeps its compact role size");
            Equal(FontWeights.Normal, menu.FontWeight, "menu keeps its normal role weight");
            Equal(SystemFonts.MessageFontFamily, TextElement.GetFontFamily(menuText),
                "actual menu item content uses the shared family");
            Equal(SystemFonts.MessageFontFamily, tooltipText.FontFamily, "tooltip actual content uses the shared family");
            Equal(12d, tooltipText.FontSize, "tooltip keeps its caption role size");
            Equal(SystemFonts.MessageFontFamily, windowText.FontFamily, "derived window content uses the shared family");
            Equal(14d, windowText.FontSize, "derived window content uses the body size");
            True(tooltip.IsOpen && menu.IsOpen && popup.IsOpen, "the consumers under test are actual opened WPF layers");
            True(menu.Items.OfType<MenuItem>().Single().Header is TextBlock, "menu keeps the real command header");
            True(windowText.FontWeight == FontWeights.Normal, "derived window body is not bold");
        }
        finally
        {
            tooltip.IsOpen = false;
            menu.IsOpen = false;
            popup.IsOpen = false;
            window.Content = null;
            directory.Content = null;
            window.Close();
            directory.Close();
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static TestCase Case(string name, Func<Task> body) =>
        new("desktop typography: " + name, () => MusicQualityConsumerTests.Sta(body));

    private static async Task Drain()
    {
        for (var index = 0; index < 3; index++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class DerivedDesktopWindow : DesktopWindow
    {
    }
}

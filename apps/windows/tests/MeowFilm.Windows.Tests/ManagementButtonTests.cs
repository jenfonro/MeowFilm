using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class ManagementButtonTests
{
    private static readonly (string Style, string Text, string? Background, string Foreground)[] Styles =
    [
        ("Settings.PrimaryButton", "添加／检测", "Accent", "OnAccent"),
        ("MeowFilm.ManagementDeleteSelected", "删除所选", null, "Error"),
        ("MeowFilm.ManagementRowButton", "检测／编辑", "ManagementBlue", "ManagementBlueText"),
        ("MeowFilm.ManagementRowButton.Primary", "更新", "ManagementSuccess", "AccentText"),
        ("MeowFilm.ManagementRowButton.Danger", "删除", "ErrorSurface", "Error")
    ];

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("management buttons: enabled and disabled templates preserve tones geometry and regular text",
            () => MusicQualityConsumerTests.Sta(ColorsAndGeometry));
        yield return new("management buttons: resource changes and high contrast restore current enabled and disabled colors",
            () => MusicQualityConsumerTests.Sta(ResourcesAndHighContrast));
    }

    private static Task ColorsAndGeometry() => WithButtons(async (root, buttons) =>
    {
        AssertButtons(root, buttons, false);
        // These are laid-out WPF snapshots, not desktop pixel captures.
        GeneralAppearanceTests.SavePreview(root, "management-buttons-normal", 500, 300);
        foreach (var button in buttons) button.IsEnabled = !button.IsEnabled;
        await Layout(root);
        AssertButtons(root, buttons, false);
        foreach (var button in buttons) button.IsEnabled = !button.IsEnabled;
        await Layout(root);
        AssertButtons(root, buttons, false);
    });

    private static Task ResourcesAndHighContrast() => WithButtons(async (root, buttons) =>
    {
        ReplaceBrushes(root, 20);
        await Layout(root);
        AssertButtons(root, buttons, false);
        DesktopTheme.SetIsHighContrast(root, true);
        await Layout(root);
        AssertButtons(root, buttons, true);
        GeneralAppearanceTests.SavePreview(root, "management-buttons-high-contrast", 500, 300);
        foreach (var button in buttons) button.IsEnabled = !button.IsEnabled;
        ReplaceBrushes(root, 40);
        await Layout(root);
        AssertButtons(root, buttons, true);
        DesktopTheme.SetIsHighContrast(root, false);
        await Layout(root);
        AssertButtons(root, buttons, false);
    });

    private static async Task WithButtons(Func<Border, Button[], Task> body)
    {
        var rows = new StackPanel();
        var root = new Border { Width = 500, Height = 300, Padding = new Thickness(24), Child = rows };
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/MeowFilm.Capabilities;component/Settings/SettingsTheme.xaml", UriKind.Relative) });
        var buttons = new List<Button>();
        for (int index = 0; index < Styles.Length; index++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var label = new TextBlock
            {
                Text = index < 2 ? "顶部" : "行内", Width = 64, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = SystemFonts.MessageFontFamily, FontWeight = FontWeights.Normal
            };
            row.Children.Add(label);
            foreach (bool enabled in new[] { true, false })
            {
                var button = new Button { Content = Styles[index].Text + (enabled ? "" : "（禁用）"), Width = 180, IsEnabled = enabled };
                button.SetResourceReference(FrameworkElement.StyleProperty, Styles[index].Style);
                row.Children.Add(button); buttons.Add(button);
            }
            rows.Children.Add(row);
        }
        var window = GeneralAppearanceTests.Window(root);
        DesktopTheme.SetIsHighContrast(root, false);
        try
        {
            window.Show(); await Layout(root);
            await body(root, buttons.ToArray());
        }
        finally { window.Close(); }
    }

    private static async Task Layout(Border root)
    {
        bool contrast = DesktopTheme.GetIsHighContrast(root);
        root.SetResourceReference(Border.BackgroundProperty, contrast ? SystemColors.WindowBrushKey : "MeowFilm.Brush.Surface");
        root.SetResourceReference(TextElement.ForegroundProperty, contrast ? SystemColors.WindowTextBrushKey : "MeowFilm.Brush.Text");
        root.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);
        root.UpdateLayout();
    }

    private static void ReplaceBrushes(FrameworkElement root, byte offset)
    {
        string[] keys = ["Accent", "OnAccent", "Hover", "Error", "ManagementBlue", "ManagementBlueText",
            "ManagementSuccess", "AccentText", "ErrorSurface", "ManagementNeutral", "Disabled"];
        for (int index = 0; index < keys.Length; index++)
            root.Resources["MeowFilm.Brush." + keys[index]] =
                new SolidColorBrush(Color.FromRgb((byte)(offset + index * 10), (byte)(90 + index), (byte)(160 - index)));
    }

    private static void AssertButtons(FrameworkElement root, Button[] buttons, bool contrast)
    {
        for (int index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            int kind = index / 2;
            var definition = Styles[kind];
            bool enabled = button.IsEnabled;
            string context = $"{definition.Style}, enabled={enabled}, highContrast={contrast}: ";
            Brush Resource(string name) => (Brush)root.FindResource("MeowFilm.Brush." + name);
            Brush background = definition.Background is null ? Brushes.Transparent : Resource(definition.Background);
            Brush foreground = Resource(definition.Foreground);
            Brush border = kind switch { 0 => Resource("Accent"), 1 => Resource("Error"), _ => Brushes.Transparent };
            if (!enabled)
            {
                background = kind switch { 0 => Resource("Hover"), 1 => Brushes.Transparent, _ => Resource("ManagementNeutral") };
                foreground = Resource("Disabled");
                if (kind == 1) border = Resource("Disabled");
            }
            if (contrast)
            {
                background = kind == 0 && enabled ? SystemColors.HighlightBrush : SystemColors.WindowBrush;
                foreground = !enabled ? SystemColors.GrayTextBrush :
                    kind == 0 ? SystemColors.HighlightTextBrush : SystemColors.WindowTextBrush;
                border = SystemColors.WindowTextBrush;
            }
            Check(ReferenceEquals(button.Style, root.FindResource(definition.Style)), context + "the real shared style is applied");
            Check(button.IsVisible && button.ActualWidth > 0, context + "the button is laid out");
            var surface = Visuals(button).OfType<Border>().First();
            var presenter = Visuals(button).OfType<ContentPresenter>().Single();
            var text = Visuals(button).OfType<TextBlock>().Single();
            Paint(button.Background, background, "control background");
            Paint(surface.Background, background, "visible template background");
            Paint(button.Foreground, foreground, "control foreground");
            Paint(TextElement.GetForeground(presenter), foreground, "content presenter foreground");
            Paint(text.Foreground, foreground, "visible text foreground");
            Paint(button.BorderBrush, border, "control border");
            Paint(surface.BorderBrush, border, "visible template border");
            Check(surface.BorderThickness == new Thickness(1), context + "the outline remains visible");
            double height = kind < 2 ? 32 : 30;
            Check(button.Height == height && button.MinHeight == height &&
                Math.Abs(button.ActualHeight - height) < .1 && Math.Abs(surface.ActualHeight - height) < .1 &&
                surface.CornerRadius == new CornerRadius(kind < 2 ? 10 : 15),
                context + "ordinary toolbar geometry stays distinct from row pills");
            Check(button.FontWeight == FontWeights.Normal && text.FontWeight == FontWeights.Normal &&
                button.FontStyle == FontStyles.Normal && text.FontStyle == FontStyles.Normal &&
                button.FontFamily.Equals(SystemFonts.MessageFontFamily) && text.FontFamily.Equals(SystemFonts.MessageFontFamily),
                context + "control and rendered text retain the regular system font");
            Check(text.IsVisible && text.ActualWidth > 0 && text.ActualHeight > 0 &&
                button.Opacity == 1 && surface.Opacity == 1 && text.Opacity == 1,
                context + "disabled text and surfaces remain visible without dimming their colors");
            Check(ReferenceEquals(button.FocusVisualStyle, root.FindResource("MeowFilm.FocusVisual")),
                context + "the shared focus visual is retained");

            void Paint(Brush? actual, Brush expected, string part) =>
                Check(actual is SolidColorBrush actualColor && expected is SolidColorBrush expectedColor &&
                    actualColor.Color == expectedColor.Color, context + part);
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Color = global::Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
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
            () => TestUiHost.InvokeAsync(ColorsAndGeometry));
        yield return new("management buttons: resource and native theme changes restore enabled and disabled colors",
            () => TestUiHost.InvokeAsync(ResourcesAndHighContrast));
    }

    private static Task ColorsAndGeometry() => WithButtons(async (root, buttons) =>
    {
        AssertButtons(root, buttons, new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
        // These are native RenderTargetBitmap snapshots, not desktop pixel captures.
        await GeneralAppearanceTests.SavePreview(root, "management-buttons-normal", 500, 300);
        foreach (var button in buttons) button.IsEnabled = !button.IsEnabled;
        await Layout(root);
        AssertButtons(root, buttons, new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
        foreach (var button in buttons) button.IsEnabled = !button.IsEnabled;
        await Layout(root);
        AssertButtons(root, buttons, new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
    });

    private static Task ResourcesAndHighContrast() => WithButtons(async (root, buttons) =>
    {
        ReplaceBrushes(root, 20);
        await Layout(root);
        AssertButtons(root, buttons, new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
        root.RequestedTheme = ElementTheme.Dark;
        await Layout(root);
        AssertButtons(root, buttons, new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
        await GeneralAppearanceTests.SavePreview(root, "management-buttons-current-theme", 500, 300);
        foreach (var button in buttons) button.IsEnabled = !button.IsEnabled;
        ReplaceBrushes(root, 40);
        await Layout(root);
        AssertButtons(root, buttons, new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
        root.RequestedTheme = ElementTheme.Light;
        await Layout(root);
        AssertButtons(root, buttons, new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
    });

    private static async Task WithButtons(Func<Border, Button[], Task> body)
    {
        var rows = new StackPanel();
        var root = new Border { Width = 500, Height = 300, Padding = new Thickness(24), Child = rows };
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("ms-appx:///MeowFilm.Capabilities/Settings/SettingsTheme.xaml") });
        var buttons = new List<Button>();
        for (int index = 0; index < Styles.Length; index++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var label = new TextBlock
            {
                Text = index < 2 ? "顶部" : "行内", Width = 64, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("XamlAutoFontFamily"), FontWeight = Microsoft.UI.Text.FontWeights.Normal
            };
            row.Children.Add(label);
            foreach (bool enabled in new[] { true, false })
            {
                var button = new Button { Content = Styles[index].Text + (enabled ? "" : "（禁用）"), Width = 180, IsEnabled = enabled };
                button.UseResource(FrameworkElement.StyleProperty, Styles[index].Style);
                row.Children.Add(button); buttons.Add(button);
            }
            rows.Children.Add(row);
        }
        var window = GeneralAppearanceTests.Window(root);
        root.RequestedTheme = ElementTheme.Light;
        try
        {
            window.Show(); await Layout(root);
            await body(root, buttons.ToArray());
        }
        finally { window.Close(); }
    }

    private static async Task Layout(Border root)
    {
        root.UseResource(Border.BackgroundProperty, "MeowFilm.Brush.Surface");
        root.UpdateLayout();
        await DesktopTestUi.DrainAsync();
        root.UpdateLayout();
    }

    private static void ReplaceBrushes(FrameworkElement root, byte offset)
    {
        string[] keys = ["Accent", "OnAccent", "Hover", "Error", "ManagementBlue", "ManagementBlueText",
            "ManagementSuccess", "AccentText", "ErrorSurface", "ManagementNeutral", "Disabled"];
        for (int index = 0; index < keys.Length; index++)
            root.Resources["MeowFilm.Brush." + keys[index]] =
                new SolidColorBrush(Color.FromArgb(255, (byte)(offset + index * 10), (byte)(90 + index), (byte)(160 - index)));
    }

    private static Brush SystemBrush(global::Windows.UI.ViewManagement.UIElementType type) => new SolidColorBrush(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(type));

    private static void AssertButtons(FrameworkElement root, Button[] buttons, bool contrast)
    {
        for (int index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            int kind = index / 2;
            var definition = Styles[kind];
            bool enabled = button.IsEnabled;
            string context = $"{definition.Style}, enabled={enabled}, highContrast={contrast}: ";
            Brush Resource(string name) => (Brush)DesktopVisual.Resource(root, "MeowFilm.Brush." + name);
            Brush background = definition.Background is null ? new SolidColorBrush(Colors.Transparent) : Resource(definition.Background);
            Brush foreground = Resource(definition.Foreground);
            Brush border = kind switch { 0 => Resource("Accent"), 1 => Resource("Error"), _ => new SolidColorBrush(Colors.Transparent) };
            if (!enabled)
            {
                background = kind switch { 0 => Resource("Hover"), 1 => new SolidColorBrush(Colors.Transparent), _ => Resource("ManagementNeutral") };
                foreground = Resource("Disabled");
                if (kind == 1) border = Resource("Disabled");
            }
            if (contrast)
            {
                background = kind == 0 && enabled ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.Highlight) : SystemBrush(global::Windows.UI.ViewManagement.UIElementType.Window);
                foreground = !enabled ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.GrayText) :
                    kind == 0 ? SystemBrush(global::Windows.UI.ViewManagement.UIElementType.HighlightText) : SystemBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText);
                border = SystemBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText);
            }
            Check(ReferenceEquals(button.Style, DesktopVisual.Resource(root, definition.Style)), context + "the real shared style is applied");
            Check(DesktopVisual.IsVisible(button) && button.ActualWidth > 0, context + "the button is laid out");
            var surface = Visuals(button).OfType<ContentPresenter>().First();
            var presenter = Visuals(button).OfType<ContentPresenter>().Single();
            var text = Visuals(button).OfType<TextBlock>().Single();
            Paint(button.Background, background, "control background");
            Paint(surface.Background, background, "visible template background");
            Paint(button.Foreground, foreground, "control foreground");
            Paint(presenter.Foreground, foreground, "content presenter foreground");
            Paint(text.Foreground, foreground, "visible text foreground");
            Paint(button.BorderBrush, border, "control border");
            Paint(surface.BorderBrush, border, "visible template border");
            Check(surface.BorderThickness == new Thickness(1), context + "the outline remains visible");
            double height = kind < 2 ? 32 : 30;
            Check(button.Height == height && button.MinHeight == height &&
                Math.Abs(button.ActualHeight - height) < .1 && Math.Abs(surface.ActualHeight - height) < .1 &&
                surface.CornerRadius == new CornerRadius(kind < 2 ? 10 : 15),
                context + "ordinary toolbar geometry stays distinct from row pills");
            Check(button.FontWeight == Microsoft.UI.Text.FontWeights.Normal && text.FontWeight == Microsoft.UI.Text.FontWeights.Normal &&
                button.FontStyle == global::Windows.UI.Text.FontStyle.Normal && text.FontStyle == global::Windows.UI.Text.FontStyle.Normal &&
                button.FontFamily.Equals(new FontFamily("XamlAutoFontFamily")) && text.FontFamily.Equals(new FontFamily("XamlAutoFontFamily")),
                context + "control and rendered text retain the regular system font");
            Check(DesktopVisual.IsVisible(text) && text.ActualWidth > 0 && text.ActualHeight > 0 &&
                button.Opacity == 1 && surface.Opacity == 1 && text.Opacity == 1,
                context + "disabled text and surfaces remain visible without dimming their colors");
            Check(button.UseSystemFocusVisuals,
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

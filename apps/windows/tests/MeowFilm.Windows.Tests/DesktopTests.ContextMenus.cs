using Colors = Microsoft.UI.Colors;
using System.Reflection;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Text;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> ContextMenuCases()
    {
        foreach (bool separateWindow in new[] { false, true })
        yield return Case("native text editing retains selection read-only undo and flyout in " + (separateWindow ? "window" : "shell"), async () =>
        {
            await using var f = new Fixture([new ProbeModule("editor")]); await f.LoadAsync();
            var editor = separateWindow ? new TextBox() : f.Search;
            using var host = separateWindow ? new DesktopTestUi.Host(editor, 400, 160) : null;
            await DrainAsync(); editor.Focus(FocusState.Programmatic);
            editor.Text = "fixture text"; editor.SelectAll();
            Equal(editor.Text.Length, editor.SelectionLength, "native select all targets original editor");
            True(editor.ContextFlyout is TextCommandBarFlyout, "complete native editor commands are retained");
            editor.ContextFlyout.ShowAt(editor); await DrainAsync();
            True(VisualTreeHelper.GetOpenPopupsForXamlRoot(editor.XamlRoot).Count > 0, "actual editor flyout opened");
            editor.ContextFlyout.Hide();
            editor.IsReadOnly = true;
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(editor)!;
            var value = (Microsoft.UI.Xaml.Automation.Provider.IValueProvider)peer.GetPattern(PatternInterface.Value);
            True(value.IsReadOnly && editor.SelectionLength == editor.Text.Length, "read-only preserves selection while refusing editing");
            editor.IsReadOnly = false;
            editor.Select(editor.Text.Length, 0); editor.SelectedText = " edit";
            True(editor.CanUndo, "native undo remains available"); editor.Undo();
            Equal("fixture text", editor.Text, "undo changes original editor rather than menu state");
        });
        yield return Case("surface gesture requires matching blank down and up and consumes dismissal", () =>
        {
            var gesture = new SurfaceGesture();
            True(!gesture.Up(true), "an orphan up never opens details");
            foreach (string layer in new[] { "quality", "spatial", "playlist", "volume", "more", "rate", "source", "queue" })
            {
                gesture.Down(true, true); gesture.Arm(true);
                True(!gesture.Up(true), layer + " closes without granting the same gesture");
                gesture.Down(true, false); gesture.Arm(true);
                True(gesture.Up(true), layer + " does not suppress a later gesture");
            }
            gesture.Down(true, false); gesture.Arm(false); True(!gesture.Up(true), "control press cannot become blank release");
            gesture.Down(true, false); gesture.Arm(true); True(!gesture.Up(false), "blank press ending on control is rejected");
            gesture.Down(true, false); gesture.Arm(true); gesture.Cancel(); True(!gesture.Up(true), "deactivation or unload cancels");
            gesture.Down(true, false); gesture.Arm(true); gesture.Block(); True(!gesture.Up(true), "closing a layer cannot rearm");
            return Task.CompletedTask;
        });
        yield return Case("activation admission preserves gesture lifetime through native message ordering", () =>
        {
            // Calls the very adapter used by the HWND hook, not physical input or desktop activation.
            var gesture = new SurfaceGesture();
            gesture.Message(0x21, 0, (nint)(0x201 << 16), false, false); // WM_MOUSEACTIVATE while inactive
            gesture.Message(0x6, 1, 0, true, false); // WM_ACTIVATE before routed pointer down
            gesture.Message(0x201, 1, 0, true, false); gesture.Arm(true);
            True(!gesture.CanDismiss, "activation click cannot dismiss an open queue either");
            True(!gesture.Up(true), "activation click cannot open details even though down sees active");
            gesture.Message(0x201, 1, 0, true, false); gesture.Arm(true);
            True(gesture.Up(true), "next click works");
            foreach (int trigger in new[] { 0x204, 0x207 })
            {
                gesture.Message(0x21, 0, (nint)(trigger << 16), false, false);
                gesture.Message(0x6, 1, 0, true, false);
                gesture.Message(0x201, 1, 0, true, false); gesture.Arm(true);
                True(gesture.Up(true), "right/middle activation does not consume a later left gesture");
            }
            gesture.Message(0x21, 0, (nint)(0x201 << 16), false, false);
            gesture.Message(0x100, 0, 0, true, false); // keyboard recovery, no stale mouse activation
            gesture.Down(true, false); gesture.Arm(true); True(gesture.Up(true), "keyboard recovery permits a normal click");
            gesture.Down(true, false); gesture.Arm(true); gesture.Message(0x6, 0, 0, false, false);
            True(!gesture.Up(true), "mid-gesture deactivation cancels");
            gesture.Message(0x201, 1, 0, true, true); gesture.Arm(true);
            True(!gesture.Up(true), "popup-captured native down preserves pre-close suppression");
            gesture.Down(true, false); gesture.Arm(true); gesture.Message(0x1f, 0, 0, true, false);
            True(!gesture.Up(true), "WM_CANCELMODE cancels admission");
            return Task.CompletedTask;
        });
        yield return Case("native menu preserves checks commands disabled admission and dynamic subitems", async () =>
        {
            var anchor = new Button { Content = "菜单" }; using var host = new DesktopTestUi.Host(anchor, 400, 200);
            var menu = new MenuFlyout(); var item = new ToggleMenuFlyoutItem { Text = "选择", IsChecked = true };
            int executions = 0; item.Click += (_, _) => executions++;
            var parent = new MenuFlyoutSubItem { Text = "更多" }; menu.Items.Add(item); menu.Items.Add(parent);
            anchor.ContextFlyout = menu; await DrainAsync(); menu.ShowAt(anchor); await DrainAsync();
            var later = new MenuFlyoutItem { Text = "后来" }; parent.Items.Add(later);
            True(item.IsChecked && parent.Items.Single() == later, "native checked state and live submenu retained");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item)!;
            ((Microsoft.UI.Xaml.Automation.Provider.IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
            await DrainAsync(); True(!item.IsChecked && executions == 1, "native action toggles and executes once");
            item.IsEnabled = false; True(!peer.IsEnabled(), "disabled menu action is exposed as unavailable");
            Equal(FontWeights.Normal, item.FontWeight, "checked and disabled states keep regular typography");
            menu.Hide();
        });
        yield return Case("theme palette reaches independent dictionaries and already mounted consumers", async () =>
        {
            var first = new ResourceDictionary { Source = new Uri("ms-appx:///MeowFilm.Desktop/Theme.xaml") };
            var second = new ResourceDictionary { Source = new Uri("ms-appx:///MeowFilm.Desktop/Theme.xaml") };
            var panel = new Border { Style = (Style)second["MeowFilm.FloatingSurface"] };
            using var host = new DesktopTestUi.Host(panel, 320, 180); await DrainAsync();
            var before = ((SolidColorBrush)panel.Background).Color;
            try
            {
                AppearanceColors.ApplyFloatingPalette("#FF2800"); await DrainAsync();
                foreach (string token in new[] { "FloatingSurface", "ItemHover", "ItemSelected", "InputSurface" })
                    Same(first["MeowFilm.Brush." + token], second["MeowFilm.Brush." + token], "shared mutable palette: " + token);
                True(((SolidColorBrush)panel.Background).Color != before, "mounted surface updates without recreation");
                True(AppearanceColors.ItemHover.Color != AppearanceColors.ItemSelected.Color, "hover and selected remain distinct");
                var contrast = (ResourceDictionary)first.ThemeDictionaries["HighContrast"];
                True(contrast.ContainsKey("MeowFilm.Brush.FloatingSurface") && contrast.ContainsKey("MeowFilm.Brush.Text"),
                    "system contrast has explicit surface and text resources");
            }
            finally { AppearanceColors.ApplyFloatingPalette(AppearanceColors.DefaultColor); }
        });
        yield return Case("queue rounded content and shadow stay in the common root", async () =>
        {
            var module = new ImmersiveProbe(); await using var f = new Fixture([module]); await f.LoadAsync();
            module.Request(DesktopGlobalSurface.PlayQueue);
            var surface = f.Element<Border>("QueueSurface"); var host = f.Element<ContentControl>("QueueHost");
            await UntilAsync(() => surface.Visibility == Visibility.Visible && Math.Abs(((TranslateTransform)surface.RenderTransform).X) < .5, "queue settled");
            var content = (Border)host.Content; content.Background = new SolidColorBrush(Colors.Black);
            Equal(new CornerRadius(12), surface.CornerRadius, "four rounded corners");
            Same(f.Shell.Root.XamlRoot, surface.XamlRoot, "queue and page share XamlRoot");
            True(surface.Shadow is not null && surface.Margin.Right >= 8, "queue retains shadow and room inside window");
            var image = await NativeDesktopCapture.CaptureAsync(f.Shell.Root);
            var bounds = DesktopTestUi.Bounds(surface, f.Shell.Root);
            int Sum(double x, double y)
            {
                int i = ((int)(y * image.Scale) * image.Width + (int)(x * image.Scale)) * 4;
                return image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2];
            }
            Equal(0, Sum(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), "fixture contains opaque black content");
            foreach (double x in new[] { bounds.Left + 1, bounds.Right - 2 })
            foreach (double y in new[] { bounds.Top + 1, bounds.Bottom - 2 })
                True(Sum(x, y) > 30, "rounded native content does not expose rectangular black corners");
        });
    }

    private static void Layout(FrameworkElement element)
    { element.Measure(new Size(600, 600)); element.Arrange(new Rect(new Point(), element.DesiredSize)); element.UpdateLayout(); }

    private sealed class MenuAppearance : IDesktopAppearance
    {
        public string ThemeColor { get; private set; } = AppearanceColors.DefaultColor;
        public bool CanEdit => true;
        public string Status => "";
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync(string color)
        {
            ThemeColor = color;
            PropertyChanged?.Invoke(this, new(nameof(ThemeColor)));
            return Task.CompletedTask;
        }
    }
}

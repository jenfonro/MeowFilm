using ColorPicker = MeowFilm.Desktop.ColorPicker;
using global::Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Key = Windows.System.VirtualKey;
using ModifierKeys = Windows.System.VirtualKeyModifiers;
using Microsoft.UI.Xaml.Media;
using Color = global::Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class GeneralAppearanceTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("General appearance: pale blue to near white gradient and strict opaque colors", () => TestUiHost.InvokeAsync(() =>
        {
            var background = (LinearGradientBrush)AppearanceColors.DefaultBackground;
            Check(background.StartPoint == new Point(0, 0) && background.EndPoint == new Point(0, 1));
            Check(background.GradientStops.Select(stop => AppearanceColors.Hex(stop.Color)).SequenceEqual(
                new[] { "#E4F2FC", "#EBF5FC", "#F5F8FB", "#F7F9FC" }));
            Check(background.GradientStops.Select(stop => stop.Offset).SequenceEqual(new[] { 0d, .24, .58, 1 }));
            Check(background.GradientStops.Zip(background.GradientStops.Skip(1),
                (first, next) => next.Color.R >= first.Color.R && next.Color.G >= first.Color.G &&
                    next.Color.B >= 251).All(value => value), "the lower canvas never returns to dark gray blue");
            Check(AppearanceColors.TryParse("#12abCD", out var parsed) && AppearanceColors.Hex(parsed) == "#12ABCD");
            foreach (var value in new[] { "red", "#123", "#ff112233", "#GG1122", " 123456", "#12345 " })
                Check(!AppearanceColors.TryParse(value, out _), "reject " + value);
            var custom = (LinearGradientBrush)AppearanceColors.Background("#FF0000");
            Check(custom.GradientStops.Select(stop => stop.Color).Distinct().Count() == 4);
            Check(custom.GradientStops.All(stop => Contrast(stop.Color, Color.FromArgb(255, 55, 65, 81)) >= 4.5),
                "custom hue remains a light layered canvas, not a saturated fill");
            return Task.CompletedTask;
        }));
        yield return new("General appearance: restore conflict lost receipt failure and invalid document", Persistence);
        yield return new("General appearance: shutdown waits for accepted write and can resume", Shutdown);
        yield return new("General appearance: color picker drafts cancel and confirm without input capture",
            () => TestUiHost.InvokeAsync(Picker));
        yield return new("General appearance: native color spectrum changes remain uncommitted and cancel restores authority",
            () => TestUiHost.InvokeAsync(PickerDrag));
        yield return new("General appearance: settings scrollbars stay visible and support both axes and contrast",
            () => TestUiHost.InvokeAsync(Scrollbars));
        yield return new("General appearance: preset dropdown custom HEX and failed writes retain authority",
            () => TestUiHost.InvokeAsync(ColorChoices));
        yield return new("General appearance: dialogs center on the window and retire with their owner",
            () => TestUiHost.InvokeAsync(Dialogs));
    }

    private static async Task Persistence()
    {
        var store = new AppSettingsTestDatabase();
        await using var owner = new AppAppearance(store);
        await owner.LoadAsync();
        Check(owner.CanEdit && owner.ThemeColor == "#E6F3FB" && store.Writes == 0);
        await owner.SaveAsync("#C0A0F0");
        await using var reopened = new AppAppearance(store);
        await reopened.LoadAsync();
        Check(reopened.ThemeColor == "#C0A0F0" && store.Writes == 1);
        await reopened.SaveAsync("#80D0C0");
        await owner.SaveAsync("#123456");
        Check(owner.ThemeColor == "#80D0C0" && owner.Status.Length > 0 && owner.CanEdit,
            "stale writer accepts authoritative value without retrying its write");

        store.Fault = true; store.FaultAfter = true;
        await owner.SaveAsync("#FFAACC");
        Check(owner.ThemeColor == "#FFAACC" && owner.Status == "", "accepted write's lost receipt reconciles");
        store.Fault = true; store.FaultAfter = false;
        await owner.SaveAsync("#AACCEE");
        Check(owner.ThemeColor == "#FFAACC" && owner.Status.Length > 0, "failed write does not publish draft");
        int writes = store.Writes;
        try { await owner.SaveAsync("#XYZ123"); throw new Exception("invalid color accepted"); }
        catch (ArgumentException) { }
        Check(store.Writes == writes);
        store.AppearanceData = JsonSerializer.SerializeToUtf8Bytes(new { schema = 99, themeColor = "#ABCDEF" });
        await reopened.LoadAsync();
        Check(!reopened.CanEdit && reopened.Status.Length > 0);
        await reopened.SaveAsync("#112233");
        Check(store.Writes == writes, "unreadable data is never silently replaced");
        store.AppearanceData = null;
        await reopened.LoadAsync();
        Check(reopened.CanEdit && reopened.ThemeColor == "#E6F3FB");
    }

    private static async Task Shutdown()
    {
        var store = new AppSettingsTestDatabase { HoldWrites = true};
        await using var owner = new AppAppearance(store);
        await owner.LoadAsync();
        var save = owner.SaveAsync("#ABCDEF");
        await store.Entered.Task;
        var shutdown = owner.PrepareShutdownAsync();
        Check(!owner.CanEdit && !shutdown.IsCompleted);
        store.Release.TrySetResult();
        await Task.WhenAll(save, shutdown);
        Check(owner.ThemeColor == "#ABCDEF" && !owner.CanEdit);
        await owner.CancelShutdownPreparationAsync();
        Check(owner.CanEdit);
    }

    private static async Task Picker()
    {
        var picker = new ColorPicker { SelectedColor = "#D7D1F7", Label = "测试颜色" };
        var window = Window(picker);
        int commits = 0; string? confirmed = null;
        picker.ColorCommitted += (_, e) => { commits++; confirmed = e.Color; };
        try
        {
            window.Show(); await Drain();
            var open = (Button)picker.FindName("OpenButton");
            Click(open); await Drain();
            var editor = FilmUiTest.Dialogs(picker).Single();
            var spectrum = (Microsoft.UI.Xaml.Controls.ColorPicker)editor.Content;
            await SavePreview((FrameworkElement)editor.Content, "color-picker", 320, 400);
            spectrum.Color = Color.FromArgb(255, 18, 52, 86);
            Check(commits == 0 && picker.SelectedColor == "#D7D1F7");
            Click(DialogButton(editor, "取消")); await Drain();
            Click(open); await Drain();
            editor = FilmUiTest.Dialogs(picker).Single();
            spectrum = (Microsoft.UI.Xaml.Controls.ColorPicker)editor.Content;
            Check(AppearanceColors.Hex(spectrum.Color) == "#D7D1F7", "cancel discards the draft");
            spectrum.Color = Color.FromArgb(255, 18, 58, 188);
            Click(DialogButton(editor, "确定")); await Drain();
            Check(commits == 1 && confirmed == "#123ABC" && picker.SelectedColor == "#D7D1F7",
                "confirmation sends one draft; authority still owns the selected value");
            var saved = (TextBox)picker.FindName("SavedHex");
            foreach (bool contrast in new[] { false, true })
            {
                DesktopTheme.SetIsHighContrast(picker, contrast);
                foreach (bool enabled in new[] { true, false })
                {
                    picker.IsEnabled = enabled;
                    await Drain();
                    var expected = new global::Windows.UI.ViewManagement.UISettings().UIElementColor(enabled ?
                        global::Windows.UI.ViewManagement.UIElementType.WindowText : global::Windows.UI.ViewManagement.UIElementType.GrayText);
                    Check(saved.IsEnabled == enabled && saved.Foreground is SolidColorBrush ink && ink.Color.A > 0 &&
                        (!new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast || ink.Color == expected),
                        "the actual inline color field preserves enabled and disabled colors in both themes");
                    Check(saved.Text == "#D7D1F7" && commits == 1,
                        "changing accessibility or editability neither changes nor commits the saved HEX");
                }
            }
            picker.IsEnabled = true;
            DesktopTheme.SetIsHighContrast(picker, true);
            Click(open); await Drain();
            Check(FilmUiTest.Dialogs(picker).Count == 1 && ((Microsoft.UI.Xaml.Controls.ColorPicker)FilmUiTest.Dialogs(picker).Single().Content).IsEnabled);
            picker.Visibility = Visibility.Collapsed;
            await Drain(); Check(FilmUiTest.Dialogs(picker).Count == 0, "hidden views retire their dialog");
        }
        finally { window.Close(); }
    }

    private static async Task PickerDrag()
    {
        var picker = new ColorPicker { SelectedColor = "#FF0000" };
        var window = Window(picker);
        int commits = 0;
        picker.ColorCommitted += (_, _) => commits++;
        try
        {
            window.Show(); await Drain();
            Click((Button)picker.FindName("OpenButton")); await Drain();
            var dialog = FilmUiTest.Dialogs(picker).Single();
            var spectrum = (Microsoft.UI.Xaml.Controls.ColorPicker)dialog.Content;
            foreach (var color in new[] { "#CCA3A3", "#B27D7D", "#995C5C", "#B27D7D", "#000000", "#CCA3A3" })
            {
                Check(AppearanceColors.TryParse(color, out var value));
                spectrum.Color = value; await Drain();
                Check(AppearanceColors.Hex(spectrum.Color) == color, "native spectrum keeps the selected color without accumulated drift");
                Check(commits == 0 && picker.SelectedColor == "#FF0000", "spectrum changes only preview");
            }
            Click(DialogButton(dialog, "取消")); await Drain();
            Click((Button)picker.FindName("OpenButton")); await Drain();
            spectrum = (Microsoft.UI.Xaml.Controls.ColorPicker)FilmUiTest.Dialogs(picker).Single().Content;
            Check(AppearanceColors.Hex(spectrum.Color) == "#FF0000" && commits == 0, "cancel discards the entire spectrum draft");
        }
        finally { window.Close(); }
    }

    private static async Task Scrollbars()
    {
        var scroll = new ScrollViewer
        {
            Width = 280, Height = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 500, Height = 800 }
        };
        scroll.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("ms-appx:///MeowFilm.Desktop/SettingsScrollChrome.xaml") });
        scroll.Style = (Style)DesktopVisual.Resource(scroll, "MeowFilm.SettingsScrollViewer");
        var window = Window(scroll);
        try
        {
            window.Show(); await Drain();
            Check(scroll.ScrollableHeight > 0 && scroll.ScrollableWidth > 0,
                "both native scroll ranges remain available");
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ScrollViewerAutomationPeer(scroll);
            var pattern = (Microsoft.UI.Xaml.Automation.Provider.IScrollProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Scroll);
            Check(pattern.VerticallyScrollable && pattern.HorizontallyScrollable, "both axes remain accessible");
            Check(!OverlayScrollChrome.GetEnabled(scroll), "settings do not attach ordinary-page input state");
            scroll.ChangeView(100, 240, null, true); await Drain();
            Check(Math.Abs(scroll.VerticalOffset - 240) < 1 && Math.Abs(scroll.HorizontalOffset - 100) < 1);
            DesktopTheme.SetIsHighContrast(scroll, true); await Drain();
            Check(pattern.VerticallyScrollable && pattern.HorizontallyScrollable, "contrast never removes scrolling capability");
        }
        finally { window.Close(); }
    }

    private static async Task ColorChoices()
    {
        var store = new AppSettingsTestDatabase();
        await using var appearance = new AppAppearance(store);
        await appearance.LoadAsync();
        var view = new GeneralSettingsView(appearance, []);
        var window = Window(view);
        try
        {
            window.Show(); await Drain();
            var selection = (ColorSelection)view.FindName("ThemeSelection");
            var choices = (ComboBox)selection.FindName("Choices");
            var editor = (ColorPicker)selection.FindName("Custom");
            var saved = (TextBox)editor.FindName("SavedHex");
            Check((string)choices.SelectedValue == AppearanceColors.DefaultColor && !DesktopVisual.IsVisible(editor));
            choices.SelectedValue = "custom"; await Drain();
            Check(DesktopVisual.IsVisible(editor) && saved.Text == AppearanceColors.DefaultColor && store.Writes == 0,
                "custom choice exposes the saved color without a speculative write");
            void Commit(string text)
            {
                saved.Text = text;
                FilmUiTest.Call(editor, "CommitHex");
            }
            Commit("#12345Z"); await Drain();
            Check(store.Writes == 0 && saved.Text == AppearanceColors.DefaultColor, "invalid inline HEX reverts without writing");
            Commit("#12abcd"); await Drain();
            Check(appearance.ThemeColor == "#12ABCD" && saved.Text == "#12ABCD" && store.Writes == 1);

            store.Fault = true; store.FaultAfter = false;
            choices.SelectedValue = AppearanceColors.DefaultColor; await Drain();
            Check(appearance.ThemeColor == "#12ABCD" && (string)choices.SelectedValue == "custom" &&
                saved.Text == "#12ABCD", "failed preset selection returns to the saved custom value");
            choices.SelectedValue = AppearanceColors.DefaultColor; await Drain();
            Check(appearance.ThemeColor == AppearanceColors.DefaultColor && !DesktopVisual.IsVisible(editor));
            choices.SelectedValue = "custom";
            Click((Button)editor.FindName("OpenButton")); await Drain();
            var popup = FilmUiTest.Dialogs(editor).Single();
            Check(ReferenceEquals(popup.XamlRoot, editor.XamlRoot), "palette uses the owner's common XAML root");
            ((Microsoft.UI.Xaml.Controls.ColorPicker)popup.Content).Color = Color.FromArgb(255, 170, 187, 204);
            int writes = store.Writes;
            Click(DialogButton(popup, "取消")); await Drain();
            Check(store.Writes == writes && saved.Text == AppearanceColors.DefaultColor);
            await SavePreview(view, "refinement-general", 860, 660);
        }
        finally { window.Close(); }
    }

    private static async Task Dialogs()
    {
        var anchor = new Button { Content = "添加", Width = 60, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var root = new Grid(); root.Children.Add(anchor);
        var surface = new Border { Width = 360, Height = 280, Background = new SolidColorBrush(Colors.White) };
        var dialog = new ContentDialog { Content = surface, CloseButtonText = "取消" };
        var window = Window(root);
        Task<ContentDialogResult>? showing = null;
        try
        {
            window.Show(); await Drain();
            showing = DialogPlacement.ShowAsync(anchor, dialog); await Drain();
            foreach (var width in new[] { 1000d, 860d })
            {
                window.Resize(width, 680); await Drain();
                var bounds = DesktopTestUi.Bounds(dialog, root);
                Check(ReferenceEquals(dialog.XamlRoot, root.XamlRoot), "dialog shares the real application root");
                Check(Math.Abs(bounds.X + bounds.Width / 2 - root.ActualWidth / 2) < 3 &&
                    Math.Abs(bounds.Y + bounds.Height / 2 - root.ActualHeight / 2) < 3,
                    "native dialog centers on the whole root after resizing");
            }
            var before = DesktopTestUi.Bounds(dialog, root);
            var position = window.AppWindow.Position;
            window.AppWindow.Move(new global::Windows.Graphics.PointInt32(position.X + 30, position.Y + 20)); await Drain();
            var after = DesktopTestUi.Bounds(dialog, root);
            Check(Math.Abs(before.X - after.X) < 1 && Math.Abs(before.Y - after.Y) < 1,
                "window movement does not change the page-local dialog layout");
            Click(DialogButton(dialog, "取消"));
            Check(await showing == ContentDialogResult.None, "native cancellation closes without committing");
            showing = DialogPlacement.ShowAsync(anchor, dialog); await Drain();
            anchor.Visibility = Visibility.Collapsed;
            Check(await showing == ContentDialogResult.None, "hidden owner retires its dialog");
            anchor.Visibility = Visibility.Visible;
            showing = DialogPlacement.ShowAsync(anchor, dialog); await Drain();
            window.Close();
            Check(await showing == ContentDialogResult.None, "owner unload retires the dialog");
        }
        finally { dialog.Hide(); if (showing is not null) await showing; window.Close(); }
    }

    private static Button DialogButton(ContentDialog dialog, string text) => Visuals(dialog).OfType<Button>()
        .Single(button => Equals(button.Content, text));

    internal static async Task SavePreview(FrameworkElement view, string name, int width, int height)
    {
        string? root = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT");
        if (root is null) return;
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root);
        Directory.CreateDirectory(root);
        view.Measure(new(width, height)); view.Arrange(new(0, 0, width, height)); view.UpdateLayout();
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(view, width * dpi / 96, height * dpi / 96);
            var pixels = await bitmap.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels.ToArray());
            await encoder.FlushAsync();
            stream.Seek(0);
            using var file = File.Create(Path.Combine(root, $"{name}-{dpi}.png"));
            await stream.AsStreamForRead().CopyToAsync(file);
        }
    }
    internal static Window Window(FrameworkElement content) => FilmUiTest.Window(content, 860, 680);
    private static void Click(Button button) => DesktopTestUi.Invoke(button);
    private static double Contrast(Color light, Color dark)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        return (Luminance(light) + .05) / (Luminance(dark) + .05);
    }
    private static async Task Drain()
    { for (int i = 0; i < 4; i++) await DesktopTestUi.DrainAsync(); }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
}

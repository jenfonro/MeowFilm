using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class GeneralAppearanceTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("General appearance: pale blue to near white gradient and strict opaque colors", () =>
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
            Check(custom.GradientStops.All(stop => Contrast(stop.Color, Color.FromRgb(55, 65, 81)) >= 4.5),
                "custom hue remains a light layered canvas, not a saturated fill");
            return Task.CompletedTask;
        });
        yield return new("General appearance: restore conflict lost receipt failure and invalid document", Persistence);
        yield return new("General appearance: shutdown waits for accepted write and can resume", Shutdown);
        yield return new("General appearance: color picker drafts cancel and confirm without input capture",
            () => MusicQualityConsumerTests.Sta(Picker));
        yield return new("General appearance: color picker drag follows absolute displacement without drift",
            () => MusicQualityConsumerTests.Sta(PickerDrag));
        yield return new("General appearance: settings scrollbars stay visible and support both axes and contrast",
            () => MusicQualityConsumerTests.Sta(Scrollbars));
        yield return new("General appearance: preset dropdown custom HEX and failed writes retain authority",
            () => MusicQualityConsumerTests.Sta(ColorChoices));
        yield return new("General appearance: dialogs center on the window and retire with their owner",
            () => MusicQualityConsumerTests.Sta(Dialogs));
    }

    private static async Task Persistence()
    {
        var store = new DocumentStore();
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
        store.FaultNamespace = AppAppearance.DocumentNamespace;
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
        store.Data[AppAppearance.DocumentNamespace] = JsonSerializer.SerializeToUtf8Bytes(new { schema = 99, themeColor = "#ABCDEF" });
        await reopened.LoadAsync();
        Check(!reopened.CanEdit && reopened.Status.Length > 0);
        await reopened.SaveAsync("#112233");
        Check(store.Writes == writes, "unreadable data is never silently replaced");
        store.Data.Remove(AppAppearance.DocumentNamespace);
        await reopened.LoadAsync();
        Check(reopened.CanEdit && reopened.ThemeColor == "#E6F3FB");
    }

    private static async Task Shutdown()
    {
        var store = new DocumentStore { HoldNamespace = AppAppearance.DocumentNamespace };
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
            var editor = (Popup)picker.FindName("Editor");
            var hex = (TextBox)picker.FindName("HexInput");
            SavePreview((FrameworkElement)editor.Child, "color-picker",
                (int)Math.Ceiling(editor.Child.RenderSize.Width), (int)Math.Ceiling(editor.Child.RenderSize.Height));
            hex.Text = "#123456";
            Check(commits == 0 && picker.SelectedColor == "#D7D1F7");
            Click(Visuals(editor.Child).OfType<Button>().Single(button => Equals(button.Content, "取消")));
            Click(open); await Drain();
            Check(hex.Text == "#D7D1F7", "cancel discards the draft");
            hex.Text = "#12345Z";
            Check(!((Button)picker.FindName("ConfirmButton")).IsEnabled);
            hex.Text = "#123abc";
            Click((Button)picker.FindName("ConfirmButton"));
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
                    var expected = contrast ? (enabled ? SystemColors.WindowTextBrush : SystemColors.GrayTextBrush)
                        : (Brush)picker.FindResource(enabled ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Disabled");
                    Check(saved.IsEnabled == enabled && saved.Foreground == expected,
                        "the actual inline color field preserves enabled and disabled colors in both themes");
                    Check(saved.Text == "#D7D1F7" && commits == 1,
                        "changing accessibility or editability neither changes nor commits the saved HEX");
                }
            }
            picker.IsEnabled = true;
            DesktopTheme.SetIsHighContrast(picker, true);
            Click(open); await Drain();
            Check(editor.IsOpen && hex.IsEnabled);
            picker.Visibility = Visibility.Collapsed;
            await Drain(); Check(!editor.IsOpen, "hidden views retire their popup");
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
            var thumb = (Thumb)picker.FindName("ColorThumb");
            var marker = (FrameworkElement)picker.FindName("Marker");
            var hex = (TextBox)picker.FindName("HexInput");
            thumb.RaiseEvent(new DragStartedEventArgs(56, 34) { RoutedEvent = Thumb.DragStartedEvent });
            void At(double x, double y, string color)
            {
                Check(Math.Abs(Canvas.GetLeft(marker) + marker.Width / 2 - x) < .01 &&
                    Math.Abs(Canvas.GetTop(marker) + marker.Height / 2 - y) < .01,
                    $"selection follows the fixed field coordinate ({x}, {y})");
                Check(hex.Text == color, $"field color at ({x}, {y}) is {color}, got {hex.Text}");
                Check(commits == 0 && picker.SelectedColor == "#FF0000", "dragging only previews");
            }
            void Drag(double x, double y) =>
                thumb.RaiseEvent(new DragDeltaEventArgs(x, y) { RoutedEvent = Thumb.DragDeltaEvent });
            At(56, 34, "#CCA3A3");
            Drag(28, 17); At(84, 51, "#B27D7D");
            Drag(56, 34); At(112, 68, "#995C5C");
            Drag(28, 17); At(84, 51, "#B27D7D");
            Drag(400, 300); At(280, 170, "#000000");
            Drag(0, 0); At(56, 34, "#CCA3A3");
            var editor = (Popup)picker.FindName("Editor");
            Click(Visuals(editor.Child).OfType<Button>().Single(button => Equals(button.Content, "取消")));
            Click((Button)picker.FindName("OpenButton")); await Drain();
            Check(hex.Text == "#FF0000" && commits == 0, "cancel discards the entire drag preview");
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
        { Source = new Uri("/MeowFilm.Desktop;component/SettingsScrollChrome.xaml", UriKind.Relative) });
        scroll.Style = (Style)scroll.FindResource("MeowFilm.SettingsScrollViewer");
        var window = Window(scroll);
        try
        {
            window.Show(); await Drain();
            var vertical = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var horizontal = (ScrollBar)scroll.Template.FindName("PART_HorizontalScrollBar", scroll);
            Check(vertical.IsVisible && horizontal.IsVisible && vertical.Opacity == 1 && horizontal.Opacity == 1);
            Check(!OverlayScrollChrome.GetEnabled(scroll), "settings do not attach the ordinary-page idle behavior");
            scroll.ScrollToVerticalOffset(240); scroll.ScrollToHorizontalOffset(100);
            await Drain();
            Check(scroll.VerticalOffset == 240 && scroll.HorizontalOffset == 100 && vertical.Opacity == 1);
            DesktopTheme.SetIsHighContrast(scroll, true); await Drain();
            Check(Visuals(vertical).OfType<Thumb>().Single().Background == SystemColors.WindowTextBrush);
            Check(!vertical.HasAnimatedProperties && !horizontal.HasAnimatedProperties);
        }
        finally { window.Close(); }
    }

    private static async Task ColorChoices()
    {
        var store = new DocumentStore();
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
            Check((string)choices.SelectedValue == AppearanceColors.DefaultColor && !editor.IsVisible);
            choices.SelectedValue = "custom"; await Drain();
            Check(editor.IsVisible && saved.Text == AppearanceColors.DefaultColor && store.Writes == 0,
                "custom choice exposes the saved color without a speculative write");
            void Commit(string text)
            {
                saved.Text = text;
                saved.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                { RoutedEvent = UIElement.PreviewKeyDownEvent });
            }
            Commit("#12345Z"); await Drain();
            Check(store.Writes == 0 && saved.Text == AppearanceColors.DefaultColor, "invalid inline HEX reverts without writing");
            Commit("#12abcd"); await Drain();
            Check(appearance.ThemeColor == "#12ABCD" && saved.Text == "#12ABCD" && store.Writes == 1);
            store.FaultNamespace = AppAppearance.DocumentNamespace;
            store.Fault = true; store.FaultAfter = false;
            choices.SelectedValue = AppearanceColors.DefaultColor; await Drain();
            Check(appearance.ThemeColor == "#12ABCD" && (string)choices.SelectedValue == "custom" &&
                saved.Text == "#12ABCD", "failed preset selection returns to the saved custom value");
            choices.SelectedValue = AppearanceColors.DefaultColor; await Drain();
            Check(appearance.ThemeColor == AppearanceColors.DefaultColor && !editor.IsVisible);
            choices.SelectedValue = "custom";
            Click((Button)editor.FindName("OpenButton")); await Drain();
            var popup = (Popup)editor.FindName("Editor");
            Check(popup.Placement == PlacementMode.Bottom && !DialogPlacement.GetCentered(popup),
                "palette remains anchored, not centered like a dialog");
            ((TextBox)editor.FindName("HexInput")).Text = "#AABBCC";
            int writes = store.Writes;
            Click(Visuals(popup.Child).OfType<Button>().Single(button => Equals(button.Content, "取消")));
            Check(store.Writes == writes && saved.Text == AppearanceColors.DefaultColor);
            SavePreview(view, "refinement-general", 860, 660);
        }
        finally { window.Close(); }
    }

    private static async Task Dialogs()
    {
        var anchor = new Button { Content = "添加", Width = 60, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var root = new Grid(); root.Children.Add(anchor);
        var surface = new Border { Width = 360, Height = 280, Background = Brushes.White };
        var popup = new Popup { PlacementTarget = anchor, Child = surface, AllowsTransparency = true, StaysOpen = true };
        root.Children.Add(popup);
        DialogPlacement.SetCentered(popup, true);
        var window = Window(root);
        window.Left = 80; window.Top = 60;
        try
        {
            window.Show(); await Drain();
            foreach (var width in new[] { 1000d, 860d })
            {
                window.Width = width; window.UpdateLayout(); popup.IsOpen = true; await Drain();
                var position = popup.CustomPopupPlacementCallback!(surface.RenderSize, anchor.RenderSize, new())[0].Point;
                var centered = anchor.TranslatePoint(position, root);
                Check(Math.Abs(centered.X + surface.ActualWidth / 2 - root.ActualWidth / 2) < 1 &&
                    Math.Abs(centered.Y + surface.ActualHeight / 2 - root.ActualHeight / 2) < 1,
                    "dialog centers on the whole window, not the right-hand trigger");
                var actual = surface.PointToScreen(new(surface.ActualWidth / 2, surface.ActualHeight / 2));
                var expected = root.PointToScreen(new(root.ActualWidth / 2, root.ActualHeight / 2));
                Check((actual - expected).Length < 3, "the actual popup HWND is centered after window resize");
            }
            window.Left += 30; window.Top += 20; await Drain();
            Check((surface.PointToScreen(new(surface.ActualWidth / 2, surface.ActualHeight / 2)) -
                root.PointToScreen(new(root.ActualWidth / 2, root.ActualHeight / 2))).Length < 3,
                "moving the window keeps the actual dialog centered");
            surface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(surface), 0, Key.Escape)
            { RoutedEvent = UIElement.KeyDownEvent });
            await Drain();
            Check(!popup.IsOpen, "Escape closes the dialog without injecting input");
            popup.IsOpen = true; await Drain();
            anchor.Visibility = Visibility.Collapsed; await Drain();
            Check(!popup.IsOpen, "hidden owner closes its dialog");
            anchor.Visibility = Visibility.Visible; popup.IsOpen = true; await Drain();
            window.Close(); await Drain();
            Check(!popup.IsOpen, "closing the owner retires the floating window");
        }
        finally { popup.IsOpen = false; window.Close(); }
    }

    internal static void SavePreview(FrameworkElement view, string name, int width, int height)
    {
        string? root = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT");
        if (root is null) return;
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root);
        Directory.CreateDirectory(root);
        view.Measure(new(width, height)); view.Arrange(new(0, 0, width, height)); view.UpdateLayout();
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            var bitmap = new RenderTargetBitmap(width * dpi / 96, height * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(root, $"{name}-{dpi}.png"));
            encoder.Save(file);
        }
    }
    internal static Window Window(FrameworkElement content) => new()
    {
        Content = content, Width = 860, Height = 680, Left = -10000, Top = -10000,
        ShowActivated = false, ShowInTaskbar = false,
        Background = AppearanceColors.DefaultBackground
    };
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
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
    { for (int i = 0; i < 4; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
}

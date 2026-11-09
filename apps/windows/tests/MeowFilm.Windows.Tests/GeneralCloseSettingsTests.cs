using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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

public static class GeneralCloseSettingsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("General close settings: absent owner preserves three working general areas",
            () => MusicQualityConsumerTests.Sta(WithoutOwner));
        yield return new("General close settings: three choices save immediately and follow the shared owner",
            () => MusicQualityConsumerTests.Sta(ChoicesAndSynchronization));
        yield return new("General close settings: failed selections restore authority and publish the standard notice",
            () => MusicQualityConsumerTests.Sta(FailedSelection));
        yield return new("General close settings: unreadable configuration disables editing until explicit reload",
            () => MusicQualityConsumerTests.Sta(UnreadableConfiguration));
        yield return new("General close settings: pending saves and cancelled shutdown preserve the current selection",
            () => MusicQualityConsumerTests.Sta(PendingSave));
        yield return new("General close settings: native focus layout and PNG previews follow all existing themes",
            () => MusicQualityConsumerTests.Sta(LayoutAndPreviews));
    }

    private static async Task WithoutOwner()
    {
        var store = new DocumentStore();
        await using var appearance = new AppAppearance(store);
        var source = new SettingsSource();
        var view = new GeneralSettingsView(appearance, [source]);
        var window = GeneralAppearanceTests.Window(view);
        try
        {
            window.Show(); await Drain();
            var tabs = Tabs(view);
            Check(tabs.Select(tab => tab.Content).SequenceEqual(new[] { "外观", "首页数据源", "缓存控制" }));
            Check(!Visuals(view).OfType<ComboBox>().Any(control => AutomationProperties.GetName(control) == "关闭主窗口时"),
                "an absent close-preference owner contributes no empty controls or tab");
            Check(source.AppearanceSettingsView.IsVisible);
            tabs[1].IsChecked = true; await Drain();
            Check(source.HomeSourceSettingsView.IsVisible && !source.AppearanceSettingsView.IsVisible);
            tabs[2].IsChecked = true; await Drain();
            Check(source.CacheSettingsView.IsVisible && !source.HomeSourceSettingsView.IsVisible);
            PressKey(tabs[2], Key.Home); await Drain();
            Check(tabs[0].IsChecked == true && source.AppearanceSettingsView.IsVisible);
            PressKey(tabs[0], Key.End); await Drain();
            Check(tabs[2].IsChecked == true && source.CacheSettingsView.IsVisible,
                "keyboard navigation visits only available capabilities");
            Check(store.Writes == 0);
        }
        finally { window.Close(); }
    }

    private static async Task ChoicesAndSynchronization()
    {
        var store = new DocumentStore();
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        var source = new SettingsSource();
        var view = new GeneralSettingsView(appearance, [source], preferences);
        var window = GeneralAppearanceTests.Window(view);
        try
        {
            window.Show(); await Drain();
            var tabs = Tabs(view);
            Check(tabs.Select(tab => tab.Content).SequenceEqual(new[] { "外观", "首页数据源", "缓存控制", "窗口行为" }));
            OpenWindowArea(view); await Drain();
            var choices = Choices(view);
            var options = choices.Items.Cast<ComboBoxItem>().ToArray();
            Check(options.Select(option => option.Content).SequenceEqual(new[] { "每次询问", "最小化到托盘", "退出应用" }));
            Check(options.Select(option => (WindowCloseBehavior)option.Tag).SequenceEqual(new[]
                { WindowCloseBehavior.Ask, WindowCloseBehavior.MinimizeToTray, WindowCloseBehavior.Exit }));
            Check(options.All(option => AutomationProperties.GetName(option) == (string)option.Content));
            Check(new ComboBoxAutomationPeer(choices).GetName() == "关闭主窗口时" && choices.Focusable &&
                choices.IsTabStop && choices.FocusVisualStyle is not null);
            Check(preferences.CanEdit && choices.IsEnabled && (WindowCloseBehavior)choices.SelectedValue == WindowCloseBehavior.Ask &&
                store.Writes == 0, "opening the settings reads the default without saving a draft");
            Check(!source.AppearanceSettingsView.IsVisible && !source.HomeSourceSettingsView.IsVisible &&
                !source.CacheSettingsView.IsVisible, "the new area does not mix capability-owned settings");
            foreach (int index in new[] { 1, 2, 0 })
            {
                int writes = store.Writes;
                choices.SelectedIndex = index; await Drain();
                Check(preferences.Behavior == (WindowCloseBehavior)options[index].Tag &&
                    Equals(choices.SelectedValue, preferences.Behavior) && preferences.Status == "" &&
                    store.Writes == writes + 1, "each changed choice is immediately saved once, including reset to Ask");
            }
            int before = store.Writes;
            Check(await preferences.SaveAsync(WindowCloseBehavior.MinimizeToTray)); await Drain();
            Check(Equals(choices.SelectedValue, WindowCloseBehavior.MinimizeToTray) && store.Writes == before + 1,
                "a close-dialog consumer updating the same owner synchronizes the setting without an echo write");
            await using var reopened = new AppClosePreferences(store);
            await reopened.LoadAsync();
            Check(reopened.Behavior == WindowCloseBehavior.MinimizeToTray);
            tabs[0].IsChecked = true; await Drain();
            Check(source.AppearanceSettingsView.IsVisible);
            OpenWindowArea(view); await Drain();
            Check(Equals(choices.SelectedValue, WindowCloseBehavior.MinimizeToTray) && store.Writes == before + 1,
                "switching tabs preserves the authoritative choice without saving again");
        }
        finally { window.Close(); }
    }

    private static async Task FailedSelection()
    {
        var store = new DocumentStore { FaultNamespace = AppClosePreferences.DocumentNamespace };
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        var view = new GeneralSettingsView(appearance, [], preferences);
        var window = GeneralAppearanceTests.Window(view);
        var outlet = (NoticeOutlet)view.FindName("ClosePreferencesNotice");
        var notices = new List<string>();
        window.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, e) =>
        {
            if (ReferenceEquals(e.Outlet, outlet)) notices.Add(e.Message);
        }));
        try
        {
            window.Show(); await Drain(); OpenWindowArea(view); await Drain();
            var choices = Choices(view);
            var reload = (Button)view.FindName("ReloadClosePreferencesButton");
            store.Fault = true; store.FaultAfter = false;
            choices.SelectedIndex = 2; await Drain();
            Check(preferences.Behavior == WindowCloseBehavior.Ask && Equals(choices.SelectedValue, WindowCloseBehavior.Ask) &&
                choices.IsEnabled && preferences.Status.Length > 0 && reload.IsVisible &&
                !store.Data.ContainsKey(AppClosePreferences.DocumentNamespace),
                "a rejected selection neither changes the saved behavior nor remains selected");
            Check(outlet.Message == preferences.Status && notices.Contains(preferences.Status),
                "save failure reaches the window through NoticeOutlet");
            Check(!Visuals(view).OfType<TextBlock>().Any(text => text.Text == preferences.Status),
                "the error is not inserted into the settings layout");
            store.Fault = true; store.FaultAfter = true;
            choices.SelectedIndex = 1; await Drain();
            Check(preferences.Behavior == WindowCloseBehavior.MinimizeToTray &&
                Equals(choices.SelectedValue, WindowCloseBehavior.MinimizeToTray) &&
                preferences.Status == "" && !reload.IsVisible && outlet.Message == "",
                "a lost receipt is shown as saved only after the owner confirms storage");

            await using var other = new AppClosePreferences(store);
            await other.LoadAsync(); Check(await other.SaveAsync(WindowCloseBehavior.Exit));
            choices.SelectedIndex = 0; await Drain();
            Check(preferences.Behavior == WindowCloseBehavior.Exit && Equals(choices.SelectedValue, WindowCloseBehavior.Exit) &&
                preferences.Status.Length > 0 && reload.IsVisible, "a conflict restores the newer saved behavior");
            Check(await other.SaveAsync(WindowCloseBehavior.Ask));
            int writes = store.Writes;
            Click(reload); await Drain();
            Check(preferences.Behavior == WindowCloseBehavior.Ask && Equals(choices.SelectedValue, WindowCloseBehavior.Ask) &&
                preferences.Status == "" && !reload.IsVisible && store.Writes == writes,
                "reload reads the latest authority without replaying a rejected selection");
        }
        finally { window.Close(); }
    }

    private static async Task UnreadableConfiguration()
    {
        var store = new DocumentStore();
        store.Data[AppClosePreferences.DocumentNamespace] = "[]"u8.ToArray();
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        var view = new GeneralSettingsView(appearance, [], preferences);
        var window = GeneralAppearanceTests.Window(view);
        try
        {
            window.Show(); await Drain(); OpenWindowArea(view); await Drain();
            var choices = Choices(view);
            var reload = (Button)view.FindName("ReloadClosePreferencesButton");
            Check(!preferences.CanEdit && !choices.IsEnabled && preferences.Status.Length > 0 &&
                reload.IsVisible && reload.IsEnabled && store.Writes == 0,
                "an unreadable document cannot appear as an editable default");
            await preferences.PrepareShutdownAsync(); await preferences.CancelShutdownPreparationAsync(); await Drain();
            Check(!choices.IsEnabled && store.Writes == 0);
            store.Data.Remove(AppClosePreferences.DocumentNamespace);
            Click(reload); await Drain();
            Check(preferences.CanEdit && choices.IsEnabled && preferences.Status == "" && !reload.IsVisible &&
                Equals(choices.SelectedValue, WindowCloseBehavior.Ask) && store.Writes == 0,
                "an explicit successful read enables the confirmed default without writing");
            choices.SelectedIndex = 2; await Drain();
            Check(preferences.Behavior == WindowCloseBehavior.Exit && store.Writes == 1);
        }
        finally { window.Close(); }
    }

    private static async Task PendingSave()
    {
        var store = new DocumentStore { HoldNamespace = AppClosePreferences.DocumentNamespace };
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        var view = new GeneralSettingsView(appearance, [], preferences);
        var window = GeneralAppearanceTests.Window(view);
        Task? closing = null;
        try
        {
            window.Show(); await Drain(); OpenWindowArea(view); await Drain();
            var choices = Choices(view);
            choices.SelectedIndex = 1;
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Drain();
            Check(!choices.IsEnabled && !preferences.CanEdit && preferences.Behavior == WindowCloseBehavior.Ask,
                "the current owner remains unchanged until the accepted write settles");
            closing = preferences.PrepareShutdownAsync();
            Check(!closing.IsCompleted, "shutdown cannot release storage before the settings save");
            store.Release.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(3)); await Drain();
            Check(!choices.IsEnabled && Equals(choices.SelectedValue, WindowCloseBehavior.MinimizeToTray) && store.Writes == 1);
            await preferences.CancelShutdownPreparationAsync(); await Drain();
            Check(choices.IsEnabled && Equals(choices.SelectedValue, WindowCloseBehavior.MinimizeToTray),
                "cancelling shutdown restores editing in the same view with the saved behavior");
            choices.SelectedIndex = 0; await Drain();
            Check(preferences.Behavior == WindowCloseBehavior.Ask && store.Writes == 2);
        }
        finally
        {
            store.Release.TrySetResult();
            if (closing is not null) await closing;
            window.Close();
        }
    }

    private static async Task LayoutAndPreviews()
    {
        var store = new DocumentStore();
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        var view = new GeneralSettingsView(appearance, [], preferences);
        var surface = new Border { Child = view, Background = AppearanceColors.DefaultBackground };
        var window = GeneralAppearanceTests.Window(surface);
        window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize; window.Height = 640;
        try
        {
            window.Show(); await Drain();
            var tabs = Tabs(view);
            Check(tabs[0].Focus(), "the first settings area accepts native keyboard focus");
            PressKey(tabs[0], Key.End); await Drain();
            Check(tabs[3].IsChecked == true && tabs[3].IsKeyboardFocused, "End opens window behavior and moves native focus");
            PressKey(tabs[3], Key.Left); await Drain();
            Check(tabs[2].IsChecked == true && tabs[2].IsKeyboardFocused);
            PressKey(tabs[2], Key.Right); await Drain();
            Check(tabs[3].IsChecked == true && tabs[3].IsKeyboardFocused);
            var choices = Choices(view);
            foreach (var theme in new[]
            {
                (Name: "default", Color: AppearanceColors.DefaultColor, Contrast: false),
                (Name: "custom", Color: "#A0D0C0", Contrast: false),
                (Name: "high-contrast", Color: "#A0D0C0", Contrast: true)
            })
            {
                await appearance.SaveAsync(theme.Color);
                Check(appearance.ThemeColor == theme.Color && appearance.CanEdit && appearance.Status == "",
                    "preview colors come from the saved application appearance");
                AppearanceColors.ApplyFloatingPalette(appearance.ThemeColor);
                DesktopTheme.SetIsHighContrast(surface, theme.Contrast);
                surface.Background = theme.Contrast ? SystemColors.WindowBrush : AppearanceColors.Background(appearance.ThemeColor);
                await Drain();
                Check(choices.Foreground == (theme.Contrast ? SystemColors.WindowTextBrush :
                    (Brush)view.FindResource("MeowFilm.Brush.Text")));
                Check(choices.Focus(), "the close-behavior selector accepts native focus");
                Check(choices.BorderBrush == (theme.Contrast ? SystemColors.HighlightBrush :
                    (Brush)view.FindResource("MeowFilm.Brush.Focus")), "the native focused selector uses the current theme focus brush");
                await preferences.PrepareShutdownAsync(); await Drain();
                Check(!choices.IsEnabled && choices.Foreground == (theme.Contrast ? SystemColors.GrayTextBrush :
                    (Brush)view.FindResource("MeowFilm.Brush.Disabled")), "disabled settings remain legible in the current theme");
                await preferences.CancelShutdownPreparationAsync(); await Drain();
                Check(choices.IsEnabled);
                foreach (int width in new[] { 900, 600 })
                {
                    window.Width = width; window.UpdateLayout(); await Drain();
                    foreach (FrameworkElement element in new FrameworkElement[] { tabs[3], choices })
                    {
                        var origin = element.TranslatePoint(new(), surface);
                        Check(element.ActualWidth > 0 && element.ActualHeight > 0 && origin.X >= 0 && origin.Y >= 0 &&
                            origin.X + element.ActualWidth <= surface.ActualWidth + 1 &&
                            origin.Y + element.ActualHeight <= surface.ActualHeight + 1,
                            "the tab and control fit within the supported settings canvas");
                    }
                    Capture(surface, $"general-close-{theme.Name}-{width}");
                }
                int writes = store.Writes;
                choices.IsDropDownOpen = true; await Drain();
                var popup = (Popup)choices.Template.FindName("PART_Popup", choices);
                Check(popup.IsOpen && popup.Child is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 });
                var menu = (FrameworkElement)popup.Child;
                Check(Visuals(menu).OfType<ComboBoxItem>().Count() == 3,
                    "the native dropdown renders all three real choices");
                Capture(menu, $"general-close-{theme.Name}-options");
                choices.IsDropDownOpen = false; await Drain();
                Check(store.Writes == writes, "opening and closing the dropdown does not save a selection");
            }
        }
        finally
        {
            Choices(view).IsDropDownOpen = false;
            AppearanceColors.ApplyFloatingPalette(AppearanceColors.DefaultColor);
            window.Close();
        }
    }

    private static RadioButton[] Tabs(GeneralSettingsView view) =>
        ((Panel)view.FindName("Areas")).Children.OfType<RadioButton>().ToArray();
    private static ComboBox Choices(GeneralSettingsView view) => (ComboBox)view.FindName("CloseBehaviorSelection");
    private static void OpenWindowArea(GeneralSettingsView view) =>
        Tabs(view).Single(tab => Equals(tab.Content, "窗口行为")).IsChecked = true;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void PressKey(RadioButton tab, Key key)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tab)!, 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        tab.RaiseEvent(input);
        Check(input.Handled, "settings navigation handles the native keyboard action");
    }
    private static async Task Drain()
    { for (int i = 0; i < 4; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static void Capture(FrameworkElement element, string name)
    {
        if (Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT") is not { Length: > 0 } root) return;
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root);
        Directory.CreateDirectory(root);
        element.UpdateLayout();
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi / 96),
                (int)Math.Ceiling(element.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(root, $"{name}-{dpi}.png");
            using var file = File.Create(path); encoder.Save(file);
            Console.WriteLine("PREVIEW " + path);
        }
    }
    private sealed class SettingsSource : IDesktopGeneralSettingsSource
    {
        public FrameworkElement AppearanceSettingsView { get; } = new TextBlock { Text = "外观内容" };
        public FrameworkElement HomeSourceSettingsView { get; } = new TextBlock { Text = "首页数据源内容" };
        public FrameworkElement CacheSettingsView { get; } = new TextBlock { Text = "缓存控制内容" };
    }
}

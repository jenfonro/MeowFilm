using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Key = Windows.System.VirtualKey;
using ModifierKeys = Windows.System.VirtualKeyModifiers;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class GeneralCloseSettingsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("General close settings: absent owner preserves three working general areas",
            () => TestUiHost.InvokeAsync(WithoutOwner));
        yield return new("General close settings: three choices save immediately and follow the shared owner",
            () => TestUiHost.InvokeAsync(ChoicesAndSynchronization));
        yield return new("General close settings: failed selections restore authority and publish the standard notice",
            () => TestUiHost.InvokeAsync(FailedSelection));
        yield return new("General close settings: unreadable configuration disables editing until explicit reload",
            () => TestUiHost.InvokeAsync(UnreadableConfiguration));
        yield return new("General close settings: pending saves and cancelled shutdown preserve the current selection",
            () => TestUiHost.InvokeAsync(PendingSave));
        yield return new("General close settings: native focus layout and PNG previews follow all existing themes",
            () => TestUiHost.InvokeAsync(LayoutAndPreviews));
    }

    private static async Task WithoutOwner()
    {
        var store = new AppSettingsTestDatabase();
        await using var appearance = new AppAppearance(store);
        var source = new SettingsSource();
        var view = new GeneralSettingsView(appearance, [source]);
        var window = GeneralAppearanceTests.Window(view);
        try
        {
            window.Show(); await Ready(view, appearance, null, store);
            var tabs = Tabs(view);
            Check(tabs.Select(tab => tab.Content).SequenceEqual(new[] { "外观", "首页数据源", "缓存控制" }));
            Check(!Visuals(view).OfType<ComboBox>().Any(control => AutomationProperties.GetName(control) == "关闭主窗口时"),
                "an absent close-preference owner contributes no empty controls or tab");
            Check(DesktopVisual.IsVisible(source.AppearanceSettingsView));
            tabs[1].IsChecked = true; await Drain();
            Check(DesktopVisual.IsVisible(source.HomeSourceSettingsView) && !DesktopVisual.IsVisible(source.AppearanceSettingsView));
            tabs[2].IsChecked = true; await Drain();
            Check(DesktopVisual.IsVisible(source.CacheSettingsView) && !DesktopVisual.IsVisible(source.HomeSourceSettingsView));
            PressKey(view, tabs[2], global::Windows.System.VirtualKey.Home); await Drain();
            Check(tabs[0].IsChecked == true && DesktopVisual.IsVisible(source.AppearanceSettingsView));
            PressKey(view, tabs[0], global::Windows.System.VirtualKey.End); await Drain();
            Check(tabs[2].IsChecked == true && DesktopVisual.IsVisible(source.CacheSettingsView),
                "keyboard navigation visits only available capabilities");
            Check(store.Writes == 0);
        }
        finally { window.Close(); }
    }

    private static async Task ChoicesAndSynchronization()
    {
        var store = new AppSettingsTestDatabase();
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        var source = new SettingsSource();
        var view = new GeneralSettingsView(appearance, [source], preferences);
        var window = GeneralAppearanceTests.Window(view);
        try
        {
            window.Show(); await Ready(view, appearance, preferences, store);
            var tabs = Tabs(view);
            Check(tabs.Select(tab => tab.Content).SequenceEqual(new[] { "外观", "首页数据源", "缓存控制", "窗口行为" }));
            OpenWindowArea(view); await Drain();
            var choices = Choices(view);
            var options = choices.Items.Cast<ComboBoxItem>().ToArray();
            Check(options.Select(option => option.Content).SequenceEqual(new[] { "每次询问", "最小化到托盘", "退出应用" }));
            Check(options.Select(option => Enum.Parse<WindowCloseBehavior>((string)option.Tag)).SequenceEqual(new[]
                { WindowCloseBehavior.Ask, WindowCloseBehavior.MinimizeToTray, WindowCloseBehavior.Exit }));
            Check(options.All(option => AutomationProperties.GetName(option) == (string)option.Content));
            Check(new ComboBoxAutomationPeer(choices).GetName() == "关闭主窗口时" && choices.IsTabStop && choices.UseSystemFocusVisuals);
            Check(preferences.CanEdit && choices.IsEnabled && Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, WindowCloseBehavior.Ask.ToString()) &&
                store.Writes == 0, "opening the settings reads the default without saving a draft; " + State(view, appearance, preferences, store));
            Check(!DesktopVisual.IsVisible(source.AppearanceSettingsView) && !DesktopVisual.IsVisible(source.HomeSourceSettingsView) &&
                !DesktopVisual.IsVisible(source.CacheSettingsView), "the new area does not mix capability-owned settings");
            foreach (int index in new[] { 1, 2, 0 })
            {
                int writes = store.Writes;
                choices.SelectedIndex = index;
                await Until(() => preferences.CanEdit && store.Writes == writes + 1 &&
                    preferences.Behavior == Enum.Parse<WindowCloseBehavior>((string)options[index].Tag) &&
                    SelectedBehavior(choices) == preferences.Behavior.ToString(), () => State(view, appearance, preferences, store));
                Check(preferences.Behavior == Enum.Parse<WindowCloseBehavior>((string)options[index].Tag) &&
                    Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, preferences.Behavior.ToString()) && preferences.Status == "" &&
                    store.Writes == writes + 1, "each changed choice is immediately saved once, including reset to Ask");
            }
            int before = store.Writes;
            Check(await preferences.SaveAsync(WindowCloseBehavior.MinimizeToTray)); await Drain();
            Check(Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, WindowCloseBehavior.MinimizeToTray.ToString()) && store.Writes == before + 1,
                "a close-dialog consumer updating the same owner synchronizes the setting without an echo write");
            await using var reopened = new AppClosePreferences(store);
            await reopened.LoadAsync();
            Check(reopened.Behavior == WindowCloseBehavior.MinimizeToTray);
            tabs[0].IsChecked = true; await Drain();
            Check(DesktopVisual.IsVisible(source.AppearanceSettingsView));
            OpenWindowArea(view); await Drain();
            Check(Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, WindowCloseBehavior.MinimizeToTray.ToString()) && store.Writes == before + 1,
                "switching tabs preserves the authoritative choice without saving again");
        }
        finally { window.Close(); }
    }

    private static async Task FailedSelection()
    {
        var store = new AppSettingsTestDatabase { };
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        var view = new GeneralSettingsView(appearance, [], preferences);
        var window = GeneralAppearanceTests.Window(view);
        var outlet = (NoticeOutlet)view.FindName("ClosePreferencesNotice");
        var notices = new List<string>();
        NoticeOutlet.SetReceiver(DesktopTestUi.WindowRoot(window), e =>
        {
            if (ReferenceEquals(e.Outlet, outlet)) notices.Add(e.Message);
        });
        try
        {
            window.Show(); await Ready(view, appearance, preferences, store); OpenWindowArea(view); await Drain();
            var choices = Choices(view);
            var reload = (Button)view.FindName("ReloadClosePreferencesButton");
            store.Fault = true; store.FaultAfter = false;
            choices.SelectedIndex = 2;
            await Until(() => preferences.CanEdit && preferences.Status.Length > 0 && SelectedBehavior(choices) == preferences.Behavior.ToString() && DesktopVisual.IsVisible(reload),
                () => State(view, appearance, preferences, store));
            Check(preferences.Behavior == WindowCloseBehavior.Ask && Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, WindowCloseBehavior.Ask.ToString()) &&
                choices.IsEnabled && preferences.Status.Length > 0 && DesktopVisual.IsVisible(reload) &&
                store.WindowData is null,
                "a rejected selection neither changes the saved behavior nor remains selected; " + State(view, appearance, preferences, store));
            Check(outlet.Message == preferences.Status && notices.Contains(preferences.Status),
                "save failure reaches the window through NoticeOutlet");
            Check(!Visuals(view).OfType<TextBlock>().Any(text => text.Text == preferences.Status),
                "the error is not inserted into the settings layout");
            store.Fault = true; store.FaultAfter = true;
            choices.SelectedIndex = 1;
            await Until(() => preferences.CanEdit && preferences.Behavior == WindowCloseBehavior.MinimizeToTray && SelectedBehavior(choices) == preferences.Behavior.ToString() && !DesktopVisual.IsVisible(reload),
                () => State(view, appearance, preferences, store));
            Check(preferences.Behavior == WindowCloseBehavior.MinimizeToTray &&
                Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, WindowCloseBehavior.MinimizeToTray.ToString()) &&
                preferences.Status == "" && !DesktopVisual.IsVisible(reload) && outlet.Message == "",
                "a lost receipt is shown as saved only after the owner confirms storage");

            await using var other = new AppClosePreferences(store);
            await other.LoadAsync(); Check(await other.SaveAsync(WindowCloseBehavior.Exit));
            choices.SelectedIndex = 0;
            await Until(() => preferences.CanEdit && preferences.Behavior == WindowCloseBehavior.Exit && preferences.Status.Length > 0 && SelectedBehavior(choices) == preferences.Behavior.ToString(),
                () => State(view, appearance, preferences, store));
            Check(preferences.Behavior == WindowCloseBehavior.Exit && Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, WindowCloseBehavior.Exit.ToString()) &&
                preferences.Status.Length > 0 && DesktopVisual.IsVisible(reload), "a conflict restores the newer saved behavior");
            Check(await other.SaveAsync(WindowCloseBehavior.Ask));
            int writes = store.Writes;
            Click(reload);
            await Until(() => preferences.CanEdit && choices.IsEnabled && preferences.Status == "" && SelectedBehavior(choices) == preferences.Behavior.ToString() && !DesktopVisual.IsVisible(reload),
                () => State(view, appearance, preferences, store));
            Check(preferences.Behavior == WindowCloseBehavior.Ask && Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, WindowCloseBehavior.Ask.ToString()) &&
                preferences.Status == "" && !DesktopVisual.IsVisible(reload) && store.Writes == writes,
                "reload reads the latest authority without replaying a rejected selection");
        }
        finally { window.Close(); }
    }

    private static async Task UnreadableConfiguration()
    {
        var store = new AppSettingsTestDatabase();
        store.WindowData = "{\"schema\":99,\"closeBehavior\":\"ask\"}"u8.ToArray();
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        var view = new GeneralSettingsView(appearance, [], preferences);
        var window = GeneralAppearanceTests.Window(view);
        try
        {
            window.Show(); await Ready(view, appearance, preferences, store); OpenWindowArea(view); await Drain();
            var choices = Choices(view);
            var reload = (Button)view.FindName("ReloadClosePreferencesButton");
            Check(!preferences.CanEdit && !choices.IsEnabled && preferences.Status.Length > 0 &&
                DesktopVisual.IsVisible(reload) && reload.IsEnabled && store.Writes == 0,
                "an unreadable document cannot appear as an editable default");
            await Drain();
            Check(!choices.IsEnabled && store.Writes == 0);
            store.WindowData = null;
            Click(reload);
            await Until(() => preferences.CanEdit && choices.IsEnabled && preferences.Status == "" && SelectedBehavior(choices) == preferences.Behavior.ToString() && !DesktopVisual.IsVisible(reload),
                () => State(view, appearance, preferences, store));
            Check(preferences.CanEdit && choices.IsEnabled && preferences.Status == "" && !DesktopVisual.IsVisible(reload) &&
                Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, WindowCloseBehavior.Ask.ToString()) && store.Writes == 0,
                "an explicit successful read enables the confirmed default without writing");
            choices.SelectedIndex = 2;
            await Until(() => preferences.CanEdit && preferences.Behavior == WindowCloseBehavior.Exit,
                () => State(view, appearance, preferences, store));
            Check(preferences.Behavior == WindowCloseBehavior.Exit && store.Writes == 1);
        }
        finally { window.Close(); }
    }

    private static async Task PendingSave()
    {
        var store = new AppSettingsTestDatabase { HoldWrites = true};
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        var view = new GeneralSettingsView(appearance, [], preferences);
        var window = GeneralAppearanceTests.Window(view);
        Task? closing = null;
        try
        {
            window.Show(); await Ready(view, appearance, preferences, store); OpenWindowArea(view); await Drain();
            var choices = Choices(view);
            choices.SelectedIndex = 1;
            await Until(() => store.Entered.Task.IsCompleted && !preferences.CanEdit && !choices.IsEnabled,
                () => State(view, appearance, preferences, store));
            Check(!choices.IsEnabled && !preferences.CanEdit && preferences.Behavior == WindowCloseBehavior.Ask,
                "the current owner remains unchanged until the accepted write settles");
            closing = preferences.PrepareShutdownAsync();
            Check(!closing.IsCompleted, "shutdown cannot release storage before the settings save");
            store.Release.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(3)); await Drain();
            Check(!choices.IsEnabled && Equals(((ComboBoxItem?)choices.SelectedItem)?.Tag, WindowCloseBehavior.MinimizeToTray.ToString()) && store.Writes == 1);
            Check(!choices.IsEnabled, "retired settings remain sealed");
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
        var store = new AppSettingsTestDatabase();
        await using var appearance = new AppAppearance(store);
        await using var preferences = new AppClosePreferences(store);
        await using var mediaControls = new AppMediaControlSettings(store);
        var view = new GeneralSettingsView(appearance, [], preferences, mediaControls);
        var surface = new Border { Child = view, Background = AppearanceColors.DefaultBackground };
        var window = GeneralAppearanceTests.Window(surface);
        window.Resize(900, 640);
        try
        {
            window.Show(); await Ready(view, appearance, preferences, store);
            var tabs = Tabs(view);
            Check(tabs.Length == 5, "the layout includes the actual shortcut settings area");
            Check(tabs[0].Focus(FocusState.Programmatic), "the first settings area accepts native keyboard focus");
            PressKey(view, tabs[0], global::Windows.System.VirtualKey.End); await Drain();
            Check(tabs[4].IsChecked == true && ReferenceEquals(DesktopVisual.Focused(view), tabs[4]), "End opens shortcuts and moves native focus");
            PressKey(view, tabs[4], global::Windows.System.VirtualKey.Left); await Drain();
            Check(tabs[3].IsChecked == true && ReferenceEquals(DesktopVisual.Focused(view), tabs[3]), "Left opens window behavior and moves native focus");
            PressKey(view, tabs[3], global::Windows.System.VirtualKey.Left); await Drain();
            Check(tabs[2].IsChecked == true && ReferenceEquals(DesktopVisual.Focused(view), tabs[2]));
            PressKey(view, tabs[2], global::Windows.System.VirtualKey.Right); await Drain();
            Check(tabs[3].IsChecked == true && ReferenceEquals(DesktopVisual.Focused(view), tabs[3]));
            var choices = Choices(view);
            foreach (var theme in new[]
            {
                (Name: "default", Color: AppearanceColors.DefaultColor),
                (Name: "custom", Color: "#A0D0C0")
            })
            {
                await appearance.SaveAsync(theme.Color);
                Check(appearance.ThemeColor == theme.Color && appearance.CanEdit && appearance.Status == "",
                    "preview colors come from the saved application appearance; " + State(view, appearance, preferences, store));
                AppearanceColors.ApplyFloatingPalette(appearance.ThemeColor);
                surface.Background = AppearanceColors.Background(appearance.ThemeColor);
                await Drain();
                Check(choices.Foreground is SolidColorBrush { Color.A: > 0 }, "selector has visible text");
                Check(choices.Focus(FocusState.Programmatic), "selector accepts native focus");
                Check(choices.UseSystemFocusVisuals, "selector preserves the native focus visual");
                choices.IsEnabled = false; await Drain();
                Check(!choices.IsEnabled && choices.Foreground is SolidColorBrush { Color.A: > 0 },
                    "disabled settings remain legible");
                choices.IsEnabled = true; await Drain();
                Check(choices.IsEnabled);
                foreach (int width in new[] { 900, 600 })
                {
                    window.Resize(width, 640); surface.UpdateLayout(); await Drain();
                    foreach (FrameworkElement element in tabs.Cast<FrameworkElement>().Append(choices))
                    {
                        var origin = element.TransformToVisual(surface).TransformPoint(new Point());
                        Check(element.ActualWidth > 0 && element.ActualHeight > 0 && origin.X >= 0 && origin.Y >= 0 &&
                            origin.X + element.ActualWidth <= surface.ActualWidth + 1 &&
                            origin.Y + element.ActualHeight <= surface.ActualHeight + 1,
                            $"the tab and control fit within the supported settings canvas: {element.GetType().Name} origin={origin} " +
                            $"actual={element.ActualWidth}x{element.ActualHeight} desired={element.DesiredSize} " +
                            $"surface={surface.ActualWidth}x{surface.ActualHeight} window={window.AppWindow.Size}");
                    }
                    foreach (var tab in tabs)
                    {
                        Check(tab.FontWeight.Weight == 400 && tab.UseSystemFocusVisuals,
                            "settings areas retain regular text and native focus visuals");
                    }
                    var underline = (Border)DesktopTestUi.Part(tabs[3], "Underline");
                    Check(underline.Visibility == Visibility.Visible &&
                        ((SolidColorBrush)underline.Background).Color == FilmUiTest.ResourceColor(tabs[3], "MeowFilm.Brush.Accent"),
                        "the selected settings area uses the theme accent underline");
                    await GeneralAppearanceTests.SavePreview(surface, $"general-close-{theme.Name}-{width}", width, 640);
                }
                int writes = store.Writes;
                choices.IsDropDownOpen = true; await Drain();
                var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).Single(item =>
                    item.Child is DependencyObject child && Visuals(child).OfType<ComboBoxItem>().Any());
                Check(popup.IsOpen && popup.Child is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 });
                var menu = (FrameworkElement)popup.Child;
                Check(Visuals(menu).OfType<ComboBoxItem>().Count() == 3,
                    "the native dropdown renders all three real choices");
                await GeneralAppearanceTests.SavePreview(menu, $"general-close-{theme.Name}-options", (int)menu.ActualWidth, (int)menu.ActualHeight);
                choices.IsDropDownOpen = false; await Drain();
                Check(store.Writes == writes, "opening and closing the dropdown does not save a selection");
            }
            using var highContrast = new FilmUiTest.HighContrastResources(view);
            await Drain();
            var selectedUnderline = (Border)DesktopTestUi.Part(tabs[3], "Underline");
            var selectedLabel = (ContentPresenter)DesktopTestUi.Part(tabs[3], "Label");
            var systemColors = new global::Windows.UI.ViewManagement.UISettings();
            Check(selectedUnderline.Visibility == Visibility.Visible && ((SolidColorBrush)selectedUnderline.Background).Color ==
                systemColors.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Highlight),
                "the selected settings underline uses the system high-contrast highlight");
            Check(((SolidColorBrush)selectedLabel.Foreground).Color ==
                systemColors.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText),
                "settings labels use the system high-contrast text color");
            await GeneralAppearanceTests.SavePreview(surface, "general-close-high-contrast-600", 600, 640);
        }
        finally
        {
            Choices(view).IsDropDownOpen = false;
            AppearanceColors.ApplyFloatingPalette(AppearanceColors.DefaultColor);
            window.Close();
        }
    }

    private static string? SelectedBehavior(ComboBox choices) => ((ComboBoxItem?)choices.SelectedItem)?.Tag as string;
    private static string State(GeneralSettingsView view, AppAppearance appearance, AppClosePreferences? preferences, AppSettingsTestDatabase store) =>
        $"loaded={view.IsLoaded}, appearanceEditable={appearance.CanEdit}, color={appearance.ThemeColor}, appearanceStatus={appearance.Status}, " +
        $"closeEditable={preferences?.CanEdit}, behavior={preferences?.Behavior}, closeStatus={preferences?.Status}, " +
        $"selected={SelectedBehavior(Choices(view))}, selectorEnabled={Choices(view).IsEnabled}, writes={store.Writes}, reads={store.Reads}, entered={store.Entered.Task.IsCompleted}";
    private static async Task Ready(GeneralSettingsView view, AppAppearance appearance, AppClosePreferences? preferences, AppSettingsTestDatabase store)
    {
        await TestUiHost.LoadedAsync(view);
        await Until(() => appearance.CanEdit && (preferences is null ||
            (preferences.CanEdit || preferences.Status.Length > 0) &&
            SelectedBehavior(Choices(view)) == preferences.Behavior.ToString() && Choices(view).IsEnabled == preferences.CanEdit),
            () => State(view, appearance, preferences, store));
    }
    private static async Task Until(Func<bool> condition, Func<string> state)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("General settings state did not settle: " + state());
            await Task.Delay(10);
        }
    }

    private static RadioButton[] Tabs(GeneralSettingsView view) =>
        ((Panel)view.FindName("Areas")).Children.OfType<RadioButton>().ToArray();
    private static ComboBox Choices(GeneralSettingsView view) => (ComboBox)view.FindName("CloseBehaviorSelection");
    private static void OpenWindowArea(GeneralSettingsView view) =>
        Tabs(view).Single(tab => Equals(tab.Content, "窗口行为")).IsChecked = true;
    private static void Click(Button button) => DesktopTestUi.Invoke(button);
    private static void PressKey(GeneralSettingsView view, RadioButton tab, global::Windows.System.VirtualKey key)
    {
        Check(view.NavigateArea(tab, key),
            "settings navigation handles the production semantic key action");
    }
    private static async Task Drain()
    { for (int i = 0; i < 4; i++) await DesktopTestUi.DrainAsync(); }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private sealed class SettingsSource : IDesktopGeneralSettingsSource
    {
        public FrameworkElement AppearanceSettingsView { get; } = new TextBlock { Text = "外观内容" };
        public FrameworkElement HomeSourceSettingsView { get; } = new TextBlock { Text = "首页数据源内容" };
        public FrameworkElement CacheSettingsView { get; } = new TextBlock { Text = "缓存控制内容" };
    }
}

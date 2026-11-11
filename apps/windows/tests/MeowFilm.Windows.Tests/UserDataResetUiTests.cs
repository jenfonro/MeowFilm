using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class UserDataResetUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("entry is optional and follows cache contributions without a new tab", Entry);
        yield return Case("confirmation defaults to cancel and supports Escape and keyboard cancellation", Confirmation);
        yield return Case("confirmed work is single flight and fences late requests and navigation", SingleFlight);
        yield return Case("failure keeps the old shell locked and explicit retry closes without normal shutdown", Retry);
        yield return Case("failed exit stays locked and requires an explicit successful abort", Exit);
        yield return Case("tray restore and close cannot supersede confirmation or pending cleanup", TrayAndClose);
        yield return Case("a pending close choice cannot overlap a reset", PendingCloseChoice);
        yield return Case("OS session ending retains its existing path during cleanup", SessionEnding);
        yield return Case("confirmation and failed actions fit the window and inherit theme and contrast", Layout);
    }

    private static TestCase Case(string name, Func<Task> body) =>
        new("user data reset UI: " + name, () => MusicQualityConsumerTests.Sta(body));

    private static async Task Entry()
    {
        await using (var absent = await Fixture.Create(withReset: false))
        {
            await absent.OpenCache();
            Check(!absent.Entry.IsVisible && !absent.SettingsElement<Border>("UserDataSection").IsVisible,
                "without an App callback neither the section nor its action is shown");
            Click(absent.Entry);
            Check(!absent.Dialog.IsOpen && absent.ResetCalls == 0, "no callback means no reset admission");
        }
        await using var f = await Fixture.Create();
        await f.OpenCache();
        Check(f.Entry.IsVisible && f.Entry.IsEnabled && Equals(f.Entry.Content, "清除全部数据"), "cache action");
        var areas = f.SettingsElement<StackPanel>("Areas").Children.OfType<RadioButton>().ToArray();
        Check(areas.Select(item => item.Content).SequenceEqual(new[] { "外观", "首页数据源", "缓存控制", "窗口行为" }),
            "local data adds a section, not a settings category");
        var page = f.SettingsElement<StackPanel>("CachePage");
        var contributions = f.SettingsElement<StackPanel>("CacheContributions");
        var section = f.SettingsElement<Border>("UserDataSection");
        Check(page.Children.Count == 2 && ReferenceEquals(page.Children[0], contributions) &&
            ReferenceEquals(page.Children[1], section) && contributions.Children.Contains(f.Module.CacheSettingsView),
            "all capability cache settings precede the independent local-data section");
        Check(Descendants(section).OfType<TextBlock>().Any(text => text.Text == "本机数据"), "section title");
        Check(f.ResetCalls == 0 && f.ShutdownCalls == 0 && f.ForceCalls == 0, "opening settings changes no lifecycle");
    }

    private static async Task Confirmation()
    {
        await using var f = await Fixture.Create();
        await f.OpenConfirmation();
        Check(f.DialogElement<TextBlock>("DialogTitle").Text == "清除全部数据？", "confirmation title");
        var text = f.DialogElement<TextBlock>("ConfirmationText").Text;
        foreach (var required in new[] { "登录凭据", "设备身份", "歌单", "收藏", "历史", "导入音源", "偏好", "缓存",
                     "无法撤销", "云端数据和 App 外的原始文件不受影响" })
            Check(text.Contains(required, StringComparison.Ordinal), "confirmation discloses " + required);
        Check(!text.Contains("重启", StringComparison.Ordinal), "Windows does not request a process restart");
        Check(ReferenceEquals(FocusManager.GetFocusedElement(f.Shell), f.Cancel) ||
            ReferenceEquals(Keyboard.FocusedElement, f.Cancel), "initial focus is Cancel");
        Check(!f.Confirm.IsDefault && !f.Confirm.IsDefaulted && f.ResetCalls == 0 && f.ShutdownCalls == 0,
            "opening the dialog is not consent and Enter cannot default to deletion");
        Check(KeyboardNavigation.GetTabNavigation(f.Dialog) == KeyboardNavigationMode.Cycle &&
            KeyboardNavigation.GetControlTabNavigation(f.Dialog) == KeyboardNavigationMode.Cycle,
            "keyboard navigation stays inside the modal surface");
        Click(f.Cancel);
        await Drain();
        Check(!f.Dialog.IsOpen && f.Root.IsEnabled && f.Entry.IsEnabled && f.ResetCalls == 0, "button cancellation");
        await f.OpenConfirmation();
        Check(RaiseKey(f.Cancel, Key.Escape).Handled, "Escape is consumed by the confirmation");
        await Drain();
        Check(!f.Dialog.IsOpen && f.Root.IsEnabled && f.ResetCalls == 0, "Escape does not call the App");
        await f.OpenConfirmation();
        RaiseKey(f.Cancel, Key.Enter);
        await Drain();
        Check(!f.Dialog.IsOpen && f.Root.IsEnabled && f.ResetCalls == 0, "Enter on the default focused Cancel cancels");
        await f.OpenConfirmation();
        Click(f.DialogElement<Button>("DismissButton"));
        Check(!f.Dialog.IsOpen && f.ResetCalls == 0 && f.ShutdownCalls == 0 && f.ForceCalls == 0,
            "dismiss is also cancellation, without stopping owners");
    }

    private static async Task SingleFlight()
    {
        await using var f = await Fixture.Create();
        var release = Pending();
        f.Reset = async () => { await release.Task; await f.Shell.AbortAsync(); };
        await f.OpenConfirmation();
        f.Module.HoldNavigation = true;
        f.Module.Request("held");
        await Until(() => f.Module.HeldToken.CanBeCanceled, "held module request");
        Check(!f.Module.HeldToken.IsCancellationRequested && f.ResetCalls == 0,
            "opening confirmation alone does not stop accepted business work");
        Click(f.Confirm);
        Click(f.Confirm);
        await Until(() => f.ResetCalls == 1, "confirmed reset");
        Check(f.Dialog.IsPending && !f.Root.IsEnabled && !f.Confirm.IsEnabled && !f.ExitButton.IsEnabled &&
            !f.Cancel.IsVisible && !f.Closed, "busy state has no cancel, repeat or exit action");
        await Until(() => f.Module.HeldToken.IsCancellationRequested, "old request retired");
        var navigations = f.Module.Navigations;
        f.Module.Request("late");
        f.Module.ReleaseNavigation.TrySetResult();
        Click(f.ShellElement<Button>("BackButton"));
        Click(f.ShellElement<Button>("SettingsButton"));
        f.Shell.Close();
        RaiseKey(f.Dialog, Key.Escape);
        Click(f.ExitButton);
        await Drain();
        Check(f.Module.Navigations == navigations && f.ResetCalls == 1 && f.ForceCalls == 0 &&
            f.ShutdownCalls == 0 && f.Dialog.IsPending && !f.Closed && !f.CloseDialog.IsOpen,
            "late completion, navigation, Close and Escape cannot supersede accepted reset work");
        release.TrySetResult();
        await Until(() => f.Closed, "App callback closes old shell");
        Check(f.Shell.CleanupCompleted && f.ForceCalls == 1 && f.ShutdownCalls == 0,
            "closing the old generation cannot invoke normal shutdown");
    }

    private static async Task Retry()
    {
        await using var f = await Fixture.Create();
        f.Reset = () => Task.FromException(new InvalidOperationException("C:\\private\\credentials secret"));
        await f.OpenConfirmation();
        Click(f.Confirm);
        await Until(() => !f.Dialog.IsPending && f.ResetCalls == 1, "failed reset");
        AssertFailed(f);
        Check(f.HasNotice("清除未完成，请重试") &&
            !Descendants(f.Dialog).OfType<TextBlock>().Any(text => text.Text.Contains("secret", StringComparison.Ordinal)),
            "only fixed safe error text is displayed");
        var navigations = f.Module.Navigations;
        RaiseKey(f.Dialog, Key.Escape);
        Click(f.Cancel);
        f.Module.Request("late");
        f.Shell.Close();
        await Drain();
        Check(f.ResetCalls == 1 && f.ShutdownCalls == 0 && f.ForceCalls == 0 && f.Module.Navigations == navigations &&
            !f.Closed && !f.CloseDialog.IsOpen, "failed reset neither auto-retries nor returns to business");
        var release = Pending();
        f.Reset = async () => { await release.Task; await f.Shell.AbortAsync(); };
        Click(f.Confirm); Click(f.Confirm);
        Check(f.ResetCalls == 2 && f.Dialog.IsPending, "one explicit retry");
        release.TrySetResult();
        await Until(() => f.Closed, "retry closes old generation");
        Check(f.ShutdownCalls == 0 && f.ForceCalls == 1 && f.Tray.Disposals == 1,
            "successful retry uses only the host abort close");
    }

    private static async Task Exit()
    {
        await using var f = await Fixture.Create();
        f.Reset = () => Task.FromException(new InvalidOperationException());
        f.Force = () => Task.FromException(new InvalidOperationException("private path"));
        await f.OpenConfirmation();
        Click(f.Confirm);
        await Until(() => !f.Dialog.IsPending && f.ResetCalls == 1, "reset failed");
        Click(f.ExitButton);
        await Until(() => !f.Dialog.IsPending && f.ForceCalls == 1, "exit failed");
        AssertFailed(f);
        Check(f.HasNotice("退出未完成，请重试") && f.ShutdownCalls == 0 && !f.Shell.CleanupCompleted,
            "failed AbortAsync does not close or claim cleanup");
        await Drain();
        Check(f.ForceCalls == 1 && f.ResetCalls == 1, "no automatic retry or exit");
        var release = Pending();
        f.Force = () => release.Task;
        Click(f.ExitButton); Click(f.ExitButton); Click(f.Confirm);
        RaiseKey(f.Dialog, Key.Escape); f.Shell.Close(); f.Tray.ExitApplication();
        await Drain();
        Check(f.Dialog.IsPending && f.ForceCalls == 2 && f.ResetCalls == 1 && !f.Closed && f.ShutdownCalls == 0,
            "exit waits for actual abort and excludes every competing action");
        release.TrySetResult();
        await Until(() => f.Closed, "explicit abort completed");
        Check(f.Shell.CleanupCompleted && !f.Shell.ShutdownSucceeded && f.ShutdownCalls == 0,
            "explicit exit does not claim an ordinary saved shutdown");
    }

    private static async Task TrayAndClose()
    {
        await using var f = await Fixture.Create();
        await f.OpenConfirmation();
        f.Tray.Restore(); f.Tray.ExitApplication(); f.Shell.Close();
        await Drain();
        Check(f.Dialog.IsOpen && !f.CloseDialog.IsOpen && !f.Root.IsEnabled && f.ShutdownCalls == 0,
            "tray exit cannot replace an open confirmation with a close choice");
        Click(f.Cancel);
        Check(f.Root.IsEnabled, "cancel still restores the unchanged business view");
        var release = Pending();
        f.Reset = async () => { await release.Task; throw new InvalidOperationException(); };
        await f.OpenConfirmation();
        Click(f.Confirm);
        f.Shell.WindowState = WindowState.Minimized;
        f.Tray.Restore(); f.Tray.ExitApplication(); f.Tray.Unavailable(); f.Shell.Close();
        await Drain();
        Check(f.Shell.IsVisible && f.Shell.WindowState != WindowState.Minimized && f.Dialog.IsPending &&
            !f.Root.IsEnabled && !f.CloseDialog.IsOpen && f.ResetCalls == 1 && f.ShutdownCalls == 0,
            "tray restore may reveal the locked dialog but never reopens owners or normal shutdown");
        release.TrySetResult();
        await Until(() => !f.Dialog.IsPending, "reset failure after tray restore");
        AssertFailed(f);
    }

    private static async Task PendingCloseChoice()
    {
        await using var f = await Fixture.Create();
        await f.OpenCache();
        f.Shell.Close();
        Click(f.Entry);
        await Until(() => f.CloseDialog.IsOpen, "queued close wins admission");
        Check(!f.Dialog.IsOpen && f.ResetCalls == 0, "reset does not overlap queued close selection");
        var save = Pending();
        f.Preferences.Save = () => save.Task;
        ((CheckBox)f.CloseDialog.FindName("RememberChoice")).IsChecked = true;
        Click((Button)f.CloseDialog.FindName("MinimizeChoice"));
        Click(f.Entry);
        Check(f.CloseDialog.IsPending && !f.Dialog.IsOpen && f.ResetCalls == 0, "pending close write cannot race reset");
        save.TrySetResult();
        await Until(() => f.Shell.IsHiddenToTray, "remembered close finishes");
        f.Tray.Restore();
        await f.OpenConfirmation();
        Check(f.Dialog.IsOpen && !f.CloseDialog.IsOpen && f.ResetCalls == 0, "reset can be opened after the close action settles");
        Click(f.Cancel);
    }

    private static async Task SessionEnding()
    {
        await using var f = await Fixture.Create();
        var reset = Pending();
        var shutdown = Pending();
        f.Reset = () => reset.Task;
        f.Shutdown = () => shutdown.Task;
        await f.OpenConfirmation();
        Click(f.Confirm);
        f.Shell.PrepareForSessionEnding();
        Check(f.ShutdownCalls == 1 && !f.Dialog.IsOpen && !f.Shell.CleanupCompleted && !f.Closed,
            "OS session ending bypasses the reset prompt and starts existing best-effort cleanup");
        f.Shell.PrepareForSessionEnding();
        Check(f.ShutdownCalls == 1, "session ending still shares one shutdown");
        f.Shell.Close();
        Check(f.Closed && !f.Shell.CleanupCompleted, "the OS close is not vetoed by pending reset or cleanup");
        reset.TrySetException(new InvalidOperationException());
        shutdown.TrySetResult();
        await f.Shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(3));
        await Drain();
        Check(!f.Dialog.IsOpen && f.ResetCalls == 1, "a late reset failure cannot revive a session-ending window");
    }

    private static async Task Layout()
    {
        await using var f = await Fixture.Create();
        await f.OpenConfirmation();
        foreach (var size in new[] { new Size(860, 600), new Size(1110, 869) })
        {
            f.Shell.Width = size.Width; f.Shell.Height = size.Height;
            await Drain();
            AssertLayout(f);
        }
        var surface = f.DialogElement<Border>("DialogSurface");
        var theme = new SolidColorBrush(Color.FromRgb(220, 230, 240));
        f.Shell.Resources["MeowFilm.Brush.FloatingSurface"] = theme;
        await Drain();
        Check(ReferenceEquals(surface.Background, theme), "the dialog uses the shell floating-surface theme token");
        DesktopTheme.SetIsHighContrast(f.Shell, true);
        await Drain();
        Check(surface.Background == SystemColors.WindowBrush && surface.BorderBrush == SystemColors.WindowTextBrush &&
            surface.Effect is null && f.Dialog.Foreground == SystemColors.WindowTextBrush,
            "high contrast follows the existing dialog theme");
        f.Reset = () => Task.FromException(new InvalidOperationException());
        Click(f.Confirm);
        await Until(() => !f.Dialog.IsPending, "failure layout");
        f.Shell.Width = 860; f.Shell.Height = 600;
        await Drain();
        AssertLayout(f);
        Check(f.Confirm.IsVisible && f.ExitButton.IsVisible && !f.Cancel.IsVisible, "only recovery actions remain");
    }

    private static void AssertFailed(Fixture f)
    {
        Check(f.Dialog.IsOpen && !f.Dialog.IsPending && !f.Root.IsEnabled &&
            !f.ShellElement<Grid>("ContentDeck").IsEnabled && !f.Cancel.IsVisible &&
            Equals(f.Confirm.Content, "重试清除") && f.Confirm.IsEnabled && f.ExitButton.IsEnabled &&
            f.DialogElement<TextBlock>("DialogTitle").Text == "清除未完成" && !f.Closed,
            "failure must retain the business fence and explicit retry/exit actions");
    }

    private static void AssertLayout(Fixture f)
    {
        var root = f.ShellElement<Grid>("PresentationSurface");
        var surface = f.DialogElement<Border>("DialogSurface");
        Check(Math.Abs(root.ActualWidth - f.Dialog.ActualWidth) < 2 &&
            Math.Abs(root.ActualHeight - f.Dialog.ActualHeight) < 2, "the modal covers the whole shell");
        var position = surface.TranslatePoint(new Point(), f.Dialog);
        Check(Math.Abs(position.X * 2 + surface.ActualWidth - f.Dialog.ActualWidth) < 2 &&
            Math.Abs(position.Y * 2 + surface.ActualHeight - f.Dialog.ActualHeight) < 2, "centered dialog");
        foreach (var button in Descendants(surface).OfType<Button>().Where(button => button.IsVisible))
        {
            var point = button.TranslatePoint(new Point(), surface);
            Check(AutomationProperties.GetName(button).Length > 0 && button.ActualWidth > 20 &&
                button.ActualHeight >= 30 && point.X >= 0 && point.Y >= 0 &&
                point.X + button.ActualWidth <= surface.ActualWidth + 1 &&
                point.Y + button.ActualHeight <= surface.ActualHeight + 1, "unclipped accessible action");
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static KeyEventArgs RaiseKey(UIElement target, Key key)
    {
        var args = new KeyEventArgs(new TestKeyboard(), PresentationSource.FromVisual(target)!, 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        if (!args.Handled) { args.RoutedEvent = Keyboard.KeyDownEvent; target.RaiseEvent(args); }
        return args;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static TaskCompletionSource Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Drain()
    { for (int i = 0; i < 4; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task Until(Func<bool> condition, string label)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Timed out: " + label);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!condition()) await Task.Delay(1);
        }
        await Drain();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class TestKeyboard() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ProbeModule Module { get; } = new();
        internal Preferences Preferences { get; } = new();
        internal Tray Tray { get; } = new();
        internal ShellWindow Shell { get; }
        internal Func<Task> Reset = () => Task.FromException(new InvalidOperationException());
        internal Func<Task> Force = () => Task.CompletedTask;
        internal Func<Task> Shutdown = () => Task.CompletedTask;
        internal int ResetCalls, ForceCalls, ShutdownCalls;
        internal bool Closed { get; private set; }
        internal GeneralSettingsView Settings => Descendants(ShellElement<Grid>("ContentDeck")).OfType<GeneralSettingsView>().Single();
        internal UserDataResetDialog Dialog => ShellElement<UserDataResetDialog>("ResetDialog");
        internal CloseWindowDialog CloseDialog => ShellElement<CloseWindowDialog>("CloseDialog");
        internal Grid Root => ShellElement<Grid>("RootLayout");
        internal Button Entry => SettingsElement<Button>("ClearUserDataButton");
        internal Button Confirm => DialogElement<Button>("ConfirmChoice");
        internal Button Cancel => DialogElement<Button>("CancelChoice");
        internal Button ExitButton => DialogElement<Button>("ExitChoice");

        private Fixture(bool withReset)
        {
            Shell = new ShellWindow([Module], [], () => { ShutdownCalls++; return Shutdown(); },
                () => { ForceCalls++; return Force(); }, null, TimeProvider.System,
                appearance: new Appearance(), closePreferences: Preferences,
                trayIconFactory: (_, restore, exit, unavailable) =>
                {
                    Tray.Restore = restore; Tray.ExitApplication = exit; Tray.Unavailable = unavailable;
                    return Tray;
                }, resetUserData: withReset ? () => { ResetCalls++; return Reset(); } : null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            Shell.Closed += (_, _) => Closed = true;
        }

        internal static async Task<Fixture> Create(bool withReset = true)
        {
            var result = new Fixture(withReset);
            result.Shell.Show();
            await Drain();
            return result;
        }
        internal T ShellElement<T>(string name) where T : FrameworkElement =>
            Shell.FindName(name) as T ?? throw new InvalidOperationException("Missing shell element: " + name);
        internal T SettingsElement<T>(string name) where T : FrameworkElement =>
            Settings.FindName(name) as T ?? throw new InvalidOperationException("Missing settings element: " + name);
        internal T DialogElement<T>(string name) where T : FrameworkElement =>
            Dialog.FindName(name) as T ?? throw new InvalidOperationException("Missing reset element: " + name);
        internal bool HasNotice(string message) => Descendants(DialogElement<NoticeStack>("FailureNotice"))
            .OfType<TextBlock>().Any(text => text.Text == message);
        internal async Task OpenCache()
        {
            Click(ShellElement<Button>("SettingsButton"));
            await Drain();
            SettingsElement<StackPanel>("Areas").Children.OfType<RadioButton>()
                .Single(tab => Equals(tab.Tag, "cache")).IsChecked = true;
            await Drain();
        }
        internal async Task OpenConfirmation()
        {
            await OpenCache();
            Entry.Focus();
            Click(Entry);
            await Until(() => Dialog.IsOpen, "reset confirmation");
        }
        public async ValueTask DisposeAsync()
        {
            Module.ReleaseNavigation.TrySetResult();
            Force = () => Task.CompletedTask;
            if (!Closed) await Shell.AbortAsync();
            await Drain();
        }
    }

    private sealed class Appearance : IDesktopAppearance
    {
        public string ThemeColor => AppearanceColors.DefaultColor;
        public bool CanEdit => true;
        public string Status => "";
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync(string color) => Task.CompletedTask;
    }
    private sealed class Preferences : IDesktopClosePreferences
    {
        internal Func<Task> Save = () => Task.CompletedTask;
        public WindowCloseBehavior Behavior { get; private set; } = WindowCloseBehavior.Ask;
        public bool CanEdit => true;
        public string Status => "";
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public Task LoadAsync() => Task.CompletedTask;
        public async Task<bool> SaveAsync(WindowCloseBehavior behavior)
        {
            await Save(); Behavior = behavior; return true;
        }
    }
    private sealed class Tray : IDesktopTrayIcon
    {
        internal Action Restore = null!, ExitApplication = null!, Unavailable = null!;
        internal int Disposals;
        public bool TryShow() => true;
        public void Dispose() => Disposals++;
    }
    private sealed class ProbeModule : IDesktopModule, IDesktopGeneralSettingsSource, IDesktopNavigationSource
    {
        public string Id => "reset-ui";
        public string Title => "清理测试";
        public string Version => "1";
        public ObservableCollection<NavigationItem> Navigation { get; } =
            [new("home", "首页", "\uE80F"), new("held", "等待", "\uE80F"), new("late", "迟到", "\uE80F")];
        public FrameworkElement View { get; } = new TextBlock { Text = "原业务内容" };
        public FrameworkElement SettingsView { get; } = new TextBox();
        public FrameworkElement? PlayerView => null;
        public FrameworkElement AppearanceSettingsView { get; } = new TextBlock();
        public FrameworkElement HomeSourceSettingsView { get; } = new TextBlock();
        public FrameworkElement CacheSettingsView { get; } = new Border
            { Height = 60, Child = new TextBlock { Text = "业务缓存" } };
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested;
        internal int Navigations;
        internal bool HoldNavigation;
        internal CancellationToken HeldToken;
        internal TaskCompletionSource ReleaseNavigation { get; } = Pending();
        internal void Request(string route) => NavigationRequested?.Invoke(this, new(route));
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public async Task NavigateAsync(string route, CancellationToken token)
        {
            Navigations++;
            if (HoldNavigation && route == "held") { HeldToken = token; await ReleaseNavigation.Task; }
        }
        public Task SearchAsync(string query, CancellationToken token) => Task.CompletedTask;
        public string CaptureNavigationLocation() => "home";
        public Task RestoreNavigationAsync(string location, CancellationToken token) => Task.CompletedTask;
        public Task PrepareShutdownAsync() => Task.CompletedTask;
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

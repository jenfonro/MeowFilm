using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.AppHost;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class ShellCloseBehaviorTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("default asks once and cancel close-button or Escape never saves", Cancel);
        yield return Case("unchecked minimize hides only this time and restores normal state", MinimizeOnce);
        yield return Case("unchecked exit uses one shutdown attempt and releases the tray", ExitOnce);
        yield return Case("remember minimize waits for persistence and applies on later closes", RememberMinimize);
        yield return Case("remember exit persists across shell and preference recreation", RememberExit);
        yield return Case("tray restores maximized state and ordinary minimize keeps the window", RestoreMaximized);
        yield return Case("explicit main-window restore shares tray hidden and maximized state", RestoreMainWindow);
        yield return Case("tray failure retains an operable window and Explorer failure restores it", TrayUnavailable);
        yield return Case("save failure keeps the dialog operable without applying the choice", SaveFailure);
        yield return Case("tray exit retires a pending remembered minimize before its save completes", PendingSaveExit);
        yield return Case("retired save cannot hide a new prompt after failed tray exit is cancelled", PendingSaveAfterCancellation);
        yield return Case("explicit tray exit bypasses minimize and cancellation restores later close behavior", ExplicitExit);
        yield return Case("failed exit can cancel and ask again on a fresh close", RetryAfterCancellation);
        yield return Case("tray exit timeout stays visible and explicit wait observes the same cleanup", Timeout);
        yield return Case("startup abort retires a pending close choice and releases the tray", Abort);
        yield return Case("session ending bypasses choice without claiming pending cleanup completed", SessionEnding);
        yield return Case("dialog inherits theme and contrast with native PNG previews at three scales", ThemePreviews);
    }

    private static TestCase Case(string name, Func<Task> body) =>
        new("shell close: " + name, () => MusicQualityConsumerTests.Sta(body));

    private static async Task Cancel()
    {
        await using var f = await Fixture.Create();
        Check(f.TrayCreations == 1 && f.Tray.ShowCalls == 1, "the real shell starts its tray after HWND creation");
        Click(f.ShellElement<Button>("CloseButton"));
        await Until(() => f.Dialog.IsOpen, "default close prompt");
        Check(f.DialogElement<TextBlock>("DialogTitle").Text == "关闭主窗口", "prompt title");
        Check(f.Shell.IsVisible && f.ShutdownCalls == 0 && f.Store.Writes == 0 &&
            !f.Shell.ShutdownCompletion.IsCompleted, "asking preserves the application lifecycle");
        f.Remember.IsChecked = true;
        f.Shell.Close(); f.Shell.Close(); await Drain();
        Check(f.Dialog.IsOpen && f.Remember.IsChecked == true && f.ShutdownCalls == 0,
            "repeated close keeps one dialog and its draft");
        Click(f.DialogElement<Button>("CancelChoice"));
        await Drain();
        Check(!f.Dialog.IsOpen && f.ShellElement<Grid>("RootLayout").IsEnabled && f.Store.Writes == 0,
            "cancel resumes shell interaction without persisting");
        await f.OpenChoice();
        Check(f.Remember.IsChecked == false, "cancelled draft is discarded");
        f.Remember.IsChecked = true;
        Click(f.DialogElement<Button>("DismissButton"));
        await Drain();
        Check(!f.Dialog.IsOpen && f.Store.Writes == 0, "closing the prompt cancels");
        await f.OpenChoice();
        f.Remember.IsChecked = true;
        var escape = new KeyEventArgs(new TestKeyboard(), PresentationSource.FromVisual(f.Shell)!, 0, Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        f.Dialog.RaiseEvent(escape);
        await Drain();
        Check(escape.Handled && !f.Dialog.IsOpen && f.Store.Writes == 0 && f.ShutdownCalls == 0,
            "Escape cancels the prompt without cancelling application work");
    }

    private static async Task MinimizeOnce()
    {
        await using var f = await Fixture.Create();
        await f.OpenChoice();
        Click(f.DialogElement<Button>("MinimizeChoice"));
        await Until(() => !f.Shell.IsVisible, "one-time tray hide");
        Check(f.Preferences.Behavior == WindowCloseBehavior.Ask && f.Store.Writes == 0 &&
            f.ShutdownCalls == 0 && !f.Shell.ShutdownCompletion.IsCompleted && f.Tray.DisposeCalls == 0,
            "hiding never enters shutdown or disposes the tray");
        f.Shell.PrepareForRender(); // Closing shells reject presentation admission.
        f.Tray.ShowWindow();
        await Until(() => f.Shell.IsVisible, "tray restore");
        Check(f.Shell.WindowState == WindowState.Normal && f.TrayCreations == 1, "restore reuses the normal window and tray");
        await f.OpenChoice();
        Check(f.Store.Writes == 0 && f.ShutdownCalls == 0, "unremembered choice asks next time");
    }

    private static async Task ExitOnce()
    {
        var release = Pending();
        await using var f = await Fixture.Create(shutdown: () => release.Task);
        try
        {
            await f.OpenChoice();
            Click(f.DialogElement<Button>("ExitChoice"));
            await Until(() => f.ShutdownCalls == 1, "normal exit started");
            f.Shell.Close(); f.Shell.Close(); await Drain();
            Check(!f.Dialog.IsOpen && !f.IsClosed && f.ShutdownCalls == 1 && f.Store.Writes == 0 &&
                f.Tray.DisposeCalls == 0, "duplicate close neither saves nor repeats cleanup");
            release.TrySetResult();
            await f.WaitClosed();
            Check(f.Shell.ShutdownSucceeded && f.Shell.CleanupCompleted && f.ShutdownCalls == 1 &&
                f.Tray.DisposeCalls == 1 && f.Preferences.Behavior == WindowCloseBehavior.Ask,
                "only actual successful cleanup closes and releases the tray once");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task RememberMinimize()
    {
        var store = new QualityFixture.DocumentStore { HoldNamespace = AppClosePreferences.DocumentNamespace };
        await using var f = await Fixture.Create(store: store);
        await f.OpenChoice();
        f.Remember.IsChecked = true;
        Click(f.DialogElement<Button>("MinimizeChoice"));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(f.Dialog.IsOpen && f.Dialog.IsPending && f.Shell.IsVisible && f.ShutdownCalls == 0 &&
            !f.DialogElement<Button>("CancelChoice").IsEnabled, "accepted persistence settles before the action");
        f.Shell.Close(); await Drain();
        Check(f.Dialog.IsPending && store.Writes == 0, "repeated close does not start a second save");
        store.Release.TrySetResult();
        await Until(() => !f.Shell.IsVisible, "remembered minimize");
        Check(store.Writes == 1 && f.Preferences.Behavior == WindowCloseBehavior.MinimizeToTray,
            "minimize is remembered only after successful persistence");
        await using var reopened = new AppClosePreferences(store);
        await reopened.LoadAsync();
        Check(reopened.Behavior == WindowCloseBehavior.MinimizeToTray, "a new preference owner reads the saved choice");
        f.Tray.ShowWindow(); await Drain();
        f.Shell.Close();
        await Until(() => !f.Shell.IsVisible, "saved behavior applies immediately");
        Check(!f.Dialog.IsOpen && f.ShutdownCalls == 0 && store.Writes == 1, "later closes use the shared preference");
    }

    private static async Task RememberExit()
    {
        var store = new QualityFixture.DocumentStore();
        await using (var first = await Fixture.Create(store: store))
        {
            await first.OpenChoice();
            first.Remember.IsChecked = true;
            Click(first.DialogElement<Button>("ExitChoice"));
            await first.WaitClosed();
            Check(store.Writes == 1 && first.Preferences.Behavior == WindowCloseBehavior.Exit,
                "exit choice persisted before closing");
        }
        await using var second = await Fixture.Create(store: store);
        second.Shell.Close();
        await second.WaitClosed();
        Check(!second.Dialog.IsOpen && second.ShutdownCalls == 1 && store.Writes == 1,
            "a recreated shell follows persisted exit without prompting or writing again");
    }

    private static async Task RestoreMaximized()
    {
        await using var f = await Fixture.Create(WindowCloseBehavior.MinimizeToTray, showActivated: true);
        f.Shell.WindowState = WindowState.Maximized; await Drain();
        f.Shell.Close();
        await Until(() => !f.Shell.IsVisible, "maximized tray hide");
        f.Tray.ShowWindow();
        await Until(() => f.Shell.IsVisible && f.Shell.WindowState == WindowState.Maximized, "maximized restore");
        var minimize = Descendants((DependencyObject)f.Shell.Content).OfType<Button>()
            .First(button => AutomationProperties.GetName(button) == "最小化");
        Click(minimize);
        await Until(() => f.Shell.WindowState == WindowState.Minimized, "ordinary minimize button");
        Check(f.Shell.Visibility == Visibility.Visible && f.ShutdownCalls == 0 && !f.Dialog.IsOpen,
            "ordinary minimize retains its native visible/minimized state");
        f.Tray.ShowWindow();
        await Until(() => f.Shell.WindowState == WindowState.Maximized, "restore ordinary minimized window");
        Check(f.TrayCreations == 1 && f.ShutdownCalls == 0, "restore does not restart application owners");
    }

    private static async Task TrayUnavailable()
    {
        await using var f = await Fixture.Create(trayAvailable: false, showActivated: true);
        Check(f.Shell.IsVisible && f.HasNotice("系统托盘不可用，主窗口保持打开"),
            "failed startup registration leaves the window and a unified notice");
        await f.OpenChoice();
        f.Remember.IsChecked = true;
        Click(f.DialogElement<Button>("MinimizeChoice")); await Drain();
        Check(f.Shell.IsVisible && f.Dialog.IsOpen && !f.Dialog.IsPending && f.Store.Writes == 0 &&
            f.ShutdownCalls == 0 && f.DialogElement<Button>("ExitChoice").IsEnabled,
            "an unavailable tray cannot save or apply minimize and exit stays available");
        Click(f.DialogElement<Button>("CancelChoice"));
        Check(await f.Preferences.SaveAsync(WindowCloseBehavior.MinimizeToTray), "set a remembered minimize");
        f.Shell.Close(); await Drain();
        Check(f.Shell.IsVisible && f.ShutdownCalls == 0, "remembered minimize also verifies tray availability");
        f.Tray.Available = true;
        f.Shell.WindowState = WindowState.Maximized;
        f.Shell.Close();
        await Until(() => !f.Shell.IsVisible, "registration recovered");
        f.Tray.Available = false;
        f.Tray.Unavailable();
        await Until(() => f.Shell.IsVisible, "Explorer recovery failure restores a window");
        Check(f.Shell.WindowState == WindowState.Maximized && f.ShutdownCalls == 0 &&
            f.HasNotice("系统托盘不可用，主窗口保持打开"), "lost tray never strands a hidden application");
    }

    private static async Task RestoreMainWindow()
    {
        await using var f = await Fixture.Create(WindowCloseBehavior.MinimizeToTray, showActivated: true);
        f.Shell.WindowState = WindowState.Maximized;
        f.Shell.Close();
        await Until(() => !f.Shell.IsVisible, "hide before desktop-lyrics restore");
        f.Shell.RestoreFromTray();
        await Until(() => f.Shell.IsVisible && f.Shell.WindowState == WindowState.Maximized, "explicit main restore");
        f.Shell.Close();
        await Until(() => !f.Shell.IsVisible, "explicit restore cleared the prior hidden state");
        f.Shell.RestoreFromTray();
        f.Shell.WindowState = WindowState.Minimized;
        f.Shell.RestoreFromTray();
        await Until(() => f.Shell.WindowState == WindowState.Maximized, "restore a normally minimized main window");
        Check(f.ShutdownCalls == 0 && f.TrayCreations == 1 && !f.Dialog.IsOpen,
            "desktop activation neither starts shutdown nor reconstructs tray/application owners");
    }

    private static async Task SaveFailure()
    {
        var store = new QualityFixture.DocumentStore
        { FaultNamespace = AppClosePreferences.DocumentNamespace, Fault = true };
        await using var f = await Fixture.Create(store: store);
        await f.OpenChoice();
        f.Remember.IsChecked = true;
        Click(f.DialogElement<Button>("MinimizeChoice"));
        await Until(() => !f.Dialog.IsPending, "failed save returned");
        Check(f.Dialog.IsOpen && f.Shell.IsVisible && f.Preferences.Behavior == WindowCloseBehavior.Ask &&
            f.ShutdownCalls == 0 && f.HasNotice(f.Preferences.Status) && f.Preferences.Status.Length > 0,
            "save failure retains the prompt and reports the authoritative error");
        f.Shell.Close(); await Drain();
        Check(f.Remember.IsChecked == true && store.Writes == 1, "close while prompting never retries a failed save");
        f.Remember.IsChecked = false;
        Click(f.DialogElement<Button>("MinimizeChoice"));
        await Until(() => !f.Shell.IsVisible, "one-time action after failed save");
        Check(store.Writes == 1 && f.Preferences.Behavior == WindowCloseBehavior.Ask && f.ShutdownCalls == 0,
            "the user can choose a one-time action after persistence fails");
    }

    private static async Task ExplicitExit()
    {
        int attempts = 0;
        await using var f = await Fixture.Create(WindowCloseBehavior.MinimizeToTray,
            shutdown: () => ++attempts == 1 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);
        f.Shell.Close();
        await Until(() => !f.Shell.IsVisible, "hidden shell");
        f.Tray.ExitApplication();
        await Until(() => f.Status == "关闭失败", "explicit tray exit preparation failed");
        Check(f.ShutdownWasVisible && f.Shell.IsVisible && !f.Dialog.IsOpen && f.ShutdownCalls == 1 &&
            f.Tray.DisposeCalls == 0, "explicit exit restores the shell and bypasses the minimize preference");
        f.Tray.ExitApplication(); await Drain();
        Check(f.ShutdownCalls == 1, "repeated explicit exit shares the active shutdown");
        Click(f.ShellElement<Button>("CancelShutdownButton"));
        await Until(() => f.Status == "" && f.ShellElement<Grid>("ContentDeck").IsEnabled, "cancel tray exit");
        f.Shell.Close();
        await Until(() => !f.Shell.IsVisible, "normal close again honors minimize");
        Check(f.ShutdownCalls == 1 && f.Preferences.Behavior == WindowCloseBehavior.MinimizeToTray,
            "an explicit exit does not change the remembered close behavior");
        f.Tray.ExitApplication();
        await f.WaitClosed();
        Check(f.ShutdownCalls == 2 && f.CancelCalls == 1 && f.ShutdownWasVisible && f.Tray.DisposeCalls == 1,
            "a fresh explicit exit owns one successful cleanup");
    }

    private static async Task PendingSaveExit()
    {
        var store = new QualityFixture.DocumentStore { HoldNamespace = AppClosePreferences.DocumentNamespace };
        var releaseShutdown = Pending();
        await using var f = await Fixture.Create(store: store, shutdown: () => releaseShutdown.Task);
        try
        {
            await f.OpenChoice();
            f.Remember.IsChecked = true;
            Click(f.DialogElement<Button>("MinimizeChoice"));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Tray.ExitApplication();
            Check(!f.Dialog.IsOpen && f.Shell.IsVisible && f.ShutdownWasVisible && f.ShutdownCalls == 1 &&
                !f.IsClosed && !f.Shell.CleanupCompleted, "tray exit retires the dialog while its store write is pending");
            f.Tray.ExitApplication(); await Drain();
            Check(f.ShutdownCalls == 1, "pending preference drain still belongs to one exit attempt");
            store.Release.TrySetResult();
            await Until(() => f.Preferences.Behavior == WindowCloseBehavior.MinimizeToTray, "retired save settled");
            Check(f.Shell.IsVisible && !f.IsClosed && !f.Dialog.IsOpen && f.ShutdownCalls == 1 && store.Writes == 1,
                "a late saved minimize cannot hide the window while explicit exit is still waiting");
            releaseShutdown.TrySetResult();
            await f.WaitClosed();
            Check(f.ShutdownCalls == 1 && f.Tray.DisposeCalls == 1, "explicit exit remains the only cleanup owner");
        }
        finally { releaseShutdown.TrySetResult(); }
    }

    private static async Task PendingSaveAfterCancellation()
    {
        int attempts = 0;
        var store = new QualityFixture.DocumentStore { HoldNamespace = AppClosePreferences.DocumentNamespace };
        await using var f = await Fixture.Create(store: store,
            shutdown: () => ++attempts == 1 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);
        await f.OpenChoice();
        f.Remember.IsChecked = true;
        Click(f.DialogElement<Button>("MinimizeChoice"));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Tray.ExitApplication();
        await Until(() => f.Status == "关闭失败", "tray exit failed before the old save settled");
        Click(f.ShellElement<Button>("CancelShutdownButton"));
        await Until(() => f.Status == "", "tray exit cancelled");
        await f.OpenChoice();
        store.Release.TrySetResult();
        await Until(() => f.Preferences.Behavior == WindowCloseBehavior.MinimizeToTray, "retired save settled after rollback");
        Check(f.Shell.IsVisible && f.Dialog.IsOpen && f.Remember.IsChecked == false && !f.Dialog.IsPending &&
            f.ShutdownCalls == 1 && store.Writes == 1,
            "the retired selection cannot hide the window or mutate the newly opened dialog");
        Click(f.DialogElement<Button>("CancelChoice"));
        f.Shell.Close();
        await Until(() => !f.Shell.IsVisible, "a new close follows the now-saved preference");
        f.Tray.ExitApplication();
        await f.WaitClosed();
        Check(f.ShutdownCalls == 2 && f.CancelCalls == 1 && f.Tray.DisposeCalls == 1,
            "after rollback, only a new explicit exit performs cleanup");
    }

    private static async Task RetryAfterCancellation()
    {
        int attempts = 0;
        await using var f = await Fixture.Create(
            shutdown: () => ++attempts == 1 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);
        await f.OpenChoice();
        Click(f.DialogElement<Button>("ExitChoice"));
        await Until(() => f.Status == "关闭失败", "failed close");
        Check(f.ShellElement<Button>("CancelShutdownButton").IsEnabled && !f.Dialog.IsOpen,
            "the existing failed-shutdown decision remains operable");
        Click(f.ShellElement<Button>("CancelShutdownButton"));
        await Until(() => f.Status == "" && f.ShellElement<Grid>("ContentDeck").IsEnabled, "failed-close rollback");
        await f.OpenChoice();
        Click(f.DialogElement<Button>("ExitChoice"));
        await f.WaitClosed();
        Check(f.ShutdownCalls == 2 && f.CancelCalls == 1 && f.Store.Writes == 0 && f.Shell.ShutdownSucceeded,
            "the next close asks and starts a fresh shutdown rather than reusing failure");
    }

    private static async Task Timeout()
    {
        var release = Pending();
        await using var f = await Fixture.Create(WindowCloseBehavior.MinimizeToTray, shutdown: () => release.Task);
        try
        {
            f.Shell.Close();
            await Until(() => !f.Shell.IsVisible, "tray hide before timed out exit");
            f.Tray.ExitApplication();
            Check(f.ShutdownWasVisible && f.ShutdownCalls == 1, "tray exit shows the shell before cleanup starts");
            f.Clock.Expire();
            await Until(() => f.Status == "关闭超时", "shutdown wait deadline");
            Check(f.Shell.IsVisible && !f.IsClosed && !f.Dialog.IsOpen && !f.Shell.CleanupCompleted &&
                f.ShellElement<Button>("WaitForShutdownButton").IsEnabled &&
                f.ShellElement<Button>("ForceExitButton").IsEnabled && f.Tray.DisposeCalls == 0,
                "timeout preserves the existing wait/force decision on a visible shell");
            f.Tray.ExitApplication(); await Drain();
            Check(f.ShutdownCalls == 1, "explicit exit cannot restart timed out cleanup");
            release.TrySetResult(); await Drain();
            Check(!f.IsClosed, "late cleanup needs an explicit wait decision");
            Click(f.ShellElement<Button>("WaitForShutdownButton"));
            await f.WaitClosed();
            Check(f.ShutdownCalls == 1 && f.Shell.CleanupCompleted && f.Tray.DisposeCalls == 1,
                "the explicit wait observes the original cleanup and closes once");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task Abort()
    {
        await using var f = await Fixture.Create();
        await f.OpenChoice();
        f.Remember.IsChecked = true;
        await f.Shell.AbortAsync();
        await f.WaitClosed();
        Check(!f.Dialog.IsOpen && f.Store.Writes == 0 && f.ForceCalls == 1 && f.Tray.DisposeCalls == 1,
            "startup abort bypasses the choice without persisting its draft");
    }

    private static async Task SessionEnding()
    {
        var release = Pending();
        await using var f = await Fixture.Create(shutdown: () => release.Task);
        try
        {
            await f.OpenChoice();
            f.Remember.IsChecked = true;
            f.Shell.PrepareForSessionEnding();
            Check(!f.Dialog.IsOpen && f.Store.Writes == 0 && f.ShutdownCalls == 1 &&
                !f.Shell.CleanupCompleted && !f.Shell.ShutdownSucceeded && !f.Shell.ShutdownCompletion.IsCompleted,
                "session ending starts cleanup without claiming asynchronous completion");
            f.Shell.Close();
            await Until(() => f.IsClosed, "system close remains permitted during pending cleanup");
            Check(f.Tray.DisposeCalls == 1 && f.ShutdownCalls == 1 && !f.Shell.CleanupCompleted,
                "the OS path is neither blocked by the prompt nor a second cleanup attempt");
            release.TrySetResult();
            await f.Shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.TrySetResult(); }
    }

    private static async Task ThemePreviews()
    {
        var store = new QualityFixture.DocumentStore();
        await using var appearance = new AppAppearance(store);
        await appearance.LoadAsync();
        await using var f = await Fixture.Create(store: store, appearance: appearance);
        await f.OpenChoice();
        f.Shell.Width = 860; f.Shell.Height = 600; await Drain();
        var surface = f.DialogElement<Border>("DialogSurface");
        var defaultColor = ((SolidColorBrush)surface.Background).Color;
        Check(surface.Background == AppearanceColors.FloatingSurface && f.Dialog.FontFamily == f.Shell.FontFamily &&
            f.Dialog.FontSize == f.Shell.FontSize, "default dialog uses the shell's palette and typography");
        CheckLayout(f);
        SavePreviews((FrameworkElement)f.Shell.Content, "shell-close-default", 860, 600);
        await appearance.SaveAsync("#A0D0C0"); await Drain();
        Check(((SolidColorBrush)surface.Background).Color != defaultColor && surface.Background == AppearanceColors.FloatingSurface,
            "an already open dialog follows the shell's custom theme change");
        f.Shell.Width = 1110; f.Shell.Height = 820; await Drain();
        CheckLayout(f);
        SavePreviews((FrameworkElement)f.Shell.Content, "shell-close-custom", 1110, 820);
        DesktopTheme.SetIsHighContrast(f.Shell, true); await Drain();
        Check(surface.Background == SystemColors.WindowBrush && surface.BorderBrush == SystemColors.WindowTextBrush &&
            surface.Effect is null && f.Dialog.Foreground == SystemColors.WindowTextBrush &&
            f.Remember.Foreground == SystemColors.WindowTextBrush &&
            f.DialogElement<Button>("MinimizeChoice").Foreground == SystemColors.WindowTextBrush,
            "high contrast uses system colors and removes floating shadows");
        f.Shell.Width = 860; f.Shell.Height = 600; await Drain();
        CheckLayout(f);
        SavePreviews((FrameworkElement)f.Shell.Content, "shell-close-high-contrast", 860, 600);
        f.Remember.IsEnabled = false; await Drain();
        Check(f.Remember.Foreground == SystemColors.GrayTextBrush, "disabled remember control preserves high contrast");
        DesktopTheme.SetIsHighContrast(f.Shell, false); await Drain();
        Check(surface.Background == AppearanceColors.FloatingSurface, "leaving high contrast restores the current custom palette");
    }

    private static void CheckLayout(Fixture f)
    {
        var surface = f.DialogElement<Border>("DialogSurface");
        var position = surface.TranslatePoint(new Point(), f.Dialog);
        Check(Math.Abs(position.X * 2 + surface.ActualWidth - f.Dialog.ActualWidth) < 2 &&
            Math.Abs(position.Y * 2 + surface.ActualHeight - f.Dialog.ActualHeight) < 2, "dialog centers on the shell");
        foreach (string name in new[] { "MinimizeChoice", "ExitChoice", "CancelChoice", "DismissButton" })
        {
            var button = f.DialogElement<Button>(name);
            var origin = button.TranslatePoint(new Point(), surface);
            Check(AutomationProperties.GetName(button).Length > 0 && button.ActualWidth > 20 && button.ActualHeight >= 30 &&
                origin.X >= 0 && origin.Y >= 0 && origin.X + button.ActualWidth <= surface.ActualWidth &&
                origin.Y + button.ActualHeight <= surface.ActualHeight, "accessible unclipped dialog action: " + name);
        }
    }

    private static void SavePreviews(FrameworkElement view, string name, int width, int height)
    {
        string root = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT")
            ?? TestWorkspace.DataPath("shell-close-previews");
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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
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
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }

    private sealed class TestKeyboard() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => KeyStates.None;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal QualityFixture.DocumentStore Store { get; }
        internal AppClosePreferences Preferences { get; }
        internal ShellWindow Shell { get; }
        internal TrayIcon Tray { get; } = new();
        internal DeadlineClock Clock { get; } = new();
        internal int TrayCreations, ShutdownCalls, ForceCalls, CancelCalls;
        internal bool ShutdownWasVisible;
        private readonly TaskCompletionSource closed = Pending();
        internal bool IsClosed => closed.Task.IsCompleted;
        internal string Status => ShellElement<TextBlock>("StatusText").Text;
        internal CloseWindowDialog Dialog => ShellElement<CloseWindowDialog>("CloseDialog");
        internal CheckBox Remember => DialogElement<CheckBox>("RememberChoice");

        private Fixture(QualityFixture.DocumentStore store, AppClosePreferences preferences,
            IDesktopAppearance? appearance, Func<Task>? shutdown, bool trayAvailable, bool showActivated)
        {
            Store = store; Preferences = preferences; Tray.Available = trayAvailable;
            // Existing contract-only module: showing the shell creates no network, runtime or business owner.
            var module = new SettingsNavigationTests.Module("close-probe", "关闭测试");
            Shell = new ShellWindow([module], [], async () =>
            {
                ShutdownCalls++; ShutdownWasVisible = Shell!.IsVisible;
                if (shutdown is not null) await shutdown();
                await Preferences.PrepareShutdownAsync();
            }, async () =>
            {
                ForceCalls++;
                await Preferences.AbortAsync();
            }, async () =>
            {
                CancelCalls++;
                await Preferences.CancelShutdownPreparationAsync();
            }, Clock, appearance, preferences, (_, show, exit, unavailable) =>
            {
                TrayCreations++;
                Tray.ShowWindow = show; Tray.ExitApplication = exit; Tray.Unavailable = unavailable;
                return Tray;
            })
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                ShowActivated = showActivated, ShowInTaskbar = false
            };
            Shell.Closed += (_, _) => closed.TrySetResult();
        }

        internal static async Task<Fixture> Create(WindowCloseBehavior behavior = WindowCloseBehavior.Ask,
            QualityFixture.DocumentStore? store = null, IDesktopAppearance? appearance = null,
            Func<Task>? shutdown = null, bool trayAvailable = true, bool showActivated = false)
        {
            store ??= new();
            var preferences = new AppClosePreferences(store);
            await preferences.LoadAsync();
            if (behavior != WindowCloseBehavior.Ask)
                Check(await preferences.SaveAsync(behavior), "fixture initial preference");
            var fixture = new Fixture(store, preferences, appearance, shutdown, trayAvailable, showActivated);
            fixture.Shell.Show(); await Drain();
            return fixture;
        }

        internal T ShellElement<T>(string name) where T : FrameworkElement =>
            Shell.FindName(name) as T ?? throw new InvalidOperationException("Missing shell element: " + name);
        internal T DialogElement<T>(string name) where T : FrameworkElement =>
            Dialog.FindName(name) as T ?? throw new InvalidOperationException("Missing dialog element: " + name);
        internal bool HasNotice(string message) => Descendants(ShellElement<NoticeStack>("Notices")).OfType<TextBlock>()
            .Any(text => text.Text == message);
        internal async Task OpenChoice()
        {
            Shell.Close();
            await Until(() => Dialog.IsOpen, "close choice opened");
        }
        internal async Task WaitClosed()
        {
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Drain();
        }
        public async ValueTask DisposeAsync()
        {
            Store.Release.TrySetResult();
            if (!IsClosed) await Shell.AbortAsync();
            await WaitClosed();
            await Preferences.DisposeAsync();
        }
    }

    private sealed class TrayIcon : IDesktopTrayIcon
    {
        internal bool Available = true;
        internal int ShowCalls, DisposeCalls;
        internal Action ShowWindow = null!, ExitApplication = null!, Unavailable = null!;
        public bool TryShow() { ShowCalls++; return Available; }
        public void Dispose() => DisposeCalls++;
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private readonly List<DeadlineTimer> timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(callback, state);
            lock (timers) timers.Add(timer);
            return timer;
        }
        internal void Expire()
        {
            DeadlineTimer[] all;
            lock (timers) all = timers.ToArray();
            foreach (var timer in all) timer.Fire();
        }
        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private int ended;
            internal void Fire() { if (Interlocked.Exchange(ref ended, 1) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref ended) == 0;
            public void Dispose() => Interlocked.Exchange(ref ended, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

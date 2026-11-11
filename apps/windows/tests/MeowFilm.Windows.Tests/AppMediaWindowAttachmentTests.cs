using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using MeowFilm.App;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using MeowFilm.SystemMedia;
using static MeowFilm.Windows.Tests.MediaShortcutTests;

namespace MeowFilm.Windows.Tests;

public static class AppMediaWindowAttachmentTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("initial availability and independent switches", Independent);
        yield return Case("cancelled native Closing and failed preparation restore both channels", CancelledClosing);
        yield return Case("actual Closed permanently stops both channels", Closed);
        yield return Case("composition waits for accepted shortcut actions before releasing system media", Drain);
        yield return Case("native release error is retried by host force cleanup", RetryRelease);
        yield return Case("retry retains only resources whose release failed", RetryRemaining);
        yield return Case("late system command completion allows retry after drain timeout", SystemDrainRetry);
        yield return Case("partial startup ownership is released once", PartialStartup);
        yield return Case("real settings save busy and failure preserve the active registration", SaveNotifications);
        yield return Case("shell asks and hides without stopping; shared save wait seals all commands and cancellation restores", ShellClosePreparation);
    }

    private static TestCase Case(string name, Func<Task> action) => new("app-media." + name, () => Sta(action));

    private static async Task Independent()
    {
        await using var keys = new ShortcutFixture();
        keys.Settings.SetAvailable(false);
        await using var owner = new AppMediaWindowAttachment(keys.Window, keys.Settings);
        var system = new SystemFixture(keys.Window, enabled: false);
        owner.OwnSystemMedia(system.Attachment, system.Attachment.SetEnabled);
        keys.Attach(); owner.OwnShortcuts(keys.Attachment!);
        Check(!system.Backend.Enabled && keys.Native.Calls.Count == 0, "unavailable startup enables neither channel");
        keys.Settings.SetAvailable(true);
        Check(system.Backend.Enabled && keys.Native.Latest.Success, "authoritative availability starts both channels");
        int updates = system.Backend.Updates;
        keys.Settings.SetPreferences(keys.Settings.Preferences with { GlobalEnabled = false });
        Check(system.Backend.Enabled && system.Backend.Updates == updates &&
            keys.Native.Unregistered.Contains(keys.Native.Latest.Id), "global switch leaves SMTC untouched");
        keys.Settings.SetRecording(true);
        Check(system.Backend.Enabled && system.Backend.Updates == updates, "recording is not the SMTC switch");
        keys.Settings.SetPreferences(keys.Settings.Preferences with { GlobalEnabled = true });
        keys.Settings.SetRecording(false);
        int registrations = keys.Native.Calls.Count;
        keys.Post(keys.Native.Latest);
        keys.Settings.SetPreferences(keys.Settings.Preferences with { SystemMediaEnabled = false });
        Check(!system.Backend.Enabled && keys.Native.Latest.Success && keys.Native.Calls.Count == registrations,
            "SMTC off leaves the existing global registration intact");
        system.Backend.Request(SystemPlaybackCommand.Pause); await Pump();
        Check(keys.Actions.Count == 1 && system.Player.Calls.Count == 0, "independent channel admission");
        keys.Settings.SetPreferences(keys.Settings.Preferences with { SystemMediaEnabled = true });
        system.Backend.Request(SystemPlaybackCommand.Pause); await Pump();
        Check(system.Backend.Enabled && system.Player.Calls.SequenceEqual(["pause"]), "SMTC reopens on current media");
    }

    private static async Task CancelledClosing()
    {
        var window = new PreparationWindow();
        await using var keys = new ShortcutFixture(window: window);
        await using var settings = new AppMediaControlSettings(new QualityFixture.DocumentStore());
        await settings.LoadAsync(); await settings.SaveAsync(ShortcutFixture.Preferences());
        await using var owner = new AppMediaWindowAttachment(window, settings);
        var system = new SystemFixture(window);
        owner.OwnSystemMedia(system.Attachment, system.Attachment.SetEnabled);
        keys.Attach(settings); owner.OwnShortcuts(keys.Attachment!);
        var prepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool pausedBeforeDomain = false;
        var domain = new Preparation(() =>
        {
            pausedBeforeDomain = !settings.IsAvailable && !system.Backend.Enabled &&
                keys.Native.Unregistered.Contains(keys.Native.Latest.Id);
            return prepare.Task;
        });
        var shutdown = new AppShutdown([domain], [new(() => owner.DisposeAsync())], sharedParticipants: [settings]);
        Task? closing = null;
        window.Prepare = () => closing = shutdown.CloseAsync();
        try
        {
            var old = keys.Native.Latest;
            keys.Post(old); system.Backend.Request(SystemPlaybackCommand.Pause);
            Console.WriteLine($"NATIVE shortcut before Close {ShortcutNative.WindowEvidence(keys.Handle)} cancelled={window.Cancelled} available={settings.IsAvailable}");
            window.Close();
            Console.WriteLine($"NATIVE shortcut after Close {ShortcutNative.WindowEvidence(keys.Handle)} cancelled={window.Cancelled} " +
                $"available={settings.IsAvailable} closingStatus={closing?.Status} pausedBeforeDomain={pausedBeforeDomain}");
            Check(window.Cancelled == 1 && !keys.Source.IsDisposed, "real Closing was cancelled after event handlers");
            Check(pausedBeforeDomain && !settings.IsAvailable, "shared settings pause inputs before domain preparation");
            keys.Post(old); system.Backend.Request(SystemPlaybackCommand.Pause); await Pump();
            Check(keys.Actions.Count == 0 && system.Player.Calls.Count == 0, "preparation invalidates pending commands on both channels");
            Check(system.Backend.Disposals == 0, "reversible preparation retains ownership");
            prepare.TrySetException(new InvalidOperationException("synthetic-preparation-failure"));
            await Fails(closing!, "SHUTDOWN_FAILED");
            await shutdown.CancelShutdownPreparationAsync();
            Check(settings.IsAvailable && system.Backend.Enabled && keys.Native.Latest.Success, "cancelled preparation restores both channels");
            keys.Post(keys.Native.Latest); system.Backend.Request(SystemPlaybackCommand.Pause); await Pump();
            Check(keys.Actions.Count == 1 && system.Player.Calls.SequenceEqual(["pause"]), "restored native and system requests execute");
            await settings.SaveAsync(settings.Preferences with { SystemMediaEnabled = false });
            Check(!system.Backend.Enabled, "cancelled Closing retained the settings subscription");
            await settings.SaveAsync(settings.Preferences with { SystemMediaEnabled = true });
            Check(system.Backend.Enabled, "SMTC was not permanently suspended by Closing");
            await owner.DisposeAsync();
            int registrations = keys.Native.Calls.Count, updates = system.Backend.Updates;
            await settings.SaveAsync(settings.Preferences with { GlobalEnabled = false, SystemMediaEnabled = false });
            await settings.SaveAsync(settings.Preferences with { GlobalEnabled = true, SystemMediaEnabled = true });
            keys.Native.Foreground = keys.Handle;
            keys.Post(keys.Native.Latest); system.Backend.Request(SystemPlaybackCommand.Pause); await Pump();
            Check(keys.Native.Calls.Count == registrations && system.Backend.Updates == updates &&
                keys.Actions.Count == 1 && system.Player.Calls.Count == 1 && system.Backend.Disposals == 1,
                "Dispose permanently seals both channels despite later setting changes");
        }
        finally
        {
            prepare.TrySetException(new InvalidOperationException("synthetic-preparation-failure"));
            if (closing is not null) { try { await closing; } catch { } }
            window.CancelClose = false;
        }
    }

    private static async Task Closed()
    {
        await using var keys = new ShortcutFixture();
        await using var owner = new AppMediaWindowAttachment(keys.Window, keys.Settings);
        var system = new SystemFixture(keys.Window);
        owner.OwnSystemMedia(system.Attachment, system.Attachment.SetEnabled);
        keys.Attach(); owner.OwnShortcuts(keys.Attachment!);
        keys.Window.Close();
        int registrations = keys.Native.Calls.Count, updates = system.Backend.Updates;
        keys.Settings.SetAvailable(false); keys.Settings.SetAvailable(true);
        keys.Settings.SetPreferences(keys.Settings.Preferences with { SystemMediaEnabled = false });
        keys.Settings.SetPreferences(keys.Settings.Preferences with { SystemMediaEnabled = true });
        system.Attachment.SetEnabled(true);
        system.Player.Notify(); system.Backend.Request(SystemPlaybackCommand.Pause); await Pump();
        Check(!system.Backend.Enabled && keys.Native.Calls.Count == registrations &&
            system.Backend.Updates == updates && system.Player.Calls.Count == 0, "actual closure is permanent, including direct SetEnabled");
        await owner.DisposeAsync(); await owner.DisposeAsync();
        Check(system.Backend.Disposals == 1, "closed resources released once");
    }

    private static async Task Drain()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var keys = new ShortcutFixture(execute: _ => release.Task);
        await using var owner = new AppMediaWindowAttachment(keys.Window, keys.Settings);
        var system = new SystemFixture(keys.Window);
        owner.OwnSystemMedia(system.Attachment, system.Attachment.SetEnabled);
        keys.Attach(); owner.OwnShortcuts(keys.Attachment!);
        keys.Post(keys.Native.Latest); await Pump();
        Check(keys.Actions.Count == 1, "accepted owner action is running");
        var disposal = owner.DisposeAsync().AsTask();
        try
        {
            Check(ReferenceEquals(disposal, owner.DisposeAsync().AsTask()) && !disposal.IsCompleted &&
                !system.Backend.Enabled && system.Backend.Disposals == 0, "admission stops without releasing the action's dependencies");
            await using var competitor = new ShortcutFixture(); competitor.Attach();
            Check(competitor.Native.Latest.Success, "native keys released before action settles");
            keys.Settings.SetAvailable(false); keys.Settings.SetAvailable(true);
            Check(system.Backend.Disposals == 0 && keys.Native.Calls.Count == 1, "no reopen during drain");
        }
        finally { release.TrySetResult(); await disposal; }
        Check(system.Backend.Disposals == 1, "system attachment released after action settles");
    }

    private static async Task RetryRelease()
    {
        await using var keys = new ShortcutFixture();
        await using var owner = new AppMediaWindowAttachment(keys.Window, keys.Settings);
        var system = new SystemFixture(keys.Window);
        owner.OwnSystemMedia(system.Attachment, system.Attachment.SetEnabled);
        keys.Attach(); owner.OwnShortcuts(keys.Attachment!);
        keys.Native.UnregisterError = 5;
        int resourcesReleased = 0;
        var shutdown = new AppShutdown([], [new(() => owner.DisposeAsync()), new(() =>
        {
            resourcesReleased++;
            return ValueTask.CompletedTask;
        })]);
        try
        {
            await Fails(shutdown.CloseAsync(), "SHUTDOWN_FAILED");
            Check(system.Backend.Disposals == 0 && resourcesReleased == 0, "failed native release retains remaining dependencies");
            await using var competitor = new ShortcutFixture(); competitor.Attach();
            Check(competitor.Settings.GlobalErrors[MediaShortcutAction.TogglePlayback] == "热键被占用", "injected failure retained the real OS registration");
            keys.Native.UnregisterError = null;
            await shutdown.ForceReleaseAsync();
            Check(shutdown.IsReleased && resourcesReleased == 1 && system.Backend.Disposals == 1 &&
                keys.Native.Unregistered.Contains(keys.Native.Latest.Id) && keys.Native.Allocated.SequenceEqual(keys.Native.Released),
                "host retry performs real unregister and atom retirement before dependent resource release");
            competitor.Settings.RequestRegistrationRefresh();
            Check(competitor.Native.Latest.Success && competitor.Settings.GlobalErrors.Count == 0, "real competing HWND recovers after retry");
            await owner.DisposeAsync();
            Check(system.Backend.Disposals == 1 && resourcesReleased == 1, "successful retry is not repeated");
        }
        finally { keys.Native.UnregisterError = null; }
    }

    private static async Task RetryRemaining()
    {
        await using var keys = new ShortcutFixture(ShortcutFixture.Preferences(global: false));
        await using var owner = new AppMediaWindowAttachment(keys.Window, keys.Settings);
        var first = new Resource();
        var second = new Resource { Failures = 1 };
        owner.OwnShortcuts(first); owner.OwnSystemMedia(second, _ => { });
        await Fails(owner.DisposeAsync().AsTask(), "SYNTHETIC_RELEASE_FAILED");
        Check(first.Attempts == 1 && second.Attempts == 1, "first resource succeeded before later resource failed");
        await owner.DisposeAsync(); await owner.DisposeAsync();
        Check(first.Attempts == 1 && second.Attempts == 2, "only the failed resource is retried");
    }

    private static async Task PartialStartup()
    {
        await using var keys = new ShortcutFixture(ShortcutFixture.Preferences(global: false));
        var enabled = new List<bool>();
        var resource = new Resource();
        await using var owner = new AppMediaWindowAttachment(keys.Window, keys.Settings);
        owner.OwnSystemMedia(resource, enabled.Add);
        await owner.DisposeAsync(); await owner.DisposeAsync();
        keys.Settings.SetAvailable(false); keys.Settings.SetAvailable(true);
        Check(resource.Attempts == 1 && enabled.SequenceEqual([true, false]), "partial startup releases its owned attachment and subscription once");
    }

    private static async Task SystemDrainRetry()
    {
        await using var keys = new ShortcutFixture(ShortcutFixture.Preferences(global: false));
        await using var owner = new AppMediaWindowAttachment(keys.Window, keys.Settings);
        var backend = new Backend();
        var player = new Player();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SystemMediaSession(player, backend,
            [new("audio", _ => { entered.TrySetResult(); return finish.Task; })], keys.Window.Dispatcher);
        var attachment = new SystemMediaWindowAttachment(keys.Window, session);
        owner.OwnSystemMedia(attachment, attachment.SetEnabled);
        var shortcuts = new Resource();
        owner.OwnShortcuts(shortcuts);
        try
        {
            backend.Request(SystemPlaybackCommand.Next);
            await entered.Task;
            await Fails(owner.DisposeAsync().AsTask(), "SYSTEM_MEDIA_DRAIN_FAILED");
            Check(backend.Disposals == 1 && shortcuts.Attempts == 1 && !finish.Task.IsCompleted,
                "timeout reports failure while the real command still owns playback");
            keys.Settings.SetAvailable(false); keys.Settings.SetAvailable(true);
            Check(!backend.Enabled, "settings cannot reopen a timed-out attachment");
            finish.TrySetResult();
            await Pump();
            await owner.DisposeAsync(); await owner.DisposeAsync();
            Check(backend.Disposals == 1 && shortcuts.Attempts == 1,
                "late completion permits host retry without repeating released resources");
        }
        finally { finish.TrySetResult(); await owner.DisposeAsync(); }
    }

    private static async Task SaveNotifications()
    {
        var store = new QualityFixture.DocumentStore();
        await using var settings = new AppMediaControlSettings(store);
        await settings.LoadAsync(); await settings.SaveAsync(ShortcutFixture.Preferences());
        await using var keys = new ShortcutFixture();
        await using var owner = new AppMediaWindowAttachment(keys.Window, settings);
        var system = new SystemFixture(keys.Window);
        owner.OwnSystemMedia(system.Attachment, system.Attachment.SetEnabled);
        keys.Attach(settings); owner.OwnShortcuts(keys.Attachment!);
        int updates = system.Backend.Updates;
        var registration = keys.Native.Latest;
        keys.Post(registration);
        store.HoldNamespace = store.FaultNamespace = AppMediaControlSettings.DocumentNamespace;
        store.Fault = true;
        var saving = settings.SaveAsync(settings.Preferences with { SystemMediaEnabled = false });
        try
        {
            await store.Entered.Task;
            Check(settings.IsAvailable && !settings.CanEdit, "actual settings busy keeps input available");
            await Pump();
            Check(keys.Actions.Count == 1 && keys.Native.Calls.Count == 1 && keys.Native.Unregistered.Count == 0 &&
                system.Backend.Enabled && system.Backend.Updates == updates, "busy notifications preserve both channels and the queued native message");
        }
        finally { store.Release.TrySetResult(); await saving; }
        Check(settings.Status.Length != 0 && settings.IsAvailable && settings.Preferences.SystemMediaEnabled &&
            keys.Native.Calls.Count == 1 && keys.Native.Unregistered.Count == 0 && system.Backend.Enabled &&
            system.Backend.Updates == updates, "failed write status with unchanged authority does not re-register or disable SMTC");
    }

    private static async Task ShellClosePreparation()
    {
        var store = new QualityFixture.DocumentStore();
        await using var settings = new AppMediaControlSettings(store);
        await using var appearance = new AppAppearance(store);
        await using var closePreferences = new AppClosePreferences(store);
        await settings.LoadAsync(); await settings.SaveAsync(ShortcutFixture.Preferences());
        await appearance.LoadAsync(); await closePreferences.LoadAsync();
        var prepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var domainEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int domainPreparations = 0;
        var domain = new Preparation(() =>
        {
            domainPreparations++;
            domainEntered.TrySetResult();
            return prepare.Task;
        });
        var module = new SettingsNavigationTests.Module("media-close-probe", "媒体关闭测试");
        AppMediaWindowAttachment? owner = null;
        var shutdown = new AppShutdown([module, domain], [new(() => owner!.DisposeAsync())],
            sharedParticipants: [settings, appearance, closePreferences]);
        var tray = new TrayIcon();
        var shell = new ShellWindow([module], [], shutdown.CloseAsync, shutdown.ForceReleaseAsync,
            shutdown.CancelShutdownPreparationAsync, TimeProvider.System, appearance, closePreferences,
            (_, _, _, _) => tray, mediaControls: settings)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false
        };
        shell.Closed += (_, _) => closed.TrySetResult();
        var native = new ShortcutNative();
        var actions = new List<MediaShortcutAction>();
        SystemFixture? system = null;
        Task? saving = null;
        bool stoppedForPreparation = false, restoredAfterShared = false;
        void AvailabilityChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(IDesktopMediaControlSettings.IsAvailable)) return;
            if (!settings.IsAvailable) stoppedForPreparation = true;
            else if (stoppedForPreparation) restoredAfterShared = appearance.CanEdit && closePreferences.CanEdit;
        }
        settings.PropertyChanged += AvailabilityChanged;
        var commands = Enum.GetValues<SystemPlaybackCommand>();
        var timeout = TimeSpan.FromSeconds(3);
        try
        {
            owner = new AppMediaWindowAttachment(shell, settings);
            system = new SystemFixture(shell);
            owner.OwnSystemMedia(system.Attachment, system.Attachment.SetEnabled);
            owner.OwnShortcuts(new MediaShortcutWindowAttachment(shell, settings, action =>
            {
                actions.Add(action); return Task.CompletedTask;
            }, native));
            shell.Show(); await Pump();
            var handle = new WindowInteropHelper(shell).Handle;
            var dialog = (CloseWindowDialog)shell.FindName("CloseDialog");
            Check(ReferenceEquals(shell.MediaControls, settings) && native.Latest.Success && system.Backend.Enabled,
                "the real shell and both attachments share the restored media settings");
            void Post(ShortcutNative.Call registration)
            {
                var gesture = ShortcutFixture.GlobalGesture;
                nint packed = (nint)((KeyInterop.VirtualKeyFromKey(gesture.Key) << 16) | (int)gesture.Modifiers);
                bool posted = PostMessageW(handle, MediaShortcutNative.HotKeyMessage, registration.Id, packed);
                int error = posted ? 0 : Marshal.GetLastWin32Error();
                Check(posted, $"queued shell hotkey; {ShortcutNative.WindowEvidence(handle)} id=0x{registration.Id:X} error={error}");
            }
            async Task AcceptAll(string stage)
            {
                int calls = system.Player.Calls.Count, shortcuts = actions.Count;
                foreach (var command in commands)
                {
                    system.Backend.Request(command, 30); await Pump();
                    Check(system.Player.Calls.Count == ++calls &&
                        system.Player.Calls.Last() == command.ToString().ToLowerInvariant(), stage + " accepts " + command);
                }
                Post(native.Latest); await Pump();
                Check(actions.Count == shortcuts + 1, stage + " accepts the global shortcut");
            }
            await AcceptAll("initial shell");
            var active = native.Latest;
            shell.Close(); await Pump();
            Check(dialog.IsOpen && settings.IsAvailable && system.Backend.Enabled && native.Latest == active &&
                domainPreparations == 0 && !shell.ShutdownCompletion.IsCompleted,
                "close inquiry does not start preparation or retire either media channel");
            await AcceptAll("close inquiry");
            ((Button)dialog.FindName("MinimizeChoice")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Pump();
            Check(!shell.IsVisible && !dialog.IsOpen && settings.IsAvailable && system.Backend.Enabled &&
                native.Latest == active && domainPreparations == 0 && closePreferences.Behavior == WindowCloseBehavior.Ask,
                "one-time tray hiding retains both channels and does not rewrite the close preference");
            await AcceptAll("hidden shell");
            shell.Show(); await Pump();
            shell.Close(); await Pump();
            Check(dialog.IsOpen, "a fresh close still asks after the one-time tray choice");
            store.HoldNamespace = AppAppearance.DocumentNamespace;
            saving = appearance.SaveAsync("#315A7D");
            await store.Entered.Task.WaitAsync(timeout);
            Check(!saving.IsCompleted && settings.IsAvailable, "a real accepted appearance save is awaiting physical settlement");
            int acceptedSystem = system.Player.Calls.Count, acceptedShortcuts = actions.Count;
            Post(active);
            foreach (var command in commands) system.Backend.Request(command, 30);
            ((Button)dialog.FindName("ExitChoice")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!settings.IsAvailable && !system.Backend.Enabled && native.Unregistered.Contains(active.Id) &&
                !saving.IsCompleted && domainPreparations == 0 && closePreferences.CanEdit,
                "media admission stops synchronously before awaiting appearance and preparing later participants");
            Post(active);
            foreach (var command in commands) system.Backend.Request(command, 30);
            await Pump();
            Check(system.Player.Calls.Count == acceptedSystem && actions.Count == acceptedShortcuts &&
                system.Backend.Disposals == 0 && !saving.IsCompleted && domainPreparations == 0,
                "both queued and new Play/Pause/Stop/Next/Previous/Seek and hotkeys are rejected throughout the shared-save wait");
            store.Release.TrySetResult();
            await saving.WaitAsync(timeout);
            await domainEntered.Task.WaitAsync(timeout);
            Check(!settings.IsAvailable && !appearance.CanEdit && !closePreferences.CanEdit &&
                appearance.ThemeColor == "#315A7D" && store.Data.ContainsKey(AppAppearance.DocumentNamespace),
                "the actual save settles before domain preparation while media admission remains closed");
            prepare.TrySetException(new InvalidOperationException("synthetic-preparation-failure"));
            await shell.ShutdownCompletion.WaitAsync(timeout); await Pump();
            var cancel = (Button)shell.FindName("CancelShutdownButton");
            Check(shell.IsVisible && cancel.IsEnabled && restoredAfterShared,
                "failed preparation remains cancellable and rollback reopens media only after later shared owners");
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Pump();
            Check(settings.IsAvailable && system.Backend.Enabled && native.Latest.Success &&
                appearance.CanEdit && closePreferences.CanEdit && closePreferences.Behavior == WindowCloseBehavior.Ask &&
                ((Grid)shell.FindName("ContentDeck")).IsEnabled,
                "actual shell cancellation restores both channels from unchanged authoritative preferences");
            await AcceptAll("cancelled preparation");
        }
        finally
        {
            store.Release.TrySetResult();
            prepare.TrySetResult();
            if (saving is not null) await saving;
            settings.PropertyChanged -= AvailabilityChanged;
            await shell.AbortAsync();
            await closed.Task.WaitAsync(timeout);
            if (owner is not null) await owner.DisposeAsync();
        }
        Check(system!.Backend.Disposals == 1 && tray.Disposals == 1 && native.Allocated.SequenceEqual(native.Released),
            "shell cleanup releases its single system attachment, tray and all real hotkey registration identities");
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, int message, nint wParam, nint lParam);

    private static async Task Fails(Task task, string code)
    {
        try { await task; throw new InvalidOperationException("Expected failure: " + code); }
        catch (InvalidOperationException error) when (error.Message == code) { }
    }

    private sealed class PreparationWindow : Window
    {
        internal bool CancelClose = true;
        internal int Cancelled;
        internal Action? Prepare;
        protected override void OnClosing(CancelEventArgs args)
        {
            base.OnClosing(args);
            if (!CancelClose) return;
            args.Cancel = true; Cancelled++;
            Prepare?.Invoke();
        }
    }

    private sealed class Preparation(Func<Task> prepare) : IDesktopShutdownParticipant
    {
        public Task PrepareShutdownAsync() => prepare();
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Resource : IAsyncDisposable
    {
        internal int Attempts, Failures;
        public ValueTask DisposeAsync()
        {
            Attempts++;
            return Attempts <= Failures ? ValueTask.FromException(new InvalidOperationException("SYNTHETIC_RELEASE_FAILED")) :
                ValueTask.CompletedTask;
        }
    }

    private sealed class TrayIcon : IDesktopTrayIcon
    {
        internal int Disposals;
        public bool TryShow() => true;
        public void Dispose() => Disposals++;
    }

    private sealed class SystemFixture
    {
        internal readonly Player Player = new();
        internal readonly Backend Backend = new();
        internal readonly SystemMediaWindowAttachment Attachment;
        internal SystemFixture(Window window, bool enabled = true) =>
            Attachment = new(window, new SystemMediaSession(Player, Backend,
                [new("audio", Next: _ => Player.NextAsync(), Previous: _ => Player.PreviousAsync())], window.Dispatcher, enabled));
    }

    private sealed class Player : ISystemPlayback
    {
        public SystemPlaybackState State { get; } = new("audio", "one", "Generated fixture", true, true, 10, 120);
        internal readonly List<string> Calls = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Notify() => PropertyChanged?.Invoke(this, new(null));
        public Task PlayAsync() { Calls.Add("play"); return Task.CompletedTask; }
        public Task PauseAsync() { Calls.Add("pause"); return Task.CompletedTask; }
        public Task StopAsync() { Calls.Add("stop"); return Task.CompletedTask; }
        internal Task NextAsync() { Calls.Add("next"); return Task.CompletedTask; }
        internal Task PreviousAsync() { Calls.Add("previous"); return Task.CompletedTask; }
        public Task SeekAsync(double seconds) { Calls.Add("seek"); return Task.CompletedTask; }
    }

    private sealed class Backend : ISystemMediaBackend
    {
        internal int Updates, Disposals;
        internal bool Enabled;
        public event EventHandler<SystemPlaybackRequest>? Requested;
        internal void Request(SystemPlaybackCommand command, double positionSeconds = 0) => Requested?.Invoke(this, new(command, positionSeconds));
        public void Update(SystemPlaybackState state) { Updates++; Enabled = state.HasMedia; }
        public void Disable() => Enabled = false;
        public void Dispose() => Disposals++;
    }
}

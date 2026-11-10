using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MeowFilm.Desktop;

public sealed class MediaShortcutWindowAttachment : IAsyncDisposable
{
    private sealed record Registration(int Id, MediaShortcutAction Action, MediaShortcutGesture Gesture);
    private sealed class Retirement
    {
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Posted;
    }

    private readonly Window window;
    private readonly HwndSource source;
    private readonly nint hwnd;
    private readonly IDesktopMediaControlSettings settings;
    private readonly Func<MediaShortcutAction, Task> execute;
    private readonly MediaShortcutNative native;
    private readonly NoticeOutlet notice = new();
    private readonly Dictionary<int, Registration> owned = [];
    private readonly Dictionary<int, Registration> active = [];
    private readonly Dictionary<int, Retirement> retiring = [];
    private readonly HashSet<Key> held = [];
    private readonly HashSet<Task> running = [];
    private MediaControlPreferences? observedPreferences;
    private bool observedAvailable, observedRecording;
    private bool stopped, finished;
    private Task? disposing;

    public MediaShortcutWindowAttachment(Window window, IDesktopMediaControlSettings settings,
        Func<MediaShortcutAction, Task> execute) : this(window, settings, execute, new MediaShortcutNative()) { }

    internal MediaShortcutWindowAttachment(Window window, IDesktopMediaControlSettings settings,
        Func<MediaShortcutAction, Task> execute, MediaShortcutNative native)
    {
        window.Dispatcher.VerifyAccess();
        this.window = window; this.settings = settings; this.execute = execute; this.native = native;
        hwnd = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("MEDIA_SHORTCUT_WINDOW_UNAVAILABLE");
        source.AddHook(Message);
        source.Disposed += SourceDisposed;
        window.PreviewKeyDown += KeyDown;
        window.PreviewKeyUp += KeyUp;
        window.Deactivated += Deactivated;
        window.Closed += Closed;
        settings.PropertyChanged += SettingsChanged;
        settings.RegistrationRefreshRequested += RefreshRequested;
        Refresh(force: true);
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or "" or nameof(IDesktopMediaControlSettings.Preferences) or
            nameof(IDesktopMediaControlSettings.IsAvailable) or nameof(IDesktopMediaControlSettings.IsRecording))
            OnDispatcher(() => Refresh(force: false));
    }

    private void RefreshRequested(object? sender, EventArgs args) => OnDispatcher(() => Refresh(force: true));

    private void OnDispatcher(Action action)
    {
        if (stopped || window.Dispatcher.HasShutdownStarted) return;
        if (window.Dispatcher.CheckAccess()) action();
        else _ = window.Dispatcher.InvokeAsync(() => { if (!stopped) action(); });
    }

    private void Refresh(bool force)
    {
        if (stopped) return;
        var preferences = settings.Preferences;
        bool available = settings.IsAvailable, recording = settings.IsRecording;
        if (!force && observedPreferences is { } observed && preferences.GlobalEnabled == observed.GlobalEnabled &&
            preferences.Bindings.SequenceEqual(observed.Bindings) &&
            available == observedAvailable && recording == observedRecording) return;
        observedPreferences = preferences; observedAvailable = available; observedRecording = recording;
        active.Clear();
        bool released = ReleaseRegistrations();
        var errors = new Dictionary<MediaShortcutAction, string>();
        if (released && available && preferences.GlobalEnabled && !recording)
        {
            foreach (var binding in preferences.Bindings)
            {
                if (binding.Global is not { } gesture) continue;
                var allocation = native.AllocateId();
                if (allocation.Id == 0)
                {
                    errors[binding.Action] = "无法分配热键注册标识";
                    continue;
                }
                var result = native.Register(hwnd, allocation.Id, (uint)gesture.Modifiers | MediaShortcutNative.NoRepeat,
                    (uint)KeyInterop.VirtualKeyFromKey(gesture.Key));
                if (result.Success)
                {
                    var registration = new Registration(allocation.Id, binding.Action, gesture);
                    owned.Add(registration.Id, registration);
                    active.Add(registration.Id, registration);
                }
                else
                {
                    native.ReleaseId(allocation.Id);
                    errors[binding.Action] = MediaShortcutNative.RegistrationError(result.Error);
                }
            }
        }
        settings.SetGlobalErrors(errors);
        if (!released) Notify("暂时无法释放媒体快捷键");
    }

    private bool ReleaseRegistrations()
    {
        foreach (var registration in owned.Values.ToArray())
        {
            var result = native.Unregister(hwnd, registration.Id);
            // A destroyed HWND or an already removed registration owns no hotkey.
            if (!result.Success && result.Error is not (1400 or 1419)) continue;
            owned.Remove(registration.Id);
            var retirement = new Retirement();
            retiring.Add(registration.Id, retirement);
            if (source.IsDisposed) CompleteRetirement(registration.Id);
            else retirement.Posted = native.PostRetirement(hwnd, registration.Id);
        }
        return owned.Count == 0;
    }

    private void CompleteRetirement(int id)
    {
        if (!retiring.Remove(id, out var retirement)) return;
        native.ReleaseId(id);
        retirement.Done.TrySetResult();
    }

    private nint Message(nint target, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == MediaShortcutNative.RetirementMessage)
        {
            CompleteRetirement((int)wParam);
            handled = true;
        }
        else if (message == MediaShortcutNative.HotKeyMessage && active.TryGetValue((int)wParam, out var registration))
        {
            handled = true;
            long packed = lParam.ToInt64();
            if (!stopped && settings.IsAvailable && !settings.IsRecording && settings.Preferences.GlobalEnabled &&
                (uint)(packed & 0x0f) == (uint)registration.Gesture.Modifiers &&
                ((packed >> 16) & 0xffff) == KeyInterop.VirtualKeyFromKey(registration.Gesture.Key) &&
                !MediaShortcutInput.ProtectsForeground(window, source, native))
                Start(registration.Action);
        }
        return 0;
    }

    private void KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Handled || stopped || !settings.IsAvailable || settings.IsRecording ||
            native.ForegroundWindow != hwnd || native.HasMenuOrChildFocus(hwnd) || InputManager.Current.IsInMenuMode ||
            !MediaShortcutInput.AllowsLocal(window, source, args.OriginalSource)) return;
        var key = MediaShortcutInput.Key(args);
        if (held.Contains(key)) { args.Handled = true; return; }
        var gesture = new MediaShortcutGesture(key, MediaShortcutInput.Modifiers(args.KeyboardDevice));
        var binding = settings.Preferences.Bindings.FirstOrDefault(item => item.Local == gesture);
        if (binding is null) return;
        held.Add(key);
        args.Handled = true;
        // RegisterHotKey is authoritative for an overlapping gesture, regardless
        // of whether WM_HOTKEY arrives before or after WPF's key event.
        if (!args.IsRepeat && !active.Values.Any(item => item.Action == binding.Action && item.Gesture == gesture))
            Start(binding.Action);
    }

    private void KeyUp(object sender, KeyEventArgs args)
    {
        if (held.Remove(MediaShortcutInput.Key(args))) args.Handled = true;
    }

    private void Deactivated(object? sender, EventArgs args) => held.Clear();

    private void Start(MediaShortcutAction action)
    {
        // Record admission before invoking the owner: a synchronous callback may
        // itself start window shutdown.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running.Add(completion.Task);
        _ = Execute(action, completion);
    }

    private async Task Execute(MediaShortcutAction action, TaskCompletionSource completion)
    {
        try { await execute(action); }
        catch (OperationCanceledException) { }
        catch { if (!stopped) Notify("暂时无法完成媒体操作"); }
        finally { running.Remove(completion.Task); completion.TrySetResult(); }
    }

    private void Notify(string message) => window.RaiseEvent(new DesktopNoticeEventArgs(notice, message));

    private void Closed(object? sender, EventArgs args) => Stop();

    private void Stop()
    {
        if (stopped) return;
        stopped = true;
        active.Clear(); held.Clear();
        window.PreviewKeyDown -= KeyDown;
        window.PreviewKeyUp -= KeyUp;
        window.Deactivated -= Deactivated;
        window.Closed -= Closed;
        settings.PropertyChanged -= SettingsChanged;
        settings.RegistrationRefreshRequested -= RefreshRequested;
        ReleaseRegistrations();
        settings.SetGlobalErrors(new Dictionary<MediaShortcutAction, string>());
        Notify("");
    }

    private void SourceDisposed(object? sender, EventArgs args)
    {
        Stop();
        ReleaseRegistrations();
        foreach (int id in retiring.Keys.ToArray()) CompleteRetirement(id);
    }

    public ValueTask DisposeAsync()
    {
        window.Dispatcher.VerifyAccess();
        if (finished) return ValueTask.CompletedTask;
        if (disposing is null || disposing.IsFaulted) disposing = DisposeCore();
        return new(disposing);
    }

    private async Task DisposeCore()
    {
        Stop();
        await Task.WhenAll(running.ToArray());
        if (!ReleaseRegistrations()) throw new InvalidOperationException("MEDIA_SHORTCUT_RELEASE_FAILED");
        foreach (var pair in retiring.ToArray())
        {
            if (source.IsDisposed) CompleteRetirement(pair.Key);
            else if (!pair.Value.Posted)
            {
                pair.Value.Posted = native.PostRetirement(hwnd, pair.Key);
                if (!pair.Value.Posted) throw new InvalidOperationException("MEDIA_SHORTCUT_RELEASE_FAILED");
            }
        }
        await Task.WhenAll(retiring.Values.Select(item => item.Done.Task).ToArray());
        source.Disposed -= SourceDisposed;
        if (!source.IsDisposed) source.RemoveHook(Message);
        finished = true;
    }
}

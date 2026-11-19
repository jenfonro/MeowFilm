using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Key = Windows.System.VirtualKey;

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
    private readonly FrameworkElement root;
    private readonly nint hwnd;
    private static long nextSubclassId;
    private readonly nuint subclassId = (nuint)Interlocked.Increment(ref nextSubclassId);
    private readonly MediaShortcutNative.Subclass callback;
    private readonly KeyEventHandler keyUpHandler;
    private GCHandle callbackRoot;
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
    private bool stopped, finished, destroyed;
    private Task? disposing;

    public MediaShortcutWindowAttachment(Window window, IDesktopMediaControlSettings settings,
        Func<MediaShortcutAction, Task> execute) : this(window, settings, execute, new MediaShortcutNative()) { }

    internal MediaShortcutWindowAttachment(Window window, IDesktopMediaControlSettings settings,
        Func<MediaShortcutAction, Task> execute, MediaShortcutNative native)
    {
        if (!window.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("MEDIA_SHORTCUT_THREAD_ACCESS");
        this.window = window; this.settings = settings; this.execute = execute; this.native = native;
        root = window.Content as FrameworkElement ?? throw new InvalidOperationException("MEDIA_SHORTCUT_ROOT_UNAVAILABLE");
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        callback = WindowMessage;
        keyUpHandler = KeyUp;
        callbackRoot = GCHandle.Alloc(callback);
        if (!MediaShortcutNative.Attach(hwnd, callback, subclassId))
        {
            callbackRoot.Free();
            throw new InvalidOperationException("MEDIA_SHORTCUT_WINDOW_UNAVAILABLE");
        }
        root.PreviewKeyDown += KeyDown;
        root.AddHandler(UIElement.PreviewKeyUpEvent, keyUpHandler, true);
        window.Activated += Activated;
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
        if (stopped) return;
        if (window.DispatcherQueue.HasThreadAccess) action();
        else window.DispatcherQueue.TryEnqueue(() => { if (!stopped) action(); });
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
                var result = native.Register(hwnd, allocation.Id, MediaShortcutInput.NativeModifiers(gesture.Modifiers) | MediaShortcutNative.NoRepeat,
                    (uint)(int)(gesture.Key));
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
            if (!destroyed && !result.Success && result.Error is not (1400 or 1419)) continue;
            owned.Remove(registration.Id);
            var retirement = new Retirement();
            retiring.Add(registration.Id, retirement);
            if (destroyed) CompleteRetirement(registration.Id);
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

    private nint WindowMessage(nint target, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == MediaShortcutNative.DestroyMessage)
        {
            destroyed = true;
            Stop();
            ReleaseRegistrations();
            foreach (int registration in retiring.Keys.ToArray()) CompleteRetirement(registration);
            MediaShortcutNative.Detach(hwnd, callback, subclassId);
            if (callbackRoot.IsAllocated) callbackRoot.Free();
        }
        else
        {
            bool handled = false;
            var result = Message(target, (int)message, (nint)wParam, lParam, ref handled);
            if (handled) return result;
        }
        return MediaShortcutNative.Continue(target, message, wParam, lParam);
    }

    private nint Message(nint target, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == MediaShortcutNative.RetirementMessage && retiring.ContainsKey((int)wParam))
        {
            CompleteRetirement((int)wParam);
            handled = true;
        }
        else if (message == MediaShortcutNative.HotKeyMessage && active.TryGetValue((int)wParam, out var registration))
        {
            handled = true;
            long packed = lParam.ToInt64();
            if (!stopped && settings.IsAvailable && !settings.IsRecording && settings.Preferences.GlobalEnabled &&
                (uint)(packed & 0xffff) == MediaShortcutInput.NativeModifiers(registration.Gesture.Modifiers) &&
                ((packed >> 16) & 0xffff) == (int)(registration.Gesture.Key) &&
                !MediaShortcutInput.ProtectsForeground(root, hwnd, native))
                Start(registration.Action);
        }
        return 0;
    }

    private void KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!args.Handled && ProcessKey(args.OriginalSource, MediaShortcutInput.Key(args),
            MediaShortcutInput.Modifiers(), down: true, args.KeyStatus.WasKeyDown)) args.Handled = true;
    }

    private void KeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (ProcessKey(args.OriginalSource, MediaShortcutInput.Key(args),
            MediaShortcutInput.Modifiers(), down: false, repeat: false)) args.Handled = true;
    }

    internal bool ProcessKey(object? target, Key key, Windows.System.VirtualKeyModifiers modifiers, bool down, bool repeat)
    {
        if (!down) return held.Remove(key);
        if (stopped || !settings.IsAvailable || settings.IsRecording ||
            native.ForegroundWindow != hwnd || native.HasMenuOrChildFocus(hwnd, root) ||
            !MediaShortcutInput.AllowsLocal(root, target)) return false;
        if (held.Contains(key)) return true;
        var gesture = new MediaShortcutGesture(key, modifiers);
        var binding = settings.Preferences.Bindings.FirstOrDefault(item => item.Local == gesture);
        if (binding is null) return false;
        held.Add(key);
        // RegisterHotKey remains authoritative when the local gesture overlaps.
        if (!repeat && !active.Values.Any(item => item.Action == binding.Action && item.Gesture == gesture))
            Start(binding.Action);
        return true;
    }

    private void Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) held.Clear();
    }

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

    private void Notify(string message) => NoticeOutlet.Publish(root, new DesktopNoticeEventArgs(notice, message));

    private void Closed(object sender, WindowEventArgs args) => Stop();

    private void Stop()
    {
        if (stopped) return;
        stopped = true;
        active.Clear(); held.Clear();
        root.PreviewKeyDown -= KeyDown;
        root.RemoveHandler(UIElement.PreviewKeyUpEvent, keyUpHandler);
        window.Activated -= Activated;
        window.Closed -= Closed;
        settings.PropertyChanged -= SettingsChanged;
        settings.RegistrationRefreshRequested -= RefreshRequested;
        ReleaseRegistrations();
        settings.SetGlobalErrors(new Dictionary<MediaShortcutAction, string>());
        Notify("");
    }

    public ValueTask DisposeAsync()
    {
        if (!window.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("MEDIA_SHORTCUT_THREAD_ACCESS");
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
            if (destroyed) CompleteRetirement(pair.Key);
            else if (!pair.Value.Posted)
            {
                pair.Value.Posted = native.PostRetirement(hwnd, pair.Key);
                if (!pair.Value.Posted) throw new InvalidOperationException("MEDIA_SHORTCUT_RELEASE_FAILED");
            }
        }
        await Task.WhenAll(retiring.Values.Select(item => item.Done.Task).ToArray());
        if (!destroyed && !MediaShortcutNative.Detach(hwnd, callback, subclassId))
            throw new InvalidOperationException("MEDIA_SHORTCUT_RELEASE_FAILED");
        if (callbackRoot.IsAllocated) callbackRoot.Free();
        finished = true;
    }
}

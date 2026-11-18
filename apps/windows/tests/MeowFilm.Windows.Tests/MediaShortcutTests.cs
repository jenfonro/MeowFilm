using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Key = System.Windows.Input.Key;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using KeyInterop = System.Windows.Input.KeyInterop;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MediaShortcutTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("real HWND conflict releases and recovers on explicit refresh", Conflict);
        yield return Case("disabled recording unavailable and closing release without probing", RegistrationLifetime);
        yield return Case("unchanged state and error notifications preserve queued hotkeys", UnchangedState);
        yield return Case("rebind and disable reopen reject old native messages", StaleMessages);
        yield return Case("overlapping local and global gestures execute once in either event order", Overlap);
        yield return Case("failed global registration retains the local gesture", LocalFallback);
        yield return Case("local editors menus popup repeat recording and foreground boundaries", LocalInput);
        yield return Case("global foreground ownership ignores stale background editor focus", GlobalInput);
        yield return Case("all eight local actions route without a music owner", AllActions);
        yield return Case("registration errors distinguish occupation and other native failures", Errors);
        yield return Case("disposal releases keys but waits for accepted actions to settle", Drain);
        yield return Case("owner failures use a fixed window notification", Failure);
    }

    internal static TestCase Case(string name, Func<Task> action) => new("media-shortcut." + name, () => Sta(action));

    private static async Task Conflict()
    {
        await using var first = new ShortcutFixture();
        await using var second = new ShortcutFixture();
        first.Attach(); second.Attach();
        Check(first.Settings.GlobalErrors.Count == 0 && first.Native.Latest.Success, "first real registration");
        Check(second.Settings.GlobalErrors[MediaShortcutAction.TogglePlayback] == "热键被占用", "real 1409 conflict");
        Check((first.Native.Latest.Modifiers & MediaShortcutNative.NoRepeat) != 0, "native no-repeat registration");
        await first.Attachment!.DisposeAsync();
        second.Settings.RequestRegistrationRefresh();
        Check(second.Settings.GlobalErrors.Count == 0 && second.Native.Latest.Success, "release and explicit re-registration");
        second.Post(second.Native.Latest); await Pump();
        Check(second.Actions.SequenceEqual([MediaShortcutAction.TogglePlayback]), "registered attachment consumes its native message payload");
    }

    private static async Task RegistrationLifetime()
    {
        await using var first = new ShortcutFixture(ShortcutFixture.Preferences(global: false));
        first.Attach();
        first.Settings.RequestRegistrationRefresh();
        Check(first.Native.Calls.Count == 0 && first.Native.Allocated.Count == 0, "off never probes");
        first.Settings.SetPreferences(first.Settings.Preferences with { GlobalEnabled = true });
        var old = first.Native.Latest;
        first.Settings.SetRecording(true);
        Check(first.Native.Unregistered.Contains(old.Id) && first.Settings.GlobalErrors.Count == 0, "recording releases");
        first.Settings.RequestRegistrationRefresh();
        first.Post(old); await Pump();
        Check(first.Native.Calls.Count == 1 && first.Actions.Count == 0, "recording never registers or executes");
        await using var second = new ShortcutFixture();
        second.Attach();
        Check(second.Native.Latest.Success, "other owner acquires while recording");
        first.Settings.SetRecording(false);
        Check(first.Settings.GlobalErrors[MediaShortcutAction.TogglePlayback] == "热键被占用", "recording exit really registers");
        await second.Attachment!.DisposeAsync();
        first.Settings.RequestRegistrationRefresh();
        Check(first.Settings.GlobalErrors.Count == 0 && first.Native.Latest.Success, "refresh recovers");
        first.Settings.SetAvailable(false);
        int registrations = first.Native.Calls.Count;
        first.Settings.RequestRegistrationRefresh();
        Check(first.Native.Calls.Count == registrations && first.Settings.GlobalErrors.Count == 0, "unavailable never probes");
        first.Settings.SetAvailable(true);
        Check(first.Native.Calls.Count == registrations + 1 && first.Native.Latest.Success, "available registers");
        first.Window.Close();
        Check(first.Native.Unregistered.Contains(first.Native.Latest.Id) && first.Settings.GlobalErrors.Count == 0, "closing releases immediately");
        registrations = first.Native.Calls.Count;
        first.Settings.SetRecording(true); first.Settings.SetRecording(false);
        first.Settings.RequestRegistrationRefresh();
        Check(first.Native.Calls.Count == registrations, "closed attachment cannot reopen");
    }

    private static async Task UnchangedState()
    {
        await using var fixture = new ShortcutFixture();
        fixture.Attach();
        var registration = fixture.Native.Latest;
        fixture.Post(registration);
        fixture.Settings.NotifyUnchangedState();
        fixture.Settings.SetPreferences(fixture.Settings.Preferences with { Bindings = fixture.Settings.Preferences.Bindings.ToArray() });
        fixture.Settings.SetGlobalErrors(new Dictionary<MediaShortcutAction, string>
            { [MediaShortcutAction.TogglePlayback] = "测试注册状态" });
        await Pump();
        Check(fixture.Native.Calls.Count == 1 && fixture.Native.Unregistered.Count == 0, "busy status and errors do not re-register");
        Check(fixture.Actions.Count == 1, "unchanged notifications do not invalidate a queued native message");
    }

    private static async Task StaleMessages()
    {
        await using var fixture = new ShortcutFixture();
        fixture.Attach();
        var old = fixture.Native.Latest;
        fixture.Post(old);
        fixture.Settings.SetPreferences(ShortcutFixture.Preferences(key: Key.F23));
        var current = fixture.Native.Latest;
        Check(old.Id != current.Id && !fixture.Native.Released.Contains(old.Id), "retired ID stays owned until queue barrier");
        fixture.Post(old);
        fixture.Post(current, gesture: ShortcutFixture.GlobalGesture);
        fixture.Post(current);
        await Pump();
        Check(fixture.Actions.Count == 1 && fixture.Native.Released.Contains(old.Id), "old ID and old gesture do not reach new binding");
        fixture.Post(current);
        fixture.Settings.SetPreferences(fixture.Settings.Preferences with { GlobalEnabled = false });
        fixture.Settings.SetPreferences(fixture.Settings.Preferences with { GlobalEnabled = true });
        var reopened = fixture.Native.Latest;
        Check(reopened.Id != current.Id, "same key reopen uses a fresh live ID");
        fixture.Post(reopened); await Pump();
        Check(fixture.Actions.Count == 2, "disabled generation never revives");
    }

    private static async Task Overlap()
    {
        await using var fixture = new ShortcutFixture(ShortcutFixture.Preferences(local: ShortcutFixture.GlobalGesture));
        fixture.Native.Foreground = fixture.Handle;
        fixture.Attach();
        var registration = fixture.Native.Latest;
        Check(fixture.SendKey(fixture.Button, ShortcutFixture.GlobalGesture).Handled && fixture.Actions.Count == 0, "WinUI defers registered overlap");
        fixture.Post(registration); await Pump();
        Check(fixture.Actions.Count == 1, "WinUI then WM_HOTKEY executes once");
        Check(fixture.SendKey(fixture.Button, ShortcutFixture.GlobalGesture, down: false).Handled, "paired release consumed");
        fixture.Post(registration); await Pump();
        fixture.SendKey(fixture.Button, ShortcutFixture.GlobalGesture);
        fixture.SendKey(fixture.Button, ShortcutFixture.GlobalGesture, repeat: true);
        fixture.SendKey(fixture.Button, ShortcutFixture.GlobalGesture, down: false);
        Check(fixture.Actions.Count == 2, "WM_HOTKEY then WinUI executes once");
        fixture.Settings.SetPreferences(fixture.Settings.Preferences with { GlobalEnabled = false });
        fixture.SendKey(fixture.Button, ShortcutFixture.GlobalGesture);
        fixture.SendKey(fixture.Button, ShortcutFixture.GlobalGesture, down: false);
        Check(fixture.Actions.Count == 3, "local resumes after global off");
    }

    private static async Task LocalFallback()
    {
        await using var owner = new ShortcutFixture(); owner.Attach();
        await using var fixture = new ShortcutFixture(ShortcutFixture.Preferences(local: ShortcutFixture.GlobalGesture));
        fixture.Native.Foreground = fixture.Handle; fixture.Attach();
        Check(fixture.Settings.GlobalErrors.Count == 1, "real conflict");
        fixture.SendKey(fixture.Button, ShortcutFixture.GlobalGesture);
        fixture.SendKey(fixture.Button, ShortcutFixture.GlobalGesture, down: false);
        Check(fixture.Actions.Count == 1, "unsuccessful global registration never swallows local action");
    }

    private static async Task LocalInput()
    {
        var gesture = new MediaShortcutGesture(Key.P, ModifierKeys.Control);
        await using var fixture = new ShortcutFixture(ShortcutFixture.Preferences(global: false, local: gesture));
        fixture.Native.Foreground = fixture.Handle; fixture.Attach();
        UIElement[] editors = [new TextBox(), new RichEditBox(), new PasswordBox(), new ComboBox { IsEditable = true }, new MenuFlyoutItem()];
        foreach (var editor in editors)
        {
            fixture.Root.Children.Add(editor);
            Check(!fixture.SendKey(editor, gesture).Handled, "editor and menu input remains unhandled");
            fixture.SendKey(editor, gesture, down: false);
        }
        var popupTarget = new Button();
        var popup = new Popup { Child = popupTarget, XamlRoot = fixture.Root.XamlRoot };
        fixture.Root.Children.Add(popup);
        Check(!fixture.SendKey(popupTarget, gesture).Handled && fixture.Actions.Count == 0, "popup does not route into main-window input");
        fixture.SendKey(fixture.Button, gesture);
        for (int i = 0; i < 4; i++)
            Check(fixture.SendKey(fixture.Button, gesture, repeat: true).Handled, "held repeats are consumed");
        Check(fixture.SendKey(fixture.Button, gesture, down: false).Handled && fixture.Actions.Count == 1, "one press one action");
        fixture.SendKey(fixture.Button, gesture, repeat: true);
        fixture.SendKey(fixture.Button, gesture, down: false);
        Check(fixture.Actions.Count == 1, "repeat entering a window is not a new press");
        fixture.Settings.SetRecording(true);
        Check(!fixture.SendKey(fixture.Button, gesture).Handled && fixture.Actions.Count == 1, "recorder owns its keys");
        Check(!fixture.SendKey(fixture.Button, gesture, down: false).Handled && fixture.Settings.IsRecording,
            "candidate release remains with the recorder until it explicitly ends recording");
        fixture.Settings.SetRecording(false);
        fixture.Native.Menu = true;
        Check(!fixture.SendKey(fixture.Button, gesture).Handled, "native menu owns input");
        fixture.Native.Menu = false;
        fixture.Native.Foreground = 0;
        Check(!fixture.SendKey(fixture.Button, gesture).Handled, "background cannot admit local input");
        await Pump();
        Check(fixture.Actions.Count == 1, "protected inputs never execute");
        fixture.Native.Foreground = fixture.Handle;
        var windowsGesture = gesture with { Modifiers = ModifierKeys.Control | ModifierKeys.Windows };
        Check(!fixture.SendKey(fixture.Button, windowsGesture).Handled, "additional Windows modifier cannot invoke a Control-only binding");
        fixture.SendKey(fixture.Button, windowsGesture, down: false);
        fixture.Settings.SetPreferences(ShortcutFixture.Preferences(global: false, local: windowsGesture));
        Check(fixture.SendKey(fixture.Button, windowsGesture).Handled, "explicit Windows modifier binding routes");
        fixture.SendKey(fixture.Button, windowsGesture, down: false);
        Check(fixture.Actions.Count == 2, "Windows modifier is not lost by WinUI's modifier projection");
        Check(fixture.SendKey(fixture.Button, windowsGesture, rightWindows: true).Handled, "right Windows modifier binding routes");
        fixture.SendKey(fixture.Button, windowsGesture, down: false, rightWindows: true);
        Check(fixture.Actions.Count == 3, "either Windows key can satisfy the explicit modifier");
    }

    private static async Task GlobalInput()
    {
        await using var fixture = new ShortcutFixture(); fixture.Attach();
        var editor = new TextBox(); fixture.Root.Children.Add(editor);
        fixture.Native.Focus = editor;
        fixture.Native.Foreground = fixture.Handle;
        fixture.Post(fixture.Native.Latest); await Pump();
        Check(fixture.Actions.Count == 0, "own foreground editor protected");
        await using var other = new ShortcutFixture(ShortcutFixture.Preferences(global: false));
        fixture.Native.Foreground = other.Handle;
        fixture.Native.Menu = true;
        fixture.Post(fixture.Native.Latest); await Pump();
        Check(fixture.Actions.Count == 1, "foreign foreground accepts global key despite retained search focus or foreign menu");
        fixture.Native.Focus = new PasswordBox();
        fixture.Post(fixture.Native.Latest); await Pump();
        Check(fixture.Actions.Count == 2, "foreign editor does not disable background control");
        fixture.Native.Foreground = fixture.Handle;
        fixture.Native.Focus = fixture.Button;
        fixture.Post(fixture.Native.Latest); await Pump();
        Check(fixture.Actions.Count == 2, "own foreground native menu protected");
        fixture.Native.Menu = false;
        using var popup = new DesktopTestUi.Host(new Grid(), 100, 80);
        SetWindowLongPtr(popup.Window.Handle, -8, fixture.Handle);
        Check(fixture.Native.OwnsWindow(fixture.Handle, popup.Window.Handle), "fixture is a real owned popup HWND");
        fixture.Native.Foreground = popup.Window.Handle;
        fixture.Post(fixture.Native.Latest); await Pump();
        Check(fixture.Actions.Count == 2, "owned foreground popup protected without stale-focus guesses");
    }

    private static async Task AllActions()
    {
        await using var fixture = new ShortcutFixture(MediaControlDefaults.Preferences with { GlobalEnabled = false });
        fixture.Native.Foreground = fixture.Handle; fixture.Attach();
        foreach (var binding in fixture.Settings.Preferences.Bindings)
        {
            Check(binding.Local is not null && fixture.SendKey(fixture.Button, binding.Local).Handled, "configured local action accepted");
            fixture.SendKey(fixture.Button, binding.Local!, down: false);
        }
        Check(fixture.Actions.SequenceEqual(Enum.GetValues<MediaShortcutAction>()) && fixture.Actions.Count == 8, "exact eight actions");
    }

    private static async Task Errors()
    {
        foreach (var pair in new (int Code, string Message)[]
        {
            (1409, "热键被占用"), (5, "系统拒绝注册热键"), (1400, "窗口不可用，无法注册热键"),
            (87, "快捷键无效，无法注册"), (1422, "快捷键无效，无法注册"),
            (8, "系统资源不足，无法注册热键"), (1450, "系统资源不足，无法注册热键"),
            (12345, "热键注册失败"), (0, "热键注册失败")
        })
        {
            await using var fixture = new ShortcutFixture();
            fixture.Native.RegisterError = pair.Code; fixture.Attach();
            Check(fixture.Settings.GlobalErrors[MediaShortcutAction.TogglePlayback] == pair.Message, "accurate fixed error classification");
            Check(fixture.Native.Allocated.SequenceEqual(fixture.Native.Released), "failed registration releases its native atom");
        }
        await using var allocation = new ShortcutFixture();
        allocation.Native.AllocateError = 8; allocation.Attach();
        Check(allocation.Settings.GlobalErrors[MediaShortcutAction.TogglePlayback] == "无法分配热键注册标识" &&
            allocation.Native.Calls.Count == 0 && allocation.Native.Allocated.Count == 0, "ID allocation failure is not a RegisterHotKey occupation");
    }

    private static async Task Drain()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ShortcutFixture(execute: _ => release.Task);
        fixture.Attach(); fixture.Post(fixture.Native.Latest); await Pump();
        Check(fixture.Actions.Count == 1, "action admitted");
        var disposal = fixture.Attachment!.DisposeAsync().AsTask();
        try
        {
            Check(!disposal.IsCompleted && fixture.Native.Unregistered.Contains(fixture.Native.Latest.Id), "release precedes actual action settle");
            await using var competitor = new ShortcutFixture(); competitor.Attach();
            Check(competitor.Native.Latest.Success, "key available during owner drain");
            fixture.Settings.RequestRegistrationRefresh();
            fixture.Settings.SetRecording(true); fixture.Settings.SetRecording(false);
            Check(fixture.Native.Calls.Count == 1 && fixture.Actions.Count == 1, "disposal closes admission permanently");
        }
        finally { release.TrySetResult(); await disposal; }
        await fixture.Attachment.DisposeAsync();
    }

    private static async Task Failure()
    {
        var gesture = new MediaShortcutGesture(Key.P, ModifierKeys.Control);
        await using var fixture = new ShortcutFixture(ShortcutFixture.Preferences(global: false, local: gesture),
            _ => throw new InvalidOperationException("private-native-provider-text"));
        var messages = new List<string>();
        NoticeOutlet.SetReceiver(fixture.Root, args => messages.Add(args.Message));
        fixture.Native.Foreground = fixture.Handle; fixture.Attach();
        fixture.SendKey(fixture.Button, gesture); fixture.SendKey(fixture.Button, gesture, down: false);
        await Pump();
        Check(messages.Contains("暂时无法完成媒体操作") &&
            messages.All(message => !message.Contains("private-native-provider-text", StringComparison.Ordinal)), "fixed existing window notice");
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    internal static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Media shortcut assertion: " + message);
    }

    internal static async Task Pump()
    {
        await DesktopTestUi.DrainAsync();
        await Task.Delay(10);
        await DesktopTestUi.DrainAsync();
    }

    internal static async Task Sta(Func<Task> action)
    { await TestUiHost.InvokeAsync(action); }
}

internal sealed class ShortcutFixture : IAsyncDisposable
{
    internal static readonly MediaShortcutGesture GlobalGesture = new(Key.F24, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
    internal readonly Window Window;
    internal readonly Grid Root = new();
    internal readonly Button Button = new() { Content = "fixture" };
    internal readonly ShortcutSettings Settings;
    internal readonly ShortcutNative Native = new();
    internal readonly List<MediaShortcutAction> Actions = [];
    internal readonly nint Handle;
    internal MediaShortcutWindowAttachment? Attachment;
    private readonly Func<MediaShortcutAction, Task>? execute;

    internal ShortcutFixture(MediaControlPreferences? preferences = null, Func<MediaShortcutAction, Task>? execute = null,
        Window? window = null)
    {
        Window = window ?? new Window();
        Window.Resize(300, 180);
        Settings = new(preferences ?? Preferences()); this.execute = execute;
        Root.Children.Add(Button); Window.Content = Root;
        Window.AttachRoot(); Window.Activate();
        Handle = Window.Handle;
    }

    internal static MediaControlPreferences Preferences(bool global = true, MediaShortcutGesture? local = null, Key key = Key.F24) =>
        new(global, true, MediaControlDefaults.Preferences.Bindings.Select(binding =>
            new MediaShortcutBinding(binding.Action, binding.Action == MediaShortcutAction.TogglePlayback ? local : null,
                binding.Action == MediaShortcutAction.TogglePlayback ? GlobalGesture with { Key = key } : null)).ToArray());

    internal void Attach(IDesktopMediaControlSettings? settings = null) => Attachment = new(Window, settings ?? Settings, action =>
    {
        Actions.Add(action);
        return execute?.Invoke(action) ?? Task.CompletedTask;
    }, Native);

    internal readonly record struct InputResult(bool Handled);
    internal InputResult SendKey(UIElement target, MediaShortcutGesture gesture, bool down = true, bool repeat = false,
        bool rightWindows = false)
    {
        // Exercise the real input owner without synthesizing an OS or XAML input event.
        var modifiers = MediaShortcutInput.Modifiers(gesture.Modifiers.HasFlag(ModifierKeys.Control),
            gesture.Modifiers.HasFlag(ModifierKeys.Alt), gesture.Modifiers.HasFlag(ModifierKeys.Shift),
            gesture.Modifiers.HasFlag(ModifierKeys.Windows) && !rightWindows,
            gesture.Modifiers.HasFlag(ModifierKeys.Windows) && rightWindows);
        MediaShortcutTests.Check(modifiers == gesture.Modifiers, "both Windows keys preserve the modifier projection");
        var recorder = DesktopTestUi.Visuals(Root).OfType<MediaControlSettingsView>().FirstOrDefault();
        bool handled = recorder?.ProcessKey(target, gesture.Key, modifiers, down, repeat) == true;
        if (!handled && Attachment is not null)
            handled = Attachment.ProcessKey(target, gesture.Key, gesture.Modifiers, down, repeat);
        return new(handled);
    }

    internal void Post(ShortcutNative.Call call, MediaShortcutGesture? gesture = null)
    {
        gesture ??= new(KeyInterop.KeyFromVirtualKey((int)call.Key), (ModifierKeys)(call.Modifiers & 0xf));
        nint packed = (nint)((KeyInterop.VirtualKeyFromKey(gesture.Key) << 16) | (int)gesture.Modifiers);
        MediaShortcutTests.Check(PostMessageW(Handle, MediaShortcutNative.HotKeyMessage, call.Id, packed),
            "the native message queue delivers the payload to the actual window attachment");
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, int message, nint wParam, nint lParam);

    public async ValueTask DisposeAsync()
    {
        if (Attachment is not null) await Attachment.DisposeAsync();
        Window.Close();
    }

}

internal sealed class ShortcutNative : MediaShortcutNative
{
    internal sealed record Call(int Id, uint Modifiers, uint Key, bool Success, int Error);
    internal readonly List<Call> Calls = [];
    internal readonly List<int> Allocated = [], Released = [], Unregistered = [];
    internal Call Latest => Calls.Last();
    internal int? RegisterError;
    internal int? AllocateError;
    internal int? UnregisterError;
    internal nint Foreground;
    internal DependencyObject? Focus;
    internal bool Menu;
    public override nint ForegroundWindow => Foreground;
    public override DependencyObject? FocusedElement(FrameworkElement root) => Focus;
    public override bool HasMenuOrChildFocus(nint window, FrameworkElement root) => Menu;
    public override (int Id, int Error) AllocateId()
    {
        var result = AllocateError is { } error ? (Id: 0, Error: error) : base.AllocateId();
        Console.WriteLine($"NATIVE shortcut allocate id=0x{result.Id:X} error={result.Error} injected={AllocateError.HasValue}");
        if (result.Id != 0) Allocated.Add(result.Id);
        return result;
    }
    public override void ReleaseId(int id) { Released.Add(id); base.ReleaseId(id); }
    public override (bool Success, int Error) Register(nint hwnd, int id, uint modifiers, uint key)
    {
        var result = RegisterError is { } error ? (Success: false, Error: error) : base.Register(hwnd, id, modifiers, key);
        Calls.Add(new(id, modifiers, key, result.Success, result.Error));
        Console.WriteLine($"NATIVE shortcut register {WindowEvidence(hwnd)} id=0x{id:X} mod=0x{modifiers:X} vk=0x{key:X} " +
            $"success={result.Success} error={result.Error} injected={RegisterError.HasValue}");
        return result;
    }
    public override (bool Success, int Error) Unregister(nint hwnd, int id)
    {
        var result = UnregisterError is { } error ? (Success: false, Error: error) : base.Unregister(hwnd, id);
        Console.WriteLine($"NATIVE shortcut unregister {WindowEvidence(hwnd)} id=0x{id:X} " +
            $"success={result.Success} error={result.Error} injected={UnregisterError.HasValue}");
        if (result.Success) Unregistered.Add(id);
        return result;
    }

    internal static string WindowEvidence(nint hwnd)
    {
        uint ownerThread = GetWindowThreadProcessId(hwnd, out uint ownerProcess);
        return $"hwnd=0x{hwnd.ToInt64():X} valid={IsWindow(hwnd)} visible={IsWindowVisible(hwnd)} " +
            $"ownerThread={ownerThread} currentThread={GetCurrentThreadId()} ownerProcess={ownerProcess} currentProcess={Environment.ProcessId} " +
            $"apartment={Thread.CurrentThread.GetApartmentState()}";
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}

internal sealed class ShortcutSettings(MediaControlPreferences preferences) : IDesktopMediaControlSettings
{
    public MediaControlPreferences Preferences { get; private set; } = preferences;
    public bool IsAvailable { get; private set; } = true;
    public bool CanEdit => IsAvailable && !IsRecording;
    public string Status => "";
    public bool IsRecording { get; private set; }
    public IReadOnlyDictionary<MediaShortcutAction, string> GlobalErrors { get; private set; } = new Dictionary<MediaShortcutAction, string>();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? RegistrationRefreshRequested;
    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync(MediaControlPreferences value) { SetPreferences(value); return Task.CompletedTask; }
    internal void SetPreferences(MediaControlPreferences value) { Preferences = value; Changed(nameof(Preferences)); }
    internal void SetAvailable(bool value) { IsAvailable = value; Changed(nameof(IsAvailable)); }
    public void SetRecording(bool value) { IsRecording = value; Changed(nameof(IsRecording)); }
    public void SetGlobalErrors(IReadOnlyDictionary<MediaShortcutAction, string> value) { GlobalErrors = value; Changed(nameof(GlobalErrors)); }
    public void RequestRegistrationRefresh() => RegistrationRefreshRequested?.Invoke(this, EventArgs.Empty);
    internal void NotifyUnchangedState()
    {
        foreach (string name in new[] { nameof(IsAvailable), nameof(CanEdit), nameof(Status), nameof(Preferences), nameof(IsRecording), "" })
            Changed(name);
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

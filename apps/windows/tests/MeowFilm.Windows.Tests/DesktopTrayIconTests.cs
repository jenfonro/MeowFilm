using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;
using Windows.Foundation;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class DesktopTrayIconTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("one registration on shell HWND survives repeated show and releases exactly once", Lifetime);
        yield return Case("add version and lost-icon failures roll back ownership and allow recovery", Failures);
        yield return Case("Explorer recreation coalesces recovery and restores an accessible shell on failure", ExplorerRestart);
        yield return Case("native and menu commands use UI dispatcher and retire queued callbacks on owner close", Callbacks);
        yield return Case("menu activation failure preserves hidden-app recovery and signed screen coordinates", MenuUnavailable);
        yield return Case("system menu preserves both labeled commands dismissal focus and keyboard positioning", MenuContract);
        yield return Case("verified desktop opens native tray menu while shell stays hidden and cancels without input", MenuLifecycle);
        yield return Case("native icon acquisition copies borrowed window icons without releasing their owner", WindowIcon);
        yield return Case("unpackaged window can acquire its executable icon before it is shown", DefaultWindowIcon);
    }
    private static TestCase Case(string name, Func<Task> body) => new("desktop tray: " + name, () => TestUiHost.InvokeAsync(body));
    private static DesktopWindow Owner()
    {
        var window = new DesktopWindow { Content = new Grid() }; window.AttachRoot(); window.Resize(320, 180); return window;
    }
    private static async Task Drain() => await DesktopTestUi.DrainAsync();
    private static void Send(DesktopTrayIconData data, int notification, Point? point = null, uint id = 1)
    {
        var position = point ?? new Point();
        nint packed = (nint)((uint)unchecked((ushort)(short)position.X) | ((uint)unchecked((ushort)(short)position.Y) << 16));
        DesktopTrayIconNative.SendMessage(data.Window, (int)data.CallbackMessage, packed, (nint)((id << 16) | (uint)notification));
    }
    private static async Task Lifetime()
    {
        var owner = Owner(); var native = new ProbeNative();
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => { }, native);
        try
        {
            Check(owner.Handle != 0 && !owner.IsVisible, "WinUI creates its HWND without showing the shell");
            Check(tray.TryShow() && tray.TryShow()); var data = native.Added;
            Check(data.Window == owner.Handle && IsWindow(data.Window), "registration uses the existing shell HWND, not a second content host");
            Check(native.Live && native.Loads == 1 && native.Calls.Count(call => call.Operation == 0) == 1, "repeat ensure never duplicates registration");
            Check(!owner.IsVisible, "registration does not show the shell");
            tray.Dispose(); tray.Dispose();
            Check(!native.Live && native.Destroys == 1 && native.Calls.Count(call => call.Operation == 2) == 1, "delete then release exactly once");
            Check(IsWindow(owner.Handle) && !tray.TryShow(), "disposing tray neither destroys the shell nor permits re-registration");
        }
        finally { owner.Close(); }
        await Drain(); Check(native.Destroys == 1, "owner close does not repeat disposal");
    }
    private static async Task Failures()
    {
        foreach (uint failed in new uint[] { 0, 4 })
        {
            var owner = Owner(); var native = new ProbeNative { FailedOperation = failed };
            using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => { }, native);
            try
            {
                Check(!tray.TryShow() && !native.Live && native.Destroys == 1, "partial registration rolls back icon ownership");
                Check(IsWindow(owner.Handle), "registration failure never destroys application window");
                Check(native.Calls.Any(call => call.Operation == 2) == (failed == 4), "accepted add is deleted when version selection fails");
                native.FailedOperation = null;
                Check(tray.TryShow() && native.Loads == 2 && native.Live, "same adapter can retry after rollback");
                native.RejectModifyAndAdd = true;
                Check(!tray.TryShow() && !native.Live && native.Destroys == 2, "lost icon cannot retain stale success");
            }
            finally { owner.Close(); }
        }
        var missingOwner = Owner(); var missing = new ProbeNative { IconAvailable = false };
        using var missingTray = new DesktopTrayIcon(missingOwner, () => { }, () => { }, () => { }, missing);
        try { Check(!missingTray.TryShow() && missing.Calls.Count == 0 && missing.Destroys == 0, "missing icon never registers or destroys an unowned handle"); }
        finally { missingOwner.Close(); }
        await Drain();
    }
    private static async Task ExplorerRestart()
    {
        var owner = Owner(); var native = new ProbeNative(); int unavailable = 0;
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () =>
        { Check(owner.DispatcherQueue.HasThreadAccess); unavailable++; owner.Show(); }, native);
        try
        {
            Check(tray.TryShow()); var data = native.Added;
            int recreated = (int)DesktopTrayIconNative.RegisterWindowMessage("TaskbarCreated"); native.Live = false;
            DesktopTrayIconNative.SendMessage(data.Window, recreated, 0, 0);
            DesktopTrayIconNative.SendMessage(data.Window, recreated, 0, 0); await Drain();
            Check(native.Live && unavailable == 0 && native.Loads == 1 && native.Calls.Count(call => call.Operation == 0) == 2,
                "repeated Explorer broadcasts coalesce one registration and reuse owned HICON");
            native.Live = false; native.FailedOperation = 0;
            DesktopTrayIconNative.SendMessage(data.Window, recreated, 0, 0); await Drain();
            Check(unavailable == 1 && owner.IsVisible && !native.Live && native.Destroys == 1 && IsWindow(owner.Handle),
                "failed recreation frees tray resources and restores usable shell");
        }
        finally { owner.Close(); }
    }
    private static async Task Callbacks()
    {
        var owner = Owner(); var native = new ProbeNative(); int shown = 0, exited = 0;
        using var tray = new DesktopTrayIcon(owner, () => { Check(owner.DispatcherQueue.HasThreadAccess); shown++; },
            () => { Check(owner.DispatcherQueue.HasThreadAccess); exited++; }, () => { }, native);
        try
        {
            Check(tray.TryShow()); var data = native.Added;
            await Task.Run(() => { Send(data, 0x400); Send(data, 0x401); Send(data, 0x400, id: 2); }); await Drain();
            Check(shown == 2 && exited == 0 && !owner.IsVisible, "primary and keyboard callbacks dispatch only the intended owner");
            native.Command = 1; Send(data, 0x7b); await Drain();
            native.Command = 2; Send(data, 0x7b); await Drain();
            Check(shown == 3 && exited == 1, "each explicit system menu command dispatches once");
            Send(data, 0x400); Send(data, 0x7b); tray.Dispose(); await Drain();
            Check(shown == 3 && exited == 1 && native.Destroys == 1 && !native.Live, "retirement rejects queued callbacks and menu commands");
            Send(data, 0x400); await Drain(); Check(shown == 3, "disposed hook cannot invoke application");
            using var replacement = new DesktopTrayIcon(owner, () => shown++, () => exited++, () => { }, native);
            Check(replacement.TryShow(), "a new owned adapter can register after disposal");
            Send(native.Added, 0x400); owner.Close(); await Drain();
            Check(shown == 3 && exited == 1 && native.Destroys == 2 && !native.Live,
                "owner close retires queued callbacks and releases its active registration once");
        }
        finally { owner.Close(); }
    }
    private static async Task MenuUnavailable()
    {
        var owner = Owner(); var native = new ProbeNative { Activate = false }; int unavailable = 0;
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => { unavailable++; owner.Show(); }, native);
        try
        {
            Check(tray.TryShow()); var data = native.Added;
            Send(data, 0x7b, new(-1920, 720)); await Drain();
            Check(unavailable == 1 && owner.IsVisible && native.Live && native.Menus.Count == 0,
                "foreground refusal cannot trap hidden application or start menu tracking");
            Check(native.MenuHosts.Count == 1 && !IsWindow(native.MenuHosts[0]),
                "foreground failure releases its temporary system-menu host");
            native.Activate = true; Send(data, 0x7b, new(-1920, 720)); await Drain();
            Check(native.LastPoint == new Point(-1920, 720), "version-four coordinates retain signed physical positions");
        }
        finally { owner.Close(); }
    }
    private static async Task MenuContract()
    {
        var owner = Owner(); var native = new ProbeNative(); int unavailable = 0;
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => unavailable++, native);
        try
        {
            Check(tray.TryShow()); var data = native.Added;
            Send(data, 0x7b, new(-1, -1)); await Drain();
            Check(native.PositionQueries == 1 && native.LastPoint == new Point(120, 240), "keyboard invocation uses tray rectangle or cursor fallback");
            Check(native.Labels.SequenceEqual(new[] { "显示主窗口", "退出" }), "real HMENU preserves complete accessible text commands");
            Check(native.Menus.Count == 1 && !IsMenu(native.Menus[0]), "menu handle is destroyed after tracking");
            Check(native.MenuHosts.Count == 1 && !IsWindow(native.MenuHosts[0]) && !owner.IsVisible,
                "system-menu host retires without revealing application content");
            Check(native.Calls.Count(call => call.Operation == 3) == 1, "dismissal with retained foreground returns focus to tray");
            native.Foreground = false; Send(data, 0x7b); await Drain();
            Check(native.Calls.Count(call => call.Operation == 3) == 1, "outside dismissal does not steal focus");
            native.PositionAvailable = false; Send(data, 0x7b, new(-1, -1)); await Drain();
            Check(unavailable == 1 && native.Menus.Count == 2, "position failure reports unavailability without tracking unknown bounds");
        }
        finally { owner.Close(); }
    }
    private static async Task MenuLifecycle()
    {
        Check(TestDesktopContext.Verify(requireTarget: true), "native menu verification requires the logged-in target desktop");
        var owner = Owner(); var native = new ProbeNative { NativeTracking = true }; int unavailable = 0;
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => unavailable++, native);
        try
        {
            Check(tray.TryShow());
            Send(native.Added, 0x7b, new Point(160, 160)); await Drain();
            Check(unavailable == 0 && native.ObservedMenu is not null,
                "the actual system menu was admitted on the verified desktop");
            Check(await native.ObservedMenu!, "real menu entered native tracking with visible command bounds");
            Check(!owner.IsVisible && native.MenuHosts.All(hwnd => !IsWindow(hwnd)),
                "dismissal preserves hidden shell and releases every temporary native menu host");
            Check(native.Calls.Count(call => call.Operation == 3) == 1, "ordinary dismissal returns focus to notification area");
        }
        finally { owner.Close(); }
    }
    private static Task WindowIcon()
    {
        var owner = Owner(); var native = new DesktopTrayIconNative();
        nint borrowed = LoadIcon(0, (nint)32512), previous = DesktopTrayIconNative.SendMessage(owner.Handle, 0x80, 0, borrowed);
        nint owned = 0;
        try
        {
            owned = native.LoadOwnedIcon(owner.Handle);
            Check(owned != 0 && owned != borrowed, "adapter owns a copy rather than borrowed window icon");
            native.DestroyOwnedIcon(owned); owned = 0;
            Check(DesktopTrayIconNative.SendMessage(owner.Handle, 0x7f, 0, 0) == borrowed, "releasing copy leaves window icon intact");
        }
        finally
        {
            if (owned != 0) native.DestroyOwnedIcon(owned);
            DesktopTrayIconNative.SendMessage(owner.Handle, 0x80, 0, previous); owner.Close();
        }
        return Task.CompletedTask;
    }
    private static Task DefaultWindowIcon()
    {
        var owner = Owner(); var native = new ProbeNative { UseNativeIcon = true };
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => { }, native);
        try { Check(!owner.IsVisible && tray.TryShow() && native.Added.Icon != 0, "default executable icon is available before show"); }
        finally { tray.Dispose(); owner.Close(); }
        Check(native.Destroys == 1, "default owned icon releases exactly once"); return Task.CompletedTask;
    }
    private sealed class ProbeNative : DesktopTrayIconNative
    {
        internal List<(uint Operation, DesktopTrayIconData Data)> Calls { get; } = [];
        internal DesktopTrayIconData Added => Calls.First(call => call.Operation == 0).Data;
        internal List<nint> Menus { get; } = [];
        internal List<nint> MenuHosts { get; } = [];
        internal List<string> Labels { get; } = [];
        internal int Loads, Destroys, PositionQueries;
        internal bool Live, RejectModifyAndAdd, UseNativeIcon;
        internal bool IconAvailable = true, Activate = true, Foreground = true, PositionAvailable = true;
        internal bool NativeTracking;
        internal Task<bool>? ObservedMenu;
        internal uint? FailedOperation;
        internal uint Command;
        internal Point LastPoint;
        internal override nint LoadOwnedIcon(nint owner) { Loads++; return UseNativeIcon ? base.LoadOwnedIcon(owner) : IconAvailable ? 42 : 0; }
        internal override void DestroyOwnedIcon(nint icon)
        {
            Check((UseNativeIcon || icon == 42) && !Live, "only acquired HICON is released after unregistration");
            if (UseNativeIcon) base.DestroyOwnedIcon(icon); Destroys++;
        }
        internal override bool Notify(uint operation, ref DesktopTrayIconData data)
        {
            Calls.Add((operation, data));
            if (FailedOperation == operation || RejectModifyAndAdd && operation is 0 or 1) return false;
            if (operation == 0) Live = true; if (operation == 2) Live = false;
            return operation != 1 || Live;
        }
        internal override nint CreateMenuHost(Point point)
        {
            var hwnd = base.CreateMenuHost(point); MenuHosts.Add(hwnd); return hwnd;
        }
        internal override bool ActivateMenuHost(nint hwnd) => NativeTracking ? base.ActivateMenuHost(hwnd) : Activate;
        internal override bool IsMenuHostForeground(nint hwnd) => NativeTracking ? base.IsMenuHostForeground(hwnd) : Foreground;
        internal override bool ResolveMenuPosition(nint hwnd, uint id, out Point position)
        { PositionQueries++; position = new(120, 240); return PositionAvailable; }
        internal override uint TrackMenu(nint menu, Point point, nint owner)
        {
            Check(IsMenu(menu) && IsWindow(owner), "native tracking receives real menu and application HWND");
            Menus.Add(menu); LastPoint = point; Labels.Clear();
            for (int i = 0; i < GetMenuItemCount(menu); i++)
            { var text = new StringBuilder(128); GetMenuString(menu, (uint)i, text, text.Capacity, 0x400); Labels.Add(text.ToString()); }
            if (NativeTracking)
            {
                uint thread = GetWindowThreadProcessId(owner, out _);
                ObservedMenu = Task.Run(async () =>
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        while (watch.Elapsed < TimeSpan.FromSeconds(3))
                        {
                            var state = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
                            if (GetGUIThreadInfo(thread, ref state) && (state.Flags & 4) != 0 &&
                                GetMenuItemRect(owner, menu, 0, out var first) && GetMenuItemRect(owner, menu, 1, out var second) &&
                                first.Right > first.Left && first.Bottom > first.Top && second.Right > second.Left)
                                return true;
                            await Task.Delay(10);
                        }
                        return false;
                    }
                    finally { PostMessage(owner, 0x1f, 0, 0); } // WM_CANCELMODE: native menu dismissal, no keyboard/mouse input.
                });
                return base.TrackMenu(menu, point, owner);
            }
            return Command;
        }
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsMenu(nint menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll", EntryPoint = "GetMenuStringW", CharSet = CharSet.Unicode)] private static extern int GetMenuString(nint menu, uint item, StringBuilder text, int maximum, uint flags);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint name);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMenuItemRect(nint window, nint menu, uint item, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}

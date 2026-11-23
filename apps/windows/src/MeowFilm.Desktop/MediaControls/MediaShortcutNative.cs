using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace MeowFilm.Desktop;

internal static class MediaShortcutNative
{
    internal const int HotKeyMessage = 0x0312;
    internal const uint DestroyMessage = 0x0082;
    internal const uint NoRepeat = 0x4000;
    internal static readonly int RetirementMessage =
        unchecked((int)RegisterWindowMessageW("MeowFilm.Desktop.MediaShortcut.RetireRegistration"));

    internal delegate nint Subclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    internal static bool Attach(nint hwnd, Subclass callback, nuint id) => SetWindowSubclass(hwnd, callback, id, 0);
    internal static bool Detach(nint hwnd, Subclass callback, nuint id) => RemoveWindowSubclass(hwnd, callback, id);
    internal static nint Continue(nint hwnd, uint message, nuint wParam, nint lParam) =>
        DefSubclassProc(hwnd, message, wParam, lParam);

    internal static (int Id, int Error) AllocateId()
    {
        // This component is a DLL: Win32 reserves the global atom range for DLL hotkey IDs.
        int id = GlobalAddAtomW("MeowFilm.MediaShortcut." + Guid.NewGuid().ToString("N"));
        return (id, id == 0 ? Marshal.GetLastWin32Error() : 0);
    }

    internal static void ReleaseId(int id) => _ = GlobalDeleteAtom((ushort)id);

    internal static (bool Success, int Error) Register(nint hwnd, int id, uint modifiers, uint key)
    {
        bool success = RegisterHotKey(hwnd, id, modifiers, key);
        return (success, success ? 0 : Marshal.GetLastWin32Error());
    }

    internal static (bool Success, int Error) Unregister(nint hwnd, int id)
    {
        bool success = UnregisterHotKey(hwnd, id);
        return (success, success ? 0 : Marshal.GetLastWin32Error());
    }

    internal static bool PostRetirement(nint hwnd, int id) =>
        RetirementMessage != 0 && PostMessageW(hwnd, RetirementMessage, id, 0);

    internal static nint ForegroundWindow => GetForegroundWindow();
    internal static bool OwnsWindow(nint window, nint candidate)
    {
        for (nint current = candidate; current != 0; current = GetWindow(current, 4))
            if (current == window) return true;
        return false;
    }

    internal static bool HasMenuOrChildFocus(nint window, FrameworkElement root)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        // WinUI normally focuses its content island's child HWND. Only foreign
        // child focus (or a native menu) is outside that root's input ownership.
        return GetGUIThreadInfo(0, ref info) &&
            ((info.Flags & 0x1c) != 0 || info.Focus != 0 && info.Focus != window &&
                (root.XamlRoot is not { } xamlRoot ||
                 !InputFocusController.GetForIsland(xamlRoot.ContentIsland).HasFocus));
    }

    internal static string RegistrationError(int error) => error switch
    {
        1409 => "热键被占用",
        5 => "系统拒绝注册热键",
        1400 => "窗口不可用，无法注册热键",
        87 or 1422 => "快捷键无效，无法注册",
        8 or 14 or 1450 => "系统资源不足，无法注册热键",
        _ => "热键注册失败"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort GlobalAddAtomW(string name);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GlobalDeleteAtom(ushort atom);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, Subclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, Subclass callback, nuint id);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}

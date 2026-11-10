using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace MeowFilm.Desktop;

internal class MediaShortcutNative
{
    internal const int HotKeyMessage = 0x0312;
    internal const uint NoRepeat = 0x4000;
    internal static readonly int RetirementMessage =
        unchecked((int)RegisterWindowMessageW("MeowFilm.Desktop.MediaShortcut.RetireRegistration"));

    public virtual (int Id, int Error) AllocateId()
    {
        // This component is a DLL: Win32 reserves the global atom range for DLL hotkey IDs.
        int id = GlobalAddAtomW("MeowFilm.MediaShortcut." + Guid.NewGuid().ToString("N"));
        return (id, id == 0 ? Marshal.GetLastWin32Error() : 0);
    }

    public virtual void ReleaseId(int id) => _ = GlobalDeleteAtom((ushort)id);

    public virtual (bool Success, int Error) Register(nint hwnd, int id, uint modifiers, uint key)
    {
        bool success = RegisterHotKey(hwnd, id, modifiers, key);
        return (success, success ? 0 : Marshal.GetLastWin32Error());
    }

    public virtual (bool Success, int Error) Unregister(nint hwnd, int id)
    {
        bool success = UnregisterHotKey(hwnd, id);
        return (success, success ? 0 : Marshal.GetLastWin32Error());
    }

    public virtual bool PostRetirement(nint hwnd, int id) =>
        RetirementMessage != 0 && PostMessageW(hwnd, RetirementMessage, id, 0);

    public virtual nint ForegroundWindow => GetForegroundWindow();
    public virtual IInputElement? FocusedElement => Keyboard.FocusedElement;
    public virtual bool OwnsWindow(nint window, nint candidate) =>
        candidate != 0 && (candidate == window || GetAncestor(candidate, 3) == window);

    public virtual bool HasMenuOrChildFocus(nint window)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(0, ref info) &&
            ((info.Flags & 0x1c) != 0 || info.Focus != 0 && info.Focus != window);
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
    private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}

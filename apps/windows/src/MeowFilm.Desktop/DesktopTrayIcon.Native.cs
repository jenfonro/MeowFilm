using System.Runtime.InteropServices;
using System.Windows;

namespace MeowFilm.Desktop;

// The adapter's native calls are kept here, with a small seam for resource and activation failures.
internal class DesktopTrayIconNative
{
    internal virtual bool Notify(uint operation, ref DesktopTrayIconData data) => Shell_NotifyIcon(operation, ref data);

    internal virtual nint LoadOwnedIcon(nint owner)
    {
        var borrowed = SendMessage(owner, 0x7f, 2, 0); // WM_GETICON / ICON_SMALL2
        if (borrowed == 0) borrowed = SendMessage(owner, 0x7f, 0, 0);
        if (borrowed == 0) borrowed = SendMessage(owner, 0x7f, 1, 0);
        // WM_GETICON does not return a class icon. WPF has already installed its
        // default executable icon before SourceInitialized; use only the window's sources.
        if (borrowed == 0) borrowed = GetClassLongPtr(owner, -34); // GCLP_HICONSM
        if (borrowed == 0) borrowed = GetClassLongPtr(owner, -14); // GCLP_HICON
        return borrowed == 0 ? 0 : CopyIcon(borrowed);
    }

    internal virtual void DestroyOwnedIcon(nint icon) => DestroyIcon(icon);

    internal bool ShowMenuHost(nint hwnd, Point position)
    {
        if (!SetWindowPos(hwnd, 0, (int)position.X, (int)position.Y, 1, 1, 0x14)) return false;
        ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE; SetForegroundWindow performs the activation.
        if (ActivateMenuHost(hwnd)) return true;
        ShowWindow(hwnd, 0);
        return false;
    }

    internal virtual bool ActivateMenuHost(nint hwnd) => SetForegroundWindow(hwnd) || GetForegroundWindow() == hwnd;
    internal virtual bool IsMenuHostForeground(nint hwnd) => GetForegroundWindow() == hwnd;

    internal bool CloseMenuHost(nint hwnd)
    {
        bool returnFocus = IsMenuHostForeground(hwnd);
        PostMessage(hwnd, 0, 0, 0); // WM_NULL settles the shell's context-menu tracking.
        ShowWindow(hwnd, 0);
        return returnFocus; // An outside click must retain its destination's foreground focus.
    }

    internal static bool TryGetMenuPosition(nint hwnd, uint id, out Point position)
    {
        var identity = new DesktopTrayIconIdentifier
        { Size = (uint)Marshal.SizeOf<DesktopTrayIconIdentifier>(), Window = hwnd, Id = id };
        if (Shell_NotifyIconGetRect(ref identity, out var rect) == 0)
        {
            position = new Point(rect.Left, rect.Bottom);
            return true;
        }
        var found = GetCursorPos(out var cursor);
        position = new Point(cursor.X, cursor.Y);
        return found;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint operation, ref DesktopTrayIconData data);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref DesktopTrayIconIdentifier identity, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    internal static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", ExactSpelling = true)]
    internal static extern nint GetClassLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CopyIcon(nint icon);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DesktopTrayIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid Guid;
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DesktopTrayIconData
{
    public uint Size;
    public nint Window;
    public uint Id;
    public uint Flags;
    public uint CallbackMessage;
    public nint Icon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tip;
    public uint State, StateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
    public uint Version;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
    public uint InfoFlags;
    public Guid Guid;
    public nint BalloonIcon;
}

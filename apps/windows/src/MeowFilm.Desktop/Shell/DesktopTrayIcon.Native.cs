using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;

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
        // WM_GETICON does not return a class icon; consult the existing window class.
        if (borrowed == 0) borrowed = GetClassLongPtr(owner, -34); // GCLP_HICONSM
        if (borrowed == 0) borrowed = GetClassLongPtr(owner, -14); // GCLP_HICON
        if (borrowed != 0) return CopyIcon(borrowed);
        // An unpackaged WinUI window need not install the executable icon as a class icon.
        // ExtractIconEx returns an owned icon; the same retirement path destroys it.
        if (Environment.ProcessPath is not { } executable) return 0;
        return ExtractIconEx(executable, 0, out var large, out var small, 1) > 0 ? Pick(large, small) : 0;
        static nint Pick(nint large, nint small)
        {
            if (small == 0) return large;
            if (large != 0) DestroyIcon(large);
            return small;
        }
    }

    internal virtual void DestroyOwnedIcon(nint icon) => DestroyIcon(icon);

    internal virtual bool ActivateMenuHost(nint hwnd) => SetForegroundWindow(hwnd) || GetForegroundWindow() == hwnd;
    internal virtual bool IsMenuHostForeground(nint hwnd) => GetForegroundWindow() == hwnd;
    internal virtual nint CreateMenuHost(Point point)
    {
        // A zero-size tool window permits the system menu to take foreground while
        // the application is hidden. It has no XAML/content surface or callback owner.
        nint host = CreateWindowEx(0x80, "STATIC", "", 0x80000000, (int)point.X, (int)point.Y, 0, 0, 0, 0, 0, 0);
        if (host != 0) ShowWindow(host, 4);
        return host;
    }
    internal virtual void DestroyMenuHost(nint hwnd) => DestroyWindow(hwnd);
    internal virtual uint TrackMenu(nint menu, Point point, nint owner) =>
        TrackPopupMenuEx(menu, 0x100 | 0x2, (int)point.X, (int)point.Y, owner, 0);
    internal virtual bool ResolveMenuPosition(nint hwnd, uint id, out Point position) =>
        TryGetMenuPosition(hwnd, id, out position);

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

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);

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
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);

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

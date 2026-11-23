using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using MeowFilm.Desktop;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace MeowFilm.Capabilities;

internal sealed class MusicDesktopLyricsUnlockWindow : Window
{
    private readonly MusicDesktopLyricsNative.SubclassProc hook;
    internal nint Handle { get; }
    internal MusicDesktopLyricsUnlockWindow(MusicDesktopLyricsWindow lyrics)
    {
        Title = "解锁桌面歌词";
        var button = new Button { Content = new FontIcon { Glyph = "\uE785", FontSize = 15 }, Width = 32, Height = 32,
            Padding = new Thickness(0), CornerRadius = new CornerRadius(5), Background = DesktopVisual.Brush("#D2212121"),
            Foreground = DesktopVisual.Brush("#F5F5F5") };
        AutomationProperties.SetName(button, Title); button.Click += (_, _) => lyrics.UnlockLyrics(); Content = button;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        { presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsAlwaysOnTop = true; }
        AppWindow.IsShownInSwitchers = false; AppWindow.Resize(new(32, 32));
        MusicDesktopLyricsNative.SetInputStyle(Handle, false);
        hook = (hwnd, message, wParam, lParam, _, _) => message == 0x0021 ? 3 : MusicDesktopLyricsNative.DefSubclassProc(hwnd, message, wParam, lParam);
        if (!MusicDesktopLyricsNative.SetWindowSubclass(Handle, hook, 1, 0)) throw new Win32Exception();
        Closed += (_, _) => MusicDesktopLyricsNative.RemoveWindowSubclass(Handle, hook, 1);
    }
    internal void Show() => AppWindow.Show(false);
    internal void Hide() => AppWindow.Hide();
    internal void ApplyHighContrast(bool enabled)
    {
        var button = (Button)Content;
        DesktopTheme.SetIsHighContrast(button, enabled);
        button.Background = enabled ? SystemAppearance.GetElementBrush(UIElementType.Window) : DesktopVisual.Brush("#D2212121");
        button.Foreground = enabled ? SystemAppearance.GetElementBrush(UIElementType.WindowText) : DesktopVisual.Brush("#F5F5F5");
    }
}

internal static class MusicDesktopLyricsNative
{
    internal readonly record struct Monitor(string Device, int Left, int Top, int Right, int Bottom,
        int WorkLeft, int WorkTop, int WorkRight, int WorkBottom, bool Primary, nint Handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal int Left, Top, Right, Bottom;
        internal int WorkLeft, WorkTop, WorkRight, WorkBottom;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }

    private delegate bool MonitorCallback(nint monitor, nint dc, nint bounds, nint data);

    internal static Monitor[] Monitors()
    {
        var monitors = new List<Monitor>();
        int enumerationError = 0;
        MonitorCallback callback = (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(handle, ref info))
            {
                enumerationError = Marshal.GetLastWin32Error();
                return false;
            }
            monitors.Add(new(info.Device, info.Left, info.Top, info.Right, info.Bottom,
                info.WorkLeft, info.WorkTop, info.WorkRight, info.WorkBottom, (info.Flags & 1) != 0, handle));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0))
            throw new Win32Exception(enumerationError != 0 ? enumerationError : Marshal.GetLastWin32Error());
        GC.KeepAlive(callback);
        return monitors.ToArray();
    }

    internal static Monitor MonitorForWindow(nint handle)
    {
        nint monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        return new(info.Device, info.Left, info.Top, info.Right, info.Bottom,
            info.WorkLeft, info.WorkTop, info.WorkRight, info.WorkBottom, (info.Flags & 1) != 0, monitor);
    }

    internal static void BringToTop(nint handle)
    {
        if (!SetWindowPos(handle, -1, 0, 0, 0, 0, 0x0213)) // HWND_TOPMOST, NOACTIVATE | NOOWNERZORDER | NOMOVE | NOSIZE
            throw new Win32Exception();
    }

    internal static void SetResizableStyle(nint handle)
    {
        Marshal.SetLastPInvokeError(0);
        nint style = GetWindowLongPtr(handle, -16);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
        style |= (nint)0x00040000; // WS_THICKFRAME
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(handle, -16, style) == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception();
        if (!SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0037)) // SWP_FRAMECHANGED | NOACTIVATE | NOZORDER | NOSIZE | NOMOVE
            throw new Win32Exception();
    }

    internal static nint ResizeHitTest(nint handle, nint lParam)
    {
        if (!GetWindowRect(handle, out var rect)) throw new Win32Exception();
        uint dpi = GetDpiForWindow(handle);
        int edge = (int)Math.Ceiling(8d * dpi / 96d);
        long packed = lParam.ToInt64();
        int x = unchecked((short)(packed & 0xffff));
        int y = unchecked((short)((packed >> 16) & 0xffff));
        if (x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom) return 0;
        bool left = x < rect.Left + edge, right = x >= rect.Right - edge;
        bool top = y < rect.Top + edge, bottom = y >= rect.Bottom - edge;
        if (top && left) return 13;     // HTTOPLEFT
        if (top && right) return 14;    // HTTOPRIGHT
        if (bottom && left) return 16;  // HTBOTTOMLEFT
        if (bottom && right) return 17; // HTBOTTOMRIGHT
        if (left) return 10;            // HTLEFT
        if (right) return 11;           // HTRIGHT
        if (top) return 12;             // HTTOP
        if (bottom) return 15;          // HTBOTTOM
        return 0;
    }

    internal static void SetInputStyle(nint handle, bool clickThrough)
    {
        Marshal.SetLastPInvokeError(0);
        nint style = GetWindowLongPtr(handle, -20);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
        style |= (nint)0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        // WS_EX_TRANSPARENT skips a layered HWND for cross-thread/process
        // hit testing; NOACTIVATE alone only prevents focus activation.
        const nint clickThroughFlags = 0x20 | 0x00080000; // WS_EX_TRANSPARENT | WS_EX_LAYERED
        style = clickThrough ? style | clickThroughFlags : style & ~clickThroughFlags;
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(handle, -20, style) == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception();
        if (clickThrough && !SetLayeredWindowAttributes(handle, 0, 255, 2))
            throw new Win32Exception();
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);

    internal delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    internal static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    internal static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnableWindow(nint handle, [MarshalAs(UnmanagedType.Bool)] bool enabled);

    internal static void Place(nint handle, Point start, Point end)
    {
        if (!SetWindowPos(handle, -1, (int)Math.Round(start.X), (int)Math.Round(start.Y),
                (int)Math.Round(end.X - start.X), (int)Math.Round(end.Y - start.Y),
                0x0010 | 0x0200)) // SWP_NOACTIVATE | SWP_NOOWNERZORDER, HWND_TOPMOST
            throw new Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenPoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint handle, out ScreenRect rect);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

internal sealed class MusicDesktopLyricsUnlockWindow : Window
{
    private HwndSource? source;

    internal MusicDesktopLyricsUnlockWindow(MusicDesktopLyricsWindow lyrics)
    {
        Owner = lyrics;
        Width = Height = 32;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true; ShowActivated = false; ShowInTaskbar = false; Focusable = false;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Title = "解锁桌面歌词";
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
        Resources["DesktopLyrics.UnlockBackground"] = new SolidColorBrush(Color.FromArgb(210, 33, 33, 33));
        var style = new Style(typeof(Button), (Style)lyrics.FindResource("DesktopLyrics.Tool"));
        style.Setters.Add(new Setter(BackgroundProperty, Resources["DesktopLyrics.UnlockBackground"]));
        var contrast = new Trigger { Property = MeowFilm.Desktop.DesktopTheme.IsHighContrastProperty, Value = true };
        contrast.Setters.Add(new Setter(BackgroundProperty, SystemColors.WindowBrush));
        style.Triggers.Add(contrast);
        var button = new Button
        {
            Content = "\uE785", ToolTip = "解锁桌面歌词",
            Style = style, Margin = new(0)
        };
        AutomationProperties.SetName(button, "解锁桌面歌词");
        button.Click += (_, _) => lyrics.UnlockLyrics();
        Content = button;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        MusicDesktopLyricsNative.SetInputStyle(handle, clickThrough: false);
        source = HwndSource.FromHwnd(handle)!;
        source.AddHook(WindowMessage);
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; }
        return 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (source is { IsDisposed: false }) source.RemoveHook(WindowMessage);
        source = null;
        Content = null;
        base.OnClosed(e);
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

    internal static bool SetLocation(nint handle, int x, int y) =>
        SetWindowPos(handle, 0, x, y, 0, 0, 0x0215); // SWP_NOACTIVATE | NOOWNERZORDER | NOZORDER | NOSIZE

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
        style = clickThrough ? style | 0x20 : style & ~(nint)0x20;
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(handle, -20, style) == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception();
    }

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

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MeowFilm.Capabilities;

internal partial class MusicDesktopLyricsWindow : Window
{
    private HwndSource? source;

    internal MusicDesktopLyricsWindow(MusicCapability module, Window anchor)
    {
        InitializeComponent();
        DataContext = module;
        // This is a placement anchor, not WPF Owner: minimizing it must not hide the lyrics.
        var bounds = anchor.WindowState == WindowState.Normal
            ? new Rect(anchor.Left, anchor.Top, anchor.Width, anchor.Height) : anchor.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top))
            bounds = SystemParameters.WorkArea;
        Left = bounds.Left + Math.Max(0, (bounds.Width - Width) / 2);
        Top = bounds.Top + Math.Max(0, bounds.Height - Height - 32);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle)!;
        source.AddHook(WindowMessage);
        Marshal.SetLastPInvokeError(0);
        var style = GetWindowLongPtr(handle, -20);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
        Marshal.SetLastPInvokeError(0);
        // NOACTIVATE also protects mouse interaction after the initial ShowActivated=false.
        if (SetWindowLongPtr(handle, -20, style | (nint)0x08000080) == 0 &&
            Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        return 0;
    }

    private void Lyrics_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (source is { IsDisposed: false }) source.RemoveHook(WindowMessage);
        source = null;
        DataContext = null;
        base.OnClosed(e);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace MeowFilm.Desktop;

/// <summary>Only the custom maximize hit target is adapted; WindowChrome owns the rest of non-client behavior.</summary>
internal sealed class NativeCaption : IDisposable
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmNcLeftButtonUp = 0x00A2;
    private const int WmNcLeftButtonDoubleClick = 0x00A3;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmCancelMode = 0x001F;
    private const int HtMaxButton = 9;
    private readonly Window _window;
    private readonly Button _maximize;
    private readonly Action _toggle;
    private readonly HwndSource? _source;
    private bool _pressed;
    private bool? _borderSuppressed;

    internal NativeCaption(Window window, Button maximize, Action toggle)
    {
        _window = window;
        _maximize = maximize;
        _toggle = toggle;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source?.AddHook(Hook);
        if (_source is not null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            uint round = 2; // DWMWCP_ROUND. Maximized/snapped shape remains a Windows decision.
            _ = DwmSetWindowAttribute(_source.Handle, 33, ref round, sizeof(uint));
        }
    }

    internal void SetImmersiveBorder(bool suppress)
    {
        if (_borderSuppressed == suppress || _source is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        // DWMWA_BORDER_COLOR affects painting only. Keep WindowChrome resize,
        // snap, shadow and native caption hit testing intact.
        uint color = suppress ? 0xfffffffeu : 0xffffffffu;
        if (DwmSetWindowAttribute(_source.Handle, 34, ref color, sizeof(uint)) >= 0) _borderSuppressed = suppress;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        try
        {
            if (message == WmNcHitTest && _maximize.IsVisible && _maximize.IsEnabled &&
                _window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            {
                // WM_NCHITTEST uses signed screen coordinates, including negative multi-monitor coordinates.
                var packed = lParam.ToInt64();
                var screen = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
                var point = _maximize.PointFromScreen(screen);
                if (new Rect(0, 0, _maximize.ActualWidth, _maximize.ActualHeight).Contains(point))
                {
                    handled = true;
                    return HtMaxButton;
                }
            }
            else if (message == WmNcLeftButtonDown && wParam == HtMaxButton)
            {
                _pressed = true;
                handled = true;
            }
            else if (message == WmNcLeftButtonUp)
            {
                var invoke = _pressed && wParam == HtMaxButton;
                _pressed = false;
                if (invoke)
                {
                    handled = true;
                    _toggle();
                }
            }
            else if (message == WmNcLeftButtonDoubleClick && wParam == HtMaxButton)
            {
                handled = true;
            }
            else if (message is WmCancelMode or WmLeftButtonUp) _pressed = false;
        }
        catch (InvalidOperationException)
        {
            // During HWND teardown, defer to normal system processing rather than throwing through WndProc.
            _pressed = false;
        }
        return 0;
    }

    public void Dispose()
    {
        try { _source?.RemoveHook(Hook); }
        catch (InvalidOperationException) { /* The HWND source may already be disposed. */ }
    }
}

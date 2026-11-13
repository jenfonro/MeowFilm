using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace MeowFilm.Media;

/// <summary>
/// A picture-only WPF/native surface. An owned, invisible HWND prevents libVLC
/// from falling back to a separate video window when the view is not mounted.
/// Mount the view before opening video. Content uses VideoView's foreground window
/// for native-client controls; never place WPF siblings above the video HWND.
/// </summary>
internal sealed class NativeVideoSurface : VideoView
{
    private readonly HwndSource _parkingWindow;
    private bool _retiring;
    private bool _closed;

    internal NativeVideoSurface()
    {
        Focusable = false;
        IsTabStop = false;
        AutomationProperties.SetName(this, "视频画面");
        _parkingWindow = new HwndSource(new HwndSourceParameters("meowfilm-native-media")
        {
            Width = 1,
            Height = 1,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, deliberately no WS_VISIBLE.
            ExtendedWindowStyle = 0x08000080 // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW.
        });
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        AttachTarget();
    }

    internal void SetPlayer(MediaPlayer? player)
    {
        MediaPlayer = player;
        _retiring = false;
        AttachTarget();
    }

    internal void BeginRetirement() => _retiring = true;

    private void OnLoaded(object sender, RoutedEventArgs args) => AttachTarget();

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_closed && !_retiring && MediaPlayer is { } player)
            player.Hwnd = _parkingWindow.Handle;
    }

    private void AttachTarget()
    {
        if (_closed || _retiring || MediaPlayer is not { } player)
            return;
        var host = Template?.FindName("PART_PlayerHost", this) as HwndHost;
        var handle = IsLoaded && host is not null ? host.Handle : IntPtr.Zero;
        player.Hwnd = handle == IntPtr.Zero ? _parkingWindow.Handle : handle;
    }

    internal void CloseSurface()
    {
        if (_closed) return;
        _closed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        MediaPlayer = null;
        try { Dispose(); }
        finally { _parkingWindow.Dispose(); }
    }
}

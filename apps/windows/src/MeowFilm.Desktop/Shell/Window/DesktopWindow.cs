using MeowFilm.Threading;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MeowFilm.Desktop;

/// <summary>A single WinUI top-level root. Native integration never owns a second content surface.</summary>
public class DesktopWindow : Window
{
    public new OwnerDispatcher Dispatcher { get; } = OwnerDispatcher.Current;
    public FrameworkElement Root => (FrameworkElement)Content;
    public ResourceDictionary Resources => Root.Resources;
    public nint Handle { get; }
    public bool IsActive { get; private set; }
    public bool IsVisible => !closed && AppWindow.IsVisible;
    public double MinWidth { get; set; }
    public double MinHeight { get; set; }
    public event EventHandler? Deactivated;
    public new event EventHandler? VisibilityChanged;
    public event EventHandler? WindowStateChanged;
    public event EventHandler? SessionEnding;
    public event EventHandler<CancelEventArgs>? Closing;
    private DesktopWindowMessageHandler[] nativeMessageHandlers = [];
    internal event DesktopWindowMessageHandler? NativeMessage
    {
        add
        {
            if (value is null) return;
            var current = nativeMessageHandlers;
            var next = new DesktopWindowMessageHandler[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = value;
            nativeMessageHandlers = next;
        }
        remove
        {
            if (value is null) return;
            var current = nativeMessageHandlers;
            int index = Array.LastIndexOf(current, value);
            if (index < 0) return;
            if (current.Length == 1)
            {
                nativeMessageHandlers = [];
                return;
            }
            var next = new DesktopWindowMessageHandler[current.Length - 1];
            if (index > 0) Array.Copy(current, 0, next, 0, index);
            if (index < current.Length - 1) Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            nativeMessageHandlers = next;
        }
    }
    private readonly NativeWin32.SubclassProc callback;
    private bool visible, sessionEnding, closingProgrammatically, closed, keepBackgroundOnShow, subclassAttached;
    private DesktopWindowState state;
    internal DesktopWindowState CachedWindowState => state;

    public DesktopWindow()
    {
        DesktopTypography.Install(Application.Current);
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        callback = ProcessMessage;
        if (!NativeWin32.SetWindowSubclass(Handle, callback, 1, 0)) throw new InvalidOperationException("WINDOW_SUBCLASS_FAILED");
        subclassAttached = true;
        state = WindowState;
        AppWindow.Closing += (_, args) =>
        {
            if (!closingProgrammatically) args.Cancel = !RequestClose();
        };
        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange && !args.DidPresenterChange && !args.DidVisibilityChange) return;
            if (state != WindowState) { state = WindowState; OnStateChanged(EventArgs.Empty); }
            if (visible != IsVisible) { visible = IsVisible; VisibilityChanged?.Invoke(this, EventArgs.Empty); }
        };
        Activated += (_, args) =>
        {
            IsActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (!IsActive) Deactivated?.Invoke(this, EventArgs.Empty);
        };
        Closed += (_, _) =>
        {
            closed = true;
            DetachSubclass(Handle);
            OnClosed(EventArgs.Empty);
        };
    }

    private void DetachSubclass(nint hwnd)
    {
        if (!subclassAttached) return;
        subclassAttached = false;
        NativeWin32.RemoveWindowSubclass(hwnd, callback, 1);
    }

    public void AttachRoot()
    {
        if (ReferenceEquals(Root.GetValue(OwnerProperty), this)) return;
        Root.SetValue(OwnerProperty, this);
        Root.Loaded += (_, _) => DesktopTypography.Apply(Root);
        DesktopTypography.Apply(Root);
    }
    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached(
        "Owner", typeof(DesktopWindow), typeof(DesktopWindow), new PropertyMetadata(null));
    public static DesktopWindow? GetWindow(DependencyObject view)
    {
        if (view is FrameworkElement { XamlRoot.Content: DependencyObject root } && root.GetValue(OwnerProperty) is DesktopWindow window)
            return window;
        for (DependencyObject? current = view; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
            if (current.GetValue(OwnerProperty) is DesktopWindow owner) return owner;
        return null;
    }
    public DesktopWindowState WindowState
    {
        get => AppWindow.Presenter is OverlappedPresenter presenter ? presenter.State switch
        {
            OverlappedPresenterState.Maximized => DesktopWindowState.Maximized,
            OverlappedPresenterState.Minimized => DesktopWindowState.Minimized,
            _ => DesktopWindowState.Normal
        } : DesktopWindowState.Normal;
        set
        {
            if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
            switch (value)
            {
                case DesktopWindowState.Maximized: presenter.Maximize(); break;
                case DesktopWindowState.Minimized: presenter.Minimize(); break;
                default: presenter.Restore(); break;
            }
        }
    }
    private bool RequestClose()
    {
        var args = new CancelEventArgs();
        Closing?.Invoke(this, args); OnClosing(args);
        return !args.Cancel;
    }
    public new void Close()
    {
        if (closed) return;
        if (!RequestClose()) return;
        closingProgrammatically = true;
        try { base.Close(); }
        finally { closingProgrammatically = false; }
    }
    public void Show() => AppWindow.Show();
    public void Hide() => AppWindow.Hide();
    /// <summary>
    /// Displays the window at normal visible size at the bottom of the desktop Z-order
    /// without activating or stealing foreground focus. Used by Windows --debug mode.
    /// </summary>
    public void ShowBackgroundInactive()
    {
        if (closed) return;
        keepBackgroundOnShow = true;
        try
        {
            AppWindow.Show(false);
            NativeWin32.SetWindowPos(Handle, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }
        finally
        {
            keepBackgroundOnShow = false;
        }
    }
    public void Resize(double width, double height)
    {
        double scale = NativeWin32.GetDpiForWindow(Handle) / 96d;
        AppWindow.Resize(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
    }
    protected virtual void OnClosing(CancelEventArgs args) { }
    protected virtual void OnClosed(EventArgs args) { }
    protected virtual void OnStateChanged(EventArgs args) => WindowStateChanged?.Invoke(this, args);
    private nint ProcessMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmNcDestroy)
        {
            DetachSubclass(hwnd);
            return NativeWin32.DefSubclassProc(hwnd, message, wParam, lParam);
        }
        // WM_QUERYENDSESSION is accepted by normal window processing. Only the confirmed
        // WM_ENDSESSION starts release, so a cancelled OS logout does not retire the app.
        // Publish once, without awaiting cleanup or vetoing the OS.
        if (message == 0x16 && wParam != 0 && !sessionEnding)
        {
            sessionEnding = true;
            SessionEnding?.Invoke(this, EventArgs.Empty);
        }
        if (message == 0x24)
        {
            var systemResult = NativeWin32.DefSubclassProc(hwnd, message, wParam, lParam);
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            double scale = NativeWin32.GetDpiForWindow(hwnd) / 96d;
            if (MinWidth > 0) limits.MinTrack.X = (int)Math.Ceiling(MinWidth * scale);
            if (MinHeight > 0) limits.MinTrack.Y = (int)Math.Ceiling(MinHeight * scale);
            Marshal.StructureToPtr(limits, lParam, false);
            return systemResult;
        }
        if (message == WmWindowPosChanging && keepBackgroundOnShow)
        {
            var pos = Marshal.PtrToStructure<NativeWin32.WindowPos>(lParam);
            pos.HwndInsertAfter = HwndBottom;
            pos.Flags = (pos.Flags & ~SwpNoZOrder) | SwpNoActivate;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        bool handled = false;
        var handlers = nativeMessageHandlers;
        for (int i = 0; i < handlers.Length; i++)
        {
            nint result = handlers[i](hwnd, (int)message, (nint)wParam, lParam, ref handled);
            if (handled) return result;
        }
        return NativeWin32.DefSubclassProc(hwnd, message, wParam, lParam);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativeWin32.NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    private const uint WmNcDestroy = 0x0082;
    private const uint WmWindowPosChanging = 0x0046;
    private static readonly nint HwndBottom = (nint)1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
}
public enum DesktopWindowState { Normal, Minimized, Maximized }
internal delegate nint DesktopWindowMessageHandler(nint hwnd, int message, nint wParam, nint lParam, ref bool handled);

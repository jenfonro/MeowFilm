using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MeowFilm.Desktop;

/// <summary>A single WinUI top-level root. Native integration never owns a second content surface.</summary>
public class DesktopWindow : Window
{
    public new System.Windows.Threading.Dispatcher Dispatcher { get; } = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    public FrameworkElement Root => (FrameworkElement)Content;
    public ResourceDictionary Resources => Root.Resources;
    public nint Handle { get; }
    public bool IsActive { get; private set; }
    public bool IsVisible => AppWindow.IsVisible;
    public double MinWidth { get; set; }
    public double MinHeight { get; set; }
    public event EventHandler? Deactivated;
    public new event EventHandler? VisibilityChanged;
    public event EventHandler? SessionEnding;
    public event EventHandler<CancelEventArgs>? Closing;
    internal event DesktopWindowMessageHandler? NativeMessage;
    private readonly NativeSubclass callback;
    private bool visible, sessionEnding, closingProgrammatically, closed;
    private DesktopWindowState state;

    public DesktopWindow()
    {
        DesktopTypography.Install(Application.Current);
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        callback = ProcessMessage;
        if (!SetWindowSubclass(Handle, callback, 1, 0)) throw new InvalidOperationException("WINDOW_SUBCLASS_FAILED");
        AppWindow.Closing += (_, args) =>
        {
            if (!closingProgrammatically) args.Cancel = !RequestClose();
        };
        AppWindow.Changed += (_, _) =>
        {
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
            RemoveWindowSubclass(Handle, callback, 1);
            OnClosed(EventArgs.Empty);
        };
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
    public void Resize(double width, double height)
    {
        double scale = GetDpiForWindow(Handle) / 96d;
        AppWindow.Resize(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
    }
    protected virtual void OnClosing(CancelEventArgs args) { }
    protected virtual void OnClosed(EventArgs args) { }
    protected virtual void OnStateChanged(EventArgs args) { }
    private nint ProcessMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
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
            var systemResult = DefSubclassProc(hwnd, message, wParam, lParam);
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            double scale = GetDpiForWindow(hwnd) / 96d;
            if (MinWidth > 0) limits.MinTrack.X = (int)Math.Ceiling(MinWidth * scale);
            if (MinHeight > 0) limits.MinTrack.Y = (int)Math.Ceiling(MinHeight * scale);
            Marshal.StructureToPtr(limits, lParam, false);
            return systemResult;
        }
        bool handled = false;
        nint result = 0;
        if (NativeMessage is { } handlers)
            foreach (DesktopWindowMessageHandler handler in handlers.GetInvocationList())
            {
                result = handler(hwnd, (int)message, (nint)wParam, lParam, ref handled);
                if (handled) return result;
            }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    private delegate nint NativeSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, NativeSubclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, NativeSubclass callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
public enum DesktopWindowState { Normal, Minimized, Maximized }
internal delegate nint DesktopWindowMessageHandler(nint hwnd, int message, nint wParam, nint lParam, ref bool handled);

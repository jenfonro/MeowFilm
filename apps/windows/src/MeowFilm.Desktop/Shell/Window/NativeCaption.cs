using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Windows.Graphics;

namespace MeowFilm.Desktop;

/// <summary>
/// Borderless native caption integration for the one application window using
/// OverlappedPresenter.SetBorderAndTitleBar(true, false) and Win32 WM_NCHITTEST
/// without creating a separate system titlebar child HWND.
/// </summary>
internal sealed class NativeCaption : IDisposable
{
    private const nuint ChildSubclassId = 2;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmNcHitTest = 0x0084;
    private static readonly nint HtTransparent = -1;
    private static readonly nint HtCaption = 2;
    private static readonly nint HtTop = 12;
    private static readonly nint HtTopLeft = 13;
    private static readonly nint HtTopRight = 14;

    private readonly DesktopWindow window;
    private readonly FrameworkElement titleBar;
    private readonly FrameworkElement captionButtons;
    private readonly Func<bool> isFullSurfaceOpen;
    private readonly FrameworkElement[] headerControls;
    private readonly FrameworkElement[] overlayControls;
    private readonly List<IDisposable> visibility = [];
    private readonly HashSet<nint> subclassedChildren = [];
    private readonly NativeSubclass childCallback;
    private XamlRoot? root;
    private bool queued, disposed;

    private RectInt32[] captionRegions = [];
    private RectInt32[] passthroughRegions = [];
    private PassthroughControlSnapshot[] passthroughControls = [];
    private int leftInsetPx;
    private int rightInsetPx;
    private int captionHeightPx;
    private int resizeBorderPx = 6;

    internal sealed record PassthroughControlSnapshot(
        string? Name,
        string Type,
        string? AutomationName,
        RectInt32 Bounds);

    internal sealed record ChildHwndSnapshot(
        string Hwnd,
        string ClassName,
        bool Visible,
        bool Subclassed,
        int X,
        int Y,
        int Width,
        int Height);

    internal NativeCaption(
        DesktopWindow window,
        FrameworkElement titleBar,
        FrameworkElement captionButtons,
        Func<bool> isFullSurfaceOpen,
        FrameworkElement[] headerControls,
        FrameworkElement[] overlayControls)
    {
        this.window = window;
        this.titleBar = titleBar;
        this.captionButtons = captionButtons;
        this.isFullSurfaceOpen = isFullSurfaceOpen;
        this.headerControls = headerControls;
        this.overlayControls = overlayControls;
        childCallback = ProcessChildMessage;

        // Keep Win11 native rounded corners, shadow, resize frame, and Aero Snap
        // without creating a separate system titlebar child HWND.
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);

        window.NativeMessage += ProcessMainWindowMessage;
        titleBar.SizeChanged += Changed;
        titleBar.Loaded += Loaded;
        captionButtons.SizeChanged += Changed;
        foreach (var control in headerControls.Concat(overlayControls))
        {
            control.SizeChanged += Changed;
            visibility.Add(DesktopVisual.ObserveVisibility(control, _ => QueueUpdate()));
            if (control is DesktopPageHeader pageHeader) pageHeader.ContributionChanged += HeaderChanged;
        }
        window.AppWindow.Changed += WindowChanged;
        EnsureChildSubclasses();
        Update();
    }

    internal void Refresh() => QueueUpdate();

    internal void RefreshImmediate()
    {
        if (disposed) return;
        queued = false;
        EnsureChildSubclasses();
        Update();
    }

    internal (RectInt32[] Caption, RectInt32[] Passthrough) InspectRegions() => (captionRegions, passthroughRegions);

    internal object InspectDebugState()
    {
        var children = EnumerateChildHwnds();
        return new
        {
            leftInsetPx,
            rightInsetPx,
            captionHeightPx,
            resizeBorderPx,
            isFullSurfaceOpen = isFullSurfaceOpen(),
            captionRects = captionRegions.Select(r => new { x = r.X, y = r.Y, width = r.Width, height = r.Height }).ToArray(),
            passthroughRects = passthroughRegions.Select(r => new { x = r.X, y = r.Y, width = r.Width, height = r.Height }).ToArray(),
            passthroughControls = passthroughControls.Select(c => new
            {
                name = c.Name,
                type = c.Type,
                automationName = c.AutomationName,
                x = c.Bounds.X,
                y = c.Bounds.Y,
                width = c.Bounds.Width,
                height = c.Bounds.Height
            }).ToArray(),
            childHwnds = children.Select(c => new
            {
                hwnd = c.Hwnd,
                className = c.ClassName,
                visible = c.Visible,
                subclassed = c.Subclassed,
                x = c.X,
                y = c.Y,
                width = c.Width,
                height = c.Height
            }).ToArray()
        };
    }

    internal int CaptionHeightPx => captionHeightPx;

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (root is not null) root.Changed -= RootChanged;
        root = titleBar.XamlRoot;
        if (root is not null) root.Changed += RootChanged;
        EnsureChildSubclasses();
        QueueUpdate();
    }

    private void Changed(object sender, SizeChangedEventArgs args) => QueueUpdate();
    private void HeaderChanged(object? sender, EventArgs args) => QueueUpdate();

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPresenterChange && !args.DidVisibilityChange) return;
        QueueUpdate();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueUpdate();

    private void QueueUpdate()
    {
        if (queued || disposed) return;
        queued = titleBar.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            queued = false;
            if (!disposed) Update();
        });
    }

    private void Update()
    {
        if (titleBar.XamlRoot is null) return;
        EnsureChildSubclasses();
        double scale = titleBar.XamlRoot.RasterizationScale;
        resizeBorderPx = Math.Max(6, (int)Math.Ceiling(6 * scale));

        int totalWidthPx = window.AppWindow.ClientSize.Width;
        if (totalWidthPx <= 0) totalWidthPx = (int)Math.Ceiling(window.Root.ActualWidth * scale);

        int computedLeftInset = 0;
        int computedRightInset = captionButtons.IsLoaded && DesktopVisual.IsVisible(captionButtons) && captionButtons.ActualWidth > 0
            ? (int)Math.Ceiling(captionButtons.ActualWidth * scale)
            : (int)Math.Ceiling(138 * scale);

        double dragHeightDip = Math.Max(64d, titleBar.ActualHeight);
        int computedCaptionHeight = Math.Max(1, (int)Math.Ceiling(dragHeightDip * scale));
        int captionWidthPx = Math.Max(0, totalWidthPx - computedLeftInset - computedRightInset);

        leftInsetPx = computedLeftInset;
        rightInsetPx = computedRightInset;
        captionHeightPx = computedCaptionHeight;

        captionRegions = captionWidthPx > 0 && computedCaptionHeight > 0
            ? [new RectInt32(computedLeftInset, 0, captionWidthPx, computedCaptionHeight)]
            : [];

        // When a full-window surface (such as fullscreen player detail) covers the shell header,
        // exclude hidden-underneath header controls so the entire top caption strip remains draggable.
        IEnumerable<FrameworkElement> activeControls = isFullSurfaceOpen()
            ? overlayControls
            : headerControls.Concat(overlayControls);

        var nextControls = new List<PassthroughControlSnapshot>();
        var nextPassthrough = new List<RectInt32>();
        foreach (var rootControl in activeControls)
        {
            if (!rootControl.IsLoaded || !DesktopVisual.IsVisible(rootControl)) continue;
            IEnumerable<FrameworkElement> targets = rootControl is DesktopPageHeader pageHeader
                ? pageHeader.CaptionInteractiveRegions
                : [rootControl];
            foreach (var control in targets)
            {
                if (!control.IsLoaded || !DesktopVisual.IsVisible(control) ||
                    control.ActualWidth <= 0 || control.ActualHeight <= 0)
                    continue;
                var bounds = control.TransformToVisual(window.Root)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
                var rect = new RectInt32(
                    (int)Math.Floor(bounds.X * scale),
                    (int)Math.Floor(bounds.Y * scale),
                    (int)Math.Ceiling(bounds.Width * scale),
                    (int)Math.Ceiling(bounds.Height * scale));
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                if (rect.Y >= computedCaptionHeight || rect.Y + rect.Height <= 0) continue;
                nextPassthrough.Add(rect);
                string? name = string.IsNullOrWhiteSpace(control.Name) ? null : control.Name;
                string? autoName = AutomationProperties.GetName(control);
                nextControls.Add(new PassthroughControlSnapshot(
                    name,
                    control.GetType().Name,
                    string.IsNullOrWhiteSpace(autoName) ? null : autoName,
                    rect));
            }
        }
        passthroughRegions = nextPassthrough.ToArray();
        passthroughControls = nextControls.ToArray();
    }

    private void EnsureChildSubclasses()
    {
        if (disposed || window.Handle == 0) return;
        var buffer = new char[256];
        EnumChildWindows(window.Handle, (child, _) =>
        {
            int len = GetClassName(child, buffer, buffer.Length);
            if (len <= 0) return true;
            string className = new(buffer, 0, len);
            if (IsUnusedTitleBarOverlayClass(className))
            {
                if (IsWindowVisible(child)) ShowWindow(child, 0);
                EnableWindow(child, false);
                return true;
            }
            if (subclassedChildren.Contains(child)) return true;
            if (IsContentBridgeClass(className) && SetWindowSubclass(child, childCallback, ChildSubclassId, 0))
                subclassedChildren.Add(child);
            return true;
        }, 0);
    }

    private static bool IsUnusedTitleBarOverlayClass(string className) =>
        className.Contains("InputNonClientPointerSource", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("ReunionWindowingCaptionControls", StringComparison.OrdinalIgnoreCase);

    private static bool IsContentBridgeClass(string className) =>
        className.Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("DesktopWindowContentBridge", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("InputSite", StringComparison.OrdinalIgnoreCase);

    private ChildHwndSnapshot[] EnumerateChildHwnds()
    {
        if (window.Handle == 0) return [];
        var list = new List<ChildHwndSnapshot>();
        var buffer = new char[256];
        EnumChildWindows(window.Handle, (child, _) =>
        {
            int len = GetClassName(child, buffer, buffer.Length);
            string className = len > 0 ? new string(buffer, 0, len) : "";
            int x = 0, y = 0, w = 0, h = 0;
            if (GetWindowRect(child, out var rect))
            {
                var pt = new NativePoint { X = rect.Left, Y = rect.Top };
                ScreenToClient(window.Handle, ref pt);
                x = pt.X;
                y = pt.Y;
                w = Math.Max(0, rect.Right - rect.Left);
                h = Math.Max(0, rect.Bottom - rect.Top);
            }
            list.Add(new ChildHwndSnapshot(
                $"0x{child.ToInt64():X}",
                className,
                IsWindowVisible(child),
                subclassedChildren.Contains(child),
                x, y, w, h));
            return true;
        }, 0);
        return list.ToArray();
    }

    private nint ProcessChildMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmNcDestroy)
        {
            RemoveWindowSubclass(hwnd, childCallback, ChildSubclassId);
            subclassedChildren.Remove(hwnd);
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }
        if (message == WmNcHitTest && !disposed)
        {
            var pt = DecodeClientPoint(lParam);
            if (window.WindowState == DesktopWindowState.Normal && pt.Y >= 0 && pt.Y < resizeBorderPx)
                return HtTransparent;
            if (IsInDraggableCaption(pt.X, pt.Y))
                return HtTransparent;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private nint ProcessMainWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != (int)WmNcHitTest || disposed) return 0;
        var pt = DecodeClientPoint(lParam);
        if (window.WindowState == DesktopWindowState.Normal && pt.Y >= 0 && pt.Y < resizeBorderPx &&
            GetClientRect(window.Handle, out var clientRect) && pt.X >= 0 && pt.X < clientRect.Right)
        {
            int cornerPx = Math.Max(resizeBorderPx * 2, 12);
            handled = true;
            if (pt.X < cornerPx) return HtTopLeft;
            if (pt.X >= clientRect.Right - cornerPx) return HtTopRight;
            return HtTop;
        }
        if (IsInDraggableCaption(pt.X, pt.Y))
        {
            handled = true;
            return HtCaption;
        }
        return 0;
    }

    private NativePoint DecodeClientPoint(nint lParam)
    {
        long packed = lParam.ToInt64();
        var pt = new NativePoint
        {
            X = (short)(packed & 0xFFFF),
            Y = (short)((packed >> 16) & 0xFFFF)
        };
        ScreenToClient(window.Handle, ref pt);
        return pt;
    }

    private bool IsInDraggableCaption(int x, int y)
    {
        var captions = captionRegions;
        bool inCaption = false;
        for (int i = 0; i < captions.Length; i++)
        {
            var r = captions[i];
            if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height)
            {
                inCaption = true;
                break;
            }
        }
        if (!inCaption) return false;
        var passthrough = passthroughRegions;
        for (int i = 0; i < passthrough.Length; i++)
        {
            var r = passthrough[i];
            if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.NativeMessage -= ProcessMainWindowMessage;
        titleBar.SizeChanged -= Changed;
        titleBar.Loaded -= Loaded;
        captionButtons.SizeChanged -= Changed;
        window.AppWindow.Changed -= WindowChanged;
        foreach (var control in headerControls.Concat(overlayControls))
        {
            control.SizeChanged -= Changed;
            if (control is DesktopPageHeader pageHeader) pageHeader.ContributionChanged -= HeaderChanged;
        }
        foreach (var observation in visibility) observation.Dispose();
        visibility.Clear();
        if (root is not null) root.Changed -= RootChanged;
        foreach (var child in subclassedChildren.ToArray())
            RemoveWindowSubclass(child, childCallback, ChildSubclassId);
        subclassedChildren.Clear();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate nint NativeSubclass(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, NativeSubclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, NativeSubclass callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnableWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hWnd, out NativeRect lpRect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(nint hWnd, ref NativePoint lpPoint);
}

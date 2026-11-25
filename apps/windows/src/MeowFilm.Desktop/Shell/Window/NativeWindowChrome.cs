using System.Runtime.InteropServices;

namespace MeowFilm.Desktop;

/// <summary>
/// Shared Win32 borderless child-HWND host used by transparent floating overlays (desktop lyrics):
/// collapses unused system titlebar overlay HWNDs and routes client hit-testing on content bridges.
/// </summary>
public sealed class NativeBorderlessChildHost : IDisposable
{
    private const nuint BridgeSubclassId = 2;
    private const nuint OverlaySubclassId = 3;
    private const uint WmShowWindow = 0x0018;
    private const uint WmWindowPosChanging = 0x0046;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmNcHitTest = 0x0084;
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x00000020;
    private const nint WsExNoActivate = 0x08000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private static readonly nint HtTransparent = -1;

    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmncrpDisabled = 1;
    private const int DwmwcpDoNotRound = 1;
    private const uint DwmwaColorNone = 0xFFFFFFFE;

    private readonly nint parentHwnd;
    private readonly Func<int, int, bool>? isClientTransparent;
    private readonly HashSet<nint> bridgeChildren = [];
    private readonly HashSet<nint> overlayChildren = [];
    private readonly char[] classNameBuffer = new char[256];
    private readonly NativeWin32.SubclassProc bridgeCallback;
    private readonly NativeWin32.SubclassProc overlayCallback;
    private readonly EnumWindowsProc enumCallback;
    private bool disposed;

    public sealed record ChildHwndSnapshot(
        string Hwnd,
        string ClassName,
        bool Visible,
        bool Subclassed,
        int X,
        int Y,
        int Width,
        int Height);

    public NativeBorderlessChildHost(nint parentHwnd, Func<int, int, bool>? isClientTransparent = null)
    {
        this.parentHwnd = parentHwnd;
        this.isClientTransparent = isClientTransparent;
        bridgeCallback = ProcessBridgeMessage;
        overlayCallback = ProcessOverlayMessage;
        enumCallback = InspectChildWindow;
    }

    public void RefreshChildren()
    {
        if (disposed || parentHwnd == 0) return;
        EnumChildWindows(parentHwnd, enumCallback, 0);
    }

    public ChildHwndSnapshot[] EnumerateChildHwnds() =>
        EnumerateChildHwnds(parentHwnd, child => bridgeChildren.Contains(child) || overlayChildren.Contains(child));

    internal static ChildHwndSnapshot[] EnumerateChildHwnds(nint parentHwnd, Func<nint, bool>? isSubclassed = null)
    {
        if (parentHwnd == 0) return [];
        var list = new List<ChildHwndSnapshot>();
        var buffer = new char[256];
        EnumChildWindows(parentHwnd, (child, _) =>
        {
            int len = GetClassName(child, buffer, buffer.Length);
            string className = len > 0 ? new string(buffer, 0, len) : "";
            int x = 0, y = 0, w = 0, h = 0;
            if (NativeWin32.GetWindowRect(child, out var rect))
            {
                var pt = new NativeWin32.NativePoint { X = rect.Left, Y = rect.Top };
                NativeWin32.ScreenToClient(parentHwnd, ref pt);
                x = pt.X;
                y = pt.Y;
                w = Math.Max(0, rect.Right - rect.Left);
                h = Math.Max(0, rect.Bottom - rect.Top);
            }
            list.Add(new ChildHwndSnapshot(
                $"0x{child.ToInt64():X}",
                className,
                IsWindowVisible(child),
                isSubclassed?.Invoke(child) == true,
                x, y, w, h));
            return true;
        }, 0);
        return list.ToArray();
    }

    /// <summary>
    /// Disables DWM non-client frame/border/rounding for transparent floating overlays
    /// (such as desktop lyrics) so WS_THICKFRAME resize support never flashes a native border.
    /// </summary>
    public static void DisableDwmNonClientFrame(nint hwnd)
    {
        if (hwnd == 0) return;
        int ncPolicy = DwmncrpDisabled;
        _ = DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref ncPolicy, sizeof(int));
        int corner = DwmwcpDoNotRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        uint borderColor = DwmwaColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));
    }

    private bool InspectChildWindow(nint child, nint _)
    {
        int len = GetClassName(child, classNameBuffer, classNameBuffer.Length);
        if (len <= 0) return true;
        string className = new(classNameBuffer, 0, len);
        if (IsUnusedTitleBarOverlayClass(className))
        {
            if (overlayChildren.Add(child) && !NativeWin32.SetWindowSubclass(child, overlayCallback, OverlaySubclassId, 0))
                overlayChildren.Remove(child);
            CollapseOverlayChild(child);
            return true;
        }
        if (isClientTransparent is not null && !bridgeChildren.Contains(child) && IsContentBridgeClass(className))
        {
            if (NativeWin32.SetWindowSubclass(child, bridgeCallback, BridgeSubclassId, 0))
                bridgeChildren.Add(child);
        }
        return true;
    }

    private static void CollapseOverlayChild(nint child)
    {
        nint exStyle = NativeWin32.GetWindowLongPtr(child, GwlExStyle);
        nint nextExStyle = exStyle | WsExTransparent | WsExNoActivate;
        if (nextExStyle != exStyle) NativeWin32.SetWindowLongPtr(child, GwlExStyle, nextExStyle);
        EnableWindow(child, false);
        NativeWin32.SetWindowPos(child, 0, 0, 0, 0, 0, SwpHideWindow | SwpNoActivate | SwpNoZOrder);
    }

    private nint ProcessOverlayMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmNcDestroy)
        {
            NativeWin32.RemoveWindowSubclass(hwnd, overlayCallback, OverlaySubclassId);
            overlayChildren.Remove(hwnd);
            return NativeWin32.DefSubclassProc(hwnd, message, wParam, lParam);
        }
        if (disposed) return NativeWin32.DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == WmNcHitTest) return HtTransparent;
        if (message == WmShowWindow && wParam != 0) return 0;
        if (message == WmWindowPosChanging && lParam != 0)
        {
            var pos = Marshal.PtrToStructure<NativeWin32.WindowPos>(lParam);
            pos.X = 0;
            pos.Y = 0;
            pos.Cx = 0;
            pos.Cy = 0;
            pos.Flags = (pos.Flags & ~SwpShowWindow) | SwpHideWindow | SwpNoActivate;
            Marshal.StructureToPtr(pos, lParam, false);
            return 0;
        }
        return NativeWin32.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private nint ProcessBridgeMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmNcDestroy)
        {
            NativeWin32.RemoveWindowSubclass(hwnd, bridgeCallback, BridgeSubclassId);
            bridgeChildren.Remove(hwnd);
            return NativeWin32.DefSubclassProc(hwnd, message, wParam, lParam);
        }
        if (message == WmNcHitTest && !disposed && isClientTransparent is not null)
        {
            var pt = DecodeClientPoint(lParam);
            if (isClientTransparent(pt.X, pt.Y))
                return HtTransparent;
        }
        return NativeWin32.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private NativeWin32.NativePoint DecodeClientPoint(nint lParam)
    {
        long packed = lParam.ToInt64();
        var pt = new NativeWin32.NativePoint
        {
            X = unchecked((short)(packed & 0xFFFF)),
            Y = unchecked((short)((packed >> 16) & 0xFFFF))
        };
        NativeWin32.ScreenToClient(parentHwnd, ref pt);
        return pt;
    }

    private static bool IsUnusedTitleBarOverlayClass(string className) =>
        className.Contains("InputNonClientPointerSource", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("ReunionWindowingCaptionControls", StringComparison.OrdinalIgnoreCase);

    private static bool IsContentBridgeClass(string className) =>
        className.Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("DesktopWindowContentBridge", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("InputSite", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var child in bridgeChildren.ToArray())
            NativeWin32.RemoveWindowSubclass(child, bridgeCallback, BridgeSubclassId);
        bridgeChildren.Clear();
        foreach (var child in overlayChildren.ToArray())
            NativeWin32.RemoveWindowSubclass(child, overlayCallback, OverlaySubclassId);
        overlayChildren.Clear();
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnableWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref uint attrValue, int attrSize);
}

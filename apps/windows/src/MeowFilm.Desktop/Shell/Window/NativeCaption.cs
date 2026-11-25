using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Windows.Graphics;

namespace MeowFilm.Desktop;

/// <summary>
/// Single-window native caption owner using OverlappedPresenter.SetBorderAndTitleBar(true, false)
/// and InputNonClientPointerSource for non-client drag, top-edge resize, and interactive passthrough.
/// </summary>
internal sealed class NativeCaption : IDisposable
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpHideWindow = 0x0080;

    private readonly DesktopWindow window;
    private readonly FrameworkElement titleBar;
    private readonly Func<bool> isHeaderCovered;
    private readonly FrameworkElement[] headerControls;
    private readonly FrameworkElement[] overlayControls;
    private readonly List<IDisposable> visibility = [];
    private readonly InputNonClientPointerSource nonClientInput;
    private XamlRoot? root;
    private bool queued, disposed;

    private RectInt32[] topBorderRegions = [];
    private RectInt32[] captionRegions = [];
    private RectInt32[] passthroughRegions = [];
    private PassthroughControlSnapshot[] passthroughControls = [];
    private int captionHeightPx;
    private int resizeBorderPx = 6;

    internal sealed record PassthroughControlSnapshot(
        string? Name,
        string Type,
        string? AutomationName,
        RectInt32 Bounds);

    internal NativeCaption(
        DesktopWindow window,
        FrameworkElement titleBar,
        Func<bool> isHeaderCovered,
        FrameworkElement[] headerControls,
        FrameworkElement[] overlayControls)
    {
        this.window = window;
        this.titleBar = titleBar;
        this.isHeaderCovered = isHeaderCovered;
        this.headerControls = headerControls;
        this.overlayControls = overlayControls;

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);

        nonClientInput = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);

        titleBar.SizeChanged += Changed;
        titleBar.Loaded += Loaded;
        foreach (var control in headerControls.Concat(overlayControls))
        {
            control.SizeChanged += Changed;
            visibility.Add(DesktopVisual.ObserveVisibility(control, _ => QueueUpdate()));
            if (control is DesktopPageHeader pageHeader)
            {
                pageHeader.ContributionChanged += HeaderChanged;
                pageHeader.InteractiveRegionsChanged += HeaderChanged;
            }
        }
        window.AppWindow.Changed += WindowChanged;
        Update();
    }

    internal void Refresh() => QueueUpdate();

    internal void RefreshImmediate()
    {
        if (disposed) return;
        queued = false;
        Update();
    }

    internal (RectInt32[] Caption, RectInt32[] Passthrough) InspectRegions() => (captionRegions, passthroughRegions);

    internal object InspectDebugState()
    {
        static object[] ToRects(RectInt32[]? rects) => rects is null
            ? []
            : rects.Select(r => (object)new { x = r.X, y = r.Y, width = r.Width, height = r.Height }).ToArray();
        return new
        {
            captionHeightPx, resizeBorderPx,
            isFullSurfaceOpen = isHeaderCovered(),
            captionRects = ToRects(captionRegions),
            passthroughRects = ToRects(passthroughRegions),
            appliedTopBorderRects = ToRects(nonClientInput.GetRegionRects(NonClientRegionKind.TopBorder)),
            appliedCaptionRects = ToRects(nonClientInput.GetRegionRects(NonClientRegionKind.Caption)),
            appliedPassthroughRects = ToRects(nonClientInput.GetRegionRects(NonClientRegionKind.Passthrough)),
            passthroughControls = passthroughControls.Select(c => new
            {
                name = c.Name, type = c.Type, automationName = c.AutomationName,
                x = c.Bounds.X, y = c.Bounds.Y, width = c.Bounds.Width, height = c.Bounds.Height
            }).ToArray(),
            childHwnds = NativeBorderlessChildHost.EnumerateChildHwnds(window.Handle).Select(c => new
            {
                hwnd = c.Hwnd, className = c.ClassName, visible = c.Visible, subclassed = c.Subclassed,
                x = c.X, y = c.Y, width = c.Width, height = c.Height
            }).ToArray()
        };
    }

    internal int CaptionHeightPx => captionHeightPx;

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (root is not null) root.Changed -= RootChanged;
        root = titleBar.XamlRoot;
        if (root is not null) root.Changed += RootChanged;
        QueueUpdate();
    }

    private void Changed(object sender, SizeChangedEventArgs args) => QueueUpdate();
    private void HeaderChanged(object? sender, EventArgs args) => QueueUpdate();

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPresenterChange || args.DidVisibilityChange)
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
        double scale = titleBar.XamlRoot.RasterizationScale;
        resizeBorderPx = Math.Max(6, (int)Math.Ceiling(6 * scale));

        int totalWidthPx = window.AppWindow.ClientSize.Width;
        if (totalWidthPx <= 0) totalWidthPx = (int)Math.Ceiling(window.Root.ActualWidth * scale);
        int totalHeightPx = window.AppWindow.ClientSize.Height;
        if (totalHeightPx <= 0) totalHeightPx = (int)Math.Ceiling(window.Root.ActualHeight * scale);

        int computedCaptionHeight = Math.Max(1, (int)Math.Ceiling(Math.Max(64d, titleBar.ActualHeight) * scale));
        captionHeightPx = computedCaptionHeight;

        bool isNormal = window.CachedWindowState == DesktopWindowState.Normal;
        int captionTopPx = isNormal && computedCaptionHeight > resizeBorderPx ? resizeBorderPx : 0;
        RectInt32[] nextTopBorder = isNormal && totalWidthPx > 0 && captionTopPx > 0
            ? [new RectInt32(0, 0, totalWidthPx, captionTopPx)]
            : [];
        RectInt32[] nextCaption = totalWidthPx > 0 && computedCaptionHeight > captionTopPx
            ? [new RectInt32(0, captionTopPx, totalWidthPx, computedCaptionHeight - captionTopPx)]
            : [];

        IEnumerable<FrameworkElement> activeControls = isHeaderCovered()
            ? overlayControls
            : headerControls.Concat(overlayControls);

        var nextControls = new List<PassthroughControlSnapshot>();
        var nextPassthrough = new List<RectInt32>();
        foreach (var rootControl in activeControls)
        {
            if (!rootControl.IsLoaded || !DesktopVisual.IsVisible(rootControl)) continue;
            foreach (var control in ResolveInteractiveTargets(rootControl))
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
        passthroughControls = nextControls.ToArray();
        if (totalWidthPx > 0 && totalHeightPx > computedCaptionHeight)
            nextPassthrough.Add(new RectInt32(0, computedCaptionHeight, totalWidthPx, totalHeightPx - computedCaptionHeight));

        SyncRegion(NonClientRegionKind.TopBorder, ref topBorderRegions, nextTopBorder);
        SyncRegion(NonClientRegionKind.Caption, ref captionRegions, nextCaption);
        SyncRegion(NonClientRegionKind.Passthrough, ref passthroughRegions, nextPassthrough.ToArray());

        // Windows App SDK shrinks the InputNonClientPointerSource child HWND to the caption strip's
        // bounding box; matching the full client rect prevents window-picker tools from snapping a 64px bar.
        nint ncHwnd = NativeWin32.FindWindowEx(window.Handle, 0, "InputNonClientPointerSource", null);
        if (ncHwnd != 0 && totalWidthPx > 0 && totalHeightPx > 0)
            NativeWin32.SetWindowPos(ncHwnd, 0, 0, 0, totalWidthPx, totalHeightPx, SwpNoActivate | SwpNoZOrder);
        nint reunionHwnd = NativeWin32.FindWindowEx(window.Handle, 0, "ReunionWindowingCaptionControls", null);
        if (reunionHwnd != 0)
            NativeWin32.SetWindowPos(reunionHwnd, 0, 0, 0, 0, 0, SwpHideWindow | SwpNoActivate | SwpNoZOrder);
    }

    private void SyncRegion(NonClientRegionKind kind, ref RectInt32[] current, RectInt32[] next)
    {
        if (current.SequenceEqual(next)) return;
        current = next;
        if (next.Length == 0) nonClientInput.ClearRegionRects(kind);
        else nonClientInput.SetRegionRects(kind, next);
    }

    private static IEnumerable<FrameworkElement> ResolveInteractiveTargets(FrameworkElement rootControl) => rootControl switch
    {
        DesktopPageHeader pageHeader => pageHeader.CaptionInteractiveRegions,
        DesktopModalHost modalHost => modalHost.ActiveContent is { } active ? [active] : [],
        CloseWindowDialog closeDialog => [closeDialog.SurfaceElement],
        UserDataResetDialog resetDialog => [resetDialog.SurfaceElement],
        _ => [rootControl]
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        titleBar.SizeChanged -= Changed;
        titleBar.Loaded -= Loaded;
        window.AppWindow.Changed -= WindowChanged;
        foreach (var control in headerControls.Concat(overlayControls))
        {
            control.SizeChanged -= Changed;
            if (control is DesktopPageHeader pageHeader)
            {
                pageHeader.ContributionChanged -= HeaderChanged;
                pageHeader.InteractiveRegionsChanged -= HeaderChanged;
            }
        }
        foreach (var observation in visibility) observation.Dispose();
        visibility.Clear();
        if (root is not null) root.Changed -= RootChanged;
        nonClientInput.ClearRegionRects(NonClientRegionKind.TopBorder);
        nonClientInput.ClearRegionRects(NonClientRegionKind.Caption);
        nonClientInput.ClearRegionRects(NonClientRegionKind.Passthrough);
    }
}

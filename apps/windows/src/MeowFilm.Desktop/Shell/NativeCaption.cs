using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MeowFilm.Desktop;

/// <summary>Native caption buttons/Snap and drag regions of the one application window.</summary>
internal sealed class NativeCaption : IDisposable
{
    private readonly DesktopWindow window;
    private readonly FrameworkElement titleBar;
    private readonly FrameworkElement header;
    private readonly Func<bool> isFullSurfaceOpen;
    private readonly FrameworkElement[] headerControls;
    private readonly FrameworkElement[] overlayControls;
    private readonly InputNonClientPointerSource input;
    private readonly List<IDisposable> visibility = [];
    private XamlRoot? root;
    private bool queued, disposed;

    internal NativeCaption(
        DesktopWindow window,
        FrameworkElement titleBar,
        FrameworkElement header,
        Func<bool> isFullSurfaceOpen,
        FrameworkElement[] headerControls,
        FrameworkElement[] overlayControls)
    {
        this.window = window;
        this.titleBar = titleBar;
        this.header = header;
        this.isFullSurfaceOpen = isFullSurfaceOpen;
        this.headerControls = headerControls;
        this.overlayControls = overlayControls;
        // Use AppWindow.TitleBar + InputNonClientPointerSource exclusively without Window.SetTitleBar
        // so WinUI does not create a conflicting custom titlebar drag bridge or separate caption child HWND.
        window.AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        input = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        titleBar.SizeChanged += Changed;
        titleBar.Loaded += Loaded;
        foreach (var control in headerControls.Concat(overlayControls))
        {
            control.SizeChanged += Changed;
            visibility.Add(DesktopVisual.ObserveVisibility(control, _ => QueueUpdate()));
            if (control is DesktopPageHeader pageHeader) pageHeader.ContributionChanged += HeaderChanged;
        }
        window.AppWindow.Changed += WindowChanged;
        Update();
    }

    internal void Refresh() => QueueUpdate();

    internal (RectInt32[] Caption, RectInt32[] Passthrough) InspectRegions() => (captionRegions, passthroughRegions);

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
        if (!args.DidSizeChange && !args.DidPresenterChange && !args.DidVisibilityChange) return;
        QueueUpdate();
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueUpdate();
    private void QueueUpdate()
    {
        if (queued || disposed) return;
        queued = titleBar.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        { queued = false; if (!disposed) Update(); });
    }

    private RectInt32[] captionRegions = [];
    private RectInt32[] passthroughRegions = [];

    private void Update()
    {
        if (titleBar.XamlRoot is null) return;
        double scale = titleBar.XamlRoot.RasterizationScale;
        var titleBarInfo = window.AppWindow.TitleBar;
        double titleBarHeightDip = titleBarInfo.Height / scale;
        if (titleBarHeightDip > 0 && header.Height != titleBarHeightDip) header.Height = titleBarHeightDip;

        int totalWidthPx = window.AppWindow.ClientSize.Width;
        if (totalWidthPx <= 0) totalWidthPx = (int)Math.Ceiling(window.Root.ActualWidth * scale);
        int leftInset = Math.Max(0, titleBarInfo.LeftInset);
        int rightInset = Math.Max(0, titleBarInfo.RightInset);
        int captionHeightPx = Math.Max(titleBarInfo.Height, (int)Math.Ceiling(titleBar.ActualHeight * scale));
        int captionWidthPx = Math.Max(0, totalWidthPx - leftInset - rightInset);
        RectInt32[] nextCaption = captionWidthPx > 0 && captionHeightPx > 0
            ? [new RectInt32(leftInset, 0, captionWidthPx, captionHeightPx)]
            : [];
        if (!captionRegions.SequenceEqual(nextCaption))
        {
            captionRegions = nextCaption;
            input.SetRegionRects(NonClientRegionKind.Caption, nextCaption);
        }

        // When a full-window surface (such as fullscreen player detail) covers the shell header,
        // exclude hidden-underneath header controls so the entire top caption strip remains draggable.
        IEnumerable<FrameworkElement> activeControls = isFullSurfaceOpen()
            ? overlayControls
            : headerControls.Concat(overlayControls);
        var nextPassthrough = activeControls
            .Where(control => control.IsLoaded && DesktopVisual.IsVisible(control))
            .SelectMany(control => control is DesktopPageHeader pageHeader ? pageHeader.CaptionInteractiveRegions : [control])
            .Where(control => control.IsLoaded && DesktopVisual.IsVisible(control) && control.ActualWidth > 0 && control.ActualHeight > 0)
            .Select(control =>
        {
            var bounds = control.TransformToVisual(window.Root).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
            return new RectInt32((int)(bounds.X * scale), (int)(bounds.Y * scale), (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale));
        }).Where(rect => rect.Width > 0 && rect.Height > 0 && rect.Y < captionHeightPx).ToArray();
        if (passthroughRegions.SequenceEqual(nextPassthrough)) return;
        passthroughRegions = nextPassthrough;
        input.SetRegionRects(NonClientRegionKind.Passthrough, nextPassthrough);
    }

    public void Dispose()
    {
        disposed = true;
        titleBar.SizeChanged -= Changed; titleBar.Loaded -= Loaded;
        window.AppWindow.Changed -= WindowChanged;
        foreach (var control in headerControls.Concat(overlayControls))
        {
            control.SizeChanged -= Changed;
            if (control is DesktopPageHeader pageHeader) pageHeader.ContributionChanged -= HeaderChanged;
        }
        foreach (var observation in visibility) observation.Dispose();
        if (root is not null) root.Changed -= RootChanged;
        input.ClearRegionRects(NonClientRegionKind.Caption);
        input.ClearRegionRects(NonClientRegionKind.Passthrough);
    }
}

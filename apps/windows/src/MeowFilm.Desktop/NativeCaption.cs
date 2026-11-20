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
    private readonly FrameworkElement[] controls;
    private readonly InputNonClientPointerSource input;
    private readonly List<IDisposable> visibility = [];
    private XamlRoot? root;
    private bool queued, disposed;
    internal NativeCaption(DesktopWindow window, FrameworkElement titleBar, FrameworkElement header, params FrameworkElement[] controls)
    {
        this.window = window; this.titleBar = titleBar; this.header = header; this.controls = controls;
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        input = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        titleBar.SizeChanged += Changed;
        titleBar.Loaded += Loaded;
        foreach (var control in controls)
        {
            control.SizeChanged += Changed;
            visibility.Add(DesktopVisual.ObserveVisibility(control, _ => QueueUpdate()));
            if (control is DesktopPageHeader pageHeader) pageHeader.ContributionChanged += HeaderChanged;
        }
        window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        window.AppWindow.Changed += WindowChanged;
        Update();
    }
    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (root is not null) root.Changed -= RootChanged;
        root = titleBar.XamlRoot;
        if (root is not null) root.Changed += RootChanged;
        QueueUpdate();
    }
    private void Changed(object sender, SizeChangedEventArgs args) => QueueUpdate();
    private void HeaderChanged(object? sender, EventArgs args) => QueueUpdate();
    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => QueueUpdate();
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueUpdate();
    private void QueueUpdate()
    {
        if (queued || disposed) return;
        queued = titleBar.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        { queued = false; if (!disposed) Update(); });
    }
    private RectInt32[] regions = [];
    private void Update()
    {
        if (titleBar.XamlRoot is null) return;
        double scale = titleBar.XamlRoot.RasterizationScale;
        double height = window.AppWindow.TitleBar.Height / scale;
        if (height > 0 && header.Height != height) header.Height = height;
        var next = controls.Where(control => control.IsLoaded && DesktopVisual.IsVisible(control))
            .SelectMany(control => control is DesktopPageHeader pageHeader ? pageHeader.CaptionInteractiveRegions : [control])
            .Where(control => control.IsLoaded && DesktopVisual.IsVisible(control)).Select(control =>
        {
            var bounds = control.TransformToVisual(window.Root).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
            return new RectInt32((int)(bounds.X * scale), (int)(bounds.Y * scale), (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale));
        }).ToArray();
        if (regions.SequenceEqual(next)) return;
        regions = next;
        input.SetRegionRects(NonClientRegionKind.Passthrough, next);
    }
    public void Dispose()
    {
        disposed = true;
        titleBar.SizeChanged -= Changed; titleBar.Loaded -= Loaded;
        window.AppWindow.Changed -= WindowChanged;
        foreach (var control in controls)
        {
            control.SizeChanged -= Changed;
            if (control is DesktopPageHeader pageHeader) pageHeader.ContributionChanged -= HeaderChanged;
        }
        foreach (var observation in visibility) observation.Dispose();
        if (root is not null) root.Changed -= RootChanged;
        input.ClearRegionRects(NonClientRegionKind.Passthrough);
    }
}

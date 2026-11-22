using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Desktop;

// WinUI computes the clipped viewport during layout. Consumers react only when
// their slot enters or leaves it, rather than walking the visual tree each frame.
public sealed class DesktopViewport : IDisposable
{
    private static event Action<DependencyObject>? TransformSettled;

    private readonly FrameworkElement element;
    private readonly Action changed;
    private readonly IDisposable visibility;
    private Rect viewport;
    private bool hasViewport, nativeInViewport;
    private readonly List<FrameworkElement> clipOwners = [];
    private readonly List<(DependencyObject Owner, DependencyProperty Property, long Token)> geometrySubscriptions = [];
    private bool clipsVisible = true, clipsQueued, geometryChanged, loaded, disposed, transformCheckPending;
    private double lastWidth, lastHeight;
    public bool IsVisible { get; private set; }
    public bool IsInViewport { get; private set; }

    /// <summary>Notifies viewports inside a translated/animated container (such as the queue drawer) once its transform settles.</summary>
    public static void NotifyTransformSettled(DependencyObject container) => TransformSettled?.Invoke(container);

    public DesktopViewport(FrameworkElement element, Action changed)
    {
        this.element = element; this.changed = changed;
        element.EffectiveViewportChanged += ViewportChanged;
        element.SizeChanged += SizeChanged;
        element.Unloaded += Unloaded;
        loaded = element.IsLoaded;
        if (loaded)
        {
            TransformSettled += OnTransformSettled;
            AttachGeometry();
        }
        else element.Loaded += Loaded;
        visibility = DesktopVisual.ObserveVisibility(element, value =>
        {
            bool previous = IsVisible;
            IsVisible = value;
            if (value && !previous) transformCheckPending = true;
            Publish(previous != value);
        });
    }
    private void ViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        viewport = args.EffectiveViewport;
        hasViewport = true;
        Publish();
    }
    private void SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if ((lastWidth <= 0 || lastHeight <= 0) && args.NewSize.Width > 0 && args.NewSize.Height > 0)
            transformCheckPending = true;
        lastWidth = args.NewSize.Width;
        lastHeight = args.NewSize.Height;
        RefreshClips();
        Publish();
    }
    private void Loaded(object sender, RoutedEventArgs args)
    {
        element.Loaded -= Loaded;
        loaded = true;
        transformCheckPending = true;
        TransformSettled -= OnTransformSettled;
        TransformSettled += OnTransformSettled;
        AttachGeometry();
        if (clipOwners.Count > 0) QueueClips();
        Publish();
    }
    private void Unloaded(object sender, RoutedEventArgs args)
    {
        loaded = false;
        TransformSettled -= OnTransformSettled;
        DetachGeometry();
        element.Loaded += Loaded;
        hasViewport = false;
        transformCheckPending = false;
        Publish();
    }
    private void OnTransformSettled(DependencyObject container)
    {
        if (disposed || !loaded || !IsVisible) return;
        if (!DesktopVisual.Contains(container, element)) return;
        transformCheckPending = true;
        RefreshClips();
        Publish();
    }
    private void AttachGeometry()
    {
        DetachGeometry();
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement ancestor)
            {
                if (!ReferenceEquals(ancestor, element))
                {
                    Observe(ancestor, UIElement.ClipProperty, rebuild: true);
                    if (ancestor.Clip is { } clip)
                    {
                        clipOwners.Add(ancestor);
                        Observe(clip, RectangleGeometry.RectProperty);
                    }
                }
                if (VisualTreeHelper.GetParent(ancestor) is Canvas)
                {
                    Observe(ancestor, Canvas.TopProperty); Observe(ancestor, Canvas.LeftProperty);
                }
            }
        RefreshClips();
    }
    private void Observe(DependencyObject owner, DependencyProperty property, bool rebuild = false) =>
        geometrySubscriptions.Add((owner, property, owner.RegisterPropertyChangedCallback(property, (_, _) =>
        { geometryChanged |= rebuild; QueueClips(); })));
    private void QueueClips()
    {
        if (clipsQueued || !loaded || disposed) return;
        clipsQueued = element.DispatcherQueue.TryEnqueue(() =>
        {
            if (!loaded || disposed) { clipsQueued = false; return; }
            if (geometryChanged) { geometryChanged = false; AttachGeometry(); }
            else RefreshClips();
            clipsQueued = false;
            Publish();
        });
    }
    private void RefreshClips()
    {
        clipsVisible = true;
        if (clipOwners.Count == 0 || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        Rect bounds = new(0, 0, element.ActualWidth, element.ActualHeight);
        UIElement from = element;
        foreach (var owner in clipOwners)
        {
            bounds = from.TransformToVisual(owner).TransformBounds(bounds); from = owner;
            bounds.Intersect(owner.Clip.Rect);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) { clipsVisible = false; return; }
        }
    }
    private void DetachGeometry()
    {
        foreach (var (owner, property, token) in geometrySubscriptions) owner.UnregisterPropertyChangedCallback(property, token);
        geometrySubscriptions.Clear(); clipOwners.Clear();
    }
    private void Publish(bool visibilityChanged = false)
    {
        bool current = false;
        if (loaded && !clipsQueued && IsVisible && hasViewport && element.ActualWidth > 0 && element.ActualHeight > 0)
        {
            var visible = viewport;
            visible.Intersect(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            current = !visible.IsEmpty && visible.Width > 0 && visible.Height > 0;
        }
        if (!current && loaded && !clipsQueued && IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0 &&
            (visibilityChanged || !hasViewport || transformCheckPending))
        {
            transformCheckPending = false;
            current = IsVisibleInRoot();
        }
        else if (current)
        {
            transformCheckPending = false;
        }
        if (nativeInViewport != current) { nativeInViewport = current; RefreshClips(); }
        current &= clipsVisible;
        if (current == IsInViewport && !visibilityChanged) return;
        IsInViewport = current; changed();
    }
    private bool IsVisibleInRoot()
    {
        if (element.XamlRoot is not { Content: UIElement rootContent } xamlRoot) return false;
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        Rect rootBounds;
        try { rootBounds = element.TransformToVisual(rootContent).TransformBounds(bounds); }
        catch (ArgumentException) { return false; }
        rootBounds.Intersect(new Rect(0, 0, xamlRoot.Size.Width, xamlRoot.Size.Height));
        if (rootBounds.IsEmpty || rootBounds.Width <= 0 || rootBounds.Height <= 0) return false;
        for (DependencyObject? node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroll && scroll.ActualWidth > 0 && scroll.ActualHeight > 0)
            {
                try
                {
                    var inScroll = element.TransformToVisual(scroll).TransformBounds(bounds);
                    inScroll.Intersect(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
                    if (inScroll.IsEmpty || inScroll.Width <= 0 || inScroll.Height <= 0) return false;
                }
                catch (ArgumentException) { return false; }
            }
        }
        return true;
    }
    public void Dispose()
    {
        disposed = true;
        TransformSettled -= OnTransformSettled;
        element.EffectiveViewportChanged -= ViewportChanged;
        element.SizeChanged -= SizeChanged;
        element.Unloaded -= Unloaded;
        element.Loaded -= Loaded; DetachGeometry();
        visibility.Dispose();
    }
}

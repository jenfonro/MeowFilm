using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Desktop;

// WinUI computes the clipped viewport during layout. Consumers react only when
// their slot enters or leaves it, rather than walking the visual tree each frame.
public sealed class DesktopViewport : IDisposable
{
    private readonly FrameworkElement element;
    private readonly Action changed;
    private readonly IDisposable visibility;
    private Rect viewport;
    private bool hasViewport, nativeInViewport;
    private readonly List<FrameworkElement> clipOwners = [];
    private readonly List<(DependencyObject Owner, DependencyProperty Property, long Token)> geometrySubscriptions = [];
    private bool clipsVisible = true, clipsQueued, geometryChanged, loaded, disposed;
    public bool IsVisible { get; private set; }
    public bool IsInViewport { get; private set; }

    public DesktopViewport(FrameworkElement element, Action changed)
    {
        this.element = element; this.changed = changed;
        element.EffectiveViewportChanged += ViewportChanged;
        element.SizeChanged += SizeChanged;
        element.Unloaded += Unloaded;
        loaded = element.IsLoaded;
        if (loaded) AttachGeometry();
        else element.Loaded += Loaded;
        visibility = DesktopVisual.ObserveVisibility(element, value =>
        {
            bool previous = IsVisible;
            IsVisible = value;
            // The first arrange of a formerly collapsed element can leave its
            // unchanged Canvas ancestors clean. WinUI needs an ancestor layout
            // pass to publish that element's first effective viewport.
            if (value && !previous && !hasViewport)
                (element.XamlRoot?.Content as UIElement)?.InvalidateArrange();
            Publish(previous != value);
        });
    }
    private void ViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        viewport = args.EffectiveViewport; hasViewport = true; Publish();
    }
    private void SizeChanged(object sender, SizeChangedEventArgs args) { RefreshClips(); Publish(); }
    private void Loaded(object sender, RoutedEventArgs args)
    {
        element.Loaded -= Loaded; loaded = true; AttachGeometry();
        if (clipOwners.Count > 0) QueueClips();
    }
    private void Unloaded(object sender, RoutedEventArgs args)
    {
        loaded = false; DetachGeometry(); element.Loaded += Loaded;
        hasViewport = false; Publish();
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
            // Attached Canvas positions invalidate the parent's arrange and need
            // not raise EffectiveViewportChanged on an otherwise unchanged child.
            (element.XamlRoot?.Content as FrameworkElement)?.UpdateLayout();
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
        if (nativeInViewport != current) { nativeInViewport = current; RefreshClips(); }
        current &= clipsVisible;
        if (current == IsInViewport && !visibilityChanged) return;
        IsInViewport = current; changed();
    }
    public void Dispose()
    {
        disposed = true;
        element.EffectiveViewportChanged -= ViewportChanged;
        element.SizeChanged -= SizeChanged;
        element.Unloaded -= Unloaded;
        element.Loaded -= Loaded; DetachGeometry();
        visibility.Dispose();
    }
}

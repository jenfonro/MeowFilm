using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Desktop;

// One-shot viewport admission + live ancestor visibility.
// Subscribes WinUI EffectiveViewportChanged/SizeChanged only while active and unadmitted,
// unsubscribing immediately upon first viewport entry so scrolling incurs zero per-frame callbacks.
public sealed class DesktopViewport : IDisposable
{
    private static readonly List<DesktopViewport> TransformListeners = [];

    private readonly FrameworkElement element;
    private readonly Action changed;
    private readonly IDisposable visibility;
    private int transformIndex = -1;
    private bool active, tracking, ready, disposed;

    public bool IsVisible { get; private set; }
    public bool Admitted { get; private set; }

    public static void NotifyTransformSettled(DependencyObject container)
    {
        for (int i = TransformListeners.Count - 1; i >= 0; i--)
            if (i < TransformListeners.Count) TransformListeners[i].OnTransformSettled(container);
    }

    public static bool IsInViewport(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0 || !DesktopVisual.IsVisible(element))
            return false;
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

    public DesktopViewport(FrameworkElement element, Action changed, bool active = true)
    {
        this.element = element;
        this.changed = changed;
        this.active = active;
        element.Unloaded += OnUnloaded;
        visibility = DesktopVisual.ObserveVisibility(element, value =>
        {
            bool becameVisible = value && !IsVisible;
            IsVisible = value;
            if (!ready) return;
            if (becameVisible && this.active && !Admitted && (element.ActualWidth <= 0 || element.ActualHeight <= 0))
                element.DispatcherQueue?.TryEnqueue(() => { if (!disposed) { Sync(this.active); this.changed(); } });
            Sync(this.active);
            this.changed();
        });
        ready = true;
        Sync(active);
    }

    public void Reset(bool active = true)
    {
        Admitted = false;
        Sync(active);
    }

    public void Sync(bool active)
    {
        if (disposed) return;
        this.active = active;
        if (!active || !element.IsLoaded)
        {
            if (!active) Admitted = false;
            SetTracking(active && !Admitted);
            return;
        }
        if (!Admitted && IsInViewport(element))
        {
            Admitted = true;
            SetTracking(false);
            return;
        }
        SetTracking(!Admitted);
    }

    private void SetTracking(bool want)
    {
        if (want && !tracking)
        {
            element.EffectiveViewportChanged += OnViewportChanged;
            element.SizeChanged += OnSizeChanged;
            if (element.IsLoaded) AddTransformListener();
            else element.Loaded += OnLoaded;
            tracking = true;
        }
        else if (!want && tracking)
        {
            element.EffectiveViewportChanged -= OnViewportChanged;
            element.SizeChanged -= OnSizeChanged;
            element.Loaded -= OnLoaded;
            RemoveTransformListener();
            tracking = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        element.Loaded -= OnLoaded;
        if (disposed || !tracking) return;
        AddTransformListener();
        TryAdmit();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (element.IsLoaded) { Sync(active); return; }
        Admitted = false;
        RemoveTransformListener();
        if (tracking) { element.Loaded -= OnLoaded; element.Loaded += OnLoaded; }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => TryAdmit();

    private void OnViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (disposed || Admitted || !element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var visible = args.EffectiveViewport;
        visible.Intersect(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0 || !IsVisible) return;
        CompleteAdmit();
    }

    private void OnTransformSettled(DependencyObject container)
    { if (!disposed && !Admitted && element.IsLoaded && DesktopVisual.Contains(container, element)) TryAdmit(); }

    private void TryAdmit() { if (!disposed && !Admitted && IsInViewport(element)) CompleteAdmit(); }
    private void CompleteAdmit() { Admitted = true; SetTracking(false); changed(); }
    private void AddTransformListener() { if (transformIndex < 0) { transformIndex = TransformListeners.Count; TransformListeners.Add(this); } }

    private void RemoveTransformListener()
    {
        if (transformIndex < 0) return;
        int last = TransformListeners.Count - 1;
        if (transformIndex != last) { var moved = TransformListeners[last]; TransformListeners[transformIndex] = moved; moved.transformIndex = transformIndex; }
        TransformListeners.RemoveAt(last);
        transformIndex = -1;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SetTracking(false);
        element.Unloaded -= OnUnloaded;
        visibility.Dispose();
    }
}

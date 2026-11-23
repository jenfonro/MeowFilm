using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Capabilities;

// The HWND shape follows the existing XAML ink. It neither presents a second
// image nor reads the animated highlight back on each playback frame.
internal sealed class MusicDesktopLyricsInputRegion : IDisposable
{
    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached(
        "Owner", typeof(MusicDesktopLyricsInputRegion), typeof(MusicDesktopLyricsInputRegion), new PropertyMetadata(null));
    private readonly Dictionary<FrameworkElement, MusicLyricOutline> registered = [];
    private XamlRoot? xamlRoot;
    private readonly nint window;
    private readonly FrameworkElement root;
    private readonly Func<bool> wholeWindow;
    private sealed record RegionGeometry(Size Size, double Scale, InkGeometry[] Ink)
    {
        internal bool Matches(RegionGeometry? other) => other is not null && Size == other.Size &&
            Scale == other.Scale && Ink.AsSpan().SequenceEqual(other.Ink);
    }
    private readonly record struct InkGeometry(byte[] Alpha, int Width, int Height, Point Origin, Point Horizontal, Point Vertical);
    private RegionGeometry? requested;
    private long revision;
    private bool queued, running, disposed, full = true;

    internal MusicDesktopLyricsInputRegion(nint window, FrameworkElement root, Func<bool> wholeWindow)
    {
        this.window = window; this.root = root; this.wholeWindow = wholeWindow;
        root.SetValue(OwnerProperty, this);
        root.Loaded += Loaded; root.Unloaded += Unloaded; root.SizeChanged += SizeChanged;
    }
    internal static MusicDesktopLyricsInputRegion? Find(DependencyObject view)
    {
        for (DependencyObject? node = view; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node.GetValue(OwnerProperty) is MusicDesktopLyricsInputRegion owner) return owner;
        return null;
    }
    internal void Register(FrameworkElement view, MusicLyricOutline outline)
    {
        if (disposed || !registered.TryAdd(view, outline)) return;
        outline.InkChanged += InkChanged;
        Refresh();
    }
    internal void Unregister(FrameworkElement view)
    {
        if (!registered.Remove(view, out var outline)) return;
        outline.InkChanged -= InkChanged;
        Refresh();
    }
    private void InkChanged(object? sender, EventArgs args) => Refresh();
    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (xamlRoot is not null) xamlRoot.Changed -= RootChanged;
        xamlRoot = root.XamlRoot;
        if (xamlRoot is not null) xamlRoot.Changed += RootChanged;
        Refresh();
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Refresh();
    private void SizeChanged(object sender, SizeChangedEventArgs args) => Refresh();
    private void Unloaded(object sender, RoutedEventArgs args)
    {
        if (xamlRoot is not null) xamlRoot.Changed -= RootChanged;
        xamlRoot = null;
        revision++; requested = null;
    }

    internal void Refresh()
    {
        if (disposed) return;
        if (!root.IsLoaded || root.XamlRoot?.IsHostVisible != true)
        { revision++; requested = null; return; }
        if (wholeWindow())
        {
            if (requested is null && full) return;
            revision++; requested = null; TryReset(); return;
        }
        if (queued || running)
        {
            revision++; requested = null; TryReset(); return;
        }
        var geometry = CaptureGeometry();
        if (geometry.Matches(requested)) return;
        requested = geometry; revision++;
        // Do not leave an old glyph shape clipping new text while its native
        // layout and outline are being completed.
        TryReset();
        queued = true;
        if (!root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            queued = false;
            if (disposed) return;
            running = true;
            try { await UpdateLatestAsync(); }
            catch { if (!disposed) TryReset(); }
            finally { running = false; }
        })) queued = false;
    }
    private async Task UpdateLatestAsync()
    {
        long captured;
        do
        {
            captured = revision;
            await UpdateAsync();
        } while (!disposed && captured != revision && root.IsLoaded && root.XamlRoot?.IsHostVisible == true && !wholeWindow());
    }
    private async Task UpdateAsync()
    {
        long current = revision;
        if (!CanApply(current)) return;
        // Keep the whole surface available until the new glyph layout is ready.
        Reset();
        MusicLyricOutline[] outlines;
        int passes = 0;
        do
        {
            root.UpdateLayout();
            outlines = registered.Where(pair => MeowFilm.Desktop.DesktopVisual.IsVisible(pair.Key) && EffectiveOpacity(pair.Key) > 0)
                .Select(pair => pair.Value).ToArray();
            await Task.WhenAll(outlines.Select(outline => outline.EnsureCurrentAsync()));
            if (!CanApply(current)) { Refresh(); return; }
            // Publishing a new outline image can itself change arranged bounds.
            // Only map coverage after that layout and every resulting raster finish.
            root.UpdateLayout();
        } while (++passes < 4 && outlines.Any(outline => !outline.IsCurrent));
        var geometry = CaptureGeometry();
        if (requested is not null && !geometry.Matches(requested)) { requested = null; Refresh(); return; }
        requested = geometry;
        double scale = root.XamlRoot!.RasterizationScale;
        int width = (int)Math.Ceiling(root.ActualWidth * scale), height = (int)Math.Ceiling(root.ActualHeight * scale);
        var coverage = new byte[checked(width * height)];
        var sources = registered.Values.Select(outline => outline.InputInk)
            .OfType<MusicLyricOutline.InkSnapshot>().Where(ink => ink.Surface.IsLoaded &&
                MeowFilm.Desktop.DesktopVisual.IsVisible(ink.Surface) && EffectiveOpacity(ink.Surface) > 0).ToArray();
        foreach (var ink in sources)
        {
            var source = ink.Surface;
            int inkWidth = ink.Width, inkHeight = ink.Height;
            if (source.ActualWidth <= 0 || source.ActualHeight <= 0 || inkWidth <= 0 || inkHeight <= 0) continue;
            if (!CanApply(current)) { Refresh(); return; }
            var transform = source.TransformToVisual(root);
            var inverse = transform.Inverse;
            if (inverse is null) continue;
            var bounds = transform.TransformBounds(new Rect(0, 0, source.ActualWidth, source.ActualHeight));
            // A XAML transform is affine here. Sample its basis once instead of
            // crossing WinRT for every destination pixel.
            var origin = inverse.TransformPoint(new Point(.5 / scale, .5 / scale));
            var horizontal = inverse.TransformPoint(new Point(1.5 / scale, .5 / scale));
            var vertical = inverse.TransformPoint(new Point(.5 / scale, 1.5 / scale));
            double dxX = horizontal.X - origin.X, dxY = horizontal.Y - origin.Y;
            double dyX = vertical.X - origin.X, dyY = vertical.Y - origin.Y;
            for (int y = Math.Max(0, (int)Math.Floor(bounds.Top * scale)); y < Math.Min(height, (int)Math.Ceiling(bounds.Bottom * scale)); y++)
            for (int x = Math.Max(0, (int)Math.Floor(bounds.Left * scale)); x < Math.Min(width, (int)Math.Ceiling(bounds.Right * scale)); x++)
            {
                double localX = origin.X + x * dxX + y * dyX;
                double localY = origin.Y + x * dxY + y * dyY;
                // Image interpolation uses the four texels around the source
                // pixel center. Include their footprint so region clipping cannot
                // remove a rendered antialiased edge after Viewbox scaling.
                int sx = (int)Math.Floor(localX * inkWidth / source.ActualWidth - .5);
                int sy = (int)Math.Floor(localY * inkHeight / source.ActualHeight - .5);
                byte alpha = 0;
                for (int iy = Math.Max(0, sy); iy <= Math.Min(inkHeight - 1, sy + 1); iy++)
                for (int ix = Math.Max(0, sx); ix <= Math.Min(inkWidth - 1, sx + 1); ix++)
                    alpha = Math.Max(alpha, ink.Alpha[iy * inkWidth + ix]);
                int at = y * width + x;
                coverage[at] = Math.Max(coverage[at], alpha);
            }
        }
        if (!CanApply(current)) { Refresh(); return; }
        if (!CaptureGeometry().Matches(requested)) { requested = null; Refresh(); return; }
        Apply(coverage, width, height);
    }
    private bool CanApply(long current) => !disposed && current == revision && root.IsLoaded &&
        root.XamlRoot?.IsHostVisible == true && !wholeWindow();
    private RegionGeometry CaptureGeometry()
    {
        var inkGeometry = new List<InkGeometry>();
        foreach (var (view, outline) in registered)
        {
            if (!MeowFilm.Desktop.DesktopVisual.IsVisible(view) || EffectiveOpacity(view) <= 0 || outline.InputInk is not { } ink) continue;
            var transform = ink.Surface.TransformToVisual(root);
            var origin = transform.TransformPoint(new Point());
            var horizontal = transform.TransformPoint(new Point(ink.Surface.ActualWidth, 0));
            var vertical = transform.TransformPoint(new Point(0, ink.Surface.ActualHeight));
            inkGeometry.Add(new(ink.Alpha, ink.Width, ink.Height, origin, horizontal, vertical));
        }
        return new(new(root.ActualWidth, root.ActualHeight), root.XamlRoot!.RasterizationScale, inkGeometry.ToArray());
    }
    private double EffectiveOpacity(DependencyObject view)
    {
        double opacity = 1;
        for (DependencyObject? owner = view; owner is not null; owner = VisualTreeHelper.GetParent(owner))
        {
            if (owner is UIElement element) opacity *= element.Opacity;
            if (ReferenceEquals(owner, root)) break;
        }
        return opacity;
    }
    private void Reset()
    {
        if (full) return;
        if (SetWindowRgn(window, 0, true) == 0) throw new Win32Exception();
        full = true;
    }
    private void TryReset()
    {
        try { Reset(); }
        catch (Win32Exception) { }
    }
    private void Apply(byte[] coverage, int width, int height)
    {
        var origin = new NativePoint();
        var xaml = root.XamlRoot!;
        var islandWindow = Microsoft.UI.Win32Interop.GetWindowFromWindowId(xaml.ContentIslandEnvironment.AppWindowId);
        if (!GetWindowRect(window, out var bounds) || !ClientToScreen(islandWindow, ref origin)) throw new Win32Exception();
        var visualOrigin = root.TransformToVisual(null).TransformPoint(new Point());
        int offsetX = (int)Math.Round(origin.X + visualOrigin.X * xaml.RasterizationScale) - bounds.Left;
        int offsetY = (int)Math.Round(origin.Y + visualOrigin.Y * xaml.RasterizationScale) - bounds.Top;
        var rectangles = new List<NativeRect>();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width;)
        {
            if (coverage[y * width + x] == 0) { x++; continue; }
            int left = x++;
            while (x < width && coverage[y * width + x] != 0) x++;
            rectangles.Add(new(left + offsetX, y + offsetY, x + offsetX, y + offsetY + 1));
        }
        int header = Marshal.SizeOf<RegionHeader>(), bytes = rectangles.Count * Marshal.SizeOf<NativeRect>();
        nint data = Marshal.AllocHGlobal(header + bytes);
        nint region = 0;
        try
        {
            Marshal.StructureToPtr(new RegionHeader
            {
                Size = (uint)header, Type = 1, Count = (uint)rectangles.Count, Bytes = (uint)bytes,
                Bounds = rectangles.Count == 0 ? new(0, 0, 0, 0) : new(rectangles.Min(rect => rect.Left),
                    rectangles[0].Top, rectangles.Max(rect => rect.Right), rectangles[^1].Bottom)
            }, data, false);
            for (int index = 0; index < rectangles.Count; index++) Marshal.StructureToPtr(rectangles[index], data + header + index * 16, false);
            region = rectangles.Count == 0 ? CreateRectRgn(0, 0, 0, 0) : ExtCreateRegion(0, (uint)(header + bytes), data);
            if (region == 0 || SetWindowRgn(window, region, true) == 0) throw new Win32Exception();
            region = 0; // successful SetWindowRgn transfers ownership to USER32
            full = false;
        }
        finally { if (region != 0) DeleteObject(region); Marshal.FreeHGlobal(data); }
    }
    public void Dispose()
    {
        disposed = true; revision++;
        root.Loaded -= Loaded; root.Unloaded -= Unloaded; root.SizeChanged -= SizeChanged;
        if (xamlRoot is not null) xamlRoot.Changed -= RootChanged;
        foreach (var outline in registered.Values) outline.InkChanged -= InkChanged;
        registered.Clear(); root.ClearValue(OwnerProperty);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct RegionHeader { internal uint Size, Type, Count, Bytes; internal NativeRect Bounds; }
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint ExtCreateRegion(nint transform, uint size, nint data);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}

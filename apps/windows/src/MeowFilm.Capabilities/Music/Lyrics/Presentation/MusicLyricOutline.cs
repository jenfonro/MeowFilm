using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace MeowFilm.Capabilities;

// The outside stroke uses the native hinted glyph coverage. The foreground and
// compositor highlight remain native text, not shifted duplicate text layers.
internal sealed class MusicLyricOutline
{
    private const int RasterCacheCapacity = 128;

    private readonly record struct RasterKey(string Text, string Family, double FontSize,
        string Language, FlowDirection Direction, double Width, double Height,
        double ScaleX, double ScaleY, double PhaseX, double PhaseY, double Thickness);

    private sealed record CachedRaster(byte[] Coverage, byte[] InputInk, int Width, int Height,
        double ScaleX, double ScaleY, int PadX, int PadY);

    private readonly record struct SampleWeight(int OffsetX, int OffsetY, float W00, float W10, float W01, float W11);

    private static readonly Dictionary<RasterKey, LinkedListNode<(RasterKey Key, CachedRaster Value)>> RasterCache = [];
    private static readonly LinkedList<(RasterKey Key, CachedRaster Value)> RasterLru = [];

    private readonly Grid host;
    private readonly Canvas layer = new() { IsHitTestVisible = false, FlowDirection = FlowDirection.LeftToRight };
    private readonly Image image = new() { IsHitTestVisible = false, Stretch = Stretch.Fill };
    private TextBlock? source;
    private FrameworkElement? captureSurface;
    private SolidColorBrush? stroke;
    private double thickness;
    private bool queued;
    private bool detached;
    private bool sourceSubscribed;
    private CancellationTokenSource? rendering;
    private IDisposable? visibility;
    private RasterKey? key;
    private RasterKey? failedKey;
    private (Windows.UI.Color Color, double Opacity)? paintedColor;
    private WriteableBitmap? paintedBitmap;
    private Rect? layoutBounds;
    private byte[]? coverage;
    private int width, height;
    private double scaleX, scaleY;
    internal Task RenderWork { get; private set; } = Task.CompletedTask;
    internal event EventHandler? InkChanged;
    internal bool IsCurrent => CurrentKey() is not { } current || !queued && (key == current || failedKey == current);
    internal Task EnsureCurrentAsync()
    {
        if (!IsCurrent) Queue();
        return RenderWork;
    }
    internal readonly record struct InkSnapshot(FrameworkElement Surface, byte[] Alpha, int Width, int Height);
    internal InkSnapshot? InputInk => key == CurrentKey() && inputInk is { } alpha ? new(image, alpha, width, height) : null;
    private byte[]? inputInk;

    internal MusicLyricOutline(Grid host, bool insertAtBottom = false)
    {
        this.host = host;
        layer.Children.Add(image);
        if (insertAtBottom && host.Children.Count > 0) host.Children.Insert(0, layer);
        else host.Children.Add(layer);
        layer.Loaded += OnLayerLoaded;
        layer.Unloaded += OnLayerUnloaded;
        if (layer.IsLoaded) AttachVisibility();
    }

    private void OnLayerLoaded(object sender, RoutedEventArgs e)
    {
        AttachSourceEvents();
        AttachVisibility();
    }

    private void OnLayerUnloaded(object sender, RoutedEventArgs e)
    {
        if (layer.IsLoaded) return;
        DetachSourceEvents();
        visibility?.Dispose();
        visibility = null;
        Clear();
    }

    private void AttachSourceEvents()
    {
        if (detached || !layer.IsLoaded || source is null || sourceSubscribed) return;
        source.SizeChanged += SizeChanged;
        source.LayoutUpdated += LayoutChanged;
        sourceSubscribed = true;
    }

    private void DetachSourceEvents()
    {
        if (source is null || !sourceSubscribed) return;
        source.SizeChanged -= SizeChanged;
        source.LayoutUpdated -= LayoutChanged;
        sourceSubscribed = false;
    }

    private void AttachVisibility()
    {
        if (detached) return;
        visibility?.Dispose();
        visibility = MeowFilm.Desktop.DesktopVisual.ObserveVisibility(layer, visible =>
        {
            if (visible) Queue();
            else Clear();
        });
    }

    internal void Detach()
    {
        if (detached) return;
        detached = true;
        layer.Loaded -= OnLayerLoaded;
        layer.Unloaded -= OnLayerUnloaded;
        visibility?.Dispose();
        visibility = null;
        DetachSourceEvents();
        source = null;
        captureSurface = null;
        stroke = null;
        thickness = 0;
        layoutBounds = null;
        Clear();
        host.Children.Remove(layer);
    }

    internal void Update(TextBlock text, FrameworkElement surface, Brush? brush, double strokeThickness)
    {
        if (detached) return;
        stroke = brush as SolidColorBrush; thickness = strokeThickness;
        if (stroke is null || thickness <= 0 || string.IsNullOrWhiteSpace(text.Text))
        {
            DetachSourceEvents();
            source = null; captureSurface = null; layoutBounds = null;
            Clear(); return;
        }
        if (!ReferenceEquals(source, text))
        {
            DetachSourceEvents();
            source = text;
            key = null;
            failedKey = null;
        }
        AttachSourceEvents();
        captureSurface = surface;
        if (CurrentKey() is { } current)
        {
            if (key == current) { Paint(); return; }
            if (TryGetCached(current, out var cached))
            {
                ApplyRaster(current, cached);
                return;
            }
        }
        Queue();
    }

    private void SizeChanged(object sender, SizeChangedEventArgs args) => Queue();
    private void LayoutChanged(object? sender, object args)
    {
        if (detached || source is null || captureSurface is null || stroke is null || thickness <= 0) return;
        var current = CurrentKey();
        if (key != current)
        {
            if (current is { } next && TryGetCached(next, out var cached))
                ApplyRaster(next, cached);
            else
                Queue();
        }
        if (InkChanged is null) return;
        Rect? bounds = captureSurface is { IsLoaded: true } surface
            ? surface.TransformToVisual(null).TransformBounds(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight)) : null;
        if (layoutBounds == bounds) return;
        layoutBounds = bounds;
        InkChanged?.Invoke(this, EventArgs.Empty);
    }
    private static double QuantizeScale(double value) => Math.Round(value * 1000.0) / 1000.0;
    private static double QuantizePhase(double value)
    {
        double mod = value % 1.0;
        if (mod < 0) mod += 1.0;
        return Math.Round(mod * 16.0) / 16.0;
    }
    private RasterKey? CurrentKey()
    {
        if (detached || source is not { IsLoaded: true, XamlRoot: { } root } text ||
            captureSurface is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } surface ||
            !MeowFilm.Desktop.DesktopVisual.IsVisible(layer) || stroke is null || thickness <= 0 || string.IsNullOrWhiteSpace(text.Text)) return null;
        var transform = surface.TransformToVisual(root.Content);
        var origin = transform.TransformPoint(new Point());
        var x = transform.TransformPoint(new Point(1, 0));
        var y = transform.TransformPoint(new Point(0, 1));
        double sx = QuantizeScale(Math.Sqrt(Math.Pow(x.X - origin.X, 2) + Math.Pow(x.Y - origin.Y, 2)) * root.RasterizationScale);
        double sy = QuantizeScale(Math.Sqrt(Math.Pow(y.X - origin.X, 2) + Math.Pow(y.Y - origin.Y, 2)) * root.RasterizationScale);
        return new(text.Text, text.FontFamily.Source, text.FontSize, text.Language, text.FlowDirection,
            Math.Round(surface.ActualWidth * 16.0) / 16.0, Math.Round(surface.ActualHeight * 16.0) / 16.0, sx, sy,
            QuantizePhase(origin.X * root.RasterizationScale), QuantizePhase(origin.Y * root.RasterizationScale), thickness);
    }
    private void Queue()
    {
        if (detached || queued || !layer.IsLoaded || stroke is null || thickness <= 0) return;
        if (CurrentKey() is { } current && TryGetCached(current, out var cached))
        {
            ApplyRaster(current, cached);
            return;
        }
        queued = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RenderWork = completion.Task;
        if (!layer.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                int attempts = 0;
                while (!detached && layer.IsLoaded && CurrentKey() is { } next && key != next && attempts++ < 4)
                {
                    if (!await RasterizeAsync()) break;
                }
                queued = false; completion.TrySetResult();
            }
            catch (Exception error) { queued = false; completion.TrySetException(error); }
        }))
        {
            queued = false;
            completion.TrySetException(new InvalidOperationException("Lyric outline queue stopped."));
        }
    }
    private async Task<bool> RasterizeAsync()
    {
        if (detached || CurrentKey() is not { } next || source is not { } text) { Clear(); return false; }
        if (key == next) { Paint(); return true; }
        if (TryGetCached(next, out var cached)) { ApplyRaster(next, cached); return true; }
        using var request = new CancellationTokenSource();
        rendering = request;
        var bitmap = new RenderTargetBitmap();
        int w = Math.Max(1, (int)Math.Ceiling(next.Width * next.ScaleX));
        int h = Math.Max(1, (int)Math.Ceiling(next.Height * next.ScaleY));
        IBuffer buffer;
        try
        {
            await bitmap.RenderAsync(captureSurface!, w, h).AsTask(request.Token);
            if (request.IsCancellationRequested || detached) return false;
            if (next != CurrentKey()) return true;
            buffer = await bitmap.GetPixelsAsync().AsTask(request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
        catch (System.Runtime.InteropServices.COMException error) when (error.HResult == unchecked((int)0x80004004) &&
            (request.IsCancellationRequested || detached || CurrentKey() is null)) { return false; }
        finally { if (ReferenceEquals(rendering, request)) rendering = null; }
        if (request.IsCancellationRequested || detached) return false;
        if (next != CurrentKey()) return true;
        int pixelWidth = bitmap.PixelWidth, pixelHeight = bitmap.PixelHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0) { failedKey = next; return false; }
        byte[] pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        double opacity = text.Opacity * (text.Foreground is SolidColorBrush fill ? fill.Opacity * fill.Color.A / 255d : 1);
        CachedRaster result;
        try
        {
            result = await Task.Run(() => BuildRaster(pixels, pixelWidth, pixelHeight, next, opacity), request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
        if (request.IsCancellationRequested || detached) return false;
        if (next != CurrentKey()) return true;
        failedKey = null;
        StoreCached(next, result);
        ApplyRaster(next, result);
        return true;
    }

    private static CachedRaster BuildRaster(byte[] pixels, int pixelWidth, int pixelHeight, RasterKey next, double opacity)
    {
        double actualScaleX = pixelWidth / next.Width, actualScaleY = pixelHeight / next.Height;
        int padX = (int)Math.Ceiling(next.Thickness * actualScaleX / 2) + 2;
        int padY = (int)Math.Ceiling(next.Thickness * actualScaleY / 2) + 2;
        int rasterWidth = pixelWidth + padX * 2, rasterHeight = pixelHeight + padY * 2;
        var alpha = new byte[rasterWidth * rasterHeight];
        double invOpacity = opacity > 0 ? 1.0 / opacity : 0;
        for (int y = 0; y < pixelHeight; y++)
        {
            int dstRow = (y + padY) * rasterWidth + padX;
            int srcRow = y * pixelWidth * 4 + 3;
            for (int x = 0; x < pixelWidth; x++)
                alpha[dstRow + x] = invOpacity > 0
                    ? (byte)Math.Clamp(Math.Round(pixels[srcRow + x * 4] * invOpacity), 0, 255) : (byte)0;
        }
        var expandedCoverage = Expand(alpha, rasterWidth, rasterHeight, next.Thickness * actualScaleX / 2, next.Thickness * actualScaleY / 2);
        var combinedInk = new byte[alpha.Length];
        for (int index = 0; index < alpha.Length; index++)
            combinedInk[index] = (byte)(alpha[index] + (expandedCoverage[index] * (255 - alpha[index]) + 127) / 255);
        return new CachedRaster(expandedCoverage, combinedInk, rasterWidth, rasterHeight, actualScaleX, actualScaleY, padX, padY);
    }

    private void ApplyRaster(RasterKey next, CachedRaster raster)
    {
        width = raster.Width;
        height = raster.Height;
        scaleX = raster.ScaleX;
        scaleY = raster.ScaleY;
        coverage = raster.Coverage;
        inputInk = raster.InputInk;
        image.Width = width / scaleX;
        image.Height = height / scaleY;
        Canvas.SetLeft(image, -raster.PadX / scaleX);
        Canvas.SetTop(image, -raster.PadY / scaleY);
        key = next;
        failedKey = null;
        paintedColor = null;
        Paint();
        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryGetCached(RasterKey cacheKey, out CachedRaster raster)
    {
        if (RasterCache.TryGetValue(cacheKey, out var node))
        {
            RasterLru.Remove(node);
            RasterLru.AddFirst(node);
            raster = node.Value.Value;
            return true;
        }
        raster = default!;
        return false;
    }

    private static void StoreCached(RasterKey cacheKey, CachedRaster raster)
    {
        if (RasterCache.TryGetValue(cacheKey, out var existing))
        {
            RasterLru.Remove(existing);
            existing.Value = (cacheKey, raster);
            RasterLru.AddFirst(existing);
            return;
        }
        if (RasterCache.Count >= RasterCacheCapacity && RasterLru.Last is { } oldest)
        {
            RasterCache.Remove(oldest.Value.Key);
            RasterLru.RemoveLast();
        }
        var node = new LinkedListNode<(RasterKey, CachedRaster)>((cacheKey, raster));
        RasterLru.AddFirst(node);
        RasterCache[cacheKey] = node;
    }

    private void Paint()
    {
        if (coverage is null || stroke is null || thickness <= 0 || width <= 0 || height <= 0) return;
        if (paintedColor == (stroke.Color, stroke.Opacity)) return;
        var color = stroke.Color;
        var pixels = new byte[coverage.Length * 4];
        double alphaScale = color.A / 255d * stroke.Opacity;
        for (int i = 0; i < coverage.Length; i++)
        {
            byte cov = coverage[i];
            if (cov == 0) continue;
            int alpha = (int)Math.Round(cov * alphaScale);
            int offset = i * 4;
            pixels[offset] = (byte)((color.B * alpha + 127) / 255);
            pixels[offset + 1] = (byte)((color.G * alpha + 127) / 255);
            pixels[offset + 2] = (byte)((color.R * alpha + 127) / 255);
            pixels[offset + 3] = (byte)alpha;
        }
        if (paintedBitmap is null || paintedBitmap.PixelWidth != width || paintedBitmap.PixelHeight != height)
        {
            paintedBitmap = new WriteableBitmap(width, height);
            image.Source = paintedBitmap;
        }
        using (var stream = paintedBitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        paintedBitmap.Invalidate();
        paintedColor = (stroke.Color, stroke.Opacity);
    }

    private static byte[] Expand(byte[] alpha, int width, int height, double rx, double ry)
    {
        var rawSamples = new List<(double X, double Y)>(64);
        for (int i = 0; i < 16; i++) rawSamples.Add((Math.Cos(i * Math.PI / 8) * rx, Math.Sin(i * Math.PI / 8) * ry));
        if (rx > 0 && ry > 0)
        {
            double invRx2 = 1.0 / (rx * rx), invRy2 = 1.0 / (ry * ry);
            int irx = (int)rx, iry = (int)ry;
            for (int y = -iry; y <= iry; y++)
            for (int x = -irx; x <= irx; x++)
                if (x * x * invRx2 + y * y * invRy2 <= 1.0) rawSamples.Add((x, y));
        }
        var weights = new SampleWeight[rawSamples.Count];
        for (int i = 0; i < rawSamples.Count; i++)
        {
            var (dx, dy) = rawSamples[i];
            int ox = (int)Math.Floor(dx), oy = (int)Math.Floor(dy);
            float fx = (float)(dx - ox), fy = (float)(dy - oy);
            float ifx = 1f - fx, ify = 1f - fy;
            weights[i] = new(ox, oy, ifx * ify, fx * ify, ifx * fy, fx * fy);
        }
        var result = new byte[alpha.Length];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                int original = alpha[rowStart + x];
                if (original == 255) continue;
                float expanded = original;
                for (int s = 0; s < weights.Length; s++)
                {
                    var sample = weights[s];
                    int lx = x + sample.OffsetX, ly = y + sample.OffsetY;
                    float v = 0f;
                    if ((uint)ly < (uint)height)
                    {
                        int r0 = ly * width;
                        if ((uint)lx < (uint)width) v += alpha[r0 + lx] * sample.W00;
                        if ((uint)(lx + 1) < (uint)width) v += alpha[r0 + lx + 1] * sample.W10;
                    }
                    int ly1 = ly + 1;
                    if ((uint)ly1 < (uint)height)
                    {
                        int r1 = ly1 * width;
                        if ((uint)lx < (uint)width) v += alpha[r1 + lx] * sample.W01;
                        if ((uint)(lx + 1) < (uint)width) v += alpha[r1 + lx + 1] * sample.W11;
                    }
                    if (v > expanded) expanded = v;
                }
                if (expanded > original)
                    result[rowStart + x] = (byte)Math.Clamp(Math.Round((expanded - original) * 255.0 / (255 - original)), 0, 255);
            }
        }
        return result;
    }

    internal void Clear()
    {
        rendering?.Cancel();
        bool changed = key is not null || inputInk is not null;
        key = null; failedKey = null; paintedColor = null; paintedBitmap = null; coverage = inputInk = null; image.Source = null;
        if (changed) InkChanged?.Invoke(this, EventArgs.Empty);
    }
}

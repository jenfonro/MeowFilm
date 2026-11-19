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
    private readonly Canvas layer = new() { IsHitTestVisible = false, FlowDirection = FlowDirection.LeftToRight };
    private readonly Image image = new() { IsHitTestVisible = false, Stretch = Stretch.Fill };
    private TextBlock? source;
    private FrameworkElement? captureSurface;
    private SolidColorBrush? stroke;
    private double thickness;
    private long epoch;
    private bool queued;
    private RasterKey? key;
    private byte[]? coverage;
    private int width, height;
    private double scaleX, scaleY;
    private readonly record struct RasterKey(string Text, string Family, double FontSize,
        string Language, FlowDirection Direction, double Width, double Height,
        double ScaleX, double ScaleY, double PhaseX, double PhaseY, double Thickness);
    internal Task RenderWork { get; private set; } = Task.CompletedTask;
    internal bool IsCurrent => CurrentKey() is not { } current || !queued && key == current;
    internal Task EnsureCurrentAsync()
    {
        if (!IsCurrent) Queue();
        return RenderWork;
    }
    internal byte[]? Coverage => coverage;
    internal readonly record struct InkSnapshot(FrameworkElement Surface, byte[] Alpha, int Width, int Height);
    internal InkSnapshot? InputInk => key == CurrentKey() && inputInk is { } alpha ? new(image, alpha, width, height) : null;
    private byte[]? inputInk;

    internal MusicLyricOutline(Grid host)
    {
        layer.Children.Add(image); host.Children.Add(layer);
        layer.Loaded += (_, _) => Queue();
        layer.Unloaded += (_, _) => Clear();
    }

    internal void Update(TextBlock text, FrameworkElement surface, Brush? brush, double strokeThickness)
    {
        if (!ReferenceEquals(source, text))
        {
            if (source is not null) { source.SizeChanged -= SizeChanged; source.LayoutUpdated -= LayoutChanged; }
            source = text; text.SizeChanged += SizeChanged; text.LayoutUpdated += LayoutChanged;
            key = null;
        }
        captureSurface = surface;
        stroke = brush as SolidColorBrush; thickness = strokeThickness;
        if (stroke is null || thickness <= 0 || string.IsNullOrWhiteSpace(text.Text)) { Clear(); return; }
        if (key == CurrentKey()) Paint();
        else Queue();
    }

    private void SizeChanged(object sender, SizeChangedEventArgs args) => Queue();
    private void LayoutChanged(object? sender, object args)
    {
        if (key != CurrentKey()) Queue();
    }
    private RasterKey? CurrentKey()
    {
        if (source is not { IsLoaded: true, XamlRoot: { } root } text ||
            captureSurface is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } surface ||
            !root.IsHostVisible || stroke is null || thickness <= 0 || string.IsNullOrWhiteSpace(text.Text)) return null;
        var transform = surface.TransformToVisual(root.Content);
        var origin = transform.TransformPoint(new Point());
        var x = transform.TransformPoint(new Point(1, 0));
        var y = transform.TransformPoint(new Point(0, 1));
        double sx = Math.Sqrt(Math.Pow(x.X - origin.X, 2) + Math.Pow(x.Y - origin.Y, 2)) * root.RasterizationScale;
        double sy = Math.Sqrt(Math.Pow(y.X - origin.X, 2) + Math.Pow(y.Y - origin.Y, 2)) * root.RasterizationScale;
        return new(text.Text, text.FontFamily.Source, text.FontSize, text.Language, text.FlowDirection,
            surface.ActualWidth, surface.ActualHeight, sx, sy,
            origin.X * root.RasterizationScale % 1, origin.Y * root.RasterizationScale % 1, thickness);
    }
    private void Queue()
    {
        if (queued || !layer.IsLoaded || stroke is null || thickness <= 0) return;
        queued = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RenderWork = completion.Task;
        if (!layer.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                do { await RasterizeAsync(); }
                while (layer.IsLoaded && CurrentKey() is { } current && key != current);
                queued = false; completion.TrySetResult();
            }
            catch (Exception error) { queued = false; completion.TrySetException(error); throw; }
        }))
        {
            queued = false;
            completion.TrySetException(new InvalidOperationException("Lyric outline queue stopped."));
        }
    }
    private async Task RasterizeAsync()
    {
        if (CurrentKey() is not { } next || source is not { } text) { Clear(); return; }
        if (key == next) { Paint(); return; }
        long request = ++epoch;
        var bitmap = new RenderTargetBitmap();
        int w = Math.Max(1, (int)Math.Ceiling(next.Width * next.ScaleX));
        int h = Math.Max(1, (int)Math.Ceiling(next.Height * next.ScaleY));
        await bitmap.RenderAsync(captureSurface!, w, h);
        var buffer = await bitmap.GetPixelsAsync();
        if (request != epoch || !layer.IsLoaded || next != CurrentKey()) { Queue(); return; }
        byte[] pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;
        scaleX = bitmap.PixelWidth / next.Width; scaleY = bitmap.PixelHeight / next.Height;
        int padX = (int)Math.Ceiling(next.Thickness * scaleX / 2) + 2;
        int padY = (int)Math.Ceiling(next.Thickness * scaleY / 2) + 2;
        width = bitmap.PixelWidth + padX * 2; height = bitmap.PixelHeight + padY * 2;
        var alpha = new byte[width * height];
        double opacity = text.Opacity * (text.Foreground is SolidColorBrush fill ? fill.Opacity * fill.Color.A / 255d : 1);
        for (int y = 0; y < bitmap.PixelHeight; y++)
        for (int x = 0; x < bitmap.PixelWidth; x++)
            alpha[(y + padY) * width + x + padX] = opacity > 0
                ? (byte)Math.Clamp(Math.Round(pixels[(y * bitmap.PixelWidth + x) * 4 + 3] / opacity), 0, 255) : (byte)0;
        coverage = Expand(alpha, width, height, next.Thickness * scaleX / 2, next.Thickness * scaleY / 2);
        inputInk = new byte[alpha.Length];
        for (int index = 0; index < alpha.Length; index++)
            inputInk[index] = (byte)(alpha[index] + (coverage[index] * (255 - alpha[index]) + 127) / 255);
        image.Width = width / scaleX; image.Height = height / scaleY;
        Canvas.SetLeft(image, -padX / scaleX); Canvas.SetTop(image, -padY / scaleY);
        key = next; Paint();
    }
    private void Paint()
    {
        if (coverage is null || stroke is null || thickness <= 0) return;
        var color = stroke.Color; var pixels = new byte[coverage.Length * 4];
        for (int i = 0; i < coverage.Length; i++)
        {
            int alpha = (int)Math.Round(coverage[i] * color.A / 255d * stroke.Opacity);
            pixels[i * 4] = (byte)((color.B * alpha + 127) / 255);
            pixels[i * 4 + 1] = (byte)((color.G * alpha + 127) / 255);
            pixels[i * 4 + 2] = (byte)((color.R * alpha + 127) / 255);
            pixels[i * 4 + 3] = (byte)alpha;
        }
        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        bitmap.Invalidate(); image.Source = bitmap;
    }
    private static byte[] Expand(byte[] alpha, int width, int height, double rx, double ry)
    {
        var samples = new List<(double X, double Y)>();
        for (int i = 0; i < 16; i++) samples.Add((Math.Cos(i * Math.PI / 8) * rx, Math.Sin(i * Math.PI / 8) * ry));
        for (int y = -(int)ry; y <= ry; y++)
        for (int x = -(int)rx; x <= rx; x++)
            if (rx > 0 && ry > 0 && x * x / (rx * rx) + y * y / (ry * ry) <= 1) samples.Add((x, y));
        double At(int x, int y) => x >= 0 && x < width && y >= 0 && y < height ? alpha[y * width + x] : 0;
        var result = new byte[alpha.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int original = alpha[y * width + x];
            if (original == 255) continue;
            double expanded = original;
            foreach (var (dx, dy) in samples)
            {
                int left = (int)Math.Floor(x + dx), top = (int)Math.Floor(y + dy);
                double fx = x + dx - left, fy = y + dy - top;
                expanded = Math.Max(expanded, At(left, top) * (1 - fx) * (1 - fy) + At(left + 1, top) * fx * (1 - fy) +
                    At(left, top + 1) * (1 - fx) * fy + At(left + 1, top + 1) * fx * fy);
            }
            result[y * width + x] = (byte)Math.Clamp(Math.Round((expanded - original) * 255 / (255 - original)), 0, 255);
        }
        return result;
    }
    internal void Clear()
    {
        epoch++; key = null; coverage = inputInk = null; image.Source = null;
    }
}

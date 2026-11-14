using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeowFilm.Capabilities;

/// <summary>A cached outside stroke of the hinted glyph coverage, not the unhinted font curves.</summary>
internal sealed class MusicLyricOutline : IDisposable
{
    private readonly FrameworkElement owner;
    private DrawingGroup? source;
    private ImageBrush? mask;
    private Matrix? deviceTransform;
    private Matrix toLocal;
    private Rect bounds;
    private double thickness;
    private bool observing;

    internal MusicLyricOutline(FrameworkElement owner)
    {
        this.owner = owner;
        owner.Loaded += OnLoaded;
        owner.Unloaded += OnUnloaded;
        owner.IsVisibleChanged += OnVisibilityChanged;
        Observe();
    }

    internal void Invalidate()
    {
        source = null;
        mask = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) { Observe(); owner.InvalidateVisual(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Observe();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Observe();
        if (owner.IsVisible) owner.InvalidateVisual();
    }

    private void Observe()
    {
        bool next = owner.IsLoaded && owner.IsVisible;
        if (next == observing) return;
        if (next) owner.LayoutUpdated += OnLayoutUpdated;
        else owner.LayoutUpdated -= OnLayoutUpdated;
        observing = next;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (deviceTransform != DeviceTransform()) owner.InvalidateVisual();
    }

    private Matrix DeviceTransform()
    {
        Matrix result;
        if (PresentationSource.FromVisual(owner) is { RootVisual: Visual root, CompositionTarget: { } target })
        {
            var transform = owner.TransformToAncestor(root);
            Point origin = transform.Transform(new());
            Vector x = transform.Transform(new(1, 0)) - origin;
            Vector y = transform.Transform(new(0, 1)) - origin;
            result = new Matrix(x.X, x.Y, y.X, y.Y, origin.X, origin.Y) * target.TransformToDevice;
        }
        else
        {
            var dpi = VisualTreeHelper.GetDpi(owner);
            result = new(dpi.DpiScaleX, 0, 0, dpi.DpiScaleY, 0, 0);
        }
        // Integer movement reuses exactly the same device pixels. Fractional movement does not.
        result.OffsetX -= Math.Floor(result.OffsetX);
        result.OffsetY -= Math.Floor(result.OffsetY);
        return result;
    }

    internal void Draw(DrawingContext drawing, DrawingGroup text, Brush stroke, double strokeThickness)
    {
        Matrix transform = DeviceTransform();
        if (!ReferenceEquals(source, text) || deviceTransform != transform || thickness != strokeThickness)
        {
            source = text;
            deviceTransform = transform;
            thickness = strokeThickness;
            mask = CreateMask(text, transform, strokeThickness / 2);
        }
        if (mask is null) return;
        // Only the outline is masked; native foreground/highlight glyphs remain direct drawings.
        drawing.PushTransform(new MatrixTransform(toLocal));
        drawing.PushOpacityMask(mask);
        drawing.DrawRectangle(stroke, null, bounds);
        drawing.Pop();
        drawing.Pop();
    }

    private ImageBrush? CreateMask(DrawingGroup text, Matrix transform, double radius)
    {
        if (text.Bounds.IsEmpty || !transform.HasInverse) return null;
        toLocal = transform;
        toLocal.Invert();
        var ink = Rect.Transform(text.Bounds, transform);
        double rx = radius * Math.Sqrt(transform.M11 * transform.M11 + transform.M21 * transform.M21);
        double ry = radius * Math.Sqrt(transform.M12 * transform.M12 + transform.M22 * transform.M22);
        // Include raster overhang and the stroke without changing the control's text metrics.
        ink.Inflate(rx + 2, ry + 2);
        bounds = new(new Point(Math.Floor(ink.Left), Math.Floor(ink.Top)),
            new Point(Math.Ceiling(ink.Right), Math.Ceiling(ink.Bottom)));
        int width = (int)bounds.Width, height = (int)bounds.Height;
        var glyphs = text.Clone();
        Opaque(glyphs);
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextOptions.GetTextFormattingMode(owner));
        TextOptions.SetTextRenderingMode(visual, TextOptions.GetTextRenderingMode(owner));
        TextOptions.SetTextHintingMode(visual, TextOptions.GetTextHintingMode(owner));
        transform.Translate(-bounds.X, -bounds.Y);
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new MatrixTransform(transform));
            drawing.DrawDrawing(glyphs);
            drawing.Pop();
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        image.CopyPixels(pixels, stride, 0);
        var outline = new byte[pixels.Length];
        var samples = Samples(transform, radius, rx, ry);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = y * stride + x * 4 + 3;
                int alpha = pixels[index];
                if (alpha == 255) continue;
                double expanded = alpha;
                foreach (var sample in samples)
                {
                    int sx = x + sample.X, sy = y + sample.Y;
                    double coverage = 0;
                    if (sx >= 0 && sx + 1 < width && sy >= 0 && sy + 1 < height)
                    {
                        int p = sy * stride + sx * 4 + 3;
                        coverage = pixels[p] * sample.TopLeft + pixels[p + 4] * sample.TopRight +
                            pixels[p + stride] * sample.BottomLeft + pixels[p + stride + 4] * sample.BottomRight;
                    }
                    expanded = Math.Max(expanded, coverage);
                }
                // Source-over the native fill must produce the expanded coverage, not darken it twice.
                outline[index] = (byte)Math.Clamp(Math.Round((expanded - alpha) * 255 / (255 - alpha)), 0, 255);
            }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, outline, stride);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private readonly record struct Sample(int X, int Y, double TopLeft, double TopRight,
        double BottomLeft, double BottomRight);

    private static List<Sample> Samples(Matrix transform, double radius, double rx, double ry)
    {
        var samples = new List<Sample>();
        void Add(double x, double y)
        {
            int left = (int)Math.Floor(x), top = (int)Math.Floor(y);
            double fx = x - left, fy = y - top;
            samples.Add(new(left, top, (1 - fx) * (1 - fy), fx * (1 - fy), (1 - fx) * fy, fx * fy));
        }
        for (int i = 0; i < 16; i++)
        {
            double angle = i * Math.PI / 8;
            Vector offset = transform.Transform(new Vector(Math.Cos(angle) * radius, Math.Sin(angle) * radius));
            Add(offset.X, offset.Y);
        }
        // Interior device samples also cover strokes wider than one device pixel.
        transform.Invert();
        for (int y = -(int)ry; y <= (int)ry; y++)
            for (int x = -(int)rx; x <= (int)rx; x++)
                if ((x != 0 || y != 0) && transform.Transform(new Vector(x, y)).Length <= radius) Add(x, y);
        return samples;
    }

    private static void Opaque(DrawingGroup drawing)
    {
        foreach (var child in drawing.Children)
            if (child is GlyphRunDrawing glyph) glyph.ForegroundBrush = Brushes.White;
            else if (child is DrawingGroup group) Opaque(group);
    }

    public void Dispose()
    {
        owner.Loaded -= OnLoaded;
        owner.Unloaded -= OnUnloaded;
        owner.IsVisibleChanged -= OnVisibilityChanged;
        if (observing) owner.LayoutUpdated -= OnLayoutUpdated;
        observing = false;
        Invalidate();
    }
}

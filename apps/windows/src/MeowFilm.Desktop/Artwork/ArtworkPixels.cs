using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace MeowFilm.Desktop.Artwork;

/// <summary>Immutable premultiplied BGRA pixels. The cache entry owns both pixels and its shared UI source.</summary>
public sealed class ArtworkPixels
{
    private readonly byte[] pixels;
    private readonly object gate = new();
    private WriteableBitmap? source;
    private (Brush Raw, Brush Palette, Brush Top)? paletteBrushes;
    private DispatcherQueue? dispatcher;
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    internal long Cost => (long)pixels.Length * 2; // Reserve the UI buffer before it is materialized.
    internal (Color Raw, Color Palette, Color Top, bool HasTopPalette) Palettes { get; }

    public ArtworkPixels(int width, int height, ReadOnlySpan<byte> premultipliedBgra)
        : this(width, height, premultipliedBgra.ToArray()) { }

    private ArtworkPixels(int width, int height, byte[] ownedPixels)
    {
        if (width is <= 0 or > 512 || height is <= 0 or > 512 || ownedPixels.Length != width * height * 4)
            throw new ArgumentException("Invalid bounded BGRA pixels.");
        PixelWidth = width; PixelHeight = height; pixels = ownedPixels;
        Palettes = Sample();
    }

    internal static ArtworkPixels TakeOwnership(int width, int height, byte[] pixels) => new(width, height, pixels);
    public void CopyPixels(Span<byte> destination) => pixels.AsSpan().CopyTo(destination);

    /// <summary>Called on the consuming UI dispatcher. All leases share this source; callers must not mutate it.</summary>
    public BitmapSource GetSource(DispatcherQueue uiDispatcher)
    {
        if (!uiDispatcher.HasThreadAccess) throw new InvalidOperationException("Artwork presentation requires its UI thread.");
        lock (gate)
        {
            if (dispatcher is not null && !dispatcher.HasThreadAccess)
                throw new InvalidOperationException("Artwork cannot share a XAML source between UI threads.");
            if (source is not null) return source;
            var bitmap = new WriteableBitmap(PixelWidth, PixelHeight);
            using (var output = bitmap.PixelBuffer.AsStream()) output.Write(pixels);
            bitmap.Invalidate();
            dispatcher = uiDispatcher; source = bitmap;
            return bitmap;
        }
    }

    internal (Brush Raw, Brush Palette, Brush Top) GetPaletteBrushes(DispatcherQueue uiDispatcher)
    {
        if (!uiDispatcher.HasThreadAccess) throw new InvalidOperationException("Artwork presentation requires its UI thread.");
        lock (gate)
        {
            if (dispatcher is not null && !dispatcher.HasThreadAccess)
                throw new InvalidOperationException("Artwork cannot share a XAML source between UI threads.");
            dispatcher ??= uiDispatcher;
            return paletteBrushes ??= (new SolidColorBrush(Palettes.Raw), new SolidColorBrush(Palettes.Palette),
                new SolidColorBrush(Palettes.Top));
        }
    }

    private (Color Raw, Color Palette, Color Top, bool HasTopPalette) Sample()
    {
        // Sample the bounded raster once, off the UI thread, without another decode or image-sized copy.
        (double Red, double Green, double Blue, long Weight, int Count) Average(int rows)
        {
            long r = 0, g = 0, b = 0, weight = 0;
            int count = PixelWidth * rows;
            for (int offset = 0; offset < count * 4; offset += 4)
            {
                b += pixels[offset]; g += pixels[offset + 1]; r += pixels[offset + 2]; weight += pixels[offset + 3];
            }
            return weight == 0 ? (0, 0, 0, 0, count) :
                (r * 255d / weight, g * 255d / weight, b * 255d / weight, weight, count);
        }
        static Color ColorOf(double red, double green, double blue, double factor = 1, byte alpha = 255) =>
            Color.FromArgb(alpha, (byte)Math.Clamp(red * factor, 0, 255),
                (byte)Math.Clamp(green * factor, 0, 255), (byte)Math.Clamp(blue * factor, 0, 255));
        var fallback = Color.FromArgb(255, 105, 105, 105);
        var all = Average(PixelHeight);
        if (all.Weight == 0) return (fallback, fallback, fallback, false);
        double scale = Math.Min(1, 105 / Math.Max(1, .2126 * all.Red + .7152 * all.Green + .0722 * all.Blue));
        var top = Average(Math.Max(1, (int)Math.Ceiling(PixelHeight * .375)));
        byte alpha = (byte)Math.Clamp((int)Math.Round((double)top.Weight / top.Count, MidpointRounding.AwayFromZero), 0, 255);
        return (ColorOf(all.Red, all.Green, all.Blue), ColorOf(all.Red, all.Green, all.Blue, scale),
            top.Weight == 0 ? fallback : ColorOf(top.Red, top.Green, top.Blue, alpha: alpha), top.Weight > 0);
    }
}

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI;

namespace MeowFilm.Capabilities;

/// <summary>GPU page target and XAML source; animation snapshots own their independent surface.</summary>
internal sealed class NovelPageRaster
{
    private CanvasRenderTarget? target;
    private CanvasImageSource? source;
    internal double Scale { get; private set; } = 1;
    internal int PixelWidth => (int)(target?.SizeInPixels.Width ?? 0);
    internal int PixelHeight => (int)(target?.SizeInPixels.Height ?? 0);

    internal CanvasImageSource Render(Size size, double scale, Action<CanvasDrawingSession> draw)
    {
        int width = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        if (target is null || PixelWidth != width || PixelHeight != height || Math.Abs(Scale - scale) > .001)
        {
            Clear(); Scale = scale;
            var device = CanvasDevice.GetSharedDevice();
            target = new(device, (float)(width / scale), (float)(height / scale), (float)(96 * scale));
            source = new(device, (float)(width / scale), (float)(height / scale), (float)(96 * scale));
        }
        using (var drawing = target.CreateDrawingSession()) { drawing.Clear(Colors.Transparent); draw(drawing); }
        using (var drawing = source!.CreateDrawingSession(Colors.Transparent)) drawing.DrawImage(target);
        return source;
    }
    internal CanvasImageSource? Snapshot()
    {
        if (target is null) return null;
        var snapshot = new CanvasImageSource(target.Device, (float)target.Size.Width, (float)target.Size.Height, target.Dpi);
        using (var drawing = snapshot.CreateDrawingSession(Colors.Transparent)) drawing.DrawImage(target);
        return snapshot;
    }
    internal void Clear() { target?.Dispose(); target = null; source = null; }
}

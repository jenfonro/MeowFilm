using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MeowFilm.Capabilities;

/// <summary>
/// GPU-accelerated Win2D vector text and solid-backed outer stroke renderer for desktop lyrics.
/// Eliminates RenderTargetBitmap GPU-to-CPU readbacks, hollowed-out stroke seams, Viewbox bitmap blur,
/// and ClearType color fringes on transparent DirectComposition windows.
/// </summary>
internal static class MusicDesktopLyricRenderer
{
    private const float MinFittedFontSize = 10f;
    private static readonly Color DefaultStrokeColor = Color.FromArgb(235, 20, 20, 20);
    private static readonly CanvasStrokeStyle RoundStroke = new()
    {
        LineJoin = CanvasLineJoin.Round,
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round
    };

    internal readonly record struct WordSpan(double Time, double Duration, float StartX, float EndX);

    internal sealed record SingleLineRender(
        CanvasImageSource Image,
        double WidthDip,
        double HeightDip);

    internal sealed record TimedLineRender(
        CanvasImageSource StrokeImage,
        CanvasImageSource NormalImage,
        CanvasImageSource HighlightImage,
        double WidthDip,
        double HeightDip,
        WordSpan[] Spans,
        bool IsRtl);

    internal static Color ResolveColor(Brush? brush, Color fallback) =>
        brush is SolidColorBrush solid
            ? Color.FromArgb((byte)Math.Clamp(Math.Round(solid.Color.A * solid.Opacity), 0, 255), solid.Color.R, solid.Color.G, solid.Color.B)
            : fallback;

    internal static Color ResolveStrokeColor(bool highContrast) =>
        highContrast
            ? MeowFilm.Desktop.SystemAppearance.GetElementColor(Windows.UI.ViewManagement.UIElementType.Window)
            : DefaultStrokeColor;

    internal static SingleLineRender? RenderSingle(
        string text,
        double baseFontSize,
        double maxWidthDip,
        double rasterScale,
        FlowDirection flowDirection,
        Color fillColor,
        Color strokeColor)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return ExecuteWithDeviceRecovery(device =>
        {
            float dpi = ClampDpi(rasterScale);
            using var layout = CreateFittedLayout(device, text, (float)baseFontSize, maxWidthDip, flowDirection, out float fittedSize);
            float strokeRadius = ComputeStrokeRadius(fittedSize);
            ComputeMetrics(layout, strokeRadius, out float originX, out float originY, out float widthDip, out float heightDip);

            var image = new CanvasImageSource(device, widthDip, heightDip, dpi);
            using var geometry = CanvasGeometry.CreateText(layout);
            using (var ds = image.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
            {
                ds.Antialiasing = CanvasAntialiasing.Antialiased;
                ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                ds.DrawGeometry(geometry, originX, originY, strokeColor, strokeRadius * 2f, RoundStroke);
                ds.FillGeometry(geometry, originX, originY, strokeColor);
                ds.DrawTextLayout(layout, new Vector2(originX, originY), fillColor);
            }
            return new SingleLineRender(image, widthDip, heightDip);
        });
    }

    internal static TimedLineRender? RenderTimedLine(
        IReadOnlyList<MusicLyricWord> words,
        double baseFontSize,
        double maxWidthDip,
        double rasterScale,
        FlowDirection flowDirection,
        Color normalColor,
        Color highlightColor,
        Color strokeColor)
    {
        if (words.Count == 0) return null;
        string fullText = string.Concat(words.Select(w => w.Text));
        if (string.IsNullOrWhiteSpace(fullText)) return null;

        return ExecuteWithDeviceRecovery(device =>
        {
            float dpi = ClampDpi(rasterScale);
            using var layout = CreateFittedLayout(device, fullText, (float)baseFontSize, maxWidthDip, flowDirection, out float fittedSize);
            float strokeRadius = ComputeStrokeRadius(fittedSize);
            ComputeMetrics(layout, strokeRadius, out float originX, out float originY, out float widthDip, out float heightDip);

            bool rtl = flowDirection == FlowDirection.RightToLeft;
            var spans = new WordSpan[words.Count];
            int charOffset = 0;
            float cursor = 0f;
            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i];
                int len = word.Text.Length;
                float startX = cursor;
                float endX = cursor;
                if (len > 0)
                {
                    var regions = layout.GetCharacterRegions(charOffset, len);
                    if (regions.Length > 0)
                    {
                        double minLeft = double.PositiveInfinity;
                        double maxRight = double.NegativeInfinity;
                        for (int r = 0; r < regions.Length; r++)
                        {
                            var b = regions[r].LayoutBounds;
                            if (b.Left < minLeft) minLeft = b.Left;
                            if (b.Right > maxRight) maxRight = b.Right;
                        }
                        if (double.IsFinite(minLeft) && double.IsFinite(maxRight))
                        {
                            if (rtl)
                            {
                                startX = Math.Clamp(widthDip - (originX + (float)maxRight), 0f, widthDip);
                                endX = Math.Clamp(widthDip - (originX + (float)minLeft), startX, widthDip);
                            }
                            else
                            {
                                startX = Math.Clamp(originX + (float)minLeft, 0f, widthDip);
                                endX = Math.Clamp(originX + (float)maxRight, startX, widthDip);
                            }
                        }
                    }
                    charOffset += len;
                }
                if (startX < cursor) startX = cursor;
                if (endX < startX) endX = startX;
                cursor = endX;
                spans[i] = new WordSpan(word.Time, Math.Max(0, word.Duration), startX, endX);
            }
            if (spans.Length > 0 && spans[^1].EndX < widthDip)
                spans[^1] = spans[^1] with { EndX = widthDip };

            var strokeImage = new CanvasImageSource(device, widthDip, heightDip, dpi);
            using (var geometry = CanvasGeometry.CreateText(layout))
            using (var ds = strokeImage.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
            {
                ds.Antialiasing = CanvasAntialiasing.Antialiased;
                ds.DrawGeometry(geometry, originX, originY, strokeColor, strokeRadius * 2f, RoundStroke);
                ds.FillGeometry(geometry, originX, originY, strokeColor);
            }

            var normalImage = RenderFillLayer(device, layout, originX, originY, widthDip, heightDip, dpi, normalColor);
            var highlightImage = RenderFillLayer(device, layout, originX, originY, widthDip, heightDip, dpi, highlightColor);
            return new TimedLineRender(strokeImage, normalImage, highlightImage, widthDip, heightDip, spans, rtl);
        });
    }

    private static CanvasImageSource RenderFillLayer(
        CanvasDevice device, CanvasTextLayout layout, float originX, float originY,
        float widthDip, float heightDip, float dpi, Color color)
    {
        var image = new CanvasImageSource(device, widthDip, heightDip, dpi);
        using var ds = image.CreateDrawingSession(Microsoft.UI.Colors.Transparent);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        ds.DrawTextLayout(layout, new Vector2(originX, originY), color);
        return image;
    }

    private static CanvasTextLayout CreateFittedLayout(
        CanvasDevice device, string text, float baseFontSize, double maxWidthDip,
        FlowDirection flowDirection, out float fittedFontSize)
    {
        fittedFontSize = Math.Max(MinFittedFontSize, baseFontSize);
        var layout = BuildLayout(device, text, fittedFontSize, flowDirection);
        if (maxWidthDip > 20 && double.IsFinite(maxWidthDip))
        {
            float pad = ComputeStrokeRadius(fittedFontSize) + 2f;
            double naturalWidth = MeasureWidth(layout) + pad * 2f;
            if (naturalWidth > maxWidthDip)
            {
                float ratio = (float)((maxWidthDip - pad * 2f) / Math.Max(1.0, MeasureWidth(layout)));
                float shrunk = Math.Max(MinFittedFontSize, fittedFontSize * Math.Clamp(ratio, 0.25f, 1f));
                if (Math.Abs(shrunk - fittedFontSize) > 0.25f)
                {
                    layout.Dispose();
                    fittedFontSize = shrunk;
                    layout = BuildLayout(device, text, fittedFontSize, flowDirection);
                }
            }
        }
        return layout;
    }

    private static CanvasTextLayout BuildLayout(CanvasDevice device, string text, float fontSize, FlowDirection flowDirection)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Microsoft YaHei UI",
            FontSize = fontSize,
            FontWeight = FontWeights.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap,
            Direction = flowDirection == FlowDirection.RightToLeft
                ? CanvasTextDirection.RightToLeftThenTopToBottom
                : CanvasTextDirection.LeftToRightThenTopToBottom
        };
        return new CanvasTextLayout(device, text, format, 0f, 0f);
    }

    private static double MeasureWidth(CanvasTextLayout layout)
    {
        var lb = layout.LayoutBoundsIncludingTrailingWhitespace;
        var db = layout.DrawBounds;
        double left = Math.Min(0, Math.Min(lb.Left, db.Left));
        double right = Math.Max(lb.Right, db.Right);
        return Math.Max(1, right - left);
    }

    private static void ComputeMetrics(
        CanvasTextLayout layout, float strokeRadius,
        out float originX, out float originY, out float widthDip, out float heightDip)
    {
        float pad = (float)Math.Ceiling(strokeRadius) + 2f;
        var lb = layout.LayoutBoundsIncludingTrailingWhitespace;
        var db = layout.DrawBounds;
        double minX = Math.Min(0, Math.Min(lb.Left, db.Left));
        double minY = Math.Min(0, Math.Min(lb.Top, db.Top));
        double maxX = Math.Max(lb.Right, db.Right);
        double maxY = Math.Max(lb.Bottom, db.Bottom);
        originX = pad - (float)minX;
        originY = pad - (float)minY;
        widthDip = Math.Max(1f, (float)Math.Ceiling(maxX - minX + pad * 2f));
        heightDip = Math.Max(1f, (float)Math.Ceiling(maxY - minY + pad * 2f));
    }

    private static float ComputeStrokeRadius(float fontSize) =>
        Math.Clamp(fontSize * 0.072f, 1.2f, 2.4f);

    private static float ClampDpi(double rasterScale) =>
        (float)(96.0 * Math.Clamp(double.IsFinite(rasterScale) && rasterScale > 0 ? rasterScale : 1.0, 0.5, 4.0));

    private static T? ExecuteWithDeviceRecovery<T>(Func<CanvasDevice, T> work) where T : class
    {
        var device = CanvasDevice.GetSharedDevice();
        try
        {
            return work(device);
        }
        catch (COMException ex) when (device.IsDeviceLost(ex.HResult))
        {
            device = CanvasDevice.GetSharedDevice(forceSoftwareRenderer: false);
            return work(device);
        }
    }
}

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Graphics.DirectX;

namespace MeowFilm.Capabilities;

/// <summary>Decoded novel image pixels, independent of the UI thread and cover-thumbnail limits.</summary>
internal sealed record NovelImagePixels(int PixelWidth, int PixelHeight, byte[] Pixels)
{
    internal static NovelImagePixels Decode(byte[] bytes, int width)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) throw new InvalidDataException("IMAGE_DECODE_FAILED");
        double scale = width <= 0 ? 1 : Math.Min(1, (double)width / codec.Info.Width);
        int targetWidth = Math.Max(1, (int)Math.Round(codec.Info.Width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(codec.Info.Height * scale));
        var size = codec.GetScaledDimensions((float)scale);
        using var decoded = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("IMAGE_DECODE_FAILED");
        using var resized = size.Width == targetWidth && size.Height == targetHeight ? null :
            decoded.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        var image = resized ?? decoded;
        if (image.Width != targetWidth || image.Height != targetHeight) throw new InvalidDataException("IMAGE_DECODE_FAILED");
        var pixels = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
            Marshal.Copy(image.GetPixels() + y * image.RowBytes, pixels, y * targetWidth * 4, targetWidth * 4);
        return new(targetWidth, targetHeight, pixels);
    }
    internal CanvasBitmap CreateBitmap(CanvasDevice device) => CanvasBitmap.CreateFromBytes(device, Pixels,
        PixelWidth, PixelHeight, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
    internal WriteableBitmap CreateSource()
    {
        var bitmap = new WriteableBitmap(PixelWidth, PixelHeight);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(Pixels);
        bitmap.Invalidate();
        return bitmap;
    }
}

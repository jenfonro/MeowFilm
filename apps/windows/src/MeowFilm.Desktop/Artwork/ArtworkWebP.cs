using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace MeowFilm.Desktop.Artwork;

internal static class ArtworkWebP
{
    internal static BitmapSource Decode(byte[] bytes)
    {
        // The RIFF size covers the entire container, including chunk padding.
        // A decoder's partial-image success must not admit a truncated response.
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - 8L)
            throw new ArtworkException("ARTWORK_FORMAT");
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Webp)
            throw new ArtworkException("ARTWORK_FORMAT");
        var original = codec.Info;
        ArtworkRaster.CheckDimensions(original.Width, original.Height);
        double scale = Math.Min(1d, 512d / Math.Max(original.Width, original.Height));
        int width = Math.Max(1, (int)Math.Round(original.Width * scale));
        int height = Math.Max(1, (int)Math.Round(original.Height * scale));
        var output = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        byte[] pixels = new byte[output.BytesSize];
        try
        {
            // WebP decodes directly at the requested scale. The default frame
            // is the composited first frame; artwork never runs an animation.
            if (codec.GetPixels(output, pixels) != SKCodecResult.Success)
                throw new ArtworkException("ARTWORK_FORMAT");
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, output.RowBytes);
            image.Freeze();
            return image;
        }
        finally { Array.Clear(pixels); }
    }
}

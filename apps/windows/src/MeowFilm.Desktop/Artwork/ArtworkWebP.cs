using System.Buffers.Binary;
using SkiaSharp;

namespace MeowFilm.Desktop.Artwork;

internal static class ArtworkWebP
{
    internal static ArtworkPixels Decode(byte[] bytes)
    {
        // The RIFF size covers the entire container, including chunk padding.
        // A decoder's partial-image success must not admit a truncated response.
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - 8L)
            throw new ArtworkException("ARTWORK_FORMAT");
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Webp)
            throw new ArtworkException("ARTWORK_FORMAT");
        return ArtworkRaster.Decode(codec);
    }
}

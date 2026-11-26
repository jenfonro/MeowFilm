using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace MeowFilm.Desktop.Artwork;

internal static unsafe class ArtworkRaster
{
    internal static ArtworkPixels Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) &&
                bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
                return ArtworkWebP.Decode(bytes);
            var (width, height) = Dimensions(bytes);
            CheckDimensions(width, height);
            fixed (byte* pinned = bytes)
            {
                using var data = SKData.Create((IntPtr)pinned, bytes.Length);
                using var codec = SKCodec.Create(data);
                if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Gif) ||
                    codec.Info.Width != width || codec.Info.Height != height)
                    throw new ArtworkException("ARTWORK_FORMAT");
                return Decode(codec);
            }
        }
        catch (ArtworkException) { throw; }
        catch { throw new ArtworkException("ARTWORK_FORMAT"); }
    }

    internal static ArtworkPixels Decode(SKCodec codec)
    {
        var original = codec.Info;
        CheckDimensions(original.Width, original.Height);
        double scale = Math.Min(1d, 512d / Math.Max(original.Width, original.Height));
        int width = Math.Max(1, (int)Math.Round(original.Width * scale));
        int height = Math.Max(1, (int)Math.Round(original.Height * scale));
        var size = codec.EncodedFormat == SKEncodedImageFormat.Webp
            ? new SKSizeI(width, height) : codec.GetScaledDimensions((float)scale);
        CheckDimensions(size.Width, size.Height);
        try
        {
            IntPtr native = ArtworkPixels.DecodeToNativeBgra(codec, size, width, height,
                out int byteCount, out int thumbWidth, out int thumbHeight, out int thumbByteCount);
            return new ArtworkPixels(width, height, native, byteCount, thumbWidth, thumbHeight, thumbByteCount);
        }
        catch (ArtworkException) { throw; }
        catch { throw new ArtworkException("ARTWORK_FORMAT"); }
    }

    internal static void CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > 32 * 1024 * 1024)
            throw new ArtworkException("ARTWORK_LIMIT");
    }

    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    private static (int Width, int Height) Dimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 33 && bytes[..8].SequenceEqual(PngSignature) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            RequireCompletePng(bytes);
            return (checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[16..])),
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[20..])));
        }
        if (bytes.Length >= 13 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            if (bytes[^1] != 0x3b) throw new ArtworkException("ARTWORK_FORMAT");
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        }
        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216)
        {
            if (bytes[^2] != 255 || bytes[^1] != 217) throw new ArtworkException("ARTWORK_FORMAT");
            int position = 2;
            while (position < bytes.Length)
            {
                if (bytes[position++] != 255) break;
                while (position < bytes.Length && bytes[position] == 255) position++;
                if (position >= bytes.Length) break;
                byte marker = bytes[position++];
                if (marker is 0 or 216 or 217 or 218) break;
                if (marker == 1 || marker is >= 208 and <= 215) continue;
                if (position + 2 > bytes.Length) break;
                int size = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
                if (size < 2 || size > bytes.Length - position) break;
                if (marker is 192 or 193 or 194 or 195 or 197 or 198 or 199 or 201 or 202 or 203 or 205 or 206 or 207)
                {
                    if (size < 8) break;
                    return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..]));
                }
                position += size;
            }
        }
        throw new ArtworkException("ARTWORK_FORMAT");
    }

    private static void RequireCompletePng(ReadOnlySpan<byte> bytes)
    {
        // A decoder may return usable pixels after IDAT even when the PNG tail is missing.
        // Walk declared chunks, not a byte-pattern search that could match compressed data.
        int position = 8;
        bool imageData = false;
        while (position <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);
            if (length > bytes.Length - position - 12L) break;
            var type = bytes.Slice(position + 4, 4);
            position += checked((int)length + 12);
            if (type.SequenceEqual("IEND"u8))
            {
                if (length == 0 && imageData && position == bytes.Length) return;
                break;
            }
            if (type.SequenceEqual("IDAT"u8)) imageData = true;
        }
        throw new ArtworkException("ARTWORK_FORMAT");
    }
}

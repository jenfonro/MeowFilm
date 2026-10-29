using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeowFilm.Desktop.Artwork;

internal static class ArtworkRaster
{
    internal static BitmapSource Decode(byte[] bytes)
    {
        try
        {
            var (width, height) = Dimensions(bytes);
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > 32 * 1024 * 1024)
                throw new ArtworkException("ARTWORK_LIMIT");
            using var input = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = input;
            if (width >= height) image.DecodePixelWidth = Math.Min(512, width);
            else image.DecodePixelHeight = Math.Min(512, height);
            image.EndInit();
            if (image.PixelWidth is <= 0 or > 512 || image.PixelHeight is <= 0 or > 512)
                throw new ArtworkException("ARTWORK_LIMIT");
            // One frozen, detached BGRA raster. No UriSource, decoder stream or
            // external color profile is retained by the returned resource.
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int stride = checked(converted.PixelWidth * 4);
            byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            try
            {
                var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
                    PixelFormats.Bgra32, null, pixels, stride);
                result.Freeze(); return result;
            }
            finally { Array.Clear(pixels); }
        }
        catch (ArtworkException) { throw; }
        catch { throw new ArtworkException("ARTWORK_FORMAT"); }
    }

    private static (int Width, int Height) Dimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 33 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return (checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[16..])),
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[20..])));
        if (bytes.Length >= 13 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216)
        {
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
}

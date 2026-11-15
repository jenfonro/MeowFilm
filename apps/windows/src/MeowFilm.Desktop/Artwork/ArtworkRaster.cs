using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;

namespace MeowFilm.Desktop.Artwork;

internal static class ArtworkRaster
{
    // BitmapImage retains StreamSource even with OnLoad. Drop the inner stream
    // after decoding so the frozen image cannot retain the encoded byte array.
    private sealed class DecodeStream(byte[] bytes) : Stream
    {
        private MemoryStream? input = new(bytes, writable: false);
        private MemoryStream Input => input ?? throw new ObjectDisposedException(nameof(DecodeStream));
        public override bool CanRead => input?.CanRead ?? false;
        public override bool CanSeek => input?.CanSeek ?? false;
        public override bool CanWrite => false;
        public override long Length => Input.Length;
        public override long Position { get => Input.Position; set => Input.Position = value; }
        public override void Flush() => Input.Flush();
        public override int Read(byte[] buffer, int offset, int count) => Input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => Input.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { input?.Dispose(); input = null; }
            base.Dispose(disposing);
        }
    }

    internal static BitmapSource Decode(byte[] bytes)
    {
        try
        {
            if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
                return ArtworkWebP.Decode(bytes);
            var (width, height) = Dimensions(bytes);
            CheckDimensions(width, height);
            using var input = new DecodeStream(bytes);
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
            image.Freeze();
            return image;
        }
        catch (ArtworkException) { throw; }
        catch { throw new ArtworkException("ARTWORK_FORMAT"); }
    }

    internal static void CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > 32 * 1024 * 1024)
            throw new ArtworkException("ARTWORK_LIMIT");
    }

    private static (int Width, int Height) Dimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 33 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
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
        // WIC may return usable pixels after IDAT even when the PNG tail is missing.
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

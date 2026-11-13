using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal static class PanQrImage
{
    internal static BitmapSource Create(string type, string value)
    {
        if (type == "url") return QrCodeImage.Create(value);
        if (type != "image" || !value.StartsWith("data:image/", StringComparison.Ordinal))
            throw new BridgeException("INVALID_RESPONSE");
        int separator = value.IndexOf(";base64,", StringComparison.Ordinal);
        if (separator < 0) throw new BridgeException("INVALID_RESPONSE");
        byte[] bytes = Convert.FromBase64String(value[(separator + 8)..]);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int stride = checked(frame.PixelWidth * 4);
            byte[] pixels = new byte[checked(stride * frame.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            var image = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);
            image.Freeze();
            return image;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}

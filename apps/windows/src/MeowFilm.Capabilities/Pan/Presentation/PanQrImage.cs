using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal static class PanQrImage
{
    internal static WriteableBitmap Create(string type, string value)
    {
        if (type == "url") return QrCodeImage.Create(value);
        if (type != "image" || !value.StartsWith("data:image/", StringComparison.Ordinal))
            throw new BridgeException("INVALID_RESPONSE");
        int separator = value.IndexOf(";base64,", StringComparison.Ordinal);
        if (separator < 0) throw new BridgeException("INVALID_RESPONSE");
        byte[] bytes = Convert.FromBase64String(value[(separator + 8)..]);
        try
        {
            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is null) throw new BridgeException("INVALID_RESPONSE");
            using var pixels = decoded.Copy(SKColorType.Bgra8888);
            var image = new WriteableBitmap(pixels.Width, pixels.Height);
            using (var output = image.PixelBuffer.AsStream()) output.Write(pixels.Bytes);
            image.Invalidate();
            return image;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}

using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Net.Codecrete.QrCodeGenerator;

namespace MeowFilm.Capabilities;

/// <summary>Encodes text supplied by the owning login protocol into local pixels.</summary>
internal static class QrCodeImage
{
    internal static WriteableBitmap Create(string text)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        const int border = 4, scale = 4;
        int width = checked((qr.Size + border * 2) * scale);
        var pixels = new byte[width * width * 4];
        Array.Fill(pixels, (byte)255);
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                if (qr.GetModule(x, y))
                    for (int dy = 0; dy < scale; dy++)
                        Array.Fill(pixels, (byte)0, (((y + border) * scale + dy) * width + (x + border) * scale) * 4, scale * 4);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bitmap = new WriteableBitmap(width, width);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        bitmap.Invalidate();
        return bitmap;
    }
}

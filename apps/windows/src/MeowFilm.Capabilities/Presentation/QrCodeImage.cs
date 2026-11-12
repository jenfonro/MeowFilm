using System.Windows.Media;
using System.Windows.Media.Imaging;
using Net.Codecrete.QrCodeGenerator;

namespace MeowFilm.Capabilities;

/// <summary>Encodes text supplied by the owning login protocol into local pixels.</summary>
internal static class QrCodeImage
{
    internal static BitmapSource Create(string text)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        const int border = 4, scale = 4;
        int width = checked((qr.Size + border * 2) * scale);
        var pixels = new byte[width * width];
        Array.Fill(pixels, (byte)255);
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                if (qr.GetModule(x, y))
                    for (int dy = 0; dy < scale; dy++)
                        Array.Fill(pixels, (byte)0, ((y + border) * scale + dy) * width + (x + border) * scale, scale);
        var bitmap = BitmapSource.Create(width, width, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        return bitmap;
    }
}

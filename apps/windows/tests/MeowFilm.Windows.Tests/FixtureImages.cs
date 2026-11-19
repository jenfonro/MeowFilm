using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MeowFilm.Windows.Tests;

internal static class FixtureImages
{
    // WIC encoding is independent of the production Skia decoder.
    internal static byte[] Encode(int width, int height, byte[] bgra, string kind = "png", bool premultiplied = false) =>
        Task.Run(async () =>
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(kind switch
            {
                "jpeg" => BitmapEncoder.JpegEncoderId,
                "gif" => BitmapEncoder.GifEncoderId,
                _ => BitmapEncoder.PngEncoderId
            }, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,
                premultiplied ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Straight,
                (uint)width, (uint)height, 96, 96, bgra);
            await encoder.FlushAsync();
            stream.Seek(0);
            var result = new byte[checked((int)stream.Size)];
            await stream.AsStreamForRead().ReadExactlyAsync(result);
            return result;
        }).GetAwaiter().GetResult();
}

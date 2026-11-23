using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MeowFilm.Desktop;

public static class DesktopWindowCapture
{
    public static async Task<byte[]?> TryRenderElementPngAsync(
        FrameworkElement element,
        bool requireVisiblePixels,
        CancellationToken cancellationToken)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return null;
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(element);
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = await rtb.GetPixelsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var pixels = buffer.ToArray();
            if (rtb.PixelWidth <= 0 || rtb.PixelHeight <= 0 || pixels.Length == 0)
                return null;
            if (requireVisiblePixels && !HasVisiblePixels(pixels))
                return null;
            return await EncodeBgraPngAsync(
                pixels, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight,
                BitmapAlphaMode.Premultiplied, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static Task<byte[]> CaptureWindowHandlePngAsync(nint hwnd, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (pixels, width, height) = CaptureWindowPixels(hwnd);
        return EncodeBgraPngAsync(pixels, (uint)width, (uint)height, BitmapAlphaMode.Ignore, cancellationToken);
    }

    private static (byte[] Pixels, int Width, int Height) CaptureWindowPixels(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
            throw DebugApiException.Internal("Unable to query window bounds for screenshot.");
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);
        nint screenDc = GetDC(0);
        if (screenDc == 0)
            throw DebugApiException.Internal("GetDC failed while capturing screenshot.");
        nint memDc = 0, dib = 0, old = 0;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == 0)
                throw DebugApiException.Internal("CreateCompatibleDC failed while capturing screenshot.");
            var bmi = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };
            dib = CreateDIBSection(memDc, ref bmi, 0, out nint bits, 0, 0);
            if (dib == 0 || bits == 0)
                throw DebugApiException.Internal("CreateDIBSection failed while capturing screenshot.");
            old = SelectObject(memDc, dib);
            if (!PrintWindow(hwnd, memDc, 0x00000002)) // PW_RENDERFULLCONTENT
                throw DebugApiException.Internal("PrintWindow failed while capturing screenshot.");
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return (pixels, width, height);
        }
        finally
        {
            if (old != 0) SelectObject(memDc, old);
            if (dib != 0) DeleteObject(dib);
            if (memDc != 0) DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    private static async Task<byte[]> EncodeBgraPngAsync(
        byte[] pixels,
        uint width,
        uint height,
        BitmapAlphaMode alphaMode,
        CancellationToken cancellationToken)
    {
        using var ras = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, alphaMode, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        ras.Seek(0);
        using var stream = ras.AsStreamForRead();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }

    private static bool HasVisiblePixels(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return true;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hWnd, out RectNative rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint ho);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);
}

using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace MeowFilm.Windows.Tests;

internal static class NativeDesktopCapture
{
    internal static void DisableWindowTransitions(Window window)
    {
        int disabled = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(window), 3,
            ref disabled, sizeof(int)));
    }

    internal static Point ScreenPoint(FrameworkElement target, Point relative)
    {
        var root = target.XamlRoot ?? throw new InvalidOperationException("Target has no XamlRoot.");
        var hwnd = Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) throw new InvalidOperationException("Target client origin unavailable.");
        var point = target.TransformToVisual(null).TransformPoint(relative);
        return new(origin.X + point.X * root.RasterizationScale, origin.Y + point.Y * root.RasterizationScale);
    }

    internal static async Task SaveAsync(FrameworkElement target, string name) => await SaveAsync(await CaptureAsync(target), name);

    internal static async Task SaveAsync(Capture image, string name)
    {
        string directory = TestWorkspace.DataPath("desktop-shots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".png");
        using (File.Create(path)) { }
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)image.Width,
            (uint)image.Height, 96 * image.Scale, 96 * image.Scale, image.Pixels);
        await encoder.FlushAsync();
        Console.WriteLine($"DESKTOP_SHOT {path}; pixels={image.Width}x{image.Height}; method=screen-BitBlt-target-bounds");
    }

    internal sealed record Capture(int Width, int Height, double Scale, byte[] Pixels);
    internal static async Task<Capture> CaptureAsync(FrameworkElement target)
    {
        if (!TestDesktopContext.Verify(requireTarget: true))
            throw new InvalidOperationException("Desktop capture requires the verified target session.");
        target.UpdateLayout();
        if (!target.IsLoaded || !DesktopVisual.IsVisible(target) || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            throw new InvalidOperationException("Desktop capture requires a visible arranged target.");
        var root = target.XamlRoot;
        nint hwnd = Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        var presenter = AppWindow.GetFromWindowId(root.ContentIslandEnvironment.AppWindowId).Presenter as OverlappedPresenter;
        bool wasTopmost = presenter?.IsAlwaysOnTop ?? false;
        try
        {
            if (presenter is not null) presenter.IsAlwaysOnTop = true;
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool renderingObserved = false;
            EventHandler<object> rendering = (_, _) => renderingObserved = true;
            EventHandler<RenderedEventArgs> completed = (_, _) =>
            {
                if (!renderingObserved) return;
                for (DependencyObject? current = target; current is not null; current = VisualTreeHelper.GetParent(current))
                    if (current is UIElement { Opacity: < 1 }) return;
                rendered.TrySetResult();
            };
            CompositionTarget.Rendering += rendering;
            CompositionTarget.Rendered += completed;
            try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { CompositionTarget.Rendering -= rendering; CompositionTarget.Rendered -= completed; }
            await ElementCompositionPreview.GetElementVisual(target).Compositor.RequestCommitAsync();
            Marshal.ThrowExceptionForHR(DwmFlush());
            // TransformToVisual includes the handoff visual's compositor values.
            // Their property notifications return on the UI dispatcher after commit.
            await DesktopTestUi.DrainAsync();
            double scale = root.RasterizationScale;
            var bounds = target.TransformToVisual(null).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            var origin = ScreenPoint(target, new Point());
            int x = (int)Math.Floor(origin.X), y = (int)Math.Floor(origin.Y);
            int width = (int)Math.Ceiling(bounds.Width * scale), height = (int)Math.Ceiling(bounds.Height * scale);
            int insetX = Math.Min(16, width / 4), insetY = Math.Min(16, height / 4);
            foreach (var point in new[] { (width / 2, height / 2), (insetX, insetY), (width - 1 - insetX, insetY),
                (insetX, height - 1 - insetY), (width - 1 - insetX, height - 1 - insetY) })
            {
                var at = new NativePoint { X = x + Math.Clamp(point.Item1, 0, width - 1), Y = y + Math.Clamp(point.Item2, 0, height - 1) };
                var displayed = GetAncestor(WindowFromPoint(at), 2);
                if (displayed != hwnd)
                    throw new InvalidOperationException($"Desktop capture target is covered: target=0x{hwnd:X}, displayed=0x{displayed:X}, " +
                        $"point={at.X},{at.Y}, bounds={x},{y},{width},{height}.");
            }
            nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
            try
            {
                memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height);
                if (screen == 0 || memory == 0 || bitmap == 0) throw new InvalidOperationException("Target capture allocation failed.");
                previous = SelectObject(memory, bitmap);
                if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x40CC0020))
                    throw new InvalidOperationException("Target desktop pixel capture failed.");
                SelectObject(memory, previous); previous = 0;
                var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
                var pixels = new byte[checked(width * height * 4)];
                if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) != height)
                    throw new InvalidOperationException("Target desktop pixel transfer failed.");
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return new(width, height, scale, pixels);
            }
            finally
            {
                if (previous != 0) SelectObject(memory, previous);
                if (bitmap != 0) DeleteObject(bitmap);
                if (memory != 0) DeleteDC(memory);
                if (screen != 0) ReleaseDC(0, screen);
            }
        }
        finally
        {
            if (presenter is not null) presenter.IsAlwaysOnTop = wasTopmost;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}

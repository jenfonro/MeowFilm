using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MeowFilm.Windows.Tests;

internal static class NativeDesktopCapture
{
    internal static async Task SaveAsync(FrameworkElement target, string name)
    {
        if (!Program.CheckDesktopContext(requireTarget: true))
            throw new InvalidOperationException("Desktop capture requires the verified target session.");
        target.UpdateLayout();
        if (!target.IsLoaded || !target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            throw new InvalidOperationException("Desktop capture requires a visible arranged target.");
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler rendering = (_, _) =>
        {
            for (DependencyObject? current = target; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is UIElement { Opacity: < 1 }) return;
            rendered.TrySetResult();
        };
        CompositionTarget.Rendering += rendering;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= rendering; }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Marshal.ThrowExceptionForHR(DwmFlush());
        var first = target.PointToScreen(new Point());
        var last = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
        int width = (int)Math.Ceiling(last.X - first.X), height = (int)Math.Ceiling(last.Y - first.Y);
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        nint previous = SelectObject(memory, bitmap);
        try
        {
            if (screen == 0 || memory == 0 || bitmap == 0 ||
                !BitBlt(memory, 0, 0, width, height, screen, (int)first.X, (int)first.Y, 0x40CC0020))
                throw new InvalidOperationException("Target desktop pixel capture failed.");
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            string directory = TestWorkspace.DataPath("desktop-shots");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name + ".png");
            using (var output = File.Create(path)) encoder.Save(output);
            Console.WriteLine($"DESKTOP_SHOT {path}; pixels={width}x{height}; method=screen-BitBlt-target-bounds");
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}

using System.Runtime.InteropServices;
using System.Windows;

namespace MeowFilm.Desktop;

internal readonly record struct ShellWindowStartupSize(
    double Width, double Height, double MinWidth, double MinHeight)
{
    internal static ShellWindowStartupSize Calculate(int screenPixelWidth, int screenPixelHeight, uint dpi)
    {
        double scale = dpi / 96d;
        return new(
            screenPixelWidth * (1300d / 2560) / scale,
            screenPixelHeight * (850d / 1440) / scale,
            screenPixelWidth * (860d / 2560) / scale,
            screenPixelHeight * (600d / 1440) / scale);
    }

    internal static void ApplyToPrimaryScreen(Window window, nint handle)
    {
        var size = Calculate(GetSystemMetrics(0), GetSystemMetrics(1), GetDpiForWindow(handle));
        window.MinWidth = size.MinWidth;
        window.MinHeight = size.MinHeight;
        window.Width = size.Width;
        window.Height = size.Height;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);
}

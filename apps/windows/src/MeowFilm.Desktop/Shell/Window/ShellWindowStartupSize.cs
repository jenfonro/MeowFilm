using System.Runtime.InteropServices;

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

    internal static void ApplyToPrimaryScreen(DesktopWindow window, nint handle)
    {
        var size = Calculate(GetSystemMetrics(0), GetSystemMetrics(1), GetDpiForWindow(handle));
        window.MinWidth = size.MinWidth;
        window.MinHeight = size.MinHeight;
        window.Resize(size.Width, size.Height);
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - window.AppWindow.Size.Width) / 2, area.Y + (area.Height - window.AppWindow.Size.Height) / 2));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);
}

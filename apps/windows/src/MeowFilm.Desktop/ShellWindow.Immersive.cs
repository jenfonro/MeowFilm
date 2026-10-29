using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private void OnSidebarWheel(object sender, MouseWheelEventArgs e)
    {
        if (SidebarScroll.ScrollableHeight <= 0 || e.Delta == 0) return;
        var distance = SystemParameters.WheelScrollLines < 0 ? SidebarScroll.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
        SidebarScroll.ScrollToVerticalOffset(SidebarScroll.VerticalOffset - e.Delta / 120d * distance);
        e.Handled = true;
    }
}

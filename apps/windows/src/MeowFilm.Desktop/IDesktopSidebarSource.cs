using System.Windows;

namespace MeowFilm.Desktop;

public interface IDesktopSidebarSource
{
    FrameworkElement SidebarView { get; }
    event EventHandler? SettingsRequested;
}

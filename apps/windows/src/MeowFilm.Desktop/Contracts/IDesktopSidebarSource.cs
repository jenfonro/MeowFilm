using Microsoft.UI.Xaml;

namespace MeowFilm.Desktop;

public interface IDesktopSidebarSource
{
    FrameworkElement SidebarView { get; }
    event EventHandler? SettingsRequested;
}

namespace MeowFilm.Desktop;

/// <summary>
/// Optional navigation intent from module-owned cards, lists, queue items, or other UI.
/// Do not re-emit this event from NavigateAsync; that method consumes a shell-directed request.
/// </summary>
public interface IDesktopNavigationSource
{
    event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested;
}

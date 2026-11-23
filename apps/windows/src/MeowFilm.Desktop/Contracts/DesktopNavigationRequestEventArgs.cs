namespace MeowFilm.Desktop;

public sealed class DesktopNavigationRequestEventArgs : EventArgs
{
    public DesktopNavigationRequestEventArgs(string route, bool fromPlayer = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        Route = route;
        FromPlayer = fromPlayer;
    }

    public bool FromPlayer { get; }
    public string Route { get; }
}

using System.Windows;

namespace MeowFilm.Desktop;

public enum DesktopGlobalSurface
{
    PlayerDetail,
    PlayQueue
}

public sealed class DesktopGlobalSurfaceRequestEventArgs(DesktopGlobalSurface surface) : EventArgs
{
    public DesktopGlobalSurface Surface { get; } = surface;
}

/// <summary>
/// A capability contributes retained content to the shell-owned global surfaces.
/// The shell alone owns placement, visibility, transitions, dismissal and focus.
/// </summary>
public interface IDesktopGlobalSurfaceSource
{
    FrameworkElement GetGlobalSurface(DesktopGlobalSurface surface);
    /// <summary>Starts content work. PlayerDetail is presented immediately; data readiness never gates its entrance.</summary>
    Task ActivateGlobalSurfaceAsync(DesktopGlobalSurface surface, CancellationToken cancellationToken);
    void DeactivateGlobalSurface(DesktopGlobalSurface surface);
    event EventHandler<DesktopGlobalSurfaceRequestEventArgs>? GlobalSurfaceRequested;
}

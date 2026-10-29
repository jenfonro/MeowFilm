namespace MeowFilm.Desktop;

/// <summary>
/// Recoverable two-phase lifecycle. Cancel reopens admission after failed App
/// preparation. Abort is explicit unsaved shutdown and must await actual owned
/// work/handle release; returning success permits dependency disposal.
/// </summary>
public interface IDesktopShutdownParticipant : IAsyncDisposable
{
    Task PrepareShutdownAsync();
    Task CancelShutdownPreparationAsync();
    Task AbortAsync();
}

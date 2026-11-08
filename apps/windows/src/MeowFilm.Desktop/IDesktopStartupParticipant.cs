namespace MeowFilm.Desktop;

/// <summary>
/// Optional local preparation required before the main window is constructed.
/// Does not navigate, start scripts, or wait for network work. Full initialization
/// remains owned by the shell's selected-module lifecycle.
/// </summary>
public interface IDesktopStartupParticipant
{
    Task PrepareStartupAsync(CancellationToken cancellationToken = default);
}

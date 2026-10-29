using System.Windows;

namespace MeowFilm.Desktop;

/// <summary>An App-owned public page, not a capability mode or a runtime module.
/// The shell mounts only the current visual tree; App composition retains object lifetime.</summary>
public interface IDesktopPage
{
    string Id { get; }
    string Title { get; }
    string Glyph { get; }
    bool ShowInSidebar => true;
    FrameworkElement View { get; }
    Task ActivateAsync(CancellationToken cancellationToken = default);
}

namespace MeowFilm.Desktop;

public sealed record DesktopOwnedNotice(object Owner, string Message);

/// <summary>Stable operation-owned notices survive page retirement. Each window
/// observes only its injected modules; this is not an application-wide event bus.</summary>
public interface IDesktopOwnedNoticeSource
{
    IReadOnlyList<DesktopOwnedNotice> OwnedNotices { get; }
    event EventHandler? OwnedNoticesChanged;
}

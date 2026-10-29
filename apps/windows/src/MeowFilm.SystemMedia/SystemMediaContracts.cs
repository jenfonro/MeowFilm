using System.ComponentModel;

namespace MeowFilm.SystemMedia;

public enum SystemPlaybackCommand { Play, Pause, Stop, Next, Previous, Seek }
public enum SystemPlaybackKind { Audio, Video }
public sealed record SystemPlaybackRequest(SystemPlaybackCommand Command, double PositionSeconds = 0);
public sealed record SystemPlaybackState(string? OwnerId, string? Identity, string Title,
    bool HasMedia, bool IsPlaying, double PositionSeconds, double DurationSeconds,
    bool CanNext = false, bool CanPrevious = false, SystemPlaybackKind Kind = SystemPlaybackKind.Audio);
public sealed record SystemPlaybackOwner(string Id,
    Func<CancellationToken, Task>? Next = null, Func<CancellationToken, Task>? Previous = null,
    SystemPlaybackKind Kind = SystemPlaybackKind.Audio);

/// <summary>Product injects its one shared player, not a second playback engine.</summary>
public interface ISystemPlayback : INotifyPropertyChanged
{
    SystemPlaybackState State { get; }
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(double seconds);
}

/// <summary>Only metadata and typed operations cross the system integration boundary.</summary>
public interface ISystemMediaBackend : IDisposable
{
    event EventHandler<SystemPlaybackRequest>? Requested;
    void Update(SystemPlaybackState state);
    void Disable();
}

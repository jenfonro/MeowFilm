using System.Runtime.CompilerServices;
using System.Windows;
using MeowFilm.Media;

[assembly: InternalsVisibleTo("MeowFilm.Windows.Tests")]

namespace MeowFilm.SystemMedia;

public sealed class SystemMediaWindowAttachment : IAsyncDisposable
{
    private readonly SystemMediaSession session;

    public SystemMediaWindowAttachment(Window window, NativePlayback playback,
        IEnumerable<SystemPlaybackOwner> owners)
        : this(window, new SystemMediaSession.NativeSource(playback), new WindowsSystemMediaBackend(window), owners) { }

    // Tests replace only this module's playback/backend ports; HWND acquisition
    // remains in the production constructor and lifecycle ownership stays here.
    internal SystemMediaWindowAttachment(Window window, ISystemPlayback playback,
        ISystemMediaBackend backend, IEnumerable<SystemPlaybackOwner> owners)
    {
        try { session = new SystemMediaSession(playback, backend, owners, window.Dispatcher); }
        catch { backend.Dispose(); throw; }
    }

    public ValueTask DisposeAsync() => session.DisposeAsync();
}

using System.Runtime.CompilerServices;
using System.Windows;
using MeowFilm.Media;

[assembly: InternalsVisibleTo("MeowFilm.Windows.Tests")]

namespace MeowFilm.SystemMedia;

public sealed class SystemMediaWindowAttachment : IAsyncDisposable
{
    private readonly Window window;
    private readonly SystemMediaSession session;

    public SystemMediaWindowAttachment(Window window, NativePlayback playback,
        IEnumerable<SystemPlaybackOwner> owners, bool enabled = true)
        : this(window, new SystemMediaSession.NativeSource(playback), new WindowsSystemMediaBackend(window), owners, enabled) { }

    // Tests replace only this module's playback/backend ports; HWND acquisition
    // remains in the production constructor and lifecycle ownership stays here.
    internal SystemMediaWindowAttachment(Window window, ISystemPlayback playback,
        ISystemMediaBackend backend, IEnumerable<SystemPlaybackOwner> owners, bool enabled = true)
        : this(window, CreateSession(window, playback, backend, owners, enabled)) { }

    internal SystemMediaWindowAttachment(Window window, SystemMediaSession session)
    {
        this.window = window;
        this.session = session;
        window.Closed += Closed;
    }

    private static SystemMediaSession CreateSession(Window window, ISystemPlayback playback,
        ISystemMediaBackend backend, IEnumerable<SystemPlaybackOwner> owners, bool enabled)
    {
        try { return new SystemMediaSession(playback, backend, owners, window.Dispatcher, enabled); }
        catch { backend.Dispose(); throw; }
    }

    // Shell can cancel Closing after its event handlers have run.
    private void Closed(object? sender, EventArgs args) => session.Suspend();

    public void SetEnabled(bool enabled) => session.SetEnabled(enabled);

    public ValueTask DisposeAsync()
    {
        window.Closed -= Closed;
        return session.DisposeAsync();
    }
}

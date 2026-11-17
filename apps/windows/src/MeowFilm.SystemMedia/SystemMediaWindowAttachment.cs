using System.Runtime.CompilerServices;
using System.Windows;

[assembly: InternalsVisibleTo("MeowFilm.Windows.Tests")]

namespace MeowFilm.SystemMedia;

public sealed class SystemMediaWindowAttachment : IAsyncDisposable
{
    private readonly Window window;
    private readonly SystemMediaSession session;

    // The App supplies its typed playback projection and OS backend; this module owns the window lifetime.
    public SystemMediaWindowAttachment(Window window, ISystemPlayback playback,
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

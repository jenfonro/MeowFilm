using System.Runtime.CompilerServices;
using System.Windows;
using MeowFilm.Media;

[assembly: InternalsVisibleTo("MeowFilm.Windows.Tests")]

namespace MeowFilm.SystemMedia;

public sealed class SystemMediaWindowAttachment : IAsyncDisposable
{
    private readonly Window window;
    private readonly SystemMediaSession session;
    private bool disposed;

    public SystemMediaWindowAttachment(Window window, NativePlayback playback,
        IEnumerable<SystemPlaybackOwner> owners, bool enabled = true)
        : this(window, CreateSession(window, playback, owners, enabled)) { }

    internal SystemMediaWindowAttachment(Window window, SystemMediaSession session)
    {
        this.window = window;
        this.session = session;
        window.Closed += Closed;
    }

    private static SystemMediaSession CreateSession(Window window, NativePlayback playback,
        IEnumerable<SystemPlaybackOwner> owners, bool enabled)
    {
        var backend = new WindowsSystemMediaBackend(window);
        try { return new SystemMediaSession(playback, backend, owners, window.Dispatcher, enabled); }
        catch { backend.Dispose(); throw; }
    }

    // Shell can cancel Closing after its event handlers have run.
    private void Closed(object? sender, EventArgs args) => session.Suspend();

    public void SetEnabled(bool enabled) => session.SetEnabled(enabled);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        window.Closed -= Closed;
        await session.DisposeAsync();
    }
}

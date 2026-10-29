using System.ComponentModel;
using System.Windows;
using MeowFilm.Media;

namespace MeowFilm.SystemMedia;

public sealed class SystemMediaWindowAttachment : IAsyncDisposable
{
    private readonly Window window;
    private readonly SystemMediaSession session;
    private bool disposed;

    public SystemMediaWindowAttachment(Window window, NativePlayback playback,
        IEnumerable<SystemPlaybackOwner> owners)
    {
        this.window = window;
        var backend = new WindowsSystemMediaBackend(window);
        try { session = new SystemMediaSession(playback, backend, owners, window.Dispatcher); }
        catch { backend.Dispose(); throw; }
        window.Closing += Closing;
    }

    private void Closing(object? sender, CancelEventArgs args) => session.Suspend();

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        window.Closing -= Closing;
        await session.DisposeAsync();
    }
}

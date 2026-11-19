using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using System.Windows.Threading;

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
        if (!window.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("SYSTEM_MEDIA_THREAD_ACCESS");
        this.window = window;
        this.session = session;
        window.Closed += Closed;
    }

    private static SystemMediaSession CreateSession(Window window, ISystemPlayback playback,
        ISystemMediaBackend backend, IEnumerable<SystemPlaybackOwner> owners, bool enabled)
    {
        try
        {
            if (!window.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("SYSTEM_MEDIA_THREAD_ACCESS");
            return new SystemMediaSession(playback, backend, owners, Dispatcher.CurrentDispatcher, enabled);
        }
        catch { backend.Dispose(); throw; }
    }

    // Shell can cancel Closing after its event handlers have run.
    private void Closed(object sender, WindowEventArgs args) => session.Suspend();

    public void SetEnabled(bool enabled) => session.SetEnabled(enabled);

    public ValueTask DisposeAsync()
    {
        if (!window.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("SYSTEM_MEDIA_THREAD_ACCESS");
        window.Closed -= Closed;
        return session.DisposeAsync();
    }
}

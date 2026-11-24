using Microsoft.UI.Xaml;
using MeowFilm.Threading;

namespace MeowFilm.SystemMedia;

public sealed class SystemMediaWindowAttachment : IAsyncDisposable
{
    private readonly Window window;
    private readonly SystemMediaSession session;

    // The App supplies its typed playback projection and OS backend; this module owns the window lifetime.
    public SystemMediaWindowAttachment(Window window, ISystemPlayback playback,
        WindowsSystemMediaBackend backend, IEnumerable<SystemPlaybackOwner> owners, bool enabled = true)
    {
        if (!window.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("SYSTEM_MEDIA_THREAD_ACCESS");
        this.window = window;
        try { session = new SystemMediaSession(playback, backend, owners, OwnerDispatcher.Current, enabled); }
        catch { backend.Dispose(); throw; }
        window.Closed += Closed;
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

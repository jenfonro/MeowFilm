using System.ComponentModel;
using System.Windows;
using MeowFilm.Capabilities;
using MeowFilm.Media;
using MeowFilm.SystemMedia;

// The App's typed projection retains the real Film owner, including external output.
internal sealed class AppSystemMediaAttachment : IAsyncDisposable
{
    private readonly AppSystemPlayback source;
    private readonly SystemMediaSession session;

    internal AppSystemMediaAttachment(Window window, NativePlayback native, FilmCapability film,
        SystemPlaybackOwner[] owners, bool enabled = true)
        : this(window, new NativeSource(native), film, new WindowsSystemMediaBackend(window), owners, enabled) { }

    // Only the native player and OS backend ports are replaced by no-decoder tests.
    internal AppSystemMediaAttachment(Window window, ISystemPlayback native, FilmCapability film,
        ISystemMediaBackend backend, SystemPlaybackOwner[] owners, bool enabled = true)
    {
        source = new(native, film);
        try
        {
            source.Attach();
            session = new(source, backend, owners, window.Dispatcher, enabled);
        }
        catch { source.Dispose(); backend.Dispose(); throw; }
    }

    public void SetEnabled(bool enabled) => session.SetEnabled(enabled);

    public async ValueTask DisposeAsync()
    {
        try { await session.DisposeAsync(); }
        finally { source.Dispose(); }
    }

    private sealed class AppSystemPlayback(ISystemPlayback native, FilmCapability film) : ISystemPlayback, IDisposable
    {
        public SystemPlaybackState State
        {
            get
            {
                var state = native.State;
                var video = film.PlaybackMedia;
                return state.OwnerId == "film" && state.Identity == video.Identity
                    ? new("film", video.Identity, video.Title, video.HasMedia, video.IsPlaying, video.Position, video.Duration)
                    : state;
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Attach()
        {
            native.PropertyChanged += Changed;
            film.PropertyChanged += FilmChanged;
        }
        private void Changed(object? sender, PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);
        private void FilmChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(FilmCapability.PlaybackMedia)) Changed(sender, args);
        }
        public Task PlayAsync() => native.State.OwnerId == "film" ? film.ResumePlaybackAsync() : native.PlayAsync();
        public Task PauseAsync() => native.State.OwnerId == "film" ? film.PausePlaybackAsync() : native.PauseAsync();
        public Task StopAsync() => native.State.OwnerId == "film" ? film.StopPlaybackAsync() : native.StopAsync();
        public Task SeekAsync(double seconds) => native.State.OwnerId == "film" ? film.SeekPlaybackAsync(seconds) : native.SeekAsync(seconds);
        public void Dispose()
        {
            native.PropertyChanged -= Changed;
            film.PropertyChanged -= FilmChanged;
        }
    }

    private sealed class NativeSource(NativePlayback native) : ISystemPlayback
    {
        public SystemPlaybackState State => new(native.OwnerId, native.Identity, native.Title,
            native.HasMedia || native.ContinuationPending, native.IsPlaying, native.PositionSeconds, native.DurationSeconds);
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => native.PropertyChanged += value;
            remove => native.PropertyChanged -= value;
        }
        public Task PlayAsync() => native.ResumeAsync();
        public Task PauseAsync() => native.PauseAsync();
        public Task StopAsync() => native.StopAsync();
        public Task SeekAsync(double seconds) => native.SeekAsync(seconds);
    }
}

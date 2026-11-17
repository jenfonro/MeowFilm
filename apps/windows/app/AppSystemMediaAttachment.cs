using System.ComponentModel;
using System.Windows;
using MeowFilm.Capabilities;
using MeowFilm.Media;
using MeowFilm.SystemMedia;

// The App's typed projection retains the real Film owner, including external output.
internal sealed class AppSystemMediaAttachment : IAsyncDisposable
{
    private readonly AppSystemPlayback source;
    private readonly SystemMediaWindowAttachment session;

    internal AppSystemMediaAttachment(Window window, NativeAudioPlayback music, FilmPlayback video, FilmCapability film,
        Func<string?> activeOwner, SystemPlaybackOwner[] owners, bool enabled = true)
        : this(window, new AudioSource(music), new VideoSource(video), film, activeOwner,
            new WindowsSystemMediaBackend(window), owners, enabled) { }

    // Only the native player and OS backend ports are replaced by no-decoder tests.
    internal AppSystemMediaAttachment(Window window, ISystemPlayback music, ISystemPlayback video, FilmCapability film,
        Func<string?> activeOwner, ISystemMediaBackend backend, SystemPlaybackOwner[] owners, bool enabled = true)
    {
        source = new(music, video, film, activeOwner);
        try { source.Attach(); }
        catch { source.Dispose(); backend.Dispose(); throw; }
        try { session = new(window, source, backend, owners, enabled); }
        catch { source.Dispose(); throw; }
    }

    public void SetEnabled(bool enabled) => session.SetEnabled(enabled);

    public async ValueTask DisposeAsync()
    {
        try { await session.DisposeAsync(); }
        finally { source.Dispose(); }
    }

    private sealed class AppSystemPlayback(ISystemPlayback music, ISystemPlayback video, FilmCapability film,
        Func<string?> activeOwner) : ISystemPlayback, IDisposable
    {
        private bool FilmSelected => activeOwner() == "film";
        public SystemPlaybackState State
        {
            get
            {
                if (!FilmSelected) return music.State;
                var state = video.State;
                var media = film.PlaybackMedia;
                return state.OwnerId == "film" && state.Identity == media.Identity
                    ? new("film", media.Identity, media.Title, media.HasMedia, media.IsPlaying,
                        media.Position, media.Duration, Kind: SystemPlaybackKind.Video)
                    : state;
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Attach()
        {
            music.PropertyChanged += Changed;
            video.PropertyChanged += Changed;
            film.PropertyChanged += FilmChanged;
        }
        private void Changed(object? sender, PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);
        private void FilmChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(FilmCapability.PlaybackMedia)) Changed(sender, args);
        }
        public Task PlayAsync() => FilmSelected ? film.ResumePlaybackAsync() : music.PlayAsync();
        public Task PauseAsync() => FilmSelected ? film.PausePlaybackAsync() : music.PauseAsync();
        public Task StopAsync() => FilmSelected ? film.StopPlaybackAsync() : music.StopAsync();
        public Task SeekAsync(double seconds) => FilmSelected ? film.SeekPlaybackAsync(seconds) : music.SeekAsync(seconds);
        public void Dispose()
        {
            music.PropertyChanged -= Changed;
            video.PropertyChanged -= Changed;
            film.PropertyChanged -= FilmChanged;
        }
    }

    private sealed class AudioSource(NativeAudioPlayback native) : ISystemPlayback
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

    private sealed class VideoSource(FilmPlayback native) : ISystemPlayback
    {
        public SystemPlaybackState State => new("film", native.Identity, "", native.HasOutput,
            native.HasOutput && !native.IsPaused, native.PositionSeconds, native.DurationSeconds,
            Kind: SystemPlaybackKind.Video);
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => native.PropertyChanged += value;
            remove => native.PropertyChanged -= value;
        }
        public Task PlayAsync() => native.Scope is { } scope ? native.SetPausedAsync(scope, false) : Task.CompletedTask;
        public Task PauseAsync() => native.Scope is { } scope ? native.SetPausedAsync(scope, true) : Task.CompletedTask;
        public Task StopAsync() => native.StopAsync();
        public Task SeekAsync(double seconds) => native.Scope is { } scope ? native.SeekAsync(scope, seconds) : Task.CompletedTask;
    }
}

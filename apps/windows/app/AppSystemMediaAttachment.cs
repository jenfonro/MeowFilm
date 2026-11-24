using System.ComponentModel;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.SystemMedia;

// The App's typed projection retains the real Film owner, including external output.
internal sealed class AppSystemMediaAttachment : IAsyncDisposable
{
    private readonly AppSystemPlayback source;
    private readonly SystemMediaWindowAttachment session;

    internal AppSystemMediaAttachment(DesktopWindow window, NativeAudioPlayback music, FilmPlayback video, FilmCapability film,
        AppPlaybackCoordinator coordinator, SystemPlaybackOwner[] owners, bool enabled = true)
    {
        var backend = new WindowsSystemMediaBackend(window);
        source = new(new AudioSource(music), video, film, coordinator);
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

    private sealed class AppSystemPlayback(ISystemPlayback music, FilmPlayback video, FilmCapability film,
        AppPlaybackCoordinator coordinator) : ISystemPlayback, IDisposable
    {
        private FilmPlaybackPage? trackedPlayback;
        private bool FilmSelected => coordinator.ActiveOwner == "film";
        public SystemPlaybackState State
        {
            get
            {
                if (!FilmSelected) return music.State;
                var media = film.PlaybackMedia;
                double rate = film.Playback is { HasOutput: true } page ? page.PlaybackRate : 1.0;
                return video.Identity == media.Identity
                    ? new("film", media.Identity, media.Title, media.HasMedia, media.IsPlaying,
                        media.Position, media.Duration, Kind: SystemPlaybackKind.Video,
                        CanSeek: media.HasMedia && video.CanSeek, PlaybackRate: rate)
                    : new("film", video.Identity, "", video.HasOutput,
                        video.HasOutput && !video.IsPaused, video.PositionSeconds, video.DurationSeconds,
                        Kind: SystemPlaybackKind.Video, CanSeek: video.HasOutput && video.CanSeek, PlaybackRate: 1.0);
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Attach()
        {
            music.PropertyChanged += Changed;
            video.PropertyChanged += Changed;
            film.PropertyChanged += FilmChanged;
            coordinator.ActiveOwnerChanged += OwnerChanged;
            TrackPlaybackPage(film.Playback);
        }
        private void TrackPlaybackPage(FilmPlaybackPage? next)
        {
            if (ReferenceEquals(trackedPlayback, next)) return;
            if (trackedPlayback is not null) trackedPlayback.PropertyChanged -= PlaybackPageChanged;
            trackedPlayback = next;
            if (trackedPlayback is not null) trackedPlayback.PropertyChanged += PlaybackPageChanged;
        }
        private void PlaybackPageChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is null or "" or nameof(FilmPlaybackPage.PlaybackRate) or nameof(FilmPlaybackPage.CanSeek))
                PropertyChanged?.Invoke(this, new(nameof(State)));
        }
        private void Changed(object? sender, PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);
        private void OwnerChanged(object? sender, EventArgs args) => PropertyChanged?.Invoke(this, new(nameof(State)));
        private void FilmChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(FilmCapability.Playback))
            {
                TrackPlaybackPage(film.Playback);
                Changed(sender, new(nameof(State)));
                return;
            }
            if (args.PropertyName == nameof(FilmCapability.PlaybackMedia)) Changed(sender, args);
        }
        public Task PlayAsync() => FilmSelected ? film.ResumePlaybackAsync() : music.PlayAsync();
        public Task PauseAsync() => FilmSelected ? film.PausePlaybackAsync() : music.PauseAsync();
        public Task StopAsync() => FilmSelected ? film.StopPlaybackAsync() : music.StopAsync();
        public Task SeekAsync(double seconds) => FilmSelected ? film.SeekPlaybackAsync(seconds) : music.SeekAsync(seconds);
        public void Dispose()
        {
            TrackPlaybackPage(null);
            music.PropertyChanged -= Changed;
            video.PropertyChanged -= Changed;
            film.PropertyChanged -= FilmChanged;
            coordinator.ActiveOwnerChanged -= OwnerChanged;
        }
    }

    private sealed class AudioSource(NativeAudioPlayback native) : ISystemPlayback
    {
        public SystemPlaybackState State => new(native.OwnerId, native.Identity, native.Title,
            native.HasMedia || native.ContinuationPending, native.IsPlaying, native.PositionSeconds, native.DurationSeconds,
            CanSeek: native.CanSeek, PlaybackRate: native.Rate);
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

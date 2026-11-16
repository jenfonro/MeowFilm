using System.Windows.Threading;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.AudioCoreFixtures;

namespace MeowFilm.Windows.Tests;

// The actual App coordinator and both native owners, with explicit no-device sinks.
public static class AppPlaybackCoordinatorTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("app.playback.native-owners-retire-before-opposite-output-start", () => Sta(Swap));
        yield return new("app.playback.paused-music-preparation-does-not-stop-film", () => Sta(PausedPreparation));
        yield return new("app.playback.held-music-admission-drains-queued-film-admission", () => Sta(QueuedAdmission));
    }

    private static NativePlayback Film(AppPlaybackCoordinator coordinator,
        Func<CancellationToken, Task<IDisposable>>? acquire = null) =>
        new(Dispatcher.CurrentDispatcher, audioOutput: NativeAudioOutput.Dummy,
            acquireOutput: acquire ?? coordinator.AcquireFilmAsync);

    private static async Task StartFilm(NativePlayback film, Lease lease, string identity = "film",
        CancellationToken token = default)
    {
        await film.OpenReadAsync("film", identity, lease, "generated WAV", token);
        await Until(() =>
        {
            if (film.LastErrorCode is { } error) throw new InvalidOperationException(error);
            return film.IsPlaying && film.PositionSeconds > 0;
        }, "actual video-owner dummy render");
    }

    private static async Task Swap()
    {
        NativeAudioPlayback? audio = null;
        NativePlayback? video = null;
        var coordinator = new AppPlaybackCoordinator(
            () => audio?.StopAsync() ?? Task.CompletedTask, () => video?.StopAsync() ?? Task.CompletedTask);
        await using var music = Dummy(coordinator.AcquireMusicAsync);
        await using var film = Film(coordinator);
        audio = music; video = film;
        var firstFilm = new Lease(WaveBytes(12));
        var song = new Lease(WaveBytes(12));
        var secondFilm = new Lease(WaveBytes(12));
        await StartFilm(film, firstFilm);
        await Start(music, () => music.OpenReadAsync("music", "song", song, "generated WAV"));
        firstFilm.AssertReleased();
        Check(coordinator.ActiveOwner == "music" && music.IsPlaying && !film.HasMedia && !film.IsPlaying,
            "music admission waits for the actual film owner to stop and release");
        await StartFilm(film, secondFilm, "next-film");
        song.AssertReleased();
        Check(coordinator.ActiveOwner == "film" && film.IsPlaying && !music.HasMedia && !music.IsPlaying,
            "film admission waits for the actual music owner to stop and release");
        await film.StopAsync(); secondFilm.AssertReleased();
    }

    private static async Task PausedPreparation()
    {
        NativeAudioPlayback? audio = null;
        NativePlayback? video = null;
        var coordinator = new AppPlaybackCoordinator(
            () => audio?.StopAsync() ?? Task.CompletedTask, () => video?.StopAsync() ?? Task.CompletedTask);
        await using var music = Dummy(coordinator.AcquireMusicAsync);
        await using var film = Film(coordinator);
        audio = music; video = film;
        var scene = new Lease(WaveBytes(12));
        var song = new Lease(WaveBytes(12));
        await StartFilm(film, scene);
        song.Stream.BlockReads();
        var opening = music.OpenReadAsync("music", "paused-song", song, "generated WAV");
        try
        {
            await UntilBlocked(song.Stream.Blocked.Task, opening);
            await music.PauseAsync();
            song.Stream.UnblockReads();
            await opening.WaitAsync(TimeSpan.FromSeconds(4));
            await music.SeekAsync(1);
            Check(coordinator.ActiveOwner == "film" && film.IsPlaying && scene.DisposeCalls == 0 &&
                music.HasMedia && music.PauseRequested && !music.IsPlaying,
                "inaudible music preparation and seek never acquire or steal film output");
            await music.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(4));
            scene.AssertReleased();
            Check(coordinator.ActiveOwner == "music" && music.IsPlaying && !film.HasMedia,
                "the later Resume alone acquires output and retires film");
            await music.StopAsync(); song.AssertReleased();
        }
        finally { song.Stream.UnblockReads(); }
    }

    private static async Task QueuedAdmission()
    {
        NativeAudioPlayback? audio = null;
        NativePlayback? video = null;
        bool hold = false;
        var stoppingFilm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedFilm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AppPlaybackCoordinator(
            () => audio?.StopAsync() ?? Task.CompletedTask, async () =>
            {
                if (hold) { stoppingFilm.TrySetResult(); await releaseStop.Task; }
                if (video is not null) await video.StopAsync();
            });
        await using var music = Dummy(coordinator.AcquireMusicAsync);
        await using var film = Film(coordinator, token =>
        {
            if (hold) queuedFilm.TrySetResult();
            return coordinator.AcquireFilmAsync(token);
        });
        audio = music; video = film;
        var oldFilm = new Lease(WaveBytes(12));
        var nextFilm = new Lease(WaveBytes(12));
        var song = new Lease(WaveBytes(12));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await StartFilm(film, oldFilm);
        hold = true;
        var opening = music.OpenReadAsync("music", "winner", song, "generated WAV", lifetime.Token);
        Task replacement = Task.CompletedTask;
        try
        {
            await stoppingFilm.Task.WaitAsync(TimeSpan.FromSeconds(4));
            replacement = film.OpenReadAsync("film", "queued", nextFilm, "generated WAV", lifetime.Token);
            await queuedFilm.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(!opening.IsCompleted && !replacement.IsCompleted && !music.IsPlaying,
                "both native openings are pending behind the App's held output permit");
            releaseStop.TrySetResult();
            await opening.WaitAsync(TimeSpan.FromSeconds(4));
            try { await replacement.WaitAsync(TimeSpan.FromSeconds(4)); throw new InvalidOperationException("Cancelled film was admitted"); }
            catch (OperationCanceledException) { }
            catch (PlaybackException error) when (error.Code == PlaybackError.Superseded) { }
            oldFilm.AssertReleased(); nextFilm.AssertReleased();
            Check(coordinator.ActiveOwner == "music" && music.Identity == "winner" && music.IsPlaying &&
                !film.HasMedia && !film.IsPlaying, "queued film admission cancels and drains before music starts");
            await music.StopAsync(); song.AssertReleased();
        }
        finally
        {
            releaseStop.TrySetResult();
            lifetime.Cancel();
            try { await Task.WhenAll(opening, replacement).WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }
}

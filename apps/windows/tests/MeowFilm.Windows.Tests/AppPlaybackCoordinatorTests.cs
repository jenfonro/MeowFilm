using System.Windows.Threading;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.AudioCoreFixtures;
using FilmLease = MeowFilm.Windows.Tests.MediaResourceTests.Lease;

namespace MeowFilm.Windows.Tests;

// The actual App coordinator, no-device music sink and muted native Film owner.
public static class AppPlaybackCoordinatorTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("app.playback.native-owners-retire-before-opposite-output-start", () => Sta(Swap));
        yield return new("app.playback.paused-music-preparation-does-not-stop-film", () => Sta(PausedPreparation));
        yield return new("app.playback.held-music-admission-drains-queued-film-admission", () => Sta(QueuedAdmission));
    }

    private static FilmPlayback Film(AppPlaybackCoordinator coordinator,
        Func<CancellationToken, Task<IDisposable>>? acquire = null) =>
        new(Dispatcher.CurrentDispatcher, acquireOutput: acquire ?? coordinator.AcquireFilmAsync) { Volume = 0 };

    private static async Task StartFilm(FilmPlayback film, FilmLease lease, string identity = "film",
        CancellationToken token = default)
    {
        var intent = new RuntimeMediaPlaybackIntent("film", identity, 0, false);
        await film.PrepareAsync(intent, lease.InputAddress, FilmKernelIntegrationTests.Input("progressive"), lease, token);
        Check(await film.TryCommitAsync(intent, () => true, () => { }, token), "actual Film output commit");
        await Until(() =>
        {
            if (film.LastErrorCode is { } error) throw new InvalidOperationException(error);
            return film.Output is { SubmittedPictures: > 0 } && !film.IsPaused && film.PositionSeconds > 0;
        }, "actual Film owner movie render");
    }

    private static async Task Swap()
    {
        NativeAudioPlayback? audio = null;
        FilmPlayback? video = null;
        var coordinator = new AppPlaybackCoordinator(
            () => audio?.StopAsync() ?? Task.CompletedTask, () => video?.StopAsync() ?? Task.CompletedTask);
        await using var music = Dummy(coordinator.AcquireMusicAsync);
        await using var origin = FilmKernelIntegrationTests.Fixture("progressive");
        var film = Film(coordinator);
        await using var window = await NativeMediaFixtures.ShowFilmAsync(film);
        audio = music; video = film;
        var firstFilm = new FilmLease(origin.Uri("/root"), origin.Headers);
        var song = new Lease(WaveBytes(12));
        var secondFilm = new FilmLease(origin.Uri("/root"), origin.Headers);
        await StartFilm(film, firstFilm);
        await Start(music, () => music.OpenReadAsync("music", "song", song, "generated WAV"));
        firstFilm.AssertReleased();
        Check(coordinator.ActiveOwner == "music" && music.IsPlaying && !film.HasOutput && film.Scope is null,
            "music admission waits for the actual film owner to stop and release");
        await StartFilm(film, secondFilm, "next-film");
        song.AssertReleased();
        Check(coordinator.ActiveOwner == "film" && film.HasOutput && !film.IsPaused && !music.HasMedia && !music.IsPlaying,
            "film admission waits for the actual music owner to stop and release");
        await film.StopAsync(); secondFilm.AssertReleased();
    }

    private static async Task PausedPreparation()
    {
        NativeAudioPlayback? audio = null;
        FilmPlayback? video = null;
        var coordinator = new AppPlaybackCoordinator(
            () => audio?.StopAsync() ?? Task.CompletedTask, () => video?.StopAsync() ?? Task.CompletedTask);
        await using var music = Dummy(coordinator.AcquireMusicAsync);
        await using var origin = FilmKernelIntegrationTests.Fixture("progressive");
        var film = Film(coordinator);
        await using var window = await NativeMediaFixtures.ShowFilmAsync(film);
        audio = music; video = film;
        var scene = new FilmLease(origin.Uri("/root"), origin.Headers);
        var song = new Lease(WaveBytes(12));
        try
        {
            await StartFilm(film, scene);
            song.Stream.BlockReads();
            var opening = music.OpenReadAsync("music", "paused-song", song, "generated WAV");
            await UntilBlocked(song.Stream.Blocked.Task, opening);
            await music.PauseAsync();
            song.Stream.UnblockReads();
            await opening.WaitAsync(TimeSpan.FromSeconds(4));
            await music.SeekAsync(1);
            Check(coordinator.ActiveOwner == "film" && film.HasOutput && !film.IsPaused && scene.DisposeCalls == 0 &&
                music.HasMedia && music.PauseRequested && !music.IsPlaying,
                "inaudible music preparation and seek never acquire or steal film output");
            await music.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(4));
            scene.AssertReleased();
            Check(coordinator.ActiveOwner == "music" && music.IsPlaying && !film.HasOutput,
                "the later Resume alone acquires output and retires film");
            await music.StopAsync(); song.AssertReleased();
        }
        finally { song.Stream.UnblockReads(); }
    }

    private static async Task QueuedAdmission()
    {
        NativeAudioPlayback? audio = null;
        FilmPlayback? video = null;
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
        await using var origin = FilmKernelIntegrationTests.Fixture("progressive");
        var film = Film(coordinator, token =>
        {
            if (hold) queuedFilm.TrySetResult();
            return coordinator.AcquireFilmAsync(token);
        });
        await using var window = await NativeMediaFixtures.ShowFilmAsync(film);
        audio = music; video = film;
        var oldFilm = new FilmLease(origin.Uri("/root"), origin.Headers);
        var nextFilm = new FilmLease(origin.Uri("/root"), origin.Headers);
        var song = new Lease(WaveBytes(12));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        Task opening = Task.CompletedTask;
        Task<bool> replacement = Task.FromResult(false);
        int queuedCommits = 0;
        async Task<bool> QueueFilm()
        {
            var intent = new RuntimeMediaPlaybackIntent("film", "queued", 0, false);
            await film.PrepareAsync(intent, nextFilm.InputAddress, FilmKernelIntegrationTests.Input("progressive"), nextFilm, lifetime.Token);
            return await film.TryCommitAsync(intent, () => true, () => queuedCommits++, lifetime.Token);
        }
        try
        {
            await StartFilm(film, oldFilm);
            hold = true;
            opening = music.OpenReadAsync("music", "winner", song, "generated WAV", lifetime.Token);
            await stoppingFilm.Task.WaitAsync(TimeSpan.FromSeconds(4));
            replacement = QueueFilm();
            await queuedFilm.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(!opening.IsCompleted && !replacement.IsCompleted && !music.IsPlaying,
                "both native openings are pending behind the App's held output permit");
            releaseStop.TrySetResult();
            await opening.WaitAsync(TimeSpan.FromSeconds(4));
            try
            {
                await replacement.WaitAsync(TimeSpan.FromSeconds(4));
                throw new InvalidOperationException("Expected superseded film admission");
            }
            catch (PlaybackException error) when (error.Code == PlaybackError.Superseded) { }
            Check(queuedCommits == 0, "queued film never publishes output");
            oldFilm.AssertReleased(); nextFilm.AssertReleased();
            Check(coordinator.ActiveOwner == "music" && music.Identity == "winner" && music.IsPlaying &&
                !film.HasOutput && film.Scope is null, "queued film admission cancels and drains before music starts");
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

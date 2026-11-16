using System.Windows.Threading;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.AudioCoreFixtures;

namespace MeowFilm.Windows.Tests;

public static partial class AudioPlaybackTests
{
    private static Task QueueRead(NativeAudioPlayback player, Lease lease, CancellationToken token = default)
    {
        // Called with dispatcher processing disabled. Waiting for the outer task
        // proves OpenReadAsync returned its pending task, not just worker entry.
        return Task.Factory.StartNew(() => Open(player, lease, token: token), CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
            .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    }

    private static async Task QueuedReadCancellation()
    {
        await using var player = Dummy();
        using var cancel = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease(WaveBytes(1)) { DisposeGate = release.Task };
        int events = 0;
        player.Started += (_, _) => events++;
        player.Ended += (_, _) => events++;
        player.Failed += (_, _) => events++;
        Task opening = Task.CompletedTask;
        try
        {
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                opening = QueueRead(player, lease, cancel.Token);
                Check(!opening.IsCompleted && lease.Stream.ReadCalls == 0, "owned read is queued, not executed");
                cancel.Cancel();
                Check(lease.ReleaseEntered.Task.Wait(3000), "queued cancellation releases without dispatcher execution");
                Check(!opening.IsCompleted && !lease.Released.Task.IsCompleted && lease.Stream.CloseCalls == 0,
                    "cancelled opening cannot complete before physical lease release");
                release.TrySetResult();
                Check(lease.Released.Task.Wait(3000), "physical release does not require dispatcher processing");
            }
            await Cancelled(() => opening.WaitAsync(TimeSpan.FromSeconds(3)));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            lease.AssertReleased();
            Check(lease.Stream.ReadCalls == 0 && !player.HasMedia && !player.IsPlaying &&
                player.OwnerId is null && player.Identity is null && player.LastErrorCode is null && events == 0,
                "cancelled queued delegate never reads, publishes identity, or emits playback events");
        }
        finally
        {
            release.TrySetResult();
            cancel.Cancel();
            try { await opening.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task QueuedReadRevocation()
    {
        await using var player = Dummy();
        var active = new Lease(WaveBytes(12));
        var rejected = new Lease(WaveBytes(12));
        await StartRead(player, active, "healthy");
        var donor = Field(player, "session");
        int terminal = 0;
        player.Failed += (_, _) => terminal++;
        player.Ended += (_, _) => terminal++;
        Task opening;
        using (Dispatcher.CurrentDispatcher.DisableProcessing())
        {
            opening = QueueRead(player, rejected);
            rejected.Revoke();
        }
        await Error(() => opening, PlaybackError.ReadRevoked);
        rejected.AssertReleased();
        Check(ReferenceEquals(donor, Field(player, "session")) && player.Identity == "healthy" &&
            player.HasMedia && player.IsPlaying && player.LastErrorCode is null && terminal == 0 &&
            active.DisposeCalls == 0 && rejected.Stream.ReadCalls == 0,
            "a queued revoked lease cannot retire or replace its healthy donor");
        await player.StopAsync();
        active.AssertReleased();
    }

    private static async Task CloseQueuedRead(bool dispose, bool cancelFirst = false)
    {
        await using var player = Dummy();
        using var cancel = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease(WaveBytes(1)) { DisposeGate = release.Task };
        int events = 0;
        player.Started += (_, _) => events++;
        player.Ended += (_, _) => events++;
        player.Failed += (_, _) => events++;
        Task opening = Task.CompletedTask, closing = Task.CompletedTask;
        try
        {
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                opening = QueueRead(player, lease, cancel.Token);
                Check(!opening.IsCompleted && lease.Stream.ReadCalls == 0, "owned read is queued before close");
                if (cancelFirst)
                {
                    cancel.Cancel();
                    Check(lease.ReleaseEntered.Task.Wait(3000), "caller cancellation entered physical release");
                }
                closing = dispose ? player.DisposeAsync().AsTask() : player.StopAsync();
                Check(lease.ReleaseEntered.Task.Wait(3000), "close cancels the queued read without executing it");
                Check(!closing.IsCompleted && !opening.IsCompleted && !lease.Released.Task.IsCompleted &&
                    lease.Stream.CloseCalls == 0, "close drains queued ownership through physical release");
            }
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(!closing.IsCompleted && !opening.IsCompleted && !lease.Released.Task.IsCompleted,
                "close remains pending on physical release, not on suspended dispatcher processing");
            release.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(3));
            lease.AssertReleased(); // At close completion, before separately awaiting the rejected open.
            await Superseded(opening);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(lease.Stream.ReadCalls == 0 && !player.HasMedia && !player.IsPlaying &&
                player.OwnerId is null && player.Identity is null && player.LastErrorCode is null && events == 0,
                "queued read cannot resurrect playback after Stop or Dispose");
            if (!dispose)
            {
                var next = new Lease(WaveBytes(12));
                await StartRead(player, next, "after-stop");
                Check(player.IsPlaying && player.Identity == "after-stop" && events == 1,
                    "Stop cancels only the old queue, not subsequent read requests");
                await player.StopAsync();
                next.AssertReleased();
            }
        }
        finally
        {
            release.TrySetResult();
            cancel.Cancel();
            try { await Task.WhenAll(opening, closing).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task QueuedReadReleaseFailure(bool dispose)
    {
        var player = Dummy();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease(WaveBytes(1)) { DisposeGate = release.Task };
        Task opening = Task.CompletedTask, closing = Task.CompletedTask;
        try
        {
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                opening = QueueRead(player, lease);
                closing = dispose ? player.DisposeAsync().AsTask() : player.StopAsync();
                Check(lease.ReleaseEntered.Task.Wait(3000), "close entered queued lease release");
                Check(!closing.IsCompleted, "close cannot finish before the release attempt");
                release.TrySetException(new System.IO.IOException("synthetic-audio-release-canary"));
            }
            await Error(() => closing.WaitAsync(TimeSpan.FromSeconds(3)), PlaybackError.ReleaseFailed);
            await Error(() => opening.WaitAsync(TimeSpan.FromSeconds(3)), PlaybackError.ReleaseFailed);
            Check(lease.DisposeCalls == 1 && lease.Stream.CloseCalls == 0 && lease.Stream.ReadCalls == 0 &&
                !lease.Released.Task.IsCompleted && !player.HasMedia && !player.IsPlaying,
                "failed release is not a successful drain or a late input start");
            if (!dispose)
            {
                using var wave = new Wave(1);
                await Error(() => player.OpenAsync("music", "after-release-failure", wave.Uri, "silent WAV"),
                    PlaybackError.ReleaseFailed);
                wave.AssertReleased();
            }
        }
        finally
        {
            release.TrySetException(new System.IO.IOException("synthetic-audio-release-canary"));
            lease.Stream.Dispose(); // Reclaim this deliberately failing fixture, not an owner success receipt.
            await Error(() => player.DisposeAsync().AsTask(), PlaybackError.ReleaseFailed);
        }
    }

    private static async Task CloseAfterReleaseFailure(bool dispose, bool preparedFailure)
    {
        var player = Dummy();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new Lease(WaveBytes(12));
        var queued = new Lease(WaveBytes(12)) { DisposeGate = release.Task };
        var candidate = preparedFailure ? new Lease(WaveBytes(12)) : null;
        var failed = candidate ?? active;
        Task opening = Task.CompletedTask, closing = Task.CompletedTask;
        try
        {
            await StartRead(player, active);
            if (candidate is not null)
            {
                await player.PrepareReadHandoffAsync(candidate, default);
                active.DisposeGate = release.Task;
            }
            failed.DisposeGate = Task.FromException(new System.IO.IOException("synthetic-audio-release-canary"));
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                opening = QueueRead(player, queued);
                closing = dispose ? player.DisposeAsync().AsTask() : player.StopAsync();
                Check(queued.ReleaseEntered.Task.Wait(3000), "close starts queued cleanup despite a different release failure");
            }
            await failed.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await active.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(!closing.IsCompleted && !opening.IsCompleted && !queued.Released.Task.IsCompleted,
                "one failed release does not bypass other accepted cleanup receipts");
            release.TrySetResult();
            await Error(() => closing.WaitAsync(TimeSpan.FromSeconds(3)), PlaybackError.ReleaseFailed);
            await Superseded(opening);
            queued.AssertReleased();
            if (candidate is not null) active.AssertReleased();
            Check(failed.DisposeCalls == 1 && !failed.Released.Task.IsCompleted &&
                queued.Stream.ReadCalls == 0 && !player.HasMedia && !player.IsPlaying &&
                Field(player, "prepared") is null && Field(player, "session") is null &&
                player.LastErrorCode == PlaybackError.ReleaseFailed,
                "all other resources drain once; the failed receipt is never retried or reported successful");
        }
        finally
        {
            release.TrySetResult();
            try { await Task.WhenAll(opening, closing).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
            failed.Stream.Dispose(); // Reclaim the deliberately failed fixture, not a successful owner receipt.
            await Error(() => player.DisposeAsync().AsTask(), PlaybackError.ReleaseFailed);
        }
    }
}

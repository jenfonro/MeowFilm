using System.Windows.Threading;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.AudioCoreFixtures;

namespace MeowFilm.Windows.Tests;

// The explicit Dummy sink exercises the real decoder/owner, not WASAPI or a speaker.
public static partial class AudioPlaybackTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("initial-volume-empty-commands-and-dispose", InitialState);
        yield return Case("wav-start-pause-resume-seek-rate-stop", Playback);
        yield return Case("natural-end-releases-before-event", NaturalEnd);
        yield return Case("rejected-owned-input-preserves-current", RejectedInput);
        yield return Case("caller-cancellation-remains-active-while-paused", CallerCancellation);
        yield return Case("caller-cancellation-fences-queued-authorization-failure", QueuedRevocationCancellation);
        yield return Case("caller-cancellation-during-ended-retirement-fences-publication", () => CancelFinishRetirement(revoke: false));
        yield return Case("caller-cancellation-during-failed-retirement-fences-publication", () => CancelFinishRetirement(revoke: true));
        yield return Case("authorization-revocation-remains-active-while-paused", PausedRevocation);
        yield return Case("replacement-fences-old-token-rate-and-continuation", Replacement);
        yield return Case("continuation-preserves-controls-and-is-single-use", Continuation);
        yield return Case("handoff-aligns-latest-controls-before-publication", Handoff);
        yield return Case("retired-output-handoff-retains-logical-occurrence-and-controls", RetiredOutputHandoff);
        yield return Case("discard-and-revoked-preparation-preserve-donor", DiscardHandoff);
        yield return Case("stop-cancels-blocked-opening-and-releases", () => CloseOpening(dispose: false));
        yield return Case("dispose-cancels-blocked-opening-and-releases", () => CloseOpening(dispose: true));
        yield return Case("stop-awaits-accepted-opening-physical-release", StopDrainsOpening);
        yield return Case("queued-caller-cancellation-releases-without-dispatch", QueuedReadCancellation);
        yield return Case("queued-authorization-revocation-preserves-healthy-donor", QueuedReadRevocation);
        yield return Case("stop-awaits-queued-read-physical-release", () => CloseQueuedRead(dispose: false));
        yield return Case("dispose-awaits-queued-read-physical-release", () => CloseQueuedRead(dispose: true));
        yield return Case("stop-awaits-cancelled-queued-read-physical-release",
            () => CloseQueuedRead(dispose: false, cancelFirst: true));
        yield return Case("dispose-awaits-cancelled-queued-read-physical-release",
            () => CloseQueuedRead(dispose: true, cancelFirst: true));
        yield return Case("stop-reports-queued-read-release-failure", () => QueuedReadReleaseFailure(dispose: false));
        yield return Case("dispose-reports-queued-read-release-failure", () => QueuedReadReleaseFailure(dispose: true));
        foreach (bool dispose in new[] { false, true })
            foreach (bool preparedFailure in new[] { false, true })
                yield return Case((dispose ? "dispose" : "stop") + "-release-failure-drains-other-owned-inputs-prepared=" + preparedFailure,
                    () => CloseAfterReleaseFailure(dispose, preparedFailure));
        yield return Case("competing-owner-permit-cancels-opening-without-late-start", CompetingAdmission);
        yield return Case("newest-opening-retires-blocked-donor", ReplaceOpening);
        yield return Case("stop-retirement-cannot-clear-new-occurrence", StopRetirement);
        yield return Case("ended-retirement-cannot-notify-new-occurrence", () => FinishRetirement(revoke: false));
        yield return Case("failed-retirement-cannot-notify-new-occurrence", () => FinishRetirement(revoke: true));
        yield return Case("published-handoff-cancellation-cannot-start-after-donor-release", CommitRetirement);
        yield return Case("paused-reconfigure-and-resume-require-output-admission", ReconfigureAdmission);
        yield return Case("reconfigure-cancellation-joins-producer-without-revoking-input", ReconfigureCancellation);
        yield return Case("disposed-owner-rejects-and-releases-transferred-input", DisposedInput);
    }

    private static TestCase Case(string name, Func<Task> body) => new("audio.owner.dummy-" + name, () => Sta(body));
    private static Task Open(NativeAudioPlayback player, Lease lease, string identity = "fixture",
        CancellationToken token = default) => player.OpenReadAsync("music", identity, lease, "generated WAV", token);
    private static Task StartRead(NativeAudioPlayback player, Lease lease, string identity = "fixture",
        CancellationToken token = default) => Start(player, () => Open(player, lease, identity, token));

    private static async Task InitialState()
    {
        await using var player = Dummy();
        int events = 0;
        player.Started += (_, _) => events++;
        player.Ended += (_, _) => events++;
        player.Failed += (_, _) => events++;
        Check(!player.HasMedia && !player.IsPlaying && player.OwnerId is null && player.Identity is null &&
            player.PositionSeconds == 0 && player.DurationSeconds == 0 && player.LastErrorCode is null,
            "empty independent owner");
        Check(player.Volume == 100 && await player.ReadRateAsync() is null, "initial volume and absent occurrence rate");
        player.Volume = -1; Check(player.Volume == 0, "lower volume clamp");
        player.Volume = 101; Check(player.Volume == 100, "upper volume clamp");
        await player.PauseAsync(); await player.ResumeAsync(); await player.SeekAsync(0); await player.StopAsync();
        await player.DisposeAsync(); await player.DisposeAsync();
        await Error(() => player.StopAsync(), PlaybackError.Disposed);
        await Error(() => player.PauseAsync(), PlaybackError.Disposed);
        await Error(() => player.ResumeAsync(), PlaybackError.Disposed);
        await Error(() => player.SeekAsync(0), PlaybackError.Disposed);
        ErrorSync(() => player.Volume = 50, PlaybackError.Disposed);
        Check(events == 0, "no synthetic playback events for empty commands or disposal");
    }

    private static async Task Playback()
    {
        using var wave = new Wave(10);
        int acquired = 0, started = 0, ended = 0, failed = 0, progress = 0;
        var admissions = new List<Admission>();
        bool correctThread = true;
        int thread = Environment.CurrentManagedThreadId;
        await using var player = Dummy(_ =>
        {
            acquired++;
            var admission = new Admission();
            admissions.Add(admission);
            return Task.FromResult<IDisposable>(admission);
        });
        player.Started += (_, _) => { started++; correctThread &= thread == Environment.CurrentManagedThreadId; };
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        player.PropertyChanged += (_, args) =>
        {
            correctThread &= thread == Environment.CurrentManagedThreadId;
            if (args.PropertyName == nameof(player.PositionSeconds)) progress++;
        };
        await Start(player, () => player.OpenAsync("music", "fixture", wave.Uri, "generated WAV"));
        await Until(() => player.PositionSeconds > .1, "dummy consumed PCM progress");
        Check(acquired == 1 && admissions.Single().DisposeCalls == 1 && started == 1 && player.HasMedia && player.IsPlaying &&
            player.OwnerId == "music" && player.Identity == "fixture" && player.Title == "generated WAV",
            "single accepted occurrence and output acquisition");
        Near(player.DurationSeconds, 10, .025, "owner decoded duration");
        await player.PauseAsync();
        Check(player.PauseRequested && !player.IsPlaying && player.HasMedia, "pause retains media and intent");
        double paused = player.PositionSeconds;
        await Task.Delay(180);
        Near(player.PositionSeconds, paused, .04, "paused logical position stable");
        await player.SeekAsync(3.125);
        Near(player.PositionSeconds, 3.125, .025, "paused accepted seek");
        player.Volume = 23;
        var rate = await player.ReadRateAsync();
        Check(rate is not null && rate.Scope.OwnerId == "music" && rate.Scope.Identity == "fixture",
            "audio-specific rate has occurrence scope, no video tracks");
        var changed = await player.SetRateAsync(rate!.Scope, 1.6, default);
        Near(changed.RequestedRate, 1.6, .000001, "owner rate accepted");
        Near((await player.ReadRateAsync())!.RequestedRate, 1.6, .000001, "owner requested rate readback");
        Check(!player.IsPlaying && player.PauseRequested && player.Volume == 23, "rate change keeps paused controls");
        await Task.Run(() => player.ResumeAsync());
        await Until(() => player.IsPlaying && player.PositionSeconds > 3.2, "worker resume reaches PCM progress");
        Check(started == 1 && !player.PauseRequested && progress > 0 && correctThread,
            "resume is not another Started and events remain dispatcher-affine");
        await Task.Run(() => player.StopAsync());
        Check(!player.HasMedia && !player.IsPlaying && player.OwnerId is null && player.Identity is null &&
            player.PositionSeconds == 0 && player.DurationSeconds == 0 && ended == 0 && failed == 0,
            "stop retires without natural-end/failure events");
        wave.AssertReleased();
    }

    private static async Task NaturalEnd()
    {
        await using var player = Dummy();
        var lease = new Lease(WaveBytes(.7));
        int started = 0, ended = 0, failed = 0;
        bool retiredAtEvent = false, dispatcherEvent = false;
        var dispatcher = Dispatcher.CurrentDispatcher;
        player.Started += (_, _) => started++;
        player.Failed += (_, _) => failed++;
        player.Ended += (_, _) =>
        {
            ended++;
            retiredAtEvent = lease.DisposeCalls == 1 && lease.Released.Task.IsCompletedSuccessfully && !player.HasMedia;
            dispatcherEvent = dispatcher.CheckAccess();
        };
        await StartRead(player, lease);
        await Until(() => ended == 1, "dummy native EOF and owner natural end");
        Check(started == 1 && failed == 0 && ended == 1 && retiredAtEvent && dispatcherEvent &&
            !player.IsPlaying && !player.HasMedia, "one end after the real input retirement");
        lease.AssertReleased();
    }

    private static async Task RejectedInput()
    {
        await using var player = Dummy();
        var active = new Lease(WaveBytes(12));
        await StartRead(player, active);
        var scope = (await player.ReadRateAsync())!.Scope;
        var invalidIdentity = new Lease(WaveBytes(1));
        await Error(() => player.OpenReadAsync("", "rejected", invalidIdentity, ""), PlaybackError.InvalidRequest);
        invalidIdentity.AssertReleased();
        var invalidLease = new Lease(WaveBytes(1)); invalidLease.Stream.Writable = true;
        await Error(() => Open(player, invalidLease), PlaybackError.InvalidReadLease);
        invalidLease.AssertReleased();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = new Lease(WaveBytes(1));
        await Cancelled(() => Open(player, cancelled, token: cancel.Token)); cancelled.AssertReleased();
        Check(player.IsPlaying && player.HasMedia && active.DisposeCalls == 0 &&
            (await player.ReadRateAsync())!.Scope == scope, "rejection never retires accepted output or occurrence");
        await player.StopAsync();
        active.AssertReleased();
    }

    private static async Task CallerCancellation()
    {
        await using var player = Dummy();
        using var cancel = new CancellationTokenSource();
        var lease = new Lease(WaveBytes(12));
        int failed = 0, ended = 0;
        player.Failed += (_, _) => failed++;
        player.Ended += (_, _) => ended++;
        await StartRead(player, lease, token: cancel.Token);
        await player.PauseAsync();
        var point = player.CaptureContinuation();
        Check(point is not null, "accepted read input is continuable before cancellation");
        cancel.Cancel();
        await Until(() => !player.HasMedia && lease.Released.Task.IsCompletedSuccessfully, "paused caller cancellation");
        Check(!player.IsPlaying && point!.Revoked.IsCancellationRequested && failed == 0 && ended == 0,
            "caller cancellation is not EOF/content failure and revokes continuation authority");
        lease.AssertReleased();
    }

    private static async Task PausedRevocation()
    {
        await using var player = Dummy();
        var lease = new Lease(WaveBytes(12));
        int failed = 0, ended = 0;
        player.Failed += (_, _) => failed++;
        player.Ended += (_, _) => ended++;
        await StartRead(player, lease); await player.PauseAsync();
        var point = player.CaptureContinuation();
        lease.Revoke();
        await Until(() => !player.HasMedia && failed == 1 && lease.Released.Task.IsCompletedSuccessfully,
            "paused authorization loss retires without needing another read");
        Check(!player.IsPlaying && ended == 0 && player.LastErrorCode == PlaybackError.ReadRevoked &&
            player.CaptureContinuation() is null && point?.Revoked.IsCancellationRequested == true,
            "revocation is a fixed error, not a recoverable continuation or natural end");
        lease.AssertReleased();
    }

    private static async Task QueuedRevocationCancellation()
    {
        await using var player = Dummy();
        using var cancel = new CancellationTokenSource();
        var lease = new Lease(WaveBytes(12));
        int failed = 0, ended = 0;
        player.Failed += (_, _) => failed++;
        player.Ended += (_, _) => ended++;
        await StartRead(player, lease, token: cancel.Token);
        await player.PauseAsync();
        using (Dispatcher.CurrentDispatcher.DisableProcessing())
        {
            lease.Revoke();
            cancel.Cancel();
        }
        await Until(() => !player.HasMedia && lease.Released.Task.IsCompletedSuccessfully,
            "cancelled input drains the earlier queued authorization notification");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Check(!player.IsPlaying && player.OwnerId is null && player.Identity is null &&
            player.LastErrorCode is null && failed == 0 && ended == 0,
            "caller cancellation cannot publish an obsolete failure while the next queue entry is being selected");
        lease.AssertReleased();
    }

    private static async Task Replacement()
    {
        await using var player = Dummy();
        using var oldLifetime = new CancellationTokenSource();
        var old = new Lease(WaveBytes(12)); var next = new Lease(WaveBytes(12));
        int started = 0, ended = 0, failed = 0;
        player.Started += (_, _) => started++;
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        await StartRead(player, old, token: oldLifetime.Token);
        var stale = (await player.ReadRateAsync())!.Scope;
        await player.SetRateAsync(stale, 1.7, default);
        var point = player.CaptureContinuation();
        await StartRead(player, next); // Same business identity, different native generation.
        oldLifetime.Cancel();
        old.AssertReleased();
        var current = (await player.ReadRateAsync())!;
        Check(current.Scope.Generation != stale.Generation && current.Scope.Identity == stale.Identity &&
            point?.Revoked.IsCancellationRequested == true, "replacement fences the old native occurrence");
        await Error(() => player.SetRateAsync(stale, .5, default), PlaybackError.Superseded);
        Near((await player.ReadRateAsync())!.RequestedRate, 1, .000001, "new occurrence starts at 1x");
        Check(player.IsPlaying && next.DisposeCalls == 0 && started == 2 && ended == 0 && failed == 0,
            "old token and stale rate command cannot touch the replacement");
        await player.StopAsync();
        next.AssertReleased();
    }

    private static async Task Continuation()
    {
        await using var player = Dummy();
        var old = new Lease(WaveBytes(12)); var next = new Lease(WaveBytes(12));
        await StartRead(player, old);
        await player.PauseAsync(); await player.SeekAsync(4.125);
        var rate = (await player.ReadRateAsync())!;
        await player.SetRateAsync(rate.Scope, 1.6, default);
        var point = player.CaptureContinuation();
        Check(point is not null, "continuation captured from accepted music owner");
        Near(point!.PositionSeconds, 4.125, .025, "captured actual logical position");
        await player.ReopenReadAsync(point, next);
        old.AssertReleased();
        var reopened = (await player.ReadRateAsync())!;
        Check(player.HasMedia && !player.IsPlaying && player.PauseRequested &&
            player.OwnerId == "music" && player.Identity == "fixture" &&
            reopened.Scope.Generation != point.Scope.Generation,
            "continuation preserves occurrence and pause");
        Near(player.PositionSeconds, 4.125, .025, "continued position");
        Near(reopened.RequestedRate, 1.6, .000001, "continued tempo");
        var reused = new Lease(WaveBytes(12));
        await Error(() => player.ReopenReadAsync(point, reused), PlaybackError.Superseded);
        reused.AssertReleased();
        Check(next.DisposeCalls == 0 && player.HasMedia, "single-use rejection keeps the recovered input");
        await player.StopAsync();
        next.AssertReleased();
        Check(point.Revoked.IsCancellationRequested, "stop revokes continuation authority");
    }

    private static async Task Handoff()
    {
        await using var player = Dummy();
        var old = new Lease(WaveBytes(12)); var next = new Lease(WaveBytes(12));
        await StartRead(player, old);
        await player.PauseAsync(); await player.SeekAsync(3.125);
        player.Volume = 37;
        await player.SetRateAsync((await player.ReadRateAsync())!.Scope, 1.5, default);
        await player.PrepareReadHandoffAsync(next, default);
        Check(old.DisposeCalls == 0 && next.DisposeCalls == 0 && player.HasMedia &&
            !player.IsPlaying && player.Identity == "fixture", "preparation is silent and retains the donor");
        Near(player.PreparedHandoffDurationSeconds, 12, .025, "real prepared input duration");
        await player.SeekAsync(5.25);
        await player.SetRateAsync((await player.ReadRateAsync())!.Scope, 1.8, default);
        int committed = 0;
        bool donorHeldAtPublication = false;
        await player.CommitHandoffAsync(() =>
        {
            committed++;
            donorHeldAtPublication = old.DisposeCalls == 0 && next.DisposeCalls == 0;
        }, default);
        old.AssertReleased();
        Check(committed == 1 && donorHeldAtPublication && player.HasMedia && !player.IsPlaying &&
            player.PauseRequested && player.Volume == 37 && player.Identity == "fixture",
            "single atomic provenance publication precedes donor release and preserves current controls");
        Near(player.PositionSeconds, 5.25, .025, "commit follows latest seek, not the preparation position");
        Near((await player.ReadRateAsync())!.RequestedRate, 1.8, .000001, "commit follows latest tempo");
        await player.StopAsync(); next.AssertReleased();
    }

    private static async Task DiscardHandoff()
    {
        await using var player = Dummy();
        var old = new Lease(WaveBytes(12)); var discarded = new Lease(WaveBytes(12));
        await StartRead(player, old);
        var scope = (await player.ReadRateAsync())!.Scope;
        await player.PrepareReadHandoffAsync(discarded, default);
        await player.DiscardHandoffAsync(); await player.DiscardHandoffAsync();
        discarded.AssertReleased();
        Check(player.IsPlaying && old.DisposeCalls == 0 && (await player.ReadRateAsync())!.Scope == scope,
            "discard releases only preparation and does not replace occurrence");
        var revoked = new Lease(WaveBytes(12));
        await player.PrepareReadHandoffAsync(revoked, default);
        revoked.Revoke();
        int committed = 0;
        await Error(() => player.CommitHandoffAsync(() => committed++, default), PlaybackError.ReadRevoked);
        await player.DiscardHandoffAsync();
        revoked.AssertReleased();
        Check(committed == 0 && player.IsPlaying && old.DisposeCalls == 0,
            "authorization lost after readiness cannot replace a healthy donor");
        await player.StopAsync(); old.AssertReleased();
    }

    private static async Task RetiredOutputHandoff()
    {
        await using var player = Dummy();
        var old = new Lease(WaveBytes(12)); var next = new Lease(WaveBytes(12));
        await StartRead(player, old);
        await player.PauseAsync(); await player.SeekAsync(3.125);
        await player.SetRateAsync((await player.ReadRateAsync())!.Scope, 1.7, default);
        player.Volume = 37;
        await player.StopOutputForHandoffAsync();
        old.AssertReleased();
        Check(!player.HasMedia && !player.IsPlaying && player.OwnerId == "music" && player.Identity == "fixture" &&
            player.PauseRequested && player.Volume == 37, "retiring rejected output does not stop the logical occurrence");
        Near(player.PositionSeconds, 3.125, .025, "retired output retains position");
        await player.PrepareReadHandoffAsync(next, default);
        int committed = 0;
        await player.CommitHandoffAsync(() => committed++, default);
        Check(committed == 1 && player.HasMedia && !player.IsPlaying && player.PauseRequested && player.Volume == 37,
            "silent replacement inherits the retained latest controls");
        Near(player.PositionSeconds, 3.125, .025, "replacement after retired output retains position");
        Near((await player.ReadRateAsync())!.RequestedRate, 1.7, .000001, "replacement after retired output retains tempo");
        await player.StopAsync(); next.AssertReleased();
    }

    private static async Task CloseOpening(bool dispose)
    {
        await using var player = Dummy();
        var lease = new Lease(WaveBytes(12)); lease.Stream.BlockReads();
        int started = 0, ended = 0, failed = 0;
        player.Started += (_, _) => started++;
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        Task opening = Open(player, lease);
        try
        {
            await UntilBlocked(lease.Stream.Blocked.Task, opening);
            await (dispose ? player.DisposeAsync().AsTask() : player.StopAsync()).WaitAsync(TimeSpan.FromSeconds(3));
            await Superseded(opening);
            lease.AssertReleased();
            Check(!player.HasMedia && !player.IsPlaying && started == 0 && ended == 0 && failed == 0,
                "stop drains token-ignoring opening without late events");
        }
        finally
        {
            lease.Stream.UnblockReads();
            try { await opening.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task ReplaceOpening()
    {
        await using var player = Dummy();
        var old = new Lease(WaveBytes(12)); old.Stream.BlockReads();
        var next = new Lease(WaveBytes(12));
        int started = 0, failed = 0;
        player.Started += (_, _) => started++;
        player.Failed += (_, _) => failed++;
        Task opening = Open(player, old, "old");
        try
        {
            await UntilBlocked(old.Stream.Blocked.Task, opening);
            await StartRead(player, next, "latest");
            await Superseded(opening);
            old.AssertReleased();
            Check(player.IsPlaying && player.Identity == "latest" && next.DisposeCalls == 0 &&
                started == 1 && failed == 0, "latest queued opening alone may publish state and events");
            await player.StopAsync(); next.AssertReleased();
        }
        finally
        {
            old.Stream.UnblockReads();
            try { await opening.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task StopDrainsOpening()
    {
        await using var player = Dummy();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease(WaveBytes(12)) { DisposeGate = release.Task };
        lease.Stream.BlockReads();
        int events = 0;
        player.Started += (_, _) => events++;
        player.Ended += (_, _) => events++;
        player.Failed += (_, _) => events++;
        Task opening = Open(player, lease), stopping = Task.CompletedTask;
        try
        {
            await UntilBlocked(lease.Stream.Blocked.Task, opening);
            stopping = player.StopAsync();
            await lease.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!stopping.IsCompleted && !opening.IsCompleted && !lease.Released.Task.IsCompleted &&
                lease.Stream.CloseCalls == 0, "Stop cannot finish while the accepted input's physical release is pending");
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(3));
            lease.AssertReleased(); // Check at Stop's return, before separately awaiting the rejected Open.
            await Superseded(opening);
            Check(!player.HasMedia && !player.IsPlaying && player.OwnerId is null && player.Identity is null && events == 0,
                "Stop drains the accepted opening without late state or playback events");
        }
        finally
        {
            release.TrySetResult();
            lease.Stream.UnblockReads();
            try { await Task.WhenAll(opening, stopping).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task CompetingAdmission()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopOther = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderLease = new Lease(WaveBytes(12)); var waitingLease = new Lease(WaveBytes(12));
        var holderPermit = new GatePermit(gate);
        NativeAudioPlayback? waitingOwner = null;
        bool admissionCancelled = false, drainedBeforeAdmission = false;
        // Exercise the owners' admission port, not a copied App coordinator implementation.
        await using var holder = Dummy(async token =>
        {
            await gate.WaitAsync(token);
            try
            {
                held.TrySetResult();
                await stopOther.Task.WaitAsync(token);
                var other = waitingOwner!;
                await other.StopAsync();
                drainedBeforeAdmission = waitingLease.Released.Task.IsCompletedSuccessfully &&
                    !other.HasMedia && !other.IsPlaying;
                return holderPermit;
            }
            catch { holderPermit.Dispose(); throw; }
        });
        await using var waiting = Dummy(async token =>
        {
            Check(token.CanBeCanceled, "admission receives the cancellable accepted-input lifetime");
            queued.TrySetResult();
            try { await gate.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { admissionCancelled = true; throw; }
            var permit = new GatePermit(gate);
            try { await holder.StopAsync(); return permit; }
            catch { permit.Dispose(); throw; }
        });
        waitingOwner = waiting;
        int holderStarts = 0, waitingStarts = 0, terminalEvents = 0;
        holder.Started += (_, _) => holderStarts++;
        waiting.Started += (_, _) => waitingStarts++;
        holder.Failed += (_, _) => terminalEvents++;
        waiting.Failed += (_, _) => terminalEvents++;
        holder.Ended += (_, _) => terminalEvents++;
        waiting.Ended += (_, _) => terminalEvents++;
        Task holding = Open(holder, holderLease, "holder", lifetime.Token), pending = Task.CompletedTask;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(3));
            pending = Open(waiting, waitingLease, "waiting", lifetime.Token);
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(gate.CurrentCount == 0 && !holding.IsCompleted && !pending.IsCompleted &&
                !holder.IsPlaying && !waiting.IsPlaying, "both openings remain pending behind the held admission permit");
            stopOther.TrySetResult();
            await holding.WaitAsync(TimeSpan.FromSeconds(3));
            await Superseded(pending);
            waitingLease.AssertReleased();
            Check(admissionCancelled && drainedBeforeAdmission && holder.IsPlaying && !waiting.HasMedia &&
                holderStarts == 1 && waitingStarts == 0 && terminalEvents == 0 &&
                holderPermit.DisposeCalls == 1 && gate.CurrentCount == 1,
                "held-permit Stop cancels the other opening's permit wait, drains it, and prevents a late start");
            await holder.StopAsync();
            holderLease.AssertReleased();
        }
        finally
        {
            stopOther.TrySetResult();
            lifetime.Cancel();
            try { await Task.WhenAll(holding, pending).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private sealed class GatePermit(SemaphoreSlim gate) : IDisposable
    {
        internal int DisposeCalls;
        public void Dispose()
        {
            if (Interlocked.Increment(ref DisposeCalls) == 1) gate.Release();
        }
    }

    private static async Task DisposedInput()
    {
        var player = Dummy();
        await player.DisposeAsync();
        var lease = new Lease(WaveBytes(1));
        await Error(() => Open(player, lease), PlaybackError.Disposed);
        lease.AssertReleased();
        Check(lease.Stream.ReadCalls == 0, "disposed owner rejects before reading transferred data");
    }

    private static async Task StopRetirement()
    {
        await using var player = Dummy();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new Lease(WaveBytes(12));
        var next = new Lease(WaveBytes(12));
        await StartRead(player, old, "old");
        old.DisposeGate = released.Task;
        var stop = player.StopAsync();
        Task opening = Task.CompletedTask;
        try
        {
            await old.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            opening = StartRead(player, next, "latest");
            Check(player.Identity == "latest" && !stop.IsCompleted, "new identity publishes while old retirement is pending");
            released.TrySetResult();
            await Task.WhenAll(stop, opening).WaitAsync(TimeSpan.FromSeconds(3));
            Check(player.OwnerId == "music" && player.Identity == "latest" && player.HasMedia && player.IsPlaying,
                "old Stop cannot clear the newer accepted occurrence");
            old.AssertReleased();
            await player.StopAsync(); next.AssertReleased();
        }
        finally
        {
            released.TrySetResult();
            await Task.WhenAll(stop, opening).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task FinishRetirement(bool revoke)
    {
        await using var player = Dummy();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new Lease(WaveBytes(revoke ? 12 : .2));
        var next = new Lease(WaveBytes(12));
        int ended = 0, failed = 0;
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        await StartRead(player, old, "old");
        old.DisposeGate = released.Task;
        if (revoke) old.Revoke();
        Task opening = Task.CompletedTask;
        try
        {
            await old.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            opening = StartRead(player, next, "latest");
            released.TrySetResult();
            await opening.WaitAsync(TimeSpan.FromSeconds(3));
            Check(player.Identity == "latest" && player.IsPlaying && player.LastErrorCode is null &&
                ended == 0 && failed == 0, "retired completion cannot send an old terminal event to a newer scope");
            old.AssertReleased();
            await player.StopAsync(); next.AssertReleased();
        }
        finally
        {
            released.TrySetResult();
            await opening.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task CancelFinishRetirement(bool revoke)
    {
        await using var player = Dummy();
        using var cancel = new CancellationTokenSource();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease(WaveBytes(revoke ? 12 : .2)) { DisposeGate = released.Task };
        int ended = 0, failed = 0;
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        await StartRead(player, lease, token: cancel.Token);
        if (revoke) lease.Revoke();
        try
        {
            await lease.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            Check(!lease.Released.Task.IsCompleted, "caller cancellation arrives during actual retirement");
            released.TrySetResult();
            await Until(() => lease.Released.Task.IsCompletedSuccessfully && player.OwnerId is null,
                "cancelled retirement drains and clears the occurrence");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            lease.AssertReleased();
            Check(!player.HasMedia && !player.IsPlaying && player.Identity is null &&
                player.LastErrorCode is null && ended == 0 && failed == 0 &&
                player.CaptureContinuation() is null, "retirement cannot turn caller cancellation into EOF or failure");
        }
        finally { released.TrySetResult(); cancel.Cancel(); }
    }

    private static async Task CommitRetirement()
    {
        await using var player = Dummy();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new Lease(WaveBytes(12));
        var next = new Lease(WaveBytes(12));
        int started = 0, terminal = 0, publications = 0;
        player.Started += (_, _) => started++;
        player.Ended += (_, _) => terminal++;
        player.Failed += (_, _) => terminal++;
        await StartRead(player, old);
        old.DisposeGate = released.Task;
        await player.PrepareReadHandoffAsync(next, default);
        var commit = player.CommitHandoffAsync(() => publications++, default);
        Task stop = Task.CompletedTask;
        try
        {
            await old.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(publications == 1 && !commit.IsCompleted, "publication precedes donor input release");
            stop = player.StopAsync();
            released.TrySetResult();
            await Superseded(commit);
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            old.AssertReleased(); next.AssertReleased();
            Check(!player.HasMedia && !player.IsPlaying && player.Identity is null && started == 1 && terminal == 0,
                "cancelled published candidate cannot start after donor retirement");
        }
        finally
        {
            released.TrySetResult();
            try { await Task.WhenAll(commit, stop).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task ReconfigureAdmission()
    {
        var grant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        int admissions = 0;
        await using var player = Dummy(async token =>
        {
            admissions++;
            if (hold) { entered.TrySetResult(); await grant.Task.WaitAsync(token); }
            return new Admission();
        });
        var lease = new Lease(WaveBytes(12));
        lease.Stream.Chunk = 512;
        lease.Stream.HonorReadCancellation = true;
        await StartRead(player, lease);
        await player.PauseAsync();
        hold = true;
        lease.Stream.BlockReads();
        var seek = player.SeekAsync(8);
        Task resume = Task.CompletedTask;
        try
        {
            await UntilBlocked(lease.Stream.Blocked.Task, seek);
            resume = player.ResumeAsync();
            lease.Stream.UnblockReads();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!player.IsPlaying && !seek.IsCompleted && !grant.Task.IsCompleted,
                "changed resume intent cannot bypass admission after paused PCM preparation");
            grant.TrySetResult();
            await Task.WhenAll(seek, resume).WaitAsync(TimeSpan.FromSeconds(3));
            Check(player.IsPlaying && admissions >= 2 && player.PositionSeconds >= 8,
                "output starts only after the newly required permit is granted");
            await player.StopAsync(); lease.AssertReleased();
        }
        finally
        {
            grant.TrySetResult();
            lease.Stream.UnblockReads();
            try { await Task.WhenAll(seek, resume).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task ReconfigureCancellation()
    {
        await using var player = Dummy();
        var lease = new Lease(WaveBytes(12));
        lease.Stream.Chunk = 512;
        lease.Stream.HonorReadCancellation = true;
        await StartRead(player, lease);
        await player.PauseAsync();
        using var cancel = new CancellationTokenSource();
        var scope = (await player.ReadRateAsync())!.Scope;
        lease.Stream.BlockReads();
        var changing = player.SetRateAsync(scope, 1.5, cancel.Token);
        try
        {
            await UntilBlocked(lease.Stream.Blocked.Task, changing);
            cancel.Cancel();
            await Cancelled(() => changing.WaitAsync(TimeSpan.FromSeconds(3)));
            Check(player.Identity == "fixture" && player.PauseRequested && !player.IsPlaying &&
                player.LastErrorCode is null && lease.DisposeCalls == 0,
                "cancelled configuration joins its blocked producer without retiring the accepted input");
            lease.Stream.UnblockReads();
            await player.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check(player.IsPlaying && (await player.ReadRateAsync())!.RequestedRate == 1,
                "explicit resume rebuilds the cancelled output at the accepted rate");
            await player.StopAsync(); lease.AssertReleased();
        }
        finally
        {
            cancel.Cancel();
            lease.Stream.UnblockReads();
            try { await changing.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task Superseded(Task request)
    {
        try { await request.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { return; }
        catch (PlaybackException error) when (error.Code == PlaybackError.Superseded) { return; }
        throw new InvalidOperationException("Obsolete audio opening completed successfully");
    }
}

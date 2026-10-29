using System.Windows.Threading;
using System.ComponentModel;
using LibVLCSharp.Shared;

namespace MeowFilm.Media;

public sealed partial class NativePlayback
{
    // At most one coordinator state, never a second player/engine or an idle refresh timer.
    private LiveRecoveryState? _liveRecovery;
    private sealed class LiveRecoveryState(Session session)
    {
        internal readonly Session Session = session;
        internal int Paused, Consecutive;
        internal long Tried, CommandSequence;
        internal bool Running, PlayIssued, PauseInterrupted;
        internal long PayloadStart;
        internal TaskCompletionSource<bool>? Frame;
        internal CancellationTokenSource? Attempt;
        internal Task? Work;
    }
    private LiveRecoveryState LiveState(Session session)
    {
        if (_liveRecovery?.Session != session) _liveRecovery = new(session);
        return _liveRecovery;
    }

    private async Task<bool> TryRecoverLiveAsync(Session session)
    {
        if (session.Adaptive?.Live is not { } live) return false; // VOD/offline untouched.
        var state = LiveState(session);
        if (state.Running)
        {
            if (state.PlayIssued && session.Player.State == VLCState.Ended) state.Frame?.TrySetResult(false);
            return true;
        }
        if (Volatile.Read(ref state.Paused) != 0) return true;
        // Stop joins the old input; a dispatcher fence drains its already-posted callbacks.
        // A delayed old EndReached must not stop a newly playing/paused input in this session.
        if (session.Player.State is not (VLCState.Ended or VLCState.Stopped)) return true;
        var ticket = live.PendingRecovery;
        if (ticket is null)
        {
            if (live.HasOpenWindow) session.Adaptive.LiveRecoveryFailed();
            return false; // Only an actual ENDLIST may be reported as natural completion.
        }
        if (!session.HasStarted || ticket.Version <= state.Tried && !state.PauseInterrupted || state.Consecutive >= 2)
        {
            session.Adaptive.LiveRecoveryFailed(); return false;
        }
        state.Running = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Work = completion.Task;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(session.Adaptive.Lifetime);
        state.Attempt = attempt;
        PropertyChangedEventHandler? progress = null;
        bool accepted = false;
        var frame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Frame = frame; state.PlayIssued = false;
        try
        {
            await _commands.WaitAsync(attempt.Token);
            try
            {
                if (!IsCurrent(session) || Volatile.Read(ref state.Paused) != 0) return true;
                await Task.Run(session.Player.Stop);
                await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (!IsCurrent(session) || Volatile.Read(ref state.Paused) != 0) return true;
                attempt.Token.ThrowIfCancellationRequested();
                session.ProgressTimer?.Stop();
                lock (session.ProgressLock) session.ProgressPending = false;
                state.PayloadStart = live.CompletedPayloads;
                // Observe existing coalesced current-input progress after the native/dispatcher
                // fence. No new native callback registration can race native disposal.
                progress = (_, args) =>
                {
                    if (args.PropertyName != nameof(PositionSeconds)) return;
                    if (!IsCurrent(session) || !state.Running || attempt.IsCancellationRequested ||
                        Volatile.Read(ref state.Paused) != 0) return;
                    if (live.CompletedPayloads > state.PayloadStart &&
                        session.Media.Statistics is { } stats &&
                        (stats.DecodedAudio > 0 || stats.DisplayedPictures > 0))
                        frame.TrySetResult(true);
                };
                PropertyChanged += progress;
                accepted = await Task.Run(() =>
                {
                    if (!IsCurrent(session) || Volatile.Read(ref state.Paused) != 0 || attempt.IsCancellationRequested) return false;
                    return session.Player.Play();
                });
                if (!IsCurrent(session)) return true;
                // Count an accepted Play even if Pause arrived while that native call was
                // already executing. Resume must observe that input, not grant a free retry.
                if (accepted)
                {
                    state.Tried = ticket.Version; state.Consecutive++; state.PlayIssued = true;
                }
                if (Volatile.Read(ref state.Paused) != 0 || attempt.IsCancellationRequested) return true;
                if (!accepted) { session.Adaptive.LiveRecoveryFailed(); return false; }
                state.PauseInterrupted = false;
            }
            finally { _commands.Release(); }

            // Bounded active recovery observation, not an always-running polling loop.
            bool decoded = await frame.Task.WaitAsync(TimeSpan.FromSeconds(12), attempt.Token);
            if (!IsCurrent(session) || Volatile.Read(ref state.Paused) != 0) return true;
            if (!decoded) { session.Adaptive.LiveRecoveryFailed(); return false; }
            state.Consecutive = 0; live.Recovered(ticket);
            return true;
        }
        catch (OperationCanceledException) when (!IsCurrent(session) || Volatile.Read(ref state.Paused) != 0)
        { return true; }
        catch
        {
            if (IsCurrent(session)) session.Adaptive.LiveRecoveryFailed();
            return false;
        }
        finally
        {
            if (progress is not null) PropertyChanged -= progress;
            state.Attempt = null; state.Running = false;
            state.Frame = null; state.PlayIssued = false;
            completion.TrySetResult();
        }
    }

    // These two private command adapters latch pause intent BEFORE entering the shared gate.
    // The public delegates are the only required command-entry changes.
    private async Task PauseLiveAwareAsync()
    {
        ThrowIfDisposed();
        var session = _session;
        if (session?.Adaptive?.Live is null) { await CommandAsync(Command.Pause); return; }
        var state = LiveState(session);
        long command = Interlocked.Increment(ref state.CommandSequence);
        state.PauseInterrupted |= state.Running;
        Volatile.Write(ref state.Paused, 1); state.Attempt?.Cancel();
        await SetLivePauseAsync(session, state, command, true);
    }
    private async Task ResumeLiveAwareAsync()
    {
        ThrowIfDisposed();
        var session = _session;
        if (session?.Adaptive?.Live is null) { await CommandAsync(Command.Resume); return; }
        var state = LiveState(session);
        long command = Interlocked.Increment(ref state.CommandSequence);
        if (state.Work is { } work) await work;
        if (!IsLiveCommandCurrent(session, state, command)) return;
        Volatile.Write(ref state.Paused, 0);
        if (session.Player.State is VLCState.Ended or VLCState.Stopped)
        {
            if (!await TryRecoverLiveAsync(session)) await FinishSessionAsync(session, session.LastInputError);
        }
        else
        {
            if (!await SetLivePauseAsync(session, state, command, false)) return;
            // If pause interrupted first-frame observation after Play, explicit resume retains
            // that input. Only a new native progress/decoded observation may reset its budget.
            if (!state.Running && state.Consecutive != 0 && session.Adaptive.Live.PendingRecovery is { } ticket)
                await ObserveResumedRecoveryAsync(session, state, ticket);
        }
    }
    private bool IsLiveCommandCurrent(Session session, LiveRecoveryState state, long command) =>
        IsCurrent(session) && Volatile.Read(ref state.CommandSequence) == command;

    private async Task<bool> SetLivePauseAsync(Session session, LiveRecoveryState state, long command, bool pause)
    {
        string? failure = null;
        bool applied = false;
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if (!IsLiveCommandCurrent(session, state, command)) return false;
            applied = await Task.Run(() =>
            {
                if (!IsLiveCommandCurrent(session, state, command)) return false;
                if (pause && session.Player.State is VLCState.Ended or VLCState.Stopped) return true;
                if (pause && !session.Player.CanPause) throw Error(PlaybackError.NotPausable);
                session.Player.SetPause(pause);
                return true;
            });
            if (pause && applied && IsLiveCommandCurrent(session, state, command))
                Set(ref _isPlaying, false, nameof(IsPlaying));
        }
        catch (PlaybackException) { throw; }
        catch { failure = PlaybackError.CommandFailed; }
        finally { _commands.Release(); }
        // Preserve the existing CommandAsync exception/retirement contract without
        // letting an older queued Resume bypass a newer synchronous Pause intent.
        if (failure is not null)
        {
            await FinishSessionAsync(session, failure);
            throw Error(failure);
        }
        return applied && IsLiveCommandCurrent(session, state, command);
    }
    private async Task ObserveResumedRecoveryAsync(Session session, LiveRecoveryState state, AdaptiveLiveSession.Recovery ticket)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(session.Adaptive!.Lifetime);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Running = state.PlayIssued = true; state.Frame = frame; state.Attempt = attempt; state.Work = completion.Task;
        PropertyChangedEventHandler progress = (_, args) =>
        {
            if (args.PropertyName == nameof(PositionSeconds) && IsCurrent(session) && !attempt.IsCancellationRequested &&
                Volatile.Read(ref state.Paused) == 0 && session.Adaptive.Live!.CompletedPayloads > state.PayloadStart &&
                session.Media.Statistics is { } stats && (stats.DecodedAudio > 0 || stats.DisplayedPictures > 0))
                frame.TrySetResult(true);
        };
        PropertyChanged += progress;
        try
        {
            bool decoded = await frame.Task.WaitAsync(TimeSpan.FromSeconds(12), attempt.Token);
            if (!IsCurrent(session) || Volatile.Read(ref state.Paused) != 0 || attempt.IsCancellationRequested) return;
            if (decoded)
            {
                state.Consecutive = 0; state.PauseInterrupted = false; session.Adaptive.Live!.Recovered(ticket);
            }
            else if (IsCurrent(session)) session.Adaptive.LiveRecoveryFailed();
        }
        catch (OperationCanceledException) when (!IsCurrent(session) || Volatile.Read(ref state.Paused) != 0) { }
        catch { if (IsCurrent(session)) session.Adaptive.LiveRecoveryFailed(); }
        finally
        {
            PropertyChanged -= progress; state.Running = state.PlayIssued = false; state.Attempt = null; state.Frame = null;
            completion.TrySetResult();
        }
    }
}

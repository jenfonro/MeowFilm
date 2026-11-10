using System.Diagnostics;
using LibVLCSharp.Shared;

namespace MeowFilm.Media;

/// <summary>
/// Seamless source replacement: a new network input is opened paused and muted beside the
/// playing one and positioned at the live time. Commit swaps them in one step; the old
/// input keeps playing until then, and a failed or abandoned preparation never touches it.
/// </summary>
public interface INativePlaybackHandoff
{
    // Retire rejected output without replacing the logical occurrence or its controls.
    Task StopOutputForHandoffAsync();
    Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token);
    Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token);
    double PreparedHandoffDurationSeconds { get; }
    // Invokes the non-throwing provenance callback synchronously at publication, before events/release.
    Task CommitHandoffAsync(Action committed, CancellationToken token);
    Task DiscardHandoffAsync();
}

public sealed partial class NativePlayback : INativePlaybackHandoff
{
    private Session? _handoffSession;
    private CancellationTokenSource? _handoffOpening;
    private (string Owner, string Identity, long Generation)? _handoffTarget;
    private TaskCompletionSource? _handoffReady;
    private TaskCompletionSource<string>? _handoffFailure;
    private Action? _handoffDetach;
    private CancellationToken _handoffLifetime;
    private float _retiredHandoffRate = 1;
    private bool _handoffDonorRetired;
    private long _handoffSequence;
    private Task? _handoffRetirement;
    private Task<bool>? _handoffPreparation;
    // Per-instance native-worker observation used by deterministic lifecycle tests.
    private Action<MediaPlayer?>? _handoffWorkerCompleted = null;

    public Task StopOutputForHandoffAsync() => DispatchAsync(StopOutputForHandoffCoreAsync);
    public Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
        DispatchAsync(() => PrepareHandoffCoreAsync(uri, headers, null, token));
    public double PreparedHandoffDurationSeconds => Math.Max(0, _handoffSession?.Player.Length ?? 0) / 1000d;
    public Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token) =>
        TrackOwnedMediaRequestAsync(() => PrepareReadHandoffOwnedAsync(lease, token));

    private async Task PrepareReadHandoffOwnedAsync(INativeMediaReadLease lease, CancellationToken token)
    {
        if (lease is null) throw Error(PlaybackError.InvalidReadLease);
        var input = new ReadLeaseMediaInput(lease);
        try { await DispatchAsync(() => PrepareHandoffCoreAsync(null, null, input, token)); }
        catch { await input.DisposeAsync(); throw; }
    }
    public Task CommitHandoffAsync(Action committed, CancellationToken token) => DispatchAsync(() =>
    {
        if (_runtimeHandoffIntent is not null) throw Error(PlaybackError.Superseded);
        return CommitHandoffCoreAsync(static () => true, committed, token);
    });
    public Task DiscardHandoffAsync() => DispatchAsync(() =>
        _runtimeHandoffIntent is null ? DiscardHandoffCoreAsync() : Task.CompletedTask);

    private async Task<Session> PrepareHandoffCoreAsync(Uri? uri, IReadOnlyDictionary<string, string>? headers,
        ReadLeaseMediaInput? readInput, CancellationToken token, MediaResourceSession? resource = null,
        RuntimeMediaPlaybackIntent? runtimeIntent = null, RuntimeVideoMaterial? video = null)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        if (_releaseFaulted) throw Error(PlaybackError.ReleaseFailed);
        long sequence = ++_handoffSequence, generation = _generation;
        var preceding = DiscardHandoffCoreAsync(invalidatePreparation: false, nextRuntimeIntent: runtimeIntent);
        var preparation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handoffPreparation = preparation.Task;
        CancellationTokenSource? opening = null;
        HttpMediaInput? input = null;
        AdaptiveMediaSession? adaptive = null;
        FFmpegMediaInput? ffmpeg = null;
        Session? prepared = null;
        Action? detach = null;
        try
        {
            await preceding;
            if (_handoffSequence != sequence || _generation != generation)
                throw Error(PlaybackError.Superseded);
            if (runtimeIntent is null && (_ownerId is null || _identity is null)) throw Error(PlaybackError.Superseded);
            var target = (_ownerId ?? "", _identity ?? "", _generation);
            if (!HandoffScopeCurrent(target)) throw Error(PlaybackError.Superseded);
            var requestOptions = _httpOptions;
            Dictionary<string, string>? requestHeaders = null;
            if (readInput is null)
            {
                if (uri?.Scheme is not ("http" or "https") || headers is null) throw Error(PlaybackError.InvalidRequest);
                Validate(runtimeIntent?.OwnerId ?? _ownerId!, runtimeIntent?.Identity ?? _identity!, uri,
                    runtimeIntent?.Title ?? _title, headers);
                requestHeaders = HttpMediaInput.ValidateRequest(uri, headers, true);
            }
            else readInput.Validate();
            token.ThrowIfCancellationRequested();
            opening = CancellationTokenSource.CreateLinkedTokenSource(token);
            _handoffOpening = opening;
            _handoffTarget = target;
            _handoffLifetime = token;
            void Check()
            {
                resource?.CheckCurrent();
                opening.Token.ThrowIfCancellationRequested();
                if (!HandoffScopeCurrent(target) ||
                    !ReferenceEquals(_handoffOpening, opening))
                    throw new OperationCanceledException(PlaybackError.Superseded, new CancellationToken(true));
            }
            void EnsureHandoffEngine()
            {
                EnsureEngine();
                _handoffWorkerCompleted?.Invoke(null);
            }
            if (readInput is not null)
            {
                readInput.AttachOpening(opening.Token);
                await Task.Run(readInput.PrepareAsync);
                Check();
            }
            else if (video is not null)
            {
                await Task.Run(EnsureHandoffEngine);
                Check();
                ffmpeg = new FFmpegMediaInput(uri!, requestHeaders!, requestOptions, video.Format,
                    video.Pairs, video.DefaultKey, opening.Token);
            }
            else input = await HttpMediaInput.OpenPlaybackAsync(uri!, requestHeaders!, requestOptions, opening.Token);
            Check();
            if (input?.DemuxName == "adaptive")
            {
                byte[] manifest;
                try { manifest = await input.ReadManifestAsync(opening.Token); }
                catch (Exception error)
                {
                    if (opening.IsCancellationRequested) throw new OperationCanceledException(opening.Token);
                    throw Error(HttpMediaInput.Classify(error));
                }
                var resolved = input.EffectiveUri;
                input.Dispose(); input = null;
                adaptive = await AdaptiveMediaSession.OpenAsync(manifest, resolved, requestHeaders!, requestOptions, opening.Token);
                Check();
            }
            var media = input; var stream = adaptive;
            if (runtimeIntent is not null) await Task.Run(EnsureHandoffEngine);
            Check();
            try
            {
                prepared = await Task.Run(() =>
                {
                    var created = CreateSession(target.Item3 + 1, uri, media, readInput,
                        adaptive: stream ?? readInput?.Adaptive, resource: resource, ffmpeg: ffmpeg);
                    prepared = created;
                    input = null; adaptive = null; ffmpeg = null; // The session now owns its inputs, even before registration.
                    _handoffWorkerCompleted?.Invoke(created.Player);
                    return created;
                });
            }
            catch { throw Error(PlaybackError.OpenFailed); }
            if (ReferenceEquals(_handoffOpening, opening)) _handoffSession = prepared;
            Check();
            prepared.Media.AddOption(":start-paused");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _handoffReady = ready;
            _handoffFailure = failure;
            var player = prepared.Player;
            if (runtimeIntent is not null) AttachRuntimeHandoffOutput(player);
            EventHandler<EventArgs> paused = (_, _) => ready.TrySetResult();
            void Fail(string code)
            {
                failure.TrySetResult(code);
                ready.TrySetException(Error(code));
            }
            EventHandler<EventArgs> failed = (_, _) => Fail(prepared.LastInputError ?? PlaybackError.OpenFailed);
            Action<string> inputFailed = Fail;
            player.Paused += paused; player.EncounteredError += failed; player.EndReached += failed;
            if (prepared.ReadInput is not null) prepared.ReadInput.Failed += inputFailed;
            if (prepared.Input is not null) prepared.Input.Failed += inputFailed;
            if (prepared.Adaptive is not null) prepared.Adaptive.Failed += inputFailed;
            if (prepared.FFmpeg is not null) prepared.FFmpeg.Failed += inputFailed;
            bool detached = false;
            _handoffDetach = detach = () =>
            {
                if (detached) return;
                detached = true;
                player.Paused -= paused; player.EncounteredError -= failed; player.EndReached -= failed;
                if (prepared.ReadInput is not null) prepared.ReadInput.Failed -= inputFailed;
                if (prepared.Input is not null) prepared.Input.Failed -= inputFailed;
                if (prepared.Adaptive is not null) prepared.Adaptive.Failed -= inputFailed;
                if (prepared.FFmpeg is not null) prepared.FFmpeg.Failed -= inputFailed;
            };
            await Task.Run(() => { player.Mute = true; player.Volume = 0; });
            Check();
            if (!await Task.Run(player.Play)) throw Error(prepared.LastInputError ?? PlaybackError.OpenFailed);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(12), opening.Token);
            Check();
            CheckHandoffInput(prepared, failure);
            // Position the paused input at the live time so the commit continues without a gap.
            double position = runtimeIntent is { PreserveCurrent: false } ? runtimeIntent.Position : _positionSeconds;
            if (position > 0)
            {
                if (!player.IsSeekable) throw Error(PlaybackError.NotSeekable);
                await Task.Run(() => player.Time = (long)(position * 1000));
            }
            Check();
            CheckHandoffInput(prepared, failure);
            if (runtimeIntent is not null)
                await WaitForRuntimeHandoffOutputAsync(prepared, opening.Token);
            return prepared;
        }
        catch (Exception error)
        {
            bool failed = false;
            try { opening?.Cancel(); }
            catch { failed = true; }
            try { detach?.Invoke(); }
            catch { failed = true; }
            if (ReferenceEquals(_handoffPreparation, preparation.Task))
                _handoffDetach = null;
            try { input?.Dispose(); }
            catch { failed = true; }
            try { if (adaptive is not null) await adaptive.DisposeAsync(); }
            catch { failed = true; }
            try { if (ffmpeg is not null) await ffmpeg.DisposeAsync(); }
            catch { failed = true; }
            try { if (prepared is not null) failed |= await ReleaseNativeAsync(prepared, detachView: false); }
            catch { failed = true; }
            try { if (readInput is not null) await readInput.DisposeAsync(); }
            catch { failed = true; }
            // Include the outer API's exact receipt in the registered preparation drain.
            try { if (resource is not null) await resource.DisposeAsync(); }
            catch { failed = true; }
            video?.Dispose(); video = null;
            if (failed) _releaseFaulted = true;
            // A preparation must finish its own cleanup before joining discard. Otherwise
            // discard/close would wait for this catch while it waited for itself.
            preparation.TrySetResult(failed);
            if (ReferenceEquals(_handoffPreparation, preparation.Task)) await DiscardHandoffCoreAsync();
            if (failed) throw Error(PlaybackError.ReleaseFailed);
            if (error is OperationCanceledException or PlaybackException) throw;
            throw Error(PlaybackError.OpenFailed);
        }
        finally
        {
            video?.Dispose();
            preparation.TrySetResult(false);
        }
    }

    private async Task<bool> CommitHandoffCoreAsync(Func<bool> canCommit, Action committed, CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        var prepared = _handoffSession;
        if (prepared?.Resource is { } resource) resource.CheckCurrent();
        if (prepared is null || _handoffFailure is not { } failure ||
            _runtimeHandoffIntent is not null && !prepared.RuntimeOutputReady ||
            _handoffReady is not { Task.IsCompletedSuccessfully: true } ||
            _handoffOpening is not { IsCancellationRequested: false } ||
            _handoffTarget is not { } target || !HandoffScopeCurrent(target))
        {
            await DiscardHandoffCoreAsync();
            throw Error(PlaybackError.Superseded);
        }
        bool failed = false;
        await _commands.WaitAsync(token);
        try
        {
            if (!CommitAllowed()) return false;
            var current = _session;
            float rate = current is { Released: false } ? current.Player.Rate : _retiredHandoffRate;
            await Task.Run(() =>
            {
                // Prepare all fallible controls while the accepted output is still untouched.
                if (rate > 0 && Math.Abs(rate - 1) > 0.001 && prepared.Player.SetRate(rate) != 0)
                    throw Error(PlaybackError.RateUnsupported);
                prepared.Player.Volume = _volume;
            });
            if (!CommitAllowed()) return false;
            long time = await SynchronizeHandoffAsync(prepared, target, failure, token);
            // Preparation yielded the dispatcher. Recheck cancellation and the latest intent
            // before the non-awaiting commit; a queued stop or replacement must win here.
            if (!CommitAllowed()) return false;
            if (Math.Abs(prepared.Player.Time - HandoffPosition()) > 250)
                throw Error(PlaybackError.ReadSeekFailed);
            if (current?.RuntimeOutput is { } output)
            {
                await output.DisposeAsync();
                if (!CommitAllowed()) return false;
            }
            var runtimeIntent = _runtimeHandoffIntent;
            bool keepPaused = runtimeIntent is { PreserveCurrent: false } ? runtimeIntent.Paused : _pauseRequested;
            prepared.Player.Volume = _volume;
            // A resource's token dies when its old lease is released. Never attach the
            // replacement to that donor token; its own playback/capability owns lifetime.
            if (runtimeIntent is not null || prepared.Resource is not null || current?.Resource is not null)
            {
                var previousAuthority = _continuationAuthority;
                _continuationAuthority = CancellationTokenSource.CreateLinkedTokenSource(_handoffLifetime);
                previousAuthority?.Cancel();
                previousAuthority?.Dispose();
            }
            // These short controls and publication are one dispatcher turn. The old
            // output falls silent before the new one may sound; release remains async.
            if (current is { Released: false }) current.Player.Mute = true;
            prepared.Player.Mute = false;
            _handoffDetach?.Invoke();
            _handoffDetach = null;
            _handoffSession = null; _handoffReady = null; _handoffFailure = null; _handoffTarget = null;
            _handoffPreparation = null;
            _handoffLifetime = default;
            var opening = _handoffOpening; _handoffOpening = null;
            opening?.Dispose();
            // Publish the prepared input as the session and fence everything the old one could still deliver.
            current?.Invalidate();
            _generation = prepared.Generation;
            _session = prepared;
            _handoffDonorRetired = false;
            if (runtimeIntent is not null)
            {
                Set(ref _ownerId, runtimeIntent.OwnerId, nameof(OwnerId));
                Set(ref _identity, runtimeIntent.Identity, nameof(Identity));
                Set(ref _title, runtimeIntent.Title, nameof(Title));
                Set(ref _pauseRequested, keepPaused, nameof(PauseRequested));
                Set(ref _videoOutput, null, nameof(ObservedVideoOutput));
            }
            // Publish business provenance in this same dispatcher turn, before events or old-input release.
            committed();
            prepared.HasStarted = true;
            prepared.Attach(this);
            _view.SetPlayer(prepared.Player);
            ReleaseRuntimeHandoffOutput();
            var authority = _continuationAuthority;
            if (authority is not null)
            {
                prepared.Cancellation = authority.Token.Register(() =>
                {
                    Interlocked.Exchange(ref prepared.Cancelled, 1);
                    prepared.Invalidate();
                    Post(() => Observe(CancelSessionAsync(prepared)));
                });
            }
            _continuable = runtimeIntent is null && prepared.Resource is null;
            _continuation = null;
            Set(ref _lastErrorCode, null, nameof(LastErrorCode));
            Set(ref _hasMedia, true, nameof(HasMedia));
            Set(ref _tracks, null, nameof(Tracks));
            lock (prepared.ProgressLock) prepared.LatestTime = time;
            Set(ref _positionSeconds, time / 1000d, nameof(PositionSeconds));
            Set(ref _durationSeconds, Math.Max(0, prepared.Player.Length) / 1000d, nameof(DurationSeconds));
            if (!keepPaused) prepared.Player.SetPause(false);
            Set(ref _isPlaying, !keepPaused, nameof(IsPlaying));
            if (runtimeIntent is not null) ObserveVideoOutput(prepared);
            if (current is not null) failed = await ReleaseNativeAsync(current, detachView: false);
        }
        finally { _commands.Release(); }
        if (failed)
        {
            _releaseFaulted = true;
            throw Error(PlaybackError.ReleaseFailed);
        }
        return true;

        bool CommitAllowed()
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_handoffSession, prepared) ||
                _handoffOpening is not { IsCancellationRequested: false } || !HandoffScopeCurrent(target))
                throw Error(PlaybackError.Superseded);
            CheckHandoffInput(prepared, failure);
            if (_session?.RuntimeOutput is { ReleaseFailed: true }) throw Error(PlaybackError.ReleaseFailed);
            return canCommit();
        }
    }

    private static void CheckHandoffInput(Session prepared, TaskCompletionSource<string> failure)
    {
        prepared.Resource?.CheckCurrent();
        if (prepared.LastInputError is { } code) throw Error(code);
        if (failure.Task.IsCompletedSuccessfully) throw Error(failure.Task.Result);
        if (prepared.Player.State is VLCState.Error or VLCState.Ended or VLCState.Stopped)
            throw Error(PlaybackError.OpenFailed);
    }

    private bool HandoffScopeCurrent((string Owner, string Identity, long Generation) target) =>
        _runtimeHandoffIntent is not null ? RuntimeHandoffScopeCurrent(target) :
        !_disposing && !_releaseFaulted && _generation == target.Generation && _ownerId == target.Owner &&
        _identity == target.Identity && (_session is { } current && IsCurrent(current) ||
            _handoffDonorRetired && _continuable && _continuationAuthority is { IsCancellationRequested: false } &&
            _lastErrorCode is not (PlaybackError.ReadRevoked or PlaybackError.ResourceRevoked));

    private bool RuntimeHandoffScopeCurrent((string Owner, string Identity, long Generation) target) =>
        !_disposing && !_releaseFaulted && _generation == target.Generation &&
        (_ownerId ?? "") == target.Owner && (_identity ?? "") == target.Identity &&
        (_session is null || IsCurrent(_session));

    private long HandoffPosition() => _runtimeHandoffIntent is { PreserveCurrent: false } intent
        ? (long)(intent.Position * 1000)
        : _session is { Released: false } current && IsCurrent(current)
        ? Math.Max(0, current.Player.Time) : (long)(_positionSeconds * 1000);

    private void CaptureHandoffControls(Session session)
    {
        if (session.Released) return;
        _retiredHandoffRate = session.Player.Rate;
        long time = session.Player.Time;
        if (time >= 0) Set(ref _positionSeconds, time / 1000d, nameof(PositionSeconds));
    }

    private async Task StopOutputForHandoffCoreAsync()
    {
        ThrowIfDisposed();
        if (_releaseFaulted) throw Error(PlaybackError.ReleaseFailed);
        var current = _session;
        _handoffDonorRetired = true;
        if (current is not null)
        {
            CaptureHandoffControls(current);
            current.Player.Mute = true;
            current.Invalidate();
        }
        _handoffOpening?.Cancel();
        Set(ref _isPlaying, false, nameof(IsPlaying));
        Set(ref _hasMedia, false, nameof(HasMedia));
        long generation = _generation;
        await _commands.WaitAsync();
        try
        {
            if (_generation != generation) return;
            await RetireAsync();
        }
        finally { _commands.Release(); }
    }

    private async Task<long> SynchronizeHandoffAsync(Session prepared,
        (string Owner, string Identity, long Generation) scope,
        TaskCompletionSource<string> failure, CancellationToken token)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            token.ThrowIfCancellationRequested();
            if (!HandoffScopeCurrent(scope) ||
                !ReferenceEquals(_handoffSession, prepared) ||
                _handoffOpening is not { IsCancellationRequested: false })
                throw Error(PlaybackError.Superseded);
            CheckHandoffInput(prepared, failure);
            long target = HandoffPosition();
            if (Math.Abs(prepared.Player.Time - target) <= 250) return Math.Max(0, prepared.Player.Time);
            if (!prepared.Player.IsSeekable) throw Error(PlaybackError.NotSeekable);
            await Task.Run(() => prepared.Player.Time = target, token);
            // A successful setter only queues the seek. Read back the native clock
            // before aligning again with a donor which may still be advancing.
            while (Math.Abs(prepared.Player.Time - target) > 250)
            {
                if (!HandoffScopeCurrent(scope) || !ReferenceEquals(_handoffSession, prepared) ||
                    _handoffOpening is not { IsCancellationRequested: false })
                    throw Error(PlaybackError.Superseded);
                CheckHandoffInput(prepared, failure);
                if (deadline.Elapsed >= TimeSpan.FromSeconds(15)) throw Error(PlaybackError.ReadSeekFailed);
                await Task.Delay(15, token);
            }
        }
        throw Error(PlaybackError.ReadSeekFailed);
    }

    private Task DiscardHandoffCoreAsync(bool invalidatePreparation = true,
        RuntimeMediaPlaybackIntent? nextRuntimeIntent = null)
    {
        if (invalidatePreparation) _handoffSequence++;
        var opening = _handoffOpening;
        var prepared = _handoffSession;
        var window = _runtimeHandoffWindow;
        var detach = _handoffDetach;
        var preparation = _handoffPreparation;
        var intent = _runtimeHandoffIntent;
        var preceding = _handoffRetirement is { IsCompletedSuccessfully: false } ? _handoffRetirement : null;
        // Admit the new owner before old resources asynchronously drain. A stale
        // music callback must not invalidate a newer runtime preparation in that interval.
        _runtimeHandoffWindow = null; _runtimeHandoffIntent = nextRuntimeIntent;
        _handoffOpening = null; _handoffSession = null; _handoffReady = null; _handoffFailure = null; _handoffTarget = null;
        _handoffPreparation = null;
        _handoffLifetime = default;
        _handoffDetach = null;
        var retirement = opening is null && prepared is null && window is null && detach is null && preparation is null
            ? preceding ?? Task.CompletedTask : Drain();
        _handoffRetirement = retirement;
        if (intent is not null) intent.Retirement ??= retirement;
        return retirement;

        async Task Drain()
        {
            // Install the shared receipt before cancellation or external cleanup
            // can reenter discard/close on this dispatcher.
            await Task.Yield();
            bool failed = false;
            try { detach?.Invoke(); }
            catch { failed = true; }
            try { opening?.Cancel(); }
            catch (ObjectDisposedException) { }
            catch { failed = true; }
            try { if (preceding is not null) await preceding; }
            catch { failed = true; }
            try { if (preparation is not null) failed |= await preparation; }
            catch { failed = true; }
            try { opening?.Dispose(); }
            catch { failed = true; }
            try { if (prepared is not null) failed |= await ReleaseNativeAsync(prepared, detachView: false); }
            catch { failed = true; }
            try { window?.Dispose(); }
            catch { failed = true; }
            if (failed)
            {
                _releaseFaulted = true;
                throw Error(PlaybackError.ReleaseFailed);
            }
        }
    }
}

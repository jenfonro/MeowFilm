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
    private float _retiredHandoffRate = 1;
    private bool _handoffDonorRetired;

    public Task StopOutputForHandoffAsync() => DispatchAsync(StopOutputForHandoffCoreAsync);
    public Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
        DispatchAsync(() => PrepareHandoffCoreAsync(uri, headers, null, token));
    public double PreparedHandoffDurationSeconds => Math.Max(0, _handoffSession?.Player.Length ?? 0) / 1000d;
    public async Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token)
    {
        if (lease is null) throw Error(PlaybackError.InvalidReadLease);
        var input = new ReadLeaseMediaInput(lease);
        try { await DispatchAsync(() => PrepareHandoffCoreAsync(null, null, input, token)); }
        catch { await input.DisposeAsync(); throw; }
    }
    public Task CommitHandoffAsync(Action committed, CancellationToken token) => DispatchAsync(() => CommitHandoffCoreAsync(committed, token));
    public Task DiscardHandoffAsync() => DispatchAsync(DiscardHandoffCoreAsync);

    private async Task PrepareHandoffCoreAsync(Uri? uri, IReadOnlyDictionary<string, string>? headers,
        ReadLeaseMediaInput? readInput, CancellationToken token)
    {
        ThrowIfDisposed();
        if (_releaseFaulted) throw Error(PlaybackError.ReleaseFailed);
        await DiscardHandoffCoreAsync();
        if (_ownerId is null || _identity is null) throw Error(PlaybackError.Superseded);
        var target = (_ownerId, _identity, _generation);
        if (!HandoffScopeCurrent(target)) throw Error(PlaybackError.Superseded);
        Dictionary<string, string>? requestHeaders = null;
        if (readInput is null)
        {
            if (uri?.Scheme is not ("http" or "https") || headers is null) throw Error(PlaybackError.InvalidRequest);
            Validate(_ownerId, _identity, uri, _title, headers);
            requestHeaders = HttpMediaInput.ValidateRequest(uri, headers, _httpOptions, true);
        }
        else readInput.Validate();
        token.ThrowIfCancellationRequested();
        var opening = CancellationTokenSource.CreateLinkedTokenSource(token);
        _handoffOpening = opening;
        _handoffTarget = target;
        HttpMediaInput? input = null;
        AdaptiveMediaSession? adaptive = null;
        Session? prepared = null;
        void Check()
        {
            opening.Token.ThrowIfCancellationRequested();
            if (!HandoffScopeCurrent(target) ||
                !ReferenceEquals(_handoffOpening, opening))
                throw new OperationCanceledException(PlaybackError.Superseded, new CancellationToken(true));
        }
        try
        {
            if (readInput is not null)
            {
                readInput.AttachOpening(opening.Token);
                await Task.Run(readInput.PrepareAsync);
                Check();
            }
            else input = await HttpMediaInput.OpenPlaybackAsync(uri!, requestHeaders!, _httpOptions, opening.Token);
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
                bool credentials = input.HeadersAllowed;
                input.Dispose(); input = null;
                adaptive = await AdaptiveMediaSession.OpenAsync(manifest, resolved, credentials,
                    uri!, requestHeaders!, _httpOptions, opening.Token);
                Check();
            }
            var media = input; var stream = adaptive;
            try { prepared = await Task.Run(() => CreateSession(target.Item3 + 1, uri, media, readInput, adaptive: stream ?? readInput?.Adaptive)); }
            catch { throw Error(PlaybackError.OpenFailed); }
            input = null; adaptive = null; // The prepared session owns its input through release.
            if (ReferenceEquals(_handoffOpening, opening)) _handoffSession = prepared;
            Check();
            prepared.Media.AddOption(":start-paused");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _handoffReady = ready;
            _handoffFailure = failure;
            var player = prepared.Player;
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
            _handoffDetach = () =>
            {
                player.Paused -= paused; player.EncounteredError -= failed; player.EndReached -= failed;
                if (prepared.ReadInput is not null) prepared.ReadInput.Failed -= inputFailed;
                if (prepared.Input is not null) prepared.Input.Failed -= inputFailed;
                if (prepared.Adaptive is not null) prepared.Adaptive.Failed -= inputFailed;
            };
            await Task.Run(() => { player.Mute = true; player.Volume = 0; });
            Check();
            if (!await Task.Run(player.Play)) throw Error(prepared.LastInputError ?? PlaybackError.OpenFailed);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(12), opening.Token);
            Check();
            CheckHandoffInput(prepared, failure);
            // Position the paused input at the live time so the commit continues without a gap.
            double position = _positionSeconds;
            if (position > 0)
            {
                if (!player.IsSeekable) throw Error(PlaybackError.NotSeekable);
                await Task.Run(() => player.Time = (long)(position * 1000));
            }
            Check();
            CheckHandoffInput(prepared, failure);
        }
        catch (Exception error)
        {
            input?.Dispose();
            if (adaptive is not null) await adaptive.DisposeAsync();
            if (ReferenceEquals(_handoffOpening, opening)) await DiscardHandoffCoreAsync();
            else if (prepared is not null && !ReferenceEquals(_handoffSession, prepared))
                await ReleaseNativeAsync(prepared, detachView: false);
            if (error is OperationCanceledException or PlaybackException) throw;
            throw Error(PlaybackError.OpenFailed);
        }
    }

    private async Task CommitHandoffCoreAsync(Action committed, CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        var prepared = _handoffSession;
        if (prepared is null || _handoffFailure is not { } failure ||
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
            if (!ReferenceEquals(_handoffSession, prepared) || !HandoffScopeCurrent(target))
                throw Error(PlaybackError.Superseded);
            CheckHandoffInput(prepared, failure);
            var current = _session;
            float rate = current is { Released: false } ? current.Player.Rate : _retiredHandoffRate;
            await Task.Run(() =>
            {
                // Prepare all fallible controls while the accepted output is still untouched.
                if (rate > 0 && Math.Abs(rate - 1) > 0.001 && prepared.Player.SetRate(rate) != 0)
                    throw Error(PlaybackError.RateUnsupported);
                prepared.Player.Volume = _volume;
            });
            long time = await SynchronizeHandoffAsync(prepared, target, failure, token);
            // Preparation yielded the dispatcher. Recheck cancellation and the latest intent
            // before the non-awaiting commit; a queued stop or replacement must win here.
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_handoffSession, prepared) ||
                _handoffOpening is not { IsCancellationRequested: false } ||
                !HandoffScopeCurrent(target))
                throw Error(PlaybackError.Superseded);
            CheckHandoffInput(prepared, failure);
            if (Math.Abs(prepared.Player.Time - HandoffPosition()) > 250)
                throw Error(PlaybackError.ReadSeekFailed);
            bool keepPaused = _pauseRequested;
            prepared.Player.Volume = _volume;
            // These short controls and publication are one dispatcher turn. The old
            // output falls silent before the new one may sound; release remains async.
            if (current is { Released: false }) current.Player.Mute = true;
            prepared.Player.Mute = false;
            _handoffDetach?.Invoke();
            _handoffDetach = null;
            _handoffSession = null; _handoffReady = null; _handoffFailure = null; _handoffTarget = null;
            var opening = _handoffOpening; _handoffOpening = null;
            opening?.Dispose();
            // Publish the prepared input as the session and fence everything the old one could still deliver.
            current?.Invalidate();
            _generation = prepared.Generation;
            _session = prepared;
            _handoffDonorRetired = false;
            // Publish business provenance in this same dispatcher turn, before events or old-input release.
            committed();
            prepared.HasStarted = true;
            prepared.Attach(this);
            _view.SetPlayer(prepared.Player);
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
            _continuable = true;
            _continuation = null;
            Set(ref _lastErrorCode, null, nameof(LastErrorCode));
            Set(ref _hasMedia, true, nameof(HasMedia));
            Set(ref _tracks, null, nameof(Tracks));
            lock (prepared.ProgressLock) prepared.LatestTime = time;
            Set(ref _positionSeconds, time / 1000d, nameof(PositionSeconds));
            Set(ref _durationSeconds, Math.Max(0, prepared.Player.Length) / 1000d, nameof(DurationSeconds));
            if (!keepPaused) prepared.Player.SetPause(false);
            Set(ref _isPlaying, !keepPaused, nameof(IsPlaying));
            if (current is not null) failed = await ReleaseNativeAsync(current, detachView: false);
        }
        finally { _commands.Release(); }
        if (failed)
        {
            _releaseFaulted = true;
            throw Error(PlaybackError.ReleaseFailed);
        }
    }

    private static void CheckHandoffInput(Session prepared, TaskCompletionSource<string> failure)
    {
        if (prepared.LastInputError is { } code) throw Error(code);
        if (failure.Task.IsCompletedSuccessfully) throw Error(failure.Task.Result);
        if (prepared.Player.State is VLCState.Error or VLCState.Ended or VLCState.Stopped)
            throw Error(PlaybackError.OpenFailed);
    }

    private bool HandoffScopeCurrent((string Owner, string Identity, long Generation) target) =>
        !_disposing && !_releaseFaulted && _generation == target.Generation && _ownerId == target.Owner &&
        _identity == target.Identity && (_session is { } current && IsCurrent(current) ||
            _handoffDonorRetired && _continuable && _continuationAuthority is { IsCancellationRequested: false } &&
            _lastErrorCode is not (PlaybackError.ReadRevoked or PlaybackError.ResourceRevoked));

    private long HandoffPosition() => _session is { Released: false } current && IsCurrent(current)
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

    private async Task DiscardHandoffCoreAsync()
    {
        var opening = _handoffOpening;
        var prepared = _handoffSession;
        _handoffOpening = null; _handoffSession = null; _handoffReady = null; _handoffFailure = null; _handoffTarget = null;
        _handoffDetach?.Invoke(); _handoffDetach = null;
        if (opening is not null) { try { opening.Cancel(); } catch (ObjectDisposedException) { } opening.Dispose(); }
        if (prepared is not null && await ReleaseNativeAsync(prepared, detachView: false)) _releaseFaulted = true;
    }
}

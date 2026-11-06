using LibVLCSharp.Shared;

namespace MeowFilm.Media;

/// <summary>
/// Seamless source replacement: a new network input is opened paused and muted beside the
/// playing one and positioned at the live time. Commit swaps them in one step; the old
/// input keeps playing until then, and a failed or abandoned preparation never touches it.
/// </summary>
public interface INativePlaybackHandoff
{
    Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token);
    Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token);
    double PreparedHandoffDurationSeconds { get; }
    Task CommitHandoffAsync(CancellationToken token);
    Task DiscardHandoffAsync();
}

public sealed partial class NativePlayback : INativePlaybackHandoff
{
    private Session? _handoffSession;
    private CancellationTokenSource? _handoffOpening;
    private (string Owner, string Identity, long Generation)? _handoffTarget;
    private TaskCompletionSource? _handoffReady;
    private Action? _handoffDetach;

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
    public Task CommitHandoffAsync(CancellationToken token) => DispatchAsync(() => CommitHandoffCoreAsync(token));
    public Task DiscardHandoffAsync() => DispatchAsync(DiscardHandoffCoreAsync);

    private async Task PrepareHandoffCoreAsync(Uri? uri, IReadOnlyDictionary<string, string>? headers,
        ReadLeaseMediaInput? readInput, CancellationToken token)
    {
        ThrowIfDisposed();
        if (_releaseFaulted) throw Error(PlaybackError.ReleaseFailed);
        await DiscardHandoffCoreAsync();
        var current = _session;
        if (current is null || !IsCurrent(current) || _ownerId is null || _identity is null) throw Error(PlaybackError.Superseded);
        Dictionary<string, string>? requestHeaders = null;
        if (readInput is null)
        {
            if (uri?.Scheme is not ("http" or "https") || headers is null) throw Error(PlaybackError.InvalidRequest);
            Validate(_ownerId, _identity, uri, _title, headers);
            requestHeaders = HttpMediaInput.ValidateRequest(uri, headers, _httpOptions, true);
        }
        else readInput.Validate();
        token.ThrowIfCancellationRequested();
        var target = (_ownerId, _identity, _generation);
        var opening = CancellationTokenSource.CreateLinkedTokenSource(token);
        _handoffOpening = opening;
        _handoffTarget = target;
        HttpMediaInput? input = null;
        AdaptiveMediaSession? adaptive = null;
        Session? prepared = null;
        void Check()
        {
            opening.Token.ThrowIfCancellationRequested();
            if (_disposing || _generation != target.Item3 || !ReferenceEquals(_session, current) || !IsCurrent(current) ||
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
            _handoffReady = ready;
            var player = prepared.Player;
            EventHandler<EventArgs> paused = (_, _) => ready.TrySetResult();
            EventHandler<EventArgs> failed = (_, _) => ready.TrySetException(Error(prepared.LastInputError ?? PlaybackError.OpenFailed));
            Action<string> inputFailed = code => ready.TrySetException(Error(code));
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
            // Position the paused input at the live time so the commit continues without a gap.
            double position = _positionSeconds;
            if (position > 0)
            {
                if (!player.IsSeekable) throw Error(PlaybackError.NotSeekable);
                await Task.Run(() => player.Time = (long)(position * 1000));
            }
            Check();
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

    private async Task CommitHandoffCoreAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        var prepared = _handoffSession;
        var current = _session;
        if (prepared is null || current is null || _handoffReady is not { Task.IsCompletedSuccessfully: true } ||
            _handoffOpening is not { IsCancellationRequested: false } ||
            _handoffTarget is not { } target || target.Item3 != _generation || target.Item1 != _ownerId ||
            target.Item2 != _identity || !IsCurrent(current))
        {
            await DiscardHandoffCoreAsync();
            throw Error(PlaybackError.Superseded);
        }
        bool failed = false;
        await _commands.WaitAsync(token);
        try
        {
            if (!ReferenceEquals(_session, current) || !ReferenceEquals(_handoffSession, prepared) ||
                _generation != target.Item3 || !IsCurrent(current))
                throw Error(PlaybackError.Superseded);
            float rate = 1;
            long time = 0;
            await Task.Run(() =>
            {
                // Prepare all fallible controls while the accepted output is still untouched.
                rate = current.Player.Rate;
                if (rate > 0 && Math.Abs(rate - 1) > 0.001 && prepared.Player.SetRate(rate) != 0)
                    throw Error(PlaybackError.RateUnsupported);
                prepared.Player.Volume = _volume;
                time = Math.Max(0, current.Player.Time);
                if (prepared.Player.IsSeekable && Math.Abs(prepared.Player.Time - time) > 250) prepared.Player.Time = time;
            });
            // Preparation yielded the dispatcher. Recheck cancellation and the latest intent
            // before the non-awaiting commit; a queued stop or replacement must win here.
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session, current) || !ReferenceEquals(_handoffSession, prepared) ||
                _handoffOpening is not { IsCancellationRequested: false } ||
                _generation != target.Item3 || !IsCurrent(current))
                throw Error(PlaybackError.Superseded);
            bool keepPaused = _pauseRequested;
            prepared.Player.Volume = _volume;
            // These short controls and publication are one dispatcher turn. The old
            // output falls silent before the new one may sound; release remains async.
            current.Player.Mute = true;
            prepared.Player.Mute = false;
            _handoffDetach?.Invoke();
            _handoffDetach = null;
            _handoffSession = null; _handoffReady = null; _handoffTarget = null;
            var opening = _handoffOpening; _handoffOpening = null;
            opening?.Dispose();
            // Publish the prepared input as the session and fence everything the old one could still deliver.
            current.Invalidate();
            _generation = prepared.Generation;
            _session = prepared;
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
            failed = await ReleaseNativeAsync(current, detachView: false);
        }
        finally { _commands.Release(); }
        if (failed)
        {
            _releaseFaulted = true;
            throw Error(PlaybackError.ReleaseFailed);
        }
    }

    private async Task DiscardHandoffCoreAsync()
    {
        var opening = _handoffOpening;
        var prepared = _handoffSession;
        _handoffOpening = null; _handoffSession = null; _handoffReady = null; _handoffTarget = null;
        _handoffDetach?.Invoke(); _handoffDetach = null;
        if (opening is not null) { try { opening.Cancel(); } catch (ObjectDisposedException) { } opening.Dispose(); }
        if (prepared is not null && await ReleaseNativeAsync(prepared, detachView: false)) _releaseFaulted = true;
    }
}

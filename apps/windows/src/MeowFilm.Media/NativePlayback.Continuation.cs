using System.Windows.Threading;

namespace MeowFilm.Media;

/// <summary>Optional port. Resolution and retry policy belong to the caller, never the media engine.</summary>
public interface INativePlaybackContinuation
{
    bool PauseRequested { get; }
    string? LastErrorCode { get; }
    NativePlaybackContinuation? CaptureContinuation();
    Task ReopenAsync(NativePlaybackContinuation continuation, Uri uri,
        IReadOnlyDictionary<string, string> headers, CancellationToken token = default);
    Task ReopenReadAsync(NativePlaybackContinuation continuation, INativeMediaReadLease lease, CancellationToken token = default);
}

/// <summary>A single-use, in-memory capability; stop, replacement and authorization cancellation revoke it.</summary>
public sealed class NativePlaybackContinuation
{
    internal NativePlaybackContinuation(NativePlaybackScope scope, double position, CancellationToken revoked)
    { Scope = scope; PositionSeconds = position; Revoked = revoked; }
    public NativePlaybackScope Scope { get; }
    public double PositionSeconds { get; }
    public CancellationToken Revoked { get; }
    public NativePlaybackScope? ReopenedScope { get; internal set; }
    internal bool Used;
}

public sealed partial class NativePlayback : INativePlaybackContinuation
{
    private CancellationTokenSource? _continuationAuthority;
    private NativePlaybackContinuation? _continuation;
    private bool _continuable, _pauseRequested;
    private long _intentSequence;
    public bool PauseRequested => _pauseRequested;
    public bool ContinuationPending => _continuation is not null && !_continuation.Revoked.IsCancellationRequested;

    public NativePlaybackContinuation? CaptureContinuation()
    {
        RequireDispatcher();
        if (_disposing || !_continuable || _lastErrorCode == PlaybackError.ReadRevoked || _ownerId is null || _identity is null ||
            _continuationAuthority is null || _continuationAuthority.IsCancellationRequested) return null;
        if (_continuation is not null) return _continuation.Used ? null : _continuation;
        _continuation = new(new(_ownerId, _identity, _generation), _positionSeconds, _continuationAuthority.Token);
        PropertyChanged?.Invoke(this, new(nameof(ContinuationPending)));
        return _continuation;
    }

    public Task ReopenAsync(NativePlaybackContinuation continuation, Uri uri,
        IReadOnlyDictionary<string, string> headers, CancellationToken token = default) =>
        DispatchAsync(() => ReopenCoreAsync(continuation, uri, headers, null, token));

    public Task ReopenReadAsync(NativePlaybackContinuation continuation, INativeMediaReadLease lease,
        CancellationToken token = default) =>
        TrackOwnedMediaRequestAsync(() => ReopenReadOwnedAsync(continuation, lease, token));

    private async Task ReopenReadOwnedAsync(NativePlaybackContinuation continuation, INativeMediaReadLease lease,
        CancellationToken token)
    {
        if (lease is null) throw Error(PlaybackError.InvalidReadLease);
        var input = new ReadLeaseMediaInput(lease);
        try { await DispatchAsync(() => ReopenCoreAsync(continuation, null, null, input, token)); }
        catch { await input.DisposeAsync(); throw; }
    }
    private async Task ReopenCoreAsync(NativePlaybackContinuation continuation, Uri? uri,
        IReadOnlyDictionary<string, string>? headers, ReadLeaseMediaInput? input, CancellationToken token)
    {
        ThrowIfDisposed();
        if (continuation is null || !ReferenceEquals(_continuation, continuation) || continuation.Used ||
            continuation.Scope.Generation != _generation || continuation.Scope.OwnerId != _ownerId ||
            continuation.Scope.Identity != _identity) throw Error(PlaybackError.Superseded);
        continuation.Revoked.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        if (input is not null) input.Validate();
        else
        {
            if (uri?.Scheme is not ("http" or "https") || headers is null) throw Error(PlaybackError.InvalidRequest);
            Validate(_ownerId!, _identity!, uri, _title, headers);
        }
        continuation.Used = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, continuation.Revoked);
        try
        {
            if (input is not null)
                await OpenReadCoreAsync(_ownerId!, _identity!, input, _title, _continuationAuthority!.Token,
                    continuation, operation.Token);
            else
                await OpenCoreAsync(_ownerId!, _identity!, uri!, _title, headers, _continuationAuthority!.Token,
                    continuation: continuation, preparationToken: operation.Token);
        }
        catch
        {
            // A failed input consumes only this capability, never the logical position/pause authority.
            if (ReferenceEquals(_continuation, continuation) && !continuation.Revoked.IsCancellationRequested &&
                _ownerId == continuation.Scope.OwnerId && _identity == continuation.Scope.Identity)
            {
                _continuation = null;
                PropertyChanged?.Invoke(this, new(nameof(ContinuationPending)));
            }
            throw;
        }
        continuation.Revoked.ThrowIfCancellationRequested();
        continuation.ReopenedScope = new(_ownerId!, _identity!, _generation);
    }

    private void RevokeContinuation()
    {
        _continuable = false;
        _continuation = null;
        var authority = _continuationAuthority;
        _continuationAuthority = null;
        authority?.Cancel(); authority?.Dispose();
        PropertyChanged?.Invoke(this, new(nameof(ContinuationPending)));
    }

    private Task PauseWithIntentAsync()
    {
        ThrowIfDisposed();
        Set(ref _pauseRequested, true, nameof(PauseRequested));
        ++_intentSequence;
        if (_session is null || !_session.Valid ||
            _session.ContinuationReady is not null && !_session.ContinuationInitializing) return Task.CompletedTask;
        return PauseLiveAwareAsync();
    }
    private Task ResumeWithIntentAsync()
    {
        ThrowIfDisposed();
        Set(ref _pauseRequested, false, nameof(PauseRequested));
        ++_intentSequence;
        if (_session is null || !_session.Valid || _session.ContinuationReady is not null) return Task.CompletedTask;
        return ResumeLiveAwareAsync();
    }

    private async Task InitializeContinuationAsync(Session session)
    {
        var ready = session.ContinuationReady;
        if (ready is null || session.ContinuationInitializing) return;
        session.ContinuationInitializing = true;
        await _commands.WaitAsync();
        try
        {
            if (!IsCurrent(session)) throw new OperationCanceledException();
            await Task.Run(() =>
            {
                if (session.ContinuationPosition > 0)
                {
                    if (!session.Player.IsSeekable) throw Error(PlaybackError.NotSeekable);
                    session.Player.Time = (long)(session.ContinuationPosition * 1000);
                }
            });
            if (!IsCurrent(session)) throw new OperationCanceledException();
            // Read intent only after preparation/seek; a Pause delivered during resolution wins.
            // No player is started audibly then paused as a substitute for paused startup.
            if (!_pauseRequested) await Task.Run(() =>
            {
                if (IsCurrent(session) && !_pauseRequested) session.Player.SetPause(false);
            });
            session.ContinuationReady = null;
            _continuation = null;
            PropertyChanged?.Invoke(this, new(nameof(ContinuationPending)));
            ready.TrySetResult();
        }
        catch (Exception error) { ready.TrySetException(error); }
        finally { _commands.Release(); }
    }
}

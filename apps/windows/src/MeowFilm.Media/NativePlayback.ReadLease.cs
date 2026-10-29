using System.Windows.Threading;

namespace MeowFilm.Media;

public sealed partial class NativePlayback
{
    /// <summary>
    /// Takes ownership of lease immediately, even on cancellation/validation/dispatcher failure.
    /// Plays only the already-authorized stream; identity/title are never interpreted as a path
    /// or URL. Completes at accepted Play(), not first frame or proven codec/output support.
    /// Caller cancellation and lease revocation remain effective for the whole session.
    /// </summary>
    public async Task OpenReadAsync(string ownerId, string identity, INativeMediaReadLease lease,
        string title, CancellationToken token = default)
    {
        if (lease is null) throw Error(PlaybackError.InvalidReadLease);
        var input = new ReadLeaseMediaInput(lease);
        try
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                throw Error(PlaybackError.DispatcherUnavailable);
            if (_dispatcher.CheckAccess()) await OpenReadCoreAsync(ownerId, identity, input, title, token);
            else
            {
                try
                {
                    await _dispatcher.InvokeAsync(
                        () => OpenReadCoreAsync(ownerId, identity, input, title, token),
                        DispatcherPriority.Normal, token).Task.Unwrap().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested &&
                    (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished))
                { throw Error(PlaybackError.DispatcherUnavailable); }
            }
        }
        catch
        {
            // Also covers a never-executed/aborted dispatcher operation. Shared release task
            // guarantees exactly one lease.DisposeAsync invocation, not just idempotent leases.
            await input.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task OpenReadCoreAsync(string ownerId, string identity, ReadLeaseMediaInput input,
        string title, CancellationToken token)
    {
        ThrowIfDisposed();
        if (_releaseFaulted) throw Error(PlaybackError.ReleaseFailed);
        ValidateIdentity(ownerId, identity, title);
        token.ThrowIfCancellationRequested();
        input.Validate(); // Invalid capability/extent leaves existing playback intact.
        token.ThrowIfCancellationRequested();
        var generation = BeginReplacement();
        // The capability owner may replace an authorized cached input with a freshly
        // resolved HTTP input. The lease still owns its bytes; this capability
        // only preserves logical position and intent, never grants URL rights.
        _continuationAuthority = CancellationTokenSource.CreateLinkedTokenSource(token);
        _continuable = true;
        Set(ref _pauseRequested, false, nameof(PauseRequested));
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(token);
        _opening = opening;
        input.AttachOpening(opening.Token);
        bool transferred = false;
        string? failure = null;
        await _commands.WaitAsync();
        try
        {
            CheckReadRequest();
            await RetireAsync();
            ClearState();
            CheckReadRequest();
            await Task.Run(input.PrepareAsync); // No synchronous stream work on WPF's dispatcher.
            CheckReadRequest();
            try { await Task.Run(EnsureEngine); }
            catch (PlaybackException) { throw; }
            catch { throw Error(PlaybackError.NativeUnavailable); }
            CheckReadRequest();
            Set(ref _nativeVersion, _engine!.Version, nameof(NativeVersion));
            Session created;
            try { created = await Task.Run(() => CreateSession(generation, null, null, input, input.Adaptive)); }
            catch { throw Error(PlaybackError.OpenFailed); }
            _session = created;
            transferred = true;
            created.Attach(this);
            _view.SetPlayer(created.Player);
            CheckReadRequest();
            Set(ref _ownerId, ownerId, nameof(OwnerId));
            Set(ref _identity, identity, nameof(Identity));
            Set(ref _title, title, nameof(Title));
            Set(ref _hasMedia, true, nameof(HasMedia));
            created.Cancellation = token.Register(() =>
            {
                Interlocked.Exchange(ref created.Cancelled, 1);
                created.Invalidate();
                Post(() => Observe(CancelSessionAsync(created)));
            });
            CheckReadRequest();
            bool accepted = await Task.Run(created.Player.Play);
            CheckReadRequest();
            if (!accepted) throw Error(created.LastInputError ?? PlaybackError.OpenFailed);
        }
        catch (OperationCanceledException)
        {
            if (_session?.Generation == generation) await RetireAsync();
            if (_generation == generation) ClearState();
            throw;
        }
        catch (Exception error)
        {
            failure = error is PlaybackException known ? known.Code : PlaybackError.OpenFailed;
            if (_session?.Generation == generation)
            {
                try { await RetireAsync(); }
                catch { failure = PlaybackError.ReleaseFailed; }
            }
            if (_generation == generation && !_disposing)
            {
                Set(ref _isPlaying, false, nameof(IsPlaying));
                Set(ref _hasMedia, false, nameof(HasMedia));
                Set(ref _lastErrorCode, failure, nameof(LastErrorCode));
            }
        }
        finally
        {
            try { if (!transferred) await input.DisposeAsync(); }
            finally
            {
                if (ReferenceEquals(_opening, opening)) _opening = null;
                _commands.Release();
            }
        }
        if (failure is not null)
        {
            if (_generation == generation && !_disposing) Failed?.Invoke(this, EventArgs.Empty);
            throw Error(failure);
        }

        void CheckReadRequest()
        {
            CheckRequest(generation, token);
            input.ThrowIfFaulted();
        }
    }
}

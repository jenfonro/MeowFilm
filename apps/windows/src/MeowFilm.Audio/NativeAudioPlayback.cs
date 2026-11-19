using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Media;

public sealed record AudioPlaybackRate(NativePlaybackScope Scope, double RequestedRate);

// Music owns its decoder, PCM pipeline and output. The neutral occurrence/lease
// capabilities are shared with video, not its player, tracks or native engine.
public sealed class NativeAudioPlayback : INotifyPropertyChanged, IAsyncDisposable,
    INativePlaybackContinuation, INativePlaybackHandoff
{
    private readonly OwnerDispatcher dispatcher;
    private readonly Func<CancellationToken, Task<IDisposable>>? acquireOutput;
    private readonly NativeAudioOutput audioOutput;
    private readonly SemaphoreSlim commands = new(1);
    private readonly HashSet<Task> operations = [];
    private readonly HashSet<Task> inputs = [];
    private readonly object readGate = new();
    private readonly HashSet<Task> readRequests = [];
    private CancellationTokenSource? readQueue = new();
    private readonly DispatcherQueueTimer progress;
    private Session? session, prepared;
    private CancellationTokenSource? authority, opening, preparing;
    private NativePlaybackContinuation? continuation;
    private long generation, handoffSequence;
    private bool disposed, releaseFaulted, pauseRequested, recoverable, hasMedia, isPlaying;
    private string? ownerId, identity, lastErrorCode;
    private string title = "";
    private int volume = 100;
    private double position, duration, rate = 1;
    private Task? disposal;

    public NativeAudioPlayback(OwnerDispatcher? dispatcher = null, Func<CancellationToken, Task<IDisposable>>? acquireOutput = null,
        NativeAudioOutput audioOutput = NativeAudioOutput.SystemDefault)
    {
        this.dispatcher = dispatcher ?? OwnerDispatcher.Current;
        if (!this.dispatcher.HasThreadAccess || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw Error(PlaybackError.WrongThread);
        if (!Enum.IsDefined(audioOutput)) throw Error(PlaybackError.InvalidRequest);
        this.acquireOutput = acquireOutput;
        this.audioOutput = audioOutput;
        progress = this.dispatcher.CreateTimer(TimeSpan.FromMilliseconds(100), UpdatePosition);
        progress.Stop();
    }

    public string? OwnerId => ownerId;
    public string? Identity => identity;
    public string Title => title;
    public bool HasMedia => hasMedia;
    public bool IsPlaying => isPlaying;
    public bool PauseRequested => pauseRequested;
    public bool ContinuationPending => continuation is not null && !continuation.Revoked.IsCancellationRequested;
    public double PositionSeconds => position;
    public double DurationSeconds => duration;
    public string? LastErrorCode => lastErrorCode;
    public string? AudioSessionName => session?.Output?.SessionName;
    public double OutputDevicePositionSeconds => session?.Output?.DevicePositionSeconds ?? 0;
    public int Volume
    {
        get => volume;
        set
        {
            RequireDispatcher();
            ThrowIfDisposed();
            Set(ref volume, Math.Clamp(value, 0, 100));
            if (session?.Output is { } output) output.Volume = volume;
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Started;
    public event EventHandler? Ended;
    public event EventHandler? Failed;

    public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token = default) =>
        OpenAsync(owner, identity, uri, title, new Dictionary<string, string>(), token);
    public Task OpenAsync(string owner, string identity, Uri uri, string title,
        IReadOnlyDictionary<string, string> headers, CancellationToken token = default) =>
        DispatchAsync(() =>
        {
            var snapshot = ValidateRequest(uri, headers);
            return Track(() => TrackInput(() => OpenCoreAsync(owner, identity, title,
                lifetime => AudioDecoder.OpenAsync(uri, snapshot, lifetime), token)));
        });
    public Task OpenReadAsync(string owner, string identity, INativeMediaReadLease lease, string title,
        CancellationToken token = default) =>
        WithReadLeaseAsync(lease, factory => OpenCoreAsync(owner, identity, title, factory, token), token);

    private async Task WithReadLeaseAsync(INativeMediaReadLease lease,
        Func<Func<CancellationToken, Task<AudioDecoder>>, Task> operation, CancellationToken token)
    {
        if (lease is null) throw Error(PlaybackError.InvalidReadLease);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? queued = null;
        bool transferred = false;
        try
        {
            lock (readGate)
            {
                if (readQueue is null) throw Error(PlaybackError.Disposed);
                queued = CancellationTokenSource.CreateLinkedTokenSource(token, readQueue.Token);
                readRequests.Add(receipt.Task);
            }
            try
            {
                lease.Revoked.ThrowIfCancellationRequested();
                var content = lease.Content;
                if (!content.CanRead || !content.CanSeek || content.CanWrite || lease.Length < 0 ||
                    content.Length != lease.Length)
                    throw Error(PlaybackError.InvalidReadLease);
            }
            catch (Exception error) when (error is not PlaybackException)
            {
                throw Error(lease.Revoked.IsCancellationRequested
                    ? PlaybackError.ReadRevoked : PlaybackError.InvalidReadLease);
            }
            await DispatchAsync(() =>
            {
                if (lease.Revoked.IsCancellationRequested) throw Error(PlaybackError.ReadRevoked);
                return operation(lifetime =>
                {
                    transferred = true;
                    return AudioDecoder.OpenReadAsync(lease, lifetime);
                });
            }, queued.Token).ConfigureAwait(false);
        }
        finally
        {
            try { if (!transferred) await lease.DisposeAsync().ConfigureAwait(false); }
            catch
            {
                Volatile.Write(ref releaseFaulted, true);
                throw Error(PlaybackError.ReleaseFailed);
            }
            finally
            {
                queued?.Dispose();
                lock (readGate)
                {
                    readRequests.Remove(receipt.Task);
                    receipt.TrySetResult();
                }
            }
        }
    }

    private Task CancelQueuedReads(bool closing = false)
    {
        CancellationTokenSource? queue;
        Task[] pending;
        lock (readGate)
        {
            queue = readQueue;
            readQueue = closing ? null : new();
            pending = readRequests.ToArray();
        }
        // Queue cancellation must not replace the caller's session lifetime.
        // Receipts include rejection cleanup, even if the dispatcher never ran.
        queue?.Cancel();
        queue?.Dispose();
        return Task.WhenAll(pending);
    }

    private async Task OpenCoreAsync(string owner, string mediaIdentity, string mediaTitle,
        Func<CancellationToken, Task<AudioDecoder>> open, CancellationToken token,
        NativePlaybackContinuation? reopening = null)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref releaseFaulted)) throw Error(PlaybackError.ReleaseFailed);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(mediaIdentity) || mediaTitle is null)
            throw Error(PlaybackError.InvalidRequest);
        token.ThrowIfCancellationRequested();
        if (reopening is null)
        {
            RevokeAuthority();
            authority = CancellationTokenSource.CreateLinkedTokenSource(token);
            Set(ref pauseRequested, false, nameof(PauseRequested));
            position = 0;
            rate = 1;
        }
        long version = ++generation;
        opening?.Cancel();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, authority!.Token);
        opening = lifetime;
        Session? candidate = null;
        Set(ref ownerId, owner, nameof(OwnerId));
        Set(ref identity, mediaIdentity, nameof(Identity));
        Set(ref title, mediaTitle, nameof(Title));
        Set(ref lastErrorCode, null, nameof(LastErrorCode));
        recoverable = true;
        Set(ref hasMedia, false, nameof(HasMedia));
        Set(ref isPlaying, false, nameof(IsPlaying));
        progress.Stop();
        await DiscardPreparedCoreAsync();
        try
        {
            await commands.WaitAsync();
            try
            {
                Check(version, lifetime.Token);
                await RetireSessionAsync();
            }
            finally { commands.Release(); }
            Check(version, lifetime.Token);
            candidate = new(await open(lifetime.Token), lifetime, token);
            candidate.BasePosition = reopening?.PositionSeconds ?? 0;
            await ConfigureOutputAsync(candidate, candidate.BasePosition, rate, lifetime.Token);
            Check(version, lifetime.Token);
            await commands.WaitAsync(lifetime.Token);
            try
            {
                Check(version, lifetime.Token);
                session = candidate;
                AttachSession(candidate);
                opening = null;
                Set(ref duration, candidate.Decoder.DurationSeconds, nameof(DurationSeconds));
                Set(ref position, candidate.BasePosition, nameof(PositionSeconds));
                Set(ref hasMedia, true, nameof(HasMedia));
                continuation = null;
                Changed(nameof(ContinuationPending));
                await StartOutputAsync(candidate, lifetime.Token);
                candidate = null;
            }
            finally { commands.Release(); }
        }
        catch (Exception error)
        {
            if (candidate is not null) await ReleaseOwnedAsync(candidate);
            else if (!ReferenceEquals(session?.Lifetime, lifetime)) { lifetime.Cancel(); lifetime.Dispose(); }
            if (version == generation && !disposed && !token.IsCancellationRequested &&
                authority?.IsCancellationRequested == false)
            {
                continuation = null;
                PublishFailure(error);
            }
            else if (version == generation && !disposed)
            {
                RevokeAuthority();
                Set(ref ownerId, null, nameof(OwnerId));
                Set(ref identity, null, nameof(Identity));
            }
            throw;
        }
        finally
        {
            if (ReferenceEquals(opening, lifetime)) opening = null;
        }
    }

    public Task PauseAsync() => DispatchAsync(async () =>
    {
        ThrowIfDisposed();
        Set(ref pauseRequested, true, nameof(PauseRequested));
        await commands.WaitAsync();
        try
        {
            session?.Output?.Pause();
            UpdatePosition();
            Set(ref isPlaying, false, nameof(IsPlaying));
        }
        finally { commands.Release(); }
    });

    public Task ResumeAsync() => DispatchAsync(() => Track(async () =>
    {
        ThrowIfDisposed();
        Set(ref pauseRequested, false, nameof(PauseRequested));
        var target = session;
        if (target is null) return;
        await commands.WaitAsync();
        try
        {
            if (!ReferenceEquals(target, session)) return;
            target.Lifetime.Token.ThrowIfCancellationRequested();
            if (target.Output is null)
            {
                await ConfigureOutputAsync(target, position, rate, target.Lifetime.Token, rewind: true);
                if (!ReferenceEquals(target, session)) return;
            }
            if (!pauseRequested) await StartOutputAsync(target, target.Lifetime.Token);
        }
        finally { commands.Release(); }
    }));

    public Task SeekAsync(double seconds) => DispatchAsync(() => Track(async () =>
    {
        ThrowIfDisposed();
        if (!double.IsFinite(seconds) || seconds < 0) throw Error(PlaybackError.InvalidSeek);
        if (session is not { } current) return;
        await ReconfigureAsync(current, Math.Min(seconds, duration), rate, CancellationToken.None);
    }));

    public Task<AudioPlaybackRate?> ReadRateAsync(CancellationToken token = default) => DispatchValueAsync(() =>
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        return Task.FromResult<AudioPlaybackRate?>(session is null ? null : new(Scope(), rate));
    });
    public Task<AudioPlaybackRate> SetRateAsync(NativePlaybackScope scope, double value,
        CancellationToken token = default) => DispatchValueAsync(async () =>
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        if (!double.IsFinite(value) || value is < 0.5 or > 3) throw Error(PlaybackError.InvalidRate);
        var target = RequireScope(scope);
        if (Math.Abs(rate - value) > .000001)
        {
            UpdatePosition();
            await ReconfigureAsync(target, position, value, token);
        }
        RequireScope(scope);
        return new AudioPlaybackRate(scope, rate);
    });

    private async Task ReconfigureAsync(Session target, double seconds, double requestedRate, CancellationToken token)
    {
        if (!target.Decoder.CanSeek) throw Error(PlaybackError.NotSeekable);
        long version = generation;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, target.Lifetime.Token);
        await commands.WaitAsync(operation.Token);
        try
        {
            Check(version, operation.Token);
            if (!ReferenceEquals(target, session)) throw Error(PlaybackError.Superseded);
            Set(ref isPlaying, false, nameof(IsPlaying));
            progress.Stop();
            await ConfigureOutputAsync(target, seconds, requestedRate, operation.Token, rewind: true);
            Check(version, operation.Token);
            rate = requestedRate;
            Set(ref position, seconds, nameof(PositionSeconds));
            await StartOutputAsync(target, operation.Token);
        }
        catch (Exception error)
        {
            if (version == generation && !disposed && error is not OperationCanceledException)
            {
                await RetireSessionAsync();
                if (version == generation && !disposed) PublishFailure(error);
            }
            throw;
        }
        finally { commands.Release(); }
    }

    private async Task ConfigureOutputAsync(Session target, double seconds, double requestedRate, CancellationToken token,
        bool rewind = false)
    {
        rewind |= target.Output is not null || seconds > 0;
        if (target.Output is { } old)
        {
            target.Output = null; // Fences late stopped callbacks before joining the render thread.
            old.Pause();
            await old.DisposeAsync();
        }
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            target.Decoder.SetRate(requestedRate);
            if (rewind) target.Decoder.Seek(seconds, token);
        }, token);
        token.ThrowIfCancellationRequested();
        var output = new AudioOutputSession(target.Decoder, audioOutput);
        target.Output = output;
        output.Stopped += error => Post(() => FinishAsync(target, output, error));
        try
        {
            await output.PrepareAsync(token);
            token.ThrowIfCancellationRequested();
            target.BasePosition = seconds;
            output.Volume = volume;
        }
        catch
        {
            target.Output = null;
            await output.DisposeAsync();
            throw;
        }
    }

    // Called with commands held. Output admission may stop the other owner, so
    // never wait for it while retaining this owner's command gate.
    private async Task StartOutputAsync(Session target, CancellationToken token)
    {
        if (pauseRequested) return;
        long version = generation;
        var output = target.Output;
        IDisposable? admission = null;
        if (acquireOutput is not null)
        {
            commands.Release();
            try { admission = await acquireOutput(token); }
            finally { await commands.WaitAsync(); }
        }
        using (admission)
        {
            Check(version, token);
            target.Lifetime.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, target) || !ReferenceEquals(target.Output, output))
                throw Error(PlaybackError.Superseded);
            if (pauseRequested) return;
            target.Decoder.CheckAvailable();
            await output!.PlayAsync(token);
            Check(version, token);
            if (!ReferenceEquals(session, target) || !ReferenceEquals(target.Output, output))
                throw Error(PlaybackError.Superseded);
            Set(ref isPlaying, true, nameof(IsPlaying));
            progress.Start();
            if (!target.Started)
            {
                target.Started = true;
                Started?.Invoke(this, EventArgs.Empty);
            }
        }
    }
    private void UpdatePosition()
    {
        if (session?.Output is not { } output) return;
        double next = session.BasePosition + output.MediaPositionSeconds * rate;
        Set(ref position, duration > 0 ? Math.Min(duration, next) : next, nameof(PositionSeconds));
    }

    private void AttachSession(Session target)
    {
        target.Generation = generation;
        target.Cancellation = target.Lifetime.Token.Register(() => Post(() => CancelSessionAsync(target)));
        target.Revocation = target.Decoder.Revoked.Register(() =>
            Post(() => FinishAsync(target, target.Output, Error(PlaybackError.ReadRevoked))));
    }
    private async Task CancelSessionAsync(Session target)
    {
        if (!ReferenceEquals(session, target) || target.Generation != generation) return;
        await StopAsync();
    }
    private async Task FinishAsync(Session target, AudioOutputSession? output, Exception? error)
    {
        bool cancelled = false;
        await commands.WaitAsync();
        try
        {
            if (!ReferenceEquals(session, target) || target.Generation != generation || target.Lifetime.IsCancellationRequested ||
                !ReferenceEquals(target.Output, output)) return;
            UpdatePosition();
            bool ended = error is null && output?.IsDrained == true;
            var ownerCancellation = authority?.Token ?? default;
            await RetireSessionAsync();
            if (disposed || target.Generation != generation) return;
            cancelled = ownerCancellation.IsCancellationRequested || target.RequestCancellation.IsCancellationRequested;
            if (!cancelled)
            {
                if (ended)
                {
                    recoverable = false;
                    Set(ref position, duration > 0 ? duration : position, nameof(PositionSeconds));
                    Ended?.Invoke(this, EventArgs.Empty);
                }
                else PublishFailure(error ?? Error(PlaybackError.PlaybackFailed));
            }
        }
        finally { commands.Release(); }
        if (cancelled && !disposed && target.Generation == generation) await StopCoreAsync();
    }
    private void PublishFailure(Exception error)
    {
        Set(ref isPlaying, false, nameof(IsPlaying));
        Set(ref hasMedia, false, nameof(HasMedia));
        Set(ref lastErrorCode, error is PlaybackException known ? known.Code : PlaybackError.OpenFailed,
            nameof(LastErrorCode));
        if (lastErrorCode == PlaybackError.ReadRevoked) { recoverable = false; RevokeAuthority(); }
        Failed?.Invoke(this, EventArgs.Empty);
    }

    public NativePlaybackContinuation? CaptureContinuation()
    {
        RequireDispatcher();
        if (disposed || !recoverable || authority is null || authority.IsCancellationRequested ||
            ownerId is null || identity is null) return null;
        UpdatePosition();
        if (continuation is not null) return continuation.Used ? null : continuation;
        continuation = new(Scope(), position, authority.Token);
        Changed(nameof(ContinuationPending));
        return continuation;
    }
    public Task ReopenAsync(NativePlaybackContinuation capability, Uri uri,
        IReadOnlyDictionary<string, string> headers, CancellationToken token = default) =>
        DispatchAsync(() => Track(() => TrackInput(() => ReopenCoreAsync(capability,
            lifetime => AudioDecoder.OpenAsync(uri, headers, lifetime), token))));
    public Task ReopenReadAsync(NativePlaybackContinuation capability, INativeMediaReadLease lease,
        CancellationToken token = default) =>
        WithReadLeaseAsync(lease, factory => ReopenCoreAsync(capability, factory, token), token);
    private Task ReopenCoreAsync(NativePlaybackContinuation capability,
        Func<CancellationToken, Task<AudioDecoder>> open, CancellationToken token)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(continuation, capability) || capability.Used || capability.Scope != Scope())
            throw Error(PlaybackError.Superseded);
        capability.Revoked.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        capability.Used = true;
        return OpenCoreAsync(ownerId!, identity!, title, open, token, capability);
    }

    public Task StopOutputForHandoffAsync() => DispatchAsync(() => Track(async () =>
    {
        ThrowIfDisposed();
        await commands.WaitAsync();
        try { UpdatePosition(); await RetireSessionAsync(); }
        finally { commands.Release(); }
    }));
    public double PreparedHandoffDurationSeconds => prepared?.Decoder.DurationSeconds ?? 0;
    public Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
        DispatchAsync(() => Track(() => TrackInput(() =>
            PrepareCoreAsync(lifetime => AudioDecoder.OpenAsync(uri, headers, lifetime), token))));
    public Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token) =>
        WithReadLeaseAsync(lease, factory => PrepareCoreAsync(factory, token), token);

    private async Task PrepareCoreAsync(Func<CancellationToken, Task<AudioDecoder>> open, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        if (releaseFaulted) throw Error(PlaybackError.ReleaseFailed);
        if (authority is null || ownerId is null || identity is null) throw Error(PlaybackError.Superseded);
        long version = generation;
        await DiscardPreparedCoreAsync();
        long sequence = handoffSequence;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, authority.Token);
        preparing = lifetime;
        Session? candidate = null;
        try
        {
            candidate = new(await open(lifetime.Token), lifetime, token);
            UpdatePosition();
            await ConfigureOutputAsync(candidate, position, rate, lifetime.Token);
            Check(version, lifetime.Token);
            if (sequence != handoffSequence) throw Error(PlaybackError.Superseded);
            prepared = candidate;
            candidate = null;
        }
        catch
        {
            if (candidate is not null) await ReleaseOwnedAsync(candidate);
            else if (!ReferenceEquals(prepared?.Lifetime, lifetime)) { lifetime.Cancel(); lifetime.Dispose(); }
            throw;
        }
        finally { if (ReferenceEquals(preparing, lifetime)) preparing = null; }
    }

    public Task CommitHandoffAsync(Action committed, CancellationToken token) => DispatchAsync(() => Track(async () =>
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        var candidate = prepared ?? throw Error(PlaybackError.Superseded);
        long version = generation, sequence = handoffSequence;
        await commands.WaitAsync(token);
        try
        {
            Check(version, token);
            candidate.Lifetime.Token.ThrowIfCancellationRequested();
            UpdatePosition();
            // Preparation is inaudible. Reposition against the live donor clock,
            // not the earlier preparation timestamp, before retiring that donor.
            await ConfigureOutputAsync(candidate, position, rate, candidate.Lifetime.Token);
            Check(version, token);
            if (!ReferenceEquals(prepared, candidate) || sequence != handoffSequence)
                throw Error(PlaybackError.Superseded);
            Check(version, token);
            var donor = session;
            progress.Stop();
            if (donor?.Output is { } donorOutput) await donorOutput.StopRenderingAsync();
            Check(version, token);
            prepared = null;
            session = candidate;
            long publishedVersion = ++generation;
            continuation = null;
            AttachSession(candidate);
            candidate.Started = true; // Same logical occurrence, not a second song start.
            committed();
            // Provenance publication precedes lease release. Only the donor's
            // renderer had to stop before publication, not its input ownership.
            if (donor is not null) await ReleaseOwnedAsync(donor);
            Check(publishedVersion, token);
            candidate.Lifetime.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, candidate)) throw Error(PlaybackError.Superseded);
            Set(ref duration, candidate.Decoder.DurationSeconds, nameof(DurationSeconds));
            Set(ref hasMedia, true, nameof(HasMedia));
            Set(ref lastErrorCode, null, nameof(LastErrorCode));
            Changed(nameof(ContinuationPending));
            await StartOutputAsync(candidate, candidate.Lifetime.Token);
        }
        finally { commands.Release(); }
    }));
    public Task DiscardHandoffAsync() => DispatchAsync(() => Track(DiscardPreparedCoreAsync));
    private async Task DiscardPreparedCoreAsync()
    {
        ++handoffSequence;
        preparing?.Cancel();
        var old = prepared;
        prepared = null;
        if (old is null) return;
        old.Lifetime.Cancel();
        old.Decoder.Cancel();
        await commands.WaitAsync();
        try { await ReleaseOwnedAsync(old); }
        finally { commands.Release(); }
    }

    public Task StopAsync() => DispatchAsync(() => Track(StopCoreAsync));
    private async Task StopCoreAsync()
    {
        ThrowIfDisposed();
        long version = ++generation;
        var pendingReads = CancelQueuedReads();
        RevokeAuthority();
        opening?.Cancel();
        var pending = inputs.ToArray();
        try { await DiscardPreparedCoreAsync(); }
        catch (PlaybackException error) when (error.Code == PlaybackError.ReleaseFailed) { }
        await commands.WaitAsync();
        try
        {
            if (generation == version)
            {
                try { await RetireSessionAsync(); }
                catch (PlaybackException error) when (error.Code == PlaybackError.ReleaseFailed) { }
                if (generation == version && !disposed)
                {
                    Set(ref ownerId, null, nameof(OwnerId));
                    Set(ref identity, null, nameof(Identity));
                    Set(ref title, "", nameof(Title));
                    Set(ref position, 0, nameof(PositionSeconds));
                    Set(ref duration, 0, nameof(DurationSeconds));
                    Set(ref pauseRequested, false, nameof(PauseRequested));
                }
            }
        }
        finally { commands.Release(); }
        foreach (var input in pending)
            try { await input; } catch { /* The input caller observes its cancellation/failure. */ }
        await pendingReads;
        if (Volatile.Read(ref releaseFaulted)) throw Error(PlaybackError.ReleaseFailed);
    }
    private async Task RetireSessionAsync()
    {
        var old = session;
        session = null;
        progress.Stop();
        Set(ref isPlaying, false, nameof(IsPlaying));
        Set(ref hasMedia, false, nameof(HasMedia));
        if (old is not null) await ReleaseOwnedAsync(old);
    }
    private async Task ReleaseOwnedAsync(Session owned)
    {
        try { await owned.DisposeAsync(); }
        catch
        {
            releaseFaulted = true;
            Set(ref lastErrorCode, PlaybackError.ReleaseFailed, nameof(LastErrorCode));
            throw Error(PlaybackError.ReleaseFailed);
        }
    }
    private void RevokeAuthority()
    {
        recoverable = false;
        continuation = null;
        var old = authority;
        authority = null;
        old?.Cancel();
        old?.Dispose();
        Changed(nameof(ContinuationPending));
    }

    public ValueTask DisposeAsync() => new(DispatchAsync(() => disposal ??= DisposeCoreAsync()));
    private async Task DisposeCoreAsync()
    {
        disposed = true;
        ++generation;
        var pendingReads = CancelQueuedReads(closing: true);
        RevokeAuthority();
        opening?.Cancel();
        try { await DiscardPreparedCoreAsync(); }
        catch (PlaybackException error) when (error.Code == PlaybackError.ReleaseFailed) { }
        await commands.WaitAsync();
        try { await RetireSessionAsync(); }
        catch (PlaybackException error) when (error.Code == PlaybackError.ReleaseFailed) { }
        finally { commands.Release(); }
        var pending = operations.ToArray();
        foreach (var operation in pending)
            try { await operation; } catch { /* Each accepted operation retains its own failure. */ }
        await pendingReads;
        if (Volatile.Read(ref releaseFaulted)) throw Error(PlaybackError.ReleaseFailed);
    }

    private NativePlaybackScope Scope() => new(ownerId!, identity!, generation);
    private Session RequireScope(NativePlaybackScope scope) =>
        scope == Scope() && session is not null ? session : throw Error(PlaybackError.Superseded);
    private void Check(long version, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed || generation != version) throw Error(PlaybackError.Superseded);
    }
    private async Task Track(Func<Task> operation)
    {
        var task = operation();
        operations.Add(task);
        try { await task; }
        finally { operations.Remove(task); }
    }
    private async Task TrackInput(Func<Task> operation)
    {
        var task = operation();
        inputs.Add(task);
        try { await task; }
        finally { inputs.Remove(task); }
    }
    private void Post(Func<Task> operation)
    {
        if (dispatcher.IsClosing) return;
        _ = dispatcher.EnqueueAsync(async () =>
        {
            if (disposed) return;
            long version = generation;
            try { await Track(operation); }
            catch (Exception error)
            {
                if (!disposed && version == generation && error is not OperationCanceledException &&
                    error is not PlaybackException { Code: PlaybackError.Superseded })
                    PublishFailure(error);
            }
        });
    }
    private Task DispatchAsync(Func<Task> operation, CancellationToken token = default)
    {
        if (dispatcher.IsClosing)
            return Task.FromException(Error(PlaybackError.DispatcherUnavailable));
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        return dispatcher.HasThreadAccess ? operation() :
            dispatcher.EnqueueAsync(operation, DispatcherQueuePriority.Normal, token);
    }
    private Task<T> DispatchValueAsync<T>(Func<Task<T>> operation) =>
        dispatcher.HasThreadAccess ? operation() : dispatcher.EnqueueAsync(operation);
    private void RequireDispatcher()
    {
        if (!dispatcher.HasThreadAccess) throw Error(PlaybackError.WrongThread);
    }
    private void ThrowIfDisposed() { if (disposed) throw Error(PlaybackError.Disposed); }
    private static PlaybackException Error(string code) => new(code);
    private static IReadOnlyDictionary<string, string> ValidateRequest(Uri uri, IReadOnlyDictionary<string, string> headers)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("file" or "http" or "https"))
            throw Error(PlaybackError.UnsupportedUri);
        if (uri.IsFile)
        {
            if (headers is null || headers.Count != 0) throw Error(PlaybackError.UnsupportedHeaders);
            if (uri.Query.Length != 0 || uri.Fragment.Length != 0) throw Error(PlaybackError.UnsupportedUri);
            return new Dictionary<string, string>();
        }
        return NativeHttpMediaPolicy.ValidateRequest(uri, headers, allowAdaptive: false);
    }
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    private void Set<T>(ref T field, T value, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(property);
    }

    private sealed class Session(AudioDecoder decoder, CancellationTokenSource lifetime,
        CancellationToken requestCancellation) : IAsyncDisposable
    {
        internal AudioDecoder Decoder { get; } = decoder;
        internal CancellationTokenSource Lifetime { get; } = lifetime;
        internal CancellationToken RequestCancellation { get; } = requestCancellation;
        internal AudioOutputSession? Output;
        internal CancellationTokenRegistration Cancellation, Revocation;
        internal double BasePosition;
        internal bool Started;
        internal long Generation;
        private Task? release;
        public ValueTask DisposeAsync() => new(release ??= ReleaseAsync());
        private async Task ReleaseAsync()
        {
            Cancellation.Dispose();
            Revocation.Dispose();
            Lifetime.Cancel();
            Decoder.Cancel(); // Closes the input before joining decoder/render workers.
            try { if (Output is not null) await Output.DisposeAsync(); }
            finally
            {
                try { await Task.Run(Decoder.Dispose); }
                finally { Lifetime.Dispose(); }
            }
        }
    }
}

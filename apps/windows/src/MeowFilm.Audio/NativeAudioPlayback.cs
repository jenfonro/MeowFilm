using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Core.Debug;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Media;

public sealed record AudioPlaybackRate(NativePlaybackScope Scope, double RequestedRate);

/// <summary>A single-use, in-memory capability; stop, replacement and authorization cancellation revoke it.</summary>
public sealed class NativePlaybackContinuation
{
    internal NativePlaybackContinuation(NativePlaybackScope scope, double position, CancellationToken revoked)
    { Scope = scope; PositionSeconds = position; Revoked = revoked; }
    public NativePlaybackScope Scope { get; }
    public double PositionSeconds { get; }
    public CancellationToken Revoked { get; }
    internal bool Used;
}

// Music owns its decoder, PCM pipeline and output. The neutral occurrence/lease
// capabilities are shared with video, not its player, tracks or native engine.
public sealed class NativeAudioPlayback : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly OwnerDispatcher dispatcher;
    private readonly Func<CancellationToken, Task<IDisposable>>? acquireOutput;
    private readonly AudioEndpointMonitor endpointMonitor;
    private readonly SemaphoreSlim commands = new(1);
    private readonly HashSet<Task> operations = [];
    private readonly HashSet<Task> inputs = [];
    private readonly object readGate = new();
    private readonly HashSet<Task> readRequests = [];
    private CancellationTokenSource? readQueue = new();
    private readonly DispatcherQueueTimer progress;
    private readonly IDisposable? leaseRegistration;
    private Session? session, prepared;
    private CancellationTokenSource? authority, opening, preparing, deviceRebuildDebounce;
    private NativePlaybackContinuation? continuation;
    private long generation, handoffSequence, releaseFaultSerial;
    private int releaseFaulted, retiringCount;
    private bool disposed, pauseRequested, devicePaused, recoverable, hasMedia, isPlaying, isBuffering;
    private string? ownerId, identity, lastErrorCode;
    private string title = "";
    private int volume = 100;
    private double position, duration, rate = 1;
    private Task? disposal;
    private AudioKernelDebugSnapshot debugSnapshot;

    public NativeAudioPlayback(OwnerDispatcher? dispatcher = null, Func<CancellationToken, Task<IDisposable>>? acquireOutput = null)
    {
        this.dispatcher = dispatcher ?? OwnerDispatcher.Current;
        if (!this.dispatcher.HasThreadAccess || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw Error(PlaybackError.WrongThread);
        this.acquireOutput = acquireOutput;
        progress = this.dispatcher.CreateTimer(TimeSpan.FromMilliseconds(200), UpdatePosition);
        progress.Stop();
        endpointMonitor = new AudioEndpointMonitor();
        endpointMonitor.DefaultRenderDeviceChanged += id =>
            Post(() => ScheduleDeviceRebuildAsync("default_device_changed", id));
        endpointMonitor.RenderDeviceUnavailable += id =>
            Post(() => ScheduleDeviceRebuildAsync("device_unavailable", id));
        endpointMonitor.RenderDeviceAvailable += id =>
            Post(() => ScheduleDeviceRebuildAsync("device_available", id));
        endpointMonitor.Start();
        debugSnapshot = BuildDebugSnapshot();
        if (DebugLog.IsEnabled)
            leaseRegistration = DebugLeaseRegistry.Register("audio.kernel", () => Volatile.Read(ref debugSnapshot));
    }

    public string? OwnerId => ownerId;
    public string? Identity => identity;
    public string Title => title;
    public bool HasMedia => hasMedia;
    public bool IsPlaying => isPlaying;
    public bool IsBuffering => isBuffering;
    public bool CanSeek => session?.Decoder.CanSeek == true;
    public double Rate => rate;
    public double BufferedSeconds => session?.Output?.BufferedSeconds ?? 0;
    public long UnderrunCount => session?.Output?.UnderrunCount ?? 0;
    public string? ActiveDeviceId => session?.Output?.ActiveDeviceId;
    public bool PauseRequested => pauseRequested;
    public bool ContinuationPending => continuation is not null && !continuation.Revoked.IsCancellationRequested;
    public double PositionSeconds => position;
    public double DurationSeconds => duration;
    public string? LastErrorCode => lastErrorCode;
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
            var snapshot = AudioDecoder.ValidateRequest(uri, headers);
            return Track(() => TrackInput(prior => OpenCoreAsync(owner, identity, title,
                lifetime => AudioDecoder.OpenValidatedAsync(uri, snapshot, lifetime), token, priorInputs: prior)));
        });
    public Task OpenReadAsync(string owner, string identity, INativeMediaReadLease lease, string title,
        CancellationToken token = default) =>
        WithReadLeaseAsync(lease, (factory, prior) => OpenCoreAsync(owner, identity, title, factory, token, priorInputs: prior), token);

    private async Task WithReadLeaseAsync(INativeMediaReadLease lease,
        Func<Func<CancellationToken, Task<AudioDecoder>>, Task[], Task> operation, CancellationToken token)
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
                return Track(() => TrackInput(prior => operation(lifetime =>
                {
                    transferred = true;
                    return AudioDecoder.OpenReadAsync(lease, lifetime);
                }, prior)));
            }, queued.Token).ConfigureAwait(false);
        }
        finally
        {
            try { if (!transferred) await lease.DisposeAsync().ConfigureAwait(false); }
            catch
            {
                MarkReleaseFault();
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
        NativePlaybackContinuation? reopening = null, Task[]? priorInputs = null)
    {
        ThrowIfDisposed();
        TryClearDrainedReleaseFault();
        if (Volatile.Read(ref releaseFaulted) != 0) throw Error(PlaybackError.ReleaseFailed);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(mediaIdentity) || mediaTitle is null)
            throw Error(PlaybackError.InvalidRequest);
        token.ThrowIfCancellationRequested();
        CancelDeviceRebuild();
        devicePaused = false;
        if (reopening is null)
        {
            RevokeAuthority();
            authority = CancellationTokenSource.CreateLinkedTokenSource(token);
            Set(ref pauseRequested, false, nameof(PauseRequested));
            Set(ref position, 0, nameof(PositionSeconds));
            Set(ref rate, 1, nameof(Rate));
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
        Set(ref isBuffering, false, nameof(IsBuffering));
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
            if (priorInputs is { Length: > 0 })
            {
                foreach (var prior in priorInputs)
                    try { await prior; } catch { }
            }
            Check(version, lifetime.Token);
            candidate = new(await open(lifetime.Token), lifetime, token);
            if (reopening is { PositionSeconds: > 0 } && !candidate.Decoder.CanSeek)
                throw Error(PlaybackError.NotSeekable);
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
                Changed(nameof(CanSeek));
                continuation = null;
                Changed(nameof(ContinuationPending));
                if (DebugLog.IsEnabled)
                    DebugLog.Info(DebugCategories.MusicPlayback, "audiokernel", "open.ok",
                        new
                        {
                            ownerId,
                            identity,
                            title,
                            inputKind = candidate.Decoder.InputKind,
                            canSeek = candidate.Decoder.CanSeek,
                            durationSeconds = Math.Round(duration, 3),
                            basePositionSeconds = Math.Round(candidate.BasePosition, 3),
                            rate,
                            deviceId = candidate.Output?.ActiveDeviceId
                        });
                try
                {
                    await StartOutputAsync(candidate, lifetime.Token);
                    candidate = null;
                }
                catch
                {
                    if (ReferenceEquals(session, candidate))
                    {
                        await RetireSessionAsync();
                        candidate = null;
                    }
                    throw;
                }
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
                Changed(nameof(ContinuationPending));
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
            PublishDebugSnapshot();
        }
    }

    public Task PauseAsync() => DispatchAsync(() => Track(async () =>
    {
        ThrowIfDisposed();
        Set(ref pauseRequested, true, nameof(PauseRequested));
        devicePaused = false;
        await commands.WaitAsync();
        try
        {
            session?.Output?.Pause();
            UpdatePosition();
            progress.Stop();
            Set(ref isPlaying, false, nameof(IsPlaying));
            Set(ref isBuffering, false, nameof(IsBuffering));
            if (DebugLog.IsEnabled)
                DebugLog.Debug(DebugCategories.MusicPlayback, "audiokernel", "state.paused",
                    new { ownerId, identity, title, positionSeconds = Math.Round(position, 3) });
        }
        finally { commands.Release(); }
    }));

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
                await ConfigureOutputAsync(target, position, rate, target.Lifetime.Token, rewind: target.Decoder.CanSeek);
                if (!ReferenceEquals(target, session)) return;
                devicePaused = false;
                if (lastErrorCode == PlaybackError.AudioDeviceUnavailable)
                    Set(ref lastErrorCode, null, nameof(LastErrorCode));
                Set(ref position, target.BasePosition, nameof(PositionSeconds));
            }
            if (!pauseRequested) await StartOutputAsync(target, target.Lifetime.Token);
        }
        catch (PlaybackException error) when (error.Code == PlaybackError.AudioDeviceUnavailable &&
            ReferenceEquals(target, session) && !disposed)
        {
            devicePaused = !pauseRequested;
            Set(ref lastErrorCode, PlaybackError.AudioDeviceUnavailable, nameof(LastErrorCode));
            throw;
        }
        finally { commands.Release(); }
    }));

    public Task SeekAsync(double seconds) => DispatchAsync(() => Track(async () =>
    {
        ThrowIfDisposed();
        if (!double.IsFinite(seconds) || seconds < 0) throw Error(PlaybackError.InvalidSeek);
        if (session is not { } current) return;
        double clamped = duration > 0 ? Math.Min(seconds, duration) : seconds;
        await ReconfigureAsync(current, clamped, rate, CancellationToken.None, "seek");
    }));

    public Task<AudioPlaybackRate?> ReadRateAsync(CancellationToken token = default) => DispatchValueAsync(() =>
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        return Task.FromResult<AudioPlaybackRate?>(session is null ? null : new(Scope(), rate));
    }, token);
    public Task<AudioPlaybackRate> SetRateAsync(NativePlaybackScope scope, double value,
        CancellationToken token = default) => DispatchValueAsync(() => Track(async () =>
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        if (!double.IsFinite(value) || value is < 0.5 or > 3) throw Error(PlaybackError.InvalidRate);
        var target = RequireScope(scope);
        if (Math.Abs(rate - value) > .000001)
        {
            UpdatePosition();
            await ReconfigureAsync(target, position, value, token, "rate");
        }
        RequireScope(scope);
        return new AudioPlaybackRate(scope, rate);
    }), token);

    private async Task ReconfigureAsync(Session target, double seconds, double requestedRate, CancellationToken token, string reason)
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
            Set(ref isBuffering, false, nameof(IsBuffering));
            progress.Stop();
            await ConfigureOutputAsync(target, seconds, requestedRate, operation.Token, rewind: true);
            Check(version, operation.Token);
            devicePaused = false;
            if (lastErrorCode == PlaybackError.AudioDeviceUnavailable)
                Set(ref lastErrorCode, null, nameof(LastErrorCode));
            Set(ref rate, requestedRate, nameof(Rate));
            Set(ref position, target.BasePosition, nameof(PositionSeconds));
            if (DebugLog.IsEnabled)
                DebugLog.Debug(DebugCategories.MusicPlayback, "audiokernel", reason,
                    new { requestedSeconds = Math.Round(seconds, 3), actualSeconds = Math.Round(target.BasePosition, 3), rate });
            await StartOutputAsync(target, operation.Token);
        }
        catch (PlaybackException error) when (error.Code == PlaybackError.AudioDeviceUnavailable && version == generation && !disposed)
        {
            devicePaused = !pauseRequested;
            Set(ref lastErrorCode, PlaybackError.AudioDeviceUnavailable, nameof(LastErrorCode));
            throw;
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
        bool shouldSeek = rewind || (target.Output is not null && target.Decoder.CanSeek) || (seconds > 0 && target.Decoder.CanSeek);
        if (target.Output is { } old)
        {
            target.Output = null; // Fences late stopped callbacks before joining the render thread.
            old.Pause();
            await old.DisposeAsync();
        }
        double actualPosition = seconds;
        if (shouldSeek || requestedRate != target.Decoder.Rate)
        {
            await AudioWorkerQueue.RunAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                double resolved = target.Decoder.Reconfigure(requestedRate, shouldSeek ? seconds : null, token);
                if (shouldSeek) actualPosition = resolved;
            });
        }
        token.ThrowIfCancellationRequested();
        var output = new AudioOutputSession(target.Decoder);
        target.Output = output;
        output.Stopped += error => Post(() => FinishAsync(target, output, error));
        output.DeviceInvalidated += deviceId => Post(() => ScheduleDeviceRebuildAsync("output_invalidated", deviceId));
        output.BufferingChanged += state => Post(() =>
        {
            if (ReferenceEquals(session, target) && ReferenceEquals(target.Output, output))
            {
                Set(ref isBuffering, state && isPlaying, nameof(IsBuffering));
                if (DebugLog.IsEnabled)
                    DebugLog.Debug(DebugCategories.MusicPlayback, "audiokernel", "state.buffering",
                        new { buffering = isBuffering, bufferedSeconds = Math.Round(output.BufferedSeconds, 3), underrunCount = output.UnderrunCount });
            }
            return Task.CompletedTask;
        });
        try
        {
            await output.PrepareAsync(token);
            token.ThrowIfCancellationRequested();
            target.BasePosition = actualPosition;
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
            Set(ref isBuffering, output.IsBuffering, nameof(IsBuffering));
            progress.Start();
            if (DebugLog.IsEnabled)
                DebugLog.Info(DebugCategories.MusicPlayback, "audiokernel", "state.playing",
                    new
                    {
                        ownerId,
                        identity,
                        title,
                        positionSeconds = Math.Round(position, 3),
                        durationSeconds = Math.Round(duration, 3),
                        rate,
                        deviceId = output.ActiveDeviceId
                    });
            if (!target.Started)
            {
                target.Started = true;
                Started?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void CancelDeviceRebuild()
    {
        var previous = deviceRebuildDebounce;
        deviceRebuildDebounce = null;
        if (previous is not null)
        {
            try { previous.Cancel(); } catch { }
            previous.Dispose();
        }
    }

    private async Task ScheduleDeviceRebuildAsync(string reason, string? deviceId)
    {
        if (disposed || session is null) return;
        var activeId = session.Output?.ActiveDeviceId;
        if (reason == "device_available")
        {
            if (!devicePaused) return;
        }
        else if (reason == "device_unavailable")
        {
            if (session.Output is null) return;
            if (activeId is not null && deviceId is not null &&
                !string.Equals(activeId, deviceId, StringComparison.OrdinalIgnoreCase))
                return;
        }
        else if (reason == "default_device_changed" && !devicePaused &&
            activeId is not null && deviceId is not null &&
            string.Equals(activeId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        CancelDeviceRebuild();
        var debounce = new CancellationTokenSource();
        deviceRebuildDebounce = debounce;
        try
        {
            await Task.Delay(120, debounce.Token);
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (ReferenceEquals(deviceRebuildDebounce, debounce))
                deviceRebuildDebounce = null;
            debounce.Dispose();
        }
        if (disposed || session is null) return;
        await RebuildOutputForDeviceChangeAsync(reason, deviceId);
    }

    private async Task RebuildOutputForDeviceChangeAsync(string reason, string? deviceId)
    {
        long version = generation;
        await commands.WaitAsync();
        try
        {
            if (disposed || generation != version || session is not { } target || target.Lifetime.IsCancellationRequested)
                return;
            UpdatePosition();
            double resumePos = position;
            bool shouldResume = (isPlaying || devicePaused) && !pauseRequested;
            if (DebugLog.IsEnabled)
                DebugLog.Info(DebugCategories.MusicPlayback, "audiokernel", "device.rebuild.start",
                    new { reason, deviceId, positionSeconds = Math.Round(resumePos, 3), shouldResume });
            progress.Stop();
            Set(ref isPlaying, false, nameof(IsPlaying));
            Set(ref isBuffering, false, nameof(IsBuffering));
            try
            {
                await ConfigureOutputAsync(target, resumePos, rate, target.Lifetime.Token, rewind: target.Decoder.CanSeek);
                if (disposed || generation != version || !ReferenceEquals(session, target)) return;
                devicePaused = false;
                if (lastErrorCode == PlaybackError.AudioDeviceUnavailable)
                    Set(ref lastErrorCode, null, nameof(LastErrorCode));
                Set(ref position, target.BasePosition, nameof(PositionSeconds));
                if (shouldResume)
                    await StartOutputAsync(target, target.Lifetime.Token);
                if (DebugLog.IsEnabled)
                    DebugLog.Info(DebugCategories.MusicPlayback, "audiokernel", "device.rebuild.ok",
                        new
                        {
                            reason,
                            activeDeviceId = target.Output?.ActiveDeviceId,
                            positionSeconds = Math.Round(position, 3),
                            resumed = shouldResume && isPlaying
                        });
            }
            catch (PlaybackException ex) when (ex.Code == PlaybackError.AudioDeviceUnavailable)
            {
                devicePaused = shouldResume;
                Set(ref isPlaying, false, nameof(IsPlaying));
                Set(ref isBuffering, false, nameof(IsBuffering));
                Set(ref lastErrorCode, PlaybackError.AudioDeviceUnavailable, nameof(LastErrorCode));
                if (DebugLog.IsEnabled)
                    DebugLog.Warn(DebugCategories.MusicPlayback, "audiokernel", "device.unavailable",
                        new { reason, deviceId, positionSeconds = Math.Round(resumePos, 3) });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (generation == version && !disposed)
                {
                    await RetireSessionAsync();
                    if (generation == version && !disposed) PublishFailure(error);
                }
            }
        }
        finally
        {
            commands.Release();
            PublishDebugSnapshot();
        }
    }

    private void UpdatePosition()
    {
        if (session?.Output is not { } output) return;
        double next = session.BasePosition + output.MediaPositionSeconds * rate;
        if (!Set(ref position, duration > 0 ? Math.Min(duration, next) : next, nameof(PositionSeconds)))
            PublishDebugSnapshot();
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
        if (AudioEndpointMonitor.IsDeviceInvalidation(error))
        {
            if (ReferenceEquals(session, target) && target.Generation == generation && ReferenceEquals(target.Output, output))
                await ScheduleDeviceRebuildAsync("wasapi_invalidated", output?.ActiveDeviceId);
            return;
        }
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
                    if (DebugLog.IsEnabled)
                        DebugLog.Info(DebugCategories.MusicPlayback, "audiokernel", "state.ended",
                            new { ownerId, identity, title, positionSeconds = Math.Round(position, 3) });
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
        Set(ref isBuffering, false, nameof(IsBuffering));
        Set(ref hasMedia, false, nameof(HasMedia));
        Changed(nameof(CanSeek));
        Set(ref lastErrorCode, error is PlaybackException known ? known.Code : PlaybackError.OpenFailed,
            nameof(LastErrorCode));
        if (DebugLog.IsEnabled)
            DebugLog.Error(DebugCategories.MusicPlayback, "audiokernel", "playback.error",
                new { ownerId, identity, title, code = lastErrorCode }, error: error);
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
        DispatchAsync(() =>
        {
            var snapshot = AudioDecoder.ValidateRequest(uri, headers);
            return Track(() => TrackInput(prior => ReopenCoreAsync(capability,
                lifetime => AudioDecoder.OpenValidatedAsync(uri, snapshot, lifetime), token, prior)));
        });
    public Task ReopenReadAsync(NativePlaybackContinuation capability, INativeMediaReadLease lease,
        CancellationToken token = default) =>
        WithReadLeaseAsync(lease, (factory, prior) => ReopenCoreAsync(capability, factory, token, prior), token);
    private Task ReopenCoreAsync(NativePlaybackContinuation capability,
        Func<CancellationToken, Task<AudioDecoder>> open, CancellationToken token, Task[] priorInputs)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(continuation, capability) || capability.Used || capability.Scope != Scope())
            throw Error(PlaybackError.Superseded);
        capability.Revoked.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        capability.Used = true;
        return OpenCoreAsync(ownerId!, identity!, title, open, token, capability, priorInputs);
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
        DispatchAsync(() =>
        {
            var snapshot = AudioDecoder.ValidateRequest(uri, headers);
            return Track(() => TrackInput(_ =>
                PrepareCoreAsync(lifetime => AudioDecoder.OpenValidatedAsync(uri, snapshot, lifetime), token)));
        });
    public Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token) =>
        WithReadLeaseAsync(lease, (factory, _) => PrepareCoreAsync(factory, token), token);

    private async Task PrepareCoreAsync(Func<CancellationToken, Task<AudioDecoder>> open, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        TryClearDrainedReleaseFault();
        if (Volatile.Read(ref releaseFaulted) != 0) throw Error(PlaybackError.ReleaseFailed);
        if (authority is null || ownerId is null || identity is null) throw Error(PlaybackError.Superseded);
        long version = generation;
        await DiscardPreparedCoreAsync();
        long sequence = handoffSequence;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, authority.Token);
        preparing = lifetime;
        Session? candidate = null;
        try
        {
            // Open and validate the decoder silently without creating a premature WASAPI renderer.
            candidate = new(await open(lifetime.Token), lifetime, token);
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
            var donor = session;
            bool donorWasPlaying = isPlaying;
            // Freeze donor clock before configuring candidate so handoff seeks once to the exact final position.
            donor?.Output?.Pause();
            UpdatePosition();
            double handoffPosition = position;
            try
            {
                if (handoffPosition > 0 && !candidate.Decoder.CanSeek)
                    throw Error(PlaybackError.NotSeekable);
                await ConfigureOutputAsync(candidate, handoffPosition, rate, candidate.Lifetime.Token,
                    rewind: handoffPosition > 0);
                Check(version, token);
                if (!ReferenceEquals(prepared, candidate) || sequence != handoffSequence)
                    throw Error(PlaybackError.Superseded);
            }
            catch
            {
                if (donorWasPlaying && !pauseRequested && ReferenceEquals(session, donor) &&
                    donor?.Output is { } activeDonor && !donor.Lifetime.IsCancellationRequested)
                {
                    try { await activeDonor.PlayAsync(donor.Lifetime.Token); }
                    catch { }
                }
                throw;
            }
            progress.Stop();
            Set(ref isPlaying, false, nameof(IsPlaying));
            Set(ref isBuffering, false, nameof(IsBuffering));
            if (donor?.Output is { } donorOutput)
            {
                donor.Output = null;
                await donorOutput.StopRenderingAsync();
            }
            Check(version, token);
            prepared = null;
            session = candidate;
            long publishedVersion = ++generation;
            continuation = null;
            devicePaused = false;
            AttachSession(candidate);
            candidate.Started = true; // Same logical occurrence, not a second song start.
            committed();
            // Provenance publication precedes lease release. Only the donor's
            // renderer had to stop before publication, not its input ownership.
            if (donor is not null) await ReleaseOwnedAsync(donor);
            try
            {
                Check(publishedVersion, token);
                candidate.Lifetime.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(session, candidate)) throw Error(PlaybackError.Superseded);
                Set(ref duration, candidate.Decoder.DurationSeconds, nameof(DurationSeconds));
                Set(ref position, candidate.BasePosition, nameof(PositionSeconds));
                Set(ref hasMedia, true, nameof(HasMedia));
                Changed(nameof(CanSeek));
                Set(ref lastErrorCode, null, nameof(LastErrorCode));
                Changed(nameof(ContinuationPending));
                if (DebugLog.IsEnabled)
                    DebugLog.Info(DebugCategories.MusicPlayback, "audiokernel", "handoff.commit",
                        new { handoffPositionSeconds = Math.Round(handoffPosition, 3), actualPositionSeconds = Math.Round(candidate.BasePosition, 3), inputKind = candidate.Decoder.InputKind });
                await StartOutputAsync(candidate, candidate.Lifetime.Token);
            }
            catch (Exception error)
            {
                if (ReferenceEquals(session, candidate)) await RetireSessionAsync();
                if (publishedVersion == generation && !disposed && error is not OperationCanceledException &&
                    !token.IsCancellationRequested && authority?.IsCancellationRequested == false)
                    PublishFailure(error);
                throw;
            }
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
        long faultBefore = Volatile.Read(ref releaseFaultSerial);
        long version = ++generation;
        CancelDeviceRebuild();
        devicePaused = false;
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
                    if (DebugLog.IsEnabled)
                        DebugLog.Debug(DebugCategories.MusicPlayback, "audiokernel", "state.stopped");
                }
            }
        }
        finally { commands.Release(); }
        foreach (var input in pending)
            try { await input; } catch { /* The input caller observes its cancellation/failure. */ }
        await pendingReads;
        bool faultedNow = Volatile.Read(ref releaseFaultSerial) != faultBefore;
        TryClearDrainedReleaseFault();
        PublishDebugSnapshot();
        if (faultedNow) throw Error(PlaybackError.ReleaseFailed);
    }
    private async Task RetireSessionAsync()
    {
        var old = session;
        session = null;
        progress.Stop();
        Set(ref isPlaying, false, nameof(IsPlaying));
        Set(ref isBuffering, false, nameof(IsBuffering));
        Set(ref hasMedia, false, nameof(HasMedia));
        Changed(nameof(CanSeek));
        if (old is not null) await ReleaseOwnedAsync(old);
    }
    private async Task ReleaseOwnedAsync(Session owned)
    {
        Interlocked.Increment(ref retiringCount);
        try { await owned.DisposeAsync(); }
        catch (Exception error)
        {
            MarkReleaseFault();
            Set(ref lastErrorCode, PlaybackError.ReleaseFailed, nameof(LastErrorCode));
            if (DebugLog.IsEnabled)
                DebugLog.Error(DebugCategories.MusicPlayback, "audiokernel", "release.failed", error: error);
            throw Error(PlaybackError.ReleaseFailed);
        }
        finally
        {
            Interlocked.Decrement(ref retiringCount);
            PublishDebugSnapshot();
        }
    }
    private void MarkReleaseFault()
    {
        Volatile.Write(ref releaseFaulted, 1);
        Interlocked.Increment(ref releaseFaultSerial);
    }
    private void TryClearDrainedReleaseFault()
    {
        if (Volatile.Read(ref releaseFaulted) != 0 &&
            Volatile.Read(ref retiringCount) == 0 &&
            session is null && prepared is null)
        {
            Volatile.Write(ref releaseFaulted, 0);
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
        long faultBefore = Volatile.Read(ref releaseFaultSerial);
        leaseRegistration?.Dispose();
        disposed = true;
        ++generation;
        CancelDeviceRebuild();
        endpointMonitor.Dispose();
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
        PublishDebugSnapshot();
        if (Volatile.Read(ref releaseFaultSerial) != faultBefore || Volatile.Read(ref releaseFaulted) != 0)
            throw Error(PlaybackError.ReleaseFailed);
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
    private async Task<T> Track<T>(Func<Task<T>> operation)
    {
        var task = operation();
        operations.Add(task);
        try { return await task; }
        finally { operations.Remove(task); }
    }
    private async Task TrackInput(Func<Task[], Task> operation)
    {
        var prior = inputs.ToArray();
        var task = operation(prior);
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
    private Task<T> DispatchValueAsync<T>(Func<Task<T>> operation, CancellationToken token = default)
    {
        if (dispatcher.IsClosing)
            return Task.FromException<T>(Error(PlaybackError.DispatcherUnavailable));
        if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
        return dispatcher.HasThreadAccess ? operation() :
            dispatcher.EnqueueAsync(operation, DispatcherQueuePriority.Normal, token);
    }
    private void RequireDispatcher()
    {
        if (!dispatcher.HasThreadAccess) throw Error(PlaybackError.WrongThread);
    }
    private void ThrowIfDisposed() { if (disposed) throw Error(PlaybackError.Disposed); }
    private static PlaybackException Error(string code) => new(code);
    private void Changed(string property)
    {
        PublishDebugSnapshot();
        PropertyChanged?.Invoke(this, new(property));
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(property);
        return true;
    }

    private void PublishDebugSnapshot()
    {
        if (leaseRegistration is not null)
            Volatile.Write(ref debugSnapshot, BuildDebugSnapshot());
    }

    private AudioKernelDebugSnapshot BuildDebugSnapshot() => new(
        HasMedia: hasMedia,
        IsPlaying: isPlaying,
        IsBuffering: isBuffering,
        PauseRequested: pauseRequested,
        DevicePaused: devicePaused,
        CanSeek: session?.Decoder.CanSeek == true,
        Rate: rate,
        InputKind: session?.Decoder.InputKind,
        DeviceId: session?.Output?.ActiveDeviceId,
        OwnerId: ownerId,
        Identity: identity,
        Title: title,
        PositionSeconds: Math.Round(position, 3),
        DurationSeconds: Math.Round(duration, 3),
        BufferedSeconds: Math.Round(session?.Output?.BufferedSeconds ?? 0, 3),
        UnderrunCount: session?.Output?.UnderrunCount ?? 0,
        Volume: volume,
        LastErrorCode: lastErrorCode,
        Generation: generation,
        InFlightOps: operations.Count,
        InFlightInputs: inputs.Count,
        RetiringCount: Volatile.Read(ref retiringCount),
        HasPrepared: prepared is not null,
        ReleaseFaulted: Volatile.Read(ref releaseFaulted) != 0,
        Disposed: disposed);

    private sealed record AudioKernelDebugSnapshot(
        bool HasMedia,
        bool IsPlaying,
        bool IsBuffering,
        bool PauseRequested,
        bool DevicePaused,
        bool CanSeek,
        double Rate,
        string? InputKind,
        string? DeviceId,
        string? OwnerId,
        string? Identity,
        string Title,
        double PositionSeconds,
        double DurationSeconds,
        double BufferedSeconds,
        long UnderrunCount,
        int Volume,
        string? LastErrorCode,
        long Generation,
        int InFlightOps,
        int InFlightInputs,
        int RetiringCount,
        bool HasPrepared,
        bool ReleaseFaulted,
        bool Disposed);

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
                try { await AudioWorkerQueue.RunAsync(Decoder.Dispose); }
                finally { Lifetime.Dispose(); }
            }
        }
    }
}

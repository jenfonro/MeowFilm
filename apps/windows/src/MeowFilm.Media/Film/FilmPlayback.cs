using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MeowFilm.Core.Debug;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Media;

/// <summary>Successful native render/Present submissions and active output mode flags.</summary>
public sealed record FilmOutputObservation(NativePlaybackScope Scope, ulong SubmittedPictures,
    bool NativeHdr, bool ToneMapped, bool DolbyVision,
    bool HardwareDecoding, bool AudioDegraded);

/// <summary>
/// Independent film owner. All blocking native mfk_* operations execute off the UI STA
/// thread; the UI thread handles only SwapChainPanel binding and immutable property projections.
/// </summary>
public sealed partial class FilmPlayback : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly OwnerDispatcher dispatcher;
    private readonly Func<CancellationToken, Task<IDisposable>>? acquireOutput;
    private readonly FilmVideoSurface surface;
    private readonly DispatcherQueueTimer timer;
    private readonly IDisposable? leaseRegistration;
    private readonly IDisposable? authorityLeaseRegistration;
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly object ownership = new();
    private readonly object debugStateGate = new();
    private readonly HashSet<Session> sessions = [];

    private Session? pending;
    private volatile Session? current;
    private Session? surfaceOwner;
    private FilmKernelNative.Surface? presentedSurface;
    private long presentedDisplayEpoch = -1;
    private bool updatingSurface;
    private long generation;
    private long admittedGeneration;
    private bool disposing, polling;
    private Task? disposal;

    // Coalesced non-blocking volume/mute pump (does not stall command gate or terminate playback).
    private readonly object audioControlGate = new();
    private bool audioControlPumping;
    private int desiredVolume = 100;
    private bool desiredMuted;

    private int volume = 100;
    private bool muted;
    private NativePlaybackScope? scope;
    private bool hasOutput, buffering, paused, canSeek;
    private double position, duration, bufferedUntil, rate = 1;
    private int videoWidth, videoHeight;
    private string? lastError;
    private FilmOutputObservation? output;

    // Lock-protected snapshot for cross-thread DebugLeaseRegistry reads.
    private object debugSnapshot = new { activeSessions = 0, hasOutput = false };

    public FilmPlayback(OwnerDispatcher? dispatcher = null,
        Func<CancellationToken, Task<IDisposable>>? acquireOutput = null)
    {
        this.acquireOutput = acquireOutput;
        this.dispatcher = dispatcher ?? OwnerDispatcher.Current;
        if (!this.dispatcher.HasThreadAccess || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new PlaybackException(PlaybackError.WrongThread);
        surface = new FilmVideoSurface();
        surface.TargetChanged += SurfaceChanged;
        timer = this.dispatcher.CreateTimer(TimeSpan.FromMilliseconds(100), Poll);
        timer.Stop();
        if (DebugLog.IsEnabled)
        {
            UpdateDebugSnapshot();
            leaseRegistration = DebugLeaseRegistry.Register("film.kernel", () =>
            {
                lock (debugStateGate) return debugSnapshot;
            });
            authorityLeaseRegistration = DebugLeaseRegistry.Register("film.media_authority", () => new
            {
                activeAuthorities = RuntimeMediaAuthority.ActiveAuthorityCount,
                activeLeases = RuntimeMediaAuthority.ActiveLeaseCount,
                httpTransports = FFmpegHttpTransport.ActiveTransportCount,
                httpReaders = FFmpegHttpTransport.ActiveReaderCount
            });
        }
    }

    public FilmVideoSurface VideoView => surface;
    public NativePlaybackScope? Scope => scope;
    public string? Identity => scope?.Identity;
    public bool HasOutput => hasOutput;
    public bool IsBuffering => buffering;
    public bool IsPaused => paused;
    public bool CanSeek => canSeek;
    public double PositionSeconds => position;
    public double DurationSeconds => duration;
    public double BufferedUntilSeconds => bufferedUntil;
    public double Rate => rate;
    public int VideoWidth => videoWidth;
    public int VideoHeight => videoHeight;
    public string? LastErrorCode => lastError;
    public FilmOutputObservation? Output => output;

    public int Volume
    {
        get => volume;
        set
        {
            dispatcher.VerifyAccess(); ThrowIfClosed();
            int clamped = Math.Clamp(value, 0, 100);
            if (!Set(ref volume, clamped)) return;
            EnqueueAudioGainUpdate(clamped, muted);
        }
    }

    public bool IsMuted
    {
        get => muted;
        set
        {
            dispatcher.VerifyAccess(); ThrowIfClosed();
            if (!Set(ref muted, value)) return;
            EnqueueAudioGainUpdate(volume, value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Failed;
    public event EventHandler? Ended;

    public async Task PrepareAsync(RuntimeMediaPlaybackIntent intent, Uri address, NativeVideoInput input,
        INativeMediaResourceLease? lease, CancellationToken token = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        Session? session = null;
        byte[] keys = [];
        byte[]? defaultKey = null;
        try
        {
            if (input is null) throw new PlaybackException(PlaybackError.InvalidRequest);
            var format = input.Format;
            (keys, defaultKey) = input.Take();
            input.Dispose();
            if (intent is null || intent.OwnerId != "film" || string.IsNullOrWhiteSpace(intent.Identity) ||
                !double.IsFinite(intent.Position) || intent.Position < 0)
                throw new PlaybackException(PlaybackError.InvalidRequest);
            token.ThrowIfCancellationRequested();
            if (lease is not null)
            {
                if (!lease.Revoked.CanBeCanceled || lease.Address != address)
                    throw new PlaybackException(PlaybackError.InvalidResourceLease);
            }
            var requestHeaders = MergeHeaders(lease?.Headers, headers);
            session = new Session(intent, Interlocked.Increment(ref generation), address, format,
                keys, defaultKey, requestHeaders, lease, token);
            keys = []; defaultKey = null; lease = null;
            lock (ownership)
            {
                if (disposing) throw new PlaybackException(PlaybackError.Disposed);
                sessions.Add(session);
            }
            var candidate = session;
            await Dispatch(async () =>
            {
                ThrowIfClosed();
                if (candidate.Scope.Generation < admittedGeneration)
                    throw new PlaybackException(PlaybackError.Superseded);
                admittedGeneration = candidate.Scope.Generation;
                var preceding = pending;
                pending = candidate;
                preceding?.Cancel();
                await commands.WaitAsync();
                try
                {
                    if (preceding is not null) await RetireAsync(preceding);
                    RequirePending(candidate);
                    var target = surface.DisplayWindow;
                    var snapshot = await Task.Run(() =>
                    {
                        candidate.Create();
                        int rc = FilmKernelNative.Prepare(candidate.Handle, intent.Position, target);
                        if (rc != 0) throw new PlaybackException(candidate.ResolveError(rc));
                        return FilmKernelNative.Read(candidate.Handle);
                    });
                    RequirePending(candidate);
                    if (snapshot.State != 1)
                        throw new PlaybackException(candidate.ResolveError(snapshot.Error));
                }
                finally { commands.Release(); }
            });
        }
        catch (Exception error)
        {
            if (session is not null)
            {
                session.Cancel();
                if (!dispatcher.IsClosing)
                {
                    try
                    {
                        await Dispatch(() =>
                        {
                            if (ReferenceEquals(pending, session)) pending = null;
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (InvalidOperationException) when (dispatcher.IsClosing) { }
                }
                await commands.WaitAsync().ConfigureAwait(false);
                try { await RetireAsync(session).ConfigureAwait(false); }
                finally { commands.Release(); }
            }
            if (error is OperationCanceledException) throw;
            throw new PlaybackException(error is PlaybackException known ? known.Code : PlaybackError.OpenFailed);
        }
        finally
        {
            input?.Dispose();
            if (lease is not null)
            {
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch (Exception leaseError)
                {
                    if (DebugLog.IsEnabled)
                        DebugLog.Warn(DebugCategories.FilmPlayback, "filmkernel", "prepare.lease_dispose_failed", error: leaseError);
                }
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> MergeHeaders(
        IReadOnlyDictionary<string, string>? primary,
        IReadOnlyDictionary<string, string>? secondary)
    {
        if (primary is null or { Count: 0 }) return secondary ?? EmptyHeaders;
        if (secondary is null or { Count: 0 }) return primary;
        var merged = new Dictionary<string, string>(primary.Count + secondary.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in primary) merged[k] = v;
        foreach (var (k, v) in secondary) merged[k] = v;
        return merged;
    }

    public Task WaitForTargetAsync(CancellationToken token = default) => Dispatch(async () =>
    {
        ThrowIfClosed();
        await surface.WaitForTargetAsync(token);
    });

    /// <summary>False only defers a live candidate on business admission; retired inputs fail as superseded.</summary>
    public Task<bool> TryCommitAsync(RuntimeMediaPlaybackIntent intent, Func<bool> canCommit,
        Action committed, CancellationToken token = default) => Dispatch(async () =>
    {
        ThrowIfClosed();
        var session = pending;
        if (session is null || !ReferenceEquals(session.Intent, intent))
            throw new PlaybackException(PlaybackError.Superseded);

        // Wait for a visible surface target strictly OUTSIDE the commands semaphore so
        // minimizing/hiding the window never freezes playback controls or polling.
        using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token, token))
        {
            await surface.WaitForTargetAsync(waitCts.Token);
        }
        ThrowIfClosed();

        await commands.WaitAsync();
        if (!ReferenceEquals(pending, session))
        {
            try
            {
                if (!ReferenceEquals(current, session)) await RetireAsync(session);
                throw new PlaybackException(PlaybackError.Superseded);
            }
            finally { commands.Release(); }
        }

        var donor = current;
        bool donorPaused = paused;
        bool silenced = false;
        bool detached = false;
        bool published = false;
        IDisposable? permit = null;
        using var cancellation = token.Register(session.Cancel);
        try
        {
            RequirePending(session); token.ThrowIfCancellationRequested();
            // If PreserveFrom was requested but the donor already ended/failed (donor == null),
            // proceed with committing this prepared candidate instead of discarding it.
            // Only reject if a different live donor has replaced it.
            if (intent.PreserveFrom is { } preserve && donor is not null && donor.Scope != preserve)
                throw new PlaybackException(PlaybackError.Superseded);
            if (!canCommit()) return false;

            // Pre-align candidate before acquiring the global playback arbitration lock so
            // network Seek never blocks cross-domain playback handoff.
            if (intent.PreserveFrom is not null && donor is not null)
            {
                double align = await Task.Run(() => FilmKernelNative.Read(donor.Handle).Position);
                await Task.Run(() =>
                {
                    var candSnap = FilmKernelNative.Read(session.Handle);
                    if (candSnap.Seekable != 0 && align > 0 && (candSnap.Duration <= 0 || align < candSnap.Duration))
                    {
                        int rc = FilmKernelNative.Seek(session.Handle, align);
                        if (rc != 0 && rc != 5) // Tolerate non-fatal seek mismatch on short/edge streams
                            throw new PlaybackException(session.ResolveError(rc));
                    }
                });
            }
            RequirePending(session); token.ThrowIfCancellationRequested();
            donor = current;
            if (intent.PreserveFrom is { } expectedBeforePermit && donor is not null && donor.Scope != expectedBeforePermit)
                throw new PlaybackException(PlaybackError.Superseded);
            if (!canCommit()) return false;

            permit = await AcquireOutputAsync(session.Token);
            RequirePending(session); token.ThrowIfCancellationRequested();
            donor = current;
            if (intent.PreserveFrom is { } expected && donor is not null && donor.Scope != expected)
                throw new PlaybackException(PlaybackError.Superseded);
            if (!canCommit()) return false;

            // Re-verify visible surface after async admission gap.
            if (surface.Describe() is not { DisplayWindow: not 0, Visible: 1 })
                return false;

            donorPaused = paused;
            if (donor is not null)
            {
                await Task.Run(() => FilmKernelNative.Check(FilmKernelNative.Pause(donor.Handle, 1)));
                silenced = true;
            }

            double initialVol = volume / 100d;
            int initialMute = muted ? 1 : 0;
            await Task.Run(() =>
            {
                FilmKernelNative.Check(FilmKernelNative.Volume(session.Handle, initialVol));
                FilmKernelNative.Check(FilmKernelNative.Mute(session.Handle, initialMute));
            });

            if (donor is not null)
            {
                await DetachSurfaceAsync(donor);
                detached = true;
            }

            bool startPaused = intent.PreserveFrom is not null && donor is not null ? donorPaused : intent.Paused;
            await AttachSurfaceAsync(session);
            var (snapshot, initialTracks, initialChapters) = await Task.Run(() =>
            {
                int rc = FilmKernelNative.Start(session.Handle, startPaused ? 1 : 0);
                if (rc != 0) throw new PlaybackException(session.ResolveError(rc));
                var snap = FilmKernelNative.Read(session.Handle);
                if (snap.Error != 0 || snap.State is not (2 or 3 or 4) ||
                    snap.SubmittedPictures == 0 || startPaused && snap.State != 3)
                    throw new PlaybackException(session.ResolveError(snap.Error));
                return (snap, CaptureTracks(session), CaptureChapters(session));
            });

            RequirePending(session); token.ThrowIfCancellationRequested();
            current = session; pending = null;
            published = true;
            Set(ref tracks, initialTracks, nameof(Tracks));
            Set(ref chapters, initialChapters, nameof(Chapters));
            Set(ref rate, 1, nameof(Rate));
            Set(ref scope, session.Scope, nameof(Scope)); Changed(nameof(Identity));
            Set(ref hasOutput, true, nameof(HasOutput)); Set(ref lastError, null, nameof(LastErrorCode));
            Publish(snapshot);
            timer.Start();
            if (DebugLog.IsEnabled)
                DebugLog.Info(DebugCategories.FilmPlayback, "filmkernel", "session.started",
                    new
                    {
                        identity = session.Scope.Identity,
                        startPaused,
                        positionSeconds = Math.Round(position, 3),
                        durationSeconds = Math.Round(duration, 3),
                        width = videoWidth,
                        height = videoHeight,
                        nativeHdr = snapshot.NativeHdr != 0,
                        toneMapped = snapshot.ToneMapped != 0,
                        dolbyVision = snapshot.DolbyVision != 0,
                        hardwareDecoding = snapshot.HardwareDecoding != 0,
                        audioDegraded = snapshot.AudioDegraded != 0
                    });
            committed();
            // Release arbitration permit immediately after commit, before retiring donor IO/native resources.
            permit?.Dispose();
            permit = null;
            return true;
        }
        catch (Exception error)
        {
            if (!published && !ReferenceEquals(pending, session))
            {
                if (!ReferenceEquals(current, session)) await RetireAsync(session);
                throw Safe(error);
            }
            if (ReferenceEquals(current, session))
            {
                Report(error);
                current = null; ClearOutput();
            }
            if (ReferenceEquals(pending, session)) pending = null;
            await RetireAsync(session);
            throw Safe(error);
        }
        finally
        {
            try
            {
                if (published && donor is not null)
                {
                    await RetireAsync(donor);
                }
                else if (silenced && ReferenceEquals(current, donor) && donor is not null)
                {
                    if (donor.Token.IsCancellationRequested)
                    {
                        Report(new PlaybackException(PlaybackError.ResourceRevoked));
                        current = null; ClearOutput();
                        await RetireAsync(donor);
                    }
                    else
                    {
                        try
                        {
                            if (detached)
                            {
                                await AttachSurfaceAsync(donor);
                                var target = surface.Describe();
                                await Task.Run(() => FilmKernelNative.Check(FilmKernelNative.UpdateSurface(donor.Handle, ref target)));
                            }
                            await Task.Run(() => FilmKernelNative.Check(FilmKernelNative.Pause(donor.Handle, donorPaused ? 1 : 0)));
                        }
                        catch (Exception rollbackError)
                        {
                            Report(rollbackError);
                            current = null; ClearOutput();
                            await RetireAsync(donor);
                        }
                    }
                }
            }
            finally
            {
                try { Interlocked.Exchange(ref permit, null)?.Dispose(); }
                finally { commands.Release(); }
            }
        }
    });

    private async Task<IDisposable?> AcquireOutputAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (acquireOutput is null) return null;
        commands.Release();
        try { return await acquireOutput(token); }
        finally { await commands.WaitAsync(); }
    }

    public Task DiscardAsync(RuntimeMediaPlaybackIntent intent) => Dispatch(async () =>
    {
        var session = pending;
        if (session is null || !ReferenceEquals(session.Intent, intent)) return;
        pending = null; session.Cancel();
        await commands.WaitAsync();
        try { await RetireAsync(session); }
        finally { commands.Release(); }
    });

    public Task SetPausedAsync(NativePlaybackScope scope, bool value) => Command(scope, session =>
    {
        FilmKernelNative.Check(FilmKernelNative.Pause(session.Handle, value ? 1 : 0));
        if (DebugLog.IsEnabled)
            DebugLog.Debug(DebugCategories.FilmPlayback, "filmkernel", value ? "state.paused" : "state.resumed",
                new { identity = session.Scope.Identity });
    });

    public Task SeekAsync(NativePlaybackScope scope, double seconds) => Command(scope, session =>
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new PlaybackException(PlaybackError.InvalidSeek);
        int rc = FilmKernelNative.Seek(session.Handle, seconds);
        if (rc != 0) throw new PlaybackException(session.ResolveError(rc));
    });

    public Task RateAsync(NativePlaybackScope expected, double value) => Dispatch(async () =>
    {
        await Command(expected, session =>
        {
            if (!double.IsFinite(value) || value < .25 || value > 4) throw new PlaybackException(PlaybackError.InvalidRate);
            FilmKernelNative.Check(FilmKernelNative.Rate(session.Handle, value));
        });
        if (current is { } active && active.Scope == expected)
            Set(ref rate, value, nameof(Rate));
    });

    public Task AspectAsync(NativePlaybackScope scope, string? aspect) => Command(scope, session =>
    {
        double ratio = 0;
        if (!string.IsNullOrEmpty(aspect))
        {
            var parts = aspect.Split(':');
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
                !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0 || !double.IsFinite(ratio = w / h))
                throw new PlaybackException(PlaybackError.InvalidRequest);
        }
        FilmKernelNative.Check(FilmKernelNative.Aspect(session.Handle, ratio));
    });

    public Task SetMutedAsync(NativePlaybackScope expected, bool value) => Dispatch(() =>
    {
        ThrowIfClosed();
        if (current is null || current.Scope != expected) throw new PlaybackException(PlaybackError.Superseded);
        IsMuted = value;
        return Task.CompletedTask;
    });

    private Task Command(NativePlaybackScope expected, Action<Session> action) => Dispatch(async () =>
    {
        ThrowIfClosed();
        var session = current;
        if (session is null || session.Scope != expected) throw new PlaybackException(PlaybackError.Superseded);
        await commands.WaitAsync();
        try
        {
            if (!ReferenceEquals(current, session)) throw new PlaybackException(PlaybackError.Superseded);
            session.Token.ThrowIfCancellationRequested();
            var snapshot = await Task.Run(() =>
            {
                action(session);
                return FilmKernelNative.Read(session.Handle);
            });
            session.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(current, session) || disposing)
                throw new PlaybackException(PlaybackError.Superseded);
            Publish(snapshot);
        }
        catch (Exception error)
        {
            await RetireCancelledAsync(session);
            throw Safe(error);
        }
        finally { commands.Release(); }
    });

    public Task StopAsync(NativePlaybackScope expected) => Dispatch(async () =>
    {
        var session = current;
        if (session is null || session.Scope != expected) return;
        current = null; ClearOutput(); session.Cancel();
        var candidate = pending; pending = null; candidate?.Cancel();
        await commands.WaitAsync();
        try
        {
            await Task.WhenAll(RetireAsync(session),
                candidate is null ? Task.CompletedTask : RetireAsync(candidate));
        }
        finally { commands.Release(); }
    });

    public Task StopAsync() => Dispatch(async () =>
    {
        Session[] owned;
        lock (ownership) owned = sessions.ToArray();
        current = null; pending = null; ClearOutput();
        foreach (var session in owned) session.Cancel();
        await commands.WaitAsync();
        try { await Task.WhenAll(owned.Select(RetireAsync)); }
        finally { commands.Release(); }
        if (DebugLog.IsEnabled && owned.Length > 0)
            DebugLog.Debug(DebugCategories.FilmPlayback, "filmkernel", "state.stopped", new { retiredSessions = owned.Length });
    });

    private void EnqueueAudioGainUpdate(int targetVol, bool targetMuted)
    {
        lock (audioControlGate)
        {
            desiredVolume = targetVol;
            desiredMuted = targetMuted;
            if (audioControlPumping) return;
            audioControlPumping = true;
        }
        _ = PumpAudioGainAsync();
    }

    private async Task PumpAudioGainAsync()
    {
        try
        {
            while (!disposing)
            {
                int vol; bool isMuted;
                lock (audioControlGate)
                {
                    vol = desiredVolume;
                    isMuted = desiredMuted;
                }
                var session = current;
                if (session is not null && !session.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Run(() =>
                        {
                            session.TryWithHandle(handle =>
                            {
                                FilmKernelNative.Volume(handle, vol / 100d);
                                FilmKernelNative.Mute(handle, isMuted ? 1 : 0);
                            });
                        }).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        // Non-fatal gain update failure must never terminate a healthy video session.
                        if (DebugLog.IsEnabled)
                            DebugLog.Warn(DebugCategories.FilmPlayback, "filmkernel", "audio.gain_update_failed", error: error);
                    }
                }
                lock (audioControlGate)
                {
                    if (desiredVolume == vol && desiredMuted == isMuted)
                    {
                        audioControlPumping = false;
                        return;
                    }
                }
            }
        }
        finally
        {
            lock (audioControlGate) audioControlPumping = false;
        }
    }

    private void Poll() => _ = PollAsync();

    private async Task PollAsync()
    {
        try { await RefreshCoreAsync(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) when (dispatcher.IsClosing) { }
        catch (Exception error)
        {
            if (DebugLog.IsEnabled)
                DebugLog.Error(DebugCategories.FilmPlayback, "filmkernel", "poll.unhandled", error: error);
        }
    }

    private async Task RefreshCoreAsync()
    {
        if (polling || disposing || current is not { } session || !commands.Wait(0)) return;
        polling = true;
        try
        {
            if (session.Token.IsCancellationRequested)
                throw new PlaybackException(PlaybackError.ResourceRevoked);
            var snapshot = await Task.Run(() => FilmKernelNative.Read(session.Handle));
            if (!ReferenceEquals(current, session) || disposing) return;
            if (snapshot.Error != 0 || snapshot.State == 6)
                throw new PlaybackException(session.ResolveError(snapshot.Error));
            Publish(snapshot);
            if (snapshot.State == 5 && !session.EndNotified)
            {
                session.EndNotified = true;
                Set(ref hasOutput, false, nameof(HasOutput));
                await RetireAsync(session);
                if (ReferenceEquals(current, session)) current = null;
                if (!disposing && scope == session.Scope)
                {
                    try { Ended?.Invoke(this, EventArgs.Empty); }
                    finally { ClearOutput(); }
                }
            }
        }
        catch (Exception error)
        {
            if (ReferenceEquals(current, session))
                Set(ref hasOutput, false, nameof(HasOutput));
            await RetireAsync(session);
            if (ReferenceEquals(current, session)) current = null;
            if (!disposing && scope == session.Scope)
            {
                try { Report(error); }
                finally { ClearOutput(); }
            }
        }
        finally { polling = false; commands.Release(); }
    }

    private void Publish(FilmKernelNative.Snapshot value)
    {
        Set(ref output, scope is { } currentScope && value.SubmittedPictures > 0 ?
            new FilmOutputObservation(currentScope, value.SubmittedPictures,
                value.NativeHdr != 0, value.ToneMapped != 0,
                value.DolbyVision != 0, value.HardwareDecoding != 0, value.AudioDegraded != 0) : null, nameof(Output));
        Set(ref position, double.IsFinite(value.Position) ? Math.Max(0, value.Position) : 0, nameof(PositionSeconds));
        Set(ref duration, double.IsFinite(value.Duration) ? Math.Max(0, value.Duration) : 0, nameof(DurationSeconds));
        Set(ref bufferedUntil, double.IsFinite(value.BufferedUntil) ? Math.Max(0, value.BufferedUntil) : 0, nameof(BufferedUntilSeconds));
        Set(ref videoWidth, Math.Max(0, value.Width), nameof(VideoWidth));
        Set(ref videoHeight, Math.Max(0, value.Height), nameof(VideoHeight));
        Set(ref canSeek, value.Seekable != 0, nameof(CanSeek));
        Set(ref buffering, value.State == 4, nameof(IsBuffering));
        Set(ref paused, value.State == 3, nameof(IsPaused));
        UpdateDebugSnapshot();
    }

    private void ClearOutput()
    {
        timer.Stop();
        Set(ref output, null, nameof(Output));
        Set(ref tracks, null, nameof(Tracks));
        Set(ref chapters, [], nameof(Chapters));
        Set(ref lastDiagnostics, null, nameof(LastDiagnostics));
        Set(ref scope, null, nameof(Scope)); Changed(nameof(Identity));
        Set(ref hasOutput, false, nameof(HasOutput));
        Set(ref buffering, false, nameof(IsBuffering));
        Set(ref paused, false, nameof(IsPaused));
        Set(ref canSeek, false, nameof(CanSeek));
        Set(ref position, 0, nameof(PositionSeconds));
        Set(ref duration, 0, nameof(DurationSeconds));
        Set(ref bufferedUntil, 0, nameof(BufferedUntilSeconds));
        Set(ref rate, 1, nameof(Rate));
        Set(ref videoWidth, 0, nameof(VideoWidth));
        Set(ref videoHeight, 0, nameof(VideoHeight));
        UpdateDebugSnapshot();
    }

    private void UpdateDebugSnapshot()
    {
        if (!DebugLog.IsEnabled) return;
        int sessionCount;
        lock (ownership) sessionCount = sessions.Count;
        var snap = new
        {
            activeSessions = sessionCount,
            hasOutput,
            buffering,
            paused,
            canSeek,
            identity = scope?.Identity,
            positionSeconds = Math.Round(position, 3),
            durationSeconds = Math.Round(duration, 3),
            bufferedUntilSeconds = Math.Round(bufferedUntil, 3),
            videoWidth,
            videoHeight,
            volume,
            muted,
            nativeHdr = output?.NativeHdr ?? false,
            toneMapped = output?.ToneMapped ?? false,
            dolbyVision = output?.DolbyVision ?? false,
            hardwareDecoding = output?.HardwareDecoding ?? false,
            audioDegraded = output?.AudioDegraded ?? false,
            droppedFrames = lastDiagnostics?.DroppedFrames ?? 0UL,
            audioUnderruns = lastDiagnostics?.AudioUnderruns ?? 0UL,
            audioReconnects = lastDiagnostics?.AudioReconnects ?? 0UL,
            subtitleErrors = lastDiagnostics?.SubtitleErrors ?? 0UL,
            hardwareFallback = lastDiagnostics?.HardwareFallback ?? false,
            lastErrorCode = lastError,
            disposing
        };
        lock (debugStateGate) debugSnapshot = snap;
    }

    private void RequirePending(Session session)
    {
        ThrowIfClosed(); session.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(pending, session)) throw new PlaybackException(PlaybackError.Superseded);
    }

    private async Task RetireAsync(Session session)
    {
        session.Cancel();
        try
        {
            if (!dispatcher.IsClosing)
            {
                try
                {
                    await Dispatch(() =>
                    {
                        if (ReferenceEquals(surfaceOwner, session))
                        {
                            surface.BindSwapchain(0);
                            surfaceOwner = null;
                            presentedSurface = null;
                            presentedDisplayEpoch = -1;
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) when (dispatcher.IsClosing) { }
            }
            await session.ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (DebugLog.IsEnabled)
                DebugLog.Error(DebugCategories.FilmPlayback, "filmkernel", "session.retire_failed",
                    new { identity = session.Scope.Identity }, error: error);
        }
        finally
        {
            lock (ownership) sessions.Remove(session);
            if (DebugLog.IsEnabled)
            {
                if (dispatcher.HasThreadAccess) UpdateDebugSnapshot();
                else if (!dispatcher.IsClosing) _ = dispatcher.EnqueueAsync(new Action(UpdateDebugSnapshot));
            }
        }
    }

    private async Task AttachSurfaceAsync(Session session)
    {
        var target = surface.Describe();
        long epoch = surface.DisplayEpoch;
        var swapchain = await Task.Run(() =>
        {
            FilmKernelNative.Check(FilmKernelNative.Attach(session.Handle, target, out var sc));
            if (sc == IntPtr.Zero) throw new PlaybackException(PlaybackError.FilmRenderFailed);
            return sc;
        });
        try
        {
            surface.BindSwapchain(swapchain);
        }
        catch
        {
            // Keep C++ and UI surface state consistent if WinUI SetSwapChain throws.
            await Task.Run(() =>
            {
                try { FilmKernelNative.Attach(session.Handle, null, out _); } catch { }
            });
            throw;
        }
        surfaceOwner = session;
        presentedSurface = target;
        presentedDisplayEpoch = epoch;
    }

    private async Task DetachSurfaceAsync(Session session)
    {
        if (ReferenceEquals(surfaceOwner, session))
        {
            surface.BindSwapchain(0);
            surfaceOwner = null;
            presentedSurface = null;
            presentedDisplayEpoch = -1;
        }
        await Task.Run(() => FilmKernelNative.Check(FilmKernelNative.Attach(session.Handle, null, out _)));
    }

    private void SurfaceChanged(object? sender, EventArgs args) => _ = UpdateSurfaceAsync();

    private async Task UpdateSurfaceAsync()
    {
        if (updatingSurface || disposing) return;
        updatingSurface = true;
        bool acquired = false;
        try
        {
            await commands.WaitAsync();
            acquired = true;
            while (!disposing && surfaceOwner is { } owner)
            {
                try
                {
                    var target = surface.Describe();
                    long epoch = surface.DisplayEpoch;
                    if (presentedSurface is { } previous && previous.Equals(target) &&
                        presentedDisplayEpoch == epoch) break;
                    var snapshot = await Task.Run(() =>
                    {
                        FilmKernelNative.Check(FilmKernelNative.UpdateSurface(owner.Handle, ref target));
                        return FilmKernelNative.Read(owner.Handle);
                    });
                    presentedSurface = target;
                    presentedDisplayEpoch = epoch;
                    if (ReferenceEquals(current, owner)) Publish(snapshot);
                }
                catch (Exception error)
                {
                    bool wasCurrent = ReferenceEquals(current, owner);
                    // Report BEFORE ClearOutput so FilmPlaybackSession.MediaFailed still matches Identity!
                    if (!disposing && wasCurrent) Report(error);
                    if (wasCurrent) { current = null; ClearOutput(); }
                    await RetireAsync(owner);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) when (dispatcher.IsClosing) { }
        catch (Exception error)
        {
            if (DebugLog.IsEnabled)
                DebugLog.Error(DebugCategories.FilmPlayback, "filmkernel", "surface.update_error", error: error);
        }
        finally
        {
            if (acquired) commands.Release();
            updatingSurface = false;
        }
    }

    private async Task RetireCancelledAsync(Session session)
    {
        if (!session.Token.IsCancellationRequested) return;
        if (ReferenceEquals(current, session)) { current = null; ClearOutput(); }
        await RetireAsync(session);
    }

    private void ThrowIfClosed()
    {
        if (disposing) throw new PlaybackException(PlaybackError.Disposed);
    }

    private void Report(Exception error)
    {
        Set(ref lastError, Safe(error).Code, nameof(LastErrorCode));
        UpdateDebugSnapshot();
        if (DebugLog.IsEnabled)
            DebugLog.Error(DebugCategories.FilmPlayback, "filmkernel", "playback.error",
                new { identity = scope?.Identity, code = lastError }, error: error);
        Failed?.Invoke(this, EventArgs.Empty);
    }

    private static PlaybackException Safe(Exception error) => error as PlaybackException ??
        new PlaybackException(error is OperationCanceledException ? PlaybackError.Superseded : PlaybackError.CommandFailed);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private Task Dispatch(Func<Task> action) => dispatcher.HasThreadAccess ? action() :
        dispatcher.EnqueueAsync(action);

    private Task<T> Dispatch<T>(Func<Task<T>> action) => dispatcher.HasThreadAccess ? action() :
        dispatcher.EnqueueAsync(action);

    public ValueTask DisposeAsync() => new(Dispatch(() => disposal ??= DisposeCoreAsync()));

    private async Task DisposeCoreAsync()
    {
        leaseRegistration?.Dispose();
        authorityLeaseRegistration?.Dispose();
        Session[] owned;
        lock (ownership) { disposing = true; owned = sessions.ToArray(); }
        timer.Stop();
        surface.TargetChanged -= SurfaceChanged;
        pending = null; current = null; ClearOutput();
        foreach (var session in owned) session.Cancel();
        await commands.WaitAsync();
        try
        {
            await Task.WhenAll(owned.Select(RetireAsync));
            surface.Dispose();
        }
        finally { commands.Release(); }
    }
}

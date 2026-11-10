using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace MeowFilm.Media;

/// <summary>
/// Inject one instance into the App's enabled capability owners. Commands are serialized;
/// ordinary opens retire the preceding player; handoff prepares a muted, paused input
/// beside it and commits without two audible outputs.
/// Construct on the owning STA dispatcher; await disposal before shutting it down.
/// </summary>
public sealed partial class NativePlayback : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly NativeVideoSurface _view;
    private readonly string? _nativeLibraryPath;
    private readonly NativeAudioOutput _audioOutput;
    private readonly NativeHttpMediaOptions _httpOptions;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private LibVLC? _engine;
    private readonly object _engineGate = new();
    private Session? _session;
    private CancellationTokenSource? _opening;
    private long _generation;
    private bool _disposing;
    private bool _releaseFaulted;
    private Task? _disposeTask;
    private readonly object _ownedMediaGate = new();
    private readonly HashSet<Task> _ownedMediaRequests = [];
    private bool _ownedMediaClosing, _ownedMediaReleaseFailed;
    private bool _volumePending;
    private int _volume = 100;
    private string? _ownerId, _identity, _lastErrorCode, _nativeVersion;
    private string _title = string.Empty;
    private bool _isPlaying, _hasMedia;
    private double _positionSeconds, _durationSeconds;
    private VideoOutputObservation? _videoOutput;

    public NativePlayback(Dispatcher? dispatcher = null, string? nativeLibraryPath = null,
        NativeAudioOutput audioOutput = NativeAudioOutput.SystemDefault)
        : this(dispatcher, nativeLibraryPath, audioOutput, new NativeHttpMediaOptions()) { }

    /// <summary>HTTP resource deadlines shared by ordinary and owned media inputs.</summary>
    public NativePlayback(Dispatcher? dispatcher, string? nativeLibraryPath,
        NativeAudioOutput audioOutput, NativeHttpMediaOptions httpOptions)
    {
        _httpOptions = httpOptions ?? throw Error(PlaybackError.InvalidRequest);
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!_dispatcher.CheckAccess() || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw Error(PlaybackError.WrongThread);
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            throw Error(PlaybackError.DispatcherUnavailable);
        if (!Enum.IsDefined(audioOutput) ||
            (nativeLibraryPath is not null && !Path.IsPathFullyQualified(nativeLibraryPath)))
            throw Error(PlaybackError.InvalidRequest);
        _nativeLibraryPath = nativeLibraryPath;
        _audioOutput = audioOutput;
        try { _view = new NativeVideoSurface(); }
        catch { throw Error(PlaybackError.NativeUnavailable); }
    }

    public string? OwnerId => _ownerId;
    public string? Identity => _identity;
    public string Title => _title;
    public bool IsPlaying => _isPlaying;
    public bool HasMedia => _hasMedia;
    public double PositionSeconds => _positionSeconds;
    public double DurationSeconds => _durationSeconds;
    public string? LastErrorCode => _lastErrorCode;
    /// <summary>Observed library version only, not codec/decoder/output capability evidence.</summary>
    public string? NativeVersion => _nativeVersion;
    public FrameworkElement VideoView => _view;
    public VideoOutputObservation? ObservedVideoOutput => _videoOutput;

    /// <summary>Dispatcher-affine binding property; clamps to 0..100, applied through the command gate.</summary>
    public int Volume
    {
        get => _volume;
        set
        {
            RequireDispatcher();
            ThrowIfDisposed();
            if (!Set(ref _volume, Math.Clamp(value, 0, 100))) return;
            QueueVolume();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Started;
    public event EventHandler? Ended;
    public event EventHandler? Failed;
    public event EventHandler<VideoOutputObservation>? VideoOutputObserved;

    /// <summary>
    /// Completes after the old player is released and libVLC accepts Play(), not
    /// at first frame. Started is emitted once per session, from native Playing.
    /// Cancellation remains attached to this session until stop/replacement/end.
    /// </summary>
    public Task OpenAsync(string ownerId, string identity, Uri uri, string title,
        CancellationToken token = default) =>
        DispatchAsync(() => OpenCoreAsync(ownerId, identity, uri, title, null, token));

    /// <summary>
    /// Nonempty headers use bounded native HTTP input. Supported adaptive VOD uses a session-only
    /// native resource service; progressive and read-lease bytes never pass through that service.
    /// </summary>
    public Task OpenAsync(string ownerId, string identity, Uri uri, string title,
        IReadOnlyDictionary<string, string> headers, CancellationToken token = default) =>
        DispatchAsync(() => headers is null
            ? Task.FromException(Error(PlaybackError.InvalidRequest))
            : OpenCoreAsync(ownerId, identity, uri, title, headers, token));

    public Task PauseAsync() => DispatchAsync(PauseWithIntentAsync);
    public Task ResumeAsync() => DispatchAsync(ResumeWithIntentAsync);
    public Task SeekAsync(double seconds) => DispatchAsync(() => CommandAsync(Command.Seek, seconds));
    public Task StopAsync() => DispatchAsync(StopCoreAsync);
    public ValueTask DisposeAsync() =>
        new(_disposeTask ?? DispatchAsync(() => _disposeTask ??= DisposeCoreAsync()));

    private async Task OpenCoreAsync(string ownerId, string identity, Uri uri, string title,
        IReadOnlyDictionary<string, string>? headers, CancellationToken token,
        MediaResourceSession? resource = null, NativePlaybackContinuation? continuation = null,
        CancellationToken preparationToken = default)
    {
        ThrowIfDisposed();
        if (_releaseFaulted) throw Error(PlaybackError.ReleaseFailed);
        Validate(ownerId, identity, uri, title, headers);
        // Take a bounded immutable request snapshot before any await/replacement.
        var requestOptions = _httpOptions;
        // An explicit empty header map or URL userinfo requests managed HTTP input.
        // Official CDN URLs carry no account headers, but need the same bounded
        // redirect/Range validation and typed failure reporting as signed inputs.
        var requestHeaders = resource is not null ||
            uri.Scheme is ("http" or "https") && (headers is not null || uri.UserInfo.Length != 0) ||
            AdaptiveManifest.HasManifestPath(uri)
            ? HttpMediaInput.ValidateRequest(uri, headers ?? new Dictionary<string, string>(), true) : null;
        token.ThrowIfCancellationRequested(); // Rejected requests never interrupt current playback.
        long generation;
        if (continuation is null)
        {
            generation = BeginReplacement();
            _continuationAuthority = CancellationTokenSource.CreateLinkedTokenSource(token);
            _continuable = resource is null && uri.Scheme is "http" or "https";
            Set(ref _pauseRequested, false, nameof(PauseRequested));
        }
        else
        {
            _opening?.Cancel(); _session?.Invalidate();
            generation = ++_generation;
        }
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(token, preparationToken);
        _opening = opening;
        HttpMediaInput? unownedInput = null;
        AdaptiveMediaSession? unownedAdaptive = null;
        bool resourceTransferred = false;
        string? failure = null;
        Task? continuationReady = null;
        await _commands.WaitAsync();
        try
        {
            CheckRequest(generation, opening.Token);
            await RetireAsync();
            if (continuation is null) ClearState();
            else
            {
                Set(ref _tracks, null, nameof(Tracks));
                Set(ref _videoOutput, null, nameof(ObservedVideoOutput));
                Set(ref _lastErrorCode, null, nameof(LastErrorCode));
            }
            Set(ref _ownerId, ownerId, nameof(OwnerId));
            Set(ref _identity, identity, nameof(Identity));
            Set(ref _title, title, nameof(Title));
            CheckRequest(generation, opening.Token);
            if (requestHeaders is not null)
            {
                unownedInput = await HttpMediaInput.OpenPlaybackAsync(uri, requestHeaders, requestOptions, opening.Token);
                if (unownedInput.DemuxName == "adaptive")
                {
                    byte[] manifest;
                    try { manifest = await unownedInput.ReadManifestAsync(opening.Token); }
                    catch (Exception error)
                    {
                        if (opening.IsCancellationRequested) throw new OperationCanceledException(opening.Token);
                        throw Error(HttpMediaInput.Classify(error));
                    }
                    var resolved = unownedInput.EffectiveUri;
                    unownedInput.Dispose(); unownedInput = null;
                    unownedAdaptive = await AdaptiveMediaSession.OpenAsync(manifest, resolved,
                        requestHeaders, requestOptions, opening.Token);
                }
            }
            CheckRequest(generation, opening.Token);
            try { await Task.Run(EnsureEngine); }
            catch (PlaybackException) { throw; }
            catch { throw Error(PlaybackError.NativeUnavailable); }
            CheckRequest(generation, opening.Token);
            Set(ref _nativeVersion, _engine!.Version, nameof(NativeVersion));
            Session created;
            try { created = await Task.Run(() => CreateSession(generation, uri, unownedInput,
                adaptive: unownedAdaptive, resource: resource)); }
            catch { throw Error(PlaybackError.OpenFailed); }
            _session = created;
            if (continuation is not null || _pauseRequested)
            {
                created.Media.AddOption(":start-paused");
                created.ContinuationPosition = continuation?.PositionSeconds ?? 0;
                created.ContinuationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                continuationReady = created.ContinuationReady.Task;
            }
            resourceTransferred = true;
            unownedInput = null; // Session now owns the callback input through native retirement.
            unownedAdaptive = null;
            // Always register/own resources before another await or a cancellation check.
            created.Attach(this);
            _view.SetPlayer(created.Player);
            CheckRequest(generation, opening.Token);
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
            CheckRequest(generation, opening.Token);
            bool accepted = await Task.Run(created.Player.Play);
            CheckRequest(generation, opening.Token);
            if (!accepted) throw Error(created.LastInputError ?? PlaybackError.OpenFailed);
            opening.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            if (_session?.Generation == generation)
                await RetireAsync();
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
            try
            {
                unownedInput?.Dispose();
                if (unownedAdaptive is not null) await unownedAdaptive.DisposeAsync();
            }
            finally
            {
                try { if (!resourceTransferred && resource is not null) await resource.DisposeAsync(); }
                finally
                {
                    if (ReferenceEquals(_opening, opening)) _opening = null;
                    _commands.Release();
                }
            }
        }
        if (failure is not null)
        {
            if (_generation == generation && !_disposing) Failed?.Invoke(this, EventArgs.Empty);
            throw Error(failure);
        }
        if (continuationReady is not null)
        {
            try { await continuationReady.WaitAsync(TimeSpan.FromSeconds(12), opening.Token); }
            catch (Exception error)
            {
                if (_session is { } session && session.Generation == generation)
                    await FinishSessionAsync(session, error is PlaybackException known ? known.Code : PlaybackError.OpenFailed);
                throw;
            }
        }
    }

    private void EnsureEngine()
    {
        lock (_engineGate)
        {
            ThrowIfDisposed();
            if (_engine is not null) return;
            // This is library bootstrap, not a decoder, global playback service or Node proxy.
            NativeRuntimeModules.EnsureLoaded(_nativeLibraryPath);
            var options = new List<string>
            {
                "--quiet", "--ignore-config", "--no-interact", "--keystore=none",
                "--no-video-title-show", "--no-osd",
                "--no-sub-autodetect-file", "--no-metadata-network-access"
            };
            FFmpegNativeLibrary.EnsureLoaded();
            if (_audioOutput == NativeAudioOutput.Dummy) options.Add("--aout=dummy");
            _engine = new LibVLC(false, options.ToArray());
            // Replaces the native default logger. No URI, native message or credentials leave this boundary.
            _engine.Log += DiscardNativeLog;
        }
    }

    private static void DiscardNativeLog(object? sender, LogEventArgs args) { }

    private Session CreateSession(long generation, Uri? uri, HttpMediaInput? input,
        ReadLeaseMediaInput? readInput = null, AdaptiveMediaSession? adaptive = null,
        MediaResourceSession? resource = null, FFmpegMediaInput? ffmpeg = null)
    {
        // Fixed demux whitelist prevents adaptive/playlist auto detection on callback content.
        // URI and headers are never passed as VLC options or an MRL on this path.
        MediaInput? callbackInput = readInput ?? (MediaInput?)input;
        var media = ffmpeg is not null
            ? new VlcMedia(_engine!, ffmpeg.Mrl, FromType.FromLocation)
            : adaptive is not null
            ? new VlcMedia(_engine!, adaptive.Root.AbsoluteUri, FromType.FromLocation,
                ":demux=adaptive,none",
                ":adaptive-maxbuffer=5", ":adaptive-minbuffer=1", ":adaptive-logic=lowest")
            : callbackInput is not null
            ? new VlcMedia(_engine!, callbackInput, $":demux={readInput?.DemuxName ?? input!.DemuxName},none",
                ":prefetch-buffer-size=256", ":prefetch-read-size=65536", ":prefetch-seek-threshold=65536")
            : uri!.IsFile
            ? new VlcMedia(_engine!, uri.LocalPath, FromType.FromPath)
            : new VlcMedia(_engine!, uri.AbsoluteUri, FromType.FromLocation);
        MediaPlayer? player = null;
        try
        {
            player = new MediaPlayer(media)
            {
                EnableKeyInput = false,
                EnableMouseInput = false,
                Volume = _volume
            };
            return new Session(generation, player, media, input, readInput, adaptive, resource, ffmpeg);
        }
        catch
        {
            player?.Dispose();
            media.Dispose();
            throw;
        }
    }

    private enum Command { Pause, Resume, Seek }

    private async Task CommandAsync(Command command, double seconds = 0)
    {
        ThrowIfDisposed();
        if (command == Command.Seek &&
            (!double.IsFinite(seconds) || seconds < 0 || seconds >= long.MaxValue / 1000d))
            throw Error(PlaybackError.InvalidSeek);
        var session = _session; // A queued command may not cross into a newer session.
        var intent = _intentSequence;
        if (session is null) return;
        string? failure = null;
        long? acceptedSeek = null;
        await _commands.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if (!IsCurrent(session)) return;
            await Task.Run(() =>
            {
                switch (command)
                {
                    case Command.Pause:
                        if (intent != _intentSequence) return;
                        if (!session.Player.CanPause) throw Error(PlaybackError.NotPausable);
                        session.Player.SetPause(true);
                        break;
                    case Command.Resume:
                        if (intent != _intentSequence) return;
                        session.Player.SetPause(false);
                        break;
                    case Command.Seek:
                        if (!session.Player.IsSeekable) throw Error(PlaybackError.NotSeekable);
                        var length = session.Player.Length;
                        var target = (long)(seconds * 1000);
                        acceptedSeek = length > 0 ? Math.Min(target, length) : target;
                        session.Player.Time = acceptedSeek.Value;
                        break;
                }
            });
            if (acceptedSeek is { } position && IsCurrent(session))
            {
                // VLC accepts seeks while paused but may not emit TimeChanged
                // until resumed. Publish the accepted position without inventing
                // playback; a queued old progress flush must not restore old time.
                lock (session.ProgressLock) session.LatestTime = position;
                Set(ref _positionSeconds, position / 1000d, nameof(PositionSeconds));
            }
        }
        catch (PlaybackException) { throw; }
        catch { failure = PlaybackError.CommandFailed; }
        finally { _commands.Release(); }
        if (failure is not null)
        {
            await FinishSessionAsync(session, failure);
            throw Error(failure);
        }
    }

    private async Task StopCoreAsync()
    {
        ThrowIfDisposed();
        var generation = BeginReplacement();
        await _commands.WaitAsync();
        try
        {
            if (generation != _generation) return;
            await RetireAsync();
            ClearState();
        }
        catch
        {
            Set(ref _lastErrorCode, PlaybackError.ReleaseFailed, nameof(LastErrorCode));
            throw Error(PlaybackError.ReleaseFailed);
        }
        finally { _commands.Release(); }
    }

    private async Task CancelSessionAsync(Session session)
    {
        await _commands.WaitAsync();
        try
        {
            if (_session != session) return;
            await RetireAsync();
            if (_generation == session.Generation) ClearState();
        }
        finally { _commands.Release(); }
    }

    private async Task FinishSessionAsync(Session session, string? error)
    {
        if (!IsCurrent(session) || session.Terminal) return;
        error = session.LastInputError ?? error; // Input failure must not appear as natural EOF.
        if (error is null && await TryRecoverLiveAsync(session)) return;
        if (!IsCurrent(session) || session.Terminal) return;
        error = session.LastInputError ?? error;
        if (!session.HasStarted && error is null) error = PlaybackError.OpenFailed;
        CaptureHandoffControls(session);
        _handoffDonorRetired = error is not null &&
            error is not (PlaybackError.ReadRevoked or PlaybackError.ResourceRevoked);
        session.Terminal = true;
        session.Invalidate();
        await _commands.WaitAsync();
        try
        {
            if (_session != session || _generation != session.Generation || _disposing) return;
            try
            {
                await RetireAsync(preserveHandoff: _handoffDonorRetired);
            }
            catch { error = PlaybackError.ReleaseFailed; }
            if (_generation != session.Generation || _disposing || Volatile.Read(ref session.Cancelled) != 0) return;
            Set(ref _isPlaying, false, nameof(IsPlaying));
            Set(ref _hasMedia, false, nameof(HasMedia));
            Set(ref _lastErrorCode, error, nameof(LastErrorCode));
            if (error is null && DurationSeconds > 0)
                Set(ref _positionSeconds, DurationSeconds, nameof(PositionSeconds));
        }
        finally { _commands.Release(); }
        // Metadata remains available for queue ownership. Invoke only after native release and outside the gate.
        if (_generation != session.Generation || _disposing || Volatile.Read(ref session.Cancelled) != 0) return;
        if (error is null) { RevokeContinuation(); Ended?.Invoke(this, EventArgs.Empty); }
        else Failed?.Invoke(this, EventArgs.Empty);
    }

    private async Task RetireAsync(bool preserveHandoff = false)
    {
        bool failed = _releaseFaulted;
        try { if (!preserveHandoff) await DiscardHandoffCoreAsync(); }
        catch { failed = true; }
        var session = _session;
        if (session is not null)
        {
            try { failed |= await ReleaseNativeAsync(session, detachView: true); }
            catch { failed = true; }
            finally
            {
                _session = null;
                Set(ref _isPlaying, false, nameof(IsPlaying));
                Set(ref _hasMedia, false, nameof(HasMedia));
            }
        }
        if (failed)
        {
            _releaseFaulted = true;
            throw Error(PlaybackError.ReleaseFailed);
        }
    }

    /// <summary>Stops and frees one session's native resources. A session that is no longer
    /// displayed (replaced by a handoff, or never published) leaves the view alone.</summary>
    private Task<bool> ReleaseNativeAsync(Session session, bool detachView) =>
        session.ReleaseTask ??= ReleaseNativeCoreAsync(session, detachView);

    private async Task<bool> ReleaseNativeCoreAsync(Session session, bool detachView)
    {
        session.Released = true;
        session.ContinuationReady?.TrySetCanceled(new CancellationToken(true));
        session.Invalidate();
        session.ProgressTimer?.Stop();
        session.Cancellation.Dispose(); // Cancellation callback only enqueues work; never waits for this dispatcher.
        if (detachView) _view.BeginRetirement();
        bool failed = false;
        try { if (session.RuntimeOutput is { } output) await output.DisposeAsync(); }
        catch { failed = true; }
        // Stop joins native threads. Never call it on a libVLC callback or block the WPF message pump.
        try { await Task.Run(session.Player.Stop); }
        catch { failed = true; }
        if (detachView)
        {
            try { _view.SetPlayer(null); }
            catch { failed = true; }
        }
        try
        {
            await Task.Run(() =>
            {
                try { session.Detach(); }
                finally
                {
                    try { session.Player.Dispose(); }
                    finally
                    {
                        try { session.Media.Dispose(); }
                        finally { session.Input?.Dispose(); }
                    }
                }
            });
        }
        catch { failed = true; }
        // Lease revocation/Cancel has already closed the underlying handle. Await its
        // actual release and then free callback state, only after native callbacks retire.
        try { if (session.ReadInput is not null) await session.ReadInput.DisposeAsync(); }
        catch { failed = true; }
        try { if (session.Adaptive is not null) await session.Adaptive.DisposeAsync(); }
        catch { failed = true; }
        try { if (session.FFmpeg is not null) await session.FFmpeg.DisposeAsync(); }
        catch { failed = true; }
        try { if (session.Resource is not null) await session.Resource.DisposeAsync(); }
        catch { failed = true; }
        finally { session.ProgressTimer = null; }
        return failed;
    }

    private async Task DisposeCoreAsync()
    {
        var ownedMediaDrain = DrainOwnedMediaRequestsAsync();
        _disposing = true;
        BeginReplacement();
        // Rejected attachment/open requests may need the command gate to finish.
        // Cancel first, then join their outer receipt before acquiring that gate.
        bool failed = await ownedMediaDrain;
        await _commands.WaitAsync();
        failed |= _releaseFaulted;
        try
        {
            try { await RetireAsync(); }
            catch { failed = true; }
            try { _view.CloseSurface(); }
            catch { failed = true; }
            LibVLC? engine;
            lock (_engineGate) { engine = _engine; _engine = null; }
            if (engine is not null)
            {
                try { await Task.Run(engine.Dispose); }
                catch { failed = true; }
            }
            ClearState();
            if (failed) Set(ref _lastErrorCode, PlaybackError.ReleaseFailed, nameof(LastErrorCode));
        }
        finally { _commands.Release(); }
        if (failed) throw Error(PlaybackError.ReleaseFailed);
    }

    private void OnPlaying(Session session)
    {
        if (!IsCurrent(session) || session.LastInputError is not null) return;
        if (session.ContinuationReady is not null) return;
        Set(ref _isPlaying, true, nameof(IsPlaying));
        QueueVolume();
        if (session.HasStarted) return;
        session.HasStarted = true;
        Started?.Invoke(this, EventArgs.Empty);
    }

    private void OnPaused(Session session)
    {
        if (IsCurrent(session))
        {
            Set(ref _isPlaying, false, nameof(IsPlaying));
            if (session.ContinuationReady is not null) Observe(InitializeContinuationAsync(session));
        }
    }

    private void QueueVolume()
    {
        if (_volumePending || _disposing) return;
        _volumePending = true;
        Observe(ApplyVolumeAsync());
    }

    private async Task ApplyVolumeAsync()
    {
        Session? session = null;
        string? failure = null;
        await _commands.WaitAsync();
        try
        {
            _volumePending = false;
            session = _session;
            if (session is not null && IsCurrent(session))
                session.Player.Volume = _volume;
        }
        catch { failure = PlaybackError.CommandFailed; }
        finally { _commands.Release(); }
        if (failure is not null && session is not null) await FinishSessionAsync(session, failure);
    }

    private void Progress(Session session, long? time = null, long? length = null)
    {
        // At most one dispatcher delivery/timer per session. Native callbacks do no blocking work.
        lock (session.ProgressLock)
        {
            if (!session.Valid) return;
            if (time.HasValue) session.LatestTime = time.Value;
            if (length.HasValue) session.LatestLength = length.Value;
            if (session.ProgressPending) return;
            session.ProgressPending = true;
        }
        Post(() =>
        {
            if (!IsCurrent(session)) return;
            var remaining = TimeSpan.FromMilliseconds(200) - Stopwatch.GetElapsedTime(session.LastProgress);
            if (remaining <= TimeSpan.Zero) FlushProgress(session);
            else
            {
                session.ProgressTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
                session.ProgressTimer.Interval = remaining;
                session.ProgressTimer.Tick -= session.ProgressTick;
                session.ProgressTimer.Tick += session.ProgressTick;
                session.ProgressTimer.Start();
            }
        });
    }

    private void FlushProgress(Session session)
    {
        session.ProgressTimer?.Stop();
        if (!IsCurrent(session)) return;
        long time, length;
        lock (session.ProgressLock)
        {
            time = session.LatestTime;
            length = session.LatestLength;
            session.ProgressPending = false;
        }
        session.LastProgress = Stopwatch.GetTimestamp();
        Set(ref _durationSeconds, Math.Max(0, length) / 1000d, nameof(DurationSeconds));
        Set(ref _positionSeconds, Math.Max(0, time) / 1000d, nameof(PositionSeconds));
        ObserveVideoOutput(session);
    }

    private void ObserveVideoOutput(Session session)
    {
        if (!IsCurrent(session) || _videoOutput is not null || OwnerId is null || Identity is null) return;
        int displayed;
        try
        {
            // Reuse native progress deliveries, never start an idle polling timer.
            // An unavailable/zero counter remains unknown. In VLC 3.0.24 the
            // displayed counter increments after vout_display_Display returns.
            if (session.Media.Statistics is not { DisplayedPictures: > 0 } statistics) return;
            displayed = statistics.DisplayedPictures;
        }
        catch
        {
            // Telemetry failure is not a playback failure or a synthetic frame.
            return;
        }
        var observation = new VideoOutputObservation(OwnerId, Identity, session.Generation, displayed);
        Set(ref _videoOutput, observation, nameof(ObservedVideoOutput));
        if (IsCurrent(session)) VideoOutputObserved?.Invoke(this, observation);
    }

    private long BeginReplacement()
    {
        _handoffDonorRetired = false;
        _retiredHandoffRate = 1;
        _handoffOpening?.Cancel(); // A prepared input never outlives the playback it was prepared for.
        RevokeContinuation();
        ++_intentSequence;
        Set(ref _pauseRequested, false, nameof(PauseRequested));
        _opening?.Cancel(); // Preflight may hold the command gate before a Session exists.
        _session?.Invalidate();
        return ++_generation;
    }

    private bool IsCurrent(Session session) =>
        !_disposing && _session == session && _generation == session.Generation &&
        session.Valid && Volatile.Read(ref session.Cancelled) == 0;

    private void CheckRequest(long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposing || _generation != generation)
            throw new OperationCanceledException(PlaybackError.Superseded, new CancellationToken(true));
    }

    private static void Validate(string ownerId, string identity, Uri uri, string title,
        IReadOnlyDictionary<string, string>? headers)
    {
        ValidateIdentity(ownerId, identity, title);
        if (uri is null || !uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeFile && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw Error(PlaybackError.UnsupportedUri);
        if (uri.IsFile && (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
            throw Error(PlaybackError.UnsupportedUri);
        if (uri.IsFile && headers is { Count: > 0 }) throw Error(PlaybackError.UnsupportedHeaders);
    }

    private static void ValidateIdentity(string ownerId, string identity, string title)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(identity) ||
            ownerId.Length > 512 || identity.Length > 2048 || title is null || title.Length > 4096)
            throw Error(PlaybackError.InvalidRequest);
    }

    private void ClearState()
    {
        Set(ref _tracks, null, nameof(Tracks));
        Set(ref _videoOutput, null, nameof(ObservedVideoOutput));
        Set(ref _ownerId, null, nameof(OwnerId));
        Set(ref _identity, null, nameof(Identity));
        Set(ref _title, string.Empty, nameof(Title));
        Set(ref _isPlaying, false, nameof(IsPlaying));
        Set(ref _hasMedia, false, nameof(HasMedia));
        Set(ref _positionSeconds, 0, nameof(PositionSeconds));
        Set(ref _durationSeconds, 0, nameof(DurationSeconds));
        Set(ref _lastErrorCode, null, nameof(LastErrorCode));
    }

    private async Task TrackOwnedMediaRequestAsync(Func<Task> operation)
    {
        TaskCompletionSource? receipt;
        lock (_ownedMediaGate)
        {
            receipt = _ownedMediaClosing ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (receipt is not null) _ownedMediaRequests.Add(receipt.Task);
        }
        try { await operation().ConfigureAwait(false); }
        catch (PlaybackException error) when (error.Code == PlaybackError.ReleaseFailed)
        {
            lock (_ownedMediaGate)
                if (receipt is not null) _ownedMediaReleaseFailed = true;
            throw;
        }
        finally
        {
            if (receipt is not null)
                lock (_ownedMediaGate)
                {
                    _ownedMediaRequests.Remove(receipt.Task);
                    receipt.TrySetResult();
                }
        }
    }

    private async Task<bool> DrainOwnedMediaRequestsAsync()
    {
        Task[] pending;
        lock (_ownedMediaGate)
        {
            _ownedMediaClosing = true;
            pending = _ownedMediaRequests.ToArray();
        }
        await Task.WhenAll(pending);
        lock (_ownedMediaGate) return _ownedMediaReleaseFailed;
    }

    private Task DispatchAsync(Func<Task> operation)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            return Task.FromException(Error(PlaybackError.DispatcherUnavailable));
        if (_dispatcher.CheckAccess()) return operation();
        return _dispatcher.InvokeAsync(operation, DispatcherPriority.Normal).Task.Unwrap();
    }

    private void Post(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try { _dispatcher.BeginInvoke(DispatcherPriority.Normal, action); }
        catch (InvalidOperationException) { /* The owner must dispose before dispatcher shutdown. */ }
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception error)
        {
            // Only fixed diagnostics for asynchronous cleanup failures; never log a native exception.
            if (!_disposing) Set(ref _lastErrorCode,
                error is PlaybackException known ? known.Code : PlaybackError.CallbackFailed, nameof(LastErrorCode));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposing) throw Error(PlaybackError.Disposed);
    }

    private void RequireDispatcher()
    {
        if (!_dispatcher.CheckAccess()) throw Error(PlaybackError.WrongThread);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        return true;
    }

    private static PlaybackException Error(string code) => new(code);

    private sealed class Session(long generation, MediaPlayer player, VlcMedia media, HttpMediaInput? input,
        ReadLeaseMediaInput? readInput = null, AdaptiveMediaSession? adaptive = null,
        MediaResourceSession? resource = null, FFmpegMediaInput? ffmpeg = null)
    {
        internal readonly long Generation = generation;
        internal readonly MediaPlayer Player = player;
        internal readonly VlcMedia Media = media;
        internal readonly HttpMediaInput? Input = input;
        internal readonly ReadLeaseMediaInput? ReadInput = readInput;
        internal readonly AdaptiveMediaSession? Adaptive = adaptive;
        internal readonly MediaResourceSession? Resource = resource;
        internal readonly FFmpegMediaInput? FFmpeg = ffmpeg;
        internal RuntimeOutput? RuntimeOutput;
        internal string? LastInputError => FFmpeg?.LastErrorCode ?? ReadInput?.LastErrorCode ??
            Input?.LastErrorCode ?? Adaptive?.LastErrorCode;
        internal CancellationTokenRegistration Cancellation;
        internal int Cancelled;
        private int _valid = 1;
        internal bool Valid => Volatile.Read(ref _valid) != 0;
        internal void Invalidate()
        {
            Interlocked.Exchange(ref _valid, 0);
            ContinuationReady?.TrySetCanceled(new CancellationToken(true));
            // Never wait on the IO lock here. A native command/Stop may already be joining Read.
            Input?.Cancel();
            ReadInput?.Cancel();
            Adaptive?.Cancel();
            FFmpeg?.Cancel();
            Resource?.Cancel();
        }
        internal bool HasStarted, Terminal, Released, RuntimeOutputReady;
        internal Task<bool>? ReleaseTask;
        internal TaskCompletionSource? ContinuationReady;
        internal bool ContinuationInitializing;
        internal double ContinuationPosition;
        internal readonly object ProgressLock = new();
        internal long LatestTime, LatestLength, LastProgress;
        internal bool ProgressPending;
        internal DispatcherTimer? ProgressTimer;
        internal EventHandler? ProgressTick;
        private EventHandler<EventArgs>? _playing, _paused, _ended, _failed;
        private EventHandler<MediaPlayerTimeChangedEventArgs>? _time;
        private EventHandler<MediaPlayerLengthChangedEventArgs>? _length;
        private Action<string>? _inputFailed;
        private Action<string>? _readFailed;
        private Action<string>? _adaptiveFailed;
        private Action<string>? _ffmpegFailed;

        internal void Attach(NativePlayback owner)
        {
            _playing = (_, _) => owner.Post(() => owner.OnPlaying(this));
            _paused = (_, _) => owner.Post(() => owner.OnPaused(this));
            _ended = (_, _) => owner.Post(() => owner.Observe(owner.FinishSessionAsync(this, null)));
            _failed = (_, _) => owner.Post(() => owner.Observe(owner.FinishSessionAsync(this, PlaybackError.PlaybackFailed)));
            _time = (_, args) => owner.Progress(this, time: args.Time);
            _length = (_, args) => owner.Progress(this, length: args.Length);
            ProgressTick = (_, _) => owner.FlushProgress(this);
            Player.Playing += _playing;
            Player.Paused += _paused;
            Player.EndReached += _ended;
            Player.EncounteredError += _failed;
            Player.TimeChanged += _time;
            Player.LengthChanged += _length;
            if (Input is not null)
            {
                _inputFailed = code => owner.Post(() => owner.Observe(owner.FinishSessionAsync(this, code)));
                Input.Failed += _inputFailed;
            }
            if (ReadInput is not null)
            {
                _readFailed = code => owner.Post(() => owner.Observe(owner.FinishSessionAsync(this, code)));
                ReadInput.Failed += _readFailed;
            }
            if (Adaptive is not null)
            {
                _adaptiveFailed = code => owner.Post(() => owner.Observe(owner.FinishSessionAsync(this, code)));
                Adaptive.Failed += _adaptiveFailed;
            }
            if (FFmpeg is not null)
            {
                _ffmpegFailed = code => owner.Post(() => owner.Observe(owner.FinishSessionAsync(this, code)));
                FFmpeg.Failed += _ffmpegFailed;
            }
        }

        internal void Detach()
        {
            if (_playing is null) return; // Never attached: a prepared handoff that was not committed.
            Player.Playing -= _playing;
            Player.Paused -= _paused;
            Player.EndReached -= _ended;
            Player.EncounteredError -= _failed;
            Player.TimeChanged -= _time;
            Player.LengthChanged -= _length;
            if (Input is not null) Input.Failed -= _inputFailed;
            if (ReadInput is not null) ReadInput.Failed -= _readFailed;
            if (Adaptive is not null) Adaptive.Failed -= _adaptiveFailed;
            if (FFmpeg is not null) FFmpeg.Failed -= _ffmpegFailed;
        }
    }
}

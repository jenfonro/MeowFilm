using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MeowFilm.Media;

/// <summary>Successful native render/Present submissions, not physical display scan-out or HDR certification.</summary>
public sealed record FilmOutputObservation(NativePlaybackScope Scope, ulong SubmittedPictures,
    bool NativeHdr, bool DolbyVision, bool HardwareDecoding);

/// <summary>Independent film owner. Native commands and retirement share one asynchronous gate.</summary>
public sealed partial class FilmPlayback : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly FilmKernelApi kernel;
    private readonly Func<CancellationToken, Task<IDisposable>>? acquireOutput;
    private readonly FilmVideoSurface surface;
    private readonly DispatcherTimer timer;
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly object ownership = new();
    private readonly HashSet<Session> sessions = [];
    private Session? pending, current;
    private long generation;
    private long admittedGeneration;
    private bool disposing, polling;
    private volatile bool releaseFaulted;
    private Task? disposal;
    private int volume = 100;
    private NativePlaybackScope? scope;
    private bool hasOutput, buffering, paused;
    private double position, duration;
    private string? lastError;
    private FilmOutputObservation? output;

    public FilmPlayback(Dispatcher? dispatcher = null,
        Func<CancellationToken, Task<IDisposable>>? acquireOutput = null) :
        this(dispatcher, new FilmKernelApi(), acquireOutput) { }

    internal FilmPlayback(Dispatcher? dispatcher, FilmKernelApi kernel,
        Func<CancellationToken, Task<IDisposable>>? acquireOutput = null)
    {
        this.kernel = kernel;
        this.acquireOutput = acquireOutput;
        this.dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!this.dispatcher.CheckAccess() || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new PlaybackException(PlaybackError.WrongThread);
        surface = new FilmVideoSurface();
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            Poll, this.dispatcher);
        timer.Stop();
    }
    public ContentControl VideoView => surface;
    public NativePlaybackScope? Scope => scope;
    public string? Identity => scope?.Identity;
    public bool HasOutput => hasOutput;
    public bool IsBuffering => buffering;
    public bool PauseRequested => paused;
    public double PositionSeconds => position;
    public double DurationSeconds => duration;
    public string? LastErrorCode => lastError;
    public FilmOutputObservation? Output => output;
    public int Volume
    {
        get => volume;
        set
        {
            dispatcher.VerifyAccess(); ThrowIfClosed();
            if (!Set(ref volume, Math.Clamp(value, 0, 100))) return;
            if (current is { } session) _ = SetVolumeAsync(session);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Failed;
    public event EventHandler? Ended;

    public Task PrepareAsync(RuntimeMediaPlaybackIntent intent, Uri address, NativeVideoInput input,
        INativeMediaResourceLease? lease, CancellationToken token = default) =>
        PrepareOwnedAsync(intent, address, input, lease, null, token);

    public Task PrepareAsync(RuntimeMediaPlaybackIntent intent, Uri address, NativeVideoInput input,
        INativeMediaResourceLease? lease, IReadOnlyDictionary<string, string> headers,
        CancellationToken token = default) => PrepareOwnedAsync(intent, address, input, lease, headers, token);

    private async Task PrepareOwnedAsync(RuntimeMediaPlaybackIntent intent, Uri address, NativeVideoInput input,
        INativeMediaResourceLease? lease, IReadOnlyDictionary<string, string>? headers, CancellationToken token)
    {
        Session? session = null;
        byte[] keys = [];
        byte[]? defaultKey = null;
        try
        {
            // This executes before the first await: both inputs are owned even on rejection.
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
            var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (lease is not null)
                foreach (var pair in lease.Headers) requestHeaders.Add(pair.Key, pair.Value);
            if (headers is not null)
                foreach (var pair in headers) requestHeaders[pair.Key] = pair.Value;
            session = new Session(kernel, intent, Interlocked.Increment(ref generation), address, format,
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
                    var target = surface.PictureHandle;
                    await Task.Run(() =>
                    {
                        candidate.Create();
                        FilmKernelNative.Check(kernel.Prepare(candidate.Handle, intent.Position, target));
                    });
                    RequirePending(candidate);
                    var snapshot = kernel.Read(candidate.Handle);
                    if (snapshot.State != 1) throw new PlaybackException(candidate.Error ?? PlaybackError.OpenFailed);
                }
                finally { commands.Release(); }
            });
        }
        catch (Exception error)
        {
            if (session is not null)
            {
                session.Cancel();
                await Dispatch(() =>
                {
                    if (ReferenceEquals(pending, session)) pending = null;
                    return Task.CompletedTask;
                });
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
            CryptographicOperations.ZeroMemory(keys);
            if (defaultKey is not null) CryptographicOperations.ZeroMemory(defaultKey);
            if (lease is not null)
            {
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch { throw new PlaybackException(PlaybackError.ReleaseFailed); }
            }
        }
    }

    public Task<bool> TryCommitAsync(RuntimeMediaPlaybackIntent intent, Func<bool> canCommit,
        Action committed, CancellationToken token = default) => Dispatch(async () =>
    {
        ThrowIfClosed();
        var session = pending;
        if (session is null || !ReferenceEquals(session.Intent, intent)) return false;
        await commands.WaitAsync();
        // Another queued commit may have published this exact session while we
        // waited. It is no longer our candidate to cancel or retire.
        if (!ReferenceEquals(pending, session))
        {
            commands.Release();
            return false;
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
            if (!canCommit()) return false;
            if (intent.PreserveFrom is { } preserve && donor?.Scope != preserve) return false;
            try { permit = await AcquireOutputAsync(session.Token); }
            catch when (!ReferenceEquals(pending, session)) { return false; }
            // Admission may stop this owner or admit another candidate while
            // commands is released. Never retire an already-published duplicate.
            if (!ReferenceEquals(pending, session)) return false;
            RequirePending(session); token.ThrowIfCancellationRequested();
            if (!canCommit() || !ReferenceEquals(current, donor) ||
                intent.PreserveFrom is { } expected && donor?.Scope != expected) return false;
            donorPaused = paused;
            if (intent.PreserveFrom is not null && donor is not null)
            {
                FilmKernelNative.Check(kernel.Pause(donor.Handle, 1));
                silenced = true;
                double align = kernel.Read(donor.Handle).Position;
                await Task.Run(() => FilmKernelNative.Check(kernel.Seek(session.Handle, align)));
            }
            RequirePending(session); token.ThrowIfCancellationRequested();
            if (!canCommit() || !ReferenceEquals(current, donor)) return false;
            if (donor is not null && !silenced)
            {
                FilmKernelNative.Check(kernel.Pause(donor.Handle, 1));
                silenced = true;
            }
            FilmKernelNative.Check(kernel.Volume(session.Handle, volume / 100d));
            if (donor is not null)
            {
                FilmKernelNative.Check(kernel.Attach(donor.Handle, IntPtr.Zero));
                detached = true;
            }
            FilmKernelNative.Check(kernel.Attach(session.Handle, surface.PictureHandle));
            FilmKernelNative.Check(kernel.Start(session.Handle, intent.Paused ? 1 : 0));
            var snapshot = kernel.Read(session.Handle);
            if (snapshot.Error != 0 || snapshot.State is not (2 or 3 or 4) ||
                snapshot.SubmittedPictures == 0 || intent.Paused && snapshot.State != 3)
                throw new PlaybackException(session.Error ?? (snapshot.Error == 0
                    ? PlaybackError.OpenFailed : FilmKernelNative.Code(snapshot.Error)));
            RequirePending(session); token.ThrowIfCancellationRequested();
            current = session; pending = null;
            published = true;
            Set(ref tracks, null, nameof(Tracks));
            Set(ref scope, session.Scope, nameof(Scope)); Changed(nameof(Identity));
            Set(ref paused, intent.Paused, nameof(PauseRequested));
            Set(ref hasOutput, true, nameof(HasOutput)); Set(ref lastError, null, nameof(LastErrorCode));
            Publish(snapshot);
            timer.Start();
            committed();
            return true;
        }
        catch (Exception error)
        {
            if (ReferenceEquals(current, session))
            {
                current = null; ClearOutput();
                Report(error);
            }
            if (ReferenceEquals(pending, session)) pending = null;
            await RetireAsync(session);
            throw Safe(error);
        }
        finally
        {
            try
            {
                if (published && donor is not null) await RetireAsync(donor);
                else if (silenced && ReferenceEquals(current, donor) && donor is not null && !donor.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (detached) FilmKernelNative.Check(kernel.Attach(donor.Handle, surface.PictureHandle));
                        FilmKernelNative.Check(kernel.Pause(donor.Handle, donorPaused ? 1 : 0));
                    }
                    catch (Exception error) { Report(error); }
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
        // The coordinator may await StopAsync on this owner. Do not hold its
        // command gate while awaiting external admission, even on cancellation.
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
        FilmKernelNative.Check(kernel.Pause(session.Handle, value ? 1 : 0));
    }, value);
    public Task SeekAsync(NativePlaybackScope scope, double seconds) => Command(scope, session =>
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new PlaybackException(PlaybackError.InvalidSeek);
        FilmKernelNative.Check(kernel.Seek(session.Handle, seconds));
    });
    public Task RateAsync(NativePlaybackScope scope, double rate) => Command(scope, session =>
    {
        if (!double.IsFinite(rate) || rate < .25 || rate > 4) throw new PlaybackException(PlaybackError.InvalidRate);
        FilmKernelNative.Check(kernel.Rate(session.Handle, rate));
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
        FilmKernelNative.Check(kernel.Aspect(session.Handle, ratio));
    });

    private Task Command(NativePlaybackScope expected, Action<Session> action, bool? pauseIntent = null) => Dispatch(async () =>
    {
        ThrowIfClosed();
        var session = current;
        if (session is null || session.Scope != expected) throw new PlaybackException(PlaybackError.Superseded);
        await commands.WaitAsync();
        try
        {
            if (!ReferenceEquals(current, session)) throw new PlaybackException(PlaybackError.Superseded);
            session.Token.ThrowIfCancellationRequested();
            await Task.Run(() => action(session));
            session.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(current, session) || disposing)
                throw new PlaybackException(PlaybackError.Superseded);
            if (pauseIntent is { } value) Set(ref paused, value, nameof(PauseRequested));
            Publish(kernel.Read(session.Handle));
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
        // Stop also revokes a prepared replacement belonging to this playback.
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
    });

    private async Task SetVolumeAsync(Session session)
    {
        var requested = volume / 100d;
        try { await Command(session.Scope, s => FilmKernelNative.Check(kernel.Volume(s.Handle, requested))); }
        catch (Exception error) { if (ReferenceEquals(current, session)) Report(error); }
    }
    private async void Poll(object? sender, EventArgs args) => await RefreshAsync();

    internal Task RefreshAsync() => Dispatch(RefreshCoreAsync);

    private async Task RefreshCoreAsync()
    {
        if (polling || disposing || current is not { } session || !commands.Wait(0)) return;
        polling = true;
        try
        {
            if (session.Token.IsCancellationRequested)
                throw new PlaybackException(PlaybackError.ResourceRevoked);
            var snapshot = kernel.Read(session.Handle);
            if (snapshot.Error != 0 || snapshot.State == 6)
                throw new PlaybackException(session.Error ?? FilmKernelNative.Code(snapshot.Error));
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
            var failure = error;
            try { await RetireAsync(session); }
            catch (Exception releaseError) { failure = releaseError; }
            if (ReferenceEquals(current, session)) current = null;
            if (!disposing && scope == session.Scope)
            {
                try { Report(failure); }
                finally { ClearOutput(); }
            }
        }
        finally { polling = false; commands.Release(); }
    }
    private void Publish(FilmKernelNative.Snapshot value)
    {
        Set(ref output, scope is { } currentScope && value.SubmittedPictures > 0 ?
            new FilmOutputObservation(currentScope, value.SubmittedPictures,
                value.NativeHdr != 0, value.DolbyVision != 0, value.HardwareDecoding != 0) : null, nameof(Output));
        Set(ref position, double.IsFinite(value.Position) ? Math.Max(0, value.Position) : 0, nameof(PositionSeconds));
        Set(ref duration, double.IsFinite(value.Duration) ? Math.Max(0, value.Duration) : 0, nameof(DurationSeconds));
        Set(ref buffering, value.State == 4, nameof(IsBuffering));
    }
    private void ClearOutput()
    {
        timer.Stop();
        Set(ref output, null, nameof(Output));
        Set(ref tracks, null, nameof(Tracks));
        Set(ref scope, null, nameof(Scope)); Changed(nameof(Identity));
        Set(ref hasOutput, false, nameof(HasOutput)); Set(ref buffering, false, nameof(IsBuffering));
        Set(ref paused, false, nameof(PauseRequested));
        Set(ref position, 0, nameof(PositionSeconds)); Set(ref duration, 0, nameof(DurationSeconds));
    }
    private void RequirePending(Session session)
    {
        ThrowIfClosed(); session.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(pending, session)) throw new PlaybackException(PlaybackError.Superseded);
    }
    private async Task RetireAsync(Session session)
    {
        session.Cancel();
        try { await session.ReleaseAsync(); }
        catch { releaseFaulted = true; throw new PlaybackException(PlaybackError.ReleaseFailed); }
        lock (ownership) sessions.Remove(session);
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
        if (releaseFaulted) throw new PlaybackException(PlaybackError.ReleaseFailed);
    }
    private void Report(Exception error)
    {
        Set(ref lastError, Safe(error).Code, nameof(LastErrorCode));
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
    private Task Dispatch(Func<Task> action) => dispatcher.CheckAccess() ? action() :
        dispatcher.InvokeAsync(action).Task.Unwrap();
    private Task<T> Dispatch<T>(Func<Task<T>> action) => dispatcher.CheckAccess() ? action() :
        dispatcher.InvokeAsync(action).Task.Unwrap();

    public ValueTask DisposeAsync() => new(Dispatch(() => disposal ??= DisposeCoreAsync()));
    private async Task DisposeCoreAsync()
    {
        Session[] owned;
        lock (ownership) { disposing = true; owned = sessions.ToArray(); }
        timer.Stop(); timer.Tick -= Poll;
        pending = null; current = null; ClearOutput();
        foreach (var session in owned) session.Cancel();
        await commands.WaitAsync();
        try
        {
            // A failure keeps the native target alive; never destroy a window still in use.
            await Task.WhenAll(owned.Select(RetireAsync));
            surface.Dispose();
        }
        finally { commands.Release(); }
    }
}

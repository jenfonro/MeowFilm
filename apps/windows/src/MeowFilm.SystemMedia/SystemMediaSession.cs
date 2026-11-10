using System.ComponentModel;
using System.Windows.Threading;
using MeowFilm.Media;

namespace MeowFilm.SystemMedia;

/// <summary>
/// One window attachment per App. It neither chooses a source nor owns a
/// player. Captured owner and item identities fence queued system commands.
/// Dispatcher-affine updates; errors never expose provider/OS messages.
/// </summary>
public sealed class SystemMediaSession : IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly ISystemPlayback playback;
    private readonly ISystemMediaBackend backend;
    private readonly Dictionary<string, SystemPlaybackOwner> owners;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly HashSet<Task> running = [];
    private bool enabled;
    private bool stopped;
    private bool disposed;
    private long generation;
    private string? owner, identity;
    private Target published = new(0, null, null, false);
    private Task? disposing;
    private int pending;
    private long interruption;
    public string? LastErrorCode { get; private set; }

    public SystemMediaSession(ISystemPlayback playback, ISystemMediaBackend backend,
        IEnumerable<SystemPlaybackOwner> owners, Dispatcher? dispatcher = null, bool enabled = true)
    {
        this.dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        this.dispatcher.VerifyAccess();
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.enabled = enabled;
        this.owners = owners.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (this.owners.Count == 0 || this.owners.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("SYSTEM_MEDIA_OWNERS");
        playback.PropertyChanged += Changed;
        backend.Requested += Requested;
        try
        {
            if (enabled) Publish();
            else backend.Disable();
        }
        catch
        {
            playback.PropertyChanged -= Changed;
            backend.Requested -= Requested;
            lifetime.Dispose();
            throw;
        }
    }

    public SystemMediaSession(NativePlayback playback, ISystemMediaBackend backend,
        IEnumerable<SystemPlaybackOwner> owners, Dispatcher? dispatcher = null, bool enabled = true)
        : this(new NativeSource(playback), backend, owners, dispatcher, enabled) { }

    /// <summary>Call on the owning dispatcher; this does not stop playback or resume a suspended session.</summary>
    public void SetEnabled(bool enabled)
    {
        dispatcher.VerifyAccess();
        if (disposed || stopped || this.enabled == enabled) return;
        this.enabled = enabled;
        if (enabled) PublishSafely();
        else
        {
            generation++;
            Volatile.Write(ref published, new(generation, owner, identity, false));
            try { backend.Disable(); }
            catch { LastErrorCode = "SYSTEM_MEDIA_DISABLE_FAILED"; }
        }
    }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (disposed || stopped) return;
        if (dispatcher.CheckAccess()) PublishSafely();
        else if (!dispatcher.HasShutdownStarted)
        {
            try { _ = dispatcher.InvokeAsync(PublishSafely); }
            catch (InvalidOperationException) { }
        }
    }

    private void PublishSafely()
    {
        if (disposed || stopped || !enabled) return;
        try { Publish(); }
        catch { LastErrorCode = "SYSTEM_MEDIA_UPDATE_FAILED"; }
    }

    private void Publish()
    {
        var state = playback.State;
        if (owner != state.OwnerId || identity != state.Identity)
        {
            owner = state.OwnerId;
            identity = state.Identity;
            generation++;
        }
        Volatile.Write(ref published, new(generation, owner, identity, enabled));
        bool available = state.HasMedia && state.OwnerId is not null && owners.ContainsKey(state.OwnerId);
        var registered = available ? owners[state.OwnerId!] : null;
        backend.Update(state with
        {
            HasMedia = available, IsPlaying = available && state.IsPlaying,
            CanNext = registered?.Next is not null, CanPrevious = registered?.Previous is not null,
            Kind = registered?.Kind ?? SystemPlaybackKind.Audio
        });
    }

    private void Requested(object? sender, SystemPlaybackRequest request)
    {
        // WinRT events can arrive on a pool thread. Do not touch WPF or the player
        // there, and bound the number of posted/queued requests before allocation.
        var target = Volatile.Read(ref published);
        if (!target.Enabled) return;
        if (Interlocked.Increment(ref pending) > 16)
        {
            Interlocked.Decrement(ref pending);
            return;
        }
        if (dispatcher.HasShutdownStarted)
        {
            Interlocked.Decrement(ref pending);
            return;
        }
        try
        {
            var operation = dispatcher.InvokeAsync(() =>
            {
                if (disposed || stopped)
                {
                    Interlocked.Decrement(ref pending);
                    return;
                }
                if (Volatile.Read(ref published) != target)
                {
                    Interlocked.Decrement(ref pending);
                    return;
                }
                var serial = request.Command is SystemPlaybackCommand.Pause or SystemPlaybackCommand.Stop
                    ? ++interruption : interruption;
                var task = Execute(request, target.Generation, target.Owner, target.Identity, serial);
                running.Add(task);
                _ = Reap(task);
            });
            _ = ObserveDispatch(operation.Task);
        }
        catch (InvalidOperationException) { Interlocked.Decrement(ref pending); }
    }

    private async Task ObserveDispatch(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { Interlocked.Decrement(ref pending); }
    }

    private async Task Reap(Task task)
    {
        try { await task; }
        catch { LastErrorCode = "SYSTEM_MEDIA_COMMAND_FAILED"; }
        finally { running.Remove(task); Interlocked.Decrement(ref pending); }
    }

    private async Task Execute(SystemPlaybackRequest request, long issued, string? expectedOwner, string? expectedIdentity,
        long serial)
    {
        bool entered = false;
        try
        {
            // Pause/Stop must reach the player's synchronous intent fence while an
            // older Play/Next is awaiting I/O. The player still serializes native work.
            if (request.Command is not (SystemPlaybackCommand.Pause or SystemPlaybackCommand.Stop))
            {
                await commands.WaitAsync(lifetime.Token);
                entered = true;
            }
            var state = playback.State;
            if (stopped || disposed || !enabled || generation != issued || serial != interruption || !state.HasMedia ||
                state.OwnerId != expectedOwner || state.Identity != expectedIdentity ||
                expectedOwner is null || !owners.TryGetValue(expectedOwner, out var registered)) return;
            switch (request.Command)
            {
                case SystemPlaybackCommand.Play:
                    if (registered.Play is { } play) await play(lifetime.Token);
                    else await playback.PlayAsync();
                    break;
                case SystemPlaybackCommand.Pause: await playback.PauseAsync(); break;
                case SystemPlaybackCommand.Stop: await playback.StopAsync(); break;
                case SystemPlaybackCommand.Next when registered.Next is not null:
                    await registered.Next(lifetime.Token); break;
                case SystemPlaybackCommand.Previous when registered.Previous is not null:
                    await registered.Previous(lifetime.Token); break;
                case SystemPlaybackCommand.Seek when double.IsFinite(request.PositionSeconds) &&
                    request.PositionSeconds >= 0 && state.DurationSeconds > 0 &&
                    request.PositionSeconds <= state.DurationSeconds:
                    await playback.SeekAsync(request.PositionSeconds); break;
            }
        }
        catch (OperationCanceledException) { }
        catch { LastErrorCode = "SYSTEM_MEDIA_COMMAND_FAILED"; }
        finally { if (entered) commands.Release(); }
    }

    /// <summary>Permanently suspend on Closed or Dispose; use SetEnabled(false) for reversible shutdown preparation.</summary>
    public void Suspend()
    {
        dispatcher.VerifyAccess();
        if (stopped) return;
        stopped = true;
        generation++;
        Volatile.Write(ref published, new(generation, owner, identity, false));
        playback.PropertyChanged -= Changed;
        backend.Requested -= Requested;
        _ = ObserveCancellation(lifetime.CancelAsync());
        try { backend.Disable(); }
        catch { LastErrorCode = "SYSTEM_MEDIA_DISABLE_FAILED"; }
    }

    private static async Task ObserveCancellation(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        dispatcher.VerifyAccess();
        return new(disposing ??= DisposeCore());
    }

    private async Task DisposeCore()
    {
        Suspend();
        disposed = true;
        try { backend.Dispose(); }
        finally
        {
            // No dispatcher-blocking wait. A non-cooperative capability command is
            // not claimed terminated merely because this bounded wait expired.
            var all = Task.WhenAll(running.ToArray());
            try { await all.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch
            {
                _ = ObserveCancellation(all);
                LastErrorCode = "SYSTEM_MEDIA_DRAIN_FAILED";
                throw new InvalidOperationException("SYSTEM_MEDIA_DRAIN_FAILED");
            }
            finally { if (all.IsCompleted) lifetime.Dispose(); }
        }
    }

    internal sealed class NativeSource(NativePlayback native) : ISystemPlayback
    {
        public SystemPlaybackState State => new(native.OwnerId, native.Identity, native.Title,
            native.HasMedia || native.ContinuationPending, native.IsPlaying, native.PositionSeconds, native.DurationSeconds);
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => native.PropertyChanged += value;
            remove => native.PropertyChanged -= value;
        }
        public Task PlayAsync() => native.ResumeAsync();
        public Task PauseAsync() => native.PauseAsync();
        public Task StopAsync() => native.StopAsync();
        public Task SeekAsync(double seconds) => native.SeekAsync(seconds);
    }
    private sealed record Target(long Generation, string? Owner, string? Identity, bool Enabled);
}

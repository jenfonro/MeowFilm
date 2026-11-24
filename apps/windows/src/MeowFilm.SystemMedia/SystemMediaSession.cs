using System.ComponentModel;
using MeowFilm.Core.Debug;
using MeowFilm.Threading;

namespace MeowFilm.SystemMedia;

/// <summary>
/// One window attachment per App. It neither chooses a source nor owns a
/// player. Captured owner and item identities fence queued system commands.
/// Dispatcher-affine updates; errors never expose provider/OS messages.
/// </summary>
public sealed class SystemMediaSession : IAsyncDisposable
{
    private readonly OwnerDispatcher dispatcher;
    private readonly ISystemPlayback playback;
    private readonly WindowsSystemMediaBackend backend;
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
    private Task? drain;
    private Task? cancellation;
    private int pending;
    private long interruption;
    public string? LastErrorCode { get; private set; }

    public SystemMediaSession(ISystemPlayback playback, WindowsSystemMediaBackend backend,
        IEnumerable<SystemPlaybackOwner> owners, OwnerDispatcher? dispatcher = null, bool enabled = true)
    {
        this.dispatcher = dispatcher ?? OwnerDispatcher.Current;
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
            catch (Exception error)
            {
                LastErrorCode = "SYSTEM_MEDIA_DISABLE_FAILED";
                if (DebugLog.IsEnabled)
                    DebugLog.Warn(DebugCategories.MusicPlayback, "system.media", "disable.failed", error: error);
            }
        }
    }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (disposed || stopped) return;
        if (dispatcher.HasThreadAccess) PublishSafely();
        else if (!dispatcher.IsClosing)
        {
            _ = PublishOnOwnerAsync();
        }
    }

    private async Task PublishOnOwnerAsync()
    {
        try { await dispatcher.EnqueueAsync(PublishSafely).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* Owner shutdown retires this update. */ }
        catch (InvalidOperationException) when (dispatcher.IsClosing) { }
    }

    private void PublishSafely()
    {
        if (disposed || stopped || !enabled) return;
        try { Publish(); }
        catch (Exception error)
        {
            LastErrorCode = "SYSTEM_MEDIA_UPDATE_FAILED";
            if (DebugLog.IsEnabled)
                DebugLog.Warn(DebugCategories.MusicPlayback, "system.media", "update.failed", error: error);
        }
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
        // WinRT events can arrive on a pool thread. Do not touch owner state or the player
        // there, and bound the number of posted/queued requests before allocation.
        var target = Volatile.Read(ref published);
        if (!target.Enabled) return;
        if (Interlocked.Increment(ref pending) > 16)
        {
            Interlocked.Decrement(ref pending);
            return;
        }
        if (dispatcher.IsClosing)
        {
            Interlocked.Decrement(ref pending);
            return;
        }
        try
        {
            var operation = dispatcher.EnqueueAsync(() =>
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
            _ = ObserveDispatch(operation);
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
                case SystemPlaybackCommand.Seek when state.CanSeek && double.IsFinite(request.PositionSeconds) &&
                    request.PositionSeconds >= 0 && state.DurationSeconds > 0 &&
                    request.PositionSeconds <= state.DurationSeconds:
                    await playback.SeekAsync(request.PositionSeconds); break;
            }
            if (DebugLog.IsEnabled)
                DebugLog.Debug(DebugCategories.MusicPlayback, "system.media", "command.handled",
                    new { command = request.Command.ToString(), owner = expectedOwner, identity = expectedIdentity });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            LastErrorCode = "SYSTEM_MEDIA_COMMAND_FAILED";
            if (DebugLog.IsEnabled)
                DebugLog.Warn(DebugCategories.MusicPlayback, "system.media", "command.failed",
                    new { command = request.Command.ToString(), owner = expectedOwner }, error: error);
        }
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
        cancellation = ObserveCancellation(lifetime.CancelAsync());
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
        drain ??= DisposeCore();
        if (disposing is null || disposing.IsFaulted || disposing.IsCanceled)
            disposing = ObserveDisposalAsync(drain);
        return new(disposing);
    }

    private async Task ObserveDisposalAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            LastErrorCode = "SYSTEM_MEDIA_DRAIN_FAILED";
            throw new InvalidOperationException("SYSTEM_MEDIA_DRAIN_FAILED");
        }
    }

    private async Task DisposeCore()
    {
        Suspend();
        disposed = true;
        try { backend.Dispose(); }
        catch { LastErrorCode = "SYSTEM_MEDIA_DISABLE_FAILED"; }
        // Keep the actual drain alive after an observer times out. Retrying
        // observes this same work without disposing the backend again.
        try
        {
            await Task.WhenAll(running.ToArray());
            if (cancellation is not null) await cancellation;
        }
        finally { lifetime.Dispose(); }
    }

    private sealed record Target(long Generation, string? Owner, string? Identity, bool Enabled);
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MeowFilm.SystemMedia;

namespace MeowFilm.Windows.Tests;

public static class SystemMediaTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("system-media.metadata-and-owner-capabilities", () => Sta(Metadata));
        yield return new("system-media.commands-route-to-one-player", () => Sta(Commands));
        yield return new("system-media.owner-play-is-authoritative-without-native-fallback", () => Sta(OwnerPlay));
        yield return new("system-media.queue-only-explicit-owner", () => Sta(QueueOwner));
        yield return new("system-media.stale-command-after-owner-change", () => Sta(Stale));
        yield return new("system-media.stale-command-before-dispatch", () => Sta(StaleDispatch));
        yield return new("system-media.seek-bounds", () => Sta(SeekBounds));
        yield return new("system-media.unknown-owner-disabled", () => Sta(UnknownOwner));
        yield return new("system-media.command-failure-is-sanitized", () => Sta(Failure));
        yield return new("system-media.suspend-detaches-and-disables", () => Sta(Suspend));
        yield return new("system-media.dispose-drains-inflight-once", () => Sta(Drain));
        yield return new("system-media.pending-commands-bounded", () => Sta(Bounded));
        yield return new("system-media.real-hwnd-activation-and-release", () => Sta(RealBackend));
    }

    private static async Task Metadata()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend,
            [new("audio", _ => Task.CompletedTask), new("video", Kind: SystemPlaybackKind.Video)]);
        Check(backend.Last is { HasMedia: true, CanNext: true, CanPrevious: false, Kind: SystemPlaybackKind.Audio });
        player.Change("video", "film-2");
        Check(backend.Last is { OwnerId: "video", Identity: "film-2", CanNext: false, Kind: SystemPlaybackKind.Video });
        player.State = player.State with { HasMedia = false }; player.Notify();
        Check(!backend.Last!.HasMedia);
    }
    private static async Task Commands()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")]);
        foreach (var command in new[] { SystemPlaybackCommand.Play, SystemPlaybackCommand.Pause, SystemPlaybackCommand.Stop })
            backend.Request(command);
        await Pump();
        Check(player.Calls.SequenceEqual(["play", "pause", "stop"]));
    }
    private static async Task QueueOwner()
    {
        var player = new Player(); var backend = new Backend(); int next = 0, previous = 0, other = 0;
        await using var session = new SystemMediaSession(player, backend,
            [new("audio", _ => { next++; return Task.CompletedTask; }, _ => { previous++; return Task.CompletedTask; }),
             new("other", _ => { other++; return Task.CompletedTask; })]);
        backend.Request(SystemPlaybackCommand.Next); backend.Request(SystemPlaybackCommand.Previous);
        await Pump();
        Check(next == 1 && previous == 1 && other == 0 && player.Calls.Count == 0);
    }
    private static async Task OwnerPlay()
    {
        var player = new Player(); var backend = new Backend(); int requested = 0;
        await using var session = new SystemMediaSession(player, backend,
            [new("audio", Play: _ => { requested++; return Task.CompletedTask; })]);
        backend.Request(SystemPlaybackCommand.Play); await Pump();
        Check(requested == 1 && player.Calls.Count == 0);
        backend.Request(SystemPlaybackCommand.Pause); await Pump();
        Check(player.Calls.SequenceEqual(["pause"]));
    }
    private static async Task Stale()
    {
        var player = new Player(); var backend = new Backend();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new SystemMediaSession(player, backend,
            [new("audio", _ => { entered.TrySetResult(); return blocked.Task; }),
             new("video", _ => { drained.TrySetResult(); return Task.CompletedTask; })]);
        try
        {
            backend.Request(SystemPlaybackCommand.Next);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            // Pause/Stop intentionally bypass the gate to fence pending playback.
            // Play must really be queued before changing the captured owner.
            backend.Request(SystemPlaybackCommand.Play);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            player.Change("video", "next");
            backend.Request(SystemPlaybackCommand.Next);
            blocked.TrySetResult();
            // This new-owner marker passes the same gate AFTER the stale Play.
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(player.Calls.Count == 0);
        }
        finally { blocked.TrySetResult(); }
    }
    private static async Task StaleDispatch()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio"), new("video")]);
        // Event is raised before the dispatcher can process it. The target must
        // be captured at event arrival, not after switching to a different item.
        backend.Request(SystemPlaybackCommand.Pause);
        player.Change("video", "next");
        await Pump();
        Check(player.Calls.Count == 0);
    }
    private static async Task SeekBounds()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")]);
        foreach (double value in new[] { -1d, double.NaN, double.PositiveInfinity, 121d, 30d })
            backend.Request(SystemPlaybackCommand.Seek, value);
        await Pump();
        Check(player.Calls.SequenceEqual(["seek:30"]));
    }
    private static async Task UnknownOwner()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("another")]);
        Check(!backend.Last!.HasMedia);
        backend.Request(SystemPlaybackCommand.Play); await Pump();
        Check(player.Calls.Count == 0);
    }
    private static async Task Failure()
    {
        var player = new Player { Throw = true }; var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")]);
        backend.Request(SystemPlaybackCommand.Play); await Pump();
        Check(session.LastErrorCode == "SYSTEM_MEDIA_COMMAND_FAILED");
        player.Throw = false;
        backend.Request(SystemPlaybackCommand.Pause); await Pump();
        Check(player.Calls.Last() == "pause");
    }
    private static async Task Suspend()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")]);
        session.Suspend(); session.Suspend();
        backend.Request(SystemPlaybackCommand.Play); player.Notify(); await Pump();
        Check(backend.Disabled == 1 && player.Calls.Count == 0);
    }
    private static async Task Drain()
    {
        var player = new Player(); var backend = new Backend();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SystemMediaSession(player, backend, [new("audio", _ => blocked.Task)]);
        backend.Request(SystemPlaybackCommand.Next); await Pump();
        var dispose = session.DisposeAsync().AsTask();
        Check(!dispose.IsCompleted);
        blocked.SetResult(); await dispose; await session.DisposeAsync();
        Check(backend.Disposals == 1);
    }
    private static async Task Bounded()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")]);
        for (int i = 0; i < 100; i++) backend.Request(SystemPlaybackCommand.Play);
        await Pump();
        Check(player.Calls.Count == 16);
    }
    private static Task RealBackend()
    {
        var window = new Window { ShowInTaskbar = false, Width = 100, Height = 100 };
        try
        {
            using var backend = new WindowsSystemMediaBackend(window);
            backend.Update(new(null, null, "", false, false, 0, 0));
            backend.Disable();
            try
            {
                using var duplicate = new WindowsSystemMediaBackend(window);
                throw new InvalidOperationException("Duplicate system session accepted");
            }
            catch (InvalidOperationException error) when (error.Message == "SYSTEM_MEDIA_ALREADY_OWNED") { }
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

    private static async Task Pump()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(10);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("System media assertion"); }
    private static async Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(thread.Join(TimeSpan.FromSeconds(5)));
    }
    private sealed class Player : ISystemPlayback
    {
        public SystemPlaybackState State { get; set; } = new("audio", "one", "Generated fixture", true, true, 10, 120);
        public readonly List<string> Calls = [];
        public bool Throw;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Notify() => PropertyChanged?.Invoke(this, new(null));
        public void Change(string owner, string id) { State = State with { OwnerId = owner, Identity = id }; Notify(); }
        private Task Call(string value)
        {
            Calls.Add(value);
            return Throw ? Task.FromException(new Exception("sensitive-fixture-error")) : Task.CompletedTask;
        }
        public Task PlayAsync() => Call("play");
        public Task PauseAsync() => Call("pause");
        public Task StopAsync() => Call("stop");
        public Task SeekAsync(double seconds) => Call("seek:" + seconds);
    }
    private sealed class Backend : ISystemMediaBackend
    {
        public SystemPlaybackState? Last;
        public int Disabled, Disposals;
        public event EventHandler<SystemPlaybackRequest>? Requested;
        public void Request(SystemPlaybackCommand command, double seconds = 0) => Requested?.Invoke(this, new(command, seconds));
        public void Update(SystemPlaybackState state) => Last = state;
        public void Disable() => Disabled++;
        public void Dispose() => Disposals++;
    }
}

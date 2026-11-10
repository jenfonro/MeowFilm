using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MeowFilm.SystemMedia;
using Windows.Media;

namespace MeowFilm.Windows.Tests;

public static class SystemMediaTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("system-media.metadata-and-owner-capabilities", () => Sta(Metadata));
        yield return new("system-media.initially-disabled", () => Sta(InitiallyDisabled));
        yield return new("system-media.enable-toggle-is-immediate", () => Sta(EnableToggle));
        yield return new("system-media.enable-toggle-requires-owning-dispatcher", () => Sta(EnableThread));
        yield return new("system-media.toggle-invalidates-undispatched-requests", () => Sta(ToggleBeforeDispatch));
        yield return new("system-media.toggle-invalidates-queued-requests", () => Sta(ToggleQueued));
        yield return new("system-media.disabled-updates-stay-disabled", () => Sta(DisabledUpdates));
        yield return new("system-media.enable-cannot-resume-suspended-session", () => Sta(EnableAfterSuspend));
        yield return new("system-media.enable-cannot-resume-disposed-session", () => Sta(EnableAfterDispose));
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
        Check(backend.Enabled && backend.Disabled == 0 && backend.Updates == 1);
        player.Change("video", "film-2");
        Check(backend.Last is { OwnerId: "video", Identity: "film-2", CanNext: false, Kind: SystemPlaybackKind.Video });
        player.State = player.State with { HasMedia = false }; player.Notify();
        Check(!backend.Last!.HasMedia);
    }
    private static async Task InitiallyDisabled()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")], enabled: false);
        Check(backend.Disabled == 1 && backend.Updates == 0 && !backend.Enabled);
        await Task.Run(() =>
        {
            backend.Request(SystemPlaybackCommand.Play);
            backend.Request(SystemPlaybackCommand.Pause);
            backend.Request(SystemPlaybackCommand.Stop);
            backend.Request(SystemPlaybackCommand.Seek, 30);
        });
        await Pump();
        Check(player.Calls.Count == 0 && player.State.IsPlaying && backend.Updates == 0);
        session.SetEnabled(true);
        Check(backend.Enabled && backend.Last == player.State);
        backend.Request(SystemPlaybackCommand.Pause); await Pump();
        Check(player.Calls.SequenceEqual(["pause"]));
    }
    private static async Task EnableToggle()
    {
        var player = new Player(); var backend = new Backend(); int next = 0, previous = 0;
        await using var session = new SystemMediaSession(player, backend,
            [new("audio", _ => { next++; return Task.CompletedTask; }, _ => { previous++; return Task.CompletedTask; })]);
        session.SetEnabled(false);
        Check(!backend.Enabled && backend.Disabled == 1 && player.Calls.Count == 0 && player.State.IsPlaying);
        foreach (var command in new[] { SystemPlaybackCommand.Play, SystemPlaybackCommand.Pause,
            SystemPlaybackCommand.Stop, SystemPlaybackCommand.Next, SystemPlaybackCommand.Previous, SystemPlaybackCommand.Seek })
            backend.Request(command, 30);
        // Reopen before pumping: requests received while disabled must stay rejected.
        session.SetEnabled(true);
        Check(backend.Enabled && backend.Last == (player.State with { CanNext = true, CanPrevious = true }));
        backend.Request(SystemPlaybackCommand.Play); await Pump();
        Check(player.Calls.SequenceEqual(["play"]) && next == 0 && previous == 0);
    }
    private static async Task ToggleBeforeDispatch()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")]);
        backend.Request(SystemPlaybackCommand.Pause);
        session.SetEnabled(false);
        session.SetEnabled(true);
        backend.Request(SystemPlaybackCommand.Play); await Pump();
        Check(player.Calls.SequenceEqual(["play"]));
    }
    private static async Task EnableThread()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")]);
        bool rejected = await Task.Run(() =>
        {
            try { session.SetEnabled(false); return false; }
            catch (InvalidOperationException) { return true; }
        });
        Check(rejected && backend.Enabled && backend.Disabled == 0);
        session.SetEnabled(false);
        Check(!backend.Enabled && backend.Disabled == 1 && player.Calls.Count == 0);
    }
    private static async Task ToggleQueued()
    {
        var player = new Player(); var backend = new Backend(); int next = 0;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken activeToken = default;
        await using var session = new SystemMediaSession(player, backend,
            [new("audio", token =>
            {
                if (++next == 1)
                {
                    activeToken = token;
                    entered.TrySetResult();
                    return blocked.Task;
                }
                drained.TrySetResult();
                return Task.CompletedTask;
            })]);
        try
        {
            backend.Request(SystemPlaybackCommand.Next);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            backend.Request(SystemPlaybackCommand.Play);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            session.SetEnabled(false);
            Check(!activeToken.IsCancellationRequested && player.Calls.Count == 0 && player.State.IsPlaying);
            session.SetEnabled(true);
            backend.Request(SystemPlaybackCommand.Next);
            blocked.TrySetResult();
            // This marker enters after the queued Play, with the same media identity.
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(next == 2 && player.Calls.Count == 0);
        }
        finally { blocked.TrySetResult(); }
    }
    private static async Task DisabledUpdates()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend,
            [new("audio"), new("video", _ => Task.CompletedTask, Kind: SystemPlaybackKind.Video)]);
        session.SetEnabled(false);
        int updates = backend.Updates;
        player.Change("video", "two");
        for (int i = 0; i < 3; i++)
        {
            player.State = player.State with { Title = "Updated fixture", PositionSeconds = 40 + i };
            player.Notify();
            await Task.Run(player.Notify);
        }
        await Pump();
        Check(!backend.Enabled && backend.Updates == updates && backend.Disabled == 1 && player.Calls.Count == 0);
        session.SetEnabled(true);
        Check(backend.Enabled && backend.Updates == updates + 1 &&
            backend.Last == (player.State with { CanNext = true, Kind = SystemPlaybackKind.Video }));
    }
    private static async Task EnableAfterSuspend()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")]);
        session.SetEnabled(false);
        session.Suspend();
        int updates = backend.Updates, disabled = backend.Disabled;
        session.SetEnabled(true);
        session.SetEnabled(false);
        session.SetEnabled(true);
        player.Change("audio", "two");
        backend.Request(SystemPlaybackCommand.Play); await Pump();
        Check(!backend.Enabled && backend.Updates == updates && backend.Disabled == disabled && player.Calls.Count == 0);
    }
    private static async Task EnableAfterDispose()
    {
        var player = new Player(); var backend = new Backend();
        await using var session = new SystemMediaSession(player, backend, [new("audio")]);
        session.SetEnabled(false);
        await session.DisposeAsync();
        int updates = backend.Updates, disabled = backend.Disabled;
        session.SetEnabled(true);
        session.SetEnabled(false);
        session.SetEnabled(true);
        player.Change("audio", "two");
        backend.Request(SystemPlaybackCommand.Play); await Pump();
        Check(!backend.Enabled && backend.Updates == updates && backend.Disabled == disabled &&
            backend.Disposals == 1 && player.Calls.Count == 0);
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
            var controls = SystemMediaTransportControlsInterop.GetForWindow(new WindowInteropHelper(window).Handle);
            var state = new SystemPlaybackState("audio", "one", "Generated fixture", true, true, 10, 120);
            backend.Update(state);
            Check(controls.IsEnabled && controls.DisplayUpdater.MusicProperties.Title == state.Title);
            backend.Disable();
            Check(!controls.IsEnabled && controls.PlaybackStatus == MediaPlaybackStatus.Closed);
            backend.Update(state);
            Check(controls.IsEnabled && controls.DisplayUpdater.MusicProperties.Title == state.Title);
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
        public int Disabled, Disposals, Updates;
        public bool Enabled;
        public event EventHandler<SystemPlaybackRequest>? Requested;
        public void Request(SystemPlaybackCommand command, double seconds = 0) => Requested?.Invoke(this, new(command, seconds));
        public void Update(SystemPlaybackState state) { Last = state; Updates++; Enabled = state.HasMedia; }
        public void Disable() { Disabled++; Enabled = false; }
        public void Dispose() => Disposals++;
    }
}

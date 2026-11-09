using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MeowFilm.AppHost;
using MeowFilm.Desktop;
using MeowFilm.SystemMedia;

namespace MeowFilm.Windows.Tests;

public static class SystemMediaWindowLifecycleTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("system-media.window-close-prompt-and-cancel-keep-session", () => Sta(CancelClose));
        yield return new("system-media.window-hide-and-restore-keep-session", () => Sta(HideAndRestore));
        yield return new("system-media.window-failed-preparation-cancel-restores-owner", () => Sta(CancelFailedPreparation));
        yield return new("system-media.window-actual-disposal-drains-once-and-retires-old-commands", () => Sta(Dispose));
    }

    private static async Task CancelClose()
    {
        var window = CreateWindow();
        var player = new Player(); var backend = new Backend();
        await using var attachment = new SystemMediaWindowAttachment(window, player, backend, [new("audio")]);
        bool promptOpen = false;
        CancelEventHandler closing = (_, args) => { args.Cancel = true; promptOpen = true; };
        window.Closing += closing;
        try
        {
            window.Show();
            window.Close();
            Check(promptOpen && window.IsVisible, "The close prompt retains the window.");
            player.Publish("While choosing");
            backend.Request(SystemPlaybackCommand.Pause);
            await Pump();
            Check(backend.Last?.Title == "While choosing" && player.Calls.SequenceEqual(["pause"]),
                "Metadata and commands remain active while a close choice is pending.");
            promptOpen = false; // The user cancels the pending choice without starting shutdown.
            backend.Request(SystemPlaybackCommand.Play);
            await Pump();
            window.Close(); // Another close attempt must keep the same live session.
            promptOpen = false;
            player.Publish("After cancel");
            backend.Request(SystemPlaybackCommand.Seek, 25);
            await Pump();
            Check(window.IsVisible && !promptOpen && backend.Last?.Title == "After cancel" &&
                player.Calls.SequenceEqual(["pause", "play", "seek"]),
                "Cancelling a close choice keeps metadata and playback control usable.");
            Check(backend.Disabled == 0 && backend.Disposals == 0 &&
                backend.Subscribers == 1 && player.Subscribers == 1, "Cancelled close does not retire the session.");
        }
        finally { window.Closing -= closing; window.Close(); }
    }

    private static async Task HideAndRestore()
    {
        var window = CreateWindow();
        var player = new Player(); var backend = new Backend();
        await using var attachment = new SystemMediaWindowAttachment(window, player, backend, [new("audio")]);
        CancelEventHandler closing = (_, args) =>
        {
            args.Cancel = true;
            _ = window.Dispatcher.InvokeAsync(window.Hide, DispatcherPriority.Background);
        };
        window.Closing += closing;
        try
        {
            window.Show();
            window.Close();
            await Pump();
            Check(!window.IsVisible, "The cancelled close hides the window.");
            player.Publish("Hidden playback");
            backend.Request(SystemPlaybackCommand.Pause);
            await Pump();
            Check(backend.Last?.Title == "Hidden playback" && player.Calls.SequenceEqual(["pause"]),
                "A hidden window keeps system metadata and playback commands active.");
            window.Show();
            backend.Request(SystemPlaybackCommand.Play);
            await Pump();
            Check(window.IsVisible && player.Calls.SequenceEqual(["pause", "play"]) &&
                backend.Disabled == 0 && backend.Disposals == 0 &&
                backend.Subscribers == 1 && player.Subscribers == 1,
                "Restoring the window uses the same active session without duplicate subscriptions.");
        }
        finally { window.Closing -= closing; window.Close(); }
    }

    private static async Task CancelFailedPreparation()
    {
        var window = CreateWindow();
        var player = new Player(); var backend = new Backend();
        var preparation = Pending();
        var releaseOwner = Pending();
        var owner = new Owner { Preparation = preparation.Task, Release = releaseOwner.Task };
        await using var attachment = new SystemMediaWindowAttachment(window, player, backend,
            [new("audio", Next: owner.Command, Play: owner.Command)]);
        var shutdown = new AppShutdown([owner],
            [new(attachment.DisposeAsync), new(() => { player.Dispose(); return ValueTask.CompletedTask; })]);
        Task? closingWork = null;
        CancelEventHandler closing = (_, args) => { args.Cancel = true; closingWork ??= shutdown.CloseAsync(); };
        window.Closing += closing;
        try
        {
            window.Show();
            backend.Request(SystemPlaybackCommand.Play);
            await Pump();
            Check(owner.Commands == 1, "The registered owner handles system Play.");
            window.Close();
            Check(closingWork is { IsCompleted: false } && owner.Preparations == 1,
                "The close waits for the real App shutdown preparation.");
            backend.Request(SystemPlaybackCommand.Play);
            backend.Request(SystemPlaybackCommand.Next);
            backend.Request(SystemPlaybackCommand.Pause);
            await Pump();
            Check(owner.Commands == 1 && player.Calls.SequenceEqual(["pause"]),
                "Owner preparation seals business admission while the App-owned player remains alive.");
            preparation.TrySetException(new InvalidOperationException("PREPARATION_FIXTURE_FAILURE"));
            try
            {
                await closingWork!;
                throw new InvalidOperationException("Expected shutdown preparation failure.");
            }
            catch (InvalidOperationException error) when (error.Message == "SHUTDOWN_FAILED") { }
            await shutdown.CancelShutdownPreparationAsync();
            player.Publish("After failed exit cancellation");
            backend.Request(SystemPlaybackCommand.Play);
            backend.Request(SystemPlaybackCommand.Next);
            await Pump();
            Check(window.IsVisible && owner.Commands == 3 && owner.Disposals == 0 &&
                backend.Last?.Title == "After failed exit cancellation" && backend.Disabled == 0 &&
                backend.Disposals == 0 && backend.Subscribers == 1 && player.Subscribers == 1,
                "Cancelling failed shutdown restores owner commands on the retained system session.");
            Check(player.Calls.SequenceEqual(["pause"]), "Owner Play never falls back to the native player.");
            var exiting = shutdown.ForceReleaseAsync();
            Check(owner.Disposals == 1 && !exiting.IsCompleted && backend.Disposals == 0 && player.Disposals == 0,
                "The App retains the attachment and player while the business owner is disposing.");
            backend.Request(SystemPlaybackCommand.Play);
            backend.Request(SystemPlaybackCommand.Next);
            backend.Request(SystemPlaybackCommand.Pause);
            await Pump();
            Check(owner.Commands == 3 && player.Calls.SequenceEqual(["pause", "pause"]) &&
                player.AccessAfterDisposal == 0, "Disposing owners reject business work; direct player commands remain safe.");
            releaseOwner.TrySetResult();
            await exiting;
            Check(shutdown.IsReleased && owner.Disposals == 1 && backend.Disposals == 1 && player.Disposals == 1,
                "Final shutdown retires the attachment before releasing the App-owned player.");
        }
        finally
        {
            preparation.TrySetResult();
            releaseOwner.TrySetResult();
            window.Closing -= closing;
            await shutdown.ForceReleaseAsync();
            window.Close();
        }
    }

    private static async Task Dispose()
    {
        var window = CreateWindow();
        var player = new Player(); var backend = new Backend();
        var entered = Pending(); var release = Pending();
        int ownerCalls = 0, exits = 0, closed = 0;
        CancellationToken commandToken = default;
        await using var attachment = new SystemMediaWindowAttachment(window, player, backend,
            [new("audio", Next: async token =>
            {
                ownerCalls++;
                commandToken = token;
                entered.TrySetResult();
                await release.Task;
            }, Play: _ => { ownerCalls++; return Task.CompletedTask; })]);
        var shutdown = new AppShutdown([],
            [new(attachment.DisposeAsync), new(() => { player.Dispose(); return ValueTask.CompletedTask; })]);
        bool allowClose = false;
        Task? closingWork = null;
        CancelEventHandler closing = (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            if (closingWork is not null) return;
            exits++;
            closingWork = shutdown.CloseAsync();
        };
        window.Closing += closing;
        window.Closed += (_, _) => closed++;
        try
        {
            window.Show();
            backend.Request(SystemPlaybackCommand.Next);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            backend.Request(SystemPlaybackCommand.Play);
            await Pump(); // Play is accepted and waiting behind the running owner operation.
            var lateRequest = backend.CaptureRequest(SystemPlaybackCommand.Play);
            backend.Request(SystemPlaybackCommand.Pause); // Posted before disposal, delivered afterwards.
            window.Close();
            window.Close();
            var repeated = attachment.DisposeAsync().AsTask();
            Check(exits == 1 && closingWork is { IsCompleted: false } && !repeated.IsCompleted &&
                commandToken.IsCancellationRequested && backend.Disabled == 1 && backend.Disposals == 1 && player.Disposals == 0,
                "Actual disposal cancels once and every caller waits for the admitted owner operation.");
            Check(backend.Subscribers == 0 && player.Subscribers == 0, "Disposal detaches both event sources.");
            release.TrySetResult();
            await Task.WhenAll(closingWork!, repeated).WaitAsync(TimeSpan.FromSeconds(4));
            allowClose = true;
            window.Close();
            lateRequest(); // A WinRT callback may have captured the handler before it was detached.
            backend.Request(SystemPlaybackCommand.Seek, 20);
            player.Publish("After release");
            await Pump();
            await attachment.DisposeAsync();
            Check(shutdown.IsReleased && closed == 1 && exits == 1 && ownerCalls == 1 && player.Calls.Count == 0 &&
                player.Disposals == 1 && player.AccessAfterDisposal == 0 && backend.AccessAfterDisposal == 0 &&
                backend.Disabled == 1 && backend.Disposals == 1,
                "Final close releases once; queued and captured old events never touch released resources.");
        }
        finally
        {
            release.TrySetResult();
            await shutdown.ForceReleaseAsync();
            window.Closing -= closing;
            window.Close();
        }
    }

    private static Window CreateWindow() => new()
    {
        ShowInTaskbar = false, ShowActivated = false, Width = 200, Height = 120,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
    };
    private static TaskCompletionSource Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Pump() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Sta(Func<Task> action)
    {
        var done = Pending();
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
        Check(thread.Join(TimeSpan.FromSeconds(5)), "The window test STA must terminate.");
    }

    private sealed class Player : ISystemPlayback, IDisposable
    {
        private SystemPlaybackState state = new("audio", "one", "Lifecycle fixture", true, true, 10, 120);
        private PropertyChangedEventHandler? changed;
        public readonly List<string> Calls = [];
        public int Disposals, AccessAfterDisposal;
        public int Subscribers => changed?.GetInvocationList().Length ?? 0;
        public SystemPlaybackState State { get { RequireAlive(); return state; } }
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => changed += value;
            remove => changed -= value;
        }
        public void Publish(string title) { state = state with { Title = title }; changed?.Invoke(this, new(null)); }
        public Task PlayAsync() => Call("play");
        public Task PauseAsync() => Call("pause");
        public Task StopAsync() => Call("stop");
        public Task SeekAsync(double seconds) => Call("seek");
        private Task Call(string command) { RequireAlive(); Calls.Add(command); return Task.CompletedTask; }
        private void RequireAlive()
        {
            if (Disposals == 0) return;
            AccessAfterDisposal++;
            throw new ObjectDisposedException(nameof(Player));
        }
        public void Dispose() => Disposals++;
    }
    private sealed class Backend : ISystemMediaBackend
    {
        private EventHandler<SystemPlaybackRequest>? requested;
        public SystemPlaybackState? Last;
        public int Disabled, Disposals, AccessAfterDisposal;
        public int Subscribers => requested?.GetInvocationList().Length ?? 0;
        public event EventHandler<SystemPlaybackRequest>? Requested
        {
            add => requested += value;
            remove => requested -= value;
        }
        public void Request(SystemPlaybackCommand command, double seconds = 0) => requested?.Invoke(this, new(command, seconds));
        public Action CaptureRequest(SystemPlaybackCommand command)
        {
            var captured = requested;
            return () => captured?.Invoke(this, new(command));
        }
        public void Update(SystemPlaybackState state)
        {
            if (Disposals != 0)
            {
                AccessAfterDisposal++;
                throw new ObjectDisposedException(nameof(Backend));
            }
            Last = state;
        }
        public void Disable() => Disabled++;
        public void Dispose() => Disposals++;
    }
    private sealed class Owner : IDesktopShutdownParticipant
    {
        private bool stopping;
        public Task Preparation = Task.CompletedTask, Release = Task.CompletedTask;
        public int Commands, Preparations, Disposals;
        public Task Command(CancellationToken token)
        {
            if (stopping) return Task.CompletedTask;
            Check(Disposals == 0, "No owner command may access released business resources.");
            Commands++;
            return Task.CompletedTask;
        }
        public Task PrepareShutdownAsync() { stopping = true; Preparations++; return Preparation; }
        public Task CancelShutdownPreparationAsync() { stopping = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { stopping = true; Disposals++; return new(Release); }
        public Task AbortAsync() => DisposeAsync().AsTask();
    }
}

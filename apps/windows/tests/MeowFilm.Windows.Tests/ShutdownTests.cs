using MeowFilm.AppHost;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class ShutdownTests
{
    private sealed class Participant(List<string> calls, string id) : IDesktopShutdownParticipant
    {
        public Func<Task> Prepare { get; set; } = () => Task.CompletedTask;
        public Func<Task> Cancel { get; set; } = () => Task.CompletedTask;
        public Func<Task> Release { get; set; } = () => Task.CompletedTask;
        public Func<Task> Abort { get; set; } = () => Task.CompletedTask;
        private void Record(string stage) => calls.Add(stage + "-" + id);
        public Task PrepareShutdownAsync() { Record("prepare"); return Prepare(); }
        public Task CancelShutdownPreparationAsync() { Record("cancel"); return Cancel(); }
        public ValueTask DisposeAsync() { Record("dispose"); return new(Release()); }
        public Task AbortAsync() { Record("abort"); return Abort(); }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly List<Alarm> alarms = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var alarm = new Alarm(callback, state); lock (alarms) alarms.Add(alarm); return alarm;
        }
        public void Expire() { Alarm[] all; lock (alarms) all = alarms.ToArray(); foreach (var item in all) item.Fire(); }
        private sealed class Alarm(TimerCallback callback, object? state) : ITimer
        {
            private int ended;
            public void Fire() { if (Interlocked.Exchange(ref ended, 1) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref ended) == 0;
            public void Dispose() => Interlocked.Exchange(ref ended, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static TaskCompletionSource Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Fail() => Task.FromException(new Exception("synthetic-private-details"));
    private static ShutdownResource Resource(List<string> calls, string id, bool interrupts = false, Action? action = null) =>
        new(() => { calls.Add(id); action?.Invoke(); return ValueTask.CompletedTask; }, interrupts);
    private static async Task Fails(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Expected shutdown failure"); }
        catch (InvalidOperationException error) { BridgeTests.Equal("SHUTDOWN_FAILED", error.Message); }
    }
    private static void Order(List<string> calls, params string[] expected) => BridgeTests.Equal(string.Join('|', expected), string.Join('|', calls));
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("shutdown: all prepare before release; shared prepares first and releases after domains", async () =>
        {
            var calls = new List<string>(); var first = new Participant(calls, "1"); var second = new Participant(calls, "2"); var shared = new Participant(calls, "shared");
            var shutdown = new AppShutdown([first, second], [Resource(calls, "media"), Resource(calls, "runtime", true), Resource(calls, "store")], sharedParticipants: [shared]);
            var task = shutdown.CloseAsync(); BridgeTests.Equal(true, ReferenceEquals(task, shutdown.CloseAsync()));
            await task; await shutdown.ForceReleaseAsync(); BridgeTests.Equal(true, shutdown.IsReleased);
            Order(calls, "prepare-shared", "prepare-1", "prepare-2", "dispose-1", "dispose-2", "dispose-shared", "media", "runtime", "store");
        });
        yield return new("shutdown: failed prepare reverses supported owners without releasing; explicit resume allows a new attempt", async () =>
        {
            var calls = new List<string>(); var domain = new Participant(calls, "domain") { Prepare = Fail }; var shared = new Participant(calls, "shared");
            var shutdown = new AppShutdown([domain], [Resource(calls, "store")], sharedParticipants: [shared]);
            var failed = shutdown.CloseAsync(); await Fails(failed); await Fails(shutdown.CloseAsync());
            Order(calls, "prepare-shared", "prepare-domain", "cancel-domain", "cancel-shared");
            await shutdown.CancelShutdownPreparationAsync(); domain.Prepare = () => Task.CompletedTask;
            var next = shutdown.CloseAsync(); BridgeTests.Equal(false, ReferenceEquals(failed, next)); await next;
            Order(calls, "prepare-shared", "prepare-domain", "cancel-domain", "cancel-shared", "prepare-shared", "prepare-domain", "dispose-domain", "dispose-shared", "store");
        });
        yield return new("shutdown: cancellation is a failed prepare and not permission to release", async () =>
        {
            var calls = new List<string>(); var module = new Participant(calls, "1") { Prepare = () => Task.FromCanceled(new(true)) };
            var shutdown = new AppShutdown([module], [Resource(calls, "store")]);
            await Fails(shutdown.CloseAsync()); Order(calls, "prepare-1", "cancel-1");
            await shutdown.ForceReleaseAsync(); Order(calls, "prepare-1", "cancel-1", "abort-1", "store");
        });
        yield return new("shutdown: force interrupts pending runtime preparation before storage", async () =>
        {
            var calls = new List<string>(); var pending = Pending(); var module = new Participant(calls, "1") { Prepare = () => pending.Task };
            var shutdown = new AppShutdown([module], [Resource(calls, "runtime", true, () => pending.TrySetException(new Exception("closed"))), Resource(calls, "store")]);
            var normal = shutdown.CloseAsync(); await shutdown.ForceReleaseAsync(); await Fails(normal);
            Order(calls, "prepare-1", "runtime", "abort-1", "store");
        });
        yield return new("shutdown: force timeout retains store until noncooperative disposal actually settles", async () =>
        {
            var calls = new List<string>(); var blocked = Pending(); var clock = new Clock();
            var module = new Participant(calls, "1") { Release = () => blocked.Task };
            var shutdown = new AppShutdown([module], [Resource(calls, "runtime", true), Resource(calls, "store")], clock);
            var normal = shutdown.CloseAsync(); var forced = shutdown.ForceReleaseAsync();
            BridgeTests.Equal(true, ReferenceEquals(forced, shutdown.ForceReleaseAsync()));
            clock.Expire(); await Fails(forced); BridgeTests.Equal(false, shutdown.IsReleased);
            Order(calls, "prepare-1", "dispose-1", "runtime", "abort-1");
            blocked.TrySetResult(); await shutdown.ForceReleaseAsync(); await Fails(normal);
            Order(calls, "prepare-1", "dispose-1", "runtime", "abort-1", "store");
        });
        yield return new("shutdown: successful abort still waits for old preparation task", async () =>
        {
            var calls = new List<string>(); var blocked = Pending(); var clock = new Clock();
            var module = new Participant(calls, "1") { Prepare = () => blocked.Task };
            var shutdown = new AppShutdown([module], [Resource(calls, "store")], clock);
            var normal = shutdown.CloseAsync(); var forced = shutdown.ForceReleaseAsync(); clock.Expire(); await Fails(forced);
            Order(calls, "prepare-1", "abort-1"); blocked.TrySetResult(); await shutdown.ForceReleaseAsync(); await Fails(normal);
            Order(calls, "prepare-1", "abort-1", "store");
        });
        yield return new("shutdown: successful abort still waits for old disposal task", async () =>
        {
            var calls = new List<string>(); var blocked = Pending(); var clock = new Clock();
            var module = new Participant(calls, "1") { Release = () => blocked.Task };
            var shutdown = new AppShutdown([module], [Resource(calls, "store")], clock);
            var normal = shutdown.CloseAsync(); var forced = shutdown.ForceReleaseAsync(); clock.Expire(); await Fails(forced);
            Order(calls, "prepare-1", "dispose-1", "abort-1"); blocked.TrySetException(new Exception("late"));
            await shutdown.ForceReleaseAsync(); await Fails(normal); Order(calls, "prepare-1", "dispose-1", "abort-1", "store");
        });
        yield return new("shutdown: failed abort retains dependencies and explicit retry only repeats failed stage", async () =>
        {
            var calls = new List<string>(); var module = new Participant(calls, "1") { Abort = Fail };
            var shutdown = new AppShutdown([module], [Resource(calls, "runtime", true), Resource(calls, "store")]);
            await Fails(shutdown.ForceReleaseAsync()); Order(calls, "runtime", "abort-1");
            module.Abort = () => Task.CompletedTask; await shutdown.ForceReleaseAsync();
            Order(calls, "runtime", "abort-1", "abort-1", "store");
        });
        yield return new("shutdown: failed normal dispose requires successful abort before dependency release", async () =>
        {
            var calls = new List<string>(); var module = new Participant(calls, "1") { Release = Fail, Abort = Fail };
            var shutdown = new AppShutdown([module], [Resource(calls, "store")]);
            await Fails(shutdown.CloseAsync()); await Fails(shutdown.ForceReleaseAsync());
            Order(calls, "prepare-1", "dispose-1", "abort-1"); BridgeTests.Equal(false, shutdown.IsReleased);
            module.Abort = () => Task.CompletedTask; await shutdown.ForceReleaseAsync();
            Order(calls, "prepare-1", "dispose-1", "abort-1", "abort-1", "store");
        });
        yield return new("shutdown: blocked interrupt cannot skip to other resources or store", async () =>
        {
            var calls = new List<string>(); var blocked = Pending(); var clock = new Clock();
            var shutdown = new AppShutdown([], [new(() => { calls.Add("runtime"); return new(blocked.Task); }, true), Resource(calls, "media"), Resource(calls, "store")], clock);
            var forced = shutdown.ForceReleaseAsync(); clock.Expire(); await Fails(forced); Order(calls, "runtime");
            blocked.TrySetResult(); await shutdown.ForceReleaseAsync(); Order(calls, "runtime", "media", "store");
        });
        yield return new("shutdown: resource failure retains downstream resources and cannot resume UI", async () =>
        {
            var calls = new List<string>(); var fail = true; var module = new Participant(calls, "1");
            var shutdown = new AppShutdown([module], [Resource(calls, "media", action: () => { if (fail) throw new Exception(); }), Resource(calls, "store")]);
            await Fails(shutdown.CloseAsync()); await Fails(shutdown.CancelShutdownPreparationAsync());
            Order(calls, "prepare-1", "dispose-1", "media"); fail = false; await shutdown.ForceReleaseAsync();
            Order(calls, "prepare-1", "dispose-1", "media", "media", "store");
        });
        yield return new("shutdown: failed rollback can be explicitly retried before reopening", async () =>
        {
            var calls = new List<string>(); var module = new Participant(calls, "1") { Prepare = Fail, Cancel = Fail };
            var shutdown = new AppShutdown([module], [Resource(calls, "store")]);
            await Fails(shutdown.CloseAsync()); await Fails(shutdown.CancelShutdownPreparationAsync());
            module.Cancel = () => Task.CompletedTask; await shutdown.CancelShutdownPreparationAsync();
            module.Prepare = () => Task.CompletedTask; await shutdown.CloseAsync();
            BridgeTests.Equal(3, calls.Count(item => item == "cancel-1")); BridgeTests.Equal(1, calls.Count(item => item == "store"));
        });
        yield return new("shutdown: force waits for in-progress rollback before abort and storage", async () =>
        {
            var calls = new List<string>(); var blocked = Pending(); var clock = new Clock();
            var module = new Participant(calls, "1") { Prepare = Fail, Cancel = () => blocked.Task };
            var shutdown = new AppShutdown([module], [Resource(calls, "store")], clock);
            var normal = shutdown.CloseAsync(); var forced = shutdown.ForceReleaseAsync(); clock.Expire(); await Fails(forced);
            Order(calls, "prepare-1", "cancel-1"); blocked.TrySetResult(); await shutdown.ForceReleaseAsync(); await Fails(normal);
            Order(calls, "prepare-1", "cancel-1", "abort-1", "store");
        });
        yield return new("shutdown: startup cleanup never begins save preparation", async () =>
        {
            var calls = new List<string>(); var domain = new Participant(calls, "domain"); var shared = new Participant(calls, "shared");
            var shutdown = new AppShutdown([domain], [Resource(calls, "store")], sharedParticipants: [shared]);
            await shutdown.ForceReleaseAsync(); await Fails(shutdown.CloseAsync()); await shutdown.ForceReleaseAsync();
            Order(calls, "abort-domain", "abort-shared", "store");
        });
        yield return new("shutdown: rejects duplicates across shared and domain participants and invalid callbacks", () =>
        {
            var module = new Participant([], "1");
            try { _ = new AppShutdown([module], [], sharedParticipants: [module]); throw new Exception("Duplicate accepted"); } catch (ArgumentException) { }
            try { _ = new AppShutdown([], [new(null!)]); throw new Exception("Null callback accepted"); } catch (ArgumentException) { }
            try { _ = new AppShutdown([], [], forceTimeout: TimeSpan.Zero); throw new Exception("Invalid bound accepted"); } catch (ArgumentOutOfRangeException) { }
            return Task.CompletedTask;
        });
    }
}

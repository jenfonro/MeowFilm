using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using MeowFilm.AppHost;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

// Preparation-only probes: no Application, real window, runtime, network or user data.
public static class AppStartupPreparationTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(4);

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("all explicit participants finish before main window actions", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var firstPending = new Pending();
            using var secondPending = new Pending();
            var secondEntered = NewCompletion();
            var events = new List<string> { "modules-created" };
            var caller = Environment.CurrentManagedThreadId;
            var first = new StartupModule("first", async token =>
            {
                Equal(caller, Environment.CurrentManagedThreadId, "preparation starts on the caller thread");
                Equal(cancellation.Token, token, "first participant receives startup cancellation");
                events.Add("first-started");
                await firstPending.Task;
                events.Add("first-ready");
            });
            var ordinary = new DesktopModule("ordinary");
            var second = new StartupModule("second", async token =>
            {
                Equal(cancellation.Token, token, "second participant receives startup cancellation");
                events.Add("second-started");
                secondEntered.TrySetResult();
                await secondPending.Task;
                events.Add("second-ready");
            });
            var fixture = new Fixture([first, ordinary, second], events);
            var work = fixture.StartAsync(cancellation.Token);
            False(work.IsCompleted, "first local preparation holds startup");
            Equal(1, first.PrepareCount, "first participant is called once");
            Equal(0, second.PrepareCount, "later preparation has not started");
            fixture.AssertWindowNotStarted();
            firstPending.Complete();
            await secondEntered.Task.WaitAsync(Guard);
            False(work.IsCompleted, "one completed participant cannot open the main window");
            fixture.AssertWindowNotStarted();
            secondPending.Complete();
            await work.WaitAsync(Guard);
            Equal("modules-created,first-started,first-ready,second-started,second-ready,shell-created,shell-shown",
                string.Join(',', events), "main window follows every local preparation");
            Equal(1, second.PrepareCount, "second participant is called once");
            fixture.AssertModulesDormant();
        });
        foreach (var synchronous in new[] { true, false })
            yield return Case($"{(synchronous ? "synchronous" : "asynchronous")} preparation failure stops later startup actions", async () =>
            {
                using var pending = new Pending();
                var failure = new InvalidOperationException("PREPARATION_FAILED");
                var first = new StartupModule("first", _ => Task.CompletedTask);
                var failed = new StartupModule("failed", _ => synchronous ? throw failure : pending.Task);
                var later = new StartupModule("later", _ => Task.CompletedTask);
                var fixture = new Fixture([first, failed, later]);
                var work = fixture.StartAsync();
                if (!synchronous)
                {
                    False(work.IsCompleted, "pending preparation is awaited");
                    fixture.AssertWindowNotStarted();
                    pending.Fail(failure);
                }
                var observed = await ThrowsAsync<InvalidOperationException>(work);
                True(ReferenceEquals(failure, observed), "original failure reaches startup cleanup");
                Equal(1, first.PrepareCount, "prior participant completed");
                Equal(1, failed.PrepareCount, "failed participant is not retried implicitly");
                Equal(0, later.PrepareCount, "failure prevents later preparation");
                fixture.AssertWindowNotStarted();
                fixture.AssertModulesDormant();
            });
        foreach (var participates in new[] { true, false })
            yield return Case($"cancelled startup stops before {(participates ? "participating" : "ordinary")} modules", async () =>
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var participant = new StartupModule("participant", _ => Task.CompletedTask);
                var fixture = new Fixture(participates ? [participant] : [new DesktopModule("ordinary")]);
                var error = await ThrowsAsync<OperationCanceledException>(fixture.StartAsync(cancellation.Token));
                Equal(cancellation.Token, error.CancellationToken, "startup cancellation is preserved");
                Equal(0, participant.PrepareCount, "cancelled startup invokes no preparation");
                fixture.AssertWindowNotStarted();
                fixture.AssertModulesDormant();
            });
        yield return Case("cancelling held preparation skips later modules and main window actions", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var pending = new Pending();
            var participant = new StartupModule("participant", token => pending.Task.WaitAsync(token));
            var later = new StartupModule("later", _ => Task.CompletedTask);
            var fixture = new Fixture([participant, later]);
            var work = fixture.StartAsync(cancellation.Token);
            False(work.IsCompleted, "preparation is held");
            cancellation.Cancel();
            var error = await ThrowsAsync<OperationCanceledException>(work);
            Equal(cancellation.Token, error.CancellationToken, "participant observes startup cancellation");
            Equal(0, later.PrepareCount, "cancellation stops later preparation");
            fixture.AssertWindowNotStarted();
            fixture.AssertModulesDormant();
        });
        foreach (var hasLaterParticipant in new[] { true, false })
            yield return Case($"late preparation success after cancellation stops {(hasLaterParticipant ? "next participant" : "main window")}", async () =>
            {
                using var cancellation = new CancellationTokenSource();
                using var pending = new Pending();
                var participant = new StartupModule("participant", _ => pending.Task);
                var later = new StartupModule("later", _ => Task.CompletedTask);
                var fixture = new Fixture(hasLaterParticipant ? [participant, later] : [participant]);
                var work = fixture.StartAsync(cancellation.Token);
                cancellation.Cancel();
                False(work.IsCompleted, "cancellation is not evidence that owned preparation settled");
                fixture.AssertWindowNotStarted();
                pending.Complete();
                var error = await ThrowsAsync<OperationCanceledException>(work);
                Equal(cancellation.Token, error.CancellationToken, "late success cannot bypass cancellation");
                Equal(0, later.PrepareCount, "no next participant after cancellation");
                fixture.AssertWindowNotStarted();
                fixture.AssertModulesDormant();
            });
        yield return Case("ordinary modules proceed without preparation or full initialization", async () =>
        {
            var fixture = new Fixture([new DesktopModule("first"), new DesktopModule("second")]);
            var work = fixture.StartAsync();
            True(work.IsCompletedSuccessfully, "no participant introduces no asynchronous wait");
            await work;
            True(fixture.WindowConstructed && fixture.WindowShown, "main window actions continue");
            fixture.AssertModulesDormant();
        });
    }

    private static TestCase Case(string name, Func<Task> body) => new("app-startup-preparation: " + name,
        () => body().WaitAsync(TimeSpan.FromSeconds(8)));
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> ThrowsAsync<T>(Task work) where T : Exception
    {
        try { await work.WaitAsync(Guard); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void True(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void False(bool condition, string message) => True(!condition, message);
    private static void Equal<T>(T expected, T actual, string message)
    { True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}"); }

    private sealed class Pending : IDisposable
    {
        private readonly TaskCompletionSource completion = NewCompletion();
        public Task Task => completion.Task;
        public void Complete() => completion.TrySetResult();
        public void Fail(Exception error) => completion.TrySetException(error);
        public void Dispose() => Complete();
    }

    private sealed class Fixture(IReadOnlyList<DesktopModule> modules, List<string>? events = null)
    {
        public bool WindowConstructed { get; private set; }
        public bool WindowShown { get; private set; }
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await AppLauncher.PrepareModulesAsync(modules, cancellationToken);
            WindowConstructed = true;
            events?.Add("shell-created");
            WindowShown = true;
            events?.Add("shell-shown");
        }
        public void AssertWindowNotStarted() =>
            False(WindowConstructed || WindowShown, "main window construction and Show must wait");
        public void AssertModulesDormant()
        {
            foreach (var module in modules)
            {
                Equal(0, module.InitializeCount, "full initialization remains shell-owned: " + module.Id);
                Equal(0, module.NavigationCount, "preparation does not navigate: " + module.Id);
            }
        }
    }

    private class DesktopModule(string id) : IDesktopModule
    {
        public string Id { get; } = id;
        public string Title => Id;
        public string Version => "test";
        public ObservableCollection<NavigationItem> Navigation { get; } = [];
        public FrameworkElement View => throw new InvalidOperationException("Preparation must not mount views");
        public FrameworkElement SettingsView => throw new InvalidOperationException("Preparation must not mount settings");
        public FrameworkElement? PlayerView => throw new InvalidOperationException("Preparation must not mount player");
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public int InitializeCount { get; private set; }
        public int NavigationCount { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken)
        { InitializeCount++; return Task.CompletedTask; }
        public Task NavigateAsync(string route, CancellationToken cancellationToken)
        { NavigationCount++; return Task.CompletedTask; }
        public Task SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preparation must not search");
        public string CaptureNavigationLocation() =>
            throw new InvalidOperationException("Preparation must not capture navigation");
        public Task RestoreNavigationAsync(string location, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preparation must not restore navigation");
        public Task PrepareShutdownAsync() => Task.CompletedTask;
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StartupModule(string id, Func<CancellationToken, Task> prepare)
        : DesktopModule(id), IDesktopStartupParticipant
    {
        public int PrepareCount { get; private set; }
        public Task PrepareStartupAsync(CancellationToken cancellationToken = default)
        { PrepareCount++; return prepare(cancellationToken); }
    }
}

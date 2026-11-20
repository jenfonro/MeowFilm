using MeowFilm.AppHost;

namespace MeowFilm.Windows.Tests;

public static class StartupTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("starts on caller and shares the active attempt", async () =>
        {
            using var startup = new AppStartup();
            var pending = Pending(); var caller = Environment.CurrentManagedThreadId; int calls = 0;
            var first = startup.StartAsync(_ =>
            {
                BridgeTests.Equal(caller, Environment.CurrentManagedThreadId);
                calls++; return pending.Task;
            });
            BridgeTests.Equal(true, ReferenceEquals(first, startup.StartAsync(_ => throw new Exception("Duplicate"))));
            BridgeTests.Equal(1, calls);
            pending.SetResult(StartupOutcome.Started);
            BridgeTests.Equal(StartupOutcome.Started, await first);
        });
        yield return Case("only a released failed attempt permits retry", async () =>
        {
            using var startup = new AppStartup(); CancellationToken firstToken = default, secondToken = default;
            await startup.StartAsync(token => { firstToken = token; return Task.FromResult(StartupOutcome.RetryableFailure); });
            var second = startup.StartAsync(token => { secondToken = token; return Task.FromResult(StartupOutcome.Started); });
            BridgeTests.Equal(StartupOutcome.Started, await second);
            BridgeTests.Equal(false, firstToken == secondToken);
            BridgeTests.Equal(true, ReferenceEquals(second, startup.StartAsync(_ => throw new Exception("Duplicate"))));
        });
        foreach (var outcome in new[] { StartupOutcome.CleanupFailed, StartupOutcome.Cancelled })
            yield return Case($"{outcome} does not admit another generation", async () =>
            {
                using var startup = new AppStartup();
                var first = startup.StartAsync(_ => Task.FromResult(outcome));
                BridgeTests.Equal(outcome, await first);
                BridgeTests.Equal(true, ReferenceEquals(first, startup.StartAsync(_ => throw new Exception("Unsafe retry"))));
            });
        yield return Case("unclassified fault and malformed results do not certify release", async () =>
        {
            foreach (Func<CancellationToken, Task<StartupOutcome>> action in new Func<CancellationToken, Task<StartupOutcome>>[]
            {
                _ => throw new InvalidOperationException(),
                _ => Task.FromException<StartupOutcome>(new InvalidOperationException()),
                _ => null!,
                _ => Task.FromResult((StartupOutcome)999)
            })
            {
                using var startup = new AppStartup();
                BridgeTests.Equal(StartupOutcome.CleanupFailed, await startup.StartAsync(action));
            }
        });
        yield return Case("dispose cancels promptly without waiting for startup or callbacks", async () =>
        {
            var startup = new AppStartup(); var pending = Pending(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken token = default;
            var work = startup.StartAsync(value => { token = value; return pending.Task; });
            using var registration = token.Register(() => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); });
            try
            {
                startup.Dispose();
                BridgeTests.Equal(true, token.IsCancellationRequested);
                BridgeTests.Equal(false, work.IsCompleted);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                startup.Dispose();
                BridgeTests.Equal(true, ReferenceEquals(work, startup.StartAsync(_ => throw new Exception("Restarted"))));
                pending.SetResult(StartupOutcome.Started);
                BridgeTests.Equal(StartupOutcome.Cancelled, await work);
            }
            finally { release.TrySetResult(); pending.TrySetResult(StartupOutcome.Cancelled); }
        });
        yield return Case("dispose before start never invokes startup", async () =>
        {
            using var startup = new AppStartup(); startup.Dispose();
            BridgeTests.Equal(StartupOutcome.Cancelled, await startup.StartAsync(_ => throw new Exception("Started")));
        });
        yield return Case("concurrent calls accept one attempt", async () =>
        {
            using var startup = new AppStartup(); var pending = Pending(); int calls = 0;
            var works = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
                new[] { startup.StartAsync(_ => { Interlocked.Increment(ref calls); return pending.Task; }) })));
            BridgeTests.Equal(1, calls);
            foreach (var work in works) BridgeTests.Equal(true, ReferenceEquals(works[0][0], work[0]));
            pending.SetResult(StartupOutcome.Started);
            await works[0][0];
        });
    }
    private static TaskCompletionSource<StartupOutcome> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TestCase Case(string name, Func<Task> run) => new("startup: " + name, run);
}

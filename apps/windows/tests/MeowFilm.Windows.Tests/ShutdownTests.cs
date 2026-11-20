using MeowFilm.AppHost;

namespace MeowFilm.Windows.Tests;

public static class ShutdownTests
{
    private sealed class Participant(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }
    private static TaskCompletionSource Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("shutdown: every owner is stopped before waiting and executor interruption releases pending reads", async () =>
        {
            var calls = new List<string>(); var read = Pending();
            var first = new Participant(() => { calls.Add("first"); return new(read.Task); });
            var second = new Participant(() => { calls.Add("second"); return ValueTask.CompletedTask; });
            var shared = new Participant(() => { calls.Add("input"); return ValueTask.CompletedTask; });
            var shutdown = new AppShutdown([first, second], [
                new(() => { calls.Add("runtime"); read.SetResult(); return ValueTask.CompletedTask; }, true),
                new(() => { calls.Add("store"); return ValueTask.CompletedTask; })], sharedParticipants: [shared]);
            var closing = shutdown.CloseAsync();
            BridgeTests.Equal(true, ReferenceEquals(closing, shutdown.CloseAsync()));
            await closing.WaitAsync(TimeSpan.FromSeconds(2));
            BridgeTests.Equal("input|first|second|runtime|store", string.Join('|', calls));
            BridgeTests.Equal(true, shutdown.IsReleased);
        });
        yield return new("shutdown: accepted local write completes before storage release", async () =>
        {
            var commit = Pending(); bool storeClosed = false;
            var shutdown = new AppShutdown([new Participant(() => new(commit.Task))],
                [new(() => { storeClosed = true; return ValueTask.CompletedTask; })]);
            var closing = shutdown.CloseAsync();
            BridgeTests.Equal(false, closing.IsCompleted || storeClosed || shutdown.IsReleased);
            commit.SetResult(); await closing;
            BridgeTests.Equal(true, storeClosed && shutdown.IsReleased);
        });
        yield return new("shutdown: release error preserves its cause and does not block independent owners from stopping", async () =>
        {
            var failure = new InvalidOperationException("fixture-release-failure");
            int attempts = 0, other = 0, resources = 0;
            var shutdown = new AppShutdown([
                new Participant(() => ++attempts == 1 ? ValueTask.FromException(failure) : ValueTask.CompletedTask),
                new Participant(() => { other++; return ValueTask.CompletedTask; })],
                [new(() => { resources++; return ValueTask.CompletedTask; })]);
            try { await shutdown.CloseAsync(); throw new Exception("Expected release failure"); }
            catch (InvalidOperationException error) { BridgeTests.Equal(true, ReferenceEquals(error, failure)); }
            BridgeTests.Equal(1, other); BridgeTests.Equal(0, resources);
            BridgeTests.Equal(false, shutdown.IsReleased);
            await shutdown.CloseAsync();
            BridgeTests.Equal(2, attempts); BridgeTests.Equal(1, other); BridgeTests.Equal(1, resources);
        });
        yield return new("shutdown: rejects duplicate owners and invalid resource callbacks", () =>
        {
            var owner = new Participant(() => ValueTask.CompletedTask);
            try { _ = new AppShutdown([owner], [], sharedParticipants: [owner]); throw new Exception("Duplicate accepted"); }
            catch (ArgumentException) { }
            try { _ = new AppShutdown([], [new(null!)]); throw new Exception("Null callback accepted"); }
            catch (ArgumentException) { }
            return Task.CompletedTask;
        });
    }
}

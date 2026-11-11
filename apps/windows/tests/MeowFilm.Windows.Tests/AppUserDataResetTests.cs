using System.IO;
using MeowFilm.AppHost;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class AppUserDataResetTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("User data reset: concurrent confirmation waits for actual owner drain before deleting", Drain);
        yield return new("User data reset: request failure does not release or delete and can retry", RequestFailure);
        yield return new("User data reset: release failure retains data and cannot reopen", ReleaseFailure);
        yield return new("User data reset: deletion failure retries without reviving or releasing old owners twice", ClearFailure);
        yield return new("User data reset: replacement failure does not erase newly created data on retry", ReopenFailure);
    }

    private static async Task Drain()
    {
        var calls = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var participant = new Participant(async () =>
        {
            calls.Add("drain"); entered.SetResult(); await release.Task; calls.Add("released");
        });
        var shutdown = new AppShutdown([participant], [new(() => { calls.Add("store-closed"); return ValueTask.CompletedTask; })]);
        var reset = new AppUserDataReset(() => calls.Add("request"), shutdown.ForceReleaseAsync,
            () => { Check(shutdown.IsReleased); calls.Add("delete"); return Task.CompletedTask; },
            () => { calls.Add("new-generation"); return Task.CompletedTask; });
        var first = reset.ResetAsync();
        Check(ReferenceEquals(first, reset.ResetAsync()));
        await entered.Task;
        Check(!first.IsCompleted && calls.SequenceEqual(new[] { "request", "drain" }),
            "an abort signal is not permission to delete");
        release.SetResult();
        await first;
        Check(calls.SequenceEqual(new[] { "request", "drain", "released", "store-closed", "delete", "new-generation" }));
        await reset.ResetAsync();
        Check(calls.Count == 6, "one confirmed operation cannot clear the replacement generation");
    }

    private static async Task RequestFailure()
    {
        int requests = 0, releases = 0, clears = 0, opens = 0;
        var reset = new AppUserDataReset(() => { if (++requests == 1) throw new IOException(); },
            () => { releases++; return Task.CompletedTask; },
            () => { clears++; return Task.CompletedTask; }, () => { opens++; return Task.CompletedTask; });
        await Fails(reset.ResetAsync());
        Check(releases == 0 && clears == 0 && opens == 0);
        await reset.ResetAsync();
        Check(requests == 2 && releases == 1 && clears == 1 && opens == 1);
    }

    private static async Task ReleaseFailure()
    {
        int requests = 0, releases = 0, clears = 0, opens = 0;
        var reset = new AppUserDataReset(() => requests++,
            () => ++releases == 1 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask,
            () => { clears++; return Task.CompletedTask; }, () => { opens++; return Task.CompletedTask; });
        await Fails(reset.ResetAsync());
        Check(clears == 0 && opens == 0);
        await reset.ResetAsync();
        Check(requests == 1 && releases == 2 && clears == 1 && opens == 1);
    }

    private static async Task ClearFailure()
    {
        int requests = 0, releases = 0, clears = 0, opens = 0;
        var reset = new AppUserDataReset(() => requests++,
            () => { releases++; return Task.CompletedTask; },
            () => ++clears == 1 ? Task.FromException(new IOException()) : Task.CompletedTask,
            () => { opens++; return Task.CompletedTask; });
        await Fails(reset.ResetAsync());
        Check(opens == 0);
        await reset.ResetAsync();
        Check(requests == 1 && releases == 1 && clears == 2 && opens == 1);
    }

    private static async Task ReopenFailure()
    {
        int clears = 0, opens = 0;
        var reset = new AppUserDataReset(() => { }, () => Task.CompletedTask,
            () => { clears++; return Task.CompletedTask; },
            () => ++opens == 1 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);
        await Fails(reset.ResetAsync());
        await reset.ResetAsync();
        Check(clears == 1 && opens == 2);
    }

    private sealed class Participant(Func<Task> abort) : IDesktopShutdownParticipant
    {
        public Task PrepareShutdownAsync() => throw new InvalidOperationException("reset does not require saving old data");
        public Task CancelShutdownPreparationAsync() => throw new InvalidOperationException("old generation cannot resume");
        public ValueTask DisposeAsync() => throw new InvalidOperationException("reset uses explicit abort");
        public Task AbortAsync() => abort();
    }

    private static async Task Fails(Task task)
    {
        try { await task; }
        catch { return; }
        throw new InvalidOperationException("expected a failed reset");
    }

    private static void Check(bool value, string message = "User data reset assertion")
    { if (!value) throw new InvalidOperationException(message); }
}

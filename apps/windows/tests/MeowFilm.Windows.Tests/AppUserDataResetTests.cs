using System.IO;
using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class AppUserDataResetTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("User data reset: concurrent confirmation waits for actual owner drain before deleting", Drain);
        yield return new("User data reset: request failure does not release or delete and can retry", RequestFailure);
        yield return new("User data reset: release failure retains data and cannot reopen", ReleaseFailure);
        yield return new("User data reset: deletion failure retries without reviving or releasing old owners twice", ClearFailure);
        yield return new("User data reset: replacement failure does not erase newly created data on retry", ReopenFailure);
        yield return new("User data reset: single root waits for accepted cache writes and preserves replacement data", CacheDrain);
        yield return new("User data reset: real aggregate closes before all native owners and clears the root",
            () => MusicQualityConsumerTests.Sta(NativeOwnersReset));
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

    private static async Task CacheDrain()
    {
        string parent = TestWorkspace.DataPath("reset-cache-drain-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(Path.Combine(parent, "aggregate"));
        Directory.CreateDirectory(paths.Documents);
        Directory.CreateDirectory(paths.Caches);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new UserDataDirectory(paths.Documents);
        owner.Open();
        var business = new NativeDatabase(paths.Documents);
        var backing = new HeldCacheBacking(business);
        var cache = new TypedRecordRepository(backing, MusicHomeSnapshotStore.Namespace, HomeStorageRecords.Home);
        Task? resetting = null, writing = null;
        try
        {
            await business.WriteAsync(db => db.CommitRevision("reset-test", StorageRevision.Empty, Guid.NewGuid().ToString("D")));
            writing = cache.UpdateAsync(_ => JsonSerializer.SerializeToElement(new { platform = "wy", pages = new[] {
                new { platform = "wy", entries = Array.Empty<object>() } } }));
            await backing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string businessFile = business.DatabasePath!;
            var shutdown = new AppShutdown([], [
                new(async () =>
                {
                    var closing = cache.DisposeAsync().AsTask();
                    entered.TrySetResult();
                    await closing;
                    await backing.DisposeAsync();
                }),
                new(() => business.DisposeAsync())
            ]);
            int reopened = 0;
            var reset = new AppUserDataReset(owner.RequestReset, shutdown.ForceReleaseAsync,
                () =>
                {
                    Check(shutdown.IsReleased && backing.Committed && backing.Disposed,
                        "Deletion requires the real cache write and owned backing disposal.");
                    owner.Clear();
                    return Task.CompletedTask;
                },
                () =>
                {
                    Check(!Directory.Exists(paths.Documents) && !Directory.Exists(paths.Caches),
                        "The current root must be gone before replacement.");
                    owner.Open();
                    Directory.CreateDirectory(paths.Documents);
                    Directory.CreateDirectory(paths.Caches);
                    File.WriteAllText(Path.Combine(paths.Documents, "new"), "new business");
                    File.WriteAllText(Path.Combine(paths.Caches, "new"), "new cache");
                    reopened++;
                    return Task.CompletedTask;
                });
            resetting = reset.ResetAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!resetting.IsCompleted && !shutdown.IsReleased && !backing.Committed && !backing.Disposed);
            Check(File.Exists(businessFile) && Directory.Exists(paths.Caches) &&
                File.Exists(Path.Combine(parent, ".aggregate.reset-pending")), "Slow cache disposal must retain the root and intent.");
            using (var blocked = new UserDataDirectory(paths.Documents))
            {
                try { blocked.Open(); throw new InvalidOperationException("Pending reset admitted a new generation."); }
                catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Storage) { }
            }
            try { await cache.UpdateAsync(_ => default); throw new InvalidOperationException("Retiring cache admitted a write."); }
            catch (ObjectDisposedException) { }
            backing.Release.TrySetResult();
            await writing.WaitAsync(TimeSpan.FromSeconds(5));
            await resetting.WaitAsync(TimeSpan.FromSeconds(5));
            await reset.ResetAsync();
            Check(reopened == 1 && File.ReadAllText(Path.Combine(paths.Documents, "new")) == "new business" &&
                File.ReadAllText(Path.Combine(paths.Caches, "new")) == "new cache");
            Check(!File.Exists(Path.Combine(parent, ".aggregate.reset-pending")));
        }
        finally
        {
            backing.Release.TrySetResult();
            if (resetting is not null)
                try { await resetting; } catch { /* The original failure remains with the test. */ }
            if (writing is not null)
                try { await writing; } catch { /* The original failure remains with the test. */ }
            await cache.DisposeAsync();
            await backing.DisposeAsync();
            await business.DisposeAsync();
            owner.Dispose();
            Directory.Delete(TestWorkspace.ValidatePath(TestWorkspace.DataRoot, parent), recursive: true);
        }
    }

    private sealed class HeldCacheBacking(IStorageDatabase inner) : NativeTestDatabase(inner)
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Committed, Disposed;
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
            int retainedBytes = 0, Action<Action>? commit = null)
        {
            var receipt = await base.WriteAsync(db =>
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
                return write(db);
            }, token, retainedBytes, commit);
            Committed = true; return receipt;
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); Disposed = true; }
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

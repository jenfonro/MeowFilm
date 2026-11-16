using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

// Synthetic trusted RPC receipts and local media only. No source headers, actual Node
// process, source accounts, external media or native playback engine.
public static class RuntimeMediaTests
{
    private static readonly string First = new('A', 32), Second = new('B', 32), Resource = new('C', 32);
    private static readonly Uri MediaOrigin = new("http://127.0.0.1:41001/");
    private static string PathFor(string id) => $"/media/{id}/{Resource}";
    private static Uri Address(string id = "") => new(MediaOrigin, PathFor(id.Length == 0 ? First : id));

    public static IEnumerable<TestCase> Cases()
    {
        foreach (string address in new[] { "http://localhost:41001/media", "http://127.1:41001/other",
            "http://[::1]:41001/media", "https://127.0.0.1:41001/media", "http://192.168.1.2/media",
            "https://example.org/media?fixture=1", "http://127.0.0.1:41002/rpc" })
            yield return Test("owned receipt does not grant URL permissions " + address, async () =>
            {
                using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
                await using var authority = Authority(runtime.Token);
                await using var lease = authority.CreateLease(First, new(address), scope.Token);
                Check(lease.Address == new Uri(address) && lease.Headers.Count == 0 && lease.Revoked.CanBeCanceled &&
                    !lease.Revoked.IsCancellationRequested, "HTTP input and revocable owner, independent of origin or URL path");
            });
        yield return Test("authority requires a revocable generation and exact unique resource IDs", async () =>
        {
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            Error(() => _ = new RuntimeMediaAuthority(default, _ => ValueTask.CompletedTask), PlaybackError.InvalidResourceLease);
            await using var authority = Authority(runtime.Token);
            await using var first = authority.CreateLease(First, Address(Second), scope.Token);
            await using var second = authority.CreateLease(Second, Address(), scope.Token);
            Check(first.Headers is IDictionary<string, string> headers && headers.IsReadOnly, "headers cannot be mutated");
            Error(() => authority.CreateLease(First, Address(), scope.Token), PlaybackError.InvalidResourceLease);
            Error(() => authority.CreateLease(new string('D', 32), Address(), default), PlaybackError.InvalidResourceLease);
            foreach (string id in new[] { "", "fixture", "../other", new string('A', 31) })
                Error(() => authority.CreateLease(id, Address(), scope.Token), PlaybackError.InvalidResourceLease);
        });
        yield return Test("source account revocation isolates resources runtime generation revokes all", async () =>
        {
            using var runtime = new CancellationTokenSource();
            using var firstScope = new CancellationTokenSource();
            using var secondScope = new CancellationTokenSource();
            await using var authority = Authority(runtime.Token);
            await using var first = authority.CreateLease(First, Address(), firstScope.Token);
            await using var second = authority.CreateLease(Second, Address(Second), secondScope.Token);
            firstScope.Cancel();
            Check(first.Revoked.IsCancellationRequested &&
                !second.Revoked.IsCancellationRequested, "one scope does not revoke its neighbor");
            runtime.Cancel();
            Check(second.Revoked.IsCancellationRequested, "old runtime receipt is dead");
            Error(() => authority.CreateLease(new string('D', 32),
                Address(new string('D', 32)), secondScope.Token), PlaybackError.ResourceRevoked);
        });
        yield return Test("lease release and authority close share one actual asynchronous drain", async () =>
        {
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int releases = 0;
            var authority = Authority(runtime.Token, async id =>
            {
                Check(id == First, "only the owned resource is released");
                Interlocked.Increment(ref releases); entered.TrySetResult();
                await finish.Task;
            });
            var lease = authority.CreateLease(First, Address(), scope.Token);
            var first = lease.DisposeAsync().AsTask();
            var second = lease.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = authority.DisposeAsync().AsTask();
            Check(lease.Revoked.IsCancellationRequested &&
                !first.IsCompleted && !second.IsCompleted && !close.IsCompleted, "physical cleanup is still owned");
            Error(() => authority.CreateLease(Second, Address(Second), scope.Token), PlaybackError.ResourceRevoked);
            finish.TrySetResult();
            await Task.WhenAll(first, second, close);
            await authority.DisposeAsync();
            Check(releases == 1, "exactly one business release");
        });
        yield return Test("close revokes unused receipts and drains all resources", async () =>
        {
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            var released = new HashSet<string>(StringComparer.Ordinal);
            var authority = Authority(runtime.Token, id =>
            {
                lock (released) Check(released.Add(id), "no duplicate release");
                return ValueTask.CompletedTask;
            });
            var first = authority.CreateLease(First, Address(), scope.Token);
            var second = authority.CreateLease(Second, Address(Second), scope.Token);
            await authority.DisposeAsync();
            Check(first.Revoked.IsCancellationRequested && second.Revoked.IsCancellationRequested &&
                released.SetEquals([First, Second]), "including resources never opened by a player");
            await first.DisposeAsync(); await second.DisposeAsync();
        });
        yield return Test("resource drain never requires a UI synchronization context", async () =>
        {
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            int releases = 0;
            var authority = Authority(runtime.Token, _ =>
            {
                Interlocked.Increment(ref releases);
                return ValueTask.CompletedTask;
            });
            var lease = authority.CreateLease(First, Address(), scope.Token);
            var dispatch = new DispatchProbe();
            var previous = SynchronizationContext.Current;
            Task release, close;
            try
            {
                SynchronizationContext.SetSynchronizationContext(dispatch);
                release = lease.DisposeAsync().AsTask();
                close = authority.DisposeAsync().AsTask();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            await Task.WhenAll(release, close).WaitAsync(TimeSpan.FromSeconds(5));
            Check(dispatch.Posts == 0 && releases == 1, "cleanup does not post to a retiring UI context");
        });
        yield return Test("release failure is fixed classified and cannot be hidden by duplicate dispose", async () =>
        {
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            int releases = 0;
            var authority = Authority(runtime.Token, _ =>
            {
                Interlocked.Increment(ref releases);
                throw new IOException("synthetic-private-upstream-canary");
            });
            var lease = authority.CreateLease(First, Address(), scope.Token);
            await ErrorAsync(() => lease.DisposeAsync().AsTask(), PlaybackError.ReleaseFailed);
            await ErrorAsync(() => lease.DisposeAsync().AsTask(), PlaybackError.ReleaseFailed);
            Error(() => authority.CreateLease(Second, Address(Second), scope.Token), PlaybackError.ReleaseFailed);
            await ErrorAsync(() => authority.DisposeAsync().AsTask(), PlaybackError.ReleaseFailed);
            Check(releases == 1 && lease.Revoked.IsCancellationRequested, "failed cleanup is not retried or reported successful");
        });
    }

    private static RuntimeMediaAuthority Authority(CancellationToken runtime, Func<string, ValueTask>? release = null) =>
        new(runtime, release ?? (_ => ValueTask.CompletedTask));
    private sealed class DispatchProbe : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            base.Post(callback, state);
        }
    }
    private static TestCase Test(string name, Func<Task> run) => new("runtime-media: " + name, run);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Runtime media: " + message); }
    private static void Error(Action action, string expected)
    {
        try { action(); }
        catch (PlaybackException error) when (error.Code == expected && error.Message == expected && error.InnerException is null) { return; }
        throw new InvalidOperationException("Expected fixed media error " + expected);
    }
    private static async Task ErrorAsync(Func<Task> action, string expected)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (PlaybackException error) when (error.Code == expected && error.Message == expected && error.InnerException is null) { return; }
        throw new InvalidOperationException("Expected fixed media error " + expected);
    }
}

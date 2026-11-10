using System.Diagnostics;
using System.Text;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.AdaptiveMediaTests;

namespace MeowFilm.Windows.Tests;

// Synthetic trusted RPC receipts and local media only. No source headers, actual Node
// process, source accounts or external media. Native cases reuse the one libVLC player.
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
        yield return Test("ordinary HTTP reads local media without a lease", async () =>
        {
            await using var origin = WaveOrigin();
            using var input = await HttpMediaInput.OpenAsync(origin.Uri("/wave"), new Dictionary<string, string>());
            Check(input.Length > 0 && origin.Requests.Count == 1, "no local-address admission policy");
        });
        yield return NativeTest("progressive Node-shaped input has no upstream headers survives UI stop and releases on playback stop", async () =>
        {
            await using var origin = WaveOrigin();
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            using var subscription = new CancellationTokenSource();
            int releases = 0;
            await using var authority = Authority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
            await using var player = Player();
            var lease = authority.CreateLease(First, origin.Uri(PathFor(First)), scope.Token);
            await player.OpenResourceAsync("film", "occurrence", lease, "fixture");
            await Decoded(player);
            subscription.Cancel(); // Deliberately not the playback/source/runtime owner.
            await player.SeekAsync(12); await player.PauseAsync();
            Check(player.HasMedia && player.PauseRequested && !lease.Revoked.IsCancellationRequested && releases == 0,
                "UI early stop does not own media");
            Check(origin.Requests.Count > 0 && origin.Requests.All(r => r.Path.StartsWith("/media/", StringComparison.Ordinal) &&
                !r.AnyCallerHeader) && origin.Requests.Any(r => r.Range), "only local media and ranged reads, no upstream credentials");
            await player.StopAsync();
            Check(releases == 1 && lease.Revoked.IsCancellationRequested, "native retirement released Node resource");
        });
        yield return NativeTest("paused input is retired on runtime revocation", async () =>
        {
            await using var origin = WaveOrigin();
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            int releases = 0;
            await using var authority = Authority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
            await using var player = Player();
            await player.OpenResourceAsync("film", "occurrence",
                authority.CreateLease(First, origin.Uri(PathFor(First)), scope.Token), "fixture");
            await Decoded(player); await player.PauseAsync();
            runtime.Cancel();
            await Until(() => !player.HasMedia && releases == 1);
            Error(() => authority.CreateLease(Second, origin.Uri(PathFor(Second)), scope.Token), PlaybackError.ResourceRevoked);
        });
        yield return NativeTest("cancelled resource open releases receipt without preempting donor", async () =>
        {
            await using var origin = WaveOrigin();
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            using var cancelled = new CancellationTokenSource();
            var released = new List<string>();
            await using var authority = Authority(runtime.Token, id =>
            { lock (released) released.Add(id); return ValueTask.CompletedTask; });
            await using var player = Player();
            await player.OpenResourceAsync("film", "occurrence",
                authority.CreateLease(First, origin.Uri(PathFor(First)), scope.Token), "fixture");
            await Decoded(player);
            var accepted = Session(player);
            int requests = origin.Requests.Count;
            cancelled.Cancel();
            try
            {
                await player.OpenResourceAsync("film", "rejected",
                    authority.CreateLease(Second, origin.Uri(PathFor(Second)), scope.Token), "fixture", cancelled.Token);
                throw new InvalidOperationException("Cancelled resource was accepted");
            }
            catch (OperationCanceledException) { }
            Check(released.SequenceEqual([Second]) && ReferenceEquals(accepted, Session(player)) &&
                player.IsPlaying && origin.Requests.Skip(requests).All(r => r.Path != PathFor(Second)),
                "cancelled admission neither reads candidate bytes nor stops accepted media");
        });
        foreach (string format in new[] { "hls-mp4", "dash-template" })
            yield return NativeTest(format + " resolves relative children with one owned receipt", async () =>
            {
                await using var origin = new Origin { RequireAuthorization = false };
                string prefix = PathFor(First) + "/";
                origin.Route = path => MediaRoute(path[(prefix.Length - 1)..], format, 60);
                using var runtime = new CancellationTokenSource();
                using var scope = new CancellationTokenSource();
                await using var authority = Authority(runtime.Token);
                await using var player = Player();
                string root = prefix + (format.StartsWith("dash", StringComparison.Ordinal) ? "root.mpd" : "root.m3u8");
                await player.OpenResourceAsync("film", "adaptive", authority.CreateLease(First, origin.Uri(root), scope.Token), "fixture");
                await Decoded(player); await player.SeekAsync(10);
                Check(origin.Requests.Select(r => r.Path).Distinct().Count() >= 3 &&
                    origin.Requests.All(r => r.Path.StartsWith(prefix, StringComparison.Ordinal) && !r.AnyCallerHeader),
                    "root initialization and segments are scoped, without source headers");
            });
        yield return NativeTest("redirect across resource IDs remains one owned receipt", async () =>
        {
            await using var origin = WaveOrigin(); var wave = Wave();
            origin.Route = path => path == PathFor(First) ? Reply.Redirect(PathFor(Second)) : new Reply(wave, "audio/wav");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            int releases = 0;
            await using var authority = Authority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
            await using var player = Player();
            await player.OpenResourceAsync("film", "fixture", authority.CreateLease(First, origin.Uri(PathFor(First)), scope.Token), "fixture");
            await Decoded(player);
            Check(origin.Requests.Any(r => r.Path == PathFor(Second)) && releases == 0, "target URL is not another ownership receipt");
            await player.StopAsync(); Check(releases == 1, "the exact original receipt is released");
        });
        yield return NativeTest("handoff releases donor without revoking replacement and preserves controls", async () =>
        {
            await using var origin = WaveOrigin();
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            using var nextScope = new CancellationTokenSource();
            var released = new List<string>();
            await using var authority = Authority(runtime.Token, id =>
            { lock (released) released.Add(id); return ValueTask.CompletedTask; });
            await using var player = Player();
            await player.OpenResourceAsync("film", "occurrence",
                authority.CreateLease(First, origin.Uri(PathFor(First)), scope.Token), "fixture");
            await Decoded(player);
            await player.SeekAsync(15); await player.PauseAsync(); player.Volume = 37;
            var next = authority.CreateLease(Second, origin.Uri(PathFor(Second)), nextScope.Token);
            await player.PrepareResourceHandoffAsync(next);
            Check(!next.Revoked.IsCancellationRequested && released.Count == 0 && player.PauseRequested,
                "prepare leaves donor alive and replacement paused");
            int commits = 0;
            await player.CommitHandoffAsync(() => commits++, default);
            Check(commits == 1 && released.SequenceEqual([First]) && !next.Revoked.IsCancellationRequested &&
                player.HasMedia && player.PauseRequested && !player.IsPlaying &&
                player.Identity == "occurrence" && player.Volume == 37 && Math.Abs(player.PositionSeconds - 15) <= .25,
                "same logical media and intent; donor release cannot cancel replacement");
            await player.ResumeAsync(); await Decoded(player);
            nextScope.Cancel();
            await Until(() => !player.HasMedia && released.Count == 2);
            await player.StopAsync();
            Check(released.SequenceEqual([First, Second]), "new scope stays effective after commit; each resource released once");
        });
        yield return NativeTest("rejected handoff keeps donor and releases rejected resource", async () =>
        {
            await using var origin = WaveOrigin();
            var wave = Wave();
            origin.Route = path => path == PathFor(Second) ? new Reply(Encoding.UTF8.GetBytes("not media")) : new Reply(wave, "audio/wav");
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            var released = new List<string>();
            await using var authority = Authority(runtime.Token, id =>
            { lock (released) released.Add(id); return ValueTask.CompletedTask; });
            await using var player = Player();
            await player.OpenResourceAsync("film", "occurrence",
                authority.CreateLease(First, origin.Uri(PathFor(First)), scope.Token), "fixture");
            await Decoded(player);
            var accepted = Session(player);
            await ErrorAsync(() => player.PrepareResourceHandoffAsync(
                authority.CreateLease(Second, origin.Uri(PathFor(Second)), scope.Token)), PlaybackError.HttpFormatUnsupported);
            Check(ReferenceEquals(accepted, Session(player)) && player.IsPlaying && released.SequenceEqual([Second]),
                "failure never replaces healthy output");
        });
        yield return NativeTest("revocation after handoff readiness cannot publish prepared media", async () =>
        {
            await using var origin = WaveOrigin();
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            using var nextScope = new CancellationTokenSource();
            var released = new List<string>();
            await using var authority = Authority(runtime.Token, id =>
            { lock (released) released.Add(id); return ValueTask.CompletedTask; });
            await using var player = Player();
            await player.OpenResourceAsync("film", "occurrence",
                authority.CreateLease(First, origin.Uri(PathFor(First)), scope.Token), "fixture");
            await Decoded(player);
            var accepted = Session(player);
            await player.PrepareResourceHandoffAsync(authority.CreateLease(Second, origin.Uri(PathFor(Second)), nextScope.Token));
            nextScope.Cancel();
            bool committed = false;
            await ErrorAsync(() => player.CommitHandoffAsync(() => committed = true, default), PlaybackError.ResourceRevoked);
            await player.DiscardHandoffAsync();
            Check(!committed && ReferenceEquals(accepted, Session(player)) && player.IsPlaying &&
                released.SequenceEqual([Second]), "revoked prepared resource cannot affect accepted donor");
        });
        yield return NativeTest("read lease and ordinary HTTP handoffs retain accurate owners", async () =>
        {
            await using var origin = WaveOrigin();
            using var runtime = new CancellationTokenSource();
            using var scope = new CancellationTokenSource();
            int releases = 0;
            await using var authority = Authority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
            await using var player = Player();
            var first = new WaveLease();
            await player.OpenReadAsync("film", "occurrence", first, "fixture");
            await Decoded(player); await player.SeekAsync(10); await player.PauseAsync();
            await player.PrepareResourceHandoffAsync(authority.CreateLease(First, origin.Uri(PathFor(First)), scope.Token));
            await player.CommitHandoffAsync(() => { }, default);
            Check(first.Disposals == 1 && player.HasMedia && player.PauseRequested, "read donor retired at runtime commit");
            var next = new WaveLease();
            await player.PrepareReadHandoffAsync(next, default);
            await player.CommitHandoffAsync(() => { }, default);
            Check(releases == 1 && player.HasMedia && player.PauseRequested && Math.Abs(player.PositionSeconds - 10) <= .25,
                "old runtime revocation does not retire the new read input");
            await player.PrepareHandoffAsync(origin.Uri(PathFor(Second)), new Dictionary<string, string>(), default);
            await player.DiscardHandoffAsync();
            Check(next.Disposals == 0 && player.HasMedia, "discarding an ordinary HTTP candidate preserves the read owner");
            await player.StopAsync();
            Check(next.Disposals == 1, "new read owner still released exactly once");
        });
    }

    private static RuntimeMediaAuthority Authority(CancellationToken runtime, Func<string, ValueTask>? release = null) =>
        new(runtime, release ?? (_ => ValueTask.CompletedTask));
    private static NativePlayback Player() => new(audioOutput: NativeAudioOutput.Dummy);
    private static Origin WaveOrigin() => new() { RequireAuthorization = false, Route = _ => new Reply(Wave(), "audio/wav") };
    private static byte[] Wave()
    {
        const int length = 8000 * 2 * 120;
        var bytes = new byte[length + 44];
        using var memory = new MemoryStream(bytes);
        using var writer = new BinaryWriter(memory, Encoding.UTF8);
        writer.Write("RIFF"u8); writer.Write(length + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length);
        return bytes;
    }
    internal sealed class WaveLease : INativeMediaReadLease
    {
        private readonly CancellationTokenSource revoked = new();
        public Stream Content { get; } = new MemoryStream(Wave(), writable: false);
        public long Length => Content.Length;
        public CancellationToken Revoked => revoked.Token;
        internal int Disposals;
        public ValueTask DisposeAsync()
        {
            Disposals++;
            revoked.Cancel(); Content.Dispose(); revoked.Dispose();
            return ValueTask.CompletedTask;
        }
    }
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
    private static TestCase NativeTest(string name, Func<Task> run) => Test(name, () => Sta(run));
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
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Runtime media retirement");
            await Task.Delay(15);
        }
    }
}

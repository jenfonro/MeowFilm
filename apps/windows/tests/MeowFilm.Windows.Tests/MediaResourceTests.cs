using System.Diagnostics;
using System.IO;
using MeowFilm.Media;
using Origin = MeowFilm.Windows.Tests.NativeMediaFixtures.Origin;
using Reply = MeowFilm.Windows.Tests.NativeMediaFixtures.Reply;

namespace MeowFilm.Windows.Tests;

// Synthetic movie bytes and the actual Film owner, muted native output.
// Exercises production resource intake and physical receipt release.
public static class MediaResourceTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("non-HTTP resource rejects actual unsupported input", async () =>
        {
            await using var player = New(); var lease = new Lease(new("file:///C:/private-media.mp4"));
            await Error(() => Open(player, lease), PlaybackError.UnsupportedUri);
            Released(lease); Check(!player.HasOutput && Session(player) is null, "no accepted decoder");
        });
        foreach (var mode in new[] { "revoked", "unrevocable", "throwing-getter", "invalid-header" })
            yield return Case("preflight " + mode + " releases without network", async () =>
            {
                await using var origin = MovieOrigin();
                await using var player = New();
                var lease = LeaseFor(origin, "/root");
                string expected = PlaybackError.InvalidResourceLease;
                switch (mode)
                {
                    case "revoked": lease.Revoke(); break;
                    case "unrevocable": lease.Unrevocable = true; break;
                    case "throwing-getter": lease.ThrowAddress = true; expected = PlaybackError.OpenFailed; break;
                    case "invalid-header": lease.HeaderValues["Host"] = "private"; expected = PlaybackError.UnsupportedHeaders; break;
                }
                if (mode == "revoked") await Cancelled(() => Open(player, lease));
                else await Error(() => Open(player, lease), expected);
                Released(lease); Check(origin.Requests.IsEmpty && !player.HasOutput, "passive rejection");
            });
        yield return Case("cancelled caller and invalid identity preserve accepted playback", async () =>
        {
            await using var origin = MovieOrigin();
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            var first = LeaseFor(origin, "/root"); await Start(player, first);
            var current = Session(player);
            var invalid = LeaseFor(origin, "/root");
            await Error(() => player.PrepareAsync(new("", "other", 0, false), invalid.InputAddress,
                FilmKernelIntegrationTests.Input("progressive"), invalid), PlaybackError.InvalidRequest);
            Released(invalid);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var rejected = LeaseFor(origin, "/root");
            await Cancelled(() => Open(player, rejected, cancelled.Token)); Released(rejected);
            Check(ReferenceEquals(current, Session(player)) && first.DisposeCalls == 0 &&
                player.HasOutput && !player.PauseRequested,
                "rejection does not preempt existing media");
            await player.StopAsync(); Released(first);
        });
        yield return Case("native progressive pause seek and synchronous revocation", async () =>
        {
            await using var origin = MovieOrigin();
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            var lease = LeaseFor(origin, "/root");
            await Start(player, lease);
            await player.SeekAsync(player.Scope!, 15); await player.SetPausedAsync(player.Scope!, true);
            Check(player.HasOutput && player.PauseRequested && lease.DisposeCalls == 0, "capability retained while paused");
            lease.Revoke();
            await Until(() => !player.HasOutput && lease.ReleaseComplete, "paused resource revoked");
            Released(lease);
            Check(origin.Requests.All(r => r.Authorized) && origin.Requests.Any(r => r.Range), "authorized ranged input");
        });
        yield return Case("caller cancellation remains bound after film commit returned", async () =>
        {
            await using var origin = MovieOrigin();
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            using var cancel = new CancellationTokenSource();
            var lease = LeaseFor(origin, "/root");
            await Open(player, lease, cancel.Token);
            Check(player.HasOutput && !player.PauseRequested, "native output committed");
            cancel.Cancel();
            await Until(() => lease.ReleaseComplete && !player.HasOutput, "caller cancellation retired");
            Released(lease);
        });
        yield return Case("replacement waits actual release and closes exactly once", async () =>
        {
            await using var origin = MovieOrigin();
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = LeaseFor(origin, "/root");
            try
            {
                await Start(player, first);
                first.ReleaseGate = release.Task;
                var second = LeaseFor(origin, "/root");
                var replacement = Open(player, second);
                await first.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!replacement.IsCompleted && !first.ReleaseComplete, "replacement waits owned resource cleanup");
                release.TrySetResult();
                await replacement; Released(first);
                await player.StopAsync(); Released(second);
            }
            finally { release.TrySetResult(); }
        });
        yield return Case("header snapshot cannot mutate after admission", async () =>
        {
            await using var origin = MovieOrigin();
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            var lease = LeaseFor(origin, "/root");
            await Start(player, lease);
            await player.SetPausedAsync(player.Scope!, true);
            int requests = origin.Requests.Count;
            lease.HeaderValues["Authorization"] = "changed-after-admission";
            await player.SeekAsync(player.Scope!, 20);
            Check(origin.Requests.Count > requests && origin.Requests.All(r => r.Authorized),
                "post-admission ranged IO retains the immutable accepted headers");
            lease.Revoke();
            await Until(() => lease.ReleaseComplete && !player.HasOutput, "mutated receipt retired");
            Released(lease);
        });
        yield return Case("receipt follows a redirect to another local port", async () =>
        {
            await using var other = MovieOrigin(); await using var origin = new Origin();
            other.Credential = origin.Credential;
            other.RequiredHeaders = origin.Headers;
            origin.Route = _ => Reply.Redirect(other.Uri("/root").AbsoluteUri);
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            var lease = LeaseFor(origin, "/root");
            await Start(player, lease);
            Check(other.Requests.Any(r => r.Authorized), "cross-port request retains the explicit input headers");
            await player.StopAsync(); Released(lease);
        });
        yield return Case("same origin redirect needs no URL authorization", async () =>
        {
            await using var origin = MovieOrigin(); var route = origin.Route;
            origin.Route = path => path == "/entry" ? Reply.Redirect("/private") : route("/root");
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            var lease = LeaseFor(origin, "/entry");
            await Start(player, lease);
            Check(origin.Requests.Any(r => r.Path == "/private"), "actual redirected input");
            await player.StopAsync(); Released(lease);
        });
        foreach (string format in new[] { "hls", "hls-aes", "dash" })
            yield return Case("actual " + format + " child resources share one scoped capability", async () =>
            {
                await using var origin = FilmKernelIntegrationTests.Fixture(format);
                var player = New();
                await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
                var lease = LeaseFor(origin, "/root");
                await Open(player, lease, format: format);
                Check(origin.Requests.Any(r => r.Path.Contains("video-", StringComparison.Ordinal)), "actual movie fragments");
                Check(origin.Requests.All(r => r.Authorized), "all resource requests have scoped credential");
                Check(origin.Requests.Select(r => r.Path).Distinct().Count() >= 3, "actual root and child IO");
                lease.Revoke();
                await Until(() => lease.ReleaseComplete && !player.HasOutput, "movie source retired");
                Released(lease);
            });
        yield return Case("redirected movie manifest resolves relative children with one owned receipt", async () =>
        {
            await using var origin = FilmKernelIntegrationTests.Fixture("hls"); var route = origin.Route;
            origin.Route = path => path == "/entry" ? Reply.Redirect("/nested/root") :
                path.StartsWith("/nested/", StringComparison.Ordinal) ? route(path["/nested".Length..]) :
                new Reply([]) { Status = 404 };
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            var lease = LeaseFor(origin, "/entry");
            await Open(player, lease, format: "hls");
            Check(origin.Requests.Any(r => r.Path == "/nested/variant") &&
                origin.Requests.Any(r => r.Path.StartsWith("/nested/video-", StringComparison.Ordinal)) &&
                origin.Requests.All(r => r.Authorized), "children use the final root location and original headers");
            await player.StopAsync(); Released(lease);
        });
        yield return Case("disposed movie owner releases rejected receipt without network", async () =>
        {
            await using var origin = MovieOrigin();
            var closed = New(); await closed.DisposeAsync();
            var lease = LeaseFor(origin, "/root");
            await Error(() => Open(closed, lease), PlaybackError.Disposed);
            Released(lease); Check(origin.Requests.IsEmpty, "no native input after owner disposal");
        });
        yield return Case("runtime receipt survives UI stop and cancelled admission but retires on exact runtime death", async () =>
        {
            await using var origin = MovieOrigin(); origin.RequireAuthorization = false;
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            using var subscription = new CancellationTokenSource(); using var cancelled = new CancellationTokenSource();
            var released = new List<string>();
            await using var authority = new RuntimeMediaAuthority(runtime.Token, id =>
            { lock (released) released.Add(id); return ValueTask.CompletedTask; });
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            string firstId = new('A', 32), nextId = new('B', 32);
            var first = authority.CreateLease(firstId, origin.Uri("/root"), scope.Token);
            var intent = new RuntimeMediaPlaybackIntent("film", "runtime-film", 0, false);
            await player.PrepareAsync(intent, first.Address, FilmKernelIntegrationTests.Input("progressive"), first);
            Check(await player.TryCommitAsync(intent, () => true, () => { }), "actual runtime receipt commit");
            var accepted = Session(player);
            int requests = origin.Requests.Count;
            cancelled.Cancel();
            var rejected = authority.CreateLease(nextId, origin.Uri("/rejected"), scope.Token);
            await Cancelled(() => player.PrepareAsync(new("film", "rejected", 0, false), rejected.Address,
                FilmKernelIntegrationTests.Input("progressive"), rejected, cancelled.Token));
            Check(released.SequenceEqual([nextId]) && ReferenceEquals(accepted, Session(player)) &&
                rejected.Revoked.IsCancellationRequested && player.HasOutput && origin.Requests.Count == requests,
                "cancelled admission releases only its receipt without reading or preempting donor");
            subscription.Cancel();
            await player.SeekAsync(player.Scope!, 8); await player.SetPausedAsync(player.Scope!, true);
            Check(player.HasOutput && player.PauseRequested && !first.Revoked.IsCancellationRequested &&
                origin.Requests.All(request => !request.AnyCallerHeader) && origin.Requests.Any(request => request.Range),
                "UI visibility does not own playback or inject upstream credentials into Node-shaped input");
            runtime.Cancel();
            await Until(() => !player.HasOutput && released.Count == 2, "runtime receipt physically retired");
            Check(released.SequenceEqual([nextId, firstId]) && first.Revoked.IsCancellationRequested,
                "the exact old runtime generation releases its accepted receipt once");
        });
        yield return Case("film replacement releases the donor receipt without revoking prepared authority or controls", async () =>
        {
            await using var origin = MovieOrigin(); origin.RequireAuthorization = false;
            using var runtime = new CancellationTokenSource();
            using var firstScope = new CancellationTokenSource(); using var nextScope = new CancellationTokenSource();
            var released = new List<string>();
            await using var authority = new RuntimeMediaAuthority(runtime.Token, id =>
            { lock (released) released.Add(id); return ValueTask.CompletedTask; });
            var player = New();
            await using var window = await NativeMediaFixtures.ShowFilmAsync(player);
            string firstId = new('A', 32), nextId = new('B', 32);
            var first = authority.CreateLease(firstId, origin.Uri("/root"), firstScope.Token);
            var initial = new RuntimeMediaPlaybackIntent("film", "first-attempt", 0, true);
            await player.PrepareAsync(initial, first.Address, FilmKernelIntegrationTests.Input("progressive"), first);
            Check(await player.TryCommitAsync(initial, () => true, () => { }), "actual receipt donor");
            await player.SeekAsync(player.Scope!, 7); player.Volume = 37;
            var donor = player.Scope!;
            var next = authority.CreateLease(nextId, origin.Uri("/root"), nextScope.Token);
            var replacement = new RuntimeMediaPlaybackIntent("film", "next-attempt", 0, player.PauseRequested, donor);
            await player.PrepareAsync(replacement, next.Address, FilmKernelIntegrationTests.Input("progressive"), next);
            Check(released.Count == 0 && !next.Revoked.IsCancellationRequested && player.Scope == donor,
                "silent prepared receipt does not retire donor or own its pause intent");
            int commits = 0;
            Check(await player.TryCommitAsync(replacement, () => true, () => commits++), "replacement commit");
            Check(commits == 1 && released.SequenceEqual([firstId]) && !next.Revoked.IsCancellationRequested &&
                player.HasOutput && player.PauseRequested && player.Identity == "next-attempt" &&
                player.Volume == 37 && Math.Abs(player.PositionSeconds - 7) <= .25,
                "new attempt keeps exact donor clock, paused controls and its independent receipt");
            firstScope.Cancel();
            Check(player.HasOutput && !next.Revoked.IsCancellationRequested, "retired source scope cannot revoke replacement");
            nextScope.Cancel();
            await Until(() => !player.HasOutput && released.Count == 2, "replacement scope physically retired");
            Check(released.SequenceEqual([firstId, nextId]), "each actual authority receipt released exactly once");
        });
        yield return Case("cleanup failure is sanitized and blocks player reuse", async () =>
        {
            await using var origin = MovieOrigin();
            var player = New();
            var window = await NativeMediaFixtures.ShowFilmAsync(player);
            try
            {
                var lease = LeaseFor(origin, "/root");
                await Start(player, lease);
                lease.ThrowRelease = true;
                await Error(() => player.StopAsync(), PlaybackError.ReleaseFailed);
                Released(lease);
                var rejected = LeaseFor(origin, "/root");
                await Error(() => Open(player, rejected), PlaybackError.ReleaseFailed); Released(rejected);
            }
            finally { try { await window.DisposeAsync(); } catch (PlaybackException error) { Check(error.Code == PlaybackError.ReleaseFailed, "release fault retained"); } }
        });
    }

    private static FilmPlayback New() => new() { Volume = 0 };
    private static async Task Open(FilmPlayback player, Lease lease, CancellationToken token = default,
        string format = "progressive")
    {
        var intent = new RuntimeMediaPlaybackIntent("film", Guid.NewGuid().ToString("N"), 0, false);
        await player.PrepareAsync(intent, lease.InputAddress, FilmKernelIntegrationTests.Input(format), lease, token);
        Check(await player.TryCommitAsync(intent, () => true, () => { }, token), "actual movie commit");
    }
    private static async Task Start(FilmPlayback player, Lease lease)
    {
        await Open(player, lease);
        Check(player.Output is { SubmittedPictures: > 0 } && !player.PauseRequested, "actual native picture submission");
    }
    private static Lease LeaseFor(Origin origin, string path) =>
        new(origin.Uri(path), origin.Headers);
    private static Origin MovieOrigin() => FilmKernelIntegrationTests.Fixture("progressive");
    private static object? Session(FilmPlayback player) => NativeMediaFixtures.Field(player, "current");
    private static TestCase Case(string name, Func<Task> run) => new("media-resource: " + name, () => NativeMediaFixtures.Sta(run));
    private static void Check(bool valid, string stage)
    { if (!valid) throw new InvalidOperationException("Resource assertion: " + stage); }
    private static void Released(Lease lease) => Check(lease.DisposeCalls == 1 && lease.ReleaseComplete, "exactly one actual release");
    private static async Task Error(Func<Task> action, string expected)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (PlaybackException error)
        {
            Check(error.Code == expected && error.Message == expected && error.InnerException is null, "fixed error " + expected);
            return;
        }
        throw new InvalidOperationException("Expected fixed error " + expected);
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { return; }
        catch (PlaybackException error) when (error.Code == PlaybackError.Superseded &&
            error.Message == error.Code && error.InnerException is null) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }
    private static async Task Until(Func<bool> condition, string phase)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Resource phase: " + phase);
            await Task.Delay(15);
        }
    }
    internal sealed class Lease(Uri address, Dictionary<string, string>? headers = null) : INativeMediaResourceLease
    {
        private readonly CancellationTokenSource revoked = new();
        internal Dictionary<string, string> HeaderValues { get; } = headers ?? [];
        internal bool Unrevocable, ThrowAddress, ThrowRelease;
        internal int DisposeCalls;
        internal volatile bool ReleaseComplete;
        internal Task? ReleaseGate;
        internal readonly TaskCompletionSource ReleaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Uri InputAddress => address;
        public Uri Address => ThrowAddress ? throw new IOException("secret-address-canary") : address;
        public IReadOnlyDictionary<string, string> Headers => HeaderValues;
        public CancellationToken Revoked => Unrevocable ? CancellationToken.None : revoked.Token;
        internal void Revoke() => revoked.Cancel();
        internal void AssertReleased() => Released(this);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            ReleaseEntered.TrySetResult();
            Revoke();
            if (ReleaseGate is not null) await ReleaseGate.ConfigureAwait(false);
            ReleaseComplete = true;
            if (ThrowRelease) throw new IOException("secret-release-canary");
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using MeowFilm.Media;
using Origin = MeowFilm.Windows.Tests.AdaptiveMediaTests.Origin;
using Reply = MeowFilm.Windows.Tests.AdaptiveMediaTests.Reply;

namespace MeowFilm.Windows.Tests;

// Synthetic loopback media and real libVLC dummy. Unlike earlier transport fixtures this
// exercises the production resource entry, never ForLoopbackTests or an offline read lease.
public static class MediaResourceTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var address in new[] { "http://localhost:4123/a", "http://[::1]:4123/a",
            "https://127.0.0.1:4123/a", "http://127.0.0.1:0/a", "https://example.org/a",
            "file:///C:/private-media.wav" })
            yield return Case("reject nonregistered address " + address, async () =>
            {
                await using var player = New();
                var lease = new Lease(new(address));
                await Error(() => Open(player, lease), PlaybackError.InvalidResourceLease);
                Released(lease);
                Check(!player.HasMedia && player.NativeVersion is null, "no decoder");
            });
        yield return Case("ordinary HTTP does not inherit resource exception", async () =>
        {
            await using var origin = WaveOrigin();
            await Error(async () =>
            {
                using var input = await HttpMediaInput.OpenAsync(origin.Uri("/wave"), origin.Headers);
            }, PlaybackError.HttpAddressBlocked);
            Check(origin.Requests.IsEmpty, "ordinary policy still refuses loopback");
        });
        foreach (var mode in new[] { "revoked", "unrevocable", "denied", "throwing-policy", "throwing-getter", "invalid-header" })
            yield return Case("preflight " + mode + " releases without network", async () =>
            {
                await using var origin = WaveOrigin();
                await using var player = New();
                var lease = LeaseFor(origin, "/wave");
                string expected = PlaybackError.InvalidResourceLease;
                switch (mode)
                {
                    case "revoked": lease.Revoke(); expected = PlaybackError.ResourceRevoked; break;
                    case "unrevocable": lease.Unrevocable = true; break;
                    case "denied": lease.Permit = _ => false; expected = PlaybackError.HttpAddressBlocked; break;
                    case "throwing-policy": lease.Permit = _ => throw new IOException("secret-policy-canary"); break;
                    case "throwing-getter": lease.ThrowAddress = true; break;
                    case "invalid-header": lease.HeaderValues["Host"] = "private"; expected = PlaybackError.UnsupportedHeaders; break;
                }
                await Error(() => Open(player, lease), expected);
                Released(lease); Check(origin.Requests.IsEmpty && player.NativeVersion is null, "passive rejection");
            });
        yield return Case("cancelled caller and invalid identity preserve accepted playback", async () =>
        {
            await using var origin = WaveOrigin();
            await using var player = New();
            var first = LeaseFor(origin, "/wave"); await Start(player, first);
            var current = Session(player);
            var invalid = LeaseFor(origin, "/wave");
            await Error(() => player.OpenResourceAsync("", "other", invalid, ""), PlaybackError.InvalidRequest);
            Released(invalid);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var rejected = LeaseFor(origin, "/wave");
            await Cancelled(() => Open(player, rejected, cancelled.Token)); Released(rejected);
            Check(ReferenceEquals(current, Session(player)) && first.DisposeCalls == 0 && player.IsPlaying,
                "rejection does not preempt existing media");
            await player.StopAsync(); Released(first);
        });
        yield return Case("native progressive pause seek and synchronous revocation", async () =>
        {
            await using var origin = WaveOrigin();
            await using var player = New();
            var lease = LeaseFor(origin, "/wave");
            await Start(player, lease);
            await player.SeekAsync(15); await player.PauseAsync();
            Check(player.HasMedia && lease.DisposeCalls == 0, "capability retained while paused");
            lease.Revoke();
            await Until(() => !player.HasMedia && lease.ReleaseComplete, "paused resource revoked");
            Released(lease);
            Check(origin.Requests.All(r => r.Authorized) && origin.Requests.Any(r => r.Range), "authorized ranged input");
        });
        yield return Case("caller cancellation remains bound after OpenResourceAsync returned", async () =>
        {
            await using var origin = WaveOrigin();
            await using var player = New();
            using var cancel = new CancellationTokenSource();
            var lease = LeaseFor(origin, "/wave");
            await Open(player, lease, cancel.Token);
            await Until(() => player.IsPlaying, "native playing");
            cancel.Cancel();
            await Until(() => lease.ReleaseComplete && !player.HasMedia, "caller cancellation retired");
            Released(lease);
        });
        yield return Case("replacement waits actual release and closes exactly once", async () =>
        {
            await using var origin = WaveOrigin();
            await using var player = New();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = LeaseFor(origin, "/wave"); first.ReleaseGate = release.Task;
            await Start(player, first);
            var second = LeaseFor(origin, "/wave");
            var replacement = Open(player, second);
            await first.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!replacement.IsCompleted && !first.ReleaseComplete, "replacement waits owned resource cleanup");
            release.TrySetResult();
            await replacement; Released(first);
            await player.StopAsync(); Released(second);
        });
        yield return Case("header snapshot cannot mutate after admission", async () =>
        {
            await using var origin = WaveOrigin();
            var initial = origin.Route;
            origin.Route = _ => initial("/wave") with { BlockBody = true, PrefixBytes = 0 };
            await using var player = New();
            var lease = LeaseFor(origin, "/wave");
            var opening = Open(player, lease);
            await Until(() => !origin.Requests.IsEmpty, "response admission");
            lease.HeaderValues["Authorization"] = "changed-after-admission";
            lease.Revoke(); await Cancelled(() => opening);
            Released(lease); Check(origin.Requests.All(r => r.Authorized), "immutable accepted headers");
        });
        yield return Case("capability cannot authorize unrelated loopback port", async () =>
        {
            await using var other = WaveOrigin();
            await using var origin = new Origin();
            origin.Route = _ => Reply.Redirect(other.Uri("/wave").AbsoluteUri);
            await using var player = New();
            var lease = LeaseFor(origin, "/root"); lease.Permit = _ => true;
            await Error(() => Open(player, lease), PlaybackError.HttpAddressBlocked);
            Released(lease); Check(other.Requests.IsEmpty, "fixed native endpoint protects against faulty adapter");
        });
        yield return Case("same origin redirect still needs resource authorization", async () =>
        {
            await using var origin = new Origin();
            origin.Route = _ => Reply.Redirect("/private");
            await using var player = New();
            var lease = LeaseFor(origin, "/root"); lease.Permit = uri => uri.AbsolutePath == "/root";
            await Error(() => Open(player, lease), PlaybackError.HttpAddressBlocked);
            Released(lease);
            Check(origin.Requests.Count == 1 && origin.Requests.Single().Path == "/root", "no unauthorized redirected request");
        });
        foreach (string format in new[] { "hls-master", "hls-mp4", "dash-list" })
            yield return Case("actual " + format + " child resources share one scoped capability", async () =>
            {
                await using var origin = new Origin();
                origin.Route = path => AdaptiveMediaTests.MediaRoute(path, format, 30);
                await using var player = New();
                var lease = LeaseFor(origin, format.StartsWith("dash", StringComparison.Ordinal) ? "/root.mpd" : "/root.m3u8");
                await Start(player, lease);
                await Until(() => origin.Requests.Any(r => r.Path.EndsWith(".ts", StringComparison.Ordinal) ||
                    r.Path.EndsWith(".m4s", StringComparison.Ordinal)), "adaptive fragments");
                Check(origin.Requests.All(r => r.Authorized), "all resource requests have scoped credential");
                Check(lease.Checked.Select(u => u.AbsolutePath).Distinct().Count() >= 3, "root and child admission");
                lease.Revoke();
                await Until(() => lease.ReleaseComplete && !player.HasMedia, "adaptive listener and source retired");
                Released(lease);
            });
        yield return Case("adaptive child authorization refusal never reaches source", async () =>
        {
            await using var origin = new Origin();
            origin.Route = path => AdaptiveMediaTests.MediaRoute(path, "hls-master", 30);
            await using var player = New();
            var lease = LeaseFor(origin, "/root.m3u8");
            lease.Permit = uri => uri.AbsolutePath == "/root.m3u8";
            await Error(() => Open(player, lease), PlaybackError.HttpAddressBlocked);
            Released(lease);
            Check(origin.Requests.All(r => r.Path == "/root.m3u8"), "child not fetched before scope authorization");
        });
        yield return Case("closed dispatcher releases lease without evaluating it", async () =>
        {
            NativePlayback? closed = null;
            await AdaptiveMediaTests.Sta(async () => { closed = New(); await closed.DisposeAsync(); });
            var lease = new Lease(new("http://127.0.0.1:4123/wave")) { ThrowAddress = true };
            await Error(() => Open(closed!, lease), PlaybackError.DispatcherUnavailable);
            Released(lease); Check(lease.Checked.IsEmpty, "no dispatch or resource lookup");
        });
        yield return Case("cleanup failure is sanitized and blocks player reuse", async () =>
        {
            await using var origin = WaveOrigin();
            var player = New();
            try
            {
                var lease = LeaseFor(origin, "/wave"); lease.ThrowRelease = true;
                await Start(player, lease);
                await Error(player.StopAsync, PlaybackError.ReleaseFailed);
                Released(lease);
                var rejected = LeaseFor(origin, "/wave");
                await Error(() => Open(player, rejected), PlaybackError.ReleaseFailed); Released(rejected);
            }
            finally { try { await player.DisposeAsync(); } catch (PlaybackException error) { Check(error.Code == PlaybackError.ReleaseFailed, "release fault retained"); } }
        });
    }

    private static NativePlayback New() => new(audioOutput: NativeAudioOutput.Dummy);
    private static Task Open(NativePlayback player, Lease lease, CancellationToken token = default) =>
        player.OpenResourceAsync("film", "fixture-identity", lease, "合成资源", token);
    private static async Task Start(NativePlayback player, Lease lease)
    {
        await Open(player, lease);
        await Until(() => player.IsPlaying && player.DurationSeconds > 0, "native start");
    }
    private static Lease LeaseFor(Origin origin, string path) =>
        new(origin.Uri(path), origin.Headers) { Permit = uri => uri.GetLeftPart(UriPartial.Authority) == origin.Base.GetLeftPart(UriPartial.Authority) };
    private static Origin WaveOrigin()
    {
        var origin = new Origin();
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            const int length = 8000 * 2 * 40;
            writer.Write("RIFF"u8); writer.Write(length + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8);
            writer.Write(length); writer.Write(new byte[length]);
        }
        var wave = bytes.ToArray();
        origin.Route = _ => new Reply(wave, "audio/wav");
        return origin;
    }
    private static object? Session(NativePlayback player) =>
        typeof(NativePlayback).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player);
    private static TestCase Case(string name, Func<Task> run) => new("media-resource: " + name, () => AdaptiveMediaTests.Sta(run));
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
    private sealed class Lease(Uri address, Dictionary<string, string>? headers = null) : INativeMediaResourceLease
    {
        private readonly CancellationTokenSource revoked = new();
        internal Dictionary<string, string> HeaderValues { get; } = headers ?? [];
        internal Func<Uri, bool> Permit = _ => true;
        internal readonly ConcurrentQueue<Uri> Checked = new();
        internal bool Unrevocable, ThrowAddress, ThrowRelease;
        internal int DisposeCalls;
        internal volatile bool ReleaseComplete;
        internal Task? ReleaseGate;
        internal readonly TaskCompletionSource ReleaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri Address => ThrowAddress ? throw new IOException("secret-address-canary") : address;
        public IReadOnlyDictionary<string, string> Headers => HeaderValues;
        public CancellationToken Revoked => Unrevocable ? CancellationToken.None : revoked.Token;
        public bool Authorizes(Uri value) { Checked.Enqueue(value); return Permit(value); }
        internal void Revoke() => revoked.Cancel();
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

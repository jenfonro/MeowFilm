using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

// All media bytes are generated on demand, not staged as a media file. No socket,
// HTTP server/client, subprocess, external media or Node fixture is used.
public static class MediaReadLeaseTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string invalid in new[] { "unreadable", "unseekable", "writable", "negative",
            "unbounded", "extent-mismatch", "null-stream", "throwing-getter" })
            yield return Case("reject lease " + invalid, async () =>
            {
                await using var player = New();
                var lease = new Lease();
                switch (invalid)
                {
                    case "unreadable": lease.Stream.Readable = false; break;
                    case "unseekable": lease.Stream.Seekable = false; break;
                    case "writable": lease.Stream.Writable = true; break;
                    case "negative": lease.DeclaredLength = -1; break;
                    case "unbounded": lease.DeclaredLength = long.MaxValue; break;
                    case "extent-mismatch": lease.DeclaredLength--; break;
                    case "null-stream": lease.NullContent = true; break;
                    case "throwing-getter": lease.ThrowContent = true; break;
                }
                await Error(() => Open(player, lease), PlaybackError.InvalidReadLease);
                Released(lease);
                Check(lease.Stream.ReadCalls == 0 && !player.HasMedia, "no bytes/decoder for invalid lease");
            });
        yield return Case("invalid metadata and cancelled request preserve current owner", async () =>
        {
            await using var player = New();
            var active = new Lease(); await Start(player, active);
            var native = Player(player);
            var rejected = new Lease();
            await Error(() => player.OpenReadAsync("", "next", rejected, ""), PlaybackError.InvalidRequest);
            Released(rejected);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            rejected = new Lease();
            await Cancelled(() => player.OpenReadAsync("music", "next", rejected, "", cancel.Token));
            Released(rejected);
            Check(ReferenceEquals(native, Player(player)) && player.OwnerId == "film" &&
                active.DisposeCalls == 0, "invalid request does not replace decoder");
            await player.StopAsync(); Released(active);
        });
        yield return Case("already revoked and zero extent release once", async () =>
        {
            await using var player = New();
            var revoked = new Lease(); revoked.Revoke();
            await Error(() => Open(player, revoked), PlaybackError.ReadRevoked); Released(revoked);
            var empty = new Lease(Array.Empty<byte>());
            await Error(() => Open(player, empty), PlaybackError.ReadFormatUnsupported); Released(empty);
            Check(empty.Stream.ReadCalls == 0 && !player.HasMedia, "zero extent not unknown length");
        });
        yield return Case("unavailable dispatcher reclaims before returning", async () =>
        {
            NativePlayback? closed = null;
            await Sta(async () =>
            {
                closed = New();
                await closed.DisposeAsync();
            });
            var lease = new Lease();
            await Error(() => Open(closed!, lease), PlaybackError.DispatcherUnavailable);
            Released(lease);
            Check(lease.Stream.ReadCalls == 0, "unavailable dispatcher never opens input");
        });
        yield return Case("queued cancelled dispatcher operation releases without execution", async () =>
        {
            await using var player = New();
            var lease = new Lease();
            using var cancel = new CancellationTokenSource();
            Task request;
            using (Dispatcher.CurrentDispatcher.DisableProcessing())
            {
                var entered = new ManualResetEventSlim();
                request = Task.Run(async () =>
                {
                    entered.Set();
                    await Open(player, lease, cancel.Token);
                });
                Check(entered.Wait(2000), "worker entered");
                cancel.Cancel();
                // Dispatcher cannot process the open here, but cancelled ownership can retire.
                Check(lease.Released.Task.Wait(3000), "queued cancellation released");
                entered.Dispose();
            }
            await Cancelled(() => request); Released(lease);
            Check(lease.Stream.ReadCalls == 0 && !player.HasMedia, "cancelled delegate not run");
        });
        yield return Case("byte-at-a-time stream exact bytes seek EOF and one close", async () =>
        {
            var lease = new Lease(seconds: 0.02);
            lease.Stream.Chunk = 1;
            await using var input = await Input.Open(lease);
            Check(input.Media.Open(out ulong size) && size == (ulong)lease.Length, "known native size");
            Check(!input.Media.Open(out _), "single input open");
            for (long offset = 0; offset < lease.Length; offset++)
            {
                var (read, bytes) = input.Read(uint.MaxValue);
                Check(read == 1 && bytes[0] == lease.Stream.ByteAt(offset), "authorized byte sequence");
            }
            Check(input.Read(1).Count == 0, "exact EOF");
            Check(input.Media.Seek(17), "backward seek");
            Check(input.Read(1).Bytes[0] == lease.Stream.ByteAt(17), "seek byte");
            Check(input.Media.Seek((ulong)lease.Length) && input.Read(1).Count == 0, "seek EOF");
            Check(lease.Stream.MaximumRequest <= 65536, "fixed managed bound");
            input.Media.Close(); input.Media.Close();
            await input.DisposeAsync(); await input.DisposeAsync(); Released(lease);
            // Upstream GcHandle is a struct-valued property: Free() does not rewrite its
            // stored value. Inspect the base disposal latch, not a stale IsAllocated copy.
            Check((bool)typeof(MediaInput).GetField("disposedValue", BindingFlags.Instance |
                BindingFlags.NonPublic)!.GetValue(input.Media)!, "callback base disposed");
        });
        yield return Case("uint max request bounded and large virtual extent never cached wholesale", async () =>
        {
            var lease = new Lease(seconds: 600);
            await using var input = await Input.Open(lease);
            Check(lease.Stream.ReturnedBytes == 512, "probe bound");
            var (read, bytes) = input.Read(uint.MaxValue);
            Check(read == 65536 && bytes[0] == lease.Stream.ByteAt(0), "native request capped");
            Check(input.Media.Seek((ulong)lease.Length - 7), "large seek");
            var tail = input.Read(uint.MaxValue);
            Check(tail.Count == 7 && tail.Bytes.SequenceEqual(Enumerable.Range(0, 7)
                .Select(i => lease.Stream.ByteAt(lease.Length - 7 + i))), "tail bytes");
            Check(lease.Stream.ReturnedBytes == 512 + 65536 + 7 && lease.Stream.MaximumRequest <= 65536,
                "no full-file prefetch in adapter");
        });
        foreach (string bad in new[] { "outside", "throws", "wrong-offset" })
            yield return Case("seek fails closed " + bad, async () =>
            {
                var lease = new Lease();
                await using var input = await Input.Open(lease);
                lease.Stream.ThrowSeek = bad == "throws";
                lease.Stream.WrongSeek = bad == "wrong-offset";
                Check(!input.Media.Seek(bad == "outside" ? ulong.MaxValue : 100), "bad seek refused");
                Check(input.ErrorCode == PlaybackError.ReadSeekFailed && input.Read(1).Count == -1, "terminal seek failure");
                await input.DisposeAsync(); Released(lease);
            });
        foreach (string bad in new[] { "truncated", "changed", "throws", "invalid-count" })
            yield return Case("read fails closed " + bad, async () =>
            {
                var lease = new Lease();
                await using var input = await Input.Open(lease);
                switch (bad)
                {
                    case "truncated": lease.Stream.EofAt = 0; break;
                    case "changed": lease.Stream.LengthDelta = 1; break;
                    case "throws": lease.Stream.ThrowRead = true; break;
                    case "invalid-count": lease.Stream.InvalidCount = true; break;
                }
                string code = bad switch
                {
                    "changed" => PlaybackError.ReadChanged, "throws" => PlaybackError.ReadFailed,
                    _ => PlaybackError.ReadTruncated
                };
                Check(input.Read(32).Count == -1 && input.ErrorCode == code, "fixed terminal read error");
                Check(!input.Media.Seek(0), "failed input cannot restart");
                await input.DisposeAsync(); Released(lease);
            });
        yield return Case("revocation during byte return copies no unauthorized buffer", async () =>
        {
            var lease = new Lease();
            await using var input = await Input.Open(lease);
            lease.Stream.AfterRead = lease.Revoke;
            var result = input.Read(128);
            Check(result.Count == -1 && input.ErrorCode == PlaybackError.ReadRevoked &&
                result.Bytes.All(value => value == 0xa5), "no copy after revocation");
            await input.DisposeAsync(); Released(lease);
        });
        foreach (var (name, content) in new[]
        {
            ("m3u disguised wav", "#EXTM3U\n#EXTINF:1,x\nhttps://media.invalid/secret.wav"),
            ("hls MIME audio wav", "\uFEFF \n#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"https://media.invalid/key\""),
            ("dash MIME video mp4", "<?xml version=\"1.0\"?><MPD><BaseURL>https://media.invalid/</BaseURL></MPD>"),
            ("smooth MIME octet stream", "<SmoothStreamingMedia><StreamIndex Url=\"https://media.invalid/\"/></SmoothStreamingMedia>"),
            ("pls filename mp3", "[playlist]\nFile1=https://media.invalid/file.wav"),
            ("xspf MIME audio flac", "<playlist><trackList><track><location>https://media.invalid/</location></track></trackList></playlist>"),
            ("HTTP MIME envelope", "HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\n\r\n#EXTM3U\nhttps://media.invalid/"),
            ("html", "<html><audio src=\"https://media.invalid/x.wav\"></audio></html>")
        })
            yield return Case("reject playlist or MIME poison " + name, async () =>
            {
                await using var player = New();
                var lease = new Lease(Encoding.UTF8.GetBytes(content));
                await Error(() => player.OpenReadAsync("film", "download.wav", lease,
                    "Content-Type: audio/wav; :demux=playlist"), PlaybackError.ReadFormatUnsupported);
                Released(lease);
                Check(!player.HasMedia && Session(player) is null && Engine(player) is null, "rejected before native discovery");
            });
        yield return Case("native WAV callback playback seek pause tracks and stop without paths", async () =>
        {
            await using var player = New();
            var lease = new Lease(seconds: 600);
            await player.OpenReadAsync("film", "https://media.invalid/not-an-input.m3u8", lease,
                ":demux=playlist Content-Type: application/vnd.apple.mpegurl");
            await Until(() => player.IsPlaying && player.DurationSeconds > 590, "native playing and duration");
            var native = Player(player);
            Check(native.NativeReference != IntPtr.Zero && player.NativeVersion?.StartsWith("3.0.24") == true, "actual pinned libVLC");
            var session = Session(player)!;
            var media = (LibVLCSharp.Shared.Media)Field(session, "Media")!;
            Check(media.Mrl.StartsWith("imem", StringComparison.OrdinalIgnoreCase), "callback MRL only");
            Check(Field(session, "Input") is null && Field(session, "ReadInput") is not null, "no HTTP input");
            var tracks = (await player.ReadTracksAsync())!;
            Check(tracks.Audio.Count > 0 && tracks.Scope.Identity == player.Identity, "native audio tracks");
            await player.SelectAudioTrackAsync(tracks.Scope, tracks.AudioId);
            await player.SetRateAsync(tracks.Scope, 1.25);
            Check(!player.Tracks!.EffectiveRateVerified, "requested rate not output evidence");
            await player.SetRateAsync(tracks.Scope, 1);
            await player.PauseAsync(); await Until(() => !player.IsPlaying, "native paused");
            await player.SeekAsync(500);
            await player.ResumeAsync();
            await Until(() => lease.Stream.LastReadOffset > 45_000_000 && player.PositionSeconds >= 499,
                "native seek reaches granted stream beyond native cache");
            Check(lease.Stream.MaximumRequest <= 65536 && lease.Stream.ReturnedBytes < lease.Length / 2,
                "bounded reads not whole-file cache");
            Check(player.ObservedVideoOutput is null, "audio test is not video or hardware evidence");
            await player.StopAsync();
            Released(lease); Check(native.NativeReference == IntPtr.Zero && !player.HasMedia && player.OwnerId is null,
                "stop native ownership cleared");
        });
        yield return Case("native short-read WAV starts and releases", async () =>
        {
            await using var player = New();
            var lease = new Lease(seconds: 3); lease.Stream.Chunk = 31;
            await Start(player, lease);
            Check(lease.Stream.ReadCalls > 20, "short reads reach native");
            await player.StopAsync(); Released(lease);
        });
        yield return Case("native natural end releases before reentrant queue", async () =>
        {
            await using var player = New();
            var first = new Lease(seconds: 0.15); var second = new Lease();
            int ended = 0, failed = 0; Task? next = null;
            player.Failed += (_, _) => failed++;
            player.Ended += (_, _) =>
            {
                ended++; Released(first);
                Check(Session(player) is null && !player.HasMedia, "retired before Ended");
                next = player.OpenReadAsync("music", "queued", second, "");
            };
            await Open(player, first);
            await Until(() => next is not null, "natural end");
            await next!;
            await Until(() => player.IsPlaying && player.Identity == "queued", "reentrant queue playing");
            Check(ended == 1 && failed == 0, "natural end once");
            await player.StopAsync(); Released(first); Released(second);
        });
        yield return Case("native corrupt binary signature cannot become playlist success", async () =>
        {
            await using var player = New();
            var bytes = Encoding.UTF8.GetBytes("RIFF0000WAVE#EXTM3U\nhttps://media.invalid/not-playable.wav");
            var lease = new Lease(bytes);
            int starts = 0, ends = 0, fails = 0;
            player.Started += (_, _) => starts++; player.Ended += (_, _) => ends++; player.Failed += (_, _) => fails++;
            try { await Open(player, lease); }
            catch (PlaybackException error) { Check(error.Code is PlaybackError.OpenFailed or PlaybackError.PlaybackFailed, "fixed native rejection"); }
            await Until(() => !player.HasMedia && lease.ReleaseComplete && fails == 1, "native rejects corrupt WAV");
            Check(starts == 0 && ends == 0 && Session(player) is null, "not a fake playable format");
            Released(lease);
        });
        yield return Case("owner cancellation on Dispose is not a false revoked natural end", async () =>
        {
            await using var player = New();
            var lease = new Lease(seconds: 0.15) { RevokeOnDispose = true };
            int ended = 0, failed = 0;
            player.Ended += (_, _) => { Released(lease); ended++; };
            player.Failed += (_, _) => failed++;
            await Open(player, lease);
            await Until(() => ended == 1, "self-revoking lease ends normally");
            Check(failed == 0 && player.LastErrorCode is null, "own release does not turn EOF into revocation");
            Released(lease);
        });
        yield return Case("owner cancellation on Dispose preserves caller cancellation during probe", async () =>
        {
            await using var player = New();
            using var caller = new CancellationTokenSource();
            var lease = new Lease { RevokeOnDispose = true };
            lease.Stream.BlockAt = 0; lease.Stream.UnblockOnlyOnDispose = true;
            int failed = 0; player.Failed += (_, _) => failed++;
            var opening = Open(player, lease, caller.Token);
            await lease.Stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Cancelled(() => opening); Released(lease);
            Check(failed == 0 && !player.HasMedia, "caller cancellation is not revoked failure");
        });
        yield return Case("native input Close keeps revocation guarded until terminal retirement", async () =>
        {
            var lease = new Lease();
            await using var input = await Input.Open(lease);
            input.Media.Close();
            Check(input.Read(1).Count == -1 && lease.DisposeCalls == 0, "no reads after demux Close");
            lease.Revoke();
            Check(input.ErrorCode == PlaybackError.ReadRevoked, "buffered playback retains revocation signal");
            await input.DisposeAsync(); Released(lease);
        });
        foreach (string action in new[] { "cancel", "revoke", "stop", "replace", "dispose" })
            yield return Case("blocked preflight retired by " + action, () => Blocked(action, preflight: true));
        foreach (string action in new[] { "cancel", "revoke", "stop", "replace", "dispose" })
            yield return Case("blocked native callback retired by " + action, () => Blocked(action, preflight: false));
        yield return Case("paused cached playback revocation stops and never ends", async () =>
        {
            await using var player = New();
            var lease = new Lease(); int ended = 0, failed = 0;
            player.Ended += (_, _) => ended++; player.Failed += (_, _) => failed++;
            await Start(player, lease);
            var native = Player(player);
            await player.PauseAsync(); await Until(() => !player.IsPlaying, "paused before revoke");
            lease.Revoke();
            await Until(() => !player.HasMedia && failed == 1, "revoke while cached and paused");
            Released(lease);
            Check(native.NativeReference == IntPtr.Zero && ended == 0 &&
                player.LastErrorCode == PlaybackError.ReadRevoked, "revocation is terminal failure not EOF");
        });
        yield return Case("old cancellation revocation and track scope cannot affect replacement", async () =>
        {
            await using var player = New();
            using var cancelled = new CancellationTokenSource();
            var first = new Lease(); await Start(player, first, cancelled.Token);
            var old = Player(player); var tracks = (await player.ReadTracksAsync())!;
            var engine = Engine(player);
            var second = new Lease();
            await player.OpenReadAsync("music", "same-identity", second, "");
            await Until(() => player.IsPlaying, "replacement playing");
            Check(old.NativeReference == IntPtr.Zero && ReferenceEquals(engine, Engine(player)), "one reusable engine one decoder");
            Released(first);
            cancelled.Cancel(); first.Revoke();
            await Error(() => player.SetRateAsync(tracks.Scope, 2), PlaybackError.Superseded);
            await Task.Delay(100);
            Check(player.OwnerId == "music" && player.Identity == "same-identity" && player.HasMedia &&
                second.DisposeCalls == 0, "old generation isolated");
            await player.StopAsync(); await player.StopAsync();
            await player.DisposeAsync(); await player.DisposeAsync();
            Released(first); Released(second);
            var afterDispose = new Lease();
            await Error(() => Open(player, afterDispose), PlaybackError.Disposed); Released(afterDispose);
        });
        yield return Case("stop awaits asynchronous lease release not just stop acceptance", async () =>
        {
            await using var player = New();
            var lease = new Lease(); await Start(player, lease);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lease.DisposeGate = gate.Task;
            Task stop = player.StopAsync();
            try
            {
                await lease.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(60);
                Check(!stop.IsCompleted && lease.DisposeCalls == 1 && !lease.ReleaseComplete, "real release awaited");
            }
            finally { gate.TrySetResult(); }
            await stop.WaitAsync(TimeSpan.FromSeconds(8)); Released(lease);
        });
        yield return Case("release failure is fixed and decoder reuse fails closed", async () =>
        {
            var player = New(); var lease = new Lease { ThrowDispose = true };
            try
            {
                await Start(player, lease);
                await Error(player.StopAsync, PlaybackError.ReleaseFailed);
                Check(lease.DisposeCalls == 1 && Session(player) is null, "failed disposal invoked once");
                var next = new Lease();
                await Error(() => Open(player, next), PlaybackError.ReleaseFailed); Released(next);
                await Error(() => player.DisposeAsync().AsTask(), PlaybackError.ReleaseFailed);
                Check(lease.DisposeCalls == 1, "no second release after error");
            }
            finally { try { await player.DisposeAsync(); } catch (PlaybackException) { } }
        });
        yield return Case("native read error retires before Failed and cannot emit Ended", async () =>
        {
            await using var player = New();
            var lease = new Lease(seconds: 600); int ends = 0, fails = 0;
            player.Ended += (_, _) => ends++;
            player.Failed += (_, _) => { Released(lease); Check(Session(player) is null, "retired before Failed"); fails++; };
            await Start(player, lease);
            var native = Player(player);
            lease.Stream.ThrowRead = true;
            await player.SeekAsync(500);
            await Until(() => fails == 1, "read error retires native");
            Check(ends == 0 && !player.HasMedia && native.NativeReference == IntPtr.Zero &&
                player.LastErrorCode == PlaybackError.ReadFailed, "read error not EOF");
        });
        yield return Case("native seek error stops rather than pretending requested position succeeded", async () =>
        {
            await using var player = New();
            var lease = new Lease(seconds: 600); int ends = 0, fails = 0;
            player.Ended += (_, _) => ends++; player.Failed += (_, _) => fails++;
            await Start(player, lease);
            var native = Player(player);
            lease.Stream.ThrowSeek = true;
            await player.SeekAsync(500);
            await Until(() => fails == 1, "seek error retires native");
            Released(lease);
            Check(ends == 0 && !player.HasMedia && native.NativeReference == IntPtr.Zero &&
                player.LastErrorCode == PlaybackError.ReadSeekFailed, "terminal seek error");
        });
    }

    private static async Task Blocked(string action, bool preflight)
    {
        await using var player = New();
        using var caller = new CancellationTokenSource();
        var lease = new Lease(seconds: 600);
        lease.Stream.BlockAt = preflight ? 0 : 65536;
        // Deliberately ignore the read cancellation token; only actual lease release unblocks it.
        lease.Stream.UnblockOnlyOnDispose = true;
        int ends = 0, fails = 0;
        player.Ended += (_, _) => ends++; player.Failed += (_, _) => fails++;
        Task open = Open(player, lease, caller.Token);
        await lease.Stream.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(8));
        MediaPlayer? old = Session(player) is null ? null : Player(player);
        var replacement = action == "replace" ? new Lease() : null;
        switch (action)
        {
            case "cancel": caller.Cancel(); break;
            case "revoke": lease.Revoke(); break;
            case "stop": await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(8)); break;
            case "replace": await player.OpenReadAsync("music", "replacement", replacement!, "").WaitAsync(TimeSpan.FromSeconds(8)); break;
            case "dispose": await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)); break;
        }
        if (preflight)
        {
            if (action == "revoke") await Error(() => open, PlaybackError.ReadRevoked);
            else await Cancelled(() => open);
        }
        else
        {
            try { await open.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (OperationCanceledException) when (action != "revoke") { }
            catch (PlaybackException error) when (action == "revoke" && error.Code == PlaybackError.ReadRevoked) { }
        }
        await Until(() => lease.ReleaseComplete &&
            (action == "replace" ? player.Identity == "replacement" && player.IsPlaying : !player.HasMedia),
            "blocked input fully retired");
        Released(lease);
        Check(ends == 0 && (action == "revoke" ? fails == 1 : fails == 0), "no success terminal for retired input");
        if (old is not null) Check(old.NativeReference == IntPtr.Zero, "blocked native reference retired");
        if (replacement is not null) { await player.StopAsync(); Released(replacement); }
    }

    private static NativePlayback New() => new(audioOutput: NativeAudioOutput.Dummy);
    private static Task Open(NativePlayback player, Lease lease, CancellationToken token = default) =>
        player.OpenReadAsync("film", "same-identity", lease, "合成音频", token);
    private static async Task Start(NativePlayback player, Lease lease, CancellationToken token = default)
    {
        await Open(player, lease, token);
        await Until(() => player.IsPlaying && player.DurationSeconds > 0, "native started");
    }
    private static object? Field(object value, string name) =>
        value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value);
    private static object? Session(NativePlayback player) => Field(player, "_session");
    private static object? Engine(NativePlayback player) => Field(player, "_engine");
    private static MediaPlayer Player(NativePlayback player) => (MediaPlayer)Field(Session(player)!, "Player")!;
    private static TestCase Case(string name, Func<Task> body) => new("media-read-lease: " + name, () => Sta(body));
    private static void Check(bool condition, string stage)
    { if (!condition) throw new InvalidOperationException("Read lease assertion: " + stage); }
    private static void Released(Lease lease) =>
        Check(lease.DisposeCalls == 1 && lease.Stream.CloseCalls == 1 && lease.ReleaseComplete, "exactly one actual lease/stream release");
    private static async Task Error(Func<Task> action, string code)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (PlaybackException error)
        {
            Check(error.Code == code && error.Message == code && error.InnerException is null, "fixed error " + code);
            return;
        }
        throw new InvalidOperationException("Expected fixed error " + code);
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation");
    }
    private static async Task Until(Func<bool> predicate, string stage)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Read lease stage: " + stage);
            await Task.Delay(15);
        }
    }
    private static async Task Sta(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await body(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
        finally { Check(thread.Join(3000), "STA dispatcher exited"); }
    }

    // Exercise the actual internal MediaInput callbacks without adding a public testing API.
    private sealed class Input(MediaInput media) : IAsyncDisposable
    {
        internal MediaInput Media => media;
        internal string? ErrorCode => (string?)media.GetType().GetProperty("LastErrorCode",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(media);
        internal static async Task<Input> Open(Lease lease)
        {
            var type = typeof(NativePlayback).Assembly.GetType("MeowFilm.Media.ReadLeaseMediaInput", true)!;
            var media = (MediaInput)Activator.CreateInstance(type, BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic, null, [lease], null)!;
            try
            {
                Invoke("Validate");
                Invoke("AttachOpening", CancellationToken.None);
                await (Task)Invoke("PrepareAsync")!;
                return new(media);
            }
            catch { await ((IAsyncDisposable)media).DisposeAsync(); throw; }

            object? Invoke(string name, params object[] args)
            {
                try { return type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(media, args); }
                catch (TargetInvocationException error) when (error.InnerException is not null)
                { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            }
        }
        internal (int Count, byte[] Bytes) Read(uint count)
        {
            int size = (int)Math.Min(65536, Math.Max(count, 1));
            var before = Enumerable.Repeat((byte)0xa5, size).ToArray();
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(before, 0, pointer, size);
                int read = media.Read(pointer, count);
                var result = new byte[read >= 0 ? read : size];
                Marshal.Copy(pointer, result, 0, result.Length);
                return (read, result);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        public ValueTask DisposeAsync() => ((IAsyncDisposable)media).DisposeAsync();
    }

    private sealed class Lease : INativeMediaReadLease
    {
        private readonly CancellationTokenSource revoked = new();
        internal readonly GeneratedStream Stream;
        internal long DeclaredLength;
        internal bool NullContent, ThrowContent, ThrowDispose, RevokeOnDispose;
        internal int DisposeCalls;
        internal volatile bool ReleaseComplete;
        internal Task? DisposeGate;
        internal readonly TaskCompletionSource ReleaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream Content => ThrowContent ? throw new IOException("private-path-cookie-canary")
            : NullContent ? null! : Stream;
        public long Length => DeclaredLength;
        public CancellationToken Revoked => revoked.Token;
        internal Lease(double seconds = 60) : this(WaveHeader(seconds), (long)(48000 * seconds) * 2 + 44) { }
        internal Lease(byte[] bytes) : this(bytes, bytes.Length) { }
        private Lease(byte[] prefix, long length)
        { DeclaredLength = length; Stream = new(prefix, length, () => revoked.IsCancellationRequested); }
        internal void Revoke() => revoked.Cancel();
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            ReleaseEntered.TrySetResult();
            if (DisposeGate is not null) await DisposeGate.ConfigureAwait(false);
            if (RevokeOnDispose) revoked.Cancel();
            Stream.Dispose();
            ReleaseComplete = true; Released.TrySetResult();
            if (ThrowDispose) throw new IOException("private-dispose-canary");
        }
        private static byte[] WaveHeader(double seconds)
        {
            int bytes = checked((int)(48000 * seconds) * 2);
            var header = new byte[44];
            "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), bytes + 36);
            "WAVEfmt "u8.CopyTo(header.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), 48000);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 96000);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
            "data"u8.CopyTo(header.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), bytes);
            return header;
        }
    }

    private sealed class GeneratedStream(byte[] prefix, long length, Func<bool> revoked) : Stream
    {
        private long position;
        private volatile bool closed;
        private readonly TaskCompletionSource close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReadBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Readable = true, Seekable = true, Writable, ThrowSeek, WrongSeek, ThrowRead, InvalidCount, UnblockOnlyOnDispose;
        internal long LengthDelta, EofAt = long.MaxValue, BlockAt = long.MaxValue, LastReadOffset, ReturnedBytes;
        internal int Chunk = int.MaxValue, MaximumRequest, ReadCalls, CloseCalls;
        internal Action? AfterRead;
        public override bool CanRead => Readable && !closed;
        public override bool CanSeek => Seekable && !closed;
        public override bool CanWrite => Writable;
        public override long Length { get { Authorized(); return length + LengthDelta; } }
        public override long Position { get { Authorized(); return position; } set => Seek(value, SeekOrigin.Begin); }
        private void Authorized()
        {
            if (closed || revoked()) throw new ObjectDisposedException("authorized-stream");
        }
        internal byte ByteAt(long offset) => offset < prefix.Length ? prefix[(int)offset] : (byte)((offset - 44) * 17 + 11);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Authorized(); cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref ReadCalls);
            MaximumRequest = Math.Max(MaximumRequest, buffer.Length);
            LastReadOffset = position;
            if (position >= BlockAt)
            {
                ReadBlocked.TrySetResult();
                if (UnblockOnlyOnDispose) await close.Task.ConfigureAwait(false);
                else await close.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                Authorized();
            }
            if (ThrowRead) throw new IOException("private-path-token-read-canary");
            if (InvalidCount) return buffer.Length + 1;
            if (position >= EofAt) return 0;
            int count = (int)Math.Min(Math.Min(Chunk, buffer.Length), length - position);
            for (int i = 0; i < count; i++) buffer.Span[i] = ByteAt(position + i);
            position += count;
            Interlocked.Add(ref ReturnedBytes, count);
            AfterRead?.Invoke();
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Authorized();
            if (ThrowSeek) throw new IOException("private-seek-canary");
            Check(origin == SeekOrigin.Begin && offset >= 0 && offset <= length, "bounded direct stream seek");
            position = offset;
            return WrongSeek ? offset + 1 : offset;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("No synchronous stream read expected");
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Interlocked.Increment(ref CloseCalls); closed = true; close.TrySetResult(); }
            base.Dispose(disposing);
        }
    }
}

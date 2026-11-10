using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using MeowFilm.Media;
using A = MeowFilm.Windows.Tests.AdaptiveMediaTests;

namespace MeowFilm.Windows.Tests;

// No public upstream, media paths, Node, ffmpeg, real output, or old test-group execution.
public static class LiveHlsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string profile in new[] { "ts", "aes", "master", "mp4" })
            yield return Case("native rolling decode discontinuity pause resume " + profile, () => Native(profile));
        foreach (bool encrypted in new[] { false, true })
            yield return Case("native expired segment window recovery " + encrypted, () => Gap(encrypted));
        yield return Case("native live ENDLIST transition naturally releases", Ended);
        yield return Case("production resource lease paused revocation releases once", Revoked);
        foreach (string command in new[] { "pause", "resume-pause", "stop", "replace", "dispose", "revoke" })
            yield return Case("recovery waiting command fence " + command, () => RecoveryFence(command));
        yield return Case("later pause fences a queued live resume", QueuedResume);
        yield return Case("accepted recovery Play pause before frame observation resumes same input", RecoveryFramePause);
        yield return Case("recovery two attempt budget prohibits a third Play", RecoveryBudget);
        yield return Case("unexplained live EOF fails instead of false natural completion", UnexpectedEof);
        yield return Case("new EOF hook leaves VOD pause resume end unchanged", VodControl);
        yield return Case("live fMP4 discontinuity repeats exact validated initialization", Mp4Initialization);
        yield return Case("live fMP4 map switch explicit boundary", MapSwitch);
        foreach (string blocked in new[] { "manifest", "key", "segment" })
            yield return Case("blocked " + blocked + " stop replacement old generation drains", () => Blocked(blocked));
        yield return Case("reload spacing changed and unchanged no busy retry", Spacing);
        yield return Case("AES byte exact window Range and zero retired keys", BytesAndKeys);
        yield return Case("128 advancing windows bounded catalog validators and keys", Bounded);
        yield return Case("admitted retired payload retains key until read drains", Retained);
        yield return Case("cross origin reload redirect preserves input headers", Redirect);
        yield return Case("transient reload two retries then success", RetrySuccess);
        yield return Case("transient reload third failure terminal", () => Terminal("server"));
        yield return Case("nine unchanged reloads terminal no infinite poll", () => Terminal("stale"));
        yield return Case("current missing media is not an expired-window gap", CurrentMissing);
        yield return Case("EVENT append retains original start", Event);
        yield return Case("invalid live update wins simultaneous local disconnect", FailureFence);
        yield return Case("archive refuses live before any segment or key IO", DownloadBoundary);
        foreach (string bad in new[] { "rollback", "uri", "range", "duration", "discontinuity", "target",
            "key", "file", "master", "sample-aes", "ll-hls", "short-window", "vod-live" })
            yield return Case("invalid update or profile " + bad, () => Invalid(bad));
    }
    private static TestCase Case(string name, Func<Task> run) => new("live-hls: " + name, () => A.Sta(run));
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException("Live: " + why); }
    private static async Task Until(Func<bool> test, string phase, int milliseconds = 14000)
    {
        var clock = Stopwatch.StartNew();
        while (!test())
        {
            if (clock.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Live: " + phase);
            await Task.Delay(15);
        }
    }
    private static object? Prop(object item, string name) => item.GetType().GetProperty(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(item);
    private static object? Invoke(object item, string name, params object?[] args) => item.GetType().GetMethod(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(item, args);
    private static void Set(object item, string field, object value) => item.GetType().GetField(field,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(item, value);
    private static NativeHttpMediaOptions Options() =>
        new NativeHttpMediaOptions(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    private static NativePlayback Player() => new(Dispatcher.CurrentDispatcher, null, NativeAudioOutput.Dummy, Options());
    private static Task Open(NativePlayback player, A.Origin origin, CancellationToken token = default) =>
        player.OpenAsync("film", "live-synthetic", origin.Uri("root.m3u8"), "合成直播", origin.Headers, token);
    private static ulong Audio(object session) => (ulong)((LibVLCSharp.Shared.Media)A.Field(session, "Media")!).Statistics.DecodedAudio;
    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] IV(ulong sequence)
    { var iv = new byte[16]; BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(8), sequence); return iv; }
    private static byte[] Key(int number) => Enumerable.Range(0, 16).Select(i => (byte)(17 + number * 37 + i)).ToArray();
    private static byte[] Encrypt(byte[] bytes, byte[] key, byte[] iv)
    { using var aes = Aes.Create(); aes.Key = key; return aes.EncryptCbc(bytes, iv, PaddingMode.PKCS7); }

    private sealed class Feed(string profile = "ts")
    {
        internal const ulong Sequence = 0x100000002;
        internal int Start, Count = 8, Loads, Expired = -1, FailureCount;
        internal bool Rolling, End, Event, ExpireNext;
        internal string? Invalid, Block, RedirectTo;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly ConcurrentQueue<long> LoadTimes = new();
        internal bool Encrypted => profile is "aes" or "master";
        internal bool Mp4 => profile == "mp4";
        internal string Manifest(int start)
        {
            var b = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n");
            if (Event) b.AppendLine("#EXT-X-PLAYLIST-TYPE:EVENT");
            b.AppendLine("#EXT-X-MEDIA-SEQUENCE:" + (Sequence + (ulong)start));
            b.AppendLine("#EXT-X-DISCONTINUITY-SEQUENCE:" + start / 4);
            b.AppendLine("#EXT-X-PROGRAM-DATE-TIME:" + new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(start * (Mp4 ? 1 : 1.008)).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            if (Mp4) b.AppendLine("#EXT-X-MAP:URI=\"init\"");
            for (int i = start; i < start + Count; i++)
            {
                if (i > start && i % 4 == 0) b.AppendLine("#EXT-X-DISCONTINUITY");
                if (Encrypted && (i == start || i % 4 == 0))
                    b.AppendLine("#EXT-X-KEY:METHOD=AES-128,URI=\"key" + i / 4 + "\"");
                b.AppendLine("#EXTINF:" + (Mp4 ? "1" : "1.008") + ",").AppendLine("s" + i);
            }
            if (End) b.AppendLine("#EXT-X-ENDLIST");
            string text = b.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
            return Invalid switch
            {
                "rollback" => text.Replace((Sequence + (ulong)start).ToString(), (Sequence - 1).ToString()),
                "uri" => text.Replace("s1\n", "different\n"),
                "range" => text.Replace("s1\n", "#EXT-X-BYTERANGE:1000@0\ns1\n"),
                "duration" => text.Replace("#EXTINF:1.008", "#EXTINF:0.9"),
                "discontinuity" => text.Replace("#EXT-X-DISCONTINUITY-SEQUENCE:0", "#EXT-X-DISCONTINUITY-SEQUENCE:1"),
                "target" => text.Replace("#EXT-X-TARGETDURATION:1", "#EXT-X-TARGETDURATION:2"),
                "key" => text.Replace("key0", "changed-key"),
                "file" => text.Replace("s1\n", "file:///C:/never-authorized\n"),
                "master" => "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\nunexpected-child.m3u8\n",
                "sample-aes" => text.Replace("AES-128", "SAMPLE-AES"),
                "ll-hls" => text + "#EXT-X-PART:DURATION=0.2,URI=\"part\"\n",
                "vod-live" => text.Replace("#EXT-X-VERSION:7", "#EXT-X-PLAYLIST-TYPE:VOD"),
                _ => text
            };
        }
        internal A.Reply Route(string path)
        {
            if (path == "/root.m3u8" && profile == "master")
                return new(Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.6B\"\nchild.m3u8\n" +
                    "#EXT-X-STREAM-INF:BANDWIDTH=256000,CODECS=\"mp4a.6B\"\ninactive.m3u8\n"));
            if (path is "/root.m3u8" or "/child.m3u8" or "/inactive.m3u8")
            {
                Interlocked.Increment(ref Loads); LoadTimes.Enqueue(Stopwatch.GetTimestamp());
                if (RedirectTo is { } redirect) return A.Reply.Redirect(redirect);
                if (Interlocked.CompareExchange(ref FailureCount, 0, 0) > 0)
                { Interlocked.Decrement(ref FailureCount); return new([]) { Status = 503 }; }
                int start = Rolling ? Math.Max(Start, (int)(Clock.Elapsed.TotalSeconds / 1.008)) : Start;
                return new(Text(Manifest(start))) { Tag = "\"live-" + start + (End ? "-end" : "") + "\"",
                    BlockBody = Block == "manifest" };
            }
            if (path.StartsWith("/key", StringComparison.Ordinal) && int.TryParse(path[4..], out int key))
                return new(Key(key), "application/octet-stream") { BlockBody = Block == "key" };
            if (path == "/init") return new(A.Init(0), "video/mp4");
            if (path.StartsWith("/s", StringComparison.Ordinal) && int.TryParse(path[2..], out int index))
            {
                if (ExpireNext)
                {
                    ExpireNext = false; Expired = index; Start = index + 1;
                    return new([]) { Status = 410 };
                }
                byte[] data = Mp4 ? A.Fragment(index) : A.Ts(index);
                if (Encrypted) data = Encrypt(data, Key(index / 4), IV(Sequence + (ulong)index));
                return new(data, "application/octet-stream") { BlockBody = Block == "segment" };
            }
            return new([]) { Status = 404 };
        }
    }
    private static async Task Native(string profile)
    {
        await using var origin = new A.Origin(); var feed = new Feed(profile) { Rolling = true }; origin.Route = feed.Route;
        await using var player = Player();
        await Open(player, origin); await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player); ulong initial = Audio(session);
        try
        {
            await Until(() => origin.Requests.Any(r => r.Path.StartsWith("/s", StringComparison.Ordinal) &&
                int.TryParse(r.Path[2..], out int n) && n >= 9) && Audio(session) > initial, "new window really decoded");
        }
        catch { Diagnostic(player, session, origin, feed); throw; }
        Check(ReferenceEquals(session, A.Session(player)) && player.OwnerId == "film" && player.LastErrorCode is null, "one owner/player");
        await player.PauseAsync(); await Until(() => !player.IsPlaying, "paused");
        await Task.Delay(700); await player.ResumeAsync(); await A.Decoded(player);
        if (profile == "master") Check(origin.Requests.Count(r => r.Path == "/inactive.m3u8") == 1, "unused rendition not polled");
        Check(origin.Requests.All(r => r.Authorized && !r.LocalCapability), "credentials scoped; local capability never upstream");
        await player.StopAsync(); await A.Released(session, port);
    }
    private static async Task Gap(bool encrypted)
    {
        await using var origin = new A.Origin(); var feed = new Feed(encrypted ? "aes" : "ts") { Rolling = true }; origin.Route = feed.Route;
        await using var player = Player(); await Open(player, origin); await A.Decoded(player);
        var session = A.Session(player)!; int port = A.Port(player); ulong before = Audio(session);
        feed.ExpireNext = true;
        await Until(() => feed.Expired >= 0, "upstream expired an actual native segment");
        try
        {
            await Until(() => origin.Requests.Any(r => r.Path.StartsWith("/s", StringComparison.Ordinal) &&
                int.TryParse(r.Path[2..], out int n) && n > feed.Expired) && Audio(session) > before &&
                A.Field(player, "_liveRecovery") is { } recovery && (long)A.Field(recovery, "Tried")! > 0,
                "new validated window payload plus actual native recovery frame");
        }
        catch { Diagnostic(player, session, origin, feed); throw; }
        await Until(() => A.Field(player, "_liveRecovery") is { } recovery &&
            (int)A.Field(recovery, "Consecutive")! == 0, "budget reset by actual new input frame");
        Check(ReferenceEquals(session, A.Session(player)) && player.LastErrorCode is null, "same native decoder owner survives gap");
        Check(origin.Requests.Count(r => r.Path == "/s" + feed.Expired) <= (encrypted ? 3 : 1), "no unbounded expired retry");
        await (Task)Invoke(player, "FinishSessionAsync", session, null)!;
        Check(player.HasMedia && ReferenceEquals(session, A.Session(player)), "late old EOF cannot stop new input");
        await player.StopAsync(); await A.Released(session, port);
    }
    private static void Diagnostic(NativePlayback player, object session, A.Origin origin, Feed feed)
    {
        // Synthetic fixture paths only; never native log text, caller headers, capabilities or real URLs.
        var media = (LibVLCSharp.Shared.Media)A.Field(session, "Media")!;
        Console.WriteLine($"DIAG live hasMedia={player.HasMedia} playing={player.IsPlaying} error={player.LastErrorCode ?? "none"} " +
            $"loads={feed.Loads} expired={feed.Expired} frames={(media.NativeReference == IntPtr.Zero ? -1 : media.Statistics.DecodedAudio)} " +
            "syntheticRequests=" + string.Join(",", origin.Requests.Select(r => r.Path).TakeLast(60)));
        var adaptive = A.Field(session, "Adaptive")!;
        var resources = (IDictionary)A.Field(A.Field(adaptive, "_manifest")!, "_resources")!;
        foreach (object value in resources.Values)
            if (A.Field(value, "Window") is { } window)
                Console.WriteLine($"DIAG window first={Prop(window, "First")} last={Prop(window, "Last")} end={Prop(window, "Ended")}");
    }
    private static async Task Ended()
    {
        await using var origin = new A.Origin(); var feed = new Feed(); origin.Route = feed.Route;
        await using var player = Player(); int ended = 0; player.Ended += (_, _) => ended++;
        await Open(player, origin); await A.Decoded(player); var session = A.Session(player)!; int port = A.Port(player);
        feed.End = true;
        await Until(() => ended == 1 && !player.HasMedia, "natural final ENDLIST", 18000);
        Check(player.LastErrorCode is null, "not failure"); await A.Released(session, port);
    }
    private sealed class Lease(A.Origin origin) : INativeMediaResourceLease
    {
        internal readonly CancellationTokenSource Stop = new();
        internal int Disposals;
        public Uri Address => origin.Uri("root.m3u8");
        public IReadOnlyDictionary<string, string> Headers => origin.Headers;
        public CancellationToken Revoked => Stop.Token;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); Stop.Cancel(); return ValueTask.CompletedTask; }
    }
    private static async Task Revoked()
    {
        await using var origin = new A.Origin(); var feed = new Feed("aes") { Rolling = true }; origin.Route = feed.Route;
        await using var player = new NativePlayback(Dispatcher.CurrentDispatcher, null, NativeAudioOutput.Dummy);
        var lease = new Lease(origin);
        await player.OpenResourceAsync("music", "live-lease", lease, "合成直播"); await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player);
        await player.PauseAsync(); lease.Stop.Cancel();
        await Until(() => !player.HasMedia && lease.Disposals == 1, "revoke paused lease");
        await A.Released(session, port); await player.StopAsync();
        Check(lease.Disposals == 1 && lease.Stop.IsCancellationRequested, "real once ownership and revocable lifetime");
        lease.Stop.Dispose();
    }
    private static async Task Blocked(string kind)
    {
        await using var origin = new A.Origin(); var feed = new Feed("aes") { Rolling = true }; origin.Route = feed.Route;
        using var cancel = new CancellationTokenSource();
        await using var player = Player(); await Open(player, origin, cancel.Token); await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player); feed.Block = kind;
        await Until(() => origin.Blocked > 0, "blocked " + kind);
        if (kind == "key") cancel.Cancel();
        await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); await A.Released(session, port);
        feed.Block = null; feed.End = false;
        await Open(player, origin); await A.Decoded(player); cancel.Cancel(); await Task.Delay(100);
        Check(player.HasMedia && player.LastErrorCode is null, "late old callback cannot retire replacement");
        await player.StopAsync();
    }
    private static async Task RecoveryFence(string command)
    {
        await using var origin = new A.Origin(); var feed = new Feed("aes") { Rolling = true }; origin.Route = feed.Route;
        await using var player = command == "revoke"
            ? new NativePlayback(Dispatcher.CurrentDispatcher, null, NativeAudioOutput.Dummy) : Player();
        Lease? lease = command == "revoke" ? new(origin) : null;
        if (lease is not null) await player.OpenResourceAsync("film", "live-synthetic", lease, "合成直播");
        else await Open(player, origin);
        await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player);
        var native = (LibVLCSharp.Shared.MediaPlayer)A.Field(session, "Player")!;
        IntPtr pointer = native.NativeReference;
        int plays = 0;
        EventHandler<EventArgs> playing = (_, _) => Interlocked.Increment(ref plays);
        native.Playing += playing;
        var gate = (SemaphoreSlim)A.Field(player, "_commands")!;
        await gate.WaitAsync();
        Task? operation = null, olderResume = null;
        try
        {
            feed.ExpireNext = true;
            await Until(() => A.Field(player, "_liveRecovery") is { } recovery &&
                (bool)A.Field(recovery, "Running")!, "actual EOF recovery queued at native command gate");
            if (command == "resume-pause") olderResume = player.ResumeAsync();
            operation = command switch
            {
                "pause" or "resume-pause" => player.PauseAsync(),
                "stop" => player.StopAsync(),
                "replace" => Open(player, origin),
                "dispose" => player.DisposeAsync().AsTask(),
                _ => null
            };
            if (command == "revoke") lease!.Stop.Cancel();
            await Task.Delay(100);
            Check(Volatile.Read(ref plays) == 0, "no automatic Play while fenced");
        }
        finally { gate.Release(); }
        if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(8));
        if (olderResume is not null) await olderResume.WaitAsync(TimeSpan.FromSeconds(8));
        if (command is "pause" or "resume-pause")
        {
            await Task.Delay(300);
            Check(!player.IsPlaying && Volatile.Read(ref plays) == 0 &&
                ReferenceEquals(session, A.Session(player)), "paused intent defeats queued recovery");
            await player.ResumeAsync();
            await Until(() => Volatile.Read(ref plays) == 1 && player.IsPlaying, "only explicit resume may Play");
            Check(native.NativeReference == pointer && ReferenceEquals(session, A.Session(player)), "same exact native player");
            native.Playing -= playing;
            await player.StopAsync(); await A.Released(session, port);
        }
        else
        {
            if (command == "revoke") await Until(() => lease!.Disposals == 1 && !player.HasMedia, "recovery revocation drains");
            Check(Volatile.Read(ref plays) == 0, "old recovering player never Play");
            await A.Released(session, port);
            if (command == "replace") { await A.Decoded(player); Check(!ReferenceEquals(session, A.Session(player)), "new explicit owner session only"); await player.StopAsync(); }
        }
        if (lease is not null) { Check(lease.Disposals == 1, "lease once"); lease.Stop.Dispose(); }
    }
    private static async Task QueuedResume()
    {
        await using var origin = new A.Origin(); var feed = new Feed { Rolling = true }; origin.Route = feed.Route;
        await using var player = Player(); await Open(player, origin); await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player);
        var native = (LibVLCSharp.Shared.MediaPlayer)A.Field(session, "Player")!;
        await player.PauseAsync(); await Until(() => !player.IsPlaying, "initial live pause");
        int plays = 0;
        EventHandler<EventArgs> playing = (_, _) => Interlocked.Increment(ref plays);
        native.Playing += playing;
        var gate = (SemaphoreSlim)A.Field(player, "_commands")!;
        await gate.WaitAsync();
        Task resume, pause;
        try
        {
            resume = player.ResumeAsync();
            pause = player.PauseAsync();
            Check((int)A.Field(A.Field(player, "_liveRecovery")!, "Paused")! == 1,
                "latest intent latched before command gate");
        }
        finally { gate.Release(); }
        await Task.WhenAll(resume, pause).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Check(Volatile.Read(ref plays) == 0 && !player.IsPlaying && ReferenceEquals(session, A.Session(player)),
            "superseded Resume never unpauses");
        await player.ResumeAsync(); await Until(() => player.IsPlaying && Volatile.Read(ref plays) == 1, "fresh explicit resume");
        native.Playing -= playing;
        await player.StopAsync(); await A.Released(session, port);
    }
    private static async Task RecoveryFramePause()
    {
        await using var origin = new A.Origin(); var feed = new Feed { Rolling = true }; origin.Route = feed.Route;
        await using var player = Player(); await Open(player, origin); await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player);
        var native = (LibVLCSharp.Shared.MediaPlayer)A.Field(session, "Player")!;
        IntPtr pointer = native.NativeReference;
        int plays = 0; Task? pause = null;
        EventHandler<EventArgs> playing = (_, _) => Interlocked.Increment(ref plays);
        // Registered before recovery's observer: deliver real native progress, then exercise
        // Pause before the observer may count it as this attempt's successful first frame.
        System.ComponentModel.PropertyChangedEventHandler progress = (_, args) =>
        {
            if (args.PropertyName == nameof(NativePlayback.PositionSeconds) && pause is null &&
                A.Field(player, "_liveRecovery") is { } state && (bool)A.Field(state, "Running")! &&
                (bool)A.Field(state, "PlayIssued")! && (int)A.Field(state, "Consecutive")! == 1)
                pause = player.PauseAsync();
        };
        native.Playing += playing; player.PropertyChanged += progress;
        try
        {
            feed.ExpireNext = true;
            await Until(() => pause is not null, "pause after actual recovery Play before frame observation");
            await pause!.WaitAsync(TimeSpan.FromSeconds(5));
            object state = A.Field(player, "_liveRecovery")!;
            await ((Task)A.Field(state, "Work")!).WaitAsync(TimeSpan.FromSeconds(5));
            Check((int)A.Field(state, "Consecutive")! == 1 && !player.IsPlaying &&
                Volatile.Read(ref plays) == 1, "paused accepted attempt counted but never reset/replayed");
            await Task.Delay(200);
            Check(Volatile.Read(ref plays) == 1 && !player.IsPlaying, "no automatic post-pause Play");
            await player.ResumeAsync();
            await Until(() => player.IsPlaying && (int)A.Field(state, "Consecutive")! == 0,
                "explicit resume obtains new native progress then resets budget");
            Check(native.NativeReference == pointer && ReferenceEquals(session, A.Session(player)) &&
                player.LastErrorCode is null && Volatile.Read(ref plays) == 2,
                "resume unpauses exact input, no automatic restart");
        }
        finally { player.PropertyChanged -= progress; native.Playing -= playing; }
        await player.StopAsync(); await A.Released(session, port);
    }
    private static async Task RecoveryBudget()
    {
        await using var origin = new A.Origin(); var feed = new Feed { Rolling = true }; origin.Route = feed.Route;
        await using var player = Player(); int ended = 0; player.Ended += (_, _) => ended++;
        await Open(player, origin); await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player);
        object state = Invoke(player, "LiveState", session)!;
        Set(state, "Consecutive", 2); // Inject only the exhausted budget, not a successful native recovery.
        feed.ExpireNext = true;
        await Until(() => !player.HasMedia, "exhausted recovery fails");
        Check(player.LastErrorCode == PlaybackError.HttpExpired && ended == 0, "no third attempt or false Ended");
        await A.Released(session, port);
    }
    private static async Task UnexpectedEof()
    {
        await using var origin = new A.Origin(); var feed = new Feed(); origin.Route = feed.Route;
        await using var player = Player(); int ended = 0; player.Ended += (_, _) => ended++;
        await Open(player, origin); await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player);
        await Task.Run(((LibVLCSharp.Shared.MediaPlayer)A.Field(session, "Player")!).Stop);
        await (Task)Invoke(player, "FinishSessionAsync", session, null)!;
        Check(!player.HasMedia && player.LastErrorCode == PlaybackError.HttpExpired && ended == 0, "no validated recovery ticket means failure");
        await A.Released(session, port);
    }
    private static async Task VodControl()
    {
        await using var origin = new A.Origin(); var feed = new Feed { End = true, Count = 4 }; origin.Route = feed.Route;
        await using var player = Player(); int ended = 0; player.Ended += (_, _) => ended++;
        await Open(player, origin); await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player);
        Check(A.Field(A.Adaptive(player)!, "_live") is null, "VOD has no live coordinator");
        await player.PauseAsync(); await Until(() => !player.IsPlaying, "VOD original pause");
        await player.ResumeAsync(); await Until(() => ended == 1, "VOD original Ended", 10000);
        Check(!player.HasMedia && player.LastErrorCode is null && feed.Loads == 1, "VOD not auto replayed");
        await A.Released(session, port);
    }
    private static async Task Mp4Initialization()
    {
        await using var origin = new A.Origin(); var feed = new Feed("mp4"); origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        string[] segments = Manual.Segments(await m.Manifest(false));
        byte[] bytes = await m.Client.GetByteArrayAsync(segments[4]);
        Check(bytes.AsSpan().SequenceEqual(A.Init(0).Concat(A.Fragment(4)).ToArray()), "only exact validated init prefix then original fragment");
        feed.Start = 4; string[] next = Manual.Segments(await m.Manifest());
        Check(next[0] == segments[4] && (await m.Client.GetByteArrayAsync(next[0])).AsSpan().SequenceEqual(bytes), "overlap prefix representation immutable");
    }
    private static async Task MapSwitch()
    {
        await using var origin = new A.Origin(); var feed = new Feed("mp4"); origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        origin.Route = path => path == "/root.m3u8"
            ? new(Text(feed.Manifest(8).Replace("URI=\"init\"", "URI=\"different-init\""))) : feed.Route(path);
        await m.Error(PlaybackError.AdaptiveUnsupported);
        Check(!origin.Requests.Any(r => r.Path == "/different-init"), "unsupported init switch before payload");
    }

    private sealed class Manual : IAsyncDisposable
    {
        internal object Session = null!;
        internal object Live => A.Field(Session, "_live")!;
        internal IDictionary Resources => (IDictionary)A.Field(A.Field(Session, "_manifest")!, "_resources")!;
        internal Uri Root => (Uri)Prop(Session, "Root")!;
        internal readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(6) };
        internal static async Task<Manual> Open(A.Origin origin, Feed feed, CancellationToken token = default)
        {
            var method = typeof(NativePlayback).Assembly.GetType("MeowFilm.Media.AdaptiveMediaSession")!
                .GetMethod("OpenAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var task = (Task)method.Invoke(null, [Text(feed.Manifest(feed.Start)), origin.Uri("root.m3u8"),
                origin.Headers, Options(), token])!;
            await task;
            var result = new Manual { Session = Prop(task, "Result")! };
            return result;
        }
        // Only scheduling-independent tests use this. Spacing/native tests use real monotonic time.
        internal void Due()
        {
            foreach (var slot in ((IDictionary)A.Field(Live, "playlists")!).Values) Set(slot!, "Next", 0L);
        }
        internal async Task<string> Manifest(bool due = true)
        {
            if (due) Due();
            return await Client.GetStringAsync(Root);
        }
        internal static string[] Segments(string text) => text.Split('\n').Where(s => s.StartsWith("http", StringComparison.Ordinal)).ToArray();
        internal async Task Error(string expected)
        {
            try { await Manifest(); } catch (HttpRequestException) { } catch (TaskCanceledException) { }
            await Until(() => (string?)Prop(Session, "LastErrorCode") is not null, "fixed terminal state", 4000);
            Check((string?)Prop(Session, "LastErrorCode") == expected, "classified " + expected);
        }
        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)Session).DisposeAsync(); Client.Dispose();
            int port = (int)Prop(Session, "Port")!;
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); } finally { listener.Stop(); }
            Check(Resources.Count == 0 && ((IDictionary)A.Field(Live, "playlists")!).Count == 0, "catalog and live slots really cleared");
        }
    }
    private static async Task Spacing()
    {
        await using var origin = new A.Origin(); var feed = new Feed(); origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        for (int i = 0; i < 10; i++) await m.Manifest(false);
        Check(feed.Loads == 0, "initial target wait not bypassed");
        await Task.Delay(1050); await m.Manifest(false); Check(feed.Loads == 1, "first eligible fetch");
        await Task.Delay(200); await m.Manifest(false); Check(feed.Loads == 1, "unchanged half target minimum");
        await Task.Delay(350); feed.Start = 1; await m.Manifest(false); Check(feed.Loads == 2, "unchanged half target reached");
        await Task.Delay(550); await m.Manifest(false); Check(feed.Loads == 2, "changed full target wait");
        var times = feed.LoadTimes.ToArray();
        Check((double)(times[1] - times[0]) / Stopwatch.Frequency >= .5, "observed upstream cadence");
    }
    private static object[] Keys(Manual m) => m.Resources.Values.Cast<object>()
        .Select(r => A.Field(r, "Encryption")).OfType<object>().Select(e => Prop(e, "Key")!).Distinct().ToArray();
    private static async Task BytesAndKeys()
    {
        await using var origin = new A.Origin(); var feed = new Feed("aes"); origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        string[] paths = Manual.Segments(await m.Manifest(false));
        byte[] full = await m.Client.GetByteArrayAsync(paths[0]);
        Check(full.AsSpan().SequenceEqual(A.Ts(0)), "every decrypted byte exact");
        using var request = new HttpRequestMessage(HttpMethod.Get, paths[0]);
        request.Headers.Range = new RangeHeaderValue(188, 999);
        using var response = await m.Client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.PartialContent &&
            (await response.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(full.AsSpan(188, 812)), "plaintext range exact");
        byte[][] old = Keys(m).Select(k => A.Field(k, "bytes")).OfType<byte[]>().ToArray();
        Check(old.Length > 0 && old.Any(k => k.Any(b => b != 0)), "actual key loaded");
        feed.Start = 12; string text = await m.Manifest();
        Check(old.All(k => k.All(b => b == 0)), "departed keys zeroed before session ends");
        Check(!text.Contains("KEY", StringComparison.Ordinal) && !text.Contains(origin.Base.AbsoluteUri, StringComparison.Ordinal), "native manifest no keys/upstream");
        Check((await m.Client.GetByteArrayAsync(Manual.Segments(text)[0])).AsSpan().SequenceEqual(A.Ts(12)), "rotation after full gap exact");
    }
    private static async Task Bounded()
    {
        await using var origin = new A.Origin(); var feed = new Feed("aes"); origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        for (int i = 0; i < 128; i++)
        {
            feed.Start = i * 8; string text = await m.Manifest();
            if (i % 8 == 0) Check((await m.Client.GetByteArrayAsync(Manual.Segments(text)[0])).AsSpan()
                .SequenceEqual(A.Ts(feed.Start)), "stream remains byte exact");
            Check(m.Resources.Count == 9 && Keys(m).Length <= 3, "window not history");
        }
        Check(((IDictionary)A.Field(A.Field(m.Session, "_transport")!, "_representations")!).Count == 0, "no transport representation history");
        Check(((IDictionary)A.Field(m.Live, "removed")!).Count <= 2048, "bounded tombstones");
    }
    private static async Task Retained()
    {
        await using var origin = new A.Origin(); var feed = new Feed("aes"); origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        string first = Manual.Segments(await m.Manifest(false))[0];
        await m.Client.GetByteArrayAsync(first);
        object resource = m.Resources[new Uri(first).AbsolutePath]!;
        object key = Prop(A.Field(resource, "Encryption")!, "Key")!;
        byte[] bytes = (byte[])A.Field(key, "bytes")!;
        // Same production acquire/release used by socket handlers, held across the commit.
        var acquire = (Task)Invoke(m.Live, "AcquireAsync", new Uri(first).AbsolutePath, CancellationToken.None)!;
        await acquire; object held = Prop(acquire, "Result")!;
        feed.Start = 16; await m.Manifest();
        Check(bytes.Any(b => b != 0), "admitted retired reference owns key");
        Invoke(held, "Release");
        Check(bytes.All(b => b == 0), "last admitted release really zeros key");
    }
    private static async Task Redirect()
    {
        await using var origin = new A.Origin(); await using var other = new A.Origin { RequireAuthorization = false };
        var feed = new Feed("aes"); origin.Route = feed.Route; other.Route = path =>
            path == "/root.m3u8" ? new(Text(feed.Manifest(8))) : feed.Route(path);
        await using var m = await Manual.Open(origin, feed);
        feed.RedirectTo = other.Uri("root.m3u8").AbsoluteUri;
        string text = await m.Manifest();
        await m.Client.GetByteArrayAsync(Manual.Segments(text)[0]);
        Check(other.Requests.All(r => r.AnyCallerHeader && r.Authorized && !r.LocalCapability), "explicit input headers accompany redirects and children");
        int previous = origin.Requests.Count;
        await m.Manifest();
        Check(origin.Requests.Count == previous, "reload pins the effective representation");
    }
    private static async Task RetrySuccess()
    {
        await using var origin = new A.Origin(); var feed = new Feed { FailureCount = 2 }; origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        await m.Manifest(); await m.Manifest(); feed.Start = 1; string text = await m.Manifest();
        Check(text.Contains("#EXT-X-MEDIA-SEQUENCE:" + (Feed.Sequence + 1), StringComparison.Ordinal) &&
            Prop(m.Session, "LastErrorCode") is null && feed.Loads == 3, "two retries then actual advance");
    }
    private static async Task Terminal(string reason)
    {
        await using var origin = new A.Origin(); var feed = new Feed { FailureCount = reason == "server" ? 3 : 0 }; origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        for (int i = 0; i < (reason == "server" ? 2 : 8); i++) await m.Manifest();
        await m.Error(reason == "server" ? PlaybackError.HttpServerFailed : PlaybackError.HttpExpired);
        Check(feed.Loads == (reason == "server" ? 3 : 9), "bounded upstream attempts");
    }
    private static async Task CurrentMissing()
    {
        await using var origin = new A.Origin(); var feed = new Feed(); origin.Route = p => p == "/s0" ? new([]) { Status = 404 } : feed.Route(p);
        await using var m = await Manual.Open(origin, feed);
        string first = Manual.Segments(await m.Manifest(false))[0]; m.Due();
        try { await m.Client.GetByteArrayAsync(first); } catch (HttpRequestException) { }
        await Until(() => Prop(m.Session, "LastErrorCode") is not null, "current missing fails");
        Check((string?)Prop(m.Session, "LastErrorCode") == PlaybackError.HttpNotFound, "not treated as completed/gap");
    }
    private static async Task Event()
    {
        await using var origin = new A.Origin(); var feed = new Feed { Event = true }; origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        string[] first = Manual.Segments(await m.Manifest(false)); feed.Count = 10;
        string[] next = Manual.Segments(await m.Manifest());
        Check(next.Take(first.Length).SequenceEqual(first), "append URLs stable");
        feed.Start = 1; await m.Error(PlaybackError.AdaptiveInvalid);
    }
    private static async Task FailureFence()
    {
        await using var origin = new A.Origin(); var feed = new Feed(); origin.Route = feed.Route;
        await using var m = await Manual.Open(origin, feed);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var error = (PlaybackException)Activator.CreateInstance(typeof(PlaybackException),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [PlaybackError.AdaptiveInvalid], null)!;
        Invoke(m.Session, "ReportRequestFailure", error, true, cancelled.Token);
        Check((string?)Prop(m.Session, "LastErrorCode") == PlaybackError.AdaptiveInvalid, "not masked by local disconnect");
    }
    private static async Task DownloadBoundary()
    {
        await using var origin = new A.Origin(); var feed = new Feed("aes"); origin.Route = feed.Route;
        try
        {
            await using var archive = await NativeAdaptiveArchive.CreateAsync(Text(feed.Manifest(0)), origin.Uri("root.m3u8"),
                origin.Headers, Options(), 1024 * 1024);
            throw new InvalidOperationException("live incorrectly published as finite archive");
        }
        catch (PlaybackException error) { Check(error.Code == PlaybackError.AdaptiveUnsupported, "explicit live archive boundary"); }
        Check(origin.Requests.IsEmpty, "no key/segment fetch for rejected live archive");
    }
    private static async Task Invalid(string scenario)
    {
        await using var origin = new A.Origin(); var feed = new Feed("aes"); origin.Route = feed.Route;
        if (scenario is "short-window" or "vod-live")
        {
            feed.Invalid = scenario; if (scenario == "short-window") feed.Count = 2;
            await using var player = Player();
            try { await Open(player, origin); throw new InvalidOperationException("profile accepted"); }
            catch (PlaybackException error)
            { Check(error.Code == (scenario == "short-window" ? PlaybackError.AdaptiveInvalid : PlaybackError.AdaptiveUnsupported), "explicit profile state"); }
            Check(player.NativeVersion is null && !origin.Requests.Any(r => r.Path.StartsWith("/key", StringComparison.Ordinal)), "no key or decoder");
            return;
        }
        await using var m = await Manual.Open(origin, feed); string original = feed.Manifest(0); feed.Invalid = scenario;
        Check(feed.Manifest(0) != original, "failure fixture really changes input");
        string expected = scenario switch
        {
            "rollback" or "target" or "master" => PlaybackError.AdaptiveInvalid,
            "file" => PlaybackError.UnsupportedUri,
            "sample-aes" => PlaybackError.AdaptiveDrmUnsupported,
            "ll-hls" => PlaybackError.AdaptiveUnsupported,
            _ => PlaybackError.HttpRepresentationChanged
        };
        await m.Error(expected);
        Check(!origin.Requests.Any(r => r.Path == "/unexpected-child.m3u8" || r.Path == "/part"), "invalid nested requests never forwarded");
    }
}

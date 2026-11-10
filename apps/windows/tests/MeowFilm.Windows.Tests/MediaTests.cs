using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

/// <summary>Native code fixtures only: generated PCM WAV, dummy output, no user media or App launch.</summary>
public static class MediaTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("media.contract.public-surface", Contract);
        yield return new("media.contract.initial-state-volume-dispose", () => Sta(InitialState));
        yield return new("media.contract.invalid-uri", () => Sta(InvalidRequests));
        yield return new("media.contract.headers-fail-closed", () => Sta(HeaderRequests));
        yield return new("media.contract.invalid-seek", () => Sta(InvalidSeek));
        yield return new("media.contract.dispatcher-construction", Construction);
        // Must precede the other native cases in the standalone runner: proves default native lookup.
        yield return new("media.native.wav-dummy-default-discovery", () => Sta(DefaultDiscovery));
        yield return new("media.native.wav-dummy-start-pause-resume-seek-stop", () => Sta(Playback));
        yield return new("media.native.wav-dummy-ends-queue-after-release", () => Sta(EndQueue));
        yield return new("media.native.wav-dummy-failure-sanitized-recovery", () => Sta(FailureRecovery));
        yield return new("media.native.wav-dummy-rejected-request-preserves-current", () => Sta(RejectPreserves));
        yield return new("media.native.wav-dummy-cancel-opening-and-active", () => Sta(Cancellation));
        yield return new("media.native.wav-dummy-replacement-old-token-events", () => Sta(Replacement));
        yield return new("media.native.wav-dummy-queued-open-latest-wins", () => Sta(QueuedOpen));
        yield return new("media.native.wav-dummy-stop-during-open", () => Sta(StopOpening));
        yield return new("media.native.wav-dummy-dispose-during-open", () => Sta(DisposeOpening));
        yield return new("media.native.wav-dummy-empty-header-overload", () => Sta(EmptyHeaders));
        yield return new("media.native.wav-dummy-cross-thread-commands-ui-events", () => Sta(CrossThread));
        yield return new("media.native.wav-dummy-repeat-release-no-idle-progress", () => Sta(RepeatRelease));
        yield return new("media.http.direct-addresses", HttpPolicy);
        yield return new("media.http.userinfo-read-redirect-and-explicit-authorization", HttpUserInfo);
        yield return new("media.http.long-url-public-read", HttpLongUrl);
        yield return new("media.http.headers-snapshot-and-redirects", HttpRedirectScope);
        yield return new("media.http.redirect-bounds-and-status-safe-errors", HttpSafeErrors);
        yield return new("media.http.adaptive-and-unclassified-content-rejected", HttpUnsupportedContent);
        yield return new("media.http.ranges-seek-and-bounded-native-buffer", HttpRanges);
        yield return new("media.http.nonseekable-200-unknown-and-weak-validator", HttpSequential);
        yield return new("media.http.invalid-initial-range-and-encoding", HttpInvalidRanges);
        yield return new("media.http.changed-validator-range-and-redirect-no-splice", HttpChangedRepresentation);
        yield return new("media.http.truncated-body-is-error-not-eof", HttpTruncation);
        yield return new("media.http.open-read-seek-deadlines-and-cancellation", HttpDeadlines);
        yield return new("media.native.http-wav-dummy-seek-stop", () => Sta(HttpNativePlayback));
        yield return new("media.native.http-blocked-read-stop-cancel-dispose-replace", () => Sta(HttpNativeRetirement));
        yield return new("media.native.http-preflight-stop-and-latest-wins", () => Sta(HttpNativePreflight));
        yield return new("media.native.http-read-failure-not-ended", () => Sta(HttpNativeFailure));
        yield return new("media.native.http-no-header-original-direct-path", () => Sta(HttpNativeDirect));
        yield return new("media.native.http-userinfo-no-header-decodes", () => Sta(HttpNativeUserInfo));
    }

    private static Task Contract()
    {
        var type = typeof(NativePlayback);
        Check(type.IsSealed && type.IsPublic);
        Check(typeof(INotifyPropertyChanged).IsAssignableFrom(type));
        Check(typeof(IAsyncDisposable).IsAssignableFrom(type));
        foreach (string property in new[] { "OwnerId", "Identity", "Title", "IsPlaying", "HasMedia",
                     "PositionSeconds", "DurationSeconds", "Volume", "VideoView", "LastErrorCode" })
            Check(type.GetProperty(property, BindingFlags.Instance | BindingFlags.Public) is not null);
        foreach (string name in new[] { "PauseAsync", "ResumeAsync", "SeekAsync", "StopAsync" })
            Check(type.GetMethod(name)!.ReturnType == typeof(Task));
        var open = type.GetMethod("OpenAsync", [typeof(string), typeof(string), typeof(Uri),
            typeof(string), typeof(CancellationToken)]);
        Check(open?.ReturnType == typeof(Task));
        Check(type.GetMethod("DisposeAsync")!.ReturnType == typeof(ValueTask));
        Check(type.GetConstructor([typeof(Dispatcher), typeof(string), typeof(NativeAudioOutput)])!
            .GetParameters().All(parameter => parameter.IsOptional));
        foreach (string name in new[] { "Started", "Ended", "Failed" })
            Check(type.GetEvent(name)!.EventHandlerType == typeof(EventHandler));
        Check(type.GetProperty("VideoView")!.PropertyType == typeof(FrameworkElement));
        Check(type.GetProperty("Volume")!.CanWrite);
        return Task.CompletedTask;
    }

    private static NativePlayback New() =>
        new(Dispatcher.CurrentDispatcher, NativePath(), NativeAudioOutput.Dummy);

    private static string NativePath() =>
        Path.Combine(AppContext.BaseDirectory, "libvlc",
            RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => throw new InvalidOperationException("Unsupported fixture architecture")
            });

    private static async Task InitialState()
    {
        // Original null/default call must not become ambiguous with the transport-policy overload.
        await using (var compatible = new NativePlayback(null)) Check(!compatible.HasMedia);
        var player = New();
        int started = 0, ended = 0, failed = 0;
        player.Started += (_, _) => started++;
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        Check(!player.HasMedia && !player.IsPlaying && player.OwnerId is null && player.Identity is null);
        Check(player.Title == "" && player.PositionSeconds == 0 && player.DurationSeconds == 0);
        Check(player.VideoView is VideoView && ((VideoView)player.VideoView).MediaPlayer is null);
        Check(((VideoView)player.VideoView).Content is null);
        Check(player.Volume == 100);
        player.Volume = -20;
        Check(player.Volume == 0);
        player.Volume = 120;
        Check(player.Volume == 100);
        await player.PauseAsync();
        await player.ResumeAsync();
        await player.SeekAsync(0);
        await player.StopAsync();
        await player.DisposeAsync();
        await player.DisposeAsync();
        await ExpectCode(() => player.StopAsync(), PlaybackError.Disposed);
        await ExpectCode(() => player.PauseAsync(), PlaybackError.Disposed);
        await ExpectCode(() => player.ResumeAsync(), PlaybackError.Disposed);
        await ExpectCode(() => player.SeekAsync(0), PlaybackError.Disposed);
        await ExpectCode(() => player.OpenAsync("owner", "item", new Uri("https://example.invalid/media"), ""), PlaybackError.Disposed);
        ExpectSyncCode(() => player.Volume = 50, PlaybackError.Disposed);
        Check(started == 0 && ended == 0 && failed == 0);
    }

    private static async Task InvalidRequests()
    {
        await using var player = New();
        foreach (var uri in new Uri?[] { null, new("relative", UriKind.Relative), new("ftp://example.invalid/a"),
                     new("data:audio/wav,AAAA"), new("rtsp://example.invalid/a"),
                     new("file:///C:/fixture.wav?credential=fixture-canary"), new("file:///C:/fixture.wav#fragment") })
            await ExpectCode(() => player.OpenAsync("owner", "id", uri!, ""), PlaybackError.UnsupportedUri);
        var valid = new Uri("https://example.invalid/a?fixture-token=not-a-real-secret");
        await ExpectCode(() => player.OpenAsync("", "id", valid, ""), PlaybackError.InvalidRequest);
        await ExpectCode(() => player.OpenAsync("owner", "", valid, ""), PlaybackError.InvalidRequest);
        await ExpectCode(() => player.OpenAsync("owner", "id", valid, null!), PlaybackError.InvalidRequest);
        Check(!player.HasMedia && player.LastErrorCode is null);
    }

    private static async Task HeaderRequests()
    {
        await using var player = New();
        var uri = new Uri("https://example.invalid/a");
        foreach (string name in new[] { "Host", "Range", "If-Range", "Accept-Encoding", "Connection",
                     "Proxy-Authorization", "Content-Length", "Transfer-Encoding" })
            await ExpectCode(() => player.OpenAsync("owner", "id", uri, "",
                new Dictionary<string, string> { [name] = "fixture-canary" }), PlaybackError.UnsupportedHeaders);
        foreach (var headers in new IReadOnlyDictionary<string, string>[]
        {
            new Dictionary<string, string> { ["X-Fixture\r\nHost"] = "canary" },
            new Dictionary<string, string> { ["Cookie"] = "canary\r\nInjected: secret" },
            new Dictionary<string, string> { ["Cookie"] = "canary\0secret" },
            new Dictionary<string, string> { ["X-Fixture"] = new string('a', 8193) },
            new Dictionary<string, string> { ["cookie"] = "one", ["Cookie"] = "two" }
        })
            await ExpectCode(() => player.OpenAsync("owner", "id", uri, "", headers), PlaybackError.InvalidHeaders);
        await ExpectCode(() => player.OpenAsync("owner", "id", uri, "",
            (IReadOnlyDictionary<string, string>)null!), PlaybackError.InvalidRequest);
        Check(!player.HasMedia && player.Identity is null);
    }

    private static async Task InvalidSeek()
    {
        await using var player = New();
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, double.MaxValue })
            await ExpectCode(() => player.SeekAsync(value), PlaybackError.InvalidSeek);
        Check(player.PositionSeconds == 0);
    }

    private static Task Construction() => Task.Run(() =>
    {
        // Test runner workers are MTA. Do not construct a native surface on the wrong thread.
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA);
        ExpectSyncCode(() => _ = new NativePlayback(audioOutput: NativeAudioOutput.Dummy), PlaybackError.WrongThread);
    });

    private static async Task DefaultDiscovery()
    {
        using var wave = new Wave(6);
        // Exactly the caller's default dispatcher/path, with the test-only dummy output explicitly selected.
        await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
        await Start(player, wave.Uri);
        Check(player.NativeVersion?.StartsWith("3.0.24", StringComparison.Ordinal) == true);
        Console.WriteLine($"OBSERVED native version={player.NativeVersion}; architecture={RuntimeInformation.ProcessArchitecture}; runtime={Environment.Version}");
        await player.StopAsync();
        wave.AssertReleased();
    }

    private static async Task Playback()
    {
        using var wave = new Wave(12);
        await using var player = New();
        int started = 0, ended = 0, failed = 0, progress = 0;
        bool actualPlaying = false;
        player.Started += (_, _) =>
        {
            started++;
            actualPlaying = ((VideoView)player.VideoView).MediaPlayer!.State == VLCState.Playing;
        };
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        player.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(player.PositionSeconds)) progress++; };
        Check(started == 0);
        await Start(player, wave.Uri, "music", "fixture-1");
        Check(actualPlaying && started == 1 && player.IsPlaying && player.HasMedia);
        Check(player.OwnerId == "music" && player.Identity == "fixture-1" && player.Title == "generated WAV");
        var native = ((VideoView)player.VideoView).MediaPlayer!;
        Check(native.Hwnd != IntPtr.Zero); // Even an unmounted audio-only fixture has no external-video fallback.
        await Eventually(() => player.DurationSeconds > 11.8 && player.PositionSeconds > 0.2);
        int dummyVolumeBefore = native.Volume;
        player.Volume = 23;
        await player.PauseAsync();
        await Eventually(() => !player.IsPlaying && native.State == VLCState.Paused);
        Check(player.Volume == 23);
        // Pinned VLC 3.0.24 adummy.c explicitly has volume_set = NULL.
        // Its cached readback is unchanged, not evidence of audible output volume.
        Check(native.Volume == dummyVolumeBefore && native.Volume != player.Volume);
        Console.WriteLine($"OBSERVED dummy volume: requested={player.Volume}; native-before={dummyVolumeBefore}; native-after={native.Volume}");
        await Task.Delay(300);
        var paused = player.PositionSeconds;
        await Task.Delay(300);
        Check(Math.Abs(player.PositionSeconds - paused) < 0.05);
        await player.SeekAsync(4);
        // Paused seeks publish their accepted position without claiming playback.
        Check(Math.Abs(player.PositionSeconds - 4) < 0.05 && !player.IsPlaying);
        await Task.Delay(300);
        Check(Math.Abs(player.PositionSeconds - 4) < 0.05 && !player.IsPlaying);
        await Eventually(() => native.Time >= 3900 && native.Time < 4500);
        await player.ResumeAsync();
        await Eventually(() => player.IsPlaying && player.PositionSeconds >= 4);
        Check(started == 1); // Resume Playing is not a second session Started.
        await Task.Delay(600);
        Check(progress > 0 && progress < 30);
        await player.StopAsync();
        Check(!player.HasMedia && !player.IsPlaying && player.Identity is null && player.OwnerId is null);
        Check(player.PositionSeconds == 0 && player.DurationSeconds == 0);
        Check(((VideoView)player.VideoView).MediaPlayer is null && native.NativeReference == IntPtr.Zero);
        Check(ended == 0 && failed == 0);
        wave.AssertReleased();
    }

    private static async Task EndQueue()
    {
        using var first = new Wave(0.9);
        using var second = new Wave(0.9);
        await using var player = New();
        int ended = 0, started = 0;
        bool releasedAtEnd = true;
        var nativePlayers = new List<MediaPlayer>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Started += (_, _) =>
        {
            started++;
            nativePlayers.Add(((VideoView)player.VideoView).MediaPlayer!);
        };
        player.Failed += (_, _) => done.TrySetException(new InvalidOperationException("Unexpected fixed playback failure"));
        player.Ended += async (_, _) =>
        {
            try
            {
                releasedAtEnd &= !player.HasMedia && !player.IsPlaying &&
                    ((VideoView)player.VideoView).MediaPlayer is null && nativePlayers.All(native => native.NativeReference == IntPtr.Zero);
                ended++;
                if (ended == 1)
                {
                    Check(player.OwnerId == "music" && player.Identity == "queue-1");
                    await player.OpenAsync("music", "queue-2", second.Uri, "generated WAV");
                }
                else done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
        };
        await player.OpenAsync("music", "queue-1", first.Uri, "generated WAV");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Check(started == 2 && ended == 2 && releasedAtEnd);
        Check(player.Identity == "queue-2" && player.LastErrorCode is null);
        first.AssertReleased();
        second.AssertReleased();
    }

    private static async Task FailureRecovery()
    {
        using var wave = new Wave(8);
        await using var player = New();
        int failed = 0, started = 0, ended = 0;
        player.Failed += (_, _) => failed++;
        player.Started += (_, _) => started++;
        player.Ended += (_, _) => ended++;
        var missing = new Uri(Path.Combine(wave.DirectoryPath, "missing-fixture-canary.wav"));
        try { await player.OpenAsync("owner", "missing", missing, "fixture"); }
        catch (PlaybackException error) { Check(error.Code == PlaybackError.OpenFailed); Check(error.Message == error.Code); }
        await Eventually(() => failed == 1);
        Check(!player.HasMedia && !player.IsPlaying && started == 0 && ended == 0);
        Check(player.LastErrorCode is PlaybackError.PlaybackFailed or PlaybackError.OpenFailed);
        Check(((VideoView)player.VideoView).MediaPlayer is null);
        await Start(player, wave.Uri);
        Check(player.LastErrorCode is null && started == 1);
        await player.StopAsync();
        await Task.Delay(300);
        Check(failed == 1 && ended == 0);
        wave.AssertReleased();
    }

    private static async Task RejectPreserves()
    {
        using var wave = new Wave(10);
        await using var player = New();
        await Start(player, wave.Uri, "music", "keep");
        var native = ((VideoView)player.VideoView).MediaPlayer;
        await ExpectCode(() => player.OpenAsync("video", "reject", new Uri("ftp://example.invalid/a"), ""), PlaybackError.UnsupportedUri);
        await ExpectCode(() => player.OpenAsync("video", "reject", new Uri("https://example.invalid/a"), "",
            new Dictionary<string, string> { ["Range"] = "fixture-canary" }), PlaybackError.UnsupportedHeaders);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectCancelled(() => player.OpenAsync("video", "reject", wave.Uri, "", cancelled.Token));
        Check(player.OwnerId == "music" && player.Identity == "keep" && player.IsPlaying);
        Check(ReferenceEquals(native, ((VideoView)player.VideoView).MediaPlayer));
    }

    private static async Task Cancellation()
    {
        using var wave = new Wave(10);
        await using var player = New();
        int started = 0, ended = 0, failed = 0;
        player.Started += (_, _) => started++;
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        using (var opening = new CancellationTokenSource())
        {
            var task = player.OpenAsync("owner", "opening", wave.Uri, "", opening.Token);
            opening.Cancel();
            await ExpectCancelled(() => task);
            await Eventually(() => !player.HasMedia);
            Check(started == 0);
        }
        using var active = new CancellationTokenSource();
        await Start(player, wave.Uri, token: active.Token);
        var native = ((VideoView)player.VideoView).MediaPlayer!;
        active.Cancel();
        await Eventually(() => !player.HasMedia && player.Identity is null);
        Check(!player.IsPlaying && native.NativeReference == IntPtr.Zero);
        Check(ended == 0 && failed == 0 && started == 1);
        wave.AssertReleased();
    }

    private static async Task Replacement()
    {
        using var first = new Wave(10);
        using var second = new Wave(10);
        using var oldToken = new CancellationTokenSource();
        await using var player = New();
        var identities = new List<string?>();
        int ended = 0, failed = 0;
        player.Started += (_, _) => identities.Add(player.Identity);
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        await Start(player, first.Uri, "music", "old", oldToken.Token);
        var oldNative = ((VideoView)player.VideoView).MediaPlayer!;
        await Start(player, second.Uri, "video", "new");
        var newNative = ((VideoView)player.VideoView).MediaPlayer!;
        oldToken.Cancel();
        await Task.Delay(400);
        Check(oldNative.NativeReference == IntPtr.Zero && !ReferenceEquals(oldNative, newNative));
        Check(newNative.NativeReference != IntPtr.Zero && player.IsPlaying && player.HasMedia);
        Check(player.OwnerId == "video" && player.Identity == "new" && player.PositionSeconds < 3);
        Check(identities.SequenceEqual(new[] { "old", "new" }) && ended == 0 && failed == 0);
        first.AssertReleased();
    }

    private static async Task QueuedOpen()
    {
        using var wave = new Wave(10);
        await using var player = New();
        var identities = new List<string?>();
        player.Started += (_, _) => identities.Add(player.Identity);
        var first = player.OpenAsync("music", "superseded", wave.Uri, "");
        var last = player.OpenAsync("video", "latest", wave.Uri, "");
        await ExpectCancelled(() => first);
        await last;
        await Eventually(() => player.IsPlaying);
        Check(player.OwnerId == "video" && player.Identity == "latest");
        Check(identities.SequenceEqual(new[] { "latest" }));
        var stop = player.StopAsync();
        var next = player.OpenAsync("music", "after-stop", wave.Uri, "");
        await Task.WhenAll(stop, next);
        await Eventually(() => player.Identity == "after-stop" && player.IsPlaying);
    }

    private static async Task CrossThread()
    {
        using var wave = new Wave(10);
        await using var player = New();
        var dispatcher = Dispatcher.CurrentDispatcher;
        bool correctThread = true;
        player.PropertyChanged += (_, _) => correctThread &= dispatcher.CheckAccess();
        player.Started += (_, _) => correctThread &= dispatcher.CheckAccess();
        await Task.Run(() => player.OpenAsync("owner", "cross-thread", wave.Uri, ""));
        await Eventually(() => player.IsPlaying && player.DurationSeconds > 0);
        await Task.Run(player.PauseAsync);
        await Eventually(() => !player.IsPlaying);
        await Task.Run(() => player.SeekAsync(2));
        await Task.Run(player.ResumeAsync);
        await Eventually(() => player.IsPlaying);
        await Task.Run(() => ExpectSyncCode(() => player.Volume = 45, PlaybackError.WrongThread));
        await Task.Run(player.StopAsync);
        Check(correctThread && !player.HasMedia);
        wave.AssertReleased();
    }

    private static async Task StopOpening()
    {
        using var wave = new Wave(10);
        await using var player = New();
        int events = 0;
        player.Started += (_, _) => events++;
        player.Ended += (_, _) => events++;
        player.Failed += (_, _) => events++;
        var opening = player.OpenAsync("music", "interrupted", wave.Uri, "");
        var stopped = player.StopAsync();
        await ExpectCancelled(() => opening);
        await stopped;
        Check(events == 0 && !player.HasMedia && player.Identity is null);
        Check(((VideoView)player.VideoView).MediaPlayer is null);
        wave.AssertReleased();
        await Start(player, wave.Uri);
        Check(events == 1);
    }

    private static async Task DisposeOpening()
    {
        using var wave = new Wave(10);
        var player = New();
        int events = 0;
        player.Started += (_, _) => events++;
        player.Ended += (_, _) => events++;
        player.Failed += (_, _) => events++;
        try
        {
            var opening = player.OpenAsync("music", "closing", wave.Uri, "");
            var disposed = player.DisposeAsync().AsTask();
            var disposedAgain = player.DisposeAsync().AsTask();
            await ExpectCancelled(() => opening);
            await Task.WhenAll(disposed, disposedAgain);
            await Task.Delay(250);
            Check(events == 0 && !player.HasMedia && player.Identity is null);
            Check(((VideoView)player.VideoView).MediaPlayer is null);
            wave.AssertReleased();
        }
        finally { await player.DisposeAsync(); }
    }

    private static async Task EmptyHeaders()
    {
        using var wave = new Wave(8);
        await using var player = New();
        int started = 0;
        player.Started += (_, _) => started++;
        await player.OpenAsync("owner", "empty-headers", wave.Uri, "",
            new Dictionary<string, string>());
        await Eventually(() => player.IsPlaying);
        Check(started == 1 && player.Identity == "empty-headers");
        await player.StopAsync();
        wave.AssertReleased();
    }

    private static async Task RepeatRelease()
    {
        using var wave = new Wave(8);
        for (int iteration = 0; iteration < 3; iteration++)
        {
            var player = New();
            int progress = 0;
            player.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(player.PositionSeconds)) progress++; };
            try
            {
                await Start(player, wave.Uri);
                var native = ((VideoView)player.VideoView).MediaPlayer!;
                await Eventually(() => player.PositionSeconds > 0);
                var dispose1 = player.DisposeAsync().AsTask();
                var dispose2 = player.DisposeAsync().AsTask();
                await Task.WhenAll(dispose1, dispose2);
                Check(native.NativeReference == IntPtr.Zero && !player.HasMedia);
                Check(((VideoView)player.VideoView).MediaPlayer is null);
                int after = progress;
                await Task.Delay(300);
                Check(progress == after);
                wave.AssertReleased();
            }
            finally { await player.DisposeAsync(); }
        }
    }

    private static Dictionary<string, string> HttpHeaders() => new()
    {
        ["Authorization"] = "Bearer fixture-secret-canary", ["Cookie"] = "session=fixture-cookie-canary",
        ["Referer"] = "https://origin.invalid/fixture", ["User-Agent"] = "meowfilm-fixture",
        ["Origin"] = "https://origin.invalid", ["X-Fixture"] = "fixture-custom-canary"
    };

    private static byte[] HttpWave(int seconds = 3)
    {
        using var wave = new Wave(seconds);
        return File.ReadAllBytes(wave.Uri.LocalPath); // Generated test data only, NOT the production transport.
    }

    private static async Task HttpPolicy()
    {
        byte[] wave = HttpWave();
        await using var server = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, wave, token));
        await using var second = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, wave, token));
        foreach (var uri in new[] { server.Uri("/wave"), new Uri($"http://localhost:{server.Port}/wave"), second.Uri("/wave") })
        {
            using var input = await HttpMediaInput.OpenAsync(uri, HttpHeaders());
            Check(input.CanSeek && ReadHttp(input, 71).SequenceEqual(wave.Take(71)));
        }
        ExpectSyncCode(() => new NativeHttpMediaOptions(readTimeout: TimeSpan.Zero), PlaybackError.InvalidRequest);
        Check(server.Requests.Count == 2 && second.Requests.Count == 1);
    }

    private static async Task HttpUserInfo()
    {
        byte[] bytes = HttpWave();
        foreach (var (userInfo, decoded) in new[]
        {
            ("fixture%20user:p%40ss%3Aword", "fixture user:p@ss:word"),
            ("fixture-user", "fixture-user:"),
            (":fixture-pass", ":fixture-pass")
        })
            foreach (bool explicitHeader in new[] { false, true })
                foreach (string redirect in new[] { "none", "relative", "absolute" })
                {
                    const string explicitValue = "Bearer fixture-explicit-only";
                    string basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded));
                    string other = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("redirect-user:redirect-pass"));
                    HttpFixture? fixture = null;
                    await using var server = new HttpFixture(async (request, stream, token) =>
                    {
                        Check(request.Headers.GetValueOrDefault("Authorization") ==
                            (explicitHeader ? explicitValue : request.Path == "/absolute-target" ? other : basic));
                        if (request.Path == "/relative")
                            await HttpReply(stream, 302, new() { ["Location"] = "/relative-target" }, ReadOnlyMemory<byte>.Empty, token);
                        else if (request.Path == "/absolute")
                            await HttpReply(stream, 302, new()
                            { ["Location"] = $"http://redirect-user:redirect-pass@127.0.0.1:{fixture!.Port}/absolute-target" },
                                ReadOnlyMemory<byte>.Empty, token);
                        else await HttpRangeReply(request, stream, bytes, token);
                    });
                    fixture = server;
                    var headers = new Dictionary<string, string>();
                    if (explicitHeader) headers["aUtHoRiZaTiOn"] = explicitValue;
                    using var input = await HttpMediaInput.OpenAsync(
                        new Uri($"http://{userInfo}@127.0.0.1:{server.Port}/{redirect}"), headers);
                    Check(ReadHttp(input, 71).SequenceEqual(bytes.Take(71)) && input.Seek(700) &&
                        ReadHttp(input, 40).SequenceEqual(bytes.Skip(700).Take(40)));
                    Check(server.Requests.Count == (redirect == "none" ? 2 : 3) &&
                        server.Requests.All(request => !request.Path.Contains('@')));
                }
    }

    private static async Task HttpLongUrl()
    {
        byte[] bytes = HttpWave();
        string path = "/wave?fixture=" + new string('x', 17000);
        await using var server = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, bytes, token));
        using var input = await HttpMediaInput.OpenAsync(server.Uri(path), new Dictionary<string, string>());
        Check(ReadHttp(input, 71).SequenceEqual(bytes.Take(71)) && server.Requests.Count == 1 &&
            server.Requests.Single().Path == path);
    }

    private static async Task HttpRedirectScope()
    {
        byte[] bytes = HttpWave();
        HttpFixture? first = null;
        await using var second = new HttpFixture(async (request, stream, token) =>
        {
            Check(HttpHeaders().All(pair => request.Headers[pair.Key] == pair.Value));
            await HttpReply(stream, 302, new() { ["Location"] = first!.Uri("/return.wav").AbsoluteUri }, ReadOnlyMemory<byte>.Empty, token);
        });
        await using var owner = new HttpFixture(async (request, stream, token) =>
        {
            if (request.Path == "/same")
            {
                Check(HttpHeaders().All(pair => request.Headers[pair.Key] == pair.Value));
                await HttpReply(stream, 302, new() { ["Location"] = "/media.wav",
                    ["Set-Cookie"] = "server=not-forwarded" }, ReadOnlyMemory<byte>.Empty, token);
            }
            else if (request.Path == "/cross")
                await HttpReply(stream, 302, new() { ["Location"] = second.Uri("/bounce").AbsoluteUri }, ReadOnlyMemory<byte>.Empty, token);
            else
            {
                Check(HttpHeaders().All(pair => request.Headers[pair.Key] == pair.Value));
                await HttpRangeReply(request, stream, bytes, token);
            }
        });
        first = owner;
        var options = new NativeHttpMediaOptions();
        using (var input = await HttpMediaInput.OpenAsync(owner.Uri("/same"), HttpHeaders(), options))
        {
            Check(input.CanSeek);
            Check(ReadHttp(input, 71).SequenceEqual(bytes.Take(71)));
            Check(input.Seek(700));
            Check(ReadHttp(input, 40).SequenceEqual(bytes.Skip(700).Take(40)));
        }
        using (var input = await HttpMediaInput.OpenAsync(owner.Uri("/cross"), HttpHeaders(), options))
        {
            Check(input.Seek(600));
            Check(ReadHttp(input, 20).SequenceEqual(bytes.Skip(600).Take(20)));
        }
        Check(owner.Requests.Count == 6 && second.Requests.Count == 1);

        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var snapshot = new HttpFixture(async (request, stream, token) =>
        {
            arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            Check(request.Headers["Cookie"] == "session=fixture-cookie-canary");
            await HttpRangeReply(request, stream, bytes, token);
        });
        var headers = HttpHeaders();
        var pending = HttpMediaInput.OpenAsync(snapshot.Uri("/media.wav"), headers, snapshot.Options());
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        headers["Cookie"] = "mutated-canary";
        headers.Clear();
        release.SetResult();
        using var copied = await pending;
        Check(copied.CanSeek);
    }

    private static async Task HttpSafeErrors()
    {
        foreach (var (status, code) in new[]
        {
            (401, PlaybackError.HttpUnauthorized), (403, PlaybackError.HttpUnauthorized),
            (404, PlaybackError.HttpNotFound), (410, PlaybackError.HttpExpired),
            (416, PlaybackError.HttpRangeRejected), (429, PlaybackError.HttpThrottled),
            (500, PlaybackError.HttpServerFailed), (204, PlaybackError.HttpStatusFailed)
        })
        {
            await using var server = new HttpFixture((_, stream, token) =>
                HttpReply(stream, status, new(), "fixture-body-secret-canary"u8.ToArray(), token));
            await ExpectCode(() => HttpMediaInput.OpenAsync(server.Uri("/private?token=fixture-url-canary"),
                HttpHeaders(), server.Options()), code);
        }
        await using var loop = new HttpFixture((request, stream, token) => HttpReply(stream, 302,
            new() { ["Location"] = request.Path == "/a" ? "/b" : "/a" }, ReadOnlyMemory<byte>.Empty, token));
        await ExpectCode(() => HttpMediaInput.OpenAsync(loop.Uri("/a"), HttpHeaders(), loop.Options()),
            PlaybackError.HttpRedirectFailed);
        Check(loop.Requests.Count == 2);
        await using var missing = new HttpFixture((_, stream, token) => HttpReply(stream, 302, new(), ReadOnlyMemory<byte>.Empty, token));
        await ExpectCode(() => HttpMediaInput.OpenAsync(missing.Uri("/a"), HttpHeaders(), missing.Options()),
            PlaybackError.HttpRedirectFailed);
        await using var many = new HttpFixture((request, stream, token) => HttpReply(stream, 307,
            new() { ["Location"] = request.Path + "x" }, ReadOnlyMemory<byte>.Empty, token));
        await ExpectCode(() => HttpMediaInput.OpenAsync(many.Uri("/a"), HttpHeaders(),
            new NativeHttpMediaOptions(maximumRedirects: 2)), PlaybackError.HttpRedirectFailed);
        Check(many.Requests.Count == 3);
        byte[] bytes = HttpWave();
        await using var target = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, bytes, token));
        await using var redirected = new HttpFixture((_, stream, token) => HttpReply(stream, 302,
            new() { ["Location"] = target.Uri("/media.wav").AbsoluteUri }, ReadOnlyMemory<byte>.Empty, token));
        using var input = await HttpMediaInput.OpenAsync(redirected.Uri("/a"), HttpHeaders());
        Check(ReadHttp(input, 71).SequenceEqual(bytes.Take(71)) && target.Requests.Count == 1);
    }

    private static async Task HttpUnsupportedContent()
    {
        byte[] wave = HttpWave();
        await using var server = new HttpFixture((request, stream, token) => request.Path switch
        {
            "/mime" => HttpReply(stream, 200, new() { ["Content-Type"] = "application/vnd.apple.mpegurl" }, wave, token),
            "/dash-mime" => HttpReply(stream, 200, new() { ["Content-Type"] = "application/dash+xml" }, wave, token),
            "/hls" => HttpReply(stream, 200, new() { ["Content-Type"] = "audio/wav" },
                Encoding.UTF8.GetPreamble().Concat("#EXTM3U\n#EXT-X-STREAM-INF:1\nhttp://private.invalid/leak"u8.ToArray()).ToArray(), token),
            "/dash" => HttpReply(stream, 200, new(), "<?xml version=\"1.0\"?><MPD><BaseURL>http://private.invalid</BaseURL></MPD>"u8.ToArray(), token),
            "/html" => HttpReply(stream, 200, new(), "<html>not a media file</html>"u8.ToArray(), token),
            _ => throw new InvalidOperationException("Unexpected unsupported-content fixture request")
        });
        foreach (string path in new[] { "/a.m3u8", "/a.MPD", "/a.%6d3u8" })
            await ExpectCode(() => HttpMediaInput.OpenAsync(server.Uri(path), HttpHeaders(), server.Options()),
                PlaybackError.UnsupportedAdaptive);
        Check(server.Requests.IsEmpty);
        foreach (string path in new[] { "/mime", "/dash-mime", "/hls", "/dash" })
            await ExpectCode(() => HttpMediaInput.OpenAsync(server.Uri(path), HttpHeaders(), server.Options()),
                PlaybackError.UnsupportedAdaptive);
        await ExpectCode(() => HttpMediaInput.OpenAsync(server.Uri("/html"), HttpHeaders(), server.Options()),
            PlaybackError.HttpFormatUnsupported);
        Check(server.Requests.Count == 5);
    }

    private static async Task HttpRanges()
    {
        byte[] bytes = HttpWave(12);
        await using var server = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, bytes, token));
        using var input = await HttpMediaInput.OpenAsync(server.Uri("/media"), HttpHeaders(), server.Options());
        Check(input.Open(out ulong size) && size == (ulong)bytes.Length && input.CanSeek);
        Check(ReadHttp(input, 128).SequenceEqual(bytes.Take(128)));
        Check(input.Seek(100000));
        Check(ReadHttp(input, 511).SequenceEqual(bytes.Skip(100000).Take(511)));
        Check(input.Seek(10));
        Check(ReadHttp(input, 123).SequenceEqual(bytes.Skip(10).Take(123)));
        Check(!input.Seek((ulong)bytes.Length + 1) && input.Position == 133);
        Check(input.Seek((ulong)bytes.Length));
        Check(ReadHttp(input, 20).Length == 0);
        Check(input.Seek(0));
        using var buffer = new NativeBuffer();
        int read = input.Read(buffer.Pointer, uint.MaxValue);
        Check(read > 0 && read <= HttpMediaInput.BufferCapacity);
        Check(server.Requests.Count == 4);
        Check(server.Requests.Skip(1).All(request => request.Headers["If-Range"] == "\"fixture-v1\""));
        Check(server.Requests.All(request => request.Headers["Accept-Encoding"] == "identity"));

        await using var capped = new HttpFixture((request, stream, token) =>
            HttpRangeReply(request, stream, bytes, token, cap: 4096));
        using var segments = await HttpMediaInput.OpenAsync(capped.Uri("/media"), HttpHeaders(), capped.Options());
        int position = 0;
        while (true)
        {
            byte[] block = ReadHttp(segments, 65536);
            if (block.Length == 0) break;
            Check(block.SequenceEqual(bytes.Skip(position).Take(block.Length)));
            position += block.Length;
        }
        Check(position == bytes.Length && capped.Requests.Count > 2 && segments.LastErrorCode is null);
    }

    private static async Task HttpSequential()
    {
        byte[] bytes = HttpWave();
        foreach (string mode in new[] { "ignored", "weak", "absent", "unknown" })
        {
            await using var server = new HttpFixture(async (request, stream, token) =>
            {
                if (mode == "unknown")
                {
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
                    await stream.WriteAsync(bytes, token);
                }
                else if (mode == "ignored")
                    await HttpReply(stream, 200, new() { ["Accept-Ranges"] = "bytes", ["ETag"] = "\"fixture-v1\"" }, bytes, token);
                else
                    await HttpRangeReply(request, stream, bytes, token, tag: mode == "weak" ? "W/\"fixture-v1\"" : null);
            });
            using var input = await HttpMediaInput.OpenAsync(server.Uri("/a"), HttpHeaders(), server.Options());
            Check(!input.CanSeek && !input.Seek(900) && input.Position == 0);
            Check(input.Open(out ulong size) && (mode == "unknown" ? size == ulong.MaxValue : size == (ulong)bytes.Length));
            int position = 0;
            while (true)
            {
                var block = ReadHttp(input, 8192);
                if (block.Length == 0) break;
                Check(block.SequenceEqual(bytes.Skip(position).Take(block.Length)));
                position += block.Length;
            }
            Check(position == bytes.Length && input.LastErrorCode is null && server.Requests.Count == 1);
        }
    }

    private static async Task HttpInvalidRanges()
    {
        byte[] bytes = HttpWave();
        foreach (var (range, contentLength) in new[]
        {
            ($"bytes 1-{bytes.Length - 1}/{bytes.Length}", bytes.Length),
            ($"bytes 0-{bytes.Length}/{bytes.Length}", bytes.Length),
            ($"bytes 0-{bytes.Length - 1}/*", bytes.Length),
            ("items 0-4/9", 5), ("bytes 0-9/10", 11)
        })
        {
            await using var server = new HttpFixture((_, stream, token) => HttpReply(stream, 206,
                new() { ["Content-Range"] = range, ["Content-Length"] = contentLength.ToString(), ["ETag"] = "\"fixture-v1\"" },
                bytes, token));
            await ExpectCode(() => HttpMediaInput.OpenAsync(server.Uri("/media"), HttpHeaders(), server.Options()),
                PlaybackError.HttpInvalidRange);
        }
        await using var encoded = new HttpFixture((_, stream, token) => HttpReply(stream, 200,
            new() { ["Content-Encoding"] = "gzip" }, bytes, token));
        await ExpectCode(() => HttpMediaInput.OpenAsync(encoded.Uri("/media"), HttpHeaders(), encoded.Options()),
            PlaybackError.HttpEncodingUnsupported);
        await using var multipart = new HttpFixture((_, stream, token) => HttpReply(stream, 206,
            new() { ["Content-Type"] = "multipart/byteranges; boundary=fixture" }, bytes, token));
        await ExpectCode(() => HttpMediaInput.OpenAsync(multipart.Uri("/media"), HttpHeaders(), multipart.Options()),
            PlaybackError.HttpInvalidRange);
    }

    private static async Task HttpChangedRepresentation()
    {
        byte[] bytes = HttpWave();
        foreach (string mode in new[] { "ignored", "tag", "missing-tag", "length", "offset", "redirect", "416" })
        {
            await using var server = new HttpFixture(async (request, stream, token) =>
            {
                if (!request.Headers.ContainsKey("If-Range"))
                    await HttpRangeReply(request, stream, bytes, token);
                else if (mode == "ignored")
                    await HttpReply(stream, 200, new(), bytes, token);
                else if (mode == "redirect")
                    await HttpReply(stream, 302, new() { ["Location"] = "/new-object" }, ReadOnlyMemory<byte>.Empty, token);
                else if (mode == "416")
                    await HttpReply(stream, 416, new() { ["Content-Range"] = $"bytes */{bytes.Length}" }, ReadOnlyMemory<byte>.Empty, token);
                else if (mode == "offset")
                    await HttpRangeReply(request with { Headers = new(request.Headers) { ["Range"] = "bytes=0-" } },
                        stream, bytes, token);
                else
                    await HttpRangeReply(request, stream, mode == "length" ? bytes[..^1] : bytes, token,
                        tag: mode == "tag" ? "\"fixture-v2\"" : mode == "missing-tag" ? null : "\"fixture-v1\"");
            });
            using var input = await HttpMediaInput.OpenAsync(server.Uri("/a"), HttpHeaders(), server.Options());
            Check(!input.Seek(1024));
            Check(input.LastErrorCode == (mode == "416" ? PlaybackError.HttpRangeRejected :
                mode == "offset" ? PlaybackError.HttpInvalidRange : PlaybackError.HttpRepresentationChanged));
            using var buffer = new NativeBuffer();
            Check(input.Read(buffer.Pointer, 2) == -1 && input.Position == 0 && server.Requests.Count == 2);
        }
    }

    private static async Task HttpTruncation()
    {
        byte[] bytes = HttpWave();
        await using var server = new HttpFixture((_, stream, token) => HttpReply(stream, 200,
            new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..1024], token));
        using var input = await HttpMediaInput.OpenAsync(server.Uri("/a"), HttpHeaders(), server.Options());
        using var buffer = new NativeBuffer();
        int read, total = 0;
        while ((read = input.Read(buffer.Pointer, 1000)) > 0) total += read;
        Check(total == 1024 && read == -1 && input.LastErrorCode == PlaybackError.HttpTruncated);
    }

    private static async Task HttpDeadlines()
    {
        byte[] bytes = HttpWave();
        await using (var headers = new HttpFixture((_, _, token) => Task.Delay(Timeout.Infinite, token)))
        {
            await ExpectCode(() => HttpMediaInput.OpenAsync(headers.Uri("/a"), HttpHeaders(),
                headers.Options(TimeSpan.FromMilliseconds(200))), PlaybackError.HttpDeadline);
            using var cancelled = new CancellationTokenSource();
            var opening = HttpMediaInput.OpenAsync(headers.Uri("/a"), HttpHeaders(), headers.Options(), cancelled.Token);
            await Eventually(() => headers.Requests.Count == 2);
            cancelled.Cancel();
            await ExpectCancelled(() => opening.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        await using (var probe = new HttpFixture(async (_, stream, token) =>
        {
            await HttpReply(stream, 200, new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..4], token);
            await Task.Delay(Timeout.Infinite, token);
        }))
            await ExpectCode(() => HttpMediaInput.OpenAsync(probe.Uri("/a"), HttpHeaders(),
                probe.Options(TimeSpan.FromMilliseconds(200))), PlaybackError.HttpDeadline);

        foreach (bool cancel in new[] { false, true })
        {
            await using var body = new HttpFixture(async (_, stream, token) =>
            {
                await HttpReply(stream, 200, new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..512], token);
                await Task.Delay(Timeout.Infinite, token);
            });
            using var input = await HttpMediaInput.OpenAsync(body.Uri("/a"), HttpHeaders(),
                body.Options(readTimeout: cancel ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(200)));
            Check(ReadHttp(input, 512).Length == 512);
            using var buffer = new NativeBuffer();
            var read = Task.Run(() => input.Read(buffer.Pointer, 4096));
            if (cancel) { await Task.Delay(100); input.Cancel(); }
            Check(await read.WaitAsync(TimeSpan.FromSeconds(2)) == -1);
            Check(input.LastErrorCode == (cancel ? null : PlaybackError.HttpDeadline));
        }
        await using var seekServer = new HttpFixture(async (request, stream, token) =>
        {
            if (request.Headers.ContainsKey("If-Range")) await Task.Delay(Timeout.Infinite, token);
            else await HttpRangeReply(request, stream, bytes, token);
        });
        using var seek = await HttpMediaInput.OpenAsync(seekServer.Uri("/a"), HttpHeaders(),
            seekServer.Options(TimeSpan.FromMilliseconds(300)));
        Check(!await Task.Run(() => seek.Seek(2048)).WaitAsync(TimeSpan.FromSeconds(2)));
        Check(seek.LastErrorCode == PlaybackError.HttpDeadline);
    }

    private static async Task HttpNativePlayback()
    {
        // Exceed VLC's own small-resource read cache so this asserts a real HTTP seek,
        // not merely movement inside a native cache. Still entirely generated test PCM.
        byte[] bytes = HttpWave(300);
        await using var server = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, bytes, token));
        await using var player = new NativePlayback(Dispatcher.CurrentDispatcher, NativePath(), NativeAudioOutput.Dummy, server.Options());
        int ended = 0, failed = 0;
        player.Ended += (_, _) => ended++;
        player.Failed += (_, _) => failed++;
        await player.OpenAsync("music", "native-http", server.Uri("/media.wav"), "generated HTTP WAV", HttpHeaders());
        await Eventually(() => player.IsPlaying && player.DurationSeconds > 299);
        var native = ((VideoView)player.VideoView).MediaPlayer!;
        await player.PauseAsync();
        await Eventually(() => !player.IsPlaying);
        await player.SeekAsync(270);
        await player.ResumeAsync();
        await Eventually(() => player.PositionSeconds >= 269);
        // TimeChanged can precede completion of native input seeking; require wire evidence too.
        await Eventually(() => server.Requests.Any(request => request.Headers.ContainsKey("If-Range")));
        await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(native.NativeReference == IntPtr.Zero && !player.HasMedia && ended == 0 && failed == 0);
        Check(server.Requests.All(request => HttpHeaders().All(pair => request.Headers[pair.Key] == pair.Value)));
    }

    private static async Task HttpNativeRetirement()
    {
        byte[] bytes = HttpWave(60);
        using var replacement = new Wave(4);
        foreach (string mode in new[] { "stop", "cancel", "dispose", "replace" })
        {
            await using var server = new HttpFixture(async (_, stream, token) =>
            {
                await HttpReply(stream, 200, new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..4096], token);
                await Task.Delay(Timeout.Infinite, token);
            });
            var player = new NativePlayback(Dispatcher.CurrentDispatcher, NativePath(), NativeAudioOutput.Dummy,
                server.Options(readTimeout: TimeSpan.FromSeconds(20)));
            using var cancellation = new CancellationTokenSource();
            int ended = 0, failed = 0;
            player.Ended += (_, _) => ended++;
            player.Failed += (_, _) => failed++;
            try
            {
                await player.OpenAsync("music", "blocked", server.Uri("/a"), "", HttpHeaders(), cancellation.Token);
                var native = ((VideoView)player.VideoView).MediaPlayer!;
                await Task.Delay(150); // Native callback is waiting for the remaining declared body.
                Check(player.HasMedia);
                if (mode == "cancel")
                {
                    cancellation.Cancel();
                    await Eventually(() => !player.HasMedia);
                }
                else if (mode == "dispose") await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                else if (mode == "replace") await Start(player, replacement.Uri, identity: "replacement");
                else await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Check(native.NativeReference == IntPtr.Zero && ended == 0 && failed == 0);
                if (mode == "replace")
                {
                    cancellation.Cancel();
                    await Task.Delay(100);
                    Check(player.Identity == "replacement" && player.IsPlaying);
                }
            }
            finally { await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        }
    }

    private static async Task HttpNativePreflight()
    {
        await using var server = new HttpFixture((_, _, token) => Task.Delay(Timeout.Infinite, token));
        using var wave = new Wave(4);
        await using var player = new NativePlayback(Dispatcher.CurrentDispatcher, NativePath(), NativeAudioOutput.Dummy, server.Options());
        var opening = player.OpenAsync("music", "pending", server.Uri("/a"), "", HttpHeaders());
        await Eventually(() => server.Requests.Count == 1);
        await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await ExpectCancelled(() => opening);
        Check(!player.HasMedia);
        var replaced = player.OpenAsync("music", "pending-again", server.Uri("/a"), "", HttpHeaders());
        await Eventually(() => server.Requests.Count == 2);
        await Start(player, wave.Uri, identity: "latest");
        await ExpectCancelled(() => replaced);
        Check(player.Identity == "latest" && player.IsPlaying && player.LastErrorCode is null);
    }

    private static async Task HttpNativeFailure()
    {
        byte[] bytes = HttpWave(30);
        await using var server = new HttpFixture(async (_, stream, token) =>
        {
            await HttpReply(stream, 200, new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..4096], token);
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var player = new NativePlayback(Dispatcher.CurrentDispatcher, NativePath(), NativeAudioOutput.Dummy,
            server.Options(readTimeout: TimeSpan.FromMilliseconds(300)));
        int failed = 0, ended = 0;
        player.Failed += (_, _) => failed++;
        player.Ended += (_, _) => ended++;
        await player.OpenAsync("music", "timeout", server.Uri("/a"), "", HttpHeaders());
        await Eventually(() => failed == 1);
        Check(ended == 0 && player.LastErrorCode == PlaybackError.HttpDeadline && !player.HasMedia);
        Check(((VideoView)player.VideoView).MediaPlayer is null);
    }

    private static async Task HttpNativeDirect()
    {
        byte[] bytes = HttpWave(5);
        await using var server = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, bytes, token));
        await using var player = New(); // No test HTTP options: existing no-header direct-location path.
        await Start(player, server.Uri("/media.wav"));
        Check(player.IsPlaying && server.Requests.Count > 0);
        Check(server.Requests.All(request => !request.Headers.ContainsKey("Authorization")));
        await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task HttpNativeUserInfo()
    {
        byte[] bytes = HttpWave(8);
        string basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture user:p@ss:word"));
        await using var server = new HttpFixture(async (request, stream, token) =>
        {
            Check(request.Headers.GetValueOrDefault("Authorization") == basic);
            await HttpRangeReply(request, stream, bytes, token);
        });
        await using var player = New();
        await Start(player, new Uri($"http://fixture%20user:p%40ss%3Aword@127.0.0.1:{server.Port}/media.wav"));
        await AdaptiveMediaTests.Decoded(player);
        Check(player.IsPlaying && server.Requests.Count > 0 &&
            server.Requests.All(request => request.Headers.GetValueOrDefault("Authorization") == basic) &&
            AdaptiveMediaTests.Field(AdaptiveMediaTests.Session(player)!, "Input") is not null);
        await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(!player.HasMedia && player.LastErrorCode is null);
    }

    private static byte[] ReadHttp(HttpMediaInput input, uint count)
    {
        using var buffer = new NativeBuffer();
        int read = input.Read(buffer.Pointer, count);
        Check(read >= 0 && read <= HttpMediaInput.BufferCapacity);
        var result = new byte[read];
        Marshal.Copy(buffer.Pointer, result, 0, read);
        return result;
    }

    private sealed class NativeBuffer : IDisposable
    {
        internal IntPtr Pointer { get; } = Marshal.AllocHGlobal(HttpMediaInput.BufferCapacity);
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    private sealed record HttpRequest(string Path, Dictionary<string, string> Headers);

    private sealed class HttpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<HttpRequest, NetworkStream, CancellationToken, Task> _handler;
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly ConcurrentQueue<Exception> _failures = [];
        private readonly Task _accept;
        internal ConcurrentQueue<HttpRequest> Requests { get; } = [];
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal Uri Origin => new($"http://127.0.0.1:{Port}/");
        internal Uri Uri(string path) => new(Origin, path);
        internal NativeHttpMediaOptions Options(TimeSpan? requestTimeout = null, TimeSpan? readTimeout = null) =>
            new NativeHttpMediaOptions(requestTimeout, readTimeout);

        internal HttpFixture(Func<HttpRequest, NetworkStream, CancellationToken, Task> handler)
        {
            _handler = handler;
            _listener.Start();
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    string? first = await reader.ReadLineAsync(_stop.Token);
                    if (first is null) return;
                    var parts = first.Split(' ');
                    Check(parts.Length == 3 && parts[0] == "GET");
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    while (true)
                    {
                        string? line = await reader.ReadLineAsync(_stop.Token);
                        if (string.IsNullOrEmpty(line)) break;
                        int colon = line.IndexOf(':');
                        Check(colon > 0 && line.Length < 20000 && headers.Count < 50);
                        headers.Add(line[..colon], line[(colon + 1)..].Trim());
                    }
                    var request = new HttpRequest(parts[1], headers);
                    Requests.Enqueue(request);
                    await _handler(request, stream, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) { /* Client deliberately closes on seek/rejection/retirement. */ }
                catch (Exception error) { _failures.Enqueue(error); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accept.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(3));
            _stop.Dispose();
            if (_failures.TryDequeue(out var error)) throw error;
        }
    }

    private static async Task HttpRangeReply(HttpRequest request, NetworkStream stream, byte[] bytes,
        CancellationToken token, string? tag = "\"fixture-v1\"", int? cap = null)
    {
        int offset = request.Headers.TryGetValue("Range", out var range)
            ? int.Parse(range["bytes=".Length..].TrimEnd('-')) : 0;
        int count = Math.Min(bytes.Length - offset, cap ?? bytes.Length);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "audio/wav", ["Accept-Ranges"] = "bytes",
            ["Content-Range"] = $"bytes {offset}-{offset + count - 1}/{bytes.Length}"
        };
        if (tag is not null) headers["ETag"] = tag;
        await HttpReply(stream, 206, headers, bytes.AsMemory(offset, count), token);
    }

    private static async Task HttpReply(NetworkStream stream, int status, Dictionary<string, string> headers,
        ReadOnlyMemory<byte> body, CancellationToken token)
    {
        headers.TryAdd("Content-Length", body.Length.ToString());
        headers["Connection"] = "close";
        var text = $"HTTP/1.1 {status} Fixture\r\n" +
            string.Join("", headers.Select(pair => $"{pair.Key}: {pair.Value}\r\n")) + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), token);
        if (!body.IsEmpty) await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    private static async Task Start(NativePlayback player, Uri uri, string owner = "owner",
        string identity = "item", CancellationToken token = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onStarted = (_, _) => completion.TrySetResult();
        EventHandler onFailed = (_, _) => completion.TrySetException(new InvalidOperationException("Native fixture failed to start"));
        player.Started += onStarted;
        player.Failed += onFailed;
        try
        {
            await player.OpenAsync(owner, identity, uri, "generated WAV", token);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            player.Started -= onStarted;
            player.Failed -= onFailed;
        }
    }

    private static async Task Eventually(Func<bool> predicate,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException($"Native fixture condition timed out at line {line}");
            await Task.Delay(25); // Bounded test observation only, never a production polling service.
        }
    }

    private static async Task ExpectCode(Func<Task> action, string code)
    {
        try { await action(); }
        catch (PlaybackException error)
        {
            Check(error.Code == code && error.Message == code && error.InnerException is null);
            return;
        }
        throw new InvalidOperationException("Expected fixed media error");
    }

    private static void ExpectSyncCode(Action action, string code)
    {
        try { action(); }
        catch (PlaybackException error)
        {
            Check(error.Code == code && error.Message == code && error.InnerException is null);
            return;
        }
        throw new InvalidOperationException("Expected fixed media error");
    }

    private static async Task ExpectCancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected session cancellation");
    }

    private static void Check(bool value, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!value) throw new InvalidOperationException($"Media fixture assertion at line {line}");
    }

    private static async Task Sta(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? failure = null;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }) { IsBackground = true, Name = "MeowFilm.Media native fixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally
        {
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Native fixture STA did not cleanly stop");
        }
    }

    private sealed class Wave : IDisposable
    {
        internal string DirectoryPath { get; } =
            Path.Combine(AppContext.BaseDirectory, "media-fixtures", Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(DirectoryPath, "generated.wav");
        internal Uri Uri => new(FilePath);

        internal Wave(double seconds)
        {
            Directory.CreateDirectory(DirectoryPath);
            const int rate = 16000;
            int samples = (int)(rate * seconds);
            using var stream = File.Create(FilePath);
            using var writer = new BinaryWriter(stream, Encoding.ASCII);
            writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVE"u8);
            writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(samples * 2);
            for (int sample = 0; sample < samples; sample++)
                writer.Write((short)(Math.Sin(2 * Math.PI * 440 * sample / rate) * 1024));
        }

        internal void AssertReleased()
        {
            // Requires all native file handles to be closed, not merely Stop() having been requested.
            using var exclusive = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Check(exclusive.Length > 44);
        }

        public void Dispose()
        {
            File.Delete(FilePath); // Only this fixture's exact generated file; never enumerate user media.
            Directory.Delete(DirectoryPath, recursive: false);
        }
    }
}

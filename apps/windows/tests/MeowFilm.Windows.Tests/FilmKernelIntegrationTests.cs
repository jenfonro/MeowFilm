using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.NativeMediaFixtures;
using static MeowFilm.Windows.Tests.FFmpegVideoFixtures;

namespace MeowFilm.Windows.Tests;

// Real DLL, controlled AVIO, decoder and D3D11 presentation. Run in the logged-in
// interactive desktop; these cases do not assert HDR/DV display certification.
public static class FilmKernelIntegrationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("film kernel decoded picture is actually visible on the desktop", () => WithPlayer(async (player, _) =>
        {
            await using var origin = Fixture("progressive");
            await using var lease = new Lease(origin.Uri("/root"), origin.Headers);
            var intent = Intent(paused: true);
            await player.PrepareAsync(intent, lease.Address, Input("progressive"), lease);
            Check(await player.TryCommitAsync(intent, () => true, () => { }), "visible picture commit");
            uint pixel = 0;
            await Until(() =>
            {
                var point = player.VideoView.PointToScreen(new Point(
                    player.VideoView.ActualWidth / 2, player.VideoView.ActualHeight / 2));
                IntPtr screen = GetDC(IntPtr.Zero);
                try { pixel = GetPixel(screen, (int)point.X, (int)point.Y); }
                finally { ReleaseDC(IntPtr.Zero, screen); }
                int red = (int)(pixel & 255), green = (int)((pixel >> 8) & 255), blue = (int)((pixel >> 16) & 255);
                // The first synthetic JPEG is RGB(32,50,180). Allow color
                // conversion/desktop quantization, but never an empty HWND.
                return pixel != uint.MaxValue && blue > green + 40 && blue > red + 40 && green < 150;
            }, "decoded blue picture reaches actual desktop pixels");
            Console.WriteLine($"OBSERVED_FILM_DESKTOP_PIXEL colorref=0x{pixel:X6}; source=synthetic-blue-jpeg");
            await NativeDesktopCapture.SaveAsync(player.VideoView, "decoded-blue-picture");
        }));
        yield return Case("film kernel forward-only paused subtitle disable repaints actual desktop pixels", () => WithPlayer(async (player, kernel) =>
        {
            byte[] movie = SubtitleMatroska();
            await using var origin = new Origin
            {
                Route = _ => new Reply(movie, "video/x-matroska") { IgnoreRange = true }
            };
            await using var lease = new Lease(origin.Uri("/forward-only.mkv"), origin.Headers);
            var intent = Intent(paused: true);
            await player.PrepareAsync(intent, lease.Address, Input("progressive"), lease);
            Check(kernel.Read(kernel.Owner).Seekable == 0, "production AVIO identifies HTTP 200 without Range as nonseekable");
            Check(await player.TryCommitAsync(intent, () => true, () => { }), "forward-only Matroska commit");
            uint[] baseline = [];
            await Until(() =>
            {
                baseline = SubtitleDesktopPixels(player);
                return baseline.Count(BluePixel) > baseline.Length * .8;
            }, "constant blue Matroska picture is actually visible");
            var tracks = await player.ReadTracksAsync() ?? throw new InvalidOperationException("Missing Matroska tracks");
            Check(tracks.Subtitles.Count == 1 && tracks.SubtitleIndex is null,
                "one real UTF-8 subtitle stream starts disabled");
            await player.SelectSubtitleTrackAsync(tracks.Scope, tracks.Subtitles[0].Index);
            await player.SetPausedAsync(tracks.Scope, false);
            uint[] subtitled = [];
            int[] white = [];
            bool VisibleSubtitle()
            {
                subtitled = SubtitleDesktopPixels(player);
                white = Enumerable.Range(0, baseline.Length)
                    .Where(i => BluePixel(baseline[i]) && WhitePixel(subtitled[i])).ToArray();
                return white.Length >= 12;
            }
            await Until(VisibleSubtitle, "real white subtitle pixels appear over the blue movie");
            await player.SetPausedAsync(tracks.Scope, true);
            await Until(VisibleSubtitle, "visible subtitle remains on the actual paused picture");
            var before = kernel.Read(kernel.Owner);
            int seeks = kernel.AvioSeeks, requests = origin.Requests.Count;
            Check(before.State == 3 && before.Seekable == 0 && before.SubtitleTrack == tracks.Subtitles[0].Index,
                "pixel observation belongs to paused nonseekable output with subtitles selected");
            await player.SelectSubtitleTrackAsync(tracks.Scope, null);
            var after = kernel.Read(kernel.Owner);
            Check(after.SubmittedPictures > before.SubmittedPictures && after.State == before.State &&
                Math.Abs(after.Position - before.Position) < .000001 && after.SubtitleTrack == -1 && after.Seekable == 0,
                "subtitle disable synchronously submits a replacement without advancing or unpausing");
            uint[] cleared = [];
            await Until(() =>
            {
                cleared = SubtitleDesktopPixels(player);
                return white.All(i => BluePixel(cleared[i]) && SamePixel(cleared[i], baseline[i])) &&
                    !cleared.Any(WhitePixel);
            }, "all observed white subtitle pixels disappear from the real desktop without resuming or remounting");
            int[] background = Enumerable.Range(0, baseline.Length)
                .Where(i => BluePixel(baseline[i]) && SamePixel(subtitled[i], baseline[i])).ToArray();
            Check(background.Length > baseline.Length * .7 &&
                background.Count(i => SamePixel(cleared[i], baseline[i])) > background.Length * .99,
                "the unchanged blue retained picture remains behind the removed subtitle");
            var final = kernel.Read(kernel.Owner);
            Check(final.State == 3 && Math.Abs(final.Position - before.Position) < .000001 &&
                kernel.AvioSeeks == seeks && origin.Requests.Count == requests &&
                origin.Requests.All(request => request.Authorized),
                "pixel replacement neither seeks AVIO nor reopens HTTP nor advances the paused clock");
            Console.WriteLine($"OBSERVED_FILM_NONSEEK_SUBTITLE_PIXELS white={white.Length}; " +
                $"presentBefore={before.SubmittedPictures}; presentAfter={after.SubmittedPictures}; " +
                $"seekable={after.Seekable}; avioSeekDelta={kernel.AvioSeeks - seeks}; state={final.State}");
        }));
        foreach (string format in new[] { "progressive", "hls", "hls-aes", "dash", "dash-cenc" })
            yield return Case("film kernel real " + format + " prepare, paused seek and playback", () => WithPlayer(async (player, kernel) =>
            {
                await using var origin = Fixture(format);
                var headers = origin.Headers;
                origin.RequiredHeaders = new Dictionary<string, string>(headers);
                await using var lease = new Lease(origin.Uri("/root"), headers);
                var intent = Intent(paused: true);
                await player.PrepareAsync(intent, lease.Address, Input(format), lease);
                var prepared = kernel.Read(kernel.Owner);
                Check(prepared.State == 1 && prepared.Width == 160 && prepared.Height == 90 &&
                    !player.HasOutput && lease.Disposals == 0, "silent prepare requires a decoded picture");
                bool published = false;
                Check(await player.TryCommitAsync(intent, () => true, () => published = true) &&
                    published && player.Scope is not null && player.IsPaused, "native commit respects paused intent");
                Check(kernel.Read(kernel.Owner).State == 3, "first native presentation is paused");
                Check(player.Output is { SubmittedPictures: > 0 } output && output.Scope == player.Scope,
                    "the committed identity has an actual native render/Present submission");
                var tracks = await player.ReadTracksAsync() ?? throw new InvalidOperationException("Missing native tracks");
                Check(tracks.Scope == player.Scope && tracks.SubtitleIndex is null &&
                    tracks.Audio.Count == (format.StartsWith("dash", StringComparison.Ordinal) ? 1 : 0),
                    "real stream indices cross the native track ABI");
                if (tracks.AudioIndex is { } audioIndex)
                    await player.SelectAudioTrackAsync(tracks.Scope, audioIndex);
                await player.SelectSubtitleTrackAsync(tracks.Scope, null);
                // Mutating the caller dictionary must not change this media's request snapshot.
                headers["Cookie"] = "synthetic=changed-after-prepare";
                var scope = player.Scope!;
                await player.SeekAsync(scope, 8);
                var sought = kernel.Read(kernel.Owner);
                Check(sought.State == 3 && Math.Abs(sought.Position - 8) < .15,
                    "paused seek completes only after decoding/presenting the target");
                await player.RateAsync(scope, 1); // Must not interrupt the next audio write.
                await player.RateAsync(scope, 2);
                await player.AspectAsync(scope, "16:9");
                await player.SetPausedAsync(scope, false);
                await Until(() => player.PositionSeconds > 8.2 || player.LastErrorCode is not null, "playing clock");
                Check(player.LastErrorCode is null, "native playback stays healthy after commands");
                await player.SetPausedAsync(scope, true);
                Check(origin.Requests.Count > 0 && origin.Requests.All(request => request.Authorized),
                    "all real root/child/seek requests retain the original full header snapshot");
                if (format.StartsWith("hls", StringComparison.Ordinal))
                    Check(origin.Requests.Any(request => request.Path == "/variant") &&
                        origin.Requests.Any(request => request.Path.StartsWith("/video-", StringComparison.Ordinal)),
                        "real HLS demux opens manifest, initialization and fragments");
                if (format == "hls-aes")
                    Check(origin.Requests.Any(request => request.Path == "/key"), "real encrypted HLS opens its protected key");
                if (format.StartsWith("dash", StringComparison.Ordinal))
                    Check(origin.Requests.Any(request => request.Path.Contains("audio-", StringComparison.Ordinal)),
                        "the independent movie audio stream is actually read");
                await player.StopAsync(scope);
                Check(!player.HasOutput && player.Scope is null && lease.Disposals == 1 && kernel.Releases == 1,
                    "stop joins real workers and releases the exact lease once");
            }));
        yield return Case("film kernel wrong ClearKey rejects candidate and preserves paused donor", () => WithPlayer(async (player, kernel) =>
        {
            await using var origin = Fixture("dash-cenc");
            var headers = origin.Headers;
            origin.RequiredHeaders = headers;
            await using var donorLease = new Lease(origin.Uri("/root"), headers);
            var donorIntent = Intent(paused: true);
            await player.PrepareAsync(donorIntent, donorLease.Address, Input("dash-cenc"), donorLease);
            Check(await player.TryCommitAsync(donorIntent, () => true, () => { }), "correct-key control commits a real encrypted movie");
            var donorScope = player.Scope!;
            IntPtr donor = kernel.Owner;
            await player.SeekAsync(donorScope, 6);
            var before = kernel.Read(donor);
            Check(before.State == 3 && before.Error == 0 && before.SubmittedPictures > 0 &&
                Math.Abs(before.Position - 6) < .15, "correct-key control decodes and seeks while paused");
            int priorRequests = origin.Requests.Count, commits = 0, publications = 0, failures = 0, ends = 0;
            var candidate = new RuntimeMediaPlaybackIntent("film", Guid.NewGuid().ToString("N"), 0, true, donorScope);
            player.PropertyChanged += (_, _) =>
            {
                if (player.Scope?.Identity == candidate.Identity || player.Output?.Scope.Identity == candidate.Identity)
                    publications++;
            };
            player.Failed += (_, _) => failures++;
            player.Ended += (_, _) => ends++;
            const string wrongKey = "00000000000000000000000000000000";
            using var input = new NativeVideoInput(NativeVideoFormat.Dash, NativeClearKeySet.FromHex(
                new Dictionary<string, string> { [VideoKid] = wrongKey, [AudioKid] = AudioKey }));
            await using var rejectedLease = new Lease(origin.Uri("/root"), headers);
            try
            {
                await player.PrepareAsync(candidate, rejectedLease.Address, input, rejectedLease);
                throw new InvalidOperationException("Wrong-key candidate unexpectedly prepared a picture");
            }
            catch (PlaybackException error)
            {
                Check(error.Code == "FILM_DECODE_FAILED" && error.Message == error.Code &&
                    error.InnerException is null && error.Data.Count == 0,
                    "wrong-key failure must be the fixed decoder error, not open, network, cancellation or timeout");
                string diagnostic = error.ToString();
                Check(new[] { wrongKey, VideoKey, AudioKey, VideoKid, AudioKid, origin.Base.AbsoluteUri,
                    headers["Authorization"], headers["Cookie"] }.All(secret =>
                        !diagnostic.Contains(secret, StringComparison.OrdinalIgnoreCase)),
                    "public decoder diagnostics contain no URL, headers, KIDs or key material");
            }
            IntPtr rejected = kernel.Owner;
            Check(rejected != donor && kernel.PrepareResult == 6 &&
                kernel.Prepared is { State: 6, Error: 6, SubmittedPictures: 0 },
                "real native wrong-key decode fails before any candidate presentation");
            var requests = origin.Requests.Skip(priorRequests).ToArray();
            Check(requests.Any(request => request.Path == "/root") &&
                requests.Any(request => request.Path == "/parts/video-init") &&
                requests.Any(request => request.Path == "/parts/video-0") &&
                requests.All(request => request.Authorized),
                "candidate reads the same valid protected manifest, initialization and ciphertext as the control");
            Check(kernel.ReleaseCounts.GetValueOrDefault(rejected) == 1 &&
                kernel.ReleaseCounts.GetValueOrDefault(donor) == 0 && kernel.Releases == 1 &&
                rejectedLease.DisposeCalls == 1 && rejectedLease.Disposals == 1 && donorLease.DisposeCalls == 0,
                "prepare failure waits for one actual candidate native and receipt drain without retiring its donor");
            try
            {
                await player.TryCommitAsync(candidate, () => true, () => commits++);
                throw new InvalidOperationException("Retired wrong-key candidate remained eligible to commit");
            }
            catch (PlaybackException error) when (error.Code == PlaybackError.Superseded &&
                error.Message == error.Code && error.InnerException is null) { }
            await player.DiscardAsync(candidate);
            var after = kernel.Read(donor);
            Check(commits == 0 && publications == 0 && kernel.Starts == 1 && failures == 0 && ends == 0 &&
                player.Scope == donorScope && player.Output?.Scope == donorScope && player.HasOutput &&
                player.IsPaused && player.LastErrorCode is null && after.State == 3 && after.Error == 0 &&
                after.SubmittedPictures == before.SubmittedPictures &&
                Math.Abs(after.Position - before.Position) < .000001 &&
                Math.Abs(player.PositionSeconds - before.Position) < .000001,
                "failed candidate never starts or publishes and leaves the healthy donor's actual output, pause and clock unchanged");
            await player.StopAsync(donorScope);
            await player.StopAsync();
            await player.DisposeAsync();
            Check(kernel.ReleaseCounts.GetValueOrDefault(rejected) == 1 &&
                kernel.ReleaseCounts.GetValueOrDefault(donor) == 1 && kernel.Releases == 2 &&
                donorLease.DisposeCalls == 1 && rejectedLease.DisposeCalls == 1 && !player.HasOutput,
                "discard, stop and disposal never drain either native owner or receipt twice");
        }));
        yield return Case("film kernel real audio and subtitle track replacement keeps the movie scope", () => WithPlayer(async (player, kernel) =>
        {
            await using var origin = Fixture("dash-tracks");
            await using var lease = new Lease(origin.Uri("/root"), origin.Headers);
            var intent = Intent(paused: true);
            await player.PrepareAsync(intent, lease.Address, Input("dash"), lease);
            Check(await player.TryCommitAsync(intent, () => true, () => { }), "multi-track commit");
            var tracks = await player.ReadTracksAsync() ?? throw new InvalidOperationException("Missing multi-track snapshot");
            Check(tracks is { Audio.Count: 2, Subtitles.Count: 1 } && tracks.AudioIndex is not null,
                "two real audio streams and one WebVTT stream are discovered");
            var scope = tracks.Scope;
            int alternate = tracks.Audio.Single(track => track.Index != tracks.AudioIndex).Index;
            await player.SelectAudioTrackAsync(scope, alternate);
            await player.SelectSubtitleTrackAsync(scope, tracks.Subtitles[0].Index);
            var selected = await player.ReadTracksAsync();
            Check(selected?.AudioIndex == alternate && selected?.SubtitleIndex == tracks.Subtitles[0].Index &&
                player.Scope == scope && player.IsPaused, "track commands preserve identity and pause intent");
            try
            {
                await player.SelectAudioTrackAsync(scope, int.MaxValue);
                throw new InvalidOperationException("Expected invalid stream rejection");
            }
            catch (PlaybackException error) when (error.Code == PlaybackError.InvalidTrack) { }
            Check(player.HasOutput && kernel.Releases == 0, "invalid track cannot retire the healthy movie");
            await player.SeekAsync(scope, 8);
            await player.SetPausedAsync(scope, false);
            await Until(() => player.PositionSeconds > 8.3 || player.LastErrorCode is not null, "selected track playback");
            Check(player.LastErrorCode is null && origin.Requests.Any(request => request.Path.Contains("caption-", StringComparison.Ordinal)),
                "the real selected subtitle source is read while movie audio/video remain healthy");
            await player.SetPausedAsync(scope, true);
            await player.SelectSubtitleTrackAsync(scope, null);
            Check((await player.ReadTracksAsync())?.SubtitleIndex is null, "native subtitle disable is confirmed");
        }));
        yield return Case("film kernel two real owners isolate headers and Cookie", () => WithPlayer(async (first, firstKernel) =>
            await WithPlayer(async (second, secondKernel) =>
            {
                await using var a = Fixture("progressive");
                await using var b = Fixture("progressive");
                a.RequiredHeaders = a.Headers; b.RequiredHeaders = b.Headers;
                await using var leaseA = new Lease(a.Uri("/root"), a.Headers);
                await using var leaseB = new Lease(b.Uri("/root"), b.Headers);
                var one = Intent(paused: true); var two = Intent(paused: true);
                await first.PrepareAsync(one, leaseA.Address, Input("progressive"), leaseA);
                Check(await first.TryCommitAsync(one, () => true, () => { }), "first independent owner");
                await second.PrepareAsync(two, leaseB.Address, Input("progressive"), leaseB);
                Check(await second.TryCommitAsync(two, () => true, () => { }), "second independent owner");
                await first.SeekAsync(first.Scope!, 18); await second.SeekAsync(second.Scope!, 8);
                Check(a.Requests.Count > 0 && b.Requests.Count > 0 &&
                    a.Requests.All(request => request.Authorized) && b.Requests.All(request => request.Authorized),
                    "the actual AVIO owners never reuse another media's authorization or Cookie");
            })));
        foreach (bool redirect in new[] { false, true })
            yield return Case("film kernel exact long HTTP location and headers; redirect=" + redirect, () => WithPlayer(async (player, _) =>
            {
                await using var origin = Fixture("progressive");
                origin.RequiredHeaders = origin.Headers;
                var route = origin.Route;
                string exact = "/long?synthetic=" + new string('x', 17000);
                origin.Route = path => path == exact ? route("/root") :
                    path == "/entry" ? Reply.Redirect(origin.Uri(exact).AbsoluteUri) : new Reply([]) { Status = 404 };
                await using var lease = new Lease(origin.Uri(redirect ? "/entry" : exact), origin.Headers);
                var intent = Intent(paused: true);
                await player.PrepareAsync(intent, lease.Address, Input("progressive"), lease);
                Check(await player.TryCommitAsync(intent, () => true, () => { }) &&
                    player.Output is { SubmittedPictures: > 0, HardwareDecoding: false },
                    "a real software-decoded picture reaches the native renderer");
                Check(origin.Requests.Any(request => request.Path == exact) &&
                    origin.Requests.All(request => request.Authorized), "long final URL and full headers reach exact AVIO reads");
                if (redirect) Check(origin.Requests.Count(request => request.Path == "/entry") == 1,
                    "reading the final location does not reopen the initial redirect");
            }));
        yield return Case("film kernel HEVC Main10 decodes on the shared D3D11 device", () => WithPlayer(async (player, _) =>
        {
            // Same self-generated x265 3.5 64x64 Main10 sequence as FilmDovi's
            // native tests. This is SDR Main10, not an HDR/DV display fixture.
            string[] units =
            [
                "40010c01ffff02200000030090000003000003001e959009",
                "42010102200000030090000003000003001ea0208104d965644a4c2e0100000303e900005dc008",
                "4401c073c089", "2801ac76071fff8db56a47", "0201d0195e20e4220cc4d273cbcc",
                "0201e04497820410204dc1c6d768", "0001e024ffe89029809ec8", "0201d03097d620ec220cc99273cbcc",
                "0201e0a225d7820410204dc1c6d768", "0001e086fffa240a609ec8", "0201d038bf5e20e4220b449cf2f3"
            ];
            byte[] raw = units.SelectMany(unit => new byte[] { 0, 0, 0, 1 }.Concat(Convert.FromHexString(unit))).ToArray();
            await using var origin = new Origin { Route = _ => new Reply(raw, "video/hevc") };
            await using var lease = new Lease(origin.Uri("/main10.hevc"), origin.Headers);
            var intent = Intent(paused: true);
            await player.PrepareAsync(intent, lease.Address, Input("progressive"), lease);
            Check(await player.TryCommitAsync(intent, () => true, () => { }) &&
                player.Output is { SubmittedPictures: > 0, NativeHdr: false, DolbyVision: false },
                "Main10 decoding and native texture rendering produce a real SDR picture");
            Console.WriteLine("OBSERVED HEVC Main10; actual D3D11 hardware decoding=" + player.Output!.HardwareDecoding);
        }));
        yield return Case("film kernel canceled blocked seek waits for actual native retirement", () => WithPlayer(async (player, kernel) =>
        {
            await using var origin = Fixture("progressive");
            await using var lease = new Lease(origin.Uri("/root"), origin.Headers);
            using var cancellation = new CancellationTokenSource();
            var intent = Intent(paused: true);
            await player.PrepareAsync(intent, lease.Address, Input("progressive"), lease, cancellation.Token);
            Check(await player.TryCommitAsync(intent, () => true, () => { }), "blocked-seek donor");
            var route = origin.Route;
            origin.Route = path => route(path) with { BlockBody = true };
            var seek = player.SeekAsync(player.Scope!, 20);
            await Until(() => origin.Blocked > 0 || seek.IsCompleted, "seek issues actual HTTP IO");
            Check(origin.Blocked > 0, "seek reached a blocked native AVIO callback");
            cancellation.Cancel();
            try { await seek.WaitAsync(TimeSpan.FromSeconds(8)); throw new InvalidOperationException("Expected canceled seek"); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
            Check(!player.HasOutput && player.Scope is null && lease.Disposals == 1 && kernel.Releases == 1,
                "command cancellation includes worker exit and exact lease release, not a future timer poll");
        }));
        yield return Case("film kernel cross-origin HLS retains one isolated request context", () => WithPlayer(async (player, _) =>
        {
            await using var root = Fixture("hls");
            await using var child = Fixture("hls-aes");
            child.Credential = root.Credential;
            var headers = root.Headers;
            root.RequiredHeaders = child.RequiredHeaders = headers;
            root.Route = _ => new Reply(Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\n" +
                child.Uri("/root").AbsoluteUri + "\n"));
            await using var lease = new Lease(root.Uri("/root"), headers);
            var intent = Intent(paused: true);
            await player.PrepareAsync(intent, lease.Address, Input("hls"), lease);
            Check(await player.TryCommitAsync(intent, () => true, () => { }), "cross-origin native commit");
            Check(root.Requests.All(request => request.Authorized) && child.Requests.Count > 0 &&
                child.Requests.All(request => request.Authorized), "manifest, AES key and fragments share only this media's headers");
        }));
        foreach (bool revoke in new[] { false, true })
            yield return Case("film kernel blocked AVIO drains on " + (revoke ? "revoke" : "cancel"), () => WithPlayer(async (player, kernel) =>
            {
                await using var origin = Fixture("progressive");
                var route = origin.Route;
                origin.Route = path => route(path) with { BlockBody = true };
                await using var lease = new Lease(origin.Uri("/root"), origin.Headers);
                using var cancel = new CancellationTokenSource();
                var request = player.PrepareAsync(Intent(), lease.Address, Input("progressive"), lease, cancel.Token);
                await Until(() => origin.Blocked > 0, "real AVIO blocked");
                if (revoke) lease.Revoke(); else cancel.Cancel();
                try { await request.WaitAsync(TimeSpan.FromSeconds(8)); throw new InvalidOperationException("Expected cancellation"); }
                catch (OperationCanceledException) { }
                catch (PlaybackException) { }
                Check(!player.HasOutput && lease.Disposals == 1 && kernel.Releases == 1,
                    "cancellation completion includes native read exit and lease retirement");
            }));
        yield return Case("film kernel seek to final frame drains and ends", () => WithPlayer(async (player, _) =>
        {
            await using var origin = Fixture("progressive");
            await using var lease = new Lease(origin.Uri("/root"), origin.Headers);
            var intent = Intent(paused: true);
            await player.PrepareAsync(intent, lease.Address, Input("progressive"), lease);
            Check(await player.TryCommitAsync(intent, () => true, () => { }), "final-frame commit");
            var scope = player.Scope!;
            await player.SeekAsync(scope, Seconds - .05);
            Check(Math.Abs(player.PositionSeconds - (Seconds - .05)) < .15, "seek retains the frame covering the requested time");
            bool ended = false; player.Ended += (_, _) => ended = true;
            await player.SetPausedAsync(scope, false);
            await Until(() => ended || player.LastErrorCode is not null, "actual decoder EOF");
            Check(ended && player.LastErrorCode is null, "normal EOF emits ended, not a decode failure");
        }));
    }

    private static TestCase Case(string name, Func<Task> body) => new(name, () => Sta(body));
    private static RuntimeMediaPlaybackIntent Intent(bool paused = false) =>
        new("film", Guid.NewGuid().ToString("N"), 0, paused);
    internal static NativeVideoInput Input(string format) => new(
        format.StartsWith("hls", StringComparison.Ordinal) ? NativeVideoFormat.Hls :
        format.StartsWith("dash", StringComparison.Ordinal) ? NativeVideoFormat.Dash : NativeVideoFormat.Progressive,
        format == "dash-cenc" ? NativeClearKeySet.FromHex(new Dictionary<string, string>
            { [VideoKid] = VideoKey, [AudioKid] = AudioKey }) : null);
    private static async Task WithPlayer(Func<FilmPlayback, ObservedKernel, Task> body)
    {
        var kernel = new ObservedKernel();
        var player = new FilmPlayback(Dispatcher.CurrentDispatcher, kernel) { Volume = 5 };
        var window = new Window
        {
            Title = "Meowfilm film kernel validation", Width = 640, Height = 400, Content = player.VideoView,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ShowActivated = false
        };
        try
        {
            window.Show(); window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            await body(player, kernel);
        }
        finally
        {
            try { await player.DisposeAsync(); }
            finally { window.Content = null; window.Close(); }
        }
    }
    internal static Origin Fixture(string format)
    {
        var origin = new Origin();
        bool encrypted = format == "dash-cenc";
        var video = Enumerable.Range(0, Seconds).Select(i => VideoFragment(i, encrypted)).ToArray();
        var audio = Enumerable.Range(0, Seconds).Select(i => AudioFragment(i, encrypted)).ToArray();
        var initialization = VideoInit(encrypted);
        var progressive = initialization.Concat(video.SelectMany(bytes => bytes)).ToArray();
        var list = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n" +
            (format == "hls-aes" ? "#EXT-X-KEY:METHOD=AES-128,URI=\"key\",IV=0x00000000000000000000000000000000\n" : "") +
            "#EXT-X-MAP:URI=\"video-init\"\n" +
            string.Concat(Enumerable.Range(0, Seconds).Select(i => $"#EXTINF:1,\nvideo-{i}\n")) + "#EXT-X-ENDLIST\n";
        Reply Video(byte[] bytes) => new(format == "hls-aes" ? Aes128(bytes) : bytes, "video/mp4");
        origin.Route = path =>
        {
            if (path == "/root") return format.StartsWith("hls", StringComparison.Ordinal) ?
                new Reply(Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\nvariant\n")) :
                format.StartsWith("dash", StringComparison.Ordinal) ? new Reply(Text(format == "dash-tracks" ? MultiTrackDash() : Dash()), "application/dash+xml") :
                new Reply(progressive, "video/mp4");
            string file = path[(path.LastIndexOf('/') + 1)..];
            if (file == "alternate-init") return new Reply(AudioInit(), "audio/mp4");
            if (file.StartsWith("alternate-", StringComparison.Ordinal) && int.TryParse(file[10..], out int alternate) &&
                alternate >= 0 && alternate < audio.Length) return new Reply(audio[alternate], "audio/mp4");
            if (file.StartsWith("caption-", StringComparison.Ordinal) && int.TryParse(file[8..], out int caption) &&
                caption >= 0 && caption < Seconds)
                return new Reply(Text("WEBVTT\n\n" + TimeSpan.FromSeconds(caption).ToString(@"hh\:mm\:ss\.fff") +
                    " --> " + TimeSpan.FromSeconds(caption + 1).ToString(@"hh\:mm\:ss\.fff") + "\nSynthetic cue\n\n"), "text/vtt");
            if (file == "variant") return new Reply(Text(list));
            if (file == "key") return new Reply(Convert.FromHexString(VideoKey), "application/octet-stream");
            if (file == "video-init") return Video(initialization);
            if (file == "audio-init") return new Reply(AudioInit(encrypted), "audio/mp4");
            if (file.StartsWith("video-", StringComparison.Ordinal) && int.TryParse(file[6..], out int v) && v >= 0 && v < video.Length)
                return Video(video[v]);
            if (file.StartsWith("audio-", StringComparison.Ordinal) && int.TryParse(file[6..], out int a) && a >= 0 && a < audio.Length)
                return new Reply(audio[a], "audio/mp4");
            return new Reply([]) { Status = 404 };
        };
        return origin;
    }
    private static string MultiTrackDash() => Dash().Replace("</Period>", """
        <AdaptationSet contentType="audio" mimeType="audio/mp4" codecs="sowt" lang="eng">
          <SegmentTemplate timescale="1" duration="1" startNumber="0" initialization="alternate-init" media="alternate-$Number$"/>
          <Representation id="alternate" bandwidth="128000"/>
        </AdaptationSet>
        <AdaptationSet contentType="text" mimeType="text/vtt" lang="zho">
          <SegmentTemplate timescale="1" duration="1" startNumber="0" media="caption-$Number$"/>
          <Representation id="captions" bandwidth="1000"/>
        </AdaptationSet>
        </Period>
        """, StringComparison.Ordinal);
    private sealed class ObservedKernel : FilmKernelApi
    {
        internal IntPtr Owner;
        internal int Releases, AvioSeeks, Starts, PrepareResult;
        internal FilmKernelNative.Snapshot Prepared;
        internal readonly ConcurrentDictionary<IntPtr, int> ReleaseCounts = new();
        private readonly List<FilmKernelNative.SeekCallback> seekCallbacks = [];
        private bool observed;
        internal override IntPtr Create(ref FilmKernelNative.Callbacks callbacks, Uri address,
            NativeVideoFormat format, byte[] keys, byte[]? defaultKey)
        {
            var seek = Marshal.GetDelegateForFunctionPointer<FilmKernelNative.SeekCallback>(callbacks.Seek);
            FilmKernelNative.SeekCallback seekCallback = (context, handle, offset, whence) =>
            {
                Interlocked.Increment(ref AvioSeeks);
                return seek(context, handle, offset, whence);
            };
            seekCallbacks.Add(seekCallback);
            callbacks.Seek = Marshal.GetFunctionPointerForDelegate(seekCallback);
            return Owner = base.Create(ref callbacks, address, format, keys, defaultKey);
        }
        internal override int Prepare(IntPtr owner, double position, IntPtr target)
        {
            PrepareResult = base.Prepare(owner, position, target);
            Prepared = base.Read(owner);
            return PrepareResult;
        }
        internal override int Start(IntPtr owner, int paused)
        {
            Interlocked.Increment(ref Starts);
            return base.Start(owner, paused);
        }
        internal override FilmKernelNative.Snapshot Read(IntPtr owner)
        {
            var value = base.Read(owner);
            if (!observed && value.SubmittedPictures > 0 && value.State is 2 or 3 or 4)
            {
                observed = true;
                Console.WriteLine($"OBSERVED_FILM_NATIVE_PRESENT pictures={value.SubmittedPictures}; " +
                    $"hdr={value.NativeHdr}; dovi={value.DolbyVision}; hardware={value.HardwareDecoding}");
            }
            return value;
        }
        internal override void Release(IntPtr owner)
        {
            base.Release(owner);
            ReleaseCounts.AddOrUpdate(owner, 1, (_, count) => count + 1);
            Interlocked.Increment(ref Releases);
        }
    }
    private sealed class Lease(Uri address, IReadOnlyDictionary<string, string> headers) : INativeMediaResourceLease
    {
        private readonly CancellationTokenSource revoked = new();
        private int disposed, disposeCalls;
        internal int Disposals => Volatile.Read(ref disposed);
        internal int DisposeCalls => Volatile.Read(ref disposeCalls);
        public Uri Address => address;
        public IReadOnlyDictionary<string, string> Headers => headers;
        public CancellationToken Revoked => revoked.Token;
        internal void Revoke() => revoked.Cancel();
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCalls);
            if (Interlocked.Exchange(ref disposed, 1) == 0) { revoked.Cancel(); revoked.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static bool BluePixel(uint pixel) => pixel != uint.MaxValue &&
        ((pixel >> 16) & 255) > (pixel & 255) + 40 &&
        ((pixel >> 16) & 255) > ((pixel >> 8) & 255) + 40 && ((pixel >> 8) & 255) < 150;
    private static bool WhitePixel(uint pixel) => pixel != uint.MaxValue &&
        (pixel & 255) > 190 && ((pixel >> 8) & 255) > 190 && ((pixel >> 16) & 255) > 190;
    private static bool SamePixel(uint first, uint second)
    {
        if (first == uint.MaxValue || second == uint.MaxValue) return false;
        for (int shift = 0; shift <= 16; shift += 8)
            if (Math.Abs((int)((first >> shift) & 255) - (int)((second >> shift) & 255)) > 24) return false;
        return true;
    }
    private static uint[] SubtitleDesktopPixels(FilmPlayback player)
    {
        // Sample actual screen pixels across the lower video, not a WPF
        // RenderTargetBitmap (the D3D11 child HWND is absent from that bitmap).
        var first = player.VideoView.PointToScreen(new Point(player.VideoView.ActualWidth * .15,
            player.VideoView.ActualHeight * .5));
        var last = player.VideoView.PointToScreen(new Point(player.VideoView.ActualWidth * .85,
            player.VideoView.ActualHeight * .98));
        var pixels = new List<uint>();
        int width = (int)last.X - (int)first.X, height = (int)last.Y - (int)first.Y;
        IntPtr screen = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            Check(screen != IntPtr.Zero && width > 0 && height > 0, "desktop subtitle capture has a visible extent");
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            Check(memory != IntPtr.Zero && bitmap != IntPtr.Zero, "desktop capture allocates GDI resources");
            previous = SelectObject(memory, bitmap);
            Check(previous != IntPtr.Zero && previous != new IntPtr(-1), "desktop capture selects its bitmap");
            // One actual screen copy avoids thousands of slow screen-DC reads
            // while the movie clock is running. Sampling the copy is local.
            Check(BitBlt(memory, 0, 0, width, height, screen, (int)first.X, (int)first.Y, 0x00CC0020),
                "actual desktop pixels copy into the subtitle sample");
            for (int y = 0; y < height; y += 3)
                for (int x = 0; x < width; x += 3)
                    pixels.Add(GetPixel(memory, x, y));
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
        return pixels.ToArray();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    private static async Task Until(Func<bool> predicate, string phase)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(phase);
            await Task.Delay(20);
        }
    }
}

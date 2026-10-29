using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

// Only generated MPEG audio/TS and PCM/fMP4, literal loopback fixtures and real libVLC dummy.
// No file media, Node, subprocess encoder, public origin, credentials or external account.
public static class AdaptiveMediaTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native runtime bound root rejects another path before loading", async () =>
        {
            await using var fixture = new Origin();
            fixture.Route = path => MediaRoute(path, "hls-mp4", 40);
            var modules = typeof(NativePlayback).Assembly.GetType("MeowFilm.Media.NativeRuntimeModules")!;
            var rootField = modules.GetField("_runtimeRoot", BindingFlags.Static | BindingFlags.NonPublic)!;
            await using (var first = New(fixture))
            {
                await Open(first, fixture, "hls-mp4"); await Decoded(first);
                string root = (string)rootField.GetValue(null)!;
                Check(Path.IsPathFullyQualified(root), "runtime is bound to an observed absolute root");
                string[] before = NativeLibraryPaths();
                Check(before.Length == 2, "one actual libvlc and libvlccore");
                string other = Path.Combine(AppContext.BaseDirectory, "not-the-bound-native-runtime");
                Check(!string.Equals(Path.GetFullPath(other), root, StringComparison.OrdinalIgnoreCase), "distinct runtime request");
                await using var rejected = new NativePlayback(Dispatcher.CurrentDispatcher, other,
                    NativeAudioOutput.Dummy, NativeHttpMediaOptions.ForLoopbackTests([fixture.Base],
                        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));
                try
                {
                    await Open(rejected, fixture, "hls-mp4");
                    throw new InvalidOperationException("Adaptive assertion: other runtime falsely ready");
                }
                catch (PlaybackException error)
                {
                    Check(error.Code == PlaybackError.NativeUnavailable && error.Message == error.Code &&
                        error.InnerException is null, "different root fails with sanitized existing code");
                }
                Check(Field(rejected, "_engine") is null && Session(rejected) is null && !rejected.HasMedia,
                    "rejected root creates no engine or player");
                Check(NativeLibraryPaths().SequenceEqual(before) && (string)rootField.GetValue(null)! == root,
                    "different root never alters loaded runtime or binding");
                Check(first.HasMedia && first.LastErrorCode is null, "existing native player is unaffected");
                object active = Session(first)!; int port = Port(first);
                var engine = (LibVLC)Field(first, "_engine")!;
                await first.DisposeAsync();
                Check(engine.NativeReference == IntPtr.Zero, "first real engine released despite bound DLLs");
                await Released(active, port);
            }
            string bound = (string)rootField.GetValue(null)!;
            // An equivalent normalized explicit path and later null/default both reuse the root.
            foreach (string? path in new[] { Path.Combine(bound, ".") + Path.DirectorySeparatorChar, null })
            {
                await using var player = new NativePlayback(Dispatcher.CurrentDispatcher, path,
                    NativeAudioOutput.Dummy, NativeHttpMediaOptions.ForLoopbackTests([fixture.Base],
                        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));
                await Open(player, fixture, "hls-mp4"); await Decoded(player);
                object active = Session(player)!; int port = Port(player);
                var engine = (LibVLC)Field(player, "_engine")!;
                await player.DisposeAsync();
                Check(engine.NativeReference == IntPtr.Zero && (string)rootField.GetValue(null)! == bound,
                    "same-root engine really released without rebinding");
                await Released(active, port);
            }
        });
        yield return Case("native runtime reinitialization keeps bounded module mappings and releases engines", async () =>
        {
            await using var fixture = new Origin();
            fixture.Route = path => MediaRoute(path, "hls-ts", 40);
            await using (var player = New(fixture))
            {
                await Open(player, fixture, "hls-ts");
                await Decoded(player);
                var engine = (LibVLC)Field(player, "_engine")!;
                await player.DisposeAsync();
                Check(engine.NativeReference == IntPtr.Zero, "coordinator really releases engine");
            }
            var modules = typeof(NativePlayback).Assembly.GetType("MeowFilm.Media.NativeRuntimeModules")!;
            var retained = (System.Collections.IDictionary)modules.GetField("Retained", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Check(retained.Count == 12, "fixed full package retains only measured leaking modules");
            // Previous fixed binaries abort before 80 cycles, without any media or WPF input.
            // No engine is kept alive to hide plugin-bank retirement; every reference reaches zero.
            for (int i = 0; i < 96; i++)
            {
                var engine = await Task.Run(() => new LibVLC(false, "--quiet", "--ignore-config", "--no-interact",
                    "--keystore=none", "--aout=dummy", "--no-metadata-network-access"));
                await Task.Run(engine.Dispose);
                Check(engine.NativeReference == IntPtr.Zero, "each engine released");
            }
            Check(retained.Count == 12, "loader references do not grow per engine");
        });
        foreach (string format in new[] { "hls-ts", "hls-master", "hls-mp4", "dash-list", "dash-number", "dash-time" })
            yield return Case("native decode seek " + format, async () =>
            {
                await using var fixture = new Origin();
                fixture.Route = path => MediaRoute(path, format, 40);
                await using var player = New(fixture);
                await Open(player, fixture, format);
                await Decoded(player);
                Check(player.NativeVersion!.StartsWith("3.0.24", StringComparison.Ordinal), "fixed native version");
                var active = Session(player)!;
                int port = Port(player);
                int beforeSeek = fixture.Requests.Count;
                await player.SeekAsync(28);
                await Until(() => fixture.Requests.Skip(beforeSeek).Any(r =>
                    (format == "dash-time" ? SegmentIndex(r.Path) / 8000 : SegmentIndex(r.Path)) >= 25),
                    "native seek requested a new later segment");
                Check(ReferenceEquals(active, Session(player)), "same native player for seek");
                Check(fixture.Requests.Count > 2 && fixture.Requests.All(r => r.Authorized), "all root/child/init/segment headers");
                Check(!fixture.Requests.Any(r => r.LocalCapability), "local capability never sent upstream");
                await player.StopAsync();
                await Released(active, port);
            });
        yield return Case("natural end releases before queue event", async () =>
        {
            await using var fixture = new Origin();
            fixture.Route = path => MediaRoute(path, "hls-mp4", 3);
            await using var player = New(fixture);
            object? session = null; int port = 0, ended = 0;
            player.Ended += (_, _) =>
            {
                Check(Session(player) is null && !player.HasMedia, "terminal native state retired");
                ended++;
            };
            await Open(player, fixture, "hls-mp4");
            session = Session(player); port = Port(player);
            await Until(() => ended == 1 || player.LastErrorCode is not null, "natural end", 12000);
            Check(ended == 1 && player.LastErrorCode is null, "one real terminal success");
            await Released(session!, port);
        });
        yield return Case("headerless manifest uses controlled graph, progressive stays direct", async () =>
        {
            await using var fixture = new Origin();
            fixture.RequireAuthorization = false;
            fixture.Route = path => path == "/wave" ? new Reply(Wave()) : MediaRoute(path, "hls-ts", 40);
            await using var player = New(fixture);
            await player.OpenAsync("music", "hls", fixture.Uri("root.m3u8"), "");
            await Decoded(player);
            Check(Adaptive(player) is not null, "headerless known manifest controlled");
            await player.OpenAsync("music", "wav", fixture.Uri("wave"), "");
            await Until(() => player.IsPlaying, "ordinary direct progressive");
            Check(Field(Session(player)!, "Adaptive") is null && Field(Session(player)!, "Input") is null,
                "ordinary direct path unchanged");
        });
        yield return Case("extensionless headered manifest detected by bytes", async () =>
        {
            await using var fixture = new Origin();
            fixture.Route = path => path == "/opaque" ? new Reply(Text(Hls("ts", 40)), "application/octet-stream") :
                MediaRoute(path, "hls-ts", 40);
            await using var player = New(fixture);
            await player.OpenAsync("music", "opaque", fixture.Uri("opaque"), "", fixture.Headers);
            await Decoded(player);
            Check(Adaptive(player) is not null, "body classification instead of suffix only");
        });
        foreach (string action in new[] { "cancel", "stop", "dispose", "replace" })
            yield return Case("blocked segment retirement " + action, async () =>
            {
                await using var fixture = new Origin();
                fixture.Route = path => path.StartsWith("/ts", StringComparison.Ordinal) ?
                    new Reply(Ts(0)) { BlockBody = true } : MediaRoute(path, "hls-ts", 40);
                var player = New(fixture);
                using var cancel = new CancellationTokenSource();
                try
                {
                    await Open(player, fixture, "hls-ts", cancel.Token);
                    var session = Session(player)!; int port = Port(player);
                    await Until(() => fixture.Blocked > 0, "blocked upstream body");
                    if (action == "cancel") { cancel.Cancel(); await Until(() => !player.HasMedia, "cancel retired"); }
                    if (action == "stop") await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(6));
                    if (action == "dispose") await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
                    if (action == "replace")
                    {
                        fixture.Route = path => MediaRoute(path, "hls-mp4", 40);
                        await Open(player, fixture, "hls-mp4");
                        await Decoded(player);
                        cancel.Cancel();
                        await Task.Delay(100);
                        Check(player.Identity == "hls-mp4" && player.LastErrorCode is null, "old cancellation cannot affect replacement");
                    }
                    await Released(session, port);
                }
                finally { await player.DisposeAsync(); await player.DisposeAsync(); }
            });
        foreach (bool child in new[] { false, true })
            yield return Case("preflight cancellation " + (child ? "child" : "root"), async () =>
            {
                await using var fixture = new Origin();
                fixture.Route = path => child && path == "/root.m3u8"
                    ? new Reply(Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\nchild.m3u8\n"))
                    : new Reply(Text(Hls("ts", 40))) { BlockBody = true };
                await using var player = New(fixture);
                using var cancel = new CancellationTokenSource();
                var pending = Open(player, fixture, "hls-ts", cancel.Token);
                await Until(() => fixture.Blocked > 0, "preflight blocked");
                cancel.Cancel();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(6)); throw new InvalidOperationException("missing cancellation"); }
                catch (OperationCanceledException) { }
                Check(!player.HasMedia && Session(player) is null, "preflight owns no decoder");
            });
        yield return Case("cross origin segments strip every supplied header", async () =>
        {
            await using var other = new Origin { RequireAuthorization = false };
            other.Route = path => MediaRoute(path, "hls-ts", 40);
            await using var first = new Origin();
            first.Route = _ => new Reply(Text(Hls("ts", 40, other.Base.AbsoluteUri)));
            await using var player = New(first, other);
            await Open(player, first, "hls-ts"); await Decoded(player);
            Check(first.Requests.All(r => r.Authorized) && other.Requests.All(r => !r.AnyCallerHeader),
                "cross origin strips authorization cookie referrer and custom fields");
        });
        yield return Case("redirect away and back never restores credentials for descendants", async () =>
        {
            await using var first = new Origin { RequireAuthorization = false };
            await using var other = new Origin { RequireAuthorization = false };
            first.Route = path => path == "/root.m3u8" ? Reply.Redirect(other.Uri("hop").AbsoluteUri) :
                path == "/returned.m3u8" ? new Reply(Text(Hls("ts", 40))) : MediaRoute(path, "hls-ts", 40);
            other.Route = _ => Reply.Redirect(first.Uri("returned.m3u8").AbsoluteUri);
            await using var player = New(first, other);
            await Open(player, first, "hls-ts"); await Decoded(player);
            Check(first.Requests.First().Authorized && first.Requests.Skip(1).All(r => !r.AnyCallerHeader) &&
                other.Requests.All(r => !r.AnyCallerHeader), "permanent redirect-chain stripping");
        });
        yield return Case("child redirect cannot restore root credentials", async () =>
        {
            await using var first = new Origin { RequireAuthorization = false };
            await using var other = new Origin { RequireAuthorization = false };
            first.Route = path => path switch
            {
                "/root.m3u8" => new Reply(Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\nchild.m3u8\n")),
                "/child.m3u8" => Reply.Redirect(other.Uri("hop").AbsoluteUri),
                "/returned.m3u8" => new Reply(Text(Hls("ts", 40))),
                _ => MediaRoute(path, "hls-ts", 40)
            };
            other.Route = _ => Reply.Redirect(first.Uri("returned.m3u8").AbsoluteUri);
            await using var player = New(first, other);
            await Open(player, first, "hls-ts"); await Decoded(player);
            Check(first.Requests.Where(r => r.Path is "/root.m3u8" or "/child.m3u8").All(r => r.Authorized) &&
                first.Requests.Where(r => r.Path != "/root.m3u8" && r.Path != "/child.m3u8").All(r => !r.AnyCallerHeader),
                "scope carried across recursive manifests");
        });
        yield return Case("capability rejects missing auth unknown path and revoked session", async () =>
        {
            await using var fixture = new Origin();
            fixture.Route = path => MediaRoute(path, "hls-ts", 40);
            await using var player = New(fixture);
            await Open(player, fixture, "hls-ts"); await Decoded(player);
            object adaptive = Adaptive(player)!;
            var root = (Uri)Property(adaptive, "Root")!;
            var cap = (string)Property(adaptive, "Capability")!;
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            using var denied = await client.GetAsync(root);
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "missing capability denied");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(cap);
            using var allowed = await client.GetAsync(root);
            Check(allowed.StatusCode == HttpStatusCode.OK, "session header accepted");
            string rewritten = await allowed.Content.ReadAsStringAsync();
            Check(!rewritten.Contains(fixture.Base.AbsoluteUri, StringComparison.Ordinal) &&
                !rewritten.Contains(fixture.Credential, StringComparison.Ordinal) &&
                !root.AbsoluteUri.Contains(cap, StringComparison.Ordinal), "no upstream/capability in local URL/body");
            using var unknown = await client.GetAsync(new Uri(root, "/r/" + new string('0', 32)));
            Check(unknown.StatusCode == HttpStatusCode.NotFound, "no arbitrary proxy target");
            int port = Port(player); var session = Session(player)!;
            await player.StopAsync(); await Released(session, port);
            try { await client.GetAsync(root); throw new InvalidOperationException("revoked endpoint still open"); }
            catch (HttpRequestException) { }
        });
        yield return Case("HLS explicit byterange and validator on shared representation", async () =>
        {
            await using var fixture = new Origin();
            byte[] first = Ts(0), second = Ts(1);
            byte[] all = first.Concat(second).ToArray();
            fixture.Route = path => path == "/root.m3u8"
                ? new Reply(Text($"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:1.008,\n#EXT-X-BYTERANGE:{first.Length}@0\nall.ts\n#EXTINF:1.008,\n#EXT-X-BYTERANGE:{second.Length}@{first.Length}\nall.ts\n#EXT-X-ENDLIST\n"))
                : new Reply(all, "video/mp2t");
            await using var player = New(fixture);
            await Open(player, fixture, "hls-ts");
            await Until(() => fixture.Requests.Count(r => r.Path == "/all.ts") >= 2, "both byte-range segments");
            Check(fixture.Requests.Where(r => r.Path == "/all.ts").All(r => r.Range) &&
                fixture.Requests.Last(r => r.Path == "/all.ts").IfRange, "range with shared strong identity");
            Check(player.LastErrorCode is null, "range playback not failed");
        });
        yield return Case("actual capability Range preserves exact bytes and If-Range", async () =>
        {
            await using var fixture = new Origin();
            fixture.Route = path => MediaRoute(path, "hls-ts", 40);
            await using var player = New(fixture);
            await Open(player, fixture, "hls-ts"); await Decoded(player);
            var adaptive = Adaptive(player)!;
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            client.DefaultRequestHeaders.UserAgent.ParseAdd((string)Property(adaptive, "Capability")!);
            string manifest = await client.GetStringAsync((Uri)Property(adaptive, "Root")!);
            string segment = manifest.Split('\n').First(l => l.StartsWith("http://", StringComparison.Ordinal));
            using var request = new HttpRequestMessage(HttpMethod.Get, segment);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(188, 375);
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.PartialContent &&
                response.Content.Headers.ContentRange?.From == 188 &&
                (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(Ts(0).Skip(188).Take(188)),
                "native resource single Range maps exact upstream bytes");
            Check(fixture.Requests.Any(r => r.Path == "/ts0.ts" && r.IfRange), "wire strong If-Range");
        });
        yield return Case("shared representation changes between HLS byte ranges fail closed", async () =>
        {
            await using var fixture = new Origin();
            byte[] segment = Ts(0); int requests = 0;
            fixture.Route = path => path == "/root.m3u8"
                ? new Reply(Text($"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:1.008,\n#EXT-X-BYTERANGE:{segment.Length}@0\nall.ts\n#EXTINF:1.008,\n#EXT-X-BYTERANGE:{segment.Length}@{segment.Length}\nall.ts\n#EXT-X-ENDLIST\n"))
                : new Reply(segment.Concat(Ts(1)).ToArray(), "video/mp2t")
                { Tag = Interlocked.Increment(ref requests) == 1 ? "\"fixture-v1\"" : "\"fixture-v2\"" };
            await using var player = New(fixture);
            await Open(player, fixture, "hls-ts");
            await Until(() => player.LastErrorCode is not null, "changed representation terminal");
            Check(player.LastErrorCode == PlaybackError.HttpRepresentationChanged && !player.HasMedia,
                "never splice another representation");
        });
        yield return Case("stalled segment read has a real deadline and releases service", async () =>
        {
            await using var fixture = new Origin();
            fixture.Route = path => path.StartsWith("/ts", StringComparison.Ordinal)
                ? new Reply(Ts(0)) { BlockBody = true } : MediaRoute(path, "hls-ts", 40);
            await using var player = new NativePlayback(Dispatcher.CurrentDispatcher, null, NativeAudioOutput.Dummy,
                NativeHttpMediaOptions.ForLoopbackTests([fixture.Base], TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250)));
            await Open(player, fixture, "hls-ts");
            var session = Session(player)!; int port = Port(player);
            await Until(() => player.LastErrorCode is not null, "read deadline");
            Check(player.LastErrorCode == PlaybackError.HttpDeadline, "deadline classification");
            await Released(session, port);
        });
        foreach (int status in new[] { 401, 403, 410, 429, 503 })
            yield return Case("upstream terminal status " + status, async () =>
            {
                await using var fixture = new Origin();
                fixture.Route = path => path.StartsWith("/ts", StringComparison.Ordinal) ?
                    new Reply([]) { Status = status } : MediaRoute(path, "hls-ts", 40);
                await using var player = New(fixture);
                int ended = 0; player.Ended += (_, _) => ended++;
                await Open(player, fixture, "hls-ts");
                var session = Session(player)!; int port = Port(player);
                string code = status switch
                {
                    401 or 403 => PlaybackError.HttpUnauthorized, 410 => PlaybackError.HttpExpired,
                    429 => PlaybackError.HttpThrottled, _ => PlaybackError.HttpServerFailed
                };
                await Until(() => player.LastErrorCode is not null, "upstream failure");
                Check(player.LastErrorCode == code && ended == 0, "fixed failure not natural end");
                await Released(session, port);
            });
        foreach (string bad in new[] { "file", "loopback", "xml-entity", "drm", "aes-version", "live", "dynamic",
            "unknown-hls", "duplicate-attribute", "mime-poison", "oversize", "cycle", "template-external" })
            yield return Case("reject before decoder " + bad, async () =>
            {
                await using var fixture = new Origin();
                string manifest = bad switch
                {
                    "file" => Hls("ts", 1).Replace("ts0.ts", "file:///C:/private.ts", StringComparison.Ordinal),
                    "loopback" => Hls("ts", 1).Replace("ts0.ts", "http://127.0.0.1:1/private.ts", StringComparison.Ordinal),
                    "xml-entity" => "<!DOCTYPE MPD [<!ENTITY x SYSTEM 'file:///C:/private'>]><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\">&x;</MPD>",
                    "drm" => Dash("list", 4).Replace("<Representation", "<ContentProtection schemeIdUri=\"urn:uuid:00000000-0000-0000-0000-000000000000\"/><Representation", StringComparison.Ordinal),
                    "aes-version" => Hls("ts", 4).Replace("#EXT-X-TARGETDURATION:2", "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",KEYFORMATVERSIONS=\"2\"\n#EXT-X-TARGETDURATION:2", StringComparison.Ordinal),
                    "live" => Hls("ts", 4).Replace("#EXT-X-ENDLIST", "", StringComparison.Ordinal),
                    "dynamic" => Dash("list", 4).Replace("type=\"static\"", "type=\"dynamic\"", StringComparison.Ordinal),
                    "unknown-hls" => Hls("ts", 4) + "#EXT-X-SESSION-DATA:DATA-ID=\"x\",URI=\"file:///private\"\n",
                    "duplicate-attribute" => "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1,BANDWIDTH=2\nchild.m3u8\n",
                    "mime-poison" => "",
                    "oversize" => "#EXTM3U\n#" + new string('a', 1024 * 1024),
                    "cycle" => "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nroot.m3u8\n",
                    _ => Dash("number", 4).Replace("mp4$Number$.m4s", "file:///C:/mp4$Number$.m4s", StringComparison.Ordinal)
                };
                fixture.Route = _ => bad == "mime-poison" ? new Reply(Wave(), "application/dash+xml") : new Reply(Text(manifest));
                await using var player = New(fixture);
                try
                {
                    await Open(player, fixture, bad is "drm" or "dynamic" or "template-external" or "xml-entity" ? "dash-list" : "hls-ts");
                    throw new InvalidOperationException("unsupported input accepted");
                }
                catch (PlaybackException error)
                {
                    Check(error.Message == error.Code && error.InnerException is null, "fixed sanitized error");
                    if (bad == "drm") Check(error.Code == PlaybackError.AdaptiveDrmUnsupported, "DRM explicit");
                    if (bad == "aes-version") Check(error.Code == PlaybackError.AdaptiveEncryptionUnsupported, "unsupported encryption version explicit");
                }
                Check(Session(player) is null && !player.HasMedia, "no decoder for invalid graph");
                Check(!fixture.Requests.Any(r => r.Path == "/key.bin"), "unsupported key not fetched");
            });
        foreach (string poison in new[] { "playlist-segment", "external-dref", "truncated", "range-ignored" })
            yield return Case("payload failure " + poison, async () =>
            {
                await using var fixture = new Origin();
                string format = poison == "external-dref" ? "hls-mp4" : "hls-ts";
                fixture.Route = path =>
                {
                    var normal = MediaRoute(path, format, 40);
                    if (path.StartsWith("/ts", StringComparison.Ordinal))
                    {
                        if (poison == "playlist-segment") return new Reply(Text(Hls("ts", 40)));
                        if (poison == "truncated") return normal with { Truncate = true };
                    }
                    if (poison == "external-dref" && path == "/init.mp4")
                    {
                        byte[] init = Init(40); int url = init.AsSpan().IndexOf("url "u8); init[url + 7] = 0;
                        return new Reply(init, "video/mp4");
                    }
                    if (poison == "range-ignored")
                    {
                        if (path == "/root.m3u8") return new Reply(Text($"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:1.008,\n#EXT-X-BYTERANGE:{Ts(0).Length}@0\nts0.ts\n#EXT-X-ENDLIST\n"));
                        return normal with { IgnoreRange = true };
                    }
                    return normal;
                };
                await using var player = New(fixture);
                object? active = null; int port = 0, failed = 0, ended = 0;
                player.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(NativePlayback.HasMedia) && player.HasMedia)
                    { active = Session(player); port = Port(player); }
                };
                player.Failed += (_, _) => failed++;
                player.Ended += (_, _) => ended++;
                await Open(player, fixture, format);
                try { await Until(() => player.LastErrorCode is not null, "payload failure"); }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"Adaptive phase: payload failure; hasMedia={player.HasMedia}; " +
                        $"failed={failed}; ended={ended}; initRequests={fixture.Requests.Count(r => r.Path == "/init.mp4")}");
                }
                Check(!player.HasMedia && player.LastErrorCode != PlaybackError.ReleaseFailed, "real stop after payload failure");
                Check(failed == 1 && ended == 0, "rejected payload is one failure, never natural EOF");
                if (poison == "external-dref")
                    Check(fixture.Requests.Any(r => r.Path == "/init.mp4") &&
                        player.LastErrorCode == PlaybackError.HttpFormatUnsupported, "native requested init and external data reference rejected");
                Check(active is not null, "registered real native session");
                await Released(active!, port);
            });
        yield return Case("confirmed payload rejection survives request cancellation", async () =>
        {
            await using var fixture = new Origin();
            fixture.Route = path => MediaRoute(path, "hls-mp4", 40);
            await using var player = New(fixture);
            int failed = 0, ended = 0;
            player.Failed += (_, _) => failed++;
            player.Ended += (_, _) => ended++;
            await Open(player, fixture, "hls-mp4"); await Decoded(player);
            object active = Session(player)!, adaptive = Adaptive(player)!;
            int port = Port(player);
            using var request = new CancellationTokenSource();
            request.Cancel(); // Deterministic order: close/seek notification before error classification.
            ReportRequestFailure(adaptive, new OperationCanceledException(request.Token), request.Token);
            Check(ReferenceEquals(Session(player), active) && player.LastErrorCode is null, "cancelled IO alone does not retire playback");
            ReportRequestFailure(adaptive, RejectedInit(adaptive), request.Token);
            await Until(() => player.LastErrorCode is not null, "cancelled request cannot swallow confirmed format error");
            Check(player.LastErrorCode == PlaybackError.HttpFormatUnsupported && failed == 1 && ended == 0,
                "confirmed error wins over request cancellation and EOF");
            await Released(active, port);
        });
        yield return Case("retired payload error cannot fail replacement", async () =>
        {
            await using var fixture = new Origin();
            fixture.Route = path => MediaRoute(path, "hls-mp4", 40);
            await using var player = New(fixture);
            await Open(player, fixture, "hls-mp4"); await Decoded(player);
            object old = Session(player)!, retired = Adaptive(player)!; int oldPort = Port(player);
            await Open(player, fixture, "hls-mp4"); await Decoded(player);
            object current = Session(player)!;
            ReportRequestFailure(retired, RejectedInit(retired), new CancellationToken(true));
            await Released(old, oldPort);
            Check(ReferenceEquals(Session(player), current) && player.LastErrorCode is null && player.HasMedia,
                "old session cancellation still suppresses a delayed confirmed failure");
        });
    }

    private delegate void ValidateInit(ReadOnlySpan<byte> bytes, int depth);
    private static string[] NativeLibraryPaths()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Where(m => m.ModuleName is "libvlc.dll" or "libvlccore.dll")
            .Select(m => m.FileName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static PlaybackException RejectedInit(object adaptive)
    {
        byte[] init = Init(40);
        init[init.AsSpan().IndexOf("url "u8) + 7] = 0;
        var validate = adaptive.GetType().GetMethod("ValidateInit", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<ValidateInit>();
        try { validate(init, 0); }
        catch (PlaybackException error)
        {
            Check(error.Code == PlaybackError.HttpFormatUnsupported, "real structural validator rejected dref");
            return error;
        }
        throw new InvalidOperationException("Adaptive assertion: external data reference accepted");
    }
    private static void ReportRequestFailure(object adaptive, Exception error, CancellationToken request) =>
        adaptive.GetType().GetMethod("ReportRequestFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(adaptive, [error, true, request]);
    private static NativePlayback New(params Origin[] origins) => new(Dispatcher.CurrentDispatcher, null,
        NativeAudioOutput.Dummy, NativeHttpMediaOptions.ForLoopbackTests(origins.Select(o => o.Base),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));
    private static Task Open(NativePlayback player, Origin origin, string format, CancellationToken token = default) =>
        player.OpenAsync("music", format, origin.Uri(format.StartsWith("dash", StringComparison.Ordinal) ? "root.mpd" : "root.m3u8"),
            "合成媒体", origin.Headers, token);
    internal static async Task Decoded(NativePlayback player)
    {
        await Until(() =>
        {
            if (player.LastErrorCode is not null) throw new InvalidOperationException("native failed " + player.LastErrorCode);
            if (Session(player) is not { } session) return false;
            return player.IsPlaying && ((LibVLCSharp.Shared.Media)Field(session, "Media")!).Statistics is { DecodedAudio: > 0 };
        }, "actual decoded audio blocks", 12000);
    }
    internal static object? Field(object obj, string name) => obj.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(obj);
    private static object? Property(object obj, string name) => obj.GetType().GetProperty(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(obj);
    internal static object? Session(NativePlayback player) => Field(player, "_session");
    internal static object? Adaptive(NativePlayback player) => Field(Session(player)!, "Adaptive");
    internal static int Port(NativePlayback player) => (int)Property(Adaptive(player)!, "Port")!;
    internal static async Task Released(object session, int port)
    {
        await Until(() => ((MediaPlayer)Field(session, "Player")!).NativeReference == IntPtr.Zero, "native released");
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); } finally { listener.Stop(); }
        var adaptive = Field(session, "Adaptive")!;
        Check(((ConcurrentDictionary<int, TcpClient>)Field(adaptive, "_clients")!).IsEmpty, "no service connections");
        var graph = Field(adaptive, "_manifest")!;
        var resources = (System.Collections.IDictionary)Field(graph, "_resources")!;
        Check(resources.Count == 0 && (string)Property(adaptive, "Capability")! == "", "retired graph/capability cleared");
    }
    internal static TestCase Case(string name, Func<Task> body) => new("adaptive-media: " + name, () => Sta(body));
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Adaptive assertion: " + message); }
    private static async Task Until(Func<bool> predicate, string phase, int milliseconds = 8000)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Adaptive phase: " + phase);
            await Task.Delay(15);
        }
    }
    internal static async Task Sta(Func<Task> body)
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
        finally { Check(thread.Join(3000), "STA exited"); }
    }
    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
    private static int SegmentIndex(string path)
    {
        string file = path.TrimStart('/');
        int prefix = file.StartsWith("ts", StringComparison.Ordinal) ? 2 : file.StartsWith("mp4", StringComparison.Ordinal) ? 3 : -1;
        return prefix < 0 || !int.TryParse(file[prefix..].Split('.')[0], out int n) ? -1 : n;
    }
    internal static Reply MediaRoute(string path, string format, int count)
    {
        if (path == "/root.mpd") return new Reply(Text(Dash(format[5..], count)), "application/dash+xml");
        if (path == "/root.m3u8" && format == "hls-master")
            return new Reply(Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.6B\"\nchild.m3u8\n"));
        if (path is "/root.m3u8" or "/child.m3u8")
            return new Reply(Text(Hls(format == "hls-mp4" ? "mp4" : "ts", count)));
        if (path == "/init.mp4") return new Reply(Init(count), "video/mp4");
        int index = SegmentIndex(path);
        if (index >= 0 && path.StartsWith("/ts", StringComparison.Ordinal)) return new Reply(Ts(index), "video/mp2t");
        if (index >= 0) return new Reply(Fragment(format == "dash-time" ? index / 8000 : index), "video/mp4");
        return new Reply([]) { Status = 404 };
    }
    internal static string Hls(string format, int count, string prefix = "")
    {
        var text = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        if (format == "mp4") text.AppendLine("#EXT-X-MAP:URI=\"" + prefix + "init.mp4\"");
        for (int i = 0; i < count; i++)
            text.AppendLine("#EXTINF:" + (format == "ts" ? "1.008" : "1") + ",")
                .AppendLine(prefix + format + i + (format == "ts" ? ".ts" : ".m4s"));
        return text.AppendLine("#EXT-X-ENDLIST").ToString();
    }
    internal static string Dash(string mode, int count)
    {
        string descriptor = mode == "list"
            ? "<SegmentList timescale=\"8000\" duration=\"8000\"><Initialization sourceURL=\"init.mp4\"/>" +
                string.Concat(Enumerable.Range(0, count).Select(i => $"<SegmentURL media=\"mp4{i}.m4s\"/>")) + "</SegmentList>"
            : "<SegmentTemplate timescale=\"8000\" duration=\"8000\" startNumber=\"0\" initialization=\"init.mp4\" media=\"" +
                (mode == "time" ? "mp4$Time$.m4s" : "mp4$Number$.m4s") + "\">" +
                (mode == "time" ? $"<SegmentTimeline><S t=\"0\" d=\"8000\" r=\"{count - 1}\"/></SegmentTimeline>" : "") +
                "</SegmentTemplate>";
        return $"<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT{count}S\" minBufferTime=\"PT1S\">" +
            "<Period><AdaptationSet mimeType=\"audio/mp4\"><Representation id=\"a\" bandwidth=\"128000\" codecs=\"sowt\" audioSamplingRate=\"8000\">" +
            descriptor + "</Representation></AdaptationSet></Period></MPD>";
    }
    private static byte[] Wave()
    {
        using var memory = new MemoryStream(); using var w = new BinaryWriter(memory);
        const int length = 8000 * 2 * 40;
        w.Write("RIFF"u8); w.Write(length + 36); w.Write("WAVEfmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(8000); w.Write(16000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(length); w.Write(new byte[length]); return memory.ToArray();
    }
    private static byte[] U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    private static byte[] U16(ushort value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); return b; }
    private static byte[] Join(params byte[][] values) => values.SelectMany(x => x).ToArray();
    private static byte[] Box(string type, params byte[][] data)
    {
        byte[] bytes = Join(data); return Join(U32((uint)bytes.Length + 8), Encoding.ASCII.GetBytes(type), bytes);
    }
    private static byte[] Full(string type, uint flags, params byte[][] data) => Box(type, Join(U32(flags), Join(data)));
    private static byte[] Matrix() => Join(U32(0x10000), U32(0), U32(0), U32(0), U32(0x10000), U32(0), U32(0), U32(0), U32(0x40000000));
    internal static byte[] Init(int seconds)
    {
        uint duration = (uint)seconds * 8000;
        byte[] sample = Box("sowt", new byte[6], U16(1), new byte[8], U16(1), U16(16), new byte[4], U32(8000u << 16));
        return Join(Box("ftyp", Text("isom"), U32(0), Text("isomiso6mp41")),
            Box("moov",
                Full("mvhd", 0, new byte[8], U32(8000), U32(duration), U32(0x10000), U16(0x100), new byte[10], Matrix(), new byte[24], U32(2)),
                Box("trak",
                    Full("tkhd", 7, new byte[8], U32(1), U32(0), U32(duration), new byte[8], U16(0), U16(0), U16(0x100), U16(0), Matrix(), new byte[8]),
                    Box("mdia", Full("mdhd", 0, new byte[8], U32(8000), U32(duration), U16(0x55c4), U16(0)),
                        Full("hdlr", 0, U32(0), Text("soun"), new byte[12], Text("Synthetic\0")),
                        Box("minf", Full("smhd", 0, new byte[4]),
                            Box("dinf", Full("dref", 0, U32(1), Full("url ", 1))),
                            Box("stbl", Full("stsd", 0, U32(1), sample), Full("stts", 0, U32(0)),
                                Full("stsc", 0, U32(0)), Full("stsz", 0, U32(0), U32(0)), Full("stco", 0, U32(0)))))),
                Box("mvex", Full("trex", 0, U32(1), U32(1), U32(8000), U32(16000), U32(0)))));
    }
    internal static byte[] Fragment(int index)
    {
        byte[] payload = new byte[16000];
        for (int i = 0; i < 8000; i++) BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(i * 2), (short)(Math.Sin(i * 0.2) * 1000));
        byte[] Make(uint offset) => Box("moof", Full("mfhd", 0, U32((uint)index + 1)),
            Box("traf", Full("tfhd", 0x20000, U32(1)), Full("tfdt", 0x1000000, U32(0), U32((uint)index * 8000)),
                Full("trun", 0x301, U32(1), U32(offset), U32(8000), U32((uint)payload.Length))));
        byte[] moof = Make(0); moof = Make((uint)moof.Length + 8);
        return Join(moof, Box("mdat", payload));
    }
    internal static byte[] Ts(int index)
    {
        using var output = new MemoryStream();
        int continuity = (index * 126) & 15;
        void Packet(int pid, bool start, byte[] data, bool pcr = false, long timestamp = 0)
        {
            int position = 0; bool first = true;
            while (position < data.Length)
            {
                var packet = Enumerable.Repeat((byte)0xff, 188).ToArray();
                packet[0] = 0x47; packet[1] = (byte)((pid >> 8) | (first && start ? 0x40 : 0)); packet[2] = (byte)pid;
                bool clock = first && pcr;
                int take = Math.Min(data.Length - position, clock ? 176 : 184);
                bool adaptation = take < 184;
                packet[3] = (byte)((adaptation ? 0x30 : 0x10) | (pid == 0x101 ? continuity++ & 15 : 0));
                int at = 4;
                if (adaptation)
                {
                    int length = 183 - take; packet[at++] = (byte)length;
                    if (length > 0) packet[at++] = clock ? (byte)0x10 : (byte)0;
                    if (clock)
                    {
                        packet[at++] = (byte)(timestamp >> 25); packet[at++] = (byte)(timestamp >> 17);
                        packet[at++] = (byte)(timestamp >> 9); packet[at++] = (byte)(timestamp >> 1);
                        packet[at++] = (byte)((timestamp & 1) << 7 | 0x7e); packet[at++] = 0;
                    }
                    at = 5 + length;
                }
                data.AsSpan(position, take).CopyTo(packet.AsSpan(at));
                output.Write(packet); position += take; first = false;
            }
        }
        byte[] Section(byte[] bytes)
        {
            uint crc = 0xffffffff;
            foreach (byte b in bytes)
            {
                crc ^= (uint)b << 24;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
            }
            return Join([0], bytes, U32(crc));
        }
        Packet(0, true, Section([0, 0xb0, 13, 0, 1, 0xc1, 0, 0, 0, 1, 0xe1, 0]));
        Packet(0x100, true, Section([2, 0xb0, 18, 0, 1, 0xc1, 0, 0, 0xe1, 1, 0xf0, 0, 3, 0xe1, 1, 0xf0, 0]));
        for (int frame = 0; frame < 42; frame++)
        {
            long pts = 90000 + ((long)index * 42 + frame) * 2160;
            byte[] audio = new byte[384]; audio[0] = 0xff; audio[1] = 0xfd; audio[2] = 0x84; audio[3] = 0xc0;
            byte[] stamp = [(byte)(0x21 | (pts >> 29) & 0x0e), (byte)(pts >> 22),
                (byte)((pts >> 14) & 0xfe | 1), (byte)(pts >> 7), (byte)((pts << 1) & 0xfe | 1)];
            Packet(0x101, true, Join([0, 0, 1, 0xc0], U16(392), [0x80, 0x80, 5], stamp, audio), true, pts);
        }
        return output.ToArray();
    }

    internal sealed record Reply(byte[] Data, string Mime = "application/vnd.apple.mpegurl")
    {
        internal int Status { get; init; } = 200;
        internal string? Location { get; init; }
        internal bool BlockBody { get; init; }
        internal int PrefixBytes { get; init; }
        internal bool Truncate { get; init; }
        internal bool IgnoreRange { get; init; }
        internal string Tag { get; init; } = "\"fixture-v1\"";
        internal static Reply Redirect(string location) => new([]) { Status = 302, Location = location };
    }
    internal sealed record Request(string Path, bool Authorized, bool AnyCallerHeader, bool LocalCapability, bool Range, bool IfRange);
    internal sealed class Origin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _handlers = [];
        private readonly Task _accept;
        internal string Credential { get; } = "Fixture " + Guid.NewGuid().ToString("N");
        internal Dictionary<string, string> Headers => new()
        {
            ["Authorization"] = Credential, ["Cookie"] = "synthetic=" + Credential[8..],
            ["Referer"] = Base.AbsoluteUri, ["X-Fixture"] = "synthetic-only"
        };
        internal Uri Base { get; }
        internal Uri Uri(string path) => new(Base, path);
        internal readonly ConcurrentQueue<Request> Requests = new();
        internal Func<string, Reply> Route = _ => new Reply([]) { Status = 404 };
        internal bool RequireAuthorization = true;
        internal int Blocked;
        internal Origin()
        {
            _listener.Start();
            Base = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _accept = Accept();
        }
        private async Task Accept()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var task = Handle(client);
                    lock (_handlers) _handlers.Add(task);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        private async Task Handle(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    using var registration = _stop.Token.Register(client.Dispose);
                    var bytes = new byte[32768]; int count = 0;
                    while (count < bytes.Length)
                    {
                        int n = await stream.ReadAsync(bytes.AsMemory(count), _stop.Token); if (n == 0) return;
                        count += n; if (bytes.AsSpan(0, count).IndexOf("\r\n\r\n"u8) >= 0) break;
                    }
                    string[] lines = Encoding.ASCII.GetString(bytes, 0, count).Split("\r\n");
                    string path = lines[0].Split(' ')[1];
                    var headers = lines.Skip(1).Where(l => l.Contains(':')).Select(l => l.Split(':', 2))
                        .ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    bool authorized = headers.GetValueOrDefault("Authorization") == Credential;
                    Requests.Enqueue(new(path, authorized,
                        new[] { "Authorization", "Cookie", "Referer", "X-Fixture" }.Any(headers.ContainsKey),
                        headers.GetValueOrDefault("User-Agent", "").StartsWith("MeowAdaptive/", StringComparison.Ordinal),
                        headers.ContainsKey("Range"), headers.ContainsKey("If-Range")));
                    Reply reply = RequireAuthorization && !authorized ? new Reply([]) { Status = 401 } : Route(path);
                    int status = reply.Status; int start = 0, end = reply.Data.Length - 1;
                    string rangeHeader = "";
                    if (status == 200 && !reply.IgnoreRange && headers.TryGetValue("Range", out var range))
                    {
                        string[] parts = range[6..].Split('-'); start = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        if (parts[1].Length > 0) end = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        end = Math.Min(end, reply.Data.Length - 1); status = 206;
                        rangeHeader = $"Content-Range: bytes {start}-{end}/{reply.Data.Length}\r\n";
                    }
                    int length = Math.Max(0, end - start + 1);
                    string head = $"HTTP/1.1 {status} Fixture\r\nContent-Type: {reply.Mime}\r\nContent-Length: {length}\r\nETag: {reply.Tag}\r\n" +
                        rangeHeader + (reply.Location is null ? "" : "Location: " + reply.Location + "\r\n") + "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                    if (reply.BlockBody) { if (reply.PrefixBytes > 0) await stream.WriteAsync(reply.Data.AsMemory(start, Math.Min(reply.PrefixBytes, length)), _stop.Token); Interlocked.Increment(ref Blocked); await Task.Delay(Timeout.Infinite, _stop.Token); }
                    int take = reply.Truncate ? Math.Min(1000, length) : length;
                    if (take > 0) await stream.WriteAsync(reply.Data.AsMemory(start, take), _stop.Token);
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); await _accept;
            Task[] handlers; lock (_handlers) handlers = _handlers.ToArray();
            await Task.WhenAll(handlers);
            var released = new TcpListener(IPAddress.Loopback, Base.Port);
            try { released.Start(); } finally { released.Stop(); }
            _stop.Dispose();
        }
    }
}

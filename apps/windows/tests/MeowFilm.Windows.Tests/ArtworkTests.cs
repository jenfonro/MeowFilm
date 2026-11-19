using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Dispatching;
using MeowFilm.Desktop;
using Color = Windows.UI.Color;
using PixelFormats = System.Windows.Media.PixelFormats;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Windows.Media.Imaging;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class ArtworkTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native artwork default file cache stores raw bytes and cold reads avoid HTTP", () => Sta(async () =>
        {
            string root = TestWorkspace.DataPath("artwork-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            byte[] png = Encode("png", 2, 1);
            const string address = "https://art.example.test/default-file?variant=original";
            try
            {
                var firstHttp = new Handler((_, _) => Task.FromResult(Reply(png)));
                await using (var first = new ArtworkService(firstHttp, cacheDirectory: root))
                {
                    using var image = await first.AcquireAsync(address);
                    Equal(2, image.Image.PixelWidth); Check(Immutable(image.Image));
                    Equal(1, firstHttp.Count);
                    string file = Directory.GetFiles(root, "*.resource").Single();
                    Check(File.ReadAllBytes(file).SequenceEqual(png));
                    Check(Directory.GetFiles(root).All(path => Path.GetFileName(path) == ".resources.lock" ||
                        path.EndsWith(".resource", StringComparison.Ordinal)));
                }
                var coldHttp = new Handler((_, _) => throw new InvalidOperationException("No cold HTTP expected"));
                await using var cold = new ArtworkService(coldHttp, cacheDirectory: root);
                using var restored = await cold.AcquireAsync(address);
                Equal(2, restored.Image.PixelWidth); Check(Immutable(restored.Image));
                Equal(0, coldHttp.Count);
            }
            finally
            {
                TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root);
                Directory.Delete(root, true);
            }
        }));
        yield return new("Native artwork one sample produces raw catalog and bounded player palettes", () => Sta(() =>
        {
            var input = new byte[] { 100, 200, 250, 255 };
            var image = new ArtworkPixels(1, 1, input); input.AsSpan().Clear(); Check(Immutable(image));
            var palette = ArtworkImage.SamplePalette(image);
            var raw = (SolidColorBrush)palette.Raw;
            var player = (SolidColorBrush)palette.Palette;
            Check(ReferenceEquals(image.GetSource(DispatcherQueue.GetForCurrentThread()), image.GetSource(DispatcherQueue.GetForCurrentThread())));
            Equal(Color.FromArgb(255, 250, 200, 100), raw.Color);
            var luminance = .2126 * player.Color.R + .7152 * player.Color.G + .0722 * player.Color.B;
            Check(Math.Abs(luminance - 105) < 2);
            var empty = new ArtworkPixels(1, 1, new byte[] { 0, 0, 0, 0 });
            var transparent = ArtworkImage.SamplePalette(empty);
            Check(IsFallback(transparent.Raw));
            Check(IsFallback(transparent.Palette));
            var splitPixels = Enumerable.Range(0, 8)
                .SelectMany(row => row < 3
                    ? new byte[] { 245, 245, 245, 255 }
                    : new byte[] { 12, 12, 12, 255 }).ToArray();
            var split = new ArtworkPixels(1, 8, splitPixels);
            var splitRaw = (SolidColorBrush)ArtworkImage.SamplePalette(split).Raw;
            var splitTop = (SolidColorBrush)ArtworkImage.SampleTopPalette(split);
            Check(splitTop.Color.R > splitRaw.Color.R + 70 && splitTop.Color.A == 255);
            var translucent = new ArtworkPixels(1, 1, new byte[] { 64, 64, 64, 64 });
            Equal((byte)64, ((SolidColorBrush)ArtworkImage.SampleTopPalette(translucent)).Color.A);
            return Task.CompletedTask;
        }));
        yield return new("Native artwork explicit local null retires inherited owner and blocks ancestor changes", () => Sta(async () =>
        {
            var inherited = new HeldLoader(); var replacement = new HeldLoader(); var local = new HeldLoader();
            var host = new Grid { Width = 100, Height = 100 };
            var scope = new Grid(); host.Children.Add(scope);
            var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/scope" };
            scope.Children.Add(view); ArtworkScope.SetLoader(host, inherited);
            using var surface = Mount(host); Layout(host); await Tick();
            Equal(1, inherited.Calls.Count);
            var first = new FakeLease(); inherited.Calls[0].Completion.SetResult(first); await Tick();
            Check(view.Source is not null);
            ArtworkScope.SetLoader(scope, null);
            Check(first.Disposed && view.Source is null && ArtworkScope.GetLoader(view) is null);
            ArtworkScope.SetLoader(host, replacement); Layout(host); await Tick();
            Equal(0, replacement.Calls.Count); Equal(1, inherited.Calls.Count);
            ArtworkScope.SetLoader(scope, local); await Tick(); Equal(1, local.Calls.Count);
            ArtworkScope.SetLoader(scope, null);
            Check(local.Calls[0].Token.IsCancellationRequested);
            var late = new FakeLease(); local.Calls[0].Completion.SetResult(late); await Tick();
            Check(late.Disposed && view.Source is null);
        }));
        foreach (string format in new[] { "png", "jpeg", "gif" })
            yield return new("Native artwork real bounded immutable raster " + format, () => Sta(async () =>
            {
                byte[] bytes = Encode(format, 800, 400); var handler = new Handler((_, _) => Task.FromResult(Reply(bytes)));
                await using var service = new ArtworkService(handler);
                using var first = await service.AcquireAsync("https://art.example.test/cover." + format);
                Equal(512, first.Image.PixelWidth); Equal(256, first.Image.PixelHeight); Check(Immutable(first.Image));
                using var second = await service.AcquireAsync("https://art.example.test/cover." + format);
                Check(ReferenceEquals(first.Image, second.Image)); Equal(1, handler.Count);
                first.Dispose(); Check(Immutable(second.Image));
            }));
        foreach (string format in new[] { "lossy", "lossless", "extended", "animated" })
            yield return new("Native artwork WebP " + format + " decodes without a system codec and retains bounded pixels", () => Sta(async () =>
            {
                // Content type and suffix are intentionally generic: format is established from bytes.
                var handler = new Handler((request, _) =>
                {
                    Check(request.Headers.Accept.Any(value => value.MediaType == "image/webp"));
                    return Task.FromResult(Reply(WebP(format)));
                });
                await using var service = new ArtworkService(handler);
                using var first = await service.AcquireAsync("https://art.example.test/cover");
                var image = first.Image;
                Equal(format == "lossy" ? 512 : 8, image.PixelWidth);
                Equal(format == "lossy" ? 256 : 4, image.PixelHeight);
                Check(Immutable(image) && Premultiplied(image));
                var pixel = new byte[4];
                Pixel(image, format == "animated" ? 3 : 1, 1).CopyTo(pixel, 0);
                if (format == "lossy")
                {
                    Check(pixel[2] > 200 && pixel[0] < 60 && pixel[1] < 40);
                    Equal((byte)255, pixel[3]);
                }
                else
                {
                    Equal((byte)128, pixel[3]);
                    if (format == "animated") Check(pixel[2] > 240 && pixel[1] < 30);
                    else Check(pixel[0] > 190 && pixel[2] < 20);
                    Pixel(image, 0, 0).CopyTo(pixel, 0);
                    Equal((byte)0, pixel[3]);
                }
                using var second = await service.AcquireAsync("https://art.example.test/cover");
                Check(ReferenceEquals(image, second.Image)); Equal(1, handler.Count);
                first.Dispose(); Check(Immutable(second.Image));
            }));
        yield return new("Native artwork WebP truncated corrupt and oversized containers fail before display", () => Sta(async () =>
        {
            byte[] truncated = WebP("lossy")[..^12];
            byte[] corrupt = WebP("lossless");
            corrupt.AsSpan(20).Clear();
            byte[] tooWide = WebP("animated");
            // VP8X canvas width is 24-bit little endian, stored minus one.
            tooWide[24] = 0x1f; tooWide[25] = 0x4e; tooWide[26] = 0; // 20000
            foreach (var (bytes, code) in new[]
            {
                (truncated, "ARTWORK_FORMAT"), (corrupt, "ARTWORK_FORMAT"),
                (WebP("tooManyPixels"), "ARTWORK_LIMIT"), (tooWide, "ARTWORK_LIMIT")
            })
            {
                await using var service = new ArtworkService(new Handler((_, _) => Task.FromResult(Reply(bytes))));
                await Code(code, () => service.AcquireAsync("https://art.example.test/invalid.webp"));
            }
        }));
        yield return new("Native artwork requests bounded NetEase thumbnails without changing stored identity or other URLs", () => Sta(async () =>
        {
            foreach (var (input, requested) in new[]
            {
                ("https://p1.music.126.net/avatar/109951100000001.jpg", "https://p1.music.126.net/avatar/109951100000001.jpg?param=512y512"),
                ("https://p2.music.163.net/cover/109951100000002.png", "https://p2.music.163.net/cover/109951100000002.png?param=512y512"),
                ("https://images.invalid/cover/109951100000002.jpg", "https://images.invalid/cover/109951100000002.jpg"),
                ("https://p1.music.126.net/obj/decoration.png", "https://p1.music.126.net/obj/decoration.png"),
                ("https://p1.music.126.net/cover/109951100000002.jpg?signature=synthetic", "https://p1.music.126.net/cover/109951100000002.jpg?signature=synthetic"),
                ("https://p1.music.126.net.evil.invalid/cover/109951100000002.jpg", "https://p1.music.126.net.evil.invalid/cover/109951100000002.jpg")
            })
            {
                int calls = 0;
                await using var service = new ArtworkService(new Handler((request, _) =>
                {
                    calls++;
                    Equal(requested, request.RequestUri!.AbsoluteUri);
                    return Task.FromResult(Reply(Encode("png", 512, 512)));
                }));
                using var first = await service.AcquireAsync(input);
                using var second = await service.AcquireAsync(input);
                Equal(512, first.Image.PixelWidth);
                Equal(1, calls); Check(ReferenceEquals(first.Image, second.Image));
            }
        }));
        yield return new("Native artwork invalid HTTP address syntax fails before transport", async () =>
        {
            var handler = new Handler((_, _) => throw new InvalidOperationException("No HTTP expected"));
            await using var service = new ArtworkService(handler);
            foreach (string uri in new[] { "", "http://", "http://:8080/x", "ftp://example.test/x",
                "file:///C:/secret.png", "data:image/png;base64,AAAA",
                "https://example.test/x#fragment", "https://example.test/\0" })
                await Code("ARTWORK_ADDRESS", () => service.AcquireAsync(uri));
            Equal(0, handler.Count);
        });
        yield return new("Native artwork ordinary HTTP host forms read and decode supplied image bytes", () => Sta(async () =>
        {
            var handler = new Handler((_, _) => Task.FromResult(Reply(Encode("png", 2, 3))));
            await using var service = new ArtworkService(handler);
            string[] addresses = ["http://169.254.169.254/x", "https://192.168.1.1/x",
                "https://[::1]/x", "https://[::ffff:127.0.0.1]/x", "http://x.local/x",
                "http://local/x", "http://x.localhost/x", "https://example.test:8080/x"];
            foreach (string address in addresses)
            {
                using var lease = await service.AcquireAsync(address);
                Equal(2, lease.Image.PixelWidth); Equal(3, lease.Image.PixelHeight); Check(Immutable(lease.Image));
            }
            Equal(addresses.Length, handler.Count); // Injected bodies, not proof of these hosts' DNS or reachability.
        }));
        foreach (string host in new[] { "127.0.0.1", "localhost" })
            yield return new("Native artwork default HTTP reads custom port and long URL from " + host,
                () => Sta(() => LocalArtworkAsync(host)));
        yield return new("Native artwork URL userinfo sends Basic on actual HTTP reads", () => Sta(ArtworkUserInfoAsync));
        foreach (string redirect in new[] { "relative", "absolute", "anonymous" })
            yield return new("Native artwork URL userinfo follows actual redirect " + redirect, () => Sta(async () =>
            {
                await using var server = new RuntimeHttpClientTests.Server();
                await using var service = new ArtworkService();
                int port = new Uri(server.BaseUrl).Port;
                var loading = service.AcquireAsync($"http://fixture%20user:p%40ss%3Aword@127.0.0.1:{port}/start");
                await using (var request = await server.AcceptAsync())
                {
                    Equal("/start", request.Path);
                    Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture user:p@ss:word")),
                        request.Headers["Authorization"]);
                    string location = redirect == "relative" ? "/poster.png" : redirect == "absolute" ?
                        $"http://redirect-user:redirect-pass@127.0.0.1:{port}/poster.png" : server.BaseUrl + "/poster.png";
                    await request.ReplyAsync([], 302, extraHeaders: $"Location: {location}\r\n");
                }
                await using (var request = await server.AcceptAsync())
                {
                    Equal("/poster.png", request.Path);
                    if (redirect == "anonymous") Check(!request.Headers.ContainsKey("Authorization"));
                    else Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(redirect == "relative" ?
                        "fixture user:p@ss:word" : "redirect-user:redirect-pass")), request.Headers["Authorization"]);
                    await request.ReplyAsync(Encode("png", 2, 3), contentType: "image/png");
                }
                using var lease = await loading;
                Equal(2, lease.Image.PixelWidth); Equal(3, lease.Image.PixelHeight); Check(Immutable(lease.Image));
            }));
        yield return new("Native artwork actual HTTPS to HTTP redirect reads and decodes the target", () => Sta(ArtworkDowngradeAsync));
        foreach (int status in new[] { 401, 403 })
            yield return new("Native artwork actual upstream status remains denied " + status, () => Sta(async () =>
            {
                await using var server = new RuntimeHttpClientTests.Server();
                await using var service = new ArtworkService();
                var loading = service.AcquireAsync(server.BaseUrl + "/denied");
                await using var request = await server.AcceptAsync();
                Equal("GET", request.Method);
                await request.ReplyAsync([], status, contentType: "image/png");
                await Code("ARTWORK_DENIED", () => loading);
            }));
        yield return new("Native artwork redirects have no credential cookie proxy or referrer authority", () => Sta(async () =>
        {
            var handler = new Handler((request, _) =>
            {
                foreach (string name in new[] { "Cookie", "Authorization", "Proxy-Authorization", "Referer" })
                    Check(!request.Headers.Contains(name));
                if (request.RequestUri!.Host == "first.example.test")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri("https://second.example.test/cover.png");
                    redirect.Headers.TryAddWithoutValidation("Set-Cookie", "synthetic=never-forward");
                    return Task.FromResult(redirect);
                }
                return Task.FromResult(Reply(Encode("png", 2, 3)));
            });
            await using var service = new ArtworkService(handler);
            using var lease = await service.AcquireAsync("https://first.example.test/cover.png");
            Equal(2, handler.Count); Equal(3, lease.Image.PixelHeight);
        }));
        yield return new("Native artwork cyclic redirects fail without another request", async () =>
        {
            var handler = new Handler((_, _) => { var reply = new HttpResponseMessage(HttpStatusCode.Redirect);
                reply.Headers.Location = new Uri("https://first.example.test/x"); return Task.FromResult(reply); });
            await using var service = new ArtworkService(handler);
            await Code("ARTWORK_REDIRECT", () => service.AcquireAsync("https://first.example.test/x"));
            Equal(1, handler.Count);
        });
        yield return new("Native artwork missing denied and malformed responses never become cache hits", () => Sta(async () =>
        {
            int round = 0;
            var handler = new Handler((_, _) => Task.FromResult(++round switch {
                1 => new HttpResponseMessage(HttpStatusCode.Forbidden),
                2 => Reply("<svg><image href='file:///secret'/></svg>"u8.ToArray()), _ => Reply(Encode("png", 1, 1)) }));
            await using var service = new ArtworkService(handler);
            await Code("ARTWORK_DENIED", () => service.AcquireAsync("https://art.example.test/x"));
            await Code("ARTWORK_FORMAT", () => service.AcquireAsync("https://art.example.test/x"));
            using var good = await service.AcquireAsync("https://art.example.test/x"); Equal(3, handler.Count);
        }));
        yield return new("Native artwork encoded dimensions and response completeness are enforced before display", () => Sta(async () =>
        {
            byte[] wide = Encode("png", 1, 1); BinaryPrimitives.WriteUInt32BigEndian(wide.AsSpan(16), 20000);
            int round = 0;
            var handler = new Handler((_, _) => {
                var response = Reply(++round == 1 ? wide : new byte[1]);
                if (round == 2) response.Content.Headers.ContentLength = ArtworkService.MaximumCacheEntryBytes + 1L;
                return Task.FromResult(response); });
            await using var service = new ArtworkService(handler);
            await Code("ARTWORK_LIMIT", () => service.AcquireAsync("https://art.example.test/first"));
            await Code("ARTWORK_FORMAT", () => service.AcquireAsync("https://art.example.test/second"));
        }));
        foreach (bool chunked in new[] { false, true })
            foreach (bool domainCache in new[] { false, true })
                yield return new($"Native artwork large complete HTTP image decodes without encoded caching chunked={chunked} domain={domainCache}", () => Sta(async () =>
                {
                    byte[] large = LargePng(), small = Encode("png", 2, 3);
                    string directory = TestWorkspace.DataPath("artwork-large-" + Guid.NewGuid().ToString("N"));
                    var stored = domainCache ? new RecordingByteCache() : null;
                    try
                    {
                        await using var server = new RuntimeHttpClientTests.Server();
                        string address = server.BaseUrl + "/large.png";
                        foreach (byte[] body in new[] { large, small })
                        {
                            await using var service = new ArtworkService(byteCache: stored, cacheDirectory: directory);
                            var loading = service.AcquireAsync(address);
                            await using (var exchange = await server.AcceptAsync())
                            {
                                if (chunked) await exchange.ReplyChunkedAsync(body);
                                else await exchange.ReplyAsync(body, contentType: "image/png");
                            }
                            using var image = await loading;
                            Equal(ReferenceEquals(body, large) ? 512 : 2, image.Image.PixelWidth);
                            Check(Immutable(image.Image));
                            using var hit = await service.AcquireAsync(address);
                            Check(ReferenceEquals(image.Image, hit.Image));
                            Check(!server.HasConnection);
                            int expected = ReferenceEquals(body, large) ? 0 : 1;
                            if (stored is null && expected == 1)
                                await WaitFor(() => Directory.GetFiles(directory, "*.resource").Length == expected,
                                    "Cache-admissible bytes did not finish persistence before shutdown");
                            await service.DisposeAsync();
                            if (stored is not null) Equal(expected, stored.Writes);
                            else Equal(expected, Directory.GetFiles(directory, "*.resource").Length);
                        }
                        byte[] saved = stored?.Content ?? File.ReadAllBytes(Directory.GetFiles(directory, "*.resource").Single());
                        Check(saved.SequenceEqual(small), "Only the cache-admissible original bytes are persisted");
                    }
                    finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                }));
        yield return new("Native artwork oversized cached bytes are removed before fetching a valid replacement", () => Sta(async () =>
        {
            var stored = new RecordingByteCache { Content = LargePng() };
            byte[] oversized = stored.Content, replacement = Encode("png", 2, 3);
            var handler = new Handler((_, _) => Task.FromResult(Reply(replacement)));
            await using var service = new ArtworkService(handler, byteCache: stored);
            using var image = await service.AcquireAsync("https://art.example.test/cached");
            Equal(1, handler.Count); Equal(1, stored.Removals); Equal(1, stored.Writes);
            Equal(2, image.Image.PixelWidth);
            Check(oversized.All(value => value == 0));
            Check(stored.Content!.SequenceEqual(replacement));
        }));
        yield return new("Native artwork default HTTP accepts response headers beyond the old explicit quota", () => Sta(async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var service = new ArtworkService();
            var loading = service.AcquireAsync(server.BaseUrl + "/headers.png");
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync(Encode("png", 2, 3), contentType: "image/png",
                    extraHeaders: "X-Image-Metadata: " + new string('x', 33 * 1024) + "\r\n");
            using var image = await loading;
            Equal(2, image.Image.PixelWidth);
        }));
        yield return new("Native artwork disk admission retains the exact per-entry quota and cancellation", async () =>
        {
            string directory = TestWorkspace.DataPath("artwork-admission-" + Guid.NewGuid().ToString("N"));
            byte[] admitted = new byte[ArtworkService.MaximumCacheEntryBytes];
            new Random(19).NextBytes(admitted);
            try
            {
                using (var disk = new ArtworkDiskCache(directory))
                {
                    await disk.WriteAsync("accepted", admitted, default);
                    await disk.WriteAsync("oversized", new byte[admitted.Length + 1], default);
                    Check((await disk.ReadAsync("accepted", default))!.SequenceEqual(admitted));
                    Check(await disk.ReadAsync("oversized", default) is null);
                    Equal(1, Directory.GetFiles(directory, "*.resource").Length);
                    using var cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    try
                    {
                        await disk.WriteAsync("cancelled", new byte[admitted.Length + 1], cancelled.Token);
                        throw new InvalidOperationException("Expected cancellation");
                    }
                    catch (OperationCanceledException) { }
                }
                using var reopened = new ArtworkDiskCache(directory);
                Check((await reopened.ReadAsync("accepted", default))!.SequenceEqual(admitted));
                Check(await reopened.ReadAsync("oversized", default) is null);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        });
        yield return new("Native artwork cancellation and disposal drain native HTTP instead of keeping a background fetch", async () =>
        {
            var entered = Signal(); var ended = Signal();
            var handler = new Handler(async (_, ct) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); }
                finally { ended.TrySetResult(); }
            });
            var service = new ArtworkService(handler);
            var load = service.AcquireAsync("https://art.example.test/x"); await entered.Task;
            await service.DisposeAsync(); Check(ended.Task.IsCompleted); Check(handler.Disposed);
            try { using var unexpected = await load; throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            await Code("ARTWORK_CLOSED", () => service.AcquireAsync("https://art.example.test/x"));
            await service.DisposeAsync();
        });
        yield return new("Native artwork eighty distinct URLs share four execution slots across hosts and trusted reads", () => Sta(async () =>
        {
            var arrived = Signal(); var release = Signal(); int active = 0, maximum = 0, allocated = 0;
            byte[] png = Encode("png", 1, 1);
            async Task<byte[]> Read(CancellationToken token)
            {
                int count = Interlocked.Increment(ref active);
                int prior;
                do { prior = Volatile.Read(ref maximum); }
                while (count > prior && Interlocked.CompareExchange(ref maximum, count, prior) != prior);
                if (count == 4) arrived.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(token);
                    Interlocked.Increment(ref allocated);
                    return png.ToArray();
                }
                finally { Interlocked.Decrement(ref active); }
            }
            var handler = new Handler(async (_, token) => Reply(await Read(token)));
            await using var service = new ArtworkService(handler, readTrusted: (_, token) => Read(token));
            var first = Enumerable.Range(0, 4).Select(i => service.AcquireAsync("https://busy.example.test/" + i)).ToArray();
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queued = Enumerable.Range(4, 76).Select(i => service.AcquireAsync(i % 2 == 0
                    ? "https://other.example.test/" + i : "/trusted/" + i)).ToArray();
                await Tick();
                Equal(4, Volatile.Read(ref active)); Equal(4, handler.Count);
                Equal(0, Volatile.Read(ref allocated));
                Check(queued.All(work => !work.IsCompleted), "queued demands cannot read or allocate image responses");
                release.TrySetResult();
                foreach (var lease in await Task.WhenAll(first.Concat(queued)).WaitAsync(TimeSpan.FromSeconds(10)))
                { Check(Immutable(lease.Image)); lease.Dispose(); }
                Equal(4, maximum); Equal(80, allocated); Equal(0, active);
            }
            finally { release.TrySetResult(); }
        }));
        yield return new("Native artwork byte reads and physical writes retain four slots until actual completion", () => Sta(async () =>
        {
            var store = new HeldArtworkBytes();
            byte[] png = Encode("png", 2, 1);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler, byteCache: store);
            var first = Enumerable.Range(0, 4).Select(i => service.AcquireAsync("https://art.example.test/write/" + i)).ToArray();
            try
            {
                await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queued = Enumerable.Range(4, 16).Select(i => service.AcquireAsync("https://art.example.test/write/" + i)).ToArray();
                await Tick();
                Equal(4, store.Reads); Equal(4, store.Writes); Equal(4, handler.Count);
                Check(first.Concat(queued).All(work => !work.IsCompleted), "decoded images cannot bypass unfinished physical writes");
                store.Release.TrySetResult();
                foreach (var lease in await Task.WhenAll(first.Concat(queued)).WaitAsync(TimeSpan.FromSeconds(10)))
                { Equal(2, lease.Image.PixelWidth); lease.Dispose(); }
                Equal(20, store.Reads); Equal(20, store.Ended); Equal(4, store.Maximum);
                Check(store.ContentCleared, "encoded bytes are cleared after the physical writer returns");
            }
            finally { store.Release.TrySetResult(); }
        }));
        yield return new("Native artwork a shared physical write survives one consumer cancellation", () => Sta(async () =>
        {
            var store = new HeldArtworkBytes();
            byte[] png = Encode("png", 2, 1);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler, byteCache: store);
            using var departed = new CancellationTokenSource();
            const string address = "https://art.example.test/shared-write";
            var first = service.AcquireAsync(address, departed.Token);
            try
            {
                await store.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second = service.AcquireAsync(address);
                departed.Cancel();
                await Cancelled(first);
                Check(!store.Cancelled.Task.IsCompleted && !second.IsCompleted && !store.ContentCleared);
                Equal(1, store.Reads); Equal(1, store.Writes); Equal(1, handler.Count);
                store.Release.TrySetResult();
                using var visible = await second.WaitAsync(TimeSpan.FromSeconds(5));
                using var cached = await service.AcquireAsync(address);
                Check(ReferenceEquals(visible.Image, cached.Image) && store.ContentCleared);
                Equal(1, store.Ended); Equal(1, handler.Count);
            }
            finally { store.Release.TrySetResult(); }
        }));
        yield return new("Native artwork same-key demand waits for cancelled or cleared physical writes before one successor", () => Sta(async () =>
        {
            foreach (bool clear in new[] { false, true })
            {
                var store = new HeldArtworkBytes();
                byte[] png = Encode("png", 2, 1);
                var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
                await using var service = new ArtworkService(handler, byteCache: store);
                using var departed = new CancellationTokenSource();
                const string address = "https://art.example.test/retired-write";
                var first = service.AcquireAsync(address, departed.Token);
                IArtworkLease? old = null;
                try
                {
                    await store.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (clear)
                    {
                        service.ClearMemory();
                        Interlocked.Increment(ref store.CurrentEpoch);
                    }
                    else
                    {
                        departed.Cancel();
                        await Cancelled(first);
                        await store.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    var successor = service.AcquireAsync(address);
                    var anotherPage = service.AcquireAsync(address);
                    await Tick();
                    Equal(1, store.Reads); Equal(1, store.Writes); Equal(0, store.Ended); Equal(1, handler.Count);
                    Check(!successor.IsCompleted && !anotherPage.IsCompleted && !store.ContentCleared);
                    store.Release.TrySetResult();
                    if (clear) old = await first.WaitAsync(TimeSpan.FromSeconds(5));
                    using var fresh = await successor.WaitAsync(TimeSpan.FromSeconds(5));
                    using var shared = await anotherPage.WaitAsync(TimeSpan.FromSeconds(5));
                    using var cached = await service.AcquireAsync(address);
                    Check(ReferenceEquals(fresh.Image, shared.Image) && ReferenceEquals(fresh.Image, cached.Image));
                    if (old is not null) Check(Immutable(old.Image) && !ReferenceEquals(old.Image, fresh.Image));
                    Equal(2, store.Reads); Equal(2, store.Writes); Equal(2, store.Ended); Equal(2, handler.Count);
                    Equal(1, store.Maximum); Equal(clear ? 1L : 0L, store.LastWriteEpoch);
                    Check(store.ContentCleared);
                }
                finally { store.Release.TrySetResult(); old?.Dispose(); }
            }
        }));
        yield return new("Native artwork unrelated persistence cancellation remains a recoverable miss after successful decoding", () => Sta(async () =>
        {
            foreach (bool corrupt in new[] { false, true })
            {
                var store = new CancelledArtworkBytes(corrupt);
                byte[] png = Encode("png", 2, 1);
                var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
                await using var service = new ArtworkService(handler, byteCache: store);
                using var decoded = await service.AcquireAsync("https://art.example.test/persistence-cancel");
                using var shared = await service.AcquireAsync("https://art.example.test/persistence-cancel");
                Check(Immutable(decoded.Image) && ReferenceEquals(decoded.Image, shared.Image));
                Equal(2, decoded.Image.PixelWidth); Equal(1, handler.Count);
                Equal(1, store.Reads); Equal(corrupt ? 1 : 0, store.Removals); Equal(1, store.Writes);
            }
        }));
        yield return new("Native artwork unrelated reader cancellation is not reported as a request timeout", () => Sta(async () =>
        {
            foreach (bool trusted in new[] { false, true })
            {
                int reads = 0;
                byte[] png = Encode("png", 2, 1);
                Task<byte[]> Read(CancellationToken token)
                {
                    Check(!token.IsCancellationRequested);
                    return ++reads == 1 ? Task.FromCanceled<byte[]>(new CancellationToken(true)) : Task.FromResult(png.ToArray());
                }
                var handler = new Handler(async (_, token) => Reply(await Read(token)));
                await using var service = new ArtworkService(handler, readTrusted: (_, token) => Read(token));
                string address = trusted ? "/trusted/independent-cancel" : "https://art.example.test/independent-cancel";
                await Code("ARTWORK_FAILED", () => service.AcquireAsync(address));
                using var ready = await service.AcquireAsync(address);
                Check(Immutable(ready.Image)); Equal(2, reads); Equal(trusted ? 0 : 2, handler.Count);
            }
        }));
        yield return new("Native artwork queued subscribers cancel independently without reading or blocking same-key retry", () => Sta(async () =>
        {
            var store = new HeldArtworkBytes();
            byte[] png = Encode("png", 1, 1);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler, byteCache: store);
            var active = Enumerable.Range(0, 4).Select(i => service.AcquireAsync("https://art.example.test/active/" + i)).ToArray();
            using var departed = new CancellationTokenSource();
            using var abandoned = new CancellationTokenSource();
            try
            {
                await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                const string shared = "https://art.example.test/queued-shared";
                const string retry = "https://art.example.test/queued-retry";
                var first = service.AcquireAsync(shared, departed.Token);
                var second = service.AcquireAsync(shared);
                var anotherPage = service.AcquireAsync(shared);
                var cancelled = service.AcquireAsync(retry, abandoned.Token);
                departed.Cancel(); abandoned.Cancel();
                await Cancelled(first); await Cancelled(cancelled);
                var successor = service.AcquireAsync(retry);
                await Tick();
                Equal(4, store.Reads); Equal(4, handler.Count);
                Check(!second.IsCompleted && !successor.IsCompleted);
                store.Release.TrySetResult();
                var leases = await Task.WhenAll(active.Concat([second, anotherPage, successor])).WaitAsync(TimeSpan.FromSeconds(10));
                try { Check(ReferenceEquals(leases[4].Image, leases[5].Image)); }
                finally { foreach (var lease in leases) lease.Dispose(); }
                Equal(6, store.Reads); Equal(6, handler.Count); Equal(6, store.Writes);
                using var sharedAgain = await service.AcquireAsync(shared);
                Equal(6, handler.Count);
            }
            finally { store.Release.TrySetResult(); }
        }));
        yield return new("Native artwork last-consumer cancellation retains slots and close drains queued reads and physical writers", () => Sta(async () =>
        {
            var store = new HeldArtworkBytes();
            byte[] png = Encode("png", 1, 1);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler, byteCache: store);
            using var departed = new CancellationTokenSource();
            var active = Enumerable.Range(0, 4).Select(i => service.AcquireAsync("https://art.example.test/closing/" + i, departed.Token)).ToArray();
            try
            {
                await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queued = Enumerable.Range(4, 12).Select(i => service.AcquireAsync("https://art.example.test/closing/" + i)).ToArray();
                departed.Cancel();
                foreach (var work in active) await Cancelled(work);
                await store.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Tick();
                Equal(4, store.Reads); Equal(0, store.Ended);
                Check(queued.All(work => !work.IsCompleted) && !store.ContentCleared,
                    "consumer cancellation cannot return a physically occupied slot or clear its input bytes");
                var close = service.DisposeAsync().AsTask();
                var secondClose = service.DisposeAsync().AsTask();
                foreach (var work in queued) await Cancelled(work);
                Equal(4, store.Reads); Equal(4, store.Writes); Equal(4, handler.Count); Equal(0, store.Ended);
                Check(!close.IsCompleted && !secondClose.IsCompleted && !handler.Disposed);
                store.Release.TrySetResult();
                await Task.WhenAll(close, secondClose).WaitAsync(TimeSpan.FromSeconds(5));
                Equal(4, store.Ended); Check(handler.Disposed && store.ContentCleared);
            }
            finally { store.Release.TrySetResult(); }
        }));
        yield return new("Native artwork queued waiting does not consume the thirty-second request timeout", () => Sta(async () =>
        {
            var store = new HeldArtworkBytes();
            byte[] png = Encode("png", 1, 1);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler, byteCache: store);
            var active = Enumerable.Range(0, 4).Select(i => service.AcquireAsync("https://art.example.test/timeout/" + i)).ToArray();
            try
            {
                await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queued = service.AcquireAsync("https://art.example.test/after-timeout");
                // Exercise the actual request timer, not a shortened test-only timeout.
                await Task.Delay(TimeSpan.FromSeconds(31));
                await store.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(4, store.Reads); Check(!queued.IsCompleted);
                store.Release.TrySetResult();
                foreach (var work in active) await Code("ARTWORK_TIMEOUT", async () => { using var lease = await work; });
                using var ready = await queued.WaitAsync(TimeSpan.FromSeconds(5));
                Check(Immutable(ready.Image)); Equal(5, store.Reads); Equal(5, handler.Count);
            }
            finally { store.Release.TrySetResult(); }
        }));
        yield return new("Native artwork close drains a response body that ignores cancellation", async () =>
        {
            var body = new HeldReadStream();
            var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(body) }));
            await using var service = new ArtworkService(handler);
            using var cancellation = new CancellationTokenSource();
            var read = service.AcquireAsync("https://art.example.test/body", cancellation.Token);
            await body.Entered.Task;
            cancellation.Cancel();
            try { using var unexpected = await read; throw new InvalidOperationException("Expected consumer cancellation"); }
            catch (OperationCanceledException) { }
            await body.Cancelled.Task;
            var close = service.DisposeAsync().AsTask();
            var secondClose = service.DisposeAsync().AsTask();
            try { Check(!close.IsCompleted && !secondClose.IsCompleted && !handler.Disposed && !body.Disposed); }
            finally { body.Release.TrySetResult(); }
            await Task.WhenAll(close, secondClose);
            Check(body.Disposed && handler.Disposed);
        });
        yield return new("Native artwork shared URL survives one consumer cancellation", () => Sta(async () =>
        {
            var entered = Signal(); var cancelled = Signal();
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler(async (_, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); return await response.Task;
            });
            await using var service = new ArtworkService(handler);
            using var firstCancellation = new CancellationTokenSource();
            var first = service.AcquireAsync("https://art.example.test/shared", firstCancellation.Token);
            await entered.Task;
            var second = service.AcquireAsync("https://art.example.test/shared");
            firstCancellation.Cancel();
            try { using var unexpected = await first; throw new InvalidOperationException("Expected consumer cancellation"); }
            catch (OperationCanceledException) { }
            Check(!cancelled.Task.IsCompleted); Equal(1, handler.Count);
            response.SetResult(Reply(Encode("png", 3, 2)));
            using var visible = await second;
            using var cached = await service.AcquireAsync("https://art.example.test/shared");
            Check(ReferenceEquals(visible.Image, cached.Image)); Equal(1, handler.Count);
        }));
        yield return new("Native artwork last consumer cancels old flight and close awaits its physical completion", () => Sta(async () =>
        {
            var entered = Signal(); var cancelled = Signal(); int calls = 0;
            var closingEntered = Signal();
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] current = Encode("png", 3, 2), stale = Encode("png", 1, 1);
            var handler = new Handler(async (_, token) =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call == 2) return Reply(current);
                using var registration = token.Register(() => cancelled.TrySetResult());
                if (call > 2)
                {
                    closingEntered.TrySetResult();
                    return await closingResponse.Task;
                }
                entered.TrySetResult();
                return await response.Task; // Deliberately ignores physical cancellation.
            });
            await using var service = new ArtworkService(handler);
            using var cancellation = new CancellationTokenSource();
            var first = service.AcquireAsync("https://art.example.test/shared", cancellation.Token);
            try
            {
                await entered.Task; cancellation.Cancel();
                try { using var unexpected = await first; throw new InvalidOperationException("Expected consumer cancellation"); }
                catch (OperationCanceledException) { }
                await cancelled.Task;
                var replacing = service.AcquireAsync("https://art.example.test/shared");
                await Tick();
                Equal(1, handler.Count); Check(!replacing.IsCompleted);
                response.SetResult(Reply(stale));
                using var replacement = await replacing;
                using var cached = await service.AcquireAsync("https://art.example.test/shared");
                Equal(2, handler.Count); Equal(3, replacement.Image.PixelWidth);
                Check(ReferenceEquals(replacement.Image, cached.Image));
                var unfinished = service.AcquireAsync("https://art.example.test/closing");
                await closingEntered.Task;
                var close = service.DisposeAsync().AsTask();
                Check(!close.IsCompleted && !handler.Disposed);
                closingResponse.SetResult(Reply(stale));
                await close;
                try { using var unexpected = await unfinished; throw new InvalidOperationException("Expected close cancellation"); }
                catch (OperationCanceledException) { }
                Check(handler.Disposed); Equal(3, replacement.Image.PixelWidth);
            }
            finally { response.TrySetResult(Reply(stale)); closingResponse.TrySetResult(Reply(stale)); }
        }));
        yield return new("Native artwork cache eviction preserves pinned visuals and admits fresh images", () => Sta(async () =>
        {
            byte[] png = Encode("png", 512, 512);
            int capacity = ArtworkService.CacheBytes / (512 * 512 * 8);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler);
            var leases = new List<IArtworkLease>();
            try
            {
                for (int i = 0; i < capacity; i++) leases.Add(await service.AcquireAsync("https://art.example.test/" + i));
                using var replacement = await service.AcquireAsync("https://art.example.test/overflow");
                Check(Immutable(leases[0].Image) && Immutable(leases[1].Image) && Immutable(replacement.Image));
                var original = leases[0].Image;
                var reused = await service.AcquireAsync("https://art.example.test/0");
                Check(ReferenceEquals(original, reused.Image)); Equal(capacity + 1, handler.Count);
                leases[0].Dispose(); leases[0].Dispose();
                using (var shared = await service.AcquireAsync("https://art.example.test/0"))
                {
                    Check(ReferenceEquals(original, shared.Image)); Equal(capacity + 1, handler.Count);
                }
                reused.Dispose(); reused.Dispose();
                using var fresh = await service.AcquireAsync("https://art.example.test/0");
                Check(!ReferenceEquals(original, fresh.Image)); Equal(capacity + 2, handler.Count);
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }));
        yield return new("Native artwork small images use byte capacity rather than a fixed entry count", () => Sta(async () =>
        {
            byte[] png = Encode("png", 1, 1);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler);
            for (int i = 0; i < 128; i++) { using var image = await service.AcquireAsync("https://art.example.test/" + i); }
            for (int i = 0; i < 128; i++) { using var image = await service.AcquireAsync("https://art.example.test/" + i); }
            Equal(128, handler.Count);
        }));
        yield return new("Native artwork hidden retained page restores identical pixels and palettes without HTTP", () => Sta(async () =>
        {
            var encoder = new PngBitmapEncoder();
            using var sample = new FakeLease();
            var colors = new byte[4]; sample.Image.CopyPixels(colors);
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, colors, 4)));
            using var encoded = new MemoryStream(); encoder.Save(encoded);
            byte[] png = encoded.ToArray();
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler);
            var host = new Grid { Width = 100, Height = 100 };
            var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/retained" };
            ArtworkScope.SetLoader(host, service); host.Children.Add(view);
            using var surface = Mount(host); Layout(host);
            using (await view.PrepareForPresentationAsync()) { }
            var pixels = ((Image)view.Children[0]).Source;
            var raw = view.RawPaletteBrush; var palette = view.PaletteBrush; var top = view.TopPaletteBrush;
            Check(pixels is not null && ReferenceEquals(pixels, view.Source)); Equal(1, handler.Count); Check(ObservesLayout(view));
            Check(view.HasTopPalette && !IsFallback(raw));
            foreach (var visibility in new[] { Visibility.Collapsed, Visibility.Collapsed })
            {
                host.Visibility = visibility; Layout(host); await Tick();
                Check(!ObservesLayout(view));
                Check(ReferenceEquals(pixels, ((Image)view.Children[0]).Source) && ReferenceEquals(pixels, view.Source));
                host.Visibility = Visibility.Visible; Layout(host); await Tick();
                Check(ObservesLayout(view)); Equal(1, handler.Count);
                Check(ReferenceEquals(pixels, ((Image)view.Children[0]).Source));
                Check(ReferenceEquals(raw, view.RawPaletteBrush));
                Check(ReferenceEquals(palette, view.PaletteBrush));
                Check(ReferenceEquals(top, view.TopPaletteBrush));
            }
            host.Children.Remove(view); await Tick();
            Check(!ObservesLayout(view)); Check(((Image)view.Children[0]).Source is null && view.Source is null);
            Check(!view.HasTopPalette);
        }));
        yield return new("Native artwork leaving a visible viewport releases its lease and reuses the bounded cache", () => Sta(async () =>
        {
            byte[] png = Encode("png", 2, 2);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler);
            var host = new Canvas { Width = 100, Height = 100, Clip = new RectangleGeometry { Rect = new(0, 0, 100, 100) } };
            var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/viewport" };
            ArtworkScope.SetLoader(host, service);
            host.Children.Add(view);
            using var surface = Mount(host);
            Layout(host);
            await WaitFor(() => ((Image)view.Children[0]).Source is not null);
            var pixels = ((Image)view.Children[0]).Source;
            Check(pixels is not null);
            Equal(1, handler.Count);

            Canvas.SetTop(view, 200);
            Layout(host);
            await Tick();
            Check(((Image)view.Children[0]).Source is null, "leaving the clipped viewport releases the image");
            Check(IsFallback(view.RawPaletteBrush), "leaving the viewport clears its palette");

            Canvas.SetTop(view, 0);
            Layout(host);
            await WaitFor(() => ReferenceEquals(pixels, ((Image)view.Children[0]).Source),
                "returning to the viewport must reuse the cached decoded image");
            Equal(1, handler.Count);
        }));
        yield return new("Native artwork close preserves external leases and forbids further acquisition", () => Sta(async () =>
        {
            byte[] png = Encode("png", 1, 1);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler);
            var first = service.AcquireAsync("https://art.example.test/shared");
            var second = service.AcquireAsync("https://art.example.test/shared");
            using var a = await first; using var b = await second;
            Check(ReferenceEquals(a.Image, b.Image)); Equal(1, handler.Count);
            await service.DisposeAsync();
            Check(handler.Disposed && Immutable(a.Image) && Immutable(b.Image));
            await Code("ARTWORK_CLOSED", () => service.AcquireAsync("https://art.example.test/shared"));
            a.Dispose(); a.Dispose(); Check(Immutable(b.Image));
        }));
        yield return new("Native artwork presentation prepares outside the viewport and retains pixels until retirement", () => Sta(async () =>
        {
            var loader = new HeldLoader();
            var host = new Canvas { Width = 100, Height = 100, Clip = new RectangleGeometry { Rect = new(0, 0, 100, 100) } };
            var view = new ArtworkImage { Width = 40, Height = 40, Address = "https://art.example.test/prepared" };
            ArtworkScope.SetLoader(host, loader); Canvas.SetTop(view, 200); host.Children.Add(view);
            using var surface = Mount(host); Layout(host); await Tick();
            Equal(0, loader.Calls.Count);
            var preparing = view.PrepareForPresentationAsync();
            Equal(1, loader.Calls.Count); Check(!preparing.IsCompleted);
            var lease = new FakeLease(); loader.Calls[0].Completion.SetResult(lease);
            using var prepared = await preparing;
            Check(ReferenceEquals(lease.Image.GetSource(DispatcherQueue.GetForCurrentThread()), ((Image)view.Children[0]).Source));
            Layout(host); await Tick(); Check(!lease.Disposed); Equal(1, loader.Calls.Count);
            Canvas.SetTop(view, 0); Layout(host); await Tick();
            Canvas.SetTop(view, 200); Layout(host); await Tick();
            Check(!lease.Disposed); Equal(1, loader.Calls.Count);
            host.Children.Remove(view); await Tick();
            Check(lease.Disposed); Check(((Image)view.Children[0]).Source is null);
        }));
        yield return new("Native artwork preparation follows replacement without waiting for a cancelled old fetch", () => Sta(async () =>
        {
            var loader = new HeldLoader(); var host = new Grid { Width = 100, Height = 100, Visibility = Visibility.Collapsed };
            var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/first" };
            ArtworkScope.SetLoader(host, loader); host.Children.Add(view); using var surface = Mount(host); Layout(host);
            var preparing = view.PrepareForPresentationAsync(); Equal(1, loader.Calls.Count);
            view.Address = "https://art.example.test/second"; Equal(2, loader.Calls.Count);
            Check(loader.Calls[0].Token.IsCancellationRequested);
            var next = new FakeLease(); loader.Calls[1].Completion.SetResult(next);
            var prepared = await preparing.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!loader.Calls[0].Completion.Task.IsCompleted);
            Check(ReferenceEquals(next.Image.GetSource(DispatcherQueue.GetForCurrentThread()), ((Image)view.Children[0]).Source));
            var old = new FakeLease(); loader.Calls[0].Completion.SetResult(old); await Tick(); Check(old.Disposed);
            prepared.Dispose(); Check(next.Disposed); Check(((Image)view.Children[0]).Source is null);
            using var cancellation = new CancellationTokenSource();
            var cancelled = view.PrepareForPresentationAsync(cancellation.Token); Equal(3, loader.Calls.Count);
            cancellation.Cancel();
            try { using var unexpected = await cancelled.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Expected cancelled preparation"); }
            catch (OperationCanceledException) { }
            Check(loader.Calls[2].Token.IsCancellationRequested);
            var late = new FakeLease(); loader.Calls[2].Completion.SetResult(late); await Tick();
            Check(late.Disposed); Check(((Image)view.Children[0]).Source is null);
        }));
        yield return new("Native artwork prepared failure is render ready and retries only on explicit action", () => Sta(async () =>
        {
            var loader = new HeldLoader(); var host = new Grid { Width = 100, Height = 100, Visibility = Visibility.Collapsed };
            var view = new ArtworkImage
            {
                Width = 60, Height = 60, Address = "https://art.example.test/failed",
                ShowRetry = false
            };
            ArtworkScope.SetLoader(host, loader); host.Children.Add(view); using var surface = Mount(host); Layout(host);
            var preparing = view.PrepareForPresentationAsync(); Equal(1, loader.Calls.Count);
            loader.Calls[0].Completion.SetException(new ArtworkException("ARTWORK_NETWORK"));
            using var prepared = await preparing;
            var retry = (Button)view.Children[1];
            Equal(Visibility.Collapsed, retry.Visibility);
            Check(!view.HasTopPalette);
            view.ShowRetry = true;
            Equal(Visibility.Visible, retry.Visibility);
            Layout(host); await Tick(); Equal(1, loader.Calls.Count);
            DesktopTestUi.Invoke(retry); await Tick(); Equal(2, loader.Calls.Count);
            var lease = new FakeLease(); loader.Calls[1].Completion.SetResult(lease); await Tick();
            Check(ReferenceEquals(lease.Image.GetSource(DispatcherQueue.GetForCurrentThread()), ((Image)view.Children[0]).Source));
            Check(view.HasTopPalette);
            prepared.Dispose(); Check(lease.Disposed);
        }));
        yield return new("Native artwork view has no implicit loader and rejects late old-image completion", () => Sta(async () =>
        {
            var host = new Grid { Width = 200, Height = 200 }; var view = new ArtworkImage { Width = 100, Height = 100, Address = "https://art.example.test/first" };
            host.Children.Add(view);
            using var surface = Mount(host);
            Layout(host); await Tick();
            Check(((Image)view.Children[0]).Source is null);
            var loader = new HeldLoader(); ArtworkScope.SetLoader(host, loader); Layout(host); await Tick();
            Equal(1, loader.Calls.Count);
            view.Address = "https://art.example.test/second"; await Tick(); Equal(2, loader.Calls.Count); Check(loader.Calls[0].Token.IsCancellationRequested);
            var old = new FakeLease();
            var next = new FakeLease();
            loader.Calls[0].Completion.SetResult(old); loader.Calls[1].Completion.SetResult(next); await Tick();
            Check(old.Disposed); Check(!next.Disposed);
            Check(ReferenceEquals(next.Image.GetSource(DispatcherQueue.GetForCurrentThread()), ((Image)view.Children[0]).Source) && ReferenceEquals(next.Image.GetSource(DispatcherQueue.GetForCurrentThread()), view.Source));
            Check(!IsFallback(view.RawPaletteBrush));
            Check(!IsFallback(view.PaletteBrush));
            Check(!IsFallback(view.TopPaletteBrush));
            Check(view.HasTopPalette);
            host.Children.Remove(view); await Tick();
            Equal(1, next.DisposeCount); Check(((Image)view.Children[0]).Source is null && view.Source is null);
            Check(IsFallback(view.RawPaletteBrush));
            Check(IsFallback(view.PaletteBrush));
            Check(IsFallback(view.TopPaletteBrush));
            Check(!view.HasTopPalette);
        }));
        yield return new("Native artwork hidden views retain pixels and unload rejects late replacement", () => Sta(async () =>
        {
            var loader = new HeldLoader(); var host = new Canvas { Width = 100, Height = 100, Clip = new RectangleGeometry { Rect = new(0, 0, 100, 100) } };
            var view = new ArtworkImage { Width = 40, Height = 40, Address = "https://art.example.test/a" };
            ArtworkScope.SetLoader(host, loader); Canvas.SetTop(view, 200); host.Children.Add(view);
            using var surface = Mount(host);
            Layout(host);
            await Tick(); await Tick(); Equal(0, loader.Calls.Count);
            Canvas.SetTop(view, 0); Layout(host); await Tick(); Equal(1, loader.Calls.Count);
            var lease = new FakeLease(); loader.Calls[0].Completion.SetResult(lease); await Tick(); Check(!lease.Disposed);
            view.Visibility = Visibility.Collapsed; await Tick(); Check(!lease.Disposed);
            Check(!ObservesLayout(view));
            view.Visibility = Visibility.Visible; Layout(host); await Tick(); Equal(1, loader.Calls.Count);
            Check(ReferenceEquals(lease.Image.GetSource(DispatcherQueue.GetForCurrentThread()), ((Image)view.Children[0]).Source));
            view.Address = "https://art.example.test/b"; Equal(2, loader.Calls.Count); Check(lease.Disposed);
            host.Children.Remove(view);
            var late = new FakeLease(); loader.Calls[1].Completion.SetResult(late); await Tick(); Check(late.Disposed);
            Equal(1, lease.DisposeCount); Equal(1, late.DisposeCount);
            Check(((Image)view.Children[0]).Source is null); Check(!ObservesLayout(view));
        }));
        yield return new("Native artwork hiding cancels unfinished demand and hidden identity changes do not fetch", () => Sta(async () =>
        {
            var first = new HeldLoader(); var second = new HeldLoader();
            var host = new Grid { Width = 100, Height = 100 };
            var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/a" };
            ArtworkScope.SetLoader(host, first); host.Children.Add(view);
            using var surface = Mount(host); Layout(host); await Tick(); Equal(1, first.Calls.Count);
            host.Visibility = Visibility.Collapsed; await Tick();
            Check(first.Calls[0].Token.IsCancellationRequested); Check(!ObservesLayout(view));
            view.Address = "https://art.example.test/b"; ArtworkScope.SetLoader(host, second);
            Layout(host); await Tick(); Equal(0, second.Calls.Count);
            var late = new FakeLease(); first.Calls[0].Completion.SetResult(late); await Tick();
            Equal(1, late.DisposeCount); Check(((Image)view.Children[0]).Source is null);
            host.Visibility = Visibility.Visible; Layout(host); await Tick(); Equal(1, second.Calls.Count);
            var current = new FakeLease(); second.Calls[0].Completion.SetResult(current); await Tick();
            host.Visibility = Visibility.Collapsed; await Tick(); Check(!current.Disposed);
            ArtworkScope.SetLoader(host, first); await Tick(); Equal(1, current.DisposeCount);
            Equal(1, first.Calls.Count); Check(((Image)view.Children[0]).Source is null);
            host.Visibility = Visibility.Visible; Layout(host); await Tick(); Equal(2, first.Calls.Count);
            host.Children.Remove(view); await Tick(); Check(first.Calls[1].Token.IsCancellationRequested);
            var unloaded = new FakeLease(); first.Calls[1].Completion.SetResult(unloaded); await Tick();
            Equal(1, unloaded.DisposeCount); Check(((Image)view.Children[0]).Source is null);
        }));
        yield return new("Native artwork visible failure retries only on explicit action and scope replacement retires old pixels", () => Sta(async () =>
        {
            var first = new HeldLoader(); var second = new HeldLoader();
            var host = new Grid { Width = 100, Height = 100 };
            var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/a" };
            ArtworkScope.SetLoader(host, first); host.Children.Add(view); using var surface = Mount(host);
            Layout(host); await Tick(); await Tick();
            Equal(1, first.Calls.Count); first.Calls[0].Completion.SetException(new ArtworkException("ARTWORK_NETWORK"));
            await Tick(); var retry = (Button)view.Children[1]; Equal(Visibility.Visible, retry.Visibility);
            Layout(host); await Tick(); Equal(1, first.Calls.Count); // Layout is not automatic retry.
            DesktopTestUi.Invoke(retry); await Tick();
            Equal(2, first.Calls.Count);
            var old = new FakeLease(); first.Calls[1].Completion.SetResult(old); await Tick();
            ArtworkScope.SetLoader(host, second); await Tick(); Check(old.Disposed); Equal(1, second.Calls.Count);
            host.Children.Remove(view); Layout(host); await Tick();
            Equal(1, second.Calls.Count); Check(second.Calls[0].Token.IsCancellationRequested);
            var late = new FakeLease(); second.Calls[0].Completion.SetResult(late); await Tick(); Check(late.Disposed);
        }));
    }

    private static async Task LocalArtworkAsync(string host)
    {
        await using var server = new RuntimeHttpClientTests.Server();
        await using var service = new ArtworkService(); // Product's default SocketsHttpHandler, including DNS and connect.
        string path = "/poster.png?fixture=" + new string('x', 17000);
        var address = new UriBuilder(server.BaseUrl) { Host = host, Path = "/poster.png", Query = path[(path.IndexOf('?') + 1)..] }.Uri;
        Check(address.Port is not (80 or 443));
        var loading = service.AcquireAsync(address.AbsoluteUri);
        await using var request = await server.AcceptAsync();
        Equal("GET", request.Method); Equal(path, request.Path);
        Check(!request.Headers.ContainsKey("Cookie") && !request.Headers.ContainsKey("Authorization"));
        await request.ReplyAsync(Encode("png", 2, 3), contentType: "image/png");
        using var lease = await loading;
        Equal(2, lease.Image.PixelWidth); Equal(3, lease.Image.PixelHeight); Check(Immutable(lease.Image));
    }

    private static async Task ArtworkUserInfoAsync()
    {
        foreach (var (userInfo, decoded) in new[]
        {
            ("fixture%20user:p%40ss%3Aword", "fixture user:p@ss:word"),
            ("fixture-user", "fixture-user:"),
            (":fixture-pass", ":fixture-pass")
        })
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var service = new ArtworkService();
            int port = new Uri(server.BaseUrl).Port;
            var loading = service.AcquireAsync($"http://{userInfo}@127.0.0.1:{port}/poster.png");
            await using var request = await server.AcceptAsync();
            Equal("GET", request.Method); Equal("/poster.png", request.Path);
            Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded)), request.Headers["Authorization"]);
            await request.ReplyAsync(Encode("png", 2, 3), contentType: "image/png");
            using var lease = await loading;
            Equal(2, lease.Image.PixelWidth); Equal(3, lease.Image.PixelHeight); Check(Immutable(lease.Image));
        }
    }

    private static async Task ArtworkDowngradeAsync()
    {
        await using var target = new RuntimeHttpClientTests.Server();
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=artwork-fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel requires an imported user key rather than CreateSelfSigned's ephemeral key.
        using var certificate = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int requests = 0;
        var serving = RedirectAsync();
        try
        {
            var handler = new SocketsHttpHandler
            {
                UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
                SslOptions = new() { RemoteCertificateValidationCallback = (_, observed, _, _) =>
                    observed?.GetCertHashString() == certificate.Thumbprint }
            };
            await using var service = new ArtworkService(handler);
            var loading = service.AcquireAsync($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/start");
            await using var request = await target.AcceptAsync();
            Equal("GET", request.Method); Equal("/poster.png", request.Path);
            await request.ReplyAsync(Encode("png", 2, 3), contentType: "image/png");
            using var lease = await loading;
            Equal(1, requests); Equal(2, lease.Image.PixelWidth); Equal(3, lease.Image.PixelHeight); Check(Immutable(lease.Image));
            await serving;
        }
        finally { deadline.Cancel(); listener.Stop(); await serving; }

        async Task RedirectAsync()
        {
            try
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                Equal("GET /start HTTP/1.1", await reader.ReadLineAsync(deadline.Token));
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
                Interlocked.Increment(ref requests);
                byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {target.BaseUrl}/poster.png\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, deadline.Token); await stream.FlushAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        internal int Count; internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Count); return action(request, token); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class HeldReadStream() : MemoryStream(new byte[] { 1 })
    {
        internal readonly TaskCompletionSource Entered = Signal(), Cancelled = Signal(), Release = Signal();
        internal bool Disposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
            return await base.ReadAsync(buffer, CancellationToken.None);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class HeldArtworkBytes : IArtworkByteCache
    {
        internal readonly TaskCompletionSource FirstEntered = Signal(), Entered = Signal(), Cancelled = Signal(), Release = Signal();
        internal int Reads, Writes, Ended, Maximum;
        internal long CurrentEpoch, LastWriteEpoch;
        private readonly List<ReadOnlyMemory<byte>> content = [];
        private int active;
        public long Epoch => Volatile.Read(ref CurrentEpoch);
        public TimeSpan Lifetime => TimeSpan.FromDays(30);
        internal bool ContentCleared
        {
            get { lock (content) return content.All(value => value.Span.IndexOfAnyExcept((byte)0) < 0); }
        }
        public Task<ArtworkBytes?> ReadAsync(string address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Reads);
            return Task.FromResult<ArtworkBytes?>(null);
        }
        public async Task WriteAsync(string address, ReadOnlyMemory<byte> bytes, long epoch, CancellationToken token)
        {
            Check(bytes.Span.IndexOfAnyExcept((byte)0) >= 0, "writer must receive the original encoded image");
            lock (content)
            {
                content.Add(bytes); Maximum = Math.Max(Maximum, ++active); LastWriteEpoch = epoch;
                if (++Writes == 1) FirstEntered.TrySetResult();
                if (Writes == 4) Entered.TrySetResult();
            }
            using var registration = token.Register(() => Cancelled.TrySetResult());
            try { await Release.Task; } // Physical storage deliberately ignores cancellation until released.
            finally { lock (content) { active--; Ended++; } }
        }
        public Task RemoveAsync(string address, long epoch, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class CancelledArtworkBytes(bool corrupt) : IArtworkByteCache
    {
        internal int Reads, Removals, Writes;
        public long Epoch => 0;
        public TimeSpan Lifetime => TimeSpan.FromDays(30);
        public Task<ArtworkBytes?> ReadAsync(string address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++;
            return corrupt ? Task.FromResult<ArtworkBytes?>(new([1, 2, 3], DateTimeOffset.UtcNow + Lifetime)) :
                Task.FromCanceled<ArtworkBytes?>(new CancellationToken(true));
        }
        public Task WriteAsync(string address, ReadOnlyMemory<byte> bytes, long epoch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Writes++;
            return Task.FromCanceled(new CancellationToken(true));
        }
        public Task RemoveAsync(string address, long epoch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Removals++;
            return Task.FromCanceled(new CancellationToken(true));
        }
    }
    private sealed class HeldLoader : IArtworkLoader
    {
        internal readonly List<(TaskCompletionSource<IArtworkLease> Completion, CancellationToken Token)> Calls = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            var next = new TaskCompletionSource<IArtworkLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((next, cancellationToken)); return next.Task;
        }
    }
    private sealed class FakeLease : IArtworkLease
    {
        internal bool Disposed;
        internal int DisposeCount;
        public ArtworkPixels Image { get; } = new(1, 1, new byte[] { 100, 200, 250, 255 });
        public void Dispose() { Disposed = true; DisposeCount++; }
    }
    private static bool ObservesLayout(ArtworkImage view) =>
        (bool)typeof(ArtworkImage).GetField("observingLayout",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
    private static HttpResponseMessage Reply(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class RecordingByteCache : IArtworkByteCache
    {
        public long Epoch => 0;
        public TimeSpan Lifetime => TimeSpan.FromDays(30);
        internal byte[]? Content;
        internal int Writes, Removals;
        public Task<ArtworkBytes?> ReadAsync(string address, CancellationToken token) =>
            Task.FromResult(Content is null ? null : new ArtworkBytes(Content, DateTimeOffset.UtcNow + Lifetime));
        public Task WriteAsync(string address, ReadOnlyMemory<byte> bytes, long epoch, CancellationToken token)
        { Writes++; Content = bytes.ToArray(); return Task.CompletedTask; }
        public Task RemoveAsync(string address, long epoch, CancellationToken token)
        { Removals++; Content = null; return Task.CompletedTask; }
    }

    internal static byte[] LargePng()
    {
        byte[] pixels = new byte[1200 * 1200 * 4];
        new Random(17).NextBytes(pixels);
        var image = BitmapSource.Create(1200, 1200, 96, 96, PixelFormats.Bgra32, null, pixels, 1200 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[] bytes = stream.ToArray();
        Check(bytes.Length > ArtworkService.MaximumCacheEntryBytes);
        return bytes;
    }
    // Synthetic solid-color fixtures encoded by Pillow 13 / libwebp, independently of the production decoder.
    private static byte[] WebP(string kind) => Convert.FromBase64String(kind switch
    {
        "lossy" =>
            "UklGRogCAABXRUJQVlA4IHwCAACwRgCdASogA5ABPm02mUmkIyKhIAgAgA2JaW7hd2Ee3AAAE9gHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D" +
            "32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2T" +
            "kPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77" +
            "ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyH" +
            "vtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99sn" +
            "Ie+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32" +
            "ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkP" +
            "fbJyHvSAAP7/tLr//ugyzhwe///uyf/2yf/2yf+45XhQJJAUAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        "lossless" =>
            "UklGRiIAAABXRUJQVlA4TBUAAAAvB8AAEA8wFKMwyAMY8Lj/f0T/wwEA",
        "extended" =>
            "UklGRmgAAABXRUJQVlA4WAoAAAAQAAAABwAAAwAAQUxQSA0AAAABDzCAERHi/v8R/Q8HAFZQOCA0AAAAkAEAnQEqCAAEAAEAHCWgAnS6AAOYAP78" +
            "HE/y2A7CqFP/nEX/ziL/5xF/5xF/i2kdCk8AAA==",
        "animated" =>
            "UklGRogAAABXRUJQVlA4WAoAAAASAAAABwAAAwAAQU5JTQYAAAAAAAAAAABBTk1GLAAAAAEAAAAAAAMAAAIAAGQAAAJWUDhMFAAAAC8DgAAQDzAU" +
            "oz8eAxjwmIeI/ocBQU5NRigAAAAAAAAAAAAHAAADAABkAAAAVlA4TBAAAAAvB8AAAAdQ5KhM/wMR0f8A",
        "tooManyPixels" =>
            "UklGRj4FAABXRUJQVlA4TDIFAAAv/x8ABAdQssoUuf/BUtq2AZP+//aCpRGRuP4yBSRJ//+HEf1PoED8/UcRkfGf//7777///vvvv//++++///77" +
            "77///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//+" +
            "+++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777//" +
            "/vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++/" +
            "//7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvv" +
            "v//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///77" +
            "77///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//+" +
            "+++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777//" +
            "/vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++/" +
            "//7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvv" +
            "v//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///77" +
            "77///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//+" +
            "+++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777//" +
            "/vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++/" +
            "//7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvv" +
            "v//++++///7777///vvvv//++++///7777///vvvv//++++///7777///vvvP//5z3/+85///Oc///nPf/7zn//85z//+c9//vOf//znP//5z3/+" +
            "85///N8/",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    });
    internal static byte[] Encode(string kind, int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        BitmapEncoder encoder = kind switch { "jpeg" => new JpegBitmapEncoder(), "gif" => new GifBitmapEncoder(), _ => new PngBitmapEncoder() };
        encoder.Frames.Add(BitmapFrame.Create(source)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static void Layout(FrameworkElement view) { view.Measure(new Size(view.Width, view.Height)); view.Arrange(new Rect(0, 0, view.Width, view.Height)); view.UpdateLayout(); }
    private static DesktopTestUi.Host Mount(FrameworkElement root) =>
        new(root, (int)root.Width, (int)root.Height);
    private static Task Tick() => DesktopTestUi.DrainAsync();
    private static async Task WaitFor(Func<bool> condition, string failure = "Artwork image acquisition did not settle")
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);
        Check(condition(), failure);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Code(string code, Func<Task> action)
    { try { await action(); throw new InvalidOperationException("Expected artwork rejection"); } catch (ArtworkException e) { Equal(code, e.Code); Equal(code, e.Message); Check(e.InnerException is null); } }
    private static async Task Cancelled(Task<IArtworkLease> work)
    { try { using var lease = await work; throw new InvalidOperationException("Expected artwork cancellation"); } catch (OperationCanceledException) { } }
    private static void Check(bool value, string message = "Artwork assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Artwork expected {expected}, actual {actual}"); }
    private static bool Premultiplied(ArtworkPixels image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes);
        for (int i = 0; i < bytes.Length; i += 4)
            if (bytes[i] > bytes[i + 3] || bytes[i + 1] > bytes[i + 3] || bytes[i + 2] > bytes[i + 3]) return false;
        return true;
    }
    private static byte[] Pixel(ArtworkPixels image, int x, int y)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes);
        var pixel = bytes.AsSpan((y * image.PixelWidth + x) * 4, 4).ToArray();
        if (pixel[3] > 0)
            for (int i = 0; i < 3; i++) pixel[i] = (byte)Math.Min(255, (pixel[i] * 255 + pixel[3] / 2) / pixel[3]);
        return pixel;
    }
    private static bool IsFallback(Brush brush) => brush is SolidColorBrush solid && solid.Color == Microsoft.UI.Colors.DimGray;
    private static bool Immutable(ArtworkPixels image)
    {
        var first = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(first);
        var original = first.ToArray(); Array.Fill(first, (byte)77);
        image.CopyPixels(first); return first.SequenceEqual(original);
    }
    internal static Task Sta(Func<Task> action) => TestUiHost.InvokeAsync(action);
}

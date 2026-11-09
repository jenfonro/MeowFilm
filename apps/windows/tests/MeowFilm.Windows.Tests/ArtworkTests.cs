using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class ArtworkTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native artwork one sample produces raw catalog and bounded player palettes", () => Sta(() =>
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 100, 200, 250, 255 }, 4);
            var palette = ArtworkImage.SamplePalette(image);
            var raw = (SolidColorBrush)palette.Raw;
            var player = (SolidColorBrush)palette.Palette;
            Check(raw.IsFrozen && player.IsFrozen);
            Equal(Color.FromRgb(250, 200, 100), raw.Color);
            var luminance = .2126 * player.Color.R + .7152 * player.Color.G + .0722 * player.Color.B;
            Check(Math.Abs(luminance - 105) < 2);
            var empty = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 0 }, 4);
            var transparent = ArtworkImage.SamplePalette(empty);
            Check(ReferenceEquals(Brushes.DimGray, transparent.Raw));
            Check(ReferenceEquals(Brushes.DimGray, transparent.Palette));
            var splitPixels = Enumerable.Range(0, 8)
                .SelectMany(row => row < 3
                    ? new byte[] { 245, 245, 245, 255 }
                    : new byte[] { 12, 12, 12, 255 }).ToArray();
            var split = BitmapSource.Create(1, 8, 96, 96, PixelFormats.Bgra32, null, splitPixels, 4);
            var splitRaw = (SolidColorBrush)ArtworkImage.SamplePalette(split).Raw;
            var splitTop = (SolidColorBrush)ArtworkImage.SampleTopPalette(split);
            Check(splitTop.Color.R > splitRaw.Color.R + 70 && splitTop.Color.A == 255);
            var translucent = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 255, 255, 255, 64 }, 4);
            Equal((byte)64, ((SolidColorBrush)ArtworkImage.SampleTopPalette(translucent)).Color.A);
            return Task.CompletedTask;
        }));
        foreach (string format in new[] { "png", "jpeg", "gif" })
            yield return new("Native artwork real WIC bounded frozen raster " + format, () => Sta(async () =>
            {
                byte[] bytes = Encode(format, 800, 400); var handler = new Handler((_, _) => Task.FromResult(Reply(bytes)));
                await using var service = new ArtworkService(handler);
                using var first = await service.AcquireAsync("https://art.example.test/cover." + format);
                Equal(512, first.Image.PixelWidth); Equal(256, first.Image.PixelHeight); Check(first.Image.IsFrozen);
                using var second = await service.AcquireAsync("https://art.example.test/cover." + format);
                Check(ReferenceEquals(first.Image, second.Image)); Equal(1, handler.Count);
                first.Dispose(); Check(second.Image.IsFrozen);
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
                Check(image.IsFrozen); Equal(PixelFormats.Bgra32, image.Format);
                var pixel = new byte[4];
                image.CopyPixels(new Int32Rect(format == "animated" ? 3 : 1, 1, 1, 1), pixel, 4, 0);
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
                    image.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                    Equal((byte)0, pixel[3]);
                }
                using var second = await service.AcquireAsync("https://art.example.test/cover");
                Check(ReferenceEquals(image, second.Image)); Equal(1, handler.Count);
                first.Dispose(); Check(second.Image.IsFrozen);
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
        yield return new("Native artwork public-only address rejection happens before transport", async () =>
        {
            var handler = new Handler((_, _) => throw new InvalidOperationException("No HTTP expected"));
            await using var service = new ArtworkService(handler);
            foreach (string uri in new[] { "file:///C:/secret.png", "data:image/png;base64,AAAA", "https://user:password@example.test/x",
                "http://127.0.0.1/x", "http://169.254.169.254/x", "https://192.168.1.1/x",
                "https://[::1]/x", "https://[::ffff:127.0.0.1]/x", "http://x.local/x", "https://localhost/x",
                "https://example.test:8080/x", "https://example.test/x#fragment", "https://example.test/\0" })
                await Code("ARTWORK_ADDRESS", () => service.AcquireAsync(uri));
            Equal(0, handler.Count);
        });
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
        foreach (string redirect in new[] { "http://next.example.test/x", "https://127.0.0.1/x", "https://first.example.test/x" })
            yield return new("Native artwork rejects downgrade private or cyclic redirect " + redirect, async () =>
            {
                var handler = new Handler((_, _) => { var reply = new HttpResponseMessage(HttpStatusCode.Redirect);
                    reply.Headers.Location = new Uri(redirect); return Task.FromResult(reply); });
                await using var service = new ArtworkService(handler);
                await Code(redirect.Contains("127.", StringComparison.Ordinal) ? "ARTWORK_ADDRESS" : "ARTWORK_REDIRECT",
                    () => service.AcquireAsync("https://first.example.test/x"));
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
        yield return new("Native artwork encoded dimensions and response length budgets are enforced before display", () => Sta(async () =>
        {
            byte[] wide = Encode("png", 1, 1); BinaryPrimitives.WriteUInt32BigEndian(wide.AsSpan(16), 20000);
            int round = 0;
            var handler = new Handler((_, _) => {
                var response = Reply(++round == 1 ? wide : new byte[1]);
                if (round == 2) response.Content.Headers.ContentLength = ArtworkService.MaximumBytes + 1L;
                return Task.FromResult(response); });
            await using var service = new ArtworkService(handler);
            await Code("ARTWORK_LIMIT", () => service.AcquireAsync("https://art.example.test/first"));
            await Code("ARTWORK_LIMIT", () => service.AcquireAsync("https://art.example.test/second"));
        }));
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
        yield return new("Native artwork concurrent visible URLs have no host or global request quota", () => Sta(async () =>
        {
            var arrived = Signal(); var release = Signal(); int active = 0;
            byte[] png = Encode("png", 1, 1);
            var handler = new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.Host == "busy.example.test")
                {
                    int count = Interlocked.Increment(ref active);
                    if (count == 80) arrived.TrySetResult();
                    try { await release.Task.WaitAsync(ct); } finally { Interlocked.Decrement(ref active); }
                }
                return Reply(png);
            });
            await using var service = new ArtworkService(handler);
            var busy = Enumerable.Range(0, 80).Select(i => service.AcquireAsync("https://busy.example.test/" + i)).ToArray();
            await arrived.Task;
            using var independent = await service.AcquireAsync("https://other.example.test/x").WaitAsync(TimeSpan.FromSeconds(5));
            Equal(80, Volatile.Read(ref active)); release.SetResult();
            foreach (var lease in await Task.WhenAll(busy)) lease.Dispose();
            Equal(0, active);
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
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] current = Encode("png", 3, 2), stale = Encode("png", 1, 1);
            var handler = new Handler(async (_, token) =>
            {
                if (Interlocked.Increment(ref calls) != 1) return Reply(current);
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                return await response.Task; // Deliberately ignores physical cancellation.
            });
            await using var service = new ArtworkService(handler);
            using var cancellation = new CancellationTokenSource();
            var first = service.AcquireAsync("https://art.example.test/shared", cancellation.Token);
            await entered.Task; cancellation.Cancel();
            try { using var unexpected = await first; throw new InvalidOperationException("Expected consumer cancellation"); }
            catch (OperationCanceledException) { }
            await cancelled.Task;
            using var replacement = await service.AcquireAsync("https://art.example.test/shared");
            using var cached = await service.AcquireAsync("https://art.example.test/shared");
            Equal(2, handler.Count); Equal(3, replacement.Image.PixelWidth);
            Check(ReferenceEquals(replacement.Image, cached.Image));
            var close = service.DisposeAsync().AsTask();
            Check(!close.IsCompleted && !handler.Disposed);
            response.SetResult(Reply(stale));
            await close;
            Check(handler.Disposed); Equal(3, replacement.Image.PixelWidth);
        }));
        yield return new("Native artwork cache eviction preserves pinned visuals and admits fresh images", () => Sta(async () =>
        {
            byte[] png = Encode("png", 1, 1);
            var handler = new Handler((_, _) => Task.FromResult(Reply(png)));
            await using var service = new ArtworkService(handler);
            var leases = new List<IArtworkLease>();
            try
            {
                for (int i = 0; i < 64; i++) leases.Add(await service.AcquireAsync("https://art.example.test/" + i));
                using var replacement = await service.AcquireAsync("https://art.example.test/overflow");
                Check(leases[0].Image.IsFrozen && leases[1].Image.IsFrozen && replacement.Image.IsFrozen);
                var original = leases[0].Image;
                var reused = await service.AcquireAsync("https://art.example.test/0");
                Check(ReferenceEquals(original, reused.Image)); Equal(65, handler.Count);
                leases[0].Dispose(); leases[0].Dispose();
                using (var shared = await service.AcquireAsync("https://art.example.test/0"))
                {
                    Check(ReferenceEquals(original, shared.Image)); Equal(65, handler.Count);
                }
                reused.Dispose(); reused.Dispose();
                using var fresh = await service.AcquireAsync("https://art.example.test/0");
                Check(!ReferenceEquals(original, fresh.Image)); Equal(66, handler.Count);
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }));
        yield return new("Native artwork hidden retained page restores identical pixels and palettes without HTTP", () => Sta(async () =>
        {
            var encoder = new PngBitmapEncoder();
            using var sample = new FakeLease();
            encoder.Frames.Add(BitmapFrame.Create(sample.Image));
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
            Check(view.HasTopPalette && !ReferenceEquals(raw, Brushes.DimGray));
            foreach (var visibility in new[] { Visibility.Hidden, Visibility.Collapsed })
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
            var host = new Canvas { Width = 100, Height = 100, ClipToBounds = true };
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
            Check(ReferenceEquals(Brushes.DimGray, view.RawPaletteBrush), "leaving the viewport clears its palette");

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
            Check(handler.Disposed && a.Image.IsFrozen && b.Image.IsFrozen);
            await Code("ARTWORK_CLOSED", () => service.AcquireAsync("https://art.example.test/shared"));
            a.Dispose(); a.Dispose(); Check(b.Image.IsFrozen);
        }));
        yield return new("Native artwork presentation prepares outside the viewport and retains pixels until retirement", () => Sta(async () =>
        {
            var loader = new HeldLoader();
            var host = new Canvas { Width = 100, Height = 100, ClipToBounds = true };
            var view = new ArtworkImage { Width = 40, Height = 40, Address = "https://art.example.test/prepared" };
            ArtworkScope.SetLoader(host, loader); Canvas.SetTop(view, 200); host.Children.Add(view);
            using var surface = Mount(host); Layout(host); await Tick();
            Equal(0, loader.Calls.Count);
            var preparing = view.PrepareForPresentationAsync();
            Equal(1, loader.Calls.Count); Check(!preparing.IsCompleted);
            var lease = new FakeLease(); loader.Calls[0].Completion.SetResult(lease);
            using var prepared = await preparing;
            Check(ReferenceEquals(lease.Image, ((Image)view.Children[0]).Source));
            Layout(host); await Tick(); Check(!lease.Disposed); Equal(1, loader.Calls.Count);
            Canvas.SetTop(view, 0); Layout(host); await Tick();
            Canvas.SetTop(view, 200); Layout(host); await Tick();
            Check(!lease.Disposed); Equal(1, loader.Calls.Count);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); await Tick();
            Check(lease.Disposed); Check(((Image)view.Children[0]).Source is null);
        }));
        yield return new("Native artwork preparation follows replacement without waiting for a cancelled old fetch", () => Sta(async () =>
        {
            var loader = new HeldLoader(); var host = new Grid { Width = 100, Height = 100, Visibility = Visibility.Hidden };
            var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/first" };
            ArtworkScope.SetLoader(host, loader); host.Children.Add(view); using var surface = Mount(host); Layout(host);
            var preparing = view.PrepareForPresentationAsync(); Equal(1, loader.Calls.Count);
            view.Address = "https://art.example.test/second"; Equal(2, loader.Calls.Count);
            Check(loader.Calls[0].Token.IsCancellationRequested);
            var next = new FakeLease(); loader.Calls[1].Completion.SetResult(next);
            var prepared = await preparing.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!loader.Calls[0].Completion.Task.IsCompleted);
            Check(ReferenceEquals(next.Image, ((Image)view.Children[0]).Source));
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
            var loader = new HeldLoader(); var host = new Grid { Width = 100, Height = 100, Visibility = Visibility.Hidden };
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
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Equal(2, loader.Calls.Count);
            var lease = new FakeLease(); loader.Calls[1].Completion.SetResult(lease); await Tick();
            Check(ReferenceEquals(lease.Image, ((Image)view.Children[0]).Source));
            Check(view.HasTopPalette);
            prepared.Dispose(); Check(lease.Disposed);
        }));
        yield return new("Native artwork view has no implicit loader and rejects late old-image completion", () => Sta(async () =>
        {
            var host = new Grid { Width = 200, Height = 200 }; var view = new ArtworkImage { Width = 100, Height = 100, Address = "https://art.example.test/first" };
            host.Children.Add(view);
            using var surface = Mount(host);
            Layout(host); view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check(((Image)view.Children[0]).Source is null);
            var loader = new HeldLoader(); ArtworkScope.SetLoader(host, loader); Layout(host); await Tick();
            Equal(1, loader.Calls.Count);
            view.Address = "https://art.example.test/second"; await Tick(); Equal(2, loader.Calls.Count); Check(loader.Calls[0].Token.IsCancellationRequested);
            var old = new FakeLease();
            var next = new FakeLease();
            loader.Calls[0].Completion.SetResult(old); loader.Calls[1].Completion.SetResult(next); await Tick();
            Check(old.Disposed); Check(!next.Disposed);
            Check(ReferenceEquals(next.Image, ((Image)view.Children[0]).Source) && ReferenceEquals(next.Image, view.Source));
            Check(!ReferenceEquals(Brushes.DimGray, view.RawPaletteBrush));
            Check(!ReferenceEquals(Brushes.DimGray, view.PaletteBrush));
            Check(!ReferenceEquals(Brushes.DimGray, view.TopPaletteBrush));
            Check(view.HasTopPalette);
            host.Children.Remove(view); await Tick();
            Equal(1, next.DisposeCount); Check(((Image)view.Children[0]).Source is null && view.Source is null);
            Check(ReferenceEquals(Brushes.DimGray, view.RawPaletteBrush));
            Check(ReferenceEquals(Brushes.DimGray, view.PaletteBrush));
            Check(ReferenceEquals(Brushes.DimGray, view.TopPaletteBrush));
            Check(!view.HasTopPalette);
        }));
        yield return new("Native artwork hidden views retain pixels and unload rejects late replacement", () => Sta(async () =>
        {
            var loader = new HeldLoader(); var host = new Canvas { Width = 100, Height = 100, ClipToBounds = true };
            var view = new ArtworkImage { Width = 40, Height = 40, Address = "https://art.example.test/a" };
            ArtworkScope.SetLoader(host, loader); Canvas.SetTop(view, 200); host.Children.Add(view);
            using var surface = Mount(host);
            Layout(host);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await Tick(); Equal(0, loader.Calls.Count);
            Canvas.SetTop(view, 0); Layout(host); await Tick(); Equal(1, loader.Calls.Count);
            var lease = new FakeLease(); loader.Calls[0].Completion.SetResult(lease); await Tick(); Check(!lease.Disposed);
            view.Visibility = Visibility.Collapsed; await Tick(); Check(!lease.Disposed);
            Check(!ObservesLayout(view));
            view.Visibility = Visibility.Visible; Layout(host); await Tick(); Equal(1, loader.Calls.Count);
            Check(ReferenceEquals(lease.Image, ((Image)view.Children[0]).Source));
            view.Address = "https://art.example.test/b"; Equal(2, loader.Calls.Count); Check(lease.Disposed);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
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
            Layout(host); view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await Tick();
            Equal(1, first.Calls.Count); first.Calls[0].Completion.SetException(new ArtworkException("ARTWORK_NETWORK"));
            await Tick(); var retry = (Button)view.Children[1]; Equal(Visibility.Visible, retry.Visibility);
            Layout(host); await Tick(); Equal(1, first.Calls.Count); // Layout is not automatic retry.
            var click = new RoutedEventArgs(Button.ClickEvent); retry.RaiseEvent(click); await Tick();
            Check(click.Handled); Equal(2, first.Calls.Count);
            var old = new FakeLease(); first.Calls[1].Completion.SetResult(old); await Tick();
            ArtworkScope.SetLoader(host, second); await Tick(); Check(old.Disposed); Equal(1, second.Calls.Count);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Layout(host); await Tick();
            Equal(1, second.Calls.Count); Check(second.Calls[0].Token.IsCancellationRequested);
            var late = new FakeLease(); second.Calls[0].Completion.SetResult(late); await Tick(); Check(late.Disposed);
        }));
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
        public BitmapSource Image { get; } = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 100, 200, 250, 255 }, 4);
        public void Dispose() { Disposed = true; DisposeCount++; }
    }
    private static bool ObservesLayout(ArtworkImage view) =>
        (bool)typeof(ArtworkImage).GetField("observingLayout",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
    private static HttpResponseMessage Reply(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
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
    private static HwndSource Mount(FrameworkElement root)
    {
        // Real WPF presentation source, hidden tool HWND; no product Main,
        // visible window, external input or system settings are involved.
        var source = new HwndSource(new HwndSourceParameters("artwork-code-fixture")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
            PositionX = -32000, PositionY = -32000, Width = (int)root.Width, Height = (int)root.Height
        });
        source.RootVisual = root; return source;
    }
    private static async Task Tick() { await Dispatcher.Yield(DispatcherPriority.Background); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task WaitFor(Func<bool> condition, string failure = "Artwork image acquisition did not settle")
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);
        Check(condition(), failure);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Code(string code, Func<Task> action)
    { try { await action(); throw new InvalidOperationException("Expected artwork rejection"); } catch (ArtworkException e) { Equal(code, e.Code); Equal(code, e.Message); Check(e.InnerException is null); } }
    private static void Check(bool value, string message = "Artwork assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Artwork expected {expected}, actual {actual}"); }
    private static async Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = Signal(); Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            var current = Dispatcher.CurrentDispatcher; Volatile.Write(ref dispatcher, current);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(current));
            _ = current.BeginInvoke(async () =>
            {
                Exception? error = null;
                try { await action(); } catch (Exception e) { error = e; }
                finally { done.TrySetResult(error); current.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); } finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "artwork-component-test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Exception? failure;
        try { failure = await done.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { Volatile.Read(ref dispatcher)?.BeginInvokeShutdown(DispatcherPriority.Send); throw; }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

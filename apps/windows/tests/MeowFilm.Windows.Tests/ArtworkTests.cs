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
        yield return new("Native artwork one congested host cannot monopolize global admission", () => Sta(async () =>
        {
            var arrived = Signal(); var release = Signal(); int active = 0, maximum = 0;
            byte[] png = Encode("png", 1, 1);
            var handler = new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.Host == "busy.example.test")
                {
                    int count = Interlocked.Increment(ref active);
                    maximum = Math.Max(maximum, count); if (count == 2) arrived.TrySetResult();
                    try { await release.Task.WaitAsync(ct); } finally { Interlocked.Decrement(ref active); }
                }
                return Reply(png);
            });
            await using var service = new ArtworkService(handler);
            var busy = Enumerable.Range(0, 10).Select(i => service.AcquireAsync("https://busy.example.test/" + i)).ToArray();
            await arrived.Task;
            using var independent = await service.AcquireAsync("https://other.example.test/x").WaitAsync(TimeSpan.FromSeconds(5));
            Equal(2, maximum); release.SetResult();
            foreach (var lease in await Task.WhenAll(busy)) lease.Dispose();
            Equal(2, maximum);
        }));
        yield return new("Native artwork cache cannot evict pinned visuals and can evict after last release", () => Sta(async () =>
        {
            byte[] png = Encode("png", 1, 1);
            await using var service = new ArtworkService(new Handler((_, _) => Task.FromResult(Reply(png))));
            var leases = new List<IArtworkLease>();
            try
            {
                for (int i = 0; i < 64; i++) leases.Add(await service.AcquireAsync("https://art.example.test/" + i));
                await Code("ARTWORK_LIMIT", () => service.AcquireAsync("https://art.example.test/overflow"));
                leases[0].Dispose();
                using var replacement = await service.AcquireAsync("https://art.example.test/overflow");
                Check(leases[1].Image.IsFrozen);
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
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
            Check(old.Disposed); Check(!next.Disposed); Check(ReferenceEquals(next.Image, ((Image)view.Children[0]).Source));
            Check(!ReferenceEquals(Brushes.DimGray, view.RawPaletteBrush));
            Check(!ReferenceEquals(Brushes.DimGray, view.PaletteBrush));
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); await Tick();
            Check(next.Disposed); Check(((Image)view.Children[0]).Source is null);
            Check(ReferenceEquals(Brushes.DimGray, view.RawPaletteBrush));
            Check(ReferenceEquals(Brushes.DimGray, view.PaletteBrush));
        }));
        yield return new("Native artwork clipped and hidden views release images until visible again", () => Sta(async () =>
        {
            var loader = new HeldLoader(); var host = new Canvas { Width = 100, Height = 100, ClipToBounds = true };
            var view = new ArtworkImage { Width = 40, Height = 40, Address = "https://art.example.test/a" };
            ArtworkScope.SetLoader(host, loader); Canvas.SetTop(view, 200); host.Children.Add(view);
            using var surface = Mount(host);
            Layout(host);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await Tick(); Equal(0, loader.Calls.Count);
            Canvas.SetTop(view, 0); Layout(host); await Tick(); Equal(1, loader.Calls.Count);
            var lease = new FakeLease(); loader.Calls[0].Completion.SetResult(lease); await Tick(); Check(!lease.Disposed);
            view.Visibility = Visibility.Collapsed; await Tick(); Check(lease.Disposed);
            view.Visibility = Visibility.Visible; Layout(host); await Tick(); Equal(2, loader.Calls.Count);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            var late = new FakeLease(); loader.Calls[1].Completion.SetResult(late); await Tick(); Check(late.Disposed);
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
        public BitmapSource Image { get; } = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 100, 200, 250, 255 }, 4);
        public void Dispose() => Disposed = true;
    }
    private static HttpResponseMessage Reply(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static byte[] Encode(string kind, int width, int height)
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
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Code(string code, Func<Task> action)
    { try { await action(); throw new InvalidOperationException("Expected artwork rejection"); } catch (ArtworkException e) { Equal(code, e.Code); Equal(code, e.Message); Check(e.InnerException is null); } }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Artwork assertion failed"); }
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

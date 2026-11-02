// SPDX-License-Identifier: GPL-3.0-only
// Catalog-only lifecycle regression; no platform business requests.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static class MusicCatalogHttpDrainTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (bool encrypted in new[] { false, true })
        {
            bool xeapi = encrypted; string prefix = "music.catalog.http.drain." + (xeapi ? "xeapi." : "json.");
            yield return new(prefix + "roundtrip-original-origin-and-owned-request", () => Roundtrip(xeapi));
            yield return new(prefix + "no-new-origin-permission", () => Origins(xeapi));
            yield return new(prefix + "cancel-handler-physical-settle", () => Drain(xeapi, 0, "cancel"));
            yield return new(prefix + "cancel-response-read-physical-settle", () => Drain(xeapi, 1, "cancel"));
            yield return new(prefix + "cancel-disposeasync-rejects-late-result", () => Drain(xeapi, 2, "cancel"));
            yield return new(prefix + "deadline-handler-physical-settle", () => Drain(xeapi, 0, "timeout"));
            yield return new(prefix + "close-handler-physical-settle", () => Drain(xeapi, 0, "close"));
        }
        yield return new("music.catalog.http.drain.cancelled-requests-drain-without-blocking-new-requests", Slots);
        yield return new("music.catalog.http.drain.ordinary-sendasync-cancellation-settles-response", OrdinaryCancellation);
    }
    private static void Check(bool value, string message = "Catalog drain assertion")
    { if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpRequestMessage Request() => new(HttpMethod.Post, "https://music.163.com/weapi/v3/song/detail") {
        Content = new ByteArrayContent("{}"u8.ToArray())
    };
    private static byte[] Reply(bool xeapi)
    {
        var bytes = """{"code":200}"""u8.ToArray();
        if (!xeapi) return bytes;
        using var aes = Aes.Create(); aes.Key = "e82ckenh8dichen8"u8.ToArray();
        return aes.EncryptEcb(bytes, PaddingMode.PKCS7);
    }
    private static HttpResponseMessage Response(bool xeapi) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Reply(xeapi)) };
    private static Task<MusicPlatformHttpResult> Send(MusicPlatformHttp http, bool xeapi,
        HttpRequestMessage request, CancellationToken token = default, TimeSpan? timeout = null)
    {
        return xeapi
            ? http.SendXeapiAsync(request, token, 65536, timeout ?? TimeSpan.FromSeconds(25))
            : http.SendAsync("wy", request, token, 65536, timeout ?? TimeSpan.FromSeconds(25));
    }
    private static async Task Failure(string code, Func<Task> run, CancellationToken token = default)
    {
        try { await run(); }
        catch (MusicPlatformException e) { Check(e.Code == code, "Expected " + code + ", got " + e.Code); Check(e.InnerException is null); return; }
        catch (OperationCanceledException e) when (code == "CANCELLED") { Check(e.CancellationToken == token); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task Roundtrip(bool xeapi)
    {
        int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) => {
            calls++; Check(request.RequestUri!.Host == "music.163.com"); return Task.FromResult(Response(xeapi));
        }));
        var request = Request(); var content = request.Content!;
        var result = await Send(http, xeapi, request); Check(result.Body["code"]!.GetValue<int>() == 200 && calls == 1);
        try { _ = await content.ReadAsByteArrayAsync(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Request content not disposed");
    }
    private static async Task Origins(bool xeapi)
    {
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => throw new InvalidOperationException("No HTTP")));
        foreach (string uri in new[] { "http://music.163.com/weapi/v3/song/detail", "https://music.163.com:444/",
            "https://music.163.com/#fragment", "https://user@music.163.com/", "http://search.kuwo.cn/r.s",
            "https://jadeite.migu.cn/music_search/v3/search/searchAll", "https://clientlog.music.163.com/eapi/feedback/weblog",
            "https://stat6.y.qq.com/android/fcgi-bin/imusic_tj" })
        { var request = Request(); request.RequestUri = new(uri); await Failure("ORIGIN_DENIED", () => Send(http, xeapi, request)); }
    }
    private static async Task Drain(bool xeapi, int phase, string outcome)
    {
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        var stream = new HoldStream(Reply(xeapi), phase, entered, release);
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, token) => {
            using var registration = token.Register(() => cancelled.TrySetResult());
            if (phase == 0) { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }));
        using var cancel = new CancellationTokenSource();
        var pending = Send(http, xeapi, Request(), cancel.Token,
            outcome == "timeout" ? TimeSpan.FromMilliseconds(60) : TimeSpan.FromSeconds(25));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (outcome == "cancel") cancel.Cancel();
            else if (outcome == "close") http.Dispose();
            else await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(40);
            Check(!pending.IsCompleted, "Catalog detached physical operation");
        }
        finally { release.TrySetResult(); }
        await Failure(outcome == "cancel" ? "CANCELLED" : outcome == "close" ? "CLOSED" : "TIMEOUT",
            () => pending, cancel.Token);
        Check(stream.Disposed);
    }
    private static async Task Slots()
    {
        var release = Signal(); var both = Signal(); int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, _) => {
            if (Interlocked.Increment(ref calls) == 2) both.TrySetResult();
            await release.Task; return Response(false);
        }));
        using var cancel = new CancellationTokenSource();
        var one = Send(http, false, Request(), cancel.Token); var two = Send(http, false, Request(), cancel.Token);
        try
        {
            await both.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await Task.Delay(40);
            Check(!one.IsCompleted && !two.IsCompleted);
            // Draining requests do not hold any admission budget.
            var three = http.SendAsync("wy", Request(), default);
            for (int i = 0; i < 200 && Volatile.Read(ref calls) < 3; i++) await Task.Delay(10);
            Check(calls == 3);
            release.TrySetResult(); await three;
        }
        finally { release.TrySetResult(); }
        await Failure("CANCELLED", () => one, cancel.Token); await Failure("CANCELLED", () => two, cancel.Token);
    }
    private static async Task OrdinaryCancellation()
    {
        var entered = Signal(); var release = Signal(); var disposed = Signal();
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, _) => {
            entered.TrySetResult(); await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new DisposeContent(disposed) };
        }));
        using var cancel = new CancellationTokenSource();
        var pending = http.SendAsync("wy", Request(), cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
            Check(!pending.IsCompleted && !disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Failure("CANCELLED", () => pending.WaitAsync(TimeSpan.FromSeconds(2)), cancel.Token);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class DisposeContent(TaskCompletionSource disposed) : ByteArrayContent("{}"u8.ToArray())
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); disposed.TrySetResult(); }
    }
    private sealed class HoldStream(byte[] data, int phase, TaskCompletionSource entered, TaskCompletionSource release) : Stream
    {
        private bool read;
        internal bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (phase == 1) { entered.TrySetResult(); await release.Task; }
            if (read) return 0;
            read = true; data.CopyTo(buffer); return data.Length;
        }
        public override async ValueTask DisposeAsync()
        {
            if (phase == 2) { entered.TrySetResult(); await release.Task; }
            Disposed = true; GC.SuppressFinalize(this);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

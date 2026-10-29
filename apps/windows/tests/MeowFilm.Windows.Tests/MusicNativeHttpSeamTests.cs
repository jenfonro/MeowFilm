// SPDX-License-Identifier: GPL-3.0-only
// Synthetic handlers only; no accounts, upstream writes or reference execution.
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static class MusicNativeHttpSeamTests
{
    private const string FeedbackUrl = "https://clientlog.music.163.com/eapi/feedback/weblog";
    private const string CloudUrl = "https://gateway.kugou.com/v1/modify_song_sort";
    private const string Router = "cloudlist.service.kugou.com";
    private static TestCase Case(string name, Func<Task> run) => new("music.native.http.seam." + name, run);
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("feedback-exact-three-routes-and-existing-native-builders", FeedbackRoutes);
        yield return Case("cloud-exact-binary-routes-preserve-signed-query-and-bytes", CloudRoutes);
        yield return Case("feedback-is-wy-only", FeedbackPlatform);
        yield return Case("cloud-requires-one-exact-router-across-all-headers", CloudRouter);
        yield return Case("ordinary-and-lyrics-permissions-unchanged", OrdinaryOrigins);
        foreach (bool cloud in new[] { false, true })
        {
            bool c = cloud; string name = c ? "cloud" : "feedback";
            yield return Case(name + "-rejects-neighbor-routes-methods-and-unsafe-authorities", () => RoutesDenied(c));
            yield return Case(name + "-non2xx-and-redirects-never-ACK", () => Statuses(c));
            yield return Case(name + "-redirected-response-and-mutated-request-denied", () => Redirected(c));
            yield return Case(name + "-request-size-and-header-budgets", () => RequestLimits(c));
            yield return Case(name + "-response-size-header-and-stream-budgets", () => ResponseLimits(c));
            yield return Case(name + "-verification-header-denied", () => Verification(c));
            yield return Case(name + "-precancel-disposes-without-HTTP", () => PreCancelled(c));
            yield return Case(name + "-cancel-waits-for-noncooperative-handler", () => CancelHandler(c));
            yield return Case(name + "-cancel-waits-for-noncooperative-body-read", () => CancelRead(c));
            yield return Case(name + "-cancel-during-async-cleanup-cannot-publish", () => CancelCleanup(c));
            yield return Case(name + "-close-waits-for-physical-settle", () => Close(c));
            yield return Case(name + "-transport-error-is-finite-redacted-and-not-retried", () => NetworkFailure(c));
        }
        yield return Case("feedback-strict-JSON-and-transient-result", FeedbackJson);
        yield return Case("cloud-empty-response-cannot-ACK", CloudEmpty);
        yield return Case("physical-slots-shared-with-ordinary-HTTP-until-settled", Slots);
        yield return Case("both-fixed-20-second-deadlines-wait-for-physical-settle", Deadlines);
    }

    // Internal capability is intentionally not made public for the test project.
    private static Func<string, HttpRequestMessage, CancellationToken, Task<MusicPlatformHttpResult>> Feedback(MusicPlatformHttp http) =>
        typeof(MusicPlatformHttp).GetMethod("SendFeedbackJsonAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<string, HttpRequestMessage, CancellationToken, Task<MusicPlatformHttpResult>>>(http);
    private static Func<HttpRequestMessage, CancellationToken, Task<byte[]>> Cloud(MusicPlatformHttp http) =>
        typeof(MusicPlatformHttp).GetMethod("SendMusicLibrarySyncCloudAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<HttpRequestMessage, CancellationToken, Task<byte[]>>>(http);
    private static Task Call(MusicPlatformHttp http, bool cloud, HttpRequestMessage request, CancellationToken token = default) =>
        cloud ? Cloud(http)(request, token) : Feedback(http)("wy", request, token);
    private static HttpRequestMessage Request(bool cloud, string? uri = null, HttpMethod? method = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, uri ?? (cloud ? CloudUrl : FeedbackUrl)) {
            Content = content ?? new TrackedContent([1, 2, 3])
        };
        if (cloud) request.Headers.TryAddWithoutValidation("x-router", Router);
        return request;
    }
    private static HttpResponseMessage Response(byte[]? bytes = null) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? "{}"u8.ToArray()) };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message = "HTTP seam assertion")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Failure(string code, Func<Task> run, CancellationToken token = default)
    {
        try { await run(); }
        catch (MusicPlatformException e)
        {
            Check(e.Code == code, "Expected " + code + "; got " + e.Code);
            Check(e.Message == code && e.InnerException is null && !e.ToString().Contains("synthetic-private", StringComparison.Ordinal));
            return;
        }
        catch (OperationCanceledException e) when (code == "CANCELLED")
        { Check(e.CancellationToken == token); return; }
        throw new InvalidOperationException("Expected finite failure " + code);
    }
    private static async Task Denied(bool cloud, HttpRequestMessage request, string code = "ORIGIN_DENIED")
    {
        int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => { calls++; return Task.FromResult(Response()); }));
        var content = request.Content;
        await Failure(code, () => Call(http, cloud, request));
        Check(calls == 0);
        await Disposed(content!);
    }
    private static async Task Disposed(HttpContent content)
    {
        try { _ = await content.ReadAsByteArrayAsync(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Owned request content not disposed");
    }
    private static async Task FeedbackRoutes()
    {
        int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) => {
            calls++; Check(request.Method == HttpMethod.Post);
            Check(request.Content!.Headers.ContentLength is > 0 and <= 65536);
            return Task.FromResult(Response());
        }));
        foreach (string path in new[] { "/api/w/nuser/account/get", "/api/v3/song/detail" })
            await Feedback(http)("wy", MusicPlatformRequests.WyWithCookie(path, new(), null, weapi: true), default);
        var report = MusicPlatformRequests.WyWithCookie("/api/feedback/weblog", new(), null);
        report.RequestUri = new Uri(FeedbackUrl);
        await Feedback(http)("wy", report, default);
        Check(calls == 3);
    }
    private static async Task CloudRoutes()
    {
        int calls = 0;
        foreach (string path in new[] { "/v2/delete_list", "/v1/modify_song_sort" })
        {
            byte[] body = path == "/v2/delete_list" ? Encoding.ASCII.GetBytes("AAECAwQ=") : [0, 255, 128, 1, 2];
            byte[] reply = [255, 0, 127, 128, 2];
            string uri = "https://gateway.kugou.com" + path + "?appid=1005&p=synthetic%2B%2F%3D&signature=synthetic";
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (request, token) => {
                calls++; Check(request.RequestUri!.OriginalString == uri && request.Method == HttpMethod.Post);
                Check(request.Headers.GetValues("x-router").SequenceEqual([Router]));
                Check((await request.Content!.ReadAsByteArrayAsync(token)).SequenceEqual(body));
                var response = Response(reply); response.Headers.TryAddWithoutValidation("Set-Cookie", "synthetic-private=not-a-session");
                return response;
            }));
            var result = await Cloud(http)(Request(true, uri, content: new TrackedContent(body)), default);
            Check(result.SequenceEqual(reply)); // Raw non-UTF8 data: no JSON conversion or cookie forwarding.
        }
        Check(calls == 2);
    }
    private static async Task FeedbackPlatform()
    {
        int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => { calls++; return Task.FromResult(Response()); }));
        foreach (string platform in new[] { "tx", "kg", "WY", "", "unknown" })
        {
            var request = Request(false);
            await Failure("ORIGIN_DENIED", () => Feedback(http)(platform, request, default));
            await Disposed(request.Content!);
        }
        Check(calls == 0);
    }
    private static async Task CloudRouter()
    {
        foreach (string[] values in new[] { Array.Empty<string>(), new[] { "other.kugou.com" },
            new[] { Router, Router }, new[] { Router + "," + Router }, new[] { " " + Router },
            new[] { Router + " " }, new[] { Router.ToUpperInvariant() } })
        {
            var request = Request(true); request.Headers.Remove("x-router");
            request.Headers.TryAddWithoutValidation("x-router", values);
            await Denied(true, request);
        }
        var duplicate = Request(true); duplicate.Content!.Headers.TryAddWithoutValidation("x-router", Router);
        await Denied(true, duplicate);
    }
    private static async Task OrdinaryOrigins()
    {
        Check(!MusicPlatformHttp.IsAllowedOrigin("wy", new Uri(FeedbackUrl)));
        Check(!MusicPlatformHttp.IsAllowedOrigin("tx", new Uri(FeedbackUrl)));
        Check(!MusicPlatformHttp.IsAllowedOrigin("kg", new Uri(FeedbackUrl)));
        Check(!MusicPlatformHttp.IsAllowedOrigin("wy", new Uri(CloudUrl)));
        Check(!MusicPlatformHttp.IsAllowedOrigin("tx", new Uri(CloudUrl)));
        Check(MusicPlatformHttp.IsAllowedOrigin("kg", new Uri(CloudUrl)));
        Check(MusicPlatformHttp.IsAllowedOrigin("wy", new Uri("https://music.163.com/weapi/w/nuser/account/get")));
        int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => { calls++; return Task.FromResult(Response()); }));
        await Failure("ORIGIN_DENIED", () => http.SendAsync("wy", Request(false), default));
        var lyrics = typeof(MusicPlatformHttp).GetMethod("SendLyricBytesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<string, HttpRequestMessage, CancellationToken, TimeSpan, Task<byte[]>>>(http);
        await Failure("ORIGIN_DENIED", () => lyrics("wy", Request(false), default, TimeSpan.FromSeconds(1)));
        await Failure("ORIGIN_DENIED", () => lyrics("kg", Request(true), default, TimeSpan.FromSeconds(1)));
        Check(calls == 0);
    }
    private static async Task RoutesDenied(bool cloud)
    {
        string good = cloud ? CloudUrl : FeedbackUrl;
        foreach (string uri in new[] {
            good.Replace("https:", "http:", StringComparison.Ordinal),
            good.Replace(".com/", ".com:444/", StringComparison.Ordinal),
            good.Replace("https://", "https://synthetic@", StringComparison.Ordinal), good + "#fragment",
            good + "/extra", good.Replace("/v1/modify_song_sort", "/v1/modify_list_sort", StringComparison.Ordinal)
                .Replace("/eapi/feedback/weblog", "/weapi/feedback/weblog", StringComparison.Ordinal),
            good.Replace(".com/", ".com.example.invalid/", StringComparison.Ordinal),
            "https://u.y.qq.com/cgi-bin/musicu.fcg", "https://127.0.0.1/",
            cloud ? "https://cloudlist.service.kugou.com/v1/modify_song_sort" : "https://music.163.com/eapi/feedback/weblog",
            cloud ? "https://gateway.kugou.com/v2/add_list" : "https://clientlog.music.163.com/weapi/v3/song/detail",
            cloud ? "https://gateway.kugou.com/V1/modify_song_sort" : "https://music.163.com/weapi/v3/song/detail?url=synthetic"
        }) await Denied(cloud, Request(cloud, uri));
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Head })
            await Denied(cloud, Request(cloud, method: method));
        var host = Request(cloud); host.Headers.Host = "other.invalid";
        await Denied(cloud, host);
        var relative = Request(cloud); relative.RequestUri = new Uri("/relative", UriKind.Relative);
        await Denied(cloud, relative);
        var missing = Request(cloud); missing.RequestUri = null;
        await Denied(cloud, missing);
    }
    private static async Task Statuses(bool cloud)
    {
        foreach (var (status, code) in new[] { (199, "UPSTREAM_ERROR"), (301, "REDIRECT_DENIED"), (302, "REDIRECT_DENIED"),
            (307, "REDIRECT_DENIED"), (308, "REDIRECT_DENIED"), (401, "AUTH_REQUIRED"), (403, "FORBIDDEN"),
            (429, "RATE_LIMITED"), (418, "UPSTREAM_ERROR"), (500, "UPSTREAM_ERROR") })
        {
            int calls = 0;
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
                calls++; var response = Response("""{"code":200,"status":1}"""u8.ToArray());
                response.StatusCode = (HttpStatusCode)status;
                if (status is >= 300 and < 400) response.Headers.Location = new Uri("https://example.invalid/never-follow");
                return Task.FromResult(response);
            }));
            await Failure(code, () => Call(http, cloud, Request(cloud)));
            Check(calls == 1);
        }
    }
    private static async Task Redirected(bool cloud)
    {
        foreach (bool mutate in new[] { false, true })
        {
            int calls = 0;
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) => {
                calls++; var response = Response();
                if (mutate) { request.RequestUri = new Uri("https://other.invalid/"); response.RequestMessage = request; }
                else response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://other.invalid/");
                return Task.FromResult(response);
            }));
            await Failure("REDIRECT_DENIED", () => Call(http, cloud, Request(cloud)));
            Check(calls == 1);
        }
    }
    private static async Task RequestLimits(bool cloud)
    {
        int calls = 0;
        using (var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (request, token) => {
            calls++; Check((await request.Content!.ReadAsByteArrayAsync(token)).Length == 65536); return Response();
        })))
            await Call(http, cloud, Request(cloud, content: new TrackedContent(new byte[65536])));
        Check(calls == 1);
        await Denied(cloud, Request(cloud, content: new TrackedContent(new byte[65537])), "LIMIT");
        await Denied(cloud, Request(cloud, content: new UnknownContent()), "LIMIT");
        var headers = Request(cloud); headers.Headers.TryAddWithoutValidation("x-pad", new string('a', 32769));
        await Denied(cloud, headers, "LIMIT");
        var combined = Request(cloud); combined.Headers.TryAddWithoutValidation("x-pad", new string('a', 20000));
        combined.Content!.Headers.TryAddWithoutValidation("x-extra", new string('b', 15000));
        await Denied(cloud, combined, "LIMIT");
    }
    private static byte[] BoundedResponse(bool cloud, int length)
    {
        var bytes = Enumerable.Repeat((byte)' ', length).ToArray();
        bytes[0] = (byte)'{'; bytes[1] = (byte)'}'; if (cloud) bytes[0] = 255;
        return bytes;
    }
    private static async Task ResponseLimits(bool cloud)
    {
        int maximum = cloud ? 1048576 : 65536;
        foreach (bool knownLength in new[] { true, false })
        {
            foreach (int length in new[] { maximum, maximum + 1 })
            {
                int calls = 0;
                using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
                    calls++; var response = Response(BoundedResponse(cloud, length));
                    if (!knownLength) response.Content = new StreamContent(new NonSeekStream(BoundedResponse(cloud, length)));
                    return Task.FromResult(response);
                }));
                if (length == maximum) await Call(http, cloud, Request(cloud));
                else await Failure("LIMIT", () => Call(http, cloud, Request(cloud)));
                Check(calls == 1);
            }
        }
        foreach (bool contentHeader in new[] { false, true })
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
                var response = Response();
                response.Headers.TryAddWithoutValidation("x-pad", new string('a', contentHeader ? 20000 : 32769));
                if (contentHeader) response.Content.Headers.TryAddWithoutValidation("x-extra", new string('b', 15000));
                return Task.FromResult(response);
            }));
            await Failure("LIMIT", () => Call(http, cloud, Request(cloud)));
        }
    }
    private static async Task Verification(bool cloud)
    {
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
            var response = Response(); response.Headers.TryAddWithoutValidation("ssa-code", "synthetic-private-verification");
            return Task.FromResult(response);
        }));
        await Failure("VERIFICATION_REQUIRED", () => Call(http, cloud, Request(cloud)));
    }
    private static async Task PreCancelled(bool cloud)
    {
        int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => { calls++; return Task.FromResult(Response()); }));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var content = new TrackedContent([1]);
        await Failure("CANCELLED", () => Call(http, cloud, Request(cloud, content: content), cancellation.Token), cancellation.Token);
        Check(calls == 0 && content.IsDisposed);
    }
    private static async Task CancelHandler(bool cloud)
    {
        var started = Signal(); var cancelled = Signal(); var release = Signal();
        var content = new TrackedContent([1]); int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, token) => {
            calls++; using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult(); await release.Task; return Response();
        }));
        using var cancellation = new CancellationTokenSource();
        var pending = Call(http, cloud, Request(cloud, content: content), cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!pending.IsCompleted && !content.IsDisposed);
        }
        finally { release.TrySetResult(); }
        await Failure("CANCELLED", () => pending, cancellation.Token);
        Check(calls == 1 && content.IsDisposed);
    }
    private static async Task CancelRead(bool cloud)
    {
        var stream = new HeldReadStream(); var content = new TrackedContent([1]);
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        using var cancellation = new CancellationTokenSource();
        var pending = Call(http, cloud, Request(cloud, content: content), cancellation.Token);
        try
        {
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
            await stream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!pending.IsCompleted && !content.IsDisposed && !stream.IsDisposed);
        }
        finally { stream.Release.TrySetResult(); }
        await Failure("CANCELLED", () => pending, cancellation.Token);
        Check(content.IsDisposed && stream.IsDisposed);
    }
    private static async Task CancelCleanup(bool cloud)
    {
        var stream = new HeldDisposeStream(); var content = new TrackedContent([1]);
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        using var cancellation = new CancellationTokenSource();
        var pending = Call(http, cloud, Request(cloud, content: content), cancellation.Token);
        try
        {
            await stream.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
            Check(!pending.IsCompleted && !content.IsDisposed);
        }
        finally { stream.Release.TrySetResult(); }
        await Failure("CANCELLED", () => pending, cancellation.Token);
        Check(content.IsDisposed && stream.IsDisposed);
    }
    private static async Task Close(bool cloud)
    {
        var started = Signal(); var cancelled = Signal(); var release = Signal(); int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, token) => {
            calls++; using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult(); await release.Task; return Response();
        }));
        var pending = Call(http, cloud, Request(cloud));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); http.Dispose();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check(!pending.IsCompleted);
            await Failure("CLOSED", () => Call(http, cloud, Request(cloud)));
        }
        finally { release.TrySetResult(); }
        await Failure("CLOSED", () => pending); Check(calls == 1);
    }
    private static async Task NetworkFailure(bool cloud)
    {
        int calls = 0; var content = new TrackedContent([1]);
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
            calls++; throw new HttpRequestException("synthetic-private-uri-cookie-body");
        }));
        await Failure("NETWORK_ERROR", () => Call(http, cloud, Request(cloud, content: content)));
        Check(calls == 1 && content.IsDisposed);
    }
    private static async Task FeedbackJson()
    {
        foreach (byte[] bytes in new[] { Array.Empty<byte>(), "[]"u8.ToArray(), "{\"x\":1,\"x\":2}"u8.ToArray(), new byte[] { 255 } })
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(Response(bytes))));
            await Failure("INVALID_RESPONSE", () => Feedback(http)("wy", Request(false), default));
        }
        using var valid = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
            var response = Response("""{"code":200}"""u8.ToArray());
            response.Headers.TryAddWithoutValidation("Set-Cookie", "synthetic-private=not-a-session"); return Task.FromResult(response);
        }));
        var result = await Feedback(valid)("wy", Request(false), default);
        Check(result.Body["code"]!.GetValue<int>() == 200 && result.SetCookieHeaders.Count == 1);
        Check(JsonSerializer.Serialize(result) == "{}" && !result.ToString().Contains("synthetic-private", StringComparison.Ordinal));
    }
    private static async Task CloudEmpty()
    {
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(Response([]))));
        await Failure("INVALID_RESPONSE", () => Cloud(http)(Request(true), default));
    }
    private static async Task Slots()
    {
        var started = Signal(); var cancelled = Signal(); var release = Signal(); int calls = 0, cancellations = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, token) => {
            if (Interlocked.Increment(ref calls) == 4) started.TrySetResult();
            using var registration = token.Register(() => { if (Interlocked.Increment(ref cancellations) == 4) cancelled.TrySetResult(); });
            await release.Task; return Response();
        }));
        using var cancellation = new CancellationTokenSource();
        var first = Call(http, false, Request(false), cancellation.Token);
        var second = Call(http, false, Request(false), cancellation.Token);
        var tasks = new List<Task> { first, second };
        try
        {
            await Failure("RATE_LIMITED", () => Call(http, false, Request(false)));
            await Failure("RATE_LIMITED", () => http.SendAsync("wy", new HttpRequestMessage(HttpMethod.Get,
                "https://music.163.com/api/song/detail"), default));
            tasks.Add(Call(http, true, Request(true), cancellation.Token));
            tasks.Add(Call(http, true, Request(true), cancellation.Token));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check(tasks.All(t => !t.IsCompleted));
            await Failure("RATE_LIMITED", () => http.SendAsync("tx", new HttpRequestMessage(HttpMethod.Post,
                "https://u.y.qq.com/cgi-bin/musicu.fcg"), default));
            await Failure("RATE_LIMITED", () => Call(http, true, Request(true)));
            Check(calls == 4);
        }
        finally { release.TrySetResult(); }
        foreach (var task in tasks) await Failure("CANCELLED", () => task, cancellation.Token);
        await Call(http, false, Request(false)); await Call(http, true, Request(true)); Check(calls == 6);
    }
    private static async Task Deadlines()
    {
        var cancelled = Signal(); var release = Signal(); int cancellations = 0, calls = 0;
        var watch = Stopwatch.StartNew();
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, token) => {
            Interlocked.Increment(ref calls);
            using var registration = token.Register(() => { if (Interlocked.Increment(ref cancellations) == 2) cancelled.TrySetResult(); });
            await release.Task; return Response();
        }));
        var feedback = Call(http, false, Request(false)); var cloud = Call(http, true, Request(true));
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check(watch.Elapsed >= TimeSpan.FromSeconds(19) && !feedback.IsCompleted && !cloud.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Failure("TIMEOUT", () => feedback); await Failure("TIMEOUT", () => cloud); Check(calls == 2);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        internal bool IsDisposed { get; private set; }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
    private sealed class UnknownContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[] { 1 }, 0, 1);
    }
    private sealed class NonSeekStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class HeldReadStream : MemoryStream
    {
        internal TaskCompletionSource Started { get; } = Signal();
        internal TaskCompletionSource Cancelled { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        internal bool IsDisposed { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult(); await Release.Task; buffer.Span[0] = 1; return 1;
        }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
    private sealed class HeldDisposeStream() : MemoryStream("{}"u8.ToArray())
    {
        internal TaskCompletionSource Disposing { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        internal bool IsDisposed { get; private set; }
        public override async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult(); await Release.Task; await base.DisposeAsync();
        }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }
}

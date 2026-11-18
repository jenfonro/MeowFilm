using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

// Generated progressive bytes over synthetic HTTP, through the current Core transfer.
// No native decoder, output device, Node process or external media is used.
public static class NativeHttpMediaTransferTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("media.http.invalid-uri-and-headers-fail-closed", HttpValidation);
        yield return new("media.http.direct-addresses", HttpPolicy);
        yield return new("media.http.userinfo-read-redirect-and-explicit-authorization", HttpUserInfo);
        yield return new("media.http.long-url-public-read", HttpLongUrl);
        yield return new("media.http.headers-snapshot-and-redirects", HttpRedirectScope);
        yield return new("media.http.redirect-bounds-and-status-safe-errors", HttpSafeErrors);
        yield return new("media.http.adaptive-and-unclassified-content-rejected", HttpUnsupportedContent);
        yield return new("media.http.capped-ranges-exact-bytes-and-bounded-copy", HttpRanges);
        yield return new("media.http.200-unknown-length-and-weak-validator", HttpSequential);
        yield return new("media.http.invalid-initial-range-and-encoding", HttpInvalidRanges);
        yield return new("media.http.changed-validator-range-and-redirect-no-splice", HttpChangedRepresentation);
        yield return new("media.http.truncated-body-is-error-not-eof", HttpTruncation);
        yield return new("media.http.open-read-range-deadlines-and-cancellation", HttpDeadlines);
        yield return new("media.http.complete-copy-budget-nonempty-and-progress", HttpBudget);
        yield return new("media.http.capped-strong-etag-not-last-modified", HttpLastModified);
        yield return new("media.http.stepping-prefix-total-request-deadline-releases", HttpPrefixDeadline);
        yield return new("media.http.probe-uses-request-not-short-read-budget", HttpProbeRequestBudget);
        yield return new("media.http.cap-request-and-read-share-body-budget", HttpRangeReadBudget);
        yield return new("media.http.content-only-headers-fail-before-send-or-publication", HttpContentOnlyHeaders);
    }

    private static Dictionary<string, string> HttpHeaders() => new()
    {
        ["Authorization"] = "Bearer fixture-secret-canary", ["Cookie"] = "session=fixture-cookie-canary",
        ["Referer"] = "https://origin.invalid/fixture", ["User-Agent"] = "meowfilm-fixture",
        ["Origin"] = "https://origin.invalid", ["X-Fixture"] = "fixture-custom-canary"
    };

    private static byte[] HttpWave(int seconds = 3)
    {
        const int rate = 16000;
        int samples = rate * seconds;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (int sample = 0; sample < samples; sample++)
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * sample / rate) * 1024));
        return stream.ToArray();
    }

    private static async Task<byte[]> Copy(Uri uri, IReadOnlyDictionary<string, string> headers,
        NativeHttpMediaOptions? options = null, CancellationToken token = default)
    {
        using var destination = new MemoryStream();
        long copied = await NativeHttpMediaTransfer.CopyCompleteAsync(uri, headers, destination, 1L << 20, options, token);
        Check(copied == destination.Length && destination.CanWrite);
        return destination.ToArray();
    }

    private static async Task HttpValidation()
    {
        byte[] wave = HttpWave();
        await using var server = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, wave, token));
        foreach (var uri in new Uri?[] { null, new("relative", UriKind.Relative), new("ftp://example.invalid/a"),
                     new("data:audio/wav,AAAA"), new("rtsp://example.invalid/a"), new("file:///C:/fixture.wav"),
                     server.Uri("/a#fragment") })
            await ExpectCode(() => Copy(uri!, HttpHeaders()), PlaybackError.UnsupportedUri);
        foreach (string name in new[] { "Host", "Range", "If-Range", "Accept-Encoding", "Connection",
                     "Proxy-Authorization", "Content-Length", "Transfer-Encoding" })
            await ExpectCode(() => Copy(server.Uri("/a"), new Dictionary<string, string> { [name] = "fixture-canary" }),
                PlaybackError.UnsupportedHeaders);
        foreach (var headers in new IReadOnlyDictionary<string, string>[]
        {
            new Dictionary<string, string> { ["X-Fixture\r\nHost"] = "canary" },
            new Dictionary<string, string> { ["Cookie"] = "canary\r\nInjected: secret" },
            new Dictionary<string, string> { ["Cookie"] = "canary\0secret" },
            new Dictionary<string, string> { ["cookie"] = "one", ["Cookie"] = "two" }
        })
            await ExpectCode(() => Copy(server.Uri("/a"), headers), PlaybackError.InvalidHeaders);
        await ExpectCode(() => Copy(server.Uri("/a"), null!), PlaybackError.InvalidHeaders);
        Check(server.Requests.IsEmpty);
        var large = Enumerable.Range(0, 33).ToDictionary(index => "X-Fixture-" + index, _ => "value");
        large["X-" + new string('a', 129)] = new string('b', 17000);
        Check((await Copy(server.Uri("/a"), large)).SequenceEqual(wave));
        Check(server.Requests.All(request => large.All(pair => request.Headers[pair.Key] == pair.Value)));
    }

    private static async Task HttpPolicy()
    {
        byte[] wave = HttpWave();
        await using var server = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, wave, token));
        await using var second = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, wave, token));
        foreach (var uri in new[] { server.Uri("/wave"), new Uri($"http://localhost:{server.Port}/wave"), second.Uri("/wave") })
            Check((await Copy(uri, HttpHeaders())).SequenceEqual(wave));
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
                        else await HttpRangeReply(request, stream, bytes, token, cap: 65536);
                    });
                    fixture = server;
                    var headers = new Dictionary<string, string>();
                    if (explicitHeader) headers["aUtHoRiZaTiOn"] = explicitValue;
                    Check((await Copy(new Uri($"http://{userInfo}@127.0.0.1:{server.Port}/{redirect}"), headers)).SequenceEqual(bytes));
                    Check(server.Requests.Count == (redirect == "none" ? 2 : 3) &&
                        server.Requests.All(request => !request.Path.Contains('@')));
                }
    }

    private static async Task HttpLongUrl()
    {
        byte[] bytes = HttpWave();
        string path = "/wave?fixture=" + new string('x', 17000);
        await using var server = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, bytes, token));
        Check((await Copy(server.Uri(path), new Dictionary<string, string>())).SequenceEqual(bytes) &&
            server.Requests.Count == 1 && server.Requests.Single().Path == path);
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
                await HttpRangeReply(request, stream, bytes, token, cap: bytes.Length / 2);
            }
        });
        first = owner;
        Check((await Copy(owner.Uri("/same"), HttpHeaders())).SequenceEqual(bytes));
        Check((await Copy(owner.Uri("/cross"), HttpHeaders())).SequenceEqual(bytes));
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
        var pending = Copy(snapshot.Uri("/media.wav"), headers, snapshot.Options());
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        headers["Cookie"] = "mutated-canary";
        headers.Clear();
        release.SetResult();
        Check((await pending).SequenceEqual(bytes));
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
            await ExpectCode(() => Copy(server.Uri("/private?token=fixture-url-canary"),
                HttpHeaders(), server.Options()), code);
        }
        await using var loop = new HttpFixture((request, stream, token) => HttpReply(stream, 302,
            new() { ["Location"] = request.Path == "/a" ? "/b" : "/a" }, ReadOnlyMemory<byte>.Empty, token));
        await ExpectCode(() => Copy(loop.Uri("/a"), HttpHeaders(), loop.Options()), PlaybackError.HttpRedirectFailed);
        Check(loop.Requests.Count == 2);
        await using var missing = new HttpFixture((_, stream, token) => HttpReply(stream, 302, new(), ReadOnlyMemory<byte>.Empty, token));
        await ExpectCode(() => Copy(missing.Uri("/a"), HttpHeaders(), missing.Options()), PlaybackError.HttpRedirectFailed);
        await using var many = new HttpFixture((request, stream, token) => HttpReply(stream, 307,
            new() { ["Location"] = request.Path + "x" }, ReadOnlyMemory<byte>.Empty, token));
        await ExpectCode(() => Copy(many.Uri("/a"), HttpHeaders(),
            new NativeHttpMediaOptions(maximumRedirects: 2)), PlaybackError.HttpRedirectFailed);
        Check(many.Requests.Count == 3);
        byte[] bytes = HttpWave();
        await using var target = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, bytes, token));
        await using var redirected = new HttpFixture((_, stream, token) => HttpReply(stream, 302,
            new() { ["Location"] = target.Uri("/media.wav").AbsoluteUri }, ReadOnlyMemory<byte>.Empty, token));
        Check((await Copy(redirected.Uri("/a"), HttpHeaders())).SequenceEqual(bytes) && target.Requests.Count == 1);
    }

    private static async Task HttpUnsupportedContent()
    {
        byte[] wave = HttpWave();
        await using var server = new HttpFixture((request, stream, token) => request.Path switch
        {
            "/mime" => HttpReply(stream, 200, new() { ["Content-Type"] = "application/vnd.apple.mpegurl" }, wave, token),
            "/dash-mime" => HttpReply(stream, 200, new() { ["Content-Type"] = "application/dash+xml" }, wave, token),
            "/smooth-mime" => HttpReply(stream, 200, new() { ["Content-Type"] = "application/vnd.ms-sstr+xml" }, wave, token),
            "/hls" => HttpReply(stream, 200, new() { ["Content-Type"] = "audio/wav" },
                Encoding.UTF8.GetPreamble().Concat("#EXTM3U\n#EXT-X-STREAM-INF:1\nhttp://private.invalid/leak"u8.ToArray()).ToArray(), token),
            "/dash" => HttpReply(stream, 200, new(), "<?xml version=\"1.0\"?><MPD><BaseURL>http://private.invalid</BaseURL></MPD>"u8.ToArray(), token),
            "/smooth" => HttpReply(stream, 200, new(), "<SmoothStreamingMedia/>"u8.ToArray(), token),
            "/html" => HttpReply(stream, 200, new(), "<html>not a media file</html>"u8.ToArray(), token),
            "/redirect" => HttpReply(stream, 302, new() { ["Location"] = "/child.m3u8" }, ReadOnlyMemory<byte>.Empty, token),
            _ => throw new InvalidOperationException("Unexpected unsupported-content fixture request")
        });
        foreach (string path in new[] { "/a.m3u8", "/a.MPD", "/a.%6d3u8", "/a.m3u", "/a.ism", "/a.isml" })
            await ExpectCode(() => Copy(server.Uri(path), HttpHeaders(), server.Options()), PlaybackError.UnsupportedAdaptive);
        Check(server.Requests.IsEmpty);
        foreach (string path in new[] { "/mime", "/dash-mime", "/smooth-mime", "/hls", "/dash", "/smooth", "/redirect" })
            await ExpectCode(() => Copy(server.Uri(path), HttpHeaders(), server.Options()), PlaybackError.UnsupportedAdaptive);
        await ExpectCode(() => Copy(server.Uri("/html"), HttpHeaders(), server.Options()), PlaybackError.HttpFormatUnsupported);
        Check(server.Requests.Count == 8 && !server.Requests.Any(request => request.Path == "/child.m3u8"));
    }

    private static async Task HttpRanges()
    {
        byte[] bytes = HttpWave(12);
        await using var capped = new HttpFixture((request, stream, token) =>
            HttpRangeReply(request, stream, bytes, token, cap: 4096));
        using var destination = new MemoryStream();
        long? length = null;
        var progress = new List<long>();
        long copied = await NativeHttpMediaTransfer.CopyCompleteAsync(capped.Uri("/media"), HttpHeaders(),
            destination, bytes.Length, capped.Options(), progress: progress.Add, opened: value => length = value);
        Check(copied == bytes.Length && length == bytes.Length && destination.ToArray().SequenceEqual(bytes));
        var requests = capped.Requests.ToArray();
        Check(requests.Length == (bytes.Length + 4095) / 4096 &&
            requests.Select((request, index) => request.Headers["Range"] == $"bytes={index * 4096}-").All(value => value));
        Check(requests.Skip(1).All(request => request.Headers["If-Range"] == "\"fixture-v1\"") &&
            requests.All(request => request.Headers["Accept-Encoding"] == "identity"));
        Check(progress.Count > 1 && progress[^1] == bytes.Length &&
            progress.Zip(progress.Prepend(0)).All(pair => pair.First > pair.Second && pair.First - pair.Second <= 65536));
        foreach (string? tag in new[] { null, "W/\"fixture-v1\"" })
        {
            await using var unvalidated = new HttpFixture((request, stream, token) =>
                HttpRangeReply(request, stream, bytes, token, tag: tag, cap: 4096,
                    modified: "Wed, 21 Oct 2015 07:28:00 GMT"));
            using var rejected = new MemoryStream();
            await ExpectCode(() => NativeHttpMediaTransfer.CopyCompleteAsync(unvalidated.Uri("/a"),
                HttpHeaders(), rejected, bytes.Length), PlaybackError.HttpInvalidRange);
            Check(rejected.Length == 0 && unvalidated.Requests.Count == 1);
        }
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
                else await HttpRangeReply(request, stream, bytes, token, tag: mode == "weak" ? "W/\"fixture-v1\"" : null);
            });
            using var destination = new MemoryStream();
            long? length = -1;
            await NativeHttpMediaTransfer.CopyCompleteAsync(server.Uri("/a"), HttpHeaders(), destination,
                bytes.Length, server.Options(), opened: value => length = value);
            Check((mode == "unknown" ? length is null : length == bytes.Length) &&
                destination.ToArray().SequenceEqual(bytes) && server.Requests.Count == 1);
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
            await ExpectCode(() => Copy(server.Uri("/media"), HttpHeaders(), server.Options()), PlaybackError.HttpInvalidRange);
        }
        await using var encoded = new HttpFixture((_, stream, token) => HttpReply(stream, 200,
            new() { ["Content-Encoding"] = "gzip" }, bytes, token));
        await ExpectCode(() => Copy(encoded.Uri("/media"), HttpHeaders(), encoded.Options()), PlaybackError.HttpEncodingUnsupported);
        await using var multipart = new HttpFixture((_, stream, token) => HttpReply(stream, 206,
            new() { ["Content-Type"] = "multipart/byteranges; boundary=fixture" }, bytes, token));
        await ExpectCode(() => Copy(multipart.Uri("/media"), HttpHeaders(), multipart.Options()), PlaybackError.HttpInvalidRange);
    }

    private static async Task HttpChangedRepresentation()
    {
        byte[] bytes = HttpWave();
        foreach (string mode in new[] { "ignored", "tag", "missing-tag", "missing-tag-capped", "weak-tag-capped",
                     "length", "offset", "redirect", "416" })
        {
            await using var server = new HttpFixture(async (request, stream, token) =>
            {
                if (!request.Headers.ContainsKey("If-Range"))
                    await HttpRangeReply(request, stream, bytes, token, cap: 1024,
                        modified: "Wed, 21 Oct 2015 07:28:00 GMT");
                else if (mode == "ignored") await HttpReply(stream, 200, new(), bytes, token);
                else if (mode == "redirect")
                    await HttpReply(stream, 302, new() { ["Location"] = "/new-object" }, ReadOnlyMemory<byte>.Empty, token);
                else if (mode == "416")
                    await HttpReply(stream, 416, new() { ["Content-Range"] = $"bytes */{bytes.Length}" }, ReadOnlyMemory<byte>.Empty, token);
                else if (mode == "offset")
                    await HttpRangeReply(request with { Headers = new(request.Headers) { ["Range"] = "bytes=0-" } },
                        stream, bytes, token);
                else await HttpRangeReply(request, stream, mode == "length" ? bytes[..^1] : bytes, token,
                    tag: mode == "tag" ? "\"fixture-v2\"" : mode.StartsWith("missing-tag", StringComparison.Ordinal) ? null :
                        mode == "weak-tag-capped" ? "W/\"fixture-v1\"" : "\"fixture-v1\"",
                    cap: mode.EndsWith("-capped", StringComparison.Ordinal) ? 1024 : null);
            });
            using var destination = new MemoryStream();
            string code = mode switch
            {
                "416" => PlaybackError.HttpRangeRejected, "offset" => PlaybackError.HttpInvalidRange,
                _ => PlaybackError.HttpRepresentationChanged
            };
            await ExpectCode(() => NativeHttpMediaTransfer.CopyCompleteAsync(server.Uri("/a"), HttpHeaders(),
                destination, bytes.Length, server.Options()), code);
            Check(destination.Length == 1024 && destination.ToArray().SequenceEqual(bytes.Take(1024)) &&
                server.Requests.Count == 2 && !server.Requests.Any(request => request.Path == "/new-object"));
        }
    }

    private static async Task HttpTruncation()
    {
        byte[] bytes = HttpWave();
        await using var server = new HttpFixture((_, stream, token) => HttpReply(stream, 200,
            new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..1024], token));
        using var destination = new MemoryStream();
        await ExpectCode(() => NativeHttpMediaTransfer.CopyCompleteAsync(server.Uri("/a"), HttpHeaders(),
            destination, bytes.Length, server.Options()), PlaybackError.HttpTruncated);
        Check(destination.Length == 1024 && destination.ToArray().SequenceEqual(bytes.Take(1024)));
    }

    private static async Task HttpDeadlines()
    {
        byte[] bytes = HttpWave();
        int requests = 0;
        var cancelledRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var headers = new HttpFixture(async (_, stream, token) =>
        {
            if (Interlocked.Increment(ref requests) == 2) cancelledRequest.TrySetResult();
            await ClientClosed(stream, token);
        }))
        {
            await ExpectCode(() => Copy(headers.Uri("/a"), HttpHeaders(),
                headers.Options(TimeSpan.FromMilliseconds(200))), PlaybackError.HttpDeadline);
            using var cancelled = new CancellationTokenSource();
            var opening = Copy(headers.Uri("/a"), HttpHeaders(), headers.Options(), cancelled.Token);
            await cancelledRequest.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancelled.Cancel();
            await ExpectCancelled(() => opening.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        await using (var probe = new HttpFixture(async (_, stream, token) =>
        {
            await HttpReply(stream, 200, new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..4], token);
            await ClientClosed(stream, token);
        }))
            await ExpectCode(() => Copy(probe.Uri("/a"), HttpHeaders(),
                probe.Options(requestTimeout: TimeSpan.FromMilliseconds(200))), PlaybackError.HttpDeadline);
        foreach (bool cancel in new[] { false, true })
        {
            await using var body = new HttpFixture(async (_, stream, token) =>
            {
                await HttpReply(stream, 200, new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..512], token);
                await ClientClosed(stream, token);
            });
            using var cancellation = new CancellationTokenSource();
            using var destination = new MemoryStream();
            var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var copying = NativeHttpMediaTransfer.CopyCompleteAsync(body.Uri("/a"), HttpHeaders(), destination,
                bytes.Length, body.Options(readTimeout: cancel ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(200)),
                cancellation.Token, progress: _ => written.TrySetResult());
            await written.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (cancel)
            {
                cancellation.Cancel();
                await ExpectCancelled(() => copying.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else await ExpectCode(() => copying.WaitAsync(TimeSpan.FromSeconds(2)), PlaybackError.HttpDeadline);
            Check(destination.Length == 512 && destination.ToArray().SequenceEqual(bytes.Take(512)));
        }
        await using var rangeServer = new HttpFixture(async (request, stream, token) =>
        {
            if (request.Headers.ContainsKey("If-Range")) await ClientClosed(stream, token);
            else await HttpRangeReply(request, stream, bytes, token, cap: 1024);
        });
        using var partial = new MemoryStream();
        await ExpectCode(() => NativeHttpMediaTransfer.CopyCompleteAsync(rangeServer.Uri("/a"), HttpHeaders(),
            partial, bytes.Length, rangeServer.Options(readTimeout: TimeSpan.FromMilliseconds(300))), PlaybackError.HttpDeadline);
        Check(partial.Length == 1024 && rangeServer.Requests.Count == 2);
    }

    private static async Task HttpLastModified()
    {
        byte[] bytes = HttpWave();
        foreach (string? nextModified in new[] { null, "Thu, 22 Oct 2015 07:28:00 GMT" })
        {
            await using var server = new HttpFixture((request, stream, token) =>
                HttpRangeReply(request, stream, bytes, token, cap: 4096,
                    modified: request.Headers.ContainsKey("If-Range") ? nextModified : "Wed, 21 Oct 2015 07:28:00 GMT"));
            Check((await Copy(server.Uri("/media"), HttpHeaders())).SequenceEqual(bytes));
            Check(server.Requests.Count == (bytes.Length + 4095) / 4096 &&
                server.Requests.Skip(1).All(request => request.Headers["If-Range"] == "\"fixture-v1\""));
        }
    }

    private static async Task HttpPrefixDeadline()
    {
        byte[] bytes = HttpWave();
        var prefixSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sent = 0, opened = 0, progress = 0;
        await using var server = new HttpFixture(async (_, stream, token) =>
        {
            Task disconnected = ClientClosed(stream, token);
            try
            {
                await HttpReply(stream, 200, new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..12], token);
                Interlocked.Exchange(ref sent, 12);
                for (int offset = 12; offset < 512 && !disconnected.IsCompleted; offset++)
                {
                    await stream.WriteAsync(bytes.AsMemory(offset, 1), token);
                    Interlocked.Increment(ref sent);
                    prefixSent.TrySetResult();
                    // Each step is within ReadTimeout, but the whole probe exceeds RequestTimeout.
                    await Task.Delay(TimeSpan.FromMilliseconds(25), token);
                }
            }
            catch (IOException) { /* Deadline closes a response while the server is writing. */ }
            finally
            {
                await disconnected;
                closed.TrySetResult();
            }
        });
        using var caller = new CancellationTokenSource();
        using var destination = new MemoryStream();
        Task copying = NativeHttpMediaTransfer.CopyCompleteAsync(server.Uri("/media"), HttpHeaders(), destination,
            bytes.Length, server.Options(requestTimeout: TimeSpan.FromMilliseconds(300),
                readTimeout: TimeSpan.FromMilliseconds(100)), caller.Token,
            progress: _ => Interlocked.Increment(ref progress), opened: _ => Interlocked.Increment(ref opened));
        try
        {
            await prefixSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await ExpectCode(() => copying.WaitAsync(TimeSpan.FromSeconds(3)), PlaybackError.HttpDeadline);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(sent > 12 && sent < 512 && server.Requests.Count == 1 &&
                destination.Length == 0 && opened == 0 && progress == 0);
        }
        finally { caller.Cancel(); }
    }

    private static async Task HttpProbeRequestBudget()
    {
        byte[] bytes = HttpWave();
        var prefixSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new HttpFixture(async (_, stream, token) =>
        {
            await HttpReply(stream, 200, new() { ["Content-Length"] = bytes.Length.ToString() }, bytes[..12], token);
            prefixSent.TrySetResult();
            // Explicit server latency after a delivered prefix, not a guess about client state.
            await Task.Delay(TimeSpan.FromMilliseconds(300), token);
            await stream.WriteAsync(bytes.AsMemory(12), token);
        });
        Task<byte[]> copying = Copy(server.Uri("/media"), HttpHeaders(),
            server.Options(requestTimeout: TimeSpan.FromSeconds(2), readTimeout: TimeSpan.FromMilliseconds(100)));
        await prefixSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check((await copying.WaitAsync(TimeSpan.FromSeconds(3))).SequenceEqual(bytes) && server.Requests.Count == 1);
    }

    private static async Task HttpRangeReadBudget()
    {
        byte[] bytes = HttpWave();
        var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var server = new HttpFixture(async (request, stream, token) =>
        {
            if (!request.Headers.ContainsKey("If-Range"))
                await HttpRangeReply(request, stream, bytes, token, cap: 1024);
            else
            {
                continuation.TrySetResult();
                // The established continuation takes longer than RequestTimeout, but fits ReadTimeout.
                await Task.Delay(TimeSpan.FromMilliseconds(400), token);
                await HttpRangeReply(request, stream, bytes, token);
            }
        }))
        {
            Task<byte[]> copying = Copy(server.Uri("/media"), HttpHeaders(),
                server.Options(requestTimeout: TimeSpan.FromMilliseconds(200), readTimeout: TimeSpan.FromSeconds(2)));
            await continuation.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check((await copying.WaitAsync(TimeSpan.FromSeconds(3))).SequenceEqual(bytes) && server.Requests.Count == 2);
        }

        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var shared = new HttpFixture(async (request, stream, token) =>
        {
            if (!request.Headers.ContainsKey("If-Range"))
            {
                await HttpRangeReply(request, stream, bytes, token, cap: 1024);
                return;
            }
            Check(request.Headers["Range"] == "bytes=1024-" && request.Headers["If-Range"] == "\"fixture-v1\"");
            Task disconnected = ClientClosed(stream, token);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), token);
                await HttpReply(stream, 206, new()
                {
                    ["Content-Type"] = "audio/wav", ["Content-Length"] = (bytes.Length - 1024).ToString(),
                    ["Content-Range"] = $"bytes 1024-{bytes.Length - 1}/{bytes.Length}", ["ETag"] = "\"fixture-v1\""
                }, ReadOnlyMemory<byte>.Empty, token);
                headersSent.TrySetResult();
                // Request and body each fit 600 ms separately; their sum must not get two budgets.
                Task bodyReady = Task.Delay(TimeSpan.FromMilliseconds(400), token);
                if (await Task.WhenAny(disconnected, bodyReady) == bodyReady)
                    await stream.WriteAsync(bytes.AsMemory(1024), token);
            }
            catch (IOException) { /* The shared body deadline physically closes the continuation. */ }
            finally
            {
                await disconnected;
                closed.TrySetResult();
            }
        });
        using var caller = new CancellationTokenSource();
        using var destination = new MemoryStream();
        try
        {
            Task copying = NativeHttpMediaTransfer.CopyCompleteAsync(shared.Uri("/media"), HttpHeaders(), destination,
                bytes.Length, shared.Options(requestTimeout: TimeSpan.FromSeconds(2),
                    readTimeout: TimeSpan.FromMilliseconds(600)), caller.Token);
            await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await ExpectCode(() => copying.WaitAsync(TimeSpan.FromSeconds(3)), PlaybackError.HttpDeadline);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(shared.Requests.Count == 2 && destination.Length == 1024 &&
                destination.ToArray().SequenceEqual(bytes.Take(1024)));
        }
        finally { caller.Cancel(); }
    }

    private static async Task HttpContentOnlyHeaders()
    {
        byte[] bytes = HttpWave();
        await using var server = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, bytes, token));
        foreach (var (name, value) in new[] { ("Allow", "GET"), ("Expires", "Wed, 21 Oct 2015 07:28:00 GMT") })
        {
            int opened = 0, progress = 0;
            using var destination = new MemoryStream();
            await ExpectCode(() => NativeHttpMediaTransfer.CopyCompleteAsync(server.Uri("/media"),
                new Dictionary<string, string> { [name] = value }, destination, bytes.Length,
                progress: _ => progress++, opened: _ => opened++), PlaybackError.UnsupportedHeaders);
            Check(server.Requests.IsEmpty && destination.Length == 0 && opened == 0 && progress == 0);
        }
    }

    private static async Task HttpBudget()
    {
        byte[] bytes = HttpWave();
        await using var known = new HttpFixture((request, stream, token) => HttpRangeReply(request, stream, bytes, token));
        using var rejected = new MemoryStream();
        await ExpectCode(() => NativeHttpMediaTransfer.CopyCompleteAsync(known.Uri("/a"), HttpHeaders(),
            rejected, 0), PlaybackError.InvalidRequest);
        Check(known.Requests.IsEmpty && rejected.Length == 0);
        await ExpectCode(() => NativeHttpMediaTransfer.CopyCompleteAsync(known.Uri("/a"), HttpHeaders(),
            rejected, bytes.Length - 1), PlaybackError.InvalidRequest);
        Check(rejected.Length == 0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ExpectCancelled(() => NativeHttpMediaTransfer.CopyCompleteAsync(known.Uri("/a"), HttpHeaders(),
            rejected, bytes.Length, token: cancelled.Token));
        Check(known.Requests.Count == 1 && rejected.Length == 0);
        await using var unknown = new HttpFixture(async (_, stream, token) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
            await stream.WriteAsync(bytes, token);
        });
        await ExpectCode(() => NativeHttpMediaTransfer.CopyCompleteAsync(unknown.Uri("/a"), HttpHeaders(),
            rejected, 512), PlaybackError.InvalidRequest);
        Check(rejected.Length == 512 && rejected.ToArray().SequenceEqual(bytes.Take(512)));
        await using var empty = new HttpFixture((_, stream, token) => HttpReply(stream, 200, new(), ReadOnlyMemory<byte>.Empty, token));
        using var noBytes = new MemoryStream();
        await ExpectCode(() => NativeHttpMediaTransfer.CopyCompleteAsync(empty.Uri("/a"), HttpHeaders(),
            noBytes, bytes.Length), PlaybackError.HttpFormatUnsupported);
        Check(noBytes.Length == 0);
        using var complete = new MemoryStream();
        var progress = new List<long>();
        int opened = 0;
        long count = await NativeHttpMediaTransfer.CopyCompleteAsync(known.Uri("/a"), HttpHeaders(), complete,
            bytes.Length, progress: progress.Add, opened: length => { Check(length == bytes.Length); opened++; });
        Check(count == bytes.Length && opened == 1 && progress.Count > 0 && progress[^1] == count &&
            progress.Zip(progress.Prepend(0)).All(pair => pair.First > pair.Second && pair.First - pair.Second <= 65536) &&
            complete.ToArray().SequenceEqual(bytes));
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
                    client.NoDelay = true;
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
        CancellationToken token, string? tag = "\"fixture-v1\"", int? cap = null, string? modified = null)
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
        if (modified is not null) headers["Last-Modified"] = modified;
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

    private static async Task ClientClosed(NetworkStream stream, CancellationToken token)
    {
        try { Check(await stream.ReadAsync(new byte[1], token) == 0); }
        catch (IOException) { /* A TCP reset also confirms actual client-side release. */ }
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

}

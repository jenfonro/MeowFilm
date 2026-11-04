using System.IO;
using System.Net;
using System.Net.Http;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicSourceNetworkDownloadTests
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private const string Address = "https://sources.invalid/scripts/Source.JS?download=1&revision=2";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    public static IEnumerable<TestCase> Cases()
    {
        foreach (var (name, address) in new[]
        {
            ("http", "http://sources.invalid/source.js"),
            ("non-js-path", "https://sources.invalid/source.json"),
            ("js-only-in-query", "https://sources.invalid/download?file=source.js"),
            ("userinfo", "https://user:password@sources.invalid/source.js"),
            ("fragment", "https://sources.invalid/source.js#part"),
            ("encoded-forward-slash-in-name", "https://sources.invalid/source%2Fother.js"),
            ("encoded-backslash-in-name", "https://sources.invalid/source%5cother.js")
        })
        {
            yield return Case("rejects-" + name + "-before-http", async () =>
            {
                using var handler = new Handler((_, _) =>
                    throw new InvalidOperationException("An invalid source address reached HTTP"));
                using var download = new MusicSourceNetworkDownload(handler);
                await Failure("INVALID_SOURCE_URL", () => download.ReadAsync(address, default));
                Check(handler.Calls == 0, "Invalid source addresses must be rejected before HTTP");
                Check(!MusicSourceNetworkDownload.TryAddress(address, out _, out _),
                    "The address validator must reject " + name);
            });
        }

        yield return Case("reads-encoded-directory-separator-with-safe-basename", async () =>
        {
            const string address = "https://sources.invalid/scripts%2fother/source.js";
            byte[] bytes = [1, 2, 3];
            using var input = new TrackedStream(bytes);
            using var response = new TrackedResponse(input, bytes.Length);
            using var handler = new Handler((request, _) =>
            {
                Check(request.RequestUri?.OriginalString == address,
                    "The encoded directory separator must remain in the HTTP address");
                return Task.FromResult<HttpResponseMessage>(response);
            });
            using var download = new MusicSourceNetworkDownload(handler);

            Check(MusicSourceNetworkDownload.TryAddress(address, out _, out var basename) &&
                basename == "source.js", "A safe basename remains available after directory decoding");
            var result = await download.ReadAsync(address, default);

            Check(handler.Calls == 1, "The accepted address must make exactly one HTTP request");
            Check(result.Basename == "source.js" && result.Bytes.SequenceEqual(bytes),
                "The script is read using only its safe basename");
            Check(response.Disposed && input.Disposed, "Successful reads release the response and stream");
        });

        foreach (bool declaredLength in new[] { false, true })
        {
            yield return Case("reads-exactly-16-mib-" + (declaredLength ? "with-length" : "without-length"), async () =>
            {
                byte[] bytes = new byte[MaximumBytes];
                for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 31 + 17);
                using var input = new TrackedStream(bytes);
                using var response = new TrackedResponse(input, declaredLength ? MaximumBytes : null);
                using var handler = new Handler((request, _) =>
                {
                    Check(request.Method == HttpMethod.Get, "Source downloads use GET");
                    Check(request.RequestUri == new Uri(Address), "HTTPS script queries must be preserved");
                    return Task.FromResult<HttpResponseMessage>(response);
                });
                using var download = new MusicSourceNetworkDownload(handler);

                Check(MusicSourceNetworkDownload.TryAddress(Address, out var uri, out var basename),
                    "Standard HTTPS .js addresses with a query are allowed");
                Check(uri == new Uri(Address) && basename == "Source.JS",
                    "The query is not part of the script filename");
                Check(declaredLength || response.Content.Headers.ContentLength is null,
                    "The streaming boundary fixture must not advertise a length");

                var result = await download.ReadAsync(Address, default);

                Check(handler.Calls == 1, "A download makes one controlled HTTP request");
                Check(result.Basename == "Source.JS" && result.Bytes.SequenceEqual(bytes),
                    "Exactly 16 MiB must remain readable without truncation or corruption");
                Check(response.Disposed && input.Disposed, "Successful reads release the response and stream");
            });
        }

        yield return Case("rejects-oversize-header-without-reading-the-body", async () =>
        {
            using var input = new TrackedStream([1]);
            using var response = new TrackedResponse(input, MaximumBytes + 1L);
            using var download = Download(response);

            await Failure("LIMIT", () => download.ReadAsync(Address, default));

            Check(input.ReadCalls == 0, "An oversize Content-Length must fail before reading the body");
            Check(response.Disposed && input.Disposed, "Header rejection releases the response and stream");
        });

        yield return Case("rejects-unknown-length-stream-above-16-mib", async () =>
        {
            using var input = new TrackedStream(new byte[MaximumBytes + 1]);
            using var response = new TrackedResponse(input);
            using var download = Download(response);
            Check(response.Content.Headers.ContentLength is null, "This response must have no Content-Length");

            await Failure("LIMIT", () => download.ReadAsync(Address, default));

            Check(input.ReadCalls > 0, "The limit must also be enforced while streaming an unknown-length body");
            Check(response.Disposed && input.Disposed, "Streaming limit rejection releases owned resources");
        });

        foreach (var (name, actualLength, declaredLength) in new (string, int, long?)[]
        {
            ("empty-unknown-length", 0, null),
            ("empty-declared-length", 0, 0),
            ("premature-eof", 7, 8),
            ("more-than-declared", 8, 7)
        })
        {
            yield return Case("rejects-" + name, async () =>
            {
                using var input = new TrackedStream(new byte[actualLength]);
                using var response = new TrackedResponse(input, declaredLength);
                using var download = Download(response);

                await Failure("SOURCE_DOWNLOAD_FAILED", () => download.ReadAsync(Address, default));

                Check(response.Disposed && input.Disposed, "Invalid body rejection releases the response and stream");
            });
        }

        yield return Case("reports-http-transport-failure", async () =>
        {
            using var handler = new Handler((_, _) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("Controlled network failure")));
            using var download = new MusicSourceNetworkDownload(handler);

            await Failure("SOURCE_DOWNLOAD_FAILED", () => download.ReadAsync(Address, default));

            Check(handler.Calls == 1, "The failure originates from the controlled transport");
        });

        yield return Case("reports-stream-io-failure-and-disposes", async () =>
        {
            using var input = new TrackedStream([1], new IOException("Controlled body read failure"));
            using var response = new TrackedResponse(input);
            using var download = Download(response);

            await Failure("SOURCE_DOWNLOAD_FAILED", () => download.ReadAsync(Address, default));

            Check(input.ReadCalls > 0, "The controlled body read must be attempted");
            Check(response.Disposed && input.Disposed, "I/O failure releases the response and stream");
        });

        yield return Case("reports-transport-timeout-without-caller-cancellation", async () =>
        {
            using var handler = new Handler((_, _) =>
                Task.FromException<HttpResponseMessage>(new TaskCanceledException("Controlled transport timeout")));
            using var download = new MusicSourceNetworkDownload(handler);

            await Failure("TIMEOUT", () => download.ReadAsync(Address, default));
        });

        yield return Case("caller-cancellation-drains-active-read-before-releasing-resources", async () =>
        {
            using var input = new HeldReadStream();
            using var response = new TrackedResponse(input);
            using var download = Download(response);
            using var cancel = new CancellationTokenSource();
            var pending = download.ReadAsync(Address, cancel.Token);
            var outcome = Outcome(pending);
            try
            {
                await input.Entered.Task.WaitAsync(Wait);
                cancel.Cancel();
                await input.Cancelled.Task.WaitAsync(Wait);

                Check(!pending.IsCompleted, "Cancellation must not detach the physical body read");
                Check(!response.Disposed && !input.Disposed,
                    "Resources must not be released while the body read is still active");
            }
            finally
            {
                cancel.Cancel();
                input.Release.TrySetResult();
                await outcome.WaitAsync(Wait);
            }

            Check(await outcome is OperationCanceledException,
                "Caller cancellation must remain cancellation, not timeout or a successful script");
            Check(input.Ended.Task.IsCompleted && !input.DisposedWhileReading,
                "The physical read must end before stream disposal");
            Check(response.Disposed && input.Disposed, "Cancellation releases both the response and stream");
        });
    }

    private static TestCase Case(string name, Func<Task> run) => new("music.source.download." + name, run);

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static MusicSourceNetworkDownload Download(HttpResponseMessage response) =>
        new(new Handler((_, _) => Task.FromResult(response)));

    private static async Task Failure(string code, Func<Task> run)
    {
        try { await run(); }
        catch (MusicOperationException error)
        {
            Check(error.Code == code, "Expected " + code + ", got " + error.Code);
            return;
        }
        throw new InvalidOperationException("Expected " + code);
    }

    private static async Task<Exception?> Outcome(Task pending)
    {
        try { await pending; return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return send(request, token);
        }
    }

    private sealed class TrackedResponse : HttpResponseMessage
    {
        internal bool Disposed { get; private set; }
        internal TrackedResponse(Stream stream, long? declaredLength = null) : base(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream);
            if (declaredLength is { } length) Content.Headers.ContentLength = length;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private class TrackedStream(byte[] bytes, Exception? readFailure = null) : MemoryStream(bytes, writable: false)
    {
        internal bool Disposed { get; private set; }
        internal int ReadCalls { get; private set; }
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return readFailure is null
                ? base.ReadAsync(buffer, cancellationToken)
                : ValueTask.FromException<int>(readFailure);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class HeldReadStream() : TrackedStream([])
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool DisposedWhileReading { get; private set; }
        private bool read;
        private bool reading;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) return 0;
            read = true;
            reading = true;
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            try
            {
                // Hold the physical read after cancellation, then return late data rather than throwing.
                await Release.Task.ConfigureAwait(false);
                buffer.Span[0] = 1;
                return 1;
            }
            finally
            {
                reading = false;
                Ended.TrySetResult();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && reading) DisposedWhileReading = true;
            base.Dispose(disposing);
        }
    }
}

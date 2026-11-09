using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class RuntimeImageTests
{
    private const string Reference = "/v1/tmdb/images/w500/poster.jpg";
    private const string Session = "b72dd937-0020-47de-9f03-6c9c5c64b7ef";
    private static readonly string Key = new('a', 64); // Synthetic session identity.
    // Independently encoded synthetic RGB poster fixture, not production metadata.
    internal static byte[] Png => Convert.FromBase64String(
        "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");
    private static RuntimeHttpClient Client(RuntimeHttpClientTests.Server server) => new(server.BaseUrl, Session, Key);

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("runtime-image: relative reference sends exact session auth and unique request IDs", async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var client = Client(server);
            var ids = new HashSet<string>();
            foreach (string reference in new[] { Reference, "/v1/tmdb/images/w300/poster.jpg" })
            {
                var read = client.ReadImageAsync(reference, ArtworkService.MaximumBytes);
                await using var exchange = await server.AcceptAsync();
                BridgeTests.Equal("GET", exchange.Method);
                BridgeTests.Equal(reference, exchange.Path);
                BridgeTests.Equal("Bearer " + Key, exchange.Headers["Authorization"]);
                BridgeTests.Equal(Session, exchange.Headers["X-Meow-Session"]);
                BridgeTests.Equal(true, ids.Add(exchange.Headers["X-Meow-Request-Id"]));
                BridgeTests.Equal(0, exchange.Body.Length);
                await exchange.ReplyAsync(Png, contentType: "image/png");
                byte[] received = await read;
                BridgeTests.Equal(true, Png.SequenceEqual(received));
            }
        });
        yield return new("runtime-image: absolute external network traversal and query references never connect", async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var client = Client(server);
            foreach (string reference in new[] { "https://example.test/poster.jpg", server.BaseUrl + Reference,
                "//example.test/a", "/v1/tmdb/images/w500/../poster.jpg", "/v1/tmdb/images/w500/%2e%2e.jpg",
                Reference + "?key=fixture", Reference + "#fragment", "/v1/douban/images/poster.jpg",
                "/v1/tmdb/images/w0/poster.jpg", "/v1/tmdb/images/w500/a\\poster.jpg", Reference + "\n" })
                await BridgeTests.Expect("INVALID_REQUEST", () => client.ReadImageAsync(reference, ArtworkService.MaximumBytes));
            BridgeTests.Equal(false, server.HasConnection);
        });
        yield return new("runtime-image: ordinary JSON rejects 502 while image failures preserve safe codes", async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var client = Client(server);
            foreach (var (method, contract, code, status) in new[]
            {
                ("json", "tmdb.data.v1", "UPSTREAM_HTTP", 502),
                ("image", "tmdb.data.v1", "UPSTREAM_FORMAT", 502),
                ("image", "runtime.http.v1", "PERMISSION_DENIED", 403)
            })
            {
                Task operation = method == "image" ? client.ReadImageAsync(Reference, ArtworkService.MaximumBytes) :
                    ReadJsonFailure();
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(new { ok = false, contract, code }), status);
                await BridgeTests.Expect(method == "json" ? "INVALID_RESPONSE" : code, () => operation);
            }
            async Task ReadJsonFailure()
            {
                var result = await client.CallAsync("tmdb.data.request");
                if (!result.GetProperty("ok").GetBoolean()) throw new BridgeException(result.GetProperty("code").GetString()!);
                throw new InvalidOperationException("Expected real failure envelope");
            }
        });
        yield return new("runtime-image: redirect wrong response ID and wrong MIME cannot become bytes", async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var target = new RuntimeHttpClientTests.Server();
            await using var client = Client(server);
            foreach (var (status, requestId, mime, extra) in new[]
            {
                (307, (string?)null, "image/png", "Location: " + target.BaseUrl + Reference + "\r\n"),
                (200, (string?)Guid.NewGuid().ToString("D"), "image/png", ""),
                (200, (string?)"duplicate", "image/png", ""),
                (200, (string?)null, "application/json", "")
            })
            {
                var read = client.ReadImageAsync(Reference, ArtworkService.MaximumBytes);
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(Png, status, requestId, mime, extra);
                await BridgeTests.Expect("INVALID_RESPONSE", () => read);
                BridgeTests.Equal(false, target.HasConnection);
            }
        });
        yield return new("runtime-image: content length and chunked reads enforce the existing four MiB boundary", async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var client = Client(server);
            var declared = client.ReadImageAsync(Reference, ArtworkService.MaximumBytes);
            await using (var exchange = await server.AcceptAsync())
                await exchange.BeginImageAsync(ArtworkService.MaximumBytes + 1);
            await BridgeTests.Expect("RESOURCE_LIMIT", () => declared);
            var streamed = client.ReadImageAsync(Reference, ArtworkService.MaximumBytes);
            await using (var exchange = await server.AcceptAsync())
            {
                // The server may see a reset as soon as the bounded reader rejects the body.
                try { await exchange.ReplyChunkedAsync(new byte[ArtworkService.MaximumBytes + 1]); }
                catch (System.IO.IOException) { }
            }
            await BridgeTests.Expect("RESOURCE_LIMIT", () => streamed);
        });
        yield return new("runtime-image: truncated bytes fail without retry or successful artwork caching", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var client = Client(server);
            await using var artwork = new ArtworkService(readTrusted: client.ReadImageAsync);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var lease = artwork.AcquireAsync(Reference);
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(Png[..12], contentType: "image/png", declaredBytes: Png.Length);
                try { using var unexpected = await lease; throw new InvalidOperationException("Truncated picture was cached"); }
                catch (ArtworkException) { }
            }
            BridgeTests.Equal(false, server.HasConnection);
        }));
        yield return new("runtime-image: artwork rejects invalid response IDs and nonimages without poisoning cache", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var client = Client(server);
            await using var artwork = new ArtworkService(readTrusted: client.ReadImageAsync);
            var wrongId = artwork.AcquireAsync(Reference);
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync(Png, requestId: Guid.NewGuid().ToString("D"), contentType: "image/png");
            try { using var ignored = await wrongId; throw new InvalidOperationException("Wrong identity became pixels"); }
            catch (ArtworkException) { }
            var invalidPixels = artwork.AcquireAsync(Reference);
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync("<html>not pixels</html>"u8.ToArray(), contentType: "image/png");
            try { using var ignored = await invalidPixels; throw new InvalidOperationException("Invalid image became pixels"); }
            catch (ArtworkException) { }
            var valid = artwork.AcquireAsync(Reference);
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync(Png, contentType: "image/png");
            using var lease = await valid;
            CheckPixels(lease);
            using var cached = await artwork.AcquireAsync(Reference);
            BridgeTests.Equal(true, ReferenceEquals(lease.Image, cached.Image));
            BridgeTests.Equal(false, server.HasConnection);
        }));
        yield return new("runtime-image: complete HTTP images missing format terminators fail artwork and explicit same-reference retry succeeds", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var client = Client(server);
            int reads = 0;
            await using var artwork = new ArtworkService(readTrusted: (reference, budget, token) =>
            { reads++; return client.ReadImageAsync(reference, budget, token); });
            foreach (var (format, trailer) in new[] { ("png", 12), ("jpeg", 2), ("gif", 1) })
            {
                byte[] complete = format == "png" ? Png : ArtworkTests.Encode(format, 32, 48);
                string reference = "/v1/tmdb/images/w500/incomplete." + format;
                if (format == "png")
                    BridgeTests.Equal(true, complete.AsSpan(complete.Length - 8, 4).SequenceEqual("IEND"u8));
                int before = reads;
                var incomplete = artwork.AcquireAsync(reference);
                await using (var exchange = await server.AcceptAsync())
                {
                    // Accurate HTTP length, complete image data, missing only the format trailer.
                    // This is not the independent short-HTTP-body or invalid-magic scenario.
                    await exchange.ReplyAsync(complete[..^trailer], contentType: "image/" + format);
                }
                try { using var ignored = await incomplete; throw new InvalidOperationException("Partial image became cached pixels"); }
                catch (ArtworkException error) { BridgeTests.Equal("ARTWORK_FORMAT", error.Code); }
                BridgeTests.Equal(before + 1, reads);
                BridgeTests.Equal(false, server.HasConnection);
                var retry = artwork.AcquireAsync(reference);
                await using (var exchange = await server.AcceptAsync())
                {
                    BridgeTests.Equal(reference, exchange.Path);
                    await exchange.ReplyAsync(complete, contentType: "image/" + format);
                }
                using var recovered = await retry;
                CheckPixels(recovered);
                using var cached = await artwork.AcquireAsync(reference);
                BridgeTests.Equal(true, ReferenceEquals(recovered.Image, cached.Image));
                BridgeTests.Equal(before + 2, reads);
                BridgeTests.Equal(false, server.HasConnection);
            }
        }));
        yield return new("runtime-image: stable artwork cache survives endpoint changes without authenticating public URLs", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var first = new RuntimeHttpClientTests.Server();
            await using var second = new RuntimeHttpClientTests.Server();
            await using var firstClient = Client(first);
            await using var secondClient = Client(second);
            IRuntimeImageClient current = firstClient;
            int publicCalls = 0;
            using var handler = new PublicImageHandler(request =>
            {
                BridgeTests.Equal(false, request.Headers.Contains("Authorization"));
                BridgeTests.Equal(false, request.Headers.Contains("X-Meow-Session"));
                BridgeTests.Equal(false, request.Headers.Contains("X-Meow-Request-Id"));
                publicCalls++;
            });
            await using var artwork = new ArtworkService(handler,
                (reference, budget, token) => current.ReadImageAsync(reference, budget, token));
            var read = artwork.AcquireAsync(Reference);
            await using (var exchange = await first.AcceptAsync()) await exchange.ReplyAsync(Png, contentType: "image/png");
            using var initial = await read;
            current = secondClient;
            using var reused = await artwork.AcquireAsync(Reference);
            BridgeTests.Equal(true, ReferenceEquals(initial.Image, reused.Image));
            BridgeTests.Equal(false, second.HasConnection);
            var differentSize = artwork.AcquireAsync("/v1/tmdb/images/w300/poster.jpg");
            await using (var exchange = await second.AcceptAsync())
            {
                BridgeTests.Equal("/v1/tmdb/images/w300/poster.jpg", exchange.Path);
                await exchange.ReplyAsync(Png, contentType: "image/png");
            }
            using var resized = await differentSize;
            using var publicImage = await artwork.AcquireAsync("https://public.fixture.invalid/poster.jpg");
            CheckPixels(publicImage);
            BridgeTests.Equal(1, publicCalls);
            foreach (string address in new[] { first.BaseUrl + Reference, "//public.fixture.invalid/poster.jpg" })
            {
                try { using var ignored = await artwork.AcquireAsync(address); throw new InvalidOperationException("Unsafe artwork URL admitted"); }
                catch (ArtworkException) { }
            }
            BridgeTests.Equal(false, first.HasConnection);
            BridgeTests.Equal(false, second.HasConnection);
            BridgeTests.Equal(1, publicCalls);
        }));
        yield return new("runtime-image: caller cancel and transport close physically dispose unfinished streams", async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var client = Client(server);
            using var cancellation = new CancellationTokenSource();
            var first = client.ReadImageAsync(Reference, ArtworkService.MaximumBytes, cancellation.Token);
            await using var exchange = await server.AcceptAsync();
            await exchange.BeginImageAsync(100);
            cancellation.Cancel();
            try { await first; throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            await exchange.AssertDisconnectedAsync();
            var second = client.ReadImageAsync(Reference, ArtworkService.MaximumBytes);
            await using var pending = await server.AcceptAsync();
            await pending.BeginImageAsync(100);
            await client.DisposeAsync();
            await BridgeTests.Expect("CLOSED", () => second);
            await pending.AssertDisconnectedAsync();
        });
        yield return new("runtime-image: artwork close cancels its trusted reader and waits for physical settlement", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] bytes = Png;
            var artwork = new ArtworkService(readTrusted: async (_, _, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.SetResult();
                return await release.Task;
            });
            var lease = artwork.AcquireAsync(Reference);
            await entered.Task;
            var close = artwork.DisposeAsync().AsTask();
            try
            {
                await cancelled.Task;
                BridgeTests.Equal(false, close.IsCompleted);
                release.SetResult(bytes);
                try { using var ignored = await lease; throw new InvalidOperationException("Closed artwork published pixels"); }
                catch (OperationCanceledException) { }
                catch (ArtworkException) { }
                await close;
                BridgeTests.Equal(true, bytes.All(value => value == 0));
            }
            finally { release.TrySetResult(bytes); await artwork.DisposeAsync(); }
        }));
    }
    private static void CheckPixels(IArtworkLease lease) =>
        BridgeTests.Equal(true, lease.Image.IsFrozen && lease.Image.PixelWidth > 0 && lease.Image.PixelHeight > 0);
    private sealed class PublicImageHandler(Action<System.Net.Http.HttpRequestMessage> inspect) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            inspect(request);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.ByteArrayContent(Png) });
        }
    }
}

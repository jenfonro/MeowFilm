using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class RuntimeHttpEventsTests
{
    private const string OtherSession = "11111111-1111-4111-8111-111111111111";
    private static readonly Guid BusinessSession = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static byte[] Frame(long sequence, string title = "中文🎬") =>
        FrameWithJson(sequence, JsonSerializer.Serialize(new
        {
            sessionId = BusinessSession, sequence, target = new { title }, stage = "ready"
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    private static byte[] FrameWithJson(long sequence, string json) =>
        Encoding.UTF8.GetBytes($"id: {sequence}\nevent: snapshot\ndata: {json}\n\n");

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("runtime-events: credential-free headers, reconnect cursor and fragmented UTF8 before EOF", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            await using var events = client.EventsAsync("film", BusinessSession, 7).GetAsyncEnumerator();
            var next = events.MoveNextAsync().AsTask();
            await using var response = await server.AcceptAsync();
            BridgeTests.Equal("GET", response.Method);
            BridgeTests.Equal($"/v1/film/events/{BusinessSession:D}?after=7", response.Path);
            BridgeTests.Equal(false, response.Headers.ContainsKey("Authorization"));
            BridgeTests.Equal(false, response.Headers.ContainsKey("X-Meow-Session"));
            BridgeTests.Equal("text/event-stream", response.Headers["Accept"]);
            BridgeTests.Equal("identity", response.Headers["Accept-Encoding"]);
            BridgeTests.Equal(true, Guid.TryParseExact(response.Headers["X-Meow-Request-Id"], "D", out _));
            BridgeTests.Equal(false, response.Headers.ContainsKey("Cookie"));
            await response.StartAsync();
            foreach (var value in Frame(8)) await response.SendAsync([value]);
            BridgeTests.Equal(true, await next.WaitAsync(TimeSpan.FromSeconds(5)));
            BridgeTests.Equal(8L, events.Current.GetProperty("sequence").GetInt64());
            BridgeTests.Equal("中文🎬", events.Current.GetProperty("target").GetProperty("title").GetString());
            // The response is deliberately still open here.
            await events.DisposeAsync();
            await response.AssertDisconnectedAsync();
            await AssertRPCStillWorks(client, server);
        });
        yield return new("runtime-events: BOM, CRLF, comments, unknown fields and multiline JSON", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            await using var events = client.EventsAsync("film", BusinessSession).GetAsyncEnumerator();
            var next = events.MoveNextAsync().AsTask();
            await using var response = await server.AcceptAsync();
            await response.StartAsync();
            await response.SendAsync([0xef, 0xbb, 0xbf]);
            await response.SendAsync(Encoding.UTF8.GetBytes(": " + new string('x', 100_000) + "\r\nunknown: ignored\r\n\r\n"));
            var json = $"{{\"sessionId\":\"{BusinessSession:D}\",\r\ndata: \"sequence\":0,\"target\":{{}},\"stage\":\"ready\"}}";
            var wire = Encoding.UTF8.GetBytes($"id: 0\r\nevent: snapshot\r\ndata: {json}\r\n\r\n");
            foreach (var value in wire) await response.SendAsync([value]);
            BridgeTests.Equal(true, await next.WaitAsync(TimeSpan.FromSeconds(5)));
            BridgeTests.Equal(0L, events.Current.GetProperty("sequence").GetInt64());
        });
        yield return new("runtime-events: whole snapshot over 4 MiB has no artwork quota", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            await using var events = client.EventsAsync("film", BusinessSession).GetAsyncEnumerator();
            var next = events.MoveNextAsync().AsTask();
            await using var response = await server.AcceptAsync();
            await response.StartAsync();
            var title = new string('x', 4 * 1024 * 1024 + 1) + "🎬";
            await response.SendAsync(Frame(9, title));
            BridgeTests.Equal(true, await next.WaitAsync(TimeSpan.FromSeconds(10)));
            BridgeTests.Equal(title, events.Current.GetProperty("target").GetProperty("title").GetString());
        });
        yield return new("runtime-events: slow observer coalesces, owner close drains while iterator is parked", async () =>
        {
            await using var server = new Server();
            var client = new RuntimeHttpClient(server.BaseUrl);
            await using var events = client.EventsAsync("film", BusinessSession).GetAsyncEnumerator();
            try
            {
                var first = events.MoveNextAsync().AsTask();
                await using var response = await server.AcceptAsync();
                await response.StartAsync();
                await response.SendAsync(Frame(0));
                BridgeTests.Equal(true, await first.WaitAsync(TimeSpan.FromSeconds(5)));
                using var burst = new MemoryStream();
                for (var sequence = 1; sequence <= 99; sequence++) burst.Write(Frame(sequence));
                await response.SendAsync(burst.ToArray());
                // EOF lets the producer fully drain without the consumer calling MoveNext.
                await response.DisposeAsync();
                var last = -1L;
                try
                {
                    while (await events.MoveNextAsync()) last = events.Current.GetProperty("sequence").GetInt64();
                    throw new InvalidOperationException("EOF cannot be successful completion");
                }
                catch (BridgeException error) { BridgeTests.Equal("CLOSED", error.Code); }
                BridgeTests.Equal(99L, last);

                await using var parked = client.EventsAsync("film", BusinessSession, 99).GetAsyncEnumerator();
                var ready = parked.MoveNextAsync().AsTask();
                await using var live = await server.AcceptAsync();
                await live.StartAsync(); await live.SendAsync(Frame(100));
                BridgeTests.Equal(true, await ready.WaitAsync(TimeSpan.FromSeconds(5)));
                await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await live.AssertDisconnectedAsync();
                await BridgeTests.Expect("CLOSED", () => parked.MoveNextAsync().AsTask());
            }
            finally { await client.DisposeAsync(); }
        });
        yield return new("runtime-events: cancellation drains HTTP without runtime shutdown", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            using var cancellation = new CancellationTokenSource();
            await using var events = client.EventsAsync("film", BusinessSession, cancellationToken: cancellation.Token).GetAsyncEnumerator();
            var next = events.MoveNextAsync().AsTask();
            await using var response = await server.AcceptAsync();
            await response.StartAsync();
            await cancellation.CancelAsync();
            try { await next; throw new InvalidOperationException("cancel must not succeed"); }
            catch (OperationCanceledException) { }
            await response.AssertDisconnectedAsync();
            await AssertRPCStillWorks(client, server);
        });

        foreach (var (name, status, type, id, body) in new (string, int, string, string?, byte[])[]
        {
            ("non-200", 401, "text/event-stream", null, []),
            ("redirect", 302, "text/event-stream", null, []),
            ("non-SSE", 200, "application/json", null, []),
            ("non-UTF8 encoding", 200, "text/event-stream; charset=iso-8859-1", null, []),
            ("wrong request identity", 200, "text/event-stream", "wrong-id", []),
            ("duplicate request identity", 200, "text/event-stream", "duplicate", []),
            ("empty stream", 200, "text/event-stream", null, []),
            ("partial frame", 200, "text/event-stream", null, "id: 0\nevent: snapshot\ndata: {"u8.ToArray()),
            ("incomplete UTF8", 200, "text/event-stream", null, [0xe4]),
            ("invalid UTF8", 200, "text/event-stream", null, [0xff]),
            ("overlong UTF8", 200, "text/event-stream", null, [0xc0, 0xaf]),
            ("missing identity", 200, "text/event-stream", null, FrameWithJson(0, "{}")),
            ("wrong session", 200, "text/event-stream", null, FrameWithJson(0, $"{{\"sessionId\":\"{OtherSession}\",\"sequence\":0}}")),
            ("wrong sequence", 200, "text/event-stream", null, FrameWithJson(1, $"{{\"sessionId\":\"{BusinessSession:D}\",\"sequence\":0}}")),
            ("invalid JSON", 200, "text/event-stream", null, FrameWithJson(0, "{private-wire}")),
            ("unsafe id", 200, "text/event-stream", null, "id: 9007199254740992\n"u8.ToArray()),
            ("negative id", 200, "text/event-stream", null, "id: -1\n"u8.ToArray()),
            ("unknown event", 200, "text/event-stream", null, "id: 0\nevent: private-wire\n"u8.ToArray()),
            ("duplicate id", 200, "text/event-stream", null, "id: 0\nid: 1\n"u8.ToArray())
        })
            yield return new("runtime-events: rejects " + name + " without wire disclosure", async () =>
            {
                await using var server = new Server();
                await using var client = new RuntimeHttpClient(server.BaseUrl);
                await using var events = client.EventsAsync("film", BusinessSession).GetAsyncEnumerator();
                var next = events.MoveNextAsync().AsTask();
                await using var response = await server.AcceptAsync();
                await response.StartAsync(status, type, id);
                try { await response.SendAsync(body); } catch (IOException) { }
                await response.DisposeAsync();
                await BridgeTests.Expect("INVALID_RESPONSE", () => next);
            });
        yield return new("runtime-events: invalid route or cursor rejected before HTTP", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            foreach (var module in new[] { "", "../film", "film?key=value", "FILM", "film/events" })
            {
                await using var events = client.EventsAsync(module, BusinessSession).GetAsyncEnumerator();
                await BridgeTests.Expect("INVALID_REQUEST", () => events.MoveNextAsync().AsTask());
            }
            foreach (var after in new[] { -1L, BridgeCodec.MaximumSafeInteger + 1 })
            {
                await using var events = client.EventsAsync("film", BusinessSession, after).GetAsyncEnumerator();
                await BridgeTests.Expect("INVALID_REQUEST", () => events.MoveNextAsync().AsTask());
            }
            BridgeTests.Equal(false, server.HasConnection);
        });
    }

    private static async Task AssertRPCStillWorks(RuntimeHttpClient client, Server server)
    {
        var description = client.DescribeAsync();
        await using var rpc = await server.AcceptAsync();
        BridgeTests.Equal("/v1/describe", rpc.Path);
        await rpc.StartAsync(type: "application/json");
        await rpc.SendAsync("""{"ok":true,"contract":"runtime.http.v1","value":{}}"""u8.ToArray());
        await rpc.DisposeAsync();
        BridgeTests.Equal(true, (await description).GetProperty("ok").GetBoolean());
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public string BaseUrl { get; }
        public bool HasConnection => listener.Pending();
        public Server() { listener.Start(); BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"; }
        public async Task<Response> AcceptAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            try { return await Response.ReadAsync(socket, deadline.Token); }
            catch { socket.Dispose(); throw; }
        }
        public ValueTask DisposeAsync() { listener.Stop(); return ValueTask.CompletedTask; }
    }

    private sealed class Response(TcpClient socket, string method, string path, Dictionary<string, string> headers) : IAsyncDisposable
    {
        public string Method => method;
        public string Path => path;
        public Dictionary<string, string> Headers => headers;

        public static async Task<Response> ReadAsync(TcpClient socket, CancellationToken cancellationToken)
        {
            var stream = socket.GetStream();
            using var bytes = new MemoryStream();
            var one = new byte[1]; var matched = 0;
            var separator = "\r\n\r\n"u8.ToArray();
            while (matched < separator.Length)
            {
                await stream.ReadExactlyAsync(one, cancellationToken); bytes.WriteByte(one[0]);
                matched = one[0] == separator[matched] ? matched + 1 : one[0] == separator[0] ? 1 : 0;
            }
            var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var first = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var split = line.IndexOf(':');
                headers.Add(line[..split], line[(split + 1)..].Trim());
            }
            return new Response(socket, first[0], first[1], headers);
        }
        public async Task StartAsync(int status = 200, string type = "text/event-stream; charset=utf-8", string? id = null)
        {
            id ??= Headers["X-Meow-Request-Id"];
            if (id == "duplicate") id = Headers["X-Meow-Request-Id"] + "\r\nX-Meow-Request-Id: " + Headers["X-Meow-Request-Id"];
            await SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: {type}\r\n" +
                $"X-Meow-Request-Id: {id}\r\nConnection: close\r\n\r\n"));
        }
        public async Task SendAsync(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            await socket.GetStream().FlushAsync();
        }
        public async Task AssertDisconnectedAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { BridgeTests.Equal(0, await socket.GetStream().ReadAsync(new byte[1], deadline.Token)); }
            catch (IOException) { }
        }
        public ValueTask DisposeAsync() { socket.Dispose(); return ValueTask.CompletedTask; }
    }
}

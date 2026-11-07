using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class RuntimeHttpClientTests
{
    private const string Session = "b72dd937-0020-47de-9f03-6c9c5c64b7ef";
    private static readonly string Key = new('a', 64); // Synthetic fixture identity, never a real credential.
    private static byte[] Success(object? value, string contract = "music.script.v3") =>
        JsonSerializer.SerializeToUtf8Bytes(new { ok = true, contract, value });
    private static byte[] Failure(string code, string contract = "music.script.v3") =>
        JsonSerializer.SerializeToUtf8Bytes(new { ok = false, contract, code });

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("runtime-http: exact auth headers, complete large Unicode JSON and raw source", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
            var text = new string('中', 400_000) + "🎵";
            var outgoing = client.CallAsync("music.script.invoke", new Dictionary<string, object?> { ["歌曲"] = text });
            await using (var exchange = await server.AcceptAsync())
            {
                CheckAuthentication(exchange, server);
                BridgeTests.Equal("POST", exchange.Method);
                BridgeTests.Equal("/v1/music/invoke", exchange.Path);
                BridgeTests.Equal("application/json", exchange.Headers["Content-Type"]);
                BridgeTests.Equal(text, BridgeCodec.DecodeJSONValue(exchange.Body).GetProperty("歌曲").GetString());
                await exchange.ReplyAsync(Success(new Dictionary<string, object?> { ["歌曲"] = text }));
            }
            var result = await outgoing;
            BridgeTests.Equal(true, result.GetProperty("ok").GetBoolean());
            BridgeTests.Equal("music.script.v3", result.GetProperty("contract").GetString());
            BridgeTests.Equal(text, result.GetProperty("value").GetProperty("歌曲").GetString());
            var source = Encoding.UTF8.GetBytes("// " + text);
            var upload = client.UploadSourceAsync("source", "candidate", 123, source, "来源.js");
            await using (var exchange = await server.AcceptAsync())
            {
                CheckAuthentication(exchange, server);
                BridgeTests.Equal("PUT", exchange.Method);
                BridgeTests.Equal("/v1/source/sources/candidate/123?basename=" + Uri.EscapeDataString("来源.js"), exchange.Path);
                BridgeTests.Equal("application/octet-stream", exchange.Headers["Content-Type"]);
                BridgeTests.Equal(true, source.SequenceEqual(exchange.Body));
                await exchange.ReplyAsync(Success(new { installationId = "candidate", sourceRevision = 123,
                    sourceBytes = source.Length, contentHash = new string('b', 64) }, "source.runtime.v2"));
            }
            BridgeTests.Equal(123, (await upload).GetProperty("value").GetProperty("sourceRevision").GetInt32());
        });
        yield return new("runtime-http: GET describe and explicit shutdown have exact routes and cleanup acknowledgement", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
            var describe = client.DescribeAsync();
            await using (var exchange = await server.AcceptAsync())
            {
                BridgeTests.Equal("GET", exchange.Method);
                BridgeTests.Equal("/v1/describe", exchange.Path);
                BridgeTests.Equal(0, exchange.Body.Length);
                BridgeTests.Equal(false, exchange.Headers.ContainsKey("Content-Type"));
                await exchange.ReplyAsync(Success(Description(), RuntimeHttpProtocol.Name));
            }
            var declared = RuntimeHttpProtocol.ValidateDescription((await describe).GetProperty("value"), "aggregate", Modules);
            BridgeTests.Equal(32, declared.Count);
            var close = client.ShutdownAsync();
            await using (var exchange = await server.AcceptAsync())
            {
                BridgeTests.Equal("POST", exchange.Method);
                BridgeTests.Equal("/v1/close", exchange.Path);
                BridgeTests.Equal("{}", Encoding.UTF8.GetString(exchange.Body));
                await exchange.ReplyAsync(Success(new { closed = true, cleanupComplete = true }, RuntimeHttpProtocol.Name));
            }
            await close;
        });
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[]
        {
            ("wrong product", root => root["product"] = "other"),
            ("old version", root => root["modules"]![1]!["version"] = "0.1.0"),
            ("wrong contract", root => root["modules"]![1]!["contract"] = "wrong"),
            ("duplicate module", root => root["modules"]![1] = root["modules"]![0]!.DeepClone()),
            ("unknown module", root => root["modules"]![1]!["id"] = "unknown"),
            ("missing method", root => root["modules"]![0]!["methods"]!.AsArray().RemoveAt(0)),
            ("duplicate method", root => root["modules"]![0]!["methods"]!.AsArray().Add("music.script.invoke")),
            ("unknown method", root => root["modules"]![0]!["methods"]!.AsArray().Add("music.script.unknown")),
        })
            yield return new("runtime-http: description rejects " + name, () =>
            {
                var description = JsonSerializer.SerializeToNode(Description())!.AsObject();
                mutate(description);
                BridgeTests.Throws("INVALID_RESPONSE", () =>
                    RuntimeHttpProtocol.ValidateDescription(BridgeCodec.Value(description), "aggregate", Modules));
                return Task.CompletedTask;
            });
        foreach (var (name, bytes, status, id, contentType) in new (string, byte[], int, string?, string)[]
        {
            ("wrong response id", Success(1), 200, Guid.NewGuid().ToString("D"), "application/json"),
            ("duplicate response id", Success(1), 200, "duplicate", "application/json"),
            ("wrong content type", Success(1), 200, null, "text/plain"),
            ("wrong contract", Success(1, "cat.script.v2"), 200, null, "application/json"),
            ("unknown error", Failure("synthetic-private-provider-message"), 200, null, "application/json"),
            ("public success on business route", Success(1, RuntimeHttpProtocol.Name), 200, null, "application/json"),
            ("array envelope", "[]"u8.ToArray(), 200, null, "application/json"),
            ("missing value", """{"ok":true,"contract":"music.script.v3"}"""u8.ToArray(), 200, null, "application/json"),
            ("invalid ok type", """{"ok":"true","contract":"music.script.v3","value":1}"""u8.ToArray(), 200, null, "application/json"),
            ("duplicate envelope field", """{"ok":true,"ok":true,"contract":"music.script.v3","value":1}"""u8.ToArray(), 200, null, "application/json"),
            ("missing error code", """{"ok":false,"contract":"music.script.v3","value":null}"""u8.ToArray(), 200, null, "application/json"),
            ("extra error field", """{"ok":false,"contract":"music.script.v3","code":"SCRIPT_FAILURE","message":"synthetic-private"}"""u8.ToArray(), 200, null, "application/json"),
            ("duplicate escaped key", """{"ok":true,"contract":"music.script.v3","value":{"x":1,"\u0078":2}}"""u8.ToArray(), 200, null, "application/json"),
            ("unsafe integer", """{"ok":true,"contract":"music.script.v3","value":9007199254740992}"""u8.ToArray(), 200, null, "application/json"),
            ("unpaired surrogate", """{"ok":true,"contract":"music.script.v3","value":"\ud800"}"""u8.ToArray(), 200, null, "application/json"),
            ("bad UTF8", [0xff], 200, null, "application/json"),
            ("bad JSON", "{"u8.ToArray(), 200, null, "application/json"),
            ("non-200 success", Success(1), 401, null, "application/json"),
            ("unsupported status", Failure("SCRIPT_FAILURE"), 500, null, "application/json")
        })
            yield return new("runtime-http: rejects " + name + " with a safe error", async () =>
            {
                await using var server = new Server();
                await using var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
                var outgoing = client.CallAsync("music.script.invoke");
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(bytes, status, id, contentType);
                await BridgeTests.Expect("INVALID_RESPONSE", () => outgoing);
            });
        yield return new("runtime-http: valid public and module failures remain complete safe envelopes", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
            foreach (var (code, contract, status) in new[]
            {
                ("PERMISSION_DENIED", RuntimeHttpProtocol.Name, 401),
                ("SCRIPT_FAILURE", "music.script.v3", 200)
            })
            {
                var outgoing = client.CallAsync("music.script.invoke");
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(Failure(code, contract), status);
                var reply = await outgoing;
                BridgeTests.Equal(false, reply.GetProperty("ok").GetBoolean());
                BridgeTests.Equal(contract, reply.GetProperty("contract").GetString());
                BridgeTests.Equal(code, reply.GetProperty("code").GetString());
            }
        });
        yield return new("runtime-http: business value depth is independent of the response envelope", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
            foreach (var depth in new[] { 64, 65 })
            {
                var json = new string('[', depth) + "null" + new string(']', depth);
                var outgoing = client.CallAsync("music.script.invoke");
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(Encoding.UTF8.GetBytes("{\"ok\":true,\"contract\":\"music.script.v3\",\"value\":" + json + "}"));
                if (depth == 65) await BridgeTests.Expect("INVALID_RESPONSE", () => outgoing);
                else BridgeTests.Equal(json, (await outgoing).GetProperty("value").GetRawText());
            }
            var business = BridgeCodec.DecodeJSONValue(Encoding.UTF8.GetBytes(new string('[', 64) + "null" + new string(']', 64)));
            _ = BridgeCodec.EncodeJSONValue(business);
            BridgeTests.Throws("INVALID_MESSAGE", () => BridgeCodec.DecodeJSONValue(
                Encoding.UTF8.GetBytes(new string('[', 65) + "null" + new string(']', 65))));
            BridgeTests.Throws("INVALID_MESSAGE", () => BridgeCodec.EncodeJSONValue(new Dictionary<string, object?> { ["\ud800"] = 1 }));
        });
        yield return new("runtime-http: caller cancellation aborts only its exchange and disposal drains pending sockets", async () =>
        {
            await using var server = new Server();
            var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
            try
            {
                using var cancelled = new CancellationTokenSource();
                var first = client.UploadSourceAsync("cat", "candidate", 1, "x"u8.ToArray(), cancellationToken: cancelled.Token);
                await using var firstExchange = await server.AcceptAsync();
                var second = client.CallAsync("music.script.sources.list");
                await using var secondExchange = await server.AcceptAsync();
                BridgeTests.Equal(false, firstExchange.Headers["X-Meow-Request-Id"] == secondExchange.Headers["X-Meow-Request-Id"]);
                cancelled.Cancel();
                try { await first; throw new InvalidOperationException("Expected cancellation"); }
                catch (OperationCanceledException) { }
                await firstExchange.AssertDisconnectedAsync();
                await secondExchange.ReplyAsync(Success(new { items = Array.Empty<object>() }));
                BridgeTests.Equal(true, (await second).GetProperty("ok").GetBoolean());
                var pending = client.CallAsync("music.script.invoke");
                await using var pendingExchange = await server.AcceptAsync();
                var close1 = client.DisposeAsync().AsTask(); var close2 = client.DisposeAsync().AsTask();
                BridgeTests.Equal(true, ReferenceEquals(close1, close2));
                await Task.WhenAll(close1, close2).WaitAsync(TimeSpan.FromSeconds(5));
                await BridgeTests.Expect("CLOSED", () => pending);
                await pendingExchange.AssertDisconnectedAsync();
            }
            finally { await client.DisposeAsync(); }
        });
        yield return new("runtime-http: redirect is rejected without sending credentials to the destination", async () =>
        {
            await using var server = new Server();
            await using var destination = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
            var call = client.CallAsync("music.script.invoke");
            await using var exchange = await server.AcceptAsync();
            await exchange.ReplyAsync(Failure("CLOSED", RuntimeHttpProtocol.Name), 307,
                extraHeaders: "Location: " + destination.BaseUrl + "/v1/music/invoke\r\n");
            await BridgeTests.Expect("INVALID_RESPONSE", () => call);
            BridgeTests.Equal(false, destination.HasConnection);
        });
        yield return new("runtime-http: truncated response closes only one exchange without business replay", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
            var call = client.CallAsync("music.script.sources.add");
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync("{"u8.ToArray(), declaredBytes: 20);
            await BridgeTests.Expect("CLOSED", () => call);
            BridgeTests.Equal(false, server.HasConnection);
        });
        yield return new("runtime-http: default request deadline cancels only its exchange without replay", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
            var stalled = client.CallAsync("music.script.invoke");
            await using var waiting = await server.AcceptAsync();
            var other = client.CallAsync("music.script.sources.list");
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync(Success(new { items = Array.Empty<object>() }));
            BridgeTests.Equal(true, (await other).GetProperty("ok").GetBoolean());
            await BridgeTests.Expect("TIMEOUT", () => stalled).WaitAsync(TimeSpan.FromSeconds(35));
            await waiting.AssertDisconnectedAsync();
            BridgeTests.Equal(false, server.HasConnection);
        });
        yield return new("runtime-http: incomplete cleanup acknowledgement never becomes shutdown success", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl, Session, Key);
            foreach (var value in new object[] { new { closed = true }, new { closed = true, cleanupComplete = false } })
            {
                var close = client.ShutdownAsync();
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(Success(value, RuntimeHttpProtocol.Name));
                await BridgeTests.Expect("INVALID_RESPONSE", () => close);
            }
        });
        yield return new("runtime-http: unsafe endpoints and malformed identities are rejected before connecting", () =>
        {
            foreach (var endpoint in new[] { "http://localhost:123", "https://127.0.0.1:123",
                "http://127.0.0.1:123@elsewhere:456", "http://127.0.0.1:123/x", "http://127.0.0.1:123?x=1",
                "http://127.0.0.1:123#x", "http://127.0.0.1", "http://127.0.0.1:65536" })
                BridgeTests.Throws("INVALID_REQUEST", () => new RuntimeHttpClient(endpoint, Session, Key));
            foreach (var session in new[] { "wrong", " " + Session, Session + " ", "\t" + Session + "\r\n" })
                BridgeTests.Throws("INVALID_REQUEST", () => new RuntimeHttpClient("http://127.0.0.1:123", session, Key));
            BridgeTests.Throws("INVALID_REQUEST", () => new RuntimeHttpClient("http://127.0.0.1:123", Session, "wrong"));
            return Task.CompletedTask;
        });
        yield return new("runtime-http: shared source revisions are globally positive unique safe integers", async () =>
        {
            var revisions = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(RuntimeSourceRevision.Next)));
            BridgeTests.Equal(100, revisions.Distinct().Count());
            BridgeTests.Equal(true, revisions.All(value => value > 0 && value <= BridgeCodec.MaximumSafeInteger));
            BridgeTests.Equal(true, RuntimeSourceRevision.Next() > revisions.Max());
        });
    }

    private static RuntimeModule[] Modules => [new("music", "0.8.0"), new("cat", "0.2.0"), new("source", "0.2.0")];
    internal static object Description() => new
    {
        protocol = "runtime.http.v1", product = "aggregate", modules = new[]
        {
            new { id = "music", version = "0.8.0", contract = "music.script.v3", methods = new[]
            {
                "music.script.sources.list", "music.script.sources.detail", "music.script.sources.add", "music.script.sources.download",
                "music.script.sources.edit", "music.script.sources.update", "music.script.sources.rollback", "music.script.sources.enable",
                "music.script.sources.disable", "music.script.sources.remove", "music.script.sources.check",
                "music.script.resolve", "music.script.invoke", "music.script.close"
            } },
            new { id = "cat", version = "0.2.0", contract = "cat.script.v2", methods = new[]
            {
                "cat.script.start", "cat.script.status", "cat.script.refresh", "cat.script.install", "cat.script.import",
                "cat.script.edit", "cat.script.update", "cat.script.restart", "cat.script.enable", "cat.script.uninstall",
                "cat.script.invoke", "cat.script.management.describe", "cat.script.management.sync"
            } },
            new { id = "source", version = "0.2.0", contract = "source.runtime.v2", methods = new[]
            {
                "source.runtime.load", "source.runtime.unload", "source.runtime.invoke", "source.runtime.status", "source.runtime.close"
            } }
        }
    };

    private static void CheckAuthentication(Exchange exchange, Server server)
    {
        BridgeTests.Equal("Bearer " + Key, exchange.Headers["Authorization"]);
        BridgeTests.Equal(Session, exchange.Headers["X-Meow-Session"]);
        BridgeTests.Equal(true, Guid.TryParseExact(exchange.Headers["X-Meow-Request-Id"], "D", out _));
        BridgeTests.Equal(new Uri(server.BaseUrl).Authority, exchange.Headers["Host"]);
        BridgeTests.Equal(false, exchange.Path.Contains(Key, StringComparison.Ordinal));
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public string BaseUrl { get; }
        public Server() { listener.Start(); BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; }
        public bool HasConnection => listener.Pending();
        public async Task<Exchange> AcceptAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            try { return await Exchange.ReadAsync(socket, deadline.Token); }
            catch { socket.Dispose(); throw; }
        }
        public ValueTask DisposeAsync() { listener.Stop(); return ValueTask.CompletedTask; }
    }

    private sealed class Exchange(TcpClient socket, string method, string path,
        Dictionary<string, string> headers, byte[] body) : IAsyncDisposable
    {
        public string Method => method;
        public string Path => path;
        public Dictionary<string, string> Headers => headers;
        public byte[] Body => body;
        public static async Task<Exchange> ReadAsync(TcpClient socket, CancellationToken cancellationToken)
        {
            var stream = socket.GetStream();
            using var header = new MemoryStream();
            var one = new byte[1]; var matched = 0;
            var separator = "\r\n\r\n"u8.ToArray();
            while (matched != separator.Length)
            {
                await stream.ReadExactlyAsync(one, cancellationToken);
                header.WriteByte(one[0]);
                matched = one[0] == separator[matched] ? matched + 1 : one[0] == separator[0] ? 1 : 0;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var request = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var index = line.IndexOf(':');
                headers.Add(line[..index], line[(index + 1)..].Trim());
            }
            var body = new byte[headers.TryGetValue("Content-Length", out var size) ? int.Parse(size, CultureInfo.InvariantCulture) : 0];
            await stream.ReadExactlyAsync(body, cancellationToken);
            return new Exchange(socket, request[0], request[1], headers, body);
        }
        public async Task ReplyAsync(byte[] bytes, int status = 200, string? requestId = null,
            string contentType = "application/json", string extraHeaders = "", int? declaredBytes = null)
        {
            var id = requestId ?? Headers["X-Meow-Request-Id"];
            if (requestId == "duplicate") id = Headers["X-Meow-Request-Id"] + "\r\nX-Meow-Request-Id: " + Headers["X-Meow-Request-Id"];
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: {contentType}\r\n" +
                $"X-Meow-Request-Id: {id}\r\nContent-Length: {declaredBytes ?? bytes.Length}\r\nConnection: close\r\n{extraHeaders}\r\n");
            await socket.GetStream().WriteAsync(header);
            await socket.GetStream().WriteAsync(bytes);
            await socket.GetStream().FlushAsync();
            socket.Dispose();
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

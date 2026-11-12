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
    private static byte[] Success(object? value, string contract = "music.script.v3") =>
        JsonSerializer.SerializeToUtf8Bytes(new { ok = true, contract, value });
    private static byte[] Failure(string code, string contract = "music.script.v3") =>
        JsonSerializer.SerializeToUtf8Bytes(new { ok = false, contract, code });

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("runtime-http: request correlation without credentials, complete large Unicode JSON and raw source", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            var text = new string('中', 400_000) + "🎵";
            var outgoing = client.CallAsync("music.script.invoke", new Dictionary<string, object?> { ["歌曲"] = text });
            await using (var exchange = await server.AcceptAsync())
            {
                CheckCorrelation(exchange, server);
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
                CheckCorrelation(exchange, server);
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
            await using var client = new RuntimeHttpClient(server.BaseUrl);
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
            BridgeTests.Equal(97, declared.Count);
            BridgeTests.Equal(true, declared.Contains("netease.token.get"));
            BridgeTests.Equal(true, declared.Contains("film.rules.group.set"));
            BridgeTests.Equal(true, declared.Contains("film.rules.group.defaults"));
            BridgeTests.Equal(true, declared.Contains("film.rules.group.preview"));
            BridgeTests.Equal(true, declared.Contains("source.runtime.rules.process"));
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
        yield return new("runtime-http: source library routes binary images and fixed source errors retain their contract", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            var list = client.CallAsync("source.runtime.library.list", new { });
            await using (var exchange = await server.AcceptAsync())
            {
                CheckCorrelation(exchange, server);
                BridgeTests.Equal("POST", exchange.Method);
                BridgeTests.Equal("/v1/source/library.list", exchange.Path);
                await exchange.ReplyAsync(Success(new { sources = Array.Empty<object>() }, "source.runtime.v2"));
            }
            BridgeTests.Equal(0, (await list).GetProperty("value").GetProperty("sources").GetArrayLength());
            const string reference = "/v1/source/images/opaque-reference";
            byte[] pixels = [137, 80, 78, 71, 13, 10, 26, 10];
            var image = client.ReadImageAsync(reference, 1024);
            await using (var exchange = await server.AcceptAsync())
            {
                CheckCorrelation(exchange, server);
                BridgeTests.Equal("GET", exchange.Method);
                BridgeTests.Equal(reference, exchange.Path);
                await exchange.ReplyAsync(pixels, contentType: "image/png");
            }
            BridgeTests.Equal(true, (await image).SequenceEqual(pixels));
            var release = client.CallAsync("source.runtime.library.releaseImage", new { reference });
            await using (var exchange = await server.AcceptAsync())
            {
                CheckCorrelation(exchange, server);
                BridgeTests.Equal("/v1/source/library.releaseImage", exchange.Path);
                BridgeTests.Equal(reference, BridgeCodec.DecodeJSONValue(exchange.Body).GetProperty("reference").GetString());
                await exchange.ReplyAsync(Success(new { released = true }, "source.runtime.v2"));
            }
            BridgeTests.Equal(true, (await release).GetProperty("value").GetProperty("released").GetBoolean());
            foreach (string contract in new[] { "source.runtime.v2", "tmdb.data.v1", "douban.data.v1" })
            {
                var missing = client.ReadImageAsync(reference, 1024);
                await using (var exchange = await server.AcceptAsync())
                    await exchange.ReplyAsync(Failure("SOURCE_NOT_FOUND", contract), status: 502);
                await BridgeTests.Expect(contract == "source.runtime.v2" ? "SOURCE_NOT_FOUND" : "INVALID_RESPONSE", () => missing);
            }
            foreach (string invalid in new[] { "/v1/source/images/../private", reference + "?cookie=fixture", reference + "#fragment" })
                await BridgeTests.Expect("INVALID_REQUEST", () => client.ReadImageAsync(invalid, 1024));
            foreach (string code in new[] { "SOURCE_INTEGRITY", "SOURCE_ENCODING", "STALE_SOURCE", "SOURCE_NOT_FOUND", "SOURCE_DISABLED",
                "UNSUPPORTED_PROTOCOL", "UNSUPPORTED_ACTION", "UNSUPPORTED_RULE", "UNSUPPORTED_DEPENDENCY",
                "CAPABILITY_UNAVAILABLE", "SOURCE_CLEANUP", "SOURCE_FAILURE", "HTTP_ERROR", "VIRTUAL_ROUTE_NOT_FOUND" })
                BridgeTests.Equal(code, new BridgeException(code).Code);
            BridgeTests.Equal("INTERNAL", new BridgeException("synthetic-private-source-error").Code);
        });
        yield return new("runtime-http: novel pure rules use their fixed source route and preserve source errors cancellation and close", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            var input = new { request = new { operation = "matchUrl", pattern = "https://fixture.invalid/.*", url = "https://fixture.invalid/book" } };
            var call = client.CallAsync("source.runtime.rules.process", input);
            await using (var exchange = await server.AcceptAsync())
            {
                CheckCorrelation(exchange, server);
                BridgeTests.Equal("POST", exchange.Method);
                BridgeTests.Equal("/v1/source/rules.process", exchange.Path);
                BridgeTests.Equal(true, JsonElement.DeepEquals(BridgeCodec.Value(input), BridgeCodec.DecodeJSONValue(exchange.Body)));
                await exchange.ReplyAsync(Success(true, "source.runtime.v2"));
            }
            BridgeTests.Equal(true, (await call).GetProperty("value").GetBoolean());
            foreach (string code in new[] { "TIMEOUT", "SOURCE_FAILURE", "CANCELLED", "CAPABILITY_UNAVAILABLE" })
            {
                var failed = client.CallAsync("source.runtime.rules.process", input);
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(Failure(code, "source.runtime.v2"));
                BridgeTests.Equal(code, (await failed).GetProperty("code").GetString());
            }
            using var cancellation = new CancellationTokenSource();
            var cancelled = client.CallAsync("source.runtime.rules.process", input, cancellation.Token);
            await using var pending = await server.AcceptAsync();
            cancellation.Cancel();
            try { await cancelled; throw new InvalidOperationException("Expected rule cancellation"); }
            catch (OperationCanceledException) { }
            await pending.AssertDisconnectedAsync();
            var closing = client.CallAsync("source.runtime.rules.process", input);
            await using var unfinished = await server.AcceptAsync();
            await client.DisposeAsync();
            await BridgeTests.Expect("CLOSED", () => closing);
            await unfinished.AssertDisconnectedAsync();
            BridgeTests.Throws("METHOD_UNAVAILABLE", () => client.CallAsync("source.runtime.rules.unknown", input));
        });
        yield return new("runtime-http: full-size source images exceed card budgets with declared and chunked bodies", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            const string reference = "/v1/source/images/full-size";
            const int cardBudget = 4 * 1024 * 1024;
            byte[] pixels = new byte[cardBudget + 1]; new Random(17).NextBytes(pixels);
            var ids = new HashSet<string>();
            foreach (bool chunked in new[] { false, true })
            {
                var full = client.ReadImageAsync(reference);
                await using (var exchange = await server.AcceptAsync())
                {
                    CheckCorrelation(exchange, server);
                    BridgeTests.Equal("GET", exchange.Method);
                    BridgeTests.Equal(reference, exchange.Path);
                    BridgeTests.Equal(0, exchange.Body.Length);
                    BridgeTests.Equal(true, ids.Add(exchange.Headers["X-Meow-Request-Id"]));
                    if (chunked) await exchange.ReplyChunkedAsync(pixels);
                    else await exchange.ReplyAsync(pixels, contentType: "image/png");
                }
                var received = await full;
                BridgeTests.Equal(true, pixels.SequenceEqual(received));
                var capped = client.ReadImageAsync(reference, cardBudget);
                await using (var exchange = await server.AcceptAsync())
                {
                    if (!chunked) await exchange.BeginImageAsync(pixels.Length);
                    else
                    {
                        try { await exchange.ReplyChunkedAsync(pixels); }
                        catch (IOException) { } // The bounded reader may reset an oversized stream.
                    }
                }
                await BridgeTests.Expect("RESOURCE_LIMIT", () => capped);
            }
        });
        yield return new("runtime-http: unbounded images retain trusted paths response identity MIME and source failures", async () =>
        {
            await using var server = new Server();
            await using var target = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            const string reference = "/v1/source/images/full-size";
            byte[] pixels = [137, 80, 78, 71, 13, 10, 26, 10];
            foreach (string invalid in new[] { "https://example.test/page.png", server.BaseUrl + reference, "//example.test/page.png",
                "/v1/source/images/../private", "/v1/source/images/%2e%2e", reference + "?private=fixture", reference + "#fragment" })
                await BridgeTests.Expect("INVALID_REQUEST", () => client.ReadImageAsync(invalid));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await client.ReadImageAsync(reference, cancelled.Token); throw new InvalidOperationException("Expected cancellation"); }
                catch (OperationCanceledException) { }
            }
            BridgeTests.Equal(false, server.HasConnection);
            foreach (var (status, requestId, mime, extra) in new[]
            {
                (307, (string?)null, "image/png", "Location: " + target.BaseUrl + reference + "\r\n"),
                (200, (string?)Guid.NewGuid().ToString("D"), "image/png", ""),
                (200, (string?)"duplicate", "image/png", ""),
                (200, (string?)null, "application/json", ""),
                (200, (string?)null, "image/png", "Content-Encoding: gzip\r\n")
            })
            {
                var read = client.ReadImageAsync(reference);
                await using (var exchange = await server.AcceptAsync())
                    await exchange.ReplyAsync(pixels, status, requestId, mime, extra);
                await BridgeTests.Expect("INVALID_RESPONSE", () => read);
                BridgeTests.Equal(false, target.HasConnection);
            }
            foreach (string contract in new[] { "source.runtime.v2", "tmdb.data.v1", "douban.data.v1" })
            {
                var read = client.ReadImageAsync(reference);
                await using (var exchange = await server.AcceptAsync())
                    await exchange.ReplyAsync(Failure("SOURCE_NOT_FOUND", contract), status: 502);
                await BridgeTests.Expect(contract == "source.runtime.v2" ? "SOURCE_NOT_FOUND" : "INVALID_RESPONSE", () => read);
            }
            var empty = client.ReadImageAsync(reference);
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync([], contentType: "image/png");
            await BridgeTests.Expect("INVALID_RESPONSE", () => empty);
            var truncated = client.ReadImageAsync(reference);
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync(pixels, contentType: "image/png", declaredBytes: pixels.Length + 1);
            await BridgeTests.Expect("CLOSED", () => truncated);
        });
        yield return new("runtime-http: unbounded image cancellation and close physically settle unfinished bodies", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            const string reference = "/v1/source/images/full-size";
            using var cancellation = new CancellationTokenSource();
            var first = client.ReadImageAsync(reference, cancellation.Token);
            await using var exchange = await server.AcceptAsync();
            await exchange.BeginImageAsync(100);
            cancellation.Cancel();
            try { await first; throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            await exchange.AssertDisconnectedAsync();
            var second = client.ReadImageAsync(reference);
            await using var pending = await server.AcceptAsync();
            await pending.BeginImageAsync(100);
            await client.DisposeAsync();
            await BridgeTests.Expect("CLOSED", () => second);
            await pending.AssertDisconnectedAsync();
        });
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[]
        {
            ("wrong product", root => root["product"] = "other"),
            ("old version", root => root["modules"]![1]!["version"] = "0.1.0"),
            ("wrong contract", root => root["modules"]![1]!["contract"] = "wrong"),
            ("duplicate module", root => root["modules"]![1] = root["modules"]![0]!.DeepClone()),
            ("unknown module", root => root["modules"]![1]!["id"] = "unknown"),
            ("reordered modules", root =>
            {
                var modules = root["modules"]!.AsArray();
                var first = modules[0]!.DeepClone();
                modules[0] = modules[1]!.DeepClone(); modules[1] = first;
            }),
            ("missing method", root => root["modules"]![0]!["methods"]!.AsArray().RemoveAt(0)),
            ("duplicate method", root => root["modules"]![0]!["methods"]!.AsArray().Add("music.script.invoke")),
            ("unknown method", root => root["modules"]![0]!["methods"]!.AsArray().Add("music.script.unknown")),
            ("missing TMDB module", root => root["modules"]!.AsArray().RemoveAt(4)),
            ("TMDB old version", root => root["modules"]![4]!["version"] = "0.0.1"),
            ("TMDB wrong contract", root => root["modules"]![4]!["contract"] = "douban.data.v1"),
            ("TMDB missing operation", root => root["modules"]![4]!["methods"]!.AsArray().RemoveAt(2)),
            ("missing film module", root => root["modules"]!.AsArray().RemoveAt(6)),
            ("missing pan module", root => root["modules"]!.AsArray().RemoveAt(3)),
            ("missing Douban module", root => root["modules"]!.AsArray().RemoveAt(5)),
            ("old seven-module description", root => root["modules"]!.AsArray().RemoveAt(7)),
            ("old five-module description", root =>
            {
                var modules = root["modules"]!.AsArray();
                modules.RemoveAt(6); modules.RemoveAt(5); modules.RemoveAt(3);
            }),
            ("old four-module description", root =>
            {
                var modules = root["modules"]!.AsArray();
                modules.RemoveAt(7); modules.RemoveAt(6); modules.RemoveAt(5); modules.RemoveAt(3);
            }),
            ("formal three-module assembly", root =>
            {
                var modules = root["modules"]!.AsArray();
                while (modules.Count > 3) modules.RemoveAt(modules.Count - 1);
            }),
            ("new module version", root => root["modules"]![6]!["version"] = "0.2.0"),
            ("missing film media release", root => root["modules"]![6]!["methods"]!.AsArray().RemoveAt(5)),
            ("duplicate film method", root => root["modules"]![6]!["methods"]!.AsArray().Add("film.library.get")),
            ("unknown film method", root => root["modules"]![6]!["methods"]!.AsArray().Add("film.session.unknown")),
            ("unknown metadata method", root => root["modules"]![4]!["methods"]!.AsArray().Add("tmdb.data.unknown")),
            ("NetEase old version", root => root["modules"]![7]!["version"] = "0.0.1"),
            ("NetEase wrong contract", root => root["modules"]![7]!["contract"] = "music.script.v3"),
            ("NetEase missing operation", root => root["modules"]![7]!["methods"]!.AsArray().Clear()),
            ("NetEase extra operation", root => root["modules"]![7]!["methods"]!.AsArray().Add("netease.token.close")),
            ("extra description field", root => root["extra"] = true),
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
                await using var client = new RuntimeHttpClient(server.BaseUrl);
                var outgoing = client.CallAsync("music.script.invoke");
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(bytes, status, id, contentType);
                await BridgeTests.Expect("INVALID_RESPONSE", () => outgoing);
            });
        yield return new("runtime-http: valid public and module failures remain complete safe envelopes", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
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
        yield return new("runtime-http: App provider and film methods use exact contracts and closed error codes", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            foreach (var (method, path, contract, code, foreignCode) in new[]
            {
                ("pan.providers", "/v1/pan/providers", "pan.runtime.v1", "LOGIN_EXPIRED", "NO_MATCH"),
                ("tmdb.data.settings.get", "/v1/tmdb/settings.get", "tmdb.data.v1", "NETWORK_ERROR", "PAN_FAILURE"),
                ("douban.data.settings.get", "/v1/douban/settings.get", "douban.data.v1", "WEB_INTERACTION_REQUIRED", "AUTH_REQUIRED"),
                ("film.library.get", "/v1/film/library.get", "film.playback.v1", "STORAGE_CHANGED", "SOURCE_CLEANUP"),
                ("film.library.remove", "/v1/film/library.remove", "film.playback.v1", "LIBRARY_UPDATE_FAILED", "SOURCE_CLEANUP"),
                ("film.session.media.release", "/v1/film/session.media.release", "film.playback.v1", "MEDIA_CLOSE_FAILED", "SOURCE_CLEANUP"),
                ("film.rules.preview", "/v1/film/rules.preview", "film.playback.v1", "RESOURCE_LIMIT", "SOURCE_CLEANUP")
            })
            {
                var input = new { id = "fixture-lease" };
                var outgoing = client.CallAsync(method, input);
                await using (var exchange = await server.AcceptAsync())
                {
                    CheckCorrelation(exchange, server);
                    BridgeTests.Equal("POST", exchange.Method);
                    BridgeTests.Equal(path, exchange.Path);
                    BridgeTests.Equal("fixture-lease", BridgeCodec.DecodeJSONValue(exchange.Body).GetProperty("id").GetString());
                    await exchange.ReplyAsync(Success(new { accepted = true }, contract));
                }
                var reply = await outgoing;
                BridgeTests.Equal(contract, reply.GetProperty("contract").GetString());
                BridgeTests.Equal(true, reply.GetProperty("value").GetProperty("accepted").GetBoolean());
                outgoing = client.CallAsync(method);
                await using (var exchange = await server.AcceptAsync()) await exchange.ReplyAsync(Failure(code, contract));
                reply = await outgoing;
                BridgeTests.Equal(false, reply.GetProperty("ok").GetBoolean());
                BridgeTests.Equal(code, reply.GetProperty("code").GetString());
                outgoing = client.CallAsync(method);
                await using (var exchange = await server.AcceptAsync()) await exchange.ReplyAsync(Failure(foreignCode, contract));
                await BridgeTests.Expect("INVALID_RESPONSE", () => outgoing);
            }
            await BridgeTests.Expect("METHOD_UNAVAILABLE", () => client.CallAsync("film.session.media.release.extra"));
        });
        yield return new("runtime-http: business value depth is independent of the response envelope", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
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
            var client = new RuntimeHttpClient(server.BaseUrl);
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
        yield return new("runtime-http: redirect is rejected without connecting to the destination", async () =>
        {
            await using var server = new Server();
            await using var destination = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
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
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            var call = client.CallAsync("music.script.sources.add");
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync("{"u8.ToArray(), declaredBytes: 20);
            await BridgeTests.Expect("CLOSED", () => call);
            BridgeTests.Equal(false, server.HasConnection);
        });
        yield return new("runtime-http: default request deadline cancels only its exchange without replay", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            var stalled = client.CallAsync("music.script.invoke");
            await using var waiting = await server.AcceptAsync();
            var token = client.CallAsync("netease.token.get");
            await using var tokenExchange = await server.AcceptAsync();
            var rules = client.CallAsync("source.runtime.rules.process", new { request = new
                { operation = "transform", title = "", blocks = Array.Empty<object>(), rules = Array.Empty<object>() } });
            await using var rulesExchange = await server.AcceptAsync();
            var other = client.CallAsync("music.script.sources.list");
            await using (var exchange = await server.AcceptAsync())
                await exchange.ReplyAsync(Success(new { items = Array.Empty<object>() }));
            BridgeTests.Equal(true, (await other).GetProperty("ok").GetBoolean());
            await BridgeTests.Expect("TIMEOUT", () => stalled).WaitAsync(TimeSpan.FromSeconds(35));
            await waiting.AssertDisconnectedAsync();
            BridgeTests.Equal(false, token.IsCompleted);
            BridgeTests.Equal(false, rules.IsCompleted);
            await rulesExchange.ReplyAsync(Success(new { title = "", blocks = Array.Empty<object>() }, "source.runtime.v2"));
            BridgeTests.Equal(true, (await rules).GetProperty("ok").GetBoolean());
            await tokenExchange.ReplyAsync(Success(new { token = "SYNTHETIC_FRESH_TOKEN" }, "netease.token.v1"));
            BridgeTests.Equal(true, (await token).GetProperty("ok").GetBoolean());
            BridgeTests.Equal(false, server.HasConnection);
        });
        yield return new("runtime-http: NetEase token route is exact and actively cancellable", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            using var cancellation = new CancellationTokenSource();
            var call = client.CallAsync("netease.token.get", new { }, cancellation.Token);
            await using var exchange = await server.AcceptAsync();
            CheckCorrelation(exchange, server);
            BridgeTests.Equal("POST", exchange.Method);
            BridgeTests.Equal("/v1/netease/get", exchange.Path);
            BridgeTests.Equal("{}", Encoding.UTF8.GetString(exchange.Body));
            cancellation.Cancel();
            try { await call; throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            await exchange.AssertDisconnectedAsync();
            BridgeTests.Throws("METHOD_UNAVAILABLE", () => client.CallAsync("netease.token.close"));
            BridgeTests.Throws("METHOD_UNAVAILABLE", () => client.CallAsync("music.script.netease.token.get"));
            BridgeTests.Equal(false, server.HasConnection);
        });
        yield return new("runtime-http: NetEase returns only its declared sanitized error codes", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            foreach (string code in new[] { "INVALID_REQUEST", "CLOSED", "CANCELLED", "TIMEOUT", "NETWORK_ERROR",
                "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "SDK_FAILURE", "SDK_CLEANUP", "SYNTHETIC_PRIVATE_ERROR" })
            {
                var call = client.CallAsync("netease.token.get");
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(Failure(code, "netease.token.v1"));
                if (code == "SYNTHETIC_PRIVATE_ERROR") await BridgeTests.Expect("INVALID_RESPONSE", () => call);
                else BridgeTests.Equal(code, (await call).GetProperty("code").GetString());
            }
        });
        yield return new("runtime-http: incomplete cleanup acknowledgement never becomes shutdown success", async () =>
        {
            await using var server = new Server();
            await using var client = new RuntimeHttpClient(server.BaseUrl);
            foreach (var value in new object[] { new { closed = true }, new { closed = true, cleanupComplete = false } })
            {
                var close = client.ShutdownAsync();
                await using var exchange = await server.AcceptAsync();
                await exchange.ReplyAsync(Success(value, RuntimeHttpProtocol.Name));
                await BridgeTests.Expect("INVALID_RESPONSE", () => close);
            }
        });
        yield return new("runtime-http: malformed endpoint components are rejected before connecting", () =>
        {
            foreach (var endpoint in new[] { "ftp://example.invalid",
                "http://127.0.0.1:123/x", "http://127.0.0.1:123?x=1",
                "http://127.0.0.1:123#x", "http://127.0.0.1:65536" })
                BridgeTests.Throws("INVALID_REQUEST", () => new RuntimeHttpClient(endpoint));
            return Task.CompletedTask;
        });
        yield return new("runtime-http: HTTP and HTTPS accept DNS private IPv6 and default-port endpoints without credentials", async () =>
        {
            foreach (var endpoint in new[] { "http://localhost:123", "https://127.0.0.1:123",
                "http://external.example.invalid:43210", "https://external.example.invalid",
                "http://10.0.0.2:43210", "http://169.254.1.2:43210", "http://[2001:db8::1]:43210", "http://127.0.0.1" })
            {
                await using var client = new RuntimeHttpClient(endpoint);
                BridgeTests.Equal(new Uri(endpoint).Host, RuntimeHttpProtocol.ValidateEndpoint(endpoint).Host);
            }
            await using var server = new Server();
            var baseUrl = new UriBuilder(server.BaseUrl) { Host = "localhost" }.Uri.AbsoluteUri;
            await using var connected = new RuntimeHttpClient(baseUrl);
            var description = connected.DescribeAsync();
            await using var exchange = await server.AcceptAsync();
            BridgeTests.Equal("GET", exchange.Method);
            BridgeTests.Equal(false, exchange.Headers.ContainsKey("Authorization"));
            BridgeTests.Equal(false, exchange.Headers.ContainsKey("X-Meow-Session"));
            BridgeTests.Equal(true, exchange.Headers["Host"].StartsWith("localhost:", StringComparison.Ordinal));
            await exchange.ReplyAsync(Success(Description(), RuntimeHttpProtocol.Name));
            BridgeTests.Equal(true, (await description).GetProperty("ok").GetBoolean());
        });
        yield return new("runtime-http: standard URI userinfo preserves the actual destination without extra authentication configuration", async () =>
        {
            foreach (var (address, host, port, userInfo) in new[]
            {
                ("http://fixture-user@external.example.invalid:43210", "external.example.invalid", 43210, "fixture-user"),
                ("http://fixture-user:fixture-password@external.example.invalid:43210",
                    "external.example.invalid", 43210, "fixture-user:fixture-password"),
                ("https://:fixture-password@external.example.invalid", "external.example.invalid", 443, ":fixture-password"),
                ("https://fixture%40user:fixture%3Apassword@external.example.invalid",
                    "external.example.invalid", 443, "fixture%40user:fixture%3Apassword"),
                ("http://127.0.0.1:123@elsewhere:456", "elsewhere", 456, "127.0.0.1:123")
            })
            {
                var endpoint = RuntimeHttpProtocol.ValidateEndpoint(address);
                BridgeTests.Equal(host, endpoint.Host);
                BridgeTests.Equal(port, endpoint.Port);
                BridgeTests.Equal(userInfo, endpoint.UserInfo);
                await using var client = new RuntimeHttpClient(address);
            }
            await using var server = new Server();
            var baseUrl = new UriBuilder(server.BaseUrl) { UserName = "fixture-user", Password = "fixture-password" }.Uri.AbsoluteUri;
            await using var connected = new RuntimeHttpClient(baseUrl);
            var description = connected.DescribeAsync();
            await using var exchange = await server.AcceptAsync();
            BridgeTests.Equal("GET", exchange.Method);
            BridgeTests.Equal("/v1/describe", exchange.Path);
            BridgeTests.Equal(new Uri(server.BaseUrl).Authority, exchange.Headers["Host"]);
            BridgeTests.Equal(false, exchange.Headers.ContainsKey("X-Meow-Session"));
            await exchange.ReplyAsync(Success(Description(), RuntimeHttpProtocol.Name));
            BridgeTests.Equal(true, (await description).GetProperty("ok").GetBoolean());
        });
        yield return new("runtime-http: shared source revisions are globally positive unique safe integers", async () =>
        {
            var revisions = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(RuntimeSourceRevision.Next)));
            BridgeTests.Equal(100, revisions.Distinct().Count());
            BridgeTests.Equal(true, revisions.All(value => value > 0 && value <= BridgeCodec.MaximumSafeInteger));
            BridgeTests.Equal(true, RuntimeSourceRevision.Next() > revisions.Max());
        });
    }

    internal static RuntimeModule[] Modules => [new("music", "0.8.0"), new("cat", "0.2.0"), new("source", "0.3.0"),
        new("pan", "0.1.0"), new("tmdb", "0.1.0"), new("douban", "0.1.0"), new("film", "0.1.0"),
        new("netease", "0.1.0")];
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
            new { id = "source", version = "0.3.0", contract = "source.runtime.v2", methods = new[]
            {
                "source.runtime.load", "source.runtime.unload", "source.runtime.invoke", "source.runtime.status", "source.runtime.close",
                "source.runtime.library.list", "source.runtime.library.get", "source.runtime.library.previewImport", "source.runtime.library.import", "source.runtime.library.save",
                "source.runtime.library.update", "source.runtime.library.remove", "source.runtime.library.export", "source.runtime.library.invoke",
                "source.runtime.library.image", "source.runtime.library.releaseImage", "source.runtime.web.pending", "source.runtime.web.respond",
                "source.runtime.rules.process"
            } },
            new { id = "pan", version = "0.1.0", contract = "pan.runtime.v1", methods = new[]
            {
                "pan.providers", "pan.settings.get", "pan.settings.set", "pan.settings.clear", "pan.login.start",
                "pan.login.poll", "pan.login.cancel", "pan.list", "pan.prepare", "pan.play", "pan.refresh"
            } },
            new { id = "tmdb", version = "0.1.0", contract = "tmdb.data.v1", methods = new[]
            {
                "tmdb.data.settings.get", "tmdb.data.settings.update", "tmdb.data.request"
            } },
            new { id = "douban", version = "0.1.0", contract = "douban.data.v1", methods = new[]
            {
                "douban.data.settings.get", "douban.data.settings.update", "douban.data.request", "douban.data.search"
            } },
            new { id = "film", version = "0.1.0", contract = "film.playback.v1", methods = new[]
            {
                "film.session.open", "film.session.command", "film.session.snapshot", "film.session.release",
                "film.session.attempt", "film.session.media.release", "film.settings.get", "film.settings.set",
                "film.library.get", "film.library.remove", "film.manual.update",
                "film.data.catalog", "film.data.search", "film.data.detail", "film.data.metadata",
                "film.data.metadata.entry", "film.data.cache.entry", "film.data.invalidate",
                "film.rules.get", "film.rules.set", "film.rules.group.set", "film.rules.group.defaults", "film.rules.group.preview",
                "film.rules.restore", "film.rules.preview", "film.rules.normalize", "film.rules.clear",
                "film.search.open", "film.search.snapshot", "film.search.command", "film.search.release", "film.search.handoff"
            } },
            new { id = "netease", version = "0.1.0", contract = "netease.token.v1", methods = new[]
            {
                "netease.token.get"
            } }
        }
    };

    private static void CheckCorrelation(Exchange exchange, Server server)
    {
        BridgeTests.Equal(false, exchange.Headers.ContainsKey("Authorization"));
        BridgeTests.Equal(false, exchange.Headers.ContainsKey("X-Meow-Session"));
        BridgeTests.Equal(true, Guid.TryParseExact(exchange.Headers["X-Meow-Request-Id"], "D", out _));
        BridgeTests.Equal(new Uri(server.BaseUrl).Authority, exchange.Headers["Host"]);
    }

    internal sealed class Server : IAsyncDisposable
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

    internal sealed class Exchange(TcpClient socket, string method, string path,
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
            var id = requestId ?? Headers.GetValueOrDefault("X-Meow-Request-Id", "");
            if (requestId == "duplicate") id = Headers["X-Meow-Request-Id"] + "\r\nX-Meow-Request-Id: " + Headers["X-Meow-Request-Id"];
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: {contentType}\r\n" +
                $"X-Meow-Request-Id: {id}\r\nContent-Length: {declaredBytes ?? bytes.Length}\r\nConnection: close\r\n{extraHeaders}\r\n");
            await socket.GetStream().WriteAsync(header);
            await socket.GetStream().WriteAsync(bytes);
            await socket.GetStream().FlushAsync();
            socket.Dispose();
        }
        internal async Task BeginImageAsync(int length)
        {
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 Fixture\r\nContent-Type: image/png\r\n" +
                $"X-Meow-Request-Id: {Headers.GetValueOrDefault("X-Meow-Request-Id", "")}\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
            await socket.GetStream().WriteAsync(header);
            await socket.GetStream().FlushAsync();
        }
        internal async Task ReplyChunkedAsync(byte[] bytes)
        {
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 Fixture\r\nContent-Type: image/png\r\n" +
                $"X-Meow-Request-Id: {Headers.GetValueOrDefault("X-Meow-Request-Id", "")}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
            var stream = socket.GetStream();
            await stream.WriteAsync(header);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n"));
            await stream.WriteAsync(bytes);
            await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray());
            await stream.FlushAsync();
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

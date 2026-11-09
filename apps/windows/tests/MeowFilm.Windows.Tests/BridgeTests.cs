using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class BridgeTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("bridge: metadata safe errors and image limits preserve codes while unknown errors stay internal", () =>
        {
            foreach (string code in new[] { "INVALID_REQUEST", "CLOSED", "CANCELLED", "NETWORK_ERROR",
                "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "WEB_INTERACTION_REQUIRED", "STORAGE_ERROR", "CORRUPT_STORAGE",
                "RESOURCE_LIMIT" })
            {
                var error = new BridgeException(code);
                Equal(code, error.Code);
                Equal(code, error.Message);
            }
            var unknown = new BridgeException("synthetic-private-provider-message");
            Equal("INTERNAL", unknown.Code);
            Equal("INTERNAL", unknown.Message);
            return Task.CompletedTask;
        });
        yield return new("bridge: strict JSON roundtrips Unicode scalar values and keys", () =>
        {
            var value = new Dictionary<string, object?>
            {
                ["歌曲🎵"] = "音乐🎵e\u0301\0", ["é"] = true, ["e\u0301"] = false,
                ["\0\u007f"] = new object?[] { 1.5, null, "𐀀" }
            };
            var bytes = BridgeCodec.EncodeJSONValue(value);
            var result = BridgeCodec.DecodeJSONValue(bytes);
            Equal("音乐🎵e\u0301\0", result.GetProperty("歌曲🎵").GetString());
            Equal(true, result.GetProperty("é").GetBoolean());
            Equal(false, result.GetProperty("e\u0301").GetBoolean());
            Equal("𐀀", result.GetProperty("\0\u007f")[2].GetString());
            Equal(JsonValueKind.Null, BridgeCodec.Value(null).ValueKind);
            return Task.CompletedTask;
        });
        foreach (var (name, json) in new[]
        {
            ("duplicate key", """{"a":1,"a":2}"""),
            ("duplicate escaped key", """{"a":1,"\u0061":2}"""),
            ("duplicate Unicode key", """{"歌曲":1,"\u6b4c\u66f2":2}"""),
            ("nested duplicate key", """{"value":[{"x":1,"x":2}]}"""),
            ("unsafe integer", "9007199254740992"),
            ("negative unsafe integer", "-9007199254740992"),
            ("nonfinite numeric range", "1e309"),
            ("unpaired high surrogate", "\"\\ud800\""),
            ("unpaired low surrogate", "\"\\udfff\""),
            ("invalid Unicode key", """{"\ud800":1}"""),
            ("truncated JSON", "["),
            ("trailing comma", "[1,]"),
            ("comment", "/*x*/1"),
            ("multiple values", "null true")
        })
            yield return new("bridge: strict JSON rejects " + name, () =>
            {
                Throws("INVALID_MESSAGE", () => BridgeCodec.DecodeJSONValue(Encoding.UTF8.GetBytes(json)));
                return Task.CompletedTask;
            });
        yield return new("bridge: strict UTF-8 rejects malformed scalars and incomplete input", () =>
        {
            foreach (var bytes in new byte[][]
            {
                [0xff], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82], []
            })
                Throws("INVALID_MESSAGE", () => BridgeCodec.DecodeJSONValue(bytes));
            Equal(0, BridgeCodec.DecodeJSONValue(new byte[] { 0xef, 0xbb, 0xbf, (byte)'0' }).GetInt32());
            Throws("INVALID_MESSAGE", () => BridgeCodec.DecodeJSONValue(
                new byte[] { 0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf, (byte)'0' }));
            return Task.CompletedTask;
        });
        yield return new("bridge: outgoing JSON rejects lossy strings chars nodes and keys", () =>
        {
            using var invalidElement = JsonDocument.Parse("\"\\ud800\"");
            foreach (var value in new object[]
            {
                "\ud800", "\udfff", "x\ud800y", '\ud800', '\udfff', invalidElement.RootElement,
                JsonValue.Create("\ud800")!, new JsonObject { ["value"] = JsonValue.Create("\udfff") },
                new JsonObject { ["\ud800"] = 1 }, new Dictionary<string, object> { ["\udfff"] = 1 },
                double.NaN, double.PositiveInfinity, 9007199254740992L
            })
            {
                Throws("INVALID_MESSAGE", () => BridgeCodec.Value(value));
                Throws("INVALID_MESSAGE", () => BridgeCodec.EncodeJSONValue(value));
            }
            var node = JsonNode.Parse("""{"歌曲":["音乐🎵",1,true,null]}""")!;
            Equal("音乐🎵", BridgeCodec.Value(node).GetProperty("歌曲")[0].GetString());
            return Task.CompletedTask;
        });
        yield return new("bridge: business JSON keeps JS numeric spelling and 64-level value depth", () =>
        {
            foreach (var number in new[] { "0", "-0", "1.0", "1e0", "1.5", "1e-9999", "5e-324",
                "9007199254740991", "9007199254740991.1", "-9007199254740991" })
                _ = BridgeCodec.DecodeJSONValue(Encoding.UTF8.GetBytes(number));
            foreach (var json in new[] { new string('[', 64) + "0" + new string(']', 64),
                new string('[', 65) + new string(']', 65) })
            {
                var value = BridgeCodec.DecodeJSONValue(Encoding.UTF8.GetBytes(json));
                Equal(json, Encoding.UTF8.GetString(BridgeCodec.EncodeJSONValue(value)));
                var node = JsonNode.Parse(json, documentOptions: new() { MaxDepth = 67 });
                Equal(json, Encoding.UTF8.GetString(BridgeCodec.EncodeJSONValue(node)));
            }
            foreach (var json in new[] { new string('[', 65) + "0" + new string(']', 65),
                new string('[', 66) + new string(']', 66) })
            {
                Throws("INVALID_MESSAGE", () => BridgeCodec.DecodeJSONValue(Encoding.UTF8.GetBytes(json)));
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 67 });
                Throws("INVALID_MESSAGE", () => BridgeCodec.EncodeJSONValue(document.RootElement));
            }
            return Task.CompletedTask;
        });
        yield return new("bridge: complete large JSON values and arrays roundtrip", () =>
        {
            var text = new string('x', 8 * 1024 * 1024) + "音乐🎵\"\\";
            foreach (var value in new object[] { text, new JsonObject { ["歌曲"] = text } })
            {
                var bytes = BridgeCodec.EncodeJSONValue(value);
                Equal(true, bytes.Length > 8 * 1024 * 1024);
                foreach (var decoded in new[] { BridgeCodec.DecodeJSONValue(bytes), BridgeCodec.Value(value) })
                    Equal(text, decoded.ValueKind == JsonValueKind.String ? decoded.GetString() : decoded.GetProperty("歌曲").GetString());
            }
            var array = Enumerable.Repeat(false, 250_001).ToArray();
            Equal(array.Length, BridgeCodec.DecodeJSONValue(BridgeCodec.EncodeJSONValue(array)).GetArrayLength());
            return Task.CompletedTask;
        });
        yield return new("bridge: strict JSON corpus matches the current public JS codec", JsCodecParity);
        yield return new("bridge: native HTTP interoperates with real JS cancellation errors and cleanup ownership", JsHttp);
    }

    private static Process StartNode(string source)
    {
        var start = new ProcessStartInfo(TestWorkspace.NodePath)
        {
            WorkingDirectory = TestWorkspace.SourceRoot, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Remove("NODE_OPTIONS");
        start.Environment.Remove("NODE_PATH");
        start.Environment.Remove("NODE_V8_COVERAGE");
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("--eval"); start.ArgumentList.Add(source);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the JS protocol fixture");
    }

    private static async Task StopNode(Process node)
    {
        if (!node.HasExited)
        {
            node.Kill(entireProcessTree: true);
            await node.WaitForExitAsync().WaitAsync(TestDeadline);
        }
    }

    private static string ModuleUrl => JsonSerializer.Serialize(new Uri(Path.Combine(
        TestWorkspace.SourceRoot, "packages/runtime-bridge/src/index.js")).AbsoluteUri);

    private static async Task JsCodecParity()
    {
        var bodies = new List<(byte[] Bytes, bool Accepted)>();
        foreach (var json in new[]
        {
            "null", "true", "false", "0", "-0", "0.125", "1e-9999", "5e-324", "9007199254740991",
            "9007199254740991.1", "-9007199254740991", "\"音乐🎵é e\\u0301\\u0000\"", "\"\\ud800\\udc00\"",
            "[]", "{}", "{\"a\":1,\"A\":2}", "{\"\\u0000\":1}", "{\"\\u007f\":1}",
            "{\"\\u0080\":1}", "{\"歌曲\":1}", "{\"__proto__\":1}", "\uFEFF0",
            new string('[', 64) + "0" + new string(']', 64), new string('[', 65) + new string(']', 65)
        }) bodies.Add((Encoding.UTF8.GetBytes(json), true));
        foreach (var json in new[]
        {
            "9007199254740992", "-9007199254740992", "1e309", "1.7976931348623157e308",
            "\"\\ud800\"", "\"\\udfff\"", "\"\\ud800x\"", "{\"a\":1,\"\\u0061\":2}",
            "{\"a\":1,\"a\":2}", "{\"\\ud800\":1}", "[1,]", "{\"a\":1,}", "NaN", "+1", "01", "/*x*/1", "",
            "\uFEFF\uFEFF0", new string('[', 65) + "0" + new string(']', 65), new string('[', 66) + new string(']', 66)
        }) bodies.Add((Encoding.UTF8.GetBytes(json), false));
        foreach (var invalid in new byte[][]
        {
            [0xff], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82]
        }) bodies.Add((new byte[] { (byte)'"' }.Concat(invalid).Append((byte)'"').ToArray(), false));
        using var node = StartNode($$"""
            import { decodeJSONValue } from {{ModuleUrl}};
            let input = ''; for await (const chunk of process.stdin) input += chunk;
            const results = JSON.parse(input).map(encoded => {
              try { decodeJSONValue(Buffer.from(encoded, 'base64')); return true; }
              catch { return false; }
            });
            process.stdout.write(JSON.stringify(results));
            """);
        var output = node.StandardOutput.ReadToEndAsync();
        var error = node.StandardError.ReadToEndAsync();
        try
        {
            await node.StandardInput.WriteAsync(JsonSerializer.Serialize(bodies.Select(body => Convert.ToBase64String(body.Bytes)).ToArray()));
            node.StandardInput.Close();
            await node.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Equal(0, node.ExitCode); Equal("", await error);
            var actual = JsonSerializer.Deserialize<bool[]>(await output)!;
            Equal(bodies.Count, actual.Length);
            for (var i = 0; i < bodies.Count; i++)
            {
                bool accepted;
                try { _ = BridgeCodec.DecodeJSONValue(bodies[i].Bytes); accepted = true; }
                catch (BridgeException failure) when (failure.Code == "INVALID_MESSAGE") { accepted = false; }
                if (bodies[i].Accepted != actual[i] || bodies[i].Accepted != accepted)
                    throw new InvalidOperationException($"JS/native strict JSON contract mismatch at corpus index {i}");
            }
        }
        finally { await StopNode(node); }
    }

    private static async Task JsHttp()
    {
        const string sessionId = "e4a110cc-4896-40ea-a866-07aab1893806";
        var key = new string('b', 64); // Synthetic fixture identity.
        using var node = StartNode($$$"""
            import { createHttpServer } from {{{ModuleUrl}}};
            import { createHash } from 'node:crypto';
            import { createInterface } from 'node:readline';
            const sessionId = {{{JsonSerializer.Serialize(sessionId)}}}, key = {{{JsonSerializer.Serialize(key)}}};
            const lines = createInterface({input:process.stdin});
            let release, stopping, calls = 0;
            const held = new Promise(resolve => { release = resolve; });
            const okay = (value, contract = 'music.script.v3') => ({ok:true, contract, value});
            const server = createHttpServer({sessionId, key, resolve: ({method, url}) => {
              if (method === 'PUT' && url.pathname === '/v1/source/sources/candidate/3') return {
                contentType:'application/octet-stream', contract:'source.runtime.v2', codes:new Set(),
                handler:bytes => okay({sourceBytes:bytes.length, contentHash:createHash('sha256').update(bytes).digest('hex')}, 'source.runtime.v2')};
              if (method !== 'POST') return;
              if (url.pathname === '/v1/music/sources.list') return {
                contentType:'application/json', contract:'music.script.v3', codes:new Set(),
                handler:() => okay({calls})};
              if (url.pathname !== '/v1/music/invoke') return;
              return {contentType:'application/json', contract:'music.script.v3', codes:new Set(),
                handler:async (input, {signal}) => {
                  calls++;
                  if (input.fail) throw new Error('synthetic-private-payload');
                  if (input.wait || input.hold) {
                    process.stdout.write('ENTERED\n');
                    const cancelled = new Promise(resolve => signal.addEventListener('abort', () => {
                      process.stdout.write('CANCELLED\n'); resolve();
                    }, {once:true}));
                    await (input.hold ? held : cancelled);
                  }
                  return okay(input);
                }};
            }});
            lines.on('line', command => {
              if (command === 'release') release();
              else if (command === 'close') {
                stopping = server.close(); process.stdout.write('CLOSING\n');
                void stopping.then(() => { process.stdout.write('CLOSED\n'); lines.close(); process.stdin.destroy(); });
              }
            });
            process.stdout.write(await server.start() + '\n');
            """);
        var error = node.StandardError.ReadToEndAsync();
        try
        {
            var baseUrl = await node.StandardOutput.ReadLineAsync().WaitAsync(TestDeadline)
                ?? throw new InvalidOperationException("Missing HTTP fixture endpoint");
            await using var client = new RuntimeHttpClient(baseUrl, sessionId, key);
            using var cancelledBeforeSend = new CancellationTokenSource(); cancelledBeforeSend.Cancel();
            try { await client.CallAsync("music.script.invoke", cancellationToken: cancelledBeforeSend.Token); throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Equal(0, (await client.CallAsync("music.script.sources.list")).GetProperty("value").GetProperty("calls").GetInt32());
            var text = new string('音', 400_000) + "🎵\"\\";
            var result = await client.CallAsync("music.script.invoke", new Dictionary<string, object?> { ["歌曲"] = text });
            Equal("music.script.v3", result.GetProperty("contract").GetString());
            Equal(text, result.GetProperty("value").GetProperty("歌曲").GetString());
            var replies = await Task.WhenAll(Enumerable.Range(0, 40).Select(index => client.CallAsync("music.script.invoke", new { index })));
            Equal(40, replies.Select(reply => reply.GetProperty("value").GetProperty("index").GetInt32()).Distinct().Count());
            var source = Encoding.UTF8.GetBytes("// " + text);
            var upload = await client.UploadSourceAsync("source", "candidate", 3, source, "source.js");
            Equal(source.Length, upload.GetProperty("value").GetProperty("sourceBytes").GetInt32());
            Equal(Convert.ToHexStringLower(SHA256.HashData(source)), upload.GetProperty("value").GetProperty("contentHash").GetString());
            var failed = await client.CallAsync("music.script.invoke", new { fail = true });
            Equal(false, failed.GetProperty("ok").GetBoolean());
            Equal(RuntimeHttpProtocol.Name, failed.GetProperty("contract").GetString());
            Equal("INVALID_RESPONSE", failed.GetProperty("code").GetString());
            Equal(3, failed.EnumerateObject().Count());
            using var cancellation = new CancellationTokenSource();
            var pending = client.CallAsync("music.script.invoke", new { wait = true }, cancellation.Token);
            Equal("ENTERED", await node.StandardOutput.ReadLineAsync().WaitAsync(TestDeadline));
            Equal(7, (await client.CallAsync("music.script.invoke", new { index = 7 })).GetProperty("value").GetProperty("index").GetInt32());
            cancellation.Cancel();
            try { await pending; throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Equal("CANCELLED", await node.StandardOutput.ReadLineAsync().WaitAsync(TestDeadline));
            var held = client.CallAsync("music.script.invoke", new { hold = true });
            Equal("ENTERED", await node.StandardOutput.ReadLineAsync().WaitAsync(TestDeadline));
            await node.StandardInput.WriteLineAsync("close");
            var closingLines = new[] { await node.StandardOutput.ReadLineAsync().WaitAsync(TestDeadline),
                await node.StandardOutput.ReadLineAsync().WaitAsync(TestDeadline) };
            Equal(true, closingLines.Contains("CLOSING") && closingLines.Contains("CANCELLED"));
            var closed = node.StandardOutput.ReadLineAsync();
            Equal(false, closed.IsCompleted);
            Equal(false, node.HasExited);
            await Expect("CLOSED", () => held);
            await node.StandardInput.WriteLineAsync("release");
            Equal("CLOSED", await closed.WaitAsync(TestDeadline));
            await node.WaitForExitAsync().WaitAsync(TestDeadline);
            Equal(0, node.ExitCode); Equal("", await error);
        }
        finally { await StopNode(node); }
    }

    internal static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> Pair()
    {
        var name = "meowfilm-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var connection = server.WaitForConnectionAsync(timeout.Token);
            await client.ConnectAsync(timeout.Token);
            await connection;
            return (server, client);
        }
        catch { client.Dispose(); server.Dispose(); throw; }
    }
    internal static async Task Expect(string code, Func<Task> action)
    {
        try { await action(); }
        catch (BridgeException error) when (error.Code == code) { return; }
        throw new InvalidOperationException($"Expected {code}");
    }
    internal static void Throws(string code, Action action)
    {
        try { action(); }
        catch (BridgeException error) when (error.Code == code) { return; }
        throw new InvalidOperationException($"Expected {code}");
    }
    internal static void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Assertion mismatch at line {line}");
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class BridgeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("bridge: exact UTF-8 frame roundtrip", () =>
        {
            var frame = BridgeCodec.Encode(new { version = 1, session = "test-1", id = 1, kind = "result", value = "音乐🎵e\u0301" });
            Equal(frame.Length - 4, BinaryPrimitives.ReadInt32BigEndian(frame));
            Equal("音乐🎵e\u0301", BridgeCodec.Decode(frame.AsMemory(4)).GetProperty("value").GetString());
            return Task.CompletedTask;
        });
        foreach (var (name, json) in new[]
        {
            ("duplicate escaped key", """{"version":1,"session":"s","id":1,"kind":"result","value":{"a":1,"\u0061":2}}"""),
            ("Unicode key", """{"version":1,"session":"s","id":1,"kind":"result","value":{"歌曲":2}}"""),
            ("extra field", """{"version":1,"session":"s","id":1,"kind":"cancel","extra":null}"""),
            ("unknown error", """{"version":1,"session":"s","id":1,"kind":"error","code":"secret URL"}"""),
            ("unsafe integer", """{"version":1,"session":"s","id":1,"kind":"result","value":9007199254740992}"""),
            ("unpaired surrogate", """{"version":1,"session":"s","id":1,"kind":"result","value":"\ud800"}"""),
            ("invalid session", """{"version":1,"session":"s\n","id":1,"kind":"cancel"}"""),
            ("nonfinite numeric range", """{"version":1,"session":"s","id":1,"kind":"result","value":1e309}""")
        })
            yield return new("bridge: reject " + name, () =>
            {
                Throws("INVALID_MESSAGE", () => BridgeCodec.Decode(Encoding.UTF8.GetBytes(json)));
                return Task.CompletedTask;
            });
        yield return new("bridge: invalid UTF-8 rejected", () =>
        {
            Throws("INVALID_MESSAGE", () => BridgeCodec.Decode(new byte[] { 0xff, 0xfe }));
            return Task.CompletedTask;
        });
        yield return new("bridge: oversized frame rejected before allocation", async () =>
        {
            var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, BridgeCodec.MaximumFrameBytes + 1);
            await Expect("LIMIT", async () => await BridgeCodec.ReadAsync(new MemoryStream(bytes), default));
        });
        yield return new("bridge: real local pipe bidirectional requests and finite errors", async () =>
        {
            var streams = await Pair();
            await using var server = new BridgePeer(streams.Server, "roundtrip",
                new Dictionary<string, Func<System.Text.Json.JsonElement, CancellationToken, Task<object?>>>
                {
                    ["native.echo"] = (value, _) => Task.FromResult<object?>(value),
                    ["native.failure"] = (_, _) => throw new InvalidOperationException("private payload")
                });
            await using var client = new BridgePeer(streams.Client, "roundtrip",
                new Dictionary<string, Func<System.Text.Json.JsonElement, CancellationToken, Task<object?>>>
                { ["domain.echo"] = (value, _) => Task.FromResult<object?>(value) });
            var requests = Enumerable.Range(0, 40).Select(i => client.CallAsync("native.echo", new { index = i })).ToArray();
            var replies = await Task.WhenAll(requests);
            Equal(40, replies.Select(value => value.GetProperty("index").GetInt32()).Distinct().Count());
            Equal("正向", (await server.CallAsync("domain.echo", "正向")).GetString());
            await Expect("METHOD_UNAVAILABLE", () => client.CallAsync("missing.operation"));
            await Expect("INTERNAL", () => client.CallAsync("native.failure"));
            Equal(0, client.PendingCount);
        });
        yield return new("bridge: timeout cancels remote operation", async () =>
        {
            var streams = await Pair();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new BridgePeer(streams.Server, "timeout",
                new Dictionary<string, Func<System.Text.Json.JsonElement, CancellationToken, Task<object?>>>
                {
                    ["native.wait"] = async (_, token) =>
                    {
                        try { await Task.Delay(Timeout.Infinite, token); }
                        finally { cancelled.TrySetResult(); }
                        return null;
                    }
                });
            await using var client = new BridgePeer(streams.Client, "timeout");
            await Expect("TIMEOUT", () => client.CallAsync("native.wait", timeout: TimeSpan.FromMilliseconds(60)));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Equal(0, client.PendingCount);
        });
        yield return new("bridge: cancellation before send and pending-close", async () =>
        {
            var streams = await Pair();
            await using var server = new BridgePeer(streams.Server, "close",
                new Dictionary<string, Func<System.Text.Json.JsonElement, CancellationToken, Task<object?>>>
                { ["native.wait"] = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; } });
            await using var client = new BridgePeer(streams.Client, "close");
            using var token = new CancellationTokenSource(); token.Cancel();
            await Expect("CANCELLED", () => client.CallAsync("native.wait", cancellationToken: token.Token));
            var pending = client.CallAsync("native.wait");
            client.Close();
            await Expect("CLOSED", () => pending);
            Equal(0, client.PendingCount);
        });
        yield return new("bridge: wrong generation closes without adopting result", async () =>
        {
            var streams = await Pair();
            await using var server = new BridgePeer(streams.Server, "generation-2");
            await using var client = new BridgePeer(streams.Client, "generation-1");
            await Expect("CLOSED", () => client.CallAsync("runtime.describe"));
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Equal(true, server.IsClosed);
        });
        yield return new("bridge: oversized result returns LIMIT not timeout", async () =>
        {
            var streams = await Pair();
            await using var server = new BridgePeer(streams.Server, "budget",
                new Dictionary<string, Func<System.Text.Json.JsonElement, CancellationToken, Task<object?>>>
                { ["native.large"] = (_, _) => Task.FromResult<object?>(new string('x', BridgeCodec.MaximumFrameBytes)) });
            await using var client = new BridgePeer(streams.Client, "budget");
            await Expect("LIMIT", () => client.CallAsync("native.large"));
        });
        yield return new("bridge: outgoing strings and chars reject lossy Unicode", () =>
        {
            using var invalidElement = JsonDocument.Parse("\"\\ud800\"");
            foreach (var value in new object[] { "\ud800", "\udfff", "x\ud800y", '\ud800', '\udfff',
                invalidElement.RootElement, System.Text.Json.Nodes.JsonValue.Create("\ud800")! })
            {
                try
                {
                    Throws("INVALID_MESSAGE", () => BridgeCodec.Value(value));
                    Throws("INVALID_MESSAGE", () => BridgeCodec.Encode(new { version = 1, session = "s", id = 1, kind = "result", value }));
                }
                catch (InvalidOperationException) { throw new InvalidOperationException($"Lossy Unicode accepted from {value.GetType().Name}"); }
            }
            var node = System.Text.Json.Nodes.JsonNode.Parse("""{"data":["音乐🎵",1,true,null]}""")!;
            Equal("音乐🎵", BridgeCodec.Value(node).GetProperty("data")[0].GetString());
            Equal(false, BridgeCodec.ValidMethod(null));
            Equal(false, BridgeCodec.ValidSession(null));
            return Task.CompletedTask;
        });
        yield return new("bridge: JS numeric spelling and depth boundaries", () =>
        {
            foreach (var id in new[] { "1", "1.0", "1e0", "9007199254740991", "9007199254740991.1" })
                _ = BridgeCodec.Decode(Encoding.UTF8.GetBytes($$"""{"version":1e0,"session":"s","id":{{id}},"kind":"cancel"}"""));
            foreach (var id in new[] { "0", "-0", "-1", "1.1", "1e309", "9007199254740992" })
                Throws("INVALID_MESSAGE", () => BridgeCodec.Decode(Encoding.UTF8.GetBytes($$"""{"version":1,"session":"s","id":{{id}},"kind":"cancel"}""")));
            _ = BridgeCodec.Decode(ResultBody(new string('[', 63) + "0" + new string(']', 63)));
            _ = BridgeCodec.Decode(ResultBody(new string('[', 64) + new string(']', 64)));
            Throws("INVALID_MESSAGE", () => BridgeCodec.Decode(ResultBody(new string('[', 64) + "0" + new string(']', 64))));
            Throws("INVALID_MESSAGE", () => BridgeCodec.Decode(ResultBody(new string('[', 65) + new string(']', 65))));
            return Task.CompletedTask;
        });
        yield return new("bridge: exact one MiB frame and UTF-8 byte limits", () =>
        {
            var overhead = ResultBody("\"\"").Length;
            var exact = ResultBody("\"" + new string('x', BridgeCodec.MaximumFrameBytes - overhead) + "\"");
            Equal(BridgeCodec.MaximumFrameBytes, exact.Length);
            var decoded = BridgeCodec.Decode(exact);
            Equal(BridgeCodec.MaximumFrameBytes + 4, BridgeCodec.Encode(decoded).Length);
            Throws("LIMIT", () => BridgeCodec.Decode(exact.Concat(new byte[] { 0x20 }).ToArray()));
            Throws("LIMIT", () => BridgeCodec.Encode(new { version = 1, session = "s", id = 1, kind = "result",
                value = new string('音', BridgeCodec.MaximumFrameBytes / 3 + 1) }));
            return Task.CompletedTask;
        });
        yield return new("bridge: fragmented coalesced and truncated framing", Framing);
        yield return new("bridge: codec corpus agrees with actual JS decoder", JsCodecParity);
        yield return new("bridge: actual JS pipe concurrent bidirectional calls and cancellation", JsPipe);
        yield return new("bridge: exponent IDs dispatch and settle on a real pipe", NumericIds);
        yield return new("bridge: synchronous handler does not block reads or cancellation", SynchronousHandler);
        yield return new("bridge: blocked error write does not block incoming cancellation", BlockedError);
        yield return new("bridge: cancelled unsent mutation never reaches the peer", CancelUnsent);
        yield return new("bridge: data backpressure returns finite LIMIT using control reserve", ResultBackpressure);
        yield return new("bridge: exhausted control queue closes instead of dropping frames", ControlBackpressure);
        yield return new("bridge: pending and cancelled frame queues remain bounded", PendingBounds);
        yield return new("bridge: close tolerates throwing reentrant cancellation callbacks", ThrowingCancellation);
        yield return new("bridge: blocking cancellation callback retains ownership not transport", BlockingCancellation);
        yield return new("bridge: noncooperative timeout retains active admission budget", ActiveBounds);
        yield return new("bridge: close stops timers and suppresses old generation results", OldGeneration);
        yield return new("bridge: cancel timeout result races emit one terminal frame", TerminalRace);
        yield return new("bridge: replayed IDs close before a second handler starts", Replay);
        yield return new("bridge: serialization does not hold the close lock", SerializationLock);
        yield return new("bridge: invalid handler result becomes finite INVALID_MESSAGE", InvalidResult);
        yield return new("bridge: throwing transport dispose still settles pending", ThrowingDispose);
        yield return new("bridge: streaming serialization stops at the frame budget", SerializationBudget);
        yield return new("bridge: timer cancellation callback exceptions stay contained", TimerCancellation);
        yield return new("bridge: concurrent close and cancellation dispose transport once", ConcurrentClose);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> Echo =
        new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
        { ["native.echo"] = (value, _) => Task.FromResult<object?>(value) };

    private static byte[] ResultBody(string value) =>
        Encoding.UTF8.GetBytes($$"""{"version":1,"session":"s","id":1,"kind":"result","value":{{value}}}""");
    private static byte[] Request(long id, string method = "native.wait", string session = "audit") =>
        BridgeCodec.Encode(new { version = 1, session, id, kind = "request", method, @params = (object?)null });
    private static byte[] Cancel(long id) =>
        BridgeCodec.Encode(new { version = 1, session = "audit", id, kind = "cancel" });
    private static byte[] RawFrame(byte[] body)
    {
        var frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }
    private static async Task<JsonElement> Read(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TestDeadline);
        return await BridgeCodec.ReadAsync(stream, timeout.Token);
    }
    private static async Task Write(Stream stream, byte[] bytes)
    {
        using var timeout = new CancellationTokenSource(TestDeadline);
        await stream.WriteAsync(bytes, timeout.Token);
    }
    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TestDeadline) throw new TimeoutException("Bridge state did not converge");
            await Task.Delay(5);
        }
    }

    private static async Task Framing()
    {
        var frames = RawFrame(ResultBody("\"音乐🎵\"")).Concat(RawFrame(ResultBody("1.5"))).ToArray();
        using var fragmented = new TestStream(new MemoryStream(frames), fragmentReads: true);
        Equal("音乐🎵", (await Read(fragmented)).GetProperty("value").GetString());
        Equal(1.5, (await Read(fragmented)).GetProperty("value").GetDouble());
        foreach (var count in new[] { 0, 1, 3, 4, frames.Length / 2 - 1 })
        {
            try { await Read(new MemoryStream(frames[..count])); }
            catch (EndOfStreamException) { continue; }
            throw new InvalidOperationException("Truncated frame accepted");
        }
    }

    private static async Task NumericIds()
    {
        var streams = await Pair();
        using var raw = streams.Client;
        await using var server = new BridgePeer(streams.Server, "audit", Echo);
        await Write(raw, RawFrame(Encoding.UTF8.GetBytes(
            """{"version":1,"session":"audit","id":1e0,"kind":"request","method":"native.echo","params":"音乐"}""")));
        Equal("音乐", (await Read(raw)).GetProperty("value").GetString());
        var pending = server.CallAsync("remote.echo");
        var request = await Read(raw);
        var id = request.GetProperty("id").GetInt64();
        await Write(raw, RawFrame(Encoding.UTF8.GetBytes($$"""{"version":1,"session":"audit","id":{{id}}.0,"kind":"result","value":1}""")));
        Equal(1, (await pending.WaitAsync(TestDeadline)).GetInt32());
    }

    private static async Task SynchronousHandler()
    {
        var streams = await Pair();
        using var release = new ManualResetEventSlim();
        var entered = Signal(); var cancelled = Signal();
        await using var server = new BridgePeer(streams.Server, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(Echo)
            {
                ["native.block"] = (_, token) =>
                {
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    entered.TrySetResult();
                    if (!release.Wait(TestDeadline)) throw new TimeoutException();
                    return Task.FromResult<object?>(null);
                }
            });
        await using var client = new BridgePeer(streams.Client, "audit");
        using var cancellation = new CancellationTokenSource();
        var slow = client.CallAsync("native.block", cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TestDeadline);
            Equal(7, (await client.CallAsync("native.echo", 7, timeout: TimeSpan.FromSeconds(1))).GetInt32());
            cancellation.Cancel();
            await Expect("CANCELLED", () => slow);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Equal(1, server.ActiveCount);
        }
        finally { release.Set(); client.Close(); await Eventually(() => server.ActiveCount == 0); }
    }

    private static async Task BlockedError()
    {
        var streams = await Pair();
        using var raw = streams.Client;
        var gate = new TestStream(streams.Server, holdWrites: true);
        var entered = Signal(); var cancelled = Signal(); var release = Signal();
        await using var server = new BridgePeer(gate, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
            {
                ["native.wait"] = async (_, token) =>
                {
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    entered.TrySetResult(); await release.Task; return null;
                }
            });
        try
        {
            await Write(raw, Request(1)); await entered.Task.WaitAsync(TestDeadline);
            await Write(raw, Request(2, "missing.method")); await gate.Writing.Task.WaitAsync(TestDeadline);
            await Write(raw, Cancel(1)); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            gate.ReleaseWrites();
            var errors = new[] { await Read(raw), await Read(raw) }.ToDictionary(value => value.GetProperty("id").GetInt64());
            Equal("CANCELLED", errors[1].GetProperty("code").GetString());
            Equal("METHOD_UNAVAILABLE", errors[2].GetProperty("code").GetString());
        }
        finally { release.TrySetResult(); gate.ReleaseWrites(); server.Close(); await Eventually(() => server.ActiveCount == 0); }
    }

    private static async Task CancelUnsent()
    {
        var streams = await Pair();
        var gate = new TestStream(streams.Client, holdWrites: true);
        var mutations = 0;
        await using var server = new BridgePeer(streams.Server, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(Echo)
            { ["native.mutate"] = (_, _) => Task.FromResult<object?>(Interlocked.Increment(ref mutations)) });
        await using var client = new BridgePeer(gate, "audit");
        var first = client.CallAsync("native.echo", 1);
        await gate.Writing.Task.WaitAsync(TestDeadline);
        using var cancellation = new CancellationTokenSource();
        var second = client.CallAsync("native.mutate", cancellationToken: cancellation.Token);
        cancellation.Cancel(); await Expect("CANCELLED", () => second);
        gate.ReleaseWrites();
        Equal(1, (await first.WaitAsync(TestDeadline)).GetInt32());
        Equal(2, (await client.CallAsync("native.echo", 2)).GetInt32());
        Equal(0, mutations);
        await Eventually(() => client.QueuedBytes == 0);
    }

    private static async Task ResultBackpressure()
    {
        var streams = await Pair();
        var gate = new TestStream(streams.Server, holdWrites: true);
        var started = 0; var allStarted = Signal();
        await using var server = new BridgePeer(gate, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
            {
                ["native.large"] = (_, _) =>
                {
                    if (Interlocked.Increment(ref started) == 12) allStarted.TrySetResult();
                    return Task.FromResult<object?>(new string('x', 900_000));
                }
            });
        await using var client = new BridgePeer(streams.Client, "audit");
        var calls = Enumerable.Range(0, 12).Select(async _ =>
        {
            try { Equal(900_000, (await client.CallAsync("native.large", timeout: TestDeadline)).GetString()!.Length); return "result"; }
            catch (BridgeException error) { return error.Code; }
        }).ToArray();
        try
        {
            await allStarted.Task.WaitAsync(TestDeadline);
            await Eventually(() => server.ActiveCount == 0);
            Equal(true, server.QueuedBytes < 8L * BridgeCodec.MaximumFrameBytes);
            gate.ReleaseWrites();
            var outcomes = await Task.WhenAll(calls).WaitAsync(TestDeadline);
            Equal(9, outcomes.Count(value => value == "result"));
            Equal(3, outcomes.Count(value => value == "LIMIT"));
            Equal(false, server.IsClosed);
            await Eventually(() => server.QueuedBytes == 0);
        }
        finally { gate.ReleaseWrites(); client.Close(); }
    }

    private static async Task ControlBackpressure()
    {
        var streams = await Pair();
        using var raw = streams.Client;
        var gate = new TestStream(streams.Server, holdWrites: true);
        await using var server = new BridgePeer(gate, "audit");
        try
        {
            // The small control frames must be bounded by count, not only bytes.
            for (var i = 1; i <= 520; i++) await Write(raw, Request(i, "missing.method"));
        }
        catch (IOException) when (server.IsClosed) { }
        await server.Completion.WaitAsync(TestDeadline);
        Equal(true, server.IsClosed); Equal(0L, server.QueuedBytes);
    }

    private static async Task PendingBounds()
    {
        var streams = await Pair();
        using var raw = streams.Server;
        var gate = new TestStream(streams.Client, holdWrites: true);
        await using var peer = new BridgePeer(gate, "audit");
        var pending = Enumerable.Range(0, BridgePeer.MaximumPending).Select(_ => peer.CallAsync("remote.wait")).ToArray();
        await Expect("LIMIT", () => peer.CallAsync("remote.wait"));
        Equal(BridgePeer.MaximumPending, peer.PendingCount);
        peer.Close();
        foreach (var task in pending) await Expect("CLOSED", () => task);
        await peer.Completion.WaitAsync(TestDeadline); Equal(0L, peer.QueuedBytes);

        var second = await Pair();
        using var raw2 = second.Server;
        var gate2 = new TestStream(second.Client, holdWrites: true);
        await using var cancelled = new BridgePeer(gate2, "audit");
        var first = cancelled.CallAsync("remote.wait");
        await gate2.Writing.Task.WaitAsync(TestDeadline);
        var limited = 0;
        for (var i = 0; i < 600; i++)
        {
            using var token = new CancellationTokenSource();
            var call = cancelled.CallAsync("remote.wait", cancellationToken: token.Token);
            token.Cancel();
            try { await call; throw new InvalidOperationException("Cancelled call succeeded"); }
            catch (BridgeException error) when (error.Code is "CANCELLED" or "LIMIT") { if (error.Code == "LIMIT") limited++; }
        }
        Equal(true, limited > 0);
        Equal(1, cancelled.PendingCount); Equal(false, cancelled.IsClosed);
        cancelled.Close(); await Expect("CLOSED", () => first);
        await cancelled.Completion.WaitAsync(TestDeadline); Equal(0L, cancelled.QueuedBytes);
    }

    private static async Task ThrowingCancellation()
    {
        var streams = await Pair();
        using var raw = streams.Client;
        var entered = Signal(); var callback = Signal(); var release = Signal();
        BridgePeer? server = null;
        await using var owner = server = new BridgePeer(streams.Server, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
            {
                ["native.wait"] = async (_, token) =>
                {
                    using var registration = token.Register(() =>
                    {
                        server!.Close(); callback.TrySetResult();
                        throw new InvalidOperationException("private cancellation error");
                    });
                    entered.TrySetResult(); await release.Task; return null;
                }
            });
        await Write(raw, Request(1)); await entered.Task.WaitAsync(TestDeadline);
        var pending = server.CallAsync("remote.wait");
        try
        {
            await Task.Run(server.Close).WaitAsync(TestDeadline);
            await Expect("CLOSED", () => pending);
            await server.Completion.WaitAsync(TestDeadline);
            await callback.Task.WaitAsync(TestDeadline);
            Equal(0, server.PendingCount); Equal(1, server.ActiveCount);
        }
        finally { release.TrySetResult(); await Eventually(() => server.ActiveCount == 0); }
    }

    private static async Task BlockingCancellation()
    {
        var streams = await Pair();
        using var raw = streams.Client;
        var entered = Signal(); var callback = Signal(); var handlerRelease = Signal(); var callbackRelease = Signal();
        await using var server = new BridgePeer(streams.Server, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
            {
                ["native.wait"] = async (_, token) =>
                {
                    // Intentionally retain the registration; the operation owns
                    // the CTS until the callback AND handler have both finished.
                    token.Register(() =>
                    {
                        callback.TrySetResult();
                        callbackRelease.Task.Wait(TestDeadline);
                    });
                    entered.TrySetResult(); await handlerRelease.Task; return null;
                }
            });
        try
        {
            await Write(raw, Request(1)); await entered.Task.WaitAsync(TestDeadline);
            await Task.Run(server.Close).WaitAsync(TimeSpan.FromSeconds(1));
            await callback.Task.WaitAsync(TestDeadline);
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(1));
            handlerRelease.TrySetResult();
            await Task.Delay(30);
            Equal(1, server.ActiveCount);
        }
        finally
        {
            handlerRelease.TrySetResult(); callbackRelease.TrySetResult();
            await Eventually(() => server.ActiveCount == 0);
        }
    }

    private static async Task ActiveBounds()
    {
        var streams = await Pair();
        using var raw = streams.Client;
        var release = Signal(); var started = Signal(); var count = 0;
        await using var server = new BridgePeer(streams.Server, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
            {
                ["native.wait"] = async (_, _) =>
                {
                    if (Interlocked.Increment(ref count) == BridgePeer.MaximumPending) started.TrySetResult();
                    await release.Task; return null;
                }
            }, TimeSpan.FromSeconds(1));
        try
        {
            for (var i = 1; i <= BridgePeer.MaximumPending; i++) await Write(raw, Request(i));
            await started.Task.WaitAsync(TestDeadline);
            var replies = new HashSet<long>();
            for (var i = 0; i < BridgePeer.MaximumPending; i++)
            {
                var reply = await Read(raw);
                Equal("TIMEOUT", reply.GetProperty("code").GetString());
                Equal(true, replies.Add(reply.GetProperty("id").GetInt64()));
            }
            Equal(BridgePeer.MaximumPending, server.ActiveCount);
            await Write(raw, Request(BridgePeer.MaximumPending + 1));
            Equal("LIMIT", (await Read(raw)).GetProperty("code").GetString());
            server.Close(); await server.Completion.WaitAsync(TestDeadline);
            Equal(BridgePeer.MaximumPending, server.ActiveCount);
        }
        finally { release.TrySetResult(); await Eventually(() => server.ActiveCount == 0); }
    }

    private static async Task OldGeneration()
    {
        var baselineTimers = Timer.ActiveCount;
        var streams = await Pair();
        using var raw = streams.Client;
        var transport = new TestStream(streams.Server);
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        await using var old = new BridgePeer(transport, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
            {
                ["native.wait"] = async (_, token) =>
                {
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    entered.TrySetResult(); await release.Task; return "old";
                }
            }, TimeSpan.FromMinutes(5));
        try
        {
            await Write(raw, Request(1)); await entered.Task.WaitAsync(TestDeadline);
            old.Close(); await old.Completion.WaitAsync(TestDeadline);
            await cancelled.Task.WaitAsync(TestDeadline);
            Equal(1, old.ActiveCount);
            // Task.WaitAsync's own timeout timer may be completing concurrently
            // with this continuation; require convergence, not a racy snapshot.
            await Eventually(() => Timer.ActiveCount <= baselineTimers);
            Equal(0L, old.QueuedBytes);
            var writes = transport.WriteCount;
            var next = await Pair();
            await using var newServer = new BridgePeer(next.Server, "next-generation", Echo);
            await using var newClient = new BridgePeer(next.Client, "next-generation");
            release.TrySetResult(); await Eventually(() => old.ActiveCount == 0);
            Equal(writes, transport.WriteCount);
            Equal("new", (await newClient.CallAsync("native.echo", "new")).GetString());
        }
        finally { release.TrySetResult(); await Eventually(() => old.ActiveCount == 0); }
    }

    private static async Task TerminalRace()
    {
        var streams = await Pair();
        using var raw = streams.Client;
        await using var server = new BridgePeer(streams.Server, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(Echo)
            { ["native.wait"] = async (_, token) => { await Task.Delay(2, token); return 1; } },
            TimeSpan.FromMilliseconds(3));
        for (var id = 1; id <= 80; id++)
        {
            await Write(raw, Request(id));
            if (id % 3 != 0) await Write(raw, Cancel(id));
        }
        var replies = new HashSet<long>();
        for (var i = 0; i < 80; i++)
            Equal(true, replies.Add((await Read(raw)).GetProperty("id").GetInt64()));
        await Eventually(() => server.ActiveCount == 0);
        await Write(raw, Request(81, "native.echo"));
        Equal(81L, (await Read(raw)).GetProperty("id").GetInt64());
        Equal(false, server.IsClosed);
    }

    private static async Task Replay()
    {
        var streams = await Pair();
        using var raw = streams.Client;
        var calls = 0;
        await using var server = new BridgePeer(streams.Server, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
            { ["native.wait"] = (_, _) => Task.FromResult<object?>(Interlocked.Increment(ref calls)) });
        await Write(raw, Request(1)); Equal(1, (await Read(raw)).GetProperty("value").GetInt32());
        await Write(raw, Request(1)); await server.Completion.WaitAsync(TestDeadline);
        Equal(1, calls); Equal(true, server.IsClosed);
    }

    private sealed class BlockingDto(TaskCompletionSource entered, TaskCompletionSource release)
    {
        public int Value { get { entered.TrySetResult(); release.Task.Wait(TestDeadline); return 1; } }
    }
    private static async Task SerializationLock()
    {
        var streams = await Pair();
        using var raw = streams.Server;
        await using var peer = new BridgePeer(streams.Client, "audit");
        var entered = Signal(); var release = Signal();
        var call = Task.Run(() => peer.CallAsync("remote.echo", new BlockingDto(entered, release)));
        try
        {
            await entered.Task.WaitAsync(TestDeadline);
            await Task.Run(peer.Close).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { release.TrySetResult(); }
        await Expect("CLOSED", () => call);
        Equal(0, peer.PendingCount);
    }

    private static async Task InvalidResult()
    {
        var streams = await Pair();
        await using var server = new BridgePeer(streams.Server, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>
            { ["native.invalid"] = (_, _) => Task.FromResult<object?>("\ud800") });
        await using var client = new BridgePeer(streams.Client, "audit");
        await Expect("INVALID_MESSAGE", () => client.CallAsync("native.invalid"));
        Equal(false, server.IsClosed);
    }

    private static async Task ThrowingDispose()
    {
        var streams = await Pair();
        using var raw = streams.Server;
        await using var peer = new BridgePeer(new TestStream(streams.Client, throwOnDispose: true), "audit");
        var pending = peer.CallAsync("remote.wait");
        peer.Close();
        await Expect("CLOSED", () => pending);
        await peer.Completion.WaitAsync(TestDeadline);
        Equal(0, peer.PendingCount); Equal(0L, peer.QueuedBytes);
    }

    private static Task SerializationBudget()
    {
        var enumerated = 0;
        IEnumerable<int> ManyValues()
        {
            for (var i = 0; i < 2_000_000; i++) { enumerated++; yield return 0; }
        }
        Throws("LIMIT", () => BridgeCodec.Encode(new
        { version = 1, session = "s", id = 1, kind = "result", value = ManyValues() }));
        Equal(true, enumerated < 600_000);
        return Task.CompletedTask;
    }

    private static async Task TimerCancellation()
    {
        var streams = await Pair();
        using var raw = streams.Client;
        var entered = Signal(); var callback = Signal(); var release = Signal();
        await using var server = new BridgePeer(streams.Server, "audit",
            new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(Echo)
            {
                ["native.wait"] = async (_, token) =>
                {
                    using var registration = token.Register(() =>
                    { callback.TrySetResult(); throw new InvalidOperationException("private timer error"); });
                    entered.TrySetResult(); await release.Task; return null;
                }
            }, TimeSpan.FromMilliseconds(150));
        try
        {
            await Write(raw, Request(1)); await entered.Task.WaitAsync(TestDeadline);
            Equal("TIMEOUT", (await Read(raw)).GetProperty("code").GetString());
            await callback.Task.WaitAsync(TestDeadline);
            Equal(false, server.IsClosed); Equal(1, server.ActiveCount);
            release.TrySetResult(); await Eventually(() => server.ActiveCount == 0);
            await Write(raw, Request(2, "native.echo"));
            Equal(2L, (await Read(raw)).GetProperty("id").GetInt64());
        }
        finally { release.TrySetResult(); await Eventually(() => server.ActiveCount == 0); }
    }

    private static async Task ConcurrentClose()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var streams = await Pair();
            using var raw = streams.Server;
            var transport = new TestStream(streams.Client, holdWrites: true);
            await using var peer = new BridgePeer(transport, "audit");
            using var token = new CancellationTokenSource();
            var call = peer.CallAsync("remote.wait", cancellationToken: token.Token);
            await transport.Writing.Task.WaitAsync(TestDeadline);
            var close = Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            {
                if (index % 2 == 0) peer.Close(); else token.Cancel();
            })));
            await close.WaitAsync(TestDeadline);
            try { await call; throw new InvalidOperationException("Closed call succeeded"); }
            catch (BridgeException error) when (error.Code is "CLOSED" or "CANCELLED") { }
            await peer.Completion.WaitAsync(TestDeadline);
            Equal(1, transport.DisposeCount);
            Equal(0, peer.PendingCount); Equal(0L, peer.QueuedBytes);
        }
    }

    private static string RepositoryRoot() => TestWorkspace.SourceRoot;

    private static Process StartNode(string source)
    {
        var start = new ProcessStartInfo(TestWorkspace.NodePath)
        {
            WorkingDirectory = RepositoryRoot(), UseShellExecute = false, CreateNoWindow = true,
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

    private static async Task JsCodecParity()
    {
        var bodies = new List<byte[]>();
        foreach (var value in new[]
        {
            "null", "true", "false", "0", "-0", "0.125", "1e-9999", "5e-324", "9007199254740991",
            "9007199254740991.1", "9007199254740992", "-9007199254740992", "1e309", "1.7976931348623157e308",
            "\"音乐🎵é e\\u0301\\u0000\"", "\"\\ud800\\udc00\"", "\"\\ud800\"", "\"\\udfff\"", "\"\\ud800x\"",
            "[]", "{}", "{\"a\":1,\"\\u0061\":2}", "{\"a\":1,\"a\":2}", "{\"a\":1,\"A\":2}",
            "{\"\\u0000\":1}", "{\"\\u007f\":1}", "{\"\\u0080\":1}", "{\"歌曲\":1}", "{\"__proto__\":1}",
            "[1,]", "{\"a\":1,}", "NaN", "+1", "01", "/*x*/1"
        }) bodies.Add(ResultBody(value));
        foreach (var id in new[] { "1e0", "1.0", "1.00000000000000001", "1e-9999", "0", "-0", "-1", "1.5", "9007199254740991.1" })
            bodies.Add(Encoding.UTF8.GetBytes($$"""{"version":1e0,"session":"s","id":{{id}},"kind":"cancel"}"""));
        foreach (var mantissa in new[] { "0", "-0", "1", "-1.5", "0.000001", "9007199254740991" })
            foreach (var exponent in new[] { "-99999999", "-325", "-324", "-1", "0", "1", "308", "309", "99999999" })
                bodies.Add(ResultBody(mantissa + "e" + exponent));
        foreach (var json in new[]
        {
            """{"version":1,"session":"s","id":1,"kind":"cancel","kind":"cancel"}""",
            """{"version":1,"session":"s","id":1,"kind":"request","method":"x\n","params":null}""",
            """{"version":1,"session":"s\n","id":1,"kind":"cancel"}""",
            """{"version":1,"session":"s","id":true,"kind":"cancel"}""",
            """{"version":1,"session":"s","id":1,"kind":"error","code":"INTERNAL"}""",
            """{"version":1,"session":"s","id":1,"kind":"error","code":"private error"}""",
            "null", "[]", "\"x\"", "{}"
        }) bodies.Add(Encoding.UTF8.GetBytes(json));
        foreach (var depth in new[] { 62, 63, 64, 65, 66, 80 })
        {
            bodies.Add(ResultBody(new string('[', depth) + "0" + new string(']', depth)));
            bodies.Add(ResultBody(new string('[', depth) + new string(']', depth)));
        }
        bodies.Add(new byte[] { 0xef, 0xbb, 0xbf }.Concat(ResultBody("null")).ToArray());
        bodies.Add(new byte[] { 0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf }.Concat(ResultBody("null")).ToArray());
        foreach (var invalid in new byte[][]
        {
            [0xff], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82]
        })
        {
            var prefix = Encoding.UTF8.GetBytes("""{"version":1,"session":"s","id":1,"kind":"result","value":""" + "\"");
            bodies.Add(prefix.Concat(invalid).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray());
        }
        bodies.Add([]);
        var exact = ResultBody("\"" + new string('x', BridgeCodec.MaximumFrameBytes - ResultBody("\"\"").Length) + "\"");
        bodies.Add(exact);
        bodies.Add(exact.Concat(new byte[] { 0x20 }).ToArray());
        var module = JsonSerializer.Serialize(new Uri(Path.Combine(RepositoryRoot(), "packages/runtime-bridge/src/index.js")).AbsoluteUri);
        using var node = StartNode($$"""
            import { FrameDecoder } from {{module}};
            let input = ''; for await (const chunk of process.stdin) input += chunk;
            const results = JSON.parse(input).map(encoded => {
              const body = Buffer.from(encoded, 'base64'), header = Buffer.alloc(4);
              header.writeUInt32BE(body.length);
              try {
                let count = 0; const decoder = new FrameDecoder();
                decoder.push(Buffer.concat([header, body]), () => count++);
                decoder.finish(); return count === 1 ? 'OK' : 'MISSING';
              } catch (error) { return error.code ?? 'UNCLASSIFIED'; }
            });
            process.stdout.write(JSON.stringify(results));
            """);
        var output = node.StandardOutput.ReadToEndAsync();
        var error = node.StandardError.ReadToEndAsync();
        try
        {
            await node.StandardInput.WriteAsync(JsonSerializer.Serialize(bodies.Select(Convert.ToBase64String).ToArray()));
            node.StandardInput.Close();
            await node.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Equal(0, node.ExitCode); Equal("", await error);
            var actual = JsonSerializer.Deserialize<string[]>(await output)!;
            Equal(bodies.Count, actual.Length);
            for (var i = 0; i < bodies.Count; i++)
            {
                string expected;
                try { _ = BridgeCodec.Decode(bodies[i]); expected = "OK"; }
                catch (BridgeException failure) { expected = failure.Code; }
                if (expected != actual[i]) throw new InvalidOperationException($"JS/native codec mismatch at corpus index {i}");
            }
        }
        finally { await StopNode(node); }
    }

    private static async Task JsPipe()
    {
        var name = "meowfilm-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var module = JsonSerializer.Serialize(new Uri(Path.Combine(RepositoryRoot(), "packages/runtime-bridge/src/descriptor.js")).AbsoluteUri);
        var protocol = JsonSerializer.Serialize(new Uri(Path.Combine(RepositoryRoot(), "packages/runtime-bridge/src/index.js")).AbsoluteUri);
        var path = JsonSerializer.Serialize(@"\\.\pipe\" + name);
        using var node = StartNode($$$"""
            import { connect } from 'node:net';
            import { attachConnectedStream } from {{{module}}};
            import { BridgeError } from {{{protocol}}};
            const socket = connect({ path: {{{path}}} });
            let peer, controller, cancelled = 0;
            const deadline = setTimeout(() => { peer?.close(); socket.destroy(); process.exitCode = 2; }, 30000);
            socket.on('error', () => { process.exitCode = 1; });
            socket.on('close', () => clearTimeout(deadline));
            socket.once('connect', () => {
              peer = attachConnectedStream(socket, { session: 'js-audit', maximumPending: 128, methods: {
                'js.echo': value => value,
                'js.reverseMany': count => Promise.all(Array.from({length:count}, (_, i) => peer.call('native.echo', i))),
                'js.wait': (_, {signal}) => new Promise((resolve, reject) => {
                  signal.addEventListener('abort', () => { cancelled++; reject(new BridgeError('CANCELLED')); }, {once:true});
                  peer.call('native.waiting').catch(() => {});
                }),
                'js.cancelled': () => cancelled,
                'js.beginCancel': () => {
                  controller = new AbortController();
                  return peer.call('native.wait', null, {signal:controller.signal}).catch(error => error.code);
                },
                'js.abort': () => { controller.abort(); return null; }
              }});
              peer.call('native.ready').catch(() => { process.exitCode = 1; peer.close(); });
            });
            """);
        node.StandardInput.Close();
        var output = node.StandardOutput.ReadToEndAsync(); var error = node.StandardError.ReadToEndAsync();
        var ready = Signal(); var waiting = Signal(); var nativeStarted = Signal(); var nativeCancelled = Signal();
        try
        {
            using var connect = new CancellationTokenSource(TestDeadline);
            await pipe.WaitForConnectionAsync(connect.Token);
            await using var peer = new BridgePeer(pipe, "js-audit",
                new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(Echo)
                {
                    ["native.ready"] = (_, _) => { ready.TrySetResult(); return Task.FromResult<object?>(null); },
                    ["native.waiting"] = (_, _) => { waiting.TrySetResult(); return Task.FromResult<object?>(null); },
                    ["native.wait"] = async (_, token) =>
                    {
                        nativeStarted.TrySetResult();
                        try { await Task.Delay(Timeout.Infinite, token); return null; }
                        finally { nativeCancelled.TrySetResult(); }
                    }
                });
            await ready.Task.WaitAsync(TestDeadline);
            Equal("音乐🎵é e\u0301", (await peer.CallAsync("js.echo", "音乐🎵é e\u0301")).GetString());
            for (var round = 0; round < 8; round++)
            {
                var start = Signal();
                var calls = Enumerable.Range(0, 96).Select(i => Task.Run(async () =>
                {
                    await start.Task;
                    return (await peer.CallAsync("js.echo", i)).GetInt32();
                })).ToArray();
                start.TrySetResult();
                Equal(96, (await Task.WhenAll(calls).WaitAsync(TestDeadline)).Distinct().Count());
            }
            var reverse = await peer.CallAsync("js.reverseMany", 64);
            Equal(2016, reverse.EnumerateArray().Sum(value => value.GetInt32()));
            using var cancellation = new CancellationTokenSource();
            var cancelled = peer.CallAsync("js.wait", cancellationToken: cancellation.Token);
            await waiting.Task.WaitAsync(TestDeadline);
            cancellation.Cancel(); await Expect("CANCELLED", () => cancelled);
            Equal(1, (await peer.CallAsync("js.cancelled")).GetInt32());
            var reverseCancel = peer.CallAsync("js.beginCancel");
            await nativeStarted.Task.WaitAsync(TestDeadline);
            await peer.CallAsync("js.abort");
            Equal("CANCELLED", (await reverseCancel.WaitAsync(TestDeadline)).GetString());
            await nativeCancelled.Task.WaitAsync(TestDeadline);
            await Eventually(() => peer.ActiveCount == 0 && peer.PendingCount == 0);
            peer.Close(); await peer.Completion.WaitAsync(TestDeadline);
            await node.WaitForExitAsync().WaitAsync(TestDeadline);
            Equal(0, node.ExitCode); Equal("", await output); Equal("", await error);
        }
        finally { await StopNode(node); }
    }

    private sealed class TestStream(Stream inner, bool holdWrites = false, bool fragmentReads = false, bool throwOnDispose = false) : Stream
    {
        private readonly TaskCompletionSource release = Signal();
        public readonly TaskCompletionSource Writing = Signal();
        private int writeCount;
        private int disposeCount;
        public int WriteCount => Volatile.Read(ref writeCount);
        public int DisposeCount => Volatile.Read(ref disposeCount);
        public void ReleaseWrites() => release.TrySetResult();
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, fragmentReads ? Math.Min(1, count) : count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            inner.ReadAsync(fragmentReads ? buffer[..Math.Min(1, buffer.Length)] : buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Interlocked.Increment(ref writeCount);
            Writing.TrySetResult();
            if (holdWrites) await release.Task.WaitAsync(token);
            await inner.WriteAsync(buffer, token);
        }
        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref disposeCount);
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
            if (throwOnDispose) throw new IOException("private dispose error");
        }
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

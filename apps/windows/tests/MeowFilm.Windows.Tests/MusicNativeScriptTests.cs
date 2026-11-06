using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>The native adapter of the shared music module. Management semantics, persistence and the
/// musicUrl result contract are covered by tests/windows-music-script.test.mjs against real QuickJS.</summary>
public static class MusicNativeScriptTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native LX: management commands forward to the shared module and stage selected files", Forwarding);
        yield return new("Native LX: a closed runtime is an unavailable script service, not missing sources", Unavailable);
        yield return new("Native LX: resolve asks one exact quality of one recording and proves refreshes", ExactResolve);
        yield return new("Native LX: lyric parsing remains native and respects persisted setting", Lyrics);
        yield return new("Native LX: shutdown cancels owned calls and never disposes borrowed owners", Shutdown);
        yield return new("Native LX: bounded snapshot rejects altered chunks and content hash", Transfer);
        yield return new("Native LX: abandoned transfers actively expire and stale callbacks cannot retire renewed stages", TransferExpiry);
        yield return new("Native LX: lookup after an overdue sweep reschedules the remaining transfer", TransferSweepReschedule);
        yield return new("Native LX: transfer shutdown reports one timer disposal failure after releasing transfers", TransferTimerCleanup);
        yield return new("Native LX: disposal preserves the first shutdown failure while releasing transfers", DisposeFirstFailure);
        yield return new("Native LX transport: direct boundary preserves parameters and original values", TransportSmall);
        yield return new("Native LX transport: complete JSON crosses 512 KiB one MiB and eight MiB", TransportLarge);
        yield return new("Native LX transport: corrupt blob cannot publish and always releases", TransportCorruption);
        yield return new("Native LX transport: cancelled timed out and failed calls reclaim owned tokens", TransportCleanup);
    }
    private static async Task TransportSmall()
    {
        foreach (int length in new[] { 2, 65536, 65537 })
        {
            // A serialized string contributes two quote bytes.
            object parameters = new string('x', length - 2);
            var runtime = new TransportRuntime();
            var transport = new MusicScriptTransport(runtime);
            var result = await transport.CallAsync("invoke", parameters, default);
            Check(result.GetProperty("value").GetProperty("blob").GetString() == "business field");
            Check(runtime.BusinessCalls == 1 && runtime.Uploads.Count == 0 && runtime.Blobs.Count == 0);
            if (length <= 65536)
                Check(runtime.Calls.SequenceEqual(["music.script.invoke"]) && ReferenceEquals(parameters, runtime.DirectParameters));
            else Check(runtime.Calls.Contains("music.script.control.call") && runtime.Aborts == 1);
        }
        foreach (string operation in new[] { "stage.chunk", "control.chunk", "blob.read" })
        {
            object parameters = new { data = new string('x', 100_000) };
            string? called = null;
            var runtime = new CallbackRuntime((method, input, _) =>
            {
                called = method; Check(ReferenceEquals(parameters, input));
                return Task.FromResult(J(new { ok = true, contract = "music.script.v3" }));
            });
            await new MusicScriptTransport(runtime).CallAsync(operation, parameters, default);
            Check(called == "music.script." + operation);
        }
        foreach (object? value in new object?[] { null, false, 7, "raw", new object?[] { null, false, 0 },
            new { blob = new { token = "business data", length = 900000 } } })
        {
            var expected = J(new { ok = true, contract = "music.script.v3", value });
            var runtime = new CallbackRuntime((_, _, _) => Task.FromResult(expected));
            Check((await new MusicScriptTransport(runtime).CallAsync("invoke", new { }, default)).GetRawText() == expected.GetRawText());
        }
        foreach (string invalid in new[] { """{"a":1,"\u0061":2}""", """{"音乐":1}""",
            """{"value":9007199254740992}""", """{"value":"\ud800"}""" })
        {
            using var document = JsonDocument.Parse(invalid);
            var runtime = new TransportRuntime();
            await TransportError("INVALID_MESSAGE", () => new MusicScriptTransport(runtime).CallAsync("invoke", document.RootElement, default));
            Check(runtime.Calls.Count == 0, "invalid logical input must not reach business");
        }
    }

    private static async Task TransportLarge()
    {
        // Exact full request and response JSON sizes, including JSON overhead.
        const int exact = 512 * 1024;
        var boundary = new TransportRuntime();
        string exactRequest = new('x', exact - 2);
        boundary.Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v3",
            value = new string('x', exact - Encoding.UTF8.GetByteCount("""{"ok":true,"contract":"music.script.v3","value":""}""")) });
        Check(boundary.Response.Length == exact);
        var exactResult = await new MusicScriptTransport(boundary).CallAsync("invoke", exactRequest, default);
        Check(BridgeCodec.EncodeJSONValue(boundary.Received!.Value).Length == exact &&
            BridgeCodec.EncodeJSONValue(exactResult).Length == exact && boundary.Releases == 1 && boundary.Aborts == 1);
        foreach (int size in new[] { 512 * 1024, 512 * 1024 + 1, 1024 * 1024 + 1, 8 * 1024 * 1024 + 1 })
        {
            var runtime = new TransportRuntime();
            // Multibyte text and escapes straddle byte chunk boundaries.
            string text = string.Concat(Enumerable.Repeat("音乐🎵\"\\", size / 8 + 1));
            var value = new { text, rows = new object?[] { null, false, 0, 1.5, new { blob = "raw" } } };
            runtime.Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v3", value });
            var result = await new MusicScriptTransport(runtime).CallAsync("invoke", value, default);
            Check(JsonNode.DeepEquals(JsonNode.Parse(result.GetProperty("value").GetRawText()), JsonSerializer.SerializeToNode(value)));
            Check(runtime.Received is { } received &&
                JsonNode.DeepEquals(JsonNode.Parse(received.GetRawText()), JsonSerializer.SerializeToNode(value)));
            Check(runtime.BusinessCalls == 1 && runtime.Aborts == 1 && runtime.Releases == 1 &&
                runtime.Uploads.Count == 0 && runtime.Blobs.Count == 0);
            Check(runtime.MaximumFrame <= BridgeCodec.MaximumFrameBytes);
        }
    }

    private static async Task TransportCorruption()
    {
        foreach (string fault in new[]
        {
            "next", "short", "long", "empty", "base64", "noncanonical", "hash", "length-short", "length-long",
            "chunk-size", "hash-case", "utf8", "json", "duplicate", "unicode-key", "unsafe-number", "surrogate",
            "depth", "envelope", "contract", "recursive"
        })
        {
            var runtime = new TransportRuntime { Fault = fault,
                Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v3", value = new string('x', 70_001) }) };
            runtime.Response = fault switch
            {
                "utf8" => [0xff],
                "json" => Encoding.UTF8.GetBytes("{"),
                "duplicate" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v3","value":{"a":1,"\u0061":2}}"""),
                "unicode-key" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v3","value":{"音乐":1}}"""),
                "unsafe-number" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v3","value":9007199254740992}"""),
                "surrogate" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v3","value":"\ud800"}"""),
                "depth" => Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65)),
                "envelope" => Encoding.UTF8.GetBytes("""{"value":false}"""),
                "contract" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"wrong","value":false}"""),
                "recursive" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v3","blob":{}}"""),
                _ => runtime.Response
            };
            await TransportError("INVALID_RESPONSE", () => new MusicScriptTransport(runtime).CallAsync("invoke", new { }, default));
            Check(runtime.Releases == 1 && runtime.Blobs.Count == 0, fault);
            runtime.Fault = "";
            runtime.Response = null;
            Check((await new MusicScriptTransport(runtime).CallAsync("sources.list", new { }, default)).GetProperty("ok").GetBoolean());
        }
    }

    private static async Task TransportCleanup()
    {
        foreach (string at in new[] { "control.chunk", "control.call", "blob.read" })
            foreach (string failure in new[] { "CANCELLED", "TIMEOUT" })
            {
                using var cancelled = new CancellationTokenSource();
                int visits = 0;
                var runtime = new TransportRuntime { Response = BridgeCodec.EncodeJSONValue(
                    new { ok = true, contract = "music.script.v3", value = new string('x', 80_000) }) };
                runtime.Before = (method, token) =>
                {
                    if (method != "music.script." + at) return;
                    if (++visits < (at == "control.call" ? 1 : 2)) return;
                    if (failure == "TIMEOUT") throw new BridgeException("TIMEOUT");
                    cancelled.Cancel(); token.ThrowIfCancellationRequested();
                };
                await TransportError(failure, () => new MusicScriptTransport(runtime).CallAsync(
                    "invoke", new { text = new string('x', 80_000) }, cancelled.Token));
                Check(runtime.Aborts == 1 && runtime.Uploads.Count == 0);
                Check(runtime.Releases == (at == "blob.read" ? 1 : 0) && runtime.Blobs.Count == 0);
                Check(runtime.BusinessCalls == (at == "blob.read" ? 1 : 0), "uncertain business is never retried");
                Check(runtime.CleanupTokens.All(token => token.CanBeCanceled && !token.IsCancellationRequested && token != cancelled.Token));
            }
        foreach (string fault in new[] { "begin", "upload-next", "business" })
        {
            var runtime = new TransportRuntime { Fault = fault };
            if (fault == "business")
            {
                var result = await new MusicScriptTransport(runtime).CallAsync("invoke", new string('x', 80_000), default);
                Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "SCRIPT_EXCEPTION");
            }
            else await TransportError("INVALID_RESPONSE", () =>
                new MusicScriptTransport(runtime).CallAsync("invoke", new string('x', 80_000), default));
            Check(runtime.Aborts == 1 && runtime.Uploads.Count == 0);
            Check(runtime.BusinessCalls == (fault == "business" ? 1 : 0));
        }
        // A receipt received concurrently with cancellation still belongs to this call.
        foreach (bool duringBegin in new[] { true, false })
        {
            using var cancelled = new CancellationTokenSource();
            var runtime = new TransportRuntime { AfterReceipt = cancelled.Cancel, CancelBegin = duringBegin,
                Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v3", value = false }) };
            await TransportError("CANCELLED", () => new MusicScriptTransport(runtime).CallAsync(
                "invoke", new string('x', 80_000), cancelled.Token));
            Check(runtime.Aborts == 1 && runtime.Uploads.Count == 0 && runtime.Blobs.Count == 0);
            Check(runtime.Releases == (duringBegin ? 0 : 1));
        }
        // An unknown begin receipt cannot be aborted locally and is never retried.
        var unknown = new TransportRuntime();
        unknown.Before = (method, _) => { if (method == "music.script.control.begin") throw new BridgeException("TIMEOUT"); };
        await TransportError("TIMEOUT", () => new MusicScriptTransport(unknown).CallAsync("invoke", new string('x', 80_000), default));
        Check(unknown.Aborts == 0 && unknown.BusinessCalls == 0 && unknown.Calls.Count == 1);
        // Cleanup failure must not replace the original operation failure.
        var brokenCleanup = new TransportRuntime { Fault = "upload-next" };
        brokenCleanup.Before = (method, _) => { if (method == "music.script.control.abort") throw new BridgeException("CLOSED"); };
        await TransportError("INVALID_RESPONSE", () => new MusicScriptTransport(brokenCleanup).CallAsync("invoke", new string('x', 80_000), default));
        Check(brokenCleanup.Calls.Count(method => method == "music.script.control.abort") == 1);
        foreach (var buffer in brokenCleanup.Uploads.Values) buffer.Dispose();
        var brokenRelease = new TransportRuntime { Fault = "next",
            Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v3", value = false }) };
        brokenRelease.Before = (method, _) => { if (method == "music.script.blob.release") throw new BridgeException("CLOSED"); };
        await TransportError("INVALID_RESPONSE", () => new MusicScriptTransport(brokenRelease).CallAsync("invoke", new { }, default));
        Check(brokenRelease.Calls.Count(method => method == "music.script.blob.release") == 1);
        foreach (string cleanup in new[] { "control.abort", "blob.release" })
        {
            using var cancelled = new CancellationTokenSource();
            var runtime = new TransportRuntime { Response = BridgeCodec.EncodeJSONValue(
                new { ok = true, contract = "music.script.v3", value = false }) };
            runtime.Before = (method, _) => { if (method == "music.script." + cleanup) cancelled.Cancel(); };
            await TransportError("CANCELLED", () => new MusicScriptTransport(runtime).CallAsync("invoke", new string('x', 80_000), cancelled.Token));
            Check(runtime.Aborts == 1 && runtime.Releases == 1 && runtime.Uploads.Count == 0 && runtime.Blobs.Count == 0);
        }
    }

    private static async Task TransportError(string code, Func<Task<JsonElement>> call)
    {
        try { await call(); }
        catch (MusicLibraryException error) when (error.Code == code) { return; }
        catch (BridgeException error) when (error.Code == code) { return; }
        catch (OperationCanceledException) when (code == "CANCELLED") { return; }
        throw new InvalidOperationException("Expected transport failure: " + code);
    }

    private sealed class CallbackRuntime(Func<string, object?, CancellationToken, Task<JsonElement>> call) : IRuntimeClient
    {
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
            call(method, parameters, cancellationToken);
    }

    private sealed class TransportRuntime : IRuntimeClient
    {
        internal readonly List<string> Calls = [];
        internal readonly Dictionary<string, MemoryStream> Uploads = [];
        internal readonly Dictionary<string, byte[]> Blobs = [];
        internal readonly List<CancellationToken> CleanupTokens = [];
        internal object? DirectParameters;
        internal JsonElement? Received;
        internal byte[]? Response;
        internal string Fault = "";
        internal int BusinessCalls, Aborts, Releases, MaximumFrame;
        internal Action<string, CancellationToken>? Before;
        internal Action? AfterReceipt;
        internal bool CancelBegin;
        private int length;
        private string hash = "";

        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(method);
            var p = BridgeCodec.Value(parameters);
            Frame(new { version = 1, session = "transport-test", id = 1, kind = "request", method, @params = p });
            Before?.Invoke(method, cancellationToken);
            JsonElement result;
            switch (method)
            {
                case "music.script.control.begin":
                    length = p.GetProperty("length").GetInt32(); hash = p.GetProperty("sha256").GetString()!;
                    Uploads.Add("upload", new());
                    result = Reply(new { token = "upload", chunkBytes = Fault == "begin" ? 1 : 65536 });
                    if (CancelBegin) AfterReceipt?.Invoke();
                    break;
                case "music.script.control.chunk":
                    var buffer = Uploads[p.GetProperty("token").GetString()!];
                    Check(p.GetProperty("offset").GetInt64() == buffer.Length);
                    string data = p.GetProperty("data").GetString()!;
                    byte[] bytes = Convert.FromBase64String(data);
                    Check(Convert.ToBase64String(bytes) == data && bytes.Length is > 0 and <= 65536 && buffer.Length + bytes.Length <= length);
                    buffer.Write(bytes);
                    result = Reply(new { next = buffer.Length + (Fault == "upload-next" ? 1 : 0) });
                    break;
                case "music.script.control.call":
                    string upload = p.GetProperty("token").GetString()!;
                    var complete = Uploads[upload].ToArray();
                    Check(complete.Length == length && Convert.ToHexStringLower(SHA256.HashData(complete)) == hash);
                    Check(p.GetProperty("operation").GetString() is "sources.list" or "resolve" or "invoke");
                    Received = BridgeCodec.DecodeJSONValue(complete); Uploads[upload].Dispose(); Uploads.Remove(upload);
                    result = Business();
                    break;
                case "music.script.control.abort":
                    CleanupTokens.Add(cancellationToken); Aborts++;
                    if (Uploads.Remove(p.GetProperty("token").GetString()!, out var pending)) pending.Dispose();
                    result = Reply(new { released = true });
                    break;
                case "music.script.blob.read":
                    byte[] body = Blobs[p.GetProperty("token").GetString()!];
                    int offset = p.GetProperty("offset").GetInt32();
                    int count = Math.Min(65536, body.Length - offset);
                    string encoded = Convert.ToBase64String(body, offset, count);
                    if (Fault == "short") encoded = Convert.ToBase64String(body, offset, count - 1);
                    if (Fault == "long") encoded = Convert.ToBase64String(new byte[65537]);
                    if (Fault == "empty") encoded = "";
                    if (Fault == "base64") encoded = "%%%=";
                    if (Fault == "noncanonical") encoded += "\n";
                    result = Reply(new { data = encoded, next = offset + count + (Fault == "next" ? 1 : 0) });
                    break;
                case "music.script.blob.release":
                    CleanupTokens.Add(cancellationToken); Releases++; Check(Blobs.Remove(p.GetProperty("token").GetString()!));
                    result = Reply(new { released = true });
                    break;
                default:
                    Check(method is "music.script.invoke" or "music.script.sources.list" or "music.script.resolve");
                    DirectParameters = parameters; Received = p; result = Business();
                    break;
            }
            Frame(new { version = 1, session = "transport-test", id = 1, kind = "result", value = result });
            return Task.FromResult(result);
        }
        private JsonElement Business()
        {
            BusinessCalls++;
            if (Fault == "business") return J(new { ok = false, contract = "music.script.v3", code = "SCRIPT_EXCEPTION" });
            if (Response is not { } bytes) return Reply(new { value = new { blob = "business field", flags = new object?[] { null, false, 0 } } });
            Blobs.Add("blob", bytes);
            var result = Reply(new { blob = new { token = "blob",
                length = bytes.Length + (Fault == "length-short" ? -1 : Fault == "length-long" ? 1 : 0),
                sha256 = Fault == "hash" ? new string('0', 64) : Fault == "hash-case"
                    ? new string('A', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)),
                chunkBytes = Fault == "chunk-size" ? 1 : 65536 } });
            if (!CancelBegin) AfterReceipt?.Invoke();
            return result;
        }
        private void Frame(object value) => MaximumFrame = Math.Max(MaximumFrame, BridgeCodec.Encode(value).Length - 4);
        private static JsonElement Reply(object value)
        {
            var result = O(value); result["ok"] = true; result["contract"] = "music.script.v3"; return J(result);
        }
    }
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonObject O(object value) => JsonNode.Parse(JsonSerializer.Serialize(value))!.AsObject();
    private static void Check(bool value, string code = "assertion") { if (!value) throw new InvalidOperationException(code); }
    private const string Installation = "5f0c1b9e-2a6d-4e43-8a1f-0b7c2d9e4f31";
    private static async Task<JsonElement> Ok(MusicScriptOwner owner, string method, object? p = null)
    {
        var result = await owner.ExecuteAsync(method, p);
        Check(result.GetProperty("ok").GetBoolean(), method + ":" + result.GetRawText()); return result;
    }
    private static async Task Error(MusicScriptOwner owner, string method, object? p, string expected)
    {
        var result = await owner.ExecuteAsync(method, p);
        Check(result.GetProperty("ok").ValueKind == JsonValueKind.False &&
            result.GetProperty("code").GetString() == expected, expected + ":" + result.GetRawText());
    }
    private static JsonObject Song(string id = "11") => MusicSongFixture.Online(
        "wy", id, "Test track", "Test artist", "", 120,
        native: JsonSerializer.SerializeToNode(new { name = "Test track", singer = "Test artist", duration = 120 })!.AsObject());
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store = new();
        internal readonly Runtime Runtime = new();
        internal readonly MusicLibraryOwner Library;
        internal readonly MusicScriptOwner Owner;
        internal Fixture(TimeProvider? clock = null) { Library = new(Store); Owner = new(Library, Runtime, clock); }
        public async ValueTask DisposeAsync() { await Owner.DisposeAsync(); await Library.DisposeAsync(); }
    }
    private static async Task<string> Upload(Fixture f, string source = "// synthetic LX", string basename = "source.js")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        var stage = await Ok(f.Owner, "music.transfer.begin", new { kind = "script", length = bytes.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), basename });
        string token = stage.GetProperty("token").GetString()!;
        await Ok(f.Owner, "music.transfer.chunk", new { token, offset = 0, data = Convert.ToBase64String(bytes) });
        return token;
    }
    private static async Task Forwarding()
    {
        await using var f = new Fixture();
        f.Runtime.Business = (method, p) => method switch
        {
            "music.script.sources.list" => new { revision = 3, items = Array.Empty<object>() },
            "music.script.sources.remove" => new { removed = true },
            _ => new { item = new { installationId = Installation, received = p } }
        };
        var listed = await Ok(f.Owner, "music.sources.list");
        Check(listed.GetProperty("revision").GetInt32() == 3 && f.Store.Writes == 0, "listing is a module read");
        string token = await Upload(f, "// @name 猫\nlx.send('inited', {})", "猫.js");
        await Ok(f.Owner, "music.sources.add", new { token });
        var added = f.Runtime.Requests.Last(request => request.Method == "music.script.sources.add").Parameters;
        string staged = added["token"]!.GetValue<string>();
        Check(staged != token && f.Runtime.Staged[staged] == "// @name 猫\nlx.send('inited', {})" &&
            f.Runtime.StageNames[staged] == "猫.js", "the selected file crosses once as a module stage");
        Check(f.Runtime.Requests.Any(request => request.Method == "music.script.stage.abort" &&
            request.Parameters["token"]!.GetValue<string>() == staged), "the module stage is always released");
        await Error(f.Owner, "music.sources.add", new { token }, "TRANSFER_NOT_FOUND");
        foreach (var (command, parameters) in new (string, object)[]
        {
            ("detail", new { installationId = Installation }), ("enable", new { installationId = Installation }),
            ("disable", new { installationId = Installation }), ("rollback", new { installationId = Installation }),
            ("check", new { installationId = Installation }), ("remove", new { installationId = Installation }),
            ("edit", new { installationId = Installation, name = "改名" }),
            ("download", new { url = "https://host.invalid/source.js" }), ("update", new { installationId = Installation })
        })
        {
            await Ok(f.Owner, "music.sources." + command, parameters);
            var forwarded = f.Runtime.Requests.Last();
            Check(forwarded.Method == "music.script.sources." + command &&
                JsonNode.DeepEquals(forwarded.Parameters, O(parameters)), command);
        }
        string update = await Upload(f, "// next");
        await Ok(f.Owner, "music.sources.update", new { installationId = Installation, token = update });
        Check(f.Runtime.Requests.Last(request => request.Method == "music.script.sources.update").Parameters["token"]!
            .GetValue<string>().StartsWith("stage-", StringComparison.Ordinal));
        await Error(f.Owner, "music.sources.detail", new { installationId = Installation, extra = 1 }, "INVALID_REQUEST");
    }
    private static async Task Unavailable()
    {
        var f = new Fixture();
        f.Runtime.Closed = true;
        await Error(f.Owner, "music.sources.list", null, "SCRIPT_RUNTIME_UNAVAILABLE");
        await Error(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "wy", song = Song(),
            quality = "320k" }, "SCRIPT_RUNTIME_UNAVAILABLE");
        await f.DisposeAsync();
        await Error(f.Owner, "music.sources.list", null, "CLOSED");
        Check(!f.Store.Disposed, "the App owns the store");
        await using var runtime = new AppRuntimeClient(_ => throw new InvalidOperationException("synthetic startup failure"));
        await using var library = new MusicLibraryOwner(new Store());
        await using var owner = new MusicScriptOwner(library, runtime);
        runtime.Start();
        await Error(owner, "music.sources.list", null, "SCRIPT_RUNTIME_UNAVAILABLE");
        await Error(owner, "music.online.resolve", new { installationId = Installation, platform = "wy", song = Song(),
            quality = "320k" }, "SCRIPT_RUNTIME_UNAVAILABLE");
    }
    private static async Task ExactResolve()
    {
        await using var f = new Fixture();
        f.Runtime.Business = (method, p) =>
        {
            Check(method == "music.script.resolve");
            return new { installationId = p["installationId"]!.GetValue<string>(), generation = 4,
                contentHash = new string('a', 64), platform = p["platform"]!.GetValue<string>(),
                quality = p["quality"]!.GetValue<string>(), url = "https://media.invalid/a.flac",
                headers = new { Referer = "https://ref.invalid" }, expiresAt = (long?)null };
        };
        var media = await Ok(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "wy",
            song = Song(), quality = "320k" });
        var request = f.Runtime.Requests.Single(entry => entry.Method == "music.script.resolve").Parameters;
        Check(request["quality"]!.GetValue<string>() == "320k" && request["platform"]!.GetValue<string>() == "wy" &&
            request["musicInfo"]!["songmid"]!.GetValue<string>() == "11" && request["generation"] is null,
            "one exact request: no generation, no quality list");
        Check(media.GetProperty("url").GetString() == "https://media.invalid/a.flac" &&
            media.GetProperty("generation").GetInt64() == 4 && media.GetProperty("quality").GetString() == "320k" &&
            media.GetProperty("headers").GetProperty("Referer").GetString() == "https://ref.invalid" &&
            !media.GetProperty("qualityVerified").GetBoolean() && !media.TryGetProperty("recordingSong", out _));
        string policy = media.GetProperty("qualityPolicySignature").GetString()!;
        string cache = media.GetProperty("qualityCacheSignature").GetString()!;
        await Ok(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "wy", song = Song(),
            quality = "320k", qualityPolicySignature = policy, qualityCacheSignature = cache });
        await Error(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "wy", song = Song(),
            quality = "320k", qualityPolicySignature = new string('0', 64) }, "QUALITY_POLICY_CHANGED");
        await Error(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "wy", song = Song(),
            quality = "320k", qualityPolicySignature = policy, qualityCacheSignature = new string('0', 64) }, "QUALITY_CACHE_CHANGED");
        int calls = f.Runtime.Requests.Count(entry => entry.Method == "music.script.resolve");
        await Error(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "wy", song = Song(),
            quality = "auto" }, "QUALITY_UNAVAILABLE");
        await Error(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "kw", song = Song(),
            quality = "320k" }, "RECORDING_UNAVAILABLE");
        await Error(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "wy", song = Song(),
            quality = "320k", generation = 4 }, "INVALID_REQUEST");
        Check(f.Runtime.Requests.Count(entry => entry.Method == "music.script.resolve") == calls,
            "rejected requests never reach a script");
        f.Runtime.Business = (_, p) => new { installationId = Installation, generation = 4, contentHash = new string('a', 64),
            platform = "wy", quality = "128k", url = "https://media.invalid/b.mp3", headers = new { }, expiresAt = (long?)null };
        await Error(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "wy", song = Song(),
            quality = "320k" }, "TARGET_CHANGED");
    }
    private static async Task Lyrics()
    {
        await using var f = new Fixture();
        f.Runtime.Business = (method, p) =>
        {
            Check(method == "music.script.invoke" && p["operation"]!.GetValue<string>() == "lyric" &&
                p["siteId"]!.GetValue<string>() == "wy");
            return new { installationId = Installation, generation = 2, contentHash = new string('a', 64), siteId = "wy",
                value = new { lyric = "[00:01.00]Hello", tlyric = "[00:01.00]你好" } };
        };
        var response = await Ok(f.Owner, "music.online.lyrics", new { installationId = Installation, platform = "wy", song = Song() });
        Check(response.GetProperty("state").GetString() == "ready" &&
            response.GetProperty("lines")[0].GetProperty("translation").GetString() == "你好");
        var settings = await f.Library.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false });
        Check(settings.GetProperty("ok").GetBoolean());
        int calls = f.Runtime.Requests.Count;
        response = await Ok(f.Owner, "music.online.lyrics", new { installationId = Installation, platform = "wy", song = Song() });
        Check(response.GetProperty("state").GetString() == "disabled" && calls == f.Runtime.Requests.Count);
    }
    private static async Task Shutdown()
    {
        var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.Entered = entered;
        var pending = f.Owner.ExecuteAsync("music.sources.list");
        await entered.Task;
        await f.Owner.PrepareShutdownAsync();
        var result = await pending;
        Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "CANCELLED",
            "preparation cancels admitted calls before it completes");
        await Error(f.Owner, "music.sources.list", null, "CLOSED");
        await f.Owner.CancelShutdownPreparationAsync();
        f.Runtime.Entered = null;
        f.Runtime.Business = (_, _) => new { revision = 0, items = Array.Empty<object>() };
        await Ok(f.Owner, "music.sources.list");
        await f.DisposeAsync();
        Check(!f.Store.Disposed, "store and runtime remain App-owned");
    }
    private static async Task Transfer()
    {
        await using var f = new Fixture();
        await Error(f.Owner, "music.transfer.begin", new { kind = "script", length = 1, sha256 = new string('0', 64), basename = "../x.js" }, "INVALID_REQUEST");
        var active = new List<string>();
        for (int index = 0; index < 12; index++)
        {
            var pending = await Ok(f.Owner, "music.transfer.begin", new
            {
                kind = "script", length = index + 1, sha256 = new string('0', 64), basename = $"pending-{index}.js"
            });
            active.Add(pending.GetProperty("token").GetString()!);
        }
        Check(active.Distinct(StringComparer.Ordinal).Count() == active.Count);
        foreach (string pending in active)
            await Ok(f.Owner, "music.transfer.cancel", new { token = pending });
        await Error(f.Owner, "music.transfer.begin", new { kind = "script",
            length = SelectedScriptSnapshot.MaximumBytes + 1, sha256 = new string('0', 64), basename = "oversize.js" }, "LIMIT");
        var stage = await Ok(f.Owner, "music.transfer.begin", new { kind = "script", length = 1, sha256 = new string('0', 64), basename = "x.js" });
        string token = stage.GetProperty("token").GetString()!;
        await Error(f.Owner, "music.transfer.chunk", new { token, offset = 1, data = "YQ==" }, "TRANSFER_OFFSET");
        await Ok(f.Owner, "music.transfer.chunk", new { token, offset = 0, data = "YQ==" });
        await Error(f.Owner, "music.sources.add", new { token }, "TRANSFER_INTEGRITY");
        await Error(f.Owner, "music.sources.add", new { token }, "TRANSFER_NOT_FOUND");
        Check(f.Runtime.Requests.Count == 0 && f.Store.Writes == 0, "a rejected snapshot never reaches the module");
    }
    private static async Task DisposeFirstFailure()
    {
        var clock = new TransferClock();
        var f = new Fixture(clock);
        _ = await Ok(f.Owner, "music.transfer.begin", new
        {
            kind = "script", length = 1, sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("x"))),
            basename = "pending.js"
        });
        clock.DisposeFailures = 1;
        try
        {
            await f.Owner.DisposeAsync();
            Check(false, "expected shutdown failure");
        }
        catch (InvalidOperationException error) { Check(error.Message == "synthetic timer disposal failure", error.Message); }
        Check(clock.DisposeAttempts == 1, "timer disposal was retried");
        await Error(f.Owner, "music.sources.list", null, "CLOSED");
        await f.Library.DisposeAsync();
    }
    private static Task TransferExpiry()
    {
        var clock = new TransferClock();
        using var transfers = new MusicNativeTransfers(clock);
        byte[] source = Encoding.UTF8.GetBytes("ab");
        var first = transfers.Begin(O(new
        {
            kind = "script",
            length = source.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(source)),
            basename = "first.js"
        }));
        string firstToken = first["token"]!.GetValue<string>();
        var stale = clock.Last!;
        clock.Advance(TimeSpan.FromMinutes(1));
        transfers.Chunk(O(new { token = firstToken, offset = 0, data = "YQ==" }));
        stale.FireStale();
        transfers.Chunk(O(new { token = firstToken, offset = 1, data = "Yg==" }));
        var abandoned = transfers.Begin(O(new
        {
            kind = "script",
            length = 1,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("x"))),
            basename = "abandoned.js"
        }));
        string abandonedToken = abandoned["token"]!.GetValue<string>();
        clock.Advance(TimeSpan.FromMinutes(5));
        Check(clock.Fired > 0);
        foreach (string token in new[] { firstToken, abandonedToken })
        {
            try
            {
                transfers.Chunk(O(new { token, offset = 0, data = "eA==" }));
                Check(false, "expired transfer survived active timer");
            }
            catch (MusicLibraryException error) { Check(error.Code == "TRANSFER_NOT_FOUND", error.Code); }
        }
        CryptographicOperations.ZeroMemory(source);
        return Task.CompletedTask;
    }
    private static Task TransferTimerCleanup()
    {
        var clock = new TransferClock();
        var transfers = new MusicNativeTransfers(clock);
        transfers.Begin(O(new
        {
            kind = "script",
            length = 1,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("x"))),
            basename = "cleanup.js"
        }));
        clock.DisposeFailures = 1;
        try
        {
            transfers.Dispose();
            Check(false, "expected timer disposal failure");
        }
        catch (InvalidOperationException error)
        {
            Check(error.Message == "synthetic timer disposal failure", error.Message);
        }
        Check(clock.DisposeAttempts == 1, "timer disposal was retried");
        var stages = typeof(MusicNativeTransfers)
            .GetField("stages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transfers)!;
        Check((int)stages.GetType().GetProperty("Count")!.GetValue(stages)! == 0,
            "staging entries survived a timer disposal failure");
        transfers.Dispose();
        Check(clock.DisposeAttempts == 1, "timer disposal was retried after close");
        return Task.CompletedTask;
    }
    private static Task TransferSweepReschedule()
    {
        var clock = new TransferClock();
        using var transfers = new MusicNativeTransfers(clock);
        JsonObject Begin(string basename) => transfers.Begin(O(new
        {
            kind = "script",
            length = 1,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("x"))),
            basename
        }));
        string first = Begin("first.js")["token"]!.GetValue<string>();
        clock.Jump(TimeSpan.FromMinutes(1));
        string second = Begin("second.js")["token"]!.GetValue<string>();
        clock.Jump(TimeSpan.FromMinutes(4));
        try
        {
            transfers.Chunk(O(new { token = first, offset = 0, data = "eA==" }));
            Check(false, "expired transfer survived lookup sweep");
        }
        catch (MusicLibraryException error) { Check(error.Code == "TRANSFER_NOT_FOUND", error.Code); }
        Check(clock.ActiveTimers == 1, "remaining transfer did not keep exactly one timer");
        clock.Advance(TimeSpan.FromMinutes(1));
        try
        {
            transfers.Chunk(O(new { token = second, offset = 0, data = "eA==" }));
            Check(false, "remaining transfer did not expire on the rescheduled timer");
        }
        catch (MusicLibraryException error) { Check(error.Code == "TRANSFER_NOT_FOUND", error.Code); }
        return Task.CompletedTask;
    }
    private sealed class TransferClock : TimeProvider
    {
        private readonly List<TransferTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        internal int Fired;
        internal int DisposeFailures;
        internal int DisposeAttempts;
        internal int ActiveTimers { get { lock (timers) return timers.Count; } }
        internal TransferTimer? Last { get; private set; }
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TransferTimer(this, callback, state);
            lock (timers) { timers.Add(timer); Last = timer; }
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            now = now.Add(duration);
            while (true)
            {
                TransferTimer[] ready;
                lock (timers) ready = timers.Where(timer => timer.Due is { } due && due <= now).ToArray();
                if (ready.Length == 0) return;
                foreach (var timer in ready) timer.Fire();
            }
        }
        internal void Jump(TimeSpan duration) => now = now.Add(duration);
        internal sealed class TransferTimer(TransferClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal DateTimeOffset? Due;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.timers)
                {
                    if (disposed) return false;
                    Check(period == Timeout.InfiniteTimeSpan);
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now.Add(dueTime);
                    return true;
                }
            }
            internal void Fire()
            {
                lock (owner.timers)
                {
                    if (disposed || Due is null || Due > owner.now) return;
                    Due = null;
                    owner.Fired++;
                }
                callback(state);
            }
            internal void FireStale() => callback(state);
            public void Dispose()
            {
                lock (owner.timers)
                {
                    if (disposed) return;
                    owner.DisposeAttempts++;
                    if (owner.DisposeFailures > 0)
                    {
                        owner.DisposeFailures--;
                        throw new InvalidOperationException("synthetic timer disposal failure");
                    }
                    disposed = true;
                    Due = null;
                    owner.timers.Remove(this);
                }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Store : IDocumentStore
    {
        private readonly Dictionary<string, byte[]> documents = new(StringComparer.Ordinal);
        internal int Writes;
        internal bool FailAfter, Disposed;
        internal readonly HashSet<string> Namespaces = [];
        // Script sources and their data belong to the shared module, never to native documents.
        private static void Namespace(string value) =>
            Check(value == MusicLibraryOwner.DocumentNamespace, "Unexpected document namespace");
        public Task<DocumentSnapshot> ReadAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Namespace(name); Namespaces.Add(name);
            byte[]? bytes = documents.TryGetValue(name, out var value) ? value.ToArray() : null;
            return Task.FromResult(new DocumentSnapshot(bytes,
                bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes)));
        }
        public Task<DocumentFingerprint> WriteAsync(string name, ReadOnlyMemory<byte> data, DocumentFingerprint expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Namespace(name); Namespaces.Add(name);
            byte[]? prior = documents.TryGetValue(name, out var value) ? value : null;
            var before = prior is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(prior);
            if (before != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            documents[name] = data.ToArray(); Writes++;
            if (FailAfter) { FailAfter = false; throw new InvalidOperationException("synthetic commit receipt loss"); }
            return Task.FromResult(DocumentFingerprint.FromBytes(documents[name]));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed record Request(string Method, JsonObject Parameters);
    /// <summary>A shared-module double: stage bookkeeping plus one business handler.</summary>
    private sealed class Runtime : IRuntimeClient
    {
        internal readonly List<Request> Requests = [];
        internal readonly Dictionary<string, string> Staged = [], StageNames = [];
        private readonly Dictionary<string, MemoryStream> stages = [];
        internal Func<string, JsonObject, object>? Business;
        internal TaskCompletionSource? Entered;
        internal bool Closed;
        private int next;
        private static JsonElement Reply(object value)
        {
            var result = O(value); result["ok"] = true; result["contract"] = "music.script.v3"; return J(result);
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Closed) throw new BridgeException("CLOSED");
            var p = O(parameters ?? new { });
            Requests.Add(new(method, p));
            switch (method)
            {
                case "music.script.stage.begin":
                {
                    string token = "stage-" + ++next;
                    stages[token] = new(); StageNames[token] = p["basename"]!.GetValue<string>();
                    return Reply(new { token, chunkBytes = 65536 });
                }
                case "music.script.stage.chunk":
                {
                    var bytes = Convert.FromBase64String(p["data"]!.GetValue<string>());
                    var buffer = stages[p["token"]!.GetValue<string>()];
                    buffer.Write(bytes);
                    Staged[p["token"]!.GetValue<string>()] = Encoding.UTF8.GetString(buffer.ToArray());
                    return Reply(new { next = buffer.Length });
                }
                case "music.script.stage.abort":
                    if (stages.Remove(p["token"]!.GetValue<string>(), out var released)) released.Dispose();
                    return Reply(new { released = true });
            }
            if (Entered is { } entered)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return Reply(Business?.Invoke(method, p) ?? throw new InvalidOperationException("unexpected business call " + method));
        }
    }
}

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

public static class MusicNativeScriptTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native LX: ordinary source and settings workflows never call Node", Ordinary);
        yield return new("Native LX: bounded snapshot rejects altered chunks and content hash", Transfer);
        yield return new("Native LX: abandoned transfers actively expire and stale callbacks cannot retire renewed stages", TransferExpiry);
        yield return new("Native LX: lookup after an overdue sweep reschedules the remaining transfer", TransferSweepReschedule);
        yield return new("Native LX: transfer shutdown reports one timer disposal failure after releasing transfers", TransferTimerCleanup);
        yield return new("Native LX: source content storage failures retain native storage error semantics", SourceStorageErrors);
        yield return new("Native LX: disposal preserves the first shutdown failure while releasing transfers", DisposeFirstFailure);
        yield return new("Native LX: explicit enable then invoke targets one selected installation", Target);
        yield return new("Native LX: stored enabled source reopens idle without starting Node", Restart);
        yield return new("Native LX: update failure preserves metadata grant and live generation", FailedUpdate);
        yield return new("Native LX: update success retires old callbacks and enables rollback", Updated);
        yield return new("Native LX: disable and removal revoke grants and clean script storage", Removed);
        yield return new("Native LX: storage callback validates source site grant and session", Storage);
        yield return new("Native LX: source metadata change refreshes unrelated active storage revision", MetadataRevision);
        yield return new("Native LX: unknown native commit retires candidate and next command reconciles", Unknown);
        yield return new("Native LX: pending resolve cannot publish after cancel or source mutation", Cancel);
        yield return new("Native LX: invalid media expiry flags headers and identity rejected", MediaCandidates);
        yield return new("Native LX: lyric parsing remains native and respects persisted setting", Lyrics);
        yield return new("Native LX: explicit resolve rejects mismatched recording or generation", RecordingIdentity);
        yield return new("Native LX: missing target platform matches only that platform once and keeps the queue identity", MatchedTarget);
        yield return new("Native LX: existing mappings explicit recordings and lyrics never start target matching", MatchScope);
        yield return new("Native LX: failed changed or cancelled target matching never invokes the script", MatchFailure);
        yield return new("Native LX: shutdown drains owned calls and never disposes borrowed owners", Shutdown);
        yield return new("Native LX: candidate storage transaction cannot leak on failed trial", CandidateStorage);
        yield return new("Native LX: candidate storage cannot persist an undeclared site", CandidateSite);
        yield return new("Native LX: more than 32 installations persist and reopen Node-free", InstallationCount);
        yield return new("Native LX: selected script inspection preserves metadata encoding and integrity", Inspection);
        yield return new("Native LX: installation and update accept only explicit storage permissions", PermissionShape);
        yield return new("Native LX: actual extension declarations survive without expanding standard music UI", Declarations);
        yield return new("Native LX: declared names and arrays accept the full protocol bounds", DeclarationBounds);
        yield return new("Native LX: extension-only source cannot manufacture playback actions", ExtensionOnly);
        yield return new("Native LX: malformed or over-budget declarations retire the failed candidate", InvalidDeclarations);
        yield return new("Native LX: suspended old grant cannot write while a replacement is loading", SuspendedGrant);
        yield return new("Native LX: runtime recovery fences old grants until explicit source activation", RecoveredGrants);
        yield return new("Native LX: current script response contract is required before activation", ScriptContract);
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
                return Task.FromResult(J(new { ok = true, contract = "music.script.v2" }));
            });
            await new MusicScriptTransport(runtime).CallAsync(operation, parameters, default);
            Check(called == "music.script." + operation);
        }
        foreach (object? value in new object?[] { null, false, 7, "raw", new object?[] { null, false, 0 },
            new { blob = new { token = "business data", length = 900000 } } })
        {
            var expected = J(new { ok = true, contract = "music.script.v2", value });
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
        boundary.Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v2",
            value = new string('x', exact - Encoding.UTF8.GetByteCount("""{"ok":true,"contract":"music.script.v2","value":""}""")) });
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
            runtime.Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v2", value });
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
                Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v2", value = new string('x', 70_001) }) };
            runtime.Response = fault switch
            {
                "utf8" => [0xff],
                "json" => Encoding.UTF8.GetBytes("{"),
                "duplicate" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v2","value":{"a":1,"\u0061":2}}"""),
                "unicode-key" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v2","value":{"音乐":1}}"""),
                "unsafe-number" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v2","value":9007199254740992}"""),
                "surrogate" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v2","value":"\ud800"}"""),
                "depth" => Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65)),
                "envelope" => Encoding.UTF8.GetBytes("""{"value":false}"""),
                "contract" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"wrong","value":false}"""),
                "recursive" => Encoding.UTF8.GetBytes("""{"ok":true,"contract":"music.script.v2","blob":{}}"""),
                _ => runtime.Response
            };
            await TransportError("INVALID_RESPONSE", () => new MusicScriptTransport(runtime).CallAsync("invoke", new { }, default));
            Check(runtime.Releases == 1 && runtime.Blobs.Count == 0, fault);
            runtime.Fault = "";
            runtime.Response = null;
            Check((await new MusicScriptTransport(runtime).CallAsync("status", new { }, default)).GetProperty("ok").GetBoolean());
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
                    new { ok = true, contract = "music.script.v2", value = new string('x', 80_000) }) };
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
                Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v2", value = false }) };
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
            Response = BridgeCodec.EncodeJSONValue(new { ok = true, contract = "music.script.v2", value = false }) };
        brokenRelease.Before = (method, _) => { if (method == "music.script.blob.release") throw new BridgeException("CLOSED"); };
        await TransportError("INVALID_RESPONSE", () => new MusicScriptTransport(brokenRelease).CallAsync("invoke", new { }, default));
        Check(brokenRelease.Calls.Count(method => method == "music.script.blob.release") == 1);
        foreach (string cleanup in new[] { "control.abort", "blob.release" })
        {
            using var cancelled = new CancellationTokenSource();
            var runtime = new TransportRuntime { Response = BridgeCodec.EncodeJSONValue(
                new { ok = true, contract = "music.script.v2", value = false }) };
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
                    Check(p.GetProperty("operation").GetString() is "load" or "unload" or "invoke" or "status");
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
                    Check(method is "music.script.invoke" or "music.script.status");
                    DirectParameters = parameters; Received = p; result = Business();
                    break;
            }
            Frame(new { version = 1, session = "transport-test", id = 1, kind = "result", value = result });
            return Task.FromResult(result);
        }
        private JsonElement Business()
        {
            BusinessCalls++;
            if (Fault == "business") return J(new { ok = false, contract = "music.script.v2", code = "SCRIPT_EXCEPTION" });
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
            var result = O(value); result["ok"] = true; result["contract"] = "music.script.v2"; return J(result);
        }
    }
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonObject O(object value) => JsonNode.Parse(JsonSerializer.Serialize(value))!.AsObject();
    private static void Check(bool value, string code = "assertion") { if (!value) throw new InvalidOperationException(code); }
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
    private static object Permissions(bool write = true) => new { storage = write ? new[] { "read", "write" } : new[] { "read" } };
    private static JsonObject Song(string id = "11") => MusicSongFixture.Online(
        "wy", id, "Test track", "Test artist", "", 120,
        native: JsonSerializer.SerializeToNode(new {
            name = "Test track", singer = "Test artist", duration = 120
        })!.AsObject());
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store;
        internal readonly Runtime Runtime = new();
        internal readonly MusicLibraryOwner Library;
        internal readonly MusicScriptOwner Owner;
        internal Fixture(Store? store = null, TimeProvider? clock = null,
            Func<JsonObject, string, CancellationToken, Task<JsonObject>>? matchSong = null)
        {
            Store = store ?? new(); Library = new(Store); Owner = new(Library, Runtime, clock, matchSong);
            Runtime.Capabilities = Owner.ScriptCapabilities;
        }
        public async ValueTask DisposeAsync() { await Owner.DisposeAsync(); await Library.DisposeAsync(); }
    }
    private static async Task<string> Upload(Fixture f, string source = "// synthetic LX", string kind = "script")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        var stage = await Ok(f.Owner, "music.transfer.begin", new { kind, length = bytes.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), basename = kind == "script" ? "source.js" : "library.json" });
        string token = stage.GetProperty("token").GetString()!;
        await Ok(f.Owner, "music.transfer.chunk", new { token, offset = 0, data = Convert.ToBase64String(bytes) });
        return token;
    }
    private static async Task<string> Install(Fixture f, bool enable = false, string source = "// synthetic LX",
        bool write = true)
    {
        string token = await Upload(f, source);
        var installed = await Ok(f.Owner, "music.sources.install", new { token, name = "Synthetic source", version = "1",
            permissions = Permissions(write) });
        string id = installed.GetProperty("item").GetProperty("installationId").GetString()!;
        if (enable) await Ok(f.Owner, "music.sources.enable", new { installationId = id });
        return id;
    }
    private static async Task<JsonElement> Resolve(Fixture f, string id) =>
        await Ok(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k" });
    private static async Task Ordinary()
    {
        await using var f = new Fixture();
        var description = await Ok(f.Owner, "music.online.describe");
        Check(!description.GetProperty("trustedScriptsOnly").GetBoolean());
        string id = await Install(f);
        await Ok(f.Owner, "music.sources.list");
        await Ok(f.Owner, "music.sources.detail", new { installationId = id });
        string token = await Upload(f, "// updated inert source");
        await Ok(f.Owner, "music.sources.update", new { installationId = id, token, version = "2", permissions = Permissions() });
        await Ok(f.Owner, "music.sources.rollback", new { installationId = id });
        await Ok(f.Owner, "music.sources.disable", new { installationId = id });
        await Ok(f.Owner, "music.sources.remove", new { installationId = id });
        Check(f.Runtime.Calls.Count == 0);
        Check(f.Store.Writes > 0 && f.Store.Namespaces.Contains(MusicLibraryOwner.DocumentNamespace) &&
            f.Store.Namespaces.Any(Store.ContentNamespace) &&
            f.Store.Namespaces.All(value => value == MusicLibraryOwner.DocumentNamespace || Store.ContentNamespace(value)));
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
                kind = "script",
                length = index + 1,
                sha256 = new string('0', 64),
                basename = $"pending-{index}.js"
            });
            active.Add(pending.GetProperty("token").GetString()!);
        }
        Check(active.Distinct(StringComparer.Ordinal).Count() == active.Count);
        foreach (string pending in active)
            await Ok(f.Owner, "music.transfer.cancel", new { token = pending });
        await Error(f.Owner, "music.transfer.begin", new { kind = "script",
            length = SelectedScriptSnapshot.MaximumBytes + 1, sha256 = new string('0', 64),
            basename = "oversize.js" }, "LIMIT");
        var stage = await Ok(f.Owner, "music.transfer.begin", new { kind = "script", length = 1, sha256 = new string('0', 64), basename = "x.js" });
        string token = stage.GetProperty("token").GetString()!;
        await Error(f.Owner, "music.transfer.chunk", new { token, offset = 1, data = "YQ==" }, "TRANSFER_OFFSET");
        await Ok(f.Owner, "music.transfer.chunk", new { token, offset = 0, data = "YQ==" });
        await Error(f.Owner, "music.sources.install", new { token, name = "x", version = "1", permissions = Permissions() }, "TRANSFER_INTEGRITY");
        await Error(f.Owner, "music.sources.install", new { token, name = "x", version = "1", permissions = Permissions() }, "TRANSFER_NOT_FOUND");
        byte[] bom = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("// source")];
        stage = await Ok(f.Owner, "music.transfer.begin", new { kind = "script", length = bom.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(bom)), basename = "bom.js" });
        token = stage.GetProperty("token").GetString()!;
        await Ok(f.Owner, "music.transfer.chunk", new { token, offset = 0, data = Convert.ToBase64String(bom) });
        await Error(f.Owner, "music.sources.install", new { token, name = "x", version = "1",
            permissions = Permissions() }, "SCRIPT_ENCODING");
        Check(f.Runtime.Calls.Count == 0 && f.Store.Writes == 0);
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
    private static async Task SourceStorageErrors()
    {
        foreach (var (error, code) in new[]
        {
            (DocumentStoreError.Unavailable, "STORAGE_UNAVAILABLE"),
            (DocumentStoreError.Storage, "STORAGE")
        })
        {
            await using var f = new Fixture();
            string token = await Upload(f);
            f.Store.ContentReadFailure = error;
            await Error(f.Owner, "music.sources.install", new
            {
                token,
                name = "Synthetic source",
                version = "1",
                permissions = Permissions()
            }, code);
        }
    }
    private static async Task DisposeFirstFailure()
    {
        var clock = new TransferClock();
        var f = new Fixture(clock: clock);
        try
        {
            _ = await Install(f, true);
            _ = await Ok(f.Owner, "music.transfer.begin", new
            {
                kind = "script",
                length = 1,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("x"))),
                basename = "pending.js"
            });
            f.Runtime.FailUnload = true;
            int disposeAttempts = clock.DisposeAttempts;
            clock.DisposeFailures = 1;
            try
            {
                await f.Owner.DisposeAsync();
                Check(false, "expected shutdown failure");
            }
            catch (MusicLibraryException error) { Check(error.Code == "SCRIPT_EXCEPTION", error.Code); }
            Check(clock.DisposeAttempts - disposeAttempts == 1,
                "transfer timer disposal was attempted more than once after shutdown failure");
        }
        finally { await f.Library.DisposeAsync(); }
    }
    private static async Task Target()
    {
        await using var f = new Fixture(); string first = await Install(f, true), second = await Install(f, true);
        var response = await Resolve(f, second);
        Check(response.GetProperty("installationId").GetString() == second);
        Check(f.Runtime.Invoked.SequenceEqual([second]));
        Check(f.Runtime.Calls.All(m => m.StartsWith("music.script.", StringComparison.Ordinal)));
        Check(f.Runtime.Instances.ContainsKey(first));
    }
    private static async Task Restart()
    {
        var store = new Store(); string id;
        await using (var first = new Fixture(store)) id = await Install(first, true);
        await using var next = new Fixture(store);
        var item = (await Ok(next.Owner, "music.sources.detail", new { installationId = id })).GetProperty("item");
        Check(item.GetProperty("state").GetString() == "idle" && item.GetProperty("generation").GetInt64() == 0 && next.Runtime.Calls.Count == 0);
        await Resolve(next, id); Check(next.Runtime.Invoked.SequenceEqual([id]));
    }
    private static async Task FailedUpdate()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        var old = f.Runtime.Instances[id].DeepClone().AsObject();
        string transfer = await Upload(f, "// invalid trial");
        f.Runtime.FailLoad = true;
        await Error(f.Owner, "music.sources.update", new { installationId = id, token = transfer,
            version = "2", permissions = Permissions() }, "SCRIPT_EXCEPTION");
        f.Runtime.FailLoad = false;
        Check(f.Runtime.Instances[id]["generation"]!.GetValue<long>() == old["generation"]!.GetValue<long>());
        var value = await f.Runtime.Storage("set", old, "k", 1);
        Check(value.GetProperty("ok").GetBoolean());
        var detail = await Ok(f.Owner, "music.sources.detail", new { installationId = id });
        Check(detail.GetProperty("item").GetProperty("version").GetString() == "1");
        await Resolve(f, id);
    }
    private static async Task Updated()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        var old = f.Runtime.Instances[id].DeepClone().AsObject(); string token = await Upload(f, "// next");
        await Ok(f.Owner, "music.sources.update", new { installationId = id, token,
            version = "2", permissions = Permissions() });
        Check((await f.Runtime.Storage("set", old, "old", 1)).GetProperty("code").GetString() == "STALE_SESSION");
        var updated = f.Runtime.Instances[id].DeepClone().AsObject();
        await Ok(f.Owner, "music.sources.rollback", new { installationId = id });
        Check((await f.Runtime.Storage("get", updated, "old")).GetProperty("code").GetString() == "STALE_SESSION");
        var detail = await Ok(f.Owner, "music.sources.detail", new { installationId = id });
        Check(detail.GetProperty("item").GetProperty("version").GetString() == "1");
        await Resolve(f, id);
    }
    private static async Task Removed()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        var old = f.Runtime.Instances[id].DeepClone().AsObject();
        Check((await f.Runtime.Storage("set", old, "k", 1)).GetProperty("ok").GetBoolean());
        await Ok(f.Owner, "music.sources.disable", new { installationId = id });
        Check((await f.Runtime.Storage("get", old, "k")).GetProperty("code").GetString() == "STALE_SESSION");
        await Ok(f.Owner, "music.sources.enable", new { installationId = id });
        Check((await f.Runtime.Storage("get", f.Runtime.Instances[id], "k")).GetProperty("value").GetInt32() == 1);
        Check((await f.Runtime.Storage("set", old, "k", 2)).GetProperty("code").GetString() == "STALE_SESSION");
        await Ok(f.Owner, "music.sources.remove", new { installationId = id });
        var meta = await f.Library.ReadSourceMetadataAsync();
        var data = await f.Library.ReadSourceScriptDataAsync(id, meta.State.GetProperty("revision").GetInt64());
        Check(data.Sites.EnumerateObject().Count() == 0 && f.Runtime.Instances.Count == 0);
    }
    private static async Task Storage()
    {
        await using var f = new Fixture(); string id = await Install(f, true, write: false);
        var live = f.Runtime.Instances[id].DeepClone().AsObject();
        Check((await f.Runtime.Storage("get", live, "k")).GetProperty("ok").GetBoolean());
        Check(!(await f.Runtime.Storage("set", live, "k", 1)).GetProperty("ok").GetBoolean());
        var wrong = live.DeepClone().AsObject(); wrong["generation"] = 123456L;
        Check(!(await f.Runtime.Storage("get", wrong, "k")).GetProperty("ok").GetBoolean());
        wrong = live.DeepClone().AsObject(); wrong["installationId"] = "lx-wrong";
        Check(!(await f.Runtime.Storage("get", wrong, "k")).GetProperty("ok").GetBoolean());
        Check(!(await f.Runtime.Storage("get", live, "k", site: "not-platform")).GetProperty("ok").GetBoolean());
    }
    private static async Task MetadataRevision()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        _ = await Install(f);
        Check((await f.Runtime.Storage("set", f.Runtime.Instances[id], "after", 2)).GetProperty("ok").GetBoolean());
    }
    private static async Task Unknown()
    {
        await using var f = new Fixture(); string id = await Install(f);
        f.Store.FailAfter = true;
        await Error(f.Owner, "music.sources.enable", new { installationId = id }, "COMMIT_UNKNOWN");
        Check(f.Runtime.Instances.Count == 0);
        var list = await Ok(f.Owner, "music.sources.list");
        var reconciled = list.GetProperty("items")[0];
        Check(reconciled.GetProperty("enabled").GetBoolean() &&
            reconciled.GetProperty("state").GetString() == "idle" &&
            reconciled.GetProperty("generation").GetInt64() == 0);
        var value = await Ok(f.Owner, "music.sources.detail", new { installationId = id });
        Check(value.GetProperty("item").GetProperty("enabled").GetBoolean());
        await Resolve(f, id);
    }
    private static async Task Cancel()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        f.Runtime.Invoking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = f.Owner.ExecuteAsync("music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k" });
        await f.Runtime.Invoking.Task;
        await Ok(f.Owner, "music.online.cancel");
        Check((await operation).GetProperty("code").GetString() == "CANCELLED");
        f.Runtime.Invoking = null;
        await Resolve(f, id);
    }
    private static async Task MediaCandidates()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        foreach (var candidate in new object[]
        {
            new { url = "file:///x" }, new { url = "https://user:pass@media.example/x" },
            new { url = "https://media.example/x", trial = true }, new { url = "https://media.example/x", drm = true }
        })
        {
            f.Runtime.Value = J(candidate);
            await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k" }, "INVALID_MEDIA_CANDIDATE");
        }
        f.Runtime.Value = J(new { url = "https://media.example/x", expiresAt = 1 });
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k" }, "EXPIRED_MEDIA_CANDIDATE");
        f.Runtime.Value = J(new { url = "https://media.example/x", headers = new { Host = "other.example" } });
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k" }, "MEDIA_HEADERS_UNSUPPORTED");
        f.Runtime.Value = J(new { url = "https://media.example/x", sourceKey = "other" });
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k" }, "TARGET_CHANGED");
    }
    private static async Task Lyrics()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        f.Runtime.Value = J(new { lyric = "[00:01.00]Hello", tlyric = "[00:01.00]你好" });
        var response = await Ok(f.Owner, "music.online.lyrics", new { installationId = id, platform = "wy", song = Song() });
        Check(response.GetProperty("lines")[0].GetProperty("translation").GetString() == "你好");
        var settings = await f.Library.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false });
        Check(settings.GetProperty("ok").GetBoolean());
        int calls = f.Runtime.Invoked.Count;
        response = await Ok(f.Owner, "music.online.lyrics", new { installationId = id, platform = "wy", song = Song() });
        Check(response.GetProperty("state").GetString() == "disabled" && calls == f.Runtime.Invoked.Count);
    }
    private static async Task RecordingIdentity()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "tx", song = Song(), quality = "320k" }, "TARGET_CHANGED");
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k", generation = 999 }, "STALE_SESSION");
        Check(f.Runtime.Invoked.Count == 0);
    }
    private static readonly JsonObject BothSites = O(new {
        wy = new { actions = new[] { "musicUrl", "lyric" }, qualitys = new[] { "320k" } },
        tx = new { actions = new[] { "musicUrl", "lyric" }, qualitys = new[] { "320k" } } });
    // The same recording identity on QQ: equal normalized title, artist and duration.
    private static JsonObject Mapped(JsonObject song, string title = "Test track")
    {
        var tx = MusicSongFixture.Online("tx", "matched-tx", title, "Test artist", "", 120,
            native: JsonSerializer.SerializeToNode(new { name = title, singer = "Test artist", duration = 120 })!.AsObject());
        var expanded = song.DeepClone().AsObject();
        expanded["sources"]!.AsArray().Add(tx["sources"]![0]!.DeepClone());
        return title == "Test track" ? NativeMusicSong.Normalize(expanded) : tx;
    }
    private static async Task MatchedTarget()
    {
        var calls = new List<(string SongId, string Platform)>();
        await using var f = new Fixture(matchSong: (song, platform, _) =>
        {
            lock (calls) calls.Add((LJ.S(song["songId"]), platform));
            return Task.FromResult(Mapped(song));
        });
        f.Runtime.Declarations = BothSites;
        string id = await Install(f, true);
        var queued = Song();
        string matchedKey = LJ.S(Mapped(queued)["sources"]!.AsArray().OfType<JsonObject>()
            .Single(source => LJ.S(source["source"]) == "tx")["sourceKey"]);
        var first = await Ok(f.Owner, "music.online.resolve", new { installationId = id, platform = "tx", song = queued, quality = "320k" });
        Check(calls.SequenceEqual([(LJ.S(queued["songId"]), "tx")]) && f.Runtime.Invoked.SequenceEqual([id]),
            "one missing target platform is matched once before one script request");
        Check(first.GetProperty("sourceKey").GetString() == matchedKey &&
            first.GetProperty("songId").GetString() == LJ.S(queued["songId"]), "the script receives the matched recording");
        var recording = first.GetProperty("recordingSong");
        Check(recording.GetProperty("songId").GetString() == LJ.S(queued["songId"]) &&
            recording.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("source").GetString() == "tx") &&
            queued["sources"]!.AsArray().Count == 1, "the queue song identity and snapshot stay unchanged");
        var refreshed = await Ok(f.Owner, "music.online.resolve", new { installationId = id, platform = "tx",
            song = recording, sourceKey = matchedKey, quality = "320k",
            qualityPolicySignature = first.GetProperty("qualityPolicySignature").GetString(),
            qualityCacheSignature = first.GetProperty("qualityCacheSignature").GetString() });
        Check(calls.Count == 1 && f.Runtime.Invoked.Count == 2 && !refreshed.TryGetProperty("recordingSong", out _) &&
            refreshed.GetProperty("qualityCacheSignature").GetString() == first.GetProperty("qualityCacheSignature").GetString(),
            "an exact refresh reuses the accepted recording without matching again");
    }
    private static async Task MatchScope()
    {
        int calls = 0;
        await using var f = new Fixture(matchSong: (song, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Mapped(song));
        });
        f.Runtime.Declarations = BothSites;
        string id = await Install(f, true);
        var resolved = await Ok(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k" });
        Check(!resolved.TryGetProperty("recordingSong", out _), "an existing mapping is not expanded");
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "tx", song = Song(),
            sourceKey = """["tx","explicit",""]""", quality = "320k" }, "TARGET_CHANGED");
        await Error(f.Owner, "music.online.lyrics", new { installationId = id, platform = "tx", song = Song() }, "TARGET_CHANGED");
        Check(calls == 0 && f.Runtime.Invoked.Count == 1, "only an implicit missing audio target may match");
    }
    private static async Task MatchFailure()
    {
        Func<JsonObject, string, CancellationToken, Task<JsonObject>> behavior = (_, _, _) =>
            throw new MusicLibraryException("TIMEOUT");
        await using var f = new Fixture(matchSong: (song, platform, token) => behavior(song, platform, token));
        f.Runtime.Declarations = BothSites;
        string id = await Install(f, true);
        object Input() => new { installationId = id, platform = "tx", song = Song(), quality = "320k" };
        await Error(f.Owner, "music.online.resolve", Input(), "TIMEOUT");
        behavior = (song, _, _) => Task.FromResult(song);
        await Error(f.Owner, "music.online.resolve", Input(), "RECORDING_UNAVAILABLE");
        behavior = (song, _, _) => Task.FromResult(Mapped(song, "Another track"));
        await Error(f.Owner, "music.online.resolve", Input(), "TARGET_CHANGED");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        behavior = async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null!; };
        var pending = f.Owner.ExecuteAsync("music.online.resolve", Input());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Ok(f.Owner, "music.online.cancel");
        Check((await pending.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("code").GetString() == "CANCELLED");
        Check(f.Runtime.Invoked.Count == 0, "a failed or retired match never reaches the script");
    }
    private static async Task Shutdown()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        var old = f.Runtime.Instances[id].DeepClone().AsObject();
        await f.Owner.PrepareShutdownAsync();
        await Error(f.Owner, "music.sources.list", null, "CLOSED");
        Check(f.Runtime.Instances.Count == 0 && !f.Store.Disposed);
        Check((await f.Library.ExecuteAsync("music.library.list")).GetProperty("ok").GetBoolean());
        await f.Owner.CancelShutdownPreparationAsync();
        Check((await f.Runtime.Storage("get", old, "k")).GetProperty("code").GetString() == "STALE_SESSION");
        Check(f.Runtime.Instances.Count == 0);
        await Ok(f.Owner, "music.sources.enable", new { installationId = id });
        Check((await f.Runtime.Storage("get", f.Runtime.Instances[id], "k")).GetProperty("ok").GetBoolean());
        Check((await f.Runtime.Storage("get", old, "k")).GetProperty("code").GetString() == "STALE_SESSION");
    }
    private static async Task CandidateStorage()
    {
        await using var f = new Fixture(); string id = await Install(f);
        // Deliberately exercises the native candidate boundary with a trusted
        // fake callback. The real LX host separately denies startup storage.
        f.Runtime.CandidateWrite = true; f.Runtime.FailAfterCandidate = true;
        await Error(f.Owner, "music.sources.enable", new { installationId = id }, "SCRIPT_EXCEPTION");
        var snapshot = await f.Library.ReadSourceMetadataAsync();
        var data = await f.Library.ReadSourceScriptDataAsync(id, snapshot.State.GetProperty("revision").GetInt64());
        Check(data.Sites.EnumerateObject().Count() == 0);
        f.Runtime.FailAfterCandidate = false;
        await Ok(f.Owner, "music.sources.enable", new { installationId = id });
        Check((await f.Runtime.Storage("get", f.Runtime.Instances[id], "candidate")).GetProperty("value").GetInt32() == 7);
    }
    private static async Task CandidateSite()
    {
        await using var f = new Fixture(); string id = await Install(f);
        f.Runtime.CandidateWrite = true; f.Runtime.CandidateSite = "undeclared";
        await Error(f.Owner, "music.sources.enable", new { installationId = id }, "SCRIPT_PROTOCOL_ERROR");
        var metadata = await f.Library.ReadSourceMetadataAsync();
        Check(!(await f.Library.ReadSourceScriptDataAsync(id, metadata.State.GetProperty("revision").GetInt64())).Sites.EnumerateObject().Any());
        Check((await f.Runtime.Storage("get", f.Runtime.LastCandidate!, "late", site: "undeclared")).GetProperty("code").GetString() == "STALE_SESSION");
    }
    private static async Task InstallationCount()
    {
        await using var f = new Fixture();
        var ids = new List<string>();
        for (int index = 0; index < 33; index++)
        {
            ids.Add(await Install(f));
            Check(f.Runtime.Calls.Count == 0);
        }
        var list = await Ok(f.Owner, "music.sources.list");
        Check(list.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("installationId").GetString()).SequenceEqual(ids));
        Check(f.Runtime.Calls.Count == 0);
        await f.Owner.DisposeAsync(); await f.Library.DisposeAsync();
        await using var restored = new Fixture(f.Store);
        var reopened = await Ok(restored.Owner, "music.sources.list");
        Check(reopened.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("installationId").GetString()).SequenceEqual(ids));
        Check(reopened.GetProperty("revision").GetInt64() == list.GetProperty("revision").GetInt64());
        Check(f.Runtime.Calls.Count == 0 && restored.Runtime.Calls.Count == 0);
    }
    private static Task Inspection()
    {
        const string source = """
            /*!
             * @name Inspector source
             * @version 2.3
             */
            const publicUrl = 'https://music.example/path';
            const localUrl = 'http://127.0.0.1:8731/private';
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        var inspected = MusicSourceScriptInspector.Inspect(bytes, "fallback.js",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Check(inspected.Name == "Inspector source" && inspected.Version == "2.3");
        byte[] manyUrls = Encoding.UTF8.GetBytes(source + string.Join('\n',
            Enumerable.Range(0, 200).Select(index => $"// https://source-{index}.example/path")));
        Check(MusicSourceScriptInspector.Inspect(manyUrls, "fallback.js",
            Convert.ToHexStringLower(SHA256.HashData(manyUrls))) == inspected);
        try
        {
            MusicSourceScriptInspector.Inspect(bytes, "changed.js", new string('0', 64));
            throw new InvalidOperationException("Altered hash accepted");
        }
        catch (MusicOperationException error) { Check(error.Code == "SCRIPT_INTEGRITY"); }
        try
        {
            MusicSourceScriptInspector.Inspect([0xef, 0xbb, 0xbf, .. bytes], "bom.js",
                Convert.ToHexStringLower(SHA256.HashData([0xef, 0xbb, 0xbf, .. bytes])));
            throw new InvalidOperationException("BOM accepted");
        }
        catch (MusicOperationException error) { Check(error.Code == "SCRIPT_ENCODING"); }
        try
        {
            MusicSourceScriptInspector.Inspect(new byte[SelectedScriptSnapshot.MaximumBytes + 1],
                "oversize.js", new string('0', 64));
            throw new InvalidOperationException("Oversize source accepted");
        }
        catch (MusicOperationException error) { Check(error.Code == "SCRIPT_INTEGRITY"); }
        return Task.CompletedTask;
    }
    private static async Task PermissionShape()
    {
        await using var f = new Fixture(); string id = await Install(f);
        var detail = await Ok(f.Owner, "music.sources.detail", new { installationId = id });
        Check(detail.GetProperty("permissions").EnumerateObject().Select(field => field.Name).SequenceEqual(["storage"]));
        foreach (object? permissions in new object?[]
        {
            null, new { }, new { storage = (object?)null }, new { storage = "read" },
            new { storage = new[] { "network" } }, new { storage = new[] { "read", "read" } },
            new { storage = new[] { 1 } }, new { storage = Array.Empty<string>(), unsupported = Array.Empty<string>() },
            new { storage = Array.Empty<string>(), localServices = false }
        })
        {
            string token = await Upload(f); int writes = f.Store.Writes;
            await Error(f.Owner, "music.sources.install", new { token, name = "invalid", version = "1", permissions }, "INVALID_REQUEST");
            await Error(f.Owner, "music.sources.update", new { installationId = id, token, version = "2", permissions }, "INVALID_REQUEST");
            Check(writes == f.Store.Writes);
            await Ok(f.Owner, "music.transfer.cancel", new { token });
        }
        Check(f.Runtime.Calls.Count == 0);
    }
    private static async Task Declarations()
    {
        await using var f = new Fixture();
        f.Runtime.Declarations = O(new
        {
            wy = new { actions = new[] { "musicSearch", "musicUrl", "search", "pic", "lyric" }, qualitys = new[] { "qs", "320k", "qsvip" } },
            custom = new { actions = new[] { "search", "pic" }, qualitys = new[] { "qsvip" } },
            site2 = new { actions = Array.Empty<string>(), qualitys = Array.Empty<string>() },
            site3 = new { actions = new[] { "catalog" }, qualitys = new[] { "lossless-custom" } },
            site4 = new { actions = new[] { "search" }, qualitys = Array.Empty<string>() },
            site5 = new { actions = new[] { "pic" }, qualitys = Array.Empty<string>() },
            site6 = new { actions = new[] { "musicSearch" }, qualitys = Array.Empty<string>() }
        });
        string id = await Install(f, true);
        var item = (await Ok(f.Owner, "music.sources.detail", new { installationId = id })).GetProperty("item");
        var capabilities = item.GetProperty("capabilities");
        Check(capabilities.GetArrayLength() == 7);
        var wy = capabilities.EnumerateArray().Single(capability => capability.GetProperty("platform").GetString() == "wy");
        Check(wy.GetProperty("actions").EnumerateArray().Select(n => n.GetString())
            .SequenceEqual(["musicSearch", "musicUrl", "search", "pic", "lyric"]));
        Check(wy.GetProperty("qualitys").EnumerateArray().Select(n => n.GetString()).SequenceEqual(["qs", "320k", "qsvip"]));
        Check(MusicSourceRow.Read(item).Platforms.SequenceEqual(["wy"]));
        var active = f.Runtime.Instances[id];
        Check((await f.Runtime.Storage("set", active, "extension", new { enabled = false, items = new object?[] { null, 7, "qs" } },
            site: "custom")).GetProperty("ok").GetBoolean());
        var stored = (await f.Runtime.Storage("get", active, "extension", site: "custom")).GetProperty("value");
        Check(!stored.GetProperty("enabled").GetBoolean() && stored.GetProperty("items")[0].ValueKind == JsonValueKind.Null &&
            stored.GetProperty("items")[1].GetInt32() == 7 && stored.GetProperty("items")[2].GetString() == "qs");
        Check((await f.Runtime.Storage("get", active, "extension", site: "undeclared")).GetProperty("code").GetString() == "STALE_SESSION");
        await Resolve(f, id);
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "qsvip" }, "INVALID_REQUEST");
        string token = await Upload(f, "// changed declarations");
        f.Runtime.Declarations.Remove("custom");
        await Ok(f.Owner, "music.sources.update", new { installationId = id, token, version = "2", permissions = Permissions() });
        Check((await f.Runtime.Storage("get", f.Runtime.Instances[id], "extension", site: "custom")).GetProperty("code").GetString() == "STALE_SESSION");
        var metadata = await f.Library.ReadSourceMetadataAsync();
        Check((await f.Library.ReadSourceScriptDataAsync(id, metadata.State.GetProperty("revision").GetInt64()))
            .Sites.GetProperty("custom").GetProperty("values").GetArrayLength() == 1);
    }
    private static async Task DeclarationBounds()
    {
        await using var f = new Fixture(); f.Runtime.Declarations = new JsonObject();
        string site = new('s', 128), action = new('a', 128), quality = new('q', 64);
        var actions = Enumerable.Range(1, 31).Select(index => "action-" + index).Prepend(action).ToArray();
        var qualities = Enumerable.Range(1, 30).Select(index => "quality-" + index).Prepend(" qs ").Prepend(quality).ToArray();
        foreach (string key in Enumerable.Range(1, 31).Select(index => "site-" + index).Prepend(site))
            f.Runtime.Declarations[key] = O(new { actions, qualitys = qualities });
        string id = await Install(f, true);
        var item = (await Ok(f.Owner, "music.sources.detail", new { installationId = id })).GetProperty("item");
        var cap = item.GetProperty("capabilities").EnumerateArray().Single(value => value.GetProperty("platform").GetString() == site);
        Check(item.GetProperty("capabilities").GetArrayLength() == 32 && cap.GetProperty("actions").GetArrayLength() == 32);
        Check(cap.GetProperty("actions")[0].GetString() == action && cap.GetProperty("qualitys")[0].GetString() == quality &&
            cap.GetProperty("qualitys")[1].GetString() == " qs ");
        Check(MusicSourceRow.Read(item).Platforms.Length == 0);
        Check((await f.Runtime.Storage("set", f.Runtime.Instances[id], "bounded", 1, site)).GetProperty("ok").GetBoolean());
    }
    private static async Task ExtensionOnly()
    {
        await using var f = new Fixture();
        f.Runtime.Declarations = O(new { wy = new { actions = new[] { "search", "musicSearch", "pic" }, qualitys = new[] { "qs", "qsvip" } } });
        string id = await Install(f, true);
        var item = (await Ok(f.Owner, "music.sources.detail", new { installationId = id })).GetProperty("item");
        Check(MusicSourceRow.Read(item).Platforms.Length == 0, "the playback UI cannot offer an undeclared musicUrl action");
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k" }, "CAPABILITY_UNAVAILABLE");
        await Error(f.Owner, "music.online.lyrics", new { installationId = id, platform = "wy", song = Song() }, "CAPABILITY_UNAVAILABLE");
        Check(f.Runtime.Invoked.Count == 0);
    }
    private static async Task InvalidDeclarations()
    {
        JsonObject Cap(object[] actions, object[] qualitys) => O(new { wy = new { actions, qualitys } });
        var tooManySites = new JsonObject();
        for (int index = 0; index < 33; index++) tooManySites["site-" + index] = O(new { actions = new[] { "search" }, qualitys = Array.Empty<string>() });
        foreach (var declarations in new[]
        {
            new JsonObject(), tooManySites, O(new Dictionary<string, object> { [" "] = new { actions = new[] { "search" }, qualitys = Array.Empty<string>() } }),
            Cap([new string('a', 129)], []), Cap(["search", "search"], []), Cap([""], []), Cap(["search\n"], []), Cap([1], []),
            Cap(["search"], [new string('q', 65)]), Cap(["search"], ["qs", "qs"]), Cap(["search"], [null!]),
            Cap(Enumerable.Range(0, 33).Select(index => (object)("action-" + index)).ToArray(), []),
            Cap(["search"], Enumerable.Range(0, 33).Select(index => (object)("quality-" + index)).ToArray())
        })
        {
            await using var f = new Fixture(); f.Runtime.Declarations = declarations;
            string id = await Install(f);
            await Error(f.Owner, "music.sources.enable", new { installationId = id }, "SCRIPT_PROTOCOL_ERROR");
            Check(f.Runtime.Instances.Count == 0);
            Check(!(await Ok(f.Owner, "music.sources.detail", new { installationId = id })).GetProperty("item").GetProperty("enabled").GetBoolean());
            Check((await f.Runtime.Storage("get", f.Runtime.LastCandidate!, "late")).GetProperty("code").GetString() == "STALE_SESSION");
        }
    }
    private static async Task SuspendedGrant()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        var old = f.Runtime.Instances[id].DeepClone().AsObject();
        f.Runtime.Loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.LoadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.FailAfterCandidate = true;
        string token = await Upload(f);
        var update = f.Owner.ExecuteAsync("music.sources.update", new { installationId = id, token, version = "2", permissions = Permissions() });
        await f.Runtime.Loading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check((await f.Runtime.Storage("set", old, "late", 1)).GetProperty("code").GetString() == "STALE_SESSION");
        f.Runtime.LoadRelease.TrySetResult();
        Check((await update).GetProperty("code").GetString() == "SCRIPT_EXCEPTION");
        Check((await f.Runtime.Storage("set", old, "restored", 2)).GetProperty("ok").GetBoolean());
        Check((await f.Runtime.Storage("set", f.Runtime.LastCandidate!, "late", 3)).GetProperty("code").GetString() == "STALE_SESSION");
    }
    private static async Task RecoveredGrants()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        var old = f.Runtime.Instances[id].DeepClone().AsObject(); int calls = f.Runtime.Calls.Count;
        await f.Owner.PrepareRuntimeRecoveryAsync(CancellationToken.None);
        Check((await f.Runtime.Storage("get", old, "late")).GetProperty("code").GetString() == "CLOSED");
        f.Runtime.Instances.Clear();
        f.Owner.ResumeAfterRuntimeRecovery();
        Check(f.Runtime.Calls.Count == calls);
        Check((await f.Runtime.Storage("get", old, "late")).GetProperty("code").GetString() == "STALE_SESSION");
        await Ok(f.Owner, "music.sources.enable", new { installationId = id });
        Check((await f.Runtime.Storage("get", f.Runtime.Instances[id], "current")).GetProperty("ok").GetBoolean());
        Check((await f.Runtime.Storage("get", old, "late")).GetProperty("code").GetString() == "STALE_SESSION");
    }
    private static async Task ScriptContract()
    {
        await using var f = new Fixture(); string id = await Install(f);
        f.Runtime.Contract = "music.script.v1";
        await Error(f.Owner, "music.sources.enable", new { installationId = id }, "INVALID_RESPONSE");
        Check(f.Runtime.Instances.Count == 0 && !f.Runtime.Calls.Contains("music.script.load"));
        f.Runtime.Contract = "music.script.v2";
        await Ok(f.Owner, "music.sources.enable", new { installationId = id });
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
        internal DocumentStoreError? ContentReadFailure;
        internal readonly HashSet<string> Namespaces = [];
        internal static bool ContentNamespace(string value)
        {
            string prefix = MusicLibraryOwner.ScriptContentCollection + ".";
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string hash = value[prefix.Length..];
            return hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        }
        private static void Namespace(string value) =>
            Check(value == MusicLibraryOwner.DocumentNamespace || ContentNamespace(value), "Unexpected document namespace");
        public Task<DocumentSnapshot> ReadAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Namespace(name); Namespaces.Add(name);
            if (ContentNamespace(name) && ContentReadFailure is { } error)
            {
                ContentReadFailure = null;
                throw new DocumentStoreException(error);
            }
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
    private sealed class Runtime : IRuntimeClient
    {
        internal readonly List<string> Calls = [], Invoked = [];
        internal readonly Dictionary<string, JsonObject> Instances = [];
        internal IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> Capabilities = null!;
        internal bool FailLoad, CandidateWrite, FailAfterCandidate, FailUnload;
        internal TaskCompletionSource? Invoking, Loading, LoadRelease;
        internal JsonObject? LastCandidate;
        internal string Contract = "music.script.v2";
        internal string CandidateSite = "wy";
        internal JsonObject Declarations = O(new { wy = new { actions = new[] { "musicUrl", "lyric" }, qualitys = new[] { "320k" } } });
        internal JsonElement Value = J(new { url = "https://media.example/audio", headers = new { } });
        private long generation;
        internal async Task<JsonElement> Storage(string operation, JsonObject instance, string key, object? value = null, string site = "wy")
        {
            var p = O(new { grant = instance["grant"]!.GetValue<string>(), installationId = instance["installationId"]!.GetValue<string>(),
                generation = instance["generation"]!.GetValue<long>(), siteId = site, key });
            if (operation == "set") p["value"] = JsonSerializer.SerializeToNode(value);
            return JsonSerializer.SerializeToElement(await Capabilities["platform.musicScript.storage." + operation](J(p), CancellationToken.None));
        }
        private JsonElement Reply(object value)
        {
            var result = O(value); result["ok"] = true; result["contract"] = Contract; return J(result);
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls.Add(method); var p = O(parameters ?? new { });
            switch (method)
            {
                case "music.script.stage.begin": return Reply(new { token = Guid.NewGuid().ToString("D"), chunkBytes = 65536 });
                case "music.script.stage.chunk":
                    return Reply(new { next = p["offset"]!.GetValue<int>() + Convert.FromBase64String(p["data"]!.GetValue<string>()).Length });
                case "music.script.stage.abort": return Reply(new { released = true });
                case "music.script.load":
                    Check(p["permissions"]!.AsObject().Select(field => field.Key).SequenceEqual(["storage"]), "Only storage permissions cross load");
                    if (FailLoad) return J(new { ok = false, contract = Contract, code = "SCRIPT_EXCEPTION" });
                    string id = p["installationId"]!.GetValue<string>();
                    var instance = O(new { installationId = id, revision = p["revision"]!.GetValue<long>(), generation = ++generation,
                        grant = p["grant"]!.GetValue<string>(), state = "ready",
                        capabilities = Declarations });
                    LastCandidate = instance;
                    if (CandidateWrite) Check((await Storage("set", instance, "candidate", 7, CandidateSite)).GetProperty("ok").GetBoolean());
                    if (Loading is not null) { Loading.TrySetResult(); await LoadRelease!.Task.WaitAsync(cancellationToken); }
                    if (FailAfterCandidate) return J(new { ok = false, contract = Contract, code = "SCRIPT_EXCEPTION" });
                    Instances[id] = instance;
                    return Reply(new { instance });
                case "music.script.status": return Reply(new { documentAccess = false, instances = Instances.Values.ToArray() });
                case "music.script.unload":
                    if (FailUnload) return J(new { ok = false, contract = Contract, code = "SCRIPT_EXCEPTION" });
                    Instances.Remove(p["installationId"]!.GetValue<string>()); return Reply(new { cleanupComplete = true });
                case "music.script.invoke":
                    Invoked.Add(p["installationId"]!.GetValue<string>());
                    if (Invoking is { } pending) { pending.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
                    return Reply(new { value = Value });
                default: throw new InvalidOperationException("ordinary business reached Node");
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicNativeScriptTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native LX: ordinary source and settings workflows never call Node", Ordinary);
        yield return new("Native LX: bounded snapshot rejects altered chunks and content hash", Transfer);
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
        yield return new("Native LX: no global target fallback for mismatched recording or generation", BadTarget);
        yield return new("Native LX: shutdown drains owned calls and never disposes borrowed owners", Shutdown);
        yield return new("Native LX: candidate storage transaction cannot leak on failed trial", CandidateStorage);
        yield return new("Native LX: current source and reviewed origin limits reject local access", Limits);
        yield return new("Native LX: selected script inspection extracts current metadata and reviewed origins", Inspection);
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
    private static object Permissions(bool write = true, string[]? origins = null) => new { networkOrigins = origins ?? [],
        storage = write ? new[] { "read", "write" } : new[] { "read" } };
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
        internal Fixture(Store? store = null)
        {
            Store = store ?? new(); Library = new(Store); Owner = new(Library, Runtime);
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
        bool write = true, string[]? origins = null)
    {
        string token = await Upload(f, source);
        var installed = await Ok(f.Owner, "music.sources.install", new { token, name = "Synthetic source", version = "1",
            permissions = Permissions(write, origins) });
        string id = installed.GetProperty("item").GetProperty("installationId").GetString()!;
        if (enable) await Ok(f.Owner, "music.sources.enable", new { installationId = id });
        return id;
    }
    private static async Task<JsonElement> Resolve(Fixture f, string id) =>
        await Ok(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k" });
    private static async Task Ordinary()
    {
        await using var f = new Fixture();
        await Ok(f.Owner, "music.online.describe"); string id = await Install(f);
        await Ok(f.Owner, "music.sources.list");
        await Ok(f.Owner, "music.sources.detail", new { installationId = id });
        string token = await Upload(f, "// updated inert source");
        await Ok(f.Owner, "music.sources.update", new { installationId = id, token, version = "2", permissions = Permissions() });
        await Ok(f.Owner, "music.sources.rollback", new { installationId = id });
        await Ok(f.Owner, "music.sources.disable", new { installationId = id });
        await Ok(f.Owner, "music.sources.remove", new { installationId = id });
        Check(f.Runtime.Calls.Count == 0);
        Check(f.Store.Writes > 0 && f.Store.Namespaces.SetEquals(["music.library.v1"]));
    }
    private static async Task Transfer()
    {
        await using var f = new Fixture();
        await Error(f.Owner, "music.transfer.begin", new { kind = "script", length = 1, sha256 = new string('0', 64), basename = "../x.js" }, "INVALID_REQUEST");
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
        Check(!(await f.Runtime.Storage("set", old, "old", 1)).GetProperty("ok").GetBoolean());
        await Ok(f.Owner, "music.sources.rollback", new { installationId = id });
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
        Check(!(await f.Runtime.Storage("get", old, "k")).GetProperty("ok").GetBoolean());
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
        var settings = await f.Library.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false, target = (object?)null });
        Check(settings.GetProperty("ok").GetBoolean());
        int calls = f.Runtime.Invoked.Count;
        response = await Ok(f.Owner, "music.online.lyrics", new { installationId = id, platform = "wy", song = Song() });
        Check(response.GetProperty("state").GetString() == "disabled" && calls == f.Runtime.Invoked.Count);
    }
    private static async Task BadTarget()
    {
        await using var f = new Fixture(); string id = await Install(f, true);
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "tx", song = Song(), quality = "320k" }, "TARGET_CHANGED");
        await Error(f.Owner, "music.online.resolve", new { installationId = id, platform = "wy", song = Song(), quality = "320k", generation = 999 }, "STALE_SESSION");
        Check(f.Runtime.Invoked.Count == 0);
    }
    private static async Task Shutdown()
    {
        await using var f = new Fixture(); _ = await Install(f, true);
        await f.Owner.PrepareShutdownAsync();
        await Error(f.Owner, "music.sources.list", null, "CLOSED");
        Check(f.Runtime.Instances.Count == 0 && !f.Store.Disposed);
        Check((await f.Library.ExecuteAsync("music.library.list")).GetProperty("ok").GetBoolean());
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
    private static async Task Limits()
    {
        await using var f = new Fixture();
        var origins = Enumerable.Range(0, 55).Select(index => $"https://source-{index}.example").ToArray();
        string first = await Install(f, origins: origins);
        var detail = await Ok(f.Owner, "music.sources.detail", new { installationId = first });
        Check(detail.GetProperty("permissions").GetProperty("networkOrigins").GetArrayLength() == 55);

        string token = await Upload(f);
        await Error(f.Owner, "music.sources.install", new { token, name = "too many", version = "1",
            permissions = Permissions(origins: Enumerable.Range(0, 129)
                .Select(index => $"https://overflow-{index}.example").ToArray()) }, "LIMIT");
        await Ok(f.Owner, "music.transfer.cancel", new { token });

        token = await Upload(f);
        await Error(f.Owner, "music.sources.install", new { token, name = "local", version = "1",
            permissions = Permissions(origins: ["http://127.0.0.1:8731"]) }, "INVALID_REQUEST");
        await Ok(f.Owner, "music.transfer.cancel", new { token });

        for (int index = 1; index < 32; index++) _ = await Install(f);
        token = await Upload(f);
        await Error(f.Owner, "music.sources.install", new { token, name = "overflow", version = "1",
            permissions = Permissions() }, "LIMIT");
        await Ok(f.Owner, "music.transfer.cancel", new { token });
        Check(f.Runtime.Calls.Count == 0);
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
        Check(inspected.NetworkOrigins.SequenceEqual(["https://music.example"]));
        Check(MusicSourceScriptInspector.ReviewedExtraOrigins(
            "c68c2ab7045b94769786b56cf7c6e614d82bc8f303bff68173bef9790583affc")
            .Contains("https://wyapi.toubiec.cn"));
        foreach (var local in new[] { "http://2130706433", "http://0x7f000001", "http://127.1",
            "http://169.254.169.254", "http://100.64.0.1", "http://[::1]",
            "http://[::ffff:127.0.0.1]", "http://[fc00::1]", "http://[fe80::1]" })
            Check(!MusicSourceNetworkPolicy.TryFromUrl(local, out _), local);
        try
        {
            MusicSourceScriptInspector.Inspect([0xef, 0xbb, 0xbf, .. bytes], "bom.js",
                Convert.ToHexStringLower(SHA256.HashData([0xef, 0xbb, 0xbf, .. bytes])));
            throw new InvalidOperationException("BOM accepted");
        }
        catch (MusicOperationException error) { Check(error.Code == "SCRIPT_ENCODING"); }
        return Task.CompletedTask;
    }
    private sealed class Store : IProtectedDocumentStore
    {
        internal byte[]? Bytes;
        internal int Writes;
        internal bool FailAfter, Disposed;
        internal readonly HashSet<string> Namespaces = [];
        public Task<ProtectedDocument> ReadAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Namespaces.Add(name);
            return Task.FromResult(new ProtectedDocument(Bytes?.ToArray(), Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes)));
        }
        public Task<DocumentFingerprint> WriteAsync(string name, ReadOnlyMemory<byte> data, DocumentFingerprint expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Namespaces.Add(name);
            var before = Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes);
            if (before != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
            Bytes = data.ToArray(); Writes++;
            if (FailAfter) { FailAfter = false; throw new InvalidOperationException("synthetic commit receipt loss"); }
            return Task.FromResult(DocumentFingerprint.FromBytes(Bytes));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Runtime : IRuntimeClient
    {
        internal readonly List<string> Calls = [], Invoked = [];
        internal readonly Dictionary<string, JsonObject> Instances = [];
        internal IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> Capabilities = null!;
        internal bool FailLoad, CandidateWrite, FailAfterCandidate;
        internal TaskCompletionSource? Invoking;
        internal JsonElement Value = J(new { url = "https://media.example/audio", headers = new { } });
        private long generation;
        internal async Task<JsonElement> Storage(string operation, JsonObject instance, string key, object? value = null, string site = "wy")
        {
            var p = O(new { grant = instance["grant"]!.GetValue<string>(), installationId = instance["installationId"]!.GetValue<string>(),
                generation = instance["generation"]!.GetValue<long>(), siteId = site, key });
            if (operation == "set") p["value"] = JsonSerializer.SerializeToNode(value);
            return JsonSerializer.SerializeToElement(await Capabilities["platform.musicScript.storage." + operation](J(p), CancellationToken.None));
        }
        private static JsonElement Reply(object value)
        {
            var result = O(value); result["ok"] = true; result["contract"] = "music.script.v1"; return J(result);
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
                    Check(p["permissions"]!["localServices"] is JsonValue flag && !flag.GetValue<bool>(), "LX cannot open local services");
                    if (FailLoad) return J(new { ok = false, contract = "music.script.v1", code = "SCRIPT_EXCEPTION" });
                    string id = p["installationId"]!.GetValue<string>();
                    var instance = O(new { installationId = id, revision = p["revision"]!.GetValue<long>(), generation = ++generation,
                        grant = p["grant"]!.GetValue<string>(), state = "ready",
                        capabilities = new { wy = new { actions = new[] { "musicUrl", "lyric" }, qualitys = new[] { "320k" } } } });
                    if (CandidateWrite) Check((await Storage("set", instance, "candidate", 7)).GetProperty("ok").GetBoolean());
                    if (FailAfterCandidate) return J(new { ok = false, contract = "music.script.v1", code = "SCRIPT_EXCEPTION" });
                    Instances[id] = instance;
                    return Reply(new { instance });
                case "music.script.status": return Reply(new { documentAccess = false, instances = Instances.Values.ToArray() });
                case "music.script.unload":
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

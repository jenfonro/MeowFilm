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

/// <summary>The native adapter of the shared music module. Management semantics, persistence and the
/// musicUrl result contract are covered by tests/windows-music-script.test.mjs against real QuickJS.</summary>
public static class MusicNativeScriptTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native LX: management forwards and selected bytes use one exact source candidate", Forwarding);
        yield return new("Native LX: a closed runtime is an unavailable script service, not missing sources", Unavailable);
        yield return new("Native LX: resolve asks one exact quality of one recording and proves refreshes", ExactResolve);
        yield return new("Native LX: receipt duration survives projection without claiming measured quality", ReceiptDuration);
        yield return new("Native LX: lyric parsing remains native and respects persisted setting", Lyrics);
        yield return new("Native LX: shutdown cancels owned calls and never disposes borrowed owners", Shutdown);
        yield return new("Native LX: original complete bytes and basename reach the runtime", CompleteSource);
        yield return new("Native LX: failed cancelled and timed out imports never replay or publish a later candidate", ImportFailure);
        yield return new("Native LX: shutdown drains an admitted source upload before resuming", ImportShutdown);
        yield return new("Native LX: native application retains an upload until its actual completion", ApplicationImportShutdown);
        yield return new("Native LX: complete large response values remain ordinary business data", FullValues);
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
        internal Fixture() { Library = new(Store); Owner = new(Library, Runtime); }
        public async ValueTask DisposeAsync() { await Owner.DisposeAsync(); await Library.DisposeAsync(); }
    }
    private static async Task Forwarding()
    {
        await using var f = new Fixture();
        f.Runtime.Business = (method, p) => method switch
        {
            "music.script.sources.list" => new { revision = 3, items = Array.Empty<object>() },
            "music.script.sources.remove" => new { removed = true },
            _ => new { item = new { installationId = p["installationId"], received = p } }
        };
        var listed = await Ok(f.Owner, "music.sources.list");
        Check(listed.GetProperty("revision").GetInt32() == 3 && f.Store.Writes == 0, "listing is a module read");
        byte[] source = Encoding.UTF8.GetBytes("// @name 猫\nlx.send('inited', {})");
        var imported = await f.Owner.ImportSourceAsync(source, "猫.js");
        Check(imported.GetProperty("ok").GetBoolean());
        var upload = f.Runtime.Uploads.Single();
        var added = f.Runtime.Requests.Last();
        Check(upload.Module == "music" && Guid.TryParseExact(upload.InstallationId, "D", out _) &&
            upload.SourceRevision > 0 && upload.SourceRevision <= BridgeCodec.MaximumSafeInteger &&
            upload.Source.Equals((ReadOnlyMemory<byte>)source) && upload.Basename == "猫.js");
        Check(added.Method == "music.script.sources.add" && added.Parameters.Count == 2 &&
            added.Parameters["installationId"]!.GetValue<string>() == upload.InstallationId &&
            added.Parameters["sourceRevision"]!.GetValue<long>() == upload.SourceRevision,
            "the commit selects exactly the uploaded installation and revision");
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
        Check(f.Runtime.Uploads.Count == 1, "network download and update remain runtime-owned");
        var updated = await f.Owner.ImportSourceAsync(Encoding.UTF8.GetBytes("// next"), "next.js", Installation);
        Check(updated.GetProperty("ok").GetBoolean());
        var update = f.Runtime.Uploads.Last();
        Check(update.InstallationId == Installation && update.SourceRevision > upload.SourceRevision);
        Check(f.Runtime.Requests.Last().Method == "music.script.sources.update" &&
            f.Runtime.Requests.Last().Parameters["sourceRevision"]!.GetValue<long>() == update.SourceRevision);
        Check(f.Store.Writes == 0 && f.Store.Namespaces.Count == 0, "native documents hold no installation facts");
        await Error(f.Owner, "music.sources.detail", new { installationId = Installation, extra = 1 }, "INVALID_REQUEST");
    }
    private static async Task CompleteSource()
    {
        await using var f = new Fixture();
        f.Runtime.Business = (_, p) => new { item = new { installationId = p["installationId"] } };
        byte[] bytes = Encoding.UTF8.GetBytes("\uFEFF// 猫\r\n" + new string('x', 1024 * 1024 + 17));
        Check((await f.Owner.ImportSourceAsync(bytes, "snapshot.js")).GetProperty("ok").GetBoolean());
        var upload = f.Runtime.Uploads.Single();
        Check(upload.Source.Span.SequenceEqual(bytes) && upload.Source.Equals((ReadOnlyMemory<byte>)bytes) &&
            upload.Basename == "snapshot.js" && f.Runtime.Requests.Count == 1);
        await using var other = new MusicScriptOwner(f.Library, f.Runtime);
        Check((await other.ImportSourceAsync(bytes, "snapshot.js", upload.InstallationId)).GetProperty("ok").GetBoolean());
        Check(f.Runtime.Uploads.Last().SourceRevision > upload.SourceRevision,
            "a replacement owner cannot reuse the preceding exact candidate revision");
        // Invalid source bytes are a runtime contract, never decoded or repaired by the native adapter.
        f.Runtime.Upload = (input, _) =>
        {
            Check(input.Source.Span.SequenceEqual(new byte[] { 0xff, 0xfe }));
            return Task.FromResult(J(new { ok = false, contract = "music.script.v3", code = "SCRIPT_ENCODING" }));
        };
        int calls = f.Runtime.Requests.Count;
        var rejected = await f.Owner.ImportSourceAsync(new byte[] { 0xff, 0xfe }, "invalid.js");
        Check(rejected.GetProperty("code").GetString() == "SCRIPT_ENCODING" && f.Runtime.Requests.Count == calls);
    }
    private static async Task ImportFailure()
    {
        foreach (string failure in new[] { "CANCELLED", "TIMEOUT", "CLOSED", "SCRIPT_ENCODING" })
        {
            await using var f = new Fixture();
            using var cancellation = new CancellationTokenSource();
            f.Runtime.Upload = (_, token) =>
            {
                if (failure == "CANCELLED")
                {
                    cancellation.Cancel();
                    // A completed upload racing caller cancellation must not start installation.
                    return Task.FromResult(Runtime.Reply(new { }));
                }
                if (failure == "SCRIPT_ENCODING") return Task.FromResult(J(new { ok = false, contract = "music.script.v3", code = failure }));
                throw new BridgeException(failure);
            };
            var rejected = await f.Owner.ImportSourceAsync(new byte[] { 1 }, "source.js", cancellationToken: cancellation.Token);
            Check(!rejected.GetProperty("ok").GetBoolean() && rejected.GetProperty("code").GetString() ==
                (failure == "CLOSED" ? "SCRIPT_RUNTIME_UNAVAILABLE" : failure));
            Check(f.Runtime.Uploads.Count == 1 && f.Runtime.Requests.Count == 0 && f.Store.Writes == 0,
                "an uncertain upload is not retried or installed");
        }
        await using var cancelledFixture = new Fixture();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var response = await cancelledFixture.Owner.ImportSourceAsync(new byte[] { 1 }, "source.js", cancellationToken: cancelled.Token);
        Check(response.GetProperty("code").GetString() == "CANCELLED" && cancelledFixture.Runtime.Uploads.Count == 0);
        await using var commit = new Fixture();
        commit.Runtime.Business = (_, _) => throw new BridgeException("TIMEOUT");
        response = await commit.Owner.ImportSourceAsync(new byte[] { 1 }, "source.js");
        Check(response.GetProperty("code").GetString() == "TIMEOUT" && commit.Runtime.Uploads.Count == 1 &&
            commit.Runtime.Requests.Count == 1, "an uncertain installation is not replayed");
    }
    private static async Task ImportShutdown()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.Upload = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };
        var importing = f.Owner.ImportSourceAsync(new byte[] { 1 }, "source.js");
        await entered.Task;
        await f.Owner.PrepareShutdownAsync();
        Check((await importing).GetProperty("code").GetString() == "CANCELLED" && f.Runtime.Requests.Count == 0);
        Check((await f.Owner.ImportSourceAsync(new byte[] { 1 }, "source.js")).GetProperty("code").GetString() == "CLOSED");
        await f.Owner.CancelShutdownPreparationAsync();
        f.Runtime.Upload = null;
        f.Runtime.Business = (_, p) => new { item = new { installationId = p["installationId"] } };
        Check((await f.Owner.ImportSourceAsync(new byte[] { 1 }, "source.js")).GetProperty("ok").GetBoolean());
        Check(f.Runtime.Uploads.Count == 2 && f.Runtime.Uploads[1].SourceRevision > f.Runtime.Uploads[0].SourceRevision);
    }
    private static async Task ApplicationImportShutdown()
    {
        var runtime = new Runtime();
        byte[] source = "// selected snapshot"u8.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Upload = async (input, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            Check(input.Source.Span.SequenceEqual("// selected snapshot"u8),
                "caller cleanup cannot clear bytes still borrowed by the sender");
            token.ThrowIfCancellationRequested();
            return Runtime.Reply(new { });
        };
        var store = new QualityFixture.DocumentStore();
        await using var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(runtime));
        port.Start();
        await using var application = new MusicNativeApplication(store, port, new QualityFixture.RejectHttp());
        async Task<JsonElement> Import()
        {
            try { return await application.ImportSourceAsync(source, "source.js"); }
            finally { CryptographicOperations.ZeroMemory(source); }
        }
        var importing = Import();
        await entered.Task;
        var preparation = application.PrepareShutdownAsync();
        try
        {
            await cancelled.Task;
            Check(runtime.Uploads.Single().Source.Equals((ReadOnlyMemory<byte>)source) &&
                source.AsSpan().SequenceEqual("// selected snapshot"u8),
                "cancellation retains the caller's original snapshot while the sender is active");
            Check(!preparation.IsCompleted && !importing.IsCompleted, "the actual upload keeps application ownership until it settles");
        }
        finally { release.TrySetResult(); }
        await preparation;
        Check((await importing).GetProperty("code").GetString() == "CANCELLED" && runtime.Requests.Count == 0);
        Check(source.All(value => value == 0), "caller cleanup runs after the actual upload settles");
        Check((await application.ImportSourceAsync(new byte[] { 1 }, "source.js")).GetProperty("code").GetString() == "CLOSED");
        await application.CancelShutdownPreparationAsync();
        runtime.Upload = null;
        runtime.Business = (_, p) => new { item = new { installationId = p["installationId"] } };
        Check((await application.ImportSourceAsync(new byte[] { 1 }, "source.js")).GetProperty("ok").GetBoolean());
    }
    private static async Task FullValues()
    {
        await using var f = new Fixture();
        foreach (int size in new[] { 65536, 65537, 512 * 1024, 512 * 1024 + 1, 1024 * 1024 + 1, 8 * 1024 * 1024 + 1 })
        {
            string text = new('x', size);
            f.Runtime.Business = (_, _) => new { revision = 1, items = new[] { new { description = text } } };
            var listed = await Ok(f.Owner, "music.sources.list");
            Check(listed.GetProperty("items")[0].GetProperty("description").GetString() == text);
        }
        Check(f.Runtime.Requests.Count == 6 && f.Runtime.Uploads.Count == 0, "one complete request and response per list");
        foreach (object value in new object[] { "[00:01.00]raw", new { lyric = "[00:01.00]raw", value = false, token = "business data", blob = "business data" } })
        {
            f.Runtime.Business = (_, _) => value;
            var lyrics = await Ok(f.Owner, "music.online.lyrics", new { installationId = Installation, platform = "wy", song = Song() });
            Check(lyrics.GetProperty("state").GetString() == "ready" && lyrics.GetProperty("lines").GetArrayLength() == 1);
        }
        foreach (object? value in new object?[] { null, false, 7, new { value = "[00:01]wrapped" } })
        {
            f.Runtime.Business = (_, _) => value;
            await Error(f.Owner, "music.online.lyrics", new { installationId = Installation, platform = "wy", song = Song() }, "INVALID_LYRIC_RESPONSE");
        }
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
    private static async Task ReceiptDuration()
    {
        await using var f = new Fixture();
        foreach (double? duration in new double?[] { 185, 190, 180.125, null })
        {
            f.Runtime.Business = (method, p) =>
            {
                Check(method == "music.script.resolve" && p["musicInfo"]!["duration"]!.GetValue<double>() == 180 &&
                    p["quality"]!.GetValue<string>() == "320k", "recording duration and request quality are unchanged");
                return new { installationId = Installation, generation = 4, contentHash = new string('a', 64),
                    platform = "wy", quality = "320k", url = "https://media.invalid/duration", headers = new { },
                    expiresAt = (long?)null, duration };
            };
            var media = await Ok(f.Owner, "music.online.resolve", new { installationId = Installation, platform = "wy",
                song = MusicSongFixture.Online("wy", "duration", duration: 180), quality = "320k" });
            Check(duration is { } seconds ? media.GetProperty("duration").GetDouble() == seconds :
                media.GetProperty("duration").ValueKind == JsonValueKind.Null, "known or unknown receipt duration is preserved");
            Check(!media.GetProperty("qualityVerified").GetBoolean() && !media.GetProperty("playbackVerified").GetBoolean() &&
                !media.TryGetProperty("actualQuality", out _) && media.GetProperty("requestedQuality").GetString() == "320k",
                "duration does not turn the request into a measured quality");
        }
    }
    private static async Task Lyrics()
    {
        await using var f = new Fixture();
        f.Runtime.Business = (method, p) =>
        {
            Check(method == "music.script.invoke" && p["operation"]!.GetValue<string>() == "lyric" &&
                p["siteId"]!.GetValue<string>() == "wy");
            return new { lyric = "[00:01.00]Hello", tlyric = "[00:01.00]你好" };
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
        Check(!f.Store.Disposed && f.Runtime.Disposals == 0, "store and runtime remain App-owned");
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
    private sealed record SourceUpload(string Module, string InstallationId, long SourceRevision,
        ReadOnlyMemory<byte> Source, string? Basename);
    private sealed class Runtime : IRuntimeSession
    {
        public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Image requests are not part of this fixture.");

        internal readonly List<Request> Requests = [];
        internal readonly List<SourceUpload> Uploads = [];
        internal Func<string, JsonObject, object?>? Business;
        internal Func<SourceUpload, CancellationToken, Task<JsonElement>>? Upload;
        internal TaskCompletionSource? Entered;
        internal bool Closed;
        internal int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        internal static JsonElement Reply(object? value) => J(new { ok = true, contract = "music.script.v3", value });
        public async Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Closed) throw new BridgeException("CLOSED");
            var input = new SourceUpload(module, installationId, sourceRevision, source, basename);
            Uploads.Add(input);
            return Upload is { } upload ? await upload(input, cancellationToken) : Reply(new { installationId, sourceRevision });
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Closed) throw new BridgeException("CLOSED");
            var p = O(parameters ?? new { });
            Requests.Add(new(method, p));
            if (Entered is { } entered)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return Reply((Business ?? throw new InvalidOperationException("unexpected business call " + method))(method, p));
        }
    }
}

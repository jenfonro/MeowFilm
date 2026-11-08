using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Scripting;

/// <summary>
/// Native adapter of the shared music source module. Installation facts, source
/// files, checks and guest storage are owned and persisted by the module; native
/// code selects recordings, applies the quality policy and parses LX lyrics.
/// </summary>
public sealed class MusicScriptOwner : IAsyncDisposable
{
    private static readonly string[] Commands = ["list", "detail", "add", "download", "edit", "update",
        "rollback", "enable", "disable", "remove", "check"];
    private readonly MusicLibraryOwner library;
    private readonly IRuntimeClient runtime;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object activityGate = new();
    private readonly HashSet<CancellationTokenSource> operations = [];
    private bool closed, preparing;
    private int pending;
    private TaskCompletionSource drained = Complete();
    private Task? preparation, disposal;

    public MusicScriptOwner(MusicLibraryOwner library, IRuntimeClient runtime)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }
    private static TaskCompletionSource Complete() { var result = new TaskCompletionSource(); result.SetResult(); return result; }
    private static JsonObject Parse(object? value)
    {
        var bytes = BridgeCodec.EncodeJSONValue(value ?? new { });
        try { return LJ.Object(JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 67 })); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private string Code(Exception error) => error switch
    {
        MusicLibraryException e => e.Code,
        MusicPlaybackCacheDataException => "CACHE_DATA_INCOMPATIBLE",
        MusicPlaybackCacheCommitException => "CACHE_COMMIT_UNKNOWN",
        // A closed transport while this owner is open: the script service, not the request, is unavailable.
        BridgeException { Code: "CLOSED" } when !closed => "SCRIPT_RUNTIME_UNAVAILABLE",
        BridgeException when !closed && runtime is IRuntimeRecovery
            { State: RuntimeConnectionState.Failed or RuntimeConnectionState.Recovering } => "SCRIPT_RUNTIME_UNAVAILABLE",
        BridgeException e => e.Code,
        DocumentStoreException e => e.Error == DocumentStoreError.Unavailable ? "STORAGE_UNAVAILABLE" : "STORAGE",
        OperationCanceledException => "CANCELLED",
        JsonException or FormatException => "INVALID_REQUEST",
        _ => "SCRIPT_EXCEPTION"
    };
    private static JsonElement Okay(JsonObject result)
    {
        result["ok"] = true;
        return BridgeCodec.DecodeJSONValue(BridgeCodec.EncodeJSONValue(result));
    }
    private static JsonElement Fail(string code) => JsonSerializer.SerializeToElement(new { ok = false, code });
    private CancellationTokenSource Admit(CancellationToken token)
    {
        lock (activityGate)
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            operations.Add(operation);
            return operation;
        }
    }
    private void Finish(CancellationTokenSource operation)
    {
        lock (activityGate)
        {
            operations.Remove(operation);
            if (--pending == 0) drained.TrySetResult();
        }
        operation.Dispose();
    }

    public async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? operation = null;
        try
        {
            var input = Parse(parameters);
            operation = Admit(cancellationToken);
            var token = operation.Token;
            token.ThrowIfCancellationRequested();
            if (method == "music.online.song") return Okay(Song(input));
            if (method == "music.online.resolve") return Okay(await ResolveAsync(input, token).ConfigureAwait(false));
            if (method == "music.online.lyrics") return Okay(await LyricsAsync(input, token).ConfigureAwait(false));
            const string prefix = "music.sources.";
            if (method.StartsWith(prefix, StringComparison.Ordinal) && Commands.Contains(method[prefix.Length..]))
                return Okay(await SourcesAsync(method[prefix.Length..], input, token).ConfigureAwait(false));
            throw new MusicLibraryException("METHOD_UNAVAILABLE");
        }
        catch (Exception error) { return Fail(Code(error)); }
        finally { if (operation is not null) Finish(operation); }
    }

    /// <summary>Imports one caller-owned selected-file snapshot, or updates the specified
    /// installation. Borrows source until the task settles, including cancellation.
    /// The runtime owns the complete candidate and installation facts.</summary>
    public async Task<JsonElement> ImportSourceAsync(ReadOnlyMemory<byte> source, string basename,
        string? installationId = null, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? operation = null;
        try
        {
            operation = Admit(cancellationToken);
            var token = operation.Token;
            token.ThrowIfCancellationRequested();
            string id = installationId ?? Guid.NewGuid().ToString("D");
            long sourceRevision = RuntimeSourceRevision.Next();
            ScriptValue(await runtime.UploadSourceAsync("music", id, sourceRevision, source, basename, token)
                .ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            return Okay(await ScriptAsync(installationId is null ? "sources.add" : "sources.update",
                new { installationId = id, sourceRevision }, token).ConfigureAwait(false));
        }
        catch (Exception error) { return Fail(Code(error)); }
        finally { if (operation is not null) Finish(operation); }
    }

    private Task<JsonObject> SourcesAsync(string command, JsonObject p, CancellationToken token)
    {
        switch (command)
        {
            case "list": LJ.Fields(p, ""); break;
            case "download": LJ.Fields(p, "url", "url"); LJ.Text(p["url"], 2048); break;
            case "edit": LJ.Fields(p, "installationId name url", "installationId"); break;
            case "add": LJ.Fields(p, "installationId sourceRevision", "installationId sourceRevision"); break;
            case "update": LJ.Fields(p, "installationId sourceRevision", "installationId"); break;
            default: LJ.Fields(p, "installationId", "installationId"); break;
        }
        return ScriptAsync("sources." + command, p, token);
    }

    private static JsonObject Song(JsonObject p)
    {
        LJ.Fields(p, "platform musicInfo title artist album duration", "platform musicInfo title");
        string platform = LJ.Text(p["platform"]); LJ.Require(NativeMusicSong.Platforms.Contains(platform));
        var info = LJ.Object(p["musicInfo"]).DeepClone().AsObject();
        var song = NativeMusicSong.CreateOnline(platform, info, LJ.Text(p["title"], 512),
            LJ.S(p["artist"]), LJ.S(p["album"]), LJ.N(p["duration"]));
        return LJ.Obj(("song", song));
    }

    /// <summary>One musicUrl request for one exact quality. Ordering, fallback and
    /// caching belong to the playback selection session, not to this adapter.</summary>
    private async Task<JsonObject> ResolveAsync(JsonObject p, CancellationToken external)
    {
        LJ.Fields(p, "installationId platform song sourceKey quality qualityPolicySignature qualityCacheSignature manualSource",
            "installationId platform song quality");
        LJ.Require(p["manualSource"] is null || LJ.IsBool(p["manualSource"]));
        bool manualSource = LJ.B(p["manualSource"]);
        string id = LJ.Text(p["installationId"], 128), platform = LJ.Text(p["platform"]);
        LJ.Require(NativeMusicSong.Platforms.Contains(platform));
        string quality = LJ.Text(p["quality"]);
        LJ.Require(MusicQualityPolicy.LxTypes.Contains(quality), "QUALITY_UNAVAILABLE");
        var song = NativeMusicSong.Normalize(p["song"]);
        var selected = SelectRecording(song, platform, p["sourceKey"]);
        bool unknown = !selected.Data.ContainsKey("types") && !LJ.B(selected.Data["qualityKnown"]);
        LJ.Require(unknown || (selected.Data["types"] as JsonArray ?? []).OfType<JsonObject>()
            .Any(type => LJ.S(type["type"]) == quality), "RECORDING_QUALITY_UNAVAILABLE");
        using var policy = await library.AcquireQualityAsync(external).ConfigureAwait(false);
        if (p.ContainsKey("qualityPolicySignature"))
            LJ.Require(LJ.Text(p["qualityPolicySignature"], 64) == policy.Signature, "QUALITY_POLICY_CHANGED");
        LJ.Require(manualSource || MusicQualityPolicy.Enabled(quality, policy.Policy), "QUALITY_DISABLED");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(external, policy.Token, deadline.Token);
        var token = timed.Token;
        try
        {
            var result = await ScriptAsync("resolve", new { installationId = id, platform, quality,
                musicInfo = ScriptInfo(selected.Data) }, token).ConfigureAwait(false);
            await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
            LJ.Require(LJ.S(result["installationId"]) == id && LJ.S(result["platform"]) == platform &&
                LJ.S(result["quality"]) == quality, "TARGET_CHANGED");
            long generation = LJ.I(result["generation"], 1);
            string signature = LJ.Digest(LJ.Obj(("contract", "music.quality.cache.v2"),
                ("policy", policy.Signature), ("installation", id), ("platform", platform),
                ("content", result["contentHash"]), ("sourceKey", selected.Key), ("recording", selected.Data),
                ("songId", song["songId"]), ("duration", song["duration"]), ("quality", quality)));
            if (p.ContainsKey("qualityCacheSignature"))
                LJ.Require(LJ.Text(p["qualityCacheSignature"], 64) == signature, "QUALITY_CACHE_CHANGED");
            return LJ.Obj(("url", result["url"]), ("headers", result["headers"]?.DeepClone() ?? new JsonObject()),
                ("songId", song["songId"]), ("sourceKey", selected.Key), ("installationId", id),
                ("platform", platform), ("generation", generation), ("contentHash", result["contentHash"]),
                ("quality", quality), ("requestedQuality", quality), ("expiresAt", result["expiresAt"]),
                ("duration", result["duration"]),
                ("playbackVerified", false), ("qualityVerified", false),
                ("qualityPolicySignature", policy.Signature), ("qualityCacheSignature", signature));
        }
        catch (Exception error) when (deadline.IsCancellationRequested && !external.IsCancellationRequested &&
            Code(error) == "CANCELLED")
        { throw new MusicLibraryException("TIMEOUT"); }
    }

    private async Task<JsonObject> LyricsAsync(JsonObject p, CancellationToken external)
    {
        LJ.Fields(p, "installationId platform song sourceKey", "installationId platform song");
        string id = LJ.Text(p["installationId"], 128), platform = LJ.Text(p["platform"]);
        LJ.Require(NativeMusicSong.Platforms.Append("local").Contains(platform));
        var song = NativeMusicSong.Normalize(p["song"]);
        var setting = await library.ExecuteAsync("music.settings.get", cancellationToken: external).ConfigureAwait(false);
        var settings = LJ.Object(JsonNode.Parse(setting.GetRawText()));
        LJ.Require(LJ.B(settings["ok"]), LJ.S(settings["code"]) is { Length: > 0 } code ? code : "STORAGE");
        if (!LJ.B(settings["settings"]!["lyrics"])) return LJ.Obj(("state", "disabled"), ("lines", new JsonArray()));
        var selected = SelectRecording(song, platform, p["sourceKey"]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(external, deadline.Token);
        try
        {
            var raw = await ScriptValueAsync("invoke", new { installationId = id, siteId = platform, operation = "lyric",
                payload = new JsonObject { ["musicInfo"] = ScriptInfo(selected.Data) } }, timed.Token).ConfigureAwait(false);
            LJ.Require(raw is JsonValue v && v.TryGetValue<string>(out _) ||
                raw is JsonObject o && o["lyric"] is JsonValue l && l.TryGetValue<string>(out _), "INVALID_LYRIC_RESPONSE");
            string text = raw is JsonValue ? LJ.S(raw) : LJ.S(raw?["lyric"]);
            string translation = raw is JsonObject lyricsResult ? LJ.S(lyricsResult["tlyric"]) : "";
            var lines = MusicLyricTiming.Parse(text, translation, LJ.N(song["duration"]));
            return LJ.Obj(("state", lines.GetArrayLength() > 0 ? "ready" : "none"), ("lines", lines));
        }
        catch (Exception error) when (deadline.IsCancellationRequested && !external.IsCancellationRequested &&
            Code(error) == "CANCELLED")
        { throw new MusicLibraryException("TIMEOUT"); }
    }

    private static JsonObject ScriptInfo(JsonObject recording)
    {
        var info = recording.DeepClone().AsObject();
        if (info["duration"] is { } duration)
            info["duration"] = Math.Max(0, LJ.N(duration));
        return info;
    }
    private static (string Key, JsonObject Data) SelectRecording(JsonObject song, string platform, JsonNode? key)
    {
        if (platform == "local")
        {
            LJ.Require(!NativeMusicSong.IsOnline(song), "TARGET_CHANGED");
            return (LJ.Text(song["songId"]), new JsonObject());
        }
        var records = LJ.Array(song["sources"]).OfType<JsonObject>()
            .Where(s => LJ.S(s["source"]) == platform && (key is null || LJ.S(s["sourceKey"]) == LJ.Text(key))).ToArray();
        LJ.Require(records.Length == 1, records.Length > 0 ? "TARGET_REQUIRED" : "RECORDING_UNAVAILABLE");
        return (LJ.Text(records[0]["sourceKey"]), NativeMusicSong.SourceData(records[0]).DeepClone().AsObject());
    }

    private async Task<JsonObject> ScriptAsync(string operation, object parameters, CancellationToken token)
        => LJ.Object(await ScriptValueAsync(operation, parameters, token).ConfigureAwait(false));

    private async Task<JsonNode?> ScriptValueAsync(string operation, object parameters, CancellationToken token)
    {
        var value = ScriptValue(await runtime.CallAsync("music.script." + operation, parameters, token).ConfigureAwait(false));
        return JsonNode.Parse(value.GetRawText(), documentOptions: new() { MaxDepth = 67 });
    }

    private static JsonElement ScriptValue(JsonElement reply)
    {
        if (!reply.GetProperty("ok").GetBoolean())
            throw new MusicLibraryException(reply.GetProperty("code").GetString()!);
        return reply.GetProperty("value");
    }

    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (activityGate)
        {
            if (preparation is null)
            {
                preparing = true;
                foreach (var operation in operations.ToArray()) operation.Cancel();
                preparation = drained.Task;
            }
            return preparation.WaitAsync(cancellationToken);
        }
    }
    public async Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (activityGate) { LJ.Require(!closed, "CLOSED"); task = preparation; }
        if (task is not null) await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (activityGate) { LJ.Require(!closed, "CLOSED"); preparation = null; preparing = false; }
    }
    public ValueTask DisposeAsync()
    {
        lock (activityGate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        lock (activityGate)
        {
            closed = true; lifetime.Cancel();
            foreach (var operation in operations.ToArray()) operation.Cancel();
        }
        try { await PrepareShutdownAsync().ConfigureAwait(false); }
        finally { lifetime.Dispose(); }
        // The App owns the runtime; its shutdown closes the shared module and every instance.
    }
}

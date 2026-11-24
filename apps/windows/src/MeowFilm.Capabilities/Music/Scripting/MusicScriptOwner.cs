using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Runtime;
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

    private static JsonElement Parse(object? value)
    {
        var bytes = BridgeCodec.EncodeJSONValue(value ?? new { });
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 67 });
        MusicRules.Require(doc.RootElement.ValueKind == JsonValueKind.Object);
        return doc.RootElement.Clone();
    }

    private string Code(Exception error) => error switch
    {
        MusicLibraryException e => e.Code,
        // A closed transport while this owner is open: the script service, not the request, is unavailable.
        BridgeException { Code: "CLOSED" } when !closed => "SCRIPT_RUNTIME_UNAVAILABLE",
        BridgeException when !closed && runtime is IRuntimeRecovery
            { State: RuntimeConnectionState.Failed or RuntimeConnectionState.Recovering } => "SCRIPT_RUNTIME_UNAVAILABLE",
        BridgeException e => e.Code,
        StorageException e => e.Error == StorageError.Unavailable ? "STORAGE_UNAVAILABLE" : "STORAGE",
        OperationCanceledException => "CANCELLED",
        JsonException or FormatException => "INVALID_REQUEST",
        _ => "SCRIPT_EXCEPTION"
    };
    private static JsonElement Okay(JsonElement result) =>
        MusicWireCodec.BuildObject(w =>
        {
            w.WriteBoolean("ok", true);
            foreach (var prop in result.EnumerateObject())
                if (prop.Name != "ok")
                    prop.WriteTo(w);
        });

    private static JsonElement Fail(string code) => MusicWireCodec.Fail(code);
    private CancellationTokenSource Admit(CancellationToken token)
    {
        lock (activityGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        }
    }

    private void Finish(CancellationTokenSource operation)
    {
        lock (activityGate)
        {
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
            if (method == "music.online.song") return Song(input);
            if (method == "music.online.resolve") return await ResolveAsync(input, token).ConfigureAwait(false);
            if (method == "music.online.lyrics") return await LyricsAsync(input, token).ConfigureAwait(false);
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

    private Task<JsonElement> SourcesAsync(string command, JsonElement p, CancellationToken token)
    {
        switch (command)
        {
            case "list": break;
            case "download": MusicWireCodec.RequireFields(p, "url"); _ = MusicWireCodec.RequireIdProp(p, "url", null); break;
            case "edit": MusicWireCodec.RequireFields(p, "installationId"); break;
            case "add": MusicWireCodec.RequireFields(p, "installationId sourceRevision"); break;
            case "update": MusicWireCodec.RequireFields(p, "installationId"); break;
            default: MusicWireCodec.RequireFields(p, "installationId"); break;
        }
        return ScriptAsync("sources." + command, p, token);
    }

    private static JsonElement Song(JsonElement p)
    {
        string platform = MusicWireCodec.RequireIdProp(p, "platform");
        MusicRules.Require(NativeMusicSong.Platforms.Contains(platform));
        var mi = MusicWireCodec.RequireObjectProp(p, "musicInfo");
        var src = MusicWireCodec.ReadSourceFromMusicInfo("", platform, mi);
        var song = NativeMusicSong.CreateOnline(platform, src,
            MusicWireCodec.Str(p, "title"),
            MusicWireCodec.Str(p, "artist"),
            MusicWireCodec.Str(p, "album"),
            MusicWireCodec.NumProp(p, "duration"),
            src.Img ?? "");
        return MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("song");
            MusicWireCodec.WriteSong(w, song);
        });
    }

    /// <summary>One musicUrl request for one exact quality. Ordering, fallback and
    /// caching belong to the playback selection session, not to this adapter.</summary>
    private async Task<JsonElement> ResolveAsync(JsonElement p, CancellationToken external)
    {
        MusicWireCodec.RequireFields(p, "installationId platform song quality");
        bool manualSource = MusicWireCodec.OptionalBoolProp(p, "manualSource");
        string id = MusicWireCodec.RequireIdProp(p, "installationId", 128);
        string platform = MusicWireCodec.RequireIdProp(p, "platform");
        MusicRules.Require(NativeMusicSong.Platforms.Contains(platform));
        string quality = MusicWireCodec.RequireIdProp(p, "quality");
        MusicRules.Require(MusicQualityPolicy.LxTypes.Contains(quality), "QUALITY_UNAVAILABLE");
        var song = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(p.GetProperty("song")));
        using var policy = await library.AcquireQualityAsync(external).ConfigureAwait(false);
        if (MusicWireCodec.Has(p, "qualityPolicySignature"))
            MusicRules.Require(MusicWireCodec.RequireIdProp(p, "qualityPolicySignature", 64) == policy.Signature, "QUALITY_POLICY_CHANGED");
        MusicRules.Require(manualSource || MusicQualityPolicy.Enabled(quality, policy.Policy), "QUALITY_DISABLED");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(external, policy.Token, deadline.Token);
        var token = timed.Token;
        try
        {
            if (library.SongSnapshot(song.SongId) is { } current)
                song = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(current));
            string? requestedKey = MusicWireCodec.TryGet(p, "sourceKey", out var skEl) && skEl.ValueKind != JsonValueKind.Null
                ? MusicWireCodec.RequireStr(skEl) : null;
            var selected = SelectRecording(song, platform, requestedKey);
            MusicRules.Require(NativeMusicSong.HasRecordingQuality(selected.Source!, quality), "RECORDING_QUALITY_UNAVAILABLE");
            var result = await ScriptAsync("resolve", new
            {
                installationId = id, platform, quality,
                musicInfo = MusicWireCodec.MusicInfoToElement(selected.Source!, clampNonNegativeDuration: true)
            }, token).ConfigureAwait(false);
            await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
            MusicRules.Require(MusicWireCodec.Str(result, "installationId") == id &&
                MusicWireCodec.Str(result, "platform") == platform &&
                MusicWireCodec.Str(result, "quality") == quality, "TARGET_CHANGED");
            long generation = MusicWireCodec.RequireIntProp(result, "generation", 1);
            string signature = MusicRules.CanonicalDigest(MusicWireCodec.BuildObject(w =>
            {
                w.WriteString("contract", "music.quality.cache.v1");
                w.WriteString("policy", policy.Signature);
                w.WriteString("installation", id);
                w.WriteString("platform", platform);
                w.WritePropertyName("content");
                if (result.TryGetProperty("contentHash", out var ch)) ch.WriteTo(w); else w.WriteNullValue();
                w.WriteString("sourceKey", selected.Key);
                w.WritePropertyName("recording");
                MusicWireCodec.WriteMusicInfo(w, selected.Source!);
                w.WriteString("songId", song.SongId);
                if (song.Duration is { } dur) w.WriteNumber("duration", dur); else w.WriteNull("duration");
                w.WriteString("quality", quality);
            }));
            if (MusicWireCodec.Has(p, "qualityCacheSignature"))
                MusicRules.Require(MusicWireCodec.RequireIdProp(p, "qualityCacheSignature", 64) == signature, "QUALITY_CACHE_CHANGED");
            return MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("url");
                if (result.TryGetProperty("url", out var url)) url.WriteTo(w); else w.WriteNullValue();
                w.WritePropertyName("headers");
                if (result.TryGetProperty("headers", out var hdr) && hdr.ValueKind == JsonValueKind.Object) hdr.WriteTo(w);
                else { w.WriteStartObject(); w.WriteEndObject(); }
                w.WriteString("songId", song.SongId);
                w.WriteString("sourceKey", selected.Key);
                w.WriteString("installationId", id);
                w.WriteString("platform", platform);
                w.WriteNumber("generation", generation);
                w.WritePropertyName("contentHash");
                if (result.TryGetProperty("contentHash", out var hash)) hash.WriteTo(w); else w.WriteNullValue();
                w.WriteString("quality", quality);
                w.WriteString("requestedQuality", quality);
                w.WritePropertyName("expiresAt");
                if (result.TryGetProperty("expiresAt", out var exp)) exp.WriteTo(w); else w.WriteNullValue();
                w.WritePropertyName("duration");
                if (result.TryGetProperty("duration", out var d)) d.WriteTo(w); else w.WriteNullValue();
                w.WriteBoolean("playbackVerified", false);
                w.WriteBoolean("qualityVerified", false);
                w.WriteString("qualityPolicySignature", policy.Signature);
                w.WriteString("qualityCacheSignature", signature);
            });
        }
        catch (Exception error) when (deadline.IsCancellationRequested && !external.IsCancellationRequested &&
            Code(error) == "CANCELLED")
        { throw new MusicLibraryException("TIMEOUT"); }
    }

    private async Task<JsonElement> LyricsAsync(JsonElement p, CancellationToken external)
    {
        MusicWireCodec.RequireFields(p, "installationId platform song");
        string id = MusicWireCodec.RequireIdProp(p, "installationId", 128);
        string platform = MusicWireCodec.RequireIdProp(p, "platform");
        MusicRules.Require(NativeMusicSong.Platforms.Append("local").Contains(platform));
        var song = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(p.GetProperty("song")));
        var setting = await library.ExecuteAsync("music.settings.get", cancellationToken: external).ConfigureAwait(false);
        MusicRules.Require(MusicWireCodec.Bool(setting, "ok"),
            MusicWireCodec.Str(setting, "code") is { Length: > 0 } code ? code : "STORAGE");
        if (!setting.GetProperty("settings").GetProperty("lyrics").GetBoolean())
            return MusicWireCodec.Ok(w =>
            {
                w.WriteString("state", "disabled");
                w.WritePropertyName("lines"); w.WriteStartArray(); w.WriteEndArray();
            });
        string? requestedKey = MusicWireCodec.TryGet(p, "sourceKey", out var skEl) && skEl.ValueKind != JsonValueKind.Null
            ? MusicWireCodec.RequireStr(skEl) : null;
        var selected = SelectRecording(song, platform, requestedKey);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(external, deadline.Token);
        try
        {
            var infoEl = selected.Source is null
                ? MusicWireCodec.BuildObject(_ => { })
                : MusicWireCodec.MusicInfoToElement(selected.Source, clampNonNegativeDuration: true);
            var raw = await ScriptValueAsync("invoke", new
            {
                installationId = id, siteId = platform, operation = "lyric",
                payload = new { musicInfo = infoEl }
            }, timed.Token).ConfigureAwait(false);
            string text;
            string translation = "";
            if (raw.ValueKind == JsonValueKind.String)
                text = raw.GetString()!;
            else if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("lyric", out var lyr) && lyr.ValueKind == JsonValueKind.String)
            {
                text = lyr.GetString()!;
                translation = MusicWireCodec.Str(raw, "tlyric");
            }
            else throw new MusicLibraryException("INVALID_LYRIC_RESPONSE");
            var lines = MusicLyricTiming.ParseLinesRecord(text, translation, song.Duration ?? 0);
            return MusicWireCodec.Ok(w =>
            {
                w.WriteString("state", lines.Length > 0 ? "ready" : "none");
                w.WritePropertyName("lines");
                MusicWireCodec.WriteLyricLines(w, lines);
            });
        }
        catch (Exception error) when (deadline.IsCancellationRequested && !external.IsCancellationRequested &&
            Code(error) == "CANCELLED")
        { throw new MusicLibraryException("TIMEOUT"); }
    }

    private static (string Key, MusicSongSourceRecord? Source) SelectRecording(MusicSongRecord song, string platform, string? selectedKey)
    {
        if (platform == "local")
        {
            MusicRules.Require(!NativeMusicSong.IsOnline(song), "TARGET_CHANGED");
            return (MusicRules.RequireId(song.SongId), null);
        }
        var records = song.Sources
            .Where(s => s.Platform == platform && (selectedKey is null || s.SourceKey == selectedKey)).ToArray();
        MusicRules.Require(records.Length == 1, records.Length > 0 ? "TARGET_REQUIRED" : "RECORDING_UNAVAILABLE");
        return (records[0].SourceKey, records[0]);
    }

    private async Task<JsonElement> ScriptAsync(string operation, object parameters, CancellationToken token)
    {
        var value = await ScriptValueAsync(operation, parameters, token).ConfigureAwait(false);
        MusicRules.Require(value.ValueKind == JsonValueKind.Object);
        return value;
    }

    private async Task<JsonElement> ScriptValueAsync(string operation, object parameters, CancellationToken token)
    {
        var reply = await runtime.CallAsync("music.script." + operation, parameters, token).ConfigureAwait(false);
        return ScriptValue(reply).Clone();
    }

    private static JsonElement ScriptValue(JsonElement reply)
    {
        if (!reply.GetProperty("ok").GetBoolean())
            throw new MusicLibraryException(reply.GetProperty("code").GetString()!);
        return reply.GetProperty("value");
    }

    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        bool cancel = false;
        Task task;
        lock (activityGate)
        {
            if (preparation is null)
            {
                preparing = true;
                cancel = true;
                preparation = drained.Task;
            }
            task = preparation;
        }
        if (cancel) CancelLifetime();
        return task.WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (activityGate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        lock (activityGate) closed = true;
        CancelLifetime();
        try { await PrepareShutdownAsync().ConfigureAwait(false); }
        finally { lifetime.Dispose(); }
        // The App owns the runtime; its shutdown closes the shared module and every instance.
    }
    private void CancelLifetime()
    {
        try { lifetime.Cancel(); }
        catch (AggregateException) { }
        catch (ObjectDisposedException) { }
    }
}

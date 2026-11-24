using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Scripting;

public sealed record MusicScriptSiteCapability(string[] Actions, string[] Qualities)
{
    public bool SupportsAction(string action) => Actions.Contains(action, StringComparer.Ordinal);
    public bool SupportsQuality(string quality) => Qualities.Contains(quality, StringComparer.Ordinal);
    public bool ContentEquals(MusicScriptSiteCapability other) =>
        Actions.SequenceEqual(other.Actions, StringComparer.Ordinal) &&
        Qualities.SequenceEqual(other.Qualities, StringComparer.Ordinal);
}

public sealed record MusicScriptOrigin(string Kind, string? Host, string? Url);

public sealed record MusicScriptCheckSiteResult(string State, string? Quality, string? Sample, string? Code);

public sealed record MusicScriptCheckInfo(
    long CheckedAt,
    string? Failure,
    string Summary,
    IReadOnlyDictionary<string, MusicScriptCheckSiteResult> Sites);

public sealed record MusicScriptSourceItem(
    string InstallationId,
    string Name,
    string Version,
    bool Enabled,
    string State,
    MusicScriptOrigin Origin,
    bool RollbackAvailable,
    string ExecutionHash,
    IReadOnlyDictionary<string, MusicScriptSiteCapability>? Sites,
    IReadOnlyDictionary<string, MusicScriptSiteCapability>? Capabilities,
    MusicScriptCheckInfo? Check);

public sealed record MusicScriptSourceSnapshot(long Revision, MusicScriptSourceItem[] Items);

public sealed record MusicScriptSourceDetail(MusicScriptSourceItem Item);

public sealed record MusicScriptCheckResult(MusicScriptSourceItem Item);

public sealed record MusicResolvedScriptAudio(
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string SongId,
    string SourceKey,
    string InstallationId,
    string Platform,
    long Generation,
    string? ContentHash,
    string Quality,
    string RequestedQuality,
    long? ExpiresAt,
    double? Duration,
    bool PlaybackVerified,
    bool QualityVerified,
    string QualityPolicySignature,
    string QualityCacheSignature);

/// <summary>
/// Native adapter of the shared music source module. Installation facts, source
/// files, checks and guest storage are owned and persisted by the module; native
/// code selects recordings, applies the quality policy and parses LX lyrics.
/// </summary>
public sealed class MusicScriptOwner : IAsyncDisposable
{
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

    private async Task<T> RunAdmittedAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        CancellationTokenSource? operation = null;
        try
        {
            operation = Admit(cancellationToken);
            var token = operation.Token;
            token.ThrowIfCancellationRequested();
            return await action(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MusicLibraryException) { throw; }
        catch (Exception error) { throw new MusicLibraryException(Code(error)); }
        finally { if (operation is not null) Finish(operation); }
    }

    public Task<MusicScriptSourceSnapshot> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token => ReadSnapshot(await ScriptAsync("sources.list", new { }, token).ConfigureAwait(false)), cancellationToken);

    public Task<MusicScriptSourceDetail> GetSourceDetailAsync(string installationId, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(MusicRules.IsValidId(installationId, 128));
            return ReadDetail(await ScriptAsync("sources.detail", new { installationId }, token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<MusicScriptSourceDetail> DownloadSourceAsync(string url, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(MusicRules.IsValidId(url, null));
            return ReadDetail(await ScriptAsync("sources.download", new { url }, token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<MusicScriptSourceDetail> EditSourceAsync(string installationId, string? name = null, string? url = null,
        bool includeUrl = false, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(MusicRules.IsValidId(installationId, 128));
            var payload = new Dictionary<string, object?> { ["installationId"] = installationId };
            if (name is not null) payload["name"] = name;
            if (includeUrl) payload["url"] = url;
            return ReadDetail(await ScriptAsync("sources.edit", payload, token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<MusicScriptSourceDetail> UpdateSourceAsync(string installationId, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(MusicRules.IsValidId(installationId, 128));
            return ReadDetail(await ScriptAsync("sources.update", new { installationId }, token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<MusicScriptSourceDetail> RollbackSourceAsync(string installationId, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(MusicRules.IsValidId(installationId, 128));
            return ReadDetail(await ScriptAsync("sources.rollback", new { installationId }, token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<MusicScriptSourceDetail> SetSourceEnabledAsync(string installationId, bool enabled, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(MusicRules.IsValidId(installationId, 128));
            return ReadDetail(await ScriptAsync(enabled ? "sources.enable" : "sources.disable", new { installationId }, token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<bool> RemoveSourceAsync(string installationId, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(MusicRules.IsValidId(installationId, 128));
            var result = await ScriptAsync("sources.remove", new { installationId }, token).ConfigureAwait(false);
            return result.TryGetProperty("removed", out var removed) && removed.ValueKind == JsonValueKind.True;
        }, cancellationToken);

    public Task<MusicScriptCheckResult> CheckSourceAsync(string installationId, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            MusicRules.Require(MusicRules.IsValidId(installationId, 128));
            var result = await ScriptAsync("sources.check", new { installationId }, token).ConfigureAwait(false);
            MusicRules.Require(result.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object, "INVALID_RESPONSE");
            return new MusicScriptCheckResult(ReadItem(item));
        }, cancellationToken);

    /// <summary>Imports one caller-owned selected-file snapshot, or updates the specified
    /// installation. Borrows source until the task settles, including cancellation.
    /// The runtime owns the complete candidate and installation facts.</summary>
    public Task<MusicScriptSourceDetail> ImportSourceAsync(ReadOnlyMemory<byte> source, string basename,
        string? installationId = null, CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async token =>
        {
            string id = installationId ?? Guid.NewGuid().ToString("D");
            long sourceRevision = RuntimeSourceRevision.Next();
            ScriptValue(await runtime.UploadSourceAsync("music", id, sourceRevision, source, basename, token)
                .ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            return ReadDetail(await ScriptAsync(installationId is null ? "sources.add" : "sources.update",
                new { installationId = id, sourceRevision }, token).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>One musicUrl request for one exact quality. Ordering, fallback and
    /// caching belong to the playback selection session, not to this adapter.</summary>
    public Task<MusicResolvedScriptAudio> ResolveOnlineAsync(
        string installationId,
        string platform,
        MusicSongRecord songRecord,
        string quality,
        string? sourceKey = null,
        bool manualSource = false,
        string? qualityPolicySignature = null,
        string? qualityCacheSignature = null,
        CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async external =>
        {
            string id = MusicRules.RequireId(installationId, 128);
            MusicRules.Require(NativeMusicSong.Platforms.Contains(platform));
            MusicRules.Require(MusicQualityPolicy.LxTypes.Contains(quality), "QUALITY_UNAVAILABLE");
            var song = NativeMusicSong.Normalize(songRecord);
            using var policy = await library.AcquireQualityAsync(external).ConfigureAwait(false);
            if (qualityPolicySignature is not null)
                MusicRules.Require(MusicRules.RequireId(qualityPolicySignature, 64) == policy.Signature, "QUALITY_POLICY_CHANGED");
            MusicRules.Require(manualSource || MusicQualityPolicy.Enabled(quality, policy.Policy), "QUALITY_DISABLED");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(external, policy.Token, deadline.Token);
            var token = timed.Token;
            try
            {
                if (library.ReadSongRecord(song.SongId) is { } current)
                    song = current;
                var selected = SelectRecording(song, platform, sourceKey);
                MusicRules.Require(NativeMusicSong.HasRecordingQuality(selected.Source!, quality), "RECORDING_QUALITY_UNAVAILABLE");
                var result = await ScriptAsync("resolve", new
                {
                    installationId = id, platform, quality,
                    musicInfo = MusicNodeSongCodec.MusicInfoToElement(selected.Source!, clampNonNegativeDuration: true)
                }, token).ConfigureAwait(false);
                await library.CheckQualityAsync(policy, token).ConfigureAwait(false);
                MusicRules.Require(ReadString(result, "installationId") == id &&
                    ReadString(result, "platform") == platform &&
                    ReadString(result, "quality") == quality, "TARGET_CHANGED");
                long generation = ReadInt64(result, "generation", 1);
                string signature = MusicRules.CanonicalDigest(MusicNodeSongCodec.BuildObject(w =>
                {
                    w.WriteString("contract", "music.quality.cache.v1");
                    w.WriteString("policy", policy.Signature);
                    w.WriteString("installation", id);
                    w.WriteString("platform", platform);
                    w.WritePropertyName("content");
                    if (result.TryGetProperty("contentHash", out var ch)) ch.WriteTo(w); else w.WriteNullValue();
                    w.WriteString("sourceKey", selected.Key);
                    w.WritePropertyName("recording");
                    MusicNodeSongCodec.WriteMusicInfo(w, selected.Source!);
                    w.WriteString("songId", song.SongId);
                    if (song.Duration is { } dur) w.WriteNumber("duration", dur); else w.WriteNull("duration");
                    w.WriteString("quality", quality);
                }));
                if (qualityCacheSignature is not null)
                    MusicRules.Require(MusicRules.RequireId(qualityCacheSignature, 64) == signature, "QUALITY_CACHE_CHANGED");
                string rawUrl = ReadString(result, "url");
                Uri? uri = null;
                MusicRules.Require(
                    rawUrl.Length > 0 &&
                    Uri.TryCreate(rawUrl, UriKind.Absolute, out uri) &&
                    uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0,
                    "INVALID_MEDIA_CANDIDATE");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (result.TryGetProperty("headers", out var hdr) && hdr.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in hdr.EnumerateObject())
                    {
                        MusicRules.Require(prop.Value.ValueKind == JsonValueKind.String, "INVALID_MEDIA_CANDIDATE");
                        headers[prop.Name] = prop.Value.GetString()!;
                    }
                }
                long? expiresAt = null;
                if (result.TryGetProperty("expiresAt", out var exp) && exp.ValueKind != JsonValueKind.Null)
                {
                    long expVal = 0;
                    MusicRules.Require(exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out expVal), "INVALID_MEDIA_CANDIDATE");
                    MusicRules.Require(expVal > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "EXPIRED_MEDIA_CANDIDATE");
                    expiresAt = expVal;
                }
                double? duration = null;
                if (result.TryGetProperty("duration", out var d) && d.ValueKind != JsonValueKind.Null)
                {
                    double durVal = 0;
                    MusicRules.Require(d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out durVal) &&
                        double.IsFinite(durVal) && durVal >= 0, "INVALID_MEDIA_CANDIDATE");
                    duration = durVal;
                }
                string? contentHash = result.TryGetProperty("contentHash", out var hash) && hash.ValueKind == JsonValueKind.String
                    ? hash.GetString() : null;
                return new MusicResolvedScriptAudio(
                    Uri: uri!,
                    Headers: headers,
                    SongId: song.SongId,
                    SourceKey: selected.Key,
                    InstallationId: id,
                    Platform: platform,
                    Generation: generation,
                    ContentHash: contentHash,
                    Quality: quality,
                    RequestedQuality: quality,
                    ExpiresAt: expiresAt,
                    Duration: duration,
                    PlaybackVerified: false,
                    QualityVerified: false,
                    QualityPolicySignature: policy.Signature,
                    QualityCacheSignature: signature);
            }
            catch (Exception error) when (deadline.IsCancellationRequested && !external.IsCancellationRequested &&
                Code(error) == "CANCELLED")
            { throw new MusicLibraryException("TIMEOUT"); }
        }, cancellationToken);

    public Task<MusicLyricLookupResult> FetchOnlineLyricsAsync(
        string installationId,
        string platform,
        MusicSongRecord song,
        string? sourceKey = null,
        CancellationToken cancellationToken = default) =>
        RunAdmittedAsync(async external =>
        {
            string id = MusicRules.RequireId(installationId, 128);
            MusicRules.Require(NativeMusicSong.Platforms.Append("local").Contains(platform));
            song = NativeMusicSong.Normalize(song);
            var setting = await library.GetOnlineSettingsAsync(external).ConfigureAwait(false);
            if (!setting.LyricsEnabled)
                return new MusicLyricLookupResult("disabled", []);
            var selected = SelectRecording(song, platform, sourceKey);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(external, deadline.Token);
            try
            {
                var infoEl = selected.Source is null
                    ? MusicNodeSongCodec.BuildObject(_ => { })
                    : MusicNodeSongCodec.MusicInfoToElement(selected.Source, clampNonNegativeDuration: true);
                var raw = await ScriptValueAsync("invoke", new
                {
                    installationId = id, siteId = platform, operation = "lyric",
                    payload = new { musicInfo = infoEl }
                }, timed.Token).ConfigureAwait(false);
                string text;
                string translation = "";
                string romanization = "";
                if (raw.ValueKind == JsonValueKind.String)
                    text = raw.GetString()!;
                else if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("lyric", out var lyr) && lyr.ValueKind == JsonValueKind.String)
                {
                    text = lyr.GetString()!;
                    translation = ReadString(raw, "tlyric");
                    romanization = ReadString(raw, "rlyric");
                }
                else throw new MusicLibraryException("INVALID_LYRIC_RESPONSE");
                var lines = MusicLyricTiming.ParseLinesRecord(text, translation, song.Duration ?? 0, romanization);
                return new MusicLyricLookupResult(lines.Length > 0 ? "ready" : "none", lines);
            }
            catch (Exception error) when (deadline.IsCancellationRequested && !external.IsCancellationRequested &&
                Code(error) == "CANCELLED")
            { throw new MusicLibraryException("TIMEOUT"); }
        }, cancellationToken);

    private static MusicScriptSourceSnapshot ReadSnapshot(JsonElement value)
    {
        long revision = ReadInt64(value, "revision", 0);
        MusicRules.Require(value.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array, "INVALID_RESPONSE");
        var items = new List<MusicScriptSourceItem>();
        foreach (var entry in itemsEl.EnumerateArray())
            items.Add(ReadItem(entry));
        return new MusicScriptSourceSnapshot(revision, items.ToArray());
    }

    private static MusicScriptSourceDetail ReadDetail(JsonElement value)
    {
        MusicRules.Require(value.TryGetProperty("item", out var itemEl) && itemEl.ValueKind == JsonValueKind.Object, "INVALID_RESPONSE");
        return new MusicScriptSourceDetail(ReadItem(itemEl));
    }

    private static MusicScriptSourceItem ReadItem(JsonElement value)
    {
        MusicRules.Require(value.ValueKind == JsonValueKind.Object, "INVALID_RESPONSE");
        string id = ReadString(value, "installationId");
        MusicRules.Require(id.Length >= 8 && id.Length <= 128, "INVALID_RESPONSE");
        MusicRules.Require(value.TryGetProperty("origin", out var originEl) && originEl.ValueKind == JsonValueKind.Object, "INVALID_RESPONSE");
        string originKind = ReadString(originEl, "kind");
        var origin = new MusicScriptOrigin(
            Kind: originKind,
            Host: ReadOptionalString(originEl, "host"),
            Url: ReadOptionalString(originEl, "url"));
        var sites = value.TryGetProperty("sites", out var sitesEl) ? ReadCapabilities(sitesEl) : null;
        var capabilities = value.TryGetProperty("capabilities", out var capEl) ? ReadCapabilities(capEl) : null;
        var check = value.TryGetProperty("check", out var checkEl) ? ReadCheck(checkEl) : null;
        return new MusicScriptSourceItem(
            InstallationId: id,
            Name: ReadString(value, "name"),
            Version: ReadString(value, "version"),
            Enabled: value.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
            State: ReadString(value, "state"),
            Origin: origin,
            RollbackAvailable: value.TryGetProperty("rollbackAvailable", out var rb) && rb.ValueKind == JsonValueKind.True,
            ExecutionHash: ReadString(value, "executionHash"),
            Sites: sites,
            Capabilities: capabilities,
            Check: check);
    }

    private static IReadOnlyDictionary<string, MusicScriptSiteCapability>? ReadCapabilities(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<string, MusicScriptSiteCapability>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var actions = ReadStringArray(prop.Value, "actions");
            var qualities = ReadStringArray(prop.Value, "qualitys");
            map[prop.Name] = new MusicScriptSiteCapability(actions, qualities);
        }
        return map;
    }

    private static MusicScriptCheckInfo? ReadCheck(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        long checkedAt = element.TryGetProperty("checkedAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var ts) ? ts : 0;
        var sites = new Dictionary<string, MusicScriptCheckSiteResult>(StringComparer.Ordinal);
        if (element.TryGetProperty("sites", out var sitesEl) && sitesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in sitesEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                sites[prop.Name] = new MusicScriptCheckSiteResult(
                    State: ReadString(prop.Value, "state"),
                    Quality: ReadOptionalString(prop.Value, "quality"),
                    Sample: ReadOptionalString(prop.Value, "sample"),
                    Code: ReadOptionalString(prop.Value, "code"));
            }
        }
        return new MusicScriptCheckInfo(
            CheckedAt: checkedAt,
            Failure: ReadOptionalString(element, "failure"),
            Summary: ReadString(element, "summary"),
            Sites: sites);
    }

    private static string[] ReadStringArray(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        return list.ToArray();
    }

    private static string ReadString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static string? ReadOptionalString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static long ReadInt64(JsonElement obj, string prop, long min)
    {
        long val = 0;
        MusicRules.Require(obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number &&
            el.TryGetInt64(out val) && val >= min, "INVALID_RESPONSE");
        return val;
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

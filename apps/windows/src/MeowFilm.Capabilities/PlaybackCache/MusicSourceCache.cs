using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>
/// Private resolution candidates and confirmed playback history, never library or download data.
/// Unreadable or unusable cached values are misses; persistence is optional.
/// </summary>
internal sealed class MusicSourceCache(IStorageDatabase database, TimeProvider? time = null)
{
    internal const string Namespace = "music.playback-sources.v1";
    internal const long HistoryTtl = 30L * 86400000;
    private const int MaximumBytes = 1024 * 1024, MaximumScopes = 4;
    private static readonly HashSet<string> CoreMusicInfoFields = new(StringComparer.Ordinal)
    {
        "source", "name", "singer", "albumName", "duration", "img", "qualityKnown"
    };
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly HashSet<(string Scope, string Song)> invalidated = [];
    private readonly HashSet<(string Scope, string Song, string Source)> rejected = [];

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    internal async Task<JsonObject?> ReadAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        try
        {
            return await database.ReadSnapshotAsync(db =>
            {
                long now = Now;
                var entry = PlaybackSourceCacheStore.ReadEntry(db, scope, song, "urls");
                if (entry is null || entry.At > now || entry.At <= now - HistoryTtl || entry.Until <= now)
                    return null;
                lock (gate)
                {
                    if (invalidated.Contains((scope, song)) ||
                        entry.SourceKey is not null && rejected.Contains((scope, song, entry.SourceKey)))
                        return null;
                }
                var info = EntryInfoToJson(entry, song, "urls");
                var expires = ValidateCandidate(JsonSerializer.SerializeToElement(info), scope, song, signature);
                if (expires is { } expiry && expiry <= now + 10000) return null;
                return entry.Signature == signature && entry.Until > now ? info : null;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return null; }
    }

    internal async Task<JsonObject?> ReadHistoryAsync(string scope, string song, string policy, string request,
        long generation, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(policy);
        RequireText(request, 64);
        if (generation <= 0) throw MusicPlaybackCacheDataException.Corrupt();
        try
        {
            return await database.ReadSnapshotAsync(db =>
            {
                long now = Now;
                var entry = PlaybackSourceCacheStore.ReadEntry(db, scope, song, "pointers");
                if (entry is null || entry.At > now || entry.At <= now - HistoryTtl)
                    return null;
                lock (gate)
                {
                    if (entry.SourceKey is not null && rejected.Contains((scope, song, entry.SourceKey)))
                        return null;
                }
                if (entry.QualityPolicySignature != policy ||
                    entry.RequestedQuality != request ||
                    entry.Generation != generation)
                    return null;
                var info = EntryInfoToJson(entry, song, "pointers");
                ValidateHistory(JsonSerializer.SerializeToElement(info), scope, song);
                info["confirmedAt"] = entry.At;
                return info;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return null; }
    }

    internal async Task PutAsync(string scope, string song, string signature, JsonObject info, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        info = info.DeepClone().AsObject();
        try
        {
            bool written = await database.WriteAsync(db =>
            {
                long at = Now;
                long? expires = info["expiresAt"]?.GetValue<long>();
                long ttl = at > long.MaxValue - HistoryTtl ? long.MaxValue : at + HistoryTtl;
                long until = expires is null ? ttl : Math.Min(expires.Value - 10000, ttl);
                if (until <= at) return false;
                var projected = ProjectInfo(info);
                int bytes = EstimateEntryBytes(signature, at, until, projected);
                if (bytes > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
                var entry = EntryFromJson(signature, at, until, bytes, projected);
                PlaybackSourceCacheStore.SaveEntry(db, scope, song, "urls", entry);
                Trim(db, at);
                return true;
            }, token, 65536).ConfigureAwait(false);
            if (written)
            {
                lock (gate) { invalidated.Remove((scope, song)); rejected.RemoveWhere(key => key.Scope == scope && key.Song == song); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
    }

    internal async Task ConfirmAsync(string scope, string song, string signature, JsonObject info, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        var metadata = HistoryInfo(scope, song, info);
        try
        {
            await database.WriteAsync(db =>
            {
                long at = Now;
                long until = at > long.MaxValue - HistoryTtl ? long.MaxValue : at + HistoryTtl;
                var projected = ProjectInfo(metadata);
                int bytes = EstimateEntryBytes(signature, at, until, projected);
                if (bytes > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
                var entry = EntryFromJson(signature, at, until, bytes, projected);
                PlaybackSourceCacheStore.SaveEntry(db, scope, song, "pointers", entry);
                Trim(db, at);
                return true;
            }, token, 65536).ConfigureAwait(false);
            lock (gate) { invalidated.Remove((scope, song)); rejected.RemoveWhere(key => key.Scope == scope && key.Song == song); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
    }

    internal async Task InvalidateAsync(string scope, string song, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        lock (gate) invalidated.Add((scope, song));
        try
        {
            await database.WriteAsync(db =>
            {
                PlaybackSourceCacheStore.DeleteEntry(db, scope, song, "urls");
                Trim(db, Now);
                return true;
            }, token, 4096).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
    }

    internal async Task RejectRecordingAsync(string scope, string song, string sourceKey, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        lock (gate) rejected.Add((scope, song, sourceKey));
        try
        {
            await database.WriteAsync(db =>
            {
                db.Execute("""
                    DELETE FROM windows_music_playback_sources
                    WHERE account_scope=$scope AND song_id=$song AND info_source_key=$skey
                    """, ("$scope", scope), ("$song", song), ("$skey", sourceKey));
                Trim(db, Now);
                return true;
            }, token, 4096).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
    }

    private static readonly string[] InfoFields = "ok kind platform songId sourceKey scope generation quality actualQuality qualityVerified playbackVerified qualityPolicySignature qualityCacheSignature url expiresAt duration size full delivery requestedQuality headers recording".Split(' ');

    private static JsonObject ProjectInfo(JsonObject info)
    {
        var projection = new JsonObject();
        foreach (var name in InfoFields)
            if (info.ContainsKey(name))
                projection[name] = info[name]?.DeepClone();
        return projection;
    }

    private static int EstimateEntryBytes(string signature, long at, long until, JsonObject projectedInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["signature"] = signature,
            ["at"] = at,
            ["until"] = until,
            ["info"] = projectedInfo.DeepClone()
        }).Length;

    private static PlaybackSourceEntry EntryFromJson(string signature, long at, long until, long storedBytes, JsonObject info)
    {
        Dictionary<string, string>? headers = null;
        if (info["headers"] is JsonObject hdrs)
        {
            headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in hdrs)
                if (pair.Value is JsonValue v && v.TryGetValue<string>(out var text))
                    headers[pair.Key] = text;
        }

        PlaybackRecordingSnapshot? recording = null;
        if (info["recording"] is JsonObject rec)
        {
            var mi = rec["musicInfo"] as JsonObject;
            List<StorageValueField>? extraFields = null;
            if (mi is not null)
            {
                foreach (var pair in mi)
                {
                    if (CoreMusicInfoFields.Contains(pair.Key)) continue;
                    extraFields ??= [];
                    extraFields.Add(new StorageValueField(pair.Key, StorageValueJson.FromJsonNode(pair.Value)));
                }
            }
            recording = new PlaybackRecordingSnapshot(
                JsonStr(rec, "sourceKey"), JsonStr(rec, "source"), JsonStr(mi, "source"),
                JsonStr(mi, "name"), JsonStr(mi, "singer"), JsonStr(mi, "albumName"),
                JsonDbl(mi, "duration"), JsonStr(mi, "img"), JsonBool(mi, "qualityKnown"),
                extraFields is null ? null : StorageValue.FromObject(extraFields));
        }

        return new PlaybackSourceEntry(
            signature, at, until, storedBytes,
            JsonBool(info, "ok"), JsonStr(info, "kind"), JsonStr(info, "platform"), JsonStr(info, "sourceKey"), JsonStr(info, "scope"),
            JsonInt64(info, "generation"), JsonStr(info, "quality"), JsonStr(info, "actualQuality"),
            JsonBool(info, "qualityVerified"), JsonBool(info, "playbackVerified"),
            JsonStr(info, "qualityPolicySignature"), JsonStr(info, "qualityCacheSignature"),
            JsonStr(info, "url"), JsonInt64(info, "expiresAt"), JsonDbl(info, "duration"), JsonInt64(info, "size"),
            JsonBool(info, "full"), JsonStr(info, "delivery"), JsonStr(info, "requestedQuality"),
            headers, recording);
    }

    private static JsonObject EntryInfoToJson(PlaybackSourceEntry e, string songId, string entryKind)
    {
        var info = new JsonObject { ["songId"] = songId };
        SetNonNull(info, "ok", e.Ok);
        SetNonNull(info, "kind", e.Kind);
        SetNonNull(info, "platform", e.Platform);
        SetNonNull(info, "sourceKey", e.SourceKey);
        SetNonNull(info, "scope", e.Scope);
        SetNonNull(info, "generation", e.Generation);
        SetNonNull(info, "quality", e.Quality);
        if (entryKind == "pointers") info["actualQuality"] = e.ActualQuality;
        else SetNonNull(info, "actualQuality", e.ActualQuality);
        SetNonNull(info, "qualityVerified", e.QualityVerified);
        SetNonNull(info, "playbackVerified", e.PlaybackVerified);
        SetNonNull(info, "qualityPolicySignature", e.QualityPolicySignature);
        SetNonNull(info, "qualityCacheSignature", e.QualityCacheSignature);
        SetNonNull(info, "url", e.Url);
        if (entryKind == "urls") info["expiresAt"] = e.ExpiresAt;
        else SetNonNull(info, "expiresAt", e.ExpiresAt);
        if (entryKind == "pointers") info["duration"] = e.Duration;
        else SetNonNull(info, "duration", e.Duration);
        SetNonNull(info, "size", e.Size);
        SetNonNull(info, "full", e.Full);
        if (entryKind == "pointers") info["delivery"] = e.Delivery;
        else SetNonNull(info, "delivery", e.Delivery);
        SetNonNull(info, "requestedQuality", e.RequestedQuality);

        if (entryKind == "urls")
        {
            var headers = new JsonObject();
            if (e.Headers is not null)
                foreach (var (k, v) in e.Headers) headers[k] = v;
            info["headers"] = headers;
        }

        if (e.Recording is { } rec)
        {
            var mi = new JsonObject();
            SetNonNull(mi, "source", rec.InfoSource);
            SetNonNull(mi, "name", rec.Name);
            SetNonNull(mi, "singer", rec.Singer);
            SetNonNull(mi, "albumName", rec.AlbumName);
            mi["duration"] = rec.Duration;
            SetNonNull(mi, "img", rec.Img);
            SetNonNull(mi, "qualityKnown", rec.QualityKnown);
            if (rec.WireExtras is { Kind: StorageValueKind.Object, ObjectFields: { } extraFields })
                foreach (var field in extraFields)
                    mi[field.Name] = StorageValueJson.ToJsonNode(field.Value);

            var recObj = new JsonObject { ["musicInfo"] = mi };
            if (rec.SourceKey is not null) recObj["sourceKey"] = rec.SourceKey;
            if (rec.Platform is not null) recObj["source"] = rec.Platform;
            info["recording"] = recObj;
        }

        return info;
    }

    private static void SetNonNull(JsonObject obj, string key, string? val) { if (val is not null) obj[key] = val; }
    private static void SetNonNull(JsonObject obj, string key, long? val) { if (val is { } v) obj[key] = v; }
    private static void SetNonNull(JsonObject obj, string key, double? val) { if (val is { } v) obj[key] = v; }
    private static void SetNonNull(JsonObject obj, string key, bool? val) { if (val is { } v) obj[key] = v; }

    private static string? JsonStr(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<string>(out var s) ? s
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()
            : null : null;
    private static long? JsonInt64(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<long>(out var n) ? n
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var en) ? en
            : null : null;
    private static double? JsonDbl(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<float>(out var f) ? f
            : v.TryGetValue<decimal>(out var m) ? (double)m
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var eld) ? eld
            : null : null;
    private static bool? JsonBool(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<bool>(out var b) ? b
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean()
            : null : null;

    private static void Trim(DatabaseSession db, long now)
    {
        db.Execute("DELETE FROM windows_music_playback_sources WHERE updated_at_ms<=$expired OR updated_at_ms>$now OR expires_until_ms<=$now", ("$expired", now - HistoryTtl), ("$now", now));
        db.Execute("DELETE FROM windows_music_playback_sources WHERE account_scope IN (SELECT account_scope FROM windows_music_playback_sources GROUP BY account_scope ORDER BY max(updated_at_ms) DESC LIMIT -1 OFFSET $limit)", ("$limit", MaximumScopes));
        foreach (var (kind, limit) in new[] { ("urls", 50), ("pointers", 200) })
            foreach (var scope in db.Query("SELECT DISTINCT account_scope FROM windows_music_playback_sources", row => row.GetString(0)))
                db.Execute("DELETE FROM windows_music_playback_sources WHERE rowid IN (SELECT rowid FROM windows_music_playback_sources WHERE account_scope=$scope AND entry_kind=$kind ORDER BY updated_at_ms DESC,song_id LIMIT -1 OFFSET $limit)",
                    ("$scope", scope), ("$kind", kind), ("$limit", limit));
        while (db.Scalar<long>("SELECT coalesce(sum(stored_bytes),0) FROM windows_music_playback_sources") > MaximumBytes)
            db.Execute("DELETE FROM windows_music_playback_sources WHERE rowid=(SELECT rowid FROM windows_music_playback_sources ORDER BY updated_at_ms,account_scope,song_id LIMIT 1)");
    }

    internal static long? ValidateCandidate(JsonElement info, string scope, string song, string signature)
    {
        if (info.GetProperty("ok").ValueKind != JsonValueKind.True ||
            Text(info.GetProperty("kind"), 32) != "official")
            throw MusicPlaybackCacheDataException.Corrupt();
        string platform = Text(info.GetProperty("platform"), 16);
        if (platform is not ("wy" or "tx" or "kg") ||
            Text(info.GetProperty("songId"), 2048) != song ||
            Text(info.GetProperty("scope"), 128) != scope)
            throw MusicPlaybackCacheDataException.Corrupt();
        _ = Text(info.GetProperty("sourceKey"), 8192);
        _ = CanonicalLong(info.GetProperty("generation"));
        _ = Text(info.GetProperty("quality"), 64);
        _ = Text(info.GetProperty("requestedQuality"), 64);
        string actual = Text(info.GetProperty("actualQuality"), 64, true);
        bool verified = Boolean(info.GetProperty("qualityVerified"));
        if (verified && actual.Length == 0) throw MusicPlaybackCacheDataException.Corrupt();
        if (Boolean(info.GetProperty("playbackVerified")))
            throw MusicPlaybackCacheDataException.Corrupt();
        string policy = Text(info.GetProperty("qualityPolicySignature"), 64);
        string cache = Text(info.GetProperty("qualityCacheSignature"), 64);
        RequireHash(policy);
        RequireHash(cache);
        if (cache != signature) throw MusicPlaybackCacheDataException.Corrupt();
        ValidateMediaUrl(Text(info.GetProperty("url"), 16384));
        if (info.GetProperty("headers").ValueKind != JsonValueKind.Object ||
            info.GetProperty("headers").EnumerateObject().Any())
            throw MusicPlaybackCacheDataException.Corrupt();
        long? expires = OptionalLong(info.GetProperty("expiresAt"));
        _ = CanonicalDouble(info.GetProperty("duration"), true);
        _ = CanonicalLong(info.GetProperty("size"), true);
        _ = Boolean(info.GetProperty("full"));
        if (Text(info.GetProperty("delivery"), 128) != platform + "-official")
            throw MusicPlaybackCacheDataException.Corrupt();
        return expires;
    }

    private static JsonObject HistoryInfo(string scope, string song, JsonObject info)
    {
        try
        {
            if (info["recording"] is not JsonObject rawRecording)
                throw MusicPlaybackCacheDataException.Corrupt();
            var recording = NativeMusicSong.DurableSource(rawRecording);
            var metadata = new JsonObject
            {
                ["platform"] = info["platform"]?.DeepClone(),
                ["songId"] = song,
                ["sourceKey"] = info["sourceKey"]?.DeepClone(),
                ["scope"] = scope,
                ["generation"] = info["generation"]?.DeepClone(),
                ["quality"] = info["quality"]?.DeepClone(),
                ["actualQuality"] = info["actualQuality"]?.DeepClone(),
                ["qualityVerified"] = info["qualityVerified"]?.DeepClone(),
                ["duration"] = info["duration"]?.DeepClone(),
                ["delivery"] = info["delivery"]?.DeepClone(),
                ["requestedQuality"] = info["requestedQuality"]?.DeepClone(),
                ["qualityPolicySignature"] = info["qualityPolicySignature"]?.DeepClone(),
                ["recording"] = recording
            };
            return metadata;
        }
        catch (MusicPlaybackCacheDataException) { throw; }
        catch (MusicLibraryException) { throw MusicPlaybackCacheDataException.Corrupt(); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        { throw MusicPlaybackCacheDataException.Corrupt(); }
    }

    private static void ValidateHistory(JsonElement info, string scope, string song)
    {
        string platform = Text(info.GetProperty("platform"), 16);
        if (platform is not ("wy" or "tx" or "kg") ||
            Text(info.GetProperty("songId"), 2048) != song ||
            Text(info.GetProperty("scope"), 128) != scope)
            throw MusicPlaybackCacheDataException.Corrupt();
        string sourceKey = Text(info.GetProperty("sourceKey"), 8192);
        _ = CanonicalLong(info.GetProperty("generation"));
        _ = Text(info.GetProperty("quality"), 64);
        string? actual = OptionalText(info.GetProperty("actualQuality"), 64, true);
        bool verified = Boolean(info.GetProperty("qualityVerified"));
        if (verified && string.IsNullOrEmpty(actual)) throw MusicPlaybackCacheDataException.Corrupt();
        var duration = info.GetProperty("duration");
        if (duration.ValueKind != JsonValueKind.Null) _ = CanonicalDouble(duration, true);
        var delivery = info.GetProperty("delivery");
        if (delivery.ValueKind != JsonValueKind.Null &&
            Text(delivery, 128) != platform + "-official")
            throw MusicPlaybackCacheDataException.Corrupt();
        _ = Text(info.GetProperty("requestedQuality"), 64);
        string policy = Text(info.GetProperty("qualityPolicySignature"), 64);
        RequireHash(policy);

        var rawRecording = info.GetProperty("recording");
        if (rawRecording.ValueKind != JsonValueKind.Object)
            throw MusicPlaybackCacheDataException.Corrupt();
        var recording = JsonNode.Parse(rawRecording.GetRawText())!.AsObject();
        _ = NativeMusicSong.DurableSource(recording);
        if (recording["source"]?.GetValue<string>() != platform ||
            recording["sourceKey"]?.GetValue<string>() != sourceKey ||
            NativeMusicSong.SourceKey(recording) != sourceKey)
            throw MusicPlaybackCacheDataException.Corrupt();
    }

    private static void ValidateIdentity(string scope, string song)
    {
        RequireText(scope, 128);
        RequireText(song, 2048);
    }

    private static long CanonicalLong(JsonElement value, bool allowZero = false)
    {
        if (!value.TryGetInt64(out long result) || (allowZero ? result < 0 : result <= 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }

    private static long? OptionalLong(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : CanonicalLong(value);

    private static double CanonicalDouble(JsonElement value, bool allowZero = false)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) || (allowZero ? result < 0 : result <= 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }

    private static bool Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw MusicPlaybackCacheDataException.Corrupt()
    };

    private static string Text(JsonElement value, int maximum, bool empty = false)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
            throw MusicPlaybackCacheDataException.Corrupt();
        RequireText(text, maximum, empty);
        return text;
    }

    private static string? OptionalText(JsonElement value, int maximum, bool empty = false) =>
        value.ValueKind == JsonValueKind.Null ? null : Text(value, maximum, empty);

    private static void RequireText(string text, int maximum, bool empty = false)
    {
        if (text.Length > maximum || !empty && text.Length == 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character < 32 || character == 127 || char.IsLowSurrogate(character))
                throw MusicPlaybackCacheDataException.Corrupt();
            if (char.IsHighSurrogate(character) &&
                (++i >= text.Length || !char.IsLowSurrogate(text[i])))
                throw MusicPlaybackCacheDataException.Corrupt();
        }
    }

    private static void RequireHash(string value)
    {
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw MusicPlaybackCacheDataException.Corrupt();
    }

    private static void ValidateMediaUrl(string value)
    {
        if (value.Any(char.IsWhiteSpace) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw MusicPlaybackCacheDataException.Corrupt();
    }
}

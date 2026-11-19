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
    internal const string Namespace = "music.playback-sources.v2";
    internal const long HistoryTtl = 30L * 86400000;
    private const int MaximumBytes = 1024 * 1024, MaximumScopes = 4;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly HashSet<(string Scope, string Song)> invalidated = [];
    private readonly HashSet<(string Scope, string Song, string Source)> rejected = [];

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    internal Task<JsonObject?> ReadAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        return Access(scope, song, false, false, (record, now) =>
        {
            var url = record["urls"]![song] as JsonObject;
            if (url is null) return (null, false);
            var expires = ValidateCandidate(JsonSerializer.SerializeToElement(url["info"]), scope, song, signature);
            if (expires is { } expiry && expiry <= now + 10000) return (null, false);
            return (url?["signature"]?.GetValue<string>() == signature &&
                url["until"]!.GetValue<long>() > now ? url["info"]?.DeepClone().AsObject() : null, false);
        }, token);
    }

    internal Task<JsonObject?> ReadHistoryAsync(string scope, string song, string policy, string request,
        long generation, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(policy);
        RequireText(request, 64);
        if (generation <= 0) throw MusicPlaybackCacheDataException.Corrupt();
        return Access(scope, song, false, false, (record, _) =>
        {
            var pointer = record["pointers"]![song] as JsonObject;
            var info = pointer?["info"] as JsonObject;
            if (info?["qualityPolicySignature"]?.GetValue<string>() != policy ||
                info["requestedQuality"]?.GetValue<string>() != request ||
                info["generation"]?.GetValue<long>() != generation) return (null, false);
            ValidateHistory(JsonSerializer.SerializeToElement(info), scope, song);
            var copy = info.DeepClone().AsObject();
            copy["confirmedAt"] = pointer!["at"]!.DeepClone();
            return (copy, false);
        }, token);
    }

    internal async Task PutAsync(string scope, string song, string signature, JsonObject info, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        info = info.DeepClone().AsObject();
        await Access(scope, song, true, true, (record, at) =>
        {
            long? expires = info["expiresAt"]?.GetValue<long>();
            long ttl = at > long.MaxValue - HistoryTtl ? long.MaxValue : at + HistoryTtl;
            long until = expires is null ? ttl : Math.Min(expires.Value - 10000, ttl);
            if (until <= at) return (null, false);
            var urls = record["urls"]!.AsObject();
            urls.Remove(song);
            urls[song] = new JsonObject
            {
                ["signature"] = signature,
                ["at"] = at,
                ["until"] = until,
                ["info"] = info.DeepClone()
            };
            return (null, true);
        }, token).ConfigureAwait(false);
    }

    internal async Task ConfirmAsync(string scope, string song, string signature, JsonObject info, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        var metadata = HistoryInfo(scope, song, info);
        await Access(scope, song, true, true, (record, at) =>
        {
            var pointers = record["pointers"]!.AsObject();
            pointers.Remove(song);
            pointers[song] = new JsonObject
            {
                ["signature"] = signature,
                ["at"] = at,
                ["info"] = metadata.DeepClone()
            };
            return (null, true);
        }, token).ConfigureAwait(false);
    }

    internal async Task InvalidateAsync(string scope, string song, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        lock (gate) invalidated.Add((scope, song));
        await Access(scope, song, true, false, (record, _) =>
        {
            bool removed = record["urls"]!.AsObject().Remove(song);
            return (null, removed);
        }, token).ConfigureAwait(false);
    }

    internal async Task RejectRecordingAsync(string scope, string song, string sourceKey, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        lock (gate) rejected.Add((scope, song, sourceKey));
        await Access(scope, song, true, false, (record, _) =>
        {
            bool removed = false;
            foreach (string area in new[] { "urls", "pointers" })
                if (record[area]![song]?["info"]?["sourceKey"]?.GetValue<string>() == sourceKey)
                    removed |= record[area]!.AsObject().Remove(song);
            return (null, removed);
        }, token).ConfigureAwait(false);
    }

    private static readonly string[] InfoFields = "ok kind platform songId sourceKey scope generation quality actualQuality qualityVerified playbackVerified qualityPolicySignature qualityCacheSignature url expiresAt duration size full delivery requestedQuality headers recording".Split(' ');
    private async Task<JsonObject?> Access(string scope, string song, bool write, bool create,
        Func<JsonObject, long, (JsonObject? Result, bool Changed)> action, CancellationToken token)
    {
        bool changedEntry = false;
        try
        {
            JsonObject? Apply(DatabaseSession db)
            {
                long now = Now;
                var current = new JsonObject { ["urls"] = new JsonObject(), ["pointers"] = new JsonObject() };
                var previous = new Dictionary<string, JsonObject?>();
                foreach (var kind in new[] { "urls", "pointers" })
                {
                    var row = PlaybackCacheRecords.Sources.Read(db, [scope, song, kind]);
                    previous.Add(kind, row);
                    if (row is null || row["at"]!.GetValue<long>() > now || row["at"]!.GetValue<long>() <= now - HistoryTtl ||
                        kind == "urls" && row["until"]!.GetValue<long>() <= now) continue;
                    bool hidden;
                    lock (gate) hidden = !write && (kind == "urls" && invalidated.Contains((scope, song)) ||
                        rejected.Contains((scope, song, row["info"]!["sourceKey"]!.GetValue<string>())));
                    if (hidden) continue;
                    var value = row.DeepClone().AsObject(); value.Remove("storedBytes");
                    if (kind == "pointers") value.Remove("until");
                    current[kind]![song] = value;
                }
                var (result, changed) = action(current, now);
                changedEntry = changed;
                if (!write) return result;
                // Even empty invalidations remove the typed row; a queued read cannot resurrect it.
                if (changed || !create)
                    foreach (var kind in new[] { "urls", "pointers" })
                    {
                        var value = current[kind]![song]?.DeepClone() as JsonObject;
                        if (value is not null)
                        {
                            var info = value["info"]!.AsObject();
                            var projection = new JsonObject();
                            foreach (var name in InfoFields) if (info.ContainsKey(name)) projection[name] = info[name]?.DeepClone();
                            value["info"] = projection;
                            value["until"] = kind == "urls" ? value["until"]?.DeepClone() : JsonValue.Create(value["at"]!.GetValue<long>() + HistoryTtl);
                            int bytes = JsonSerializer.SerializeToUtf8Bytes(value).Length;
                            if (bytes > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
                            value["storedBytes"] = bytes;
                        }
                        PlaybackCacheRecords.Sources.Save(db, value, previous[kind], [scope, song, kind]);
                    }
                Trim(db, now);
                return result;
            }
            var result = write ? await database.WriteAsync(Apply, token, 65536).ConfigureAwait(false) :
                await database.ReadAsync(Apply, token).ConfigureAwait(false);
            if (write && create && changedEntry)
            {
                lock (gate) { invalidated.Remove((scope, song)); rejected.RemoveWhere(key => key.Scope == scope && key.Song == song); }
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return null; }
    }
    private static void Trim(DatabaseSession db, long now)
    {
        db.Execute("DELETE FROM windows_playback_source WHERE at<=$expired OR at>$now OR until<=$now", ("$expired", now - HistoryTtl), ("$now", now));
        db.Execute("DELETE FROM windows_playback_source WHERE account_scope IN (SELECT account_scope FROM windows_playback_source GROUP BY account_scope ORDER BY max(at) DESC LIMIT -1 OFFSET $limit)", ("$limit", MaximumScopes));
        foreach (var (kind, limit) in new[] { ("urls", 50), ("pointers", 200) })
            foreach (var scope in db.Query("SELECT DISTINCT account_scope FROM windows_playback_source", row => row.GetString(0)))
                db.Execute("DELETE FROM windows_playback_source WHERE rowid IN (SELECT rowid FROM windows_playback_source WHERE account_scope=$scope AND entry_kind=$kind ORDER BY at DESC,song_id LIMIT -1 OFFSET $limit)",
                    ("$scope", scope), ("$kind", kind), ("$limit", limit));
        while (db.Scalar<long>("SELECT coalesce(sum(storedBytes),0) FROM windows_playback_source") > MaximumBytes)
            db.Execute("DELETE FROM windows_playback_source WHERE rowid=(SELECT rowid FROM windows_playback_source ORDER BY at,account_scope,song_id LIMIT 1)");
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

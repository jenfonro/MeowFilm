using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>
/// Private resolution candidates and confirmed playback history, never library or download data.
/// Unreadable or unusable cached values are misses; persistence is optional.
/// </summary>
internal sealed class MusicSourceCache(CacheDocumentStore store, TimeProvider? time = null)
{
    internal const string Namespace = "music.playback-sources.v1";
    internal const long HistoryTtl = 30L * 86400000;
    private const int MaximumBytes = 1024 * 1024, MaximumScopes = 4;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    internal Task<JsonObject?> ReadAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        return Access(scope, false, false, (record, now) =>
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
        return Access(scope, false, false, (record, _) =>
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
        await Access(scope, true, true, (record, at) =>
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
        await Access(scope, true, true, (record, at) =>
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
        await Access(scope, true, false, (record, _) =>
        {
            bool removed = record["urls"]!.AsObject().Remove(song);
            return (null, removed);
        }, token).ConfigureAwait(false);
    }

    internal async Task RejectRecordingAsync(string scope, string song, string sourceKey, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        await Access(scope, true, false, (record, _) =>
        {
            bool removed = false;
            foreach (string area in new[] { "urls", "pointers" })
                if (record[area]![song]?["info"]?["sourceKey"]?.GetValue<string>() == sourceKey)
                    removed |= record[area]!.AsObject().Remove(song);
            return (null, removed);
        }, token).ConfigureAwait(false);
    }

    private async Task<JsonObject?> Access(string scope, bool write, bool create,
        Func<JsonObject, long, (JsonObject? Result, bool Changed)> action, CancellationToken token)
    {
        if (write) await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var saved = write ? store.ReadMemory(Namespace) :
                await store.ReadAsync(Namespace, token).ConfigureAwait(false);
            var root = Decode(saved);

            long now = Now;
            var scopes = root["scopes"]!.AsObject();
            bool pruned = false;
            foreach (var pair in scopes.ToArray())
            {
                try
                {
                    var record = pair.Value!.AsObject();
                    long at = record["at"]!.GetValue<long>();
                    if (at > now || at <= now - HistoryTtl)
                    {
                        scopes.Remove(pair.Key);
                        pruned = true;
                        continue;
                    }
                    pruned |= Prune(record, now);
                }
                catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error))
                { scopes.Remove(pair.Key); pruned = true; }
            }

            bool existing = scopes[scope] is JsonObject;
            var current = scopes[scope] as JsonObject ??
                new JsonObject { ["at"] = now, ["urls"] = new JsonObject(), ["pointers"] = new JsonObject() };
            if (!existing && !create)
            {
                // Even an empty invalidation must supersede an initial disk read
                // that has not loaded yet; otherwise it can resurrect the rejected URL.
                if (write) await Write(root, token).ConfigureAwait(false);
                return null;
            }

            var (result, changed) = action(current, now);
            if (!write) return result;
            if (changed)
            {
                current["at"] = now;
                _ = Prune(current, now);
                scopes.Remove(scope);
                scopes[scope] = current;
                while (scopes.Count > MaximumScopes) scopes.Remove(scopes.First().Key);
            }
            if (changed || pruned || !create)
                await Write(root, token).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error))
        { return null; }
        finally { if (write) gate.Release(); }
    }

    private static JsonObject Decode(DocumentSnapshot saved)
    {
        if (saved.Data is null || saved.Data.Length > MaximumBytes) return Empty();
        try
        {
            var root = JsonNode.Parse(saved.Data)!.AsObject();
            _ = root["scopes"]!.AsObject();
            return root;
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return Empty(); }
    }

    private Task Write(JsonObject root, CancellationToken token)
    {
        var scopes = root["scopes"]!.AsObject();
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(root);
        while (encoded.Length > MaximumBytes)
        {
            if (scopes.Count > 1) scopes.Remove(scopes.First().Key);
            else
            {
                var current = scopes.FirstOrDefault().Value as JsonObject;
                if (current?["urls"]?.AsObject() is { Count: > 0 } urls)
                    urls.Remove(urls.First().Key);
                else if (current?["pointers"]?.AsObject() is { Count: > 0 } pointers)
                    pointers.Remove(pointers.First().Key);
                else throw MusicPlaybackCacheDataException.Limit();
            }
            encoded = JsonSerializer.SerializeToUtf8Bytes(root);
        }
        token.ThrowIfCancellationRequested();
        store.Save(Namespace, encoded);
        return Task.CompletedTask;
    }

    private static JsonObject Empty() =>
        new() { ["scopes"] = new JsonObject() };


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

    private static bool Prune(JsonObject record, long now)
    {
        bool changed = false;
        foreach (var (field, maximum) in new[] { ("urls", 50), ("pointers", 200) })
        {
            var entries = record[field]!.AsObject();
            foreach (var entry in entries.ToArray())
            {
                try
                {
                    long at = entry.Value!["at"]!.GetValue<long>();
                    if (at > now || at <= now - HistoryTtl ||
                        field == "urls" && entry.Value!["until"]!.GetValue<long>() <= now)
                    {
                        entries.Remove(entry.Key);
                        changed = true;
                    }
                }
                catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error))
                { entries.Remove(entry.Key); changed = true; }
            }
            while (entries.Count > maximum)
            {
                entries.Remove(entries.First().Key);
                changed = true;
            }
        }
        return changed;
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

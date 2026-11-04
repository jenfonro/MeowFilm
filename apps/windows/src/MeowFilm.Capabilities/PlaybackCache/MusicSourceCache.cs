using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>
/// Private resolution candidates and confirmed playback history, never library or download data.
/// Operational cache unavailability is a miss. Existing non-current data is an explicit
/// failure and is never interpreted, replaced or rewritten.
/// </summary>
internal sealed class MusicSourceCache(IProtectedDocumentStore store, TimeProvider? time = null)
{
    internal const string Namespace = "music.playback-sources.v1";
    internal const long HistoryTtl = 30L * 86400000;
    private const int MaximumBytes = 1024 * 1024, MaximumScopes = 4;
    private static readonly string[] CandidateFields =
    [
        "ok", "kind", "platform", "songId", "sourceKey", "scope", "generation",
        "quality", "requestedQuality", "actualQuality", "qualityVerified", "playbackVerified",
        "qualityPolicySignature", "qualityCacheSignature", "url", "headers", "expiresAt",
        "duration", "size", "full", "licenseExpiresAt", "delivery", "offlineAllowed",
        "authorizationExpiresAt"
    ];
    private static readonly string[] HistoryFields =
    [
        "platform", "songId", "sourceKey", "scope", "generation", "quality", "actualQuality",
        "qualityVerified", "duration", "delivery", "requestedQuality",
        "qualityPolicySignature", "recording"
    ];
    private static readonly HashSet<string> RecordingFields =
        "sourceKey source musicInfo".Split(' ').ToHashSet(StringComparer.Ordinal);
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
            var copy = info.DeepClone().AsObject();
            copy["confirmedAt"] = pointer!["at"]!.DeepClone();
            return (copy, false);
        }, token);
    }

    internal async Task PutAsync(string scope, string song, string signature, JsonObject info, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        ValidateCandidate(JsonSerializer.SerializeToElement(info), scope, song, signature);
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

    private async Task<JsonObject?> Access(string scope, bool write, bool create,
        Func<JsonObject, long, (JsonObject? Result, bool Changed)> action, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var saved = await store.ReadAsync(Namespace, token).ConfigureAwait(false);
            JsonObject root;
            try { root = Decode(saved); }
            finally { if (saved.Data is { } bytes) CryptographicOperations.ZeroMemory(bytes); }

            long now = Now;
            var scopes = root["scopes"]!.AsObject();
            bool pruned = false;
            foreach (var pair in scopes.ToArray())
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

            bool existing = scopes[scope] is JsonObject;
            var current = scopes[scope] as JsonObject ??
                new JsonObject { ["at"] = now, ["urls"] = new JsonObject(), ["pointers"] = new JsonObject() };
            if (!existing && !create)
            {
                if (write && pruned) await Write(root, saved.Fingerprint, token).ConfigureAwait(false);
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
            if (changed || pruned)
                await Write(root, saved.Fingerprint, token).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsStoredFormatFailure(error))
        { throw MusicPlaybackCacheDataException.FromStore(error); }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error))
        { return null; }
        finally { gate.Release(); }
    }

    private static JsonObject Decode(ProtectedDocument saved)
    {
        if (saved.Data is null)
        {
            if (saved.Fingerprint != DocumentFingerprint.Missing)
                throw MusicPlaybackCacheDataException.Corrupt();
            return Empty();
        }
        if (saved.Data.Length == 0 || DocumentFingerprint.FromBytes(saved.Data) != saved.Fingerprint)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (saved.Data.Length > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
        try
        {
            using var document = JsonDocument.Parse(saved.Data, new JsonDocumentOptions { MaxDepth = 16 });
            CheckJson(document.RootElement);
            RequireFields(document.RootElement, "schema", "scopes");
            var schema = document.RootElement.GetProperty("schema");
            if (schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != "1")
                throw MusicPlaybackCacheDataException.UnsupportedVersion();
            var scopes = document.RootElement.GetProperty("scopes");
            if (scopes.ValueKind != JsonValueKind.Object) throw MusicPlaybackCacheDataException.Corrupt();
            int scopeCount = 0;
            foreach (var scope in scopes.EnumerateObject())
            {
                if (++scopeCount > MaximumScopes) throw MusicPlaybackCacheDataException.Limit();
                RequireText(scope.Name, 128);
                RequireFields(scope.Value, "at", "urls", "pointers");
                long recordAt = CanonicalLong(scope.Value.GetProperty("at"));
                var urls = scope.Value.GetProperty("urls");
                var pointers = scope.Value.GetProperty("pointers");
                if (urls.ValueKind != JsonValueKind.Object || pointers.ValueKind != JsonValueKind.Object)
                    throw MusicPlaybackCacheDataException.Corrupt();
                ValidateEntries(scope.Name, urls, true, recordAt);
                ValidateEntries(scope.Name, pointers, false, recordAt);
            }
            return JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        }
        catch (MusicPlaybackCacheDataException) { throw; }
        catch (MusicLibraryException) { throw MusicPlaybackCacheDataException.Corrupt(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        { throw MusicPlaybackCacheDataException.Corrupt(); }
    }

    private async Task Write(JsonObject root, DocumentFingerprint before, CancellationToken token)
    {
        var scopes = root["scopes"]!.AsObject();
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(root);
        try
        {
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
                CryptographicOperations.ZeroMemory(encoded);
                encoded = JsonSerializer.SerializeToUtf8Bytes(root);
            }
            var expected = DocumentFingerprint.FromBytes(encoded);
            _ = Decode(new ProtectedDocument(encoded, expected));
            token.ThrowIfCancellationRequested();
            var receipt = await store.WriteAsync(Namespace, encoded, before, token).ConfigureAwait(false);
            if (receipt != expected) throw new MusicPlaybackCacheCommitException();
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    private static JsonObject Empty() =>
        new() { ["schema"] = 1, ["scopes"] = new JsonObject() };

    private static void ValidateEntries(string scope, JsonElement entries, bool urls, long recordAt)
    {
        int count = 0, maximum = urls ? 50 : 200;
        foreach (var property in entries.EnumerateObject())
        {
            if (++count > maximum) throw MusicPlaybackCacheDataException.Limit();
            string song = property.Name;
            RequireText(song, 2048);
            if (urls) RequireFields(property.Value, "signature", "at", "until", "info");
            else RequireFields(property.Value, "signature", "at", "info");
            string signature = Text(property.Value.GetProperty("signature"), 64);
            RequireHash(signature);
            long at = CanonicalLong(property.Value.GetProperty("at"));
            if (at > recordAt) throw MusicPlaybackCacheDataException.Corrupt();
            if (urls)
            {
                long until = CanonicalLong(property.Value.GetProperty("until"));
                if (until <= at) throw MusicPlaybackCacheDataException.Corrupt();
                long? expires = ValidateCandidate(property.Value.GetProperty("info"), scope, song, signature);
                long ttl = at > long.MaxValue - HistoryTtl ? long.MaxValue : at + HistoryTtl;
                long expected = expires is null ? ttl : Math.Min(expires.Value - 10000, ttl);
                if (until != expected) throw MusicPlaybackCacheDataException.Corrupt();
            }
            else ValidateHistory(property.Value.GetProperty("info"), scope, song);
        }
    }

    internal static long? ValidateCandidate(JsonElement info, string scope, string song, string signature)
    {
        RequireFields(info, CandidateFields);
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
        bool full = Boolean(info.GetProperty("full"));
        long? license = OptionalLong(info.GetProperty("licenseExpiresAt"));
        if (Text(info.GetProperty("delivery"), 128) != platform + "-official")
            throw MusicPlaybackCacheDataException.Corrupt();
        bool offline = Boolean(info.GetProperty("offlineAllowed"));
        long? authorization = OptionalLong(info.GetProperty("authorizationExpiresAt"));
        if (authorization != license || offline && (!full || license is null))
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
            if (!JsonNode.DeepEquals(rawRecording, recording))
                throw MusicPlaybackCacheDataException.Corrupt();
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
            ValidateHistory(JsonSerializer.SerializeToElement(metadata), scope, song);
            return metadata;
        }
        catch (MusicPlaybackCacheDataException) { throw; }
        catch (MusicLibraryException) { throw MusicPlaybackCacheDataException.Corrupt(); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        { throw MusicPlaybackCacheDataException.Corrupt(); }
    }

    private static void ValidateHistory(JsonElement info, string scope, string song)
    {
        RequireFields(info, HistoryFields);
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
        var names = rawRecording.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Any(name => !RecordingFields.Contains(name)) ||
            RecordingFields.Any(field => !names.Contains(field, StringComparer.Ordinal)))
            throw MusicPlaybackCacheDataException.Corrupt();
        var recording = JsonNode.Parse(rawRecording.GetRawText())!.AsObject();
        var canonical = NativeMusicSong.DurableSource(recording);
        if (!JsonNode.DeepEquals(recording, canonical) ||
            recording["source"]?.GetValue<string>() != platform ||
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
                long at = entry.Value!["at"]!.GetValue<long>();
                if (at > now || at <= now - HistoryTtl ||
                    field == "urls" && entry.Value!["until"]!.GetValue<long>() <= now)
                {
                    entries.Remove(entry.Key);
                    changed = true;
                }
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

    private static void CheckJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw MusicPlaybackCacheDataException.Corrupt();
                CheckJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckJson(item);
    }

    private static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw MusicPlaybackCacheDataException.Corrupt();
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != fields.Length || fields.Any(field => !names.Contains(field, StringComparer.Ordinal)))
            throw MusicPlaybackCacheDataException.Corrupt();
    }

    private static long CanonicalLong(JsonElement value, bool allowZero = false)
    {
        string raw = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
        if (raw.Length is < 1 or > 19 || raw.Length > 1 && raw[0] == '0' ||
            raw.Any(character => character is < '0' or > '9') ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long result) ||
            (allowZero ? result < 0 : result <= 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }

    private static long? OptionalLong(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : CanonicalLong(value);

    private static double CanonicalDouble(JsonElement value, bool allowZero = false)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) || (allowZero ? result < 0 : result <= 0) ||
            value.GetRawText() != JsonSerializer.Serialize(result))
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

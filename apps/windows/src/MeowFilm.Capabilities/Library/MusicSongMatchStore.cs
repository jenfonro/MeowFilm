using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Disposable recording facts and scoped observations, separate from local membership and playback data.</summary>
internal sealed class MusicSongMatchStore(CacheDocumentStore store)
{
    internal const string Namespace = "music.song-matches.v1";
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<IReadOnlyList<JsonObject>> ReadAsync(CancellationToken cancellationToken)
    {
        try { return await ReadCurrentAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return []; }
    }

    internal async Task WriteAsync(IReadOnlyList<JsonObject> entries, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? encoded = null;
        try
        {
            encoded = Encode(entries, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            store.Save(Namespace, encoded);
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
        finally
        {
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<JsonObject>> ReadCurrentAsync(CancellationToken token)
    {
        var saved = await store.ReadAsync(Namespace, token).ConfigureAwait(false);
        try
        {
            if (saved.Data is null || saved.Data.Length > MaximumBytes) return [];
            using var document = JsonDocument.Parse(saved.Data);
            var result = new List<JsonObject>();
            foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                result.Add(DecodeEntry(entry));
            }
            return result;
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error))
        { return []; }
        finally { if (saved.Data is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Encode(IReadOnlyList<JsonObject> entries, CancellationToken token)
    {
        var retained = new Queue<byte[]>();
        long length = "{\"entries\":[]}"u8.Length;
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                byte[]? bytes = JsonSerializer.SerializeToUtf8Bytes(PersistentEntry(entry), LJ.Json);
                try
                {
                    length += bytes.Length + (retained.Count == 0 ? 0 : 1);
                    retained.Enqueue(bytes);
                    bytes = null;
                    while (length > MaximumBytes)
                    {
                        byte[] oldest = retained.Dequeue();
                        length -= oldest.Length + (retained.Count == 0 ? 0 : 1);
                        CryptographicOperations.ZeroMemory(oldest);
                    }
                }
                finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
            }
            using var output = new MemoryStream((int)length);
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("entries");
                foreach (byte[] entry in retained) writer.WriteRawValue(entry, skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return output.ToArray();
        }
        catch (MusicPlaybackCacheDataException) { throw; }
        catch (MusicLibraryException) { throw MusicPlaybackCacheDataException.Corrupt(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        { throw MusicPlaybackCacheDataException.Corrupt(); }
        finally { foreach (byte[] bytes in retained) CryptographicOperations.ZeroMemory(bytes); }
    }

    // Project the owner's facts only. Candidates, credentials and request authority are not persisted.
    private static JsonObject PersistentEntry(JsonObject entry)
    {
        var platforms = new JsonObject();
        foreach (var pair in entry["platforms"]!.AsObject())
        {
            var status = new JsonObject();
            foreach (string field in new[] { "state", "checkedAt", "context", "query", "code", "page", "pages", "limit", "matchedAt" })
                status[field] = pair.Value![field]?.DeepClone();
            platforms[pair.Key] = status;
        }
        RejectUrls(JsonSerializer.SerializeToElement(platforms));
        var song = NativeMusicSong.Normalize(entry["song"]);
        foreach (var source in song["sources"]!.AsArray())
        {
            RejectUrls(JsonSerializer.SerializeToElement(source!["sourceKey"]));
            foreach (var field in source["musicInfo"]!.AsObject())
                if (field.Key is not ("img" or "name" or "singer" or "albumName"))
                    RejectUrls(JsonSerializer.SerializeToElement(field.Value));
        }
        return new JsonObject
        {
            ["key"] = entry["key"]?.DeepClone(),
            ["song"] = song,
            ["platforms"] = platforms,
            ["excluded"] = entry["excluded"]?.DeepClone()
        };
    }

    private static JsonObject DecodeEntry(JsonElement entry)
    {
        string key = Text(entry.GetProperty("key"), 16);
        Hash(key);
        var rawSong = entry.GetProperty("song");
        if (rawSong.ValueKind != JsonValueKind.Object) throw MusicPlaybackCacheDataException.Corrupt();
        var songFields = new JsonObject();
        foreach (string field in new[] { "songId", "title", "artist", "album", "duration", "cover_url" })
            songFields[field] = JsonNode.Parse(rawSong.GetProperty(field).GetRawText());
        var decodedSongSources = new JsonArray();
        foreach (var source in rawSong.GetProperty("sources").EnumerateArray())
        {
            string platform = source.GetProperty("source").GetString()!;
            string sourceKey = source.GetProperty("sourceKey").GetString()!;
            var info = JsonNode.Parse(source.GetProperty("musicInfo").GetRawText())!.AsObject();
            if (info["source"]?.GetValue<string>() != platform) throw MusicPlaybackCacheDataException.Corrupt();
            var decodedSource = NativeMusicSong.CreateSource(platform, info,
                info["name"]?.GetValue<string>() ?? "", info["singer"]?.GetValue<string>() ?? "",
                info["albumName"]?.GetValue<string>() ?? "", info["duration"]?.GetValue<double>() ?? 0,
                info["img"]?.GetValue<string>() ?? "");
            if (decodedSource["sourceKey"]!.GetValue<string>() != sourceKey)
                throw MusicPlaybackCacheDataException.Corrupt();
            RejectUrls(JsonSerializer.SerializeToElement(sourceKey));
            foreach (var field in decodedSource["musicInfo"]!.AsObject())
                if (field.Key is not ("img" or "name" or "singer" or "albumName"))
                    RejectUrls(JsonSerializer.SerializeToElement(field.Value));
            decodedSongSources.Add(decodedSource);
        }
        songFields["sources"] = decodedSongSources;
        var song = NativeMusicSong.Normalize(songFields);
        var platforms = entry.GetProperty("platforms");
        if (platforms.ValueKind != JsonValueKind.Object) throw MusicPlaybackCacheDataException.Corrupt();
        var decodedPlatforms = new JsonObject();
        foreach (var platform in platforms.EnumerateObject())
        {
            if (!NativeMusicSong.Platforms.Contains(platform.Name)) throw MusicPlaybackCacheDataException.Corrupt();
            var status = platform.Value;
            string state = Text(status.GetProperty("state"), 8);
            if (state is not ("matched" or "notFound" or "failed"))
                throw MusicPlaybackCacheDataException.Corrupt();
            var at = status.GetProperty("checkedAt");
            if (at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out long checkedAt) || checkedAt < 0)
                throw MusicPlaybackCacheDataException.Corrupt();
            string context = Text(status.GetProperty("context"), 16);
            if (context.Length > 0) Hash(context);
            string query = Text(status.GetProperty("query"), 256);
            string code = Text(status.GetProperty("code"), 80);
            int page = Positive(status.GetProperty("page")), pages = Positive(status.GetProperty("pages"));
            int limit = Positive(status.GetProperty("limit"));
            var paging = MusicSearchPaging.For(platform.Name);
            if (pages > paging.PagesPerRequest || (long)limit * pages > MusicSearchPaging.MaxItemsPerRequest ||
                page + (long)pages - 1 > int.MaxValue) throw MusicPlaybackCacheDataException.Corrupt();
            var matched = status.GetProperty("matchedAt");
            if (matched.ValueKind != JsonValueKind.Null &&
                (matched.ValueKind != JsonValueKind.Number || !matched.TryGetInt64(out long matchedAt) ||
                    matchedAt < 0 || matchedAt > checkedAt)) throw MusicPlaybackCacheDataException.Corrupt();
            decodedPlatforms[platform.Name] = new JsonObject
            {
                ["state"] = state, ["checkedAt"] = checkedAt, ["context"] = context,
                ["query"] = query, ["code"] = code, ["page"] = page, ["pages"] = pages, ["limit"] = limit,
                ["matchedAt"] = matched.ValueKind == JsonValueKind.Null ? null : JsonValue.Create(matched.GetInt64())
            };
        }
        var excluded = entry.GetProperty("excluded");
        if (excluded.ValueKind != JsonValueKind.Array) throw MusicPlaybackCacheDataException.Corrupt();
        var decodedExcluded = new JsonArray();
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in excluded.EnumerateArray())
        {
            string owner = Text(scope.GetProperty("key"), 16); Hash(owner);
            if (!scopes.Add(owner)) throw MusicPlaybackCacheDataException.Corrupt();
            var sources = scope.GetProperty("sources");
            if (sources.ValueKind != JsonValueKind.Array) throw MusicPlaybackCacheDataException.Corrupt();
            var decodedSources = new JsonArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources.EnumerateArray())
            {
                string sourceKey = Text(source, 2048);
                if (!seen.Add(sourceKey)) throw MusicPlaybackCacheDataException.Corrupt();
                using var parsed = JsonDocument.Parse(sourceKey, new JsonDocumentOptions { MaxDepth = 2 });
                var parts = parsed.RootElement;
                if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 3 ||
                    !NativeMusicSong.Platforms.Contains(Text(parts[0], 2)) ||
                    !LJ.Id(Text(parts[1], 2048)) ||
                    parts[0].GetString() != "kg" && Text(parts[2], 2048).Length != 0 ||
                    LJ.Stringify(JsonNode.Parse(sourceKey)) != sourceKey)
                    throw MusicPlaybackCacheDataException.Corrupt();
                _ = Text(parts[2], 2048);
                RejectUrls(parts);
                decodedSources.Add(sourceKey);
            }
            decodedExcluded.Add(new JsonObject { ["key"] = owner, ["sources"] = decodedSources });
        }
        // Canonical songs already whitelist public artwork locators. Do not confuse those
        // stable metadata URLs with playback URLs or credentials.
        RejectUrls(JsonSerializer.SerializeToElement(decodedPlatforms));
        return new JsonObject
        {
            ["key"] = key, ["song"] = song, ["platforms"] = decodedPlatforms, ["excluded"] = decodedExcluded
        };
    }



    private static void RejectUrls(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            if (text.Contains("://", StringComparison.Ordinal) ||
                text.TrimStart().StartsWith("//", StringComparison.Ordinal) ||
                Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file" or "data" or "blob")
                throw MusicPlaybackCacheDataException.Corrupt();
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) RejectUrls(property.Value);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectUrls(item);
    }



    private static string Text(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum)
            throw MusicPlaybackCacheDataException.Corrupt();
        return text;
    }

    private static int Positive(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number <= 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        return number;
    }

    private static void Hash(string value)
    {
        if (value.Length != 16 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw MusicPlaybackCacheDataException.Corrupt();
    }
}

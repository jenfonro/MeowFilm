using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Disposable matching observations, separate from canonical library and playback data.</summary>
internal sealed class MusicSongMatchStore(IDocumentStore store)
{
    internal const string Namespace = "music.song-matches.v1";
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record Snapshot(IReadOnlyList<JsonObject> Entries, DocumentFingerprint Fingerprint);

    internal async Task<IReadOnlyList<JsonObject>> ReadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return saved.Entries;
        }
        catch (DocumentStoreException error) when (MusicPlaybackCacheDataException.IsStoredFormatFailure(error))
        { throw MusicPlaybackCacheDataException.FromStore(error); }
        catch (DocumentStoreException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error))
        { return []; }
        finally { gate.Release(); }
    }

    internal async Task WriteAsync(IReadOnlyList<JsonObject> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        byte[] encoded = Encode(entries, cancellationToken);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var before = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var expected = DocumentFingerprint.FromBytes(encoded);
                if (before.Fingerprint == expected ||
                    before.Fingerprint == DocumentFingerprint.Missing && encoded.AsSpan().SequenceEqual("{\"version\":1,\"entries\":[]}"u8))
                    return;
                DocumentFingerprint receipt;
                try
                {
                    // After CAS admission, cancellation cannot imply that the write was rolled back.
                    receipt = await store.WriteAsync(Namespace, encoded, before.Fingerprint, CancellationToken.None).ConfigureAwait(false);
                }
                catch (DocumentStoreException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error))
                {
                    // Validate the durable outcome, then abandon this snapshot rather than replaying it
                    // over a competing write. A future caller supplies a fresh eligibility snapshot.
                    _ = await ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                if (receipt != expected)
                {
                    var actual = await ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                    if (actual.Fingerprint != expected) throw new MusicPlaybackCacheCommitException();
                }
            }
            catch (DocumentStoreException error) when (MusicPlaybackCacheDataException.IsStoredFormatFailure(error))
            { throw MusicPlaybackCacheDataException.FromStore(error); }
            catch (DocumentStoreException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error))
            { }
            finally { gate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    private async Task<Snapshot> ReadCurrentAsync(CancellationToken token)
    {
        var saved = await store.ReadAsync(Namespace, token).ConfigureAwait(false);
        try
        {
            if (saved.Data is null)
            {
                if (saved.Fingerprint != DocumentFingerprint.Missing) throw MusicPlaybackCacheDataException.Corrupt();
                return new([], saved.Fingerprint);
            }
            if (saved.Data.Length == 0 || DocumentFingerprint.FromBytes(saved.Data) != saved.Fingerprint)
                throw MusicPlaybackCacheDataException.Corrupt();
            if (saved.Data.Length > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
            using var document = JsonDocument.Parse(saved.Data, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            CheckJson(root);
            Fields(root, "version", "entries");
            if (root.GetProperty("version").GetRawText() != "1")
                throw MusicPlaybackCacheDataException.UnsupportedVersion();
            var entries = root.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array) throw MusicPlaybackCacheDataException.Corrupt();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<JsonObject>();
            foreach (var entry in entries.EnumerateArray())
            {
                string key = ValidateEntry(entry);
                if (!keys.Add(key)) throw MusicPlaybackCacheDataException.Corrupt();
                result.Add(JsonNode.Parse(entry.GetRawText())!.AsObject());
            }
            return new(result, saved.Fingerprint);
        }
        catch (MusicPlaybackCacheDataException) { throw; }
        catch (MusicLibraryException) { throw MusicPlaybackCacheDataException.Corrupt(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        { throw MusicPlaybackCacheDataException.Corrupt(); }
        finally { if (saved.Data is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Encode(IReadOnlyList<JsonObject> entries, CancellationToken token)
    {
        var retained = new Queue<byte[]>();
        long length = "{\"version\":1,\"entries\":[]}"u8.Length;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                byte[]? bytes = JsonSerializer.SerializeToUtf8Bytes(entry, LJ.Json);
                try
                {
                    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                    CheckJson(document.RootElement);
                    string key = ValidateEntry(document.RootElement);
                    if (!keys.Add(key)) throw MusicPlaybackCacheDataException.Corrupt();
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
                writer.WriteNumber("version", 1);
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

    /// <summary>Lets the owner skip one ineligible derived entry instead of failing every write.</summary>
    internal static bool Accepts(JsonObject entry)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(entry, LJ.Json),
                new JsonDocumentOptions { MaxDepth = 32 });
            CheckJson(document.RootElement);
            _ = ValidateEntry(document.RootElement);
            return true;
        }
        catch (Exception error) when (error is MusicPlaybackCacheDataException or MusicLibraryException or JsonException or
            InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        { return false; }
    }

    private static string ValidateEntry(JsonElement entry)
    {
        Fields(entry, "key", "song", "platforms", "excluded");
        string key = Text(entry.GetProperty("key"), 16);
        Hash(key);
        var rawSong = entry.GetProperty("song");
        if (rawSong.ValueKind != JsonValueKind.Object) throw MusicPlaybackCacheDataException.Corrupt();
        var song = JsonNode.Parse(rawSong.GetRawText())!.AsObject();
        if (!JsonNode.DeepEquals(song, NativeMusicSong.Normalize(song))) throw MusicPlaybackCacheDataException.Corrupt();
        foreach (var source in rawSong.GetProperty("sources").EnumerateArray())
        {
            RejectUrls(source.GetProperty("sourceKey"));
            foreach (var field in source.GetProperty("musicInfo").EnumerateObject())
                if (field.Name is not ("img" or "name" or "singer" or "albumName")) RejectUrls(field.Value);
        }
        var platforms = entry.GetProperty("platforms");
        if (platforms.ValueKind != JsonValueKind.Object) throw MusicPlaybackCacheDataException.Corrupt();
        foreach (var platform in platforms.EnumerateObject())
        {
            if (!NativeMusicSong.Platforms.Contains(platform.Name)) throw MusicPlaybackCacheDataException.Corrupt();
            var status = platform.Value;
            Fields(status, "state", "checkedAt", "context", "query", "code");
            if (Text(status.GetProperty("state"), 8) is not ("matched" or "notFound" or "failed"))
                throw MusicPlaybackCacheDataException.Corrupt();
            var at = status.GetProperty("checkedAt");
            if (at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out long checkedAt) || checkedAt < 0)
                throw MusicPlaybackCacheDataException.Corrupt();
            string context = Text(status.GetProperty("context"), 16);
            if (context.Length > 0) Hash(context);
            _ = Text(status.GetProperty("query"), 256);
            _ = Text(status.GetProperty("code"), 80);
        }
        var excluded = entry.GetProperty("excluded");
        if (excluded.ValueKind != JsonValueKind.Array) throw MusicPlaybackCacheDataException.Corrupt();
        foreach (var source in excluded.EnumerateArray())
        {
            string sourceKey = Text(source, 2048);
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
        }
        // Canonical songs already whitelist public artwork locators. Do not confuse those
        // stable metadata URLs with playback URLs or credentials.
        RejectUrls(platforms);
        return key;
    }

    private static void CheckJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw MusicPlaybackCacheDataException.Corrupt();
                CheckJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckJson(item);
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

    private static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw MusicPlaybackCacheDataException.Corrupt();
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != fields.Length || fields.Any(field => !names.Contains(field, StringComparer.Ordinal)))
            throw MusicPlaybackCacheDataException.Corrupt();
    }

    private static string Text(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum)
            throw MusicPlaybackCacheDataException.Corrupt();
        return text;
    }

    private static void Hash(string value)
    {
        if (value.Length != 16 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw MusicPlaybackCacheDataException.Corrupt();
    }
}

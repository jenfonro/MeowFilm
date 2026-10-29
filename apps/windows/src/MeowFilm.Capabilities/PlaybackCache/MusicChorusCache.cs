using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed class MusicChorusCache(IProtectedDocumentStore store, TimeProvider? time = null)
{
    internal const string Namespace = "music.chorus.v1";
    private const int MaximumBytes = 65536, MaximumEntries = 100;
    private const long Ttl = 30L * 86400000;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private sealed record Entry(long At, double Start, double End);

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    internal static string Key(string owner, string song, string platform, string nativeId, double duration) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
            owner, song, platform, nativeId, duration }))));

    internal Task<(double Start, double End)?> ReadAsync(string key, double duration, CancellationToken token) =>
        Access(key, duration, null, token);

    internal async Task PutAsync(string key, double duration, double start, double end, CancellationToken token) =>
        _ = await Access(key, duration, (start, end), token).ConfigureAwait(false);

    private async Task<(double Start, double End)?> Access(string key, double duration,
        (double Start, double End)? point, CancellationToken token)
    {
        if (!LowerHex(key, 64) || !double.IsFinite(duration) || duration <= 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var saved = await store.ReadAsync(Namespace, token).ConfigureAwait(false);
            Dictionary<string, Entry> entries;
            try { entries = Decode(saved); }
            finally { if (saved.Data is not null) CryptographicOperations.ZeroMemory(saved.Data); }

            long now = Now;
            foreach (var pair in entries.ToArray())
                if (pair.Value.At > now || pair.Value.At <= now - Ttl)
                    entries.Remove(pair.Key);

            if (point is { } value)
            {
                if (!Fits(value.Start, value.End, duration)) return null;
                entries.Remove(key);
                entries[key] = new(now, value.Start, value.End);
                while (entries.Count > MaximumEntries) entries.Remove(entries.First().Key);
                await Write(entries, saved.Fingerprint, token).ConfigureAwait(false);
            }

            return entries.TryGetValue(key, out var hit) && Fits(hit.Start, hit.End, duration)
                ? (hit.Start, hit.End)
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsStoredFormatFailure(error))
        { throw MusicPlaybackCacheDataException.FromStore(error); }
        catch (ProtectedDocumentException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error))
        { return null; }
        finally { gate.Release(); }
    }

    private static Dictionary<string, Entry> Decode(ProtectedDocument saved)
    {
        if (saved.Data is null)
        {
            if (saved.Fingerprint != DocumentFingerprint.Missing)
                throw MusicPlaybackCacheDataException.Corrupt();
            return new(StringComparer.Ordinal);
        }
        if (saved.Data.Length == 0 || DocumentFingerprint.FromBytes(saved.Data) != saved.Fingerprint)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (saved.Data.Length > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
        try
        {
            using var document = JsonDocument.Parse(saved.Data, new JsonDocumentOptions { MaxDepth = 4 });
            CheckJson(document.RootElement);
            RequireFields(document.RootElement, "schema", "entries");
            var schema = document.RootElement.GetProperty("schema");
            if (schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != "1")
                throw MusicPlaybackCacheDataException.UnsupportedVersion();
            var rawEntries = document.RootElement.GetProperty("entries");
            if (rawEntries.ValueKind != JsonValueKind.Object)
                throw MusicPlaybackCacheDataException.Corrupt();

            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var property in rawEntries.EnumerateObject())
            {
                if (entries.Count >= MaximumEntries) throw MusicPlaybackCacheDataException.Limit();
                if (!LowerHex(property.Name, 64)) throw MusicPlaybackCacheDataException.Corrupt();
                RequireFields(property.Value, "at", "start", "end");
                long at = CanonicalLong(property.Value.GetProperty("at"));
                double start = CanonicalDouble(property.Value.GetProperty("start"));
                double end = CanonicalDouble(property.Value.GetProperty("end"));
                if (start < 0 || end <= start || !entries.TryAdd(property.Name, new(at, start, end)))
                    throw MusicPlaybackCacheDataException.Corrupt();
            }
            return entries;
        }
        catch (MusicPlaybackCacheDataException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        { throw MusicPlaybackCacheDataException.Corrupt(); }
    }

    private async Task Write(Dictionary<string, Entry> entries, DocumentFingerprint before, CancellationToken token)
    {
        var outputEntries = new JsonObject();
        foreach (var pair in entries)
            outputEntries[pair.Key] = new JsonObject
            {
                ["at"] = pair.Value.At,
                ["start"] = pair.Value.Start,
                ["end"] = pair.Value.End
            };
        byte[] output = JsonSerializer.SerializeToUtf8Bytes(
            new JsonObject { ["schema"] = 1, ["entries"] = outputEntries });
        try
        {
            if (output.Length > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
            var expected = DocumentFingerprint.FromBytes(output);
            _ = Decode(new ProtectedDocument(output, expected));
            var receipt = await store.WriteAsync(Namespace, output, before, token).ConfigureAwait(false);
            if (receipt != expected) throw new MusicPlaybackCacheCommitException();
        }
        finally { CryptographicOperations.ZeroMemory(output); }
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

    private static long CanonicalLong(JsonElement value)
    {
        string raw = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
        if (raw.Length is < 1 or > 19 || raw.Length > 1 && raw[0] == '0' ||
            raw.Any(character => character is < '0' or > '9') ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long result) || result <= 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }

    private static double CanonicalDouble(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) || value.GetRawText() != JsonSerializer.Serialize(result))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }

    private static bool LowerHex(string value, int length) =>
        value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Fits(double start, double end, double duration) =>
        double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start && end <= duration;
}

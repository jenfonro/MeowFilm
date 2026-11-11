using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed class MusicChorusCache(CacheDocumentStore store, TimeProvider? time = null)
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
        if (point is not null) await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var saved = point is not null ? store.ReadMemory(Namespace) :
                await store.ReadAsync(Namespace, token).ConfigureAwait(false);
            var entries = Decode(saved);

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
                await Write(entries, token).ConfigureAwait(false);
            }

            return entries.TryGetValue(key, out var hit) && Fits(hit.Start, hit.End, duration)
                ? (hit.Start, hit.End)
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error))
        { return null; }
        finally { if (point is not null) gate.Release(); }
    }

    private static Dictionary<string, Entry> Decode(DocumentSnapshot saved)
    {
        if (saved.Data is null || saved.Data.Length > MaximumBytes) return new(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(saved.Data, new JsonDocumentOptions { MaxDepth = 4 });
            var rawEntries = document.RootElement.GetProperty("entries");
            if (rawEntries.ValueKind != JsonValueKind.Object)
                throw MusicPlaybackCacheDataException.Corrupt();

            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var property in rawEntries.EnumerateObject())
            {
                if (entries.Count >= MaximumEntries) throw MusicPlaybackCacheDataException.Limit();
                if (!LowerHex(property.Name, 64)) throw MusicPlaybackCacheDataException.Corrupt();
                long at = CanonicalLong(property.Value.GetProperty("at"));
                double start = CanonicalDouble(property.Value.GetProperty("start"));
                double end = CanonicalDouble(property.Value.GetProperty("end"));
                if (start < 0 || end <= start || !entries.TryAdd(property.Name, new(at, start, end)))
                    throw MusicPlaybackCacheDataException.Corrupt();
            }
            return entries;
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error))
        { return new(StringComparer.Ordinal); }
    }

    private Task Write(Dictionary<string, Entry> entries, CancellationToken token)
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
            new JsonObject { ["entries"] = outputEntries });
        if (output.Length > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
        token.ThrowIfCancellationRequested();
        store.Save(Namespace, output);
        return Task.CompletedTask;
    }





    private static long CanonicalLong(JsonElement value)
    {
        if (!value.TryGetInt64(out long result) || result <= 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }

    private static double CanonicalDouble(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }

    private static bool LowerHex(string value, int length) =>
        value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Fits(double start, double end, double duration) =>
        double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start && end <= duration;
}

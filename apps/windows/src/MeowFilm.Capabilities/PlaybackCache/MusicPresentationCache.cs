using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>URL image bytes and song-referenced lyrics share one bounded resource cache.</summary>
internal sealed class MusicPresentationCache(string? directory = null, TimeProvider? time = null) : IArtworkByteCache, IAsyncDisposable
{
    internal const string DirectoryName = "music-presentation-v2";
    internal const long Ttl = 30L * 86400000;
    private readonly FileResourceCache resources = new(directory, 8L * 1024 * 1024,
        ArtworkService.MaximumCacheEntryBytes, TimeSpan.FromMilliseconds(Ttl), time);
    private long lyricVersion;
    public long Epoch => resources.Epoch("image");
    public TimeSpan Lifetime => TimeSpan.FromMilliseconds(Ttl);
    internal long LyricEpoch => resources.Epoch("lyrics");
    internal long LyricVersion => Interlocked.Read(ref lyricVersion);
    internal static string SongKey(JsonObject song) => LJ.S(song["songId"]);

    internal async Task<JsonObject?> ReadLyricsAsync(string songId, CancellationToken token)
    {
        var stored = await resources.ReadAsync("lyrics", songId, token).ConfigureAwait(false);
        if (stored is null || stored.Content.Length > 512 * 1024) return null;
        try
        {
            var row = JsonNode.Parse(stored.Content)?.AsObject();
            return row?["songId"] is JsonValue song && song.TryGetValue<string>(out var id) && id == songId &&
                row["sourceKey"] is JsonValue source && source.TryGetValue<string>(out _) &&
                row["lines"] is JsonArray ? row : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal async Task SaveLyricsAsync(string songId, string sourceKey, JsonArray lines, long epoch, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(LJ.Obj(("songId", songId), ("sourceKey", sourceKey), ("lines", lines)), LJ.Json);
        if (bytes.Length > 512 * 1024) throw new DocumentStoreException(DocumentStoreError.Limit);
        await resources.WriteAsync("lyrics", songId, bytes, epoch, token).ConfigureAwait(false);
        Interlocked.Increment(ref lyricVersion);
    }

    public async Task<ArtworkBytes?> ReadAsync(string address, CancellationToken token)
    {
        var stored = await resources.ReadAsync("image", address, token).ConfigureAwait(false);
        return stored is null ? null : new(stored.Content, stored.ExpiresAt!.Value);
    }

    public Task WriteAsync(string address, ReadOnlyMemory<byte> bytes, long epoch, CancellationToken token) =>
        resources.WriteAsync("image", address, bytes, epoch, token);
    public Task RemoveAsync(string address, long epoch, CancellationToken token) =>
        resources.RemoveAsync("image", address, epoch, token);

    internal Task ClearImagesAsync(CancellationToken token) => resources.ClearAsync("image", token);
    internal Task ClearAllAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref lyricVersion);
        return resources.ClearAsync(null, token);
    }
    internal async Task TouchPlayedAsync(string songId, string image, CancellationToken token)
    {
        await resources.TouchAsync("lyrics", songId, LyricEpoch, token).ConfigureAwait(false);
        if (image.Length > 0)
            await resources.TouchAsync("image", image, Epoch, token).ConfigureAwait(false);
    }
    internal Task DrainAsync() => resources.DrainAsync();
    public ValueTask DisposeAsync() => resources.DisposeAsync();
}

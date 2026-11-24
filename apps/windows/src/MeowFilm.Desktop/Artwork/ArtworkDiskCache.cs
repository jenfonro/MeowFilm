using MeowFilm.Platform.Storage;

namespace MeowFilm.Desktop.Artwork;

internal sealed class ArtworkDiskCache(string directory) : IAsyncDisposable
{
    private readonly FileResourceCache cache = new(directory, 256L * 1024 * 1024, ArtworkService.MaximumCacheEntryBytes);

    internal Task<byte[]?> ReadAsync(string key, CancellationToken token) =>
        cache.ReadAsync(key, token);

    internal Task RemoveAsync(string key, CancellationToken token) =>
        cache.RemoveAsync(key, cache.CurrentEpoch, token);

    internal Task WriteAsync(string key, byte[] encoded, CancellationToken token) =>
        cache.WriteAsync(key, encoded, cache.CurrentEpoch, token);

    internal Task ClearAsync(CancellationToken token) =>
        cache.ClearAsync(token);

    internal ResourceCacheStats GetStats() => cache.GetStats();

    public ValueTask DisposeAsync() => cache.DisposeAsync();
}

using MeowFilm.Platform.Storage;

namespace MeowFilm.Desktop.Artwork;

internal sealed class ArtworkDiskCache(string directory) : IAsyncDisposable
{
    private readonly FileResourceCache cache = new(directory, 256L * 1024 * 1024, ArtworkService.MaximumCacheEntryBytes);

    internal long CurrentEpoch => cache.CurrentEpoch;

    internal Task<byte[]?> ReadAsync(string key, CancellationToken token) =>
        cache.ReadAsync(key, token);

    internal Task RemoveAsync(string key, long expectedEpoch, CancellationToken token) =>
        cache.RemoveAsync(key, expectedEpoch, token);

    internal Task WriteAsync(string key, byte[] encoded, long expectedEpoch, CancellationToken token) =>
        cache.WriteAsync(key, encoded, expectedEpoch, token);

    internal Task ClearAsync(CancellationToken token) =>
        cache.ClearAsync(token);

    internal ResourceCacheStats GetStats() => cache.GetStats();

    public ValueTask DisposeAsync() => cache.DisposeAsync();
}

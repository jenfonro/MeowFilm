using MeowFilm.Platform.Storage;

namespace MeowFilm.Desktop.Artwork;

internal sealed class ArtworkDiskCache(string directory) : IAsyncDisposable
{
    private const string Group = "artwork";
    private readonly FileResourceCache cache = new(directory, 256L * 1024 * 1024, ArtworkService.MaximumCacheEntryBytes);

    internal async Task<byte[]?> ReadAsync(string key, CancellationToken token) =>
        (await cache.ReadAsync(Group, key, token).ConfigureAwait(false))?.Content;

    internal Task RemoveAsync(string key, CancellationToken token) =>
        cache.RemoveAsync(Group, key, cache.Epoch(Group), token);

    internal Task WriteAsync(string key, byte[] encoded, CancellationToken token) =>
        cache.WriteAsync(Group, key, encoded, cache.Epoch(Group), token);

    public ValueTask DisposeAsync() => cache.DisposeAsync();
}

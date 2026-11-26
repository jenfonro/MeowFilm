using System.Collections.Frozen;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Desktop.Artwork;

internal sealed class ArtworkDiskCache(string directory) : IAsyncDisposable
{
    private const long CapacityBytes = 256L * 1024 * 1024;
    private readonly RawResourceDirectory files = new(directory, "artwork");
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, (string Id, int Bytes, LinkedListNode<string> Node)> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = new();
    private long totalBytes, epoch;
    private bool closed, swept;

    internal long CurrentEpoch => Volatile.Read(ref epoch);

    internal Task<byte[]?> ReadAsync(string key, CancellationToken token)
    {
        string id;
        int bytes;
        lock (gate)
        {
            if (closed || !entries.TryGetValue(key, out var entry)) return Task.FromResult<byte[]?>(null);
            if (!ReferenceEquals(lru.First, entry.Node))
            {
                lru.Remove(entry.Node);
                lru.AddFirst(entry.Node);
            }
            id = entry.Id;
            bytes = entry.Bytes;
        }
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var data = files.Read(id, bytes);
            if (data is null)
            {
                lock (gate)
                {
                    if (entries.TryGetValue(key, out var current) && current.Id == id)
                    {
                        entries.Remove(key);
                        lru.Remove(current.Node);
                        totalBytes -= current.Bytes;
                    }
                }
            }
            return data;
        }, token);
    }

    internal async Task WriteAsync(string key, byte[] encoded, long expectedEpoch, CancellationToken token)
    {
        if (encoded.Length is <= 0 or > ArtworkService.MaximumCacheEntryBytes) return;
        await io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (gate) if (closed || epoch != expectedEpoch) return;
            if (!swept)
            {
                swept = true;
                files.RemoveUnreferenced(FrozenSet<string>.Empty);
            }
            var (id, _) = files.Write(encoded);
            List<string> evicted = [];
            lock (gate)
            {
                if (closed || epoch != expectedEpoch)
                {
                    evicted.Add(id);
                }
                else
                {
                    if (entries.Remove(key, out var prior))
                    {
                        lru.Remove(prior.Node);
                        totalBytes -= prior.Bytes;
                        evicted.Add(prior.Id);
                    }
                    while (totalBytes + encoded.Length > CapacityBytes && lru.Last is { } tail)
                    {
                        var victimKey = tail.Value;
                        lru.RemoveLast();
                        if (entries.Remove(victimKey, out var victim))
                        {
                            totalBytes -= victim.Bytes;
                            evicted.Add(victim.Id);
                        }
                    }
                    var node = lru.AddFirst(key);
                    entries[key] = (id, encoded.Length, node);
                    totalBytes += encoded.Length;
                }
            }
            foreach (var stale in evicted) files.Delete(stale);
        }
        finally { io.Release(); }
    }

    internal Task RemoveAsync(string key, long expectedEpoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? removedId = null;
        lock (gate)
        {
            if (!closed && epoch == expectedEpoch && entries.Remove(key, out var old))
            {
                lru.Remove(old.Node);
                totalBytes -= old.Bytes;
                removedId = old.Id;
            }
        }
        if (removedId is not null) files.Delete(removedId);
        return Task.CompletedTask;
    }

    internal async Task ClearAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closed) return;
            Interlocked.Increment(ref epoch);
            entries.Clear();
            lru.Clear();
            totalBytes = 0;
        }
        await io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            swept = true;
            files.RemoveUnreferenced(FrozenSet<string>.Empty);
        }
        finally { io.Release(); }
    }

    internal (int EntryCount, long TotalBytes, long CapacityBytes) GetStats()
    {
        lock (gate) return (entries.Count, totalBytes, CapacityBytes);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            Interlocked.Increment(ref epoch);
            entries.Clear();
            lru.Clear();
            totalBytes = 0;
        }
        await io.WaitAsync().ConfigureAwait(false);
        io.Release();
        io.Dispose();
    }
}

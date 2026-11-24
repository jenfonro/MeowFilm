using System.Net.Http;
using System.Text.Json.Serialization;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Desktop.Artwork;

public interface IArtworkLease : IDisposable
{
    ArtworkPixels Image { get; }
}

/// <summary>Optional original-byte persistence, owned by the consuming domain.
/// Epoch fences late writes; acquisition never prolongs persistence lifetime.</summary>
public interface IArtworkByteCache
{
    long Epoch { get; }
    TimeSpan Lifetime { get; }
    Task<ArtworkBytes?> ReadAsync(string address, CancellationToken cancellationToken);
    Task WriteAsync(string address, ReadOnlyMemory<byte> bytes, long epoch, CancellationToken cancellationToken);
    Task RemoveAsync(string address, long epoch, CancellationToken cancellationToken);
}

public sealed record ArtworkBytes(byte[] Content, DateTimeOffset ExpiresAt);

public sealed record ArtworkCacheStats(
    [property: JsonPropertyName("memoryEntryCount")] int MemoryEntryCount,
    [property: JsonPropertyName("memoryBytes")] long MemoryBytes,
    [property: JsonPropertyName("memoryCapacityBytes")] long MemoryCapacityBytes,
    [property: JsonPropertyName("activeLeases")] int ActiveLeases,
    [property: JsonPropertyName("activeFlights")] int ActiveFlights,
    [property: JsonPropertyName("diskEntryCount")] int DiskEntryCount,
    [property: JsonPropertyName("diskBytes")] long DiskBytes,
    [property: JsonPropertyName("diskCapacityBytes")] long DiskCapacityBytes,
    [property: JsonPropertyName("closed")] bool Closed);

public sealed class ArtworkException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>One App cache; public URLs and host-injected trusted relative resources share pixel ownership.</summary>
public sealed class ArtworkService : IAsyncDisposable
{
    public const int MaximumCacheEntryBytes = 4 * 1024 * 1024, CacheBytes = 64 * 1024 * 1024;
    private readonly object gate = new();
    private readonly ArtworkHttp http;
    private readonly ArtworkDiskCache? disk;
    private readonly Func<string, CancellationToken, Task<byte[]>>? readTrusted;
    private readonly IArtworkByteCache? byteCache;
    private readonly Func<string, bool, Task>? loadCompleted;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim execution = new(4, 4);
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> leased = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> lru = new();
    private TaskCompletionSource drained = Completed(), released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int pending;
    private long bytes, epoch;
    private bool closed;
    private sealed class Flight(CancellationToken lifetime, long epoch, long diskEpoch)
    {
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal Task<Entry> Work = null!;
        internal int Readers;
        internal readonly long Epoch = epoch, DiskEpoch = diskEpoch;
    }
    private sealed class Entry
    {
        internal readonly string Key;
        internal readonly ArtworkPixels Image;
        internal readonly long Cost, Epoch, DiskEpoch;
        internal readonly DateTimeOffset? ExpiresAt;
        internal readonly LinkedListNode<Entry> Node;
        internal int Leases;
        internal Entry(string key, ArtworkPixels image, long cost, long epoch, long diskEpoch, DateTimeOffset? expiresAt)
        {
            Key = key; Image = image; Cost = cost; Epoch = epoch; DiskEpoch = diskEpoch; ExpiresAt = expiresAt;
            Node = new LinkedListNode<Entry>(this);
        }
    }
    private sealed class Lease(ArtworkService owner, string key, Entry entry) : IArtworkLease
    {
        private Entry? value = entry;
        private ArtworkService? service = owner;
        public ArtworkPixels Image => Volatile.Read(ref value)?.Image ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose()
        {
            if (Interlocked.Exchange(ref value, null) is { } prior)
                Interlocked.Exchange(ref service, null)!.Release(key, prior);
        }
    }
    public ArtworkService(HttpMessageHandler? handler = null,
        Func<string, CancellationToken, Task<byte[]>>? readTrusted = null,
        IArtworkByteCache? byteCache = null,
        Func<string, bool, Task>? loadCompleted = null, string? cacheDirectory = null)
    {
        http = new(handler); this.readTrusted = readTrusted; this.byteCache = byteCache;
        this.loadCompleted = loadCompleted;
        disk = byteCache is null && cacheDirectory is not null ? new(cacheDirectory) : null;
    }

    public async Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool trusted = address.StartsWith("/", StringComparison.Ordinal);
        if (trusted && readTrusted is null) throw new ArtworkException("ARTWORK_ADDRESS");
        var uri = trusted ? null : ArtworkHttp.Parse(address);
        if (byteCache is not null && (uri is null || uri.UserInfo.Length > 0))
            throw new ArtworkException("ARTWORK_ADDRESS");
        string key = uri?.AbsoluteUri ?? address;
        for (;;)
        {
            Flight flight;
            bool retiring;
            lock (gate)
            {
                CheckOpen();
                if (cache.TryGetValue(key, out var hit) || leased.TryGetValue(key, out hit))
                {
                    bool freshDisk = IsDiskEpochCurrent(hit.DiskEpoch);
                    if (freshDisk && (hit.ExpiresAt is null || hit.ExpiresAt > DateTimeOffset.UtcNow)) return Own(key, hit);
                    RemoveFromCache(key);
                    leased.Remove(key);
                }
                bool registered = flights.TryGetValue(key, out flight!);
                retiring = registered && (flight.Readers == 0 || flight.Epoch != epoch ||
                    !IsDiskEpochCurrent(flight.DiskEpoch));
                if (retiring && flight.Work.IsCompleted)
                {
                    flights.Remove(key);
                    registered = retiring = false;
                }
                if (!retiring)
                {
                    if (!registered)
                    {
                        flight = new(lifetime.Token, epoch, CurrentDiskEpoch);
                        flights.Add(key, flight);
                        if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        flight.Work = Task.Run(() => LoadAsync(key, uri, flight));
                        _ = flight.Work.ContinueWith(t =>
                        {
                            _ = t.Exception;
                            lock (gate)
                            {
                                if (flight.Readers == 0 && flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                                    flights.Remove(key);
                                flight.Cancellation.Dispose();
                                if (--pending == 0) drained.TrySetResult();
                            }
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    flight.Readers++;
                }
            }
            if (retiring)
            {
                // A successor owns neither the retired result nor its failure.
                // It waits for the actual work to end before reading this key again.
                try { await flight.Work.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            try
            {
                var entry = await flight.Work.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    CheckOpen(); cancellationToken.ThrowIfCancellationRequested();
                    return Own(key, entry);
                }
            }
            finally
            {
                bool cancel;
                lock (gate)
                {
                    cancel = --flight.Readers == 0 && !flight.Work.IsCompleted;
                    if (flight.Readers == 0 && flight.Work.IsCompleted &&
                        flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                        flights.Remove(key);
                }
                if (cancel) Cancel(flight.Cancellation);
            }
        }
    }

    private async Task<Entry> LoadAsync(string key, Uri? uri, Flight flight)
    {
        // Waiting owns no image bytes and does not consume the request timeout.
        await execution.WaitAsync(flight.Cancellation.Token).ConfigureAwait(false);
        try
        {
            var result = await LoadCoreAsync(key, uri, flight).ConfigureAwait(false);
            await CompleteAsync(true).ConfigureAwait(false);
            return result;
        }
        catch (ArtworkException error) when (error.Code != "ARTWORK_CLOSED")
        {
            await CompleteAsync(false).ConfigureAwait(false);
            throw;
        }
        finally { execution.Release(); }
        async Task CompleteAsync(bool succeeded)
        {
            if (loadCompleted is null) return;
            lock (gate)
                if (closed || flight.Readers == 0 || flight.Epoch != epoch ||
                    !IsDiskEpochCurrent(flight.DiskEpoch)) return;
            // Resource observation is optional. A metadata save cannot reverse
            // successful decoding or change the real image-load failure.
            try { await loadCompleted(key, succeeded).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task<Entry> LoadCoreAsync(string key, Uri? uri, Flight flight)
    {
        var request = flight.Cancellation;
        request.CancelAfter(TimeSpan.FromSeconds(30));
        byte[]? cached = null;
        ArtworkPixels? image = null;
        bool ownedByEntry = false;
        try
        {
            request.Token.ThrowIfCancellationRequested();
            DateTimeOffset? expiresAt = null;
            if (byteCache is not null)
            {
                try
                {
                    var stored = await byteCache.ReadAsync(key, request.Token).ConfigureAwait(false);
                    cached = stored?.Content;
                    expiresAt = stored?.ExpiresAt;
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { throw; }
                catch { }
            }
            else if (disk is not null)
            {
                try { cached = await disk.ReadAsync(key, request.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { throw; }
                catch { }
            }
            if (cached is not null)
            {
                try
                {
                    if (cached.Length > MaximumCacheEntryBytes) throw new ArtworkException("ARTWORK_LIMIT");
                    image = Decode(cached, request.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    try
                    {
                        if (byteCache is not null) await byteCache.RemoveAsync(key, flight.DiskEpoch, request.Token).ConfigureAwait(false);
                        else if (disk is not null) await disk.RemoveAsync(key, flight.DiskEpoch, request.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { throw; }
                    catch { }
                }
            }
            if (image is null)
            {
                byte[] encoded = uri is null
                    ? await readTrusted!(key, request.Token).ConfigureAwait(false)
                    : await http.ReadAsync(uri, request.Token).ConfigureAwait(false);
                image = Decode(encoded, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (byteCache is not null)
                {
                    expiresAt = DateTimeOffset.UtcNow + byteCache.Lifetime;
                    try
                    {
                        if (encoded.Length <= MaximumCacheEntryBytes)
                            await byteCache.WriteAsync(key, encoded, flight.DiskEpoch, request.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (request.IsCancellationRequested) { throw; }
                    catch { }
                }
                else if (disk is not null)
                {
                    bool persist;
                    lock (gate) persist = !closed && flight.Epoch == epoch && flight.Readers > 0 && IsDiskEpochCurrent(flight.DiskEpoch);
                    if (persist)
                    {
                        try { await disk.WriteAsync(key, encoded, flight.DiskEpoch, request.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (request.IsCancellationRequested) { throw; }
                        catch { }
                    }
                }
            }
            request.Token.ThrowIfCancellationRequested();
            var decoded = image!;
            long cost = decoded.Cost;
            lock (gate)
            {
                CheckOpen(); request.Token.ThrowIfCancellationRequested();
                if (flight.Readers == 0) throw new OperationCanceledException(request.Token);
                var entry = new Entry(key, decoded, cost, flight.Epoch, flight.DiskEpoch, expiresAt);
                if (flight.Epoch == epoch && IsDiskEpochCurrent(flight.DiskEpoch))
                {
                    RemoveFromCache(key);
                    if (bytes + cost > CacheBytes)
                    {
                        var now = DateTimeOffset.UtcNow;
                        for (var cur = lru.Last; cur is not null;)
                        {
                            var prev = cur.Previous;
                            if (cur.Value.ExpiresAt is { } exp && exp <= now) RemoveFromCache(cur.Value.Key);
                            cur = prev;
                        }
                    }
                    while (bytes + cost > CacheBytes && lru.Last is { } oldest) RemoveFromCache(oldest.Value.Key);
                    if (cost <= CacheBytes) { cache.Add(key, entry); lru.AddFirst(entry.Node); bytes += cost; }
                }
                ownedByEntry = true;
                return entry;
            }
        }
        catch (OperationCanceledException)
        {
            lock (gate) if (closed || flight.Readers == 0) throw;
            throw new ArtworkException(request.IsCancellationRequested ? "ARTWORK_TIMEOUT" : "ARTWORK_FAILED");
        }
        catch (ArtworkException) { throw; }
        catch { throw new ArtworkException("ARTWORK_FAILED"); }
        finally { if (!ownedByEntry) image?.ReleaseStaging(); }
    }

    private static ArtworkPixels Decode(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ArtworkRaster.Decode(bytes);
    }

    public void ClearMemory()
    {
        lock (gate) { CheckOpen(); epoch++; lru.Clear(); cache.Clear(); leased.Clear(); bytes = 0; }
    }

    private void TouchLru(Entry entry)
    {
        if (entry.Node.List is not null && !ReferenceEquals(lru.First, entry.Node))
        {
            lru.Remove(entry.Node);
            lru.AddFirst(entry.Node);
        }
    }

    private void RemoveFromCache(string key)
    {
        if (!cache.Remove(key, out var existing)) return;
        if (existing.Node.List is not null) lru.Remove(existing.Node);
        bytes -= existing.Cost;
    }

    public async Task ClearDiskAsync(CancellationToken cancellationToken = default)
    {
        ClearMemory();
        if (disk is not null)
            await disk.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    public ArtworkCacheStats GetStats()
    {
        var diskStats = disk?.GetStats();
        lock (gate)
        {
            return new ArtworkCacheStats(
                MemoryEntryCount: cache.Count,
                MemoryBytes: bytes,
                MemoryCapacityBytes: CacheBytes,
                ActiveLeases: leased.Count,
                ActiveFlights: flights.Count,
                DiskEntryCount: diskStats?.EntryCount ?? 0,
                DiskBytes: diskStats?.TotalBytes ?? 0,
                DiskCapacityBytes: diskStats?.CapacityBytes ?? 0,
                Closed: closed);
        }
    }
    private long CurrentDiskEpoch => byteCache?.Epoch ?? disk?.CurrentEpoch ?? 0;

    private bool IsDiskEpochCurrent(long diskEpoch) =>
        byteCache is not null ? diskEpoch == byteCache.Epoch :
        disk is null || diskEpoch == disk.CurrentEpoch;

    private IArtworkLease Own(string key, Entry entry)
    {
        TouchLru(entry);
        entry.Leases++;
        if (entry.Epoch == epoch && IsDiskEpochCurrent(entry.DiskEpoch)) leased[key] = entry;
        return new Lease(this, key, entry);
    }
    private void Release(string key, Entry entry)
    {
        lock (gate)
        {
            if (--entry.Leases == 0 && leased.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                leased.Remove(key);
        }
    }
    private static void Cancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }
    private void CheckOpen() { if (closed) throw new ArtworkException("ARTWORK_CLOSED"); }
    private static TaskCompletionSource Completed()
    { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }

    public async ValueTask DisposeAsync()
    {
        Task drain; bool owner;
        lock (gate) { owner = !closed; closed = true; drain = drained.Task; }
        if (!owner) { await released.Task.ConfigureAwait(false); return; }
        try
        {
            Cancel(lifetime);
            await drain.ConfigureAwait(false);
            http.Dispose();
            if (disk is not null) await disk.DisposeAsync().ConfigureAwait(false);
            execution.Dispose();
            lock (gate) { lru.Clear(); cache.Clear(); leased.Clear(); bytes = 0; }
            lifetime.Dispose();
            released.TrySetResult();
        }
        catch (Exception error) { released.TrySetException(error); throw; }
    }
}

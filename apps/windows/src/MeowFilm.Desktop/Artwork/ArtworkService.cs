using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace MeowFilm.Desktop.Artwork;

public interface IArtworkLease : IDisposable
{
    BitmapSource Image { get; }
}

public interface IArtworkLoader
{
    Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default);
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

public sealed class ArtworkException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>One App cache; public URLs and host-injected trusted relative resources share pixel ownership.</summary>
public sealed class ArtworkService : IArtworkLoader, IAsyncDisposable
{
    public const int MaximumCacheEntryBytes = 4 * 1024 * 1024, CacheBytes = 32 * 1024 * 1024;
    private readonly object gate = new();
    private readonly ArtworkHttp http;
    private readonly ArtworkDiskCache? disk;
    private readonly Func<string, CancellationToken, Task<byte[]>>? readTrusted;
    private readonly IArtworkByteCache? byteCache;
    private readonly Func<string, bool, Task>? loadCompleted;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> writes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> leased = new(StringComparer.Ordinal);
    private TaskCompletionSource drained = Completed(), released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int pending;
    private long bytes, clock, epoch;
    private bool closed;
    private sealed class Flight(CancellationToken lifetime, long epoch, long diskEpoch)
    {
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal Task<Entry> Work = null!;
        internal int Readers;
        internal readonly long Epoch = epoch, DiskEpoch = diskEpoch;
    }
    private sealed class Entry(BitmapSource image, long cost, long epoch, DateTimeOffset? expiresAt)
    {
        internal readonly BitmapSource Image = image;
        internal readonly long Cost = cost;
        internal long Used;
        internal int Leases;
        internal readonly long Epoch = epoch;
        internal readonly DateTimeOffset? ExpiresAt = expiresAt;
    }
    private sealed class Lease(ArtworkService owner, string key, Entry entry) : IArtworkLease
    {
        private Entry? value = entry;
        private ArtworkService? service = owner;
        public BitmapSource Image => Volatile.Read(ref value)?.Image ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose()
        {
            if (Interlocked.Exchange(ref value, null) is { } prior)
                Interlocked.Exchange(ref service, null)!.Release(key, prior);
        }
    }
    public ArtworkService(HttpMessageHandler? handler = null,
        Func<string, CancellationToken, Task<byte[]>>? readTrusted = null,
        IArtworkByteCache? byteCache = null, TimeProvider? time = null,
        Func<string, bool, Task>? loadCompleted = null, string? cacheDirectory = null)
    {
        http = new(handler); this.readTrusted = readTrusted; this.byteCache = byteCache;
        this.time = time ?? TimeProvider.System; this.loadCompleted = loadCompleted;
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
                    if (hit.ExpiresAt is null || hit.ExpiresAt > time.GetUtcNow()) return Own(key, hit);
                    if (cache.Remove(key, out var expired)) bytes -= expired.Cost;
                    leased.Remove(key);
                }
                bool registered = flights.TryGetValue(key, out flight!);
                retiring = registered && (flight.Readers == 0 || flight.Epoch != epoch ||
                    byteCache is not null && flight.DiskEpoch != byteCache.Epoch);
                if (retiring && flight.Work.IsCompleted)
                {
                    flights.Remove(key);
                    registered = retiring = false;
                }
                if (!retiring)
                {
                    if (!registered)
                    {
                        flight = new(lifetime.Token, epoch, byteCache?.Epoch ?? 0);
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
        async Task CompleteAsync(bool succeeded)
        {
            if (loadCompleted is null) return;
            lock (gate)
                if (closed || flight.Readers == 0 || flight.Epoch != epoch ||
                    byteCache is not null && flight.DiskEpoch != byteCache.Epoch) return;
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
        byte[]? encoded = null;
        try
        {
            request.Token.ThrowIfCancellationRequested();
            Task? writing;
            lock (gate) writes.TryGetValue(key, out writing);
            if (writing is not null) await writing.WaitAsync(request.Token).ConfigureAwait(false);
            BitmapSource? image = null;
            DateTimeOffset? expiresAt = null;
            if (byteCache is not null)
            {
                try
                {
                    var stored = await byteCache.ReadAsync(key, request.Token).ConfigureAwait(false);
                    encoded = stored?.Content;
                    expiresAt = stored?.ExpiresAt;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Recoverable persistence miss; HTTP still owns the real load failure. */ }
            }
            else if (disk is not null) encoded = await disk.ReadAsync(key, request.Token).ConfigureAwait(false);
            if (encoded is not null)
            {
                try
                {
                    if (encoded.Length > MaximumCacheEntryBytes) throw new ArtworkException("ARTWORK_LIMIT");
                    image = await Decode(encoded, request.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    try
                    {
                        if (byteCache is not null) await byteCache.RemoveAsync(key, flight.DiskEpoch, request.Token).ConfigureAwait(false);
                        else if (disk is not null) await disk.RemoveAsync(key, request.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(encoded); encoded = null; }
            }
            bool downloaded = image is null;
            if (downloaded)
            {
                encoded = uri is null ? await readTrusted!(key, request.Token).ConfigureAwait(false)
                    : await http.ReadAsync(uri, request.Token).ConfigureAwait(false);
                image = await Decode(encoded, request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                if (byteCache is not null)
                {
                    expiresAt = time.GetUtcNow() + byteCache.Lifetime;
                    try
                    {
                        if (encoded.Length <= MaximumCacheEntryBytes)
                            await byteCache.WriteAsync(key, encoded, flight.DiskEpoch, request.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* Decoded pixels remain usable even when persistence is unavailable. */ }
                }
            }
            request.Token.ThrowIfCancellationRequested();
            var decoded = image!;
            long cost = checked(((long)decoded.PixelWidth * Math.Max(32, decoded.Format.BitsPerPixel) + 7) / 8 * decoded.PixelHeight);
            lock (gate)
            {
                CheckOpen(); request.Token.ThrowIfCancellationRequested();
                if (flight.Readers == 0) throw new OperationCanceledException(request.Token);
                // Cache ownership and visible leases are independent. Eviction never invalidates an image.
                var entry = new Entry(decoded, cost, flight.Epoch, expiresAt) { Used = ++clock };
                if (flight.Epoch == epoch)
                {
                    if (bytes + cost > CacheBytes)
                    {
                        foreach (var item in cache.OrderBy(pair => pair.Value.Used).ToArray())
                        {
                            cache.Remove(item.Key); bytes -= item.Value.Cost;
                            if (bytes + cost <= CacheBytes) break;
                        }
                    }
                    if (cost <= CacheBytes) { cache.Add(key, entry); bytes += cost; }
                    if (downloaded && disk is not null && encoded!.Length <= MaximumCacheEntryBytes)
                    {
                        Persist(key, encoded!);
                        encoded = null; // The writer now owns and clears these bytes.
                    }
                }
                return entry;
            }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            lock (gate) if (flight.Readers == 0) throw;
            throw new ArtworkException("ARTWORK_TIMEOUT");
        }
        catch (ArtworkException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new ArtworkException("ARTWORK_FAILED"); }
        finally { if (encoded is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(encoded); }
    }

    // Registered under gate before publishing the image. Persistence holds only
    // encoded bytes, so queued I/O cannot pin evicted decoded images.
    private void Persist(string key, byte[] encoded)
    {
        pending++;
        var work = Task.Run(async () =>
        {
            try { await disk!.WriteAsync(key, encoded, lifetime.Token).ConfigureAwait(false); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(encoded); }
        });
        writes[key] = work;
        _ = work.ContinueWith(t =>
        {
            _ = t.Exception;
            lock (gate)
            {
                if (writes.TryGetValue(key, out var current) && ReferenceEquals(current, work)) writes.Remove(key);
                if (--pending == 0) drained.TrySetResult();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static Task<BitmapSource> Decode(byte[] bytes, CancellationToken token)
    {
        return Task.Run(() => ArtworkRaster.Decode(bytes), token);
    }
    /// <summary>Visible leases survive explicit clearing; old work cannot refill memory.</summary>
    public void ClearMemory()
    {
        lock (gate)
        {
            CheckOpen(); epoch++;
            cache.Clear(); leased.Clear(); bytes = 0;
        }
    }
    private IArtworkLease Own(string key, Entry entry)
    {
        entry.Used = ++clock;
        entry.Leases++;
        if (entry.Epoch == epoch) leased[key] = entry;
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
            disk?.Dispose();
            lock (gate) { cache.Clear(); leased.Clear(); bytes = 0; }
            lifetime.Dispose();
            released.TrySetResult();
        }
        catch (Exception error) { released.TrySetException(error); throw; }
    }
}

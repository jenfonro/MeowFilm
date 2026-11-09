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

public sealed class ArtworkException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>One App cache; public URLs and host-injected trusted relative resources share pixel ownership.</summary>
public sealed class ArtworkService : IArtworkLoader, IAsyncDisposable
{
    public const int MaximumBytes = 4 * 1024 * 1024, CacheBytes = 32 * 1024 * 1024;
    private readonly object gate = new();
    private readonly ArtworkHttp http;
    private readonly Func<string, int, CancellationToken, Task<byte[]>>? readTrusted;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> leased = new(StringComparer.Ordinal);
    private TaskCompletionSource drained = Completed(), released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int pending;
    private long bytes, clock;
    private bool closed;
    private sealed class Flight(CancellationToken lifetime)
    {
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal Task<Entry> Work = null!;
        internal int Readers;
    }
    private sealed class Entry(BitmapSource image, long cost)
    {
        internal readonly BitmapSource Image = image;
        internal readonly long Cost = cost;
        internal long Used;
        internal int Leases;
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
        Func<string, int, CancellationToken, Task<byte[]>>? readTrusted = null)
    { http = new(handler); this.readTrusted = readTrusted; }

    public async Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool trusted = address.StartsWith("/", StringComparison.Ordinal);
        if (trusted && readTrusted is null) throw new ArtworkException("ARTWORK_ADDRESS");
        var uri = trusted ? null : ArtworkHttp.Parse(address);
        string key = uri?.AbsoluteUri ?? address;
        Flight flight;
        lock (gate)
        {
            CheckOpen();
            if (cache.TryGetValue(key, out var hit) || leased.TryGetValue(key, out hit)) return Own(key, hit);
            if (!flights.TryGetValue(key, out flight!))
            {
                flight = new(lifetime.Token);
                flights.Add(key, flight);
                if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                flight.Work = LoadAsync(key, uri, flight);
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
                if (flight.Readers == 0 && flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                    flights.Remove(key);
            }
            if (cancel) Cancel(flight.Cancellation);
        }
    }

    private async Task<Entry> LoadAsync(string key, Uri? uri, Flight flight)
    {
        await Task.Yield(); // Register the flight and its first consumer before starting physical work.
        var request = flight.Cancellation;
        request.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            request.Token.ThrowIfCancellationRequested();
            byte[] encoded = uri is null
                ? await readTrusted!(key, MaximumBytes, request.Token).ConfigureAwait(false)
                : await http.ReadAsync(uri, request.Token).ConfigureAwait(false);
            BitmapSource image;
            try
            {
                if (encoded.Length > MaximumBytes) throw new ArtworkException("ARTWORK_LIMIT");
                image = await Task.Run(() => ArtworkRaster.Decode(encoded), request.Token).ConfigureAwait(false);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(encoded); }
            request.Token.ThrowIfCancellationRequested();
            long cost = checked((long)image.PixelWidth * image.PixelHeight * 4);
            lock (gate)
            {
                CheckOpen(); request.Token.ThrowIfCancellationRequested();
                if (flight.Readers == 0) throw new OperationCanceledException(request.Token);
                // Cache ownership and visible leases are independent. Eviction never invalidates an image.
                foreach (var item in cache.OrderBy(pair => pair.Value.Used).ToArray())
                {
                    if (bytes + cost <= CacheBytes && cache.Count < 64) break;
                    cache.Remove(item.Key); bytes -= item.Value.Cost;
                }
                var entry = new Entry(image, cost) { Used = ++clock };
                if (cost <= CacheBytes) { cache.Add(key, entry); bytes += cost; }
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
    }

    private IArtworkLease Own(string key, Entry entry)
    {
        entry.Used = ++clock;
        entry.Leases++;
        leased[key] = entry;
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
            lock (gate) { cache.Clear(); leased.Clear(); bytes = 0; }
            lifetime.Dispose();
            released.TrySetResult();
        }
        catch (Exception error) { released.TrySetException(error); throw; }
    }
}

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

/// <summary>One explicitly owned App cache for public, noncredentialed artwork.</summary>
public sealed class ArtworkService : IArtworkLoader, IAsyncDisposable
{
    public const int MaximumBytes = 4 * 1024 * 1024, CacheBytes = 32 * 1024 * 1024;
    private readonly object gate = new();
    private readonly ArtworkHttp http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim slots = new(4, 4);
    private readonly Dictionary<string, HostSlot> hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private TaskCompletionSource drained = Completed(), released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int pending;
    private long bytes, clock;
    private bool closed;
    private sealed class HostSlot { internal readonly SemaphoreSlim Slots = new(2, 2); internal int Users; }
    private sealed class Entry(BitmapSource image, long cost)
    {
        internal readonly BitmapSource Image = image;
        internal readonly long Cost = cost;
        internal int Readers;
        internal long Used;
    }
    private sealed class Lease(ArtworkService owner, Entry entry) : IArtworkLease
    {
        private ArtworkService? parent = owner;
        public BitmapSource Image => Volatile.Read(ref parent) is not null ? entry.Image : throw new ObjectDisposedException(nameof(Lease));
        public void Dispose() { var old = Interlocked.Exchange(ref parent, null); if (old is not null) lock (old.gate) entry.Readers--; }
    }
    public ArtworkService(HttpMessageHandler? handler = null) => http = new(handler);

    public async Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = ArtworkHttp.Parse(address);
        string key = uri.AbsoluteUri;
        HostSlot host;
        lock (gate)
        {
            CheckOpen();
            if (cache.TryGetValue(key, out var hit)) return Own(hit);
            if (pending >= 64) throw new ArtworkException("ARTWORK_LIMIT");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!hosts.TryGetValue(uri.IdnHost, out host!)) hosts.Add(uri.IdnHost, host = new());
            host.Users++;
        }
        bool globalHeld = false, hostHeld = false;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // Host admission precedes global admission: one host cannot occupy
            // all global slots while merely waiting on its own two slots.
            await host.Slots.WaitAsync(request.Token).ConfigureAwait(false); hostHeld = true;
            await slots.WaitAsync(request.Token).ConfigureAwait(false); globalHeld = true;
            lock (gate)
            {
                CheckOpen(); request.Token.ThrowIfCancellationRequested();
                if (cache.TryGetValue(key, out var hit)) return Own(hit);
            }
            byte[] encoded = await http.ReadAsync(uri, request.Token).ConfigureAwait(false);
            BitmapSource image;
            try { image = await Task.Run(() => ArtworkRaster.Decode(encoded), request.Token).ConfigureAwait(false); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(encoded); }
            request.Token.ThrowIfCancellationRequested();
            long cost = checked((long)image.PixelWidth * image.PixelHeight * 4);
            lock (gate)
            {
                CheckOpen(); request.Token.ThrowIfCancellationRequested();
                if (cache.TryGetValue(key, out var hit)) return Own(hit);
                // Never evict an active visual lease to manufacture room.
                foreach (var item in cache.Where(pair => pair.Value.Readers == 0).OrderBy(pair => pair.Value.Used).ToArray())
                {
                    if (bytes + cost <= CacheBytes && cache.Count < 64) break;
                    cache.Remove(item.Key); bytes -= item.Value.Cost;
                }
                if (bytes + cost > CacheBytes || cache.Count >= 64) throw new ArtworkException("ARTWORK_LIMIT");
                var entry = new Entry(image, cost);
                cache.Add(key, entry); bytes += cost;
                return Own(entry);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new ArtworkException("ARTWORK_TIMEOUT"); }
        catch (ArtworkException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new ArtworkException("ARTWORK_FAILED"); }
        finally
        {
            if (globalHeld) slots.Release();
            if (hostHeld) host.Slots.Release();
            lock (gate)
            {
                if (--host.Users == 0) { hosts.Remove(uri.IdnHost); host.Slots.Dispose(); }
                if (--pending == 0) drained.TrySetResult();
            }
        }
    }

    private IArtworkLease Own(Entry entry) { entry.Readers++; entry.Used = ++clock; return new Lease(this, entry); }
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
            lifetime.Cancel();
            await drain.ConfigureAwait(false);
            http.Dispose();
            lock (gate) { cache.Clear(); bytes = 0; }
            slots.Dispose(); lifetime.Dispose();
            released.TrySetResult();
        }
        catch (Exception error) { released.TrySetException(error); throw; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Storage;

public sealed record ResourceCacheStats(
    int EntryCount,
    long TotalBytes,
    long CapacityBytes,
    bool Initialized,
    bool Closed);

/// <summary>Bounded, LRU-evicted local disk resource cache with atomic staged writes.</summary>
public sealed class FileResourceCache : IAsyncDisposable
{
    private static readonly TimeSpan MtimeTouchInterval = TimeSpan.FromMinutes(15);

    private readonly string directory;
    private readonly long capacityBytes;
    private readonly int maximumEntryBytes;
    private readonly object gate = new();
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
    private long bytes, clock, epoch;
    private int active;
    private bool initialized, closed;
    private TaskCompletionSource drained = Completed();
    private Task? disposal;

    private sealed class Item(long size, DateTimeOffset modified, long itemEpoch, long used)
    {
        internal long Size = size, Used = used;
        internal readonly long Epoch = itemEpoch;
        internal DateTimeOffset Modified = modified;
    }

    public FileResourceCache(string directory, long capacityBytes, int maximumEntryBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (capacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        if (maximumEntryBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntryBytes));
        this.directory = Path.GetFullPath(directory);
        this.capacityBytes = capacityBytes;
        this.maximumEntryBytes = maximumEntryBytes;
    }

    public long CurrentEpoch { get { lock (gate) { CheckOpen(); return epoch; } } }

    public ResourceCacheStats GetStats()
    {
        lock (gate)
            return new ResourceCacheStats(items.Count, bytes, capacityBytes, initialized, closed);
    }

    public Task<byte[]?> ReadAsync(string resource, CancellationToken token = default)
    {
        string name = Name(resource);
        lock (gate)
        {
            long expectedEpoch = epoch;
            return Run(() => Read(name, expectedEpoch, token), token, swallowErrors: true);
        }
    }

    public Task WriteAsync(string resource, ReadOnlyMemory<byte> content, long expectedEpoch,
        CancellationToken token = default)
    {
        string name = Name(resource);
        return Run(() => { Write(name, content, expectedEpoch, token); return true; }, token, swallowErrors: true);
    }

    public Task RemoveAsync(string resource, long expectedEpoch, CancellationToken token = default)
    {
        string name = Name(resource);
        return Run(() =>
        {
            lock (gate) if (epoch != expectedEpoch) return false;
            Open(token);
            lock (gate) if (epoch != expectedEpoch) return false;
            Remove(name);
            return true;
        }, token, swallowErrors: true);
    }

    /// <summary>Explicitly clears all cached entries. Propagates real filesystem errors to callers.</summary>
    public Task ClearAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            CheckOpen();
            long boundary = ++epoch;
            return Run(() =>
            {
                Open(token);
                lock (gate)
                {
                    foreach (var name in items
                        .Where(pair => pair.Value.Epoch < boundary)
                        .Select(pair => pair.Key)
                        .ToList())
                    {
                        Forget(name);
                    }
                }
                if (Directory.Exists(directory))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        string fileName = Path.GetFileName(entry);
                        bool keep;
                        lock (gate) keep = items.ContainsKey(fileName);
                        if (keep) continue;
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else if (File.Exists(entry)) File.Delete(entry);
                    }
                }
                return true;
            }, token, swallowErrors: false);
        }
    }

    private void CheckOpen()
    {
        if (closed) throw new ObjectDisposedException(nameof(FileResourceCache));
    }

    private Task<T> Run<T>(Func<T> operation, CancellationToken token, bool swallowErrors)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            CheckOpen();
            if (active++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Task.Run(async () =>
            {
                try
                {
                    await io.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        return operation();
                    }
                    catch (Exception error) when (swallowErrors && Recoverable(error)) { return default!; }
                    finally { io.Release(); }
                }
                finally { lock (gate) if (--active == 0) drained.TrySetResult(); }
            });
        }
    }

    private void Open(CancellationToken token)
    {
        if (initialized) return;
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);

        var loaded = new List<(string Name, Item Item)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(entry);
            if (!IsValidResourceName(fileName))
            {
                // Sweep interrupted .stage files and stray leftovers on startup.
                TryDeleteEntry(entry);
                continue;
            }
            try
            {
                var info = new FileInfo(entry);
                long size = info.Length;
                if (size <= 0 || size > maximumEntryBytes)
                {
                    TryDeleteEntry(entry);
                    continue;
                }
                loaded.Add((fileName, new Item(size, info.LastWriteTimeUtc, 0, 0)));
            }
            catch (IOException) { TryDeleteEntry(entry); }
            catch (UnauthorizedAccessException) { }
        }

        lock (gate)
        {
            foreach (var (fileName, item) in loaded.OrderBy(pair => pair.Item.Modified))
            {
                item.Used = ++clock;
                items[fileName] = item;
                bytes += item.Size;
            }
            initialized = true;
        }
        Trim(0, token);
        token.ThrowIfCancellationRequested();
    }

    private string PathFor(string name) => Path.Combine(directory, name);

    private byte[]? Read(string name, long expectedEpoch, CancellationToken token)
    {
        Open(token);
        Item? item;
        lock (gate)
        {
            if (epoch != expectedEpoch) return null;
            if (!items.TryGetValue(name, out item) || item.Epoch != expectedEpoch) return null;
        }

        string path = PathFor(name);
        if (!File.Exists(path)) { lock (gate) Forget(name); return null; }

        byte[] content;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            long size = stream.Length;
            if (size <= 0 || size > maximumEntryBytes)
            {
                stream.Dispose();
                Remove(name);
                return null;
            }
            content = new byte[checked((int)size)];
            stream.ReadExactly(content);
            if (size != item.Size)
            {
                lock (gate) Resize(item, size);
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            Remove(name);
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool touchMtime;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (epoch != expectedEpoch) return null;
            touchMtime = now - item.Modified >= MtimeTouchInterval;
            if (touchMtime) item.Modified = now;
            item.Used = ++clock;
        }
        if (touchMtime)
        {
            try { File.SetLastWriteTimeUtc(path, now.UtcDateTime); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return content;
    }

    private void Write(string name, ReadOnlyMemory<byte> content, long expectedEpoch, CancellationToken token)
    {
        if (content.Length == 0 || content.Length > maximumEntryBytes || content.Length > capacityBytes) return;
        lock (gate) if (epoch != expectedEpoch) return;
        Open(token);
        lock (gate) if (epoch != expectedEpoch) return;
        if (!Trim(content.Length, token)) return;

        string stagePath = PathFor(name + "." + Guid.NewGuid().ToString("N") + ".stage");
        try
        {
            using (var output = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: false))
            {
                output.Write(content.Span);
                output.Flush(flushToDisk: true);
            }
            var modified = DateTimeOffset.UtcNow;
            token.ThrowIfCancellationRequested();
            lock (gate) if (epoch != expectedEpoch) return;
            string targetPath = PathFor(name);
            File.Move(stagePath, targetPath, overwrite: true);
            stagePath = null!;
            bool stale;
            lock (gate)
            {
                stale = epoch != expectedEpoch;
                if (!stale)
                {
                    Forget(name);
                    items[name] = new(content.Length, modified, expectedEpoch, ++clock);
                    bytes += content.Length;
                }
            }
            if (stale) TryDeleteEntry(targetPath);
        }
        finally
        {
            if (stagePath is not null) TryDeleteEntry(stagePath);
        }
    }

    private bool Trim(long reserve, CancellationToken token)
    {
        List<string> retired;
        lock (gate)
        {
            retired = items
                .Where(pair => pair.Value.Epoch != epoch)
                .Select(pair => pair.Key)
                .ToList();
        }
        foreach (var key in retired)
        {
            token.ThrowIfCancellationRequested();
            Remove(key);
        }
        List<string> lruOrder;
        lock (gate)
        {
            if (bytes <= capacityBytes - reserve) return true;
            lruOrder = items.OrderBy(pair => pair.Value.Used).Select(pair => pair.Key).ToList();
        }
        foreach (var key in lruOrder)
        {
            token.ThrowIfCancellationRequested();
            Remove(key);
            lock (gate)
            {
                if (bytes <= capacityBytes - reserve) return true;
            }
        }
        lock (gate) return bytes <= capacityBytes - reserve;
    }

    private void Remove(string name)
    {
        TryDeleteEntry(PathFor(name));
        lock (gate) Forget(name);
    }

    private void Resize(Item item, long size)
    {
        bytes += size - item.Size;
        item.Size = size;
    }

    private void Forget(string name)
    {
        if (!items.Remove(name, out var item)) return;
        bytes -= item.Size;
    }

    private static void TryDeleteEntry(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool Recoverable(Exception error) =>
        error is IOException or UnauthorizedAccessException or StorageException;

    private static string Name(string resource)
    {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resource))) + ".resource";
    }

    private static bool IsValidResourceName(string name) =>
        name.Length == 73 &&
        name.EndsWith(".resource", StringComparison.Ordinal) &&
        name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closed = true;
            return new(disposal ??= DisposeCoreAsync(drained.Task));
        }
    }

    private async Task DisposeCoreAsync(Task drain)
    {
        await drain.ConfigureAwait(false);
        lock (gate)
        {
            items.Clear();
            bytes = 0;
        }
        io.Dispose();
    }
}

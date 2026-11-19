using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

public sealed record ResourceCacheEntry(byte[] Content, DateTimeOffset? ExpiresAt);

public sealed class FileResourceCache : IAsyncDisposable
{
    private readonly string? directory;
    private readonly long capacityBytes;
    private readonly int maximumEntryBytes;
    private readonly TimeSpan? lifetime;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly Dictionary<string, long> epochs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
    private WindowsDocumentFiles.Root? root;
    private SafeFileHandle? ownership;
    private long bytes, clock, generation, allEpoch;
    private int active;
    private bool initialized, inventoryFailed, closed;
    private TaskCompletionSource drained = Completed();
    private Task? disposal;

    private sealed class Item(string group, long size, DateTimeOffset modified, long epoch, long used)
    {
        internal readonly string Group = group;
        internal long Size = size, Used = used;
        internal readonly long Epoch = epoch;
        internal DateTimeOffset Modified = modified;
        internal bool Readable = true;
        internal byte[]? Content;
    }

    public FileResourceCache(string? directory, long capacityBytes, int maximumEntryBytes,
        TimeSpan? lifetime = null, TimeProvider? time = null)
    {
        if (capacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        if (maximumEntryBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntryBytes));
        if (lifetime is { } duration && duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        this.directory = directory;
        this.capacityBytes = capacityBytes;
        this.maximumEntryBytes = maximumEntryBytes;
        this.lifetime = lifetime;
        this.time = time ?? TimeProvider.System;
    }

    public long Epoch(string group)
    {
        ValidateGroup(group);
        lock (gate) { CheckOpen(); return CurrentEpoch(group); }
    }

    public Task<ResourceCacheEntry?> ReadAsync(string group, string resource, CancellationToken token = default)
    {
        string name = Name(group, resource);
        lock (gate)
        {
            long epoch = CurrentEpoch(group);
            return Run(() => Read(name, group, epoch, token), token);
        }
    }

    public Task WriteAsync(string group, string resource, ReadOnlyMemory<byte> content, long expectedEpoch,
        CancellationToken token = default)
    {
        string name = Name(group, resource);
        // The caller owns content until this task settles; no queued owned snapshots.
        return Run(() => { Write(name, group, content, expectedEpoch, token); return true; }, token);
    }

    public Task RemoveAsync(string group, string resource, long expectedEpoch, CancellationToken token = default)
    {
        string name = Name(group, resource);
        return Run(() =>
        {
            lock (gate) if (CurrentEpoch(group) != expectedEpoch) return false;
            Open(token);
            lock (gate) return CurrentEpoch(group) == expectedEpoch && Remove(name);
        }, token);
    }

    public Task TouchAsync(string group, string resource, long expectedEpoch, CancellationToken token = default)
    {
        string name = Name(group, resource);
        return Run(() =>
        {
            lock (gate) if (CurrentEpoch(group) != expectedEpoch) return false;
            Open(token);
            if (!items.TryGetValue(name, out var item) || !item.Readable || item.Epoch != expectedEpoch)
                return false;
            try
            {
                using var file = directory is null ? null : OpenFile(name, 0x180 /* FILE_READ/WRITE_ATTRIBUTES */);
                if (directory is not null)
                {
                    if (file is null) { Forget(name); return false; }
                    long size = WindowsDocumentFiles.FileLength(file);
                    if (size != item.Size)
                    {
                        Resize(item, size); item.Readable = false; return false;
                    }
                    item.Modified = File.GetLastWriteTimeUtc(file);
                }
                DateTimeOffset modified = time.GetUtcNow();
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (CurrentEpoch(group) != expectedEpoch) return false;
                    if (Expired(item, modified))
                    {
                        file?.Dispose();
                        Remove(name);
                        return false;
                    }
                    if (modified > item.Modified)
                    {
                        if (file is not null) File.SetLastWriteTimeUtc(file, modified.UtcDateTime);
                        item.Modified = modified;
                    }
                    item.Used = ++clock;
                    return true;
                }
            }
            catch (Exception error) when (Recoverable(error)) { Quarantine(name, item); return false; }
        }, token);
    }

    public Task ClearAsync(string? group, CancellationToken token = default)
    {
        if (group is not null) ValidateGroup(group);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            CheckOpen();
            // This fence is synchronous, before scheduling any physical cleanup.
            long boundary = ++generation;
            if (group is null)
            {
                allEpoch = boundary;
                epochs.Clear();
            }
            else epochs[group] = boundary;
            return Run(() =>
            {
                Open(token);
                foreach (var pair in items.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    if ((group is null || pair.Value.Group == group) && pair.Value.Epoch < boundary)
                        Remove(pair.Key);
                }
                return true;
            }, token);
        }
    }

    public Task DrainAsync() { lock (gate) return drained.Task; }

    private long CurrentEpoch(string group) => epochs.GetValueOrDefault(group, allEpoch);
    private void CheckOpen()
    {
        if (closed) throw new ObjectDisposedException(nameof(FileResourceCache));
    }

    private Task<T> Run<T>(Func<T> operation, CancellationToken token)
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
                    catch (Exception error) when (Recoverable(error)) { return default!; }
                    finally { io.Release(); }
                }
                finally { lock (gate) if (--active == 0) drained.TrySetResult(); }
            });
        }
    }

    private void Open(CancellationToken token)
    {
        if (initialized)
        {
            root?.Check();
            if (inventoryFailed) throw new DocumentStoreException(DocumentStoreError.Storage);
            return;
        }
        token.ThrowIfCancellationRequested();
        if (directory is null) { initialized = true; return; }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
        if (root is null)
        {
            string validated = WindowsDocumentFiles.ValidateRoot(directory);
            using var parent = WindowsDocumentFiles.Root.OpenParent(validated);
            using var leaf = WindowsDocumentFiles.OpenDirectory(validated, true) ??
                WindowsDocumentFiles.CreateDirectory(validated);
            root = new WindowsDocumentFiles.Root(validated);
        }
        root.Check();
        ownership ??= WindowsDocumentFiles.OpenFile(Path.Combine(root.Path, ".resources.lock"),
            WindowsDocumentFiles.Read | WindowsDocumentFiles.Write, 0, WindowsDocumentFiles.OpenAlways)!;
        if (WindowsDocumentFiles.FileLength(ownership) != 0)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        // Once enumeration begins, a partial/failed inventory stays closed to reads
        // and writes. Retrying it could restore retired entries or undercount bytes.
        initialized = inventoryFailed = true;
        // FindFirstFile metadata only. No resource payload or persisted index is loaded.
        foreach (var file in new DirectoryInfo(WindowsDocumentFiles.NativePath(root.Path)).EnumerateFiles())
        {
            bool stage = file.Name.EndsWith(".stage", StringComparison.Ordinal);
            string name = file.Name;
            if (stage)
            {
                if (name.Length < 39 || name[^39] != '.' ||
                    name.AsSpan(name.Length - 38, 32).ContainsAnyExcept("0123456789abcdef")) continue;
                name = name[..^39];
            }
            if (!TryGroup(name, out string group)) continue;
            long size = file.Length;
            var item = new Item(group, size, file.LastWriteTimeUtc, 0, 0)
            {
                Readable = !stage && size is > 0 && size <= maximumEntryBytes &&
                    (file.Attributes & FileAttributes.ReparsePoint) == 0
            };
            items.Add(file.Name, item); bytes += size;
        }
        foreach (var item in items.Values.OrderBy(item => item.Modified)) item.Used = ++clock;
        inventoryFailed = false;
        Trim(0, token);
        token.ThrowIfCancellationRequested();
    }

    private string PathFor(string name) => Path.Combine(root!.Path, name);
    private SafeFileHandle? OpenFile(string name, uint access, uint share = WindowsDocumentFiles.ShareRead) =>
        WindowsDocumentFiles.OpenFile(PathFor(name), access, share,
            WindowsDocumentFiles.OpenExisting, allowMissing: true);

    private ResourceCacheEntry? Read(string name, string group, long epoch, CancellationToken token)
    {
        byte[]? content = null;
        Item? item = null;
        try
        {
            Open(token);
            lock (gate) if (CurrentEpoch(group) != epoch) return null;
            if (!items.TryGetValue(name, out item) || !item.Readable || item.Epoch != epoch) return null;
            using var handle = directory is null ? null : OpenFile(name, WindowsDocumentFiles.Read);
            if (directory is not null)
            {
                if (handle is null) { Forget(name); return null; }
                long size = WindowsDocumentFiles.FileLength(handle);
                if (size != item.Size)
                {
                    Resize(item, size); item.Readable = false;
                    return null;
                }
                item.Modified = File.GetLastWriteTimeUtc(handle);
            }
            if (Expired(item))
            {
                handle?.Dispose();
                Remove(name);
                return null;
            }
            using var input = handle is null ? null : new FileStream(handle, FileAccess.Read);
            content = input is null ? item.Content!.ToArray() : new byte[checked((int)item.Size)];
            input?.ReadExactly(content);
            DateTimeOffset modified = lifetime is null ? time.GetUtcNow() : item.Modified;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (CurrentEpoch(group) != epoch) return null;
                if (lifetime is null && modified > item.Modified)
                {
                    // Default artwork hits renew mtime/LRU; denied attribute writes
                    // do not turn an otherwise safe, readable file into a miss.
                    try
                    {
                        if (handle is not null)
                        {
                            using var attributes = OpenFile(name, 0x100 /* FILE_WRITE_ATTRIBUTES */);
                            if (attributes is not null) File.SetLastWriteTimeUtc(attributes, modified.UtcDateTime);
                        }
                        item.Modified = modified;
                    }
                    catch (Exception error) when (Recoverable(error)) { }
                }
                item.Used = ++clock;
                var result = new ResourceCacheEntry(content, ExpiresAt(item));
                content = null;
                return result;
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            if (item is not null) Quarantine(name, item);
            return null;
        }
        finally { if (content is not null) CryptographicOperations.ZeroMemory(content); }
    }

    private void Write(string name, string group, ReadOnlyMemory<byte> content, long epoch, CancellationToken token)
    {
        if (content.Length == 0 || content.Length > maximumEntryBytes || content.Length > capacityBytes) return;
        string? staging = null;
        try
        {
            lock (gate) if (CurrentEpoch(group) != epoch) return;
            Open(token);
            lock (gate) if (CurrentEpoch(group) != epoch) return;
            if (!Trim(content.Length, token)) return;
            if (directory is null)
            {
                var modified = time.GetUtcNow();
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (CurrentEpoch(group) != epoch) return;
                    var owned = content.ToArray();
                    Forget(name);
                    items[name] = new(group, owned.Length, modified, epoch, ++clock) { Content = owned };
                    bytes += owned.Length;
                }
                return;
            }
            // Validate and pin the previous name even though no old bytes are read.
            using var previous = OpenFile(name, WindowsDocumentFiles.Read,
                WindowsDocumentFiles.ShareRead | 4 /* FILE_SHARE_DELETE */);
            staging = name + "." + Guid.NewGuid().ToString("N") + ".stage";
            items.Add(staging, new(group, content.Length, DateTimeOffset.MinValue, epoch, ++clock) { Readable = false });
            bytes += content.Length; // Full stage reservation, including failed cleanup.
            using var stage = WindowsDocumentFiles.OpenFile(PathFor(staging),
                WindowsDocumentFiles.Read | WindowsDocumentFiles.Write | WindowsDocumentFiles.Delete,
                WindowsDocumentFiles.ShareRead, WindowsDocumentFiles.CreateNew)!;
            using var output = new FileStream(stage, FileAccess.ReadWrite);
            try
            {
                output.Write(content.Span);
                output.Flush(flushToDisk: true);
                var modified = time.GetUtcNow();
                token.ThrowIfCancellationRequested();
                root!.Check();
                if (WindowsDocumentFiles.FileLength(stage) != content.Length)
                    throw new DocumentStoreException(DocumentStoreError.Corrupt);
                if (previous is not null) _ = WindowsDocumentFiles.FileLength(previous);
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (CurrentEpoch(group) != epoch) return;
                    File.SetLastWriteTimeUtc(stage, modified.UtcDateTime);
                    WindowsDocumentFiles.Replace(stage, PathFor(name));
                    Forget(name);
                    Forget(staging); staging = null;
                    items.Add(name, new(group, content.Length, modified, epoch, ++clock));
                    bytes += content.Length;
                }
            }
            finally
            {
                if (staging is not null)
                {
                    try { WindowsDocumentFiles.DeleteEntry(stage); }
                    catch (Exception error) when (Recoverable(error)) { }
                }
            }
        }
        catch (Exception error) when (Recoverable(error)) { }
        finally { if (staging is not null) Remove(staging); }
    }

    private bool Expired(Item item) => lifetime is not null && Expired(item, time.GetUtcNow());
    private bool Expired(Item item, DateTimeOffset now) => lifetime is { } ttl && now - item.Modified >= ttl;
    private DateTimeOffset? ExpiresAt(Item item) => lifetime is { } ttl
        ? ttl <= DateTimeOffset.MaxValue - item.Modified ? item.Modified + ttl : DateTimeOffset.MaxValue
        : null;
    private bool Retired(Item item) { lock (gate) return item.Epoch != CurrentEpoch(item.Group); }
    private bool Trim(long reserve, CancellationToken token)
    {
        foreach (var pair in items.Where(pair => !pair.Value.Readable || Retired(pair.Value) || Expired(pair.Value)).ToArray())
        { token.ThrowIfCancellationRequested(); Remove(pair.Key); }
        if (bytes <= capacityBytes - reserve) return true;
        foreach (var pair in items.OrderBy(pair => pair.Value.Used).ToArray())
        {
            token.ThrowIfCancellationRequested();
            Remove(pair.Key);
            if (bytes <= capacityBytes - reserve) return true;
        }
        return false;
    }

    private bool Remove(string name)
    {
        if (items.TryGetValue(name, out var item)) item.Readable = false;
        try
        {
            if (directory is not null)
            {
                using var file = OpenFile(name, WindowsDocumentFiles.Delete);
                if (file is not null)
                {
                    if (item is not null) Resize(item, WindowsDocumentFiles.FileLength(file));
                    WindowsDocumentFiles.DeleteEntry(file);
                }
            }
            Forget(name);
            return true;
        }
        catch (Exception error) when (Recoverable(error))
        {
            if (item is not null) Quarantine(name, item);
            return false;
        }
    }

    private void Resize(Item item, long size)
    {
        bytes += size - item.Size;
        item.Size = size;
    }

    private void Quarantine(string name, Item item)
    {
        item.Readable = false;
        if (directory is null) return;
        try
        {
            root!.Check();
            var file = new FileInfo(WindowsDocumentFiles.NativePath(PathFor(name)));
            // Failed opens/deletes may hide a grown file. Keep a conservative charge
            // from this entry's metadata, without reading any bytes or other entries.
            if (file.Exists)
            {
                long size = file.Length;
                if (size > item.Size) Resize(item, size);
            }
        }
        catch (Exception error) when (Recoverable(error)) { }
    }

    private void Forget(string name)
    {
        if (!items.Remove(name, out var item)) return;
        bytes -= item.Size;
        if (item.Content is not null) CryptographicOperations.ZeroMemory(item.Content);
    }

    private static bool Recoverable(Exception error) =>
        error is IOException or UnauthorizedAccessException or DocumentStoreException;
    private static void ValidateGroup(string group)
    {
        // Canonical case prevents group aliases on the Windows filesystem.
        if (!ValidGroup(group))
            throw new ArgumentException("A lowercase ordinary resource group is required.", nameof(group));
    }
    private static bool ValidGroup(string group) => group.Length is >= 1 and <= 128 &&
        char.IsAsciiLetterOrDigit(group[0]) && !group.Contains("..", StringComparison.Ordinal) &&
        group.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static string Name(string group, string resource)
    {
        ValidateGroup(group);
        ArgumentException.ThrowIfNullOrEmpty(resource);
        return "r-" + group + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resource))) + ".resource";
    }
    private static bool TryGroup(string name, out string group)
    {
        group = "";
        if (!name.StartsWith("r-", StringComparison.Ordinal) || !name.EndsWith(".resource", StringComparison.Ordinal) ||
            name.Length < 77 || name[^74] != '-') return false;
        group = name[2..^74];
        return ValidGroup(group) && name[^73..^9].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult(); return result;
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
        foreach (string name in items.Keys.ToArray()) Forget(name);
        ownership?.Dispose();
        root?.Dispose();
        io.Dispose();
    }
}

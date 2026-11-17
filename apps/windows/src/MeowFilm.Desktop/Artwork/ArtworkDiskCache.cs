using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MeowFilm.Desktop.Artwork;

internal sealed class ArtworkDiskCache(string directory) : IDisposable
{
    private const long CapacityBytes = 256L * 1024 * 1024;
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
    private FileStream? ownership;
    private long bytes, clock;
    private bool initialized;
    private sealed class Item(long size, long used)
    {
        internal readonly long Size = size;
        internal long Used = used;
    }
    private static string Name(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".image";
    private string PathFor(string name) => Path.Combine(directory, name);

    // Called only by a background flight. One owner accounts for this directory;
    // another App instance can continue without disk caching while it is in use.
    private void Open(CancellationToken token)
    {
        if (initialized) return;
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        ownership ??= new FileStream(PathFor(".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (string temporary in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            token.ThrowIfCancellationRequested();
            File.Delete(temporary);
        }
        items.Clear(); bytes = clock = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.image").OrderBy(file => file.LastWriteTimeUtc))
        {
            token.ThrowIfCancellationRequested();
            if (file.Name.Length != 70 || file.Name.AsSpan(0, 64).ContainsAnyExcept("0123456789ABCDEF")) continue;
            long size = file.Length;
            if (size is <= 0 or > ArtworkService.MaximumCacheEntryBytes) { file.Delete(); continue; }
            items.Add(file.Name, new(size, ++clock)); bytes += size;
        }
        Trim(0, token);
        initialized = true;
    }

    private void Trim(long reserve, CancellationToken token)
    {
        if (bytes + reserve <= CapacityBytes) return;
        foreach (var item in items.OrderBy(pair => pair.Value.Used).ToArray())
        {
            token.ThrowIfCancellationRequested();
            File.Delete(PathFor(item.Key));
            items.Remove(item.Key); bytes -= item.Value.Size;
            if (bytes + reserve <= CapacityBytes) break;
        }
    }

    internal async Task<byte[]?> ReadAsync(string key, CancellationToken token)
    {
        await io.WaitAsync(token).ConfigureAwait(false);
        byte[]? encoded = null;
        try
        {
            Open(token);
            string name = Name(key);
            if (!items.TryGetValue(name, out var item)) return null;
            await using (var input = new FileStream(PathFor(name), FileMode.Open, FileAccess.Read, FileShare.Read,
                32768, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (input.Length != item.Size) { initialized = false; return null; }
                encoded = new byte[checked((int)item.Size)];
                await input.ReadExactlyAsync(encoded, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            item.Used = ++clock;
            try { File.SetLastWriteTimeUtc(PathFor(name), DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var result = encoded; encoded = null;
            return result;
        }
        catch (IOException) { initialized = false; return null; }
        catch (UnauthorizedAccessException) { initialized = false; return null; }
        finally
        {
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            io.Release();
        }
    }

    internal async Task RemoveAsync(string key, CancellationToken token)
    {
        await io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Open(token);
            string name = Name(key);
            File.Delete(PathFor(name));
            if (items.Remove(name, out var item)) bytes -= item.Size;
        }
        catch (IOException) { initialized = false; }
        catch (UnauthorizedAccessException) { initialized = false; }
        finally { io.Release(); }
    }

    internal async Task WriteAsync(string key, byte[] encoded, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (encoded.Length is 0 or > ArtworkService.MaximumCacheEntryBytes) return;
        await io.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Open(token);
            string name = Name(key);
            // Reserve space before writing, including the temporary file.
            Trim(encoded.Length, token);
            temporary = PathFor(name + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                32768, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(encoded, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, PathFor(name), overwrite: true);
            temporary = null;
            if (items.Remove(name, out var prior)) bytes -= prior.Size;
            items.Add(name, new(encoded.Length, ++clock)); bytes += encoded.Length;
        }
        catch (IOException) { initialized = false; }
        catch (UnauthorizedAccessException) { initialized = false; }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { initialized = false; }
                catch (UnauthorizedAccessException) { initialized = false; }
            }
            io.Release();
        }
    }

    public void Dispose()
    {
        ownership?.Dispose();
        io.Dispose();
    }
}

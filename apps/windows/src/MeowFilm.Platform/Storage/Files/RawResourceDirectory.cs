using System.Security.Cryptography;
namespace MeowFilm.Platform.Storage;

/// <summary>Original content files in a pinned current resource directory, never metadata documents.</summary>
public sealed class RawResourceDirectory : IDisposable
{
    private readonly WindowsStorageFiles.Root root;
    public RawResourceDirectory(string productRoot, string name)
    {
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new StorageException(StorageError.InvalidRequest);
        root = WindowsStorageFiles.Root.OpenOrCreate(Path.Combine(productRoot, "resources-v1", name));
    }
    private string PathFor(string id)
    {
        root.Check();
        if (id.Length != 64 || id.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new StorageException(StorageError.InvalidRequest);
        return Path.Combine(root.Path, id + ".resource");
    }
    public byte[] Read(string id, int bytes)
    {
        using var handle = WindowsStorageFiles.OpenFile(PathFor(id), WindowsStorageFiles.Read, WindowsStorageFiles.ShareRead, WindowsStorageFiles.OpenExisting)!;
        if (WindowsStorageFiles.FileLength(handle) != bytes) throw new StorageException(StorageError.Corrupt);
        using var stream = new FileStream(handle, FileAccess.Read);
        var data = new byte[bytes]; stream.ReadExactly(data);
        if (Convert.ToHexStringLower(SHA256.HashData(data)) != id) throw new StorageException(StorageError.Corrupt);
        return data;
    }
    public (string Id, bool Created) Write(byte[] bytes)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = PathFor(id);
        using (var existing = WindowsStorageFiles.OpenFile(path, WindowsStorageFiles.Read,
            WindowsStorageFiles.ShareRead, WindowsStorageFiles.OpenExisting, allowMissing: true))
        {
            if (existing is not null)
            {
                if (WindowsStorageFiles.FileLength(existing) != bytes.Length)
                    throw new StorageException(StorageError.Corrupt);
                using var input = new FileStream(existing, FileAccess.Read);
                var data = new byte[bytes.Length];
                input.ReadExactly(data);
                if (Convert.ToHexStringLower(SHA256.HashData(data)) != id)
                    throw new StorageException(StorageError.Corrupt);
                return (id, false);
            }
        }
        using var handle = WindowsStorageFiles.OpenFile(path,
            WindowsStorageFiles.Write | WindowsStorageFiles.Delete, 0, WindowsStorageFiles.CreateNew)!;
        bool committed = false;
        try
        {
            using var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            committed = true;
            return (id, true);
        }
        finally
        {
            if (!committed)
            {
                try { WindowsStorageFiles.DeleteEntry(handle); }
                catch { }
            }
        }
    }
    public void Delete(string id)
    {
        using var handle = WindowsStorageFiles.OpenDeletionEntry(PathFor(id));
        if (handle is null) return;
        _ = WindowsStorageFiles.FileLength(handle);
        WindowsStorageFiles.DeleteEntry(handle);
    }
    public void RemoveUnreferenced(IReadOnlySet<string> ids)
    {
        root.Check();
        var entries = Directory.EnumerateFileSystemEntries(WindowsStorageFiles.NativePath(root.Path)).ToArray();
        // Validate all ownership and handles before deleting any entry.
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!name.EndsWith(".resource", StringComparison.Ordinal) || name.Length != 73)
                throw new StorageException(StorageError.UnsafePath);
            using var handle = WindowsStorageFiles.OpenFile(PathFor(name[..64]), WindowsStorageFiles.Read,
                WindowsStorageFiles.ShareRead, WindowsStorageFiles.OpenExisting)!;
        }
        foreach (var entry in entries)
        {
            var id = Path.GetFileName(entry)[..64];
            if (!ids.Contains(id)) Delete(id);
        }
    }
    public void Dispose() => root.Dispose();
}

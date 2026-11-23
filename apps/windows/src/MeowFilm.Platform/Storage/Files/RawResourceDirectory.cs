using System.Security.Cryptography;

namespace MeowFilm.Platform.Storage;

/// <summary>Original content files in a current resource directory, never metadata documents.</summary>
public sealed class RawResourceDirectory
{
    private readonly string rootPath;

    public RawResourceDirectory(string productRoot, string name)
    {
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new StorageException(StorageError.InvalidRequest);
        rootPath = WindowsStorageFiles.EnsureDirectory(Path.Combine(productRoot, "resources-v1", name));
    }

    private string PathFor(string id)
    {
        if (id.Length != 64 || id.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new StorageException(StorageError.InvalidRequest);
        return Path.Combine(rootPath, id + ".resource");
    }

    public byte[] Read(string id, int bytes)
    {
        byte[] data;
        try { data = File.ReadAllBytes(PathFor(id)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new StorageException(StorageError.Storage); }
        if (data.Length != bytes || Convert.ToHexStringLower(SHA256.HashData(data)) != id)
            throw new StorageException(StorageError.Corrupt);
        return data;
    }

    public (string Id, bool Created) Write(byte[] bytes)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = PathFor(id);
        if (File.Exists(path))
        {
            byte[] existing;
            try { existing = File.ReadAllBytes(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new StorageException(StorageError.Storage); }
            if (existing.Length != bytes.Length || Convert.ToHexStringLower(SHA256.HashData(existing)) != id)
                throw new StorageException(StorageError.Corrupt);
            return (id, false);
        }

        bool committed = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            committed = true;
            return (id, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new StorageException(StorageError.Storage); }
        finally
        {
            if (!committed)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
            }
        }
    }

    public void Delete(string id)
    {
        try
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new StorageException(StorageError.Storage); }
    }

    public void RemoveUnreferenced(IReadOnlySet<string> ids)
    {
        var entries = Directory.EnumerateFiles(rootPath, "*.resource").ToArray();
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name.Length != 73) continue;
            var id = name[..64];
            if (!ids.Contains(id)) Delete(id);
        }
    }
}

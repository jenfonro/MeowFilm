using System.IO;
using System.Security.Cryptography;

namespace MeowFilm.Platform.Storage;

/// <summary>Content-addressed local resource file directory with atomic staged writes.</summary>
public sealed class RawResourceDirectory
{
    private readonly string directory;

    public RawResourceDirectory(string productRoot, string name)
    {
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new StorageException(StorageError.InvalidRequest);
        directory = Path.Combine(Path.GetFullPath(productRoot), "resources-v1", name);
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => directory;

    private string PathFor(string id)
    {
        if (id.Length != 64 || id.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new StorageException(StorageError.InvalidRequest);
        return Path.Combine(directory, id + ".resource");
    }

    /// <summary>Reads a verified resource file. Missing or corrupted files self-heal and return null.</summary>
    public byte[]? Read(string id, int expectedBytes)
    {
        if (expectedBytes <= 0) return null;
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length != expectedBytes)
            {
                stream.Dispose();
                TryDeletePath(path);
                return null;
            }
            var data = new byte[expectedBytes];
            stream.ReadExactly(data);
            if (Convert.ToHexStringLower(SHA256.HashData(data)) != id)
            {
                stream.Dispose();
                TryDeletePath(path);
                return null;
            }
            return data;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException)
        {
            TryDeletePath(path);
            return null;
        }
    }

    /// <summary>Atomically stages and commits bytes under their SHA-256 content ID.</summary>
    public (string Id, bool Created) Write(byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var target = PathFor(id);

        if (File.Exists(target))
        {
            if (TryVerifyExisting(target, id, bytes.Length))
                return (id, false);
            // Existing file is truncated or corrupted; fall through to atomic overwrite.
        }

        var stage = Path.Combine(directory, $".{id}.{Guid.NewGuid():N}.stage");
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: false))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(stage, target, overwrite: true);
            stage = null!;
            return (id, true);
        }
        finally
        {
            if (stage is not null) TryDeletePath(stage);
        }
    }

    public void Delete(string id) => TryDeletePath(PathFor(id));

    /// <summary>Deletes unreferenced resource files and any stray or interrupted stage files.</summary>
    public void RemoveUnreferenced(IReadOnlySet<string> ids, bool throwOnError = false)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(entry);
            if (IsValidResourceName(name))
            {
                var id = name[..64];
                if (!ids.Contains(id))
                {
                    if (throwOnError) DeletePathStrict(entry);
                    else TryDeletePath(entry);
                }
                continue;
            }
            // Sweep interrupted .stage files or stray leftovers instead of failing recovery.
            if (throwOnError) DeleteEntryStrict(entry);
            else TryDeleteEntry(entry);
        }
    }

    public (int FileCount, long TotalBytes) GetDiskStats()
    {
        if (!Directory.Exists(directory)) return (0, 0);
        int count = 0;
        long total = 0;
        foreach (var entry in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(entry);
            if (!IsValidResourceName(name)) continue;
            try
            {
                total += new FileInfo(entry).Length;
                count++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (count, total);
    }

    private static bool IsValidResourceName(string name) =>
        name.Length == 73 &&
        name.EndsWith(".resource", StringComparison.Ordinal) &&
        name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool TryVerifyExisting(string path, string id, int expectedLength)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length != expectedLength) return false;
            var data = new byte[expectedLength];
            stream.ReadExactly(data);
            return Convert.ToHexStringLower(SHA256.HashData(data)) == id;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void DeletePathStrict(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteEntryStrict(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDeletePath(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteEntry(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
namespace MeowFilm.Platform.Storage;

/// <summary>Original content files in a pinned current resource directory, never metadata documents.</summary>
public sealed class RawResourceDirectory : IDisposable
{
    private readonly WindowsDocumentFiles.Root root;
    public RawResourceDirectory(string productRoot, string name)
    {
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        var path = WindowsDocumentFiles.ValidateRoot(Path.Combine(productRoot, "resources-v1", name));
        using var parent = WindowsDocumentFiles.Root.OpenParent(path);
        using var leaf = WindowsDocumentFiles.CreateDirectory(path);
        root = new(path);
    }
    private string PathFor(string id)
    {
        root.Check();
        if (id.Length != 64 || id.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        return Path.Combine(root.Path, id + ".resource");
    }
    public byte[] Read(string id, int bytes)
    {
        using var handle = WindowsDocumentFiles.OpenFile(PathFor(id), WindowsDocumentFiles.Read, WindowsDocumentFiles.ShareRead, WindowsDocumentFiles.OpenExisting)!;
        if (WindowsDocumentFiles.FileLength(handle) != bytes) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        using var stream = new FileStream(handle, FileAccess.Read);
        var data = new byte[bytes]; stream.ReadExactly(data);
        if (Convert.ToHexStringLower(SHA256.HashData(data)) != id) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return data;
    }
    public (string Id, bool Created) Write(byte[] bytes)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = PathFor(id);
        if (File.Exists(path)) { _ = Read(id, bytes.Length); return (id, false); }
        using var handle = WindowsDocumentFiles.OpenFile(path, WindowsDocumentFiles.Write, 0, WindowsDocumentFiles.CreateNew)!;
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
        return (id, true);
    }
    public void Delete(string id)
    {
        using var handle = WindowsDocumentFiles.OpenDeletionEntry(PathFor(id));
        if (handle is null) return;
        _ = WindowsDocumentFiles.FileLength(handle);
        WindowsDocumentFiles.DeleteEntry(handle);
    }
    public void RemoveUnreferenced(IReadOnlySet<string> ids)
    {
        root.Check();
        var entries = Directory.EnumerateFileSystemEntries(root.Path).ToArray();
        // Validate all ownership and handles before deleting any entry.
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!name.EndsWith(".resource", StringComparison.Ordinal) || name.Length != 73)
                throw new DocumentStoreException(DocumentStoreError.UnsafePath);
            using var handle = WindowsDocumentFiles.OpenFile(PathFor(name[..64]), WindowsDocumentFiles.Read,
                WindowsDocumentFiles.ShareRead, WindowsDocumentFiles.OpenExisting)!;
        }
        foreach (var entry in entries)
        {
            var id = Path.GetFileName(entry)[..64];
            if (!ids.Contains(id)) Delete(id);
        }
    }
    public void Dispose() => root.Dispose();
}

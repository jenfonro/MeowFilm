using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

/// <summary>Pins an ordinary database and its persistent rollback journal for their entire open lifetime.</summary>
public sealed class DatabaseDirectory : IDisposable
{
    private readonly WindowsDocumentFiles.Root root;
    private SafeFileHandle? database, journal;
    public string DatabasePath { get; }
    public bool Created { get; }

    public DatabaseDirectory(string databaseRoot, string fileName)
    {
        if (!OperatingSystem.IsWindows()) throw new DocumentStoreException(DocumentStoreError.Unavailable);
        if (!DocumentRules.ValidNamespace(fileName) || !fileName.EndsWith(".sqlite3", StringComparison.Ordinal))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        string validatedRoot = WindowsDocumentFiles.ValidateRoot(databaseRoot);
        using (var parent = WindowsDocumentFiles.Root.OpenParent(validatedRoot))
        using (var leaf = WindowsDocumentFiles.OpenDirectory(validatedRoot, true) ?? WindowsDocumentFiles.CreateDirectory(validatedRoot))
            root = new WindowsDocumentFiles.Root(validatedRoot);
        DatabasePath = Path.Combine(root.Path, fileName);
        try
        {
            // This store uses only SQLite's native PERSIST rollback journal. Never
            // let SQLite discover an unvalidated WAL or follow a sidecar link.
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                using var unexpected = WindowsDocumentFiles.OpenFile(DatabasePath + suffix,
                    WindowsDocumentFiles.Read, 3 /* share read/write, never delete */,
                    WindowsDocumentFiles.OpenExisting, allowMissing: true);
                if (unexpected is not null) throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
            }
            database = OpenExisting(DatabasePath);
            Created = database is null;
            database ??= Create(DatabasePath);
            if (!Created && WindowsDocumentFiles.FileLength(database) == 0)
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            journal = OpenExisting(DatabasePath + "-journal") ?? Create(DatabasePath + "-journal");
        }
        catch { Dispose(); throw; }
    }

    private static SafeFileHandle? OpenExisting(string path) => WindowsDocumentFiles.OpenFile(path,
        WindowsDocumentFiles.Read, 3, WindowsDocumentFiles.OpenExisting, allowMissing: true);
    private static SafeFileHandle Create(string path) => WindowsDocumentFiles.OpenFile(path,
        WindowsDocumentFiles.Read | WindowsDocumentFiles.Write, 3, WindowsDocumentFiles.CreateNew)!;

    public void Check()
    {
        root.Check();
        if (database is null || journal is null) throw new DocumentStoreException(DocumentStoreError.Closed);
        _ = WindowsDocumentFiles.FileLength(database);
        _ = WindowsDocumentFiles.FileLength(journal);
    }

    public void Dispose()
    {
        journal?.Dispose(); journal = null;
        database?.Dispose(); database = null;
        root.Dispose();
    }
}

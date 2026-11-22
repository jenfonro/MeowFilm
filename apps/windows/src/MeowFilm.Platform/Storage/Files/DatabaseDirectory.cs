using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

/// <summary>Pins the database name and validates ordinary WAL/SHM/journal files without preventing SQLite's native cleanup.</summary>
public sealed class DatabaseDirectory : IDisposable
{
    private readonly WindowsDocumentFiles.Root root;
    private SafeFileHandle? database;
    public string DatabasePath { get; }
    public bool Created { get; private set; }

    public DatabaseDirectory(string databaseRoot, string fileName)
    {
        if (!OperatingSystem.IsWindows()) throw new DocumentStoreException(DocumentStoreError.Unavailable);
        if (fileName is not (NativeDatabase.DataFileName or NativeDatabase.CacheFileName))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        string validatedRoot = WindowsDocumentFiles.ValidateRoot(databaseRoot);
        using (var parent = WindowsDocumentFiles.Root.OpenParent(validatedRoot))
        using (var leaf = WindowsDocumentFiles.OpenDirectory(validatedRoot, true) ?? WindowsDocumentFiles.CreateDirectory(validatedRoot))
            root = new WindowsDocumentFiles.Root(validatedRoot);
        DatabasePath = Path.Combine(root.Path, fileName);
    }

    // The coordinator holds role-specific initialize lock before checking or creating this name.
    public void OpenDatabase()
    {
        try
        {
            database = OpenExisting(DatabasePath);
            Created = database is null;
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            {
                using var unexpected = WindowsDocumentFiles.OpenFile(DatabasePath + suffix,
                    WindowsDocumentFiles.Read, 7 /* SQLite owns sidecar removal */,
                    WindowsDocumentFiles.OpenExisting, allowMissing: true);
                if (Created && unexpected is not null) throw new DocumentStoreException(DocumentStoreError.Corrupt);
            }
            database ??= Create(DatabasePath);
            if (!Created && WindowsDocumentFiles.FileLength(database) == 0)
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
        }
        catch { Dispose(); throw; }
    }

    private static SafeFileHandle? OpenExisting(string path) => WindowsDocumentFiles.OpenFile(path,
        WindowsDocumentFiles.Read, 3, WindowsDocumentFiles.OpenExisting, allowMissing: true);
    private static SafeFileHandle Create(string path) => WindowsDocumentFiles.OpenFile(path,
        WindowsDocumentFiles.Read | WindowsDocumentFiles.Write, 3, WindowsDocumentFiles.CreateNew)!;

    public void CheckRoot() => root.Check();

    public void Check()
    {
        root.Check();
        if (database is null) throw new DocumentStoreException(DocumentStoreError.Closed);
        _ = WindowsDocumentFiles.FileLength(database);
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            using var sidecar = WindowsDocumentFiles.OpenFile(DatabasePath + suffix,
                WindowsDocumentFiles.Read, 7, WindowsDocumentFiles.OpenExisting, allowMissing: true);
        }
    }

    public void DeleteFailedCreation()
    {
        if (!Created) return;
        database?.Dispose();
        database = null;
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try
            {
                using var entry = WindowsDocumentFiles.OpenDeletionEntry(DatabasePath + suffix);
                if (entry is not null) WindowsDocumentFiles.DeleteEntry(entry);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        database?.Dispose(); database = null;
        root.Dispose();
    }
}

using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

/// <summary>Pins the database name and validates ordinary WAL/SHM/journal files without preventing SQLite's native cleanup.</summary>
public sealed class DatabaseDirectory : IDisposable
{
    private readonly WindowsStorageFiles.Root root;
    private SafeFileHandle? database;
    public string DatabasePath { get; }
    public bool Created { get; private set; }

    public DatabaseDirectory(string databaseRoot, string fileName)
    {
        if (!OperatingSystem.IsWindows()) throw new StorageException(StorageError.Unavailable);
        if (fileName is not (NativeDatabase.DataFileName or NativeDatabase.CacheFileName))
            throw new StorageException(StorageError.InvalidRequest);
        string validatedRoot = WindowsStorageFiles.ValidateRoot(databaseRoot);
        using (var parent = WindowsStorageFiles.Root.OpenParent(validatedRoot))
        using (var leaf = WindowsStorageFiles.OpenDirectory(validatedRoot, true) ?? WindowsStorageFiles.CreateDirectory(validatedRoot))
            root = new WindowsStorageFiles.Root(validatedRoot);
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
                using var unexpected = WindowsStorageFiles.OpenFile(DatabasePath + suffix,
                    WindowsStorageFiles.Read, 7 /* SQLite owns sidecar removal */,
                    WindowsStorageFiles.OpenExisting, allowMissing: true);
                if (Created && unexpected is not null) throw new StorageException(StorageError.Corrupt);
            }
            database ??= Create(DatabasePath);
            if (!Created && WindowsStorageFiles.FileLength(database) == 0)
                throw new StorageException(StorageError.Corrupt);
        }
        catch { Dispose(); throw; }
    }

    private static SafeFileHandle? OpenExisting(string path) => WindowsStorageFiles.OpenFile(path,
        WindowsStorageFiles.Read, 3, WindowsStorageFiles.OpenExisting, allowMissing: true);
    private static SafeFileHandle Create(string path) => WindowsStorageFiles.OpenFile(path,
        WindowsStorageFiles.Read | WindowsStorageFiles.Write, 3, WindowsStorageFiles.CreateNew)!;

    public void CheckRoot() => root.Check();

    public void Check()
    {
        root.Check();
        if (database is null) throw new StorageException(StorageError.Closed);
        _ = WindowsStorageFiles.FileLength(database);
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            using var sidecar = WindowsStorageFiles.OpenFile(DatabasePath + suffix,
                WindowsStorageFiles.Read, 7, WindowsStorageFiles.OpenExisting, allowMissing: true);
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
                using var entry = WindowsStorageFiles.OpenDeletionEntry(DatabasePath + suffix);
                if (entry is not null) WindowsStorageFiles.DeleteEntry(entry);
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

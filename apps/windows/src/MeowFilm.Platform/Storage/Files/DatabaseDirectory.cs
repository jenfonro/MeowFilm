using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

/// <summary>Holds the database directory root and file open handle while SQLite owns WAL/SHM/journal cleanup.</summary>
public sealed class DatabaseDirectory : IDisposable
{
    private SafeFileHandle? database;
    public string DatabasePath { get; }
    public bool Created { get; private set; }

    public DatabaseDirectory(string databaseRoot, string fileName)
    {
        if (!OperatingSystem.IsWindows()) throw new StorageException(StorageError.Unavailable);
        if (fileName is not (NativeDatabase.DataFileName or NativeDatabase.CacheFileName))
            throw new StorageException(StorageError.InvalidRequest);
        DatabasePath = Path.Combine(WindowsStorageFiles.EnsureDirectory(databaseRoot), fileName);
    }

    // The coordinator holds role-specific initialize lock before checking or creating this name.
    public void OpenDatabase()
    {
        try
        {
            database = WindowsStorageFiles.OpenHandle(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, allowMissing: true);
            Created = database is null;
            if (Created)
            {
                foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    if (File.Exists(DatabasePath + suffix))
                        throw new StorageException(StorageError.Corrupt);
                }
                database = WindowsStorageFiles.OpenHandle(DatabasePath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.ReadWrite, options: FileOptions.WriteThrough)!;
            }
            else if (WindowsStorageFiles.FileLength(database!) == 0)
            {
                throw new StorageException(StorageError.Corrupt);
            }
        }
        catch { Dispose(); throw; }
    }

    public void Check()
    {
        if (database is null) throw new StorageException(StorageError.Closed);
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
                string path = DatabasePath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        database?.Dispose(); database = null;
    }
}

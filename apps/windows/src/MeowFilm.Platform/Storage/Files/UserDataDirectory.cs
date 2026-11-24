using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

/// <summary>
/// Lifetime admission and one explicit reset of the host-selected current data root.
/// Callers release all owners before Clear.
/// All operations are synchronous and failures use fixed StorageException codes.
/// </summary>
public sealed class UserDataDirectory : IDisposable
{
    private enum State { Unopened, Shared, Pending, DataCleared, ResetComplete }
    private readonly object gate = new();
    private readonly string path, usagePath, admissionPath, pendingPath;
    private bool parentReady;
    private SafeFileHandle? usage;
    private State state;
    private bool disposed;

    public UserDataDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
        this.path = WindowsStorageFiles.ValidateRoot(path);
        string directory = Path.GetDirectoryName(this.path)!;
        string name = "." + Path.GetFileName(this.path);
        usagePath = Path.Combine(directory, name + ".usage.lock");
        admissionPath = Path.Combine(directory, name + ".usage-gate.lock");
        pendingPath = Path.Combine(directory, name + ".reset-pending");
    }

    public void Open()
    {
        lock (gate)
        {
            using (var admission = Enter())
            {
                if (state == State.ResetComplete)
                {
                    if (HasPending()) AcquireExclusive();
                    else { RestoreShared(); return; }
                }
                else if (state is not (State.Pending or State.DataCleared))
                {
                    if (HasPending()) AcquireExclusive();
                    else
                    {
                        if (state != State.Shared) { usage = OpenUsage(false); state = State.Shared; }
                        return;
                    }
                }
            }
            ClearPending();
        }
    }

    public void RequestReset()
    {
        lock (gate)
        {
            using var admission = Enter();
            if (state is State.DataCleared or State.ResetComplete) return;
            if (state != State.Pending) AcquireExclusive();
            PersistPending();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (disposed) WindowsStorageFiles.Fail(StorageError.Closed);
            if (state is not (State.Pending or State.DataCleared or State.ResetComplete))
                WindowsStorageFiles.Fail(StorageError.InvalidRequest);
            ClearPending();
        }
    }

    private SafeFileHandle Enter()
    {
        if (disposed) WindowsStorageFiles.Fail(StorageError.Closed);
        if (!parentReady)
        {
            WindowsStorageFiles.EnsureParentDirectory(path);
            parentReady = true;
        }
        return WindowsStorageFiles.OpenHandle(admissionPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)!;
    }

    private SafeFileHandle OpenUsage(bool exclusive) =>
        WindowsStorageFiles.OpenHandle(usagePath, FileMode.OpenOrCreate,
            exclusive ? FileAccess.ReadWrite : FileAccess.Read,
            exclusive ? FileShare.None : FileShare.Read)!;

    private bool HasPending() => File.Exists(pendingPath);

    private void AcquireExclusive()
    {
        bool restoreShared = state == State.Shared;
        usage?.Dispose();
        usage = null;
        state = State.Unopened;
        try
        {
            usage = OpenUsage(true);
            state = State.Pending;
        }
        catch
        {
            if (restoreShared)
            {
                try { usage = OpenUsage(false); state = State.Shared; }
                catch (StorageException) { }
            }
            throw;
        }
    }

    private void PersistPending()
    {
        try
        {
            using var stream = new FileStream(pendingPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
        }
        catch (StorageException) { throw; }
        catch (IOException) { WindowsStorageFiles.Fail(StorageError.Storage); }
        catch (UnauthorizedAccessException) { WindowsStorageFiles.Fail(StorageError.Storage); }
    }

    private void ClearPending()
    {
        if (state == State.Pending)
        {
            PersistPending();
            DeleteRoots();
            state = State.DataCleared;
        }
        using var admission = Enter();
        if (state == State.DataCleared)
        {
            try { if (File.Exists(pendingPath)) File.Delete(pendingPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { WindowsStorageFiles.Fail(StorageError.Storage); }
            state = State.ResetComplete;
        }
        RestoreShared();
    }

    private void RestoreShared()
    {
        usage?.Dispose();
        usage = null;
        try
        {
            if (HasPending()) WindowsStorageFiles.Fail(StorageError.Storage);
            usage = OpenUsage(false);
            state = State.Shared;
        }
        catch
        {
            try { usage = OpenUsage(true); }
            catch (StorageException) { }
            throw;
        }
    }

    private void DeleteRoots()
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { WindowsStorageFiles.Fail(StorageError.Storage); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            usage?.Dispose();
            usage = null;
        }
    }
}

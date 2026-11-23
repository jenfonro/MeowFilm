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
    private WindowsStorageFiles.Root? parent;
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

    /// <summary>
    /// Obtain shared admission before constructing business owners. Existing normal
    /// admission is idempotent; an interrupted reset is completed before returning.
    /// </summary>
    public void Open()
    {
        lock (gate)
        {
            using (var admission = Enter())
            {
                if (state == State.ResetComplete)
                {
                    // A newer interrupted reset is distinct from our completed one.
                    // Open recovers it; retrying Clear never deletes new data.
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

    /// <summary>
    /// After explicit user confirmation, obtain exclusive admission and persist reset
    /// intent. Does not delete data or require the current process's owners to be closed.
    /// </summary>
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

    /// <summary>
    /// After owners actually release, remove the owned root without following links.
    /// Failed cleanup retains pending intent/exclusive admission for explicit retry.
    /// Once deletion commits, a lease retry never deletes a later generation's data.
    /// </summary>
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
        parent ??= WindowsStorageFiles.Root.OpenParent(path);
        parent.Check();
        // Short, fail-fast transition gate, not a normal-running single-instance lock.
        // It closes the gap between closing one sharing mode and opening the next.
        return WindowsStorageFiles.OpenFile(admissionPath,
            WindowsStorageFiles.Read | WindowsStorageFiles.Write, 0, WindowsStorageFiles.OpenAlways)!;
    }

    private SafeFileHandle OpenUsage(bool exclusive) =>
        WindowsStorageFiles.OpenFile(usagePath,
            exclusive ? WindowsStorageFiles.Read | WindowsStorageFiles.Write : WindowsStorageFiles.Read,
            exclusive ? 0 : WindowsStorageFiles.ShareRead, WindowsStorageFiles.OpenAlways)!;

    private bool HasPending()
    {
        using var marker = WindowsStorageFiles.OpenFile(pendingPath, WindowsStorageFiles.Read,
            WindowsStorageFiles.ShareRead, WindowsStorageFiles.OpenExisting, allowMissing: true);
        return marker is not null; // Presence is authority; never parse old data or marker contents.
    }

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
            // Still inside admission: cooperating resets cannot slip into this gap.
            // If restoration itself fails, the next Open must check pending again.
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
            using var marker = WindowsStorageFiles.OpenFile(pendingPath,
                WindowsStorageFiles.Read | WindowsStorageFiles.Write, 0, WindowsStorageFiles.OpenAlways)!;
            using var stream = new FileStream(marker, FileAccess.ReadWrite);
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
            using (var marker = WindowsStorageFiles.OpenFile(pendingPath, WindowsStorageFiles.Delete,
                0, WindowsStorageFiles.OpenExisting, allowMissing: true))
                if (marker is not null) WindowsStorageFiles.DeleteEntry(marker);
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
            // A previous failed downgrade may have lost all admission. Another
            // reset's marker must not be bypassed, nor mistaken for our old reset.
            if (HasPending()) WindowsStorageFiles.Fail(StorageError.Storage);
            usage = OpenUsage(false);
            state = State.Shared;
        }
        catch
        {
            try { usage = OpenUsage(true); }
            catch (StorageException) { }
            // ResetComplete survives even if neither handle could be reopened.
            throw;
        }
    }

    private void DeleteRoots()
    {
        // Admit deletion before touching any child. Database/resource pins must
        // have been released by the actual owners.
        using var business = WindowsStorageFiles.OpenDeletionEntry(path);
        DeleteTree(path, business);
    }

    private static void DeleteTree(string path)
    {
        using var entry = WindowsStorageFiles.OpenDeletionEntry(path);
        DeleteTree(path, entry);
    }

    private static void DeleteTree(string path, SafeFileHandle? entry)
    {
        try
        {
            if (entry is null) return;
            if (WindowsStorageFiles.IsOrdinaryDirectory(entry))
                foreach (string child in Directory.EnumerateFileSystemEntries(WindowsStorageFiles.NativePath(path)))
                    DeleteTree(Path.Combine(path, Path.GetFileName(child)));
            WindowsStorageFiles.DeleteEntry(entry);
        }
        catch (StorageException) { throw; }
        catch (IOException) { WindowsStorageFiles.Fail(StorageError.Storage); }
        catch (UnauthorizedAccessException) { WindowsStorageFiles.Fail(StorageError.Storage); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            usage?.Dispose();
            parent?.Dispose();
            usage = null;
            parent = null;
        }
    }
}

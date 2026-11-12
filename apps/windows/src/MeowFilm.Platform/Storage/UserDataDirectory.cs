using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

/// <summary>
/// Lifetime admission and one explicit reset of the host-selected business directory
/// and its optional sibling cache directory. Callers release all owners before Clear.
/// All operations are synchronous and failures use fixed DocumentStoreException codes.
/// </summary>
public sealed class UserDataDirectory : IDisposable
{
    private enum State { Unopened, Shared, Pending, DataCleared, ResetComplete }
    private readonly object gate = new();
    private readonly string path, usagePath, admissionPath, pendingPath;
    private readonly string? cachePath;
    private WindowsDocumentFiles.Root? parent;
    private SafeFileHandle? usage;
    private State state;
    private bool disposed;

    public UserDataDirectory(string path, string? cachePath = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
        this.path = WindowsDocumentFiles.ValidateRoot(path);
        string directory = Path.GetDirectoryName(this.path)!;
        string name = "." + Path.GetFileName(this.path);
        usagePath = Path.Combine(directory, name + ".usage.lock");
        admissionPath = Path.Combine(directory, name + ".usage-gate.lock");
        pendingPath = Path.Combine(directory, name + ".reset-pending");
        if (cachePath is not null)
        {
            this.cachePath = WindowsDocumentFiles.ValidateRoot(cachePath);
            // Both roots share this App's admission and pinned parent, never a
            // document queue. Normal Open does not touch the cache leaf.
            var comparer = StringComparer.OrdinalIgnoreCase;
            if (!comparer.Equals(directory, Path.GetDirectoryName(this.cachePath)) ||
                comparer.Equals(this.path, this.cachePath) || comparer.Equals(usagePath, this.cachePath) ||
                comparer.Equals(admissionPath, this.cachePath) || comparer.Equals(pendingPath, this.cachePath))
                WindowsDocumentFiles.Fail(DocumentStoreError.InvalidRequest);
        }
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
    /// After owners actually release, remove both owned roots without following links.
    /// Failed cleanup retains pending intent/exclusive admission for explicit retry.
    /// Once deletion commits, a lease retry never deletes a later generation's data.
    /// </summary>
    public void Clear()
    {
        lock (gate)
        {
            if (disposed) WindowsDocumentFiles.Fail(DocumentStoreError.Closed);
            if (state is not (State.Pending or State.DataCleared or State.ResetComplete))
                WindowsDocumentFiles.Fail(DocumentStoreError.InvalidRequest);
            ClearPending();
        }
    }

    private SafeFileHandle Enter()
    {
        if (disposed) WindowsDocumentFiles.Fail(DocumentStoreError.Closed);
        parent ??= WindowsDocumentFiles.Root.OpenParent(path);
        parent.Check();
        // Short, fail-fast transition gate, not a normal-running single-instance lock.
        // It closes the gap between closing one sharing mode and opening the next.
        return WindowsDocumentFiles.OpenFile(admissionPath,
            WindowsDocumentFiles.Read | WindowsDocumentFiles.Write, 0, WindowsDocumentFiles.OpenAlways)!;
    }

    private SafeFileHandle OpenUsage(bool exclusive) =>
        WindowsDocumentFiles.OpenFile(usagePath,
            exclusive ? WindowsDocumentFiles.Read | WindowsDocumentFiles.Write : WindowsDocumentFiles.Read,
            exclusive ? 0 : WindowsDocumentFiles.ShareRead, WindowsDocumentFiles.OpenAlways)!;

    private bool HasPending()
    {
        using var marker = WindowsDocumentFiles.OpenFile(pendingPath, WindowsDocumentFiles.Read,
            WindowsDocumentFiles.ShareRead, WindowsDocumentFiles.OpenExisting, allowMissing: true);
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
                catch (DocumentStoreException) { }
            }
            throw;
        }
    }

    private void PersistPending()
    {
        try
        {
            using var marker = WindowsDocumentFiles.OpenFile(pendingPath,
                WindowsDocumentFiles.Read | WindowsDocumentFiles.Write, 0, WindowsDocumentFiles.OpenAlways)!;
            using var stream = new FileStream(marker, FileAccess.ReadWrite);
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
        }
        catch (DocumentStoreException) { throw; }
        catch (IOException) { WindowsDocumentFiles.Fail(DocumentStoreError.Storage); }
        catch (UnauthorizedAccessException) { WindowsDocumentFiles.Fail(DocumentStoreError.Storage); }
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
            using (var marker = WindowsDocumentFiles.OpenFile(pendingPath, WindowsDocumentFiles.Delete,
                0, WindowsDocumentFiles.OpenExisting, allowMissing: true))
                if (marker is not null) WindowsDocumentFiles.DeleteEntry(marker);
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
            if (HasPending()) WindowsDocumentFiles.Fail(DocumentStoreError.Storage);
            usage = OpenUsage(false);
            state = State.Shared;
        }
        catch
        {
            try { usage = OpenUsage(true); }
            catch (DocumentStoreException) { }
            // ResetComplete survives even if neither handle could be reopened.
            throw;
        }
    }

    private void DeleteRoots()
    {
        // Admit deletion of every root before touching any child. An existing
        // cache store pin is as authoritative as an existing business store pin.
        using var business = WindowsDocumentFiles.OpenDeletionEntry(path);
        using var cache = cachePath is null ? null : WindowsDocumentFiles.OpenDeletionEntry(cachePath);
        DeleteTree(path, business);
        if (cachePath is not null) DeleteTree(cachePath, cache);
    }

    private static void DeleteTree(string path)
    {
        using var entry = WindowsDocumentFiles.OpenDeletionEntry(path);
        DeleteTree(path, entry);
    }

    private static void DeleteTree(string path, SafeFileHandle? entry)
    {
        try
        {
            if (entry is null) return;
            if (WindowsDocumentFiles.IsOrdinaryDirectory(entry))
                foreach (string child in Directory.EnumerateFileSystemEntries(WindowsDocumentFiles.NativePath(path)))
                    DeleteTree(Path.Combine(path, Path.GetFileName(child)));
            WindowsDocumentFiles.DeleteEntry(entry);
        }
        catch (DocumentStoreException) { throw; }
        catch (IOException) { WindowsDocumentFiles.Fail(DocumentStoreError.Storage); }
        catch (UnauthorizedAccessException) { WindowsDocumentFiles.Fail(DocumentStoreError.Storage); }
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

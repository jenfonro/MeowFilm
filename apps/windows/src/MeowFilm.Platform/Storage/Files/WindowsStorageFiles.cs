using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

// All path traversal is native, no-follow at each component; pinned directory handles
// deny rename/delete. Files are validated through their open handle before any bytes are read.
internal static class WindowsStorageFiles
{
    internal const uint Read = 0x80000000, Write = 0x40000000, Delete = 0x00010000;
    internal const uint ShareRead = 1, CreateNew = 1, OpenExisting = 3, OpenAlways = 4;
    private const uint DirectoryShare = ShareRead | 2 /* FILE_SHARE_WRITE; never FILE_SHARE_DELETE */;
    private const uint OpenReparse = 0x00200000, BackupSemantics = 0x02000000;
    private const uint DirectoryAttribute = 0x10, ReparseAttribute = 0x400;

    internal sealed class Root : IDisposable
    {
        internal string Path { get; }

        internal Root(string path) : this(ValidateRoot(path), false) { }

        internal static Root OpenOrCreate(string path) => new(ValidateRoot(path), true);

        internal static Root OpenParent(string validatedRoot) =>
            new(System.IO.Path.GetDirectoryName(validatedRoot)!, true);

        private Root(string path, bool createMissing)
        {
            Path = path;
            if (createMissing) System.IO.Directory.CreateDirectory(Path);
            else if (!System.IO.Directory.Exists(Path)) Fail(StorageError.UnsafePath);
        }

        internal void Check() { }

        public void Dispose() { }
    }

    internal static string ValidateRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) Fail(StorageError.InvalidRequest);
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            string? root = System.IO.Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) throw new StorageException(StorageError.UnsafePath);
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }
        catch (StorageException) { throw; }
        catch { throw new StorageException(StorageError.UnsafePath); }
    }

    internal static string NativePath(string validatedPath)
    {
        if (validatedPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return validatedPath;
        if (validatedPath.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + validatedPath[2..];
        if (validatedPath.Length >= 3 && char.IsAsciiLetter(validatedPath[0]) &&
            validatedPath[1] == ':' && validatedPath[2] == '\\')
            return @"\\?\" + validatedPath;
        return validatedPath;
    }

    internal static SafeFileHandle? OpenDirectory(string path, bool allowMissing)
    {
        var handle = CreateFileW(NativePath(path), 0x81 /* FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES */, DirectoryShare, IntPtr.Zero,
            OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (allowMissing && error is 2 or 3) return null;
            Fail(StorageError.UnsafePath);
        }
        try { ValidateDirectory(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static SafeFileHandle CreateDirectory(string path)
    {
        if (!CreateDirectoryW(NativePath(path), IntPtr.Zero) && Marshal.GetLastPInvokeError() != 183)
            Fail(StorageError.Storage);
        return OpenDirectory(path, false)!;
    }

    private static void ValidateDirectory(SafeFileHandle handle)
    {
        uint flags = Information(handle).FileAttributes;
        if ((flags & DirectoryAttribute) == 0)
            Fail(StorageError.UnsafePath);
    }

    internal static SafeFileHandle? OpenFile(string path, uint access, uint share, uint creation,
        bool allowMissing = false, bool sharingIsBusy = false)
    {
        var handle = CreateFileW(NativePath(path), access, share, IntPtr.Zero, creation,
            0x80000000 /* WRITE_THROUGH */, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (allowMissing && error is 2 or 3) return null;
            if (sharingIsBusy && error is 32 or 33) return null;
            Fail(StorageError.Storage);
        }
        try { _ = FileLength(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static long FileLength(SafeFileHandle handle)
    {
        var info = Information(handle);
        if ((info.FileAttributes & DirectoryAttribute) != 0 || GetFileType(handle) != 1)
            Fail(StorageError.UnsafePath);
        return checked((long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow));
    }

    // DELETE both tests existing store pins before the first child is touched and
    // blocks a legacy store (which does not share DELETE) from opening this root.
    // Denying DELETE sharing pins the name throughout path-based child traversal.
    internal static SafeFileHandle? OpenDeletionEntry(string path)
    {
        var handle = CreateFileW(NativePath(path), Delete | 0x80 /* FILE_READ_ATTRIBUTES */,
            DirectoryShare, IntPtr.Zero, OpenExisting, OpenReparse | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 2 or 3) return null;
            Fail(StorageError.Storage);
        }
        try
        {
            if (GetFileType(handle) != 1) Fail(StorageError.UnsafePath);
            _ = Information(handle);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static bool IsOrdinaryDirectory(SafeFileHandle handle) =>
        (Information(handle).FileAttributes & (DirectoryAttribute | ReparseAttribute)) == DirectoryAttribute;

    // Unlink only this opened entry, never a re-resolved path or a link target.
    // POSIX disposition removes the name on close even if another reader shares
    // DELETE; ignoring READONLY does not change attributes of an external hardlink.
    internal static void DeleteEntry(SafeFileHandle handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, 0x1 | 0x2 | 0x10);
            if (!SetFileInformationByHandle(handle, 21 /* FileDispositionInfoEx */, buffer, sizeof(int)))
                Fail(StorageError.Storage);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // Rename by the already-open source handle, never a string-based delete/move.
    // Destination ancestors remain pinned and all cooperating writers hold the root lock.
    internal static void Replace(SafeFileHandle source, string destination)
    {
        byte[] name = Encoding.Unicode.GetBytes(NativePath(destination));
        int header = IntPtr.Size * 2 + sizeof(uint);
        // Win32 FILE_RENAME_INFO requires a NUL-terminated WCHAR FileName, even
        // though FileNameLength excludes the terminator. Never let native path
        // conversion scan uninitialized memory beyond this allocation.
        int size = checked(header + name.Length + sizeof(char));
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[header], 0, buffer, header);
            // FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS.
            // The older FileRenameInfo class rejects an open destination even when its
            // readers share DELETE. Ex preserves their old file while new opens
            // see the replacement; readers that deny DELETE still block commit.
            // Unsupported systems/filesystems fail closed, never delete-then-copy.
            Marshal.WriteInt32(buffer, 0x1 | 0x2);
            Marshal.WriteInt32(buffer, IntPtr.Size * 2, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, header), name.Length);
            Marshal.WriteInt16(buffer, header + name.Length, 0);
            if (!SetFileInformationByHandle(source, 22 /* FileRenameInfoEx */, buffer, (uint)size))
                Fail(StorageError.Storage);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) Fail(StorageError.Storage);
        return info;
    }

    internal static void Fail(StorageError error) => throw new StorageException(error);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

// All path traversal is native, no-follow at each component; pinned directory handles
// deny rename/delete. Files are validated through their open handle before any bytes are read.
internal static class WindowsDocumentFiles
{
    internal const uint Read = 0x80000000, Write = 0x40000000, Delete = 0x00010000;
    internal const uint ShareRead = 1, CreateNew = 1, OpenExisting = 3, OpenAlways = 4;
    private const uint DirectoryShare = ShareRead | 2 /* FILE_SHARE_WRITE; never FILE_SHARE_DELETE */;
    private const uint OpenReparse = 0x00200000, BackupSemantics = 0x02000000;
    private const uint DirectoryAttribute = 0x10, ReparseAttribute = 0x400;

    internal sealed class Root : IDisposable
    {
        private readonly List<SafeFileHandle> handles = new();
        internal string Path { get; }
        internal byte[] Identity { get; }

        internal Root(string path)
        {
            Path = ValidateRoot(path);
            try
            {
                string cursor = System.IO.Path.GetPathRoot(Path)!;
                handles.Add(OpenDirectory(cursor, false)!);
                foreach (string segment in Path[cursor.Length..].Split('\\'))
                {
                    cursor = System.IO.Path.Combine(cursor, segment);
                    handles.Add(OpenDirectory(cursor, false)!);
                }
                var info = Information(handles[^1]);
                if (info.FileIndexHigh == 0 && info.FileIndexLow == 0) Fail(DocumentStoreError.UnsafePath);
                Identity = new byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(Identity, info.VolumeSerialNumber);
                BinaryPrimitives.WriteUInt32LittleEndian(Identity.AsSpan(4), info.FileIndexHigh);
                BinaryPrimitives.WriteUInt32LittleEndian(Identity.AsSpan(8), info.FileIndexLow);
            }
            catch { Dispose(); throw; }
        }

        internal void Check()
        {
            foreach (var handle in handles) ValidateDirectory(handle);
        }

        public void Dispose()
        {
            for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
            handles.Clear();
        }
    }

    private static string ValidateRoot(string path)
    {
        if (string.IsNullOrEmpty(path)) Fail(DocumentStoreError.InvalidRequest);
        path = path.Replace('/', '\\').TrimEnd('\\');
        if (path.Length is < 4 or > 1024 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            Fail(DocumentStoreError.UnsafePath);
        foreach (string segment in path[3..].Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny([':', '<', '>', '"', '|', '?', '*', '\0']) >= 0)
                Fail(DocumentStoreError.UnsafePath);
            // Extended paths bypass Win32 normalization: reject nonordinary input
            // explicitly rather than relying on a later Win32 path conversion.
            foreach (char c in segment) if (c < ' ') Fail(DocumentStoreError.UnsafePath);
            string stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 stem[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')) Fail(DocumentStoreError.UnsafePath);
        }
        uint driveType = GetDriveTypeW(System.IO.Path.GetPathRoot(path)!);
        if (driveType is not (2 or 3)) Fail(DocumentStoreError.UnsafePath); // No network/device roots.
        string full = System.IO.Path.GetFullPath(path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(full, path)) Fail(DocumentStoreError.UnsafePath);
        return full;
    }

    // Call only for the canonical root/ancestors accepted by ValidateRoot or the
    // fixed, generated descendants of that root. Never accept an external extended,
    // UNC, device or relative path. Keep Root.Path/namespace/entropy identities unchanged.
    // This bypasses MAX_PATH without depending on host manifests or machine policy;
    // it does not bypass no-follow opens, directory pins or file-handle validation.
    private static string NativePath(string validatedPath)
    {
        if (validatedPath.Length < 3 || !char.IsAsciiLetter(validatedPath[0]) ||
            validatedPath[1] != ':' || validatedPath[2] != '\\') Fail(DocumentStoreError.UnsafePath);
        return @"\\?\" + validatedPath;
    }

    internal static SafeFileHandle? OpenDirectory(string path, bool allowMissing)
    {
        // Attribute-only opens are exempt from sharing checks. FILE_LIST_DIRECTORY
        // makes denial of delete/rename effective for the lifetime of this handle.
        // Rename also opens the target directory for FILE_ADD_FILE. Allow that
        // write access so our own pins do not cause ERROR_SHARING_VIOLATION.
        // Only directories use this sharing mask; document/stage sharing stays read-only.
        var handle = CreateFileW(NativePath(path), 0x81 /* FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES */, DirectoryShare, IntPtr.Zero,
            OpenExisting, OpenReparse | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (allowMissing && error is 2 or 3) return null;
            Fail(DocumentStoreError.UnsafePath);
        }
        try { ValidateDirectory(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static SafeFileHandle CreateDirectory(string path)
    {
        if (!CreateDirectoryW(NativePath(path), IntPtr.Zero) && Marshal.GetLastPInvokeError() != 183)
            Fail(DocumentStoreError.Storage);
        return OpenDirectory(path, false)!;
    }

    private static void ValidateDirectory(SafeFileHandle handle)
    {
        uint flags = Information(handle).FileAttributes;
        if ((flags & DirectoryAttribute) == 0 || (flags & ReparseAttribute) != 0)
            Fail(DocumentStoreError.UnsafePath);
    }

    internal static SafeFileHandle? OpenFile(string path, uint access, uint share, uint creation,
        bool allowMissing = false, bool sharingIsBusy = false)
    {
        var handle = CreateFileW(NativePath(path), access, share, IntPtr.Zero, creation,
            OpenReparse | 0x80000000 /* WRITE_THROUGH */, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (allowMissing && error is 2 or 3) return null;
            if (sharingIsBusy && error is 32 or 33) return null;
            Fail(DocumentStoreError.Storage);
        }
        try { _ = FileLength(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static long FileLength(SafeFileHandle handle)
    {
        var info = Information(handle);
        if ((info.FileAttributes & (DirectoryAttribute | ReparseAttribute)) != 0 || info.NumberOfLinks != 1 ||
            GetFileType(handle) != 1) Fail(DocumentStoreError.UnsafePath);
        return checked((long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow));
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
                Fail(DocumentStoreError.Storage);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void DiscardStage(SafeFileHandle source)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, 1);
            // A failed cleanup leaves only a ciphertext orphan. Never delete a path
            // that may now name another file and never mask the original failure.
            _ = SetFileInformationByHandle(source, 4 /* FileDispositionInfo */, buffer, sizeof(int));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) Fail(DocumentStoreError.Storage);
        return info;
    }

    internal static void Fail(DocumentStoreError error) => throw new ProtectedDocumentException(error);

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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string rootPath);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
}

internal static class CurrentUserDpapi
{
    internal const int MaximumCiphertextBytes = DocumentRules.MaximumDocumentBytes + 64 * 1024;

    internal static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        var inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        Blob output = default;
        try
        {
            var source = new Blob { Size = input.Length, Data = inputPin.AddrOfPinnedObject() };
            var optional = new Blob { Size = entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
            // No LOCAL_MACHINE flag; UI forbidden, no prompt structure, no description.
            bool success = protect
                ? CryptProtectData(ref source, null, ref optional, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, ref optional, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) WindowsDocumentFiles.Fail(DocumentStoreError.Protection);
            if (output.Size <= 0 || output.Size > MaximumCiphertextBytes || output.Data == IntPtr.Zero)
                WindowsDocumentFiles.Fail(DocumentStoreError.Corrupt);
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // Best effort for this native allocation; not a claim about DPAPI/GC/caller copies.
                if (output.Size is > 0 and <= MaximumCiphertextBytes)
                {
                    var zeros = new byte[Math.Min(output.Size, 8192)];
                    for (int offset = 0; offset < output.Size; offset += zeros.Length)
                        Marshal.Copy(zeros, 0, IntPtr.Add(output.Data, offset), Math.Min(zeros.Length, output.Size - offset));
                }
                _ = LocalFree(output.Data);
            }
            entropyPin.Free();
            inputPin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { internal int Size; internal IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Files;

// Internal only: callers cannot choose arbitrary extensions or increase a public bound.
internal sealed record SelectedFileSnapshotData(byte[] Bytes, string Basename);
internal enum SelectedFileSnapshotError
{
    InvalidPath, Unavailable, UnsafeFile, TooLarge, InUse, Changed, ReadFailed, IncompleteSnapshot
}
internal sealed class SelectedFileSnapshotException(SelectedFileSnapshotError error) : Exception(error.ToString())
{
    internal SelectedFileSnapshotError Error { get; } = error;
}

/// <summary>
/// One policy for native, explicitly selected inputs. No cached path grants or runtime RPC.
/// Pins a verified local volume root, then opens one child at a time relative to the held
/// parent handle. Later directory/drive-name resolution cannot redirect the read.
/// Not a sandbox against malicious same-user processes, kernel/filter manipulation or
/// pre-existing writable memory mappings. No filesystem/OS-global policy is changed.
/// </summary>
internal static class SelectedFileSnapshot
{
    private const int ChunkBytes = 64 * 1024, MaximumPathCharacters = 4096, MaximumComponents = 128;
    private const uint DirectoryAttribute = 0x10, DeviceAttribute = 0x40, ReparseAttribute = 0x400, OfflineAttribute = 0x1000;
    private const uint NoFollow = 0x00200000, BackupSemantics = 0x02000000;

    internal static async Task<SelectedFileSnapshotData> ReadAsync(string selectedPath, string extension,
        int maximumBytes, CancellationToken cancellation, string[]? forbiddenCompanionSuffixes = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
        try
        {
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                string path = ValidatePath(selectedPath, extension);
                using var opened = OpenSnapshot(path, maximumBytes, cancellation, forbiddenCompanionSuffixes ?? []);
                return opened.Read(cancellation);
            }, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new OperationCanceledException(cancellation); }
        catch (SelectedFileSnapshotException) { throw; }
        catch { throw new SelectedFileSnapshotException(SelectedFileSnapshotError.ReadFailed); }
    }

    private static string ValidatePath(string path, string extension)
    {
        if (string.IsNullOrEmpty(path) || path.Length is < 4 or > MaximumPathCharacters)
            Fail(SelectedFileSnapshotError.InvalidPath);
        path = path.Replace('/', '\\');
        if (!char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            Fail(SelectedFileSnapshotError.InvalidPath);
        string[] segments = path[3..].Split('\\');
        if (segments.Length > MaximumComponents) Fail(SelectedFileSnapshotError.InvalidPath);
        foreach (string segment in segments)
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0)
                Fail(SelectedFileSnapshotError.InvalidPath);
            for (int i = 0; i < segment.Length; i++)
            {
                char c = segment[i];
                if (c < ' ') Fail(SelectedFileSnapshotError.InvalidPath);
                if (!char.IsSurrogate(c)) continue;
                if (!char.IsHighSurrogate(c) || i + 1 == segment.Length || !char.IsLowSurrogate(segment[++i]))
                    Fail(SelectedFileSnapshotError.InvalidPath);
            }
            string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 stem[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))
                Fail(SelectedFileSnapshotError.InvalidPath);
        }
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase) ||
            !StringComparer.OrdinalIgnoreCase.Equals(path, Path.GetFullPath(path)))
            Fail(SelectedFileSnapshotError.InvalidPath);
        ValidateDriveType(GetDriveTypeW(path[..3]));
        return path;
    }

    private static void ValidateDriveType(uint type)
    {
        // Fail closed for unknown, absent, remote (including mapped SMB), optical and RAM drives.
        if (type is not (2 or 3)) Fail(SelectedFileSnapshotError.UnsafeFile);
    }

    private static string LocalVolume(string root)
    {
        ValidateDriveType(GetDriveTypeW(root));
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, name, (uint)name.Capacity))
            Fail(SelectedFileSnapshotError.UnsafeFile);
        string volume = name.ToString();
        if (!volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
            !volume.EndsWith(@"}\", StringComparison.Ordinal))
            Fail(SelectedFileSnapshotError.UnsafeFile);
        ValidateDriveType(GetDriveTypeW(volume));
        return volume;
    }

    private static OpenedSnapshot OpenSnapshot(string validatedPath, int maximumBytes, CancellationToken cancellation,
        string[] companionSuffixes)
    {
        var opened = new OpenedSnapshot(Path.GetFileName(validatedPath), maximumBytes, companionSuffixes);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            opened.Root = validatedPath[..3];
            opened.Volume = LocalVolume(opened.Root);
            // The \\?\ conversion is internal only. A caller-supplied device path is never accepted.
            var root = CreateFileW(@"\\?\" + opened.Root, 0x00100081, 3, IntPtr.Zero, 3,
                NoFollow | BackupSemantics, IntPtr.Zero);
            CheckOpen(root);
            opened.Directories.Add(root);
            opened.DirectoryIdentities.Add(ValidateDirectory(root));
            var finalRoot = new StringBuilder(128);
            uint count = GetFinalPathNameByHandleW(root, finalRoot, (uint)finalRoot.Capacity, 1); // VOLUME_NAME_GUID
            if (count == 0 || count >= finalRoot.Capacity ||
                !StringComparer.OrdinalIgnoreCase.Equals(finalRoot.ToString(), opened.Volume))
                Fail(SelectedFileSnapshotError.UnsafeFile); // Includes a SUBST rooted inside another directory.
            string[] segments = validatedPath[3..].Split('\\');
            for (int i = 0; i < segments.Length - 1; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var directory = OpenChild(opened.Directories[^1], segments[i], directory: true);
                opened.Directories.Add(directory);
                opened.DirectoryIdentities.Add(ValidateDirectory(directory));
            }
            cancellation.ThrowIfCancellationRequested();
            opened.File = OpenChild(opened.Directories[^1], segments[^1], directory: false);
            opened.CheckCompanions(cancellation);
            opened.Initial = Information(opened.File);
            ValidateFile(opened.File, opened.Initial);
            if (Length(opened.Initial.Info) > (ulong)maximumBytes) Fail(SelectedFileSnapshotError.TooLarge);
            cancellation.ThrowIfCancellationRequested();
            return opened;
        }
        catch { opened.Dispose(); throw; }
    }

    private sealed class OpenedSnapshot(string basename, int maximumBytes, string[] companionSuffixes) : IDisposable
    {
        internal readonly List<SafeFileHandle> Directories = [];
        internal readonly List<FileIdentity> DirectoryIdentities = [];
        internal SafeFileHandle? File;
        internal SnapshotInformation Initial;
        internal string Root = "", Volume = "";
        internal void CheckCompanions(CancellationToken cancellation)
        {
            foreach (string suffix in companionSuffixes)
            {
                cancellation.ThrowIfCancellationRequested();
                // Only trusted wrappers supply these fixed suffixes; no public
                // arbitrary sibling path or directory-enumeration capability.
                if (suffix.Length == 0 || suffix.Any(c => !char.IsAsciiLetter(c) && c != '-') ||
                    basename.Length + suffix.Length > 255) Fail(SelectedFileSnapshotError.InvalidPath);
                RequireAbsentChild(Directories[^1], basename + suffix);
            }
        }

        internal SelectedFileSnapshotData Read(CancellationToken cancellation)
        {
            byte[]? bytes = null;
            bool returned = false;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                // Disable FileStream read-ahead: the EOF probe must request only one extra byte.
                using var stream = new FileStream(File!, FileAccess.Read, bufferSize: 1, isAsync: false);
                bytes = ReadFixed(stream, checked((int)Length(Initial.Info)), maximumBytes, cancellation);
                cancellation.ThrowIfCancellationRequested();
                var after = Information(File!);
                ValidateFile(File!, after);
                if (!SameIdentity(Initial.Identity, after.Identity) || Length(Initial.Info) != Length(after.Info) ||
                    Initial.Basic.CreationTime != after.Basic.CreationTime ||
                    Initial.Basic.LastWriteTime != after.Basic.LastWriteTime ||
                    Initial.Basic.ChangeTime != after.Basic.ChangeTime ||
                    Initial.Basic.FileAttributes != after.Basic.FileAttributes)
                    Fail(SelectedFileSnapshotError.Changed);
                for (int i = 0; i < Directories.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!SameIdentity(DirectoryIdentities[i], ValidateDirectory(Directories[i])))
                        Fail(SelectedFileSnapshotError.Changed);
                }
                if (!StringComparer.OrdinalIgnoreCase.Equals(Volume, LocalVolume(Root)))
                    Fail(SelectedFileSnapshotError.Changed);
                CheckCompanions(cancellation);
                cancellation.ThrowIfCancellationRequested();
                var result = new SelectedFileSnapshotData(bytes, basename);
                returned = true;
                return result;
            }
            finally { if (!returned && bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
        }

        public void Dispose()
        {
            File?.Dispose();
            for (int i = Directories.Count - 1; i >= 0; i--) Directories[i].Dispose();
            Directories.Clear();
            DirectoryIdentities.Clear();
        }
    }

    private static unsafe void RequireAbsentChild(SafeFileHandle parent, string component)
    {
        bool referenced = false;
        try
        {
            parent.DangerousAddRef(ref referenced);
            fixed (char* text = component)
            {
                var name = new UnicodeString { Length = checked((ushort)(component.Length * 2)),
                    MaximumLength = checked((ushort)(component.Length * 2)), Buffer = (IntPtr)text };
                var attributes = new ObjectAttributes { Length = (uint)sizeof(ObjectAttributes),
                    RootDirectory = parent.DangerousGetHandle(), ObjectName = (IntPtr)(&name), Attributes = 0x1040 };
                // Attributes only, FILE_OPEN, no recall/reparse, no creation.
                // A present file (including empty), link, directory, access
                // denial or other uncertainty is not evidence of absence.
                int status = NtCreateFile(out var handle, 0x00100080, ref attributes, out _, IntPtr.Zero, 0,
                    7, 1, 0x00600020, IntPtr.Zero, 0);
                handle?.Dispose();
                if (unchecked((uint)status) != 0xC0000034) Fail(SelectedFileSnapshotError.IncompleteSnapshot);
            }
        }
        finally { if (referenced) parent.DangerousRelease(); }
    }

    // Fixed allocation and bounded chunks, plus an EOF probe; never return a prefix on change.
    private static byte[] ReadFixed(Stream stream, int length, int maximumBytes, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (length < 0 || length > maximumBytes) Fail(SelectedFileSnapshotError.TooLarge);
        var bytes = new byte[length];
        try
        {
            for (int offset = 0; offset < bytes.Length;)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = stream.Read(bytes, offset, Math.Min(ChunkBytes, bytes.Length - offset));
                if (count == 0) Fail(SelectedFileSnapshotError.Changed);
                offset += count;
            }
            cancellation.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1) Fail(SelectedFileSnapshotError.Changed);
            cancellation.ThrowIfCancellationRequested();
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private static unsafe SafeFileHandle OpenChild(SafeFileHandle parent, string component, bool directory)
    {
        bool referenced = false;
        try
        {
            parent.DangerousAddRef(ref referenced);
            fixed (char* text = component)
            {
                var name = new UnicodeString
                {
                    Length = checked((ushort)(component.Length * 2)),
                    MaximumLength = checked((ushort)(component.Length * 2)), Buffer = (IntPtr)text
                };
                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes), RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = (IntPtr)(&name), Attributes = 0x1040 // CASE_INSENSITIVE | DONT_REPARSE, not INHERIT.
                };
                // FILE_OPEN, SYNCHRONOUS_IO_NONALERT, OPEN_REPARSE_POINT, OPEN_NO_RECALL.
                // Never CREATE or WRITE; opening metadata must not recall offline data.
                int status = NtCreateFile(out var handle, directory ? 0x00100081u : 0x80100000u,
                    ref attributes, out _, IntPtr.Zero, 0, directory ? 3u : 1u, 1, 0x00600020, IntPtr.Zero, 0);
                if (status < 0)
                {
                    handle?.Dispose();
                    if (unchecked((uint)status) is 0xC000050B or 0x8000002D)
                        Fail(SelectedFileSnapshotError.UnsafeFile);
                    FailOpen((int)RtlNtStatusToDosError(status));
                }
                return handle!;
            }
        }
        finally { if (referenced) parent.DangerousRelease(); }
    }

    private static void CheckOpen(SafeFileHandle handle)
    {
        if (!handle.IsInvalid) return;
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        FailOpen(error);
    }
    private static void FailOpen(int error) => Fail(error is 32 or 33 ? SelectedFileSnapshotError.InUse :
        error is 2 or 3 or 5 ? SelectedFileSnapshotError.Unavailable :
        error is 4390 or 4392 or 4393 or 1920 ? SelectedFileSnapshotError.UnsafeFile : SelectedFileSnapshotError.ReadFailed);

    private static FileIdentity ValidateDirectory(SafeFileHandle handle)
    {
        var info = Information(handle);
        if ((info.Info.FileAttributes & DirectoryAttribute) == 0 ||
            (info.Info.FileAttributes & (ReparseAttribute | DeviceAttribute | OfflineAttribute)) != 0 || GetFileType(handle) != 1)
            Fail(SelectedFileSnapshotError.UnsafeFile);
        return info.Identity;
    }
    private static void ValidateFile(SafeFileHandle handle, SnapshotInformation info)
    {
        if ((info.Info.FileAttributes & (DirectoryAttribute | ReparseAttribute | DeviceAttribute | OfflineAttribute)) != 0 ||
            info.Info.NumberOfLinks != 1 || GetFileType(handle) != 1)
            Fail(SelectedFileSnapshotError.UnsafeFile);
    }
    private static bool SameIdentity(FileIdentity before, FileIdentity after) =>
        before.VolumeSerialNumber == after.VolumeSerialNumber && before.Low == after.Low && before.High == after.High;
    private static ulong Length(FileInformation info) => ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
    private static SnapshotInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) Fail(SelectedFileSnapshotError.ReadFailed);
        if (!GetFileIdentity(handle, 18, out var identity, (uint)Marshal.SizeOf<FileIdentity>()))
            Fail(SelectedFileSnapshotError.ReadFailed);
        if (!GetFileBasicInformation(handle, 0, out var basic, (uint)Marshal.SizeOf<FileBasicInformation>()))
            Fail(SelectedFileSnapshotError.ReadFailed);
        return new SnapshotInformation(info, identity, basic);
    }
    private static void Fail(SelectedFileSnapshotError error) => throw new SelectedFileSnapshotException(error);

    private readonly record struct SnapshotInformation(FileInformation Info, FileIdentity Identity, FileBasicInformation Basic);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { internal ulong VolumeSerialNumber, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        internal uint FileAttributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory, ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { internal IntPtr Status, Information; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string rootPath);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder name, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdentity(SafeFileHandle handle, int kind, out FileIdentity information, uint length);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileBasicInformation(SafeFileHandle handle, int kind, out FileBasicInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition,
        uint options, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}

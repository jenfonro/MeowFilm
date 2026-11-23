using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

internal static class WindowsStorageFiles
{
    internal static string EnsureDirectory(string path)
    {
        string validated = ValidateRoot(path);
        Directory.CreateDirectory(validated);
        return validated;
    }

    internal static string EnsureParentDirectory(string validatedRoot)
    {
        string parent = Path.GetDirectoryName(validatedRoot)
            ?? throw new StorageException(StorageError.UnsafePath);
        Directory.CreateDirectory(parent);
        return ValidateRoot(parent);
    }

    internal static string ValidateRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) Fail(StorageError.InvalidRequest);
        try
        {
            if (!Path.IsPathFullyQualified(path)) Fail(StorageError.UnsafePath);
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (StorageException) { throw; }
        catch { Fail(StorageError.UnsafePath); return ""; }
    }

    internal static SafeFileHandle? OpenHandle(string path, FileMode mode, FileAccess access, FileShare share,
        bool allowMissing = false, bool sharingIsBusy = false, FileOptions options = FileOptions.None)
    {
        try
        {
            return File.OpenHandle(path, mode, access, share, options);
        }
        catch (Exception error) when (allowMissing && error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException error) when (sharingIsBusy && (error.HResult & 0xFFFF) is 32 or 33)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Fail(StorageError.Storage);
            return null;
        }
    }

    internal static long FileLength(SafeFileHandle handle) => RandomAccess.GetLength(handle);

    internal static void Fail(StorageError error) => throw new StorageException(error);
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Files;

internal sealed record SelectedFileSnapshotData(byte[] Bytes, string Basename);
internal enum SelectedFileSnapshotError
{
    InvalidPath, Unavailable, UnsafeFile, TooLarge, InUse, Changed, ReadFailed
}
internal sealed class SelectedFileSnapshotException(SelectedFileSnapshotError error) : Exception(error.ToString())
{
    internal SelectedFileSnapshotError Error { get; } = error;
}

internal static class SelectedFileSnapshot
{
    internal static async Task<SelectedFileSnapshotData> ReadAsync(string selectedPath, string extension,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(selectedPath))
            throw new SelectedFileSnapshotException(SelectedFileSnapshotError.InvalidPath);
        string fullPath;
        try { fullPath = Path.GetFullPath(selectedPath); }
        catch { throw new SelectedFileSnapshotException(SelectedFileSnapshotError.InvalidPath); }
        if (!Path.GetExtension(fullPath).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new SelectedFileSnapshotException(SelectedFileSnapshotError.InvalidPath);
        try
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            if (stream.Length > int.MaxValue)
                throw new SelectedFileSnapshotException(SelectedFileSnapshotError.TooLarge);
            var bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(offset), cancellation).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }
            if (offset != bytes.Length) Array.Resize(ref bytes, offset);
            return new SelectedFileSnapshotData(bytes, Path.GetFileName(fullPath));
        }
        catch (OperationCanceledException) { throw new OperationCanceledException(cancellation); }
        catch (SelectedFileSnapshotException) { throw; }
        catch (FileNotFoundException) { throw new SelectedFileSnapshotException(SelectedFileSnapshotError.Unavailable); }
        catch (DirectoryNotFoundException) { throw new SelectedFileSnapshotException(SelectedFileSnapshotError.Unavailable); }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
        { throw new SelectedFileSnapshotException(SelectedFileSnapshotError.InUse); }
        catch { throw new SelectedFileSnapshotException(SelectedFileSnapshotError.ReadFailed); }
    }
}

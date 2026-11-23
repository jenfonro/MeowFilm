using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Files;

internal sealed record SelectedFileSnapshotData(byte[] Bytes, string Basename);
internal enum SelectedFileSnapshotError
{
    InvalidPath, Unavailable, TooLarge, InUse, ReadFailed
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
        if (string.IsNullOrWhiteSpace(selectedPath) || !Path.IsPathFullyQualified(selectedPath))
            throw new SelectedFileSnapshotException(SelectedFileSnapshotError.InvalidPath);
        string path;
        try { path = Path.GetFullPath(selectedPath); }
        catch { throw new SelectedFileSnapshotException(SelectedFileSnapshotError.InvalidPath); }
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new SelectedFileSnapshotException(SelectedFileSnapshotError.InvalidPath);

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            if (stream.Length > int.MaxValue)
                throw new SelectedFileSnapshotException(SelectedFileSnapshotError.TooLarge);
            using var buffer = new MemoryStream(stream.Length > 0 && stream.Length <= int.MaxValue ? (int)stream.Length : 0);
            await stream.CopyToAsync(buffer, 64 * 1024, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return new SelectedFileSnapshotData(buffer.ToArray(), Path.GetFileName(path));
        }
        catch (OperationCanceledException) { throw new OperationCanceledException(cancellation); }
        catch (SelectedFileSnapshotException) { throw; }
        catch (OverflowException) { throw new SelectedFileSnapshotException(SelectedFileSnapshotError.TooLarge); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw new SelectedFileSnapshotException(SelectedFileSnapshotError.Unavailable);
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
        {
            throw new SelectedFileSnapshotException(SelectedFileSnapshotError.InUse);
        }
        catch
        {
            throw new SelectedFileSnapshotException(SelectedFileSnapshotError.ReadFailed);
        }
    }
}

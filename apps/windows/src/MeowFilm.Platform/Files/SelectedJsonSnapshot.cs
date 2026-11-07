using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Files;

/// <summary>
/// Caller-owned, exact source bytes; lowercase SHA-256; filename only, never the source path.
/// This is not JSON/schema validation and grants no permission to open files named in the JSON.
/// </summary>
public sealed record SelectedJsonSnapshotData(byte[] Bytes, string Sha256, string Basename);

public enum SelectedJsonSnapshotError
{
    InvalidPath, Unavailable, UnsafeFile, TooLarge, InUse, Changed, ReadFailed
}

/// <summary>Fixed diagnostics only; never contains a path, payload or inner exception.</summary>
public sealed class SelectedJsonSnapshotException : Exception
{
    public SelectedJsonSnapshotError Error { get; }
    public SelectedJsonSnapshotException(SelectedJsonSnapshotError error) : base(error.ToString()) => Error = error;
}

/// <summary>
/// Native UI supplies only an explicitly selected ordinary local absolute .json path.
/// No directory search, file mutation, fallback or Node path endpoint is provided.
/// Hardlinks, reparse points, UNC, device paths and mapped network drives are rejected.
/// </summary>
public static class SelectedJsonSnapshot
{
    public const int MaximumBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Exact bytes, including empty/invalid JSON; parsing belongs to the caller.
    /// Bounds: 8 MiB inclusive, 4096 UTF-16 path characters, 128 components, 255 per component.
    /// Work runs off the UI context; cancellation is checked between bounded synchronous OS
    /// calls, not an OS-call deadline. All pins/handles are released before the task settles.
    /// </summary>
    public static async Task<SelectedJsonSnapshotData> ReadAsync(string selectedPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await SelectedFileSnapshot.ReadAsync(selectedPath, ".json", MaximumBytes, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                string sha256 = Convert.ToHexStringLower(SHA256.HashData(data.Bytes));
                cancellationToken.ThrowIfCancellationRequested();
                return new SelectedJsonSnapshotData(data.Bytes, sha256, data.Basename);
            }
            catch (Exception error)
            {
                CryptographicOperations.ZeroMemory(data.Bytes);
                if (error is OperationCanceledException) throw;
                throw new SelectedJsonSnapshotException(SelectedJsonSnapshotError.ReadFailed);
            }
        }
        catch (SelectedFileSnapshotException error)
        {
            throw new SelectedJsonSnapshotException((SelectedJsonSnapshotError)error.Error);
        }
    }
}

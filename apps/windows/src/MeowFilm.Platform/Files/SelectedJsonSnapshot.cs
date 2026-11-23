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
    InvalidPath, Unavailable, TooLarge, InUse, ReadFailed
}

/// <summary>Fixed diagnostics only; never contains a path, payload or inner exception.</summary>
public sealed class SelectedJsonSnapshotException : Exception
{
    public SelectedJsonSnapshotError Error { get; }
    public SelectedJsonSnapshotException(SelectedJsonSnapshotError error) : base(error.ToString()) => Error = error;
}

/// <summary>
/// Native UI supplies only an explicitly selected local absolute .json path.
/// No directory search, file mutation, fallback or Node path endpoint is provided.
/// </summary>
public static class SelectedJsonSnapshot
{
    /// <summary>
    /// Exact bytes, including empty/invalid JSON; parsing belongs to the caller.
    /// Work runs off the UI context with cooperative cancellation.
    /// </summary>
    public static async Task<SelectedJsonSnapshotData> ReadAsync(string selectedPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await SelectedFileSnapshot.ReadAsync(selectedPath, ".json", cancellationToken)
                .ConfigureAwait(false);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(data.Bytes));
            cancellationToken.ThrowIfCancellationRequested();
            return new SelectedJsonSnapshotData(data.Bytes, sha256, data.Basename);
        }
        catch (SelectedFileSnapshotException error)
        {
            throw new SelectedJsonSnapshotException((SelectedJsonSnapshotError)error.Error);
        }
    }
}

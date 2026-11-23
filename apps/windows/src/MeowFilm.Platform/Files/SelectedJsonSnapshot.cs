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
/// Reads an explicitly selected .json file and computes its SHA-256 digest.
/// </summary>
public static class SelectedJsonSnapshot
{
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

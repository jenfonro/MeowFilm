using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Files;

/// <summary>Caller-owned original bytes, lowercase SHA-256 and filename only; no local path.</summary>
public sealed record SelectedScriptSnapshotData(byte[] Bytes, string Sha256, string Basename);

public enum SelectedScriptSnapshotError
{
    InvalidPath, Unavailable, UnsafeFile, TooLarge, InUse, Changed, ReadFailed
}

/// <summary>Fixed diagnostics only; never contains a path, payload or inner exception.</summary>
public sealed class SelectedScriptSnapshotException : Exception
{
    public SelectedScriptSnapshotError Error { get; }
    public SelectedScriptSnapshotException(SelectedScriptSnapshotError error) : base(error.ToString()) => Error = error;
}

/// <summary>
/// Snapshots a raw .js file explicitly selected by native UI, never a .jsc plugin.
/// Does not decode, parse, execute, install or authorize the script. Does not expose Node
/// filesystem access. Callers still own protocol validation and framed transport.
/// </summary>
public static class SelectedScriptSnapshot
{
    /// <summary>
    /// Same local-only safety and lifecycle policy as SelectedJsonSnapshot, without an
    /// application source-size gate. The owned byte[] remains subject to .NET allocation
    /// and available memory. Empty/invalid bytes are preserved, not marked valid LX.
    /// Cancellation is cooperative between bounded OS calls; no partial snapshot escapes.
    /// </summary>
    public static async Task<SelectedScriptSnapshotData> ReadAsync(string selectedPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await SelectedFileSnapshot.ReadAsync(selectedPath, ".js", Array.MaxLength, cancellationToken)
                .ConfigureAwait(false);
            return new SelectedScriptSnapshotData(data.Bytes, data.Sha256, data.Basename);
        }
        catch (SelectedFileSnapshotException error)
        {
            throw new SelectedScriptSnapshotException((SelectedScriptSnapshotError)error.Error);
        }
    }
}

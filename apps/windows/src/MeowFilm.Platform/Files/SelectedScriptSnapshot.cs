using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Files;

/// <summary>Caller-owned original bytes and filename only; no local path.</summary>
public sealed record SelectedScriptSnapshotData(byte[] Bytes, string Basename);

public enum SelectedScriptSnapshotError
{
    InvalidPath, Unavailable, TooLarge, InUse, ReadFailed
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
/// filesystem access. Callers still own protocol validation and complete HTTP upload.
/// </summary>
public static class SelectedScriptSnapshot
{
    /// <summary>
    /// Empty/invalid bytes are preserved, not marked valid LX.
    /// Work runs off the UI context with cooperative cancellation.
    /// </summary>
    public static async Task<SelectedScriptSnapshotData> ReadAsync(string selectedPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await SelectedFileSnapshot.ReadAsync(selectedPath, ".js", cancellationToken)
                .ConfigureAwait(false);
            return new SelectedScriptSnapshotData(data.Bytes, data.Basename);
        }
        catch (SelectedFileSnapshotException error)
        {
            throw new SelectedScriptSnapshotException((SelectedScriptSnapshotError)error.Error);
        }
    }
}

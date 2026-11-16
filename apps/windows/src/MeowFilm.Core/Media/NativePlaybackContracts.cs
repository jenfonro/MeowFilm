using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeowFilm.Media")]
[assembly: InternalsVisibleTo("MeowFilm.Audio")]
[assembly: InternalsVisibleTo("MeowFilm.Windows.Tests")]

namespace MeowFilm.Media;

public sealed record NativePlaybackScope(string OwnerId, string Identity, long Generation);

/// <summary>Optional port. Resolution and retry policy belong to the caller, never the media engine.</summary>
public interface INativePlaybackContinuation
{
    bool PauseRequested { get; }
    string? LastErrorCode { get; }
    NativePlaybackContinuation? CaptureContinuation();
    Task ReopenAsync(NativePlaybackContinuation continuation, Uri uri,
        IReadOnlyDictionary<string, string> headers, CancellationToken token = default);
    Task ReopenReadAsync(NativePlaybackContinuation continuation, INativeMediaReadLease lease, CancellationToken token = default);
}

/// <summary>A single-use, in-memory capability; stop, replacement and authorization cancellation revoke it.</summary>
public sealed class NativePlaybackContinuation
{
    internal NativePlaybackContinuation(NativePlaybackScope scope, double position, CancellationToken revoked)
    { Scope = scope; PositionSeconds = position; Revoked = revoked; }
    public NativePlaybackScope Scope { get; }
    public double PositionSeconds { get; }
    public CancellationToken Revoked { get; }
    internal bool Used;
}

/// <summary>
/// Inaudible source preparation beside the playing input, positioned at its live time.
/// Commit swaps them in one step; the old
/// input keeps playing until then, and a failed or abandoned preparation never touches it.
/// </summary>
public interface INativePlaybackHandoff
{
    // Retire rejected output without replacing the logical occurrence or its controls.
    Task StopOutputForHandoffAsync();
    Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token);
    Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token);
    double PreparedHandoffDurationSeconds { get; }
    // Invokes the non-throwing provenance callback synchronously at publication, before events/release.
    Task CommitHandoffAsync(Action committed, CancellationToken token);
    Task DiscardHandoffAsync();
}

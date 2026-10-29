using System.IO;

namespace MeowFilm.Media;

/// <summary>
/// An already-authorized, immutable, read-only, seekable representation, not a path.
/// OpenReadAsync takes exclusive ownership immediately, including rejected calls.
/// DisposeAsync must close the underlying handle and unblock reads, without requiring
/// the UI dispatcher. Do not separately dispose Content or reuse a transferred lease.
/// Revoked must signal authorization loss even while native playback is paused/cached.
/// </summary>
public interface INativeMediaReadLease : IAsyncDisposable
{
    Stream Content { get; }
    long Length { get; }
    CancellationToken Revoked { get; }
}

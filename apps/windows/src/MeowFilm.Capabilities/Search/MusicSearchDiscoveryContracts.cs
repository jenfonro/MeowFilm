// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace MeowFilm.Capabilities.Search;

public sealed record MusicSearchDiscoveryHot(string Keyword, double? Score);
// Songs contain only the existing native-normalized song shape, never raw platform responses or credentials.
public sealed record MusicSearchDiscoveryChart(string Id, string Name, string Cover, long? Count, string Creator,
    JsonElement[] Songs);
public sealed record MusicSearchDiscoveryFailure(string Stage, string? ChartId, string Code);
public sealed record MusicSearchDiscoverySnapshot(long Generation, MusicSearchDiscoveryHot[] Hot,
    string[] Suggestions, string[] Recommended, MusicSearchDiscoveryChart[] Charts,
    MusicSearchDiscoveryFailure[] Failures, long FetchedAt);

/// <summary>In-process, read-only WY discovery. This is not per-keystroke search suggestion or account verification.</summary>
public interface IMusicSearchDiscovery : IAsyncDisposable
{
    Task<MusicSearchDiscoverySnapshot> ReadAsync(bool refresh = false, CancellationToken cancellationToken = default);
    // Local six-keyword rotation only; expectedGeneration is the displayed snapshot, not an account generation.
    MusicSearchDiscoverySnapshot Rotate(long expectedGeneration);
    // The same account owner's WY SessionChanged must invoke this, including anonymous-to-account changes.
    void Invalidate(string? platform = null);
    // Retires pending work and awaits real session/HTTP cleanup; retains an already completed current cache.
    Task CancelAndDrainAsync();
}

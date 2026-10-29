// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Lyrics;

/// <summary>Exact native recording identity, never a title, URL, account or LX selection.</summary>
public sealed record NativeLyricRequest(string Platform, string RecordingId,
    double DurationSeconds = 0, string? AlbumAudioId = null);
public sealed record NativeLyricPayload(string Platform, string RecordingId,
    string Text, string Translation, string Romanization);

/// <summary>
/// Confirmed absence / no exact usable lyric returns null; protocol, authorization and identity failures are MusicPlatformException.
/// No cache or persistence. Caller owns account notifications, deduplication and result lifetime.
/// </summary>
public interface INativeMusicLyrics
{
    Task<NativeLyricPayload?> ReadAsync(NativeLyricRequest request, CancellationToken ct);
    void Invalidate(string? platform = null);
}

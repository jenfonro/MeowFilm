// SPDX-License-Identifier: GPL-3.0-only

namespace MeowFilm.Capabilities.Lyrics;

/// <summary>Exact native recording identity, never a title, URL, account or LX selection.</summary>
public sealed record NativeLyricRequest(string Platform, string RecordingId,
    double DurationSeconds = 0, string? AlbumAudioId = null);
public sealed record NativeLyricPayload(string Text, string Translation, string Romanization);

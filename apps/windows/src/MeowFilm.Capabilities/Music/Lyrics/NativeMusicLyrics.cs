// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Lyrics;

/// <summary>Adapts decoded Node lyrics. Rendering and local lyric ownership remain native.</summary>
public sealed class NativeMusicLyrics
{
    public const int ContractVersion = 1;
    private readonly MusicPlatformClient client;
    internal NativeMusicLyrics(MusicPlatformClient client) => this.client = client;
    public async Task<NativeLyricPayload?> ReadAsync(NativeLyricRequest request, CancellationToken ct)
    {
        var value = await client.ReadAsync(request.Platform, "lyrics", new { id = request.RecordingId, musicInfo = new
        { songmid = request.RecordingId, duration = request.DurationSeconds, albumAudioId = request.AlbumAudioId } }, ct).ConfigureAwait(false);
        string text = value.GetProperty("text").GetString()!;
        return text.Length == 0 ? null : new(text,
            value.GetProperty("translation").GetString()!, value.GetProperty("romanization").GetString()!);
    }
}

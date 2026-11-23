// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

internal sealed record OfficialAudioResult(Uri Uri, string RequestQuality, string ActualQuality,
    bool QualityVerified, double Duration, long Size, long? ExpiresAt, bool Full);

/// <summary>Adapts Node's media facts; native playback retains source and quality policy.</summary>
internal sealed class OfficialMusicAudio(MusicPlatformClient client)
{
    internal async Task<OfficialAudioResult> ResolveAsync(MusicPlatformSession session, Func<JsonElement> recording,
        IReadOnlyList<string> qualities, CancellationToken token, Action accountGuard)
    {
        var value = await client.RequestAsync(session.Platform, "audio", () =>
        {
            var data = recording();
            return MusicWireCodec.BuildObject(w =>
            {
                if (data.TryGetProperty("songmid", out var mid))
                {
                    w.WritePropertyName("id");
                    mid.WriteTo(w);
                }
                else w.WriteNull("id");
                w.WritePropertyName("musicInfo");
                data.WriteTo(w);
                MusicWireCodec.WriteStringArray(w, "qualities", qualities);
            });
        }, session, token, accountGuard).ConfigureAwait(false);
        var actual = value.GetProperty("actualQuality").GetString()!;
        return new(MediaUri(value.GetProperty("url").GetString()),
            value.GetProperty("requestQuality").GetString()!, actual,
            value.GetProperty("qualityVerified").GetBoolean(),
            value.GetProperty("duration").ValueKind == JsonValueKind.Null ? 0 : value.GetProperty("duration").GetDouble(),
            value.GetProperty("size").GetInt64(),
            value.GetProperty("expiresAt").ValueKind == JsonValueKind.Null ? null : value.GetProperty("expiresAt").GetInt64(),
            value.GetProperty("full").GetBoolean());
    }

    internal static Uri MediaUri(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0)
            throw new MusicPlatformException("INVALID_RESPONSE");
        return uri;
    }
}

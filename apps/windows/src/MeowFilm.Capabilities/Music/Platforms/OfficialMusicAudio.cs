// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

internal sealed record OfficialAudioResult(Uri Uri, string RequestQuality, string ActualQuality,
    bool QualityVerified, double Duration, long Size, long? ExpiresAt, bool Full);

/// <summary>Adapts Node's media facts; native playback retains source and quality policy.</summary>
internal sealed class OfficialMusicAudio(MusicPlatformClient client)
{
    internal async Task<OfficialAudioResult> ResolveAsync(MusicPlatformSession session, Func<JsonObject> recording,
        IReadOnlyList<string> qualities, CancellationToken token, Action accountGuard)
    {
        var value = await client.RequestAsync(session.Platform, "audio", () =>
        {
            var data = recording();
            return JsonSerializer.SerializeToElement(new { id = data["songmid"], musicInfo = data, qualities = qualities.ToArray() });
        }, session, token, accountGuard).ConfigureAwait(false);
        var actual = value.GetProperty("actualQuality").GetString()!;
        var url = value.GetProperty("url");
        return new(MediaUri(url.ValueKind == JsonValueKind.String ? url.GetString() : null),
            value.GetProperty("requestQuality").GetString()!, actual,
            value.GetProperty("qualityVerified").GetBoolean(),
            value.GetProperty("duration").ValueKind == JsonValueKind.Null ? 0 : value.GetProperty("duration").GetDouble(),
            value.GetProperty("size").GetInt64(), value.GetProperty("expiresAt").ValueKind == JsonValueKind.Null ? null : value.GetProperty("expiresAt").GetInt64(),
            value.GetProperty("full").GetBoolean());
    }
    internal static Uri MediaUri(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0)
            throw new MusicPlatformException("INVALID_RESPONSE");
        return uri;
    }
}

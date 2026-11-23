// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace MeowFilm.Capabilities.Platforms;

internal sealed class MusicVipPageProtocol(MusicPlatformClient client)
{
    internal async Task<MusicVipSignCardData?> ReadSignAsync(MusicPlatformSession session, CancellationToken token, Action accountGuard)
    {
        var value = await client.RequestAsync("wy", "vip.signCard", new { }, session, token, accountGuard).ConfigureAwait(false);
        if (value.ValueKind == JsonValueKind.Null) return null;
        var days = value.GetProperty("days").EnumerateArray().Select(day => new MusicVipSignDay(
            day.GetProperty("text").GetString()!, day.GetProperty("signed").GetBoolean(), day.GetProperty("cover").GetString()!,
            day.GetProperty("signedAt").GetInt64(), day.GetProperty("today").GetBoolean())).ToArray();
        return new(value.GetProperty("text").GetString()!, value.GetProperty("subText").GetString()!,
            value.GetProperty("redPrize").GetBoolean(), value.GetProperty("redDay").GetBoolean(), value.GetProperty("buttonText").GetString()!,
            value.GetProperty("background").GetString()!, days);
    }
    internal async Task<MusicVipBannerData?> ReadBannerAsync(MusicPlatformSession session, CancellationToken token, Action accountGuard)
    {
        var value = await client.RequestAsync("wy", "vip.banner", new { }, session, token, accountGuard).ConfigureAwait(false);
        return value.ValueKind == JsonValueKind.Null ? null : new(value.GetProperty("image").GetString()!,
            value.GetProperty("resourceType").GetString()!, value.GetProperty("resourceId").GetString()!);
    }
    internal Task<JsonElement> SignAsync(MusicPlatformSession session, CancellationToken token, Action accountGuard) =>
        client.RequestAsync("wy", "vip.checkIn", new { }, session, token, accountGuard);
}

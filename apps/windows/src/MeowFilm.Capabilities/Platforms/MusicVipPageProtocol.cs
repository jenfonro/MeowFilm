// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

internal sealed class MusicVipPageProtocol(MusicPlatformHttp http)
{
    private const int Maximum = 512 * 1024;

    internal async Task<MusicVipSignCardData?> ReadSignAsync(
        MusicPlatformSession session, CancellationToken token)
    {
        var request = MusicPlatformRequests.WyVipSignCard(session.Cookie);
        var response = await http.SendVipPageEapiAsync(request, token, Maximum).ConfigureAwait(false);
        var data = PlatformProtocols.WySuccess(response.Body);
        var background = Https(data["bgTexture"]);
        if (background.Length == 0) return null;
        if (data["signInfoList"] is not JsonArray rows || rows.Count > 31 ||
            !rows.All(row => row is JsonObject))
            throw new MusicPlatformException("INVALID_RESPONSE");
        var days = rows.Cast<JsonObject>().Select(row => new MusicVipSignDay(
            Text(row["dayText"], 64),
            PJ.Bool(row["sign"]),
            Https(row["songCoverUrl"]),
            row["signTime"] is null ? 0 : PJ.Count(row["signTime"]),
            PJ.Bool(row["today"]))).ToArray();
        return new(
            Text(data["text"], 256),
            Text(data["subText"], 512),
            OptionalFlag(data["redPrize"]),
            OptionalFlag(data["redDay"]),
            Text(data["btnText"], 64),
            background,
            Array.AsReadOnly(days));
    }

    internal async Task<MusicVipBannerData?> ReadBannerAsync(
        MusicPlatformSession session, CancellationToken token)
    {
        var request = MusicPlatformRequests.WyVipBanner(session.Cookie);
        var response = await http.SendVipPageEapiAsync(request, token, Maximum).ConfigureAwait(false);
        var body = PlatformProtocols.WySuccess(response.Body);
        if (body["data"] is not JsonObject data ||
            data["contentList"] is not JsonArray rows ||
            rows.Count > 100 || !rows.All(row => row is JsonValue value &&
                value.TryGetValue<string>(out var text) && text.Length <= 50000))
            throw new MusicPlatformException("INVALID_RESPONSE");
        foreach (var value in rows)
        {
            token.ThrowIfCancellationRequested();
            var text = value!.GetValue<string>();
            JsonObject item;
            try { item = PJ.Parse(Encoding.UTF8.GetBytes(text), 65536); }
            catch (MusicPlatformException) { continue; }
            if (item["algFlag"] is not null && PJ.Count(item["algFlag"]) == 1) continue;
            var image = Https(item["imgUrl"]);
            if (image.Length == 0) continue;
            var type = PJ.Text(item["resourceType"]);
            if (type is not ("SONG" or "PLAYLIST")) continue;
            string id;
            try { id = PJ.Id(item["resourceId"], @"^[1-9][0-9]{0,19}$"); }
            catch (MusicPlatformException) { continue; }
            return new(image, type, id);
        }
        return null;
    }

    internal async Task SignAsync(MusicPlatformSession session, CancellationToken token)
    {
        var request = MusicPlatformRequests.WyVipCheckIn(session.Cookie);
        var response = await http.SendVipPageWeapiAsync(request, token, 65536).ConfigureAwait(false);
        var body = PlatformProtocols.WySuccess(response.Body);
        PJ.Require(body["data"] switch
        {
            JsonObject or JsonArray => true,
            JsonValue value when value.TryGetValue<bool>(out var result) => result,
            JsonValue value when value.TryGetValue<int>(out var result) => result != 0,
            JsonValue value when value.TryGetValue<long>(out var result) => result != 0,
            _ => false
        }, "UPSTREAM_ERROR");
    }

    private static string Text(JsonNode? value, int maximum)
    {
        var text = PJ.Text(value);
        PJ.Require(text.Length <= maximum && !text.Any(char.IsControl), "INVALID_RESPONSE");
        return text;
    }

    private static bool OptionalFlag(JsonNode? value) => value is not null && PJ.Bool(value);

    private static string Https(JsonNode? value)
    {
        var text = PJ.Text(value);
        if (text.Length is 0 or > 4096 ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.IsLoopback || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return "";
        return uri.AbsoluteUri;
    }
}

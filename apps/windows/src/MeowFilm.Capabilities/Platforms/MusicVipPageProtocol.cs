// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

internal sealed class MusicVipPageProtocol(MusicPlatformHttp http, MusicPlatformClientContextOwner clients)
{
    private const int Maximum = 512 * 1024;

    internal Task<MusicVipSignCardData?> ReadSignAsync(
        MusicPlatformSession session, CancellationToken token, Action accountGuard) =>
        Read(session, c => MusicPlatformRequests.WyVipSignCard(session.Cookie, c),
            SignCard, token, Maximum, accountGuard);
    private static MusicVipSignCardData? SignCard(JsonObject data)
    {
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

    internal Task<MusicVipBannerData?> ReadBannerAsync(
        MusicPlatformSession session, CancellationToken token, Action accountGuard) =>
        Read(session, c => MusicPlatformRequests.WyVipBanner(session.Cookie, c),
            body => Banner(body, token), token, Maximum, accountGuard);
    private static MusicVipBannerData? Banner(JsonObject body, CancellationToken token)
    {
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
            try { item = PJ.Parse(Encoding.UTF8.GetBytes(text)); }
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

    internal Task SignAsync(MusicPlatformSession session, CancellationToken token, Action accountGuard) =>
        Read(session, c => MusicPlatformRequests.WyVipCheckIn(session.Cookie, c),
            Signed, token, 65536, accountGuard, weapi: true);
    private static bool Signed(JsonObject body)
    {
        PJ.Require(body["data"] switch
        {
            JsonObject or JsonArray => true,
            JsonValue value when value.TryGetValue<bool>(out var result) => result,
            JsonValue value when value.TryGetValue<int>(out var result) => result != 0,
            JsonValue value when value.TryGetValue<long>(out var result) => result != 0,
            _ => false
        }, "UPSTREAM_ERROR");
        return true;
    }

    private async Task<T> Read<T>(MusicPlatformSession session,
        Func<MusicWyClientContext, System.Net.Http.HttpRequestMessage> build, Func<JsonObject, T> decode, CancellationToken token,
        int maximum, Action accountGuard, bool weapi = false)
    {
        var client = await clients.AcquireWyAsync(token).ConfigureAwait(false);
        void Guard()
        {
            accountGuard();
            token.ThrowIfCancellationRequested();
            PJ.Require(clients.IsCurrent(client), clients.NeedsReconciliation ? "COMMIT_UNKNOWN" : "STORAGE_CONFLICT");
        }
        Guard();
        var response = weapi
            ? await http.SendVipPageWeapiAsync(build(client), token, maximum).ConfigureAwait(false)
            : await http.SendVipPageEapiAsync(build(client), token, maximum).ConfigureAwait(false);
        Guard();
        var body = PlatformProtocols.WySuccess(response.Body);
        var value = decode(body);
        client = await clients.AcceptWyAsync(client, session.Cookie, response.SetCookieHeaders, Guard, token).ConfigureAwait(false);
        Guard(); return value;
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

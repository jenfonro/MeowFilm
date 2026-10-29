// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

/// <summary>
/// Narrow, trusted native account/content protocol builders. No I/O and no authentication assertion.
/// Requests contain secrets: never log, serialize, cache or send them through Node/RPC.
/// Only MusicPlatformHttp's fixed-origin transport may send the result.
/// </summary>
public static class MusicPlatformRequests
{
    public static HttpRequestMessage WyWithCookie(string path, JsonObject parameters,
        string? cookieHeader, bool weapi = false)
        => Safe(() => PlatformProtocols.WyWithCookie(path, parameters, cookieHeader, weapi));
    internal static HttpRequestMessage WyVipSignCard(string cookieHeader) =>
        Safe(() => PlatformProtocols.WyVipEapi(
            "/api/vipnewcenter/app/minidesk/music/sign/pc",
            new() { ["type"] = 1 }, cookieHeader));
    internal static HttpRequestMessage WyVipBanner(string cookieHeader) =>
        Safe(() => PlatformProtocols.WyVipEapi(
            "/api/vipnewcenter/app/resource/common/list",
            new() { ["positionType"] = 16 }, cookieHeader));
    internal static HttpRequestMessage WyVipCheckIn(string cookieHeader) =>
        Safe(() => PlatformProtocols.WyWithCookie(
            "/api/vip-center-bff/task/sign", new(), cookieHeader, weapi: true));
    public static HttpRequestMessage TxWithCookie(string module, string method, JsonObject parameters,
        string? cookieHeader)
        => Safe(() => PlatformProtocols.TxWithCookie(module, method, parameters, cookieHeader));
    /// <summary>Only QQ QR's short-lived OAuth code; not an arbitrary comm/module override.</summary>
    public static HttpRequestMessage TxQrLogin(string authorizationCode)
        => Safe(() => PlatformProtocols.TxQrLogin(authorizationCode));
    public static HttpRequestMessage KgWithCookie(string path, JsonObject parameters,
        string? cookieHeader, string guestMid, string? router = null, JsonObject? body = null)
        => Safe(() => PlatformProtocols.KgWithCookie(path, parameters, cookieHeader, guestMid, router, body));
    // Internal account-only builders: no arbitrary URL, cookie, router or platform override.
    internal static HttpRequestMessage WySmsSend(string phone, string country) =>
        Safe(() => PlatformProtocols.WySms(phone, country, null));
    internal static HttpRequestMessage WySmsLogin(string phone, string country, string captcha) =>
        Safe(() => PlatformProtocols.WySms(phone, country, captcha));
    internal static HttpRequestMessage KgRegisterDevice(string mid, long time, string portrait, string encrypted) =>
        Safe(() => PlatformProtocols.KgAccount(MusicAccountHttpStep.KgRegister, mid, null, time,
            new() { ["part"] = "1", ["platid"] = "1", ["p"] = portrait }, encrypted));
    internal static HttpRequestMessage KgQrKey(string mid, string? dfid, long time) =>
        Safe(() => PlatformProtocols.KgAccount(MusicAccountHttpStep.KgQrKey, mid, dfid, time,
            new() { ["appid"] = "1001", ["type"] = "1", ["plat"] = "4",
                ["qrcode_txt"] = "https://h5.kugou.com/apps/loginQRCode/html/index.html?appid=1005&", ["srcappid"] = "2919" }));
    internal static HttpRequestMessage KgQrCheck(string mid, string? dfid, long time, string key, string dev) =>
        Safe(() => PlatformProtocols.KgAccount(MusicAccountHttpStep.KgQrCheck, mid, dfid, time,
            new() { ["plat"] = "4", ["srcappid"] = "2919", ["qrcode"] = key, ["dev"] = dev }));
    private static HttpRequestMessage Safe(Func<HttpRequestMessage> build)
    {
        try { return build(); }
        catch (MusicPlatformException) { throw; }
        catch { throw new MusicPlatformException("INVALID_REQUEST"); }
    }
}

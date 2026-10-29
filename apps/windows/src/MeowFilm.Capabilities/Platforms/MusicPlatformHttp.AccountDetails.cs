// SPDX-License-Identifier: GPL-3.0-only
// Account-only fixed TLS reads. Never delegates to PublicCatalog or widens the
// ordinary allowlist. Source pins/attributions: LICENSES/NOTICE.md.
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformHttp
{
    internal Task<MusicPlatformHttpResult> SendAccountDetailsAsync(string platform,
        HttpRequestMessage request, CancellationToken token, TimeSpan remaining)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { PJ.Require(remaining.TotalMilliseconds is >= 1 and <= 60000, "LIMIT"); }
        catch { request.Dispose(); throw; }
        return SendCoreAsync(platform, request, token, 1048576,
            TimeSpan.FromMilliseconds(Math.Min(20000, remaining.TotalMilliseconds)),
            (bytes, _, _) => new MusicPlatformHttpResult(PJ.Parse(bytes, 1048576), []),
            nativePolicy: NativeHttpPolicy.AccountDetails);
    }
    private static bool AccountDetailsOrigin(string platform, HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.OriginalString.Length > 65536 ||
            !NoHeader(request, "Authorization") || !NoHeader(request, "Proxy-Authorization")) return false;
        if (platform == "wy")
            return request.Method == HttpMethod.Post && uri.Host == "music.163.com" && uri.Query.Length == 0 &&
                uri.AbsolutePath is "/weapi/w/nuser/account/get" or "/weapi/music-vip-membership/client/vip/info" &&
                DetailsHeaders(request, ["User-Agent", "Referer", "Cookie"]) &&
                HeaderIs(request, "Referer", "https://music.163.com/") &&
                DetailsAgent(request, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36") &&
                request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Count() == 1 &&
                PJ.CookieHeader(cookies.Single()).GetValueOrDefault("MUSIC_U", "").Length > 0;
        if (platform == "tx")
            return request.Method == HttpMethod.Post && uri.Host == "u.y.qq.com" &&
                uri.AbsolutePath == "/cgi-bin/musicu.fcg" && uri.Query.Length == 0 &&
                DetailsHeaders(request, ["User-Agent"]) && DetailsAgent(request, "QQMusic 20090008(android 12)");
        if (platform != "kg" || !DetailsQuery(uri.Query, out var query)) return false;
        bool profile = uri.Host == "gateway.kugou.com" && uri.AbsolutePath == "/v3/get_my_info" &&
            request.Method == HttpMethod.Post && HeaderIs(request, "x-router", "usercenter.kugou.com");
        bool membership = uri.Host == "kugouvip.kugou.com" && uri.AbsolutePath == "/v1/get_union_vip" &&
            request.Method == HttpMethod.Get && request.Content is null && NoHeader(request, "x-router");
        if (!profile && !membership) return false;
        string variable = profile ? "plat" : "busi_type";
        string[] fields = ["dfid", "mid", "uuid", "appid", "clientver", "clienttime", "token", "userid", "signature", variable];
        return query.Count == fields.Length && fields.All(query.ContainsKey) &&
            query[variable] == (profile ? "1" : "concept") && query["uuid"] == "-" && query["appid"] == "1005" &&
            query["clientver"] == "20489" && DetailsDigits(query["clienttime"], 1, 12) &&
            DetailsDigits(query["userid"], 1, 20) && query["userid"][0] != '0' &&
            DetailsDevice(query["mid"]) && DetailsDevice(query["dfid"]) &&
            query["token"].Length is > 0 and <= 4096 && query["signature"].Length == 32 &&
            query["signature"].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            DetailsHeaders(request, profile
                ? ["User-Agent", "dfid", "mid", "clienttime", "kg-rc", "kg-thash", "kg-rec", "kg-rf", "x-router"]
                : ["User-Agent", "dfid", "mid", "clienttime", "kg-rc", "kg-thash", "kg-rec", "kg-rf"]) &&
            DetailsAgent(request, "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi") &&
            HeaderIs(request, "mid", query["mid"]) && HeaderIs(request, "dfid", query["dfid"]) &&
            HeaderIs(request, "clienttime", query["clienttime"]) && HeaderIs(request, "kg-rc", "1") &&
            HeaderIs(request, "kg-thash", "5d816a0") && HeaderIs(request, "kg-rec", "1") &&
            HeaderIs(request, "kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F");
    }
    private static bool DetailsDigits(string value, int min, int max) =>
        value.Length >= min && value.Length <= max && value.All(char.IsAsciiDigit);
    private static bool DetailsDevice(string value) => value.Length is > 0 and <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool DetailsAgent(HttpRequestMessage request, string value) =>
        request.Headers.TryGetValues("User-Agent", out var values) && string.Join(" ", values) == value;
    private static bool DetailsHeaders(HttpRequestMessage request, string[] keys) =>
        request.Headers.Count() == keys.Length &&
        request.Headers.All(h => keys.Contains(h.Key, StringComparer.OrdinalIgnoreCase)) &&
        (request.Content is null || request.Content.Headers.All(h =>
            h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
            h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)));
    private static bool DetailsQuery(string input, out Dictionary<string, string> query)
    {
        query = new(StringComparer.Ordinal);
        if (input.Length is < 2 or > 65536 || input[0] != '?') return false;
        foreach (var part in input[1..].Split('&'))
        {
            int at = part.IndexOf('=');
            if (at < 1 || !part[..at].All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
            string decoded;
            // Every reference builder percent-encodes the complete value. Reject
            // malformed encodings rather than normalizing signed credential query.
            for (int i = at + 1; i < part.Length; i++)
                if (part[i] == '%')
                {
                    if (i + 2 >= part.Length || !byte.TryParse(part.AsSpan(i + 1, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out _)) return false;
                    i += 2;
                }
            decoded = Uri.UnescapeDataString(part[(at + 1)..].Replace("+", " ", StringComparison.Ordinal));
            if (!decoded.All(c => c is >= '!' and <= '~') || !query.TryAdd(part[..at], decoded) || query.Count > 12) return false;
        }
        return true;
    }
    private static bool DetailsFields(JsonObject value, params string[] keys) =>
        value.Count == keys.Length && keys.All(value.ContainsKey);
    private static async Task CheckAccountDetailsBody(HttpRequestMessage request, CancellationToken token)
    {
        var content = request.Content;
        byte[] bytes = [];
        try
        {
            if (request.Method == HttpMethod.Post)
            {
                PJ.Require(content is not null, "INVALID_REQUEST");
                try { await content!.LoadIntoBufferAsync(65536, token).ConfigureAwait(false); }
                catch (HttpRequestException) { throw new MusicPlatformException("LIMIT"); }
                bytes = await content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                PJ.Require(bytes.Length is > 0 and <= 65536, "LIMIT");
            }
            token.ThrowIfCancellationRequested();
            PJ.Require(ReferenceEquals(content, request.Content), "ORIGIN_DENIED");
            string host = request.RequestUri!.Host;
            if (host == "music.163.com")
            {
                PJ.Require(content!.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded", "INVALID_REQUEST");
                var text = new UTF8Encoding(false, true).GetString(bytes);
                PJ.Require(DetailsQuery("?" + text, out var form) && form.Count == 2 &&
                    form.ContainsKey("params") && form.TryGetValue("encSecKey", out var key) &&
                    key.Length == 256 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "INVALID_REQUEST");
                return;
            }
            if (host == "u.y.qq.com")
            {
                PJ.Require(content!.Headers.ContentType?.MediaType == "application/json", "INVALID_REQUEST");
                var body = PJ.Parse(bytes, 65536);
                PJ.Require(DetailsFields(body, "comm", "req_0") && body["comm"] is JsonObject && body["req_0"] is JsonObject, "INVALID_REQUEST");
                var comm = body["comm"]!.AsObject(); var req = body["req_0"]!.AsObject();
                PJ.Require(DetailsFields(comm, "ct", "cv", "v", "chid", "tmeAppID", "qq", "authst", "tmeLoginType") &&
                    PJ.Text(comm["ct"]) == "11" && PJ.Text(comm["cv"]) == "20090008" &&
                    PJ.Text(comm["v"]) == "20090008" && PJ.Text(comm["chid"]) == "10003505" &&
                    PJ.Text(comm["tmeAppID"]) == "qqmusic" && DetailsDigits(PJ.Text(comm["qq"]), 1, 20) &&
                    PJ.Text(comm["qq"])[0] != '0' &&
                    DetailsDigits(PJ.Text(comm["tmeLoginType"]), 1, 2) && PJ.Text(comm["tmeLoginType"])[0] != '0' &&
                    PJ.Text(comm["authst"]).Length is > 0 and <= 8192, "INVALID_REQUEST");
                PJ.Require(DetailsFields(req, "module", "method", "param") && req["param"] is JsonObject, "INVALID_REQUEST");
                var param = req["param"]!.AsObject();
                bool home = PJ.Text(req["module"]) == "music.UnifiedHomepage.UnifiedHomepageSrv" && PJ.Text(req["method"]) == "GetHomepageHeader";
                PJ.Require(home ? DetailsFields(param, "uin", "IsQueryTabDetail") && PJ.Scalar(param["IsQueryTabDetail"]) == "1" &&
                    PJ.Text(param["uin"]).Length is > 0 and <= 256 :
                    param.Count == 0 && (PJ.Text(req["module"]) == "music.UserInfo.userInfoServer" && PJ.Text(req["method"]) == "GetLoginUserInfo" ||
                    PJ.Text(req["module"]) == "VipLogin.VipLoginInter" && PJ.Text(req["method"]) == "vip_login_base"), "ORIGIN_DENIED");
                return;
            }
            PJ.Require(DetailsQuery(request.RequestUri.Query, out var query), "INVALID_REQUEST");
            string payload = bytes.Length == 0 ? "" : new UTF8Encoding(false, true).GetString(bytes);
            PJ.Require(query.TryGetValue("signature", out var signature), "INVALID_REQUEST");
            query.Remove("signature");
            string expected = PlatformProtocols.Md5(PlatformProtocols.KgSalt +
                string.Concat(query.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)) + payload + PlatformProtocols.KgSalt);
            PJ.Require(signature == expected, "INVALID_REQUEST");
            if (content is not null)
            {
                PJ.Require(content.Headers.ContentType?.MediaType == "application/json", "INVALID_REQUEST");
                var body = PJ.Parse(bytes, 65536);
                PJ.Require(DetailsFields(body, "visit_time", "usertype", "p", "userid") &&
                    PJ.Scalar(body["usertype"]) == "1" && PJ.Scalar(body["userid"]) == query["userid"] &&
                    DetailsDigits(PJ.Scalar(body["visit_time"]), 1, 12) && PJ.Text(body["p"]).Length == 256 &&
                    PJ.Text(body["p"]).All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'), "INVALID_REQUEST");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (!ReferenceEquals(content, request.Content)) content?.Dispose();
        }
    }
}

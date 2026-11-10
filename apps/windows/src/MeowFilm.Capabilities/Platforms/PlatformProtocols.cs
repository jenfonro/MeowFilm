// SPDX-License-Identifier: GPL-3.0-only
// Small native translations of fixed MIT Netease/KuGou protocol recipes.
// These are public protocol parameters, not user credentials. See LICENSES/NOTICE.md.
using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

internal static class PlatformProtocols
{
    private const string WyPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB
        -----END PUBLIC KEY-----
        """;
    internal const string KgPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDIAG7QOELSYoIJvTFJhMpe1s/gbjDJX51HBNnEl5HXqTW6lQ7LC8jr9fWZTwusknp+sVGzwd40MwP6U5yDE27M/X1+UR4tvOGOqp94TJtQ1EPnWGWXngpeIW5GxoQGao1rmYWAu6oi1z9XkChrsUdC6DJE5E221wf/4WLFxwAtRQIDAQAB
        -----END PUBLIC KEY-----
        """;
    internal const string KgSalt = "OIlwieks28dk2k092lksi2UIkp";
    internal static string Md5(string value) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Form(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key).Replace("%20", "+", StringComparison.Ordinal)
            + "=" + Uri.EscapeDataString(p.Value).Replace("%20", "+", StringComparison.Ordinal)));
    private static byte[] Aes(string text, string key, bool cbc)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(key);
        return cbc ? aes.EncryptCbc(Encoding.UTF8.GetBytes(text), "0102030405060708"u8, PaddingMode.PKCS7)
            : aes.EncryptEcb(Encoding.UTF8.GetBytes(text), PaddingMode.PKCS7);
    }
    internal static HttpRequestMessage Wy(string path, JsonObject parameters, MusicPlatformSession? session,
        MusicWyClientContext client, bool weapi = false)
        => WyWithCookie(path, parameters, session?.Cookie, client, weapi);
    // api-enhanced captcha_sent/login_cellphone at the pinned MIT source.
    // Never inherit a stored session, environment cookie, password or domain override.
    internal static HttpRequestMessage WySms(string phone, string country, string? captcha, MusicWyClientContext client) =>
        captcha is null
        ? WyWithCookie("/api/sms/captcha/sent", new() {
            ["ctcode"] = country, ["secrete"] = "music_middleuser_pclogin", ["cellphone"] = phone
        }, null, client, weapi: true)
        : WyWithCookie("/api/w/login/cellphone", new() {
            ["type"] = "1", ["https"] = "true", ["phone"] = phone, ["countrycode"] = country,
            ["captcha"] = captcha, ["remember"] = "true", ["secureCaptcha"] = ""
        }, null, client, weapi: true);
    // Raw-cookie overloads are native account-validation seams, NOT public catalog/UI inputs.
    internal static HttpRequestMessage WyWithCookie(string path, JsonObject parameters, string? cookieHeader,
        MusicWyClientContext client, bool weapi = false, string? antiCheatToken = null)
    {
        PJ.Require(path.StartsWith("/api/", StringComparison.Ordinal));
        if (antiCheatToken is not null)
            PJ.Require(!weapi && (path is "/api/playlist/subscribe" or "/api/playlist/unsubscribe") &&
                MusicNeteaseTokenProvider.ValidToken(antiCheatToken), "INVALID_REQUEST");
        var cookies = MusicWyRequestContext.Cookies(client, cookieHeader);
        var data = (JsonObject)parameters.DeepClone();
        var csrf = PJ.Cookie(cookies, "__csrf");
        data["e_r"] = false;
        string body, cookie, agent, origin;
        if (weapi)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var key = string.Concat(Enumerable.Range(0, 16).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
            data["csrf_token"] = csrf;
            var inner = Convert.ToBase64String(Aes(PJ.Encode(data), "0CoJUm6Qyw8W8jud", true));
            var encrypted = Convert.ToBase64String(Aes(inner, key, true));
            // RSA_NO_PADDING is the fixed WEAPI wire protocol. Only the public exponent is used.
            using var rsa = RSA.Create();
            rsa.ImportFromPem(WyPublicKey);
            var pub = rsa.ExportParameters(false);
            var message = new BigInteger(Encoding.ASCII.GetBytes(new string(key.Reverse().ToArray())), true, true);
            var secured = BigInteger.ModPow(message, new BigInteger(pub.Exponent!, true, true), new BigInteger(pub.Modulus!, true, true));
            var hex = Convert.ToHexStringLower(secured.ToByteArray(true, true)).PadLeft(256, '0');
            body = Form(new Dictionary<string, string> { ["params"] = encrypted, ["encSecKey"] = hex });
            cookie = MusicWyRequestContext.Header(cookies);
            agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36";
            origin = "https://music.163.com";
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            var header = PJ.Obj(("os", "pc"), ("appver", "3.1.17.204416"),
                ("osver", "Microsoft-Windows-10-Professional-build-19045-64bit"), ("channel", "netease"),
                ("versioncode", "140"), ("__csrf", csrf), ("resolution", "1920x1080"),
                ("buildver", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
                ("requestId", now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" +
                    RandomNumberGenerator.GetInt32(1000).ToString("000", CultureInfo.InvariantCulture)));
            foreach (var name in new[] { "MUSIC_U", "MUSIC_A", "NMTID", "deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID" })
                if (cookies.TryGetValue(name, out var value)) header[name] = value;
            if (antiCheatToken is not null) header["X-antiCheatToken"] = antiCheatToken;
            data["header"] = header.DeepClone();
            var payload = PJ.Encode(data);
            var digest = Md5($"nobody{path}use{payload}md5forencrypt");
            body = Form(new Dictionary<string, string> {
                ["params"] = Convert.ToHexString(Aes($"{path}-36cd479b6b5-{payload}-36cd479b6b5-{digest}", "e82ckenh8dichen8", false)) });
            cookie = string.Join("; ", header.Select(p => p.Key + "=" + p.Value!.GetValue<string>()));
            agent = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.1.29.205117";
            origin = "https://interfacepc.music.163.com";
        }
        var request = new HttpRequestMessage(HttpMethod.Post, origin + (weapi ? "/weapi/" : "/eapi/") + path[5..]) {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("User-Agent", agent);
        return request;
    }
    internal static HttpRequestMessage WyVipEapi(string path, JsonObject parameters, string cookieHeader, MusicWyClientContext client)
    {
        PJ.Require(path is "/api/vipnewcenter/app/minidesk/music/sign/pc" or
            "/api/vipnewcenter/app/resource/common/list");
        var cookies = MusicWyRequestContext.Cookies(client, cookieHeader);
        PJ.Require(PJ.Cookie(cookies, "MUSIC_U").Length > 0, "INVALID_CREDENTIALS");
        var data = (JsonObject)parameters.DeepClone();
        data["e_r"] = true;
        var now = DateTimeOffset.UtcNow;
        var header = PJ.Obj(("os", "pc"), ("appver", "3.1.42.205594"),
            ("osver", "Microsoft-Windows-10-Professional-build-19045-64bit"), ("channel", "netease"),
            ("versioncode", "140"), ("__csrf", PJ.Cookie(cookies, "__csrf")), ("resolution", "1920x1080"),
            ("buildver", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ("requestId", now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" +
                RandomNumberGenerator.GetInt32(1000).ToString("000", CultureInfo.InvariantCulture)));
        foreach (var name in new[] { "MUSIC_U", "MUSIC_A", "NMTID", "deviceId", "_ntes_nuid", "_ntes_nnid", "WNMCID" })
            if (cookies.TryGetValue(name, out var value)) header[name] = value;
        data["header"] = header.DeepClone();
        var payload = PJ.Encode(data);
        var digest = Md5($"nobody{path}use{payload}md5forencrypt");
        var body = Form(new Dictionary<string, string> {
            ["params"] = Convert.ToHexString(Aes(
                $"{path}-36cd479b6b5-{payload}-36cd479b6b5-{digest}",
                "e82ckenh8dichen8", false)) });
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://interface.music.163.com/eapi/" + path[5..]) {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
        request.Headers.TryAddWithoutValidation("Cookie",
            string.Join("; ", header.Select(pair => pair.Key + "=" + pair.Value!.GetValue<string>())));
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.1.42.205594");
        return request;
    }
    internal static JsonObject WySuccess(JsonObject data)
    {
        var code = PJ.Scalar(data["code"]);
        var error = code switch {
            "301" or "401" => "AUTH_REQUIRED", "403" => "FORBIDDEN", "405" or "429" or "509" => "RATE_LIMITED",
            "250" or "460" or "461" => "VERIFICATION_REQUIRED", "200" => null, _ => "UPSTREAM_ERROR" };
        if (error is not null) throw new MusicPlatformException(error);
        return data;
    }
    private static JsonObject TxComm() =>
        PJ.Obj(("ct", "11"), ("cv", "20090008"), ("v", "20090008"), ("chid", "10003505"), ("tmeAppID", "qqmusic"));
    internal static JsonObject TxAndroidComm(MusicQqClientContext device, DateTimeOffset now, bool includeSession)
    {
        PJ.Require(includeSession ? device.SessionCurrent(now) : device.QimeiCurrent(now), "DEVICE_CONTEXT_REQUIRED");
        var comm = TxComm();
        comm["traceid"] = "10002_" + device.OpenUdid + "_" + now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        comm["QIMEI36"] = device.Qimei!.Q36; comm["OpenUDID"] = device.OpenUdid; comm["udid"] = device.OpenUdid;
        comm["OpenUDID2"] = device.OpenUdid2; comm["aid"] = device.AndroidId;
        comm["os_ver"] = "15"; comm["phonetype"] = "V2408A";
        if (includeSession) { comm["uid"] = device.Session!.Uid; comm["sid"] = device.Session.Sid; }
        return comm;
    }
    internal static HttpRequestMessage Tx(string module, string method, JsonObject param, MusicPlatformSession? session,
        MusicQqClientContext? client = null, DateTimeOffset? now = null)
    {
        if (session is not null) PJ.Require(PJ.Cookie(PJ.Cookies(session), "musicid") == session.UserId, "INVALID_CREDENTIALS");
        return TxRequest(module, method, param, session?.Cookie, false, client, now);
    }
    internal static HttpRequestMessage TxWithCookie(string module, string method, JsonObject param, string? cookieHeader)
        => TxRequest(module, method, param, cookieHeader, false);
    internal static HttpRequestMessage TxQrLogin(string code)
    {
        PJ.Require(code.Length is > 0 and <= 512 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
        return TxRequest("QQConnectLogin.LoginServer", "QQLogin", PJ.Obj(("code", code)), null, true);
    }
    private static HttpRequestMessage TxRequest(string module, string method, JsonObject param, string? cookieHeader, bool qrLogin,
        MusicQqClientContext? client = null, DateTimeOffset? now = null)
    {
        var at = client is null ? default : now ?? DateTimeOffset.UtcNow;
        var comm = client is null ? TxComm() : TxAndroidComm(client, at, includeSession: true);
        if (qrLogin) comm["tmeLoginType"] = 2;
        if (cookieHeader is not null)
        {
            var cookies = PJ.CookieHeader(cookieHeader);
            var id = PJ.Cookie(cookies, "musicid"); var key = PJ.Cookie(cookies, "musickey");
            PJ.Require(id.Length > 0 && key.Length > 0, "INVALID_CREDENTIALS");
            PJ.Id(JsonValue.Create(id), "^[1-9][0-9]{0,19}$", "INVALID_CREDENTIALS");
            comm["qq"] = id; comm["authst"] = key;
            comm["tmeLoginType"] = PJ.Cookie(cookies, "login_type", PJ.Cookie(cookies, "loginType", key.StartsWith("W_X", StringComparison.Ordinal) ? "1" : "2"));
            if (client is not null)
                comm["traceid"] = "10002_" + id + "_" + at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }
        // Pin defaults sign=false. A signature-required result remains an error, not a retry route.
        var body = PJ.Obj(("comm", comm), ("req_0", PJ.Obj(("module", module), ("method", method), ("param", param))));
        var request = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg") {
            Content = new StringContent(PJ.Encode(body), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", client is null ? "QQMusic 20090008(android 12)" : "QQMusic 20090008(android 15)");
        return request;
    }
    internal static JsonObject TxSuccess(JsonObject data)
    {
        foreach (var node in new[] { data["code"], PJ.At(data, "req_0", "code") })
        {
            var code = PJ.Scalar(node);
            var error = code switch { "0" => null, "1000" or "104400" or "104401" => "AUTH_REQUIRED",
                "2000" => "SIGNATURE_REQUIRED", "2001" => "RATE_LIMITED", _ => "UPSTREAM_ERROR" };
            if (error is not null) throw new MusicPlatformException(error);
        }
        PJ.Require(PJ.At(data, "req_0", "data") is JsonObject, "INVALID_RESPONSE");
        return (JsonObject)PJ.At(data, "req_0", "data")!;
    }
    internal static HttpRequestMessage Kg(string path, JsonObject parameters, MusicPlatformSession? session,
        MusicKgClientContext client, string? router = null, JsonObject? body = null)
    {
        if (session is not null) PJ.Require(PJ.Cookie(PJ.Cookies(session), "userid") == session.UserId, "INVALID_CREDENTIALS");
        return KgWithCookie(path, parameters, session?.Cookie, client, router, body);
    }
    internal static HttpRequestMessage KgWithCookie(string path, JsonObject parameters, string? cookieHeader,
        MusicKgClientContext client, string? router = null, JsonObject? body = null)
    {
        var cookies = PJ.CookieHeader(cookieHeader);
        var (mid, dfid) = KgIdentity(cookies, client);
        if (cookieHeader is not null)
        {
            PJ.Require(PJ.Cookie(cookies, "userid").Length > 0 && PJ.Cookie(cookies, "token").Length > 0, "INVALID_CREDENTIALS");
            PJ.Id(JsonValue.Create(PJ.Cookie(cookies, "userid")), "^[1-9][0-9]{0,19}$", "INVALID_CREDENTIALS");
        }
        void CheckIdentity(JsonObject fields) => PJ.Require(
            (!fields.TryGetPropertyValue("mid", out var suppliedMid) || PJ.Scalar(suppliedMid) == mid) &&
            (!fields.TryGetPropertyValue("dfid", out var suppliedDfid) || PJ.Scalar(suppliedDfid) == dfid), "INVALID_REQUEST");
        CheckIdentity(parameters); if (body is not null) CheckIdentity(body);
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var query = PJ.Obj(("dfid", dfid), ("mid", mid), ("uuid", "-"), ("appid", 1005), ("clientver", 20489), ("clienttime", time));
        foreach (var p in parameters) query[p.Key] = p.Value?.DeepClone();
        if (cookies.TryGetValue("token", out var token)) query["token"] = token;
        if (cookies.TryGetValue("userid", out var userId) && userId != "0") query["userid"] = userId;
        var text = body is null ? "" : PJ.Encode(body);
        query["signature"] = Md5(KgSalt + string.Concat(query.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + PJ.Scalar(p.Value))) + text + KgSalt);
        var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            "https://gateway.kugou.com" + path + "?" + Form(query.ToDictionary(p => p.Key, p => PJ.Scalar(p.Value))));
        if (body is not null) request.Content = new StringContent(text, Encoding.UTF8, "application/json");
        foreach (var (key, value) in new[] { ("User-Agent", "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi"),
            ("dfid", dfid), ("mid", mid), ("clienttime", time.ToString(CultureInfo.InvariantCulture)), ("kg-rc", "1"),
            ("kg-thash", "5d816a0"), ("kg-rec", "1"), ("kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F") })
            request.Headers.TryAddWithoutValidation(key, value);
        if (router is not null) request.Headers.TryAddWithoutValidation("x-router", router);
        return request;
    }
    internal static (string Mid, string Dfid) KgIdentity(Dictionary<string, string> cookies, MusicKgClientContext client)
    {
        string localMid = client.Mid;
        bool associated = cookies.TryGetValue("KUGOU_API_MID", out var accountMid);
        string mid = associated ? accountMid! : localMid;
        PJ.Require(mid.Length is > 0 and <= 39 && mid.All(char.IsAsciiDigit), "DEVICE_CONTEXT_REQUIRED");
        string accountDfid = PJ.Cookie(cookies, "dfid", "-");
        PJ.Require(accountDfid == "-" || accountDfid.Length is >= 8 and <= 128 &&
            accountDfid.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "DEVICE_CONTEXT_REQUIRED");
        string dfid = mid == localMid ? client.Dfid ?? (associated ? accountDfid : "-") : accountDfid;
        return (mid, dfid);
    }
    internal static JsonObject KgSuccess(JsonObject data, string path)
    {
        var codes = new[] { "error_code", "errcode", "err_code" }.Select(k => data[k]).Where(v => v is not null).Select(PJ.Scalar).ToArray();
        if (codes.Contains("20018")) throw new MusicPlatformException("AUTH_REQUIRED");
        if (codes.Contains("20028") || data["ssaCode"] is not null || data["ssa-code"] is not null)
            throw new MusicPlatformException("VERIFICATION_REQUIRED");
        PJ.Require((PJ.Scalar(data["status"]) == "1" || path == "/v1/audio/audio" && data["status"] is null && codes.Length > 0) &&
            codes.All(c => c == "0") && (data["error"] is null || PJ.Scalar(data["error"]) is "false" or "0" or "\"\""), "UPSTREAM_ERROR");
        return data;
    }
    internal static void KgAccountStatus(JsonObject data)
    {
        var codes = new[] { "error_code", "errcode", "err_code" }.Where(k => data[k] is not null)
            .Select(k => PJ.Scalar(data[k])).ToArray();
        if (codes.Contains("20018")) throw new MusicPlatformException("AUTH_REQUIRED");
        static bool Truth(JsonNode? node) => node is not null &&
            node.ToJsonString() is not ("null" or "false" or "0" or "\"\"");
        if (codes.Contains("20028") || Truth(data["ssaCode"]) || Truth(data["ssa-code"]))
            throw new MusicPlatformException("VERIFICATION_REQUIRED");
        PJ.Require(data["status"] is JsonValue status && !status.TryGetValue<string>(out _), "INVALID_RESPONSE");
        using var json = JsonDocument.Parse(data["status"]!.ToJsonString());
        PJ.Require(json.RootElement.ValueKind == JsonValueKind.Number && json.RootElement.TryGetDecimal(out var number) &&
            number is >= int.MinValue and <= int.MaxValue && decimal.Truncate(number) == number, "INVALID_RESPONSE");
        PJ.Require(json.RootElement.GetDecimal() == 1 && !Truth(data["error"]) && codes.All(c => c == "0"), "UPSTREAM_ERROR");
    }
    // KuGouMusicApi 514f870d util/request.js + helper.js; anonymous auth steps only.
    // Do not add these origins to the ordinary content/lyrics transport.
    internal static HttpRequestMessage KgAccount(MusicAccountHttpStep step, string mid, string? dfid, long time,
        Dictionary<string, string> fields, string? body = null)
    {
        PJ.Require(mid.Length is > 0 and <= 39 && mid.All(char.IsAsciiDigit) && time >= 0);
        PJ.Require(dfid is null || dfid.Length is >= 8 and <= 128 &&
            dfid.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
        var (origin, path) = step switch
        {
            MusicAccountHttpStep.KgRegister => ("https://userservice.kugou.com", "/risk/v2/r_register_dev"),
            MusicAccountHttpStep.KgQrKey => ("https://login-user.kugou.com", "/v2/qrcode"),
            MusicAccountHttpStep.KgQrCheck => ("https://login-user.kugou.com", "/v2/get_userinfo_qrcode"),
            _ => throw new MusicPlatformException("ORIGIN_DENIED")
        };
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dfid"] = dfid ?? "-", ["mid"] = mid, ["uuid"] = "-", ["appid"] = "1005",
            ["clientver"] = "20489", ["clienttime"] = time.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var field in fields) query[field.Key] = field.Value;
        string salt = step == MusicAccountHttpStep.KgRegister ? KgSalt : "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt";
        query["signature"] = Md5(salt + string.Concat(query.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value)) + (body ?? "") + salt);
        var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, origin + path + "?" + Form(query));
        // The source passes a base64 string to axios, not decoded cipher bytes.
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        foreach (var (key, value) in new[] { ("User-Agent", "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi"),
            ("dfid", dfid ?? "-"), ("mid", mid), ("clienttime", query["clienttime"]), ("kg-rc", "1"),
            ("kg-thash", "5d816a0"), ("kg-rec", "1"), ("kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F") })
            request.Headers.TryAddWithoutValidation(key, value);
        return request;
    }
}

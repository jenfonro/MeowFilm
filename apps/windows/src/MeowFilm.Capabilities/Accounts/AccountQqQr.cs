// SPDX-License-Identifier: GPL-3.0-only
// QQMusicApi 27861e51432ea6b6e88dc35c4e8c8e239c0339e8 modules/login.py.
// Native fixed client recipe, NOT the upstream web server/credential pool. See LICENSES/NOTICE.md.
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountProtocol
{
    private const string QqJump = "https://graph.qq.com/oauth2.0/login_jump";
    private const string QqRedirect = "https://y.qq.com/portal/wx_redirect.html?login_type=1&surl=https://y.qq.com/";
    private static string QqAtom(string value, int max = 2048)
    {
        AccountRules.Require(value.Length > 0 && value.Length <= max &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), "INVALID_RESPONSE");
        return value;
    }
    private static int Hash33(string value, uint seed = 0)
    {
        foreach (char c in value) seed = unchecked(seed * 33 + c);
        return (int)(seed & 0x7fffffff);
    }
    private static HttpRequestMessage QqGet(string host, string path, Dictionary<string, string> query, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://" + host + path + "?" +
            string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
        request.Headers.Referrer = new("https://xui.ptlogin2.qq.com/");
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }
    private Task<MusicAccountChallenge> BeginTx(CancellationToken token) => Run("tx", token, async ctx =>
    {
        lock (gate)
        {
            var state = states["tx"];
            AccountRules.Require(state.LastQrCreation is not { } last || clock.GetElapsedTime(last) >= TimeSpan.FromSeconds(3), "RATE_LIMITED");
            state.LastQrCreation = clock.GetTimestamp();
        }
        var response = await ctx.SendAccount(MusicAccountHttpStep.QqImage, QqGet("ssl.ptlogin2.qq.com", "/ptqrshow", new()
        {
            ["appid"] = "716027609", ["e"] = "2", ["l"] = "M", ["s"] = "3", ["d"] = "72", ["v"] = "4",
            ["t"] = "0." + RandomNumberGenerator.GetInt32(100000000, 1000000000).ToString(CultureInfo.InvariantCulture),
            ["daid"] = "383", ["pt_3rd_aid"] = "100497308"
        })).ConfigureAwait(false);
        AccountRules.Require(response.Location is null && response.MediaType == "image/png", "INVALID_RESPONSE");
        AccountQrPng.Validate(response.Bytes, ctx.Token);
        string key = ReadQqCookies(response.Cookies, "ssl.ptlogin2.qq.com", ["qrsig"])["qrsig"];
        var challenge = new MusicAccountChallenge("tx", Guid.NewGuid().ToString("D"), "",
            clock.GetUtcNow().ToUnixTimeMilliseconds() + 180000, Convert.ToBase64String(response.Bytes));
        lock (gate)
        {
            ctx.Guard();
            states["tx"].Qr = new(challenge.ChallengeId, key, ctx.Generation, clock.GetTimestamp());
        }
        return challenge;
    });
    private Task<MusicAccountPoll> PollTx(string id, MusicAccountSessionHandoff accept, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Qr qr;
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            var current = states["tx"].Qr;
            if (current is null || current.Id != id || current.Consumed)
                return Task.FromResult(new MusicAccountPoll("tx", id, "expired"));
            if (clock.GetElapsedTime(current.Started) >= TimeSpan.FromSeconds(180))
            { Fence(states["tx"], "CANCELLED"); return Task.FromResult(new MusicAccountPoll("tx", id, "expired")); }
            qr = current;
        }
        return Run("tx", token, async ctx =>
        {
            void GuardQr()
            {
                ctx.Guard();
                lock (gate) AccountRules.Require(ReferenceEquals(states["tx"].Qr, qr), "SUPERSEDED");
                AccountRules.Require(clock.GetElapsedTime(qr.Started) < TimeSpan.FromSeconds(180), "QR_EXPIRED");
            }
            GuardQr();
            var response = await ctx.SendAccount(MusicAccountHttpStep.QqPoll, QqGet("ssl.ptlogin2.qq.com", "/ptqrlogin", new()
            {
                ["u1"] = QqJump, ["ptqrtoken"] = Hash33(qr.Key).ToString(CultureInfo.InvariantCulture),
                ["ptredirect"] = "0", ["h"] = "1", ["t"] = "1", ["g"] = "1", ["from_ui"] = "1",
                ["ptlang"] = "2052", ["action"] = "0-0-" + clock.GetUtcNow().ToUnixTimeMilliseconds(),
                ["js_ver"] = "20102616", ["js_type"] = "1", ["pt_uistyle"] = "40",
                ["aid"] = "716027609", ["daid"] = "383", ["pt_3rd_aid"] = "100497308", ["has_onekey"] = "1"
            }, "qrsig=" + qr.Key)).ConfigureAwait(false);
            GuardQr();
            AccountRules.Require(response.Location is null, "REDIRECT_DENIED");
            var fields = QqCallback(response.Bytes);
            string status = fields[0] switch { "66" => "waiting", "67" => "confirming",
                "65" => "expired", "68" => "refused", "0" => "authenticated", _ => throw new MusicAccountException("INVALID_RESPONSE") };
            if (status != "authenticated")
            {
                if (status is "expired" or "refused") lock (gate) { GuardQr(); states["tx"].Qr = null; }
                return new MusicAccountPoll("tx", id, status);
            }
            lock (gate) { GuardQr(); AccountRules.Require(!qr.Consumed, "QR_EXPIRED"); qr.Consumed = true; }
            var redirect = QqUrl(fields[2], "ssl.ptlogin2.graph.qq.com", "/check_sig");
            AccountRules.Require(redirect.GetValueOrDefault("service") == "ptqrlogin" &&
                redirect.GetValueOrDefault("s_url") == QqJump, "REDIRECT_DENIED");
            string uin = AccountRules.Id(redirect.GetValueOrDefault("uin"));
            string sig = QqAtom(redirect.GetValueOrDefault("ptsigx", ""), 512);
            var signature = await ctx.SendAccount(MusicAccountHttpStep.QqSignature, QqGet("ssl.ptlogin2.graph.qq.com", "/check_sig", new()
            {
                ["uin"] = uin, ["pttype"] = "1", ["service"] = "ptqrlogin", ["nodirect"] = "0",
                ["ptsigx"] = sig, ["s_url"] = QqJump, ["ptlang"] = "2052", ["ptredirect"] = "100",
                ["aid"] = "716027609", ["daid"] = "383", ["j_later"] = "0", ["low_login_hour"] = "0",
                ["regmaster"] = "0", ["pt_login_type"] = "3", ["pt_aid"] = "0", ["pt_aaid"] = "16",
                ["pt_light"] = "0", ["pt_3rd_aid"] = "100497308"
            })).ConfigureAwait(false);
            GuardQr();
            if (signature.Location is { } jump) _ = QqUrl(jump, "graph.qq.com", "/oauth2.0/login_jump");
            var cookies = ReadQqCookies(signature.Cookies, "ssl.ptlogin2.graph.qq.com",
                ["p_skey", "p_uin", "pt4_token", "pt2gguin", "uin", "skey"]);
            AccountRules.Require(cookies.ContainsKey("p_skey"), "INVALID_RESPONSE");
            foreach (string name in new[] { "p_uin", "uin" })
                if (cookies.TryGetValue(name, out var returned))
                    AccountRules.Require(returned.TrimStart('o', '0') == uin.TrimStart('0'), "IDENTITY_MISMATCH");
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["response_type"] = "code", ["client_id"] = "100497308", ["redirect_uri"] = QqRedirect,
                ["scope"] = "get_user_info,get_app_friends", ["state"] = "state", ["switch"] = "",
                ["from_ptlogin"] = "1", ["src"] = "1", ["update_auth"] = "1", ["openapi"] = "1010_1030",
                ["g_tk"] = Hash33(cookies["p_skey"], 5381).ToString(CultureInfo.InvariantCulture),
                ["auth_time"] = (clock.GetUtcNow().ToUnixTimeSeconds() * 1000).ToString(CultureInfo.InvariantCulture),
                ["ui"] = Guid.NewGuid().ToString("D")
            });
            var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.qq.com/oauth2.0/authorize")
            { Content = new StringContent(await form.ReadAsStringAsync(ctx.Token).ConfigureAwait(false), Encoding.UTF8, "application/x-www-form-urlencoded") };
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(p => p.Key + "=" + p.Value)));
            var authorized = await ctx.SendAccount(MusicAccountHttpStep.QqAuthorize, request).ConfigureAwait(false);
            GuardQr();
            var location = QqUrl(authorized.Location ?? "", "y.qq.com", "/portal/wx_redirect.html");
            AccountRules.Require(location.Keys.All(k => k is "code" or "state" or "login_type" or "surl") &&
                location.GetValueOrDefault("state") == "state" &&
                (!location.ContainsKey("login_type") || location["login_type"] == "1") &&
                (!location.ContainsKey("surl") || location["surl"] == "https://y.qq.com/"), "REDIRECT_DENIED");
            string code = QqAtom(location.GetValueOrDefault("code", ""), 512);
            var login = (await ctx.Send(MusicPlatformRequests.TxQrLogin(code)).ConfigureAwait(false)).Body;
            QqLoginStatus(login["code"]);
            AccountRules.Require(login["req_0"] is JsonObject, "INVALID_RESPONSE");
            QqLoginStatus(login["req_0"]!["code"]);
            AccountRules.Require(login["req_0"]!["data"] is JsonObject, "INVALID_RESPONSE");
            var data = login["req_0"]!["data"]!.AsObject();
            if (data["code"] is { } loginCode) QqLoginStatus(loginCode);
            string cookie = QqCredential(data);
            var profile = await ValidateTx(ctx, cookie, AccountRules.Cookies(cookie)).ConfigureAwait(false);
            foreach (string secret in cookies.Where(p => p.Key is not ("uin" or "p_uin" or "pt2gguin")).Select(p => p.Value)
                .Concat(new[] { qr.Key, sig, code }))
                AccountRules.Require(!new[] { profile.UserId, profile.DisplayName, profile.Avatar ?? "" }
                    .Any(value => value.Contains(secret, StringComparison.Ordinal) ||
                        value.Contains(Uri.EscapeDataString(secret), StringComparison.Ordinal)), "INVALID_RESPONSE");
            GuardQr();
            var details = await ObserveAccountDetails(ctx, profile, cookie).ConfigureAwait(false);
            GuardQr();
            await ctx.Accept(accept, new("tx", id, cookie, details)).ConfigureAwait(false);
            return new MusicAccountPoll("tx", id, "authenticated");
        }, replace: false);
    }
    private static void QqLoginStatus(JsonNode? node)
    {
        int code = StrictCode(node);
        if (code is 20277 or 20278 or 20450) throw new MusicAccountException("FORBIDDEN");
        if (code == 20279) throw new MusicAccountException("DEVICE_LIMIT");
        if (code is 20271 or 20276) throw new MusicAccountException("VERIFICATION_REQUIRED");
        if (code is 104604 or 100001) throw new MusicAccountException("RATE_LIMITED");
        if (code is 20272 or 20274) throw new MusicAccountException("IDENTITY_MISMATCH");
        QqStatus(node);
    }
    private static string QqCredential(JsonObject data)
    {
        string id = Id(data["musicid"]);
        string Alias(string a, string b)
        {
            var x = data[a] is null ? "" : Scalar(data[a]); var y = data[b] is null ? "" : Scalar(data[b]);
            AccountRules.Require(x.Length == 0 || y.Length == 0 || x == y, "IDENTITY_MISMATCH"); return x.Length > 0 ? x : y;
        }
        string euin = Alias("encrypt_uin", "encryptUin"), login = Alias("login_type", "loginType");
        string musicKey = Text(data["musickey"]);
        // Preserve the pinned Credential model's missing-type inference; a returned
        // WeChat credential must not be relabelled as the requested QQ login.
        if (login.Length == 0) login = musicKey.StartsWith("W_X", StringComparison.Ordinal) ? "1" : "2";
        AccountRules.Require(login == "2", "IDENTITY_MISMATCH");
        AccountRules.Require(AccountRules.Match(euin, "^[A-Za-z0-9=_-]{1,256}$"), "INVALID_RESPONSE");
        if (data["str_musicid"] is { } strId) AccountRules.Require(Scalar(strId) == id, "IDENTITY_MISMATCH");
        var values = new Dictionary<string, string> { ["musicid"] = id, ["str_musicid"] = id,
            ["encrypt_uin"] = euin, ["login_type"] = "2", ["musickey"] = musicKey };
        foreach (string key in new[] { "openid", "refresh_token", "access_token", "unionid", "refresh_key" })
            if (data[key] is { } value) values[key] = Text(value);
        foreach (string key in new[] { "expired_at", "musickey_create_time", "key_expires_in" })
            if (data[key] is { } value)
            {
                values[key] = AccountRules.QqNumber(Scalar(value), "INVALID_RESPONSE");
            }
        foreach (var value in values.Values)
            AccountRules.Require(value.Length <= 8192 && value.All(c => c is >= '!' and <= '~' &&
                c is not (';' or ',' or '"' or '\\')), "INVALID_RESPONSE");
        string cookie = string.Join("; ", values.Select(p => p.Key + "=" + p.Value));
        _ = AccountRules.Cookies(cookie); return cookie;
    }
    private Dictionary<string, string> ReadQqCookies(string[] rows, string host, string[] allowed)
    {
        AccountRules.Require(rows.Length is > 0 and <= 32, "INVALID_RESPONSE");
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string row in rows)
        {
            AccountRules.Text(row, 8192); var fields = row.Split(';'); int eq = fields[0].IndexOf('=');
            AccountRules.Require(eq > 0, "INVALID_RESPONSE");
            string name = fields[0][..eq].Trim(), value = fields[0][(eq + 1)..].Trim();
            if (!allowed.Contains(name, StringComparer.Ordinal)) continue;
            AccountRules.Require(value.Length is > 0 and <= 2048 && value.All(c => c is >= '!' and <= '~' &&
                c is not (',' or ';' or '"' or '\\')) && cookies.TryAdd(name, value), "INVALID_RESPONSE");
            var attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in fields.Skip(1))
            {
                int at = field.IndexOf('=');
                string key = (at < 0 ? field : field[..at]).Trim(), text = at < 0 ? "" : field[(at + 1)..].Trim();
                AccountRules.Require(attributes.Add(key), "INVALID_RESPONSE");
                if (key.Equals("domain", StringComparison.OrdinalIgnoreCase))
                {
                    string domain = text.TrimStart('.').ToLowerInvariant();
                    // Cookie scopes may be an ancestor of the fixed response host.
                    // This validates a cookie, NOT a new outbound origin permission.
                    AccountRules.Require(domain == host || domain == "qq.com" ||
                        domain.EndsWith(".qq.com", StringComparison.Ordinal) &&
                        host.EndsWith("." + domain, StringComparison.Ordinal), "ORIGIN_DENIED");
                }
                if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
                    AccountRules.Require(text == "/", "INVALID_RESPONSE");
                if (key.Equals("max-age", StringComparison.OrdinalIgnoreCase))
                    AccountRules.Require(long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long age) && age > 0, "AUTH_EXPIRED");
                if (key.Equals("expires", StringComparison.OrdinalIgnoreCase))
                    AccountRules.Require(DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date) && date > clock.GetUtcNow(), "AUTH_EXPIRED");
            }
        }
        if (allowed.Length == 1) AccountRules.Require(cookies.ContainsKey(allowed[0]), "INVALID_RESPONSE");
        return cookies;
    }
    private static Dictionary<string, string> QqUrl(string address, string host, string path)
    {
        AccountRules.Require(address.Length is > 0 and <= 4096 && !address.Any(c => char.IsControl(c) || c == '\\') &&
            Uri.TryCreate(address, UriKind.Absolute, out _), "REDIRECT_DENIED");
        var uri = new Uri(address);
        AccountRules.Require(uri.Scheme == "https" && uri.Host == host && uri.IsDefaultPort &&
            uri.AbsolutePath == path && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0, "REDIRECT_DENIED");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (uri.Query.Length == 0) return result;
        foreach (string part in uri.Query[1..].Split('&'))
        {
            AccountRules.Require(result.Count < 32, "LIMIT");
            int at = part.IndexOf('='); AccountRules.Require(at > 0, "REDIRECT_DENIED");
            string key = Uri.UnescapeDataString(part[..at]), value = Uri.UnescapeDataString(part[(at + 1)..]);
            AccountRules.Require(key.Length is > 0 and <= 80 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), "REDIRECT_DENIED");
            AccountRules.Require(!value.Any(char.IsControl) && result.TryAdd(key, value), "REDIRECT_DENIED");
        }
        return result;
    }
    private static string[] QqCallback(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).Trim(); }
        catch { throw new MusicAccountException("INVALID_RESPONSE"); }
        AccountRules.Require(text.StartsWith("ptuiCB(", StringComparison.Ordinal), "INVALID_RESPONSE");
        int at = 7;
        var args = new List<string>();
        while (true)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            AccountRules.Require(at < text.Length && text[at++] == '\'' && args.Count < 6, "INVALID_RESPONSE");
            var value = new StringBuilder(); bool ended = false;
            while (at < text.Length)
            {
                char c = text[at++];
                if (c == '\'') { ended = true; break; }
                if (c == '\\')
                {
                    AccountRules.Require(at < text.Length, "INVALID_RESPONSE"); c = text[at++];
                    if (c is 'x' or 'u')
                    {
                        int n = c == 'x' ? 2 : 4;
                        AccountRules.Require(at + n <= text.Length && ushort.TryParse(text.AsSpan(at, n),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _), "INVALID_RESPONSE");
                        c = (char)ushort.Parse(text.AsSpan(at, n), NumberStyles.HexNumber, CultureInfo.InvariantCulture); at += n;
                    }
                    else AccountRules.Require(c is '\\' or '\'' or '/', "INVALID_RESPONSE");
                }
                AccountRules.Require(c >= 32 && c != 127 && value.Length < 8192, "INVALID_RESPONSE"); value.Append(c);
            }
            AccountRules.Require(ended, "INVALID_RESPONSE"); args.Add(value.ToString());
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            AccountRules.Require(at < text.Length, "INVALID_RESPONSE");
            if (text[at] == ',') { at++; continue; }
            AccountRules.Require(text[at++] == ')', "INVALID_RESPONSE");
            if (at < text.Length && text[at] == ';') at++;
            AccountRules.Require(args.Count == 6 && string.IsNullOrWhiteSpace(text[at..]), "INVALID_RESPONSE");
            return args.ToArray();
        }
    }
}

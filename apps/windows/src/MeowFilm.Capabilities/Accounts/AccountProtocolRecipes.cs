// SPDX-License-Identifier: GPL-3.0-only
// Narrow account recipes ported from pinned sources; see LICENSES/NOTICE.md.
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountProtocol
{
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text)
        ? text : throw new MusicAccountException("INVALID_RESPONSE");
    private static string Scalar(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text
        : node is JsonValue ? node.ToJsonString() : "";
    private static int StrictCode(JsonNode? node)
    {
        AccountRules.Require(node is JsonValue && !((JsonValue)node).TryGetValue<string>(out _), "INVALID_RESPONSE");
        using var json = JsonDocument.Parse(node!.ToJsonString());
        AccountRules.Require(json.RootElement.TryGetDecimal(out var number) && number is >= int.MinValue and <= int.MaxValue &&
            decimal.Truncate(number) == number, "INVALID_RESPONSE");
        return (int)number;
    }
    private static string Id(JsonNode? node)
    {
        string raw = Scalar(node);
        if (node is JsonValue value && !value.TryGetValue<string>(out _) && !AccountRules.Match(raw, "^[1-9][0-9]{0,19}$"))
        {
            using var json = JsonDocument.Parse(raw);
            AccountRules.Require(json.RootElement.TryGetDecimal(out var number) && number is > 0 and <= AccountRules.MaxInteger &&
                decimal.Truncate(number) == number, "INVALID_RESPONSE");
            raw = number.ToString("0", CultureInfo.InvariantCulture);
        }
        return AccountRules.Id(raw);
    }
    private static bool Truth(JsonNode? node) => node is not null && node.ToJsonString() is not ("null" or "false" or "0" or "\"\"");
    private static void Inspect(JsonNode node)
    {
        int count = 0;
        void Walk(JsonNode? child, int depth)
        {
            AccountRules.Require(++count <= 20000 && depth <= 24, "LIMIT");
            if (child is JsonObject obj) foreach (var (key, value) in obj)
            {
                AccountRules.Require(key is not ("__proto__" or "constructor" or "prototype"), "INVALID_RESPONSE");
                Walk(value, depth + 1);
            }
            else if (child is JsonArray array) foreach (var item in array) Walk(item, depth + 1);
            else if (child is JsonValue v && v.TryGetValue<string>(out var text))
            {
                AccountRules.Require(text.Length <= 65536, "LIMIT");
                // Upstream content may contain controls; only fields published in a DTO use Text().
                try { _ = new UTF8Encoding(false, true).GetByteCount(text); }
                catch { throw new MusicAccountException("INVALID_RESPONSE"); }
            }
        }
        Walk(node, 0);
    }
    private static void WyStatus(JsonObject data)
    {
        string code = Scalar(data["code"]);
        string? error = code switch { "301" or "401" => "AUTH_EXPIRED", "403" => "FORBIDDEN",
            "405" or "429" or "509" => "RATE_LIMITED", "250" or "460" or "461" => "VERIFICATION_REQUIRED", _ => null };
        if (error is not null) throw new MusicAccountException(error);
        AccountRules.Require(StrictCode(data["code"]) == 200, "UPSTREAM_ERROR");
    }
    private static MusicAccountProfile Summary(string platform, string id, JsonNode? name, JsonNode? avatar, string cookie) =>
        AccountRules.Profile(new(platform, AccountRules.Id(id), AccountRules.Text(Text(name), 200, empty: true),
            AccountRules.Avatar(avatar is JsonValue v && v.TryGetValue<string>(out var text) ? text : null)), platform, cookie);
    private static async Task<MusicAccountProfile> ValidateWy(Context ctx, string cookie, Dictionary<string, string> parts,
        string? expectedUserId = null)
    {
        AccountRules.Require(parts.TryGetValue("MUSIC_U", out var value) && value.Length > 0, "INVALID_CREDENTIALS");
        var response = await ctx.Send(ctx.Wy("/api/w/nuser/account/get", new(), cookie, weapi: true))
            .ConfigureAwait(false);
        var data = response.Body;
        WyStatus(data);
        if (data.ContainsKey("profile") && data["profile"] is null) throw new MusicAccountException("AUTH_EXPIRED");
        AccountRules.Require(data["profile"] is JsonObject, "INVALID_RESPONSE");
        var profile = data["profile"]!.AsObject();
        string userId = Id(profile["userId"]);
        AccountRules.Require(expectedUserId is null || userId == expectedUserId, "IDENTITY_MISMATCH");
        if (data["account"] is not null)
        {
            AccountRules.Require(data["account"] is JsonObject, "INVALID_RESPONSE");
            if (data["account"]!["id"] is { } accountId)
                AccountRules.Require(Id(accountId) == userId, "IDENTITY_MISMATCH");
        }
        var summary = Summary("wy", userId, profile["nickname"], profile["avatarUrl"], cookie);
        await ctx.AcceptWyCookies(response.SetCookieHeaders, cookie).ConfigureAwait(false);
        return summary;
    }
    private static void QqStatus(JsonNode? value)
    {
        int code = StrictCode(value);
        if (code is 1000 or 104400 or 104401) throw new MusicAccountException("AUTH_EXPIRED");
        if (code == 2000) throw new MusicAccountException("SIGNATURE_REQUIRED");
        if (code == 2001) throw new MusicAccountException("RATE_LIMITED");
        AccountRules.Require(code == 0, "UPSTREAM_ERROR");
    }
    private static async Task<JsonObject> QqRead(Context ctx, string cookie, string module, string method, JsonObject param)
    {
        var result = (await ctx.Send(MusicPlatformRequests.TxWithCookie(module, method, param, cookie)).ConfigureAwait(false)).Body;
        QqStatus(result["code"]);
        AccountRules.Require(result["req_0"] is JsonObject, "INVALID_RESPONSE");
        var request = result["req_0"]!.AsObject();
        QqStatus(request["code"]);
        AccountRules.Require(request["data"] is JsonObject, "INVALID_RESPONSE");
        return request["data"]!.AsObject();
    }
    private static async Task<MusicAccountProfile> ValidateTx(Context ctx, string cookie, Dictionary<string, string> parts)
    {
        string userId = AccountRules.Id(parts.GetValueOrDefault("musicid"), "INVALID_CREDENTIALS");
        AccountRules.Text(parts.GetValueOrDefault("musickey"), 8192, "INVALID_CREDENTIALS");
        if (parts.TryGetValue("str_musicid", out var id)) AccountRules.Require(id == userId, "INVALID_CREDENTIALS");
        string Alias(string a, string b)
        {
            string x = parts.GetValueOrDefault(a, ""), y = parts.GetValueOrDefault(b, "");
            AccountRules.Require(x.Length == 0 || y.Length == 0 || x == y, "INVALID_CREDENTIALS");
            return x.Length > 0 ? x : y;
        }
        string login = Alias("login_type", "loginType"), euin = Alias("encrypt_uin", "encryptUin");
        AccountRules.Require(login.Length == 0 || AccountRules.Match(login, "^[1-9][0-9]?$"), "INVALID_CREDENTIALS");
        AccountRules.Require(AccountRules.Match(euin, "^[A-Za-z0-9=_-]{1,256}$"), "INVALID_CREDENTIALS");
        var auth = await QqRead(ctx, cookie, "music.UserInfo.userInfoServer", "GetLoginUserInfo", new()).ConfigureAwait(false);
        if (auth["code"] is { } authCode) QqStatus(authCode);
        var home = await QqRead(ctx, cookie, "music.UnifiedHomepage.UnifiedHomepageSrv", "GetHomepageHeader",
            new() { ["uin"] = euin, ["IsQueryTabDetail"] = 1 }).ConfigureAwait(false);
        AccountRules.Require(home["Info"] is JsonObject info && info["BaseInfo"] is JsonObject, "INVALID_RESPONSE");
        var profile = home["Info"]!["BaseInfo"]!.AsObject();
        AccountRules.Require(Scalar(profile["EncryptedUin"]) == euin, "IDENTITY_MISMATCH");
        return Summary("tx", userId, profile["Name"], profile["Avatar"], cookie);
    }
    // MIT KuGouMusicApi public client key. This is NOT private material.
    // Source format: right-zero-padded 128-byte plaintext + RSA_NO_PADDING, not PKCS#1.
    private async Task<MusicAccountProfile> ValidateKg(Context ctx, string cookie, Dictionary<string, string> parts,
        bool requireIdentity = false)
    {
        string userId = AccountRules.Id(parts.GetValueOrDefault("userid"), "INVALID_CREDENTIALS");
        string token = AccountRules.Text(parts.GetValueOrDefault("token"), 4096, "INVALID_CREDENTIALS");
        _ = PlatformProtocols.KgIdentity(parts, ctx.KgClient);
        long time = clock.GetUtcNow().ToUnixTimeSeconds();
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new { token, clienttime = time },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        byte[] block = new byte[128];
        string encrypted;
        try
        {
            AccountRules.Require(plain.Length <= block.Length, "INVALID_CREDENTIALS");
            plain.CopyTo(block, 0);
            using var rsa = RSA.Create(); rsa.ImportFromPem(PlatformProtocols.KgPublicKey);
            var key = rsa.ExportParameters(false);
            var p = BigInteger.ModPow(new BigInteger(block, isUnsigned: true, isBigEndian: true),
                new BigInteger(key.Exponent!, true, true), new BigInteger(key.Modulus!, true, true));
            encrypted = Convert.ToHexString(p.ToByteArray(isUnsigned: true, isBigEndian: true)).PadLeft(256, '0');
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(block); }
        var data = (await ctx.Send(ctx.Kg("/v3/get_my_info", new() { ["plat"] = 1 },
            cookie, "usercenter.kugou.com", new() { ["visit_time"] = time, ["usertype"] = 1,
                ["p"] = encrypted, ["userid"] = JsonNode.Parse(userId) })).ConfigureAwait(false)).Body;
        string[] codes = ["error_code", "errcode", "err_code"];
        var values = codes.Where(k => data[k] is not null).Select(k => Scalar(data[k])).ToArray();
        if (values.Contains("20018")) throw new MusicAccountException("AUTH_EXPIRED");
        if (values.Contains("20028") || Truth(data["ssaCode"]) || Truth(data["ssa-code"]))
            throw new MusicAccountException("VERIFICATION_REQUIRED");
        AccountRules.Require(Scalar(data["status"]) == "1" && !Truth(data["error"]) && values.All(v => v == "0"), "UPSTREAM_ERROR");
        AccountRules.Require(data["data"] is JsonObject, "INVALID_RESPONSE");
        var profile = data["data"]!.AsObject();
        if (requireIdentity) AccountRules.Require(profile["userid"] is not null, "IDENTITY_MISMATCH");
        if (profile["userid"] is { } returnedId)
            AccountRules.Require(Id(returnedId) == userId, "IDENTITY_MISMATCH");
        return Summary("kg", userId, profile["nickname"], profile["pic"], cookie);
    }
    private static string QrCookie(IReadOnlyList<string> rows, DateTimeOffset now) =>
        MusicWyRequestContext.Authorization(rows, now);
}

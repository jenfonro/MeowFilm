// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

internal static class AccountRules
{
    internal static readonly string[] Platforms = ["wy", "tx", "kg"];
    internal const long MaxInteger = 9007199254740991;
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "INVALID_REQUEST", "INVALID_CREDENTIALS", "AUTH_REQUIRED", "AUTH_EXPIRED", "FORBIDDEN",
        "VERIFICATION_REQUIRED", "SIGNATURE_REQUIRED", "DEVICE_CONTEXT_REQUIRED", "UNSUPPORTED",
        "NETWORK_ERROR", "UPSTREAM_ERROR", "INVALID_RESPONSE", "REDIRECT_DENIED", "RATE_LIMITED",
        "CANCELLED", "TIMEOUT", "CLOSED", "LIMIT", "STORAGE", "STORAGE_CONFLICT", "STORAGE_UNAVAILABLE",
        "CORRUPT_STORAGE", "COMMIT_UNKNOWN", "STALE_SESSION", "IDENTITY_MISMATCH", "ORIGIN_DENIED",
        "SESSION_CHANGED", "SUPERSEDED", "BUSY", "QR_EXPIRED", "HANDOFF_REJECTED", "HANDOFF_UNCONFIRMED",
        "ACCOUNT_FAILED", "DEVICE_LIMIT", "INVALID_CODE", "SMS_SEND_UNCONFIRMED"
    };
    internal static string SafeCode(string? code) => code is not null && Codes.Contains(code) ? code : "ACCOUNT_FAILED";
    internal static string Error(Exception error) => error switch
    {
        MusicAccountException known => known.Code,
        OperationCanceledException => "CANCELLED",
        ProtectedDocumentException e => e.Error switch
        {
            DocumentStoreError.Conflict => "STORAGE_CONFLICT",
            DocumentStoreError.Unavailable => "STORAGE_UNAVAILABLE",
            DocumentStoreError.Corrupt or DocumentStoreError.UnsupportedVersion or DocumentStoreError.Protection => "CORRUPT_STORAGE",
            DocumentStoreError.Limit => "LIMIT",
            DocumentStoreError.Closed => "CLOSED",
            _ => "STORAGE"
        },
        _ => "ACCOUNT_FAILED"
    };
    internal static void Require(bool test, string code = "INVALID_REQUEST")
    { if (!test) throw new MusicAccountException(code); }
    internal static string Platform(string value)
    { Require(value is "wy" or "tx" or "kg"); return value; }
    internal static bool Match(string value, string pattern) =>
        Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    internal static string Text(string? value, int max, string code = "INVALID_RESPONSE", bool empty = false)
    {
        Require(value is not null && (empty || value.Length > 0) && value.Length <= max, code);
        for (int i = 0; i < value!.Length; i++)
        {
            char c = value[i];
            Require(c >= 32 && c != 127, code);
            if (char.IsHighSurrogate(c))
            { Require(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]), code); i++; }
            else Require(!char.IsLowSurrogate(c), code);
        }
        return value;
    }
    internal static long Integer(JsonElement node)
    {
        string raw = node.ValueKind == JsonValueKind.Number ? node.GetRawText() : "";
        long value = 0;
        Require(raw.Length is > 0 and <= 16 && raw.All(char.IsAsciiDigit) &&
            long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value <= MaxInteger, "CORRUPT_STORAGE");
        return value;
    }
    internal static string Id(string? value, string code = "INVALID_RESPONSE")
    { Require(value is not null && Match(value, "^[1-9][0-9]{0,19}$"), code); return value!; }
    internal static void Fields(JsonElement value, string[] allowed, string[] required, string code = "INVALID_REQUEST")
    {
        Require(value.ValueKind == JsonValueKind.Object, code);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            Require(allowed.Contains(property.Name, StringComparer.Ordinal) && names.Add(property.Name), code);
        Require(required.All(names.Contains), code);
    }
    internal static string String(JsonElement value, string name, int max, string code = "INVALID_REQUEST", bool empty = false)
    {
        Require(value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String, code);
        return Text(item.GetString(), max, code, empty);
    }
    internal static Dictionary<string, string> Cookies(string cookie)
    {
        Text(cookie, 16384, "INVALID_CREDENTIALS");
        Require(cookie.All(c => c is >= ' ' and <= '~'), "INVALID_CREDENTIALS");
        var parts = cookie.Split(';');
        Require(parts.Length <= 64, "INVALID_CREDENTIALS");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0 && i == parts.Length - 1) continue;
            int at = part.IndexOf('=');
            Require(at > 0, "INVALID_CREDENTIALS");
            string key = part[..at], value = part[(at + 1)..];
            Require(Match(key, "^[A-Za-z0-9_-]{1,80}$") &&
                key is not ("__proto__" or "prototype" or "constructor") &&
                key.ToLowerInvariant() is not ("domain" or "path" or "expires" or "max-age" or "secure" or "httponly" or "samesite" or "priority") &&
                !value.Any(c => char.IsWhiteSpace(c) || c is ',' or '"' or '\\') && result.TryAdd(key, value), "INVALID_CREDENTIALS");
        }
        return result;
    }
    internal static MusicAccountProfile Profile(MusicAccountProfile profile, string platform, string? cookie = null)
    {
        Require(profile is not null && profile.Platform == platform, "IDENTITY_MISMATCH");
        Text(profile!.UserId, 256);
        Require(profile.UserId.Trim() == profile.UserId, "INVALID_RESPONSE");
        Text(profile.DisplayName, 200, empty: true);
        if (profile.Avatar is not null)
        {
            Text(profile.Avatar, 2048, empty: true);
            Require(profile.Avatar.Length == 0 || Uri.TryCreate(profile.Avatar, UriKind.Absolute, out var uri) &&
                uri.Scheme == "https" && uri.UserInfo.Length == 0, "INVALID_RESPONSE");
        }
        if (cookie is not null)
        {
            string[] published = [profile.Platform, profile.UserId, profile.DisplayName, profile.Avatar ?? ""];
            foreach (var (key, secret) in Cookies(cookie))
            {
                if (key is "musicid" or "str_musicid" or "userid" or "login_type" or "loginType" or
                    "os" or "appver" or "versioncode" or "__remember_me" || secret is "" or "-") continue;
                Require(!published.Any(value => value.Contains(secret, StringComparison.Ordinal) ||
                    value.Contains(Uri.EscapeDataString(secret), StringComparison.Ordinal)), "INVALID_RESPONSE");
            }
        }
        return profile;
    }
    internal static string? Avatar(string? value)
    {
        if (value is null || value.Length > 2048) return null;
        value = value.Replace("{size}", "480", StringComparison.Ordinal);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
            Match(uri.Host, @"^(localhost|127\.|0\.|10\.|192\.168\.|169\.254\.|172\.(1[6-9]|2[0-9]|3[01])\.|\[)") ||
            Regex.IsMatch(uri.Query, @"\b(cookie|token|authst|musickey|MUSIC_U)=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100))) return null;
        return uri.AbsoluteUri;
    }
    internal static void Challenge(MusicAccountChallenge value, string platform)
    {
        Require(value is not null && value.Platform == platform && platform is "wy" or "tx" or "kg", "INVALID_RESPONSE");
        Text(value!.ChallengeId, 80);
        Require(Guid.TryParseExact(value.ChallengeId, "D", out _), "INVALID_RESPONSE");
        Require(value.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
            value.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() <= 180000, "INVALID_RESPONSE");
        if (platform == "tx")
        {
            Require(value.Url.Length == 0 && value.ImageBase64 is { Length: > 0 and <= 699052 }, "INVALID_RESPONSE");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(value.ImageBase64!); }
            catch { throw new MusicAccountException("INVALID_RESPONSE"); }
            AccountQrPng.Validate(bytes);
            return;
        }
        Require(value.ImageBase64 is null, "INVALID_RESPONSE");
        Text(value.Url, 2048);
        Require(Uri.TryCreate(value.Url, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
            (platform == "wy" ? uri.Host == "music.163.com" && uri.AbsolutePath == "/login" &&
                Match(uri.Query, @"^\?codekey=[A-Za-z0-9_-]{1,256}$")
                : uri.Host == "h5.kugou.com" && uri.AbsolutePath == "/apps/loginQRCode/html/index.html" &&
                Match(uri.Query, @"^\?qrcode=[A-Za-z0-9_-]{1,256}$")) &&
            value.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "ORIGIN_DENIED");
    }
}

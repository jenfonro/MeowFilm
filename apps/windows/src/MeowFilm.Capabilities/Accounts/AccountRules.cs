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
        "NETWORK_ERROR", "UPSTREAM_HTTP", "STORAGE_ERROR", "NOT_FOUND", "MEDIA_UNAVAILABLE", "TRIAL_ONLY", "QUALITY_UNAVAILABLE", "UPSTREAM_ERROR", "INVALID_RESPONSE", "REDIRECT_DENIED", "RATE_LIMITED",
        "CANCELLED", "TIMEOUT", "CLOSED", "LIMIT", "STORAGE", "STORAGE_CONFLICT", "STORAGE_UNAVAILABLE",
        "CORRUPT_STORAGE", "COMMIT_UNKNOWN", "STALE_SESSION", "IDENTITY_MISMATCH", "ORIGIN_DENIED",
        "SESSION_CHANGED", "SUPERSEDED", "BUSY", "QR_EXPIRED", "HANDOFF_REJECTED", "HANDOFF_UNCONFIRMED",
        "ACCOUNT_FAILED", "DEVICE_LIMIT", "INVALID_CODE", "INVALID_PHONE", "ACCOUNT_SELECTION_REQUIRED",
        "SMS_SEND_UNCONFIRMED", "TRANSFER_FILE", "TRANSFER_FORMAT"
    };
    internal static string SafeCode(string? code) => code is not null && Codes.Contains(code) ? code : "ACCOUNT_FAILED";
    internal static string Error(Exception error) => error switch
    {
        MusicAccountException known => known.Code,
        MeowFilm.Capabilities.Platforms.MusicPlatformException known =>
            SafeCode(known.Code == "AUTH_REQUIRED" ? "AUTH_EXPIRED" : known.Code),
        OperationCanceledException => "CANCELLED",
        DocumentStoreException e => e.Error switch
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
    internal static string QqNumber(string value, string code)
    {
        Require(Match(value, "^(0|[1-9][0-9]{0,15})$") &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number <= MaxInteger, code);
        return value;
    }
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
                !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\' || c == ',' && key != "_ntes_nnid") && result.TryAdd(key, value), "INVALID_CREDENTIALS");
        }
        return result;
    }
    internal static void Challenge(MusicAccountChallenge value, string platform)
    {
        // The native owner associates the challenge with its live operation; Node owns the QR protocol and expiry.
        Require(value.Platform == platform && !string.IsNullOrEmpty(value.ChallengeId), "INVALID_RESPONSE");
    }
}

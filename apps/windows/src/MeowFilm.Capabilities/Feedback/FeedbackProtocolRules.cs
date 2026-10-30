// SPDX-License-Identifier: GPL-3.0-only
// Native adaptation of the pinned iWebPlus feedback guards. See LICENSES/NOTICE.md.
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

internal static class FeedbackProtocolRules
{
    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static void Require([DoesNotReturnIf(false)] bool valid, string code = "INVALID_REQUEST")
    { if (!valid) throw new MusicFeedbackException(code); }
    internal static bool Text(string? value, int maximum, bool empty = false)
    {
        if (value is null || value.Length > maximum || !empty && value.Length == 0) return false;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i])) return false;
            if (char.IsHighSurrogate(value[i]))
            { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
    internal static bool Id(string? value, bool safeInteger = false) =>
        value is { Length: > 0 and <= 20 } && value[0] != '0' && value.All(char.IsAsciiDigit) &&
        (!safeInteger || ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 9007199254740991);
    internal static bool Hash(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit) && value.Any(c => c != '0');
    internal static string Scalar(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s :
        value is JsonValue ? value.ToJsonString(Json) : "";
    internal static string ResponseId(JsonNode? value)
    {
        string result = Scalar(value);
        Require(Id(result), "INVALID_RESPONSE");
        return result;
    }
    internal static double Number(JsonNode? value)
    {
        Require(value is JsonValue v && v.TryGetValue<double>(out var n) && double.IsFinite(n), "INVALID_RESPONSE");
        return value!.GetValue<double>();
    }
    internal static void WySuccess(JsonObject value)
    {
        // Never treat HTTP 200, boolean true, or a textual "200" as a receipt.
        using var document = JsonDocument.Parse(value["code"]?.ToJsonString(Json) ?? "null");
        var code = document.RootElement;
        Require(code.ValueKind == JsonValueKind.Number && code.TryGetDecimal(out _), "UPSTREAM_UNCONFIRMED");
        decimal raw = code.GetDecimal();
        string? error = raw switch
        {
            200 => null, 301 or 401 => "AUTH_EXPIRED", 403 => "FORBIDDEN",
            405 or 429 or 509 => "RATE_LIMITED", 250 or 460 or 461 => "VERIFICATION_REQUIRED",
            _ => "UPSTREAM_UNCONFIRMED"
        };
        if (error is not null) throw new MusicFeedbackException(error);
    }
    internal static Dictionary<string, string> Cookies(MusicPlatformSession account)
    {
        string raw = account.Cookie;
        Require(Text(raw, 16384) && raw.All(c => c is >= ' ' and <= '~'), "INVALID_CREDENTIALS");
        string[] fields = raw.Split(';');
        Require(fields.Length <= 64, "INVALID_CREDENTIALS");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < fields.Length; i++)
        {
            string field = fields[i].Trim();
            if (field.Length == 0 && i == fields.Length - 1) continue;
            int at = field.IndexOf('=');
            Require(at > 0, "INVALID_CREDENTIALS");
            string key = field[..at], value = field[(at + 1)..];
            Require(key.Length <= 80 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') &&
                key is not ("__proto__" or "constructor" or "prototype") &&
                key.ToLowerInvariant() is not ("domain" or "path" or "expires" or "max-age" or "secure" or "httponly" or "samesite") &&
                !value.Any(c => char.IsWhiteSpace(c) || c is ',' or '"' or '\\') && result.TryAdd(key, value), "INVALID_CREDENTIALS");
        }
        Require(Id(account.UserId), "INVALID_CREDENTIALS");
        switch (account.Platform)
        {
            case "wy": Require(result.GetValueOrDefault("MUSIC_U", "").Length > 0, "AUTH_REQUIRED"); break;
            case "tx":
                Require(result.GetValueOrDefault("musicid") == account.UserId &&
                    result.GetValueOrDefault("musickey", "").Length > 0 &&
                    (!result.TryGetValue("str_musicid", out var id) || id == account.UserId), "INVALID_CREDENTIALS");
                break;
            case "kg":
                Require(result.GetValueOrDefault("userid") == account.UserId &&
                    result.GetValueOrDefault("token", "").Length > 0, "INVALID_CREDENTIALS");
                break;
        }
        return result;
    }
    internal static MusicFeedbackStart Start(MusicPlatformSession account, MusicFeedbackStart value)
    {
        Require(value is not null && value.Recording is not null);
        Require(value!.Recording.Platform == account.Platform, "IDENTITY_MISMATCH");
        Require(value.InstanceId is { Length: >= 8 and <= 128 } &&
            value.InstanceId.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-'));
        var recording = value.Recording;
        Require(recording.Platform switch
        {
            "wy" => Id(recording.Id, true) && recording.MixSongId is null,
            "tx" => recording.Id is { Length: > 0 and <= 64 } && recording.Id.All(char.IsAsciiLetterOrDigit) && recording.MixSongId is null,
            "kg" => Hash(recording.Id) && (recording.MixSongId is null || Id(recording.MixSongId, true)),
            _ => false
        }, "IDENTITY_MISMATCH");
        Require(Seconds(value.Duration) && value.Duration > 0, "DURATION_REQUIRED");
        Require(Seconds(value.Position) && value.Position <= value.Duration + (account.Platform == "kg" ? 1 : 0));
        Require(Rate(value.Rate, account.Platform));
        Require(Text(value.Source, 32) && Text(value.SourceId, 512, true) &&
            Text(value.SourceType, 32, true) && Text(value.Algorithm, 2000, true) && Mode(value.Mode));
        if (account.Platform == "wy")
        {
            Require(value.Source is "userfm" or "playlist" or "album" or "search" or "dailySongRecommend" or
                "song" or "likeMusic" or "aiMode" or "moreRcmd" or "unknown");
            Require(value.SourceType is "" or "list" or "album" or "track" or "dailyRecommend");
            if (value.Source is "playlist" or "likeMusic" or "album" or "aiMode")
                Require(Id(value.SourceId, true) && value.SourceType ==
                    (value.Source == "album" ? "album" : value.Source == "aiMode" ? "track" : "list"));
            else if (value.Source != "search") Require(value.SourceId.Length == 0);
        }
        return account.Platform == "kg" ? value with { Recording = recording with { Id = recording.Id.ToUpperInvariant() } } : value;
    }
    private static bool Seconds(double n) => double.IsFinite(n) && n is >= 0 and <= 86400;
    private static bool Rate(double n, string platform) => double.IsFinite(n) && n > 0 && n <= 16 && (platform != "kg" || n >= .25);
    private static bool Mode(string? value) => value is "" or "order" or "circulation" or "single" or "random" or "ai" or "playFm";
    internal static bool CompatibleDuration(double a, double b) =>
        Seconds(a) && Seconds(b) && a > 0 && b > 0 && Math.Abs(a - b) <= Math.Max(3, Math.Min(a, b) * .02);
    internal static void End(MusicFeedbackStart start, MusicFeedbackEnd end, TimeSpan elapsed)
    {
        Require(end is not null);
        Require(Seconds(end.Listened) && Seconds(end.MediaListened) && Seconds(end.Duration) && Seconds(end.Position) &&
            Rate(end.Rate, start.Recording.Platform) && Mode(end.Mode));
        Require(CompatibleDuration(start.Duration, end.Duration), "IDENTITY_MISMATCH");
        Require(end.Position <= end.Duration + (start.Recording.Platform == "kg" ? 1 : 0));
        Require(end.Reason is "playend" or "ui" or "interrupt" or "exception");
        Require(end.Reason != "playend" || Math.Abs(end.Duration - end.Position) <= 1);
        Require(end.Listened <= Math.Max(0, elapsed.TotalSeconds) + 2 &&
            end.MediaListened <= end.Listened * 16 + 1 &&
            (start.Recording.Platform != "kg" || end.MediaListened + 1 >= end.Listened * .25), "INVALID_LISTEN_TIME");
    }
    internal static string Fingerprint(MusicPlatformSession account, MusicFeedbackStart start)
    {
        // Only a digest survives retirement; no credentials or outbound report are persisted.
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            account.Platform, account.Scope, account.Generation, account.UserId, account.Cookie, Start = start
        }, Json));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static string SafeError(Exception error) => error is MusicPlatformException known ? known.Code switch
    {
        "AUTH_REQUIRED" => "AUTH_EXPIRED",
        "FORBIDDEN" or "VERIFICATION_REQUIRED" or "SIGNATURE_REQUIRED" or "INVALID_CREDENTIALS" or
        "DEVICE_CONTEXT_REQUIRED" or "ORIGIN_DENIED" or "REDIRECT_DENIED" or "RATE_LIMITED" or "LIMIT" or
        "CLOSED" or "TIMEOUT" or "NETWORK_ERROR" or "INVALID_RESPONSE" or "CRYPTO_UNAVAILABLE" or
        "SESSION_CHANGED" or "DEVICE_CONTEXT_EXPIRED" or "COMMIT_UNKNOWN" or "COMMIT_CONFLICT" or
        "STORAGE" or "STORAGE_CONFLICT" or "STORAGE_UNAVAILABLE" or "CORRUPT_STORAGE" or "RELEASE_FAILED" => known.Code,
        _ => "UPSTREAM_UNCONFIRMED"
    } : "UPSTREAM_UNCONFIRMED";
}

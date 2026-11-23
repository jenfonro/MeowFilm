using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

/// <summary>Trusted receipt adapter. Key material never becomes a media URL or native string option.</summary>
internal static class FilmVideoInput
{
    internal static NativeVideoInput Read(JsonElement format, JsonElement drm)
    {
        var selected = ReadFormat(format);
        if (drm.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new(selected);
        var keys = NativeClearKeySet.FromHex(ReadClearKeys(drm));
        try { return new(selected, keys); }
        catch { keys.Dispose(); throw; }
    }

    internal static NativeVideoFormat ReadFormat(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return NativeVideoFormat.Auto;
        if (value.ValueKind != JsonValueKind.String) throw new FilmPlaybackException(PlaybackError.InvalidRequest);
        // Format is a provider hint. Other containers use the same demuxer's normal probing.
        return value.GetString()!.ToLowerInvariant() switch
        {
            "hls" or "m3u8" or "application/vnd.apple.mpegurl" or "application/x-mpegurl" => NativeVideoFormat.Hls,
            "dash" or "mpd" or "application/dash+xml" => NativeVideoFormat.Dash,
            "mp4" or "video/mp4" or "progressive" => NativeVideoFormat.Progressive,
            _ => NativeVideoFormat.Auto
        };
    }

    internal static IReadOnlyDictionary<string, string> ReadClearKeys(JsonElement drm)
    {
        if (drm.ValueKind != JsonValueKind.Object || !Matches(drm, "type", "clearkey") ||
            !Matches(drm, "keySystem", "org.w3.clearkey"))
            throw new FilmPlaybackException(PlaybackError.AdaptiveDrmUnsupported);
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var hex = FilmJson.Member(drm, "clearKeys");
        var jwk = FilmJson.Member(drm, "keys");
        if (hex.ValueKind == JsonValueKind.Object && jwk.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            foreach (var entry in hex.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String || !pairs.TryAdd(entry.Name, entry.Value.GetString()!))
                    throw new FilmPlaybackException(PlaybackError.InvalidRequest);
            }
        }
        else if (jwk.ValueKind == JsonValueKind.Array && hex.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            foreach (var entry in jwk.EnumerateArray())
            {
                if (FilmJson.Text(entry, "kty") != "oct" ||
                    !pairs.TryAdd(JwkHex(FilmJson.Member(entry, "kid")), JwkHex(FilmJson.Member(entry, "k"))))
                    throw new FilmPlaybackException(PlaybackError.InvalidRequest);
            }
        }
        else throw new FilmPlaybackException(PlaybackError.AdaptiveDrmUnsupported);
        if (pairs.Count == 0) throw new FilmPlaybackException(PlaybackError.InvalidRequest);
        return pairs;
    }

    private static bool Matches(JsonElement value, string name, string expected)
    {
        var field = FilmJson.Member(value, name);
        return field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            field.ValueKind == JsonValueKind.String && field.GetString() == expected;
    }

    private static string JwkHex(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw new FilmPlaybackException(PlaybackError.InvalidRequest);
        string text = value.GetString()!;
        // RFC 7517 Base64urlUInt: unpadded, canonical 128-bit KID/key bytes, not a Guid's little-endian fields.
        if (text.Length != 22 || text.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new FilmPlaybackException(PlaybackError.InvalidRequest);
        byte[] bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + "==");
        try
        {
            if (Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != text)
                throw new FilmPlaybackException(PlaybackError.InvalidRequest);
            return Convert.ToHexString(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

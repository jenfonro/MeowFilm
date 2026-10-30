using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities;

internal sealed record MusicSourceScriptInspection(string Name, string Version);

internal static class MusicSourceScriptInspector
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex HeaderPattern = new(
        @"^\s*(/\*[\s\S]*?\*/|(?://[^\n]*\n?)+)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    internal static MusicSourceScriptInspection Inspect(byte[] bytes, string basename, string sha256)
    {
        if (bytes.Length is < 1 or > 512 * 1024 ||
            !StringComparer.Ordinal.Equals(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes))))
            throw new MusicOperationException("SCRIPT_INTEGRITY");
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            throw new MusicOperationException("SCRIPT_ENCODING");
        string source;
        try
        {
            source = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new MusicOperationException("SCRIPT_ENCODING");
        }
        if (string.IsNullOrWhiteSpace(source) ||
            !StrictUtf8.GetBytes(source).AsSpan().SequenceEqual(bytes))
            throw new MusicOperationException("SCRIPT_ENCODING");

        var fallbackName = Path.GetFileNameWithoutExtension(basename);
        var header = HeaderPattern.Match(source[..Math.Min(source.Length, 8192)]).Value;
        var name = HeaderValue(header, "name", 255) ?? Clean(fallbackName, 255, "LX 音源") ?? "LX 音源";
        var version = HeaderValue(header, "version", 128) ?? "1";
        return new(name, version);
    }

    private static string? HeaderValue(string header, string field, int maximum)
    {
        if (header.Length == 0) return null;
        var match = Regex.Match(header,
            $@"(?:^|\n)\s*(?://|/\*|\*)?\s*@{Regex.Escape(field)}\s+([^\r\n]*)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? Clean(match.Groups[1].Value.Replace("*/", "", StringComparison.Ordinal), maximum, "") : null;
    }

    private static string? Clean(string value, int maximum, string fallback)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Any(char.IsControl)) return fallback.Length == 0 ? null : fallback;
        int length = Math.Min(value.Length, maximum);
        if (length < value.Length && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}

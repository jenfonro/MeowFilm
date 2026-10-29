using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

internal sealed record MusicSourceScriptInspection(string Name, string Version, string[] NetworkOrigins);

internal static class MusicSourceScriptInspector
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex HeaderPattern = new(
        @"^\s*(/\*[\s\S]*?\*/|(?://[^\n]*\n?)+)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex UrlPattern = new(
        """https?://[^\s"'`\\<>{}\])]+""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
    private static readonly IReadOnlyDictionary<string, string[]> ReviewedOrigins =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["9667af5501684e6948d8a24d20269cc90af6aa0fc196f2e6b8534f82ef053dd2"] =
                ["https://music.gdstudio.xyz"],
            ["48d5fed8b46344e9298d55427382ce573485a264a2ae287ed829e2f2548223e8"] =
                ["https://musicserver.haitangw.cc", "https://www.97abc.com"],
            ["8cd3f90d51011d2b82d9fbf8a7ffe2d087f334e6fb1ff27dfba07374db3f9acc"] =
                ["https://api.cenguigui.cn", "https://api.xingzhige.com"],
            ["c68c2ab7045b94769786b56cf7c6e614d82bc8f303bff68173bef9790583affc"] =
            [
                "https://88.lxmusic.xn--fiqs8s", "https://api.cenguigui.cn", "https://api.vkeys.cn",
                "https://c.musicapp.migu.cn", "https://music-api.gdstudio.xyz", "https://nmobi.kuwo.cn",
                "https://wyapi.toubiec.cn"
            ],
            ["7ba2ca2c4b5ec2b0931f1a4f7f542a19e844f40f2765b661d0aee3b38e63ad4a"] =
            [
                "https://api.xcvts.cn", "https://lc.guoyue2010.top", "https://lxmusicapi.onrender.com",
                "https://music-api.gdstudio.xyz", "https://oiapi.net"
            ],
            ["b788ad01c034cecd780dd75948d94405e9adedde60622dc27225cc35694f0048"] =
            [
                "https://api-v2.yuafeng.cn", "https://api.2bi.cn", "https://api.317ak.com",
                "https://api.chksz.top", "https://api.gdstudio.cn", "https://api.huibq.cn",
                "https://api.linglan.icu", "https://api.music.lerd.dpdns.org",
                "https://api.xinghai-backend.cn", "https://app.c.nf.migu.cn", "https://cdyzr.dpdns.org",
                "https://kw-api.cenguigui.cn", "https://music.migu.cn", "https://music.nxinxz.com",
                "https://oiapi.net", "https://tang.api.s01s.cn", "https://tx.nianxinapi.cn",
                "https://ut.y.qq.com", "https://wy.nianxinapi.cn", "https://yy.zddyr.top"
            ],
            ["5c0fd239110e4835533d7f289ad895cae2ded3917af501db0650f08f592265ea"] =
            [
                "http://171.80.3.149:3035", "http://171.80.3.149:3036", "http://171.80.3.149:3037",
                "https://api.chksz.top", "https://api.cxzja.cn", "https://api.xcvts.cn",
                "https://api.yaohud.cn", "https://api.ygking.top", "https://api.yunmge.com",
                "https://m-api.ceseet.me", "https://mcp.nianxinxz.com", "https://music.bxa241d4.shop",
                "https://oiapi.net", "https://v.yuafeng.cn", "https://wyapi.toubiec.cn", "https://yy.zddyr.top"
            ],
            ["b45e05b8c043f0e715f9856ea400707bdf131b9c05b5014e1de9d0821a9972a7"] =
                ["https://metingapi.nanorocky.top", "https://musicserver.haitangw.cc", "https://www.97abc.com", "https://yy.zddyr.top"],
            ["2a9ed0de34d0c19ebf4b225cde3d9cba28599c238c9502c49be2c411b805bfad"] =
            [
                "http://175.27.166.236", "https://app.c.nf.migu.cn", "https://metingapi.nanorocky.top",
                "https://music.haitangw.cc", "https://music.xuanluoge.top", "https://nmobi.kuwo.cn",
                "https://oiapi.net", "https://tang.api.s01s.cn", "https://www.97abc.com"
            ],
            ["46a151119f72e2b04e7fa1a42baf2208f82be5d94c0c1b580eab5b23e754149d"] =
                ["https://music.gdstudio.xyz"],
            ["669b235196af1f3e1446636ca99e964fe80e94641ee1f67e67d42ae114e64d63"] =
                ["https://music-api.gdstudio.xyz", "https://yy.zddyr.top"],
            ["9ddcc32f55ba1981166f16bf55c03c43165b098932208a5fb4c9c64192650f0c"] =
            [
                "http://music.xn--q35am7h.site", "https://api.chksz.top", "https://mcp.nianxinxz.com",
                "https://metingapi.nanorocky.top", "https://music.haitangw.cc", "https://nmobi.kuwo.cn",
                "https://tang.api.s01s.cn", "https://www.97abc.com", "https://yy.zddyr.top"
            ]
        };

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
        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in UrlPattern.Matches(source))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (MusicSourceNetworkPolicy.TryFromUrl(candidate, out var origin)) origins.Add(origin);
        }
        foreach (var candidate in ReviewedExtraOrigins(sha256))
            if (MusicSourceNetworkPolicy.TryCanonical(candidate, out var origin)) origins.Add(origin);
        if (origins.Count > MusicSourceNetworkPolicy.MaximumOrigins)
            throw new MusicOperationException("LIMIT");
        return new(name, version, origins.OrderBy(origin => origin, StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<string> ReviewedExtraOrigins(string sha256) =>
        ReviewedOrigins.GetValueOrDefault(sha256) ?? [];

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

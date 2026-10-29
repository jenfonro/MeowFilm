// SPDX-License-Identifier: GPL-3.0-only
// Fixed anonymous LX / meowmusic recipes; see LICENSES/NOTICE.md
// and docs/windows/music-native-platforms.md. No account capability.
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformHttp
{
    private const string PublicKwAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36";
    private const string PublicMgMobileAgent = "Mozilla/5.0 (Linux; U; Android 11.0.0; zh-cn; MI 11 Build/OPR1.170623.032) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30";
    private const string PublicMgPlaylistAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1";
    // Public SDK profile, never a generated device or an account identity.
    private const string PublicMgDevice = "963B7AA0D21511ED807EE5846EC87D20";
    private const string PublicMgSongs = """{"song":1,"album":0,"singer":0,"tagSong":1,"mvSong":0,"bestShow":1,"songlist":0,"lyricSong":0}""";
    private const string PublicMgPlaylists = """{"song":0,"album":0,"singer":0,"tagSong":0,"mvSong":0,"bestShow":0,"songlist":1,"lyricSong":0}""";

    // Transfers request ownership, including validation failures. Uses the same
    // physical slots and settle semantics as native writes, not ordinary WaitAsync.
    internal Task<byte[]> SendPublicCatalogReadAsync(string platform, HttpRequestMessage request,
        CancellationToken token, int maxResponseBytes, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            token.ThrowIfCancellationRequested();
            PJ.Require(maxResponseBytes is >= 1 and <= 4194304 &&
                timeout.TotalMilliseconds is >= 1 and <= 25000, "LIMIT");
        }
        catch { request.Dispose(); throw; }
        return SendCoreAsync(platform, request, token, maxResponseBytes, timeout,
            (bytes, _, _) => { PJ.Require(bytes.Length > 0, "INVALID_RESPONSE"); return bytes; },
            nativePolicy: NativeHttpPolicy.PublicCatalogRead);
    }

    private static bool PublicCatalogOrigin(string platform, HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (platform is not ("kw" or "mg") || uri is null || !uri.IsAbsoluteUri ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.OriginalString.Length > 65536 || uri.OriginalString.Contains('\\') ||
            !PublicUnescape(uri.OriginalString, out _) ||
            !PublicQuery(uri.Query, out var q)) return false;
        if (request.Method == HttpMethod.Get)
        {
            if (request.Content is not null) return false;
        }
        else if (request.Method != HttpMethod.Post) return false;

        if (platform == "kw")
        {
            if (request.Method != HttpMethod.Get || !PublicHeaders(request, PublicKwAgent, "https://www.kuwo.cn/"))
                return false;
            if (uri.Scheme == "http" && uri.Host == "search.kuwo.cn" && uri.AbsolutePath == "/r.s")
            {
                if (!PublicKeyword(q.GetValueOrDefault("all")) || !PublicNumber(q.GetValueOrDefault("pn"), 0, 99) ||
                    !PublicNumber(q.GetValueOrDefault("rn"), 1, 100)) return false;
                return q.GetValueOrDefault("ft") switch
                {
                    "music" => PublicFields(q, ["all", "pn", "rn"],
                        ("rformat", "json"), ("encoding", "utf8"), ("client", "kt"), ("uid", "794762570"),
                        ("ver", "kwplayer_ar_9.2.2.1"), ("vipver", "1"), ("show_copyright_off", "1"),
                        ("newver", "1"), ("ft", "music"), ("cluster", "0"), ("strategy", "2012"),
                        ("vermerge", "1"), ("mobi", "1"), ("issubtitle", "1")),
                    "playlist" => PublicFields(q, ["all", "pn", "rn"],
                        ("rformat", "json"), ("encoding", "utf8"), ("ver", "mbox"),
                        ("vipver", "MUSIC_8.7.7.0_BCS37"), ("plat", "pc"), ("devid", "28156413"),
                        ("ft", "playlist"), ("pay", "0"), ("needliveshow", "0")),
                    _ => false
                };
            }
            if (uri.Scheme == "http" && uri.Host == "nplserver.kuwo.cn" && uri.AbsolutePath == "/pl.svc")
                return PublicNumericId(q.GetValueOrDefault("pid")) && PublicNumber(q.GetValueOrDefault("pn"), 0, 9) &&
                    PublicFields(q, ["pid", "pn"], ("op", "getlistinfo"), ("rn", "1000"), ("encode", "utf8"),
                        ("keyset", "pl2012"), ("identity", "kuwo"), ("pcmp4", "1"),
                        ("vipver", "MUSIC_9.0.5.0_W1"), ("newver", "1"));
            if (uri.Scheme != "https") return false;
            if (uri.Host == "m.kuwo.cn" && uri.AbsolutePath == "/newh5/singles/songinfoandlrc")
                return PublicFields(q, ["musicId"]) && PublicNumericId(q["musicId"]);
            const string prefix = "/play_detail/";
            return uri.Host == "www.kuwo.cn" && uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) &&
                PublicNumericId(uri.AbsolutePath[prefix.Length..]) && uri.Query.Length == 0;
        }

        if (uri.Scheme != "https") return false;
        if (request.Method == HttpMethod.Post)
            return uri.Host == "c.musicapp.migu.cn" && uri.AbsolutePath == "/MIGUM2.0/v1.0/content/resourceinfo.do" &&
                PublicFields(q, [], ("resourceType", "2")) &&
                PublicHeaders(request, PublicMgMobileAgent, "https://m.music.migu.cn/", migu: true) &&
                PublicFormHeaders(request);
        if (uri.Host == "jadeite.migu.cn" && uri.AbsolutePath == "/music_search/v3/search/searchAll")
            return PublicFields(q, ["isCorrect", "pageSize", "text", "pageNo", "searchSwitch"],
                    ("isCopyright", "1"), ("sort", "0"), ("sid", "USS")) &&
                PublicKeyword(q["text"]) && PublicNumber(q["pageSize"], 1, 100) && PublicNumber(q["pageNo"], 1, 100) &&
                (q["isCorrect"] == "0" && q["searchSwitch"] == PublicMgSongs ||
                 q["isCorrect"] == "1" && q["searchSwitch"] == PublicMgPlaylists) &&
                PublicHeaders(request, PublicMgMobileAgent, "https://m.music.migu.cn/", migu: true, search: q["text"]);
        if (!PublicHeaders(request, PublicMgPlaylistAgent, "https://m.music.migu.cn/", migu: true)) return false;
        if (uri.Host == "c.musicapp.migu.cn" && uri.AbsolutePath == "/MIGUM3.0/resource/playlist/v2.0")
            return PublicFields(q, ["playlistId"]) && PublicNumericId(q["playlistId"]);
        return uri.Host == "app.c.nf.migu.cn" && uri.AbsolutePath == "/MIGUM3.0/resource/playlist/song/v2.0" &&
            PublicFields(q, ["playlistId", "pageNo"], ("pageSize", "50")) &&
            PublicNumericId(q["playlistId"]) && PublicNumber(q["pageNo"], 1, 200);
    }

    private static bool PublicFields(Dictionary<string, string> fields, string[] variables,
        params (string Key, string Value)[] fixedValues) =>
        fields.Count == variables.Length + fixedValues.Length && variables.All(fields.ContainsKey) &&
        fixedValues.All(pair => fields.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool PublicNumericId(string? text) =>
        text is { Length: >= 1 and <= 20 } && text[0] is >= '1' and <= '9' &&
        text.All(c => c is >= '0' and <= '9');
    private static bool PublicNativeId(string text) =>
        text.Length is >= 1 and <= 32 && text.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');
    private static bool PublicNumber(string? text, int min, int max) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max &&
        text == value.ToString(CultureInfo.InvariantCulture);
    private static bool PublicKeyword(string? text)
    {
        if (text is not { Length: >= 1 and <= 200 } || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    // Decode values exactly once with strict UTF-8. Canonical ASCII field names,
    // duplicate fields (including encoded aliases), malformed escapes and controls
    // are rejected before any I/O. No generic nested query or JSON capability.
    private static bool PublicQuery(string query, out Dictionary<string, string> values)
    {
        values = new(StringComparer.Ordinal);
        if (query.Length == 0) return true;
        if (query[0] != '?' || query.Length is < 2 or > 65536) return false;
        var segments = query[1..].Split('&');
        if (segments.Length > 20) return false;
        foreach (string segment in segments)
        {
            int equals = segment.IndexOf('=');
            if (equals <= 0 || !segment[..equals].All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') ||
                !PublicUnescape(segment[(equals + 1)..], out var value) ||
                !values.TryAdd(segment[..equals], value)) return false;
        }
        return true;
    }
    private static bool PublicUnescape(string encoded, out string value)
    {
        value = "";
        var bytes = new byte[encoded.Length];
        int count = 0;
        for (int i = 0; i < encoded.Length; i++)
        {
            char c = encoded[i];
            if (c == '%')
            {
                if (i + 2 >= encoded.Length || !byte.TryParse(encoded.AsSpan(i + 1, 2),
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b)) return false;
                bytes[count++] = b; i += 2;
            }
            else
            {
                if (c < 0x21 || c > 0x7e) return false;
                bytes[count++] = (byte)(c == '+' ? ' ' : c);
            }
        }
        try { value = new UTF8Encoding(false, true).GetString(bytes, 0, count); }
        catch (DecoderFallbackException) { return false; }
        return !value.Any(char.IsControl);
    }

    private static bool PublicHeaders(HttpRequestMessage request, string agent, string referer,
        bool migu = false, string? search = null)
    {
        var allowed = new HashSet<string>(["User-Agent", "Referer"], StringComparer.OrdinalIgnoreCase);
        if (migu) allowed.Add("channel");
        if (search is not null) allowed.UnionWith(["uiVersion", "deviceId", "timestamp", "sign"]);
        if (request.Headers.Any(h => !allowed.Contains(h.Key)) || request.Headers.Count() != allowed.Count ||
            !PublicAgent(request, agent) || !HeaderIs(request, "Referer", referer) ||
            migu && !HeaderIs(request, "channel", "0146921")) return false;
        if (search is null) return true;
        if (!HeaderIs(request, "uiVersion", "A_music_3.6.1") || !HeaderIs(request, "deviceId", PublicMgDevice) ||
            !request.Headers.TryGetValues("timestamp", out var timestamps)) return false;
        var time = timestamps.ToArray();
        if (time.Length != 1 || time[0].Length != 13 || time[0][0] is < '1' or > '9' ||
            !time[0].All(c => c is >= '0' and <= '9')) return false;
        // Signature is the fixed anonymous protocol recipe, not authentication.
        var sign = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(search +
            "6cdc72a439cef99a3418d2a78aa28c73yyapp2d16148780a1dcc7408e06336b98cfd50" + PublicMgDevice + time[0])));
        return HeaderIs(request, "timestamp", time[0]) && HeaderIs(request, "sign", sign);
    }

    // HttpClient parses one User-Agent field into separate product/comment values.
    // Compare the complete canonical value, not a fictional one-element sequence.
    private static bool PublicAgent(HttpRequestMessage request, string expected) =>
        request.Headers.TryGetValues("User-Agent", out var values) &&
        string.Join(" ", values) == expected &&
        (request.Content is null || !HasHeader(request.Content.Headers, "User-Agent"));

    private static bool PublicFormHeaders(HttpRequestMessage request)
    {
        var content = request.Content;
        return content is not null &&
            content.Headers.All(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) &&
            content.Headers.TryGetValues("Content-Type", out var types) &&
            types.SequenceEqual(["application/x-www-form-urlencoded"], StringComparer.Ordinal) &&
            content.Headers.ContentLength is >= 1 and <= 65536;
    }

    private static async Task CheckPublicCatalogBody(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method == HttpMethod.Get) { PJ.Require(request.Content is null, "ORIGIN_DENIED"); return; }
        var content = request.Content;
        PJ.Require(content is not null, "INVALID_REQUEST");
        try
        {
            try { await content!.LoadIntoBufferAsync(65536, token).ConfigureAwait(false); }
            catch (HttpRequestException) { throw new MusicPlatformException("LIMIT"); }
            var bytes = await content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                PJ.Require(ReferenceEquals(request.Content, content), "ORIGIN_DENIED");
                PJ.Require(bytes.Length is > 0 and <= 65536, "LIMIT");
                // nativeId is alphanumeric, so the fixed FormUrlEncodedContent
                // recipe has no escapes. Reject batch/extra/encoded identity fields.
                const string prefix = "resourceId=";
                string form = Encoding.ASCII.GetString(bytes);
                PJ.Require(bytes.All(b => b <= 127) && form.StartsWith(prefix, StringComparison.Ordinal) &&
                    PublicNativeId(form[prefix.Length..]) && PublicFormHeaders(request), "INVALID_REQUEST");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            // A trusted producer may swap request.Content while awaiting. Dispose
            // its original object too; neither object can escape request ownership.
            if (!ReferenceEquals(request.Content, content)) content!.Dispose();
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
// Native data-only adaptation of fixed LX/Kuwo/Migu metadata. See LICENSES/NOTICE.md.
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Platforms;

internal static partial class PlatformSongs
{
    internal const string PublicNumericId = @"\A[1-9][0-9]{0,19}\z";
    internal const string MiguNativeId = @"\A[A-Za-z0-9]{1,32}\z";
    private static string PublicText(JsonNode? value) => WebUtility.HtmlDecode(M(value));
    private static JsonNode? Pick(JsonNode? value, params string[] names) =>
        names.Select(n => PJ.At(value, n)).FirstOrDefault(v => v is not null && (v is not JsonValue || PJ.Scalar(v).Length > 0));
    private static string Interval(double seconds)
    {
        PJ.Require(double.IsFinite(seconds) && seconds is >= 0 and <= 31536000, "INVALID_RESPONSE");
        return ((long)seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
            ((long)seconds % 60).ToString("00", CultureInfo.InvariantCulture);
    }
    private static void CopyPublicScalars(JsonNode raw, JsonObject target, params string[] names)
    {
        foreach (var name in names)
        {
            if (PJ.At(raw, name) is not JsonValue value) continue;
            // Only small metadata/locators, never URLs or nested unreviewed response structures.
            var scalar = PJ.Scalar(value);
            PJ.Require(scalar.Length <= 4096, "LIMIT");
            if (scalar.Contains("://", StringComparison.Ordinal) || scalar.StartsWith("//", StringComparison.Ordinal)) continue;
            target[name] = value.DeepClone();
        }
    }
    internal static string KwId(JsonNode raw) =>
        PJ.Id(JsonValue.Create(PJ.Scalar(Pick(raw, "MUSICRID", "id", "rid", "musicrid"))
            .Replace("MUSIC_", "", StringComparison.Ordinal)), PublicNumericId);
    private static string KwCover(JsonNode raw)
    {
        foreach (var name in new[] { "pic", "img" })
        {
            var cover = PJ.Image(PJ.At(raw, name));
            if (cover.Length is 0 or > 4096) continue;
            var uri = new Uri(cover);
            // Do not let a transport-ineligible candidate hide the next cover.
            if (uri.Port is not (80 or 443)) continue;
            var host = uri.Host.TrimEnd('.').ToLowerInvariant();
            // Apply the durable metadata rules before selecting a candidate.
            if (uri.Query.Length == 0 && uri.Fragment.Length == 0 && host is not ("local" or "localhost") &&
                !host.EndsWith(".local", StringComparison.Ordinal) && !host.EndsWith(".localhost", StringComparison.Ordinal))
                return cover;
        }
        var path = PJ.S(raw, "web_albumpic_short");
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) ||
            !Uri.TryCreate(path, UriKind.Relative, out _)) return "";
        var address = PJ.Image(JsonValue.Create("http://img1.kwcdn.kuwo.cn/star/albumcover/" + path));
        if (address.Length is 0 or > 4096) return "";
        // This field is a relative path, never an origin, query or traversal.
        foreach (var segment in path.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#'))
                return "";
        }
        return address;
    }
    internal static JsonObject Kw(JsonNode raw, bool requireQuality = true)
    {
        var id = KwId(raw);
        // If more than one ID is declared, all must identify the same recording.
        foreach (var name in new[] { "MUSICRID", "id", "rid", "musicrid" })
            if (Pick(raw, name) is { } declared)
                PJ.Require(PJ.Scalar(declared) is var s && (s == id || s == "MUSIC_" + id), "IDENTITY_MISMATCH");
        var title = PublicText(Pick(raw, "SONGNAME", "name", "songName"));
        PJ.Require(title.Length > 0, "INVALID_RESPONSE");
        var minfo = PJ.S(raw, "N_MINFO");
        PJ.Require(!requireQuality || minfo.Length > 0, "INVALID_RESPONSE");
        PJ.Require(minfo.Length <= 8192, "LIMIT");
        var formats = PJ.Scalar(Pick(raw, "FORMATS", "formats"));
        var types = new JsonArray(); var byType = new JsonObject();
        foreach (Match match in Regex.Matches(minfo, @"level:(\w+),bitrate:(\d+),format:(\w+),size:([\w.]+)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            string? type = match.Groups[2].Value switch
            { "4000" => "flac24bit", "2000" => "flac", "320" => "320k", "128" => "128k", _ => null };
            if (type is null || byType.ContainsKey(type)) continue;
            var size = match.Groups[4].Value.ToUpperInvariant();
            byType[type] = PJ.Obj(("size", size), ("bitrate", match.Groups[2].Value),
                ("format", match.Groups[3].Value), ("level", match.Groups[1].Value));
            types.Insert(0, PJ.Obj(("type", type), ("size", size)));
        }
        if (minfo.Length == 0)
            foreach (var format in formats.Split('|'))
            {
                string? type = format switch
                { "MP3128" => "128k", "MP3H" => "320k", "ALFLAC" => "flac", "HIRFLAC" => "flac24bit", _ => null };
                if (type is null || byType.ContainsKey(type)) continue;
                byType[type] = PJ.Obj(("size", null), ("format", format));
                types.Add(PJ.Obj(("type", type)));
            }
        var duration = PJ.Number(Pick(raw, "DURATION", "duration"));
        var native = PJ.Obj(("id", id), ("albumId", PJ.Scalar(Pick(raw, "ALBUMID", "albumid", "albumId"))),
            ("interval", Interval(duration)), ("types", types), ("_types", byType),
            ("N_MINFO", minfo), ("qualityKnown", minfo.Length > 0 || formats.Length > 0));
        CopyPublicScalars(raw, native, "MUSICRID", "FORMATS", "formats", "hash", "md5", "PAY", "pay",
            "ONLINE", "online", "hasLossless", "isListenFee");
        return Song("kw", id, title, PublicText(Pick(raw, "ARTIST", "artist")).Replace("&", "、", StringComparison.Ordinal),
            PublicText(Pick(raw, "ALBUM", "album")), duration, KwCover(raw), native);
    }
    internal static JsonObject Mg(JsonNode raw)
    {
        var id = PJ.Id(PJ.At(raw, "songId"), MiguNativeId);
        var copyright = PJ.Id(PJ.At(raw, "copyrightId"), MiguNativeId);
        var title = M(Pick(raw, "songName", "name"));
        PJ.Require(title.Length > 0, "INVALID_RESPONSE");
        var singers = Pick(raw, "singerList", "artists");
        var artist = string.Join("、", PJ.Rows(singers ?? new JsonArray(), 100).Select(s => M(PJ.At(s, "name"))));
        var duration = PJ.Number(PJ.At(raw, "duration"));
        if (duration == 0 && PJ.S(raw, "length") is { Length: > 0 } length)
        {
            PJ.Require(Regex.IsMatch(length, @"^[0-9]{1,3}:[0-5][0-9](:[0-5][0-9])?$", RegexOptions.CultureInvariant), "INVALID_RESPONSE");
            duration = length.Split(':').Aggregate(0d, (n, part) => n * 60 + int.Parse(part, CultureInfo.InvariantCulture));
        }
        var native = PJ.Obj(("id", id), ("songId", id), ("copyrightId", copyright),
            ("albumId", PJ.Scalar(PJ.At(raw, "albumId"))), ("interval", Interval(duration)));
        var types = new JsonArray(); var byType = new JsonObject();
        bool known = false;
        foreach (var key in new[] { "audioFormats", "newRateFormats" })
        {
            if (PJ.At(raw, key) is not { } source) continue;
            known = true;
            var formats = new JsonArray();
            foreach (var row in PJ.Rows(source, 32))
            {
                var meta = new JsonObject();
                CopyPublicScalars(row, meta, "formatType", "resourceType", "size", "androidSize", "iosSize", "asize", "isize",
                    "format", "androidFormat", "iosFormat", "aformat", "iformat", "bitrate", "sampleRate", "bitDepth",
                    "fileType", "androidFileType", "iosFileType", "iosAccuracyLevel", "androidAccuracyLevel",
                    "hash", "md5", "androidMd5", "fileId", "contentId", "price", "showTag", "showTagNew");
                formats.Add(meta);
                string? type = PJ.S(row, "formatType") switch
                { "PQ" => "128k", "HQ" => "320k", "SQ" => "flac", "ZQ" or "ZQ24" => "flac24bit", _ => null };
                if (type is null || byType.ContainsKey(type)) continue;
                long size = Pick(row, "size", "androidSize", "asize", "isize") is { } bytes ? PJ.Count(bytes) : 0;
                var display = size == 0 ? "" : (size / 1048576d).ToString("F2", CultureInfo.InvariantCulture) + " MB";
                byType[type] = PJ.Obj(("size", display), ("sizeBytes", size), ("formatType", PJ.S(row, "formatType")));
                types.Add(PJ.Obj(("type", type), ("size", display)));
            }
            native[key] = formats;
        }
        native["types"] = types; native["_types"] = byType; native["qualityKnown"] = known;
        CopyPublicScalars(raw, native, "contentId", "hash", "md5", "resourceType", "copyrightType", "restrictType");
        var cover = PJ.Scalar(Pick(raw, "img3", "img2", "img1"));
        if (cover.Length == 0 && PJ.At(raw, "albumImgs") is JsonArray images && images.Count > 0)
            cover = PJ.S(images[0], "img");
        if (cover.StartsWith("/", StringComparison.Ordinal) && !cover.StartsWith("//", StringComparison.Ordinal))
            cover = "http://d.musicapp.migu.cn" + cover; // Fixed LX artwork origin; never a media URL.
        return Song("mg", id, title, artist, M(PJ.At(raw, "album")), duration, PJ.Image(JsonValue.Create(cover)), native);
    }
    internal static JsonObject PublicPlaylist(string platform, JsonNode raw, bool detail = false)
    {
        var kw = platform == "kw";
        var id = PJ.Id(Pick(raw, kw ? detail ? "id" : "playlistid" : detail ? "musicListId" : "id"), PublicNumericId);
        var title = PublicText(Pick(raw, detail ? "title" : "name"));
        PJ.Require(title.Length > 0, "INVALID_RESPONSE");
        var cover = kw ? Pick(raw, "pic") : detail ? PJ.At(raw, "imgItem", "img") : PJ.At(raw, "musicListPicUrl");
        return PJ.Obj(("id", id), ("name", title), ("source", platform), ("engine", kw ? "Kuwo" : "Migu"),
            ("cover", PJ.Image(cover)), ("count", PJ.Count(Pick(raw, kw ? detail ? "total" : "songnum" : "musicNum"))),
            ("creator", PublicText(Pick(raw, kw ? detail ? "uname" : "nickname" : detail ? "ownerName" : "userName"))),
            ("description", PublicText(Pick(raw, kw ? detail ? "info" : "intro" : "summary"))));
    }
}

/// <summary>Bounded literal decoders. No JavaScript execution or user-supplied request target.</summary>
internal static class PublicCatalogData
{
    internal static string Utf8(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new MusicPlatformException("INVALID_RESPONSE"); }
    }
    internal static JsonObject KuwoJson(byte[] bytes)
    {
        var text = Utf8(bytes);
        var converted = new StringBuilder(text.Length);
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quote == '\0')
            {
                if (ch is '\'' or '"') { quote = ch; converted.Append('"'); }
                else converted.Append(ch);
                continue;
            }
            if (ch == '\\')
            {
                PJ.Require(++i < text.Length, "INVALID_RESPONSE");
                char next = text[i];
                if (next == '\'' && quote == '\'') converted.Append('\'');
                else converted.Append('\\').Append(next);
            }
            else if (ch == quote)
            {
                int next = i + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                // The fixed public response sometimes includes unescaped apostrophes in names.
                if (quote == '\'' && next < text.Length && !":,}]".Contains(text[next]))
                    converted.Append(ch);
                else { quote = '\0'; converted.Append('"'); }
            }
            else { if (ch == '"') converted.Append('\\'); converted.Append(ch); }
        }
        PJ.Require(quote == '\0', "INVALID_RESPONSE");
        return PJ.Parse(Encoding.UTF8.GetBytes(converted.ToString()));
    }
    internal static JsonObject KuwoPage(byte[] bytes, string id)
    {
        const string prefix = "window.__NUXT__=(function(";
        var html = Utf8(bytes);
        JsonObject? selected = null;
        foreach (Match script in Regex.Matches(html, @"<script(?:\s[^>]*)?>(.*?)</script>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
        {
            var text = script.Groups[1].Value.Trim();
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            PJ.Require(selected is null && text.EndsWith("));", StringComparison.Ordinal), "INVALID_RESPONSE");
            var body = text[prefix.Length..^3];
            var head = body.IndexOf("){return ", StringComparison.Ordinal);
            var tail = body.LastIndexOf("}(", StringComparison.Ordinal);
            PJ.Require(head >= 0 && tail > head, "INVALID_RESPONSE");
            string[] names = body[..head].Length == 0 ? [] : body[..head].Split(',');
            var args = PJ.Parse(Encoding.UTF8.GetBytes("{\"args\":[" + body[(tail + 2)..] + "]}"))["args"]!.AsArray();
            PJ.Require(names.Length == args.Count && names.Length <= 64, "INVALID_RESPONSE");
            var aliases = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++)
                PJ.Require(LiteralReader.Identifier(names[i]) && (args[i] is null or JsonValue) &&
                    aliases.TryAdd(names[i], args[i]), "INVALID_RESPONSE");
            // Parse the WHOLE returned object, not just a substring containing songinfo.
            var root = new LiteralReader(body[(head + 9)..tail], aliases).Read();
            PJ.Require(PJ.At(root, "data") is JsonArray data && data.Count > 0, "INVALID_RESPONSE");
            var raw = PJ.At(((JsonArray)PJ.At(root, "data")!)[0], "songinfo");
            PJ.Require(raw is JsonObject, "INVALID_RESPONSE");
            selected = (JsonObject)raw!.DeepClone();
            PJ.Require(PJ.Scalar(selected["rid"]) == id && PJ.Scalar(selected["musicrid"]) == "MUSIC_" + id &&
                PJ.S(selected, "name").Length > 0, "IDENTITY_MISMATCH");
        }
        return selected ?? throw new MusicPlatformException("INVALID_RESPONSE");
    }
    private sealed class LiteralReader(string text, Dictionary<string, JsonNode?> aliases)
    {
        private int at, nodes;
        internal static bool Identifier(string name) => Regex.IsMatch(name, @"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant);
        private void White() { while (at < text.Length && char.IsWhiteSpace(text[at])) at++; }
        private bool Take(char ch)
        {
            White();
            if (at == text.Length || text[at] != ch) return false;
            at++; return true;
        }
        private void Need(char ch) => PJ.Require(Take(ch), "INVALID_RESPONSE");
        internal JsonNode? Read()
        {
            var value = Value(0);
            White(); PJ.Require(at == text.Length, "INVALID_RESPONSE");
            return value;
        }
        private string String()
        {
            White(); int start = at;
            Need('"');
            bool escaped = false, ended = false;
            while (at < text.Length)
            {
                char c = text[at++];
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') { ended = true; break; }
            }
            PJ.Require(ended && at - start <= 50002, "INVALID_RESPONSE");
            try { return JsonSerializer.Deserialize<string>(text[start..at])!; }
            catch (JsonException) { throw new MusicPlatformException("INVALID_RESPONSE"); }
        }
        private string Word()
        {
            White(); int start = at;
            while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] is '_' or '$')) at++;
            PJ.Require(at > start, "INVALID_RESPONSE");
            return text[start..at];
        }
        private JsonNode? Value(int depth)
        {
            PJ.Require(++nodes <= 100000 && depth <= 32, "LIMIT");
            White(); PJ.Require(at < text.Length, "INVALID_RESPONSE");
            if (Take('{'))
            {
                var obj = new JsonObject();
                if (Take('}')) return obj;
                do
                {
                    White();
                    string name = at < text.Length && text[at] == '"' ? String() : Word();
                    PJ.Require(!obj.ContainsKey(name), "INVALID_RESPONSE");
                    Need(':'); obj[name] = Value(depth + 1);
                    if (Take('}')) return obj;
                } while (Take(','));
                throw new MusicPlatformException("INVALID_RESPONSE");
            }
            if (Take('['))
            {
                var array = new JsonArray();
                if (Take(']')) return array;
                do
                {
                    array.Add(Value(depth + 1));
                    if (Take(']')) return array;
                } while (Take(','));
                throw new MusicPlatformException("INVALID_RESPONSE");
            }
            if (text[at] == '"') return JsonValue.Create(String());
            if (text[at] == '-' || char.IsAsciiDigit(text[at]))
            {
                int start = at++;
                while (at < text.Length && "0123456789.eE+-".Contains(text[at])) at++;
                try
                {
                    var number = JsonNode.Parse(text[start..at]);
                    PJ.Require(number is JsonValue, "INVALID_RESPONSE"); return number;
                }
                catch (JsonException) { throw new MusicPlatformException("INVALID_RESPONSE"); }
            }
            var word = Word();
            if (word == "null") return null;
            if (word is "true" or "false") return JsonValue.Create(word == "true");
            PJ.Require(aliases.TryGetValue(word, out var alias), "INVALID_RESPONSE");
            return alias?.DeepClone();
        }
    }
}

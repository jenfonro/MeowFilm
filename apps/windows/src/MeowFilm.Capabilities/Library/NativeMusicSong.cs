// Native translation of this branch's music-core song-identity/native-data/library contracts.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

internal static class NativeMusicSong
{
    internal static readonly string[] Platforms = ["wy", "tx", "kw", "kg", "mg"];
    private const string SongFields = "songId title artist album duration cover_url source_data sources quarantinedSources revision nextCheckAt stableCount sourcesCheckedAt sourcesRetryAfter partial libraryIds memberLibraryIds";
    private const string RequiredSongFields = "songId title artist album duration cover_url source_data sources quarantinedSources";
    private const string SourceFields = "sourceKey source source_data title artist album duration cover_url observedAt nextCheckAt revision";
    private const string RequiredSourceFields = "sourceKey source source_data title artist album duration cover_url";
    private const string LocatorFields = "musicId songmid mid id songId qqSongId copyrightId mediaMid strMediaMid albumId album_id albumMid albumAudioId mixsongid fileid fileId contentId hash hash128 hash320 hashflac md5 androidMd5";
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const string JsWhitespace = @"[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
    private static string Pattern(string p) => p.Replace(@"\s", JsWhitespace, StringComparison.Ordinal);
    private static string Replace(string s, string p, string r) => Regex.Replace(s, Pattern(p), r, RegexOptions.CultureInvariant, RegexTimeout);
    private static bool Match(string s, string p) => Regex.IsMatch(s, p, RegexOptions.CultureInvariant, RegexTimeout);
    internal static string NormalText(string s) => Replace(Replace(Lower(s.Normalize(NormalizationForm.FormKC)), "[\u200b-\u200d\ufeff]", ""), @"\s+", " ").Trim(' ');
    private static string Lower(string value)
    {
        // JavaScript uses Unicode default (not locale-sensitive) full lowercase.
        // .NET's invariant simple mapping misses dotted-I and contextual final sigma.
        var runes = value.EnumerateRunes().ToArray(); var result = new StringBuilder();
        static bool Cased(Rune r) => Rune.ToUpperInvariant(r) != Rune.ToLowerInvariant(r);
        static bool Ignorable(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol ||
            r.Value is 0x27 or 0x3a or 0xb7 or 0x387 or 0x5f4 or 0x2019 or 0x2027 or 0xff07 or 0xff1a;
        for (int i = 0; i < runes.Length; i++)
        {
            if (runes[i].Value == 0x130) { result.Append("i\u0307"); continue; }
            if (runes[i].Value == 0x3a3)
            {
                int before = i - 1, after = i + 1;
                while (before >= 0 && Ignorable(runes[before])) before--;
                while (after < runes.Length && Ignorable(runes[after])) after++;
                if (before >= 0 && Cased(runes[before]) && (after == runes.Length || !Cased(runes[after])))
                { result.Append('\u03c2'); continue; }
            }
            result.Append(Rune.ToLowerInvariant(runes[i]));
        }
        return result.ToString();
    }
    internal static string Hash(string s)
    {
        uint a = 2166136261, b = 3339675911;
        foreach (char c in s) { a = unchecked((a ^ c) * 16777619); b = unchecked((b ^ c) * 2246822519); }
        return a.ToString("x8", CultureInfo.InvariantCulture) + b.ToString("x8", CultureInfo.InvariantCulture);
    }
    private static string First(params JsonNode?[] nodes) => nodes.Select(LJ.Scalar).FirstOrDefault(s => s.Length > 0 && s != "0" && s != "false") ?? "";
    internal static JsonObject SourceData(JsonObject song) =>
        LJ.Object(song["source_data"], "INVALID_SONG");
    internal static string NativeId(JsonObject song)
    {
        var data = SourceData(song);
        return LJ.Scalar(data["musicId"] ?? data["songmid"] ?? data["id"]);
    }
    internal static string SourceKey(JsonObject song)
    {
        var sd = SourceData(song); string platform = LJ.S(sd["source"]);
        string id = NativeId(song);
        if (!Platforms.Contains(platform) || id.Length == 0) return "";
        return LJ.Stringify(new JsonArray(platform, id, platform == "kg" ? First(JsonValue.Create(NormalText(LJ.S(sd["hash"]))), sd["albumAudioId"]) : ""));
    }
    internal static JsonObject Descriptor(JsonObject song)
    {
        var sd = SourceData(song);
        string name = NormalText(LJ.S(song["title"]));
        string artist = string.Join(" & ", Regex.Split(NormalText(LJ.S(song["artist"])),
            Pattern(@"\s*(?:、|;|\s/\s|\s&\s|\sfeat\.?\s|\sfeaturing\s)\s*"), RegexOptions.CultureInvariant, RegexTimeout).Where(s => s.Length > 0).Order(StringComparer.Ordinal));
        string[] patterns = ["和声伴奏|和聲伴奏", "伴奏|instrumental|karaoke", @"(?<![a-z0-9_])live(?![a-z0-9_])|现场|現場",
            @"remix|(?<![a-z0-9_])dj(?![a-z0-9_])", "remaster|重制|重製", "acoustic|不插电|不插電", @"翻唱|(?<![a-z0-9_])cover(?![a-z0-9_])"];
        string[] tags = ["harmony", "instrumental", "live", "remix", "remaster", "acoustic", "cover"];
        var versions = tags.Where((_, i) => Match(name, patterns[i]));
        string title = Regex.Replace(name, Pattern(@"\s*[\(（]([^()（）]+)[\)）]\s*$"), m =>
            NormalText(m.Groups[1].Value) == Regex.Split(name, "[（(]", RegexOptions.None, RegexTimeout)[0].Trim(' ') ? "" : m.Value, RegexOptions.CultureInvariant, RegexTimeout);
        title = Replace(Replace(title, @"\s*\(\s*", "("), @"\s*\)\s*", ")").Trim(' ');
        return LJ.Obj(("title", title), ("artist", artist), ("version", string.Join(',', versions)), ("duration", LJ.N(song["duration"])));
    }
    internal static string Fingerprint(JsonObject song)
    {
        var d = Descriptor(song); string title = LJ.S(d["title"]), artist = LJ.S(d["artist"]);
        return title.Length == 0 || artist.Length == 0 || Match(title, "^(未知|unknown)") || Match(artist, "^(未知|unknown)") ? "" :
            LJ.Stringify(new JsonArray(title, artist, LJ.S(d["version"])));
    }
    internal static bool Compatible(JsonObject a, JsonObject b)
    {
        string ka = SourceKey(a), kb = SourceKey(b); var da = Descriptor(a); var db = Descriptor(b);
        double x = LJ.N(da["duration"]), y = LJ.N(db["duration"]);
        bool durations = !(x > 0 && y > 0) || Math.Abs(x - y) <= Math.Max(3, Math.Min(x, y) * .02);
        bool Known(string s) => s.Length > 0 && !Match(s, "^(未知|unknown)");
        if (ka.Length > 0 && ka == kb)
            return (!Known(LJ.S(da["artist"])) || !Known(LJ.S(db["artist"])) || LJ.S(da["artist"]) == LJ.S(db["artist"])) &&
                (!Known(LJ.S(da["title"])) || !Known(LJ.S(db["title"])) || LJ.S(da["version"]) == LJ.S(db["version"])) && durations;
        return Fingerprint(a).Length > 0 && Fingerprint(a) == Fingerprint(b) && x > 0 && y > 0 && durations;
    }
    internal static bool Same(JsonObject a, JsonObject b)
    {
        HashSet<string> Keys(JsonObject s) => LJ.Array(s["sources"], "INVALID_SONG").OfType<JsonObject>()
            .Select(n => LJ.S(n["sourceKey"])).Append(SourceKey(s)).Where(value => value.Length > 0).ToHashSet();
        var ka = Keys(a); var kb = Keys(b);
        return ka.Overlaps(kb) || LJ.S(a["songId"]) == LJ.S(b["songId"]) && (ka.Count == 0 || kb.Count == 0 || Compatible(a, b));
    }
    private static void ValidateLocator(JsonObject value)
    {
        foreach (string key in LocatorFields.Split(' '))
            if (value[key] is JsonValue v && !v.TryGetValue<string>(out _)) _ = LJ.I(value[key], -LJ.MaxInteger);
    }
    private static string PublicUrl(JsonNode? n)
    {
        string s = LJ.S(n);
        return s.Length <= 4096 && !s.Contains('?') && !s.Contains('#') &&
            Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 &&
            uri.Host.TrimEnd('.').ToLowerInvariant() is var h && h is not ("local" or "localhost") && !h.EndsWith(".local", StringComparison.Ordinal) &&
            !h.EndsWith(".localhost", StringComparison.Ordinal) ? s : "";
    }
    private static bool Scalar(JsonNode? n, int max = 4096) => n is JsonValue v &&
        (v.TryGetValue<string>(out var s) ? s.Length <= max : double.IsFinite(LJ.N(n, double.NaN)));
    private static bool Resource(JsonNode? n) => n is JsonValue v && (v.TryGetValue<string>(out var s) ? Match(s, "^[a-zA-Z0-9_. -]{1,128}$") : LJ.N(n, -1) >= 0);
    private static JsonObject Quality(JsonNode? n)
    {
        var r = new JsonObject();
        if (n is not JsonObject o) return r;
        if (o["hash"] is JsonValue h && h.TryGetValue<string>(out var s) && s.Length <= 128) r["hash"] = s;
        if (Scalar(o["size"], 128)) r["size"] = o["size"]?.DeepClone();
        return r;
    }
    private static JsonObject CleanNative(JsonObject sd)
    {
        ValidateLocator(sd); var r = new JsonObject();
        foreach (string key in ("source musicId songmid mid id songId qqSongId songType type copyrightId mediaMid strMediaMid hash hash128 hash320 hashflac " +
            "albumId album_id albumMid albumAudioId mixsongid fileid fileId name singer album albumName duration size interval catalogSongId sourceSongId").Split(' '))
            if (Scalar(sd[key])) r[key] = sd[key]?.DeepClone();
        if (PublicUrl(sd["img"]) is { Length: > 0 } img) r["img"] = img;
        if (LJ.S(r["source"]) == "mg")
        {
            foreach (string key in new[] { "contentId", "md5" }) if (Resource(sd[key])) r[key] = sd[key]?.DeepClone();
            foreach (string key in new[] { "audioFormats", "newRateFormats" })
                if (sd[key] is JsonArray formats)
                {
                    var list = new JsonArray();
                    foreach (var f in formats.OfType<JsonObject>())
                    {
                        ValidateLocator(f); var row = new JsonObject();
                        foreach (string field in ("formatType resourceType size androidSize iosSize asize isize format androidFormat iosFormat aformat iformat bitrate sampleRate bitDepth " +
                            "fileType androidFileType iosFileType iosAccuracyLevel androidAccuracyLevel hash md5 androidMd5 fileId contentId").Split(' '))
                            if (Resource(f[field])) row[field] = f[field]?.DeepClone();
                        if (row.Count > 0) list.Add(row);
                        if (list.Count == 32) break;
                    }
                    r[key] = list;
                }
            foreach (string key in new[] { "lrcUrl", "mrcUrl", "trcUrl" }) if (PublicUrl(sd[key]) is { Length: > 0 } url) r[key] = url;
        }
        string[] qualities = ["128k", "192k", "320k", "flac", "flac24bit", "master"];
        if (LJ.IsBool(sd["qualityKnown"])) r["qualityKnown"] = sd["qualityKnown"]?.DeepClone();
        if (sd.ContainsKey("types")) r["types"] = LJ.Arr(LJ.Array(sd["types"], "INVALID_NATIVE_TYPES").OfType<JsonObject>()
            .Where(o => qualities.Contains(LJ.S(o["type"]))).Take(16).Select(o => LJ.Merge(LJ.Obj(("type", LJ.S(o["type"]))), Quality(o))));
        if (sd.ContainsKey("_types"))
        {
            var types = LJ.Object(sd["_types"], "INVALID_NATIVE_TYPES");
            r["_types"] = new JsonObject(types.Where(p => qualities.Contains(p.Key)).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)Quality(p.Value))));
        }
        return r;
    }
    internal static JsonNode? Durable(JsonNode? n, string field = "")
    {
        if (n is JsonArray a) return new JsonArray(a.Select(x => Durable(x)).ToArray());
        if (n is JsonObject o)
            return new JsonObject(o.Where(p => !Match(p.Key, "(?i)cookie|token|password|secret|credential|authorization|api[-_]?key|^headers$") &&
                !Match(p.Key, "^(?i:url|audioUrl|playUrl|currentTime|sessionTime|resumeTime|seekTime|progress|activeSource|activeSourceKey|activeQuality|actualLevel|qualityVerified|qualityUnrecognized|lxScriptId|lxScriptName|lxScriptVersion|recommendation|fm|fmManual|reportInstance|reportScope)$"))
                .Select(p => KeyValuePair.Create(p.Key, Durable(p.Value, p.Key))));
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (Match(s, "^(?i:blob:|data:)") || Match(s, "^(?i:https?://|/)") && Match(s, "[?&](?i:access_token|token|signature|sign|auth|expires|x-amz-[^=]*|x-goog-[^=]*)=")) return JsonValue.Create("");
        }
        return n?.DeepClone();
    }
    internal static JsonObject DurableSource(JsonNode? input)
    {
        var source = LJ.Object(input, "INVALID_SOURCE");
        LJ.Fields(source, SourceFields, RequiredSourceFields);
        foreach (string field in new[] { "sourceKey", "source", "title", "artist", "album", "cover_url" })
            LJ.Require(source[field] is JsonValue value && value.TryGetValue<string>(out _), "INVALID_SOURCE");
        LJ.Require(Platforms.Contains(LJ.S(source["source"])), "INVALID_SOURCE");
        LJ.Require(source["duration"] is JsonValue && double.IsFinite(LJ.N(source["duration"], double.NaN)) &&
            LJ.N(source["duration"]) >= 0, "INVALID_SOURCE");
        foreach (string field in new[] { "observedAt", "nextCheckAt", "revision" })
            if (source.ContainsKey(field)) _ = LJ.I(source[field]);
        var data = LJ.Object(source["source_data"], "INVALID_SOURCE");
        var cleanedData = CleanNative(data);
        LJ.Require(JsonNode.DeepEquals(data, cleanedData), "INVALID_SOURCE");
        var clean = (JsonObject)source.DeepClone();
        LJ.Require(LJ.S(clean["cover_url"]) == PublicUrl(clean["cover_url"]), "INVALID_SOURCE");
        LJ.Require(LJ.S(clean["source"]) == LJ.S(cleanedData["source"]) &&
            LJ.S(clean["sourceKey"]) == SourceKey(clean), "INVALID_SOURCE");
        return clean;
    }
    internal static bool IsOnline(JsonObject song) => SourceKey(song).Length > 0;
    internal static JsonObject CreateOnline(string platform, JsonObject native, string title, string artist = "",
        string album = "", double duration = 0, string cover = "")
    {
        LJ.Require(Platforms.Contains(platform) && double.IsFinite(duration) && duration >= 0, "INVALID_SONG");
        var sourceData = (JsonObject)native.DeepClone();
        sourceData["source"] = platform;
        string nativeId = First(sourceData["musicId"], sourceData["songmid"], sourceData["id"]);
        LJ.Require(LJ.Id(nativeId, 512), "INVALID_SONG");
        sourceData["musicId"] = nativeId;
        sourceData["songmid"] = nativeId;
        sourceData = CleanNative(sourceData);
        title = title.Length == 0 ? "未知歌曲" : title;
        artist = artist.Length == 0 ? "未知歌手" : artist;
        cover = PublicUrl(JsonValue.Create(cover));
        var song = LJ.Obj(("songId", ""), ("title", title), ("artist", artist), ("album", album),
            ("duration", duration), ("cover_url", cover), ("source_data", sourceData),
            ("sources", new JsonArray()), ("quarantinedSources", new JsonArray()));
        string key = SourceKey(song), fingerprint = Fingerprint(song);
        LJ.Require(key.Length > 0, "INVALID_SONG");
        song["songId"] = "song:" + Hash(fingerprint.Length > 0 && duration > 0 ? fingerprint : key);
        song["sources"] = LJ.Arr([
            LJ.Obj(("sourceKey", key), ("source", platform), ("source_data", sourceData),
                ("title", title), ("artist", artist), ("album", album), ("duration", duration), ("cover_url", cover))
        ]);
        return Normalize(song);
    }
    internal static JsonObject CreateLocal(string songId, string title, string artist = "", string album = "",
        double duration = 0, string cover = "") => Normalize(LJ.Obj(
            ("songId", songId), ("title", title), ("artist", artist), ("album", album),
            ("duration", duration), ("cover_url", cover), ("source_data", new JsonObject()),
            ("sources", new JsonArray()), ("quarantinedSources", new JsonArray())));
    internal static JsonObject Normalize(JsonNode? input)
    {
        var raw = LJ.Object(input, "INVALID_SONG");
        LJ.Fields(raw, SongFields, RequiredSongFields);
        LJ.Require(LJ.Id(LJ.S(raw["songId"])), "INVALID_SONG_ID");
        foreach (string field in new[] { "title", "artist", "album", "cover_url" })
            LJ.Require(raw[field] is JsonValue value && value.TryGetValue<string>(out _), "INVALID_SONG");
        LJ.Require(raw["duration"] is JsonValue && double.IsFinite(LJ.N(raw["duration"], double.NaN)) &&
            LJ.N(raw["duration"]) >= 0, "INVALID_SONG");
        LJ.Require(LJ.S(raw["cover_url"]) == PublicUrl(raw["cover_url"]), "INVALID_SONG");
        foreach (string field in new[] { "revision", "nextCheckAt", "stableCount", "sourcesCheckedAt", "sourcesRetryAfter" })
            if (raw.ContainsKey(field)) _ = LJ.I(raw[field]);
        if (raw.ContainsKey("partial")) LJ.Require(LJ.IsBool(raw["partial"]), "INVALID_SONG");
        foreach (string field in new[] { "libraryIds", "memberLibraryIds" })
            if (raw.ContainsKey(field))
            {
                var ids = LJ.Array(raw[field], "INVALID_SONG");
                LJ.Require(ids.All(value => value is JsonValue scalar && scalar.TryGetValue<string>(out var id) && LJ.Id(id)) &&
                    ids.Select(value => LJ.S(value)).Distinct(StringComparer.Ordinal).Count() == ids.Count, "INVALID_SONG");
            }
        var data = SourceData(raw);
        var cleanData = CleanNative(data);
        LJ.Require(JsonNode.DeepEquals(data, cleanData), "INVALID_SONG");
        var sources = LJ.Array(raw["sources"], "INVALID_SOURCE").Select(DurableSource)
            .OrderBy(source => LJ.S(source["sourceKey"]), StringComparer.Ordinal).ToArray();
        var quarantined = LJ.Array(raw["quarantinedSources"], "INVALID_SOURCE").Select(DurableSource)
            .OrderBy(source => LJ.S(source["sourceKey"]), StringComparer.Ordinal).ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        LJ.Require(sources.Concat(quarantined).All(source => keys.Add(LJ.S(source["sourceKey"]))), "INVALID_SOURCE");
        string primaryKey = SourceKey(raw);
        if (primaryKey.Length == 0)
            LJ.Require(data.Count == 0 && sources.Length == 0 && quarantined.Length == 0, "INVALID_SONG");
        else
        {
            LJ.Require(sources.Length > 0 && sources.All(source => Compatible(raw, source)) &&
                sources.All(source => sources.All(other => Compatible(source, other))), "RECORDING_CONFLICT");
            var primary = sources.Where(source => LJ.S(source["sourceKey"]) == primaryKey).ToArray();
            LJ.Require(primary.Length == 1 && LJ.S(primary[0]["source"]) == LJ.S(data["source"]) &&
                JsonNode.DeepEquals(primary[0]["source_data"], data) &&
                new[] { "title", "artist", "album", "duration", "cover_url" }
                    .All(field => JsonNode.DeepEquals(primary[0][field], raw[field])), "INVALID_SONG");
        }
        var result = (JsonObject)raw.DeepClone();
        result["sources"] = LJ.Arr(sources);
        result["quarantinedSources"] = LJ.Arr(quarantined);
        return result;
    }
    internal static JsonObject View(JsonNode? song) => Normalize(song);
}

// Native translation of this branch's music-core song-identity/native-data/library contracts.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

internal static partial class NativeMusicSong
{
    internal static readonly string[] Platforms = ["wy", "tx", "kw", "kg", "mg"];
    // Lean unified song: display fields plus one record per platform recording.
    // sources[0] is the primary recording; the rest are ordered by sourceKey.
    private const string SongFields = "songId title artist album duration cover_url sources";
    private const string SourceFields = "sourceKey source musicInfo";
    private const string LocatorFields = "songmid mid id songId qqSongId copyrightId strMediaMid albumId album_id albumMid albumAudioId mixsongid fileid fileId contentId hash md5 androidMd5";
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
    private static bool IsSource(JsonObject value) => value.ContainsKey("musicInfo");
    /// <summary>Primary recording of a song, or null for a local song.</summary>
    internal static JsonObject? Primary(JsonObject song) =>
        LJ.Array(song["sources"], "INVALID_SONG").FirstOrDefault() as JsonObject;
    /// <summary>musicInfo of a source record, or of the primary recording of a song.</summary>
    internal static JsonObject SourceData(JsonObject value) => IsSource(value)
        ? LJ.Object(value["musicInfo"], "INVALID_SOURCE")
        : LJ.Object(Primary(value)?["musicInfo"], "INVALID_SONG");
    internal static string InfoId(JsonObject info) => LJ.Scalar(info["songmid"] ?? info["id"]);
    internal static string NativeId(JsonObject value) => InfoId(SourceData(value));
    internal static string InfoKey(JsonObject info)
    {
        string platform = LJ.S(info["source"]), id = InfoId(info);
        if (!Platforms.Contains(platform) || id.Length == 0) return "";
        return LJ.Stringify(new JsonArray(platform, id, platform == "kg" ? First(JsonValue.Create(NormalText(LJ.S(info["hash"]))), info["albumAudioId"]) : ""));
    }
    /// <summary>Key of a source record, or of the primary recording of a song ("" for local).</summary>
    internal static string SourceKey(JsonObject value) => IsSource(value)
        ? InfoKey(SourceData(value))
        : Primary(value) is { } primary ? InfoKey(SourceData(primary)) : "";
    /// <summary>Per-recording display metadata, read from its musicInfo.</summary>
    internal static (string Title, string Artist, string Album, double Duration, string Cover) Display(JsonObject value)
    {
        if (!IsSource(value))
            return (LJ.S(value["title"]), LJ.S(value["artist"]), LJ.S(value["album"]), LJ.N(value["duration"]), LJ.S(value["cover_url"]));
        var info = SourceData(value);
        string title = LJ.S(info["name"]), artist = LJ.S(info["singer"]);
        return (title.Length == 0 ? "未知歌曲" : title, artist.Length == 0 ? "未知歌手" : artist,
            LJ.S(info["albumName"]), LJ.N(info["duration"]), LJ.S(info["img"]));
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
        foreach (string key in ("source songmid mid id songId qqSongId songType type copyrightId strMediaMid hash " +
            "albumId album_id albumMid albumAudioId mixsongid fileid fileId name singer albumName duration size").Split(' '))
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
        LJ.Fields(source, SourceFields, SourceFields);
        foreach (string field in new[] { "sourceKey", "source" })
            LJ.Require(source[field] is JsonValue value && value.TryGetValue<string>(out _), "INVALID_SOURCE");
        LJ.Require(Platforms.Contains(LJ.S(source["source"])), "INVALID_SOURCE");
        var info = LJ.Object(source["musicInfo"], "INVALID_SOURCE");
        LJ.Require(JsonNode.DeepEquals(info, CleanNative(info)), "INVALID_SOURCE");
        LJ.Require(info["duration"] is null || LJ.N(info["duration"], -1) >= 0, "INVALID_SOURCE");
        LJ.Require(LJ.S(source["source"]) == LJ.S(info["source"]) &&
            LJ.S(source["sourceKey"]) == InfoKey(info) && LJ.Id(LJ.S(source["sourceKey"])), "INVALID_SOURCE");
        return (JsonObject)source.DeepClone();
    }
    internal static bool IsOnline(JsonObject song) => Primary(song) is not null;
    /// <summary>One canonical recording record. Its musicInfo carries the recording's own display metadata.</summary>
    internal static JsonObject CreateSource(string platform, JsonObject native, string title, string artist = "",
        string album = "", double duration = 0, string cover = "")
    {
        LJ.Require(Platforms.Contains(platform) && double.IsFinite(duration) && duration >= 0, "INVALID_SONG");
        var info = (JsonObject)native.DeepClone();
        info["source"] = platform;
        string nativeId = First(info["musicId"], info["songmid"], info["id"]);
        LJ.Require(LJ.Id(nativeId, 512), "INVALID_SONG");
        info["songmid"] = nativeId;
        info["name"] = title; info["singer"] = artist; info["albumName"] = album; info["duration"] = duration;
        if (PublicUrl(JsonValue.Create(cover)) is { Length: > 0 } image) info["img"] = image; else info.Remove("img");
        info = CleanNative(info);
        string key = InfoKey(info);
        LJ.Require(key.Length > 0, "INVALID_SONG");
        return LJ.Obj(("sourceKey", key), ("source", platform), ("musicInfo", info));
    }
    internal static JsonObject CreateOnline(string platform, JsonObject native, string title, string artist = "",
        string album = "", double duration = 0, string cover = "")
    {
        var source = CreateSource(platform, native, title, artist, album, duration, cover);
        title = title.Length == 0 ? "未知歌曲" : title;
        artist = artist.Length == 0 ? "未知歌手" : artist;
        cover = PublicUrl(JsonValue.Create(cover));
        var song = LJ.Obj(("songId", ""), ("title", title), ("artist", artist), ("album", album),
            ("duration", duration), ("cover_url", cover), ("sources", LJ.Arr([source])));
        string key = LJ.S(source["sourceKey"]), fingerprint = Fingerprint(song);
        song["songId"] = "song:" + Hash(fingerprint.Length > 0 && duration > 0 ? fingerprint : key);
        return Normalize(song);
    }
    internal static JsonObject CreateLocal(string songId, string title, string artist = "", string album = "",
        double duration = 0, string cover = "") => Normalize(LJ.Obj(
            ("songId", songId), ("title", title), ("artist", artist), ("album", album),
            ("duration", duration), ("cover_url", cover), ("sources", new JsonArray())));
    internal static JsonObject Normalize(JsonNode? input)
    {
        var raw = LJ.Object(input, "INVALID_SONG");
        LJ.Fields(raw, SongFields, SongFields);
        LJ.Require(LJ.Id(LJ.S(raw["songId"])), "INVALID_SONG_ID");
        foreach (string field in new[] { "title", "artist", "album", "cover_url" })
            LJ.Require(raw[field] is JsonValue value && value.TryGetValue<string>(out _), "INVALID_SONG");
        LJ.Require(raw["duration"] is JsonValue && double.IsFinite(LJ.N(raw["duration"], double.NaN)) &&
            LJ.N(raw["duration"]) >= 0, "INVALID_SONG");
        LJ.Require(LJ.S(raw["cover_url"]) == PublicUrl(raw["cover_url"]), "INVALID_SONG");
        var listed = LJ.Array(raw["sources"], "INVALID_SOURCE").Select(DurableSource).ToArray();
        return new Recordings(raw, listed).ToSong();
    }
    internal static JsonObject View(JsonNode? song) => Normalize(song);
}

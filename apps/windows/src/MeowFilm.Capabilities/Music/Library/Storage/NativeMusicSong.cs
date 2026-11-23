using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

/// <summary>Native projections of the shared song result. Permanent identity is assigned by the repository.</summary>
internal static partial class NativeMusicSong
{
    internal static readonly string[] Platforms = ["wy", "tx", "kw", "kg", "mg"];
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const string Whitespace = @"[\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+";
    internal static string NormalText(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var english = new string(normalized.Select(ch => ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch).ToArray());
        return Regex.Replace(english, Whitespace, " ", RegexOptions.CultureInvariant, RegexTimeout).Trim(' ');
    }
    // Cache/observation keys only. This function never assigns a song ID.
    internal static string Hash(string value)
    {
        uint a = 2166136261, b = 3339675911;
        foreach (char ch in value) { a = unchecked((a ^ ch) * 16777619); b = unchecked((b ^ ch) * 2246822519); }
        return a.ToString("x8", CultureInfo.InvariantCulture) + b.ToString("x8", CultureInfo.InvariantCulture);
    }
    internal static double? ValidDuration(JsonNode? value)
    {
        double duration = LJ.N(value, double.NaN);
        return double.IsFinite(duration) && duration > 0 ? duration : null;
    }
    internal static JsonObject? Primary(JsonObject song) => (song["sources"] as JsonArray)?.FirstOrDefault() as JsonObject;
    internal static JsonObject SourceData(JsonObject value) => LJ.Object(value.ContainsKey("musicInfo")
        ? value["musicInfo"] : Primary(value)?["musicInfo"], "INVALID_SOURCE");
    internal static string InfoId(JsonObject info) => LJ.Scalar(info["songmid"]);
    internal static string NativeId(JsonObject value) => InfoId(SourceData(value));
    internal static string InfoKey(JsonObject info)
    {
        string platform = LJ.S(info["source"]), id = InfoId(info);
        if (id.Length == 0) return "";
        string variant = platform == "kg" ? LJ.Scalar(info["hash"] ?? info["albumAudioId"]).ToLowerInvariant() : "";
        return LJ.Stringify(new JsonArray(platform, id, variant));
    }
    internal static string SourceKey(JsonObject value) => value.ContainsKey("musicInfo")
        ? LJ.S(value["sourceKey"]) : Primary(value) is { } primary ? LJ.S(primary["sourceKey"]) : "";
    internal static (string Title, string Artist, string Album, double Duration, string Cover) Display(JsonObject value)
    {
        if (!value.ContainsKey("musicInfo"))
            return (LJ.S(value["title"]), LJ.S(value["artist"]), LJ.S(value["album"]), LJ.N(value["duration"]), LJ.S(value["cover_url"]));
        var info = SourceData(value);
        return (LJ.S(info["name"]), LJ.S(info["singer"]), LJ.S(info["albumName"]), LJ.N(info["duration"]), LJ.S(info["img"]));
    }
    internal static string PublicUrl(JsonNode? value)
    {
        string text = LJ.S(value);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            uri.UserInfo.Length == 0 ? uri.AbsoluteUri : "";
    }
    private static readonly string[] SourceInfoTextFields =
    [
        "source", "hash", "name", "singer", "albumName", "md5"
    ];
    private static readonly string[] SourceInfoIdentifierFields =
    [
        "songmid", "mid", "id", "songId", "qqSongId", "songType", "type", "copyrightId", "strMediaMid",
        "albumId", "album_id", "albumMid", "albumAudioId", "mixsongid", "fileid", "fileId", "size", "contentId"
    ];
    private static readonly string[] FormatTextFields = ["hash", "md5", "androidMd5"];
    private static readonly string[] FormatIdentifierFields =
    [
        "formatType", "resourceType", "size", "androidSize", "iosSize", "asize", "isize",
        "format", "androidFormat", "iosFormat", "aformat", "iformat", "bitrate", "sampleRate", "bitDepth",
        "fileType", "androidFileType", "iosFileType", "iosAccuracyLevel", "androidAccuracyLevel",
        "fileId", "contentId"
    ];
    private static void CopyText(JsonObject target, JsonObject source, string field)
    {
        if (source[field] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            target[field] = text;
    }
    private static void CopyIdentifier(JsonObject target, JsonObject source, string field)
    {
        if (source[field] is not JsonValue scalar) return;
        if (scalar.TryGetValue<string>(out var text))
            target[field] = text;
        else if (scalar.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
            double.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number))
            target[field] = scalar.DeepClone();
    }
    private static JsonArray CleanStringArray(JsonArray items)
    {
        var result = new JsonArray();
        foreach (var item in items)
            if (item is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                result.Add(text);
        return result;
    }
    private static JsonObject CleanFormatRow(JsonObject row)
    {
        var clean = new JsonObject();
        foreach (string field in FormatIdentifierFields) CopyIdentifier(clean, row, field);
        foreach (string field in FormatTextFields) CopyText(clean, row, field);
        return clean;
    }
    private static JsonObject CleanNative(JsonObject data)
    {
        var result = new JsonObject();
        foreach (string field in SourceInfoTextFields) CopyText(result, data, field);
        foreach (string field in SourceInfoIdentifierFields) CopyIdentifier(result, data, field);
        result["duration"] = ValidDuration(data["duration"]);
        result["img"] = PublicUrl(data["img"]);
        if (LJ.IsBool(data["qualityKnown"])) result["qualityKnown"] = data["qualityKnown"]!.DeepClone();
        if (data["artistNames"] is JsonArray artistNames) result["artistNames"] = CleanStringArray(artistNames);
        if (data["types"] is JsonArray rawTypes)
        {
            var types = new JsonArray();
            foreach (var item in rawTypes)
            {
                if (item is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    types.Add(new JsonObject { ["type"] = text });
                }
                else if (item is JsonObject row)
                {
                    var clean = new JsonObject();
                    CopyIdentifier(clean, row, "type");
                    CopyText(clean, row, "hash");
                    CopyIdentifier(clean, row, "size");
                    types.Add(clean);
                }
            }
            result["types"] = types;
        }
        if (data["_types"] is JsonObject rawTypeMap)
        {
            var typeMap = new JsonObject();
            foreach (var pair in rawTypeMap)
            {
                var clean = new JsonObject();
                if (pair.Value is JsonObject row)
                {
                    CopyText(clean, row, "hash");
                    CopyIdentifier(clean, row, "size");
                }
                typeMap[pair.Key] = clean;
            }
            result["_types"] = typeMap;
        }
        foreach (string field in new[] { "audioFormats", "newRateFormats" })
        {
            if (data[field] is JsonArray rows)
            {
                var cleaned = new JsonArray();
                foreach (var row in rows.OfType<JsonObject>()) cleaned.Add(CleanFormatRow(row));
                result[field] = cleaned;
            }
        }
        return result;
    }
    internal static JsonNode? Durable(JsonNode? value, string field = "")
    {
        if (value is JsonArray array) return LJ.Arr(array.Select(item => Durable(item)));
        if (value is JsonObject obj) return new JsonObject(obj.Where(pair =>
            !Regex.IsMatch(pair.Key, @"(?i)cookie|token|password|secret|credential|authorization|api[-_]?key|^headers$", RegexOptions.CultureInvariant, RegexTimeout) &&
            !Regex.IsMatch(pair.Key, @"^(?i:url|audioUrl|playUrl|currentTime|sessionTime|resumeTime|seekTime|progress|activeSource|activeSourceKey|activeQuality|actualLevel|qualityVerified|qualityUnrecognized|lxScriptId|lxScriptName|lxScriptVersion|recommendation|fm|fmManual|reportInstance|reportScope)$", RegexOptions.CultureInvariant, RegexTimeout))
            .Select(pair => KeyValuePair.Create(pair.Key, Durable(pair.Value, pair.Key))));
        return value?.DeepClone();
    }
    internal static JsonObject DurableSource(JsonNode? input)
    {
        var source = LJ.Object(input, "INVALID_SOURCE");
        var info = CleanNative(LJ.Object(source["musicInfo"], "INVALID_SOURCE"));
        return LJ.Obj(("sourceKey", LJ.S(source["sourceKey"])), ("source", LJ.S(source["source"])), ("musicInfo", info));
    }
    internal static bool IsOnline(JsonObject song) => Primary(song) is not null;
    internal static JsonObject CreateSource(string platform, JsonObject data, string title, string artist,
        string album, double duration, string cover)
    {
        var info = data.DeepClone().AsObject();
        info["source"] = platform; info["name"] = title; info["singer"] = artist; info["albumName"] = album;
        info["duration"] = duration > 0 ? duration : null; info["img"] = cover;
        info = CleanNative(info);
        return LJ.Obj(("sourceKey", InfoKey(info)), ("source", platform), ("musicInfo", info));
    }
    internal static JsonObject CreateOnline(string platform, JsonObject data, string title, string artist,
        string album, double duration, string cover)
    {
        var song = LJ.Obj(("title", title), ("artist", artist), ("album", album), ("duration", duration > 0 ? duration : null),
            ("cover_url", PublicUrl(JsonValue.Create(cover))), ("sources", new JsonArray(CreateSource(platform, data, title, artist, album, duration, cover))));
        if (data["artistNames"] is not null) song["artistNames"] = data["artistNames"]!.DeepClone();
        return Normalize(song);
    }
    internal static JsonObject CreateLocal(string fileId, string title, string artist = "", string album = "",
        double duration = 0, string cover = "") => Normalize(LJ.Obj(("localFileId", fileId), ("title", title), ("artist", artist),
            ("album", album), ("duration", duration), ("cover_url", cover), ("sources", new JsonArray())));
    internal static JsonObject Normalize(JsonNode? input)
    {
        var raw = LJ.Object(input, "INVALID_SONG");
        var song = LJ.Obj(("songId", LJ.S(raw["songId"])), ("title", LJ.S(raw["title"])), ("artist", LJ.S(raw["artist"])),
            ("album", LJ.S(raw["album"])), ("duration", ValidDuration(raw["duration"])), ("cover_url", PublicUrl(raw["cover_url"])),
            ("sources", LJ.Arr(LJ.Array(raw["sources"], "INVALID_SOURCE").Select(DurableSource))));
        if (raw["artistNames"] is JsonArray artistNames) song["artistNames"] = CleanStringArray(artistNames);
        if (raw["localFileId"] is JsonValue) song["localFileId"] = LJ.S(raw["localFileId"]);
        if (raw["artworkSourceKey"] is JsonValue) song["artworkSourceKey"] = LJ.S(raw["artworkSourceKey"]);
        if (raw["artworkSourceImage"] is JsonValue) song["artworkSourceImage"] = LJ.S(raw["artworkSourceImage"]);
        song["baselineDuration"] = ValidDuration(raw["baselineDuration"]) ?? ValidDuration(raw["duration"]);
        return song;
    }
}

using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    // A display snapshot, not a media URL/cache, login session or new writer.
    private const string LyricDisplayArea = "native-lyric-display";
    private static string DisplayKey(JsonObject song) => LJ.Digest(LJ.Obj(
        ("songId", song["songId"]), ("descriptor", NativeMusicSong.Descriptor(song)),
        ("duration", song["duration"]),
        ("sources", LJ.Arr(LJ.Array(song["sources"]).OfType<JsonObject>()
            .Select(row => LJ.S(row["sourceKey"])).Order(StringComparer.Ordinal)))));

    private static void ValidateLyricDisplay(JsonObject display)
    {
        LJ.Fields(display, "schema songKey songId sourceKey lines", "schema songKey songId sourceKey lines");
        LJ.Require(LJ.I(display["schema"]) == 2 && LJ.S(display["songKey"]).Length == 64, "CORRUPT_STORAGE");
        _ = LJ.Text(display["songId"], 8192);
        _ = LJ.Text(display["sourceKey"], 8192);
        ValidateDisplayLines(LJ.Array(display["lines"]));
        LJ.Require(LJ.Bytes(display).Length <= 512 * 1024, "LIMIT");
    }

    private static void ValidateDisplayLines(JsonArray lines)
    {
        LJ.Require(lines.Count is > 0 and <= 10000, "INVALID_LYRIC_RESPONSE");
        double previous = -1;
        int characters = 0, words = 0;
        void Text(JsonNode? value)
        {
            LJ.Require(value is JsonValue v && v.TryGetValue<string>(out var text) &&
                text.Length <= 65536 && !text.Contains('\0'), "INVALID_LYRIC_RESPONSE");
            characters += LJ.S(value).Length;
            LJ.Require(characters <= 4 * 65536, "LIMIT");
        }
        double Seconds(JsonNode? value)
        {
            LJ.Require(value is JsonValue v && v.TryGetValue<double>(out var number) &&
                double.IsFinite(number) && number is >= 0 and <= 86400000, "INVALID_LYRIC_RESPONSE");
            return LJ.N(value);
        }
        foreach (var item in lines)
        {
            var row = LJ.Object(item);
            LJ.Fields(row, "time end text words translation romanization", "time text");
            double time = Seconds(row["time"]);
            LJ.Require(time >= previous, "INVALID_LYRIC_RESPONSE"); previous = time;
            if (row["end"] is not null) LJ.Require(Seconds(row["end"]) >= time, "INVALID_LYRIC_RESPONSE");
            Text(row["text"]);
            foreach (string key in new[] { "translation", "romanization" })
                if (row[key] is not null) Text(row[key]);
            if (row["words"] is not null)
                foreach (var wordValue in LJ.Array(row["words"]))
                {
                    LJ.Require(++words <= 65536, "LIMIT");
                    var word = LJ.Object(wordValue);
                    LJ.Fields(word, "time duration text offset", "time duration text");
                    // Negative word timestamps can occur with a negative LRC
                    // offset. They are valid, but never become a negative seek.
                    LJ.Require(word["time"] is JsonValue w && w.TryGetValue<double>(out var t) &&
                        double.IsFinite(t) && Math.Abs(t) <= 86400000, "INVALID_LYRIC_RESPONSE");
                    _ = Seconds(word["duration"]); Text(word["text"]);
                    if (word["offset"] is not null)
                        LJ.Require(word["offset"] is JsonValue o && o.TryGetValue<double>(out var offset) &&
                            double.IsFinite(offset) && Math.Abs(offset) <= 86400000, "INVALID_LYRIC_RESPONSE");
                }
        }
    }

    private (JsonObject Result, bool Mutate) LyricDisplayCommand(string method, JsonObject p, JsonObject doc)
    {
        bool save = method == "music.lyrics.display.save";
        LJ.Require(save || method == "music.lyrics.display.get", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, save ? "song sourceKey lines" : "song sourceKey", save ? "song sourceKey lines" : "song");
        var song = NativeMusicSong.Normalize(p["song"]);
        string key = DisplayKey(song);
        string? requested = p["sourceKey"] is null ? null : LJ.Text(p["sourceKey"], 8192);
        if (requested is not null)
            LJ.Require(LJ.Array(song["sources"]).OfType<JsonObject>()
                .Any(source => LJ.S(source["sourceKey"]) == requested), "TARGET_CHANGED");
        if (!save)
        {
            LJ.Require(pending is null, "PENDING_WRITE");
            if (!LJ.B(Settings(doc)["lyrics"]))
                return (LJ.Obj(("state", "disabled"), ("lines", new JsonArray())), false);
            var saved = Value(doc, LyricDisplayArea) as JsonObject;
            if (saved is null || LJ.S(saved["songKey"]) != key ||
                requested is not null && LJ.S(saved["sourceKey"]) != requested)
                return (LJ.Obj(("state", "none"), ("lines", new JsonArray())), false);
            if (!MetadataSourceEnabled(doc, song, LJ.S(saved["sourceKey"])))
            {
                bool alternative = requested is null && LJ.Array(song["sources"]).OfType<JsonObject>()
                    .Any(source => MetadataSourceEnabled(doc, song, LJ.S(source["sourceKey"])));
                return (LJ.Obj(("state", alternative ? "none" : "disabled"), ("lines", new JsonArray())), false);
            }
            return (LJ.Obj(("state", "restored"), ("sourceKey", saved["sourceKey"]), ("lines", saved["lines"])), false);
        }
        LJ.Require(pending is null, "PENDING_WRITE");
        LJ.Require(LJ.B(Settings(doc)["lyrics"]) && requested is not null &&
            MetadataSourceEnabled(doc, song, requested), "DISABLED");
        RequireCurrentLyricSong(doc, key);
        var display = LJ.Obj(("schema", 2), ("songKey", key), ("songId", song["songId"]),
            ("sourceKey", requested), ("lines", p["lines"]));
        ValidateLyricDisplay(display);
        if (Value(doc, LyricDisplayArea) is { } existing && JsonNode.DeepEquals(existing, display))
            return (LJ.Obj(("saved", true)), false);
        Set(doc, LyricDisplayArea, display);
        return (LJ.Obj(("saved", true)), true);
    }
}

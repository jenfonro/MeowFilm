using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string LocalLyricsArea = "native-local-lyrics";
    private JsonObject LocalLyrics(JsonObject doc) => Value(doc, LocalLyricsArea) as JsonObject ??
        LJ.Obj(("schema", 1), ("items", new JsonArray()));

    private static void ValidateLocalLyrics(JsonObject state)
    {
        LJ.Fields(state, "schema items", "schema items");
        LJ.Require(LJ.I(state["schema"]) == 1);
        var rows = LJ.Array(state["items"]);
        LJ.Require(rows.Count <= 64 && LJ.Bytes(state).Length <= 2 * 1024 * 1024, "LIMIT");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rows)
        {
            var row = LJ.Object(item);
            LJ.Fields(row, "songKey songId contentHash lines", "songKey songId contentHash lines");
            LJ.Require(LJ.S(row["songKey"]).Length == 64 && identities.Add(LJ.S(row["songKey"])) &&
                LJ.S(row["contentHash"]).Length == 64, "CORRUPT_STORAGE");
            _ = LJ.Text(row["songId"], 8192);
            ValidateDisplayLines(LJ.Array(row["lines"]));
            LJ.Require(LJ.Bytes(row).Length <= 256 * 1024, "LIMIT");
        }
    }

    private (JsonObject Result, bool Mutate) LocalLyricsCommand(string method, JsonObject p, JsonObject doc)
    {
        LJ.Require(method is "music.lyrics.local.get" or "music.lyrics.local.save" or "music.lyrics.local.remove",
            "CAPABILITY_UNAVAILABLE");
        bool save = method == "music.lyrics.local.save";
        LJ.Fields(p, save ? "song text" : "song", save ? "song text" : "song");
        LJ.Require(pending is null, "PENDING_WRITE");
        var song = NativeMusicSong.Normalize(p["song"]);
        string key = DisplayKey(song);
        var state = LocalLyrics(doc);
        var items = LJ.Array(state["items"]);
        var existing = items.OfType<JsonObject>().SingleOrDefault(item => LJ.S(item["songKey"]) == key);
        if (method == "music.lyrics.local.get")
        {
            if (!LJ.B(Settings(doc)["lyrics"]))
                return (LJ.Obj(("state", "disabled"), ("lines", new JsonArray()), ("available", existing is not null)), false);
            return (existing is null ? LJ.Obj(("state", "none"), ("lines", new JsonArray()), ("available", false)) :
                LJ.Obj(("state", "local"), ("lines", existing["lines"]), ("available", true)), false);
        }
        if (!save)
        {
            if (existing is null) return (LJ.Obj(("removed", false)), false);
            items.Remove(existing);
            Set(doc, LocalLyricsArea, state);
            return (LJ.Obj(("removed", true)), true);
        }
        RequireCurrentLyricSong(doc, key);
        LJ.Require(p["text"] is JsonValue value && value.TryGetValue<string>(out _), "INVALID_LYRIC_RESPONSE");
        string text = LJ.S(p["text"]);
        LJ.Require(text.Length is > 0 and <= 65536 && !text.Any(c => c < 32 && c is not ('\r' or '\n' or '\t')),
            "INVALID_LYRIC_RESPONSE");
        var lines = JsonNode.Parse(MusicLyricTiming.Parse(text, duration: LJ.N(song["duration"])).GetRawText())!.AsArray();
        LJ.Require(lines.Count > 0, "INVALID_LYRIC_RESPONSE");
        var next = LJ.Obj(("songKey", key), ("songId", song["songId"]),
            ("contentHash", LJ.Digest(LJ.Obj(("text", text)))), ("lines", lines));
        if (existing is not null && JsonNode.DeepEquals(existing, next))
            return (LJ.Obj(("saved", true)), false);
        if (existing is not null) items.Remove(existing);
        items.Add(next);
        ValidateLocalLyrics(state);
        Set(doc, LocalLyricsArea, state);
        return (LJ.Obj(("saved", true)), true);
    }

    private void RequireCurrentLyricSong(JsonObject doc, string key)
    {
        var queue = Queue(doc);
        var entry = LJ.Array(queue["entries"]).OfType<JsonObject>()
            .FirstOrDefault(row => LJ.S(row["id"]) == LJ.S(queue["currentId"]));
        LJ.Require(entry is not null && DisplayKey(NativeMusicSong.Normalize(entry["song"])) == key, "TARGET_CHANGED");
    }
}

using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string LocalLyricsArea = "native-local-lyrics";
    private (JsonObject Result, bool Mutate) LocalLyricsCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        LJ.Require(method is "music.lyrics.local.get" or "music.lyrics.local.save" or "music.lyrics.local.remove",
            "CAPABILITY_UNAVAILABLE");
        bool save = method == "music.lyrics.local.save";
        LJ.Fields(p, save ? "song text" : "song");
        LJ.Require(pending is null, "PENDING_WRITE");
        var song = NativeMusicSong.Normalize(p["song"]);
        string key = DisplayKey(song);
        var state = doc.Select(LocalLyricsArea, "schema");
        if (state is not null)
        {
            try { LJ.Require(LJ.I(state["schema"]) == 1); }
            catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
            LJ.Require(doc.HasRelation(LocalLyricsArea, [], [doc.Scope], "items"), "CORRUPT_STORAGE");
        }
        var rows = doc.Rows(LocalLyricsArea, ["items"], [doc.Scope], "songId");
        var matches = rows.Where(item => LJ.S(item.Value["songId"]) == key).ToArray();
        LJ.Require(matches.Length <= 1, "CORRUPT_STORAGE");
        var existing = matches.SingleOrDefault();
        if (method == "music.lyrics.local.get")
        {
            if (!LJ.B(Settings(doc)["lyrics"]))
                return (LJ.Obj(("state", "disabled"), ("lines", new JsonArray()), ("available", existing is not null)), false);
            if (existing is null) return (LJ.Obj(("state", "none"), ("lines", new JsonArray()), ("available", false)), false);
            var selected = doc.Row(LocalLyricsArea, ["items"], existing.Key);
            LJ.Require(LJ.S(selected["contentHash"]).Length == 64, "CORRUPT_STORAGE");
            try { _ = DecodeDisplayLines(selected["lines"], enforceLimits: false); }
            catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
            return (LJ.Obj(("state", "local"), ("lines", selected["lines"]), ("available", true)), false);
        }
        if (!save)
        {
            if (existing is null) return (LJ.Obj(("removed", false)), false);
            doc.ChangeRow(LocalLyricsArea, ["items"], existing.Key, null, existing.Ordinal);
            return (LJ.Obj(("removed", true)), true);
        }
        RequireCurrentLyricSong(doc, key);
        LJ.Require(p["text"] is JsonValue value && value.TryGetValue<string>(out _), "INVALID_LYRIC_RESPONSE");
        string text = LJ.S(p["text"]);
        LJ.Require(text.Length > 0 && !text.Any(c => c < 32 && c is not ('\r' or '\n' or '\t')),
            "INVALID_LYRIC_RESPONSE");
        var lines = JsonNode.Parse(MusicLyricTiming.Parse(text, duration: LJ.N(song["duration"])).GetRawText())!.AsArray();
        LJ.Require(lines.Count > 0, "INVALID_LYRIC_RESPONSE");
        var next = LJ.Obj(("songId", song["songId"]),
            ("contentHash", LJ.Digest(LJ.Obj(("text", text)))), ("lines", lines));
        if (existing is not null && doc.Matches(LocalLyricsArea, ["items"], existing.Key,
            next))
            return (LJ.Obj(("saved", true)), false);
        if (state is null) Set(doc, LocalLyricsArea, LJ.Obj(("schema", 1), ("items", new JsonArray())));
        doc.ChangeRow(LocalLyricsArea, ["items"], existing?.Key ?? [doc.Scope, Guid.NewGuid().ToString("D")], next,
            existing?.Ordinal ?? (rows.Count == 0 ? 0 : rows.Max(row => row.Ordinal) + 1));
        return (LJ.Obj(("saved", true)), true);
    }

    private void RequireCurrentLyricSong(MusicLibraryData doc, string key)
    {
        var queue = Queue(doc);
        var entry = LJ.Array(queue["entries"]).OfType<JsonObject>()
            .FirstOrDefault(row => LJ.S(row["id"]) == LJ.S(queue["currentId"]));
        LJ.Require(entry is not null && DisplayKey(EntrySong(doc, entry)) == key, "TARGET_CHANGED");
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    public const string LyricDisplayNamespace = "music.lyric-display.v1";
    private static string DisplayKey(JsonObject song) => LJ.Digest(LJ.Obj(
        ("songId", song["songId"]), ("descriptor", NativeMusicSong.Descriptor(song)),
        ("duration", song["duration"]),
        ("sources", LJ.Arr(LJ.Array(song["sources"]).OfType<JsonObject>()
            .Select(row => LJ.S(row["sourceKey"])).Order(StringComparer.Ordinal)))));

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

    private static JsonArray DecodeCachedDisplayLines(JsonNode? value)
    {
        var rows = value!.AsArray();
        LJ.Require(rows.Count is > 0 and <= 10000, "INVALID_LYRIC_RESPONSE");
        var decoded = new JsonArray();
        double previous = -1;
        int characters = 0, wordCount = 0;
        string Text(JsonNode? node)
        {
            string text = node!.GetValue<string>();
            characters += text.Length;
            LJ.Require(text.Length <= 65536 && !text.Contains('\0') && characters <= 4 * 65536, "LIMIT");
            return text;
        }
        double Number(JsonNode? node, bool signed = false)
        {
            double number = node!.GetValue<double>();
            LJ.Require(double.IsFinite(number) && Math.Abs(number) <= 86400000 && (signed || number >= 0), "INVALID_LYRIC_RESPONSE");
            return number;
        }
        foreach (var item in rows)
        {
            var row = item!.AsObject();
            double time = Number(row["time"]);
            LJ.Require(time >= previous, "INVALID_LYRIC_RESPONSE"); previous = time;
            var line = LJ.Obj(("time", time), ("text", Text(row["text"])));
            if (row["end"] is { } end)
            {
                double seconds = Number(end); LJ.Require(seconds >= time, "INVALID_LYRIC_RESPONSE");
                line["end"] = seconds;
            }
            foreach (string field in new[] { "translation", "romanization" })
                if (row[field] is { } text) line[field] = Text(text);
            if (row["words"] is { } words)
            {
                var decodedWords = new JsonArray();
                foreach (var wordValue in words.AsArray())
                {
                    LJ.Require(++wordCount <= 65536, "LIMIT");
                    var word = wordValue!.AsObject();
                    var decodedWord = LJ.Obj(("time", Number(word["time"], true)),
                        ("duration", Number(word["duration"])), ("text", Text(word["text"])));
                    if (word["offset"] is { } offset) decodedWord["offset"] = Number(offset, true);
                    decodedWords.Add(decodedWord);
                }
                line["words"] = decodedWords;
            }
            decoded.Add(line);
        }
        return decoded;
    }

    private async Task<JsonElement> ExecuteLyricDisplayAsync(string method, object? parameters, CancellationToken token)
    {
        bool entered = false;
        try
        {
            bool save = method == "music.lyrics.display.save";
            LJ.Require(save || method == "music.lyrics.display.get", "CAPABILITY_UNAVAILABLE");
            var p = LJ.Parameters(parameters, 512 * 1024);
            LJ.Fields(p, save ? "song sourceKey lines" : "song sourceKey", save ? "song sourceKey lines" : "song");
            var song = NativeMusicSong.Normalize(p["song"]);
            string key = DisplayKey(song);
            string? requested = p["sourceKey"] is null ? null : LJ.Text(p["sourceKey"], 8192);
            if (requested is not null)
                LJ.Require(LJ.Array(song["sources"]).OfType<JsonObject>()
                    .Any(source => LJ.S(source["sourceKey"]) == requested), "TARGET_CHANGED");
            JsonObject? saved = null;
            if (!save)
            {
                // Disk latency never occupies the business library gate. Re-read
                // policy after acquiring it, including changes during this read.
                try { await displayCache.ReadAsync(LyricDisplayNamespace, token).ConfigureAwait(false); }
                catch (DocumentStoreException) { }
            }
            await EnterAsync(token).ConfigureAwait(false); entered = true;
            LJ.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitCommandLockedAsync(method, token).ConfigureAwait(false);
            var doc = document!;
            JsonObject result;
            if (!save)
            {
                // A save admitted while disk loading or gate acquisition was in
                // flight wins; inspect the latest memory snapshot without I/O.
                try
                {
                    var snapshot = displayCache.ReadMemory(LyricDisplayNamespace);
                    if (snapshot.Data is not null)
                    {
                        saved = JsonNode.Parse(snapshot.Data) as JsonObject;
                        if (saved is not null) saved["lines"] = DecodeCachedDisplayLines(saved["lines"]);
                    }
                }
                catch { saved = null; }
                if (!LJ.B(Settings(doc)["lyrics"])) result = LJ.Obj(("state", "disabled"), ("lines", new JsonArray()));
                else if (saved is null || LJ.S(saved["scope"]) != scope || LJ.S(saved["songKey"]) != key ||
                    requested is not null && LJ.S(saved["sourceKey"]) != requested)
                    result = LJ.Obj(("state", "none"), ("lines", new JsonArray()));
                else if (!MetadataSourceEnabled(doc, song, LJ.S(saved["sourceKey"])))
                {
                    bool alternative = requested is null && LJ.Array(song["sources"]).OfType<JsonObject>()
                        .Any(source => MetadataSourceEnabled(doc, song, LJ.S(source["sourceKey"])));
                    result = LJ.Obj(("state", alternative ? "none" : "disabled"), ("lines", new JsonArray()));
                }
                else result = LJ.Obj(("state", "restored"), ("sourceKey", saved["sourceKey"]), ("lines", saved["lines"]));
            }
            else
            {
                LJ.Require(LJ.B(Settings(doc)["lyrics"]) && requested is not null &&
                    MetadataSourceEnabled(doc, song, requested), "DISABLED");
                RequireCurrentLyricSong(doc, key);
                ValidateDisplayLines(LJ.Array(p["lines"]));
                var display = LJ.Obj(("scope", scope), ("songKey", key), ("sourceKey", requested), ("lines", p["lines"]));
                byte[] bytes = LJ.Bytes(display);
                LJ.Require(bytes.Length <= 512 * 1024, "LIMIT");
                try { displayCache.Save(LyricDisplayNamespace, bytes); }
                catch (DocumentStoreException) { System.Diagnostics.Trace.TraceWarning("LYRIC_DISPLAY_CACHE_UNAVAILABLE"); }
                result = LJ.Obj(("saved", true));
            }
            return LJ.Element(LJ.Merge(LJ.Obj(("ok", true)), result));
        }
        catch (OperationCanceledException) { return LJ.Element(LJ.Obj(("ok", false), ("code", "CANCELLED"))); }
        catch (MusicLibraryException error) { return LJ.Element(LJ.Obj(("ok", false), ("code", error.Code))); }
        catch (DocumentStoreException error) { return LJ.Element(LJ.Obj(("ok", false), ("code", error.Error == DocumentStoreError.Unavailable ? "STORAGE_UNAVAILABLE" : "STORAGE"))); }
        catch { return LJ.Element(LJ.Obj(("ok", false), ("code", "INVALID_REQUEST"))); }
        finally { if (entered) gate.Release(); }
    }
}

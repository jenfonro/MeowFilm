using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using MeowFilm.Capabilities.PlaybackCache;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private long lyricDisplaySaveSequence;
    private static string DisplayKey(JsonObject song) => MusicPresentationCache.SongKey(song);

    private static JsonArray DecodeDisplayLines(JsonNode? value, bool enforceLimits = true)
    {
        var rows = LJ.Array(value, "INVALID_LYRIC_RESPONSE");
        LJ.Require(rows.Count > 0, "INVALID_LYRIC_RESPONSE");
        if (enforceLimits) LJ.Require(rows.Count <= 10000, "LIMIT");
        var decoded = new JsonArray();
        double previous = -1;
        int characters = 0, wordCount = 0;
        string Text(JsonNode? node)
        {
            string? text = null;
            LJ.Require(node is JsonValue v && v.TryGetValue(out text) && text is not null && !text.Contains('\0'),
                "INVALID_LYRIC_RESPONSE");
            if (enforceLimits)
            {
                characters += text!.Length;
                LJ.Require(text.Length <= 65536 && characters <= 4 * 65536, "LIMIT");
            }
            return text!;
        }
        double Number(JsonNode? node, bool signed = false)
        {
            LJ.Require(node is JsonValue v && v.TryGetValue<double>(out var number) &&
                double.IsFinite(number) && Math.Abs(number) <= 86400000 && (signed || number >= 0),
                "INVALID_LYRIC_RESPONSE");
            return LJ.N(node);
        }
        foreach (var item in rows)
        {
            var row = LJ.Object(item, "INVALID_LYRIC_RESPONSE");
            LJ.Require(row.ContainsKey("time") && row.ContainsKey("text"), "INVALID_LYRIC_RESPONSE");
            double time = Number(row["time"]);
            LJ.Require(time >= previous, "INVALID_LYRIC_RESPONSE"); previous = time;
            var line = LJ.Obj(("time", time), ("text", Text(row["text"])));
            if (row["end"] is { } end)
            {
                double seconds = Number(end); LJ.Require(seconds >= time, "INVALID_LYRIC_RESPONSE");
                line["end"] = seconds;
            }
            if (row["translation"] is { } translation) line["translation"] = Text(translation);
            if (row["romanization"] is { } romanization) line["romanization"] = Text(romanization);
            if (row["words"] is { } words)
            {
                var decodedWords = new JsonArray();
                foreach (var wordValue in LJ.Array(words, "INVALID_LYRIC_RESPONSE"))
                {
                    if (enforceLimits) LJ.Require(++wordCount <= 65536, "LIMIT");
                    var word = LJ.Object(wordValue, "INVALID_LYRIC_RESPONSE");
                    LJ.Require(word.ContainsKey("time") && word.ContainsKey("duration") && word.ContainsKey("text"),
                        "INVALID_LYRIC_RESPONSE");
                    // Negative word timestamps can occur with a negative LRC
                    // offset. They are valid, but never become a negative seek.
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
            lock (repositoryGate) LJ.Require(!closed && !preparing, "CLOSED");
            bool save = method == "music.lyrics.display.save";
            LJ.Require(save || method == "music.lyrics.display.get", "CAPABILITY_UNAVAILABLE");
            var p = LJ.Parameters(parameters);
            LJ.Fields(p, save ? "song sourceKey lines" : "song");
            var song = NativeMusicSong.Normalize(p["song"]);
            string key = DisplayKey(song);
            string? requested = null;
            if (p["sourceKey"] is { } requestedKey)
            {
                LJ.Require(requestedKey is JsonValue value && value.TryGetValue<string>(out _));
                requested = requestedKey.GetValue<string>();
            }
            if (requested is not null)
                LJ.Require(LJ.Array(song["sources"]).OfType<JsonObject>()
                    .Any(source => LJ.S(source["sourceKey"]) == requested), "TARGET_CHANGED");
            JsonObject? saved = null;
            var presentations = PresentationCache;
            long cacheEpoch = 0;
            long saveSequence = 0;
            if (save)
            {
                await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
                LJ.Require(!closed && !preparing, "CLOSED");
                cacheEpoch = presentations.LyricEpoch;
                saveSequence = ++lyricDisplaySaveSequence;
            }
            else
            {
                for (;;)
                {
                    long version = presentations.LyricVersion;
                    try { saved = key.Length == 0 ? null : await presentations.ReadLyricsAsync(key, token).ConfigureAwait(false); }
                    catch (DocumentStoreException) { saved = null; }
                    await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
                    // A save completed while this individual read or gate wait
                    // was in flight wins. Re-read only that song, outside the gate.
                    if (key.Length == 0 || version == presentations.LyricVersion) break;
                    gate.Release(); entered = false;
                }
            }
            LJ.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitCommandLockedAsync(method, token).ConfigureAwait(false);
            var doc = document!;
            JsonObject result;
            if (!save)
            {
                try
                {
                    if (saved is not null)
                    {
                        saved["lines"] = DecodeDisplayLines(saved["lines"]);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (DocumentStoreException) { saved = null; }
                catch (MusicLibraryException) { saved = null; }
                if (!LJ.B(Settings(doc)["lyrics"])) result = LJ.Obj(("state", "disabled"), ("lines", new JsonArray()));
                else if (saved is null ||
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
                var lines = DecodeDisplayLines(p["lines"]);
                var display = LJ.Obj(("sourceKey", requested), ("lines", lines));
                byte[] bytes = LJ.Bytes(display);
                LJ.Require(bytes.Length <= 512 * 1024, "LIMIT");
                gate.Release(); entered = false;
                try
                {
                    await presentations.SaveLyricsAsync(key, requested, lines, cacheEpoch, saveSequence, token).ConfigureAwait(false);
                }
                catch (DocumentStoreException) { System.Diagnostics.Trace.TraceWarning("LYRIC_DISPLAY_CACHE_UNAVAILABLE"); }
                result = LJ.Obj(("saved", true));
            }
            return LJ.Element(LJ.Merge(LJ.Obj(("ok", true)), result));
        }
        catch (OperationCanceledException) { return LJ.Element(LJ.Obj(("ok", false), ("code", "CANCELLED"))); }
        catch (ObjectDisposedException) when (closed) { return LJ.Element(LJ.Obj(("ok", false), ("code", "CLOSED"))); }
        catch (MusicLibraryException error) { return LJ.Element(LJ.Obj(("ok", false), ("code", error.Code))); }
        catch (DocumentStoreException error) { return LJ.Element(LJ.Obj(("ok", false), ("code", error.Error == DocumentStoreError.Unavailable ? "STORAGE_UNAVAILABLE" : "STORAGE"))); }
        catch { return LJ.Element(LJ.Obj(("ok", false), ("code", "INVALID_REQUEST"))); }
        finally { if (entered) gate.Release(); }
    }
}

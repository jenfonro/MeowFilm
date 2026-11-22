using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed record CachedLyricWord(double TimeSec, string Text, double DurationSec, double? OffsetSec);
internal sealed record CachedLyricLine(
    double TimeSec,
    string Text,
    string? Translation,
    string? Romanization,
    double? EndSec,
    IReadOnlyList<CachedLyricWord>? Words);
internal sealed record CachedLyricsRecord(
    string SourceKey,
    long UpdatedAt,
    long StoredBytes,
    IReadOnlyList<CachedLyricLine> Lines);

internal static class LyricsCacheStore
{
    internal static CachedLyricsRecord? ReadRecord(DatabaseSession db, string songId)
    {
        if (!db.TryQuerySingle("""
            SELECT source_key,updated_at,stored_bytes
            FROM music_lyrics
            WHERE song_id=$id
            """,
            row => (SourceKey: row.GetString(0), UpdatedAt: (long)row.GetDouble(1), StoredBytes: row.GetInt64(2)),
            out var header,
            ("$id", songId)))
            return null;

        var wordsByLine = new Dictionary<int, List<CachedLyricWord>>();
        foreach (var item in db.Query("""
            SELECT line_position,time_sec,text,duration_sec,offset_sec
            FROM music_lyric_words
            WHERE song_id=$id
            ORDER BY line_position, position
            """,
            row => (LinePos: row.GetInt32(0), Word: new CachedLyricWord(
                row.GetDouble(1), row.GetString(2), row.GetDouble(3), row.GetNullableDouble(4))),
            ("$id", songId)))
        {
            if (!wordsByLine.TryGetValue(item.LinePos, out var list))
                wordsByLine[item.LinePos] = list = [];
            list.Add(item.Word);
        }

        var lines = db.Query("""
            SELECT position,time_sec,text,translation,romanization,end_sec
            FROM music_lyric_lines
            WHERE song_id=$id
            ORDER BY position
            """,
            row =>
            {
                int pos = row.GetInt32(0);
                wordsByLine.TryGetValue(pos, out var words);
                return new CachedLyricLine(
                    row.GetDouble(1), row.GetString(2),
                    row.GetNullableString(3), row.GetNullableString(4), row.GetNullableDouble(5),
                    words);
            }, ("$id", songId));

        return new CachedLyricsRecord(header.SourceKey, header.UpdatedAt, header.StoredBytes, lines);
    }

    internal static JsonObject? Read(DatabaseSession db, string songId)
    {
        var rec = ReadRecord(db, songId);
        if (rec is null) return null;
        var lines = new JsonArray();
        foreach (var line in rec.Lines)
        {
            var obj = new JsonObject { ["time"] = line.TimeSec, ["text"] = line.Text };
            if (line.Translation is not null) obj["translation"] = line.Translation;
            if (line.Romanization is not null) obj["romanization"] = line.Romanization;
            if (line.EndSec is { } end) obj["end"] = end;
            if (line.Words is { Count: > 0 } words)
            {
                var wArr = new JsonArray();
                foreach (var w in words)
                {
                    var wObj = new JsonObject { ["time"] = w.TimeSec, ["text"] = w.Text, ["duration"] = w.DurationSec };
                    if (w.OffsetSec is { } off) wObj["offset"] = off;
                    wArr.Add(wObj);
                }
                obj["words"] = wArr;
            }
            lines.Add(obj);
        }
        return new JsonObject
        {
            ["sourceKey"] = rec.SourceKey,
            ["updatedAt"] = rec.UpdatedAt,
            ["storedBytes"] = rec.StoredBytes,
            ["lines"] = lines
        };
    }

    internal static void Save(DatabaseSession db, string songId, string sourceKey, long updatedAt, int storedBytes, JsonArray lines)
    {
        var parsed = new List<CachedLyricLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i] is not JsonObject line) continue;
            List<CachedLyricWord>? words = null;
            if (line["words"] is JsonArray wArr)
            {
                words = new List<CachedLyricWord>(wArr.Count);
                for (int w = 0; w < wArr.Count; w++)
                {
                    if (wArr[w] is not JsonObject word) continue;
                    words.Add(new CachedLyricWord(
                        Dbl(word, "time") ?? 0d, Str(word, "text") ?? "",
                        Dbl(word, "duration") ?? 0d, Dbl(word, "offset")));
                }
            }
            parsed.Add(new CachedLyricLine(
                Dbl(line, "time") ?? 0d, Str(line, "text") ?? "",
                Str(line, "translation"), Str(line, "romanization"), Dbl(line, "end"), words));
        }
        SaveRecord(db, songId, new CachedLyricsRecord(sourceKey, updatedAt, storedBytes, parsed));
    }

    internal static void SaveRecord(DatabaseSession db, string songId, CachedLyricsRecord record)
    {
        db.Execute("DELETE FROM music_lyrics WHERE song_id=$id", ("$id", songId));
        db.Execute("""
            INSERT INTO music_lyrics(song_id,source_key,updated_at,stored_bytes)
            VALUES($id,$skey,$at,$bytes)
            """, ("$id", songId), ("$skey", record.SourceKey), ("$at", (double)record.UpdatedAt), ("$bytes", record.StoredBytes));

        for (int i = 0; i < record.Lines.Count; i++)
        {
            var line = record.Lines[i];
            db.Execute("""
                INSERT INTO music_lyric_lines(song_id,position,line_index,time_sec,end_sec,text,translation,romanization)
                VALUES($id,$pos,$idx,$time,$end,$text,$tr,$roma)
                """,
                ("$id", songId), ("$pos", i), ("$idx", i),
                ("$time", line.TimeSec), ("$end", line.EndSec), ("$text", line.Text),
                ("$tr", line.Translation), ("$roma", line.Romanization));

            if (line.Words is { Count: > 0 } words)
            {
                for (int w = 0; w < words.Count; w++)
                {
                    var word = words[w];
                    db.Execute("""
                        INSERT INTO music_lyric_words(song_id,line_position,position,time_sec,duration_sec,offset_sec,text)
                        VALUES($id,$lpos,$wpos,$time,$dur,$off,$text)
                        """,
                        ("$id", songId), ("$lpos", i), ("$wpos", w),
                        ("$time", word.TimeSec), ("$dur", word.DurationSec), ("$off", word.OffsetSec), ("$text", word.Text));
                }
            }
        }
    }

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue v
            ? v.TryGetValue<string>(out var s) ? s
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()
            : null : null;

    private static double? Dbl(JsonObject obj, string key) =>
        obj[key] is JsonValue v
            ? v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<float>(out var f) ? f
            : v.TryGetValue<decimal>(out var m) ? (double)m
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var eld) ? eld
            : null : null;
}

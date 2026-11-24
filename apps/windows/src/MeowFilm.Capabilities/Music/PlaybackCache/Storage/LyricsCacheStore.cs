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
            SELECT source_key,updated_at_ms,stored_bytes
            FROM cached_music_lyrics
            WHERE song_id=$id
            """,
            row => (SourceKey: row.GetString(0), UpdatedAt: row.GetInt64(1), StoredBytes: row.GetInt64(2)),
            out var header,
            ("$id", songId)))
            return null;

        var wordsByLine = new Dictionary<int, List<CachedLyricWord>>();
        foreach (var item in db.Query("""
            SELECT w.line_position,w.time_sec,w.text,w.duration_sec,e.offset_sec
            FROM cached_music_lyric_words w
            LEFT JOIN windows_cached_music_lyric_word_extras e
                ON e.song_id=w.song_id AND e.line_position=w.line_position AND e.position=w.position
            WHERE w.song_id=$id
            ORDER BY w.line_position, w.position
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
            FROM cached_music_lyric_lines
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

    internal static void SaveRecord(DatabaseSession db, string songId, CachedLyricsRecord record)
    {
        db.Execute("DELETE FROM cached_music_lyrics WHERE song_id=$id", ("$id", songId));
        db.Execute("""
            INSERT INTO cached_music_lyrics(song_id,source_key,updated_at_ms,stored_bytes)
            VALUES($id,$skey,$at,$bytes)
            """, ("$id", songId), ("$skey", record.SourceKey), ("$at", record.UpdatedAt), ("$bytes", record.StoredBytes));

        for (int i = 0; i < record.Lines.Count; i++)
        {
            var line = record.Lines[i];
            db.Execute("""
                INSERT INTO cached_music_lyric_lines(song_id,position,line_index,time_sec,end_sec,text,translation,romanization)
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
                        INSERT INTO cached_music_lyric_words(song_id,line_position,position,time_sec,duration_sec,text)
                        VALUES($id,$lpos,$wpos,$time,$dur,$text)
                        """,
                        ("$id", songId), ("$lpos", i), ("$wpos", w),
                        ("$time", word.TimeSec), ("$dur", word.DurationSec), ("$text", word.Text));

                    if (word.OffsetSec is { } off)
                    {
                        db.Execute("""
                            INSERT INTO windows_cached_music_lyric_word_extras(song_id,line_position,position,offset_sec)
                            VALUES($id,$lpos,$wpos,$off)
                            """,
                            ("$id", songId), ("$lpos", i), ("$wpos", w), ("$off", off));
                    }
                }
            }
        }
    }
}

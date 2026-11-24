namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static bool LocalLyricsEqual(MusicLyricLineRecord[] a, MusicLyricLineRecord[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            var la = a[i]; var lb = b[i];
            if (BitConverter.DoubleToInt64Bits(la.TimeSec) != BitConverter.DoubleToInt64Bits(lb.TimeSec) ||
                la.Text != lb.Text ||
                la.Translation != lb.Translation ||
                la.Romanization != lb.Romanization ||
                !MusicStorageModelHelpers.NullableDoubleEqual(la.EndSec, lb.EndSec) ||
                la.Words.Length != lb.Words.Length)
                return false;
            for (int j = 0; j < la.Words.Length; j++)
            {
                var wa = la.Words[j]; var wb = lb.Words[j];
                if (BitConverter.DoubleToInt64Bits(wa.TimeSec) != BitConverter.DoubleToInt64Bits(wb.TimeSec) ||
                    wa.Text != wb.Text ||
                    BitConverter.DoubleToInt64Bits(wa.DurationSec) != BitConverter.DoubleToInt64Bits(wb.DurationSec) ||
                    !MusicStorageModelHelpers.NullableDoubleEqual(wa.OffsetSec, wb.OffsetSec))
                    return false;
            }
        }
        return true;
    }

    private static void ValidateLocalLyricLines(MusicLyricLineRecord[] lines)
    {
        MusicRules.Require(lines.Length > 0, "INVALID_LYRIC_RESPONSE");
        double previous = -1;
        foreach (var row in lines)
        {
            MusicRules.Require(double.IsFinite(row.TimeSec) && row.TimeSec is >= 0 and <= 86400000, "INVALID_LYRIC_RESPONSE");
            MusicRules.Require(row.TimeSec >= previous, "INVALID_LYRIC_RESPONSE");
            previous = row.TimeSec;
            if (row.EndSec is { } end)
                MusicRules.Require(double.IsFinite(end) && end is >= 0 and <= 86400000 && end >= row.TimeSec, "INVALID_LYRIC_RESPONSE");
            MusicRules.Require(!row.Text.Contains('\0'), "INVALID_LYRIC_RESPONSE");
            if (row.Translation is not null) MusicRules.Require(!row.Translation.Contains('\0'), "INVALID_LYRIC_RESPONSE");
            if (row.Romanization is not null) MusicRules.Require(!row.Romanization.Contains('\0'), "INVALID_LYRIC_RESPONSE");
            foreach (var word in row.Words)
            {
                MusicRules.Require(double.IsFinite(word.TimeSec) && Math.Abs(word.TimeSec) <= 86400000, "INVALID_LYRIC_RESPONSE");
                MusicRules.Require(double.IsFinite(word.DurationSec) && word.DurationSec is >= 0 and <= 86400000, "INVALID_LYRIC_RESPONSE");
                MusicRules.Require(!word.Text.Contains('\0'), "INVALID_LYRIC_RESPONSE");
                if (word.OffsetSec is { } off)
                    MusicRules.Require(double.IsFinite(off) && Math.Abs(off) <= 86400000, "INVALID_LYRIC_RESPONSE");
            }
        }
    }

    public Task<MusicLyricLookupResult> GetLocalLyricsAsync(MusicSongRecord song, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            string key = DisplayKey(NativeMusicSong.Normalize(song));
            var existingSummary = doc.ReadLocalLyricSummaries(key);
            if (!Settings(doc).LyricsEnabled)
                return new MusicLyricLookupResult("disabled", [], Available: existingSummary.Count > 0);
            if (existingSummary.Count == 0)
                return new MusicLyricLookupResult("none", [], Available: false);
            var selected = doc.ReadLocalLyric(key);
            MusicRules.Require(selected is not null && selected.ContentHash.Length == 64, "CORRUPT_STORAGE");
            try { ValidateLocalLyricLines(selected!.Lines); }
            catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
            return new MusicLyricLookupResult("local", selected.Lines, Available: true);
        }, cancellationToken);

    public Task<bool> SaveLocalLyricsAsync(MusicSongRecord song, string text, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.LocalLyrics, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var normalized = NativeMusicSong.Normalize(song);
            string key = DisplayKey(normalized);
            RequireCurrentLyricSong(doc, key);
            MusicRules.Require(text is { Length: > 0 } && !text.Any(c => c < 32 && c is not ('\r' or '\n' or '\t')),
                "INVALID_LYRIC_RESPONSE");
            var lines = MusicLyricTiming.ParseLinesRecord(text, duration: normalized.Duration ?? 0);
            MusicRules.Require(lines.Length > 0, "INVALID_LYRIC_RESPONSE");
            string contentHash = MusicRules.CanonicalDigest(MusicNodeSongCodec.BuildObject(w => w.WriteString("text", text)));
            var existing = doc.ReadLocalLyric(key);
            if (existing is not null && existing.ContentHash == contentHash && LocalLyricsEqual(existing.Lines, lines))
                return (true, false);

            int ordinal = existing?.Ordinal ?? NextLocalLyricOrdinal(doc);
            var next = new MusicLocalLyricRecord(key, ordinal, contentHash, lines);
            doc.StageLocalLyric(new MusicLocalLyricMutation(key, next, ordinal));
            return (true, true);
        }, cancellationToken);

    public Task<bool> RemoveLocalLyricsAsync(MusicSongRecord song, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.LocalLyrics, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var normalized = NativeMusicSong.Normalize(song);
            string key = DisplayKey(normalized);
            var existingSummary = doc.ReadLocalLyricSummaries(key);
            if (existingSummary.Count == 0)
                return (false, false);
            doc.StageLocalLyric(new MusicLocalLyricMutation(key, null, existingSummary[0].Ordinal));
            return (true, true);
        }, cancellationToken);

    private static int NextLocalLyricOrdinal(MusicLibrarySession doc)
    {
        var all = doc.ReadLocalLyricSummaries();
        return all.Count == 0 ? 0 : all.Max(r => r.Ordinal) + 1;
    }

    private void RequireCurrentLyricSong(MusicLibrarySession doc, string key)
    {
        var queue = Queue(doc);
        var entry = queue.Entries.FirstOrDefault(row => row.EntryId == queue.CurrentId);
        MusicRules.Require(entry is not null && DisplayKey(EntrySong(doc, entry)) == key, "TARGET_CHANGED");
    }
}

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

    public Task<MusicLyricLookupResult> GetLocalLyricsAsync(MusicSongRecord song, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            string key = DisplayKey(song);
            var existingSummary = doc.ReadLocalLyricSummaries(key);
            if (!Settings(doc).LyricsEnabled)
                return new MusicLyricLookupResult("disabled", [], Available: existingSummary.Count > 0);
            if (existingSummary.Count == 0)
                return new MusicLyricLookupResult("none", [], Available: false);
            var selected = doc.ReadLocalLyric(key);
            return selected is null
                ? new MusicLyricLookupResult("none", [], Available: false)
                : new MusicLyricLookupResult("local", selected.Lines, Available: true);
        }, cancellationToken);

    public Task<bool> SaveLocalLyricsAsync(MusicSongRecord song, string text, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(doc =>
        {
            string key = DisplayKey(song);
            RequireCurrentLyricSong(doc, key);
            MusicRules.Require(text is { Length: > 0 } && !text.Any(c => c < 32 && c is not ('\r' or '\n' or '\t')),
                "INVALID_LYRIC_RESPONSE");
            var lines = MusicLyricTiming.ParseLinesRecord(text, duration: song.Duration ?? 0);
            MusicRules.Require(lines.Length > 0, "INVALID_LYRIC_RESPONSE");
            string contentHash = MusicRules.HashText(text);
            var existing = doc.ReadLocalLyric(key);
            if (existing is not null && existing.ContentHash == contentHash && LocalLyricsEqual(existing.Lines, lines))
                return (true, false);

            int ordinal = existing?.Ordinal ?? NextLocalLyricOrdinal(doc);
            var next = new MusicLocalLyricRecord(key, ordinal, contentHash, lines);
            doc.StageLocalLyric(new MusicLocalLyricMutation(key, next, ordinal));
            return (true, true);
        }, cancellationToken, onCommitted: () => InvokeSafe(LocalLyricsChanged));

    public Task<bool> RemoveLocalLyricsAsync(MusicSongRecord song, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(doc =>
        {
            string key = DisplayKey(song);
            var existingSummary = doc.ReadLocalLyricSummaries(key);
            if (existingSummary.Count == 0)
                return (false, false);
            doc.StageLocalLyric(new MusicLocalLyricMutation(key, null, existingSummary[0].Ordinal));
            return (true, true);
        }, cancellationToken, onCommitted: () => InvokeSafe(LocalLyricsChanged));

    private static int NextLocalLyricOrdinal(MusicLibrarySession doc)
    {
        var all = doc.ReadLocalLyricSummaries();
        return all.Count == 0 ? 0 : all.Max(r => r.Ordinal) + 1;
    }

    private void RequireCurrentLyricSong(MusicLibrarySession doc, string key)
    {
        var queue = Queue(doc);
        var entry = queue.Entries.FirstOrDefault(row => row.EntryId == queue.CurrentId);
        MusicRules.Require(entry is not null && entry.SongId == key, "TARGET_CHANGED");
    }
}

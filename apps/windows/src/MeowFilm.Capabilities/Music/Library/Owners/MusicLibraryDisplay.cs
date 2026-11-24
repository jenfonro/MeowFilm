using System.Text;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Core.Debug;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private long lyricDisplaySaveSequence;
    private static string DisplayKey(MusicSongRecord song) => song.SongId;

    private static MusicLyricLineRecord[] ToLyricLineRecords(IReadOnlyList<CachedLyricLine> lines)
    {
        var result = new MusicLyricLineRecord[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            MusicLyricWordRecord[] words;
            if (line.Words is { Count: > 0 } cachedWords)
            {
                words = new MusicLyricWordRecord[cachedWords.Count];
                for (int j = 0; j < cachedWords.Count; j++)
                {
                    var w = cachedWords[j];
                    words[j] = new MusicLyricWordRecord(w.TimeSec, w.Text, w.DurationSec, w.OffsetSec);
                }
            }
            else words = [];
            result[i] = new MusicLyricLineRecord(line.TimeSec, line.Text, line.Translation, line.Romanization, line.EndSec, words);
        }
        return result;
    }

    private static CachedLyricLine[] ToCachedLines(IReadOnlyList<MusicLyricLineRecord> lines)
    {
        var result = new CachedLyricLine[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            List<CachedLyricWord>? words = null;
            if (line.Words.Length > 0)
            {
                words = new List<CachedLyricWord>(line.Words.Length);
                foreach (var w in line.Words)
                    words.Add(new CachedLyricWord(w.TimeSec, w.Text, w.DurationSec, w.OffsetSec));
            }
            result[i] = new CachedLyricLine(line.TimeSec, line.Text, line.Translation, line.Romanization, line.EndSec, words);
        }
        return result;
    }

    private static void ValidateCachedLines(IReadOnlyList<CachedLyricLine> lines)
    {
        MusicRules.Require(lines.Count > 0, "INVALID_LYRIC_RESPONSE");
        MusicRules.Require(lines.Count <= 10000, "LIMIT");
        double previous = -1;
        int characters = 0, wordCount = 0;

        void CheckText(string text)
        {
            MusicRules.Require(!text.Contains('\0'), "INVALID_LYRIC_RESPONSE");
            characters += text.Length;
            MusicRules.Require(text.Length <= 65536 && characters <= 4 * 65536, "LIMIT");
        }

        foreach (var line in lines)
        {
            MusicRules.Require(double.IsFinite(line.TimeSec) && line.TimeSec is >= 0 and <= 86400000, "INVALID_LYRIC_RESPONSE");
            MusicRules.Require(line.TimeSec >= previous, "INVALID_LYRIC_RESPONSE");
            previous = line.TimeSec;
            if (line.EndSec is { } end)
                MusicRules.Require(double.IsFinite(end) && end is >= 0 and <= 86400000 && end >= line.TimeSec, "INVALID_LYRIC_RESPONSE");
            CheckText(line.Text);
            if (line.Translation is not null) CheckText(line.Translation);
            if (line.Romanization is not null) CheckText(line.Romanization);
            if (line.Words is { } words)
            {
                foreach (var word in words)
                {
                    MusicRules.Require(++wordCount <= 65536, "LIMIT");
                    MusicRules.Require(double.IsFinite(word.TimeSec) && Math.Abs(word.TimeSec) <= 86400000, "INVALID_LYRIC_RESPONSE");
                    MusicRules.Require(double.IsFinite(word.DurationSec) && word.DurationSec is >= 0 and <= 86400000, "INVALID_LYRIC_RESPONSE");
                    CheckText(word.Text);
                    if (word.OffsetSec is { } off)
                        MusicRules.Require(double.IsFinite(off) && Math.Abs(off) <= 86400000, "INVALID_LYRIC_RESPONSE");
                }
            }
        }
    }

    private static int EstimateDisplayBytes(string sourceKey, IReadOnlyList<CachedLyricLine> lines)
    {
        int bytes = 32 + Encoding.UTF8.GetByteCount(sourceKey);
        foreach (var line in lines)
        {
            bytes += 48 + Encoding.UTF8.GetByteCount(line.Text);
            if (line.Translation is not null) bytes += 18 + Encoding.UTF8.GetByteCount(line.Translation);
            if (line.Romanization is not null) bytes += 18 + Encoding.UTF8.GetByteCount(line.Romanization);
            if (line.Words is { } words)
                foreach (var w in words)
                    bytes += 48 + Encoding.UTF8.GetByteCount(w.Text);
        }
        return bytes;
    }

    public async Task<MusicLyricLookupResult> GetDisplayLyricsAsync(MusicSongRecord song, string? requestedSourceKey = null,
        CancellationToken token = default)
    {
        bool entered = false;
        try
        {
            lock (repositoryGate) MusicRules.Require(!closed && !preparing, "CLOSED");
            var normalized = NativeMusicSong.Normalize(song);
            string key = DisplayKey(normalized);
            if (requestedSourceKey is not null)
                MusicRules.Require(normalized.Sources.Any(source => source.SourceKey == requestedSourceKey), "TARGET_CHANGED");

            CachedLyricsRecord? saved = null;
            var presentations = PresentationCache;
            for (;;)
            {
                long version = presentations.LyricVersion;
                try { saved = key.Length == 0 ? null : await presentations.ReadLyricsAsync(key, token).ConfigureAwait(false); }
                catch (StorageException error)
                {
                    DebugLog.Warn(DebugCategories.MusicPlayback, "lyrics", "display_lyric_cache_read_failed",
                        new { songId = key, error = error.Error.ToString() });
                    saved = null;
                }
                await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
                // A save completed while this individual read or gate wait
                // was in flight wins. Re-read only that song, outside the gate.
                if (key.Length == 0 || version == presentations.LyricVersion) break;
                gate.Release(); entered = false;
            }
            MusicRules.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitReadLockedAsync(null, token).ConfigureAwait(false);
            var doc = session!;

            try
            {
                if (saved is not null) ValidateCachedLines(saved.Lines);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is StorageException or MusicLibraryException)
            {
                DebugLog.Warn(DebugCategories.MusicPlayback, "lyrics", "display_lyric_cache_invalid",
                    new { songId = key, error = error.Message });
                saved = null;
            }

            if (!Settings(doc).LyricsEnabled)
                return new MusicLyricLookupResult("disabled", []);
            if (saved is null || requestedSourceKey is not null && saved.SourceKey != requestedSourceKey)
                return new MusicLyricLookupResult("none", []);
            if (!MetadataSourceEnabled(doc, normalized, saved.SourceKey))
            {
                bool alternative = requestedSourceKey is null && normalized.Sources.Any(s => MetadataSourceEnabled(doc, normalized, s.SourceKey));
                return new MusicLyricLookupResult(alternative ? "none" : "disabled", []);
            }
            return new MusicLyricLookupResult("restored", ToLyricLineRecords(saved.Lines), saved.SourceKey);
        }
        catch (ObjectDisposedException) when (closed) { throw new MusicLibraryException("CLOSED"); }
        finally { if (entered) gate.Release(); }
    }

    public async Task<bool> SaveDisplayLyricsAsync(MusicSongRecord song, string sourceKey, IReadOnlyList<MusicLyricLineRecord> lines,
        CancellationToken token = default)
    {
        bool entered = false;
        try
        {
            lock (repositoryGate) MusicRules.Require(!closed && !preparing, "CLOSED");
            MusicRules.Require(!string.IsNullOrEmpty(sourceKey), "TARGET_CHANGED");
            var normalized = NativeMusicSong.Normalize(song);
            string key = DisplayKey(normalized);
            MusicRules.Require(normalized.Sources.Any(source => source.SourceKey == sourceKey), "TARGET_CHANGED");

            var presentations = PresentationCache;
            await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
            MusicRules.Require(!closed && !preparing, "CLOSED");
            long cacheEpoch = presentations.LyricEpoch;
            long saveSequence = ++lyricDisplaySaveSequence;
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitWriteLockedAsync(token).ConfigureAwait(false);
            var doc = session!;

            MusicRules.Require(Settings(doc).LyricsEnabled && MetadataSourceEnabled(doc, normalized, sourceKey), "DISABLED");
            RequireCurrentLyricSong(doc, key);
            var cached = ToCachedLines(lines);
            ValidateCachedLines(cached);
            MusicRules.Require(EstimateDisplayBytes(sourceKey, cached) <= MusicPresentationCache.MaximumSingleLyricBytes, "LIMIT");
            gate.Release(); entered = false;
            try
            {
                await presentations.SaveLyricsAsync(key, sourceKey, cached, cacheEpoch, saveSequence, token).ConfigureAwait(false);
            }
            catch (StorageException error)
            {
                DebugLog.Warn(DebugCategories.MusicPlayback, "lyrics", "display_lyric_cache_save_failed",
                    new { songId = key, error = error.Error.ToString() });
            }
            return true;
        }
        catch (ObjectDisposedException) when (closed) { throw new MusicLibraryException("CLOSED"); }
        finally { if (entered) gate.Release(); }
    }
}

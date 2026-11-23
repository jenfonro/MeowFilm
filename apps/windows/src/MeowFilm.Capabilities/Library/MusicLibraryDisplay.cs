using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static string DisplayKey(MusicSongRecord song) => song.SongId;

    private static CachedLyricLine[] ReadAndValidateDisplayLines(JsonElement value)
    {
        MusicRules.Require(value.ValueKind == JsonValueKind.Array, "INVALID_LYRIC_RESPONSE");
        int count = value.GetArrayLength();
        MusicRules.Require(count is > 0 and <= 10000, "INVALID_LYRIC_RESPONSE");
        var decoded = new CachedLyricLine[count];
        double previous = -1;
        int characters = 0, wordCount = 0;

        string ReadText(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.String || el.GetString() is not { } text || text.Contains('\0'))
                throw new MusicLibraryException("INVALID_LYRIC_RESPONSE");
            characters += text.Length;
            MusicRules.Require(text.Length <= 65536 && characters <= 4 * 65536, "LIMIT");
            return text;
        }

        double ReadNumber(JsonElement el, bool signed = false)
        {
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var number) ||
                !double.IsFinite(number) || Math.Abs(number) > 86400000 || (!signed && number < 0))
                throw new MusicLibraryException("INVALID_LYRIC_RESPONSE");
            return number;
        }

        int i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("time", out var timeEl) ||
                !item.TryGetProperty("text", out var textEl))
                throw new MusicLibraryException("INVALID_LYRIC_RESPONSE");
            double time = ReadNumber(timeEl);
            MusicRules.Require(time >= previous, "INVALID_LYRIC_RESPONSE");
            previous = time;

            double? end = null;
            if (item.TryGetProperty("end", out var endEl) && endEl.ValueKind != JsonValueKind.Null)
            {
                double seconds = ReadNumber(endEl);
                MusicRules.Require(seconds >= time, "INVALID_LYRIC_RESPONSE");
                end = seconds;
            }

            string text = ReadText(textEl);
            string? translation = item.TryGetProperty("translation", out var trEl) && trEl.ValueKind != JsonValueKind.Null
                ? ReadText(trEl) : null;
            string? romanization = item.TryGetProperty("romanization", out var roEl) && roEl.ValueKind != JsonValueKind.Null
                ? ReadText(roEl) : null;

            List<CachedLyricWord>? words = null;
            if (item.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind != JsonValueKind.Null)
            {
                MusicRules.Require(wordsEl.ValueKind == JsonValueKind.Array, "INVALID_LYRIC_RESPONSE");
                words = new List<CachedLyricWord>(wordsEl.GetArrayLength());
                foreach (var wordEl in wordsEl.EnumerateArray())
                {
                    MusicRules.Require(++wordCount <= 65536, "LIMIT");
                    if (wordEl.ValueKind != JsonValueKind.Object ||
                        !wordEl.TryGetProperty("time", out var wtEl) ||
                        !wordEl.TryGetProperty("duration", out var wdEl) ||
                        !wordEl.TryGetProperty("text", out var wtxtEl))
                        throw new MusicLibraryException("INVALID_LYRIC_RESPONSE");
                    // Negative word timestamps can occur with a negative LRC
                    // offset. They are valid, but never become a negative seek.
                    double wt = ReadNumber(wtEl, signed: true);
                    double wd = ReadNumber(wdEl);
                    string wtxt = ReadText(wtxtEl);
                    double? woff = wordEl.TryGetProperty("offset", out var woEl) && woEl.ValueKind != JsonValueKind.Null
                        ? ReadNumber(woEl, signed: true) : null;
                    words.Add(new CachedLyricWord(wt, wtxt, wd, woff));
                }
            }

            decoded[i++] = new CachedLyricLine(time, text, translation, romanization, end, words);
        }
        return decoded;
    }

    private static void ValidateCachedLines(IReadOnlyList<CachedLyricLine> lines)
    {
        MusicRules.Require(lines.Count is > 0 and <= 10000, "INVALID_LYRIC_RESPONSE");
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

    private async Task<JsonElement> ExecuteLyricDisplayAsync(string method, object? parameters, CancellationToken token)
    {
        bool entered = false;
        try
        {
            lock (repositoryGate) MusicRules.Require(!closed && !preparing, "CLOSED");
            bool save = method == "music.lyrics.display.save";
            MusicRules.Require(save || method == "music.lyrics.display.get", "CAPABILITY_UNAVAILABLE");
            var p = MusicWireCodec.ParseParameters(parameters);
            MusicWireCodec.RequireFields(p, save ? "song sourceKey lines" : "song");
            var song = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(p.GetProperty("song")));
            string key = DisplayKey(song);
            string? requested = null;
            if (MusicWireCodec.TryGet(p, "sourceKey", out var reqEl) && reqEl.ValueKind != JsonValueKind.Null)
                requested = MusicWireCodec.RequireStr(reqEl);
            if (requested is not null)
                MusicRules.Require(song.Sources.Any(source => source.SourceKey == requested), "TARGET_CHANGED");

            CachedLyricsRecord? saved = null;
            var presentations = PresentationCache;
            long cacheEpoch = 0;
            if (save)
            {
                await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
                MusicRules.Require(!closed && !preparing, "CLOSED");
                cacheEpoch = presentations.LyricEpoch;
            }
            else
            {
                for (;;)
                {
                    long version = presentations.LyricVersion;
                    try { saved = key.Length == 0 ? null : await presentations.ReadLyricsAsync(key, token).ConfigureAwait(false); }
                    catch (StorageException) { saved = null; }
                    await EnterAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding); entered = true;
                    // A save completed while this individual read or gate wait
                    // was in flight wins. Re-read only that song, outside the gate.
                    if (key.Length == 0 || version == presentations.LyricVersion) break;
                    gate.Release(); entered = false;
                }
            }
            MusicRules.Require(!closed && !preparing, "CLOSED");
            token.ThrowIfCancellationRequested();
            await LoadAsync(token).ConfigureAwait(false);
            await AdmitCommandLockedAsync(method, token).ConfigureAwait(false);
            var doc = session!;

            if (!save)
            {
                try
                {
                    if (saved is not null) ValidateCachedLines(saved.Lines);
                }
                catch (OperationCanceledException) { throw; }
                catch (StorageException) { saved = null; }
                catch (MusicLibraryException) { saved = null; }

                if (!Settings(doc).LyricsEnabled)
                {
                    return MusicWireCodec.Ok(w =>
                    {
                        w.WriteString("state", "disabled");
                        w.WritePropertyName("lines"); w.WriteStartArray(); w.WriteEndArray();
                    });
                }
                if (saved is null || requested is not null && saved.SourceKey != requested)
                {
                    return MusicWireCodec.Ok(w =>
                    {
                        w.WriteString("state", "none");
                        w.WritePropertyName("lines"); w.WriteStartArray(); w.WriteEndArray();
                    });
                }
                if (!MetadataSourceEnabled(doc, song, saved.SourceKey))
                {
                    bool alternative = requested is null && song.Sources.Any(s => MetadataSourceEnabled(doc, song, s.SourceKey));
                    return MusicWireCodec.Ok(w =>
                    {
                        w.WriteString("state", alternative ? "none" : "disabled");
                        w.WritePropertyName("lines"); w.WriteStartArray(); w.WriteEndArray();
                    });
                }
                return MusicWireCodec.Ok(w =>
                {
                    w.WriteString("state", "restored");
                    w.WriteString("sourceKey", saved.SourceKey);
                    w.WritePropertyName("lines");
                    MusicWireCodec.WriteCachedLyricLines(w, saved.Lines);
                });
            }
            else
            {
                MusicRules.Require(Settings(doc).LyricsEnabled && requested is not null &&
                    MetadataSourceEnabled(doc, song, requested), "DISABLED");
                RequireCurrentLyricSong(doc, key);
                var lines = ReadAndValidateDisplayLines(p.GetProperty("lines"));
                MusicRules.Require(EstimateDisplayBytes(requested!, lines) <= 512 * 1024, "LIMIT");
                gate.Release(); entered = false;
                try
                {
                    await presentations.SaveLyricsAsync(key, requested!, lines, cacheEpoch, token).ConfigureAwait(false);
                }
                catch (StorageException) { System.Diagnostics.Trace.TraceWarning("LYRIC_DISPLAY_CACHE_UNAVAILABLE"); }
                return MusicWireCodec.Ok(w => w.WriteBoolean("saved", true));
            }
        }
        catch (OperationCanceledException) { return MusicWireCodec.Fail("CANCELLED"); }
        catch (ObjectDisposedException) when (closed) { return MusicWireCodec.Fail("CLOSED"); }
        catch (MusicLibraryException error) { return MusicWireCodec.Fail(error.Code); }
        catch (StorageException error) { return MusicWireCodec.Fail(error.Error == StorageError.Unavailable ? "STORAGE_UNAVAILABLE" : "STORAGE"); }
        catch { return MusicWireCodec.Fail("INVALID_REQUEST"); }
        finally { if (entered) gate.Release(); }
    }
}

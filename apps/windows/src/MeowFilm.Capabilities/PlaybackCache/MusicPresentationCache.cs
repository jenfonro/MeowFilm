using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>Original image files and normalized lyric rows share an 8 MiB logical content budget.</summary>
internal sealed class MusicPresentationCache(IStorageDatabase database, TimeProvider? time = null) : IArtworkByteCache, IAsyncDisposable
{
    internal const string Namespace = "music.presentation-resources.v1";
    internal const long Ttl = 30L * 86400000;
    private const long MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumPendingImages = 128, MaximumPendingImageBytes = 16 * 1024 * 1024;
    private const string ImageOwner = Namespace + ".images", LyricOwner = Namespace + ".lyrics";
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object gate = new();
    private RawResourceDirectory? files;
    private StorageRevision imageRevision = StorageRevision.Empty, lyricRevision = StorageRevision.Empty;
    private long imageEpoch, lyricEpoch, lyricVersion;
    private int pendingImages, pendingImageBytes;
    private bool imagesReadable = true, lyricsReadable = true, closed;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    public long Epoch { get { lock (gate) return imageEpoch; } }
    internal long LyricEpoch { get { lock (gate) return lyricEpoch; } }
    internal long LyricVersion { get { lock (gate) return lyricVersion; } }
    public TimeSpan Lifetime => TimeSpan.FromMilliseconds(Ttl);
    internal static string SongKey(JsonObject song) => LJ.S(song["songId"]);
    internal async Task LoadAsync(CancellationToken token)
    {
        await database.ReadSnapshotAsync(db =>
        {
            lock (gate)
            {
                if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
                var images = db.Revision(ImageOwner); var lyrics = db.Revision(LyricOwner);
                if (images != imageRevision) { imageRevision = images; imageEpoch++; }
                if (lyrics != lyricRevision) { lyricRevision = lyrics; lyricEpoch++; lyricVersion++; }
            }
            return true;
        }, token).ConfigureAwait(false);
    }
    private RawResourceDirectory Files => files ??= new(database.ProductRoot ?? throw new DocumentStoreException(DocumentStoreError.Unavailable), "music-images");
    internal async Task<JsonObject?> ReadLyricsAsync(string songId, CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        var epoch = LyricEpoch;
        lock (gate) if (closed || !lyricsReadable) return null;
        try
        {
            var record = await database.ReadSnapshotAsync(db => LyricsCacheStore.ReadRecord(db, songId), token).ConfigureAwait(false);
            if (record is null || record.UpdatedAt > Now || record.UpdatedAt <= Now - Ttl) return null;
            lock (gate) if (closed || !lyricsReadable || epoch != lyricEpoch) return null;
            return LyricsToJson(record);
        }
        catch (OperationCanceledException) { throw; }
        catch (DocumentStoreException) { return null; }
    }
    public async Task<ArtworkBytes?> ReadAsync(string address, CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        var epoch = Epoch;
        lock (gate) if (closed || !imagesReadable) return null;
        try
        {
            var result = await database.ReadAsync(db =>
            {
                lock (gate) if (closed || !imagesReadable || epoch != imageEpoch) return null;
                var rows = db.Query("""
                    SELECT filename,bytes,updated_at_ms
                    FROM cached_images
                    WHERE resource_key=$address AND ready=1 AND updated_at_ms>$expired AND updated_at_ms<=$now
                    """,
                    row => (Id: row.GetString(0), Bytes: row.GetInt32(1), At: row.GetInt64(2)), ("$address", address), ("$expired", Now - Ttl), ("$now", Now));
                return rows.Count == 0 ? null : new ArtworkBytes(Files.Read(rows[0].Id, rows[0].Bytes), DateTimeOffset.FromUnixTimeMilliseconds(rows[0].At + Ttl));
            }, token).ConfigureAwait(false);
            lock (gate) return closed || !imagesReadable || epoch != imageEpoch ? null : result;
        }
        catch (OperationCanceledException) { throw; }
        catch (DocumentStoreException) { return null; }
    }
    private void Guard(DatabaseSession db, long epoch, bool image)
    {
        lock (gate)
        {
            if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
            if (epoch != (image ? imageEpoch : lyricEpoch) || db.Revision(image ? ImageOwner : LyricOwner) != (image ? imageRevision : lyricRevision))
                throw new DocumentStoreException(DocumentStoreError.Conflict);
        }
    }
    public async Task WriteAsync(string address, ReadOnlyMemory<byte> bytes, long epoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length is <= 0 or > ArtworkService.MaximumCacheEntryBytes) throw new DocumentStoreException(DocumentStoreError.Limit);
        lock (gate)
        {
            if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
            if (pendingImages == MaximumPendingImages || pendingImageBytes > MaximumPendingImageBytes - bytes.Length)
                throw new DocumentStoreException(DocumentStoreError.Limit);
            pendingImages++; pendingImageBytes += bytes.Length;
        }
        try { await WriteImageAsync(address, bytes, epoch, token).ConfigureAwait(false); }
        finally { lock (gate) { pendingImages--; pendingImageBytes -= bytes.Length; } }
    }
    private async Task WriteImageAsync(string address, ReadOnlyMemory<byte> bytes, long epoch, CancellationToken token)
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        string? created = null;
        try
        {
            var owned = bytes.ToArray();
            await database.WriteAsync(db =>
            {
                Guard(db, epoch, true);
                lock (gate) if (!imagesReadable) db.Execute("DELETE FROM cached_images");
                var file = Files.Write(owned); if (file.Created) created = file.Id;
                db.Execute("DELETE FROM cached_images WHERE resource_key=$address AND filename<>$file",
                    ("$address", address), ("$file", file.Id));
                db.Execute("""
                    INSERT INTO cached_images(resource_key,filename,bytes,ready,updated_at_ms)
                    VALUES($address,$file,$bytes,1,$at)
                    ON CONFLICT(resource_key,filename) DO UPDATE SET
                        bytes=excluded.bytes,
                        ready=1,
                        updated_at_ms=excluded.updated_at_ms
                    """,
                    ("$address", address), ("$file", file.Id), ("$bytes", bytes.Length), ("$at", Now));
                Trim(db); return true;
            }, token, bytes.Length, commit: commit => { lock (gate) { if (closed || imageEpoch != epoch) throw new DocumentStoreException(DocumentStoreError.Conflict); commit(); } }).ConfigureAwait(false);
            created = null;
            lock (gate) if (imageEpoch == epoch) imagesReadable = true;
            await CleanupFilesAsync().ConfigureAwait(false);
        }
        catch
        {
            if (created is { } id) { try { Files.Delete(id); } catch { } }
            throw;
        }
        finally { writes.Release(); }
    }
    internal async Task SaveLyricsAsync(string songId, string sourceKey, JsonArray lines, long epoch, CancellationToken token)
    {
        long now = Now;
        int bytes = EstimateLyricsBytes(sourceKey, lines);
        if (bytes > 512 * 1024) throw new DocumentStoreException(DocumentStoreError.Limit);
        var record = LyricsFromJson(sourceKey, now, bytes, lines);
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await database.WriteAsync(db =>
            {
                Guard(db, epoch, false);
                lock (gate) if (!lyricsReadable) db.Execute("DELETE FROM cached_music_lyrics");
                LyricsCacheStore.SaveRecord(db, songId, record);
                Trim(db); return true;
            }, token, bytes, commit: commit => { lock (gate) { if (closed || lyricEpoch != epoch) throw new DocumentStoreException(DocumentStoreError.Conflict); commit(); } }).ConfigureAwait(false);
            lock (gate) { lyricVersion++; if (lyricEpoch == epoch) lyricsReadable = true; }
        }
        finally { writes.Release(); }
    }
    private static int EstimateLyricsBytes(string sourceKey, JsonArray lines)
    {
        int bytes = 48 + Encoding.UTF8.GetByteCount(sourceKey);
        foreach (var item in lines)
        {
            if (item is not JsonObject line) continue;
            bytes += 48;
            if (line["text"] is JsonValue tv && tv.TryGetValue<string>(out var text)) bytes += Encoding.UTF8.GetByteCount(text);
            if (line["translation"] is JsonValue trv && trv.TryGetValue<string>(out var tr)) bytes += 16 + Encoding.UTF8.GetByteCount(tr);
            if (line["romanization"] is JsonValue rmv && rmv.TryGetValue<string>(out var rm)) bytes += 16 + Encoding.UTF8.GetByteCount(rm);
            if (line["words"] is JsonArray words)
            {
                foreach (var wItem in words)
                {
                    if (wItem is not JsonObject word) continue;
                    bytes += 32;
                    if (word["text"] is JsonValue wv && wv.TryGetValue<string>(out var wt)) bytes += Encoding.UTF8.GetByteCount(wt);
                }
            }
        }
        return bytes;
    }
    private void Trim(DatabaseSession db)
    {
        long now = Now;
        db.Execute("DELETE FROM cached_images WHERE updated_at_ms<=$expired OR updated_at_ms>$now",
            ("$expired", now - Ttl), ("$now", now));
        db.Execute("DELETE FROM cached_music_lyrics WHERE updated_at_ms<=$expired OR updated_at_ms>$now",
            ("$expired", now - Ttl), ("$now", now));
        while (db.Scalar<long>("SELECT (SELECT coalesce(sum(bytes),0) FROM cached_images WHERE ready=1)+(SELECT coalesce(sum(stored_bytes),0) FROM cached_music_lyrics)") > MaximumBytes)
        {
            var oldest = db.Query("""
                SELECT 'image',resource_key,updated_at_ms FROM cached_images WHERE ready=1
                UNION ALL
                SELECT 'lyrics',song_id,updated_at_ms FROM cached_music_lyrics
                ORDER BY 3,1,2 LIMIT 1
                """,
                row => (Kind: row.GetString(0), Key: row.GetString(1))).Single();
            if (oldest.Kind == "image") db.Execute("DELETE FROM cached_images WHERE resource_key=$key", ("$key", oldest.Key));
            else db.Execute("DELETE FROM cached_music_lyrics WHERE song_id=$key", ("$key", oldest.Key));
        }
    }
    private Task CleanupFilesAsync() => database.ReadAsync(db =>
    {
        if (files is not null) files.RemoveUnreferenced(db.Query("SELECT DISTINCT filename FROM cached_images", row => row.GetString(0)).ToHashSet(StringComparer.Ordinal));
        return true;
    });
    public async Task RemoveAsync(string address, long epoch, CancellationToken token)
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await database.WriteAsync(db => { Guard(db, epoch, true); db.Execute("DELETE FROM cached_images WHERE resource_key=$address", ("$address", address)); return true; }, token).ConfigureAwait(false);
            await CleanupFilesAsync().ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
    internal Task ClearImagesAsync(CancellationToken token) => ClearAsync(false, token);
    internal Task ClearAllAsync(CancellationToken token) => ClearAsync(true, token);
    private async Task ClearAsync(bool lyrics, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) { imageEpoch++; imagesReadable = false; if (lyrics) { lyricEpoch++; lyricVersion++; lyricsReadable = false; } }
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result = await database.WriteAsync(db =>
            {
                db.Execute("DELETE FROM cached_images");
                var image = db.CommitRevision(ImageOwner, db.Revision(ImageOwner));
                var lyric = db.Revision(LyricOwner);
                if (lyrics) { db.Execute("DELETE FROM cached_music_lyrics"); lyric = db.CommitRevision(LyricOwner, lyric); }
                return (image, lyric);
            }, token).ConfigureAwait(false);
            lock (gate) { imageRevision = result.image; imagesReadable = true; if (lyrics) { lyricRevision = result.lyric; lyricsReadable = true; } }
            await CleanupFilesAsync().ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
    internal async Task TouchPlayedAsync(string songId, string image, CancellationToken token)
    {
        await database.WriteAsync(db =>
        {
            long now = Now;
            db.Execute("UPDATE cached_music_lyrics SET updated_at_ms=$now WHERE song_id=$id", ("$now", now), ("$id", songId));
            if (image.Length > 0) db.Execute("UPDATE cached_images SET updated_at_ms=$now WHERE resource_key=$address AND ready=1", ("$now", now), ("$address", image));
            return true;
        }, token).ConfigureAwait(false);
    }
    internal async Task DrainAsync()
    {
        await writes.WaitAsync().ConfigureAwait(false);
        try { await database.DrainAsync().ConfigureAwait(false); }
        finally { writes.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { closed = true; imageEpoch++; lyricEpoch++; }
        await writes.WaitAsync().ConfigureAwait(false);
        try { await database.DrainAsync().ConfigureAwait(false); }
        finally { files?.Dispose(); files = null; writes.Release(); }
    }

    private static JsonObject LyricsToJson(CachedLyricsRecord rec)
    {
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
            ["lines"] = lines
        };
    }

    private static CachedLyricsRecord LyricsFromJson(string sourceKey, long updatedAt, int storedBytes, JsonArray lines)
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
        return new CachedLyricsRecord(sourceKey, updatedAt, storedBytes, parsed);
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

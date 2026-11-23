using System.Text;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>Original image files and normalized lyric rows share an 8 MiB logical content budget.</summary>
internal sealed class MusicPresentationCache(IStorageDatabase database) : IArtworkByteCache, IAsyncDisposable
{
    internal const string StorageOwner = "music.presentation-resources.v1";
    internal const long Ttl = 30L * 86400000;
    private const long MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumPendingImages = 128, MaximumPendingImageBytes = 16 * 1024 * 1024;
    private const string ImageOwner = StorageOwner + ".images", LyricOwner = StorageOwner + ".lyrics";
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object gate = new();
    private RawResourceDirectory? files;
    private StorageRevision imageRevision = StorageRevision.Empty, lyricRevision = StorageRevision.Empty;
    private bool imageRevisionLoaded, lyricRevisionLoaded;
    private readonly Dictionary<string, long> lyricSaveSequences = new(StringComparer.Ordinal);
    private long imageEpoch, lyricEpoch, lyricVersion;
    private int pendingImages, pendingImageBytes;
    private bool imagesReadable = true, lyricsReadable = true, closed;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long Epoch { get { lock (gate) return imageEpoch; } }
    internal long LyricEpoch { get { lock (gate) return lyricEpoch; } }
    internal long LyricVersion { get { lock (gate) return lyricVersion; } }
    public TimeSpan Lifetime => TimeSpan.FromMilliseconds(Ttl);

    private RawResourceDirectory Files => files ??= new(database.ProductRoot ?? throw new StorageException(StorageError.Unavailable), "music-images");

    internal async Task<CachedLyricsRecord?> ReadLyricsAsync(string songId, CancellationToken token)
    {
        try
        {
            return await database.ReadSnapshotAsync(db =>
            {
                long epoch;
                lock (gate)
                {
                    if (closed) throw new StorageException(StorageError.Closed);
                    var lyrics = db.Revision(LyricOwner);
                    if (!lyricRevisionLoaded || lyrics != lyricRevision)
                    {
                        bool changed = lyricRevisionLoaded && lyrics != lyricRevision;
                        lyricRevision = lyrics; lyricRevisionLoaded = true;
                        if (changed) { lyricEpoch++; lyricVersion++; }
                    }
                    if (!lyricsReadable) return null;
                    epoch = lyricEpoch;
                }
                long now = Now;
                var record = LyricsCacheStore.ReadRecord(db, songId);
                if (record is null || record.UpdatedAt > now || record.UpdatedAt <= now - Ttl) return null;
                lock (gate) return closed || !lyricsReadable || epoch != lyricEpoch ? null : record;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (StorageException) { return null; }
    }

    public async Task<ArtworkBytes?> ReadAsync(string address, CancellationToken token)
    {
        try
        {
            return await database.ReadAsync(db =>
            {
                long epoch;
                lock (gate)
                {
                    if (closed) throw new StorageException(StorageError.Closed);
                    var images = db.Revision(ImageOwner);
                    if (!imageRevisionLoaded || images != imageRevision)
                    {
                        bool changed = imageRevisionLoaded && images != imageRevision;
                        imageRevision = images; imageRevisionLoaded = true;
                        if (changed) imageEpoch++;
                    }
                    if (!imagesReadable) return null;
                    epoch = imageEpoch;
                }
                long now = Now;
                var rows = db.Query("""
                    SELECT filename,bytes,updated_at_ms
                    FROM cached_images
                    WHERE resource_key=$address AND ready=1 AND updated_at_ms>$expired AND updated_at_ms<=$now
                    """,
                    row => (Id: row.GetString(0), Bytes: row.GetInt32(1), At: row.GetInt64(2)), ("$address", address), ("$expired", now - Ttl), ("$now", now));
                var result = rows.Count == 0 ? null : new ArtworkBytes(Files.Read(rows[0].Id, rows[0].Bytes), DateTimeOffset.FromUnixTimeMilliseconds(rows[0].At + Ttl));
                lock (gate) return closed || !imagesReadable || epoch != imageEpoch ? null : result;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (StorageException) { return null; }
    }

    private void Guard(DatabaseSession db, long epoch, bool image)
    {
        lock (gate)
        {
            if (closed) throw new StorageException(StorageError.Closed);
            if (epoch != (image ? imageEpoch : lyricEpoch))
                throw new StorageException(StorageError.Conflict);
            var current = db.Revision(image ? ImageOwner : LyricOwner);
            if (image)
            {
                if (!imageRevisionLoaded) { imageRevision = current; imageRevisionLoaded = true; }
                else if (current != imageRevision) throw new StorageException(StorageError.Conflict);
            }
            else
            {
                if (!lyricRevisionLoaded) { lyricRevision = current; lyricRevisionLoaded = true; }
                else if (current != lyricRevision) throw new StorageException(StorageError.Conflict);
            }
        }
    }

    public async Task WriteAsync(string address, ReadOnlyMemory<byte> bytes, long epoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length is <= 0 or > ArtworkService.MaximumCacheEntryBytes) throw new StorageException(StorageError.Limit);
        lock (gate)
        {
            if (closed) throw new StorageException(StorageError.Closed);
            if (pendingImages == MaximumPendingImages || pendingImageBytes > MaximumPendingImageBytes - bytes.Length)
                throw new StorageException(StorageError.Limit);
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
            }, token, bytes.Length, commit: commit => { lock (gate) { if (closed || imageEpoch != epoch) throw new StorageException(StorageError.Conflict); commit(); } }).ConfigureAwait(false);
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

    internal async Task SaveLyricsAsync(string songId, string sourceKey, IReadOnlyList<CachedLyricLine> lines, long epoch, long sequence, CancellationToken token)
    {
        long now = Now;
        int bytes = EstimateLyricsBytes(sourceKey, lines);
        if (bytes > 512 * 1024) throw new StorageException(StorageError.Limit);
        var record = new CachedLyricsRecord(sourceKey, now, bytes, lines);
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (closed) throw new StorageException(StorageError.Closed);
                if (lyricEpoch != epoch) throw new StorageException(StorageError.Conflict);
                if (lyricSaveSequences.TryGetValue(songId, out var latest) && sequence <= latest) return;
            }
            bool evictedImages = await database.WriteAsync(db =>
            {
                Guard(db, epoch, false);
                lock (gate) if (!lyricsReadable) db.Execute("DELETE FROM cached_music_lyrics");
                LyricsCacheStore.SaveRecord(db, songId, record);
                return Trim(db);
            }, token, bytes, commit: commit => { lock (gate) { if (closed || lyricEpoch != epoch) throw new StorageException(StorageError.Conflict); commit(); } }).ConfigureAwait(false);
            lock (gate)
            {
                lyricVersion++;
                if (lyricEpoch == epoch)
                {
                    lyricsReadable = true;
                    if (!lyricSaveSequences.TryGetValue(songId, out var latest) || sequence > latest)
                        lyricSaveSequences[songId] = sequence;
                }
            }
            if (evictedImages) await CleanupFilesAsync().ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }

    internal static int EstimateLyricsBytes(string sourceKey, IReadOnlyList<CachedLyricLine> lines)
    {
        int bytes = 48 + Encoding.UTF8.GetByteCount(sourceKey);
        foreach (var line in lines)
        {
            bytes += 48 + Encoding.UTF8.GetByteCount(line.Text);
            if (line.Translation is not null) bytes += 16 + Encoding.UTF8.GetByteCount(line.Translation);
            if (line.Romanization is not null) bytes += 16 + Encoding.UTF8.GetByteCount(line.Romanization);
            if (line.Words is { Count: > 0 } words)
            {
                foreach (var word in words)
                    bytes += 32 + Encoding.UTF8.GetByteCount(word.Text);
            }
        }
        return bytes;
    }

    private bool Trim(DatabaseSession db)
    {
        long now = Now;
        bool evictedImages = db.Execute("DELETE FROM cached_images WHERE updated_at_ms<=$expired OR updated_at_ms>$now",
            ("$expired", now - Ttl), ("$now", now)) > 0;
        db.Execute("DELETE FROM cached_music_lyrics WHERE updated_at_ms<=$expired OR updated_at_ms>$now",
            ("$expired", now - Ttl), ("$now", now));
        long total = db.Scalar<long>("SELECT (SELECT coalesce(sum(bytes),0) FROM cached_images WHERE ready=1)+(SELECT coalesce(sum(stored_bytes),0) FROM cached_music_lyrics)");
        if (total > MaximumBytes)
        {
            foreach (var entry in db.Query("""
                SELECT 'image',resource_key,updated_at_ms,bytes FROM cached_images WHERE ready=1
                UNION ALL
                SELECT 'lyrics',song_id,updated_at_ms,stored_bytes FROM cached_music_lyrics
                ORDER BY 3,2,1
                """,
                row => (Kind: row.GetString(0), Key: row.GetString(1), Bytes: row.GetInt64(3))))
            {
                if (total <= MaximumBytes) break;
                int removed = entry.Kind == "image"
                    ? db.Execute("DELETE FROM cached_images WHERE resource_key=$key", ("$key", entry.Key))
                    : db.Execute("DELETE FROM cached_music_lyrics WHERE song_id=$key", ("$key", entry.Key));
                if (removed > 0)
                {
                    if (entry.Kind == "image") evictedImages = true;
                    total -= entry.Bytes;
                }
            }
        }
        return evictedImages;
    }

    private Task CleanupFilesAsync() => database.ReadAsync(db =>
    {
        RawResourceDirectory? dir;
        lock (gate)
        {
            if (closed) return true;
            dir = files ?? (database.ProductRoot is not null ? Files : null);
        }
        dir?.RemoveUnreferenced(db.Query("SELECT DISTINCT filename FROM cached_images", row => row.GetString(0)).ToHashSet(StringComparer.Ordinal));
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

    internal async Task ClearAllAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) { imageEpoch++; imagesReadable = false; lyricEpoch++; lyricVersion++; lyricsReadable = false; lyricSaveSequences.Clear(); }
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result = await database.WriteAsync(db =>
            {
                db.Execute("DELETE FROM cached_images");
                var image = db.CommitRevision(ImageOwner, db.Revision(ImageOwner));
                db.Execute("DELETE FROM cached_music_lyrics");
                var lyric = db.CommitRevision(LyricOwner, db.Revision(LyricOwner));
                return (image, lyric);
            }, token).ConfigureAwait(false);
            lock (gate)
            {
                imageRevision = result.image; imageRevisionLoaded = true; imagesReadable = true;
                lyricRevision = result.lyric; lyricRevisionLoaded = true; lyricsReadable = true;
            }
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

    public async ValueTask DisposeAsync()
    {
        lock (gate) { closed = true; imageEpoch++; lyricEpoch++; }
        await writes.WaitAsync().ConfigureAwait(false);
        try { await database.DrainAsync().ConfigureAwait(false); }
        finally { files = null; writes.Release(); }
    }
}

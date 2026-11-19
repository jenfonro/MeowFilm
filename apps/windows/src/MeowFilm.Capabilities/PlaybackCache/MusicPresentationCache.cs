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
    private long imageEpoch, lyricEpoch;
    private int pendingImages, pendingImageBytes;
    private bool imagesReadable = true, lyricsReadable = true, closed;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    public long Epoch { get { lock (gate) return imageEpoch; } }
    internal long LyricEpoch { get { lock (gate) return lyricEpoch; } }
    public TimeSpan Lifetime => TimeSpan.FromMilliseconds(Ttl);
    internal static string SongKey(JsonObject song) => LJ.S(song["songId"]);
    internal async Task LoadAsync(CancellationToken token)
    {
        await database.ReadAsync(db =>
        {
            lock (gate)
            {
                if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
                var images = db.Revision(ImageOwner); var lyrics = db.Revision(LyricOwner);
                if (images != imageRevision) { imageRevision = images; imageEpoch++; }
                if (lyrics != lyricRevision) { lyricRevision = lyrics; lyricEpoch++; }
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
            var value = await database.ReadAsync(db => PlaybackCacheRecords.Lyrics.Read(db, [songId]), token).ConfigureAwait(false);
            if (value is null || value["updatedAt"]!.GetValue<long>() > Now || value["updatedAt"]!.GetValue<long>() <= Now - Ttl) return null;
            lock (gate) if (closed || !lyricsReadable || epoch != lyricEpoch) return null;
            value.Remove("storedBytes"); return value;
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
                var rows = db.Query("SELECT file_id,bytes,updated_at FROM windows_images WHERE address=$address AND updated_at>$expired AND updated_at<=$now",
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
        if (bytes.Length is <= 0 or > ArtworkService.MaximumBytes) throw new DocumentStoreException(DocumentStoreError.Limit);
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
                lock (gate) if (!imagesReadable) db.Execute("DELETE FROM windows_images");
                var file = Files.Write(owned); if (file.Created) created = file.Id;
                db.Execute("INSERT INTO windows_images(address,file_id,bytes,updated_at) VALUES($address,$file,$bytes,$at) " +
                    "ON CONFLICT(address) DO UPDATE SET file_id=excluded.file_id,bytes=excluded.bytes,updated_at=excluded.updated_at",
                    ("$address", address), ("$file", file.Id), ("$bytes", bytes.Length), ("$at", Now));
                Trim(db); return true;
            }, token, bytes.Length, commit: commit => { lock (gate) { if (closed || imageEpoch != epoch) throw new DocumentStoreException(DocumentStoreError.Conflict); commit(); } }).ConfigureAwait(false);
            created = null;
            lock (gate) if (imageEpoch == epoch) imagesReadable = true;
            await CleanupFilesAsync().ConfigureAwait(false);
        }
        catch
        {
            if (created is { } id) await database.ReadAsync(_ => { Files.Delete(id); return true; }).ConfigureAwait(false);
            throw;
        }
        finally { writes.Release(); }
    }
    internal async Task SaveLyricsAsync(string songId, string sourceKey, JsonArray lines, long epoch, CancellationToken token)
    {
        var value = new JsonObject { ["sourceKey"] = sourceKey, ["lines"] = lines.DeepClone(), ["updatedAt"] = Now };
        int bytes = JsonSerializer.SerializeToUtf8Bytes(value).Length;
        if (bytes > 512 * 1024) throw new DocumentStoreException(DocumentStoreError.Limit);
        value["storedBytes"] = bytes;
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await database.WriteAsync(db =>
            {
                Guard(db, epoch, false);
                lock (gate) if (!lyricsReadable) db.Execute("DELETE FROM windows_lyrics");
                PlaybackCacheRecords.Lyrics.Save(db, value, PlaybackCacheRecords.Lyrics.Read(db, [songId]), [songId]);
                Trim(db); return true;
            }, token, bytes, commit: commit => { lock (gate) { if (closed || lyricEpoch != epoch) throw new DocumentStoreException(DocumentStoreError.Conflict); commit(); } }).ConfigureAwait(false);
            lock (gate) if (lyricEpoch == epoch) lyricsReadable = true;
        }
        finally { writes.Release(); }
    }
    private void Trim(DatabaseSession db)
    {
        db.Execute("DELETE FROM windows_images WHERE updated_at<=$expired OR updated_at>$now; DELETE FROM windows_lyrics WHERE updatedAt<=$expired OR updatedAt>$now",
            ("$expired", Now - Ttl), ("$now", Now));
        while (db.Scalar<long>("SELECT (SELECT coalesce(sum(bytes),0) FROM windows_images)+(SELECT coalesce(sum(storedBytes),0) FROM windows_lyrics)") > MaximumBytes)
        {
            var oldest = db.Query("SELECT 'image',address,updated_at FROM windows_images UNION ALL SELECT 'lyrics',song_id,updatedAt FROM windows_lyrics ORDER BY 3,1,2 LIMIT 1",
                row => (Kind: row.GetString(0), Key: row.GetString(1))).Single();
            if (oldest.Kind == "image") db.Execute("DELETE FROM windows_images WHERE address=$key", ("$key", oldest.Key));
            else db.Execute("DELETE FROM windows_lyrics WHERE song_id=$key", ("$key", oldest.Key));
        }
    }
    private Task CleanupFilesAsync() => database.ReadAsync(db =>
    {
        if (files is not null) files.RemoveUnreferenced(db.Query("SELECT DISTINCT file_id FROM windows_images", row => row.GetString(0)).ToHashSet(StringComparer.Ordinal));
        return true;
    });
    public async Task RemoveAsync(string address, long epoch, CancellationToken token)
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await database.WriteAsync(db => { Guard(db, epoch, true); db.Execute("DELETE FROM windows_images WHERE address=$address", ("$address", address)); return true; }, token).ConfigureAwait(false);
            await CleanupFilesAsync().ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
    internal Task ClearImagesAsync(CancellationToken token) => ClearAsync(false, token);
    internal Task ClearAllAsync(CancellationToken token) => ClearAsync(true, token);
    private async Task ClearAsync(bool lyrics, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) { imageEpoch++; imagesReadable = false; if (lyrics) { lyricEpoch++; lyricsReadable = false; } }
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result = await database.WriteAsync(db =>
            {
                db.Execute("DELETE FROM windows_images");
                var image = db.CommitRevision(ImageOwner, db.Revision(ImageOwner), Guid.NewGuid().ToString("D"));
                var lyric = db.Revision(LyricOwner);
                if (lyrics) { db.Execute("DELETE FROM windows_lyrics"); lyric = db.CommitRevision(LyricOwner, lyric, Guid.NewGuid().ToString("D")); }
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
            db.Execute("UPDATE windows_lyrics SET updatedAt=$now WHERE song_id=$id", ("$now", Now), ("$id", songId));
            if (image.Length > 0) db.Execute("UPDATE windows_images SET updated_at=$now WHERE address=$address", ("$now", Now), ("$address", image));
            return true;
        }, token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { closed = true; imageEpoch++; lyricEpoch++; }
        await writes.WaitAsync().ConfigureAwait(false);
        try { await database.DrainAsync().ConfigureAwait(false); }
        finally { files?.Dispose(); files = null; writes.Release(); }
    }
}

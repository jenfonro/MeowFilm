using System.Text;
using System.Text.Json.Serialization;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed record MusicPresentationCacheStats(
    [property: JsonPropertyName("imageCount")] int ImageCount,
    [property: JsonPropertyName("imageBytes")] long ImageBytes,
    [property: JsonPropertyName("lyricCount")] int LyricCount,
    [property: JsonPropertyName("lyricBytes")] long LyricBytes,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("capacityBytes")] long CapacityBytes,
    [property: JsonPropertyName("diskFileCount")] int DiskFileCount,
    [property: JsonPropertyName("diskBytes")] long DiskBytes,
    [property: JsonPropertyName("closed")] bool Closed);

/// <summary>
/// Original image files and normalized lyric rows sharing a 128 MiB budget (aligned with iOS).
/// All disk I/O executes outside SQLite write transactions.
/// </summary>
internal sealed class MusicPresentationCache(IStorageDatabase database) : IArtworkByteCache, IAsyncDisposable
{
    internal const string StorageOwner = "music.presentation-resources.v1";
    internal const long Ttl = 30L * 86400000;
    internal const long MaximumBytes = 128L * 1024 * 1024;
    internal const int MaximumSingleLyricBytes = 1024 * 1024;
    private const int MaximumPendingImages = 128, MaximumPendingImageBytes = 16 * 1024 * 1024;
    private const string ImageOwner = StorageOwner + ".images", LyricOwner = StorageOwner + ".lyrics";

    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object gate = new();
    private RawResourceDirectory? files;
    private bool recovered;
    private StorageRevision imageRevision = StorageRevision.Empty, lyricRevision = StorageRevision.Empty;
    private bool imageRevisionLoaded, lyricRevisionLoaded;
    private readonly Dictionary<string, long> lyricSaveSequences = new(StringComparer.Ordinal);
    private long imageEpoch, lyricEpoch, lyricVersion;
    private int pendingImages, pendingImageBytes, activeWrites;
    private TaskCompletionSource writesDrained = CompletedWrites();
    private Task? disposal;
    private bool closed;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long Epoch { get { lock (gate) return imageEpoch; } }
    internal long LyricEpoch { get { lock (gate) return lyricEpoch; } }
    internal long LyricVersion { get { lock (gate) return lyricVersion; } }
    public TimeSpan Lifetime => TimeSpan.FromMilliseconds(Ttl);

    private static TaskCompletionSource CompletedWrites()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private void EnterWrite()
    {
        lock (gate)
        {
            if (closed) throw new StorageException(StorageError.Closed);
            if (activeWrites++ == 0) writesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void LeaveWrite()
    {
        lock (gate) if (--activeWrites == 0) writesDrained.TrySetResult();
    }

    private RawResourceDirectory EnsureFiles()
    {
        lock (gate)
        {
            if (closed) throw new StorageException(StorageError.Closed);
            return files ??= new(database.ProductRoot ?? throw new StorageException(StorageError.Unavailable), "music-images");
        }
    }

    internal async Task<CachedLyricsRecord?> ReadLyricsAsync(string songId, CancellationToken token)
    {
        try
        {
            return await database.ReadSnapshotAsync(db =>
            {
                lock (gate) if (closed) throw new StorageException(StorageError.Closed);
                var lyrics = db.Revision(LyricOwner);
                long epoch;
                lock (gate)
                {
                    if (closed) throw new StorageException(StorageError.Closed);
                    if (!lyricRevisionLoaded || lyrics != lyricRevision)
                    {
                        bool changed = lyricRevisionLoaded && lyrics != lyricRevision;
                        lyricRevision = lyrics; lyricRevisionLoaded = true;
                        if (changed) { lyricEpoch++; lyricVersion++; }
                    }
                    epoch = lyricEpoch;
                }
                long now = Now;
                var record = LyricsCacheStore.ReadRecord(db, songId);
                if (record is null || record.UpdatedAt <= now - Ttl) return null;
                lock (gate) return closed || epoch != lyricEpoch ? null : record;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (StorageException) { return null; }
    }

    public async Task<ArtworkBytes?> ReadAsync(string address, CancellationToken token)
    {
        try
        {
            var hit = await database.ReadAsync(db =>
            {
                lock (gate) if (closed) throw new StorageException(StorageError.Closed);
                var images = db.Revision(ImageOwner);
                long epoch;
                lock (gate)
                {
                    if (closed) throw new StorageException(StorageError.Closed);
                    if (!imageRevisionLoaded || images != imageRevision)
                    {
                        bool changed = imageRevisionLoaded && images != imageRevision;
                        imageRevision = images; imageRevisionLoaded = true;
                        if (changed) imageEpoch++;
                    }
                    epoch = imageEpoch;
                }
                long now = Now;
                var rows = db.Query("""
                    SELECT filename,bytes,updated_at_ms
                    FROM cached_images
                    WHERE resource_key=$address AND updated_at_ms>$expired
                    """,
                    row => (Id: row.GetString(0), Bytes: row.GetInt32(1), At: row.GetInt64(2)),
                    ("$address", address), ("$expired", now - Ttl));
                return rows.Count == 0 ? ((string Id, int Bytes, long At, long Epoch)?)null : (rows[0].Id, rows[0].Bytes, rows[0].At, epoch);
            }, token).ConfigureAwait(false);

            if (hit is not { } row) return null;
            var dir = EnsureFiles();
            // Read disk payload outside SQLite read lock.
            var bytes = dir.Read(row.Id, row.Bytes);
            if (bytes is null)
            {
                // Missing or self-healed corrupt file: drop stale index row cleanly.
                TrackRemoveBrokenIndex(row.Id);
                return null;
            }
            lock (gate) return closed || row.Epoch != imageEpoch
                ? null
                : new ArtworkBytes(bytes, DateTimeOffset.FromUnixTimeMilliseconds(row.At + Ttl));
        }
        catch (OperationCanceledException) { throw; }
        catch (StorageException) { return null; }
    }

    private void TrackRemoveBrokenIndex(string fileId)
    {
        lock (gate)
        {
            if (closed) return;
            if (activeWrites++ == 0) writesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = RemoveBrokenIndexAsync(fileId);
    }

    private async Task RemoveBrokenIndexAsync(string fileId)
    {
        try
        {
            await database.WriteAsync(db =>
            {
                db.Execute("DELETE FROM cached_images WHERE filename=$file", ("$file", fileId));
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Storage, "music.presentationCache", "removeBrokenIndex.failed",
                new { fileId }, error: error);
        }
        finally { LeaveWrite(); }
    }

    private void Guard(DatabaseSession db, long epoch, bool image)
    {
        lock (gate)
        {
            if (closed) throw new StorageException(StorageError.Closed);
            if (epoch != (image ? imageEpoch : lyricEpoch))
                throw new StorageException(StorageError.Conflict);
        }
        var current = db.Revision(image ? ImageOwner : LyricOwner);
        lock (gate)
        {
            if (closed) throw new StorageException(StorageError.Closed);
            if (epoch != (image ? imageEpoch : lyricEpoch))
                throw new StorageException(StorageError.Conflict);
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
        EnterWrite();
        bool entered = false;
        RawResourceDirectory? dir = null;
        string? newlyCreated = null;
        try
        {
            await writes.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            lock (gate)
            {
                if (closed) throw new StorageException(StorageError.Closed);
                if (imageEpoch != epoch) throw new StorageException(StorageError.Conflict);
            }
            dir = EnsureFiles();
            var owned = bytes.ToArray();
            // Stage and atomically commit file outside SQLite write transaction.
            var written = dir.Write(owned);
            if (written.Created) newlyCreated = written.Id;

            var filesToDelete = await database.WriteAsync(db =>
            {
                Guard(db, epoch, true);
                var candidates = new HashSet<string>(StringComparer.Ordinal);
                if (db.TryQuerySingle(
                    "SELECT filename FROM cached_images WHERE resource_key=$address AND filename<>$file",
                    r => r.GetString(0), out var oldFile, ("$address", address), ("$file", written.Id)))
                {
                    candidates.Add(oldFile);
                }
                db.Execute("""
                    INSERT INTO cached_images(resource_key,filename,bytes,updated_at_ms)
                    VALUES($address,$file,$bytes,$at)
                    ON CONFLICT(resource_key) DO UPDATE SET
                        filename=excluded.filename,
                        bytes=excluded.bytes,
                        updated_at_ms=excluded.updated_at_ms
                    """,
                    ("$address", address), ("$file", written.Id), ("$bytes", owned.Length), ("$at", Now));
                Trim(db, candidates);
                return FilterUnreferenced(db, candidates);
            }, token, owned.Length, commit: commit =>
            {
                lock (gate) if (closed || imageEpoch != epoch) throw new StorageException(StorageError.Conflict);
                commit();
            }).ConfigureAwait(false);

            newlyCreated = null;
            // Delete evicted/replaced files outside SQLite write transaction.
            foreach (var id in filesToDelete) dir.Delete(id);
            await RecoverOnceIfNeededAsync(dir, token).ConfigureAwait(false);
        }
        catch
        {
            if (newlyCreated is { } id && dir is not null)
            {
                try { dir.Delete(id); } catch { }
            }
            throw;
        }
        finally
        {
            if (entered) writes.Release();
            LeaveWrite();
        }
    }

    internal async Task SaveLyricsAsync(string songId, string sourceKey, IReadOnlyList<CachedLyricLine> lines,
        long epoch, long sequence, CancellationToken token)
    {
        long now = Now;
        int bytes = EstimateLyricsBytes(sourceKey, lines);
        if (bytes > MaximumSingleLyricBytes) throw new StorageException(StorageError.Limit);
        var record = new CachedLyricsRecord(sourceKey, now, bytes, lines);
        EnterWrite();
        bool entered = false;
        try
        {
            await writes.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            lock (gate)
            {
                if (closed) throw new StorageException(StorageError.Closed);
                if (lyricEpoch != epoch) throw new StorageException(StorageError.Conflict);
                if (lyricSaveSequences.TryGetValue(songId, out var latest) && sequence <= latest) return;
            }
            var filesToDelete = await database.WriteAsync(db =>
            {
                Guard(db, epoch, false);
                var candidates = new HashSet<string>(StringComparer.Ordinal);
                LyricsCacheStore.SaveRecord(db, songId, record);
                Trim(db, candidates);
                return FilterUnreferenced(db, candidates);
            }, token, bytes, commit: commit =>
            {
                lock (gate) if (closed || lyricEpoch != epoch) throw new StorageException(StorageError.Conflict);
                commit();
            }).ConfigureAwait(false);
            lock (gate)
            {
                lyricVersion++;
                if (lyricEpoch == epoch &&
                    (!lyricSaveSequences.TryGetValue(songId, out var latest) || sequence > latest))
                {
                    if (lyricSaveSequences.Count >= 1024 && !lyricSaveSequences.ContainsKey(songId))
                    {
                        foreach (var stale in lyricSaveSequences.OrderBy(pair => pair.Value).Take(512).Select(pair => pair.Key).ToArray())
                            lyricSaveSequences.Remove(stale);
                    }
                    lyricSaveSequences[songId] = sequence;
                }
            }
            if (filesToDelete.Count > 0 && database.ProductRoot is not null)
            {
                var dir = EnsureFiles();
                foreach (var id in filesToDelete) dir.Delete(id);
            }
        }
        finally
        {
            if (entered) writes.Release();
            LeaveWrite();
        }
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

    private static void Trim(DatabaseSession db, HashSet<string> candidateDeletes)
    {
        long now = Now;
        foreach (var file in db.Query(
            "SELECT filename FROM cached_images WHERE updated_at_ms<=$expired",
            row => row.GetString(0), ("$expired", now - Ttl)))
        {
            candidateDeletes.Add(file);
        }
        db.Execute("DELETE FROM cached_images WHERE updated_at_ms<=$expired",
            ("$expired", now - Ttl));
        db.Execute("DELETE FROM cached_music_lyrics WHERE updated_at_ms<=$expired",
            ("$expired", now - Ttl));

        long total = db.Scalar<long>(
            "SELECT (SELECT coalesce(sum(bytes),0) FROM cached_images)+(SELECT coalesce(sum(stored_bytes),0) FROM cached_music_lyrics)");
        if (total <= MaximumBytes) return;

        foreach (var entry in db.Query("""
            SELECT 'image',resource_key,filename,updated_at_ms,bytes FROM cached_images
            UNION ALL
            SELECT 'lyrics',song_id,'',updated_at_ms,stored_bytes FROM cached_music_lyrics
            ORDER BY 4 ASC,2 ASC,1 ASC
            """,
            row => (Kind: row.GetString(0), Key: row.GetString(1), File: row.GetString(2), Bytes: row.GetInt64(4))))
        {
            if (total <= MaximumBytes) break;
            if (entry.Kind == "image")
            {
                if (db.Execute("DELETE FROM cached_images WHERE resource_key=$key",
                    ("$key", entry.Key)) > 0)
                {
                    candidateDeletes.Add(entry.File);
                    total -= entry.Bytes;
                }
            }
            else
            {
                if (db.Execute("DELETE FROM cached_music_lyrics WHERE song_id=$key", ("$key", entry.Key)) > 0)
                    total -= entry.Bytes;
            }
        }
    }

    private static List<string> FilterUnreferenced(DatabaseSession db, HashSet<string> candidates)
    {
        var unreferenced = new List<string>(candidates.Count);
        foreach (var id in candidates)
        {
            if (!db.TryQuerySingle("SELECT 1 FROM cached_images WHERE filename=$file LIMIT 1",
                row => row.GetInt32(0), out _, ("$file", id)))
            {
                unreferenced.Add(id);
            }
        }
        return unreferenced;
    }

    private async Task RecoverOnceIfNeededAsync(RawResourceDirectory dir, CancellationToken token)
    {
        lock (gate) { if (recovered || closed) return; recovered = true; }
        try
        {
            var live = await database.ReadAsync(
                db => db.Query("SELECT DISTINCT filename FROM cached_images", row => row.GetString(0))
                    .ToHashSet(StringComparer.Ordinal),
                token).ConfigureAwait(false);
            dir.RemoveUnreferenced(live);
        }
        catch
        {
            lock (gate) recovered = false;
        }
    }

    public async Task RemoveAsync(string address, long epoch, CancellationToken token)
    {
        EnterWrite();
        bool entered = false;
        try
        {
            await writes.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            var filesToDelete = await database.WriteAsync(db =>
            {
                Guard(db, epoch, true);
                var candidates = db.Query("SELECT filename FROM cached_images WHERE resource_key=$address",
                    row => row.GetString(0), ("$address", address)).ToHashSet(StringComparer.Ordinal);
                db.Execute("DELETE FROM cached_images WHERE resource_key=$address", ("$address", address));
                return FilterUnreferenced(db, candidates);
            }, token).ConfigureAwait(false);
            if (filesToDelete.Count > 0 && database.ProductRoot is not null)
            {
                var dir = EnsureFiles();
                foreach (var id in filesToDelete) dir.Delete(id);
            }
        }
        finally
        {
            if (entered) writes.Release();
            LeaveWrite();
        }
    }

    internal async Task ClearAllAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closed) throw new StorageException(StorageError.Closed);
            if (activeWrites++ == 0) writesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            imageEpoch++; lyricEpoch++; lyricVersion++; lyricSaveSequences.Clear();
        }
        bool entered = false;
        try
        {
            await writes.WaitAsync(token).ConfigureAwait(false);
            entered = true;
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
                imageRevision = result.image; imageRevisionLoaded = true;
                lyricRevision = result.lyric; lyricRevisionLoaded = true;
            }
            if (database.ProductRoot is not null)
            {
                var dir = EnsureFiles();
                dir.RemoveUnreferenced(new HashSet<string>(StringComparer.Ordinal), throwOnError: true);
                lock (gate) recovered = true;
            }
        }
        finally
        {
            if (entered) writes.Release();
            LeaveWrite();
        }
    }

    internal async Task TouchPlayedAsync(string songId, string image, CancellationToken token)
    {
        await database.WriteAsync(db =>
        {
            long now = Now;
            long expired = now - Ttl;
            db.Execute("UPDATE cached_music_lyrics SET updated_at_ms=$now WHERE song_id=$id AND updated_at_ms>$expired AND updated_at_ms<$now",
                ("$now", now), ("$id", songId), ("$expired", expired));
            if (image.Length > 0)
                db.Execute("UPDATE cached_images SET updated_at_ms=$now WHERE resource_key=$address AND updated_at_ms>$expired AND updated_at_ms<$now",
                    ("$now", now), ("$address", image), ("$expired", expired));
            return true;
        }, token).ConfigureAwait(false);
    }

    internal async Task<MusicPresentationCacheStats> GetStatsAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            if (closed)
                return new MusicPresentationCacheStats(0, 0, 0, 0, 0, MaximumBytes, 0, 0, true);
        }
        (int ImageCount, long ImageBytes, int LyricCount, long LyricBytes) dbStats;
        try
        {
            dbStats = await database.ReadSnapshotAsync(db =>
            {
                var img = db.Query("SELECT count(*), coalesce(sum(bytes), 0) FROM cached_images",
                    r => (Count: r.GetInt32(0), Bytes: r.GetInt64(1)))[0];
                var lyr = db.Query("SELECT count(*), coalesce(sum(stored_bytes), 0) FROM cached_music_lyrics",
                    r => (Count: r.GetInt32(0), Bytes: r.GetInt64(1)))[0];
                return (img.Count, img.Bytes, lyr.Count, lyr.Bytes);
            }, token).ConfigureAwait(false);
        }
        catch (StorageException error) when (error.Error == StorageError.Closed)
        {
            return new MusicPresentationCacheStats(0, 0, 0, 0, 0, MaximumBytes, 0, 0, true);
        }
        RawResourceDirectory? existingFiles;
        lock (gate)
        {
            if (closed)
                return new MusicPresentationCacheStats(0, 0, 0, 0, 0, MaximumBytes, 0, 0, true);
            existingFiles = files;
        }
        var disk = existingFiles is not null
            ? existingFiles.GetDiskStats()
            : database.ProductRoot is { } root ? GetDiskStatsWithoutCreating(root) : (0, 0L);
        return new MusicPresentationCacheStats(
            ImageCount: dbStats.ImageCount,
            ImageBytes: dbStats.ImageBytes,
            LyricCount: dbStats.LyricCount,
            LyricBytes: dbStats.LyricBytes,
            TotalBytes: dbStats.ImageBytes + dbStats.LyricBytes,
            CapacityBytes: MaximumBytes,
            DiskFileCount: disk.Item1,
            DiskBytes: disk.Item2,
            Closed: false);
    }

    private static (int FileCount, long TotalBytes) GetDiskStatsWithoutCreating(string productRoot)
    {
        string dirPath = Path.Combine(Path.GetFullPath(productRoot), "resources-v1", "music-images");
        if (!Directory.Exists(dirPath)) return (0, 0L);
        int count = 0;
        long total = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFiles(dirPath))
            {
                var name = Path.GetFileName(entry);
                if (name.Length != 73 || !name.EndsWith(".resource", StringComparison.Ordinal) ||
                    name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") >= 0)
                    continue;
                try { total += new FileInfo(entry).Length; count++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (count, total);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closed = true; imageEpoch++; lyricEpoch++; lyricSaveSequences.Clear();
            var waitWrites = writesDrained.Task;
            disposal ??= DisposeCoreAsync(waitWrites);
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync(Task waitWrites)
    {
        await waitWrites.ConfigureAwait(false);
        await writes.WaitAsync().ConfigureAwait(false);
        try { await database.DrainAsync().ConfigureAwait(false); }
        finally { files = null; writes.Release(); writes.Dispose(); }
    }
}

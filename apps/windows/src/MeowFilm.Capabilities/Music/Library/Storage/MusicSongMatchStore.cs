using System.Text;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

internal sealed record SongMatchPlatformState(
    string State,
    long CheckedAt,
    string Context,
    string Query,
    string Code,
    long? MatchedAt);

internal sealed record SongMatchRecord(
    string SongId,
    IReadOnlyDictionary<string, SongMatchPlatformState> Platforms,
    long StoredBytes = 0)
{
    internal long EstimateBytes()
    {
        long bytes = 64 + Encoding.UTF8.GetByteCount(SongId);
        foreach (var (platform, state) in Platforms)
        {
            bytes += 96 + Encoding.UTF8.GetByteCount(platform) + Encoding.UTF8.GetByteCount(state.State) +
                Encoding.UTF8.GetByteCount(state.Context) + Encoding.UTF8.GetByteCount(state.Query) +
                Encoding.UTF8.GetByteCount(state.Code);
        }
        return bytes;
    }
}

/// <summary>Rebuildable cross-platform match cache (4 MiB LRU, 30-day TTL, aligned with iOS).</summary>
internal sealed class MusicSongMatchStore(IStorageDatabase database) : IDisposable
{
    internal const string StorageOwner = "music.source-discovery.v1";
    private const long MaximumBytes = 4 * 1024 * 1024;
    private const long TtlMs = 30L * 86400000;
    private readonly SemaphoreSlim gate = new(1, 1);

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose() => gate.Dispose();

    internal async Task<IReadOnlyList<SongMatchRecord>> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await database.ReadSnapshotAsync(db => (IReadOnlyList<SongMatchRecord>)ReadEntries(db, Now), token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return []; }
        finally { gate.Release(); }
    }

    internal async Task ApplyChangesAsync(
        IReadOnlyList<SongMatchRecord> updated,
        IReadOnlyList<string> removed,
        CancellationToken token)
    {
        if (updated.Count == 0 && removed.Count == 0) return;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var prepared = new List<SongMatchRecord>(updated.Count);
            long writeBytes = 256;
            foreach (var entry in updated)
            {
                var validated = Validate(entry);
                long size = validated.EstimateBytes();
                if (size > MaximumBytes) continue;
                prepared.Add(validated with { StoredBytes = size });
                writeBytes = checked(writeBytes + size);
            }
            var removeIds = removed.Where(id => Guid.TryParseExact(id, "D", out _)).Distinct(StringComparer.Ordinal).ToArray();
            if (prepared.Count == 0 && removeIds.Length == 0) return;

            long now = Now;
            long cutoff = now - TtlMs;
            await database.WriteAsync(db =>
            {
                db.Execute("DELETE FROM windows_music_source_matches WHERE updated_at_ms<=$cutoff OR updated_at_ms>$now",
                    ("$cutoff", cutoff), ("$now", now));

                foreach (string id in removeIds)
                    db.Execute("DELETE FROM windows_music_source_matches WHERE song_id=$id", ("$id", id));

                foreach (var entry in prepared)
                {
                    db.Execute("""
                        INSERT INTO windows_music_source_matches(song_id,updated_at_ms,stored_bytes)
                        VALUES($id,$at,$bytes)
                        ON CONFLICT(song_id) DO UPDATE SET updated_at_ms=excluded.updated_at_ms,stored_bytes=excluded.stored_bytes
                        """, ("$id", entry.SongId), ("$at", now), ("$bytes", entry.StoredBytes));

                    db.Execute("DELETE FROM windows_music_source_match_platforms WHERE song_id=$id", ("$id", entry.SongId));
                    foreach (var (plat, st) in entry.Platforms)
                    {
                        db.Execute("""
                            INSERT INTO windows_music_source_match_platforms(
                                song_id,platform,state,checked_at_ms,context,query_text,code,matched_at_ms)
                            VALUES($id,$plat,$state,$chk,$ctx,$q,$code,$mat)
                            """,
                            ("$id", entry.SongId), ("$plat", plat),
                            ("$state", st.State), ("$chk", st.CheckedAt),
                            ("$ctx", st.Context), ("$q", st.Query),
                            ("$code", st.Code), ("$mat", st.MatchedAt));
                    }
                }

                long totalBytes = db.Scalar<long>("SELECT COALESCE(SUM(stored_bytes),0) FROM windows_music_source_matches");
                if (totalBytes > MaximumBytes)
                {
                    foreach (var (songId, storedBytes) in db.Query(
                        "SELECT song_id,stored_bytes FROM windows_music_source_matches ORDER BY updated_at_ms ASC, song_id ASC",
                        r => (r.GetString(0), r.GetInt64(1))))
                    {
                        if (totalBytes <= MaximumBytes) break;
                        if (db.Execute("DELETE FROM windows_music_source_matches WHERE song_id=$id", ("$id", songId)) > 0)
                            totalBytes -= storedBytes;
                    }
                }

                return db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
            }, token, checked((int)Math.Min(writeBytes, int.MaxValue))).ConfigureAwait(false);
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
        finally { gate.Release(); }
    }

    internal async Task ClearAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await database.WriteAsync(db =>
            {
                db.Execute("DELETE FROM windows_music_source_matches");
                return db.CommitRevision(StorageOwner, db.Revision(StorageOwner));
            }, token, 256).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<(int SongCount, long StoredBytes, long MaximumBytes)> GetStatsAsync(CancellationToken token = default)
    {
        var row = await database.ReadAsync(
            db => db.Query("SELECT count(*), coalesce(sum(stored_bytes), 0) FROM windows_music_source_matches",
                r => (r.GetInt32(0), r.GetInt64(1)))[0],
            token).ConfigureAwait(false);
        return (row.Item1, row.Item2, MaximumBytes);
    }

    private static SongMatchRecord[] ReadEntries(DatabaseSession db, long now)
    {
        long cutoff = now - TtlMs;
        var validRows = db.Query("""
            SELECT song_id,stored_bytes
            FROM windows_music_source_matches
            WHERE updated_at_ms>$cutoff AND updated_at_ms<=$now
            ORDER BY updated_at_ms ASC, song_id ASC
            """, r => (SongId: r.GetString(0), StoredBytes: r.GetInt64(1)),
            ("$cutoff", cutoff), ("$now", now));
        if (validRows.Count == 0) return [];

        var validIds = validRows.Select(r => r.SongId).ToHashSet(StringComparer.Ordinal);
        var platformsBySong = new Dictionary<string, Dictionary<string, SongMatchPlatformState>>(StringComparer.Ordinal);
        foreach (var p in db.Query("""
            SELECT song_id,platform,state,checked_at_ms,context,query_text,code,matched_at_ms
            FROM windows_music_source_match_platforms
            ORDER BY song_id, platform
            """, r => (
                SongId: r.GetString(0),
                Platform: r.GetString(1),
                State: new SongMatchPlatformState(
                    r.GetString(2),
                    r.GetInt64(3),
                    r.GetNullableString(4) ?? "",
                    r.GetNullableString(5) ?? "",
                    r.GetNullableString(6) ?? "",
                    r.GetNullableInt64(7)))))
        {
            if (!validIds.Contains(p.SongId)) continue;
            if (!platformsBySong.TryGetValue(p.SongId, out var dict))
                platformsBySong[p.SongId] = dict = new Dictionary<string, SongMatchPlatformState>(StringComparer.Ordinal);
            dict[p.Platform] = p.State;
        }

        var values = new List<SongMatchRecord>(validRows.Count);
        foreach (var (songId, storedBytes) in validRows)
        {
            var row = new SongMatchRecord(
                songId,
                platformsBySong.TryGetValue(songId, out var plats)
                    ? plats
                    : new Dictionary<string, SongMatchPlatformState>(StringComparer.Ordinal),
                storedBytes);
            values.Add(Validate(row));
        }
        return values.ToArray();
    }

    private static SongMatchRecord Validate(SongMatchRecord entry)
    {
        MusicRules.Require(Guid.TryParseExact(entry.SongId, "D", out _), "CORRUPT_STORAGE");
        var platforms = new Dictionary<string, SongMatchPlatformState>(entry.Platforms.Count, StringComparer.Ordinal);
        foreach (var (platform, state) in entry.Platforms)
        {
            MusicRules.Require(platform is "wy" or "tx" or "kw" or "kg" or "mg" &&
                state.State is "matched" or "notFound" or "failed" &&
                state.CheckedAt >= 0, "CORRUPT_STORAGE");
            platforms[platform] = state;
        }
        return new SongMatchRecord(entry.SongId, platforms, entry.StoredBytes);
    }
}

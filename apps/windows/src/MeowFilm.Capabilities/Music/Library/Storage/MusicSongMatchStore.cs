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
    IReadOnlyList<string> Excluded,
    long StoredBytes = 0)
{
    internal bool ContentEquals(SongMatchRecord? other)
    {
        if (other is null || SongId != other.SongId ||
            Platforms.Count != other.Platforms.Count ||
            Excluded.Count != other.Excluded.Count)
            return false;
        foreach (var (platform, state) in Platforms)
            if (!other.Platforms.TryGetValue(platform, out var otherState) || state != otherState)
                return false;
        for (int i = 0; i < Excluded.Count; i++)
            if (Excluded[i] != other.Excluded[i])
                return false;
        return true;
    }

    internal long EstimateBytes()
    {
        long bytes = 64 + Encoding.UTF8.GetByteCount(SongId);
        foreach (var (platform, state) in Platforms)
        {
            bytes += 96 + Encoding.UTF8.GetByteCount(platform) + Encoding.UTF8.GetByteCount(state.State) +
                Encoding.UTF8.GetByteCount(state.Context) + Encoding.UTF8.GetByteCount(state.Query) +
                Encoding.UTF8.GetByteCount(state.Code);
        }
        foreach (var key in Excluded)
            bytes += 8 + Encoding.UTF8.GetByteCount(key);
        return bytes;
    }
}

/// <summary>Rebuildable cross-platform match cache, separate from the music library.</summary>
internal sealed class MusicSongMatchStore(IStorageDatabase database)
{
    internal const string StorageOwner = "music.source-discovery.v1";
    private const long MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<IReadOnlyList<SongMatchRecord>> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await database.ReadSnapshotAsync(db => (IReadOnlyList<SongMatchRecord>)ReadEntries(db), token)
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

            await database.WriteAsync(db =>
            {
                foreach (string id in removeIds)
                    db.Execute("DELETE FROM windows_music_source_matches WHERE song_id=$id", ("$id", id));

                foreach (var entry in prepared)
                {
                    int position = db.TryQuerySingle(
                        "SELECT position FROM windows_music_source_matches WHERE song_id=$id",
                        r => r.GetInt32(0), out int existingPos, ("$id", entry.SongId))
                        ? existingPos
                        : checked((int)db.Scalar<long>("SELECT COALESCE(MAX(position) + 1, 0) FROM windows_music_source_matches"));

                    db.Execute("""
                        INSERT INTO windows_music_source_matches(song_id,position,stored_bytes)
                        VALUES($id,$pos,$bytes)
                        ON CONFLICT(song_id) DO UPDATE SET position=excluded.position,stored_bytes=excluded.stored_bytes
                        """, ("$id", entry.SongId), ("$pos", position), ("$bytes", entry.StoredBytes));

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

                    db.Execute("DELETE FROM windows_music_source_match_excluded WHERE song_id=$id", ("$id", entry.SongId));
                    for (int i = 0; i < entry.Excluded.Count; i++)
                    {
                        db.Execute("""
                            INSERT INTO windows_music_source_match_excluded(song_id,position,excluded_key)
                            VALUES($id,$pos,$key)
                            """, ("$id", entry.SongId), ("$pos", i), ("$key", entry.Excluded[i]));
                    }
                }

                long totalBytes = db.Scalar<long>("SELECT COALESCE(SUM(stored_bytes),0) FROM windows_music_source_matches");
                if (totalBytes > MaximumBytes)
                {
                    foreach (var (songId, storedBytes) in db.Query(
                        "SELECT song_id,stored_bytes FROM windows_music_source_matches ORDER BY position, song_id",
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

    private static SongMatchRecord[] ReadEntries(DatabaseSession db)
    {
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
            if (!platformsBySong.TryGetValue(p.SongId, out var dict))
                platformsBySong[p.SongId] = dict = new Dictionary<string, SongMatchPlatformState>(StringComparer.Ordinal);
            dict[p.Platform] = p.State;
        }

        var excludedBySong = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in db.Query("""
            SELECT song_id,excluded_key
            FROM windows_music_source_match_excluded
            ORDER BY song_id, position
            """, r => (SongId: r.GetString(0), Key: r.GetString(1))))
        {
            if (!excludedBySong.TryGetValue(e.SongId, out var list))
                excludedBySong[e.SongId] = list = [];
            list.Add(e.Key);
        }

        var values = new List<SongMatchRecord>();
        foreach (var (songId, storedBytes) in db.Query("""
            SELECT song_id,stored_bytes
            FROM windows_music_source_matches
            ORDER BY position, song_id
            """, r => (r.GetString(0), r.GetInt64(1))))
        {
            var row = new SongMatchRecord(
                songId,
                platformsBySong.TryGetValue(songId, out var plats)
                    ? plats
                    : new Dictionary<string, SongMatchPlatformState>(StringComparer.Ordinal),
                excludedBySong.TryGetValue(songId, out var excl) ? excl : [],
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
        return new SongMatchRecord(entry.SongId, platforms, entry.Excluded.ToArray(), entry.StoredBytes);
    }
}

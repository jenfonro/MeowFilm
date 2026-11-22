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
    internal const string Namespace = "music.source-discovery.v1";
    private const long MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, SongMatchRecord>? previous;
    private StorageRevision revision = StorageRevision.Empty;

    internal async Task<IReadOnlyList<SongMatchRecord>> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await database.ReadSnapshotAsync(db =>
            {
                previous = ReadEntries(db, repair: false);
                revision = db.Revision(Namespace);
                return (IReadOnlyList<SongMatchRecord>)previous.Values.ToArray();
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { previous = null; return []; }
        finally { gate.Release(); }
    }

    internal async Task WriteAsync(IReadOnlyList<SongMatchRecord> entries, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var next = new Dictionary<string, SongMatchRecord>(StringComparer.Ordinal);
            var order = new Queue<string>();
            long bytes = 0;
            foreach (var entry in entries)
            {
                var validated = Validate(entry);
                string id = validated.SongId;
                if (previous?.TryGetValue(id, out var old) == true && validated.ContentEquals(old))
                {
                    validated = old;
                    bytes += old.StoredBytes;
                }
                else
                {
                    long size = validated.EstimateBytes();
                    validated = validated with { StoredBytes = size };
                    bytes += size;
                }
                next.Add(id, validated);
                order.Enqueue(id);
                while (bytes > MaximumBytes && next.Count > 0)
                {
                    string first = order.Dequeue();
                    bytes -= next[first].StoredBytes;
                    next.Remove(first);
                }
            }
            next = order.ToDictionary(id => id, id => next[id], StringComparer.Ordinal);
            var committed = await database.WriteAsync(db =>
            {
                if (previous is null || db.Revision(Namespace) != revision)
                {
                    previous = ReadEntries(db, repair: true);
                    revision = db.Revision(Namespace);
                }
                foreach (var id in previous.Keys.Where(id => !next.ContainsKey(id)))
                    db.Execute("DELETE FROM windows_music_source_matches WHERE song_id=$id", ("$id", id));

                var positions = previous.Keys.Select((id, index) => (id, index))
                    .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);
                int pos = 0;
                foreach (var (id, value) in next)
                {
                    int ordinal = pos++;
                    if (previous.TryGetValue(id, out var old) &&
                        (ReferenceEquals(value, old) || value.ContentEquals(old)))
                    {
                        if (positions.GetValueOrDefault(id, -1) != ordinal)
                            db.Execute("UPDATE windows_music_source_matches SET position=$pos WHERE song_id=$id",
                                ("$pos", ordinal), ("$id", id));
                        continue;
                    }
                    SaveEntryIncremental(db, id, ordinal, value, old);
                }
                return db.CommitRevision(Namespace, revision);
            }, token, checked((int)bytes)).ConfigureAwait(false);
            previous = next;
            revision = committed;
        }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { previous = null; }
        finally { gate.Release(); }
    }

    private static void SaveEntryIncremental(DatabaseSession db, string songId, int position, SongMatchRecord value, SongMatchRecord? old)
    {
        db.Execute("""
            INSERT INTO windows_music_source_matches(song_id,position,stored_bytes)
            VALUES($id,$pos,$bytes)
            ON CONFLICT(song_id) DO UPDATE SET position=excluded.position,stored_bytes=excluded.stored_bytes
            """, ("$id", songId), ("$pos", position), ("$bytes", value.StoredBytes));

        if (old is not null)
        {
            foreach (var plat in old.Platforms.Keys)
                if (!value.Platforms.ContainsKey(plat))
                    db.Execute("DELETE FROM windows_music_source_match_platforms WHERE song_id=$id AND platform=$plat",
                        ("$id", songId), ("$plat", plat));
        }

        foreach (var (plat, st) in value.Platforms)
        {
            if (old is not null && old.Platforms.TryGetValue(plat, out var prevState) && prevState == st)
                continue;
            db.Execute("""
                INSERT INTO windows_music_source_match_platforms(
                    song_id,platform,state,checked_at_ms,context,query_text,code,matched_at_ms)
                VALUES($id,$plat,$state,$chk,$ctx,$q,$code,$mat)
                ON CONFLICT(song_id,platform) DO UPDATE SET
                    state=excluded.state,checked_at_ms=excluded.checked_at_ms,context=excluded.context,
                    query_text=excluded.query_text,code=excluded.code,matched_at_ms=excluded.matched_at_ms
                """,
                ("$id", songId), ("$plat", plat),
                ("$state", st.State), ("$chk", st.CheckedAt),
                ("$ctx", st.Context), ("$q", st.Query),
                ("$code", st.Code), ("$mat", st.MatchedAt));
        }

        bool excludedSame = old is not null && old.Excluded.Count == value.Excluded.Count &&
            old.Excluded.SequenceEqual(value.Excluded, StringComparer.Ordinal);
        if (!excludedSame)
        {
            db.Execute("DELETE FROM windows_music_source_match_excluded WHERE song_id=$id", ("$id", songId));
            for (int i = 0; i < value.Excluded.Count; i++)
                db.Execute("""
                    INSERT INTO windows_music_source_match_excluded(song_id,position,excluded_key)
                    VALUES($id,$pos,$key)
                    """, ("$id", songId), ("$pos", i), ("$key", value.Excluded[i]));
        }
    }

    private static Dictionary<string, SongMatchRecord> ReadEntries(DatabaseSession db, bool repair)
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

        var values = new Dictionary<string, SongMatchRecord>(StringComparer.Ordinal);
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
            try { _ = Validate(row); values.Add(songId, row); }
            catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error))
            {
                if (!repair) throw;
                db.Execute("DELETE FROM windows_music_source_matches WHERE song_id=$id", ("$id", songId));
            }
        }
        return values;
    }

    private static SongMatchRecord Validate(SongMatchRecord entry)
    {
        LJ.Require(Guid.TryParseExact(entry.SongId, "D", out _), "CORRUPT_STORAGE");
        var platforms = new Dictionary<string, SongMatchPlatformState>(entry.Platforms.Count, StringComparer.Ordinal);
        foreach (var (platform, state) in entry.Platforms)
        {
            LJ.Require(platform is "wy" or "tx" or "kw" or "kg" or "mg" &&
                state.State is "matched" or "notFound" or "failed" &&
                state.CheckedAt >= 0, "CORRUPT_STORAGE");
            platforms[platform] = state;
        }
        return new SongMatchRecord(entry.SongId, platforms, entry.Excluded.ToArray(), entry.StoredBytes);
    }
}

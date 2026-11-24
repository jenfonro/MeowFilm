using MeowFilm.Platform.Storage;
namespace MeowFilm.Capabilities.PlaybackCache;

internal readonly record struct MusicChorusKey(string Scope, string Platform, string NativeId, double RecordingDuration);

internal sealed class MusicChorusCache(IStorageDatabase database)
{
    private const long Ttl = 30L * 86400000;
    private const int MaximumEntries = 100;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal Task<(double Start, double End)?> ReadAsync(MusicChorusKey key, CancellationToken token) =>
        Access(key, null, token);

    internal async Task PutAsync(MusicChorusKey key, double start, double end, CancellationToken token) =>
        _ = await Access(key, (start, end), token).ConfigureAwait(false);

    private async Task<(double Start, double End)?> Access(MusicChorusKey key, (double Start, double End)? point, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key.Scope) || string.IsNullOrEmpty(key.Platform) || string.IsNullOrEmpty(key.NativeId) ||
            !double.IsFinite(key.RecordingDuration) || key.RecordingDuration <= 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        try
        {
            (double Start, double End)? Read(DatabaseSession db)
            {
                long now = Now;
                if (!db.TryQuerySingle("""
                    SELECT updated_at_ms,expires_at_ms,start_sec,end_sec
                    FROM cached_music_chorus
                    WHERE scope=$scope AND platform=$platform AND native_id=$nid AND recording_duration=$dur
                    """,
                    row => (At: row.GetInt64(0), ExpiresAt: row.GetInt64(1), Start: row.GetDouble(2), End: row.GetDouble(3)),
                    out var entry,
                    ("$scope", key.Scope), ("$platform", key.Platform), ("$nid", key.NativeId), ("$dur", key.RecordingDuration)))
                    return null;
                if (entry.At > now || entry.ExpiresAt <= now || entry.ExpiresAt > now + Ttl) return null;
                return Fits(entry.Start, entry.End, key.RecordingDuration) ? (entry.Start, entry.End) : null;
            }
            if (point is not { } value) return await database.ReadAsync(Read, token).ConfigureAwait(false);
            if (!Fits(value.Start, value.End, key.RecordingDuration)) return null;
            return await database.WriteAsync(db =>
            {
                long now = Now;
                db.Execute("""
                    INSERT INTO cached_music_chorus(scope,platform,native_id,recording_duration,start_sec,end_sec,updated_at_ms,expires_at_ms)
                    VALUES($scope,$platform,$nid,$dur,$start,$end,$at,$exp)
                    ON CONFLICT(scope,platform,native_id,recording_duration) DO UPDATE SET
                        start_sec=excluded.start_sec,
                        end_sec=excluded.end_sec,
                        updated_at_ms=excluded.updated_at_ms,
                        expires_at_ms=excluded.expires_at_ms
                    """,
                    ("$scope", key.Scope), ("$platform", key.Platform), ("$nid", key.NativeId), ("$dur", key.RecordingDuration),
                    ("$start", value.Start), ("$end", value.End), ("$at", now), ("$exp", now + Ttl));
                db.Execute("DELETE FROM cached_music_chorus WHERE expires_at_ms<=$now OR updated_at_ms>$now",
                    ("$now", now));
                db.Execute("""
                    DELETE FROM cached_music_chorus
                    WHERE rowid IN (SELECT rowid FROM cached_music_chorus ORDER BY expires_at_ms DESC, rowid DESC LIMIT -1 OFFSET $limit)
                    """,
                    ("$limit", MaximumEntries));
                return ((double Start, double End)?)value;
            }, token, 512).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return null; }
    }

    internal async Task ClearAsync(string? platform, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await database.WriteAsync(db =>
        {
            if (string.IsNullOrEmpty(platform))
                db.Execute("DELETE FROM cached_music_chorus");
            else
                db.Execute("DELETE FROM cached_music_chorus WHERE platform=$platform", ("$platform", platform));
            return true;
        }, token, 256).ConfigureAwait(false);
    }

    internal async Task<(int EntryCount, int MaximumEntries)> GetStatsAsync(CancellationToken token = default)
    {
        int count = await database.ReadAsync(
            db => db.Scalar<int>("SELECT count(*) FROM cached_music_chorus"),
            token).ConfigureAwait(false);
        return (count, MaximumEntries);
    }

    private static bool Fits(double start, double end, double duration) =>
        double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start && end <= duration;
}

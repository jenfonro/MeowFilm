using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed class MusicChorusCache(IStorageDatabase database, TimeProvider? time = null)
{
    internal const string Namespace = "music.chorus.v2";
    private const long Ttl = 30L * 86400000;
    private const int MaximumEntries = 100;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    internal static string Key(string owner, string song, string platform, string nativeId, double duration) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] {
            owner, song, platform, nativeId, duration }))));


    internal Task<(double Start, double End)?> ReadAsync(string key, double duration, CancellationToken token) => Access(key, duration, null, token);
    internal async Task PutAsync(string key, double duration, double start, double end, CancellationToken token) =>
        _ = await Access(key, duration, (start, end), token).ConfigureAwait(false);
    private async Task<(double Start, double End)?> Access(string key, double duration, (double Start, double End)? point, CancellationToken token)
    {
        if (!LowerHex(key, 64) || !double.IsFinite(duration) || duration <= 0) throw MusicPlaybackCacheDataException.Corrupt();
        try
        {
            (double Start, double End)? Read(DatabaseSession db)
            {
                if (!db.TryQuerySingle(
                    "SELECT updated_at,start_sec,end_sec FROM windows_music_chorus WHERE resource_key=$key",
                    row => (At: row.GetInt64(0), Start: row.GetDouble(1), End: row.GetDouble(2)),
                    out var entry,
                    ("$key", key)))
                    return null;
                if (entry.At > Now || entry.At <= Now - Ttl) return null;
                return Fits(entry.Start, entry.End, duration) ? (entry.Start, entry.End) : null;
            }
            if (point is not { } value) return await database.ReadAsync(Read, token).ConfigureAwait(false);
            if (!Fits(value.Start, value.End, duration)) return null;
            return await database.WriteAsync(db =>
            {
                long now = Now;
                db.Execute("""
                    INSERT INTO windows_music_chorus(resource_key,updated_at,start_sec,end_sec)
                    VALUES($key,$at,$start,$end)
                    ON CONFLICT(resource_key) DO UPDATE SET
                        updated_at=excluded.updated_at,
                        start_sec=excluded.start_sec,
                        end_sec=excluded.end_sec
                    """, ("$key", key), ("$at", now), ("$start", value.Start), ("$end", value.End));
                db.Execute("DELETE FROM windows_music_chorus WHERE updated_at<=$expired OR updated_at>$now",
                    ("$expired", now - Ttl), ("$now", now));
                db.Execute("DELETE FROM windows_music_chorus WHERE resource_key IN (SELECT resource_key FROM windows_music_chorus ORDER BY updated_at DESC,resource_key LIMIT -1 OFFSET $limit)",
                    ("$limit", MaximumEntries));
                return Read(db);
            }, token, 512).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return null; }
    }
    private static bool LowerHex(string value, int length) =>
        value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Fits(double start, double end, double duration) =>
        double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start && end <= duration;
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                var row = PlaybackCacheRecords.Chorus.Read(db, [key]);
                if (row is null || row["at"]!.GetValue<long>() > Now || row["at"]!.GetValue<long>() <= Now - Ttl) return null;
                var start = row["start"]!.GetValue<double>(); var end = row["end"]!.GetValue<double>();
                return Fits(start, end, duration) ? (start, end) : null;
            }
            if (point is not { } value) return await database.ReadAsync(Read, token).ConfigureAwait(false);
            if (!Fits(value.Start, value.End, duration)) return null;
            return await database.WriteAsync(db =>
            {
                var next = new JsonObject { ["at"] = Now, ["start"] = value.Start, ["end"] = value.End };
                PlaybackCacheRecords.Chorus.Save(db, next, PlaybackCacheRecords.Chorus.Read(db, [key]), [key]);
                db.Execute("DELETE FROM windows_chorus WHERE at<=$expired OR at>$now", ("$expired", Now - Ttl), ("$now", Now));
                db.Execute("DELETE FROM windows_chorus WHERE resource_key IN (SELECT resource_key FROM windows_chorus ORDER BY at DESC,resource_key LIMIT -1 OFFSET $limit)", ("$limit", MaximumEntries));
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

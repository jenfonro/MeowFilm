using System.Text;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>
/// Private resolution candidates and confirmed playback history, never library or download data.
/// Unreadable or unusable cached values are misses; persistence is optional.
/// </summary>
internal sealed class MusicSourceCache(IStorageDatabase database, TimeProvider? time = null)
{
    internal const long HistoryTtl = 30L * 86400000;
    private const int MaximumBytes = 1024 * 1024, MaximumScopes = 4;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly HashSet<(string Scope, string Song)> invalidated = [];
    private readonly HashSet<(string Scope, string Song, string Source)> rejected = [];

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    internal async Task<PlaybackSourceEntry?> ReadAsync(string scope, string song, string signature, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        try
        {
            return await database.ReadSnapshotAsync(db =>
            {
                long now = Now;
                var entry = PlaybackSourceCacheStore.ReadEntry(db, scope, song, "urls");
                if (entry is null || entry.At > now || entry.At <= now - HistoryTtl || entry.Until <= now)
                    return null;
                lock (gate)
                {
                    if (invalidated.Contains((scope, song)) ||
                        entry.SourceKey is not null && rejected.Contains((scope, song, entry.SourceKey)))
                        return null;
                }
                var expires = ValidateCandidate(entry, scope, song, signature);
                if (expires is { } expiry && expiry <= now + 10000) return null;
                return entry.Signature == signature && entry.Until > now ? entry : null;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return null; }
    }

    internal async Task<PlaybackSourceEntry?> ReadHistoryAsync(string scope, string song, string policy, string request,
        long generation, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(policy);
        RequireText(request, 64);
        if (generation <= 0) throw MusicPlaybackCacheDataException.Corrupt();
        try
        {
            return await database.ReadSnapshotAsync(db =>
            {
                long now = Now;
                var entry = PlaybackSourceCacheStore.ReadEntry(db, scope, song, "pointers");
                if (entry is null || entry.At > now || entry.At <= now - HistoryTtl)
                    return null;
                lock (gate)
                {
                    if (entry.SourceKey is not null && rejected.Contains((scope, song, entry.SourceKey)))
                        return null;
                }
                if (entry.QualityPolicySignature != policy ||
                    entry.RequestedQuality != request ||
                    entry.Generation != generation)
                    return null;
                ValidateHistory(entry, scope, song);
                return entry;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { return null; }
    }

    internal async Task PutAsync(string scope, string song, string signature, PlaybackSourceEntry info, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        try
        {
            bool written = await database.WriteAsync(db =>
            {
                long at = Now;
                long? expires = info.ExpiresAt;
                long ttl = at > long.MaxValue - HistoryTtl ? long.MaxValue : at + HistoryTtl;
                long until = expires is null ? ttl : Math.Min(expires.Value - 10000, ttl);
                if (until <= at) return false;
                var candidate = info with { Signature = signature, At = at, Until = until, Recording = null };
                int bytes = EstimateEntryBytes(candidate);
                if (bytes > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
                PlaybackSourceCacheStore.SaveEntry(db, scope, song, "urls", candidate with { StoredBytes = bytes });
                Trim(db, at);
                return true;
            }, token, 65536).ConfigureAwait(false);
            if (written)
            {
                lock (gate) { invalidated.Remove((scope, song)); rejected.RemoveWhere(key => key.Scope == scope && key.Song == song); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
    }

    internal async Task ConfirmAsync(string scope, string song, string signature, PlaybackSourceEntry info,
        MusicSongSourceRecord recording, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        RequireHash(signature);
        var durable = NativeMusicSong.DurableSource(recording);
        var snapshot = ToRecordingSnapshot(durable);
        try
        {
            await database.WriteAsync(db =>
            {
                long at = Now;
                long until = at > long.MaxValue - HistoryTtl ? long.MaxValue : at + HistoryTtl;
                var pointer = new PlaybackSourceEntry(
                    signature, at, until, 0,
                    Ok: null, Kind: null, Platform: info.Platform, SourceKey: info.SourceKey, Scope: scope,
                    Generation: info.Generation, Quality: info.Quality, ActualQuality: info.ActualQuality,
                    QualityVerified: info.QualityVerified, PlaybackVerified: null,
                    QualityPolicySignature: info.QualityPolicySignature, QualityCacheSignature: null,
                    Url: null, ExpiresAt: null, Duration: info.Duration, Size: null, Full: null,
                    Delivery: info.Delivery, RequestedQuality: info.RequestedQuality,
                    Headers: null, Recording: snapshot);
                int bytes = EstimateEntryBytes(pointer);
                if (bytes > MaximumBytes) throw MusicPlaybackCacheDataException.Limit();
                PlaybackSourceCacheStore.SaveEntry(db, scope, song, "pointers", pointer with { StoredBytes = bytes });
                Trim(db, at);
                return true;
            }, token, 65536).ConfigureAwait(false);
            lock (gate) { invalidated.Remove((scope, song)); rejected.RemoveWhere(key => key.Scope == scope && key.Song == song); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
    }

    internal async Task InvalidateAsync(string scope, string song, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        lock (gate) invalidated.Add((scope, song));
        try
        {
            await database.WriteAsync(db =>
            {
                PlaybackSourceCacheStore.DeleteEntry(db, scope, song, "urls");
                Trim(db, Now);
                return true;
            }, token, 4096).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
    }

    internal async Task RejectRecordingAsync(string scope, string song, string sourceKey, CancellationToken token)
    {
        ValidateIdentity(scope, song);
        lock (gate) rejected.Add((scope, song, sourceKey));
        try
        {
            await database.WriteAsync(db =>
            {
                db.Execute("""
                    DELETE FROM windows_music_playback_sources
                    WHERE account_scope=$scope AND song_id=$song AND info_source_key=$skey
                    """, ("$scope", scope), ("$song", song), ("$skey", sourceKey));
                Trim(db, Now);
                return true;
            }, token, 4096).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (MusicPlaybackCacheDataException.IsCacheFailure(error)) { }
    }

    internal static PlaybackRecordingSnapshot ToRecordingSnapshot(MusicSongSourceRecord source) =>
        new(
            source.SourceKey, source.Platform, source.InfoSource,
            source.SongMid, source.Mid, source.NativeId, source.Hash,
            source.Name, source.Singer, source.AlbumName,
            source.Duration, source.Img, source.QualityKnown,
            source.ArtistNames, source.WireExtras);

    internal static MusicSongSourceRecord ToSongSourceRecord(PlaybackRecordingSnapshot rec) =>
        new(
            rec.SourceKey ?? "",
            rec.Platform ?? "",
            rec.InfoSource ?? rec.Platform ?? "",
            rec.SongMid, rec.Mid, rec.NativeId, rec.Hash,
            rec.Name, rec.Singer, rec.AlbumName,
            rec.Duration, rec.Img, rec.QualityKnown,
            rec.ArtistNames, rec.WireExtras);

    private static int EstimateEntryBytes(PlaybackSourceEntry entry)
    {
        static int Str(string? s) => s is null ? 0 : Encoding.UTF8.GetByteCount(s);
        int bytes = 256 + Str(entry.Signature) + Str(entry.Kind) + Str(entry.Platform) + Str(entry.SourceKey) +
            Str(entry.Scope) + Str(entry.Quality) + Str(entry.ActualQuality) +
            Str(entry.QualityPolicySignature) + Str(entry.QualityCacheSignature) + Str(entry.Url) +
            Str(entry.Delivery) + Str(entry.RequestedQuality);
        if (entry.Headers is not null)
            foreach (var (k, v) in entry.Headers)
                bytes += 8 + Str(k) + Str(v);
        if (entry.Recording is { } rec)
        {
            bytes += 128 + Str(rec.SourceKey) + Str(rec.Platform) + Str(rec.InfoSource) +
                Str(rec.SongMid) + Str(rec.Mid) + Str(rec.NativeId) + Str(rec.Hash) +
                Str(rec.Name) + Str(rec.Singer) + Str(rec.AlbumName) + Str(rec.Img);
            foreach (string artist in rec.ArtistNames) bytes += 8 + Str(artist);
            if (rec.WireExtras is not null) bytes += rec.WireExtras.EstimateBytes();
        }
        return bytes;
    }

    private static void Trim(DatabaseSession db, long now)
    {
        db.Execute("DELETE FROM windows_music_playback_sources WHERE updated_at_ms<=$expired OR updated_at_ms>$now OR expires_until_ms<=$now", ("$expired", now - HistoryTtl), ("$now", now));
        db.Execute("DELETE FROM windows_music_playback_sources WHERE account_scope IN (SELECT account_scope FROM windows_music_playback_sources GROUP BY account_scope ORDER BY max(updated_at_ms) DESC LIMIT -1 OFFSET $limit)", ("$limit", MaximumScopes));
        var scopes = db.Query("SELECT DISTINCT account_scope FROM windows_music_playback_sources", row => row.GetString(0));
        foreach (var (kind, limit) in new[] { ("urls", 50), ("pointers", 200) })
            foreach (var scope in scopes)
                db.Execute("DELETE FROM windows_music_playback_sources WHERE rowid IN (SELECT rowid FROM windows_music_playback_sources WHERE account_scope=$scope AND entry_kind=$kind ORDER BY updated_at_ms DESC,song_id DESC LIMIT -1 OFFSET $limit)",
                    ("$scope", scope), ("$kind", kind), ("$limit", limit));
        long total = db.Scalar<long>("SELECT coalesce(sum(stored_bytes),0) FROM windows_music_playback_sources");
        if (total > MaximumBytes)
        {
            foreach (var (rowId, bytes) in db.Query(
                "SELECT rowid,stored_bytes FROM windows_music_playback_sources ORDER BY updated_at_ms,account_scope,song_id",
                row => (row.GetInt64(0), row.GetInt64(1))))
            {
                if (total <= MaximumBytes) break;
                if (db.Execute("DELETE FROM windows_music_playback_sources WHERE rowid=$id", ("$id", rowId)) > 0)
                    total -= bytes;
            }
        }
    }

    internal static long? ValidateCandidate(PlaybackSourceEntry info, string scope, string song, string signature)
    {
        if (info.Ok != true || RequireNonNullText(info.Kind, 32) != "official")
            throw MusicPlaybackCacheDataException.Corrupt();
        string platform = RequireNonNullText(info.Platform, 16);
        if (platform is not ("wy" or "tx" or "kg") || RequireNonNullText(info.Scope, 128) != scope)
            throw MusicPlaybackCacheDataException.Corrupt();
        _ = RequireNonNullText(info.SourceKey, 8192);
        if (info.Generation is not > 0) throw MusicPlaybackCacheDataException.Corrupt();
        _ = RequireNonNullText(info.Quality, 64);
        _ = RequireNonNullText(info.RequestedQuality, 64);
        string actual = RequireNonNullText(info.ActualQuality, 64, empty: true);
        if (info.QualityVerified is not { } verified || info.PlaybackVerified is not { } playbackVerified)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (verified && actual.Length == 0 || playbackVerified)
            throw MusicPlaybackCacheDataException.Corrupt();
        string policy = RequireNonNullText(info.QualityPolicySignature, 64);
        string cache = RequireNonNullText(info.QualityCacheSignature, 64);
        RequireHash(policy);
        RequireHash(cache);
        if (cache != signature) throw MusicPlaybackCacheDataException.Corrupt();
        ValidateMediaUrl(RequireNonNullText(info.Url, 16384));
        if (info.Headers is { Count: > 0 })
            throw MusicPlaybackCacheDataException.Corrupt();
        if (info.ExpiresAt is <= 0) throw MusicPlaybackCacheDataException.Corrupt();
        if (info.Duration is not { } dur || !double.IsFinite(dur) || dur < 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (info.Size is not >= 0 || info.Full is null)
            throw MusicPlaybackCacheDataException.Corrupt();
        if (RequireNonNullText(info.Delivery, 128) != platform + "-official")
            throw MusicPlaybackCacheDataException.Corrupt();
        ValidateIdentity(scope, song);
        return info.ExpiresAt;
    }

    private static void ValidateHistory(PlaybackSourceEntry info, string scope, string song)
    {
        string platform = RequireNonNullText(info.Platform, 16);
        if (platform is not ("wy" or "tx" or "kg") || RequireNonNullText(info.Scope, 128) != scope)
            throw MusicPlaybackCacheDataException.Corrupt();
        string sourceKey = RequireNonNullText(info.SourceKey, 8192);
        if (info.Generation is not > 0) throw MusicPlaybackCacheDataException.Corrupt();
        _ = RequireNonNullText(info.Quality, 64);
        if (info.ActualQuality is not null) RequireText(info.ActualQuality, 64, empty: true);
        if (info.QualityVerified is not { } verified) throw MusicPlaybackCacheDataException.Corrupt();
        if (verified && string.IsNullOrEmpty(info.ActualQuality)) throw MusicPlaybackCacheDataException.Corrupt();
        if (info.Duration is { } dur && (!double.IsFinite(dur) || dur < 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        if (info.Delivery is not null && RequireNonNullText(info.Delivery, 128) != platform + "-official")
            throw MusicPlaybackCacheDataException.Corrupt();
        _ = RequireNonNullText(info.RequestedQuality, 64);
        RequireHash(RequireNonNullText(info.QualityPolicySignature, 64));
        ValidateIdentity(scope, song);

        if (info.Recording is not { } rawRec)
            throw MusicPlaybackCacheDataException.Corrupt();
        var record = NativeMusicSong.DurableSource(ToSongSourceRecord(rawRec));
        if (record.Platform != platform || record.SourceKey != sourceKey)
            throw MusicPlaybackCacheDataException.Corrupt();
    }

    private static void ValidateIdentity(string scope, string song)
    {
        RequireText(scope, 128);
        RequireText(song, 2048);
    }

    private static string RequireNonNullText(string? value, int maximum, bool empty = false)
    {
        if (value is null) throw MusicPlaybackCacheDataException.Corrupt();
        RequireText(value, maximum, empty);
        return value;
    }

    private static void RequireText(string text, int maximum, bool empty = false)
    {
        if (text.Length > maximum || !empty && text.Length == 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character < 32 || character == 127 || char.IsLowSurrogate(character))
                throw MusicPlaybackCacheDataException.Corrupt();
            if (char.IsHighSurrogate(character) &&
                (++i >= text.Length || !char.IsLowSurrogate(text[i])))
                throw MusicPlaybackCacheDataException.Corrupt();
        }
    }

    private static void RequireHash(string value)
    {
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw MusicPlaybackCacheDataException.Corrupt();
    }

    internal static void ValidateMediaUrl(string value)
    {
        if (value.Any(char.IsWhiteSpace) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw MusicPlaybackCacheDataException.Corrupt();
    }
}

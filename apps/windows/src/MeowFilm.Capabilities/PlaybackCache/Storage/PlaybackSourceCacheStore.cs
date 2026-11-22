using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed record PlaybackSourceEntry(
    string Signature,
    long At,
    long Until,
    long StoredBytes,
    bool? Ok,
    string? Kind,
    string? Platform,
    string? SourceKey,
    string? Scope,
    long? Generation,
    string? Quality,
    string? ActualQuality,
    bool? QualityVerified,
    bool? PlaybackVerified,
    string? QualityPolicySignature,
    string? QualityCacheSignature,
    string? Url,
    long? ExpiresAt,
    double? Duration,
    long? Size,
    bool? Full,
    string? Delivery,
    string? RequestedQuality,
    IReadOnlyDictionary<string, string>? Headers,
    PlaybackRecordingSnapshot? Recording);

internal sealed record PlaybackRecordingSnapshot(
    string? SourceKey,
    string? Platform,
    string? InfoSource,
    string? Name,
    string? Singer,
    string? AlbumName,
    double? Duration,
    string? Img,
    bool? QualityKnown,
    StorageValue? WireExtras);

internal static class PlaybackSourceCacheStore
{
    internal static PlaybackSourceEntry? ReadEntry(DatabaseSession db, string scope, string songId, string entryKind)
    {
        if (!db.TryQuerySingle("""
            SELECT signature,updated_at_ms,expires_until_ms,stored_bytes,
                   info_ok,info_kind,info_platform,info_source_key,info_scope,info_generation,
                   info_quality,info_actual_quality,info_quality_verified,info_playback_verified,
                   info_quality_policy_signature,info_quality_cache_signature,info_url,info_expires_at_ms,
                   info_duration,info_size,info_full,info_delivery,info_requested_quality,
                   rec_source_key,rec_platform,rec_info_source,rec_name,rec_singer,rec_album_name,
                   rec_duration,rec_img,rec_quality_known
            FROM windows_music_playback_sources
            WHERE account_scope=$scope AND song_id=$song AND entry_kind=$kind
            """,
            row => ReadMainRow(db, row, scope, songId, entryKind),
            out var result,
            ("$scope", scope), ("$song", songId), ("$kind", entryKind)))
            return null;
        return result;
    }

    internal static void DeleteEntry(DatabaseSession db, string scope, string songId, string entryKind)
    {
        db.Execute("""
            DELETE FROM windows_music_playback_sources
            WHERE account_scope=$scope AND song_id=$song AND entry_kind=$kind
            """, ("$scope", scope), ("$song", songId), ("$kind", entryKind));
    }

    internal static void SaveEntry(DatabaseSession db, string scope, string songId, string entryKind, PlaybackSourceEntry entry)
    {
        var rec = entry.Recording;
        db.Execute("""
            INSERT INTO windows_music_playback_sources(
                account_scope,song_id,entry_kind,signature,updated_at_ms,expires_until_ms,stored_bytes,
                info_ok,info_kind,info_platform,info_source_key,info_scope,info_generation,
                info_quality,info_actual_quality,info_quality_verified,info_playback_verified,
                info_quality_policy_signature,info_quality_cache_signature,info_url,info_expires_at_ms,
                info_duration,info_size,info_full,info_delivery,info_requested_quality,
                rec_source_key,rec_platform,rec_info_source,rec_name,rec_singer,rec_album_name,
                rec_duration,rec_img,rec_quality_known)
            VALUES(
                $scope,$song,$kind,$sig,$at,$until,$bytes,
                $iok,$ikind,$iplat,$isrc,$iscope,$igen,
                $iqual,$iact,$iqv,$ipv,
                $iqps,$iqcs,$iurl,$iexp,
                $idur,$isize,$ifull,$idel,$ireq,
                $rskey,$rplat,$risrc,$rname,$rsinger,$ralbname,$rdur,$rimg,$rqk)
            ON CONFLICT(account_scope,song_id,entry_kind) DO UPDATE SET
                signature=excluded.signature,
                updated_at_ms=excluded.updated_at_ms,
                expires_until_ms=excluded.expires_until_ms,
                stored_bytes=excluded.stored_bytes,
                info_ok=excluded.info_ok,
                info_kind=excluded.info_kind,
                info_platform=excluded.info_platform,
                info_source_key=excluded.info_source_key,
                info_scope=excluded.info_scope,
                info_generation=excluded.info_generation,
                info_quality=excluded.info_quality,
                info_actual_quality=excluded.info_actual_quality,
                info_quality_verified=excluded.info_quality_verified,
                info_playback_verified=excluded.info_playback_verified,
                info_quality_policy_signature=excluded.info_quality_policy_signature,
                info_quality_cache_signature=excluded.info_quality_cache_signature,
                info_url=excluded.info_url,
                info_expires_at_ms=excluded.info_expires_at_ms,
                info_duration=excluded.info_duration,
                info_size=excluded.info_size,
                info_full=excluded.info_full,
                info_delivery=excluded.info_delivery,
                info_requested_quality=excluded.info_requested_quality,
                rec_source_key=excluded.rec_source_key,
                rec_platform=excluded.rec_platform,
                rec_info_source=excluded.rec_info_source,
                rec_name=excluded.rec_name,
                rec_singer=excluded.rec_singer,
                rec_album_name=excluded.rec_album_name,
                rec_duration=excluded.rec_duration,
                rec_img=excluded.rec_img,
                rec_quality_known=excluded.rec_quality_known
            """,
            ("$scope", scope), ("$song", songId), ("$kind", entryKind),
            ("$sig", entry.Signature), ("$at", entry.At), ("$until", entry.Until), ("$bytes", entry.StoredBytes),
            ("$iok", entry.Ok), ("$ikind", entry.Kind), ("$iplat", entry.Platform), ("$isrc", entry.SourceKey),
            ("$iscope", entry.Scope), ("$igen", entry.Generation),
            ("$iqual", entry.Quality), ("$iact", entry.ActualQuality),
            ("$iqv", entry.QualityVerified), ("$ipv", entry.PlaybackVerified),
            ("$iqps", entry.QualityPolicySignature), ("$iqcs", entry.QualityCacheSignature),
            ("$iurl", entry.Url), ("$iexp", entry.ExpiresAt),
            ("$idur", entry.Duration), ("$isize", entry.Size), ("$ifull", entry.Full),
            ("$idel", entry.Delivery), ("$ireq", entry.RequestedQuality),
            ("$rskey", rec?.SourceKey), ("$rplat", rec?.Platform), ("$risrc", rec?.InfoSource),
            ("$rname", rec?.Name), ("$rsinger", rec?.Singer), ("$ralbname", rec?.AlbumName),
            ("$rdur", rec?.Duration), ("$rimg", rec?.Img), ("$rqk", rec?.QualityKnown));

        db.Execute("""
            DELETE FROM windows_music_playback_source_headers
            WHERE account_scope=$scope AND song_id=$song AND entry_kind=$kind
            """, ("$scope", scope), ("$song", songId), ("$kind", entryKind));
        if (entry.Headers is { Count: > 0 } headers)
        {
            foreach (var (k, v) in headers)
                db.Execute("""
                    INSERT INTO windows_music_playback_source_headers(account_scope,song_id,entry_kind,header_name,header_value)
                    VALUES($scope,$song,$kind,$k,$v)
                    """, ("$scope", scope), ("$song", songId), ("$kind", entryKind), ("$k", k), ("$v", v));
        }

        TypedValueNodes.Replace(db, "windows_music_playback_source_rec_extras",
            [("account_scope", scope), ("song_id", songId), ("entry_kind", entryKind)],
            rec?.WireExtras);
    }

    private static PlaybackSourceEntry ReadMainRow(DatabaseSession db, SqliteDataReader row, string scope, string songId, string entryKind)
    {
        Dictionary<string, string>? headers = null;
        if (entryKind == "urls")
        {
            headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in db.Query("""
                SELECT header_name,header_value FROM windows_music_playback_source_headers
                WHERE account_scope=$scope AND song_id=$song AND entry_kind=$kind
                ORDER BY header_name
                """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope), ("$song", songId), ("$kind", entryKind)))
                headers[k] = v;
        }

        string? recSourceKey = row.GetNullableString(23);
        string? recPlatform = row.GetNullableString(24);
        PlaybackRecordingSnapshot? recording = null;
        if (recSourceKey is not null || recPlatform is not null)
        {
            var extras = TypedValueNodes.ReadObject(db, "windows_music_playback_source_rec_extras",
                [("account_scope", scope), ("song_id", songId), ("entry_kind", entryKind)]);
            recording = new PlaybackRecordingSnapshot(
                recSourceKey, recPlatform, row.GetNullableString(25),
                row.GetNullableString(26), row.GetNullableString(27), row.GetNullableString(28),
                row.GetNullableDouble(29), row.GetNullableString(30), row.GetNullableBool(31),
                extras);
        }

        return new PlaybackSourceEntry(
            row.GetString(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3),
            row.GetNullableBool(4), row.GetNullableString(5), row.GetNullableString(6),
            row.GetNullableString(7), row.GetNullableString(8), row.GetNullableInt64(9),
            row.GetNullableString(10), row.GetNullableString(11),
            row.GetNullableBool(12), row.GetNullableBool(13),
            row.GetNullableString(14), row.GetNullableString(15),
            row.GetNullableString(16), row.GetNullableInt64(17),
            row.GetNullableDouble(18), row.GetNullableInt64(19),
            row.GetNullableBool(20), row.GetNullableString(21), row.GetNullableString(22),
            headers, recording);
    }
}

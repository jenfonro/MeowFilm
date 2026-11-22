using System.Text.Json;
using System.Text.Json.Nodes;
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
    JsonObject? WireExtras);

internal static class PlaybackSourceCacheStore
{
    private static readonly HashSet<string> CoreMusicInfoFields = new(StringComparer.Ordinal)
    {
        "source", "name", "singer", "albumName", "duration", "img", "qualityKnown"
    };

    internal static JsonObject? Read(DatabaseSession db, string scope, string songId, string entryKind) =>
        ReadEntry(db, scope, songId, entryKind) is { } entry ? ToJson(entry, songId, entryKind) : null;

    internal static PlaybackSourceEntry? ReadEntry(DatabaseSession db, string scope, string songId, string entryKind)
    {
        if (!db.TryQuerySingle("""
            SELECT signature,updated_at,expires_until,stored_bytes,
                   info_ok,info_kind,info_platform,info_source_key,info_scope,info_generation,
                   info_quality,info_actual_quality,info_quality_verified,info_playback_verified,
                   info_quality_policy_signature,info_quality_cache_signature,info_url,info_expires_at,
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

    internal static void Save(DatabaseSession db, string scope, string songId, string entryKind, JsonObject? value)
    {
        if (value is null)
        {
            db.Execute("""
                DELETE FROM windows_music_playback_sources
                WHERE account_scope=$scope AND song_id=$song AND entry_kind=$kind
                """, ("$scope", scope), ("$song", songId), ("$kind", entryKind));
            return;
        }
        SaveEntry(db, scope, songId, entryKind, FromJson(value));
    }

    internal static void SaveEntry(DatabaseSession db, string scope, string songId, string entryKind, PlaybackSourceEntry entry)
    {
        var rec = entry.Recording;
        db.Execute("""
            INSERT INTO windows_music_playback_sources(
                account_scope,song_id,entry_kind,signature,updated_at,expires_until,stored_bytes,
                info_ok,info_kind,info_platform,info_source_key,info_scope,info_generation,
                info_quality,info_actual_quality,info_quality_verified,info_playback_verified,
                info_quality_policy_signature,info_quality_cache_signature,info_url,info_expires_at,
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
                updated_at=excluded.updated_at,
                expires_until=excluded.expires_until,
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
                info_expires_at=excluded.info_expires_at,
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

    private static PlaybackSourceEntry FromJson(JsonObject value)
    {
        var info = value["info"] as JsonObject ?? new JsonObject();
        Dictionary<string, string>? headers = null;
        if (info["headers"] is JsonObject hdrs)
        {
            headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in hdrs)
                if (pair.Value is JsonValue v && v.TryGetValue<string>(out var text))
                    headers[pair.Key] = text;
        }

        PlaybackRecordingSnapshot? recording = null;
        if (info["recording"] is JsonObject rec)
        {
            var mi = rec["musicInfo"] as JsonObject;
            JsonObject? wireExtras = null;
            if (mi is not null)
            {
                foreach (var pair in mi)
                {
                    if (CoreMusicInfoFields.Contains(pair.Key)) continue;
                    wireExtras ??= new JsonObject();
                    wireExtras[pair.Key] = pair.Value?.DeepClone();
                }
            }
            recording = new PlaybackRecordingSnapshot(
                Str(rec, "sourceKey"), Str(rec, "source"), Str(mi, "source"),
                Str(mi, "name"), Str(mi, "singer"), Str(mi, "albumName"),
                Dbl(mi, "duration"), Str(mi, "img"), Bool(mi, "qualityKnown"),
                wireExtras);
        }

        return new PlaybackSourceEntry(
            Str(value, "signature")!, Int64(value, "at")!.Value, Int64(value, "until")!.Value, Int64(value, "storedBytes")!.Value,
            Bool(info, "ok"), Str(info, "kind"), Str(info, "platform"), Str(info, "sourceKey"), Str(info, "scope"),
            Int64(info, "generation"), Str(info, "quality"), Str(info, "actualQuality"),
            Bool(info, "qualityVerified"), Bool(info, "playbackVerified"),
            Str(info, "qualityPolicySignature"), Str(info, "qualityCacheSignature"),
            Str(info, "url"), Int64(info, "expiresAt"), Dbl(info, "duration"), Int64(info, "size"),
            Bool(info, "full"), Str(info, "delivery"), Str(info, "requestedQuality"),
            headers, recording);
    }

    private static JsonObject ToJson(PlaybackSourceEntry e, string songId, string entryKind)
    {
        var info = new JsonObject { ["songId"] = songId };
        SetNonNull(info, "ok", e.Ok);
        SetNonNull(info, "kind", e.Kind);
        SetNonNull(info, "platform", e.Platform);
        SetNonNull(info, "sourceKey", e.SourceKey);
        SetNonNull(info, "scope", e.Scope);
        SetNonNull(info, "generation", e.Generation);
        SetNonNull(info, "quality", e.Quality);
        if (entryKind == "pointers") info["actualQuality"] = e.ActualQuality;
        else SetNonNull(info, "actualQuality", e.ActualQuality);
        SetNonNull(info, "qualityVerified", e.QualityVerified);
        SetNonNull(info, "playbackVerified", e.PlaybackVerified);
        SetNonNull(info, "qualityPolicySignature", e.QualityPolicySignature);
        SetNonNull(info, "qualityCacheSignature", e.QualityCacheSignature);
        SetNonNull(info, "url", e.Url);
        if (entryKind == "urls") info["expiresAt"] = e.ExpiresAt;
        else SetNonNull(info, "expiresAt", e.ExpiresAt);
        if (entryKind == "pointers") info["duration"] = e.Duration;
        else SetNonNull(info, "duration", e.Duration);
        SetNonNull(info, "size", e.Size);
        SetNonNull(info, "full", e.Full);
        if (entryKind == "pointers") info["delivery"] = e.Delivery;
        else SetNonNull(info, "delivery", e.Delivery);
        SetNonNull(info, "requestedQuality", e.RequestedQuality);

        if (entryKind == "urls")
        {
            var headers = new JsonObject();
            if (e.Headers is not null)
                foreach (var (k, v) in e.Headers) headers[k] = v;
            info["headers"] = headers;
        }

        if (e.Recording is { } rec)
        {
            var mi = new JsonObject();
            SetNonNull(mi, "source", rec.InfoSource);
            SetNonNull(mi, "name", rec.Name);
            SetNonNull(mi, "singer", rec.Singer);
            SetNonNull(mi, "albumName", rec.AlbumName);
            mi["duration"] = rec.Duration;
            SetNonNull(mi, "img", rec.Img);
            SetNonNull(mi, "qualityKnown", rec.QualityKnown);
            if (rec.WireExtras is not null)
                foreach (var pair in rec.WireExtras)
                    mi[pair.Key] = pair.Value?.DeepClone();

            var recObj = new JsonObject { ["musicInfo"] = mi };
            if (rec.SourceKey is not null) recObj["sourceKey"] = rec.SourceKey;
            if (rec.Platform is not null) recObj["source"] = rec.Platform;
            info["recording"] = recObj;
        }

        return new JsonObject
        {
            ["signature"] = e.Signature,
            ["at"] = e.At,
            ["until"] = e.Until,
            ["storedBytes"] = e.StoredBytes,
            ["info"] = info
        };
    }

    private static void SetNonNull(JsonObject obj, string key, string? val) { if (val is not null) obj[key] = val; }
    private static void SetNonNull(JsonObject obj, string key, long? val) { if (val is { } v) obj[key] = v; }
    private static void SetNonNull(JsonObject obj, string key, double? val) { if (val is { } v) obj[key] = v; }
    private static void SetNonNull(JsonObject obj, string key, bool? val) { if (val is { } v) obj[key] = v; }

    private static string? Str(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<string>(out var s) ? s
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()
            : null : null;
    private static long? Int64(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<long>(out var n) ? n
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var en) ? en
            : null : null;
    private static double? Dbl(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<float>(out var f) ? f
            : v.TryGetValue<decimal>(out var m) ? (double)m
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var ed) ? ed
            : null : null;
    private static bool? Bool(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<bool>(out var b) ? b
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean()
            : null : null;
}

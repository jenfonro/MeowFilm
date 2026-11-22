using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Capabilities.PlaybackCache;

internal sealed record AudioFileInfoRecord(
    string? Kind,
    string? Platform,
    string? SongId,
    string? SourceKey,
    string? Scope,
    long? Generation,
    string? InstallationId,
    string? ProviderName,
    string? Quality,
    string? RequestedQuality,
    string? ActualQuality,
    bool? QualityVerified,
    bool? PlaybackVerified,
    string? QualityPolicySignature,
    string? QualityCacheSignature,
    double? Duration,
    long? Size,
    bool? Full,
    string? Delivery,
    string? QualityEvidence,
    string? Codec,
    long? SampleRate,
    long? BitDepth,
    long? Channels,
    long? Bitrate)
{
    internal JsonObject ToJson()
    {
        var info = new JsonObject();
        SetNonNull(info, "kind", Kind);
        SetNonNull(info, "platform", Platform);
        SetNonNull(info, "songId", SongId);
        SetNonNull(info, "sourceKey", SourceKey);
        SetNonNull(info, "scope", Scope);
        SetNonNull(info, "generation", Generation);
        SetNonNull(info, "installationId", InstallationId);
        SetNonNull(info, "providerName", ProviderName);
        SetNonNull(info, "quality", Quality);
        SetNonNull(info, "requestedQuality", RequestedQuality);
        SetNonNull(info, "actualQuality", ActualQuality);
        SetNonNull(info, "qualityVerified", QualityVerified);
        SetNonNull(info, "playbackVerified", PlaybackVerified);
        SetNonNull(info, "qualityPolicySignature", QualityPolicySignature);
        SetNonNull(info, "qualityCacheSignature", QualityCacheSignature);
        SetNonNull(info, "duration", Duration);
        SetNonNull(info, "size", Size);
        SetNonNull(info, "full", Full);
        SetNonNull(info, "delivery", Delivery);
        info["qualityEvidence"] = QualityEvidence;
        info["codec"] = Codec;
        info["sampleRate"] = SampleRate;
        info["bitDepth"] = BitDepth;
        info["channels"] = Channels;
        info["bitrate"] = Bitrate;
        return info;
    }

    internal int EstimateBytes()
    {
        int bytes = 320;
        bytes += Utf8Len(Kind) + Utf8Len(Platform) + Utf8Len(SongId) + Utf8Len(SourceKey) + Utf8Len(Scope);
        bytes += Utf8Len(InstallationId) + Utf8Len(ProviderName) + Utf8Len(Quality) + Utf8Len(RequestedQuality);
        bytes += Utf8Len(ActualQuality) + Utf8Len(QualityPolicySignature) + Utf8Len(QualityCacheSignature);
        bytes += Utf8Len(Delivery) + Utf8Len(QualityEvidence) + Utf8Len(Codec);
        return bytes;
    }

    internal static AudioFileInfoRecord FromJson(JsonObject info) => new(
        Str(info, "kind"),
        Str(info, "platform"),
        Str(info, "songId"),
        Str(info, "sourceKey"),
        Str(info, "scope"),
        Int64(info, "generation"),
        Str(info, "installationId"),
        Str(info, "providerName"),
        Str(info, "quality"),
        Str(info, "requestedQuality"),
        Str(info, "actualQuality"),
        Bool(info, "qualityVerified"),
        Bool(info, "playbackVerified"),
        Str(info, "qualityPolicySignature"),
        Str(info, "qualityCacheSignature"),
        Dbl(info, "duration"),
        Int64(info, "size"),
        Bool(info, "full"),
        Str(info, "delivery"),
        Str(info, "qualityEvidence"),
        Str(info, "codec"),
        Int64(info, "sampleRate"),
        Int64(info, "bitDepth"),
        Int64(info, "channels"),
        Int64(info, "bitrate"));

    private static int Utf8Len(string? s) => s is null ? 0 : Encoding.UTF8.GetByteCount(s);
    private static void SetNonNull(JsonObject obj, string key, string? val) { if (val is not null) obj[key] = val; }
    private static void SetNonNull(JsonObject obj, string key, long? val) { if (val is { } v) obj[key] = v; }
    private static void SetNonNull(JsonObject obj, string key, double? val) { if (val is { } v) obj[key] = v; }
    private static void SetNonNull(JsonObject obj, string key, bool? val) { if (val is { } v) obj[key] = v; }

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue v
            ? v.TryGetValue<string>(out var s) ? s
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()
            : null : null;
    private static long? Int64(JsonObject obj, string key) =>
        obj[key] is JsonValue v
            ? v.TryGetValue<long>(out var n) ? n
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var en) ? en
            : null : null;
    private static double? Dbl(JsonObject obj, string key) =>
        obj[key] is JsonValue v
            ? v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<float>(out var f) ? f
            : v.TryGetValue<decimal>(out var m) ? (double)m
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var ed) ? ed
            : null : null;
    private static bool? Bool(JsonObject obj, string key) =>
        obj[key] is JsonValue v
            ? v.TryGetValue<bool>(out var b) ? b
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean()
            : null : null;
}

internal sealed record AudioFileCacheRecord(
    string ResourceKey,
    string FileName,
    long FileSize,
    string Sha256,
    string Signature,
    long ConfirmedAt,
    double? Duration,
    AudioFileInfoRecord Info)
{
    internal int EstimateBytes() =>
        160 + Encoding.UTF8.GetByteCount(ResourceKey) + Encoding.UTF8.GetByteCount(FileName) +
        Encoding.UTF8.GetByteCount(Sha256) + Encoding.UTF8.GetByteCount(Signature) + Info.EstimateBytes();
}

internal static class AudioFileCacheStore
{
    private const string SelectColumns = """
        resource_key,file_name,file_size,sha256,signature,confirmed_at,duration,
        info_kind,info_platform,info_song_id,info_source_key,info_scope,info_generation,
        info_installation_id,info_provider_name,info_quality,info_requested_quality,info_actual_quality,
        info_quality_verified,info_playback_verified,info_quality_policy_signature,info_quality_cache_signature,
        info_duration,info_size,info_full,info_delivery,info_quality_evidence,
        info_codec,info_sample_rate,info_bit_depth,info_channels,info_bitrate
        """;

    internal static AudioFileCacheRecord? ReadOne(DatabaseSession db, string resourceKey) =>
        db.TryQuerySingle($"SELECT {SelectColumns} FROM windows_music_audio_files WHERE resource_key=$key",
            ReadRow, out var row, ("$key", resourceKey))
            ? row
            : null;

    internal static IReadOnlyList<AudioFileCacheRecord> ReadByScriptSong(DatabaseSession db, string songId) =>
        db.Query($"SELECT {SelectColumns} FROM windows_music_audio_files WHERE info_song_id=$song AND info_kind='script'",
            ReadRow, ("$song", songId));

    internal static IReadOnlyList<AudioFileCacheRecord> ReadAll(DatabaseSession db) =>
        db.Query($"SELECT {SelectColumns} FROM windows_music_audio_files", ReadRow);

    internal static void Save(DatabaseSession db, string resourceKey, AudioFileCacheRecord? value)
    {
        if (value is null)
        {
            db.Execute("DELETE FROM windows_music_audio_files WHERE resource_key=$key", ("$key", resourceKey));
            return;
        }
        var info = value.Info;
        db.Execute("""
            INSERT INTO windows_music_audio_files(
                resource_key,file_name,file_size,sha256,signature,confirmed_at,duration,
                info_kind,info_platform,info_song_id,info_source_key,info_scope,info_generation,
                info_installation_id,info_provider_name,info_quality,info_requested_quality,info_actual_quality,
                info_quality_verified,info_playback_verified,info_quality_policy_signature,info_quality_cache_signature,
                info_duration,info_size,info_full,info_delivery,info_quality_evidence,
                info_codec,info_sample_rate,info_bit_depth,info_channels,info_bitrate)
            VALUES(
                $key,$file,$size,$sha,$sig,$conf,$dur,
                $ikind,$iplat,$isong,$isrc,$iscope,$igen,
                $iinst,$iprov,$iqual,$ireq,$iact,
                $iqv,$ipv,$iqps,$iqcs,
                $idur,$isize,$ifull,$idel,$iqe,
                $icodec,$isr,$ibd,$ich,$ibr)
            ON CONFLICT(resource_key) DO UPDATE SET
                file_name=excluded.file_name,
                file_size=excluded.file_size,
                sha256=excluded.sha256,
                signature=excluded.signature,
                confirmed_at=excluded.confirmed_at,
                duration=excluded.duration,
                info_kind=excluded.info_kind,
                info_platform=excluded.info_platform,
                info_song_id=excluded.info_song_id,
                info_source_key=excluded.info_source_key,
                info_scope=excluded.info_scope,
                info_generation=excluded.info_generation,
                info_installation_id=excluded.info_installation_id,
                info_provider_name=excluded.info_provider_name,
                info_quality=excluded.info_quality,
                info_requested_quality=excluded.info_requested_quality,
                info_actual_quality=excluded.info_actual_quality,
                info_quality_verified=excluded.info_quality_verified,
                info_playback_verified=excluded.info_playback_verified,
                info_quality_policy_signature=excluded.info_quality_policy_signature,
                info_quality_cache_signature=excluded.info_quality_cache_signature,
                info_duration=excluded.info_duration,
                info_size=excluded.info_size,
                info_full=excluded.info_full,
                info_delivery=excluded.info_delivery,
                info_quality_evidence=excluded.info_quality_evidence,
                info_codec=excluded.info_codec,
                info_sample_rate=excluded.info_sample_rate,
                info_bit_depth=excluded.info_bit_depth,
                info_channels=excluded.info_channels,
                info_bitrate=excluded.info_bitrate
            """,
            ("$key", resourceKey),
            ("$file", value.FileName),
            ("$size", value.FileSize),
            ("$sha", value.Sha256),
            ("$sig", value.Signature),
            ("$conf", value.ConfirmedAt),
            ("$dur", value.Duration),
            ("$ikind", info.Kind),
            ("$iplat", info.Platform),
            ("$isong", info.SongId),
            ("$isrc", info.SourceKey),
            ("$iscope", info.Scope),
            ("$igen", info.Generation),
            ("$iinst", info.InstallationId),
            ("$iprov", info.ProviderName),
            ("$iqual", info.Quality),
            ("$ireq", info.RequestedQuality),
            ("$iact", info.ActualQuality),
            ("$iqv", info.QualityVerified),
            ("$ipv", info.PlaybackVerified),
            ("$iqps", info.QualityPolicySignature),
            ("$iqcs", info.QualityCacheSignature),
            ("$idur", info.Duration),
            ("$isize", info.Size),
            ("$ifull", info.Full),
            ("$idel", info.Delivery),
            ("$iqe", info.QualityEvidence),
            ("$icodec", info.Codec),
            ("$isr", info.SampleRate),
            ("$ibd", info.BitDepth),
            ("$ich", info.Channels),
            ("$ibr", info.Bitrate));
    }

    private static AudioFileCacheRecord ReadRow(SqliteDataReader row) => new(
        row.GetString(0),
        row.GetString(1),
        row.GetInt64(2),
        row.GetString(3),
        row.GetString(4),
        row.GetInt64(5),
        row.GetNullableDouble(6),
        new AudioFileInfoRecord(
            row.GetNullableString(7),
            row.GetNullableString(8),
            row.GetNullableString(9),
            row.GetNullableString(10),
            row.GetNullableString(11),
            row.GetNullableInt64(12),
            row.GetNullableString(13),
            row.GetNullableString(14),
            row.GetNullableString(15),
            row.GetNullableString(16),
            row.GetNullableString(17),
            row.GetNullableBool(18),
            row.GetNullableBool(19),
            row.GetNullableString(20),
            row.GetNullableString(21),
            row.GetNullableDouble(22),
            row.GetNullableInt64(23),
            row.GetNullableBool(24),
            row.GetNullableString(25),
            row.GetNullableString(26),
            row.GetNullableString(27),
            row.GetNullableInt64(28),
            row.GetNullableInt64(29),
            row.GetNullableInt64(30),
            row.GetNullableInt64(31)));
}

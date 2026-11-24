using System.Text;
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
    internal int EstimateBytes()
    {
        int bytes = 320;
        bytes += Utf8Len(Kind) + Utf8Len(Platform) + Utf8Len(SongId) + Utf8Len(SourceKey) + Utf8Len(Scope);
        bytes += Utf8Len(InstallationId) + Utf8Len(ProviderName) + Utf8Len(Quality) + Utf8Len(RequestedQuality);
        bytes += Utf8Len(ActualQuality) + Utf8Len(QualityPolicySignature) + Utf8Len(QualityCacheSignature);
        bytes += Utf8Len(Delivery) + Utf8Len(QualityEvidence) + Utf8Len(Codec);
        return bytes;
    }

    private static int Utf8Len(string? s) => s is null ? 0 : Encoding.UTF8.GetByteCount(s);
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
        resource_key,file_name,file_size,sha256,signature,confirmed_at_ms,duration,
        info_kind,info_platform,info_song_id,info_source_key,info_scope,info_generation,
        info_installation_id,info_provider_name,info_quality,info_requested_quality,info_actual_quality,
        info_quality_verified,info_playback_verified,info_quality_policy_signature,
        info_full,info_delivery,info_quality_evidence,
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

    internal static IReadOnlyList<(string Key, string FileName, long FileSize, long ConfirmedAtMs)> ReadSummaries(DatabaseSession db) =>
        db.Query("""
            SELECT resource_key,file_name,file_size,confirmed_at_ms
            FROM windows_music_audio_files
            ORDER BY confirmed_at_ms ASC, resource_key ASC
            """,
            row => (row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetInt64(3)));

    internal static IReadOnlyList<(string FileName, long FileSize)> ReadOfficialFiles(DatabaseSession db) =>
        db.Query("""
            SELECT file_name,file_size
            FROM windows_music_audio_files
            WHERE info_kind='official'
            """,
            row => (row.GetString(0), row.GetInt64(1)));

    internal static void DeleteOfficial(DatabaseSession db) =>
        db.Execute("DELETE FROM windows_music_audio_files WHERE info_kind='official'");

    internal static (int Count, long TotalBytes) ReadStats(DatabaseSession db) =>
        db.Query("SELECT count(*), coalesce(sum(file_size), 0) FROM windows_music_audio_files",
            row => (row.GetInt32(0), row.GetInt64(1)))[0];

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
                resource_key,file_name,file_size,sha256,signature,confirmed_at_ms,duration,
                info_kind,info_platform,info_song_id,info_source_key,info_scope,info_generation,
                info_installation_id,info_provider_name,info_quality,info_requested_quality,info_actual_quality,
                info_quality_verified,info_playback_verified,info_quality_policy_signature,
                info_full,info_delivery,info_quality_evidence,
                info_codec,info_sample_rate,info_bit_depth,info_channels,info_bitrate)
            VALUES(
                $key,$file,$size,$sha,$sig,$conf,$dur,
                $ikind,$iplat,$isong,$isrc,$iscope,$igen,
                $iinst,$iprov,$iqual,$ireq,$iact,
                $iqv,$ipv,$iqps,
                $ifull,$idel,$iqe,
                $icodec,$isr,$ibd,$ich,$ibr)
            ON CONFLICT(resource_key) DO UPDATE SET
                file_name=excluded.file_name,
                file_size=excluded.file_size,
                sha256=excluded.sha256,
                signature=excluded.signature,
                confirmed_at_ms=excluded.confirmed_at_ms,
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
            ("$ifull", info.Full),
            ("$idel", info.Delivery),
            ("$iqe", info.QualityEvidence),
            ("$icodec", info.Codec),
            ("$isr", info.SampleRate),
            ("$ibd", info.BitDepth),
            ("$ich", info.Channels),
            ("$ibr", info.Bitrate));
    }

    private static AudioFileCacheRecord ReadRow(SqliteDataReader row)
    {
        long fileSize = row.GetInt64(2);
        string signature = row.GetString(4);
        double? duration = row.GetNullableDouble(6);
        return new AudioFileCacheRecord(
            row.GetString(0),
            row.GetString(1),
            fileSize,
            row.GetString(3),
            signature,
            row.GetInt64(5),
            duration,
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
                signature,
                duration,
                fileSize,
                row.GetNullableBool(21),
                row.GetNullableString(22),
                row.GetNullableString(23),
                row.GetNullableString(24),
                row.GetNullableInt64(25),
                row.GetNullableInt64(26),
                row.GetNullableInt64(27),
                row.GetNullableInt64(28)));
    }
}

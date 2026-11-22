using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

internal static class MusicPlayContextStore
{
    internal const string SelectColumns =
        "c.collection_id,c.playlist_id,c.source_title,c.playlist,c.source_type," +
        "c.source_id,c.source_platform,c.platform,c.source_label,c.search_keyword," +
        "c.daily_branch,c.heartbeat_playlist_id,c.heartbeat_account_id,c.provider," +
        "c.remote_session,c.queue_source_id,c.queue_source_title,c.queue_source_kind," +
        "c.queue_source_platform,c.similar_batch_id,c.similar_based_on_song_id,c.similar_provider";

    internal static string? ComputeId(MusicPlayContextRecord context)
    {
        if (context.IsEmpty) return null;
        var sb = new StringBuilder(256);
        sb.Append("ctx.v1");
        AppendField(sb, context.CollectionId);
        AppendField(sb, context.PlaylistId);
        AppendField(sb, context.SourceTitle);
        AppendField(sb, context.Playlist);
        AppendField(sb, context.SourceType);
        AppendField(sb, context.SourceId);
        AppendField(sb, context.SourcePlatform);
        AppendField(sb, context.Platform);
        AppendField(sb, context.SourceLabel);
        AppendField(sb, context.SearchKeyword);
        AppendField(sb, context.DailyBranch);
        AppendField(sb, context.HeartbeatPlaylistId);
        AppendField(sb, context.HeartbeatAccountId);
        AppendField(sb, context.Provider);
        AppendField(sb, context.RemoteSession);
        AppendField(sb, context.QueueSourceId);
        AppendField(sb, context.QueueSourceTitle);
        AppendField(sb, context.QueueSourceKind);
        AppendField(sb, context.QueueSourcePlatform);
        AppendField(sb, context.SimilarBatchId);
        AppendField(sb, context.SimilarBasedOnSongId);
        AppendField(sb, context.SimilarProvider);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    internal static string? Ensure(DatabaseSession db, MusicPlayContextRecord context)
    {
        string? id = ComputeId(context);
        if (id is null) return null;
        db.Execute("""
            INSERT OR IGNORE INTO music_play_contexts(
                context_id,collection_id,playlist_id,source_title,playlist,source_type,
                source_id,source_platform,platform,source_label,search_keyword,
                daily_branch,heartbeat_playlist_id,heartbeat_account_id,provider,
                remote_session,queue_source_id,queue_source_title,queue_source_kind,
                queue_source_platform,similar_batch_id,similar_based_on_song_id,similar_provider)
            VALUES(
                $id,$ccid,$cplid,$cstitle,$cpl,$cstype,
                $csid,$csplat,$cplat,$cslbl,$csq,
                $cdb,$chpid,$chaid,$cprov,
                $crs,$cqsid,$cqst,$cqsk,
                $cqsp,$csbid,$csbos,$csprov)
            """,
            ("$id", id),
            ("$ccid", context.CollectionId),
            ("$cplid", context.PlaylistId),
            ("$cstitle", context.SourceTitle),
            ("$cpl", context.Playlist),
            ("$cstype", context.SourceType),
            ("$csid", context.SourceId),
            ("$csplat", context.SourcePlatform),
            ("$cplat", context.Platform),
            ("$cslbl", context.SourceLabel),
            ("$csq", context.SearchKeyword),
            ("$cdb", context.DailyBranch),
            ("$chpid", context.HeartbeatPlaylistId),
            ("$chaid", context.HeartbeatAccountId),
            ("$cprov", context.Provider),
            ("$crs", context.RemoteSession),
            ("$cqsid", context.QueueSourceId),
            ("$cqst", context.QueueSourceTitle),
            ("$cqsk", context.QueueSourceKind),
            ("$cqsp", context.QueueSourcePlatform),
            ("$csbid", context.SimilarBatchId),
            ("$csbos", context.SimilarBasedOnSongId),
            ("$csprov", context.SimilarProvider));
        return id;
    }

    internal static void PruneOrphans(DatabaseSession db)
    {
        db.Execute("""
            DELETE FROM music_play_contexts
            WHERE NOT EXISTS (SELECT 1 FROM music_queue_entries WHERE context_id = music_play_contexts.context_id)
              AND NOT EXISTS (SELECT 1 FROM music_history_playlists WHERE context_id = music_play_contexts.context_id)
              AND NOT EXISTS (SELECT 1 FROM music_history_playlist_entries WHERE context_id = music_play_contexts.context_id)
              AND NOT EXISTS (SELECT 1 FROM music_history_day_songs WHERE context_id = music_play_contexts.context_id)
            """);
    }

    private static void AppendField(StringBuilder sb, string? value)
    {
        if (value is null) sb.Append("|-");
        else sb.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }
}

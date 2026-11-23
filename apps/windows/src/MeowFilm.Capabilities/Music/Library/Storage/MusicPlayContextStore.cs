using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

internal static class MusicPlayContextStore
{
    internal const string SelectColumns =
        "c.source_title,c.playlist,c.source_type,c.source_id,c.search_keyword,c.daily_branch," +
        "ce.playlist_id,ce.source_platform,ce.heartbeat_playlist_id,ce.heartbeat_account_id";

    internal const string JoinClause =
        "LEFT JOIN music_play_contexts c ON c.context_id = e.context_id " +
        "LEFT JOIN windows_music_play_context_extras ce ON ce.context_id = c.context_id";

    internal const string DaySongJoinClause =
        "LEFT JOIN music_play_contexts c ON c.context_id = s.context_id " +
        "LEFT JOIN windows_music_play_context_extras ce ON ce.context_id = c.context_id";

    internal static string? ComputeId(MusicPlayContextRecord context)
    {
        if (context.IsEmpty) return null;
        var sb = new StringBuilder(160);
        sb.Append("ctx.v1");
        AppendField(sb, context.SourceTitle);
        AppendField(sb, context.Playlist);
        AppendField(sb, context.SourceType);
        AppendField(sb, context.SourceId);
        AppendField(sb, context.SearchKeyword);
        AppendField(sb, context.DailyBranch);
        AppendField(sb, context.PlaylistId);
        AppendField(sb, context.SourcePlatform);
        AppendField(sb, context.HeartbeatPlaylistId);
        AppendField(sb, context.HeartbeatAccountId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    internal static string? Ensure(DatabaseSession db, MusicPlayContextRecord context)
    {
        string? id = ComputeId(context);
        if (id is null) return null;
        db.Execute("""
            INSERT OR IGNORE INTO music_play_contexts(
                context_id,source_title,playlist,source_type,source_id,search_keyword,daily_branch)
            VALUES($id,$cstitle,$cpl,$cstype,$csid,$csq,$cdb)
            """,
            ("$id", id),
            ("$cstitle", context.SourceTitle),
            ("$cpl", context.Playlist),
            ("$cstype", context.SourceType),
            ("$csid", context.SourceId),
            ("$csq", context.SearchKeyword),
            ("$cdb", context.DailyBranch));
        if (context.HasWindowsExtras)
        {
            db.Execute("""
                INSERT OR IGNORE INTO windows_music_play_context_extras(
                    context_id,playlist_id,source_platform,heartbeat_playlist_id,heartbeat_account_id)
                VALUES($id,$cplid,$csplat,$chpid,$chaid)
                """,
                ("$id", id),
                ("$cplid", context.PlaylistId),
                ("$csplat", context.SourcePlatform),
                ("$chpid", context.HeartbeatPlaylistId),
                ("$chaid", context.HeartbeatAccountId));
        }
        return id;
    }

    internal static void PruneOrphans(DatabaseSession db)
    {
        db.Execute("""
            DELETE FROM music_play_contexts
            WHERE NOT EXISTS (SELECT 1 FROM music_queue_entries WHERE context_id = music_play_contexts.context_id)
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

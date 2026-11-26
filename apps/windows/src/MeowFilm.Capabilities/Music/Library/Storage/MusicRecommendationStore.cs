using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

internal static class MusicRecommendationStore
{
    private static void EnsureQueueStateRow(DatabaseSession db, string scope)
    {
        db.Execute("""
            INSERT OR IGNORE INTO music_queue_state(
                scope,current_id,serial,revision,source_id,source_title,source_platform)
            VALUES($scope,NULL,0,0,NULL,NULL,NULL)
            """, ("$scope", scope));
    }

    internal static IReadOnlyList<MusicRecommendationCheckpointRecord> ReadCheckpoints(DatabaseSession db, string scope)
    {
        var headers = db.Query("""
            SELECT checkpoint_id,current_id,serial,revision,source_id,source_title,source_kind,source_platform
            FROM windows_music_recommendation_checkpoints
            WHERE scope=$scope
            ORDER BY checkpoint_id
            """,
            r => (
                CheckpointId: r.GetString(0),
                CurrentId: r.GetNullableString(1),
                Serial: r.GetInt64(2),
                Revision: r.GetInt64(3),
                Source: MusicQueueSourceRecord.ReadFrom(r, 4, 5, 6, 7)),
            ("$scope", scope));

        if (headers.Count == 0) return [];

        var entriesByCp = new Dictionary<string, List<MusicQueueEntryRecord>>(headers.Count, StringComparer.Ordinal);
        foreach (var item in db.Query($"""
            SELECT e.checkpoint_id,e.entry_id,e.song_id,{MusicPlayContextStore.SelectColumns}
            FROM windows_music_recommendation_checkpoint_entries e
            {MusicPlayContextStore.JoinClause}
            WHERE e.scope=$scope
            ORDER BY e.checkpoint_id, e.position
            """,
            r => (
                CheckpointId: r.GetString(0),
                Entry: new MusicQueueEntryRecord(
                    r.GetString(1),
                    r.GetString(2),
                    MusicPlayContextRecord.ReadFrom(r, 3))),
            ("$scope", scope)))
        {
            if (!entriesByCp.TryGetValue(item.CheckpointId, out var list))
                entriesByCp[item.CheckpointId] = list = [];
            list.Add(item.Entry);
        }

        var listsByCp = new Dictionary<(string CheckpointId, string ListKind), List<string>>();
        foreach (var item in db.Query("""
            SELECT checkpoint_id,list_kind,item_value
            FROM windows_music_recommendation_checkpoint_lists
            WHERE scope=$scope
            ORDER BY checkpoint_id, list_kind, position
            """,
            r => (CheckpointId: r.GetString(0), ListKind: r.GetString(1), Value: r.GetString(2)),
            ("$scope", scope)))
        {
            var key = (item.CheckpointId, item.ListKind);
            if (!listsByCp.TryGetValue(key, out var list))
                listsByCp[key] = list = [];
            list.Add(item.Value);
        }

        var result = new List<MusicRecommendationCheckpointRecord>(headers.Count);
        foreach (var h in headers)
        {
            var queue = new MusicQueueStateRecord(
                h.CurrentId,
                h.Serial,
                h.Revision,
                h.Source,
                entriesByCp.TryGetValue(h.CheckpointId, out var entries) ? entries.ToArray() : [],
                listsByCp.TryGetValue((h.CheckpointId, "priority"), out var prio) ? prio.ToArray() : []);
            result.Add(new MusicRecommendationCheckpointRecord(
                h.CheckpointId,
                queue,
                listsByCp.TryGetValue((h.CheckpointId, "history"), out var hist) ? hist.ToArray() : [],
                listsByCp.TryGetValue((h.CheckpointId, "failed"), out var fail) ? fail.ToArray() : []));
        }
        return result;
    }

    internal static bool ApplyCheckpointMutation(DatabaseSession db, string scope, MusicRecommendationCheckpointMutation mutation)
    {
        if (mutation.Next is null)
        {
            int removed = db.Execute("""
                DELETE FROM windows_music_recommendation_checkpoints
                WHERE scope=$scope AND checkpoint_id=$cpid
                """, ("$scope", scope), ("$cpid", mutation.CheckpointId));
            return removed > 0;
        }

        EnsureQueueStateRow(db, scope);
        var cp = mutation.Next;
        var q = cp.Queue;
        var src = q.Source ?? MusicQueueSourceRecord.Empty;

        db.Execute("""
            DELETE FROM windows_music_recommendation_checkpoints
            WHERE scope=$scope AND checkpoint_id=$cpid
            """, ("$scope", scope), ("$cpid", cp.CheckpointId));

        db.Execute("""
            INSERT INTO windows_music_recommendation_checkpoints(
                scope,checkpoint_id,current_id,serial,revision,
                source_id,source_title,source_platform,source_kind)
            VALUES($scope,$cpid,$cid,$ser,$rev,$sid,$stitle,$splat,$skind)
            """,
            ("$scope", scope),
            ("$cpid", cp.CheckpointId),
            ("$cid", q.CurrentId),
            ("$ser", q.Serial),
            ("$rev", q.Revision),
            ("$sid", src.Id),
            ("$stitle", src.Title),
            ("$splat", src.Platform),
            ("$skind", src.Kind));

        for (int i = 0; i < q.Entries.Length; i++)
        {
            var entry = q.Entries[i];
            string? ctxId = MusicPlayContextStore.Ensure(db, entry.Context);
            db.Execute("""
                INSERT INTO windows_music_recommendation_checkpoint_entries(
                    scope,checkpoint_id,entry_id,position,song_id,context_id)
                VALUES($scope,$cpid,$eid,$pos,$sid,$ctx)
                """,
                ("$scope", scope),
                ("$cpid", cp.CheckpointId),
                ("$eid", entry.EntryId),
                ("$pos", i),
                ("$sid", entry.SongId),
                ("$ctx", ctxId));
        }

        WriteCheckpointList(db, scope, cp.CheckpointId, "priority", q.Priority);
        WriteCheckpointList(db, scope, cp.CheckpointId, "history", cp.History);
        WriteCheckpointList(db, scope, cp.CheckpointId, "failed", cp.Failed);
        return true;
    }

    private static void WriteCheckpointList(DatabaseSession db, string scope, string checkpointId, string listKind, string[] items)
    {
        for (int i = 0; i < items.Length; i++)
        {
            db.Execute("""
                INSERT INTO windows_music_recommendation_checkpoint_lists(
                    scope,checkpoint_id,list_kind,position,item_value)
                VALUES($scope,$cpid,$kind,$pos,$val)
                """,
                ("$scope", scope),
                ("$cpid", checkpointId),
                ("$kind", listKind),
                ("$pos", i),
                ("$val", items[i]));
        }
    }

    internal static MusicRecommendationSessionRecord? ReadSession(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT session_id,kind,platform,source_type,seed,fixed_seed,
                   restore,continue_enabled,exhausted,offset_val,batch,recommendation_started,checkpoint_id,
                   heartbeat_entry,heartbeat_playlist_id,heartbeat_start_music_id,heartbeat_song_id,
                   heartbeat_from_home,heartbeat_count,heartbeat_account_id
            FROM windows_music_recommendation_sessions
            WHERE scope=$scope
            """,
            r => new MusicRecommendationSessionRecord(
                SessionId: r.GetString(0),
                Kind: r.GetString(1),
                Platform: r.GetString(2),
                SourceType: r.GetString(3),
                Seed: r.GetString(4),
                FixedSeed: r.GetString(5),
                Restore: r.GetBool(6),
                ContinueEnabled: r.GetBool(7),
                Exhausted: r.GetBool(8),
                OffsetVal: r.GetInt32(9),
                Batch: r.GetInt32(10),
                RecommendationStarted: r.GetBool(11),
                CheckpointId: r.GetString(12),
                HeartbeatEntry: r.GetNullableString(13),
                HeartbeatPlaylistId: r.GetNullableString(14),
                HeartbeatStartMusicId: r.GetNullableString(15),
                HeartbeatSongId: r.GetNullableString(16),
                HeartbeatFromHome: r.GetNullableBool(17),
                HeartbeatCount: r.GetNullableInt64(18),
                HeartbeatAccountId: r.GetNullableString(19),
                SourceIds: [],
                Attempted: []),
            out var header,
            ("$scope", scope)))
            return null;

        var sourceIds = new List<string>();
        var attempted = new List<string>();
        foreach (var (kind, value) in db.Query("""
            SELECT list_kind,item_value
            FROM windows_music_recommendation_session_lists
            WHERE scope=$scope
            ORDER BY list_kind, position
            """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
        {
            if (kind == "source_ids") sourceIds.Add(value);
            else if (kind == "attempted") attempted.Add(value);
        }

        return header with
        {
            SourceIds = sourceIds.ToArray(),
            Attempted = attempted.ToArray()
        };
    }

    internal static void WriteSession(DatabaseSession db, string scope, MusicRecommendationSessionRecord? session)
    {
        if (session is null)
        {
            db.Execute("DELETE FROM windows_music_recommendation_sessions WHERE scope=$scope", ("$scope", scope));
            return;
        }

        EnsureQueueStateRow(db, scope);
        db.Execute("""
            INSERT INTO windows_music_recommendation_sessions(
                scope,session_id,kind,platform,source_type,seed,fixed_seed,
                restore,continue_enabled,exhausted,offset_val,batch,recommendation_started,checkpoint_id,
                heartbeat_entry,heartbeat_playlist_id,heartbeat_start_music_id,heartbeat_song_id,
                heartbeat_from_home,heartbeat_count,heartbeat_account_id)
            VALUES(
                $scope,$sid,$kind,$plat,$stype,$seed,$fseed,
                $rest,$cont,$exh,$off,$batch,$recst,$cpid,
                $hentry,$hpl,$hstart,$hsong,$hhome,$hcnt,$hacc)
            ON CONFLICT(scope) DO UPDATE SET
                session_id=excluded.session_id,
                kind=excluded.kind,
                platform=excluded.platform,
                source_type=excluded.source_type,
                seed=excluded.seed,
                fixed_seed=excluded.fixed_seed,
                restore=excluded.restore,
                continue_enabled=excluded.continue_enabled,
                exhausted=excluded.exhausted,
                offset_val=excluded.offset_val,
                batch=excluded.batch,
                recommendation_started=excluded.recommendation_started,
                checkpoint_id=excluded.checkpoint_id,
                heartbeat_entry=excluded.heartbeat_entry,
                heartbeat_playlist_id=excluded.heartbeat_playlist_id,
                heartbeat_start_music_id=excluded.heartbeat_start_music_id,
                heartbeat_song_id=excluded.heartbeat_song_id,
                heartbeat_from_home=excluded.heartbeat_from_home,
                heartbeat_count=excluded.heartbeat_count,
                heartbeat_account_id=excluded.heartbeat_account_id
            """,
            ("$scope", scope),
            ("$sid", session.SessionId),
            ("$kind", session.Kind),
            ("$plat", session.Platform),
            ("$stype", session.SourceType),
            ("$seed", session.Seed),
            ("$fseed", session.FixedSeed),
            ("$rest", session.Restore),
            ("$cont", session.ContinueEnabled),
            ("$exh", session.Exhausted),
            ("$off", session.OffsetVal),
            ("$batch", Math.Max(0, session.Batch)),
            ("$recst", session.RecommendationStarted),
            ("$cpid", session.CheckpointId),
            ("$hentry", session.HeartbeatEntry),
            ("$hpl", session.HeartbeatPlaylistId),
            ("$hstart", session.HeartbeatStartMusicId),
            ("$hsong", session.HeartbeatSongId),
            ("$hhome", session.HeartbeatFromHome),
            ("$hcnt", session.HeartbeatCount),
            ("$hacc", session.HeartbeatAccountId));

        db.Execute("DELETE FROM windows_music_recommendation_session_lists WHERE scope=$scope", ("$scope", scope));
        for (int i = 0; i < session.SourceIds.Length; i++)
        {
            db.Execute("""
                INSERT INTO windows_music_recommendation_session_lists(scope,list_kind,position,item_value)
                VALUES($scope,'source_ids',$pos,$val)
                """, ("$scope", scope), ("$pos", i), ("$val", session.SourceIds[i]));
        }
        for (int i = 0; i < session.Attempted.Length; i++)
        {
            db.Execute("""
                INSERT INTO windows_music_recommendation_session_lists(scope,list_kind,position,item_value)
                VALUES($scope,'attempted',$pos,$val)
                """, ("$scope", scope), ("$pos", i), ("$val", session.Attempted[i]));
        }
    }
}

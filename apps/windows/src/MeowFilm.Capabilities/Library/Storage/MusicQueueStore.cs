using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicQueueStore
{
    internal static MusicQueueStateRecord? ReadRecord(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT current_id,serial,revision,run_id,
                   source_id,source_title,source_kind,source_platform,source_type,
                   source_source_title,source_source_id,source_playlist,source_seed_entry_id,
                   source_remote_session,source_daily_branch,source_search_keyword
            FROM music_queue_state
            WHERE scope=$scope
            """,
            r => (
                CurrentId: r.GetNullableString(0),
                Serial: r.GetInt64(1),
                Revision: r.GetInt64(2),
                RunId: r.GetNullableString(3),
                Source: MusicQueueSourceRecord.ReadFrom(r, 4)),
            out var state,
            ("$scope", scope)))
            return null;

        var entries = db.Query($"""
            SELECT e.entry_id,e.song_id,{MusicPlayContextStore.SelectColumns}
            FROM music_queue_entries e
            LEFT JOIN music_play_contexts c ON c.context_id = e.context_id
            WHERE e.scope=$scope
            ORDER BY e.position
            """,
            r => new MusicQueueEntryRecord(
                r.GetString(0),
                r.GetString(1),
                MusicPlayContextRecord.ReadFrom(r, 2)),
            ("$scope", scope)).ToArray();

        var priority = db.Query("""
            SELECT item_value FROM music_queue_list_items
            WHERE scope=$scope AND list_kind='priority'
            ORDER BY position
            """, r => r.GetString(0), ("$scope", scope)).ToArray();

        return new MusicQueueStateRecord(
            state.CurrentId, state.Serial, state.Revision, state.RunId,
            state.Source, entries, priority);
    }

    internal static bool Save(DatabaseSession db, string scope, MusicQueueStateRecord? previous, MusicQueueStateRecord next)
    {
        if (previous is null || !next.HeaderEquals(previous))
        {
            var p = new List<(string, object?)>
            {
                ("$scope", scope),
                ("$cid", next.CurrentId),
                ("$ser", next.Serial),
                ("$rev", next.Revision),
                ("$run", next.RunId)
            };
            p.AddRange((next.Source ?? MusicQueueSourceRecord.Empty).ToParams("s_"));

            db.Execute("""
                INSERT INTO music_queue_state(
                    scope,current_id,serial,revision,run_id,
                    source_id,source_title,source_kind,source_platform,source_type,
                    source_source_title,source_source_id,source_playlist,source_seed_entry_id,
                    source_remote_session,source_daily_branch,source_search_keyword)
                VALUES(
                    $scope,$cid,$ser,$rev,$run,
                    $s_id,$s_title,$s_kind,$s_plat,$s_stype,
                    $s_stitle,$s_sid,$s_pl,$s_seed,
                    $s_rs,$s_db,$s_sq)
                ON CONFLICT(scope) DO UPDATE SET
                    current_id=excluded.current_id,
                    serial=excluded.serial,
                    revision=excluded.revision,
                    run_id=excluded.run_id,
                    source_id=excluded.source_id,
                    source_title=excluded.source_title,
                    source_kind=excluded.source_kind,
                    source_platform=excluded.source_platform,
                    source_type=excluded.source_type,
                    source_source_title=excluded.source_source_title,
                    source_source_id=excluded.source_source_id,
                    source_playlist=excluded.source_playlist,
                    source_seed_entry_id=excluded.source_seed_entry_id,
                    source_remote_session=excluded.source_remote_session,
                    source_daily_branch=excluded.source_daily_branch,
                    source_search_keyword=excluded.source_search_keyword
                """, p.ToArray());
        }

        bool contextsTouched = DiffEntries(db, scope, previous?.Entries, next.Entries);

        if (previous is null || !next.Priority.SequenceEqual(previous.Priority, StringComparer.Ordinal))
        {
            if (previous is null)
            {
                db.Execute("DELETE FROM music_queue_list_items WHERE scope=$scope AND list_kind='priority'", ("$scope", scope));
                for (int i = 0; i < next.Priority.Length; i++)
                    db.Execute("""
                        INSERT INTO music_queue_list_items(scope,list_kind,position,item_value)
                        VALUES($scope,'priority',$pos,$val)
                        """, ("$scope", scope), ("$pos", i), ("$val", next.Priority[i]));
            }
            else
            {
                int prefix = CommonPrefixLength(previous.Priority, next.Priority, StringComparer.Ordinal);
                if (prefix < previous.Priority.Length)
                    db.Execute("""
                        DELETE FROM music_queue_list_items
                        WHERE scope=$scope AND list_kind='priority' AND position>=$pos
                        """, ("$scope", scope), ("$pos", prefix));
                for (int i = prefix; i < next.Priority.Length; i++)
                    db.Execute("""
                        INSERT INTO music_queue_list_items(scope,list_kind,position,item_value)
                        VALUES($scope,'priority',$pos,$val)
                        """, ("$scope", scope), ("$pos", i), ("$val", next.Priority[i]));
            }
        }
        return contextsTouched;
    }

    private static bool DiffEntries(DatabaseSession db, string scope,
        MusicQueueEntryRecord[]? previous, MusicQueueEntryRecord[] next)
    {
        bool contextsRemovedOrReplaced = false;
        var prevById = new Dictionary<string, (int Position, MusicQueueEntryRecord Entry)>(StringComparer.Ordinal);
        if (previous is null)
        {
            db.Execute("DELETE FROM music_queue_entries WHERE scope=$scope", ("$scope", scope));
            contextsRemovedOrReplaced = true;
        }
        else
        {
            for (int i = 0; i < previous.Length; i++)
                prevById[previous[i].EntryId] = (i, previous[i]);
            var nextIds = new HashSet<string>(next.Select(e => e.EntryId), StringComparer.Ordinal);
            foreach (var old in previous)
            {
                if (!nextIds.Contains(old.EntryId))
                {
                    db.Execute("DELETE FROM music_queue_entries WHERE scope=$scope AND entry_id=$eid",
                        ("$scope", scope), ("$eid", old.EntryId));
                    if (!old.Context.IsEmpty) contextsRemovedOrReplaced = true;
                }
            }
        }

        for (int i = 0; i < next.Length; i++)
        {
            var e = next[i];
            if (prevById.TryGetValue(e.EntryId, out var oldEntry))
            {
                if (oldEntry.Position == i && oldEntry.Entry == e) continue;
                if (oldEntry.Entry == e)
                {
                    db.Execute("UPDATE music_queue_entries SET position=$pos WHERE scope=$scope AND entry_id=$eid",
                        ("$pos", i), ("$scope", scope), ("$eid", e.EntryId));
                    continue;
                }
                if (oldEntry.Entry.Context != e.Context && !oldEntry.Entry.Context.IsEmpty)
                    contextsRemovedOrReplaced = true;
            }

            string? ctxId = MusicPlayContextStore.Ensure(db, e.Context);
            db.Execute("""
                INSERT INTO music_queue_entries(scope,entry_id,position,song_id,context_id)
                VALUES($scope,$eid,$pos,$sid,$ctx)
                ON CONFLICT(scope,entry_id) DO UPDATE SET
                    position=excluded.position,
                    song_id=excluded.song_id,
                    context_id=excluded.context_id
                """,
                ("$scope", scope), ("$eid", e.EntryId), ("$pos", i), ("$sid", e.SongId), ("$ctx", ctxId));
        }

        return contextsRemovedOrReplaced;
    }
}

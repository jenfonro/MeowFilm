using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicQueueStore
{
    internal static MusicQueueStateRecord? ReadRecord(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT q.current_id,q.serial,q.revision,
                   q.source_id,q.source_title,sk.source_kind,q.source_platform
            FROM music_queue_state q
            LEFT JOIN windows_music_queue_source_kind sk ON sk.scope=q.scope
            WHERE q.scope=$scope
            """,
            r => (
                CurrentId: r.GetNullableString(0),
                Serial: r.GetInt64(1),
                Revision: r.GetInt64(2),
                Source: MusicQueueSourceRecord.ReadFrom(r, 3, 4, 5, 6)),
            out var state,
            ("$scope", scope)))
            return null;

        var entries = db.Query($"""
            SELECT e.entry_id,e.song_id,{MusicPlayContextStore.SelectColumns}
            FROM music_queue_entries e
            {MusicPlayContextStore.JoinClause}
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
            state.CurrentId, state.Serial, state.Revision,
            state.Source, entries, priority);
    }

    internal static bool Save(DatabaseSession db, string scope, MusicQueueStateRecord? previous, MusicQueueStateRecord next)
    {
        if (previous is null || !next.HeaderEquals(previous))
        {
            var src = next.Source ?? MusicQueueSourceRecord.Empty;
            db.Execute("""
                INSERT INTO music_queue_state(
                    scope,current_id,serial,revision,
                    source_id,source_title,source_platform)
                VALUES($scope,$cid,$ser,$rev,$s_id,$s_title,$s_plat)
                ON CONFLICT(scope) DO UPDATE SET
                    current_id=excluded.current_id,
                    serial=excluded.serial,
                    revision=excluded.revision,
                    source_id=excluded.source_id,
                    source_title=excluded.source_title,
                    source_platform=excluded.source_platform
                """,
                ("$scope", scope),
                ("$cid", next.CurrentId),
                ("$ser", next.Serial),
                ("$rev", next.Revision),
                ("$s_id", src.Id),
                ("$s_title", src.Title),
                ("$s_plat", src.Platform));

            if (src.Kind is null)
            {
                db.Execute("DELETE FROM windows_music_queue_source_kind WHERE scope=$scope", ("$scope", scope));
            }
            else
            {
                db.Execute("""
                    INSERT INTO windows_music_queue_source_kind(scope,source_kind)
                    VALUES($scope,$kind)
                    ON CONFLICT(scope) DO UPDATE SET source_kind=excluded.source_kind
                    """, ("$scope", scope), ("$kind", src.Kind));
            }
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

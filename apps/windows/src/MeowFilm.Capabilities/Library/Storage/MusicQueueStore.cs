using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.LibraryStorageHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicQueueStore
{
    internal static JsonObject? Read(DatabaseSession db, string scope) =>
        ReadRecord(db, scope) is { } rec ? ToJson(rec) : null;

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

        var entries = db.Query("""
            SELECT entry_id,song_id,
                   ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                   ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                   ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                   ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                   ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider
            FROM music_queue_entries
            WHERE scope=$scope
            ORDER BY position
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

    internal static void SaveIncremental(DatabaseSession db, string scope, JsonObject value)
    {
        var previous = ReadRecord(db, scope);
        var next = FromJson(value);

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
            p.AddRange((next.Source ?? new MusicQueueSourceRecord(null, null, null, null, null, null, null, null, null, null, null, null)).ToParams("s_"));

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

        DiffEntries(db, scope, previous?.Entries, next.Entries);

        if (previous is null || !next.Priority.SequenceEqual(previous.Priority, StringComparer.Ordinal))
        {
            int prefix = previous is null ? 0 : CommonPrefixLength(previous.Priority, next.Priority, StringComparer.Ordinal);
            if (previous is not null && prefix < previous.Priority.Length)
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

    private static void DiffEntries(DatabaseSession db, string scope,
        MusicQueueEntryRecord[]? previous, MusicQueueEntryRecord[] next)
    {
        var prevById = new Dictionary<string, (int Position, MusicQueueEntryRecord Entry)>(StringComparer.Ordinal);
        if (previous is not null)
        {
            for (int i = 0; i < previous.Length; i++)
                prevById[previous[i].EntryId] = (i, previous[i]);
        }

        var nextIds = new HashSet<string>(next.Select(e => e.EntryId), StringComparer.Ordinal);
        if (previous is not null)
        {
            foreach (var old in previous)
                if (!nextIds.Contains(old.EntryId))
                    db.Execute("DELETE FROM music_queue_entries WHERE scope=$scope AND entry_id=$eid",
                        ("$scope", scope), ("$eid", old.EntryId));
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
            }

            var ep = new List<(string, object?)>
            {
                ("$scope", scope), ("$eid", e.EntryId), ("$pos", i), ("$sid", e.SongId)
            };
            ep.AddRange(e.Context.ToParams());
            db.Execute("""
                INSERT INTO music_queue_entries(
                    scope,entry_id,position,song_id,
                    ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                    ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                    ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                    ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                    ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider)
                VALUES(
                    $scope,$eid,$pos,$sid,
                    $ccid,$cplid,$cstitle,$cpl,$cstype,
                    $csid,$csplat,$cplat,$cslbl,$csq,
                    $cdb,$chpid,$chaid,$cprov,
                    $crs,$cqsid,$cqst,$cqsk,
                    $cqsp,$csbid,$csbos,$csprov)
                ON CONFLICT(scope,entry_id) DO UPDATE SET
                    position=excluded.position,
                    song_id=excluded.song_id,
                    ctx_collection_id=excluded.ctx_collection_id,
                    ctx_playlist_id=excluded.ctx_playlist_id,
                    ctx_source_title=excluded.ctx_source_title,
                    ctx_playlist=excluded.ctx_playlist,
                    ctx_source_type=excluded.ctx_source_type,
                    ctx_source_id=excluded.ctx_source_id,
                    ctx_source_platform=excluded.ctx_source_platform,
                    ctx_platform=excluded.ctx_platform,
                    ctx_source_label=excluded.ctx_source_label,
                    ctx_search_keyword=excluded.ctx_search_keyword,
                    ctx_daily_branch=excluded.ctx_daily_branch,
                    ctx_heartbeat_playlist_id=excluded.ctx_heartbeat_playlist_id,
                    ctx_heartbeat_account_id=excluded.ctx_heartbeat_account_id,
                    ctx_provider=excluded.ctx_provider,
                    ctx_remote_session=excluded.ctx_remote_session,
                    ctx_queue_source_id=excluded.ctx_queue_source_id,
                    ctx_queue_source_title=excluded.ctx_queue_source_title,
                    ctx_queue_source_kind=excluded.ctx_queue_source_kind,
                    ctx_queue_source_platform=excluded.ctx_queue_source_platform,
                    ctx_similar_batch_id=excluded.ctx_similar_batch_id,
                    ctx_similar_based_on_song_id=excluded.ctx_similar_based_on_song_id,
                    ctx_similar_provider=excluded.ctx_similar_provider
                """, ep.ToArray());
        }
    }

    private static JsonObject ToJson(MusicQueueStateRecord rec)
    {
        var entries = new JsonArray();
        foreach (var e in rec.Entries)
            entries.Add(new JsonObject
            {
                ["id"] = e.EntryId,
                ["songId"] = e.SongId,
                ["context"] = e.Context.ToJson()
            });
        var priority = new JsonArray();
        foreach (var p in rec.Priority) priority.Add(p);

        return new JsonObject
        {
            ["schema"] = 1,
            ["entries"] = entries,
            ["currentId"] = rec.CurrentId,
            ["priority"] = priority,
            ["source"] = rec.Source?.ToJson()
        };
    }

    private static MusicQueueStateRecord FromJson(JsonObject value)
    {
        var entries = new List<MusicQueueEntryRecord>();
        if (value["entries"] is JsonArray entriesArr)
        {
            foreach (var item in entriesArr)
                if (item is JsonObject e)
                    entries.Add(new MusicQueueEntryRecord(
                        Str(e, "id")!,
                        Str(e, "songId")!,
                        MusicPlayContextRecord.FromJson(e["context"] as JsonObject)));
        }
        var priority = new List<string>();
        if (value["priority"] is JsonArray priArr)
            foreach (var item in priArr)
                if (item is JsonValue v && v.TryGetValue<string>(out var s))
                    priority.Add(s);

        return new MusicQueueStateRecord(
            Str(value, "currentId"),
            Int64(value, "serial") ?? 0,
            Int64(value, "revision") ?? 0,
            Str(value, "runId"),
            MusicQueueSourceRecord.FromJson(value["source"] as JsonObject),
            entries.ToArray(),
            priority.ToArray());
    }
}

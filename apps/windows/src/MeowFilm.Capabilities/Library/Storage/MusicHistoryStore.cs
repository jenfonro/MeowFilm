using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.LibraryStorageHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicHistoryStore
{
    internal static bool Exists(DatabaseSession db, string scope) =>
        db.Scalar<long>("SELECT count(*) FROM music_history_playlists WHERE scope=$scope", ("$scope", scope)) > 0 ||
        db.Scalar<long>("SELECT count(*) FROM music_history_days WHERE scope=$scope", ("$scope", scope)) > 0;

    internal static JsonObject? ReadArea(DatabaseSession db, string scope, string[]? members)
    {
        if (!Exists(db, scope)) return null;
        if (members is not null && members.Length == 1 && members[0] == "schema")
            return new JsonObject { ["schema"] = 1 };

        var playlists = new JsonArray();
        foreach (var pid in db.Query("SELECT playlist_id FROM music_history_playlists WHERE scope=$scope ORDER BY position",
            r => r.GetString(0), ("$scope", scope)))
            if (ReadPlaylist(db, scope, pid) is { } pl) playlists.Add(pl);

        var days = new JsonArray();
        foreach (var date in db.Query("SELECT day_date FROM music_history_days WHERE scope=$scope ORDER BY position",
            r => r.GetString(0), ("$scope", scope)))
            if (ReadDay(db, scope, date) is { } d) days.Add(d);

        var full = new JsonObject
        {
            ["schema"] = 1,
            ["playlists"] = playlists,
            ["days"] = days
        };
        return SelectMembers(full, members);
    }

    internal static void SaveAreaIncremental(DatabaseSession db, string scope, JsonObject value)
    {
        var nextPlaylistIds = new HashSet<string>(StringComparer.Ordinal);
        var playlists = value["playlists"] as JsonArray;
        if (playlists is not null)
        {
            foreach (var item in playlists)
                if (item is JsonObject pl && Str(pl, "id") is { } pid)
                    nextPlaylistIds.Add(pid);
        }
        foreach (var oldPid in db.Query("SELECT playlist_id FROM music_history_playlists WHERE scope=$scope",
            r => r.GetString(0), ("$scope", scope)))
        {
            if (!nextPlaylistIds.Contains(oldPid))
                db.Execute("DELETE FROM music_history_playlists WHERE scope=$scope AND playlist_id=$pid",
                    ("$scope", scope), ("$pid", oldPid));
        }
        if (playlists is not null)
        {
            for (int i = 0; i < playlists.Count; i++)
                if (playlists[i] is JsonObject pl)
                    SavePlaylistIncremental(db, scope, Str(pl, "id")!, pl, i, insert: false);
        }

        var nextDayDates = new HashSet<string>(StringComparer.Ordinal);
        var days = value["days"] as JsonArray;
        if (days is not null)
        {
            foreach (var item in days)
                if (item is JsonObject d && Str(d, "date") is { } date)
                    nextDayDates.Add(date);
        }
        foreach (var oldDate in db.Query("SELECT day_date FROM music_history_days WHERE scope=$scope",
            r => r.GetString(0), ("$scope", scope)))
        {
            if (!nextDayDates.Contains(oldDate))
                db.Execute("DELETE FROM music_history_days WHERE scope=$scope AND day_date=$date",
                    ("$scope", scope), ("$date", oldDate));
        }
        if (days is not null)
        {
            for (int i = 0; i < days.Count; i++)
                if (days[i] is JsonObject d)
                    SaveDayIncremental(db, scope, Str(d, "date")!, d, i, insert: false);
        }
    }

    internal static bool HasRelation(DatabaseSession db, string[] relations, object?[] key, string relation) =>
        (relations, relation) switch
        {
            ([], "playlists" or "days") => Exists(db, (string)key[0]!),
            (["playlists"], "entries") => db.Scalar<long>(
                "SELECT count(*) FROM music_history_playlists WHERE scope=$scope AND playlist_id=$pid",
                ("$scope", key[0]), ("$pid", key[1])) > 0,
            (["days"], "songs") => db.Scalar<long>(
                "SELECT count(*) FROM music_history_days WHERE scope=$scope AND day_date=$date",
                ("$scope", key[0]), ("$date", key[1])) > 0,
            _ => false
        };

    internal static IReadOnlyList<MusicLibrarySelectedRow> ReadRows(
        DatabaseSession db, string[] relations, object?[] prefix, string[] members)
    {
        if (relations is ["playlists"])
        {
            string scope = (string)prefix[0]!;
            bool single = prefix.Length == 2;
            string sql = single
                ? """
                  SELECT playlist_id,position,played_at,title,current_id,
                         source_id,source_title,source_kind,source_platform,source_type,
                         source_source_title,source_source_id,source_playlist,source_seed_entry_id,
                         source_remote_session,source_daily_branch,source_search_keyword
                  FROM music_history_playlists WHERE scope=$scope AND playlist_id=$pid ORDER BY position
                  """
                : """
                  SELECT playlist_id,position,played_at,title,current_id,
                         source_id,source_title,source_kind,source_platform,source_type,
                         source_source_title,source_source_id,source_playlist,source_seed_entry_id,
                         source_remote_session,source_daily_branch,source_search_keyword
                  FROM music_history_playlists WHERE scope=$scope ORDER BY position
                  """;
            var parameters = single
                ? new (string, object?)[] { ("$scope", scope), ("$pid", prefix[1]) }
                : new (string, object?)[] { ("$scope", scope) };
            return db.Query(sql, r =>
            {
                string pid = r.GetString(0);
                var header = new JsonObject
                {
                    ["id"] = pid,
                    ["at"] = WholeOrDouble(r.GetDouble(2)),
                    ["title"] = r.GetString(3),
                    ["currentId"] = r.GetNullableString(4),
                    ["source"] = ReadQueueSource(r, 5)
                };
                return new MusicLibrarySelectedRow([scope, pid], r.GetInt32(1), SelectMembers(header, members)!);
            }, parameters);
        }

        if (relations is ["playlists", "entries"])
        {
            string scope = (string)prefix[0]!;
            string pid = (string)prefix[1]!;
            bool single = prefix.Length == 3;
            string sql = single
                ? "SELECT entry_id,position,song_id FROM music_history_playlist_entries WHERE scope=$scope AND playlist_id=$pid AND entry_id=$eid ORDER BY position"
                : "SELECT entry_id,position,song_id FROM music_history_playlist_entries WHERE scope=$scope AND playlist_id=$pid ORDER BY position";
            var parameters = single
                ? new (string, object?)[] { ("$scope", scope), ("$pid", pid), ("$eid", prefix[2]) }
                : new (string, object?)[] { ("$scope", scope), ("$pid", pid) };
            return db.Query(sql, r =>
            {
                string eid = r.GetString(0);
                var obj = new JsonObject { ["id"] = eid, ["songId"] = r.GetString(2) };
                return new MusicLibrarySelectedRow([scope, pid, eid], r.GetInt32(1), SelectMembers(obj, members)!);
            }, parameters);
        }

        if (relations is ["days"])
        {
            string scope = (string)prefix[0]!;
            bool single = prefix.Length == 2;
            string sql = single
                ? "SELECT day_date,position FROM music_history_days WHERE scope=$scope AND day_date=$date ORDER BY position"
                : "SELECT day_date,position FROM music_history_days WHERE scope=$scope ORDER BY position";
            var parameters = single
                ? new (string, object?)[] { ("$scope", scope), ("$date", prefix[1]) }
                : new (string, object?)[] { ("$scope", scope) };
            return db.Query(sql, r =>
            {
                string date = r.GetString(0);
                var obj = new JsonObject { ["date"] = date };
                return new MusicLibrarySelectedRow([scope, date], r.GetInt32(1), SelectMembers(obj, members)!);
            }, parameters);
        }

        if (relations is ["days", "songs"])
        {
            string scope = (string)prefix[0]!;
            string date = (string)prefix[1]!;
            bool single = prefix.Length == 3;
            string sql = single
                ? "SELECT song_id,position,played_at FROM music_history_day_songs WHERE scope=$scope AND day_date=$date AND song_id=$sid ORDER BY position"
                : "SELECT song_id,position,played_at FROM music_history_day_songs WHERE scope=$scope AND day_date=$date ORDER BY position";
            var parameters = single
                ? new (string, object?)[] { ("$scope", scope), ("$date", date), ("$sid", prefix[2]) }
                : new (string, object?)[] { ("$scope", scope), ("$date", date) };
            return db.Query(sql, r =>
            {
                string sid = r.GetString(0);
                var obj = new JsonObject { ["songId"] = sid, ["at"] = WholeOrDouble(r.GetDouble(2)) };
                return new MusicLibrarySelectedRow([scope, date, sid], r.GetInt32(1), SelectMembers(obj, members)!);
            }, parameters);
        }

        throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }

    internal static JsonObject? ReadRow(DatabaseSession db, string[] relations, object?[] key) =>
        relations switch
        {
            ["playlists"] => ReadPlaylist(db, (string)key[0]!, (string)key[1]!),
            ["playlists", "entries"] => ReadPlaylistEntry(db, (string)key[0]!, (string)key[1]!, (string)key[2]!),
            ["days"] => ReadDay(db, (string)key[0]!, (string)key[1]!),
            ["days", "songs"] => ReadDaySong(db, (string)key[0]!, (string)key[1]!, (string)key[2]!),
            _ => throw new DocumentStoreException(DocumentStoreError.InvalidRequest)
        };

    internal static void ApplyRowChange(DatabaseSession db, MusicLibraryRowChange row)
    {
        string scope = (string)row.Key[0]!;
        if (row.Relations is ["playlists"])
        {
            string pid = row.Value is not null ? Str(row.Value, "id") ?? (string)row.Key[1]! : (string)row.Key[1]!;
            if (row.Value is null)
                db.Execute("DELETE FROM music_history_playlists WHERE scope=$scope AND playlist_id=$pid",
                    ("$scope", scope), ("$pid", pid));
            else
                SavePlaylistIncremental(db, scope, pid, row.Value, row.Ordinal, row.Insert);
            return;
        }
        if (row.Relations is ["days"])
        {
            string date = row.Value is not null ? Str(row.Value, "date") ?? (string)row.Key[1]! : (string)row.Key[1]!;
            if (row.Value is null)
                db.Execute("DELETE FROM music_history_days WHERE scope=$scope AND day_date=$date",
                    ("$scope", scope), ("$date", date));
            else
                SaveDayIncremental(db, scope, date, row.Value, row.Ordinal, row.Insert);
            return;
        }
        throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }

    private static JsonObject? ReadPlaylist(DatabaseSession db, string scope, string playlistId)
    {
        if (!db.TryQuerySingle("""
            SELECT played_at,title,current_id,
                   source_id,source_title,source_kind,source_platform,source_type,
                   source_source_title,source_source_id,source_playlist,source_seed_entry_id,
                   source_remote_session,source_daily_branch,source_search_keyword
            FROM music_history_playlists
            WHERE scope=$scope AND playlist_id=$pid
            """,
            r => (At: r.GetDouble(0), Title: r.GetString(1), CurrentId: r.GetNullableString(2), Source: ReadQueueSource(r, 3)),
            out var header,
            ("$scope", scope), ("$pid", playlistId)))
            return null;

        var entries = new JsonArray();
        foreach (var e in db.Query("""
            SELECT entry_id,song_id,
                   ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                   ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                   ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                   ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                   ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider
            FROM music_history_playlist_entries
            WHERE scope=$scope AND playlist_id=$pid
            ORDER BY position
            """,
            r => new JsonObject
            {
                ["id"] = r.GetString(0),
                ["songId"] = r.GetString(1),
                ["context"] = ReadContext(r, 2)
            }, ("$scope", scope), ("$pid", playlistId)))
            entries.Add(e);

        var priority = new JsonArray();
        var history = new JsonArray();
        foreach (var (lk, val) in db.Query("""
            SELECT list_kind,item_value FROM music_history_playlist_lists
            WHERE scope=$scope AND playlist_id=$pid
            ORDER BY list_kind, position
            """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope), ("$pid", playlistId)))
        {
            if (lk == "priority") priority.Add(val);
            else history.Add(val);
        }

        return new JsonObject
        {
            ["id"] = playlistId,
            ["title"] = header.Title,
            ["at"] = WholeOrDouble(header.At),
            ["currentId"] = header.CurrentId,
            ["entries"] = entries,
            ["priority"] = priority,
            ["history"] = history,
            ["source"] = header.Source
        };
    }

    private static JsonObject? ReadPlaylistEntry(DatabaseSession db, string scope, string playlistId, string entryId) =>
        db.TryQuerySingle("""
            SELECT song_id,
                   ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                   ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                   ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                   ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                   ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider
            FROM music_history_playlist_entries
            WHERE scope=$scope AND playlist_id=$pid AND entry_id=$eid
            """,
            r => new JsonObject
            {
                ["id"] = entryId,
                ["songId"] = r.GetString(0),
                ["context"] = ReadContext(r, 1)
            },
            out var entry,
            ("$scope", scope), ("$pid", playlistId), ("$eid", entryId))
            ? entry
            : null;

    private static JsonObject? ReadDay(DatabaseSession db, string scope, string dayDate)
    {
        if (db.Scalar<long>("SELECT count(*) FROM music_history_days WHERE scope=$scope AND day_date=$date",
            ("$scope", scope), ("$date", dayDate)) == 0)
            return null;

        var songs = new JsonArray();
        foreach (var s in db.Query("""
            SELECT song_id,played_at,
                   ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                   ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                   ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                   ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                   ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider
            FROM music_history_day_songs
            WHERE scope=$scope AND day_date=$date
            ORDER BY position
            """,
            r => new JsonObject
            {
                ["songId"] = r.GetString(0),
                ["at"] = WholeOrDouble(r.GetDouble(1)),
                ["context"] = ReadContext(r, 2)
            }, ("$scope", scope), ("$date", dayDate)))
            songs.Add(s);

        return new JsonObject
        {
            ["date"] = dayDate,
            ["songs"] = songs
        };
    }

    private static JsonObject? ReadDaySong(DatabaseSession db, string scope, string dayDate, string songId) =>
        db.TryQuerySingle("""
            SELECT played_at,
                   ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                   ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                   ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                   ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                   ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider
            FROM music_history_day_songs
            WHERE scope=$scope AND day_date=$date AND song_id=$sid
            """,
            r => new JsonObject
            {
                ["songId"] = songId,
                ["at"] = WholeOrDouble(r.GetDouble(0)),
                ["context"] = ReadContext(r, 1)
            },
            out var item,
            ("$scope", scope), ("$date", dayDate), ("$sid", songId))
            ? item
            : null;

    private static void SavePlaylistIncremental(DatabaseSession db, string scope, string playlistId, JsonObject pl, int ordinal, bool insert)
    {
        bool existed = db.Scalar<long>(
            "SELECT count(*) FROM music_history_playlists WHERE scope=$scope AND playlist_id=$pid",
            ("$scope", scope), ("$pid", playlistId)) > 0;
        if (insert && !existed)
            db.Execute("UPDATE music_history_playlists SET position=position+1 WHERE scope=$scope AND position>=$ord",
                ("$scope", scope), ("$ord", ordinal));

        var p = new List<(string, object?)>
        {
            ("$scope", scope),
            ("$pid", playlistId),
            ("$pos", ordinal),
            ("$at", Dbl(pl, "at") ?? 0d),
            ("$title", Str(pl, "title") ?? ""),
            ("$cid", Str(pl, "currentId"))
        };
        p.AddRange(QueueSourceParams("s_", pl["source"] as JsonObject));
        p.AddRange(ContextParams(pl["context"] as JsonObject));

        db.Execute("""
            INSERT INTO music_history_playlists(
                scope,playlist_id,position,played_at,title,current_id,
                source_id,source_title,source_kind,source_platform,source_type,
                source_source_title,source_source_id,source_playlist,source_seed_entry_id,
                source_remote_session,source_daily_branch,source_search_keyword,
                ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider)
            VALUES(
                $scope,$pid,$pos,$at,$title,$cid,
                $s_id,$s_title,$s_kind,$s_plat,$s_stype,
                $s_stitle,$s_sid,$s_pl,$s_seed,
                $s_rs,$s_db,$s_sq,
                $ccid,$cplid,$cstitle,$cpl,$cstype,
                $csid,$csplat,$cplat,$cslbl,$csq,
                $cdb,$chpid,$chaid,$cprov,
                $crs,$cqsid,$cqst,$cqsk,
                $cqsp,$csbid,$csbos,$csprov)
            ON CONFLICT(scope,playlist_id) DO UPDATE SET
                position=excluded.position,
                played_at=excluded.played_at,
                title=excluded.title,
                current_id=excluded.current_id,
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
                source_search_keyword=excluded.source_search_keyword,
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
            """, p.ToArray());

        var prevEntries = new Dictionary<string, (int Position, MusicQueueEntryRecord Entry)>(StringComparer.Ordinal);
        if (existed)
        {
            foreach (var row in db.Query("""
                SELECT entry_id,position,song_id,
                       ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                       ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                       ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                       ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                       ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider
                FROM music_history_playlist_entries
                WHERE scope=$scope AND playlist_id=$pid
                """,
                r => (
                    Pos: r.GetInt32(1),
                    Entry: new MusicQueueEntryRecord(r.GetString(0), r.GetString(2), MusicPlayContextRecord.ReadFrom(r, 3))),
                ("$scope", scope), ("$pid", playlistId)))
                prevEntries[row.Entry.EntryId] = (row.Pos, row.Entry);
        }

        var nextEntries = new List<MusicQueueEntryRecord>();
        if (pl["entries"] is JsonArray entries)
        {
            foreach (var item in entries)
                if (item is JsonObject e)
                    nextEntries.Add(new MusicQueueEntryRecord(
                        Str(e, "id")!, Str(e, "songId")!, MusicPlayContextRecord.FromJson(e["context"] as JsonObject)));
        }

        var nextEntryIds = new HashSet<string>(nextEntries.Select(e => e.EntryId), StringComparer.Ordinal);
        foreach (var oldId in prevEntries.Keys)
            if (!nextEntryIds.Contains(oldId))
                db.Execute("DELETE FROM music_history_playlist_entries WHERE scope=$scope AND playlist_id=$pid AND entry_id=$eid",
                    ("$scope", scope), ("$pid", playlistId), ("$eid", oldId));

        for (int i = 0; i < nextEntries.Count; i++)
        {
            var e = nextEntries[i];
            if (prevEntries.TryGetValue(e.EntryId, out var oldEntry))
            {
                if (oldEntry.Position == i && oldEntry.Entry == e) continue;
                if (oldEntry.Entry == e)
                {
                    db.Execute("""
                        UPDATE music_history_playlist_entries SET position=$pos
                        WHERE scope=$scope AND playlist_id=$pid AND entry_id=$eid
                        """, ("$pos", i), ("$scope", scope), ("$pid", playlistId), ("$eid", e.EntryId));
                    continue;
                }
            }
            var ep = new List<(string, object?)>
            {
                ("$scope", scope), ("$pid", playlistId), ("$eid", e.EntryId), ("$pos", i), ("$sid", e.SongId)
            };
            ep.AddRange(e.Context.ToParams());
            db.Execute("""
                INSERT INTO music_history_playlist_entries(
                    scope,playlist_id,entry_id,position,song_id,
                    ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                    ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                    ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                    ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                    ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider)
                VALUES(
                    $scope,$pid,$eid,$pos,$sid,
                    $ccid,$cplid,$cstitle,$cpl,$cstype,
                    $csid,$csplat,$cplat,$cslbl,$csq,
                    $cdb,$chpid,$chaid,$cprov,
                    $crs,$cqsid,$cqst,$cqsk,
                    $cqsp,$csbid,$csbos,$csprov)
                ON CONFLICT(scope,playlist_id,entry_id) DO UPDATE SET
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

        foreach (var (lk, arr) in new[] { ("priority", pl["priority"] as JsonArray), ("history", pl["history"] as JsonArray) })
        {
            var prevList = existed
                ? db.Query("""
                    SELECT item_value FROM music_history_playlist_lists
                    WHERE scope=$scope AND playlist_id=$pid AND list_kind=$lk
                    ORDER BY position
                    """, r => r.GetString(0), ("$scope", scope), ("$pid", playlistId), ("$lk", lk))
                : [];
            var nextList = new List<string>();
            if (arr is not null)
                foreach (var item in arr)
                    if (item is JsonValue v && v.TryGetValue<string>(out var s))
                        nextList.Add(s);
            if (prevList.Count == nextList.Count && prevList.SequenceEqual(nextList, StringComparer.Ordinal))
                continue;
            int prefix = CommonPrefixLength(prevList, nextList, StringComparer.Ordinal);
            if (prefix < prevList.Count)
                db.Execute("""
                    DELETE FROM music_history_playlist_lists
                    WHERE scope=$scope AND playlist_id=$pid AND list_kind=$lk AND position>=$pos
                    """, ("$scope", scope), ("$pid", playlistId), ("$lk", lk), ("$pos", prefix));
            for (int i = prefix; i < nextList.Count; i++)
                db.Execute("""
                    INSERT INTO music_history_playlist_lists(scope,playlist_id,list_kind,position,item_value)
                    VALUES($scope,$pid,$lk,$pos,$val)
                    """, ("$scope", scope), ("$pid", playlistId), ("$lk", lk), ("$pos", i), ("$val", nextList[i]));
        }
    }

    private static void SaveDayIncremental(DatabaseSession db, string scope, string dayDate, JsonObject day, int ordinal, bool insert)
    {
        bool existed = db.Scalar<long>(
            "SELECT count(*) FROM music_history_days WHERE scope=$scope AND day_date=$date",
            ("$scope", scope), ("$date", dayDate)) > 0;
        if (insert && !existed)
            db.Execute("UPDATE music_history_days SET position=position+1 WHERE scope=$scope AND position>=$ord",
                ("$scope", scope), ("$ord", ordinal));

        db.Execute("""
            INSERT INTO music_history_days(scope,day_date,position,day_id,updated_at)
            VALUES($scope,$date,$pos,$did,$at)
            ON CONFLICT(scope,day_date) DO UPDATE SET
                position=excluded.position,
                day_id=excluded.day_id,
                updated_at=excluded.updated_at
            """,
            ("$scope", scope), ("$date", dayDate), ("$pos", ordinal),
            ("$did", Str(day, "id") ?? dayDate), ("$at", Dbl(day, "at") ?? 0d));

        var prevSongs = new Dictionary<string, (int Position, double At, MusicPlayContextRecord Ctx)>(StringComparer.Ordinal);
        if (existed)
        {
            foreach (var row in db.Query("""
                SELECT song_id,position,played_at,
                       ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                       ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                       ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                       ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                       ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider
                FROM music_history_day_songs
                WHERE scope=$scope AND day_date=$date
                """,
                r => (SongId: r.GetString(0), Pos: r.GetInt32(1), At: r.GetDouble(2), Ctx: MusicPlayContextRecord.ReadFrom(r, 3)),
                ("$scope", scope), ("$date", dayDate)))
                prevSongs[row.SongId] = (row.Pos, row.At, row.Ctx);
        }

        var nextSongs = new List<(string SongId, double At, MusicPlayContextRecord Ctx)>();
        if (day["songs"] is JsonArray songs)
        {
            foreach (var item in songs)
                if (item is JsonObject s)
                    nextSongs.Add((Str(s, "songId")!, Dbl(s, "at") ?? 0d, MusicPlayContextRecord.FromJson(s["context"] as JsonObject)));
        }

        var nextSongIds = new HashSet<string>(nextSongs.Select(s => s.SongId), StringComparer.Ordinal);
        foreach (var oldId in prevSongs.Keys)
            if (!nextSongIds.Contains(oldId))
                db.Execute("DELETE FROM music_history_day_songs WHERE scope=$scope AND day_date=$date AND song_id=$sid",
                    ("$scope", scope), ("$date", dayDate), ("$sid", oldId));

        for (int i = 0; i < nextSongs.Count; i++)
        {
            var s = nextSongs[i];
            if (prevSongs.TryGetValue(s.SongId, out var old))
            {
                bool sameContent = BitConverter.DoubleToInt64Bits(old.At) == BitConverter.DoubleToInt64Bits(s.At) && old.Ctx == s.Ctx;
                if (old.Position == i && sameContent) continue;
                if (sameContent)
                {
                    db.Execute("""
                        UPDATE music_history_day_songs SET position=$pos
                        WHERE scope=$scope AND day_date=$date AND song_id=$sid
                        """, ("$pos", i), ("$scope", scope), ("$date", dayDate), ("$sid", s.SongId));
                    continue;
                }
            }
            var sp = new List<(string, object?)>
            {
                ("$scope", scope), ("$date", dayDate), ("$sid", s.SongId), ("$pos", i), ("$at", s.At)
            };
            sp.AddRange(s.Ctx.ToParams());
            db.Execute("""
                INSERT INTO music_history_day_songs(
                    scope,day_date,song_id,position,played_at,
                    ctx_collection_id,ctx_playlist_id,ctx_source_title,ctx_playlist,ctx_source_type,
                    ctx_source_id,ctx_source_platform,ctx_platform,ctx_source_label,ctx_search_keyword,
                    ctx_daily_branch,ctx_heartbeat_playlist_id,ctx_heartbeat_account_id,ctx_provider,
                    ctx_remote_session,ctx_queue_source_id,ctx_queue_source_title,ctx_queue_source_kind,
                    ctx_queue_source_platform,ctx_similar_batch_id,ctx_similar_based_on_song_id,ctx_similar_provider)
                VALUES(
                    $scope,$date,$sid,$pos,$at,
                    $ccid,$cplid,$cstitle,$cpl,$cstype,
                    $csid,$csplat,$cplat,$cslbl,$csq,
                    $cdb,$chpid,$chaid,$cprov,
                    $crs,$cqsid,$cqst,$cqsk,
                    $cqsp,$csbid,$csbos,$csprov)
                ON CONFLICT(scope,day_date,song_id) DO UPDATE SET
                    position=excluded.position,
                    played_at=excluded.played_at,
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
                """, sp.ToArray());
        }
    }
}

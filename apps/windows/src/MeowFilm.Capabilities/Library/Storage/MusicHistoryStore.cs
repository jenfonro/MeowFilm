using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicHistoryStore
{
    internal static bool Exists(DatabaseSession db, string scope) =>
        db.Scalar<long>("SELECT count(*) FROM music_history_playlists WHERE scope=$scope", ("$scope", scope)) > 0 ||
        db.Scalar<long>("SELECT count(*) FROM music_history_days WHERE scope=$scope", ("$scope", scope)) > 0;

    internal static MusicHistoryAreaRecord? ReadArea(DatabaseSession db, string scope)
    {
        if (!Exists(db, scope)) return null;
        var playlists = new List<MusicHistoryPlaylistRecord>();
        foreach (var pid in db.Query("SELECT playlist_id FROM music_history_playlists WHERE scope=$scope ORDER BY position",
            r => r.GetString(0), ("$scope", scope)))
            if (ReadPlaylist(db, scope, pid) is { } pl) playlists.Add(pl);

        var days = new List<MusicHistoryDayRecord>();
        foreach (var date in db.Query("SELECT day_date FROM music_history_days WHERE scope=$scope ORDER BY position",
            r => r.GetString(0), ("$scope", scope)))
            if (ReadDay(db, scope, date) is { } d) days.Add(d);

        return new MusicHistoryAreaRecord(playlists.ToArray(), days.ToArray());
    }

    internal static IReadOnlyList<MusicHistoryPlaylistSummaryRecord> ReadPlaylistSummaries(
        DatabaseSession db, string scope, string? playlistId = null)
    {
        string sql = playlistId is not null
            ? """
              SELECT p.playlist_id,p.position,p.played_at_ms,p.title,p.current_id,
                     p.source_id,p.source_title,p.source_kind,p.source_platform,p.source_type,
                     p.source_source_title,p.source_source_id,p.source_playlist,p.source_seed_entry_id,
                     p.source_remote_session,p.source_daily_branch,p.source_search_keyword,
                     (SELECT count(*) FROM music_history_playlist_entries e
                      WHERE e.scope=p.scope AND e.playlist_id=p.playlist_id)
              FROM music_history_playlists p
              WHERE p.scope=$scope AND p.playlist_id=$pid
              ORDER BY p.position
              """
            : """
              SELECT p.playlist_id,p.position,p.played_at_ms,p.title,p.current_id,
                     p.source_id,p.source_title,p.source_kind,p.source_platform,p.source_type,
                     p.source_source_title,p.source_source_id,p.source_playlist,p.source_seed_entry_id,
                     p.source_remote_session,p.source_daily_branch,p.source_search_keyword,
                     (SELECT count(*) FROM music_history_playlist_entries e
                      WHERE e.scope=p.scope AND e.playlist_id=p.playlist_id)
              FROM music_history_playlists p
              WHERE p.scope=$scope
              ORDER BY p.position
              """;
        var parameters = playlistId is not null
            ? new (string, object?)[] { ("$scope", scope), ("$pid", playlistId) }
            : new (string, object?)[] { ("$scope", scope) };

        return db.Query(sql, r => new MusicHistoryPlaylistSummaryRecord(
            r.GetString(0),
            r.GetInt32(1),
            r.GetInt64(2),
            r.GetString(3),
            r.GetNullableString(4),
            MusicQueueSourceRecord.ReadFrom(r, 5),
            r.GetInt32(17)), parameters);
    }

    internal static MusicHistoryPlaylistRecord? ReadPlaylist(DatabaseSession db, string scope, string playlistId)
    {
        if (!db.TryQuerySingle($"""
            SELECT p.played_at_ms,p.title,p.current_id,
                   p.source_id,p.source_title,p.source_kind,p.source_platform,p.source_type,
                   p.source_source_title,p.source_source_id,p.source_playlist,p.source_seed_entry_id,
                   p.source_remote_session,p.source_daily_branch,p.source_search_keyword,
                   {MusicPlayContextStore.SelectColumns}
            FROM music_history_playlists p
            LEFT JOIN music_play_contexts c ON c.context_id = p.context_id
            WHERE p.scope=$scope AND p.playlist_id=$pid
            """,
            r => (
                PlayedAtMs: r.GetInt64(0),
                Title: r.GetString(1),
                CurrentId: r.GetNullableString(2),
                Source: MusicQueueSourceRecord.ReadFrom(r, 3),
                Context: MusicPlayContextRecord.ReadFrom(r, 15)),
            out var header,
            ("$scope", scope), ("$pid", playlistId)))
            return null;

        var entries = ReadPlaylistEntries(db, scope, playlistId);

        var priority = new List<string>();
        var history = new List<string>();
        foreach (var (lk, val) in db.Query("""
            SELECT list_kind,item_value FROM music_history_playlist_lists
            WHERE scope=$scope AND playlist_id=$pid
            ORDER BY list_kind, position
            """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope), ("$pid", playlistId)))
        {
            if (lk == "priority") priority.Add(val);
            else history.Add(val);
        }

        return new MusicHistoryPlaylistRecord(
            playlistId,
            header.PlayedAtMs,
            header.Title,
            header.CurrentId,
            header.Source,
            header.Context,
            entries.ToArray(),
            priority.ToArray(),
            history.ToArray());
    }

    internal static IReadOnlyList<MusicQueueEntryRecord> ReadPlaylistEntries(
        DatabaseSession db, string scope, string playlistId, string? entryId = null)
    {
        string sql = entryId is not null
            ? $"""
               SELECT e.entry_id,e.song_id,{MusicPlayContextStore.SelectColumns}
               FROM music_history_playlist_entries e
               LEFT JOIN music_play_contexts c ON c.context_id = e.context_id
               WHERE e.scope=$scope AND e.playlist_id=$pid AND e.entry_id=$eid
               ORDER BY e.position
               """
            : $"""
               SELECT e.entry_id,e.song_id,{MusicPlayContextStore.SelectColumns}
               FROM music_history_playlist_entries e
               LEFT JOIN music_play_contexts c ON c.context_id = e.context_id
               WHERE e.scope=$scope AND e.playlist_id=$pid
               ORDER BY e.position
               """;
        var parameters = entryId is not null
            ? new (string, object?)[] { ("$scope", scope), ("$pid", playlistId), ("$eid", entryId) }
            : new (string, object?)[] { ("$scope", scope), ("$pid", playlistId) };
        return db.Query(sql, r => new MusicQueueEntryRecord(
            r.GetString(0), r.GetString(1), MusicPlayContextRecord.ReadFrom(r, 2)), parameters);
    }

    internal static IReadOnlyList<MusicHistoryDaySummaryRecord> ReadDaySummaries(
        DatabaseSession db, string scope, string? dayDate = null)
    {
        string sql = dayDate is not null
            ? """
              SELECT d.day_date,d.position,
                     (SELECT count(*) FROM music_history_day_songs s
                      WHERE s.scope=d.scope AND s.day_date=d.day_date)
              FROM music_history_days d
              WHERE d.scope=$scope AND d.day_date=$date
              ORDER BY d.position
              """
            : """
              SELECT d.day_date,d.position,
                     (SELECT count(*) FROM music_history_day_songs s
                      WHERE s.scope=d.scope AND s.day_date=d.day_date)
              FROM music_history_days d
              WHERE d.scope=$scope
              ORDER BY d.position
              """;
        var parameters = dayDate is not null
            ? new (string, object?)[] { ("$scope", scope), ("$date", dayDate) }
            : new (string, object?)[] { ("$scope", scope) };
        return db.Query(sql, r => new MusicHistoryDaySummaryRecord(r.GetString(0), r.GetInt32(1), r.GetInt32(2)), parameters);
    }

    internal static MusicHistoryDayRecord? ReadDay(DatabaseSession db, string scope, string dayDate)
    {
        if (!db.TryQuerySingle("""
            SELECT day_id,updated_at_ms FROM music_history_days WHERE scope=$scope AND day_date=$date
            """, r => (DayId: r.GetString(0), UpdatedAtMs: r.GetInt64(1)), out var header,
            ("$scope", scope), ("$date", dayDate)))
            return null;

        var songs = ReadDaySongs(db, scope, dayDate);
        return new MusicHistoryDayRecord(dayDate, header.DayId, header.UpdatedAtMs, songs.ToArray());
    }

    internal static IReadOnlyList<MusicHistoryDaySongRecord> ReadDaySongs(
        DatabaseSession db, string scope, string dayDate, string? songId = null)
    {
        string sql = songId is not null
            ? $"""
               SELECT s.song_id,s.played_at_ms,{MusicPlayContextStore.SelectColumns}
               FROM music_history_day_songs s
               LEFT JOIN music_play_contexts c ON c.context_id = s.context_id
               WHERE s.scope=$scope AND s.day_date=$date AND s.song_id=$sid
               ORDER BY s.position
               """
            : $"""
               SELECT s.song_id,s.played_at_ms,{MusicPlayContextStore.SelectColumns}
               FROM music_history_day_songs s
               LEFT JOIN music_play_contexts c ON c.context_id = s.context_id
               WHERE s.scope=$scope AND s.day_date=$date
               ORDER BY s.position
               """;
        var parameters = songId is not null
            ? new (string, object?)[] { ("$scope", scope), ("$date", dayDate), ("$sid", songId) }
            : new (string, object?)[] { ("$scope", scope), ("$date", dayDate) };
        return db.Query(sql, r => new MusicHistoryDaySongRecord(
            r.GetString(0), r.GetInt64(1), MusicPlayContextRecord.ReadFrom(r, 2)), parameters);
    }

    internal static bool ReplaceArea(DatabaseSession db, string scope, MusicHistoryAreaRecord area)
    {
        db.Execute("DELETE FROM music_history_playlists WHERE scope=$scope", ("$scope", scope));
        for (int i = 0; i < area.Playlists.Length; i++)
            SavePlaylist(db, scope, previous: null, area.Playlists[i], i, insert: false);

        db.Execute("DELETE FROM music_history_days WHERE scope=$scope", ("$scope", scope));
        for (int i = 0; i < area.Days.Length; i++)
            SaveDay(db, scope, previous: null, area.Days[i], i, insert: false);

        return true;
    }

    internal static bool ApplyPlaylistMutation(DatabaseSession db, string scope, MusicHistoryPlaylistMutation mutation)
    {
        if (mutation.Next is null)
        {
            db.Execute("DELETE FROM music_history_playlists WHERE scope=$scope AND playlist_id=$pid",
                ("$scope", scope), ("$pid", mutation.PlaylistId));
            return true;
        }
        return SavePlaylist(db, scope, mutation.Previous, mutation.Next, mutation.Ordinal, mutation.Insert);
    }

    internal static bool ApplyDayMutation(DatabaseSession db, string scope, MusicHistoryDayMutation mutation)
    {
        if (mutation.Next is null)
        {
            db.Execute("DELETE FROM music_history_days WHERE scope=$scope AND day_date=$date",
                ("$scope", scope), ("$date", mutation.DayDate));
            return true;
        }
        return SaveDay(db, scope, mutation.Previous, mutation.Next, mutation.Ordinal, mutation.Insert);
    }

    private static bool SavePlaylist(DatabaseSession db, string scope, MusicHistoryPlaylistRecord? previous,
        MusicHistoryPlaylistRecord next, int ordinal, bool insert)
    {
        bool contextsRemoved = false;
        if (insert && previous is null)
            db.Execute("UPDATE music_history_playlists SET position=position+1 WHERE scope=$scope AND position>=$ord",
                ("$scope", scope), ("$ord", ordinal));

        string? playlistCtxId = MusicPlayContextStore.Ensure(db, next.Context);
        if (previous is not null && previous.Context != next.Context && !previous.Context.IsEmpty)
            contextsRemoved = true;

        var p = new List<(string, object?)>
        {
            ("$scope", scope),
            ("$pid", next.PlaylistId),
            ("$pos", ordinal),
            ("$at", next.PlayedAtMs),
            ("$title", next.Title),
            ("$cid", next.CurrentId),
            ("$ctx", playlistCtxId)
        };
        p.AddRange((next.Source ?? MusicQueueSourceRecord.Empty).ToParams("s_"));

        db.Execute("""
            INSERT INTO music_history_playlists(
                scope,playlist_id,position,played_at_ms,title,current_id,
                source_id,source_title,source_kind,source_platform,source_type,
                source_source_title,source_source_id,source_playlist,source_seed_entry_id,
                source_remote_session,source_daily_branch,source_search_keyword,
                context_id)
            VALUES(
                $scope,$pid,$pos,$at,$title,$cid,
                $s_id,$s_title,$s_kind,$s_plat,$s_stype,
                $s_stitle,$s_sid,$s_pl,$s_seed,
                $s_rs,$s_db,$s_sq,
                $ctx)
            ON CONFLICT(scope,playlist_id) DO UPDATE SET
                position=excluded.position,
                played_at_ms=excluded.played_at_ms,
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
                context_id=excluded.context_id
            """, p.ToArray());

        var prevById = new Dictionary<string, (int Position, MusicQueueEntryRecord Entry)>(StringComparer.Ordinal);
        if (previous is null)
        {
            if (!insert)
            {
                db.Execute("DELETE FROM music_history_playlist_entries WHERE scope=$scope AND playlist_id=$pid",
                    ("$scope", scope), ("$pid", next.PlaylistId));
                contextsRemoved = true;
            }
        }
        else
        {
            for (int i = 0; i < previous.Entries.Length; i++)
                prevById[previous.Entries[i].EntryId] = (i, previous.Entries[i]);
            var nextIds = new HashSet<string>(next.Entries.Select(e => e.EntryId), StringComparer.Ordinal);
            foreach (var old in previous.Entries)
                if (!nextIds.Contains(old.EntryId))
                {
                    db.Execute("DELETE FROM music_history_playlist_entries WHERE scope=$scope AND playlist_id=$pid AND entry_id=$eid",
                        ("$scope", scope), ("$pid", next.PlaylistId), ("$eid", old.EntryId));
                    if (!old.Context.IsEmpty) contextsRemoved = true;
                }
        }

        for (int i = 0; i < next.Entries.Length; i++)
        {
            var e = next.Entries[i];
            if (prevById.TryGetValue(e.EntryId, out var oldEntry))
            {
                if (oldEntry.Position == i && oldEntry.Entry == e) continue;
                if (oldEntry.Entry == e)
                {
                    db.Execute("""
                        UPDATE music_history_playlist_entries SET position=$pos
                        WHERE scope=$scope AND playlist_id=$pid AND entry_id=$eid
                        """, ("$pos", i), ("$scope", scope), ("$pid", next.PlaylistId), ("$eid", e.EntryId));
                    continue;
                }
                if (oldEntry.Entry.Context != e.Context && !oldEntry.Entry.Context.IsEmpty)
                    contextsRemoved = true;
            }

            string? ctxId = MusicPlayContextStore.Ensure(db, e.Context);
            db.Execute("""
                INSERT INTO music_history_playlist_entries(scope,playlist_id,entry_id,position,song_id,context_id)
                VALUES($scope,$pid,$eid,$pos,$sid,$ctx)
                ON CONFLICT(scope,playlist_id,entry_id) DO UPDATE SET
                    position=excluded.position,
                    song_id=excluded.song_id,
                    context_id=excluded.context_id
                """,
                ("$scope", scope), ("$pid", next.PlaylistId), ("$eid", e.EntryId),
                ("$pos", i), ("$sid", e.SongId), ("$ctx", ctxId));
        }

        ReplacePlaylistList(db, scope, next.PlaylistId, "priority", previous?.Priority, next.Priority, insert);
        ReplacePlaylistList(db, scope, next.PlaylistId, "history", previous?.History, next.History, insert);
        return contextsRemoved;
    }

    private static void ReplacePlaylistList(DatabaseSession db, string scope, string playlistId, string listKind,
        string[]? previous, string[] next, bool insert)
    {
        if (previous is not null && previous.SequenceEqual(next, StringComparer.Ordinal)) return;
        if (previous is null)
        {
            if (!insert)
                db.Execute("""
                    DELETE FROM music_history_playlist_lists
                    WHERE scope=$scope AND playlist_id=$pid AND list_kind=$lk
                    """, ("$scope", scope), ("$pid", playlistId), ("$lk", listKind));
            for (int i = 0; i < next.Length; i++)
                db.Execute("""
                    INSERT INTO music_history_playlist_lists(scope,playlist_id,list_kind,position,item_value)
                    VALUES($scope,$pid,$lk,$pos,$val)
                    """, ("$scope", scope), ("$pid", playlistId), ("$lk", listKind), ("$pos", i), ("$val", next[i]));
            return;
        }
        int prefix = CommonPrefixLength(previous, next, StringComparer.Ordinal);
        if (prefix < previous.Length)
            db.Execute("""
                DELETE FROM music_history_playlist_lists
                WHERE scope=$scope AND playlist_id=$pid AND list_kind=$lk AND position>=$pos
                """, ("$scope", scope), ("$pid", playlistId), ("$lk", listKind), ("$pos", prefix));
        for (int i = prefix; i < next.Length; i++)
            db.Execute("""
                INSERT INTO music_history_playlist_lists(scope,playlist_id,list_kind,position,item_value)
                VALUES($scope,$pid,$lk,$pos,$val)
                """, ("$scope", scope), ("$pid", playlistId), ("$lk", listKind), ("$pos", i), ("$val", next[i]));
    }

    private static bool SaveDay(DatabaseSession db, string scope, MusicHistoryDayRecord? previous,
        MusicHistoryDayRecord next, int ordinal, bool insert)
    {
        bool contextsRemoved = false;
        if (insert && previous is null)
            db.Execute("UPDATE music_history_days SET position=position+1 WHERE scope=$scope AND position>=$ord",
                ("$scope", scope), ("$ord", ordinal));

        db.Execute("""
            INSERT INTO music_history_days(scope,day_date,position,day_id,updated_at_ms)
            VALUES($scope,$date,$pos,$did,$at)
            ON CONFLICT(scope,day_date) DO UPDATE SET
                position=excluded.position,
                day_id=excluded.day_id,
                updated_at_ms=excluded.updated_at_ms
            """,
            ("$scope", scope), ("$date", next.DayDate), ("$pos", ordinal),
            ("$did", next.DayId), ("$at", next.UpdatedAtMs));

        var prevById = new Dictionary<string, (int Position, MusicHistoryDaySongRecord Song)>(StringComparer.Ordinal);
        if (previous is null)
        {
            if (!insert)
            {
                db.Execute("DELETE FROM music_history_day_songs WHERE scope=$scope AND day_date=$date",
                    ("$scope", scope), ("$date", next.DayDate));
                contextsRemoved = true;
            }
        }
        else
        {
            for (int i = 0; i < previous.Songs.Length; i++)
                prevById[previous.Songs[i].SongId] = (i, previous.Songs[i]);
            var nextIds = new HashSet<string>(next.Songs.Select(s => s.SongId), StringComparer.Ordinal);
            foreach (var old in previous.Songs)
                if (!nextIds.Contains(old.SongId))
                {
                    db.Execute("DELETE FROM music_history_day_songs WHERE scope=$scope AND day_date=$date AND song_id=$sid",
                        ("$scope", scope), ("$date", next.DayDate), ("$sid", old.SongId));
                    if (!old.Context.IsEmpty) contextsRemoved = true;
                }
        }

        for (int i = 0; i < next.Songs.Length; i++)
        {
            var s = next.Songs[i];
            if (prevById.TryGetValue(s.SongId, out var old))
            {
                if (old.Position == i && old.Song == s) continue;
                if (old.Song == s)
                {
                    db.Execute("""
                        UPDATE music_history_day_songs SET position=$pos
                        WHERE scope=$scope AND day_date=$date AND song_id=$sid
                        """, ("$pos", i), ("$scope", scope), ("$date", next.DayDate), ("$sid", s.SongId));
                    continue;
                }
                if (old.Song.Context != s.Context && !old.Song.Context.IsEmpty)
                    contextsRemoved = true;
            }
            string? ctxId = MusicPlayContextStore.Ensure(db, s.Context);
            db.Execute("""
                INSERT INTO music_history_day_songs(scope,day_date,song_id,position,played_at_ms,context_id)
                VALUES($scope,$date,$sid,$pos,$at,$ctx)
                ON CONFLICT(scope,day_date,song_id) DO UPDATE SET
                    position=excluded.position,
                    played_at_ms=excluded.played_at_ms,
                    context_id=excluded.context_id
                """,
                ("$scope", scope), ("$date", next.DayDate), ("$sid", s.SongId),
                ("$pos", i), ("$at", s.PlayedAtMs), ("$ctx", ctxId));
        }

        return contextsRemoved;
    }
}

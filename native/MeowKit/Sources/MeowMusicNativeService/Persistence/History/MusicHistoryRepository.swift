import Foundation
import MeowStorage

enum MusicHistoryRepository {
    private static let playlistSharedColumns =
        ["scope", "playlist_id", "position", "played_at_ms", "title", "current_id"] +
        MusicSourceRecord.sharedColumns(prefix: "source_")
    private static let playlistExtrasColumns =
        ["scope", "playlist_id"] + MusicSourceRecord.extrasColumns(prefix: "source_") + ["context_id"]
    private static let playlistEntryColumns = ["scope", "playlist_id", "entry_id", "position", "song_id", "context_id"]
    private static let daySongColumns = ["scope", "day_date", "song_id", "position", "played_at_ms", "context_id"]
    private static let playlistSelectColumns =
        "p.playlist_id,p.played_at_ms,p.title,p.current_id,p.source_id,p.source_title,p.source_platform," +
        "e.source_type,e.source_source_title,e.source_source_id,e.source_playlist," +
        "e.source_seed_entry_id,e.source_remote_session,e.source_daily_branch,e.source_search_keyword,e.context_id"

    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicHistoryState? {
        let playlistHeaders = try db.query(
            "SELECT \(playlistSelectColumns) FROM music_history_playlists p " +
            "LEFT JOIN ios_music_history_playlist_extras e ON e.scope=p.scope AND e.playlist_id=p.playlist_id " +
            "WHERE p.scope=? ORDER BY p.position",
            [.text(scope)]
        ) { c in
            (pid: try c.text(0), playedAtMs: try c.int64(1), title: try c.text(2), currentID: try c.optionalText(3),
             source: try MusicSourceRecord.read(from: c, offset: 4), contextID: try c.optionalText(15))
        }
        let dayHeaders = try db.query(
            "SELECT day_date,day_id,updated_at_ms FROM music_history_days WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            (date: try c.text(0), dayID: try c.text(1), updatedAtMs: try c.int64(2))
        }
        if playlistHeaders.isEmpty && dayHeaders.isEmpty { return nil }

        var contextIDs = Set(playlistHeaders.compactMap(\.contextID))
        var rawEntriesByPlaylist: [String: [(id: String, songID: String, contextID: String?)]] = [:]
        try db.scan(
            "SELECT playlist_id,entry_id,song_id,context_id FROM music_history_playlist_entries WHERE scope=? ORDER BY playlist_id,position",
            [.text(scope)]
        ) { c in
            let pid = try c.text(0), cid = try c.optionalText(3)
            if let cid { contextIDs.insert(cid) }
            rawEntriesByPlaylist[pid, default: []].append((try c.text(1), try c.text(2), cid))
        }

        var listsByPlaylist: [String: [String: [String]]] = [:]
        try db.scan(
            "SELECT playlist_id,list_kind,item_value FROM music_history_playlist_lists WHERE scope=? ORDER BY playlist_id,list_kind,position",
            [.text(scope)]
        ) { c in
            listsByPlaylist[try c.text(0), default: [:]][try c.text(1), default: []].append(try c.text(2))
        }

        var rawSongsByDay: [String: [(songID: String, playedAtMs: Int64, contextID: String?)]] = [:]
        try db.scan(
            "SELECT day_date,song_id,played_at_ms,context_id FROM music_history_day_songs WHERE scope=? ORDER BY day_date,position",
            [.text(scope)]
        ) { c in
            let date = try c.text(0), cid = try c.optionalText(3)
            if let cid { contextIDs.insert(cid) }
            rawSongsByDay[date, default: []].append((try c.text(1), try c.int64(2), cid))
        }

        let contexts = try MusicPlayContextStore.loadMap(db, ids: contextIDs)
        let playlists = playlistHeaders.map { header in
            let entries = (rawEntriesByPlaylist[header.pid] ?? []).map { row in
                MusicQueueEntryRecord(id: row.id, songID: row.songID, context: row.contextID.flatMap { contexts[$0] } ?? .empty)
            }
            return MusicHistoryPlaylistRecord(
                id: header.pid, playedAtMs: header.playedAtMs, title: header.title, currentID: header.currentID,
                source: header.source, context: header.contextID.flatMap { contexts[$0] } ?? .empty,
                entries: entries, priority: listsByPlaylist[header.pid]?["priority"] ?? [],
                history: listsByPlaylist[header.pid]?["history"] ?? []
            )
        }
        let days = dayHeaders.map { header in
            let songs = (rawSongsByDay[header.date] ?? []).map { row in
                MusicHistoryDaySongRecord(songID: row.songID, playedAtMs: row.playedAtMs, context: row.contextID.flatMap { contexts[$0] } ?? .empty)
            }
            return MusicHistoryDayRecord(date: header.date, dayID: header.dayID, updatedAtMs: header.updatedAtMs, songs: songs)
        }
        return MusicHistoryState(playlists: playlists, days: days)
    }

    static func applyChanges(_ db: SQLiteConnection, scope: String, previous: MusicHistoryState?, next: MusicHistoryState) throws {
        var contextsTouched = false

        if previous == nil || previous?.playlists != next.playlists {
            if previous == nil, try db.execute("DELETE FROM music_history_playlists WHERE scope=?", [.text(scope)]) > 0 {
                contextsTouched = true
            }
            let prevPlaylistsByID = Dictionary(uniqueKeysWithValues: (previous?.playlists ?? []).enumerated().map { ($1.id, (pos: $0, pl: $1)) })
            let nextPIDs = Set(next.playlists.map(\.id))
            for (oldPID, old) in prevPlaylistsByID where !nextPIDs.contains(oldPID) {
                try db.execute("DELETE FROM music_history_playlists WHERE scope=? AND playlist_id=?", [.text(scope), .text(oldPID)])
                if !old.pl.context.isEmpty || old.pl.entries.contains(where: { !$0.context.isEmpty }) { contextsTouched = true }
            }

            for (index, playlist) in next.playlists.enumerated() {
                let old = prevPlaylistsByID[playlist.id]
                if let old, old.pos == index, old.pl == playlist { continue }
                if let old, old.pl.context != playlist.context && !old.pl.context.isEmpty { contextsTouched = true }
                let sourceRec = playlist.source ?? .empty
                let contextID = try MusicPlayContextStore.upsert(db, context: playlist.context)
                let vals: [SQLiteValue] = [
                    .text(scope), .text(playlist.id), .integer(index), .integer(playlist.playedAtMs),
                    .text(playlist.title), .optional(playlist.currentID.flatMap { $0.isEmpty ? nil : $0 })
                ] + sourceRec.sharedSQLiteValues
                try db.upsert("music_history_playlists", columns: playlistSharedColumns, values: vals, key: ["scope", "playlist_id"])

                if !sourceRec.hasIOSExtras && contextID == nil {
                    try db.execute("DELETE FROM ios_music_history_playlist_extras WHERE scope=? AND playlist_id=?", [.text(scope), .text(playlist.id)])
                } else {
                    try db.upsert(
                        "ios_music_history_playlist_extras", columns: playlistExtrasColumns,
                        values: [.text(scope), .text(playlist.id)] + sourceRec.extrasSQLiteValues + [.optional(contextID)],
                        key: ["scope", "playlist_id"]
                    )
                }

                if old == nil || old!.pl.entries != playlist.entries {
                    if old == nil && previous != nil,
                       try db.execute("DELETE FROM music_history_playlist_entries WHERE scope=? AND playlist_id=?", [.text(scope), .text(playlist.id)]) > 0 {
                        contextsTouched = true
                    }
                    let prevEntries = Dictionary(uniqueKeysWithValues: (old?.pl.entries ?? []).enumerated().map { ($1.id, (pos: $0, val: $1)) })
                    let nextEIDs = Set(playlist.entries.map(\.id))
                    for (oldEID, oldEntry) in prevEntries where !nextEIDs.contains(oldEID) {
                        try db.execute(
                            "DELETE FROM music_history_playlist_entries WHERE scope=? AND playlist_id=? AND entry_id=?",
                            [.text(scope), .text(playlist.id), .text(oldEID)]
                        )
                        if !oldEntry.val.context.isEmpty { contextsTouched = true }
                    }
                    for (eIndex, entry) in playlist.entries.enumerated() {
                        if let prevE = prevEntries[entry.id], prevE.pos == eIndex, prevE.val == entry { continue }
                        if let prevE = prevEntries[entry.id], prevE.val.context != entry.context, !prevE.val.context.isEmpty { contextsTouched = true }
                        let entryCtxID = try MusicPlayContextStore.upsert(db, context: entry.context)
                        try db.upsert(
                            "music_history_playlist_entries", columns: playlistEntryColumns,
                            values: [.text(scope), .text(playlist.id), .text(entry.id), .integer(eIndex), .text(entry.songID), .optional(entryCtxID)],
                            key: ["scope", "playlist_id", "entry_id"]
                        )
                    }
                }

                for (kind, nextList, prevList) in [("priority", playlist.priority, old?.pl.priority ?? []), ("history", playlist.history, old?.pl.history ?? [])] {
                    if old != nil && prevList == nextList { continue }
                    for (pos, item) in nextList.enumerated() {
                        try db.upsert(
                            "music_history_playlist_lists",
                            columns: ["scope", "playlist_id", "list_kind", "position", "item_value"],
                            values: [.text(scope), .text(playlist.id), .text(kind), .integer(pos), .text(item)],
                            key: ["scope", "playlist_id", "list_kind", "position"]
                        )
                    }
                    try db.execute(
                        "DELETE FROM music_history_playlist_lists WHERE scope=? AND playlist_id=? AND list_kind=? AND position>=?",
                        [.text(scope), .text(playlist.id), .text(kind), .integer(nextList.count)]
                    )
                }
            }
        }

        if previous == nil || previous?.days != next.days {
            if previous == nil, try db.execute("DELETE FROM music_history_days WHERE scope=?", [.text(scope)]) > 0 {
                contextsTouched = true
            }
            let prevDaysByDate = Dictionary(uniqueKeysWithValues: (previous?.days ?? []).enumerated().map { ($1.date, (pos: $0, day: $1)) })
            let nextDates = Set(next.days.map(\.date))
            for (oldDate, old) in prevDaysByDate where !nextDates.contains(oldDate) {
                try db.execute("DELETE FROM music_history_days WHERE scope=? AND day_date=?", [.text(scope), .text(oldDate)])
                if old.day.songs.contains(where: { !$0.context.isEmpty }) { contextsTouched = true }
            }

            for (index, day) in next.days.enumerated() {
                let old = prevDaysByDate[day.date]
                if let old, old.pos == index, old.day == day { continue }
                let dayID = day.dayID.isEmpty ? day.date : day.dayID
                try db.upsert(
                    "music_history_days", columns: ["scope", "day_date", "position", "day_id", "updated_at_ms"],
                    values: [.text(scope), .text(day.date), .integer(index), .text(dayID), .integer(day.updatedAtMs)],
                    key: ["scope", "day_date"]
                )

                if old == nil || old!.day.songs != day.songs {
                    if old == nil && previous != nil,
                       try db.execute("DELETE FROM music_history_day_songs WHERE scope=? AND day_date=?", [.text(scope), .text(day.date)]) > 0 {
                        contextsTouched = true
                    }
                    let prevSongs = Dictionary(uniqueKeysWithValues: (old?.day.songs ?? []).enumerated().map { ($1.songID, (pos: $0, val: $1)) })
                    let nextSongIDs = Set(day.songs.map(\.songID))
                    for (oldSID, oldSong) in prevSongs where !nextSongIDs.contains(oldSID) {
                        try db.execute("DELETE FROM music_history_day_songs WHERE scope=? AND day_date=? AND song_id=?", [.text(scope), .text(day.date), .text(oldSID)])
                        if !oldSong.val.context.isEmpty { contextsTouched = true }
                    }
                    for (sIndex, item) in day.songs.enumerated() {
                        if let prevS = prevSongs[item.songID], prevS.pos == sIndex, prevS.val == item { continue }
                        if let prevS = prevSongs[item.songID], prevS.val.context != item.context, !prevS.val.context.isEmpty { contextsTouched = true }
                        let songCtxID = try MusicPlayContextStore.upsert(db, context: item.context)
                        try db.upsert(
                            "music_history_day_songs", columns: daySongColumns,
                            values: [.text(scope), .text(day.date), .text(item.songID), .integer(sIndex), .integer(item.playedAtMs), .optional(songCtxID)],
                            key: ["scope", "day_date", "song_id"]
                        )
                    }
                }
            }
        }

        if contextsTouched { try MusicPlayContextStore.pruneOrphans(db) }
    }
}

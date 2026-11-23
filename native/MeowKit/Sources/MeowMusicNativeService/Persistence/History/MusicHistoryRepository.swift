import Foundation
import MeowStorage

enum MusicHistoryRepository {
    private static let playlistColumns =
        ["scope", "playlist_id", "position", "played_at_ms", "title", "current_id"] +
        MusicSourceRecord.columns(prefix: "source_") +
        ["context_id"]

    private static let playlistEntryColumns =
        ["scope", "playlist_id", "entry_id", "position", "song_id", "context_id"]

    private static let daySongColumns =
        ["scope", "day_date", "song_id", "position", "played_at_ms", "context_id"]

    private static let playlistSelectColumns =
        "playlist_id,played_at_ms,title,current_id," +
        MusicSourceRecord.columns(prefix: "source_").joined(separator: ",") +
        ",context_id"

    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicHistoryState? {
        let playlistHeaders: [(pid: String, playedAtMs: Int64, title: String, currentID: String?, source: MusicSourceRecord?, contextID: String?)] =
            try db.query(
                "SELECT \(playlistSelectColumns) FROM music_history_playlists WHERE scope=? ORDER BY position",
                [.text(scope)]
            ) { c in
                (
                    pid: try c.text(0),
                    playedAtMs: try c.int64(1),
                    title: try c.text(2),
                    currentID: try c.optionalText(3),
                    source: try MusicSourceRecord.read(from: c, offset: 4),
                    contextID: try c.optionalText(16)
                )
            }

        let dayHeaders: [(date: String, dayID: String, updatedAtMs: Int64)] = try db.query(
            "SELECT day_date,day_id,updated_at_ms FROM music_history_days WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { c in
            (date: try c.text(0), dayID: try c.text(1), updatedAtMs: try c.int64(2))
        }

        if playlistHeaders.isEmpty && dayHeaders.isEmpty { return nil }

        var contextIDs = Set<String>()
        for header in playlistHeaders {
            if let cid = header.contextID { contextIDs.insert(cid) }
        }

        var rawEntriesByPlaylist: [String: [(id: String, songID: String, contextID: String?)]] = [:]
        try db.scan(
            "SELECT playlist_id,entry_id,song_id,context_id FROM music_history_playlist_entries " +
            "WHERE scope=? ORDER BY playlist_id,position",
            [.text(scope)]
        ) { c in
            let pid = try c.text(0)
            let cid = try c.optionalText(3)
            if let cid { contextIDs.insert(cid) }
            rawEntriesByPlaylist[pid, default: []].append((id: try c.text(1), songID: try c.text(2), contextID: cid))
        }

        var listsByPlaylist: [String: [String: [String]]] = [:]
        try db.scan(
            "SELECT playlist_id,list_kind,item_value FROM music_history_playlist_lists " +
            "WHERE scope=? ORDER BY playlist_id,list_kind,position",
            [.text(scope)]
        ) { c in
            listsByPlaylist[try c.text(0), default: [:]][try c.text(1), default: []].append(try c.text(2))
        }

        var rawSongsByDay: [String: [(songID: String, playedAtMs: Int64, contextID: String?)]] = [:]
        try db.scan(
            "SELECT day_date,song_id,played_at_ms,context_id FROM music_history_day_songs " +
            "WHERE scope=? ORDER BY day_date,position",
            [.text(scope)]
        ) { c in
            let date = try c.text(0)
            let cid = try c.optionalText(3)
            if let cid { contextIDs.insert(cid) }
            rawSongsByDay[date, default: []].append((songID: try c.text(1), playedAtMs: try c.int64(2), contextID: cid))
        }

        let contexts = try MusicPlayContextStore.loadMap(db, ids: contextIDs)

        let playlists: [MusicHistoryPlaylistRecord] = playlistHeaders.map { header in
            let entries = (rawEntriesByPlaylist[header.pid] ?? []).map { row in
                MusicQueueEntryRecord(
                    id: row.id,
                    songID: row.songID,
                    context: row.contextID.flatMap { contexts[$0] } ?? .empty
                )
            }
            return MusicHistoryPlaylistRecord(
                id: header.pid,
                playedAtMs: header.playedAtMs,
                title: header.title,
                currentID: header.currentID,
                source: header.source,
                context: header.contextID.flatMap { contexts[$0] } ?? .empty,
                entries: entries,
                priority: listsByPlaylist[header.pid]?["priority"] ?? [],
                history: listsByPlaylist[header.pid]?["history"] ?? []
            )
        }

        let days: [MusicHistoryDayRecord] = dayHeaders.map { header in
            let songs = (rawSongsByDay[header.date] ?? []).map { row in
                MusicHistoryDaySongRecord(
                    songID: row.songID,
                    playedAtMs: row.playedAtMs,
                    context: row.contextID.flatMap { contexts[$0] } ?? .empty
                )
            }
            return MusicHistoryDayRecord(
                date: header.date,
                dayID: header.dayID,
                updatedAtMs: header.updatedAtMs,
                songs: songs
            )
        }

        return MusicHistoryState(playlists: playlists, days: days)
    }

    static func applyChanges(_ db: SQLiteConnection,
                             scope: String,
                             previous: MusicHistoryState?,
                             next: MusicHistoryState) throws {
        var contextsTouched = false

        if previous == nil || previous?.playlists != next.playlists {
            var prevPlaylistsByID: [String: (pos: Int, pl: MusicHistoryPlaylistRecord)] = [:]
            if let prevPls = previous?.playlists {
                for (index, pl) in prevPls.enumerated() {
                    prevPlaylistsByID[pl.id] = (index, pl)
                }
            }
            let nextPIDs = Set(next.playlists.map(\.id))
            for (oldPID, old) in prevPlaylistsByID where !nextPIDs.contains(oldPID) {
                try db.execute("DELETE FROM music_history_playlists WHERE scope=? AND playlist_id=?", [.text(scope), .text(oldPID)])
                if !old.pl.context.isEmpty || old.pl.entries.contains(where: { !$0.context.isEmpty }) {
                    contextsTouched = true
                }
            }

            for (index, playlist) in next.playlists.enumerated() {
                let old = prevPlaylistsByID[playlist.id]
                if let old, old.pos == index, old.pl == playlist { continue }

                if let old, old.pl.context != playlist.context && !old.pl.context.isEmpty {
                    contextsTouched = true
                }
                let sourceRec = playlist.source ?? .empty
                let contextID = try MusicPlayContextStore.upsert(db, context: playlist.context)
                let vals: [SQLiteValue] = [
                    .text(scope),
                    .text(playlist.id),
                    .integer(index),
                    .integer(playlist.playedAtMs),
                    .text(playlist.title),
                    .optional(playlist.currentID.flatMap { $0.isEmpty ? nil : $0 })
                ] + sourceRec.sqliteValues + [.optional(contextID)]
                try db.upsert("music_history_playlists", columns: playlistColumns, values: vals, key: ["scope", "playlist_id"])

                if old == nil || old!.pl.entries != playlist.entries {
                    var prevEntries: [String: (pos: Int, val: MusicQueueEntryRecord)] = [:]
                    if let oldArr = old?.pl.entries {
                        for (idx, entry) in oldArr.enumerated() { prevEntries[entry.id] = (idx, entry) }
                    }
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
                        if let prevE = prevEntries[entry.id], prevE.val.context != entry.context, !prevE.val.context.isEmpty {
                            contextsTouched = true
                        }
                        let entryCtxID = try MusicPlayContextStore.upsert(db, context: entry.context)
                        try db.upsert(
                            "music_history_playlist_entries",
                            columns: playlistEntryColumns,
                            values: [
                                .text(scope), .text(playlist.id), .text(entry.id),
                                .integer(eIndex), .text(entry.songID), .optional(entryCtxID)
                            ],
                            key: ["scope", "playlist_id", "entry_id"]
                        )
                    }
                }

                for (kind, nextList, prevList) in [
                    ("priority", playlist.priority, old?.pl.priority ?? []),
                    ("history", playlist.history, old?.pl.history ?? [])
                ] {
                    if old != nil && prevList == nextList { continue }
                    for (pos, item) in nextList.enumerated() {
                        try db.upsert(
                            "music_history_playlist_lists",
                            columns: ["scope", "playlist_id", "list_kind", "position", "item_value"],
                            values: [.text(scope), .text(playlist.id), .text(kind), .integer(pos), .text(item)],
                            key: ["scope", "playlist_id", "list_kind", "position"]
                        )
                    }
                    if old != nil {
                        try db.execute(
                            "DELETE FROM music_history_playlist_lists WHERE scope=? AND playlist_id=? AND list_kind=? AND position>=?",
                            [.text(scope), .text(playlist.id), .text(kind), .integer(nextList.count)]
                        )
                    }
                }
            }
        }

        if previous == nil || previous?.days != next.days {
            var prevDaysByDate: [String: (pos: Int, day: MusicHistoryDayRecord)] = [:]
            if let prevDays = previous?.days {
                for (index, day) in prevDays.enumerated() {
                    prevDaysByDate[day.date] = (index, day)
                }
            }
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
                    "music_history_days",
                    columns: ["scope", "day_date", "position", "day_id", "updated_at_ms"],
                    values: [.text(scope), .text(day.date), .integer(index), .text(dayID), .integer(day.updatedAtMs)],
                    key: ["scope", "day_date"]
                )

                if old == nil || old!.day.songs != day.songs {
                    var prevSongs: [String: (pos: Int, val: MusicHistoryDaySongRecord)] = [:]
                    if let oldSongs = old?.day.songs {
                        for (idx, item) in oldSongs.enumerated() { prevSongs[item.songID] = (idx, item) }
                    }
                    let nextSongIDs = Set(day.songs.map(\.songID))
                    for (oldSID, oldSong) in prevSongs where !nextSongIDs.contains(oldSID) {
                        try db.execute(
                            "DELETE FROM music_history_day_songs WHERE scope=? AND day_date=? AND song_id=?",
                            [.text(scope), .text(day.date), .text(oldSID)]
                        )
                        if !oldSong.val.context.isEmpty { contextsTouched = true }
                    }
                    for (sIndex, item) in day.songs.enumerated() {
                        if let prevS = prevSongs[item.songID], prevS.pos == sIndex, prevS.val == item { continue }
                        if let prevS = prevSongs[item.songID], prevS.val.context != item.context, !prevS.val.context.isEmpty {
                            contextsTouched = true
                        }
                        let songCtxID = try MusicPlayContextStore.upsert(db, context: item.context)
                        try db.upsert(
                            "music_history_day_songs",
                            columns: daySongColumns,
                            values: [
                                .text(scope), .text(day.date), .text(item.songID),
                                .integer(sIndex), .integer(item.playedAtMs), .optional(songCtxID)
                            ],
                            key: ["scope", "day_date", "song_id"]
                        )
                    }
                }
            }
        }

        if contextsTouched {
            try MusicPlayContextStore.pruneOrphans(db)
        }
    }
}

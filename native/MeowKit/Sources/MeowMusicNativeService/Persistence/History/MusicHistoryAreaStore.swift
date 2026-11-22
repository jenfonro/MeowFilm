import Foundation
import MeowStorage

enum MusicHistoryAreaStore {
    private static let playlistColumns =
        ["scope", "playlist_id", "position", "played_at", "title", "current_id"] +
        MusicPersistenceHelpers.sourceColumns(prefix: "source_") +
        MusicPersistenceHelpers.contextColumns

    private static let playlistEntryColumns =
        ["scope", "playlist_id", "entry_id", "position", "song_id"] + MusicPersistenceHelpers.contextColumns

    private static let daySongColumns =
        ["scope", "day_date", "song_id", "position", "played_at"] + MusicPersistenceHelpers.contextColumns

    private static let playlistSelectColumns =
        "playlist_id,played_at,title,current_id," +
        MusicPersistenceHelpers.sourceColumns(prefix: "source_").joined(separator: ",") + "," +
        MusicPersistenceHelpers.contextColumns.joined(separator: ",")

    private static let playlistEntrySelectColumns =
        "playlist_id,entry_id,song_id," + MusicPersistenceHelpers.contextColumns.joined(separator: ",")

    private static let daySongSelectColumns =
        "day_date,song_id,played_at," + MusicPersistenceHelpers.contextColumns.joined(separator: ",")

    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        let playlistHeaders: [(pid: String, at: Double, title: String, currentID: String?, source: MusicSourceRecord?, context: MusicPlayContextRecord)] =
            try db.query(
                "SELECT \(playlistSelectColumns) FROM music_history_playlists WHERE scope=? ORDER BY position",
                [.text(scope)]
            ) { c in
                (
                    pid: try c.text(0),
                    at: try c.double(1),
                    title: try c.text(2),
                    currentID: try c.optionalText(3),
                    source: try MusicSourceRecord.read(from: c, offset: 4),
                    context: try MusicPlayContextRecord.read(from: c, offset: 16)
                )
            }
        let dayDates: [String] = try db.query(
            "SELECT day_date FROM music_history_days WHERE scope=? ORDER BY position",
            [.text(scope)]
        ) { try $0.text(0) }
        if playlistHeaders.isEmpty && dayDates.isEmpty { return nil }

        var entriesByPlaylist: [String: [MJ]] = [:]
        try db.scan(
            "SELECT \(playlistEntrySelectColumns) FROM music_history_playlist_entries WHERE scope=? ORDER BY playlist_id,position",
            [.text(scope)]
        ) { c in
            let pid = try c.text(0)
            var fields: [(String, MJ)] = [
                ("id", .string(try c.text(1))),
                ("songId", .string(try c.text(2)))
            ]
            if let context = try MusicPlayContextRecord.read(from: c, offset: 3).toMJ() {
                fields.append(("context", context))
            }
            entriesByPlaylist[pid, default: []].append(.obj(fields))
        }

        var listsByPlaylist: [String: [String: [String]]] = [:]
        try db.scan(
            "SELECT playlist_id,list_kind,item_value FROM music_history_playlist_lists " +
            "WHERE scope=? ORDER BY playlist_id,list_kind,position",
            [.text(scope)]
        ) { c in
            listsByPlaylist[try c.text(0), default: [:]][try c.text(1), default: []].append(try c.text(2))
        }

        var playlists: [MJ] = []
        playlists.reserveCapacity(playlistHeaders.count)
        for header in playlistHeaders {
            var fields: [(String, MJ)] = [
                ("id", .string(header.pid)),
                ("at", .number(header.at)),
                ("title", .string(header.title)),
                ("entries", .array(entriesByPlaylist[header.pid] ?? [])),
                ("currentId", header.currentID.map(MJ.string) ?? .null),
                ("priority", .strings(listsByPlaylist[header.pid]?["priority"] ?? [])),
                ("history", .strings(listsByPlaylist[header.pid]?["history"] ?? [])),
                ("source", header.source?.toMJ() ?? .null)
            ]
            if let context = header.context.toMJ() {
                fields.append(("context", context))
            }
            playlists.append(.obj(fields))
        }

        var songsByDay: [String: [MJ]] = [:]
        try db.scan(
            "SELECT \(daySongSelectColumns) FROM music_history_day_songs WHERE scope=? ORDER BY day_date,position",
            [.text(scope)]
        ) { c in
            let date = try c.text(0)
            var fields: [(String, MJ)] = [
                ("songId", .string(try c.text(1))),
                ("at", .number(try c.double(2)))
            ]
            if let context = try MusicPlayContextRecord.read(from: c, offset: 3).toMJ() {
                fields.append(("context", context))
            }
            songsByDay[date, default: []].append(.obj(fields))
        }

        var days: [MJ] = []
        days.reserveCapacity(dayDates.count)
        for date in dayDates {
            days.append(.obj([
                ("date", .string(date)),
                ("songs", .array(songsByDay[date] ?? []))
            ]))
        }

        return .obj([
            ("schema", .n(1)),
            ("playlists", .array(playlists)),
            ("days", .array(days))
        ])
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MJ) throws {
        let previous = try read(db, scope: scope)

        // Row-level incremental diff for playlists
        var prevPlaylistsByID: [String: (pos: Int, pl: MJ)] = [:]
        if let prevPls = previous?["playlists"].array {
            for (idx, pl) in prevPls.enumerated() { prevPlaylistsByID[pl["id"].string] = (idx, pl) }
        }
        let nextPlaylists = value["playlists"].array
        let nextPIDs = Set(nextPlaylists.map { $0["id"].string })
        for oldPID in prevPlaylistsByID.keys where !nextPIDs.contains(oldPID) {
            try db.execute("DELETE FROM music_history_playlists WHERE scope=? AND playlist_id=?", [.text(scope), .text(oldPID)])
        }

        for (index, playlist) in nextPlaylists.enumerated() {
            let pid = playlist["id"].string
            let old = prevPlaylistsByID[pid]
            if let old, old.pos == index, MusicPersistenceHelpers.semanticEquals(playlist, old.pl) {
                continue
            }

            let sourceRec = MusicSourceRecord.from(playlist["source"]) ?? .empty
            let ctxRec = MusicPlayContextRecord.from(playlist["context"])
            let vals: [SQLiteValue] = [
                .text(scope),
                .text(pid),
                .integer(index),
                .real(MusicPersistenceHelpers.finiteDouble(playlist["at"], fallback: 0)),
                .text(playlist["title"].string),
                MusicPersistenceHelpers.optionalNonEmpty(playlist["currentId"])
            ] + sourceRec.sqliteValues + ctxRec.sqliteValues
            try db.upsert("music_history_playlists", columns: playlistColumns, values: vals, key: ["scope", "playlist_id"])

            if old == nil || !MusicPersistenceHelpers.semanticEquals(playlist["entries"], old!.pl["entries"]) {
                var prevEntries: [String: (pos: Int, val: MJ)] = [:]
                if let oldArr = old?.pl["entries"].array {
                    for (idx, e) in oldArr.enumerated() { prevEntries[e["id"].string] = (idx, e) }
                }
                let nextArr = playlist["entries"].array
                let nextEIDs = Set(nextArr.map { $0["id"].string })
                for oldEID in prevEntries.keys where !nextEIDs.contains(oldEID) {
                    try db.execute(
                        "DELETE FROM music_history_playlist_entries WHERE scope=? AND playlist_id=? AND entry_id=?",
                        [.text(scope), .text(pid), .text(oldEID)]
                    )
                }
                for (eIndex, entry) in nextArr.enumerated() {
                    let eid = entry["id"].string
                    if let prevE = prevEntries[eid], prevE.pos == eIndex, MusicPersistenceHelpers.semanticEquals(entry, prevE.val) {
                        continue
                    }
                    let entryVals: [SQLiteValue] = [
                        .text(scope), .text(pid), .text(eid), .integer(eIndex), .text(entry["songId"].string)
                    ] + MusicPlayContextRecord.from(entry["context"]).sqliteValues
                    try db.upsert("music_history_playlist_entries", columns: playlistEntryColumns,
                                  values: entryVals, key: ["scope", "playlist_id", "entry_id"])
                }
            }

            for kind in ["priority", "history"] {
                let nextList = playlist[kind].strings
                let prevList = old?.pl[kind].strings ?? []
                if old != nil && prevList == nextList { continue }
                let prefix = old == nil ? 0 : MusicPersistenceHelpers.commonPrefixLength(prevList, nextList)
                if old != nil && prefix < prevList.count {
                    try db.execute(
                        "DELETE FROM music_history_playlist_lists WHERE scope=? AND playlist_id=? AND list_kind=? AND position>=?",
                        [.text(scope), .text(pid), .text(kind), .integer(prefix)]
                    )
                }
                for pos in prefix..<nextList.count {
                    try db.execute(
                        "INSERT INTO music_history_playlist_lists(scope,playlist_id,list_kind,position,item_value) VALUES(?,?,?,?,?)",
                        [.text(scope), .text(pid), .text(kind), .integer(pos), .text(nextList[pos])]
                    )
                }
            }
        }

        // Row-level incremental diff for days
        var prevDaysByDate: [String: (pos: Int, day: MJ)] = [:]
        if let prevDays = previous?["days"].array {
            for (idx, d) in prevDays.enumerated() { prevDaysByDate[d["date"].string] = (idx, d) }
        }
        let nextDays = value["days"].array
        let nextDates = Set(nextDays.map { $0["date"].string })
        for oldDate in prevDaysByDate.keys where !nextDates.contains(oldDate) {
            try db.execute("DELETE FROM music_history_days WHERE scope=? AND day_date=?", [.text(scope), .text(oldDate)])
        }

        for (index, day) in nextDays.enumerated() {
            let date = day["date"].string
            let old = prevDaysByDate[date]
            if let old, old.pos == index, MusicPersistenceHelpers.semanticEquals(day, old.day) {
                continue
            }

            let dayID = day["id"].string.isEmpty ? date : day["id"].string
            let updatedAt: Double
            if day["updatedAt"].isNumber {
                updatedAt = day["updatedAt"].number
            } else {
                updatedAt = MusicPersistenceHelpers.finiteDouble(day["songs"].array.first?["at"] ?? .null, fallback: 0)
            }
            try db.upsert(
                "music_history_days",
                columns: ["scope", "day_date", "position", "day_id", "updated_at"],
                values: [.text(scope), .text(date), .integer(index), .text(dayID), .real(updatedAt)],
                key: ["scope", "day_date"]
            )

            if old == nil || !MusicPersistenceHelpers.semanticEquals(day["songs"], old!.day["songs"]) {
                var prevSongs: [String: (pos: Int, val: MJ)] = [:]
                if let oldSongs = old?.day["songs"].array {
                    for (idx, s) in oldSongs.enumerated() { prevSongs[s["songId"].string] = (idx, s) }
                }
                let nextSongs = day["songs"].array
                let nextSongIDs = Set(nextSongs.map { $0["songId"].string })
                for oldSID in prevSongs.keys where !nextSongIDs.contains(oldSID) {
                    try db.execute(
                        "DELETE FROM music_history_day_songs WHERE scope=? AND day_date=? AND song_id=?",
                        [.text(scope), .text(date), .text(oldSID)]
                    )
                }
                for (sIndex, item) in nextSongs.enumerated() {
                    let sid = item["songId"].string
                    if let prevS = prevSongs[sid], prevS.pos == sIndex, MusicPersistenceHelpers.semanticEquals(item, prevS.val) {
                        continue
                    }
                    let songVals: [SQLiteValue] = [
                        .text(scope), .text(date), .text(sid), .integer(sIndex),
                        .real(MusicPersistenceHelpers.finiteDouble(item["at"], fallback: 0))
                    ] + MusicPlayContextRecord.from(item["context"]).sqliteValues
                    try db.upsert("music_history_day_songs", columns: daySongColumns,
                                  values: songVals, key: ["scope", "day_date", "song_id"])
                }
            }
        }
    }
}

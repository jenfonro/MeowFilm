import Foundation
import MeowStorage

enum MusicHistoryAreaStore {
    private static let playlistInsertSQL: String = {
        let cols = ["scope", "playlist_id", "position", "played_at", "title", "current_id"] +
            MusicPersistenceHelpers.sourceColumns(prefix: "source_") +
            MusicPersistenceHelpers.contextColumns
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO music_history_playlists(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    private static let playlistEntryInsertSQL: String = {
        let cols = ["scope", "playlist_id", "entry_id", "position", "song_id"] +
            MusicPersistenceHelpers.contextColumns
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO music_history_playlist_entries(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    private static let daySongInsertSQL: String = {
        let cols = ["scope", "day_date", "song_id", "position", "played_at"] +
            MusicPersistenceHelpers.contextColumns
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT INTO music_history_day_songs(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        let playlistRows = try db.query(
            "SELECT * FROM music_history_playlists WHERE scope=? ORDER BY position",
            [.text(scope)]
        )
        let dayRows = try db.query(
            "SELECT day_date,day_id,updated_at FROM music_history_days WHERE scope=? ORDER BY position",
            [.text(scope)]
        )
        if playlistRows.isEmpty && dayRows.isEmpty { return nil }

        var entriesByPlaylist: [String: [MJ]] = [:]
        for row in try db.query(
            "SELECT * FROM music_history_playlist_entries WHERE scope=? ORDER BY playlist_id,position",
            [.text(scope)]
        ) {
            let pid = try row.text("playlist_id")
            var fields: [(String, MJ)] = [
                ("id", .string(try row.text("entry_id"))),
                ("songId", .string(try row.text("song_id")))
            ]
            if let context = try MusicPersistenceHelpers.readContext(row) {
                fields.append(("context", context))
            }
            entriesByPlaylist[pid, default: []].append(.obj(fields))
        }

        var listsByPlaylist: [String: [String: [String]]] = [:]
        for row in try db.query(
            "SELECT playlist_id,list_kind,item_value FROM music_history_playlist_lists " +
            "WHERE scope=? ORDER BY playlist_id,list_kind,position",
            [.text(scope)]
        ) {
            let pid = try row.text("playlist_id")
            let kind = try row.text("list_kind")
            listsByPlaylist[pid, default: [:]][kind, default: []].append(try row.text("item_value"))
        }

        var playlists: [MJ] = []
        playlists.reserveCapacity(playlistRows.count)
        for row in playlistRows {
            let pid = try row.text("playlist_id")
            var fields: [(String, MJ)] = [
                ("id", .string(pid)),
                ("at", .number(try row.double("played_at"))),
                ("title", .string(try row.text("title"))),
                ("entries", .array(entriesByPlaylist[pid] ?? [])),
                ("currentId", (try row.optionalText("current_id")).map(MJ.string) ?? .null),
                ("priority", .strings(listsByPlaylist[pid]?["priority"] ?? [])),
                ("history", .strings(listsByPlaylist[pid]?["history"] ?? [])),
                ("source", try MusicPersistenceHelpers.readSource(row, prefix: "source_"))
            ]
            if let context = try MusicPersistenceHelpers.readContext(row) {
                fields.append(("context", context))
            }
            playlists.append(.obj(fields))
        }

        var songsByDay: [String: [MJ]] = [:]
        for row in try db.query(
            "SELECT * FROM music_history_day_songs WHERE scope=? ORDER BY day_date,position",
            [.text(scope)]
        ) {
            let date = try row.text("day_date")
            var fields: [(String, MJ)] = [
                ("songId", .string(try row.text("song_id"))),
                ("at", .number(try row.double("played_at")))
            ]
            if let context = try MusicPersistenceHelpers.readContext(row) {
                fields.append(("context", context))
            }
            songsByDay[date, default: []].append(.obj(fields))
        }

        var days: [MJ] = []
        days.reserveCapacity(dayRows.count)
        for row in dayRows {
            let date = try row.text("day_date")
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
        // Cascades to music_history_playlist_entries and music_history_playlist_lists.
        try db.execute("DELETE FROM music_history_playlists WHERE scope=?", [.text(scope)])
        for (index, playlist) in value["playlists"].array.enumerated() {
            let pid = playlist["id"].string
            let vals: [SQLiteValue] = [
                .text(scope),
                .text(pid),
                .integer(index),
                .real(MusicPersistenceHelpers.finiteDouble(playlist["at"], fallback: 0)),
                .text(playlist["title"].string),
                MusicPersistenceHelpers.optionalNonEmpty(playlist["currentId"])
            ] + MusicPersistenceHelpers.bindSource(playlist["source"])
              + MusicPersistenceHelpers.bindContext(playlist["context"])
            try db.execute(playlistInsertSQL, vals)

            for (eIndex, entry) in playlist["entries"].array.enumerated() {
                let entryVals: [SQLiteValue] = [
                    .text(scope), .text(pid), .text(entry["id"].string), .integer(eIndex), .text(entry["songId"].string)
                ] + MusicPersistenceHelpers.bindContext(entry["context"])
                try db.execute(playlistEntryInsertSQL, entryVals)
            }
            for kind in ["priority", "history"] {
                for (pos, item) in playlist[kind].strings.enumerated() {
                    try db.execute(
                        "INSERT INTO music_history_playlist_lists(scope,playlist_id,list_kind,position,item_value) " +
                        "VALUES(?,?,?,?,?)",
                        [.text(scope), .text(pid), .text(kind), .integer(pos), .text(item)]
                    )
                }
            }
        }

        // Cascades to music_history_day_songs.
        try db.execute("DELETE FROM music_history_days WHERE scope=?", [.text(scope)])
        for (index, day) in value["days"].array.enumerated() {
            let date = day["date"].string
            let dayID = day["id"].string.isEmpty ? date : day["id"].string
            let updatedAt: Double
            if day["updatedAt"].isNumber {
                updatedAt = day["updatedAt"].number
            } else {
                updatedAt = MusicPersistenceHelpers.finiteDouble(day["songs"].array.first?["at"] ?? .null, fallback: 0)
            }
            try db.execute(
                "INSERT INTO music_history_days(scope,day_date,position,day_id,updated_at) VALUES(?,?,?,?,?)",
                [.text(scope), .text(date), .integer(index), .text(dayID), .real(updatedAt)]
            )
            for (sIndex, item) in day["songs"].array.enumerated() {
                let songVals: [SQLiteValue] = [
                    .text(scope), .text(date), .text(item["songId"].string), .integer(sIndex),
                    .real(MusicPersistenceHelpers.finiteDouble(item["at"], fallback: 0))
                ] + MusicPersistenceHelpers.bindContext(item["context"])
                try db.execute(daySongInsertSQL, songVals)
            }
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowStorage

#if os(iOS) || os(macOS)
enum SearchHistoryRepository {
    static func load(_ db: SQLiteConnection) throws -> [SearchHistoryEntry] {
        try db.query("SELECT text FROM ios_search_history ORDER BY used_at DESC, text") { cursor in
            SearchHistoryEntry(text: try cursor.text(0))
        }
    }

    static func record(_ keyword: String, _ db: SQLiteConnection) throws -> [SearchHistoryEntry] {
        try db.execute(
            "INSERT INTO ios_search_history(text, used_at) VALUES(?, ?) " +
            "ON CONFLICT(text) DO UPDATE SET used_at=excluded.used_at",
            [.text(keyword), .optional(Date())]
        )
        return try load(db)
    }

    static func clear(_ db: SQLiteConnection) throws {
        try db.execute("DELETE FROM ios_search_history")
    }
}
#endif

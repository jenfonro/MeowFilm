// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowStorage

#if os(iOS) || os(macOS)
enum SearchHistoryRepository {
    private static let scopeKey = "device"
    private static let kindKey = "default"

    static func load(_ db: SQLiteConnection) throws -> [SearchHistoryEntry] {
        try db.query(
            "SELECT query_text FROM music_search_history WHERE scope=? AND kind=? ORDER BY used_at_ms DESC, query_text",
            [.text(scopeKey), .text(kindKey)]
        ) { cursor in
            SearchHistoryEntry(text: try cursor.text(0))
        }
    }

    static func record(_ keyword: String, _ db: SQLiteConnection) throws -> [SearchHistoryEntry] {
        try db.execute(
            "INSERT INTO music_search_history(scope, kind, query_text, position, used_at_ms) VALUES(?, ?, ?, 0, ?) " +
            "ON CONFLICT(scope, kind, query_text) DO UPDATE SET used_at_ms=excluded.used_at_ms",
            [.text(scopeKey), .text(kindKey), .text(keyword), .epochMillis(Date())]
        )
        return try load(db)
    }

    static func clear(_ db: SQLiteConnection) throws {
        try db.execute(
            "DELETE FROM music_search_history WHERE scope=? AND kind=?",
            [.text(scopeKey), .text(kindKey)]
        )
    }
}
#endif

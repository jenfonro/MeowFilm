import CryptoKit
import Foundation
import MeowStorage

enum MusicPlayContextStore {
    private static let insertSQL: String = {
        let cols = [
            "context_id",
            "collection_id", "playlist_id", "source_title", "playlist",
            "source_type", "source_id", "source_platform", "platform",
            "source_label", "search_keyword", "daily_branch",
            "heartbeat_playlist_id", "heartbeat_account_id", "provider", "remote_session",
            "queue_source_id", "queue_source_title", "queue_source_kind", "queue_source_platform",
            "similar_batch_id", "similar_based_on_song_id", "similar_provider"
        ]
        let placeholders = Array(repeating: "?", count: cols.count).joined(separator: ",")
        return "INSERT OR IGNORE INTO music_play_contexts(\(cols.joined(separator: ","))) VALUES(\(placeholders))"
    }()

    private static let selectColumns =
        "context_id,collection_id,playlist_id,source_title,playlist," +
        "source_type,source_id,source_platform,platform," +
        "source_label,search_keyword,daily_branch," +
        "heartbeat_playlist_id,heartbeat_account_id,provider,remote_session," +
        "queue_source_id,queue_source_title,queue_source_kind,queue_source_platform," +
        "similar_batch_id,similar_based_on_song_id,similar_provider"

    static func contextID(for context: MusicPlayContextRecord) -> String? {
        guard !context.isEmpty else { return nil }
        var hasher = SHA256()
        for field in context.orderedFields {
            if let field {
                hasher.update(data: Data([1]))
                hasher.update(data: Data(field.utf8))
                hasher.update(data: Data([0]))
            } else {
                hasher.update(data: Data([0]))
            }
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    static func upsert(_ db: SQLiteConnection, context: MusicPlayContextRecord) throws -> String? {
        guard let id = contextID(for: context) else { return nil }
        try db.execute(insertSQL, [.text(id)] + context.sqliteValues)
        return id
    }

    static func loadMap(_ db: SQLiteConnection, ids: Set<String>) throws -> [String: MusicPlayContextRecord] {
        guard !ids.isEmpty else { return [:] }
        var map: [String: MusicPlayContextRecord] = [:]
        map.reserveCapacity(ids.count)
        let values = Array(ids)
        for start in stride(from: 0, to: values.count, by: 500) {
            let batch = Array(values[start..<min(start + 500, values.count)])
            let placeholders = Array(repeating: "?", count: batch.count).joined(separator: ",")
            try db.scan(
                "SELECT \(selectColumns) FROM music_play_contexts WHERE context_id IN (\(placeholders))",
                batch.map(SQLiteValue.text)
            ) { cursor in
                let id = try cursor.text(0)
                map[id] = try MusicPlayContextRecord.read(from: cursor, offset: 1)
            }
        }
        return map
    }

    static func pruneOrphans(_ db: SQLiteConnection) throws {
        try db.execute(
            "DELETE FROM music_play_contexts WHERE context_id NOT IN (" +
            "SELECT context_id FROM music_queue_entries WHERE context_id IS NOT NULL " +
            "UNION SELECT context_id FROM ios_music_queue_similar_return_entries WHERE context_id IS NOT NULL " +
            "UNION SELECT context_id FROM music_history_playlists WHERE context_id IS NOT NULL " +
            "UNION SELECT context_id FROM music_history_playlist_entries WHERE context_id IS NOT NULL " +
            "UNION SELECT context_id FROM music_history_day_songs WHERE context_id IS NOT NULL)"
        )
    }
}

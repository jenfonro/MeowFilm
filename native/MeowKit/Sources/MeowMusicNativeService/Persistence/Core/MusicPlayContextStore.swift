import CryptoKit
import Foundation
import MeowStorage

enum MusicPlayContextStore {
    private static let insertSharedSQL =
        "INSERT OR IGNORE INTO music_play_contexts(" +
        "context_id,source_title,playlist,source_type,source_id,search_keyword,daily_branch" +
        ") VALUES(?,?,?,?,?,?,?)"

    private static let insertExtrasSQL =
        "INSERT OR IGNORE INTO ios_music_play_context_extras(" +
        "context_id,collection_id,platform,provider,remote_session," +
        "similar_batch_id,similar_based_on_song_id,similar_provider" +
        ") VALUES(?,?,?,?,?,?,?,?)"

    private static let selectColumns =
        "c.context_id,c.source_title,c.playlist,c.source_type,c.source_id,c.search_keyword,c.daily_branch," +
        "e.collection_id,e.platform,e.provider,e.remote_session," +
        "e.similar_batch_id,e.similar_based_on_song_id,e.similar_provider"

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
        try db.execute(insertSharedSQL, [.text(id)] + context.sharedSQLiteValues)
        if context.hasIOSExtras {
            try db.execute(insertExtrasSQL, [.text(id)] + context.extrasSQLiteValues)
        }
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
                "SELECT \(selectColumns) FROM music_play_contexts c " +
                "LEFT JOIN ios_music_play_context_extras e ON e.context_id=c.context_id " +
                "WHERE c.context_id IN (\(placeholders))",
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
            "UNION SELECT context_id FROM ios_music_history_playlist_extras WHERE context_id IS NOT NULL " +
            "UNION SELECT context_id FROM music_history_playlist_entries WHERE context_id IS NOT NULL " +
            "UNION SELECT context_id FROM music_history_day_songs WHERE context_id IS NOT NULL)"
        )
    }
}

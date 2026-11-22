import Foundation
import MeowStorage

enum PlaybackVolumeRepository {
    static func loadAll(_ db: SQLiteConnection) throws -> [String: Double] {
        var result: [String: Double] = [:]
        try db.scan("SELECT owner, volume FROM ios_playback_volume") { cursor in
            result[try cursor.text(0)] = try cursor.double(1)
        }
        return result
    }

    static func saveBatch(_ batch: [String: Double], _ db: SQLiteConnection) throws {
        for (owner, volume) in batch {
            try db.upsert(
                "ios_playback_volume",
                columns: ["owner", "volume"],
                values: [.text(owner), .real(volume)],
                key: ["owner"]
            )
        }
    }
}

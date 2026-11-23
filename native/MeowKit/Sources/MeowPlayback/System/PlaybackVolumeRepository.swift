import Foundation
import MeowStorage

enum PlaybackVolumeRepository {
    static func loadAll(_ db: SQLiteConnection) throws -> [String: Double] {
        var result: [String: Double] = [:]
        try db.scan("SELECT owner, volume FROM playback_volume") { cursor in
            result[try cursor.text(0)] = try cursor.double(1)
        }
        return result
    }

    static func saveBatch(_ batch: [String: Double], _ db: SQLiteConnection) throws {
        for (owner, volume) in batch {
            try db.execute(
                "INSERT INTO playback_volume(owner, revision, volume) VALUES(?, 1, ?) " +
                "ON CONFLICT(owner) DO UPDATE SET volume=excluded.volume, revision=playback_volume.revision+1",
                [.text(owner), .real(volume)]
            )
        }
    }
}

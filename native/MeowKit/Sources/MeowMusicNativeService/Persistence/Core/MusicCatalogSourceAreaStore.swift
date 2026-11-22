import Foundation
import MeowStorage

enum MusicCatalogSourceAreaStore {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicCatalogSourceAreaRecord? {
        try db.queryOne(
            "SELECT revision,platform FROM music_catalog_source WHERE scope=?",
            [.text(scope)],
            map: { c in
                MusicCatalogSourceAreaRecord(
                    revision: try c.integer(0),
                    platform: try c.text(1)
                )
            }
        )
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MusicCatalogSourceAreaRecord) throws {
        let platform = value.platform.isEmpty ? "wy" : value.platform
        try db.upsert(
            "music_catalog_source",
            columns: ["scope", "revision", "platform"],
            values: [.text(scope), .integer(value.revision), .text(platform)],
            key: ["scope"]
        )
    }
}

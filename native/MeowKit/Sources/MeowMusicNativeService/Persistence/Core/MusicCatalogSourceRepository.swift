import Foundation
import MeowStorage

enum MusicCatalogSourceRepository {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MusicCatalogSourceState? {
        try db.queryOne(
            "SELECT revision,platform FROM music_catalog_source WHERE scope=?",
            [.text(scope)],
            map: { c in
                MusicCatalogSourceState(
                    revision: try c.integer(0),
                    platform: try c.text(1)
                )
            }
        )
    }

    static func save(_ db: SQLiteConnection, scope: String, value: MusicCatalogSourceState) throws {
        let platform = value.platform.isEmpty ? "wy" : value.platform
        try db.upsert(
            "music_catalog_source",
            columns: ["scope", "revision", "platform"],
            values: [.text(scope), .integer(value.revision), .text(platform)],
            key: ["scope"]
        )
    }
}

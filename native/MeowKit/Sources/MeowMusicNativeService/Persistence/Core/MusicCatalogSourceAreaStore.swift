import Foundation
import MeowStorage

enum MusicCatalogSourceAreaStore {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let row = try db.query(
            "SELECT revision,platform FROM ios_music_catalog_source WHERE scope=?",
            [.text(scope)]
        ).first else { return nil }
        return .obj([
            ("schema", .n(1)),
            ("revision", .n(try row.integer("revision"))),
            ("platform", .string(try row.text("platform")))
        ])
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MJ) throws {
        let revision = value["revision"].int
        let platform = value["platform"].string.isEmpty ? "wy" : value["platform"].string
        try db.upsert(
            "ios_music_catalog_source",
            columns: ["scope", "revision", "platform"],
            values: [.text(scope), .integer(revision), .text(platform)],
            key: ["scope"]
        )
    }
}

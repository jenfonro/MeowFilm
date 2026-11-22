import Foundation
import MeowStorage

enum MusicCatalogSourceAreaStore {
    static func read(_ db: SQLiteConnection, scope: String) throws -> MJ? {
        guard let row = try db.queryOne(
            "SELECT revision,platform FROM music_catalog_source WHERE scope=?",
            [.text(scope)],
            map: { (revision: try $0.integer(0), platform: try $0.text(1)) }
        ) else { return nil }
        return .obj([
            ("schema", .n(1)),
            ("revision", .n(row.revision)),
            ("platform", .string(row.platform))
        ])
    }

    static func write(_ db: SQLiteConnection, scope: String, value: MJ) throws {
        let revision = value["revision"].int
        let platform = value["platform"].string.isEmpty ? "wy" : value["platform"].string
        try db.upsert(
            "music_catalog_source",
            columns: ["scope", "revision", "platform"],
            values: [.text(scope), .integer(revision), .text(platform)],
            key: ["scope"]
        )
    }
}

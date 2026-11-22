import Foundation
import MeowStorage

enum MusicChannelSelectionRepository {
    static func load(_ db: SQLiteConnection) throws -> String? {
        try db.queryOne("SELECT channel_key FROM ios_music_channel_selection WHERE singleton=1") { try $0.text(0) }
    }

    static func save(_ channelKey: String, _ db: SQLiteConnection) throws {
        try db.upsert(
            "ios_music_channel_selection",
            columns: ["singleton", "channel_key"],
            values: [.integer(1), .text(channelKey)],
            key: ["singleton"]
        )
    }
}

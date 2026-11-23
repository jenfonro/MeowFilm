import Foundation
import MeowMusicFeature
import MeowStorage

/// Disposable exact-recording metadata in the evictable cache database. No URL,
/// source credentials, account UID or trial offsets. A cache hit still requires a live local-scope fence.
struct MusicAccountChorusCache {
    struct Key: Equatable {
        let scope: String
        let platform: String
        let nativeID: String
        let duration: Double
    }
    struct Entry { let key: Key; let point: MusicChorusPoint; let expires: Date }
    private let database: SQLiteDatabase?
    private var loaded = false
    private var entries: [Entry] = []
    init(database: SQLiteDatabase?) { self.database = database }
    mutating func value(_ key: Key, actualDuration: Double, at now: Date) -> MusicChorusPoint? {
        load(at: now)
        return entries.first { $0.key == key &&
            $0.point.fits(recordingDuration: key.duration, actualDuration: actualDuration) }?.point
    }
    mutating func put(_ point: MusicChorusPoint, key: Key, at now: Date) {
        load(at: now)
        let expires = now.addingTimeInterval(30 * 86_400)
        entries.removeAll { $0.key == key }
        entries.append(.init(key: key, point: point, expires: expires))
        entries = Array(entries.suffix(100))
        if let database {
            try? database.transactionSync { db in
                try db.upsert(
                    "cached_music_chorus",
                    columns: [
                        "scope", "platform", "native_id", "recording_duration",
                        "start_sec", "end_sec", "updated_at_ms", "expires_at_ms"
                    ],
                    values: [
                        .text(key.scope), .text(key.platform), .text(key.nativeID),
                        .real(key.duration), .real(point.start), .real(point.end),
                        .epochMillis(now), .epochMillis(expires)
                    ],
                    key: ["scope", "platform", "native_id", "recording_duration"]
                )
                try db.execute("DELETE FROM cached_music_chorus WHERE expires_at_ms<=?", [.epochMillis(now)])
                try db.execute(
                    "DELETE FROM cached_music_chorus WHERE rowid IN " +
                    "(SELECT rowid FROM cached_music_chorus ORDER BY expires_at_ms DESC LIMIT -1 OFFSET 100)"
                )
            }
        }
    }
    private mutating func load(at now: Date) {
        if !loaded {
            loaded = true
            if let database, let values = try? database.readSync({ db -> [Entry] in
                Array(try db.query(
                    "SELECT scope, platform, native_id, recording_duration, start_sec, end_sec, expires_at_ms " +
                    "FROM cached_music_chorus WHERE expires_at_ms>? ORDER BY expires_at_ms DESC LIMIT 100",
                    [.epochMillis(now)]
                ) { cursor in
                    Entry(
                        key: .init(
                            scope: try cursor.text(0),
                            platform: try cursor.text(1),
                            nativeID: try cursor.text(2),
                            duration: try cursor.double(3)
                        ),
                        point: .init(start: try cursor.double(4), end: try cursor.double(5)),
                        expires: try cursor.dateFromMillis(6)
                    )
                }.reversed())
            }) {
                entries = values
            }
        }
        entries.removeAll { $0.expires <= now || $0.expires > now.addingTimeInterval(30 * 86_400) ||
            !$0.point.fits(recordingDuration: $0.key.duration, actualDuration: $0.key.duration) }
    }
}

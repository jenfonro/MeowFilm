import Foundation
import MeowMusicFeature
import MeowStorage

/// Disposable exact-recording metadata. No URL, source credentials, account
/// UID or trial offsets. A cache hit still requires a live local-scope fence.
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
        entries.removeAll { $0.key == key }
        entries.append(.init(key: key, point: point, expires: now.addingTimeInterval(30 * 86_400)))
        entries = Array(entries.suffix(100))
        if let database {
            try? database.transactionSync { db in
                try db.upsert("ios_music_chorus", columns: ["scope", "platform", "native_id", "recording_duration", "start", "end", "expires_at"],
                    values: [.text(key.scope), .text(key.platform), .text(key.nativeID), .real(key.duration),
                             .real(point.start), .real(point.end), .optional(now.addingTimeInterval(30 * 86_400))],
                    key: ["scope", "platform", "native_id", "recording_duration"])
                try db.execute("DELETE FROM ios_music_chorus WHERE expires_at<=?", [.optional(now)])
                try db.execute("DELETE FROM ios_music_chorus WHERE (scope,platform,native_id,recording_duration) IN " +
                    "(SELECT scope,platform,native_id,recording_duration FROM ios_music_chorus ORDER BY expires_at DESC LIMIT -1 OFFSET 100)")
            }
        }
    }
    private mutating func load(at now: Date) {
        if !loaded {
            loaded = true
            if let database, let values = try? database.readSync({ db in
                try db.query("SELECT * FROM ios_music_chorus ORDER BY expires_at LIMIT 100").map { row in
                    Entry(key: .init(scope: try row.text("scope"), platform: try row.text("platform"),
                                    nativeID: try row.text("native_id"), duration: try row.double("recording_duration")),
                          point: .init(start: try row.double("start"), end: try row.double("end")),
                          expires: Date(timeIntervalSince1970: try row.double("expires_at")))
                }
            }) {
                entries = values
            }
        }
        entries.removeAll { $0.expires <= now || $0.expires > now.addingTimeInterval(30 * 86_400) ||
            !$0.point.fits(recordingDuration: $0.key.duration, actualDuration: $0.key.duration) }
    }
}

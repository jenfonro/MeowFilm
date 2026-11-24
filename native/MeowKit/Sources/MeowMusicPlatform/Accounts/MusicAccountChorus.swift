import Foundation
import MeowDebug
import MeowMusicFeature
import MeowStorage

/// Disposable exact-recording chorus cache in the evictable cache database (max 100 entries, 30-day TTL).
struct MusicAccountChorusCache {
    struct Key: Equatable {
        let scope: String
        let platform: String
        let nativeID: String
        let duration: Double
    }
    struct Entry { let key: Key; let point: MusicChorusPoint; let expires: Date }

    private static let maximumEntries = 100
    private static let lifetime: TimeInterval = 30 * 86_400
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
        let expires = now.addingTimeInterval(Self.lifetime)
        entries.removeAll { $0.key == key }
        entries.append(.init(key: key, point: point, expires: expires))
        entries = Array(entries.suffix(Self.maximumEntries))
        if let database {
            do {
                try database.transactionSync { db in
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
                        "(SELECT rowid FROM cached_music_chorus ORDER BY expires_at_ms DESC LIMIT -1 OFFSET ?)",
                        [.integer(Self.maximumEntries)]
                    )
                }
            } catch {
                DebugRuntime.logError(.storage, sub: "music.chorusCache", event: "put.error", error: error)
            }
        }
    }

    /// Explicitly clears cached chorus rows (all or for a specific platform). Propagates SQLite failures.
    mutating func clear(platform: MusicAccountPlatform? = nil) throws {
        if let platform {
            entries.removeAll { $0.key.platform == platform.rawValue }
            if let database {
                try database.transactionSync { db in
                    try db.execute("DELETE FROM cached_music_chorus WHERE platform=?", [.text(platform.rawValue)])
                }
            }
        } else {
            entries.removeAll()
            loaded = true
            if let database {
                try database.transactionSync { db in
                    try db.execute("DELETE FROM cached_music_chorus")
                }
            }
        }
    }

    func debugStats() -> DebugJSONValue {
        let dbCount: Int = (try? database?.readSync { db in
            try db.queryOne("SELECT count(*) FROM cached_music_chorus") { try $0.integer(0) } ?? 0
        }) ?? entries.count
        return .object([
            "entryCount": .int(dbCount),
            "memoryEntryCount": .int(entries.count),
            "maximumEntries": .int(Self.maximumEntries)
        ])
    }

    private mutating func load(at now: Date) {
        if !loaded {
            loaded = true
            if let database {
                do {
                    let values = try database.readSync { db -> [Entry] in
                        Array(try db.query(
                            "SELECT scope, platform, native_id, recording_duration, start_sec, end_sec, expires_at_ms " +
                            "FROM cached_music_chorus WHERE expires_at_ms>? ORDER BY expires_at_ms DESC LIMIT ?",
                            [.epochMillis(now), .integer(Self.maximumEntries)]
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
                    }
                    entries = values
                } catch {
                    DebugRuntime.logError(.storage, sub: "music.chorusCache", event: "load.error", error: error)
                }
            }
        }
        entries.removeAll { $0.expires <= now ||
            !$0.point.fits(recordingDuration: $0.key.duration, actualDuration: $0.key.duration) }
    }
}

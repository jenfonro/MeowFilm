import Foundation
import MeowDebug
import MeowMusicFeature
import MeowStorage

struct MusicCachedAudio: Equatable, Sendable {
    let url: URL
    let full: Bool?
    let actualQuality: String?
    let bitrate: Double?
    let sampleRate: Double?
    let duration: Double?
    let expiresAtMs: Double?
    let licenseExpiresAtMs: Double?

    var expiresAt: Date? {
        expiresAtMs.map { Date(timeIntervalSince1970: $0 / 1000) }
    }

    var estimatedBytes: Int {
        var size = 128 + 3 + url.absoluteString.utf8.count
        if full != nil { size += 4 + 16 }
        if let actualQuality { size += 13 + actualQuality.utf8.count }
        if bitrate != nil { size += 7 + 16 }
        if sampleRate != nil { size += 10 + 16 }
        if duration != nil { size += 8 + 16 }
        if expiresAtMs != nil { size += 9 + 16 }
        if licenseExpiresAtMs != nil { size += 16 + 16 }
        return size
    }
}

/// Actor-confined disposable URL cache (max 4 scopes, 50 candidates/scope, 1 MiB, 30-day retention).
struct MusicNativeAccountURLCache {
    static let maximumBytes = 1_024 * 1_024
    static let retention: TimeInterval = 30 * 86_400

    struct Key: Hashable, Sendable {
        let scope: String
        let platform: String
        let nativeID: String
        let quality: String
    }

    private struct Entry: Equatable {
        let key: Key
        let record: MusicCachedAudio
        let savedAt: Date
        func usable(at now: Date) -> Bool {
            guard MusicNativeAccountURLCache.validateCandidate(record) else { return false }
            return savedAt <= now && savedAt.addingTimeInterval(MusicNativeAccountURLCache.retention) > now &&
                (record.expiresAt == nil || record.expiresAt!.addingTimeInterval(-10) > now)
        }
    }

    private let database: SQLiteDatabase?
    private static let columns = [
        "scope", "platform", "native_id", "quality", "url", "full", "actual_quality", "bitrate",
        "sample_rate", "duration", "expires_at_ms", "license_expires_at_ms", "saved_at_ms"
    ]
    private static let keyColumns = ["scope", "platform", "native_id", "quality"]
    private var loaded = false
    private var entries: [Entry] = []
    private var persisted: [Key: Entry] = [:]
    private var rejected: [Key: [(url: URL, at: Date)]] = [:]

    init(database: SQLiteDatabase?) { self.database = database }

    private static let forbiddenQueryParams: Set<String> = [
        "cookie", "authorization", "music_u", "music_a", "musickey",
        "p_skey", "skey", "p_uin", "access_token", "refresh_token"
    ]

    static func validateURL(_ url: URL) -> Bool {
        guard ["https", "http"].contains(url.scheme?.lowercased() ?? ""),
              let host = url.host, !host.isEmpty,
              url.user == nil, url.password == nil, url.fragment == nil,
              !url.absoluteString.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            return false
        }
        return !(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? [])
            .contains(where: { forbiddenQueryParams.contains($0.name.lowercased()) })
    }

    private static func validateCandidate(_ record: MusicCachedAudio) -> Bool {
        guard validateURL(record.url) else { return false }
        if let exp = record.expiresAtMs, !(exp.isFinite && exp > 0) { return false }
        if let lic = record.licenseExpiresAtMs, !(lic.isFinite && lic > 0) { return false }
        return true
    }

    private static func valid(_ key: Key) -> Bool {
        !key.scope.isEmpty && MusicAccountPlatform(rawValue: key.platform) != nil &&
            !key.nativeID.isEmpty && !key.quality.isEmpty
    }

    private mutating func load(at now: Date) {
        guard !loaded else { return }
        loaded = true
        do {
            guard let database else { return }
            let loadedEntries: [Entry] = try database.readSync { db in
                let cutoff = now.addingTimeInterval(-Self.retention)
                var list: [Entry] = []
                try db.scan(
                    "SELECT scope, platform, native_id, quality, url, full, actual_quality, bitrate, " +
                    "sample_rate, duration, expires_at_ms, license_expires_at_ms, saved_at_ms " +
                    "FROM ios_music_audio_urls " +
                    "WHERE saved_at_ms>? AND (expires_at_ms IS NULL OR expires_at_ms>?) " +
                    "ORDER BY saved_at_ms ASC",
                    [.epochMillis(cutoff), .epochMillis(now)]
                ) { cursor in
                    let key = Key(
                        scope: try cursor.text(0),
                        platform: try cursor.text(1),
                        nativeID: try cursor.text(2),
                        quality: try cursor.text(3)
                    )
                    guard Self.valid(key), let url = URL(string: try cursor.text(4)) else { return }
                    let record = MusicCachedAudio(
                        url: url,
                        full: try cursor.optionalBool(5),
                        actualQuality: try cursor.optionalText(6),
                        bitrate: try cursor.optionalDouble(7),
                        sampleRate: try cursor.optionalDouble(8),
                        duration: try cursor.optionalDouble(9),
                        expiresAtMs: try cursor.optionalInt64(10).map(Double.init),
                        licenseExpiresAtMs: try cursor.optionalInt64(11).map(Double.init)
                    )
                    let entry = Entry(key: key, record: record, savedAt: try cursor.dateFromMillis(12))
                    if entry.usable(at: now) { list.append(entry) }
                }
                return list
            }
            for entry in loadedEntries {
                entries.removeAll { $0.key == entry.key }
                entries.append(entry)
                persisted[entry.key] = entry
            }
            trim(at: now)
        } catch {}
    }

    mutating func value(for key: Key, at now: Date) -> MusicCachedAudio? {
        load(at: now); trim(at: now)
        guard let entry = entries.last(where: { $0.key == key }),
              !isRejected(entry.record.url, for: key, at: now) else { return nil }
        return entry.record
    }

    mutating func put(_ record: MusicCachedAudio, for key: Key, at now: Date) {
        load(at: now)
        guard Self.valid(key), Self.validateCandidate(record),
              !isRejected(record.url, for: key, at: now) else { return }
        let entry = Entry(key: key, record: record, savedAt: now)
        guard entry.usable(at: now) else { return }
        entries.removeAll { $0.key == key }; entries.append(entry)
        trim(at: now); persistDelta(at: now)
    }

    mutating func reject(_ url: URL, for key: Key, at now: Date) {
        load(at: now)
        trimRejections(at: now)
        var values = rejected[key, default: []].filter { $0.url != url }
        values.append((url, now)); rejected[key] = Array(values.suffix(8))
        while rejected.count > 200, let oldest = rejected.min(by: {
            ($0.value.last?.at ?? .distantPast) < ($1.value.last?.at ?? .distantPast)
        })?.key { rejected.removeValue(forKey: oldest) }
        entries.removeAll { $0.key == key && $0.record.url == url }
        trim(at: now); persistDelta(at: now)
    }

    mutating func isRejected(_ url: URL, for key: Key, at now: Date) -> Bool {
        trimRejections(at: now)
        return rejected[key]?.contains(where: { $0.url == url }) == true
    }

    /// Explicitly clears cached URLs (all or for a specific platform). Propagates SQLite failures.
    mutating func clear(platform: MusicAccountPlatform? = nil) throws {
        if let platform {
            entries.removeAll { $0.key.platform == platform.rawValue }
            persisted = persisted.filter { $0.key.platform != platform.rawValue }
            rejected = rejected.filter { $0.key.platform != platform.rawValue }
            if let database {
                try database.transactionSync { db in
                    try db.execute("DELETE FROM ios_music_audio_urls WHERE platform=?", [.text(platform.rawValue)])
                }
            }
        } else {
            entries.removeAll()
            persisted.removeAll()
            rejected.removeAll()
            loaded = true
            if let database {
                try database.transactionSync { db in
                    try db.execute("DELETE FROM ios_music_audio_urls")
                }
            }
        }
    }

    func debugStats() -> DebugJSONValue {
        let dbCount: Int = (try? database?.readSync { db in
            try db.queryOne("SELECT count(*) FROM ios_music_audio_urls") { try $0.integer(0) } ?? 0
        }) ?? entries.count
        let memBytes = entries.reduce(0) { $0 + Self.bytes($1) }
        return .object([
            "entryCount": .int(dbCount),
            "memoryEntryCount": .int(entries.count),
            "estimatedBytes": .int(memBytes),
            "rejectedCount": .int(rejected.count),
            "capacityBytes": .int(Self.maximumBytes)
        ])
    }

    private mutating func trimRejections(at now: Date) {
        for key in Array(rejected.keys) {
            rejected[key] = rejected[key]?.filter { $0.at <= now && $0.at.addingTimeInterval(120) > now }
            if rejected[key]?.isEmpty == true { rejected.removeValue(forKey: key) }
        }
    }

    private mutating func trim(at now: Date) {
        entries.removeAll { !$0.usable(at: now) }
        var scopes: [String] = []
        for entry in entries {
            scopes.removeAll { $0 == entry.key.scope }; scopes.append(entry.key.scope)
        }
        let retained = Set(scopes.suffix(4))
        entries.removeAll { !retained.contains($0.key.scope) }
        for scope in retained {
            while entries.filter({ $0.key.scope == scope }).count > 50 {
                if let index = entries.firstIndex(where: { $0.key.scope == scope }) { entries.remove(at: index) }
            }
        }
        var bytes = entries.reduce(0) { $0 + Self.bytes($1) }
        while bytes > Self.maximumBytes, !entries.isEmpty {
            bytes -= Self.bytes(entries.removeFirst())
        }
    }

    private static func bytes(_ entry: Entry) -> Int {
        let identity = entry.key.scope.utf8.count + entry.key.platform.utf8.count +
            entry.key.nativeID.utf8.count + entry.key.quality.utf8.count
        return identity + entry.record.estimatedBytes
    }

    private static func cells(_ entry: Entry) -> [SQLiteValue] {
        let r = entry.record
        return [
            .text(entry.key.scope),
            .text(entry.key.platform),
            .text(entry.key.nativeID),
            .text(entry.key.quality),
            .text(r.url.absoluteString),
            r.full.map(SQLiteValue.bool) ?? .null,
            .optional(r.actualQuality),
            r.bitrate.map(SQLiteValue.real) ?? .null,
            r.sampleRate.map(SQLiteValue.real) ?? .null,
            r.duration.map(SQLiteValue.real) ?? .null,
            r.expiresAtMs.map { .integer(Int64($0.rounded())) } ?? .null,
            r.licenseExpiresAtMs.map { .integer(Int64($0.rounded())) } ?? .null,
            .epochMillis(entry.savedAt),
        ]
    }

    private mutating func persistDelta(at now: Date) {
        let nextByKey = Dictionary(uniqueKeysWithValues: entries.map { ($0.key, $0) })
        let removedKeys = persisted.keys.filter { nextByKey[$0] == nil }
        let upserted = entries.filter { persisted[$0.key] != $0 }
        guard !removedKeys.isEmpty || !upserted.isEmpty else { return }
        guard let database else {
            persisted = nextByKey
            return
        }
        do {
            let upsertedRows = upserted.map(Self.cells)
            try database.transactionSync { db in
                for removed in removedKeys {
                    try db.execute(
                        "DELETE FROM ios_music_audio_urls WHERE scope=? AND platform=? AND native_id=? AND quality=?",
                        [.text(removed.scope), .text(removed.platform), .text(removed.nativeID), .text(removed.quality)]
                    )
                }
                for row in upsertedRows {
                    try db.upsert("ios_music_audio_urls", columns: Self.columns, values: row, key: Self.keyColumns)
                }
                try db.execute(
                    "DELETE FROM ios_music_audio_urls WHERE saved_at_ms<=? OR (expires_at_ms IS NOT NULL AND expires_at_ms<=?)",
                    [.epochMillis(now.addingTimeInterval(-Self.retention)), .epochMillis(now.addingTimeInterval(10))]
                )
            }
            persisted = nextByKey
        } catch {}
    }
}

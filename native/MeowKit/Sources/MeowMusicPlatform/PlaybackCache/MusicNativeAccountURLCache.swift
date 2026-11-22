import Foundation
import MeowRuntime
import MeowMusicFeature
import MeowStorage

/// Actor-confined disposable cache, not a credential store, queue or download.
/// Mirrors b900c259's URL limits: four scopes, fifty candidates/scope,
/// 1 MiB, thirty-day retention,
/// and ten seconds of safety before a declared expiry. Unknown expiry stays
/// unknown in the returned audio; retention is NOT a fabricated media lifetime.
struct MusicNativeAccountURLCache {
    static let key = "music.platform.audio-values.v1"
    static let maximumBytes = 1_024 * 1_024
    static let retention: TimeInterval = 30 * 86_400

    struct Key: Hashable, Sendable {
        let scope: String
        let platform: String
        let nativeID: String
        let quality: String
    }

    private struct CachedAudioRecord {
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

        func toRuntimeValue() -> RuntimeValue {
            var fields: RuntimeObject = ["url": .string(url.absoluteString)]
            if let full { fields["full"] = .bool(full) }
            if let actualQuality { fields["actualQuality"] = .string(actualQuality) }
            if let bitrate { fields["bitrate"] = .number(bitrate) }
            if let sampleRate { fields["sampleRate"] = .number(sampleRate) }
            if let duration { fields["duration"] = .number(duration) }
            if let expiresAtMs { fields["expiresAt"] = .number(expiresAtMs) }
            if let licenseExpiresAtMs { fields["licenseExpiresAt"] = .number(licenseExpiresAtMs) }
            return .object(fields)
        }

        static func from(_ value: RuntimeValue) throws -> CachedAudioRecord {
            guard case .object(let fields) = value else { throw MusicServiceError.invalidMediaCandidate }
            let parsed = try MusicNativeAccountURLCache.audio(value)
            let full: Bool?
            if let raw = fields["full"], raw != .null {
                guard case .bool(let b) = raw else { throw MusicServiceError.invalidMediaCandidate }
                full = b
            } else { full = nil }
            let actualQuality: String?
            if let raw = fields["actualQuality"], raw != .null {
                guard case .string(let s) = raw else { throw MusicServiceError.invalidMediaCandidate }
                actualQuality = s
            } else { actualQuality = nil }
            func finiteNumber(_ name: String) throws -> Double? {
                guard let raw = fields[name], raw != .null else { return nil }
                guard case .number(let n) = raw, n.isFinite else { throw MusicServiceError.invalidMediaCandidate }
                return n
            }
            return CachedAudioRecord(
                url: parsed.url,
                full: full,
                actualQuality: actualQuality,
                bitrate: try finiteNumber("bitrate"),
                sampleRate: try finiteNumber("sampleRate"),
                duration: try finiteNumber("duration"),
                expiresAtMs: try finiteNumber("expiresAt"),
                licenseExpiresAtMs: try finiteNumber("licenseExpiresAt")
            )
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

    private struct Entry {
        let key: Key
        let record: CachedAudioRecord
        let savedAt: Date
        func usable(at now: Date) -> Bool {
            guard MusicNativeAccountURLCache.validateCandidate(record) else { return false }
            return savedAt <= now && savedAt.addingTimeInterval(MusicNativeAccountURLCache.retention) > now &&
                (record.expiresAt == nil || record.expiresAt!.addingTimeInterval(-10) > now)
        }
    }

    private let database: SQLiteDatabase?
    private static let columns = ["scope", "platform", "native_id", "quality", "url", "full", "actual_quality", "bitrate",
                                  "sample_rate", "duration", "expires_at", "license_expires_at", "saved_at"]
    private var loaded = false
    private var entries: [Entry] = []
    // A delayed pre-failure resolver must not reinsert a URL rejected by the
    // player. This is short-lived request state, never another disk schema.
    private var rejected: [Key: [(url: URL, at: Date)]] = [:]

    init(database: SQLiteDatabase?) { self.database = database }

    private static let forbiddenQueryParams: Set<String> = [
        "cookie", "authorization", "music_u", "music_a", "musickey",
        "p_skey", "skey", "p_uin", "access_token", "refresh_token"
    ]

    private static func validateURL(_ url: URL) -> Bool {
        guard ["https", "http"].contains(url.scheme?.lowercased() ?? ""),
              let host = url.host, !host.isEmpty,
              url.user == nil, url.password == nil, url.fragment == nil,
              !url.absoluteString.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            return false
        }
        return !(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? [])
            .contains(where: { forbiddenQueryParams.contains($0.name.lowercased()) })
    }

    private static func validateCandidate(_ record: CachedAudioRecord) -> Bool {
        guard validateURL(record.url) else { return false }
        if let exp = record.expiresAtMs, !(exp.isFinite && exp > 0) { return false }
        if let lic = record.licenseExpiresAtMs, !(lic.isFinite && lic > 0) { return false }
        return true
    }

    /// Consume the shared audio fields. Unused fields never participate in
    /// admission and are not copied into the disposable cache.
    static func audio(_ value: RuntimeValue) throws -> MusicNativeAudio {
        guard case .object(let fields) = value else { throw MusicServiceError.invalidMediaCandidate }
        let audio = try MusicAccountFields.nativeAccountAudio(fields)
        guard validateURL(audio.url),
              audio.expiresAt == nil || audio.expiresAt!.timeIntervalSince1970 > 0 else {
            throw MusicServiceError.invalidMediaCandidate
        }
        return audio
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
                var list: [Entry] = []
                try db.scan(
                    "SELECT scope, platform, native_id, quality, url, full, actual_quality, bitrate, " +
                    "sample_rate, duration, expires_at, license_expires_at, saved_at " +
                    "FROM ios_music_audio_urls ORDER BY saved_at LIMIT 200"
                ) { cursor in
                    let key = Key(
                        scope: try cursor.text(0),
                        platform: try cursor.text(1),
                        nativeID: try cursor.text(2),
                        quality: try cursor.text(3)
                    )
                    guard Self.valid(key), let url = URL(string: try cursor.text(4)) else { return }
                    let record = CachedAudioRecord(
                        url: url,
                        full: try cursor.optionalBool(5),
                        actualQuality: try cursor.optionalText(6),
                        bitrate: try cursor.optionalDouble(7),
                        sampleRate: try cursor.optionalDouble(8),
                        duration: try cursor.optionalDouble(9),
                        expiresAtMs: try cursor.optionalDouble(10),
                        licenseExpiresAtMs: try cursor.optionalDouble(11)
                    )
                    let entry = Entry(key: key, record: record, savedAt: Date(timeIntervalSince1970: try cursor.double(12)))
                    if entry.usable(at: now) { list.append(entry) }
                }
                return list
            }
            for entry in loadedEntries {
                entries.removeAll { $0.key == entry.key }
                entries.append(entry)
            }
            trim(at: now)
        } catch {
            // Reading the disposable cache is best effort; a later successful
            // write can establish the current cache even after an I/O failure.
        }
    }

    mutating func value(for key: Key, at now: Date) -> RuntimeValue? {
        load(at: now); trim(at: now)
        guard let entry = entries.last(where: { $0.key == key }),
              !isRejected(entry.record.url, for: key, at: now) else { return nil }
        return entry.record.toRuntimeValue()
    }

    mutating func put(_ value: RuntimeValue, for key: Key, at now: Date) {
        load(at: now)
        guard Self.valid(key), let record = try? CachedAudioRecord.from(value),
              !isRejected(record.url, for: key, at: now) else { return }
        let entry = Entry(key: key, record: record, savedAt: now)
        guard entry.usable(at: now) else { return }
        entries.removeAll { $0.key == key }; entries.append(entry)
        trim(at: now); save()
    }

    /// A delayed failure for A must not evict a newer candidate B.
    mutating func reject(_ url: URL, for key: Key, at now: Date) {
        load(at: now)
        trimRejections(at: now)
        var values = rejected[key, default: []].filter { $0.url != url }
        values.append((url, now)); rejected[key] = Array(values.suffix(8))
        while rejected.count > 200, let oldest = rejected.min(by: {
            ($0.value.last?.at ?? .distantPast) < ($1.value.last?.at ?? .distantPast)
        })?.key { rejected.removeValue(forKey: oldest) }
        entries.removeAll { $0.key == key && $0.record.url == url }
        trim(at: now); save()
    }
    mutating func isRejected(_ url: URL, for key: Key, at now: Date) -> Bool {
        trimRejections(at: now)
        return rejected[key]?.contains(where: { $0.url == url }) == true
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

    private mutating func save() {
        func cells(_ entry: Entry) throws -> [SQLiteValue] {
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
                r.expiresAtMs.map(SQLiteValue.real) ?? .null,
                r.licenseExpiresAtMs.map(SQLiteValue.real) ?? .null,
                .optional(entry.savedAt),
            ]
        }
        do {
            var rows = try entries.map(cells)
            while rows.reduce(0, { $0 + $1.reduce(0, { $0 + $1.byteCount + 16 }) }) > Self.maximumBytes, !entries.isEmpty {
                entries.removeFirst(); rows.removeFirst()
            }
            // The byte budget also applies when persistence is absent/failed.
            guard let database else { return }
            try database.transactionSync(ownedBytes: rows.reduce(0) { $0 + $1.reduce(0) { $0 + $1.byteCount } }) { db in
                try db.reconcile("ios_music_audio_urls", columns: Self.columns,
                    key: ["scope", "platform", "native_id", "quality"], rows: rows)
            }
        } catch {
            // A disk failure cannot turn usable media into failure.
            // Keep process-local eviction effective instead of reloading stale disk.
        }
    }
}

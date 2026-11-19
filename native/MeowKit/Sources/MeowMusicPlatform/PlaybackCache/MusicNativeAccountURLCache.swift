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
    private static let audioFields = ["url", "full", "actualQuality", "bitrate", "sampleRate", "duration", "expiresAt", "licenseExpiresAt"]

    struct Key: Hashable, Sendable {
        let scope: String
        let platform: String
        let nativeID: String
        let quality: String
    }
    private struct Entry {
        let key: Key
        let value: RuntimeValue
        let savedAt: Date
        func usable(at now: Date) -> Bool {
            guard let audio = try? MusicNativeAccountURLCache.audio(value) else { return false }
            return savedAt <= now && savedAt.addingTimeInterval(MusicNativeAccountURLCache.retention) > now &&
                (audio.expiresAt == nil || audio.expiresAt!.addingTimeInterval(-10) > now)
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

    /// Consume the shared audio fields. Unused fields never participate in
    /// admission and are not copied into the disposable cache.
    static func audio(_ value: RuntimeValue) throws -> MusicNativeAudio {
        guard case .object(let fields) = value else { throw MusicServiceError.invalidMediaCandidate }
        let audio = try MusicAccountFields.nativeAccountAudio(fields)
        guard ["https", "http"].contains(audio.url.scheme?.lowercased() ?? ""),
              let host = audio.url.host, !host.isEmpty,
              audio.url.user == nil, audio.url.password == nil, audio.url.fragment == nil,
              !audio.url.absoluteString.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              audio.expiresAt == nil || audio.expiresAt!.timeIntervalSince1970 > 0 else {
            throw MusicServiceError.invalidMediaCandidate
        }
        // Signed media capabilities (vkey, etc.) belong to the candidate URL.
        // Platform login credentials must never be copied into a candidate.
        let forbidden: Set<String> = ["cookie", "authorization", "music_u", "music_a", "musickey",
                                      "p_skey", "skey", "p_uin", "access_token", "refresh_token"]
        guard !(URLComponents(url: audio.url, resolvingAgainstBaseURL: false)?.queryItems ?? [])
            .contains(where: { forbidden.contains($0.name.lowercased()) }) else {
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
            let rows = try database.readSync { try $0.query("SELECT * FROM ios_music_audio_urls ORDER BY saved_at LIMIT 200") }
            for row in rows {
                var projected = RuntimeObject()
                for (name, column) in zip(Self.audioFields, Self.columns.dropFirst(4)) {
                    switch row[column] {
                    case .null: break
                    case .text(let value): projected[name] = .string(value)
                    case .real(let value): projected[name] = .number(value)
                    case .integer(let value): projected[name] = name == "full" ? .bool(value == 1) : .number(Double(value))
                    case .blob: throw SQLiteStorageError.corrupt
                    }
                }
                let entry = Entry(key: .init(scope: try row.text("scope"), platform: try row.text("platform"),
                                            nativeID: try row.text("native_id"), quality: try row.text("quality")),
                                  value: .object(projected), savedAt: Date(timeIntervalSince1970: try row.double("saved_at")))
                guard Self.valid(entry.key), entry.usable(at: now) else { continue }
                entries.removeAll { $0.key == entry.key }; entries.append(entry)
            }
            trim(at: now)
        } catch {
            // Reading the disposable cache is best effort; a later successful
            // write can establish the current cache even after an I/O failure.
        }
    }

    mutating func value(for key: Key, at now: Date) -> RuntimeValue? {
        load(at: now); trim(at: now)
        guard let value = entries.last(where: { $0.key == key })?.value,
              let audio = try? Self.audio(value), !isRejected(audio.url, for: key, at: now) else { return nil }
        return value
    }

    mutating func put(_ value: RuntimeValue, for key: Key, at now: Date) {
        load(at: now)
        guard Self.valid(key), let audio = try? Self.audio(value),
              !isRejected(audio.url, for: key, at: now),
              case .object(let fields) = value else { return }
        let projected = RuntimeValue.object(RuntimeObject(uniqueKeysWithValues: fields.filter { Self.audioFields.contains($0.key) }))
        let entry = Entry(key: key, value: projected, savedAt: now)
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
        entries.removeAll { $0.key == key && (try? Self.audio($0.value).url) == url }
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
        guard case .object(let fields) = entry.value else { return identity }
        return fields.reduce(identity + 128) { size, field in
            let bytes: Int
            if case .string(let text) = field.value { bytes = text.utf8.count } else { bytes = 16 }
            return size + field.key.utf8.count + bytes
        }
    }

    private mutating func save() {
        func cells(_ entry: Entry) throws -> [SQLiteValue] {
            guard case .object(let fields) = entry.value else { throw SQLiteStorageError.invalidValue }
            var cells: [SQLiteValue] = [.text(entry.key.scope), .text(entry.key.platform), .text(entry.key.nativeID), .text(entry.key.quality)]
            for name in Self.audioFields {
                switch fields[name] ?? .null {
                case .null: cells.append(.null)
                case .string(let value): cells.append(.text(value))
                case .bool(let value): cells.append(.bool(value))
                case .number(let value) where value.isFinite: cells.append(.real(value))
                default: throw SQLiteStorageError.invalidValue
                }
            }
            cells.append(.optional(entry.savedAt))
            return cells
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

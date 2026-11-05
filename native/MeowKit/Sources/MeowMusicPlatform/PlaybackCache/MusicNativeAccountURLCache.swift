import Foundation
import MeowMusicFeature
import MeowJSON
import MeowStorage

/// Actor-confined disposable cache, not a credential store, queue or download.
/// Mirrors b900c259's URL limits: four scopes, fifty candidates/scope,
/// 1 MiB, thirty-day retention,
/// and ten seconds of safety before a declared expiry. Unknown expiry stays
/// unknown in the returned audio; retention is NOT a fabricated media lifetime.
struct MusicNativeAccountURLCache {
    static let key = "music.account-url-cache.v1"
    static let maximumBytes = 1_024 * 1_024
    static let retention: TimeInterval = 30 * 86_400

    struct Key: Codable, Hashable, Sendable {
        let scope: String
        let platform: String
        let nativeID: String
        let quality: String
    }
    private struct Entry: Codable {
        let key: Key
        let value: JSONValue
        let savedAt: Date
        func usable(at now: Date) -> Bool {
            guard let audio = try? MusicNativeAccountURLCache.audio(value) else { return false }
            return savedAt <= now && savedAt.addingTimeInterval(MusicNativeAccountURLCache.retention) > now &&
                (audio.expiresAt == nil || audio.expiresAt!.addingTimeInterval(-10) > now)
        }
    }
    private struct Document: Codable {
        var version = 1
        var entries: [Entry] = []
    }
    private let store: DocumentStore?
    private var loaded = false
    private var writable = true
    private var entries: [Entry] = []
    // A delayed pre-failure resolver must not reinsert a URL rejected by the
    // player. This is short-lived request state, never another disk schema.
    private var rejected: [Key: [(url: URL, at: Date)]] = [:]

    init(store: DocumentStore?) { self.store = store }

    /// Preserve the provider's original typed payload (including native media
    /// metadata), not a projection that loses actual level/file license fields.
    /// Account material/headers are never eligible for this cache.
    static func audio(_ value: JSONValue) throws -> MusicNativeAudio {
        guard case .object(let fields) = value else { throw MusicServiceError.invalidMediaCandidate }
        _ = try MusicPlatformJSON.bytes(value, maximum: 32 * 1024)
        let forbiddenFields: Set<String> = ["headers", "cookie", "cookies", "authorization", "credential",
            "credentials", "auth", "music_u", "music_a", "musickey", "p_skey", "skey",
            "access_token", "refresh_token"]
        func hasCredential(_ value: JSONValue) -> Bool {
            switch value {
            case .object(let object):
                return object.contains { forbiddenFields.contains($0.key.lowercased()) || hasCredential($0.value) }
            case .array(let array): return array.contains(where: hasCredential)
            default: return false
            }
        }
        guard !hasCredential(value) else { throw MusicServiceError.invalidMediaCandidate }
        let audio = try MusicPlatformJSON.nativeAccountAudio(fields)
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
        key.scope.count == 64 && key.scope.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) } &&
            MusicAccountPlatform(rawValue: key.platform) != nil &&
            !key.nativeID.isEmpty && key.nativeID.utf8.count <= 64 &&
            !key.nativeID.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) &&
            MusicPlatformJSON.officialQualities.contains(key.quality)
    }

    private mutating func load(at now: Date) {
        guard !loaded else { return }
        loaded = true
        do {
            guard let data = try store?.read(key: Self.key) else { return }
            guard data.count <= Self.maximumBytes else { writable = false; return }
            let document = try JSONDecoder().decode(Document.self, from: data)
            guard document.version == 1, document.entries.count <= 200 else { writable = false; return }
            // A malformed/unknown document is a miss, never silently overwritten.
            guard document.entries.allSatisfy({ Self.valid($0.key) && (try? Self.audio($0.value)) != nil }) else {
                writable = false; return
            }
            for entry in document.entries where entry.usable(at: now) {
                entries.removeAll { $0.key == entry.key }; entries.append(entry)
            }
            trim(at: now)
        } catch { writable = false }
    }

    mutating func value(for key: Key, at now: Date) -> JSONValue? {
        load(at: now); trim(at: now)
        guard let value = entries.last(where: { $0.key == key })?.value,
              let audio = try? Self.audio(value), !isRejected(audio.url, for: key, at: now) else { return nil }
        return value
    }

    mutating func put(_ value: JSONValue, for key: Key, at now: Date) {
        load(at: now)
        guard Self.valid(key), let audio = try? Self.audio(value),
              !isRejected(audio.url, for: key, at: now) else { return }
        let entry = Entry(key: key, value: value, savedAt: now)
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
    }

    private mutating func save() {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        do {
            var data = try encoder.encode(Document(entries: entries))
            while data.count > Self.maximumBytes, !entries.isEmpty {
                entries.removeFirst(); data = try encoder.encode(Document(entries: entries))
            }
            // The byte budget also applies when persistence is absent/failed.
            guard writable, let store else { return }
            try store.write(data, key: Self.key)
        } catch {
            // A disk failure cannot turn usable media into failure.
            // Keep process-local eviction effective instead of reloading stale disk.
            writable = false
        }
    }
}

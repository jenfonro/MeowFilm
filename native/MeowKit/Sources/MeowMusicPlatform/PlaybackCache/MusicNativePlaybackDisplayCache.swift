import Foundation
import CryptoKit
import ImageIO
import MeowMusicFeature
import MeowStorage

/// Passive, bounded recording presentation cache. No credentials, audio
/// URLs or playback position are stored. Failure is an optional cache miss.
public actor MusicNativePlaybackDisplayCache: MusicPlaybackDisplayCaching {
    public static let namespace = "music.playback-display.v2"
    private static let indexKey = "index"
    private static let lifetime: TimeInterval = 30 * 86_400
    private static let capacity = Int(NativeMusicAudioCacheHTTPPolicy.maximumBytes)
    private let factory: @Sendable () throws -> DocumentStore
    private let clock: @Sendable () -> Date
    private var document: DocumentStore?
    private struct Envelope: Codable {
        let version: Int
        let value: MusicPlaybackDisplaySnapshot
    }
    private struct Entry: Codable {
        let key: String
        let songID: String
        var updatedAt: Date
        let bytes: Int
    }
    private struct Index: Codable {
        let version: Int
        var entries: [Entry]
    }
    public init(storeFactory: @escaping @Sendable () throws -> DocumentStore,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        factory = storeFactory; self.clock = clock
    }
    private func store() throws -> DocumentStore {
        if let document { return document }
        let value = try factory(); document = value; return value
    }
    private static func text(_ value: String, maximum: Int = 1024) -> Bool {
        !value.isEmpty && value.utf8.count <= maximum &&
            !value.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
    }
    private static func valid(_ identity: MusicPlaybackDisplayIdentity) -> Bool {
        text(identity.scope) &&
            text(identity.songID) && identity.contentFingerprint.utf8.count == 64 &&
            identity.contentFingerprint.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }
    private static func validLyrics(_ lines: [MusicLyricLine]) -> Bool {
        guard lines.count <= 16_384, lines.allSatisfy({
            $0.time.isFinite && $0.end.isFinite && $0.time >= 0 && $0.end >= $0.time &&
                $0.words.allSatisfy { $0.time.isFinite && $0.duration.isFinite && $0.duration >= 0 }
        }), let bytes = try? JSONEncoder().encode(lines) else { return false }
        return bytes.count <= 1024 * 1024
    }
    private static func validArtwork(_ data: Data) -> Bool {
        guard !data.isEmpty, data.count <= 8 * 1024 * 1024,
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              CGImageSourceGetCount(source) > 0,
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? NSNumber,
              let height = properties[kCGImagePropertyPixelHeight] as? NSNumber else { return false }
        let w = width.int64Value, h = height.int64Value
        return w > 0 && h > 0 && w <= 8192 && h <= 8192 && w * h <= 32 * 1024 * 1024
    }
    static func key(_ identity: MusicPlaybackDisplayIdentity) -> String {
        let value = [identity.scope, identity.songID, identity.contentFingerprint]
        return SHA256.hash(data: Data(value.map { "\($0.utf8.count):\($0)" }.joined().utf8))
            .map { String(format: "%02x", $0) }.joined()
    }
    private func index() throws -> Index {
        guard let data = try store().read(key: Self.indexKey), data.count <= 1024 * 1024,
              let value = try? JSONDecoder().decode(Index.self, from: data),
              value.version == 1, value.entries.count <= 4096,
              Set(value.entries.map(\.key)).count == value.entries.count,
              value.entries.allSatisfy({ Self.text($0.songID) && $0.key.utf8.count == 64 &&
                  $0.key.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) } &&
                  $0.bytes > 0 && $0.bytes <= 12 * 1024 * 1024 &&
                  $0.updatedAt.timeIntervalSince1970.isFinite && $0.updatedAt.timeIntervalSince1970 > 0 &&
                  $0.updatedAt <= clock() }) else { return .init(version: 1, entries: []) }
        return value
    }
    private func commit(_ value: Index, removing: [Entry] = []) throws {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        try store().write(encoder.encode(value), key: Self.indexKey)
        for entry in removing { try store().remove(key: entry.key) }
    }
    private func prune(_ value: inout Index) throws {
        let expired = value.entries.filter { $0.updatedAt.addingTimeInterval(Self.lifetime) <= clock() }
        guard !expired.isEmpty else { return }
        value.entries.removeAll { $0.updatedAt.addingTimeInterval(Self.lifetime) <= clock() }
        try commit(value, removing: expired)
    }
    private func load(_ identity: MusicPlaybackDisplayIdentity) throws -> MusicPlaybackDisplaySnapshot? {
        guard let data = try store().read(key: Self.key(identity)), data.count <= 12 * 1024 * 1024,
              let saved = try? JSONDecoder().decode(Envelope.self, from: data),
              saved.version == 3, saved.value.identity == identity, Self.valid(saved.value.identity) else { return nil }
        let lyrics = Self.validLyrics(saved.value.lines) && (saved.value.lyricSourceKey.map { Self.text($0) } ?? saved.value.lines.isEmpty)
        let artwork = (saved.value.artworkSourceKey.map { Self.text($0) } ?? (saved.value.artwork == nil)) &&
            (saved.value.artwork.map(Self.validArtwork) ?? true)
        return .init(identity: identity, lyricSourceKey: lyrics ? saved.value.lyricSourceKey : nil,
            lines: lyrics ? saved.value.lines : [], artworkSourceKey: artwork ? saved.value.artworkSourceKey : nil,
            artwork: artwork ? saved.value.artwork : nil)
    }
    public func read(_ identity: MusicPlaybackDisplayIdentity,
                     authorization: MusicQueueAuthorization) async -> MusicPlaybackDisplaySnapshot? {
        guard !Task.isCancelled, Self.valid(identity) else { return nil }
        do {
            return try authorization.perform {
                var value = try index(); try prune(&value)
                guard value.entries.contains(where: { $0.key == Self.key(identity) }) else { return nil }
                return try load(identity)
            }
        } catch { return nil }
    }
    private func patch(_ identity: MusicPlaybackDisplayIdentity, authorization: MusicQueueAuthorization,
                       update: (MusicPlaybackDisplaySnapshot?) -> MusicPlaybackDisplaySnapshot) {
        guard !Task.isCancelled, Self.valid(identity) else { return }
        do {
            try authorization.perform {
                var index = try index(); try prune(&index)
                let key = Self.key(identity)
                let current = index.entries.contains(where: { $0.key == key }) ? try load(identity) : nil
                let value = update(current)
                let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
                let bytes = try encoder.encode(Envelope(version: 3, value: value))
                guard bytes.count <= 12 * 1024 * 1024 else { return }
                try Task.checkCancellation()
                index.entries.removeAll { $0.key == key }
                index.entries.append(.init(key: key, songID: identity.songID, updatedAt: clock(), bytes: bytes.count))
                var removed: [Entry] = []
                while index.entries.count > 4096 || index.entries.reduce(0, { $0 + $1.bytes }) > Self.capacity {
                    guard let oldest = index.entries.filter({ $0.key != key }).min(by: { $0.updatedAt < $1.updatedAt }) else { return }
                    index.entries.removeAll { $0.key == oldest.key }; removed.append(oldest)
                }
                // Index reservation first: a failed leaf write cannot create an
                // uncharged orphan. A missing leaf is an optional per-song miss.
                try commit(index, removing: removed)
                try store().write(bytes, key: key)
            }
        } catch { /* Presentation is optional; other songs and audio remain intact. */ }
    }
    public func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String,
                            identity: MusicPlaybackDisplayIdentity, authorization: MusicQueueAuthorization) async {
        guard Self.text(sourceKey), !lines.isEmpty, Self.validLyrics(lines) else { return }
        patch(identity, authorization: authorization) {
            .init(identity: identity, lyricSourceKey: sourceKey, lines: lines,
                  artworkSourceKey: $0?.artworkSourceKey, artwork: $0?.artwork)
        }
    }
    public func storeArtwork(_ data: Data, sourceKey: String,
                             identity: MusicPlaybackDisplayIdentity, authorization: MusicQueueAuthorization) async {
        guard Self.text(sourceKey), Self.validArtwork(data) else { return }
        patch(identity, authorization: authorization) {
            .init(identity: identity, lyricSourceKey: $0?.lyricSourceKey, lines: $0?.lines ?? [],
                  artworkSourceKey: sourceKey, artwork: data)
        }
    }
    public func clear() async {
        do { try commit(.init(version: 1, entries: []), removing: index().entries) } catch {}
    }
    func remove(songIDs: Set<String>, through date: Date) {
        do {
            var value = try index()
            let removed = value.entries.filter { songIDs.contains($0.songID) && $0.updatedAt <= date }
            value.entries.removeAll { songIDs.contains($0.songID) && $0.updatedAt <= date }
            if !removed.isEmpty { try commit(value, removing: removed) }
        } catch {}
    }
    func touch(songID: String, confirmedAt: Date) {
        guard confirmedAt.timeIntervalSince1970.isFinite, confirmedAt > Date(timeIntervalSince1970: 0), confirmedAt <= clock() else { return }
        do {
            var value = try index()
            for i in value.entries.indices where value.entries[i].songID == songID {
                value.entries[i].updatedAt = max(value.entries[i].updatedAt, confirmedAt)
            }
            try commit(value)
        } catch {}
    }
}

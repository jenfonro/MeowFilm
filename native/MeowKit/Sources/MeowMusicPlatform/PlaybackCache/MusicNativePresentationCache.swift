import Foundation
import CryptoKit
import MeowUI
import MeowMusicFeature
import MeowStorage

private final class PresentationCacheRevisions: @unchecked Sendable {
    private let lock = NSLock()
    private var image = UUID(), lyric = UUID()
    func value(images: Bool) -> UUID { lock.lock(); defer { lock.unlock() }; return images ? image : lyric }
    func clear(images: Bool, lyrics: Bool) {
        lock.lock(); defer { lock.unlock() }
        if images { image = UUID() }
        if lyrics { lyric = UUID() }
    }
}

/// Original network bytes, keyed by full public URL. Lyrics and images share
/// the original 256 MiB budget and 30-day lifetime; no song image copies exist.
public actor MusicNativePresentationCache: MusicPresentationCaching {
    public static let namespace = "music.presentation-resources.v1"
    private static let lifetime: TimeInterval = 30 * 86_400
    private static let capacity = Int(NativeMusicAudioCacheHTTPPolicy.maximumBytes)
    private nonisolated let revisions = PresentationCacheRevisions()
    public nonisolated var revision: UUID { revisions.value(images: true) }
    public nonisolated var lyricRevision: UUID { revisions.value(images: false) }
    private let factory: @Sendable () throws -> DocumentStore
    private let clock: @Sendable () -> Date
    private var document: DocumentStore?
    private enum Kind: String, Codable { case image, lyrics }
    private struct Entry: Codable {
        let key: String
        let resource: String
        let kind: Kind
        var updatedAt: Date
        var bytes: Int
        var ready: Bool
    }
    private struct Index: Codable { var entries: [Entry] = [] }
    private var imagesReadable = true, lyricsReadable = true

    public init(storeFactory: @escaping @Sendable () throws -> DocumentStore,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        factory = storeFactory; self.clock = clock
    }
    private func store() throws -> DocumentStore {
        if let document { return document }
        let value = try factory(); document = value; return value
    }
    private static func key(_ resource: String, kind: Kind) -> String {
        kind.rawValue + "-" + SHA256.hash(data: Data(resource.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    private func index() throws -> Index {
        guard let data = try store().read(key: "index") else { return .init() }
        guard data.count <= 1024 * 1024 else { throw DocumentStoreError.documentTooLarge }
        return try JSONDecoder().decode(Index.self, from: data)
    }
    private func commit(_ value: Index) throws {
        try store().write(JSONEncoder().encode(value), key: "index")
    }
    private func prune(_ value: inout Index) throws {
        let expired = value.entries.filter { $0.updatedAt.addingTimeInterval(Self.lifetime) <= clock() }
        guard !expired.isEmpty else { return }
        // Failed removals remain charged. Missing leaves are individual misses.
        for entry in expired {
            do {
                try store().remove(key: entry.key)
                value.entries.removeAll { $0.key == entry.key }
            } catch {
                if let position = value.entries.firstIndex(where: { $0.key == entry.key }) {
                    value.entries[position].ready = false
                }
            }
        }
        try commit(value)
    }
    private func put(_ data: Data, resource: String, kind: Kind, at: Date) throws {
        var value = try index(); try prune(&value)
        for position in value.entries.indices where
            value.entries[position].kind == .image && !imagesReadable ||
            value.entries[position].kind == .lyrics && !lyricsReadable {
            value.entries[position].ready = false
        }
        let key = Self.key(resource, kind: kind)
        let old = value.entries.first { $0.key == key }
        value.entries.removeAll { $0.key == key }
        // Reserve enough for both the old leaf and its atomic replacement. A
        // failed leaf write leaves a charged miss, never an uncharged orphan.
        value.entries.append(.init(key: key, resource: resource, kind: kind, updatedAt: at,
                                   bytes: max(old?.bytes ?? 0, data.count), ready: false))
        var encoded = try JSONEncoder().encode(value)
        var removed: [Entry] = []
        while encoded.count > 1024 * 1024 || value.entries.reduce(encoded.count, { $0 + $1.bytes }) > Self.capacity {
            guard let victim = value.entries.filter({ $0.key != key }).min(by: { $0.updatedAt < $1.updatedAt }) else {
                throw DocumentStoreError.documentTooLarge
            }
            value.entries.removeAll { $0.key == victim.key }; removed.append(victim)
            encoded = try JSONEncoder().encode(value)
        }
        for entry in removed { try store().remove(key: entry.key) }
        try store().write(encoded, key: "index")
        try store().write(data, key: key)
        if let position = value.entries.firstIndex(where: { $0.key == key }) {
            value.entries[position].bytes = data.count
            value.entries[position].ready = true
            try commit(value)
        }
    }
    public func read(_ url: URL) -> ArtworkBytes? {
        guard imagesReadable else { return nil }
        do {
            let value = try index(), key = Self.key(url.absoluteString, kind: .image)
            guard let entry = value.entries.first(where: {
                $0.kind == .image && $0.ready && $0.key == key && $0.resource == url.absoluteString &&
                    $0.updatedAt.addingTimeInterval(Self.lifetime) > clock()
            }),
                  let data = try store().read(key: entry.key), data.count <= 4 * 1024 * 1024 else { return nil }
            return .init(data: data, expiresAt: entry.updatedAt.addingTimeInterval(Self.lifetime))
        } catch { return nil }
    }
    public func store(_ data: Data, for url: URL, revision expected: UUID) -> ArtworkBytes? {
        guard !Task.isCancelled, revision == expected, data.count <= 4 * 1024 * 1024,
              MusicArtworkURL.normalized(url.absoluteString) == url else { return nil }
        let at = clock()
        // Decode admission is owned by ArtworkImages, before this call.
        do {
            try put(data, resource: url.absoluteString, kind: .image, at: at)
            imagesReadable = true
        } catch {}
        guard revision == expected else { return nil }
        return .init(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
    }
    public func lyrics(songID: String, authorization: MusicQueueAuthorization) -> MusicLyricDisplaySnapshot? {
        guard lyricsReadable else { return nil }
        do {
            return try authorization.perform {
                let value = try index()
                let key = Self.key(songID, kind: .lyrics)
                guard value.entries.contains(where: { $0.key == key && $0.ready &&
                    $0.updatedAt.addingTimeInterval(Self.lifetime) > clock() }),
                      let data = try store().read(key: key), data.count <= 1024 * 1024 else { return nil }
                let saved = try JSONDecoder().decode(MusicLyricDisplaySnapshot.self, from: data)
                return saved.songID == songID && Self.usableLyrics(saved.lines) ? saved : nil
            }
        } catch { return nil }
    }
    public func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String, songID: String,
                            revision expected: UUID, authorization: MusicQueueAuthorization) {
        guard !Task.isCancelled, lyricRevision == expected, !lines.isEmpty, Self.usableLyrics(lines) else { return }
        do {
            let value = MusicLyricDisplaySnapshot(songID: songID, sourceKey: sourceKey, lines: lines)
            let data = try JSONEncoder().encode(value)
            guard data.count <= 1024 * 1024 else { return }
            try authorization.perform { try put(data, resource: songID, kind: .lyrics, at: clock()) }
            lyricsReadable = true
        } catch {}
    }
    private static func usableLyrics(_ lines: [MusicLyricLine]) -> Bool {
        lines.allSatisfy {
            $0.time.isFinite && $0.end.isFinite && $0.time >= 0 && $0.end >= $0.time &&
                $0.words.allSatisfy { $0.time.isFinite && $0.duration.isFinite && $0.duration >= 0 }
        }
    }
    public func played(songID: String, artwork: URL?, confirmedAt: Date) {
        guard confirmedAt <= clock() else { return }
        do {
            var value = try index(); try prune(&value)
            for i in value.entries.indices where value.entries[i].ready &&
                (value.entries[i].kind == .lyrics && value.entries[i].resource == songID ||
                value.entries[i].kind == .image && value.entries[i].resource == artwork?.absoluteString) {
                value.entries[i].updatedAt = max(value.entries[i].updatedAt, confirmedAt)
            }
            try commit(value)
        } catch {}
    }
    public func clearImages() { clear(images: true, lyrics: false) }
    public func clearLyrics() { clear(images: false, lyrics: true) }
    public func clear() { clear(images: true, lyrics: true) }
    private func clear(images: Bool, lyrics: Bool) {
        revisions.clear(images: images, lyrics: lyrics)
        if images { imagesReadable = false }
        if lyrics { lyricsReadable = false }
        do {
            var value = try index()
            let removed = value.entries.filter { $0.kind == .image ? images : lyrics }
            for index in value.entries.indices where value.entries[index].kind == .image ? images : lyrics {
                value.entries[index].ready = false
            }
            // Invalidate successful-read eligibility before deleting leaves.
            // Failed deletions remain charged but cannot be served after clear.
            try commit(value)
            if images { imagesReadable = true }
            if lyrics { lyricsReadable = true }
            for entry in removed {
                do {
                    try store().remove(key: entry.key)
                    value.entries.removeAll { $0.key == entry.key }
                } catch {}
            }
            try commit(value)
        } catch {}
    }
}

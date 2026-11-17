import Foundation
import CryptoKit
import MeowUI
import MeowMusicFeature
import MeowStorage

private final class PresentationCacheRevisions: @unchecked Sendable {
    private let lock = NSLock()
    private var image = UUID(), lyric = UUID()
    private var imageClears = 0, lyricClears = 0
    func value(images: Bool) -> UUID { lock.lock(); defer { lock.unlock() }; return images ? image : lyric }
    func permits(_ expected: UUID, images: Bool) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return (images ? image : lyric) == expected && (images ? imageClears : lyricClears) == 0
    }
    func perform(_ expected: UUID, images: Bool, _ operation: () throws -> Void) throws {
        lock.lock(); defer { lock.unlock() }
        guard !Task.isCancelled, (images ? image : lyric) == expected,
              (images ? imageClears : lyricClears) == 0 else { throw CancellationError() }
        try operation()
    }
    func beginClear(images: Bool, lyrics: Bool) {
        lock.lock(); defer { lock.unlock() }
        if images { image = UUID(); imageClears += 1 }
        if lyrics { lyric = UUID(); lyricClears += 1 }
    }
    func endClear(images: Bool, lyrics: Bool) {
        lock.lock(); defer { lock.unlock() }
        if images { imageClears -= 1 }
        if lyrics { lyricClears -= 1 }
    }
}

/// Original network bytes, keyed by full public URL. Lyrics and images share
/// the original 256 MiB budget and 30-day lifetime; no song image copies exist.
public actor MusicNativePresentationCache: MusicPresentationCaching {
    public static let namespace = "music.presentation-resources.v2"
    private static let lifetime: TimeInterval = 30 * 86_400
    private static let capacity = Int(NativeMusicAudioCacheHTTPPolicy.maximumBytes)
    private nonisolated let revisions = PresentationCacheRevisions()
    public nonisolated var revision: UUID { revisions.value(images: true) }
    public nonisolated var lyricRevision: UUID { revisions.value(images: false) }
    private let factory: @Sendable () throws -> ResourceFileStore
    private let clock: @Sendable () -> Date
    private var files: ResourceFileStore?
    private enum Kind: String {
        case image, lyrics
        var maximumBytes: Int { self == .image ? 4 * 1024 * 1024 : 1024 * 1024 }
        static func from(key: String) -> Kind? {
            for kind in [Kind.image, .lyrics] where key.hasPrefix(kind.rawValue + "-") {
                let hash = key.dropFirst(kind.rawValue.count + 1)
                if hash.utf8.count == 64 && hash.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) {
                    return kind
                }
            }
            return nil
        }
    }
    private struct Entry {
        let kind: Kind
        var updatedAt: Date
        let bytes: Int
        var ready: Bool
        let stageName: String?
    }
    private var entries: [String: Entry] = [:]
    private var byteCount = 0
    private var nextExpiry: Date?
    // A failed first clear must not adopt surviving leaves on a later inventory retry.
    private var imagesReadable = true, lyricsReadable = true

    public init(storeFactory: @escaping @Sendable () throws -> ResourceFileStore,
                clock: @escaping @Sendable () -> Date = { Date() }) {
        factory = storeFactory; self.clock = clock
    }
    private func store() throws -> ResourceFileStore {
        if let files { return files }
        let value = try factory()
        let inventory = try value.inventory()
        for leaf in inventory {
            guard let kind = Kind.from(key: leaf.key) else { continue }
            entries[leaf.stageName ?? leaf.key] = .init(kind: kind, updatedAt: leaf.modifiedAt, bytes: leaf.byteCount,
                ready: leaf.stageName == nil && leaf.byteCount > 0 && leaf.byteCount <= kind.maximumBytes &&
                    (kind == .image ? imagesReadable : lyricsReadable), stageName: leaf.stageName)
            byteCount += leaf.byteCount
        }
        updateNextExpiry()
        files = value
        return value
    }
    private static func key(_ resource: String, kind: Kind) -> String {
        kind.rawValue + "-" + SHA256.hash(data: Data(resource.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    private func updateNextExpiry() {
        nextExpiry = entries.values.lazy.filter(\.ready).map { $0.updatedAt.addingTimeInterval(Self.lifetime) }.min()
    }
    private func forget(_ key: String) {
        if let old = entries.removeValue(forKey: key) { byteCount -= old.bytes }
    }
    private func remove(_ key: String, from files: ResourceFileStore) throws {
        // Failed removals remain charged, but are never readable after invalidation.
        guard let entry = entries[key] else { return }
        entries[key]?.ready = false
        if let stageName = entry.stageName { try files.removeStage(name: stageName) }
        else { try files.remove(key: key) }
        forget(key)
    }
    private func prune(at: Date, files: ResourceFileStore) {
        guard let nextExpiry, nextExpiry <= at else { return }
        let expired = entries.filter { $0.value.updatedAt.addingTimeInterval(Self.lifetime) <= at }.map(\.key)
        for key in expired { try? remove(key, from: files) }
        updateNextExpiry()
    }
    private func put(_ data: Data, resource: String, kind: Kind, at: Date, expected: UUID) throws {
        let files = try store(), key = Self.key(resource, kind: kind)
        guard revisions.permits(expected, images: kind == .image), !Task.isCancelled else { throw CancellationError() }
        prune(at: at, files: files)
        // Charge the temporary raw leaf as well as the old committed leaf until rename.
        // Only budget pressure sorts metadata; ordinary puts never scan/encode an index.
        if byteCount + data.count > Self.capacity {
            let victims = entries.filter { $0.key != key }.sorted {
                $0.value.updatedAt == $1.value.updatedAt ? $0.key < $1.key : $0.value.updatedAt < $1.value.updatedAt
            }
            for victim in victims where byteCount + data.count > Self.capacity {
                try? remove(victim.key, from: files)
            }
            guard byteCount + data.count <= Self.capacity else { throw DocumentStoreError.documentTooLarge }
        }
        try files.write(data, key: key, modifiedAt: at, retainedStage: { leaf in
            if let stageName = leaf.stageName {
                entries[stageName] = .init(kind: kind, updatedAt: leaf.modifiedAt, bytes: leaf.byteCount,
                    ready: false, stageName: stageName)
                byteCount += leaf.byteCount
            }
        }) { publish in
            try revisions.perform(expected, images: kind == .image, publish)
        }
        forget(key)
        entries[key] = .init(kind: kind, updatedAt: at, bytes: data.count, ready: true, stageName: nil)
        byteCount += data.count
        let expiry = at.addingTimeInterval(Self.lifetime)
        nextExpiry = nextExpiry.map { min($0, expiry) } ?? expiry
    }
    private func bytes(resource: String, kind: Kind) throws -> (Data, Date)? {
        let files = try store(), key = Self.key(resource, kind: kind)
        guard let entry = entries[key], entry.ready,
              entry.updatedAt.addingTimeInterval(Self.lifetime) > clock() else { return nil }
        do {
            guard let data = try files.read(key: key, maximumBytes: kind.maximumBytes) else {
                forget(key); return nil
            }
            guard !data.isEmpty else { entries[key]?.ready = false; return nil }
            return (data, entry.updatedAt.addingTimeInterval(Self.lifetime))
        } catch DocumentStoreError.unsafePath, DocumentStoreError.documentTooLarge {
            entries[key]?.ready = false
            return nil
        }
    }
    public func read(_ url: URL) -> ArtworkBytes? {
        let expected = revision
        guard !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
        do {
            guard let (data, expiresAt) = try bytes(resource: url.absoluteString, kind: .image),
                  !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
            return .init(data: data, expiresAt: expiresAt)
        } catch { return nil }
    }
    public func store(_ data: Data, for url: URL, revision expected: UUID) -> ArtworkBytes? {
        guard !Task.isCancelled, revisions.permits(expected, images: true), !data.isEmpty,
              data.count <= Kind.image.maximumBytes, MusicArtworkURL.normalized(url.absoluteString) == url else { return nil }
        let at = clock()
        // Decode admission is owned by ArtworkImages, before this call.
        do { try put(data, resource: url.absoluteString, kind: .image, at: at, expected: expected) } catch {}
        guard !Task.isCancelled, revisions.permits(expected, images: true) else { return nil }
        return .init(data: data, expiresAt: at.addingTimeInterval(Self.lifetime))
    }
    public func lyrics(songID: String, authorization: MusicQueueAuthorization) -> MusicLyricDisplaySnapshot? {
        let expected = lyricRevision
        guard !Task.isCancelled, revisions.permits(expected, images: false) else { return nil }
        do {
            return try authorization.perform {
                guard let (data, _) = try bytes(resource: songID, kind: .lyrics) else { return nil }
                guard let saved = try? JSONDecoder().decode(MusicLyricDisplaySnapshot.self, from: data),
                      saved.songID == songID, !saved.lines.isEmpty, Self.usableLyrics(saved.lines) else {
                    entries[Self.key(songID, kind: .lyrics)]?.ready = false
                    return nil
                }
                return !Task.isCancelled && revisions.permits(expected, images: false) ? saved : nil
            }
        } catch { return nil }
    }
    public func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String, songID: String,
                            revision expected: UUID, authorization: MusicQueueAuthorization) {
        guard !Task.isCancelled, revisions.permits(expected, images: false), !lines.isEmpty, Self.usableLyrics(lines) else { return }
        do {
            let value = MusicLyricDisplaySnapshot(songID: songID, sourceKey: sourceKey, lines: lines)
            let data = try JSONEncoder().encode(value)
            guard data.count <= Kind.lyrics.maximumBytes else { return }
            try authorization.perform { try put(data, resource: songID, kind: .lyrics, at: clock(), expected: expected) }
        } catch {}
    }
    private static func usableLyrics(_ lines: [MusicLyricLine]) -> Bool {
        lines.allSatisfy {
            $0.time.isFinite && $0.end.isFinite && $0.time >= 0 && $0.end >= $0.time &&
                $0.words.allSatisfy { $0.time.isFinite && $0.duration.isFinite && $0.duration >= 0 }
        }
    }
    public func played(songID: String, artwork: URL?, confirmedAt: Date) {
        guard !Task.isCancelled, confirmedAt <= clock() else { return }
        do {
            let files = try store()
            let resources: [(Kind, String)] = [(Kind.lyrics, songID)] + (artwork.map { [(.image, $0.absoluteString)] } ?? [])
            for (kind, resource) in resources {
                let expected = revisions.value(images: kind == .image), key = Self.key(resource, kind: kind)
                guard revisions.permits(expected, images: kind == .image), let entry = entries[key], entry.ready,
                      entry.updatedAt.addingTimeInterval(Self.lifetime) > clock(), confirmedAt > entry.updatedAt else { continue }
                do {
                    if let updatedAt = try files.touch(key: key, modifiedAt: confirmedAt, commit: { touch in
                        try revisions.perform(expected, images: kind == .image, touch)
                    }) {
                        entries[key]?.updatedAt = updatedAt
                    } else { forget(key) }
                } catch {}
            }
        } catch {}
    }
    public nonisolated func clearImages() async { await clear(images: true, lyrics: false) }
    public nonisolated func clearLyrics() async { await clear(images: false, lyrics: true) }
    public nonisolated func clear() async { await clear(images: true, lyrics: true) }
    private nonisolated func clear(images: Bool, lyrics: Bool) async {
        // Revoke before waiting for actor I/O. An already staged write checks at rename.
        revisions.beginClear(images: images, lyrics: lyrics)
        defer { revisions.endClear(images: images, lyrics: lyrics) }
        await removeCleared(images: images, lyrics: lyrics)
    }
    private func removeCleared(images: Bool, lyrics: Bool) {
        if images { imagesReadable = false }
        if lyrics { lyricsReadable = false }
        do {
            let files = try store()
            let removed = entries.filter { $0.value.kind == .image ? images : lyrics }.map(\.key)
            for key in removed { try? remove(key, from: files) }
            updateNextExpiry()
        } catch {}
    }
}

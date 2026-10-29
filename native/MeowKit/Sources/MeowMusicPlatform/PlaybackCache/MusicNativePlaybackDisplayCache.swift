import Foundation
import ImageIO
import MeowMusicFeature
import MeowStorage

/// Passive, single-record encrypted presentation cache. No credentials, audio
/// URLs or playback position are stored. Failure is an optional cache miss.
public actor MusicNativePlaybackDisplayCache: MusicPlaybackDisplayCaching {
    public static let namespace = "music.playback-display.v1"
    private static let key = "current"
    private let factory: @Sendable () throws -> EncryptedDocumentStore
    private var document: EncryptedDocumentStore?
    private struct Envelope: Codable {
        let version: Int
        let value: MusicPlaybackDisplaySnapshot
    }
    public init(storeFactory: @escaping @Sendable () throws -> EncryptedDocumentStore) {
        factory = storeFactory
    }
    private func store() throws -> EncryptedDocumentStore {
        if let document { return document }
        let value = try factory(); document = value; return value
    }
    private static func text(_ value: String, maximum: Int = 1024) -> Bool {
        !value.isEmpty && value.utf8.count <= maximum &&
            !value.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) }
    }
    private static func valid(_ identity: MusicPlaybackDisplayIdentity) -> Bool {
        text(identity.scope) && text(identity.occurrenceID, maximum: 128) &&
            text(identity.songID) && identity.contentFingerprint.utf8.count == 64 &&
            identity.contentFingerprint.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }
    private static func validLyrics(_ lines: [MusicLyricLine]) -> Bool {
        guard lines.count <= 16_384, lines.allSatisfy({
            $0.time.isFinite && $0.end.isFinite && $0.time >= 0 && $0.end >= $0.time &&
                $0.words.allSatisfy { $0.time.isFinite && $0.duration.isFinite && $0.time >= 0 && $0.duration >= 0 }
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
    private func load() throws -> MusicPlaybackDisplaySnapshot? {
        guard let data = try store().read(key: Self.key), data.count <= 12 * 1024 * 1024,
              let saved = try? JSONDecoder().decode(Envelope.self, from: data),
              saved.version == 1, Self.valid(saved.value.identity), Self.validLyrics(saved.value.lines),
              saved.value.lyricSourceKey.map({ Self.text($0) }) ?? saved.value.lines.isEmpty,
              saved.value.artworkSourceKey.map({ Self.text($0) }) ?? (saved.value.artwork == nil),
              saved.value.artwork.map(Self.validArtwork) ?? true else { return nil }
        return saved.value
    }
    public func read(_ identity: MusicPlaybackDisplayIdentity,
                     authorization: MusicQueueAuthorization) async -> MusicPlaybackDisplaySnapshot? {
        guard !Task.isCancelled, Self.valid(identity) else { return nil }
        do {
            return try authorization.perform {
                let value = try load()
                return value?.identity == identity ? value : nil
            }
        } catch { return nil }
    }
    private func patch(_ identity: MusicPlaybackDisplayIdentity, authorization: MusicQueueAuthorization,
                       update: (MusicPlaybackDisplaySnapshot?) -> MusicPlaybackDisplaySnapshot) {
        guard !Task.isCancelled, Self.valid(identity) else { return }
        do {
            try authorization.perform {
                let current = try load()
                let value = update(current?.identity == identity ? current : nil)
                let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
                let bytes = try encoder.encode(Envelope(version: 1, value: value))
                guard bytes.count <= 12 * 1024 * 1024 else { return }
                try Task.checkCancellation()
                try store().write(bytes, key: Self.key)
            }
        } catch { /* Last presentation is optional; audio and the library remain intact. */ }
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
        // A cache clear writes an empty record; it never removes the namespace
        // directory or touches the user's queue/library/download documents.
        do { try store().write(Data(), key: Self.key) } catch {}
    }
}

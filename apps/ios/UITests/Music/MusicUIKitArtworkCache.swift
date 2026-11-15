#if os(iOS)
import Foundation
import XCTest
import MeowUI
@testable import MeowMusicFeature

/// Current resource bytes for visual fixtures; decoding still uses the real loader.
final class MusicUIKitArtworkCache: MusicPresentationCaching, @unchecked Sendable {
    private let lock = NSLock()
    private var imageRevision = UUID()
    private var images: [URL: ArtworkBytes]
    let lyricRevision = UUID()
    var revision: UUID { lock.withLock { imageRevision } }

    init(_ bytes: [URL: Data]) {
        let expiresAt = Date().addingTimeInterval(30 * 86_400)
        images = bytes.mapValues { ArtworkBytes(data: $0, expiresAt: expiresAt) }
    }
    func read(_ url: URL) async -> ArtworkBytes? { lock.withLock { images[url] } }
    func store(_ data: Data, for url: URL, revision: UUID) async -> ArtworkBytes? {
        lock.withLock {
            guard revision == imageRevision else { return nil }
            let value = ArtworkBytes(data: data, expiresAt: Date().addingTimeInterval(30 * 86_400))
            images[url] = value
            return value
        }
    }
    func clearImages() async { lock.withLock { imageRevision = UUID(); images.removeAll() } }
    func lyrics(songID: String, authorization: MusicQueueAuthorization) async -> MusicLyricDisplaySnapshot? {
        XCTFail("Artwork fixtures do not attach a lyric cache")
        return nil
    }
    func storeLyrics(_ lines: [MusicLyricLine], sourceKey: String, songID: String,
                     revision: UUID, authorization: MusicQueueAuthorization) async {
        XCTFail("Artwork fixtures do not attach a lyric cache")
    }
    func played(songID: String, artwork: URL?, confirmedAt: Date) async {
        XCTFail("Visual fixtures do not play media")
    }
    func clearLyrics() async { XCTFail("Artwork fixtures do not attach a lyric cache") }
    func clear() async { await clearImages(); await clearLyrics() }
}
#endif

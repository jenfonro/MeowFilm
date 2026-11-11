import Foundation
import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicPlatform

final class MusicNativePlaybackDisplayCacheTests: XCTestCase {
    private struct Envelope: Codable {
        let version: Int
        let value: MusicPlaybackDisplaySnapshot
    }
    private let identity = MusicPlaybackDisplayIdentity(scope: "local", occurrenceID: "entry",
        songID: "song", contentFingerprint: String(repeating: "a", count: 64))
    private var lines: [MusicLyricLine] {
        [.init(index: 0, time: 0, end: 1, text: "AB", words: [
            .init(time: -0.1, duration: 0.01, text: "A"),
            .init(time: -0.09, duration: 0, text: "B")
        ]), .init(index: 1, time: 1, end: 1, text: "整句")]
    }
    private func fixture() throws -> (URL, DocumentStore, MusicNativePlaybackDisplayCache) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: root, namespace: MusicNativePlaybackDisplayCache.namespace)
        return (root, store, MusicNativePlaybackDisplayCache(storeFactory: { store }))
    }
    private func artwork() throws -> Data {
        try XCTUnwrap(Data(base64Encoded:
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5HcAAAAASUVORK5CYII="))
    }

    func testCurrentCacheRoundTripsRealWordsAndLineOnlyLyricsAcrossColdRead() async throws {
        let (root, store, cache) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let auth = MusicQueueAuthorization()
        await cache.storeLyrics(lines, sourceKey: "native", identity: identity, authorization: auth)
        let saved = try JSONDecoder().decode(Envelope.self, from: XCTUnwrap(store.read(key: "current")))
        XCTAssertEqual(saved.version, 2)
        let cold = MusicNativePlaybackDisplayCache(storeFactory: { store })
        let hit = await cold.read(identity, authorization: auth)
        XCTAssertEqual(hit?.lines, lines)
        XCTAssertEqual(hit?.lyricSourceKey, "native")
        XCTAssertTrue(hit?.lines[1].words.isEmpty == true)
    }

    func testOldSimulatedCacheIsAMissWithoutMigrationOrUnrelatedDeletion() async throws {
        let (root, store, cache) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let original = Data("""
            {"version":1,"value":{"identity":{"scope":"local","occurrenceID":"entry","songID":"song",
            "contentFingerprint":"\(identity.contentFingerprint)"},"lyricSourceKey":"native",
            "lines":[{"index":0,"time":1,"end":6,"text":"AB","translation":"","timing":"estimated",
            "words":[{"time":1,"duration":2.5,"text":"A"},{"time":3.5,"duration":2.5,"text":"B"}]}]}}
            """.utf8)
        try store.write(original, key: "current")
        let library = try DocumentStore(baseURL: root, namespace: "unrelated.library")
        let untouched = Data(#"{"user":"retained"}"#.utf8)
        try library.write(untouched, key: "songs")
        let hit = await cache.read(identity, authorization: .init())
        XCTAssertNil(hit)
        XCTAssertEqual(try store.read(key: "current"), original)
        XCTAssertEqual(try library.read(key: "songs"), untouched)

        await cache.storeLyrics(lines, sourceKey: "fresh", identity: identity, authorization: .init())
        let replacement = await cache.read(identity, authorization: .init())
        XCTAssertEqual(replacement?.lines, lines)
        XCTAssertEqual(replacement?.lyricSourceKey, "fresh")
        XCTAssertEqual(try library.read(key: "songs"), untouched)
    }

    func testArtworkPatchDoesNotCarryOldLyricsIntoCurrentFormat() async throws {
        let (root, store, cache) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let old = MusicPlaybackDisplaySnapshot(identity: identity, lyricSourceKey: "old", lines: [
            .init(index: 0, time: 1, end: 6, text: "A", words: [.init(time: 1, duration: 5, text: "A")])
        ])
        try store.write(JSONEncoder().encode(Envelope(version: 1, value: old)), key: "current")
        let image = try artwork()
        await cache.storeArtwork(image, sourceKey: "cover", identity: identity, authorization: .init())
        let hit = await cache.read(identity, authorization: .init())
        XCTAssertEqual(hit?.artwork, image)
        XCTAssertNil(hit?.lyricSourceKey)
        XCTAssertEqual(hit?.lines, [])
    }

    func testCurrentPatchesPreserveArtworkAndRejectInvalidWordDurations() async throws {
        let (root, _, cache) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), auth = MusicQueueAuthorization()
        await cache.storeArtwork(image, sourceKey: "cover", identity: identity, authorization: auth)
        await cache.storeLyrics(lines, sourceKey: "native", identity: identity, authorization: auth)
        let invalid = [MusicLyricLine(index: 0, time: 0, end: 1, text: "bad",
            words: [.init(time: 0, duration: -0.01, text: "bad")])]
        await cache.storeLyrics(invalid, sourceKey: "bad", identity: identity, authorization: auth)
        let hit = await cache.read(identity, authorization: auth)
        XCTAssertEqual(hit?.lines, lines)
        XCTAssertEqual(hit?.artwork, image)
        XCTAssertEqual(hit?.artworkSourceKey, "cover")
        XCTAssertEqual(hit?.lyricSourceKey, "native")
    }

    func testNegativeLineSeekPositionIsRejectedOnWriteAndColdRead() async throws {
        let (root, store, cache) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let invalid = [MusicLyricLine(index: 0, time: -0.1, end: 1, text: "invalid")]
        await cache.storeLyrics(invalid, sourceKey: "native", identity: identity, authorization: .init())
        XCTAssertNil(try store.read(key: "current"))
        let snapshot = MusicPlaybackDisplaySnapshot(identity: identity, lyricSourceKey: "native", lines: invalid)
        try store.write(JSONEncoder().encode(Envelope(version: 2, value: snapshot)), key: "current")
        let cold = MusicNativePlaybackDisplayCache(storeFactory: { store })
        let hit = await cold.read(identity, authorization: .init())
        XCTAssertNil(hit)
    }
}

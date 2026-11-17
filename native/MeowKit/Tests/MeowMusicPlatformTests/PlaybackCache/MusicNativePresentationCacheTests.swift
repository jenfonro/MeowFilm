import Foundation
import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicPlatform

final class MusicNativePresentationCacheTests: XCTestCase {
    private let songID = UUID().uuidString.lowercased()
    private let imageURL = URL(string: "https://images.invalid/artwork?size=256&public=one")!
    private var lines: [MusicLyricLine] {
        [.init(index: 0, time: 0, end: 1, text: "AB", words: [
            .init(time: -0.1, duration: 0.01, text: "A"),
            .init(time: -0.09, duration: 0, text: "B")
        ]), .init(index: 1, time: 1, end: 1, text: "整句")]
    }
    private func fixture() throws -> (URL, DocumentStore, MusicNativePresentationCache, MusicAccountTestClock) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let store = try DocumentStore(baseURL: root, namespace: MusicNativePresentationCache.namespace)
        let clock = MusicAccountTestClock()
        return (root, store, MusicNativePresentationCache(storeFactory: { store }, clock: { clock.now() }), clock)
    }
    private func artwork() throws -> Data {
        try XCTUnwrap(Data(base64Encoded:
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5HcAAAAASUVORK5CYII="))
    }
    private func lyrics(_ cache: MusicNativePresentationCache, song: String? = nil) async {
        await cache.storeLyrics(lines, sourceKey: "native", songID: song ?? songID,
            revision: cache.lyricRevision, authorization: .init())
    }

    func testCurrentResourcesRoundTripThroughASeparateColdOwnerWithoutSongImageCopies() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), otherSong = UUID().uuidString.lowercased()
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        await lyrics(cache); await lyrics(cache, song: otherSong)
        let cold = MusicNativePresentationCache(storeFactory: { store }, clock: { clock.now() })
        let hit = await cold.read(imageURL), first = await cold.lyrics(songID: songID, authorization: .init())
        let second = await cold.lyrics(songID: otherSong, authorization: .init())
        XCTAssertEqual(hit?.data, image)
        XCTAssertEqual(first?.lines, lines); XCTAssertEqual(second?.lines, lines)
        XCTAssertEqual(first?.sourceKey, "native"); XCTAssertTrue(first?.lines[1].words.isEmpty == true)
        let leaves = try FileManager.default.contentsOfDirectory(at: root.appendingPathComponent(
            MusicNativePresentationCache.namespace), includingPropertiesForKeys: nil)
        XCTAssertEqual(leaves.filter { $0.lastPathComponent.hasPrefix("image-") }.count, 1)
        XCTAssertEqual(leaves.filter { $0.lastPathComponent.hasPrefix("lyrics-") }.count, 2)
    }

    func testFullResourceQueriesRemainIndependentAndLyricsNeverContainImageBytes() async throws {
        let (root, store, cache, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let otherURL = URL(string: "https://images.invalid/artwork?size=512&public=one")!
        let image = try artwork()
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        await lyrics(cache)
        let missing = await cache.read(otherURL)
        XCTAssertNil(missing)
        let other = image + Data([0])
        _ = await cache.store(other, for: otherURL, revision: cache.revision)
        let first = await cache.read(imageURL), second = await cache.read(otherURL)
        XCTAssertEqual(first?.data, image); XCTAssertEqual(second?.data, other)
        let leaves = try FileManager.default.contentsOfDirectory(at: root.appendingPathComponent(
            MusicNativePresentationCache.namespace), includingPropertiesForKeys: nil)
        let lyric = try XCTUnwrap(leaves.first { $0.lastPathComponent.hasPrefix("lyrics-") })
        let value = try JSONDecoder().decode(MusicLyricDisplaySnapshot.self, from: Data(contentsOf: lyric))
        XCTAssertEqual(value.songID, songID); XCTAssertEqual(value.lines, lines)
        XCTAssertNotNil(try store.read(key: "index"))
    }

    func testImageClearRevokesOldWritersWithoutDeletingLyricsOrUserDocuments() async throws {
        let (root, _, cache, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let library = try DocumentStore(baseURL: root, namespace: "user-library")
        let userDocument = Data(#"{"songId":"permanent","queue":["occurrence"]}"#.utf8)
        try library.write(userDocument, key: "songs")
        let image = try artwork(), imageRevision = cache.revision, lyricRevision = cache.lyricRevision
        _ = await cache.store(image, for: imageURL, revision: imageRevision)
        await lyrics(cache)
        await cache.clearImages()
        XCTAssertNotEqual(cache.revision, imageRevision); XCTAssertEqual(cache.lyricRevision, lyricRevision)
        let staleWrite = await cache.store(image, for: imageURL, revision: imageRevision)
        let cleared = await cache.read(imageURL), healthy = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(staleWrite); XCTAssertNil(cleared); XCTAssertEqual(healthy?.lines, lines)
        XCTAssertEqual(try library.read(key: "songs"), userDocument)
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        await cache.clearLyrics()
        let retainedImage = await cache.read(imageURL), removedLyrics = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(retainedImage?.data, image); XCTAssertNil(removedLyrics)
        await cache.clear()
        let removedImage = await cache.read(imageURL)
        XCTAssertNil(removedImage); XCTAssertEqual(try library.read(key: "songs"), userDocument)
    }

    func testRealWordTimingIsPreservedButInvalidMediaTimingsCannotReplaceHealthyLyrics() async throws {
        let (root, _, cache, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        await lyrics(cache)
        for invalid in [
            [MusicLyricLine(index: 0, time: -0.1, end: 1, text: "invalid")],
            [MusicLyricLine(index: 0, time: 0, end: 1, text: "invalid",
                words: [.init(time: 0, duration: -0.01, text: "invalid")])]
        ] {
            await cache.storeLyrics(invalid, sourceKey: "invalid", songID: songID,
                revision: cache.lyricRevision, authorization: .init())
        }
        let hit = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(hit?.lines, lines); XCTAssertEqual(hit?.sourceKey, "native")
        let revoked = MusicQueueAuthorization(); revoked.revoke()
        let denied = await cache.lyrics(songID: songID, authorization: revoked)
        XCTAssertNil(denied)
    }

    func testDamagedCurrentLyricLeafDoesNotDiscardAnIndependentImage() async throws {
        let (root, _, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork()
        await lyrics(cache); _ = await cache.store(image, for: imageURL, revision: cache.revision)
        let leaves = try FileManager.default.contentsOfDirectory(at: root.appendingPathComponent(
            MusicNativePresentationCache.namespace), includingPropertiesForKeys: nil)
        let leaf = try XCTUnwrap(leaves.first { $0.lastPathComponent.hasPrefix("lyrics-") })
        try Data("invalid current document".utf8).write(to: leaf)
        let cold = MusicNativePresentationCache(storeFactory: {
            try DocumentStore(baseURL: root, namespace: MusicNativePresentationCache.namespace)
        }, clock: { clock.now() })
        let missing = await cold.lyrics(songID: songID, authorization: .init()), healthy = await cold.read(imageURL)
        XCTAssertNil(missing); XCTAssertEqual(healthy?.data, image)
    }

    func testOnlyActualPlaybackRenewsThirtyDayRetentionAndDisplayReadsDoNot() async throws {
        let (root, _, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), other = URL(string: "https://images.invalid/not-played")!
        let start = clock.now()
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        _ = await cache.store(image, for: other, revision: cache.revision); await lyrics(cache)
        clock.advance(29 * 86_400)
        let warm = await cache.read(imageURL); XCTAssertNotNil(warm)
        let displayOnly = await cache.read(other); XCTAssertNotNil(displayOnly)
        await cache.played(songID: songID, artwork: imageURL, confirmedAt: clock.now())
        clock.advance(2 * 86_400)
        let retained = await cache.read(imageURL), expired = await cache.read(other)
        let lyric = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(retained?.expiresAt, start.addingTimeInterval(59 * 86_400))
        XCTAssertNil(expired); XCTAssertEqual(lyric?.lines, lines)
        clock.advance(29 * 86_400)
        let gone = await cache.read(imageURL), oldLyrics = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(gone); XCTAssertNil(oldLyrics)
    }

    func testDecodedCurrentLyricLeafWithNegativeSeekDoesNotDiscardAnIndependentImage() async throws {
        let (root, _, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork()
        await lyrics(cache); _ = await cache.store(image, for: imageURL, revision: cache.revision)
        let leaves = try FileManager.default.contentsOfDirectory(at: root.appendingPathComponent(
            MusicNativePresentationCache.namespace), includingPropertiesForKeys: nil)
        let leaf = try XCTUnwrap(leaves.first { $0.lastPathComponent.hasPrefix("lyrics-") })
        let invalid = MusicLyricDisplaySnapshot(songID: songID, sourceKey: "native",
            lines: [.init(index: 0, time: -0.1, end: 1, text: "invalid seek")])
        let data = try JSONEncoder().encode(invalid)
        let decoded = try JSONDecoder().decode(MusicLyricDisplaySnapshot.self, from: data)
        XCTAssertEqual(decoded.lines.first?.time, -0.1, "The current document remains decodable")
        try data.write(to: leaf)
        let cold = MusicNativePresentationCache(storeFactory: {
            try DocumentStore(baseURL: root, namespace: MusicNativePresentationCache.namespace)
        }, clock: { clock.now() })
        let missing = await cold.lyrics(songID: songID, authorization: .init()), healthy = await cold.read(imageURL)
        XCTAssertNil(missing); XCTAssertEqual(healthy?.data, image)
    }

    func testPersistenceFailureDoesNotTurnDecodedNetworkBytesIntoAFailure() async throws {
        let image = try artwork(), cache = MusicNativePresentationCache(storeFactory: { throw DocumentStoreError.storageFailure })
        let accepted = await cache.store(image, for: imageURL, revision: cache.revision)
        XCTAssertEqual(accepted?.data, image)
        let coldMiss = await cache.read(imageURL); XCTAssertNil(coldMiss)
    }

    func testImageBeyondSingleItemBudgetIsNotPersisted() async throws {
        let (root, _, cache, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        var image = try artwork()
        image.append(Data(repeating: 0, count: 4 * 1024 * 1024 + 1 - image.count))
        let saved = await cache.store(image, for: imageURL, revision: cache.revision)
        let read = await cache.read(imageURL)
        XCTAssertNil(saved); XCTAssertNil(read)
        XCTAssertFalse(FileManager.default.fileExists(atPath: root.appendingPathComponent(
            MusicNativePresentationCache.namespace).path))
    }

    func testImagesAndLyricsShareTheExistingRealDiskBudgetIncludingIndex() async throws {
        let (root, _, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        var image = try artwork()
        image.append(Data(repeating: 0, count: 4 * 1024 * 1024 - image.count))
        for index in 0..<63 {
            _ = await cache.store(image, for: URL(string: "https://images.invalid/\(index)")!, revision: cache.revision)
            clock.advance(1)
        }
        await lyrics(cache); clock.advance(1)
        let newest = URL(string: "https://images.invalid/63")!
        _ = await cache.store(image, for: newest, revision: cache.revision)
        let oldest = await cache.read(URL(string: "https://images.invalid/0")!), latest = await cache.read(newest)
        let lyric = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(oldest); XCTAssertEqual(latest?.data.count, image.count); XCTAssertEqual(lyric?.lines, lines)
        let files = try FileManager.default.contentsOfDirectory(at: root.appendingPathComponent(
            MusicNativePresentationCache.namespace), includingPropertiesForKeys: [.fileSizeKey])
        let bytes = try files.reduce(0) { try $0 + $1.resourceValues(forKeys: [.fileSizeKey]).fileSize! }
        XCTAssertLessThanOrEqual(bytes, 256 * 1024 * 1024)
        XCTAssertGreaterThan(bytes, 252 * 1024 * 1024)
    }
}

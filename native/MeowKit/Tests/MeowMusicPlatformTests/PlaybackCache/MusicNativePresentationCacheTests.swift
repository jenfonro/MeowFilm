import Foundation
import CryptoKit
import Darwin
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
    private func fixture() throws -> (URL, ResourceFileStore, MusicNativePresentationCache, MusicAccountTestClock) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let store = try ResourceFileStore(baseURL: root, namespace: MusicNativePresentationCache.namespace)
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
    private func key(_ resource: String, kind: String = "image") -> String {
        kind + "-" + SHA256.hash(data: Data(resource.utf8)).map { String(format: "%02x", $0) }.joined()
    }
    private func directory(_ root: URL) -> URL {
        root.appendingPathComponent(MusicNativePresentationCache.namespace, isDirectory: true)
    }
    private func sparseLeaf(_ url: URL, bytes: Int, at: Date) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        XCTAssertTrue(FileManager.default.createFile(atPath: url.path, contents: nil))
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        try handle.truncate(atOffset: UInt64(bytes))
        try FileManager.default.setAttributes([.modificationDate: at], ofItemAtPath: url.path)
    }
    private func seedImages(_ root: URL, clock: MusicAccountTestClock) throws -> [URL] {
        let urls = (0..<63).map { URL(string: "https://images.invalid/cold-\($0)")! }
        for url in urls {
            try sparseLeaf(directory(root).appendingPathComponent(key(url.absoluteString) + ".bytes"),
                           bytes: 4 * 1024 * 1024, at: clock.now())
            clock.advance(1)
        }
        return urls
    }
    private func diskBytes(_ root: URL) throws -> Int {
        let files = try FileManager.default.contentsOfDirectory(at: directory(root),
            includingPropertiesForKeys: [.fileSizeKey])
        return try files.reduce(0) { try $0 + $1.resourceValues(forKeys: [.fileSizeKey]).fileSize! }
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
        XCTAssertEqual(try store.inventory().count, 3)
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
            try ResourceFileStore(baseURL: root, namespace: MusicNativePresentationCache.namespace)
        }, clock: { clock.now() })
        let missing = await cold.lyrics(songID: songID, authorization: .init()), healthy = await cold.read(imageURL)
        XCTAssertNil(missing); XCTAssertEqual(healthy?.data, image)
    }

    func testOnlyActualPlaybackRenewsThirtyDayRetentionAndDisplayReadsDoNot() async throws {
        let (root, store, cache, clock) = try fixture()
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
        let cold = MusicNativePresentationCache(storeFactory: { store }, clock: { clock.now() })
        let retained = await cold.read(imageURL), expired = await cold.read(other)
        let lyric = await cold.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(retained?.expiresAt, start.addingTimeInterval(59 * 86_400))
        XCTAssertNil(expired); XCTAssertEqual(lyric?.lines, lines)
        clock.advance(29 * 86_400)
        let gone = await cold.read(imageURL), oldLyrics = await cold.lyrics(songID: songID, authorization: .init())
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
            try ResourceFileStore(baseURL: root, namespace: MusicNativePresentationCache.namespace)
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

    func testImagesAndLyricsShareTheExistingRawResourceDiskBudget() async throws {
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

    func testWarmReadsAndUnchangedPlaybackLeaveRawLeavesUnchanged() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), start = clock.now()
        _ = await cache.store(image, for: imageURL, revision: cache.revision); await lyrics(cache)
        let factory = PresentationCacheTestFactory(store)
        let warm = MusicNativePresentationCache(storeFactory: { try factory.make() }, clock: { clock.now() })
        let before = Dictionary(uniqueKeysWithValues: try store.inventory().map { ($0.key, $0.modifiedAt) })
        for _ in 0..<8 {
            let hit = await warm.read(imageURL), lyric = await warm.lyrics(songID: songID, authorization: .init())
            XCTAssertEqual(hit?.data, image); XCTAssertEqual(lyric?.lines, lines)
            await warm.played(songID: songID, artwork: imageURL, confirmedAt: start)
            await warm.played(songID: songID, artwork: imageURL, confirmedAt: start.addingTimeInterval(-1))
        }
        let after = try store.inventory()
        XCTAssertEqual(factory.calls(), 1)
        XCTAssertEqual(Dictionary(uniqueKeysWithValues: after.map { ($0.key, $0.modifiedAt) }), before)
        XCTAssertTrue(after.allSatisfy { $0.stageName == nil })
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: directory(root).path).count, 2)
    }

    func testRevokedAuthorizationDoesNotReadOrWriteResourceFiles() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        await lyrics(cache)
        let factory = PresentationCacheTestFactory(store)
        let cold = MusicNativePresentationCache(storeFactory: { try factory.make() }, clock: { clock.now() })
        let revoked = MusicQueueAuthorization(); revoked.revoke()
        let denied = await cold.lyrics(songID: songID, authorization: revoked)
        await cold.storeLyrics([.init(index: 0, time: 0, end: 1, text: "denied")],
            sourceKey: "denied", songID: songID, revision: cold.lyricRevision, authorization: revoked)
        XCTAssertNil(denied); XCTAssertEqual(factory.calls(), 0)
        let healthy = await cold.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(healthy?.lines, lines); XCTAssertEqual(healthy?.sourceKey, "native")
        XCTAssertEqual(factory.calls(), 1)
    }

    func testFailedFirstImageClearDoesNotReadoptOldLeavesAfterRecoveryOrFreshWrite() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), other = URL(string: "https://images.invalid/uncleared-old-leaf")!
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        _ = await cache.store(image, for: other, revision: cache.revision); await lyrics(cache)
        let factory = PresentationCacheTestFactory(store); factory.fail(true)
        let recovering = MusicNativePresentationCache(storeFactory: { try factory.make() }, clock: { clock.now() })
        let oldRevision = recovering.revision, lyricRevision = recovering.lyricRevision
        await recovering.clearImages()
        XCTAssertNotEqual(recovering.revision, oldRevision); XCTAssertEqual(recovering.lyricRevision, lyricRevision)
        factory.fail(false)
        let cleared = await recovering.read(imageURL), healthy = await recovering.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(cleared); XCTAssertEqual(healthy?.lines, lines)
        let replacement = image + Data([1])
        _ = await recovering.store(replacement, for: imageURL, revision: recovering.revision)
        let fresh = await recovering.read(imageURL), stillCleared = await recovering.read(other)
        XCTAssertEqual(fresh?.data, replacement); XCTAssertNil(stillCleared)
        XCTAssertEqual(factory.calls(), 2)
    }

    func testClearRevokesAWriterBeforeWaitingForItsBlockedStoreFactory() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        await lyrics(cache)
        let gate = PresentationCacheTestIOGate()
        defer { gate.release() }
        let blocked = MusicNativePresentationCache(storeFactory: { gate.wait(); return store }, clock: { clock.now() })
        let image = try artwork(), url = imageURL, expected = blocked.revision
        let writer = Task.detached { await blocked.store(image, for: url, revision: expected) }
        await fulfillment(of: [gate.entered], timeout: 3)
        let clearing = Task.detached { await blocked.clearImages() }
        let deadline = Date().addingTimeInterval(3)
        while blocked.revision == expected && Date() < deadline { await Task.yield() }
        XCTAssertNotEqual(blocked.revision, expected, "Clear must revoke before the actor finishes its I/O")
        gate.release()
        let stale = await writer.value
        await clearing.value
        let miss = await blocked.read(url), healthy = await blocked.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(stale); XCTAssertNil(miss); XCTAssertEqual(healthy?.lines, lines)
        _ = await blocked.store(image, for: url, revision: blocked.revision)
        let fresh = await blocked.read(url)
        XCTAssertEqual(fresh?.data, image)
    }

    func testMissingLeafIsAnIndividualMissAndCanBeStoredAgain() async throws {
        let (root, store, cache, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork()
        _ = await cache.store(image, for: imageURL, revision: cache.revision); await lyrics(cache)
        let imageKey = try XCTUnwrap(try store.inventory().first { $0.key.hasPrefix("image-") }?.key)
        try store.remove(key: imageKey)
        let missing = await cache.read(imageURL), healthy = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(missing); XCTAssertEqual(healthy?.lines, lines)
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        let fresh = await cache.read(imageURL)
        XCTAssertEqual(fresh?.data, image)
    }

    func testCancelledWriterDoesNotReplaceHealthyResourceBytes() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), url = imageURL
        _ = await cache.store(image, for: url, revision: cache.revision)
        let gate = PresentationCacheTestIOGate()
        defer { gate.release() }
        let blocked = MusicNativePresentationCache(storeFactory: { gate.wait(); return store }, clock: { clock.now() })
        let expected = blocked.revision, replacement = image + Data([1])
        let writer = Task.detached { await blocked.store(replacement, for: url, revision: expected) }
        await fulfillment(of: [gate.entered], timeout: 3)
        writer.cancel(); gate.release()
        let cancelled = await writer.value, healthy = await blocked.read(url)
        XCTAssertNil(cancelled); XCTAssertEqual(healthy?.data, image)
        XCTAssertEqual(try store.inventory().count, 1)
    }

    func testSingleResourceByteLimitsDoNotReplaceHealthyLeaves() async throws {
        let (root, _, cache, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork()
        _ = await cache.store(image, for: imageURL, revision: cache.revision); await lyrics(cache)
        let oversized = await cache.store(Data(repeating: 0, count: 4 * 1024 * 1024 + 1),
            for: imageURL, revision: cache.revision)
        let empty = await cache.store(Data(), for: imageURL, revision: cache.revision)
        await cache.storeLyrics([.init(index: 0, time: 0, end: 1, text: String(repeating: "x", count: 1024 * 1024))],
            sourceKey: "oversized", songID: songID, revision: cache.lyricRevision, authorization: .init())
        let retainedImage = await cache.read(imageURL), retainedLyrics = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(oversized); XCTAssertNil(empty); XCTAssertEqual(retainedImage?.data, image)
        XCTAssertEqual(retainedLyrics?.lines, lines); XCTAssertEqual(retainedLyrics?.sourceKey, "native")
    }

    func testResourceStoreFencesPublicationAndOnlyTouchesChangedModificationTime() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), start = clock.now()
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        let imageKey = key(imageURL.absoluteString)
        var retainedStage: ResourceFileStore.Metadata?
        XCTAssertThrowsError(try store.write(Data([1]), key: imageKey, modifiedAt: start.addingTimeInterval(1),
            retainedStage: { retainedStage = $0 }, commit: { _ in throw CancellationError() })) {
            XCTAssertTrue($0 is CancellationError)
        }
        XCTAssertNil(retainedStage); XCTAssertEqual(try store.inventory().count, 1)
        XCTAssertEqual(try store.read(key: imageKey, maximumBytes: 4 * 1024 * 1024), image)
        for at in [start, start.addingTimeInterval(-1)] {
            XCTAssertEqual(try store.touch(key: imageKey, modifiedAt: at, commit: { _ in
                XCTFail("An unchanged mtime must not invoke the persistent mutation")
            }), start)
        }
        XCTAssertThrowsError(try store.touch(key: imageKey, modifiedAt: start.addingTimeInterval(1),
            commit: { _ in throw CancellationError() }))
        XCTAssertEqual(try store.inventory().first?.modifiedAt, start)
        let renewed = start.addingTimeInterval(1)
        XCTAssertEqual(try store.touch(key: imageKey, modifiedAt: renewed), renewed)
        XCTAssertEqual(try store.inventory().first?.modifiedAt, renewed)
        XCTAssertEqual(try store.read(key: imageKey, maximumBytes: 4 * 1024 * 1024), image)
    }

    func testResourceStoreRejectsLinkedReadsAndTouchesWithoutChangingTheTarget() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), imageKey = key(imageURL.absoluteString)
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        let leaf = directory(root).appendingPathComponent(imageKey + ".bytes")
        let target = root.appendingPathComponent("outside.bytes"), original = Data("outside".utf8)
        try original.write(to: target)
        let targetTime = try target.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
        try FileManager.default.removeItem(at: leaf)
        try FileManager.default.createSymbolicLink(at: leaf, withDestinationURL: target)
        XCTAssertThrowsError(try store.read(key: imageKey, maximumBytes: 4 * 1024 * 1024)) {
            XCTAssertEqual($0 as? DocumentStoreError, .unsafePath)
        }
        XCTAssertThrowsError(try store.touch(key: imageKey, modifiedAt: clock.now().addingTimeInterval(1))) {
            XCTAssertEqual($0 as? DocumentStoreError, .unsafePath)
        }
        try store.write(image, key: imageKey, modifiedAt: clock.now())
        XCTAssertEqual(try Data(contentsOf: target), original)
        try store.remove(key: imageKey)
        XCTAssertEqual(Darwin.link(target.path, leaf.path), 0)
        XCTAssertThrowsError(try store.read(key: imageKey, maximumBytes: 4 * 1024 * 1024)) {
            XCTAssertEqual($0 as? DocumentStoreError, .unsafePath)
        }
        XCTAssertThrowsError(try store.touch(key: imageKey, modifiedAt: clock.now().addingTimeInterval(1))) {
            XCTAssertEqual($0 as? DocumentStoreError, .unsafePath)
        }
        try store.remove(key: imageKey)
        XCTAssertEqual(try Data(contentsOf: target), original)
        XCTAssertEqual(try target.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate, targetTime)
    }

    func testFailedImageClearStaysChargedAndUnreadableAfterDirectoryRecovery() async throws {
        let (root, _, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let old = try seedImages(root, clock: clock)
        await lyrics(cache)
        let resources = directory(root), held = root.appendingPathComponent("held-current-resources", isDirectory: true)
        try FileManager.default.moveItem(at: resources, to: held)
        try Data("blocked namespace".utf8).write(to: resources)
        await cache.clearImages()
        try FileManager.default.removeItem(at: resources)
        try FileManager.default.moveItem(at: held, to: resources)
        let missing = await cache.read(old[0]), healthy = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(missing); XCTAssertEqual(healthy?.lines, lines)
        var image = try artwork()
        image.append(Data(repeating: 0, count: 4 * 1024 * 1024 - image.count))
        let freshURL = URL(string: "https://images.invalid/after-failed-clear")!
        _ = await cache.store(image, for: freshURL, revision: cache.revision)
        let fresh = await cache.read(freshURL), stillCleared = await cache.read(old[1])
        XCTAssertEqual(fresh?.data, image); XCTAssertNil(stillCleared)
        XCTAssertFalse(FileManager.default.fileExists(atPath: resources.appendingPathComponent(
            key(old[0].absoluteString) + ".bytes").path), "The failed deletion must remain charged until actually removed")
        XCTAssertLessThanOrEqual(try diskBytes(root), 256 * 1024 * 1024)
    }

    func testUnpublishedStagesAreChargedAgainstTheSameBudgetAndCannotBeRead() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let start = clock.now(), stagedURL = URL(string: "https://images.invalid/unpublished")!
        _ = try seedImages(root, clock: clock)
        let stage = "." + key(stagedURL.absoluteString) + ".bytes." + UUID().uuidString.lowercased() + ".stage"
        try sparseLeaf(directory(root).appendingPathComponent(stage), bytes: 4 * 1024 * 1024, at: start.addingTimeInterval(-1))
        XCTAssertEqual(try store.inventory().filter { $0.stageName != nil }.count, 1)
        let unpublished = await cache.read(stagedURL)
        XCTAssertNil(unpublished)
        var image = try artwork()
        image.append(Data(repeating: 0, count: 4 * 1024 * 1024 - image.count))
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        let fresh = await cache.read(imageURL)
        XCTAssertEqual(fresh?.data, image)
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory(root).appendingPathComponent(stage).path))
        XCTAssertEqual(try store.inventory().count, 64)
        XCTAssertEqual(try diskBytes(root), 256 * 1024 * 1024)
    }

    func testDirectionalClearDoesNotRemoveTheOtherKindsUnpublishedStage() async throws {
        let (root, store, cache, clock) = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        _ = await cache.store(try artwork(), for: imageURL, revision: cache.revision); await lyrics(cache)
        for leaf in try store.inventory() {
            let stage = "." + leaf.key + ".bytes." + UUID().uuidString.lowercased() + ".stage"
            try FileManager.default.moveItem(at: directory(root).appendingPathComponent(leaf.key + ".bytes"),
                to: directory(root).appendingPathComponent(stage))
        }
        let cold = MusicNativePresentationCache(storeFactory: { store }, clock: { clock.now() })
        let image = await cold.read(imageURL), lyric = await cold.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(image); XCTAssertNil(lyric)
        await cold.clearImages()
        let retained = try store.inventory()
        XCTAssertEqual(retained.count, 1); XCTAssertTrue(retained.first?.key.hasPrefix("lyrics-") == true)
        XCTAssertNotNil(retained.first?.stageName)
        await cold.clearLyrics()
        XCTAssertTrue(try store.inventory().isEmpty)
    }
}

private final class PresentationCacheTestFactory: @unchecked Sendable {
    private let store: ResourceFileStore
    private let lock = NSLock()
    private var failing = false, count = 0
    init(_ store: ResourceFileStore) { self.store = store }
    func fail(_ value: Bool) { lock.lock(); failing = value; lock.unlock() }
    func calls() -> Int { lock.lock(); defer { lock.unlock() }; return count }
    func make() throws -> ResourceFileStore {
        lock.lock(); defer { lock.unlock() }
        count += 1
        guard !failing else { throw DocumentStoreError.storageFailure }
        return store
    }
}

private final class PresentationCacheTestIOGate: @unchecked Sendable {
    let entered = XCTestExpectation(description: "store factory entered")
    private let condition = NSCondition()
    private var released = false
    func wait() {
        condition.lock(); defer { condition.unlock() }
        entered.fulfill()
        while !released { condition.wait() }
    }
    func release() { condition.lock(); released = true; condition.broadcast(); condition.unlock() }
}

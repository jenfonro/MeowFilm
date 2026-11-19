import Foundation
import CryptoKit
import Darwin
import XCTest
import MeowMusicFeature
import MeowUI
import MeowStorage
import MeowStorageTestSupport
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
    private func fixture() throws -> (URL, SQLiteDatabase, MusicNativePresentationCache, MusicAccountTestClock) {
        let database = try storageTestDatabase(self), clock = MusicAccountTestClock()
        let cache = MusicNativePresentationCache(database: database, clock: { clock.now() })
        addTeardownBlock { await cache.close() }
        return (database.dataRoot, database, cache, clock)
    }
    private func rawFixture() throws -> (URL, ResourceFileStore, MusicAccountTestClock) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return (root, try ResourceFileStore(baseURL: root, namespace: "presentation-resource-tests"), MusicAccountTestClock())
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
        root.appendingPathComponent("presentation-resource-tests", isDirectory: true)
    }

    func testCurrentResourcesRoundTripThroughASeparateColdOwnerWithoutSongImageCopies() async throws {
        let (root, database, cache, clock) = try fixture()
        let image = try artwork(), otherSong = UUID().uuidString.lowercased()
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        await lyrics(cache); await lyrics(cache, song: otherSong)
        await cache.close()
        let cold = MusicNativePresentationCache(database: database, clock: { clock.now() })
        addTeardownBlock { await cold.close() }
        let hit = await cold.read(imageURL), first = await cold.lyrics(songID: songID, authorization: .init())
        let second = await cold.lyrics(songID: otherSong, authorization: .init())
        XCTAssertEqual(hit?.data, image)
        XCTAssertEqual(first?.lines, lines); XCTAssertEqual(second?.lines, lines)
        XCTAssertEqual(first?.sourceKey, "native"); XCTAssertTrue(first?.lines[1].words.isEmpty == true)
        let rows = try await database.snapshot { try $0.query("SELECT song_id FROM ios_music_lyrics") }
        XCTAssertEqual(rows.count, 2)
        let leaves = try FileManager.default.contentsOfDirectory(at: root.appendingPathComponent("resources-v1/music-images"), includingPropertiesForKeys: nil)
        XCTAssertEqual(leaves.filter { $0.pathExtension == "bin" }.count, 1)
    }

    func testFullResourceQueriesRemainIndependentAndLyricsNeverContainImageBytes() async throws {
        let (_, database, cache, _) = try fixture()
        let otherURL = URL(string: "https://images.invalid/artwork?size=512&public=one")!
        let image = try artwork()
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        await lyrics(cache)
        let missing = await cache.read(otherURL); XCTAssertNil(missing)
        let other = image + Data([0])
        _ = await cache.store(other, for: otherURL, revision: cache.revision)
        let first = await cache.read(imageURL), second = await cache.read(otherURL)
        XCTAssertEqual(first?.data, image); XCTAssertEqual(second?.data, other)
        let rows = try await database.snapshot { try $0.query("SELECT text FROM ios_music_lyric_lines ORDER BY position") }
        XCTAssertEqual(try rows.map { try $0.text("text") }, lines.map(\.text))
        let columns = try await database.snapshot { try $0.query("PRAGMA table_info(ios_music_lyrics)") }
        XCTAssertFalse(try columns.contains { try $0.text("type") == "BLOB" })
    }

    func testImageClearRevokesOldWritersWithoutDeletingLyricsOrOtherOwners() async throws {
        let (_, database, cache, _) = try fixture()
        try await database.transaction { try $0.execute("INSERT INTO owner_revisions VALUES('durable-fixture',1,'receipt')") }
        let image = try artwork(), imageRevision = cache.revision, lyricRevision = cache.lyricRevision
        _ = await cache.store(image, for: imageURL, revision: imageRevision)
        await lyrics(cache)
        await cache.clearImages()
        XCTAssertNotEqual(cache.revision, imageRevision); XCTAssertEqual(cache.lyricRevision, lyricRevision)
        let staleWrite = await cache.store(image, for: imageURL, revision: imageRevision)
        let cleared = await cache.read(imageURL), healthy = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(staleWrite); XCTAssertNil(cleared); XCTAssertEqual(healthy?.lines, lines)
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        await cache.clearLyrics()
        let retainedImage = await cache.read(imageURL), removedLyrics = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(retainedImage?.data, image); XCTAssertNil(removedLyrics)
        await cache.clear()
        let removedImage = await cache.read(imageURL); XCTAssertNil(removedImage)
        let durable = try await database.snapshot { try $0.query("SELECT revision FROM owner_revisions WHERE owner='durable-fixture'").first!.integer("revision") }
        XCTAssertEqual(durable, 1)
    }

    func testRealWordTimingIsPreservedButInvalidMediaTimingsCannotReplaceHealthyLyrics() async throws {
        let (_, _, cache, _) = try fixture()
        await lyrics(cache)
        for invalid in [
            [MusicLyricLine(index: 0, time: -0.1, end: 1, text: "invalid")],
            [MusicLyricLine(index: 0, time: 0, end: 1, text: "invalid", words: [.init(time: 0, duration: -0.01, text: "invalid")])],
            [MusicLyricLine(index: 0, time: 2, end: 2, text: "later"), MusicLyricLine(index: 1, time: 1, end: 1, text: "earlier")]
        ] {
            await cache.storeLyrics(invalid, sourceKey: "invalid", songID: songID,
                revision: cache.lyricRevision, authorization: .init())
        }
        let hit = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(hit?.lines, lines); XCTAssertEqual(hit?.sourceKey, "native")
        let revoked = MusicQueueAuthorization(); revoked.revoke()
        let denied = await cache.lyrics(songID: songID, authorization: revoked); XCTAssertNil(denied)
    }

    func testDamagedCurrentLyricRowsDoNotDiscardAnIndependentImage() async throws {
        let (_, database, cache, clock) = try fixture()
        let image = try artwork()
        await lyrics(cache); _ = await cache.store(image, for: imageURL, revision: cache.revision)
        await cache.close()
        let id = songID
        try await database.transaction { try $0.execute("DELETE FROM ios_music_lyric_lines WHERE song_id=?", [.text(id)]) }
        let cold = MusicNativePresentationCache(database: database, clock: { clock.now() })
        addTeardownBlock { await cold.close() }
        let missing = await cold.lyrics(songID: songID, authorization: .init()), healthy = await cold.read(imageURL)
        XCTAssertNil(missing); XCTAssertEqual(healthy?.data, image)
    }

    func testOnlyActualPlaybackRenewsThirtyDayRetentionAndDisplayReadsDoNot() async throws {
        let (_, database, cache, clock) = try fixture()
        let image = try artwork(), other = URL(string: "https://images.invalid/not-played")!, start = clock.now()
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        _ = await cache.store(image, for: other, revision: cache.revision); await lyrics(cache)
        clock.advance(29 * 86_400)
        let warm = await cache.read(imageURL), displayOnly = await cache.read(other)
        XCTAssertNotNil(warm); XCTAssertNotNil(displayOnly)
        let before = try await database.snapshot { try $0.query("SELECT updated_at FROM ios_music_images") }
        XCTAssertTrue(try before.allSatisfy { try $0.double("updated_at") == start.timeIntervalSince1970 })
        await cache.played(songID: songID, artwork: imageURL, confirmedAt: clock.now())
        clock.advance(2 * 86_400)
        await cache.close()
        let cold = MusicNativePresentationCache(database: database, clock: { clock.now() })
        addTeardownBlock { await cold.close() }
        let retained = await cold.read(imageURL), expired = await cold.read(other)
        let lyric = await cold.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(retained?.expiresAt, start.addingTimeInterval(59 * 86_400))
        XCTAssertNil(expired); XCTAssertEqual(lyric?.lines, lines)
        clock.advance(29 * 86_400)
        let gone = await cold.read(imageURL), oldLyrics = await cold.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(gone); XCTAssertNil(oldLyrics)
    }

    func testCurrentSchemaRejectsNegativeSeekWithoutDiscardingAnIndependentImage() async throws {
        let (_, database, cache, _) = try fixture()
        let image = try artwork(), id = songID
        await lyrics(cache); _ = await cache.store(image, for: imageURL, revision: cache.revision)
        await storageTestThrows({ try await database.transaction { try $0.execute("UPDATE ios_music_lyric_lines SET time=-0.1 WHERE song_id=?", [.text(id)]) } }) {
            XCTAssertEqual($0 as? SQLiteStorageError, .constraint)
        }
        let lyric = await cache.lyrics(songID: songID, authorization: .init()), healthy = await cache.read(imageURL)
        XCTAssertEqual(lyric?.lines, lines); XCTAssertEqual(healthy?.data, image)
    }

    func testPersistenceFailureDoesNotTurnDecodedNetworkBytesIntoAFailure() async throws {
        let (_, database, cache, _) = try fixture()
        try storageTestReadOnly(database, true)
        let image = try artwork(), accepted = await cache.store(image, for: imageURL, revision: cache.revision)
        XCTAssertEqual(accepted?.data, image)
        let coldMiss = await cache.read(imageURL); XCTAssertNil(coldMiss)
        try storageTestReadOnly(database, false)
    }

    func testImagesAndLyricsShareTheLogicalContentBudget() async throws {
        let (root, database, cache, clock) = try fixture()
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
        let bytes = try await database.snapshot { try $0.query("SELECT (SELECT sum(bytes) FROM ios_music_images)+(SELECT sum(bytes) FROM ios_music_lyrics) AS bytes").first!.integer("bytes") }
        XCTAssertLessThanOrEqual(bytes, 256 * 1024 * 1024); XCTAssertGreaterThan(bytes, 252 * 1024 * 1024)
        let files = try FileManager.default.contentsOfDirectory(at: root.appendingPathComponent("resources-v1/music-images"), includingPropertiesForKeys: [.fileSizeKey])
        let rawBytes = try files.filter { $0.pathExtension == "bin" }.reduce(0) { try $0 + $1.resourceValues(forKeys: [.fileSizeKey]).fileSize! }
        XCTAssertLessThanOrEqual(rawBytes, bytes)
    }

    func testWarmReadsAndUnchangedPlaybackLeaveRowsUnchanged() async throws {
        let (_, database, cache, clock) = try fixture()
        let image = try artwork(), start = clock.now()
        _ = await cache.store(image, for: imageURL, revision: cache.revision); await lyrics(cache)
        let before = try await database.read { try $0.query("SELECT updated_at FROM ios_music_images").first!.double("updated_at") }
        for _ in 0..<8 {
            let hit = await cache.read(imageURL), lyric = await cache.lyrics(songID: songID, authorization: .init())
            XCTAssertEqual(hit?.data, image); XCTAssertEqual(lyric?.lines, lines)
            await cache.played(songID: songID, artwork: imageURL, confirmedAt: start)
            await cache.played(songID: songID, artwork: imageURL, confirmedAt: start.addingTimeInterval(-1))
        }
        let after = try await database.read { try $0.query("SELECT updated_at FROM ios_music_images").first!.double("updated_at") }
        XCTAssertEqual(after, before)
    }

    func testRevokedAuthorizationCannotReadOrReplaceLyrics() async throws {
        let (_, _, cache, _) = try fixture()
        await lyrics(cache)
        let revoked = MusicQueueAuthorization(); revoked.revoke()
        let denied = await cache.lyrics(songID: songID, authorization: revoked)
        await cache.storeLyrics([.init(index: 0, time: 0, end: 1, text: "denied")],
            sourceKey: "denied", songID: songID, revision: cache.lyricRevision, authorization: revoked)
        XCTAssertNil(denied)
        let healthy = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(healthy?.lines, lines); XCTAssertEqual(healthy?.sourceKey, "native")
    }

    func testMissingImageLeafIsAnIndividualMissAndCanBeStoredAgain() async throws {
        let (_, database, cache, _) = try fixture()
        let image = try artwork()
        _ = await cache.store(image, for: imageURL, revision: cache.revision); await lyrics(cache)
        let files = try FileManager.default.contentsOfDirectory(at: database.dataRoot.appendingPathComponent("resources-v1/music-images"), includingPropertiesForKeys: nil)
        for file in files where file.pathExtension == "bin" { try FileManager.default.removeItem(at: file) }
        let missing = await cache.read(imageURL), healthy = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(missing); XCTAssertEqual(healthy?.lines, lines)
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        let fresh = await cache.read(imageURL); XCTAssertEqual(fresh?.data, image)
    }

    func testCancelledWriterDoesNotReplaceHealthyResourceBytes() async throws {
        let (_, _, cache, _) = try fixture()
        let image = try artwork(), url = imageURL
        _ = await cache.store(image, for: url, revision: cache.revision)
        let gate = PresentationCacheTestIOGate()
        let writer = Task.detached {
            gate.wait()
            return await cache.store(image + Data([1]), for: url, revision: cache.revision)
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        writer.cancel(); gate.release()
        let cancelled = await writer.value, healthy = await cache.read(url)
        XCTAssertNil(cancelled); XCTAssertEqual(healthy?.data, image)
    }

    func testSingleResourceByteLimitsDoNotReplaceHealthyLeaves() async throws {
        let (_, _, cache, _) = try fixture()
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
        let (root, store, clock) = try rawFixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), start = clock.now(), imageKey = key(imageURL.absoluteString)
        try store.write(image, key: imageKey, modifiedAt: start)
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
        let (root, store, clock) = try rawFixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let image = try artwork(), imageKey = key(imageURL.absoluteString)
        try store.write(image, key: imageKey, modifiedAt: clock.now())
        let leaf = directory(root).appendingPathComponent(imageKey + ".bytes")
        let target = root.appendingPathComponent("outside.bytes"), original = Data("outside".utf8)
        try original.write(to: target)
        let targetTime = try target.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
        try FileManager.default.removeItem(at: leaf)
        try FileManager.default.createSymbolicLink(at: leaf, withDestinationURL: target)
        XCTAssertThrowsError(try store.read(key: imageKey, maximumBytes: 4 * 1024 * 1024)) {
            XCTAssertEqual($0 as? ResourceFileError, .unsafePath)
        }
        XCTAssertThrowsError(try store.touch(key: imageKey, modifiedAt: clock.now().addingTimeInterval(1))) {
            XCTAssertEqual($0 as? ResourceFileError, .unsafePath)
        }
        try store.write(image, key: imageKey, modifiedAt: clock.now())
        XCTAssertEqual(try Data(contentsOf: target), original)
        try store.remove(key: imageKey)
        XCTAssertEqual(Darwin.link(target.path, leaf.path), 0)
        XCTAssertThrowsError(try store.read(key: imageKey, maximumBytes: 4 * 1024 * 1024)) {
            XCTAssertEqual($0 as? ResourceFileError, .unsafePath)
        }
        XCTAssertThrowsError(try store.touch(key: imageKey, modifiedAt: clock.now().addingTimeInterval(1))) {
            XCTAssertEqual($0 as? ResourceFileError, .unsafePath)
        }
        try store.remove(key: imageKey)
        XCTAssertEqual(try Data(contentsOf: target), original)
        XCTAssertEqual(try target.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate, targetTime)
    }

    func testFailedFirstClearDoesNotReadoptOldRowsAfterRecoveryOrFreshWrite() async throws {
        let (_, database, cache, clock) = try fixture()
        let image = try artwork(), other = URL(string: "https://images.invalid/uncleared")!
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        _ = await cache.store(image, for: other, revision: cache.revision); await lyrics(cache)
        await cache.close()
        let recovering = MusicNativePresentationCache(database: database, clock: { clock.now() })
        addTeardownBlock { await recovering.close() }
        try storageTestReadOnly(database, true)
        await recovering.clearImages()
        try storageTestReadOnly(database, false)
        let missing = await recovering.read(imageURL), healthy = await recovering.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(missing); XCTAssertEqual(healthy?.lines, lines)
        let replacement = image + Data([1])
        _ = await recovering.store(replacement, for: imageURL, revision: recovering.revision)
        let fresh = await recovering.read(imageURL), stillCleared = await recovering.read(other)
        XCTAssertEqual(fresh?.data, replacement); XCTAssertNil(stillCleared)
    }

    func testClearRevokesImageAndLyricWritersBeforeWaitingForBlockedStorage() async throws {
        for images in [true, false] {
            let (_, database, cache, clock) = try fixture()
            let image = try artwork(), url = imageURL, id = songID, value = lines
            _ = await cache.store(image, for: url, revision: cache.revision); await lyrics(cache)
            await cache.close()
            let gate = PresentationCacheTestIOGate()
            defer { gate.release() }
            let blocked = MusicNativePresentationCache(database: database, clock: { gate.wait(); return clock.now() })
            addTeardownBlock { await blocked.close() }
            let expected = images ? blocked.revision : blocked.lyricRevision
            let writer = Task.detached {
                if images { return await blocked.store(image + Data([1]), for: url, revision: expected) }
                await blocked.storeLyrics(value, sourceKey: "stale", songID: id, revision: expected, authorization: .init())
                return nil as ArtworkBytes?
            }
            await fulfillment(of: [gate.entered], timeout: 3)
            let clearing = Task.detached {
                if images { await blocked.clearImages() } else { await blocked.clearLyrics() }
            }
            let deadline = Date().addingTimeInterval(3)
            while (images ? blocked.revision : blocked.lyricRevision) == expected && Date() < deadline { await Task.yield() }
            XCTAssertNotEqual(images ? blocked.revision : blocked.lyricRevision, expected)
            gate.release()
            let stale = await writer.value; await clearing.value
            XCTAssertNil(stale)
            let currentImage = await blocked.read(url), currentLyrics = await blocked.lyrics(songID: id, authorization: .init())
            if images { XCTAssertNil(currentImage); XCTAssertEqual(currentLyrics?.lines, value) }
            else { XCTAssertEqual(currentImage?.data, image); XCTAssertNil(currentLyrics) }
        }
    }

    func testFailedImageDeletionRemainsChargedAndUnreadableAfterDirectoryRecovery() async throws {
        let (root, database, cache, _) = try fixture()
        let image = try artwork()
        _ = await cache.store(image, for: imageURL, revision: cache.revision); await lyrics(cache)
        let resources = root.appendingPathComponent("resources-v1/music-images"), held = root.appendingPathComponent("held-resources")
        try FileManager.default.moveItem(at: resources, to: held)
        try Data("blocked namespace".utf8).write(to: resources)
        await cache.clearImages()
        let charged = try await database.read { try $0.query("SELECT bytes,ready FROM ios_music_images") }
        XCTAssertEqual(charged.count, 1); XCTAssertEqual(try charged.first?.integer("bytes"), image.count)
        XCTAssertEqual(try charged.first?.integer("ready"), 0)
        try FileManager.default.removeItem(at: resources)
        try FileManager.default.moveItem(at: held, to: resources)
        let missing = await cache.read(imageURL), healthy = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertNil(missing); XCTAssertEqual(healthy?.lines, lines)
        _ = await cache.store(image, for: imageURL, revision: cache.revision)
        let fresh = await cache.read(imageURL)
        XCTAssertEqual(fresh?.data, image)
        let rows = try await database.read { try $0.query("SELECT ready FROM ios_music_images") }
        XCTAssertEqual(rows.count, 1); XCTAssertEqual(try rows.first?.integer("ready"), 1)
    }

    func testUnpublishedImageLeavesAreRecoveredBeforeBudgetAdmissionAndLyricsClearDoesNotTouchThem() async throws {
        let (root, database, cache, clock) = try fixture()
        let image = try artwork()
        _ = await cache.store(image, for: imageURL, revision: cache.revision); await lyrics(cache)
        await cache.close()
        let resources = root.appendingPathComponent("resources-v1/music-images")
        let stage = resources.appendingPathComponent(UUID().uuidString.lowercased() + ".part")
        let orphan = resources.appendingPathComponent(UUID().uuidString.lowercased() + ".bin")
        try image.write(to: stage); try image.write(to: orphan)
        let cold = MusicNativePresentationCache(database: database, clock: { clock.now() })
        addTeardownBlock { await cold.close() }
        await cold.clearLyrics()
        XCTAssertTrue(FileManager.default.fileExists(atPath: stage.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: orphan.path))
        let hit = await cold.read(imageURL)
        XCTAssertEqual(hit?.data, image)
        XCTAssertFalse(FileManager.default.fileExists(atPath: stage.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: orphan.path))
        let lyric = await cold.lyrics(songID: songID, authorization: .init()); XCTAssertNil(lyric)
        let rows = try await database.read { try $0.query("SELECT filename FROM ios_music_images") }
        XCTAssertEqual(rows.count, 1)
    }

    func testCancelledReplacementAfterWritingRowsPreservesTheCommittedResource() async throws {
        let (_, database, cache, clock) = try fixture()
        let image = try artwork(), id = songID, value = lines
        _ = await cache.store(image, for: imageURL, revision: cache.revision); await lyrics(cache)
        await cache.close()
        let gate = PresentationCacheTestIOGate()
        defer { gate.release() }
        let blocked = MusicNativePresentationCache(database: database, clock: { gate.wait(); return clock.now() })
        addTeardownBlock { await blocked.close() }
        let expected = blocked.lyricRevision
        let writer = Task.detached {
            await blocked.storeLyrics(value, sourceKey: "cancelled", songID: id, revision: expected, authorization: .init())
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        writer.cancel(); gate.release(); await writer.value
        let healthy = await blocked.lyrics(songID: id, authorization: .init())
        XCTAssertEqual(healthy?.sourceKey, "native"); XCTAssertEqual(healthy?.lines, value)
    }

}

private final class PresentationCacheTestIOGate: @unchecked Sendable {
    let entered = XCTestExpectation(description: "store factory entered")
    private let condition = NSCondition()
    private var released = false
    func wait() {
        condition.lock(); defer { condition.unlock() }
        if !released { entered.fulfill() }
        while !released { condition.wait() }
    }
    func release() { condition.lock(); released = true; condition.broadcast(); condition.unlock() }
}

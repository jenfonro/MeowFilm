import Foundation
import XCTest
import MeowMusicFeature
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
    private func artwork() throws -> Data {
        try XCTUnwrap(Data(base64Encoded:
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5HcAAAAASUVORK5CYII="))
    }
    private func lyrics(_ cache: MusicNativePresentationCache, song: String? = nil) async {
        await cache.storeLyrics(lines, sourceKey: "native", songID: song ?? songID,
            revision: cache.lyricRevision, authorization: .init())
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
        let retained = await cache.read(imageURL), expired = await cache.read(other)
        let lyric = await cache.lyrics(songID: songID, authorization: .init())
        XCTAssertEqual(retained?.expiresAt, start.addingTimeInterval(59 * 86_400))
        XCTAssertNil(expired); XCTAssertEqual(lyric?.lines, lines)
        clock.advance(29 * 86_400)
        let gone = await cache.read(imageURL), oldLyrics = await cache.lyrics(songID: songID, authorization: .init())
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
}

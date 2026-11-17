import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeMiguSearchTests: XCTestCase {
    func testExpandedEditionsAndDuplicateOccurrencesAreNotTruncatedByRequestedLimit() async throws {
        let rows = (0..<201).map { MusicNativeCandidateDiscoveryFixture.track("mg", id: String(1000 + $0), title: "Edition \($0)") }
        let peer = NativeMusicResultFixture([
            try MusicPlatformFixture.page(rows, platform: "mg", total: 1),
            try MusicPlatformFixture.page([rows[0], rows[0]], platform: "mg", total: 2)
        ])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.search(platform: "mg", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(page.items.count, 201); XCTAssertEqual(page.total, 1); XCTAssertNil(page.nextPage)
        XCTAssertEqual(Set(page.items.flatMap(\.sources).map(\.nativeId)).count, 201)
        let repeated = try await service.search(platform: "mg", query: "Synthetic", page: 1, limit: 30)
        XCTAssertEqual(repeated.items.count, 2)
        XCTAssertEqual(repeated.items[0].songId, repeated.items[1].songId)
        _ = try await service.changeQueue(.replace(page.items.map(\.ref), index: 200), expectedRevision: 0)
        let queue = try await service.queue()
        XCTAssertEqual(Set(queue.entries.map(\.id)).count, 201)
        XCTAssertEqual(queue.currentId, queue.entries.last?.id)
        await service.close()
    }

    @MainActor func testSearchConsumerUsesProviderContinuationForShortExpandedAndEmptyPages() async throws {
        for count in [0, 2, 40] {
            let rows = (0..<count).map { MusicNativeCandidateDiscoveryFixture.track("mg", id: String(1000 + $0), title: "Edition \($0)") }
            let peer = NativeMusicResultFixture([
                try MusicPlatformFixture.page(rows, platform: "mg", total: 100, continuation: .object(["page": .number(2)])),
                try MusicPlatformFixture.page([MusicNativeCandidateDiscoveryFixture.track("mg", id: "9999", title: "Tail")], platform: "mg", page: 2)
            ])
            let service = try await NativeMusicFixture.service(platform: peer)
            let results = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
                try await service.search(platform: platform.rawValue, query: query, page: page, limit: limit)
            }, readPlaylists: { _, _, _, _ in throw MusicServiceError.unsupported },
                readRecording: { _ in throw MusicServiceError.unsupported })
            results.setAccess(available: true, generation: UUID()); results.appear()
            results.submit("Synthetic", preferences: .init(songPlatforms: [.migu], searchPlaylists: false, pages: 1, concurrency: 1))
            await results.finishPendingReads()
            results.more(.songs); await results.finishPendingReads()
            XCTAssertEqual(results.songs.count, count + 1); XCTAssertFalse(results.moreSongs)
            XCTAssertTrue(results.songFailures.isEmpty)
            let requests = await peer.calls(); XCTAssertEqual(requests.count, 2)
            XCTAssertEqual(requests[1].parameters[music: "page"], .number(2))
            results.disappear(); await service.close()
        }
    }

    func testCanonicalMusicInfoKeepsActualLocatorAndFormatsButNotPlayableURLsOrCredentials() async throws {
        var info = try MJ.parse(MusicNativeCandidateDiscoveryFixture.track("mg", id: "123").data)
        info["copyrightId"] = .string("600123"); info["contentId"] = .string("60090001")
        info["audioFormats"] = .array([.obj([("aformat", .string("020010")), ("hash", .string("synthetic-checksum")),
            ("url", .string("https://private.invalid/signed"))])])
        info["Cookie"] = .string("synthetic-not-a-cookie"); info["token"] = .string("synthetic-not-a-token")
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([.init(platform: "mg", data: info.bytes)], platform: "mg")])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let page = try await service.search(platform: "mg", query: "Synthetic", page: 1, limit: 30)
        let track = try XCTUnwrap(page.items.first)
        let document = try MusicDocument(store.snapshot().0, runID: "inspect")
        let song = (try document.fixtureSong(track.songId))
        let locator = try XCTUnwrap(song["sources"].array.first)["musicInfo"]
        XCTAssertEqual(locator["songmid"].string, "123"); XCTAssertEqual(locator["copyrightId"].string, "600123")
        XCTAssertEqual(locator["contentId"].string, "60090001")
        XCTAssertEqual(locator["audioFormats"].array[0]["aformat"].string, "020010")
        XCTAssertFalse(song.json.contains("private.invalid")); XCTAssertFalse(song.json.contains("synthetic-not"))
        await service.close()
    }

    func testAutomaticConsumerDiscoversOnlyPlayablePlatformAndKeepsRepositoryIdentity() async throws {
        for platform in ["kw", "mg"] {
            let store = try MusicNativeCandidateDiscoveryFixture.store(), scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.supported([platform])
            let peer = MusicNativeDiscoveryPlatforms()
            let service = try await NativeMusicFixture.service(store, scripts: scripts,
                platform: NativeMusicFixture.routes(primary: MusicNativeCandidateDiscoveryQQ(), others: peer))
            let before = try await service.queue()
            let consumer = MusicCandidateDiscoveryConsumer(provider: service)
            let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: .automatic())
            XCTAssertEqual(result.target.selection.siteId, platform); XCTAssertEqual(result.attempts, 1)
            let searches = await peer.calls(); XCTAssertEqual(searches, [platform])
            let calls = await scripts.calls(); XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].siteID, platform)
            let queue = try await service.queue()
            XCTAssertEqual(queue.entries[0].track.songId, before.entries[0].track.songId)
            XCTAssertEqual(queue.entries[0].id, before.entries[0].id)
            XCTAssertTrue(queue.entries[0].track.sources.contains { $0.platform == platform })
            let facts = try await service.playedSongs(); XCTAssertTrue(facts.isEmpty)
            await service.close()
        }
    }
}

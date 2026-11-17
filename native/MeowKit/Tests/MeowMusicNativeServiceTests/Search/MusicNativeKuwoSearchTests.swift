import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeKuwoSearchTests: XCTestCase {
    func testCanonicalSearchPreservesLongLocatorAndRequestMeaningWithoutAccountOrScript() async throws {
        let track = MusicNativeCandidateDiscoveryFixture.track("kw", id: "9007199254740993", title: "A & B", artist: "One、Two")
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([track], platform: "kw", total: 61, page: 2)])
        let service = try await NativeMusicFixture.service(platform: peer)
        let inventory = try await service.platforms()
        XCTAssertEqual(inventory.map(\.id), ["wy", "tx", "kg", "kw", "mg"])
        let before = await peer.calls(); XCTAssertTrue(before.isEmpty)
        let page = try await service.search(platform: "kw", query: "A + B & 中文", page: 2, limit: 30)
        XCTAssertEqual(page.total, 61); XCTAssertNil(page.nextPage)
        let result = try XCTUnwrap(page.items.first)
        XCTAssertEqual(result.sources[0].nativeId, "9007199254740993")
        XCTAssertEqual(result.title, "A & B"); XCTAssertEqual(result.artist, "One、Two")
        XCTAssertNotNil(UUID(uuidString: result.songId))
        let calls = await peer.calls()
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].platform, "kw"); XCTAssertEqual(calls[0].operation, "search")
        XCTAssertEqual(calls[0].parameters[music: "keyword"], .string("A + B & 中文"))
        XCTAssertEqual(calls[0].parameters[music: "page"], .number(2))
        await service.close()
    }

    func testMissingOptionalArtworkStillPersistsAndPlaysFromTheSameRepository() async throws {
        for platform in ["kw", "mg"] {
            let input = MusicNativeCandidateDiscoveryFixture.track(platform, id: "123")
            let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([input], platform: platform)])
            let store = NativeMusicTestLibrary(), scripts = MusicNativeCandidateDiscoveryScripts()
            await scripts.supported([platform])
            let service = try await NativeMusicFixture.service(store, scripts: scripts, platform: peer)
            let page = try await service.search(platform: platform, query: "Synthetic", page: 1, limit: 30)
            let first = try XCTUnwrap(page.items.first)
            XCTAssertNil(first.artwork)
            let playlist = try await service.createPlaylist(name: "Synthetic", refs: [first.ref])
            let saved = try await service.tracks(in: playlist)
            XCTAssertEqual(saved.map(\.songId), [first.songId]); XCTAssertEqual(saved[0].sources, first.sources)
            _ = try await service.changeQueue(.replace([saved[0].ref, saved[0].ref], index: 1), expectedRevision: 0)
            let queue = try await service.queue()
            XCTAssertNotEqual(queue.entries[0].id, queue.entries[1].id)
            XCTAssertEqual(queue.entries.map { $0.track.songId }, [first.songId, first.songId])
            let options = try await service.sourceAvailability(for: saved[0].ref).options
            let option = try XCTUnwrap(options.first)
            let before = store.snapshot()
            _ = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: queue.entries[1].id,
                selection: option.selection(quality: "128k"))
            let calls = await scripts.calls()
            XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].siteID, platform)
            XCTAssertEqual(calls[0].sourceKey, first.sources[0].sourceKey)
            XCTAssertEqual(try MJ.parse(calls[0].musicInfo)["songmid"].string, "123")
            XCTAssertEqual(store.snapshot().0, before.0)
            let facts = try await service.playedSongs(); XCTAssertTrue(facts.isEmpty)
            let requests = await peer.calls(); XCTAssertEqual(requests.count, 1)
            let document = try MusicDocument(store.snapshot().0, runID: "inspect")
            XCTAssertEqual(document.queue["entries"].array.map { $0["songId"].string }, [first.songId, first.songId])
            XCTAssertTrue(document.queue["entries"].array.allSatisfy { !$0.contains("song") })
            await service.close()
        }
    }

    func testMoreThanFormerLimitAndShortPageUseCanonicalContinuationWithoutTruncation() async throws {
        let rows = (1...131).map { MusicNativeCandidateDiscoveryFixture.track("kw", id: String($0), title: "Song \($0)") }
        let peer = NativeMusicResultFixture([
            try MusicPlatformFixture.page(rows, platform: "kw", total: 200, limit: 101, continuation: .object(["page": .number(2)])),
            try MusicPlatformFixture.page([rows[0], rows[0]], platform: "kw", total: 202, page: 2, continuation: .object(["page": .number(3)])),
            try MusicPlatformFixture.page([], platform: "kw", total: 202, page: 3)
        ])
        let service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.search(platform: "kw", query: String(repeating: "a", count: 201), page: 1, limit: 101)
        XCTAssertEqual(first.items.count, 131); XCTAssertEqual(first.nextPage, 2)
        let next = try await service.search(platform: "kw", query: "Synthetic", page: 2, limit: 101)
        XCTAssertEqual(next.items.count, 2); XCTAssertEqual(next.nextPage, 3)
        XCTAssertEqual(next.items[0].songId, next.items[1].songId)
        let end = try await service.search(platform: "kw", query: "Synthetic", page: 3, limit: 101)
        XCTAssertTrue(end.items.isEmpty); XCTAssertNil(end.nextPage)
        await service.close()
    }

    func testCanonicalFailuresKeepTheirClassificationWithoutRetryOrInventedEmptySuccess() async throws {
        for expected in [MusicServiceError.rateLimited, .upstreamFailed, .requestRejected, .upstreamInvalid, .limit] {
            let peer = NativeMusicResultFixture(failure: expected)
            let service = try await NativeMusicFixture.service(platform: peer)
            await assertMusicFailure(expected) { _ = try await service.search(platform: "kw", query: "Synthetic", page: 1, limit: 30) }
            let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
            await service.close()
        }
    }

    func testCancellationAndRevocationRejectLateCanonicalResultsWithoutRepositoryWrites() async throws {
        for revoke in [false, true] {
            let gate = NativeMusicGate(), store = NativeMusicTestLibrary()
            let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([MusicNativeCandidateDiscoveryFixture.track("kw")], platform: "kw")], gate: gate)
            let service = try await NativeMusicFixture.service(store, platform: peer)
            let task = Task { try await service.search(platform: "kw", query: "Synthetic", page: 1, limit: 30) }
            await gate.started()
            if revoke { service.revokeAccess(generation: 2) } else { task.cancel() }
            await gate.release(); await peer.waitUntilIdle()
            do { _ = try await task.value; XCTFail("late result accepted") }
            catch {
                if revoke { XCTAssertTrue([MusicServiceError.staleAccess, .accessUnavailable, .cancelled].contains(error as? MusicServiceError ?? .invalidResponse)) }
                else { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
            }
            XCTAssertEqual(store.snapshot().2, 0)
            await service.close()
        }
    }
}

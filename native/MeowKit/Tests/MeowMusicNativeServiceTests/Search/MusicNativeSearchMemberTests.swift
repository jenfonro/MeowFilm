import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeSearchMemberTests: XCTestCase {
    private let collection = MusicNativePublicBrowseFixture.collection
    func testCanonicalMemberDataIsAdoptedWithoutSupplementAndPermanentIDsSurviveTime() async throws {
        let ids = ["17", MusicNativePublicBrowseFixture.id, "23"], clock = NativeMusicFixtureClock()
        let page = try MusicNativePublicBrowseFixture.page(ids), peer = NativeMusicResultFixture([page, page])
        let service = try await NativeMusicFixture.service(platform: peer, clock: { clock.now() })
        let first = try await service.publicDetail(collection, cursor: nil)
        clock.advance(601)
        let second = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(first.members.map(\.nativeID), ids); XCTAssertEqual(first.members.map(\.sourcePosition), [0, 1, 2])
        XCTAssertEqual(first.tracks.map(\.songId), second.tracks.map(\.songId))
        XCTAssertTrue(first.tracks.allSatisfy { $0.artwork != nil && $0.artist == "Artist" && $0.album == "Album" })
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist", "playlist"])
        await service.close()
    }
    func testCanonicalCompletedFieldsNeedNoNativeMetadataOrAudioRequests() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(["17", "18", "19", "20"])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let result = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(result.tracks.map(\.title), ["Song 17", "Song 18", "Song 19", "Song 20"])
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist"])
        await service.close()
    }
    func testCanonicalDifferentIDsAndRepeatedOccurrencesAreAcceptedNotComparedToOldList() async throws {
        for ids in [["999"], ["17", "17"]] {
            let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(ids)])
            let service = try await NativeMusicFixture.service(platform: peer)
            let result = try await service.publicDetail(collection, cursor: nil)
            XCTAssertEqual(result.members.map(\.nativeID), ids)
            XCTAssertEqual(Set(result.tracks.map { $0.ref.occurrence }).count, ids.count)
            if ids.count == 2 { XCTAssertEqual(result.tracks[0].songId, result.tracks[1].songId) }
            await service.close()
        }
    }
    func testMissingCanonicalLocatorKeepsDisplayWhileOnlyIdentifiedSongsAreAdopted() async throws {
        let good = try MusicPlatformFixture.song(MusicNativePublicBrowseFixture.track("17", title: "Song 17"))
        let bad: RuntimeValue = .object(["title": .string("No locator"), "sources": .array([])])
        let peer = NativeMusicResultFixture([.object(["id": .string("41"), "items": .array([good, bad]), "total": .number(2)])])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let page = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(page.tracks.map(\.title), ["Song 17", "No locator"])
        XCTAssertFalse(page.tracks[0].songId.isEmpty); XCTAssertTrue(page.tracks[1].songId.isEmpty)
        let document = try await service.read()
        XCTAssertEqual(try document.songRows.allSongIDs().count, 1)
        await service.close()
    }
    func testCanonicalAliasMemberIsUsedWithoutRequestIdentityRevalidation() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(["canonical-999"], collectionID: "canonical-list")])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(page.collection.nativeID, "canonical-list"); XCTAssertEqual(page.members.map(\.nativeID), ["canonical-999"])
        await service.close()
    }
    func testMissingMembersKeepAbsolutePositionsAndDoNotFillPreviewFromLaterRanks() async throws {
        let firstIDs = (1...30).filter { $0 != 2 }.map(String.init)
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(firstIDs, total: 35,
            positions: (0..<30).filter { $0 != 1 }.map { Optional($0) }, consumed: 30, continuation: .object(["page": .number(2)])),
            try MusicNativePublicBrowseFixture.page(["31", "33", "34", "35"], total: 35, positions: [30, 32, 33, 34], consumed: 5)])
        let service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(first.members.map(\.nativeID), firstIDs); XCTAssertEqual(first.consumed, 30)
        XCTAssertEqual(first.members.filter { ($0.sourcePosition ?? Int.max) < 20 }.count, 19)
        let second = try await service.publicDetail(collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.members.map(\.sourcePosition), [30, 32, 33, 34]); XCTAssertNil(second.cursor)
        await service.close()
    }
    func testEntirelyUnavailablePageAdvancesOnlyProviderContinuationWithoutPlaceholders() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page([], total: 31, consumed: 30,
            continuation: .object(["page": .number(2), "offset": .number(30)])),
            try MusicNativePublicBrowseFixture.page(["31"], total: 31, positions: [30])])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        XCTAssertTrue(first.tracks.isEmpty); XCTAssertEqual(first.consumed, 30); XCTAssertEqual(store.snapshot().2, 0)
        let second = try await service.publicDetail(collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.members.map(\.nativeID), ["31"]); XCTAssertEqual(second.members.map(\.sourcePosition), [30])
        await service.close()
    }
    func testSearchAndDetailReadTheSamePermanentRepositoryRecords() async throws {
        let track = MusicNativePublicBrowseFixture.track("17", title: "Song 17", artwork: "https://artwork.invalid/song-17.jpg")
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([track], platform: "wy"), try MusicNativePublicBrowseFixture.page(["17"])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let search = try await service.search(platform: "wy", query: "Song", page: 1, limit: 30)
        let detail = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(search.items[0].songId, detail.tracks[0].songId)
        XCTAssertEqual(search.items[0].ref.id, detail.tracks[0].ref.id)
        await service.close()
    }
    func testCurrentDetailMetadataChangesWithoutChangingPermanentReferenceOrQueue() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(["17"]),
            try MusicNativePublicBrowseFixture.page(["17"], title: "Current collection")])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil), before = try await service.read()
        let current = try await service.publicDetail(collection, cursor: nil), after = try await service.read()
        XCTAssertEqual(first.tracks[0].songId, current.tracks[0].songId); XCTAssertEqual(current.collection.title, "Current collection")
        XCTAssertEqual(after.queue, before.queue); XCTAssertEqual(after.playback, before.playback)
        await service.close()
    }
    func testRefreshForwardsExplicitInvalidationAndAdoptsCurrentFields() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(["17"]), try MusicNativePublicBrowseFixture.page(["18"])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        await service.refreshPublicCollection(collection)
        let next = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(first.members.map(\.nativeID), ["17"]); XCTAssertEqual(next.members.map(\.nativeID), ["18"])
        await service.close()
    }
    func testAccessChangeRejectsOldCursorButKeepsRepositoryIdentityAfterUnlock() async throws {
        let response = try MusicNativePublicBrowseFixture.page(["17"], total: 31, continuation: .object(["page": .number(2)]))
        let peer = NativeMusicResultFixture([response, response]), service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        try await service.setAccess(available: false, generation: 2)
        await assertMusicFailure(.accessUnavailable) { _ = try await service.publicDetail(self.collection, cursor: nil) }
        try await service.setAccess(available: true, generation: 3)
        await assertMusicFailure(.staleResult) { _ = try await service.publicDetail(self.collection, cursor: first.cursor) }
        let fresh = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(fresh.tracks[0].songId, first.tracks[0].songId)
        await service.close()
    }
    func testPermanentReferencesStayUsableBeyondFormerHandleCapacity() async throws {
        let peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(["17"])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil), ref = try XCTUnwrap(first.tracks.first).ref
        let filler = (1...650).map { NativeMusicFixture.track("Capacity filler \($0)") }
        _ = try await service.metadataPage(.init(items: filler))
        let state = try await service.changeQueue(.replace([ref, ref], index: 1), expectedRevision: 0)
        let queue = try await service.queue(); XCTAssertEqual(state.currentId, queue.entries[1].id)
        XCTAssertEqual(queue.entries.map(\.track.songId), [first.tracks[0].songId, first.tracks[0].songId])
        await service.close()
    }
    func testMemberPageAllRowsResolveFromRepositoryAtEqualClockWithoutReservations() async throws {
        let ids = (1...650).map(String.init), peer = NativeMusicResultFixture([try MusicNativePublicBrowseFixture.page(ids)])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(page.tracks.count, ids.count); XCTAssertEqual(page.members.map(\.nativeID), ids)
        let records = try await service.repositoryTracks(songIDs: Set(page.tracks.map(\.songId)))
        XCTAssertEqual(records.count, ids.count)
        await service.close()
    }
}

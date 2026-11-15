import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

private actor KGPagedBrowsePeer: MusicPlatformFixtureResponding {
    private var sent: [MusicPlatformFixture.Request] = []
    private var revision = 0, title = "Synthetic collection"
    private var ending = false, changedTotal = false
    func changeObservedMember() { revision += 1 }
    func changeMetadata() { title = "Changed collection" }
    func configure(end: Bool = false, totalChanged: Bool = false) { ending = end; changedTotal = totalChanged }
    func calls() -> [MusicPlatformFixture.Request] { sent }
    func respond(_ request: MusicPlatformFixture.Request) throws -> RuntimeValue {
        sent.append(request)
        let offset = Int(request.parameters[music: "continuation"][music: "offset"].musicNumber ?? 0)
        let complete = request.operation == "playlist.complete"
        let count = complete ? 65 : ending && offset > 0 ? 0 : min(27, 65 - offset)
        let end = offset + count
        let firstID = 1 + revision * 1000
        let ids = (offset..<end).map { index -> String in
            let id = index == 0 ? firstID : index + 1
            return String(id)
        }
        return try MusicNativePublicBrowseFixture.page(ids, platform: "kg", collectionID: "synthetic-list", title: title,
            total: changedTotal && offset > 0 ? 66 : 65, positions: (offset..<end).map { Optional($0) },
            continuation: !complete && !ending && end < 65 ? .object(["page": .number(Double(sent.count + 1)), "offset": .number(Double(end)), "opaque": .string("kg-next")]) : .null)
    }
}
final class MusicNativePublicBrowsePagingTests: XCTestCase {
    private let collection = MusicPublicBrowseCollection(platform: .kugou, nativeID: "synthetic-list", title: "Stale title")
    func testRepeatedDynamicDetailReadsDoNotRequireNativeWitnesses() async throws {
        let peer = KGPagedBrowsePeer(), service = try await NativeMusicFixture.service(platform: peer)
        var ids = Set<String>()
        for _ in 0..<65 {
            await peer.changeObservedMember()
            let page = try await service.publicDetail(collection, cursor: nil)
            ids.insert(try XCTUnwrap(page.tracks.first?.sources.first?.nativeId))
        }
        XCTAssertEqual(ids.count, 65)
        let queue = try await service.queue(); XCTAssertTrue(queue.entries.isEmpty); XCTAssertEqual(queue.revision, 0)
        await service.close()
    }
    func testKGDetailAndCompleteConsumeCanonicalCollectionMetadata() async throws {
        let peer = KGPagedBrowsePeer(), service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        let second = try await service.publicDetail(collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(first.collection.title, "Synthetic collection"); XCTAssertEqual(second.collection.title, first.collection.title)
        let source = try await service.openPublicQueue(.init(collection: first.collection))
        let rows = try await source.collectionTracks(intent: .init()); XCTAssertEqual(rows.count, 65)
        XCTAssertEqual(source.context.title, "Synthetic collection")
        await source.close(); await service.close()
    }
    func testKGCurrentSummaryReplacesStaleDisplayWithoutComparingSnapshots() async throws {
        let peer = KGPagedBrowsePeer(), service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        await peer.changeMetadata()
        let renewed = try await service.publicDetail(collection, cursor: nil)
        XCTAssertNotEqual(renewed.collection.title, first.collection.title)
        let next = try await service.publicDetail(collection, cursor: renewed.cursor)
        XCTAssertEqual(next.collection.title, "Changed collection")
        let source = try await service.openPublicQueue(.init(collection: first.collection))
        XCTAssertEqual(source.context.title, "Changed collection")
        await source.close(); await service.close()
    }
    func testKGExplicitRefreshRevokesOpenedAuthorityButOldSummaryCanOpenCurrentCollection() async throws {
        let peer = KGPagedBrowsePeer(), service = try await NativeMusicFixture.service(platform: peer)
        let old = try await service.openPublicQueue(.init(collection: collection))
        await service.refreshPublicCollection(collection)
        XCTAssertFalse(old.authorization.isActive)
        let current = try await service.openPublicQueue(.init(collection: collection))
        XCTAssertTrue(current.authorization.isActive)
        await old.close(); await current.close(); await service.close()
    }
    func testKGShortPagesForwardProviderConsumedAndContinuationAndCompleteReadsOnce() async throws {
        let peer = KGPagedBrowsePeer(), service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(first.tracks.count, 27); XCTAssertEqual(first.consumed, 27)
        let second = try await service.publicDetail(collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.members.first?.sourcePosition, 27)
        let source = try await service.openPublicQueue(.init(collection: second.collection))
        let rows = try await source.collectionTracks(intent: .init()); XCTAssertEqual(rows.count, 65)
        let calls = await peer.calls()
        XCTAssertEqual(calls.map(\.operation), ["playlist", "playlist", "playlist.complete"])
        XCTAssertEqual(calls[1].parameters[music: "continuation"][music: "offset"], .number(27))
        XCTAssertEqual(calls[1].parameters[music: "continuation"][music: "opaque"], .string("kg-next"))
        await source.close(); await service.close()
    }
    func testKGChangedObservedMemberIsAcceptedByCurrentExplicitPlayback() async throws {
        let peer = KGPagedBrowsePeer(), service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        await peer.changeObservedMember()
        let source = try await service.openPublicQueue(.init(collection: first.collection))
        _ = try await service.replaceRemoteQueue(source, start: .init(index: 0), expectedRevision: 0, intent: .init())
        let queue = try await service.queue(); XCTAssertEqual(queue.entries.first?.track.sources.first?.nativeId, "1001")
        await source.close(); await service.close()
    }
    func testKGLaterPageRetainsItsActualTotalWithoutMatchingPreviousCount() async throws {
        let peer = KGPagedBrowsePeer(), service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        await peer.configure(totalChanged: true)
        let second = try await service.publicDetail(collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(first.total, 65); XCTAssertEqual(second.total, 66); XCTAssertEqual(second.tracks.count, 27)
        await service.close()
    }
    func testKGEndResultIsAnEmptyLastPageWithoutPlaceholders() async throws {
        let peer = KGPagedBrowsePeer(), service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.publicDetail(collection, cursor: nil)
        await peer.configure(end: true)
        let second = try await service.publicDetail(collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertTrue(second.tracks.isEmpty); XCTAssertNil(second.cursor); XCTAssertEqual(second.total, first.total)
        await service.close()
    }
}

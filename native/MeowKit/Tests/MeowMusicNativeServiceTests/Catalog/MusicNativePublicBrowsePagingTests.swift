import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor KGPagedBrowseHTTP: MusicNativeHTTPTransport {
    var calls: [(Int, Int)] = []
    var changed = false
    var displayVersion = 1
    var omitListInfoAfterFirstPage = false
    var endCodeAfterFirstPage = false
    var mismatchedCountAfterFirstPage = false
    var observedMemberRevision = 1
    var metadata = KGPagedBrowseMetadata.initial
    func changeObservedMember() { changed = true }
    func changeHead() { displayVersion += 1 }
    func changeObservedMemberOnly() { observedMemberRevision += 1 }
    func changeMetadata(to value: KGPagedBrowseMetadata) { metadata = value }
    func configure(omitListInfo: Bool = false, endCode: Bool = false, mismatch: Bool = false) {
        omitListInfoAfterFirstPage = omitListInfo
        endCodeAfterFirstPage = endCode
        mismatchedCountAfterFirstPage = mismatch
    }
    func send(_ request: MusicNativeHTTPRequest) throws -> MusicNativeHTTPResponse {
        XCTAssertEqual(request.url.path, "/pubsongs/v2/get_other_list_file_nofilt")
        XCTAssertNil(request.headers["Cookie"]); XCTAssertNil(request.headers["Authorization"])
        let fields = Dictionary(uniqueKeysWithValues: try XCTUnwrap(URLComponents(url: request.url,
            resolvingAgainstBaseURL: false)?.queryItems).map { ($0.name, $0.value ?? "") })
        let offset = try XCTUnwrap(Int(fields["begin_idx"] ?? "")), limit = try XCTUnwrap(Int(fields["pagesize"] ?? ""))
        calls.append((offset, limit))
        if endCodeAfterFirstPage && offset > 0 {
            return .init(status: 200, data: MJ.obj([("status", .n(1)), ("error_code", .n(149)),
                ("data", .obj([("songs", .array([])), ("count", .n(0)), ("list_info", .null)]))]).bytes)
        }
        let end = min(offset + min(limit, 27), 65)
        let songs = (offset..<end).map { index -> MJ in
            let raw = changed && index == 0 ? 1000 : index + 1
            let hash = String(format: "%032X", observedMemberRevision > 1 ? raw + observedMemberRevision : raw)
            return .obj([("hash", .string(hash)), ("name", .string("Synthetic Artist - Song\(index)"))])
        }
        let dataCount = mismatchedCountAfterFirstPage && offset > 0 ? 66 : 65
        var data = MJ.obj([("count", .n(dataCount)), ("songs", .array(songs))])
        if offset == 0 || !omitListInfoAfterFirstPage {
            data["list_info"] = .obj([("global_collection_id", .string("synthetic-list")),
                ("name", .string(metadata.title)), ("imgurl", .string(metadata.artwork)),
                ("count", .n(65)), ("list_ver", .n(displayVersion))])
        }
        let body = MJ.obj([("status", .n(1)), ("error_code", .n(0)), ("data", data)])
        return .init(status: 200, data: body.bytes)
    }
}
private struct KGPagedBrowseMetadata: Sendable {
    let title: String
    let artwork: String
}
private extension KGPagedBrowseMetadata {
    static let initial = KGPagedBrowseMetadata(title: "Synthetic collection", artwork: "https://imge.kugou.com/new.jpg")
    static let changed = KGPagedBrowseMetadata(title: "Changed collection", artwork: "https://imge.kugou.com/changed.jpg")
}
final class MusicNativePublicBrowsePagingTests: XCTestCase {
    private let collection = MusicPublicBrowseCollection(platform: .kugou, nativeID: "synthetic-list", title: "Synthetic collection")
    func testNewDetailEvidenceUsesMemberBudgetAndExpiryRatherThanAnOwnerCount() async throws {
        let http = KGPagedBrowseHTTP(), clock = NativeMusicFixtureClock()
        let service = try await NativeMusicFixture.service(http: http, clock: { clock.now() })
        let platforms = await service.publicPlatforms
        var snapshots = Set<String>()
        for _ in 0..<65 {
            await http.changeObservedMemberOnly()
            let page = try await platforms.browsePagedDetail(collection, offset: 0, snapshot: "")
            snapshots.insert(page.snapshot)
        }
        let retained = await platforms.detailWitnesses.count
        XCTAssertEqual(snapshots.count, 65); XCTAssertEqual(retained, 65)
        clock.advance(601)
        await http.changeObservedMemberOnly()
        _ = try await platforms.browsePagedDetail(collection, offset: 0, snapshot: "")
        let refreshed = await platforms.detailWitnesses.count
        XCTAssertEqual(refreshed, 1)
        await service.close()
    }
    func testKGDetailAndCompleteReuseFirstPageMetadataWhenLaterPagesOmitListInfo() async throws {
        let stale = MusicPublicBrowseCollection(platform: .kugou, nativeID: "synthetic-list", title: "Stale catalog title",
            artwork: URL(string: "https://imge.kugou.com/stale.jpg"))
        let http = KGPagedBrowseHTTP()
        await http.configure(omitListInfo: true)
        let service = try await NativeMusicFixture.service(http: http)
        let first = try await service.publicDetail(stale, cursor: nil)
        let second = try await service.publicDetail(stale, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(first.collection.title, "Synthetic collection")
        XCTAssertEqual(first.collection.artwork, URL(string: "https://imge.kugou.com/new.jpg"))
        XCTAssertEqual(second.collection.title, first.collection.title)
        XCTAssertEqual(second.collection.artwork, first.collection.artwork)
        let source = try await service.openPublicQueue(.init(collection: stale, snapshot: second.snapshot))
        let rows = try await source.collectionTracks(intent: .init())
        XCTAssertEqual(rows.count, 65)
        XCTAssertEqual(source.context.title, "Synthetic collection")
        await source.close(); await service.close()
    }
    func testKGFreshReadUpdatesFirstPageMetadataForLaterPagesWithoutChangingSnapshot() async throws {
        let stale = MusicPublicBrowseCollection(platform: .kugou, nativeID: "synthetic-list", title: "Stale catalog title",
            artwork: URL(string: "https://imge.kugou.com/stale.jpg"))
        let http = KGPagedBrowseHTTP()
        await http.configure(omitListInfo: true)
        let service = try await NativeMusicFixture.service(http: http)
        let first = try await service.publicDetail(stale, cursor: nil)
        XCTAssertEqual(first.collection.title, "Synthetic collection")
        XCTAssertEqual(first.collection.artwork, URL(string: "https://imge.kugou.com/new.jpg"))
        let second = try await service.publicDetail(stale, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.collection, first.collection)
        await http.changeMetadata(to: .changed)
        await http.changeHead()
        let renewed = try await service.publicDetail(stale, cursor: nil)
        XCTAssertEqual(renewed.snapshot, first.snapshot)
        XCTAssertEqual(renewed.collection.title, "Changed collection")
        XCTAssertEqual(renewed.collection.artwork, URL(string: "https://imge.kugou.com/changed.jpg"))
        let renewedSecond = try await service.publicDetail(stale, cursor: XCTUnwrap(renewed.cursor))
        XCTAssertEqual(renewedSecond.collection, renewed.collection)
        let source = try await service.openPublicQueue(.init(collection: renewed.collection, snapshot: renewedSecond.snapshot))
        let rows = try await source.collectionTracks(intent: .init())
        XCTAssertEqual(rows.count, 65)
        XCTAssertEqual(source.context.title, "Changed collection")
        await source.close(); await service.close()
    }
    @MainActor func testKGFirstPageActionRenewalUsesSameLiveWitnessAndExplicitRefreshInvalidatesIt() async throws {
        let http = KGPagedBrowseHTTP(), service = try await NativeMusicFixture.service(http: http)
        let page = MusicPublicBrowseViewModel(service: service, request: .detail(collection))
        page.setAccess(available: true, generation: UUID()); page.appear()
        await page.refresh()
        let selected = try XCTUnwrap(page.tracks.first), original = try XCTUnwrap(page.queueRequest)
        let renewed = try await page.refreshedActionTrack(selected)
        XCTAssertEqual(renewed.songId, selected.songId)
        var calls = await http.calls
        XCTAssertEqual(calls.map(\.0), [0, 0]); XCTAssertEqual(calls.map(\.1), [30, 30])
        await page.refresh()
        do { _ = try await service.openPublicQueue(original); XCTFail("Refresh retained old evidence") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        calls = await http.calls
        XCTAssertEqual(calls[2].1, 30)
        page.disappear(); await service.close()
    }
    func testKGDisplayDoesNotRunWholeValidationAndCompleteActionKeepsShortPageMembers() async throws {
        let http = KGPagedBrowseHTTP(), service = try await NativeMusicFixture.service(http: http)
        let first = try await service.publicDetail(collection, cursor: nil)
        XCTAssertEqual(first.tracks.count, 27); XCTAssertEqual(first.consumed, 27)
        let second = try await service.publicDetail(collection, cursor: XCTUnwrap(first.cursor))
        var calls = await http.calls
        XCTAssertEqual(calls.map(\.0), [0, 27]); XCTAssertEqual(calls.map(\.1), [30, 30])
        let source = try await service.openPublicQueue(.init(collection: collection, snapshot: second.snapshot))
        let rows = try await source.collectionTracks(intent: .init())
        XCTAssertEqual(rows.count, 65)
        calls = await http.calls
        XCTAssertEqual(calls.map(\.0), [0, 27, 0, 27, 54, 0, 27, 54])
        XCTAssertEqual(calls.dropFirst(2).map(\.1), Array(repeating: 100, count: 6))
        await source.close(); await service.close()
    }
    func testKGCompleteActionCannotMixChangedObservedMemberWithUnchangedVersion() async throws {
        let http = KGPagedBrowseHTTP(), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, http: http)
        let page = try await service.publicDetail(collection, cursor: nil)
        await http.changeObservedMember()
        do { _ = try await service.openPublicQueue(.init(collection: collection, snapshot: page.snapshot)); XCTFail("Observed member changed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }
    func testKGLaterPageMayOmitListInfoAndCountMustMatchDataCount() async throws {
        let http = KGPagedBrowseHTTP()
        await http.configure(omitListInfo: true)
        let service = try await NativeMusicFixture.service(http: http)
        let first = try await service.publicDetail(collection, cursor: nil)
        let second = try await service.publicDetail(first.collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.total, first.total)
        XCTAssertEqual(second.collection, first.collection)
        let source = try await service.openPublicQueue(.init(collection: collection, snapshot: second.snapshot))
        let rows = try await source.collectionTracks(intent: .init())
        XCTAssertEqual(rows.count, 65)
        await source.close(); await service.close()
    }
    func testKGMismatchedListAndDataCountsAreUpstreamInvalid() async throws {
        let http = KGPagedBrowseHTTP()
        await http.configure(mismatch: true)
        let service = try await NativeMusicFixture.service(http: http)
        let first = try await service.publicDetail(collection, cursor: nil)
        do {
            _ = try await service.publicDetail(first.collection, cursor: XCTUnwrap(first.cursor))
            XCTFail("list_info.count and data.count disagree")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
    }
    func testKugouOutOfRangeErrorIsAnEmptyLastPage() async throws {
        let http = KGPagedBrowseHTTP()
        await http.configure(endCode: true)
        let service = try await NativeMusicFixture.service(http: http)
        let first = try await service.publicDetail(collection, cursor: nil)
        let second = try await service.publicDetail(first.collection, cursor: XCTUnwrap(first.cursor))
        XCTAssertTrue(second.tracks.isEmpty); XCTAssertNil(second.cursor)
        XCTAssertEqual(second.total, first.total)
    }
}

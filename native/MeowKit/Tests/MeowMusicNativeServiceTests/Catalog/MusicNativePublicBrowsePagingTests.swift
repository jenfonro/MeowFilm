import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor KGPagedBrowseHTTP: MusicNativeHTTPTransport {
    var calls: [(Int, Int)] = []
    var changed = false
    func changeObservedMember() { changed = true }
    func send(_ request: MusicNativeHTTPRequest) throws -> MusicNativeHTTPResponse {
        XCTAssertEqual(request.url.path, "/pubsongs/v2/get_other_list_file_nofilt")
        XCTAssertNil(request.headers["Cookie"]); XCTAssertNil(request.headers["Authorization"])
        let fields = Dictionary(uniqueKeysWithValues: try XCTUnwrap(URLComponents(url: request.url,
            resolvingAgainstBaseURL: false)?.queryItems).map { ($0.name, $0.value ?? "") })
        let offset = try XCTUnwrap(Int(fields["begin_idx"] ?? "")), limit = try XCTUnwrap(Int(fields["pagesize"] ?? ""))
        calls.append((offset, limit))
        let end = min(offset + min(limit, 27), 65)
        let songs = (offset..<end).map { index -> MJ in
            let hash = String(format: "%032X", changed && index == 0 ? 1000 : index + 1)
            return .obj([("hash", .string(hash)), ("name", .string("Synthetic Artist - Song\(index)"))])
        }
        let body = MJ.obj([("status", .n(1)), ("error_code", .n(0)), ("data", .obj([
            ("list_info", .obj([("global_collection_id", .string("synthetic-list")),
                ("name", .string("Synthetic collection")), ("count", .n(65)), ("list_ver", .n(1))])),
            ("songs", .array(songs))
        ]))])
        return .init(status: 200, data: body.bytes)
    }
}
final class MusicNativePublicBrowsePagingTests: XCTestCase {
    private let collection = MusicPublicBrowseCollection(platform: .kugou, nativeID: "synthetic-list", title: "Synthetic collection")
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
}

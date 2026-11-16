import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmSearchSelectionTests: XCTestCase {
    private typealias F = FilmSearchWireFixture

    func testFixedSelectionOpensWithoutHandoffAfterSearchRelease() async throws {
        let rows = [F.row(), F.row(.string("0"), key: "site:string-zero")]
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.group(rows)], raw: rows))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let initial = try context.playbackRequest()
        XCTAssertNotNil(initial.searchReference)
        await service.close()
        let restored = try context.playbackRequest()
        XCTAssertEqual(restored.target, initial.target)
        XCTAssertEqual(restored.sources, initial.sources)
        XCTAssertEqual(restored.query, "原查询")
        XCTAssertEqual(restored.target.value["members"]?.filmArray?.map { $0.filmObject?["detailId"] },
                       [.number(0), .string("0")])
        XCTAssertNil(restored.searchReference)
        let calls = await rpc.calls
        XCTAssertFalse(calls.contains { $0.0 == "search.handoff" })
    }

    func testRawSelectionAndOpaqueBusinessIdentityRemainExact() async throws {
        let numeric = F.row(), string = F.row(.string("0"), key: "site:string-zero")
        let rpc = FilmSearchRPCFixture(F.snapshot(raw: [numeric, string]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        let contexts = session.snapshot.rawCards.map { session.context(for: $0) }
        let requests = try contexts.map { try $0.playbackRequest() }
        XCTAssertNotEqual(contexts[0].detail.item.identity, contexts[1].detail.item.identity)
        XCTAssertEqual(requests[0].target.value["source"]?.filmObject?["detailId"], .number(0))
        XCTAssertEqual(requests[1].target.value["source"]?.filmObject?["detailId"], .string("0"))
        XCTAssertTrue(requests.allSatisfy { $0.target.kind == .source && $0.results.isEmpty })
        await service.close()
    }

    func testMissingTypedTargetCannotBeReconstructedFromLabels() throws {
        var card = try F.row().filmRequiredObject()
        card["playbackTarget"] = nil
        XCTAssertThrowsError(try FilmSearchSnapshot(value: F.snapshot(raw: [.object(card)]))) { error in
            guard case FilmSearchError.invalidResponse = error else {
                return XCTFail("Unexpected search response error: \(error)")
            }
        }
    }

    func testRetargetedRequestPreservesRestoreOnlyAndMetadataNamespace() throws {
        let tv = FilmPlaybackTarget(id: "opaque", kind: .tv, title: "剧集", metadataProvider: .tmdb, metadataID: "7")
        let metadata: RuntimeValue = .object(["id": .number(7), "name": .string("剧集资料")])
        let reference: RuntimeValue = .object(["sessionId": .string(F.id.uuidString.lowercased()),
                                             "selectionId": .string("selected")])
        var request = FilmPlaybackRequest(target: tv, sources: [], metadata: metadata, searchReference: reference)
        request.restoreOnly = true
        XCTAssertEqual(request.retargeted(tv).metadata, metadata)
        let movie = FilmPlaybackTarget(id: "opaque", kind: .movie, title: "同号电影", metadataProvider: .tmdb, metadataID: "7")
        let retargeted = request.retargeted(movie)
        XCTAssertNil(retargeted.metadata)
        XCTAssertEqual(retargeted.value.filmObject?["restoreOnly"], .bool(true))
        XCTAssertEqual(retargeted.value.filmObject?["resumeTarget"], .object(movie.value))
    }
}

import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackSearchContextTests: XCTestCase {
    private typealias F = FilmSearchWireFixture

    func testLateSearchUpdatesKeepAuthorizationAndCloseFencesPublication() async throws {
        let card = F.group([F.row()])
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [card]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain(); await rpc.awaitObservation()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let request = try context.playbackRequest()
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(target: .object(request.target.value)))
        let media = FilmPlaybackTestMedia()
        let model = FilmPlaybackModel(transport: server.transport, media: media, searchContext: context) {
            try await context.playbackRequest()
        }
        await model.open()?.value
        let updated = expectation(description: "incremental reference")
        await server.beforeAction { if $0 == "search.update" { updated.fulfill() } }
        try await rpc.emit(F.snapshot(4, cards: [card]))
        await fulfillment(of: [updated], timeout: 3)
        await model.waitForPendingWork()
        let calls = await server.calls()
        let update = try XCTUnwrap(calls.last { $0.1.filmObject?["action"] == .string("search.update") }?.1.filmObject)
        XCTAssertEqual(update["selectionId"], .string(request.target.id))
        XCTAssertEqual(update["sources"], .array(request.sources.map { .object($0.value) }))
        XCTAssertNil(update["results"]); XCTAssertNil(update["cacheEntries"])
        XCTAssertTrue(media.prepared.isEmpty)
        await model.close()
        let closedCount = await server.calls().count
        try await rpc.emit(F.snapshot(5, cards: [card]))
        await model.waitForPendingWork()
        let finalCount = await server.calls().count
        XCTAssertEqual(finalCount, closedCount)
        await service.close()
    }

    func testReleasedSearchDoesNotBlockFirstPlaybackOpen() async throws {
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.group([F.row()])]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        await service.close()
        let request = try context.playbackRequest()
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(target: .object(request.target.value)))
        let model = FilmPlaybackModel(transport: server.transport, media: FilmPlaybackTestMedia(), searchContext: context) {
            try await context.playbackRequest()
        }
        await model.open()?.value
        XCTAssertNotNil(model.snapshot); XCTAssertNil(model.actionError)
        let calls = await server.calls()
        let opened = try XCTUnwrap(calls.first { $0.0 == "session.open" }?.1.filmObject)
        XCTAssertEqual(opened["target"], .object(request.target.value))
        XCTAssertNil(opened["searchReference"])
        XCTAssertNil(opened["restoreOnly"])
        await model.close()
    }
}

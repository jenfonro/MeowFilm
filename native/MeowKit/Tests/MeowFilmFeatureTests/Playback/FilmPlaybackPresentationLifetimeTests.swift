import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackPresentationLifetimeTests: XCTestCase {
    func testClosingExecutionKeepsLoadedPresentationWithoutAnActiveStage() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(
            metadata: .object(["name": .string("已加载资料")])))
        let model = FilmPlaybackModel(transport: server.transport, media: FilmPlaybackTestMedia()) {
            FilmPlaybackRequest(target: try FilmPlaybackTarget(FilmPlaybackFixture.target), sources: [])
        }
        await model.open()?.value
        let loaded = try XCTUnwrap(model.presentation)
        await model.close()
        XCTAssertEqual(model.presentation, loaded)
        XCTAssertNil(model.executionStage)
        XCTAssertTrue(model.isReleased)
    }

    func testReopeningLoadedPageUsesReadOnlyOpenAndDoesNotPrepareMedia() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot())
        let media = FilmPlaybackTestMedia()
        let model = FilmPlaybackModel(transport: server.transport, media: media) { FilmPlaybackFixture.request() }
        await model.open()?.value
        await model.close()
        await model.open()?.value
        let calls = await server.calls()
        let opens = calls.filter { $0.0 == "session.open" }
        XCTAssertEqual(opens.count, 2)
        XCTAssertNil(opens[0].1.filmObject?["restoreOnly"])
        XCTAssertEqual(opens[1].1.filmObject?["restoreOnly"], .bool(true))
        XCTAssertTrue(media.prepared.isEmpty)
        XCTAssertFalse(calls.contains { ["play", "prepare", "select.target"]
            .contains($0.1.filmObject?["action"]?.filmText ?? "") })
        await model.close()
    }

    func testRestoreRequestCarriesOriginalBrowseSelectorsAndAccurateSource() throws {
        let browsing: RuntimeValue = .object(["sourceId": .string("result-a"), "lineId": .number(0),
            "season": .number(2), "loading": .bool(false), "error": .null,
            "lines": .array([]), "episodes": .array([])])
        let snapshot = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(browsing: browsing))
        var request = FilmPlaybackFixture.request()
        request.restoreBrowsing(snapshot)
        let fields = try XCTUnwrap(request.value.filmObject?["browsing"]?.filmObject)
        XCTAssertEqual(fields["sourceId"], .string("result-a"))
        XCTAssertEqual(fields["lineId"], .number(0))
        XCTAssertEqual(fields["season"], .number(2))
        XCTAssertEqual(fields["source"], FilmPlaybackFixture.source)
        XCTAssertEqual(request.value.filmObject?["restoreOnly"], .bool(true))
        XCTAssertEqual(request.target, FilmPlaybackFixture.request().target)
    }

    func testLoadingAndFailedSessionsRetainTheSameLoadedDirectory() throws {
        let loaded = try FilmPlaybackSnapshot(value: FilmPlaybackFixture.snapshot(
            browsing: browsing(loading: false),
            metadata: .object(["name": .string("已加载资料")]),
            candidates: [FilmPlaybackFixture.candidate()],
            episodes: [.object(["season": .number(1), "episode": .number(1), "name": .string("第一集")])]))
        var next = try FilmPlaybackFixture.snapshot(stage: "loading", browsing: browsing(loading: true)).filmRequiredObject()
        next["sessionId"] = .string(UUID().uuidString.lowercased())
        let incoming = try FilmPlaybackSnapshot(value: .object(next))
        let display = incoming.retainingLoadedPresentation(loaded)
        XCTAssertNotEqual(display.sessionID, loaded.sessionID)
        XCTAssertEqual(display.metadata, loaded.metadata)
        XCTAssertEqual(display.browsing, loaded.browsing)
        XCTAssertEqual(display.episodes, loaded.episodes)
        XCTAssertEqual(display.candidates, loaded.candidates)
        XCTAssertTrue(incoming.episodes.isEmpty, "Retained display is not execution authorization")
        next["stage"] = .string("error")
        next["error"] = .string("NETWORK_ERROR")
        next["entryResolution"] = .string("error")
        next["sources"] = .array([])
        let failed = try FilmPlaybackSnapshot(value: .object(next))
        let retained = failed.retainingLoadedPresentation(loaded)
        XCTAssertEqual(retained.sources, loaded.sources)
        XCTAssertEqual(retained.browsing, loaded.browsing)
        XCTAssertEqual(retained.episodes, loaded.episodes)
        XCTAssertEqual(retained.entryResolution, "error")
        XCTAssertEqual(retained.error, "NETWORK_ERROR")
    }

    func testSearchExecutionReplacementKeepsLoadedCardsWhileLoading() throws {
        var loaded = FilmSearchSnapshot(sessionID: UUID(), query: "原查询")
        loaded.cards = [FilmSearchCard(value: .object(["displayKey": .string("opaque-business-card")]))]
        var incoming = FilmSearchSnapshot(sessionID: UUID(), query: loaded.query)
        incoming.siteState = .loading; incoming.metadataState = .loading
        incoming.loading = true
        let display = incoming.retainingLoadingDisplay(loaded)
        XCTAssertEqual(display.cards, loaded.cards)
        XCTAssertEqual(display.sessionID, incoming.sessionID)
        incoming.siteState = .completed; incoming.metadataState = .completed; incoming.loading = false
        XCTAssertTrue(incoming.retainingLoadingDisplay(loaded).cards.isEmpty)
    }

    private func browsing(loading: Bool) -> RuntimeValue {
        .object(["sourceId": .null, "lineId": .null, "season": .number(1),
                 "loading": .bool(loading), "error": .null, "lines": .array([]), "episodes": .array([])])
    }
}

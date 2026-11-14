import XCTest
import Combine
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackSearchContextTests: XCTestCase {
    private typealias F = FilmSearchWireFixture
    func testSingleCardResultsDoNotNarrowAuthorizedScopeForLateMetadataOrReentry() async throws {
        let other = FilmSearchSite(
            cat: .init(installationId: "other-installation", name: "fixture", sourceType: "local", generation: 1),
            source: .init(key: .init(installationId: "other-installation", siteKey: "site"),
                name: "来源", api: "fixture", actions: ["search"], methods: [:]))
        let scope = [F.site.playbackSource, other.playbackSource]
        let selected = F.row(), unrelated = F.row(.string("unrelated"), key: "site:unrelated", title: "另一详情")
        let results: [RuntimeValue] = [.object([
            "sourceKey": .string(F.site.key), "items": .array([selected.catField("raw")!]),
        ])]
        let handoff = F.handoff(sources: scope, results: results)
        let rpc = FilmSearchRPCFixture(
            F.snapshot(loading: true, cards: [F.group([selected])], raw: [selected, unrelated]), handoff: handoff)
        let service = filmSearchService(rpc, sites: [F.site, other])
        let session = try service.session(for: "原查询")
        await rpc.awaitObservation()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let target = try FilmSearchHandoff(handoff).request.target
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(target: .object(target.value)))
        let media = FilmPlaybackTestMedia(), transport = server.transport
        let model = FilmPlaybackModel(transport: transport, media: media, searchContext: context,
            request: { try await context.playbackRequest(sources: scope, owner: transport.producer()) })
        addTeardownBlock { await model.close(); await service.close() }
        await model.open()?.value
        let initialCalls = await server.calls()
        let opened = try XCTUnwrap(initialCalls.first { $0.0 == "session.open" }?.1.filmObject)
        let authorized = RuntimeValue.array(scope.map { .object($0.value) })
        XCTAssertEqual(opened["sources"], authorized)
        XCTAssertEqual(opened["results"], .array(results), "The other search card is not a playback source member")
        XCTAssertNil(opened["resumeTarget"])
        XCTAssertTrue(session.snapshot.loading)

        let carrier = FilmPlaybackTarget(id: context.selectionID, kind: .tv, title: "可靠关联",
            metadataProvider: .tmdb, metadataID: "7",
            members: [.object(["sourceKey": .string(F.site.key), "detailId": .number(0)])])
        let updated = expectation(description: "late metadata reference retains the full admitted scope")
        await server.beforeAction { action in if action == "search.update" { updated.fulfill() } }
        await rpc.setHandoff(F.handoff(target: carrier, revision: 4, sources: scope, results: results,
            metadata: .object(["name": .string("可靠关联")])))
        try await rpc.emit(F.snapshot(4, cards: [F.group([selected])], raw: [selected, unrelated]))
        await session.drain()
        await fulfillment(of: [updated], timeout: 3)
        await model.waitForPendingWork()
        let calls = await server.calls()
        let update = try XCTUnwrap(calls.last { $0.1.filmObject?["action"] == .string("search.update") }?.1.filmObject)
        XCTAssertEqual(update["sources"], authorized)
        XCTAssertEqual(update["searchReference"], opened["searchReference"])
        XCTAssertNil(update["results"], "The reference, not the displayed search page, supplies late members")
        XCTAssertEqual(context.metadataDetail?.catField("name"), .string("可靠关联"))
        XCTAssertTrue(media.prepared.isEmpty)

        await model.close()
        await model.open()?.value
        await model.waitForPendingWork()
        let handoffs = await rpc.calls.filter { $0.0 == "search.handoff" }
        XCTAssertEqual(handoffs.count, 3)
        XCTAssertTrue(handoffs.allSatisfy { $0.1.filmObject?["sources"] == authorized })
        let opens = await server.calls().filter { $0.0 == "session.open" }
        XCTAssertEqual(opens.count, 2)
        XCTAssertEqual(opens.last?.1.filmObject?["sources"], authorized)
        XCTAssertEqual(opens.last?.1.filmObject?["results"], .array(results))
        XCTAssertEqual(opens.last?.1.filmObject?["searchReference"], opened["searchReference"])
        XCTAssertTrue(media.prepared.isEmpty)
    }

    func testOpenDoesNotWaitForSearchCompletionAndLateUpdateUsesReferenceOnSamePlaybackSession() async throws {
        let rpc = FilmSearchRPCFixture(F.snapshot(loading: true, cards: [F.group([F.row()])], raw: [F.row()]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await rpc.awaitObservation()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let target = try FilmSearchHandoff(F.handoff()).request.target
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(target: .object(target.value)))
        let media = FilmPlaybackTestMedia(), model = makeModel(server.transport, media, context)
        await model.open()?.value
        XCTAssertTrue(session.snapshot.loading)
        let opens = await server.calls().filter { $0.0 == "session.open" }
        XCTAssertEqual(opens.count, 1)
        XCTAssertEqual(opens.first?.1.filmObject?["searchReference"], .object(["sessionId": .string(F.id.uuidString.lowercased()), "selectionId": .string(context.selectionID)]))
        XCTAssertNil(opens.first?.1.filmObject?["rules"])
        XCTAssertNil(opens.first?.1.filmObject?["resumeTarget"], "Initial entry follows the current Node handoff")
        let updated = expectation(description: "late handoff reaches the same playback session")
        await server.beforeAction { action in if action == "search.update" { updated.fulfill() } }
        await rpc.setHandoff(F.handoff(revision: 5))
        try await rpc.emit(F.snapshot(5, cards: [F.group([F.row(), F.row(.string("0"), key: "site:string-zero")])], raw: [F.row()]))
        await session.drain()
        await fulfillment(of: [updated], timeout: 3); await model.waitForPendingWork()
        let calls = await server.calls(), updates = calls.filter { $0.1.filmObject?["action"] == .string("search.update") }
        XCTAssertEqual(calls.filter { $0.0 == "session.open" }.count, 1)
        XCTAssertEqual(updates.last?.1.filmObject?["searchReference"], opens.first?.1.filmObject?["searchReference"])
        XCTAssertEqual(updates.last?.1.filmObject?["revision"], .number(5))
        XCTAssertNil(updates.last?.1.filmObject?["metadataTarget"]); XCTAssertNil(updates.last?.1.filmObject?["cacheEntries"])
        XCTAssertTrue(media.prepared.isEmpty)
        await model.close(); await service.close()
    }

    func testLateMetadataCannotReprepareOrCloseAnAlreadyStartedFile() async throws {
        let rpc = FilmSearchRPCFixture(F.snapshot(loading: true, cards: [F.group([F.row()])]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await rpc.awaitObservation()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first)), target = try FilmSearchHandoff(F.handoff()).request.target
        let candidate = FilmPlaybackFixture.candidate()
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending", attempt: FilmPlaybackFixture.attempt("live", candidate: candidate), target: .object(target.value)))
        let media = FilmPlaybackTestMedia(), model = makeModel(server.transport, media, context)
        await model.open()?.value; await model.waitForPendingWork(); await server.awaitObservation()
        XCTAssertEqual(model.snapshot?.current?.attemptID, "live")
        let current: RuntimeValue = .object(["attemptId": .string("live"), "candidate": candidate, "position": .number(0), "duration": .number(600), "paused": .bool(false)])
        let stable = FilmPlaybackFixture.snapshot(20, stage: "playing", current: current, target: .object(target.value))
        await server.reply(to: "search.update", with: stable)
        let updated = expectation(description: "late metadata updates the playing session")
        await server.beforeAction { action in if action == "search.update" { updated.fulfill() } }
        await rpc.setHandoff(F.handoff(revision: 6))
        try await rpc.emit(F.snapshot(6, cards: [F.group([F.row()])]))
        await session.drain(); await fulfillment(of: [updated], timeout: 3); await model.waitForPendingWork()
        XCTAssertEqual(model.snapshot?.current?.attemptID, "live")
        XCTAssertEqual(media.prepared.map(\.attemptID), ["live"]); XCTAssertEqual(media.committed, ["live"])
        XCTAssertTrue(media.stopped.isEmpty); XCTAssertTrue(media.cancelled.isEmpty)
        await model.close(); await service.close()
    }

    func testReopenCarriesTheAcknowledgedEpisodeZeroSourceLineAndCandidate() async throws {
        let raw = F.row(), selectedID = "site:numeric-zero"
        let initial = FilmPlaybackTarget(id: selectedID, kind: .source, title: "原始单源", source: ["key": .string(F.site.key), "detailId": .number(0)])
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [raw], raw: [raw]), handoff: F.handoff(target: initial, selection: selectedID))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        let context = session.context(for: try XCTUnwrap(session.snapshot.rawCards.first))
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(target: .object(initial.value)))
        let model = makeModel(server.transport, FilmPlaybackTestMedia(), context)
        await model.open()?.value
        var fields = initial.value
        fields["season"] = .number(0); fields["episode"] = .number(0)
        fields["source"] = .object(["key": .string(F.site.key), "detailId": .number(0), "lineId": .number(0), "candidateId": .string("opaque-current")])
        let selected = try FilmPlaybackTarget(.object(fields))
        await server.reply(to: "select.candidate", with: FilmPlaybackFixture.snapshot(2, target: .object(selected.value)))
        model.selectCandidate("opaque-current"); await model.waitForPendingWork(); await model.close()
        await model.open()?.value; await model.waitForPendingWork()
        let calls = await server.calls().filter { $0.0 == "session.open" }
        XCTAssertEqual(calls.count, 2); XCTAssertEqual(calls.last?.1.filmObject?["target"], .object(selected.value))
        XCTAssertNil(calls.first?.1.filmObject?["resumeTarget"])
        XCTAssertEqual(calls.last?.1.filmObject?["resumeTarget"], .object(selected.value))
        XCTAssertEqual(calls.last?.1.filmObject?["searchReference"], calls.first?.1.filmObject?["searchReference"])
        await model.close(); await service.close()
    }

    func testCloseCancelsOnlyUnadmittedSearchWaitAndDoesNotCreatePlaybackSession() async throws {
        let gate = FilmRuleGate(), entered = expectation(description: "base prepared")
        let rpc = FilmSearchRPCFixture()
        let service = FilmSearchService(transport: rpc.transport(), sites: { await gate.wait(); return [F.site] })
        let session = try service.session(for: "原查询"), context = session.context(for: FilmSearchCard(value: F.group([F.row()])))
        let server = FilmPlaybackWireServer(), request = FilmPlaybackFixture.request()
        let model = FilmPlaybackModel(transport: server.transport, media: FilmPlaybackTestMedia(), searchContext: context, request: {
            entered.fulfill()
            return try await context.playbackRequest(sources: request.sources, owner: nil)
        })
        let opening = model.open()
        await fulfillment(of: [entered], timeout: 3)
        await model.close()
        XCTAssertTrue(model.isReleased); XCTAssertNil(model.actionError)
        XCTAssertTrue(session.snapshot.loading)
        let calls = await server.calls(); XCTAssertTrue(calls.isEmpty)
        await gate.release(); await opening?.value; await service.close()
    }

    func testCloseFencesLateSearchAndNeverReopensPlayback() async throws {
        let rpc = FilmSearchRPCFixture(F.snapshot(loading: true, cards: [F.group([F.row()])]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await rpc.awaitObservation()
        let context = session.context(for: try XCTUnwrap(session.snapshot.cards.first))
        let server = FilmPlaybackWireServer(), model = makeModel(server.transport, FilmPlaybackTestMedia(), context)
        await model.open()?.value; await model.close()
        let before = await server.calls().count
        await rpc.setHandoff(F.handoff(revision: 7))
        try await rpc.emit(F.snapshot(7, cards: [F.group([F.row()])]))
        await session.drain(); await model.waitForPendingWork()
        let after = await server.calls().count
        XCTAssertEqual(after, before); XCTAssertTrue(model.isReleased)
        await service.close()
    }

    private func makeModel(_ transport: FilmPlaybackTransport, _ media: FilmPlaybackTestMedia, _ context: FilmSearchContext) -> FilmPlaybackModel {
        .init(transport: transport, media: media, searchContext: context,
              request: { try await context.playbackRequest(sources: [F.site.playbackSource], owner: transport.producer()) })
    }
}

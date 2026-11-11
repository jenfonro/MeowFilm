#if os(iOS) || os(macOS)
import XCTest
import Combine
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmSearchSessionTests: XCTestCase {
    private typealias F = FilmSearchWireFixture

    func testCompleteNodePagesAndBothModesKeepExactOrderAndEveryRawField() async throws {
        let first = (0..<126).map { F.row(.number(Double($0)), key: "site:\($0)", title: "标题\($0)") }
        let more = F.row(.string("next"), key: "site:next", title: "下一页")
        let rpc = FilmSearchRPCFixture(F.snapshot(hasMore: true, cards: Array(first.reversed()), raw: first, items: first))
        await rpc.setNext([F.snapshot(2, round: 2, cards: Array(first.reversed()) + [more], raw: first + [more], items: first + [more])])
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        XCTAssertEqual(session.snapshot.siteItems, first)
        XCTAssertEqual(session.snapshot.cards.map(\.value), Array(first.reversed()))
        XCTAssertEqual(session.snapshot.rawCards.map(\.value), first)
        XCTAssertEqual(session.snapshot.rawCards.first?.value.catField("raw")?.catField("unknown"), .object(["complete": .bool(true)]))
        session.loadMore(); await session.drain()
        XCTAssertEqual(session.snapshot.siteItems.count, 127)
        XCTAssertEqual(session.snapshot.cards.map(\.value), Array(first.reversed()) + [more])
        XCTAssertEqual(session.snapshot.round, 2); XCTAssertFalse(session.snapshot.hasMore)
        session.loadMore(); await session.drain()
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.command"])
        XCTAssertEqual(calls[1].1.filmObject?["action"], .string("more"))
        XCTAssertNil(calls[0].1.filmObject?["rules"])
        await service.close()
    }

    func testFailedEmptyUnavailableCancelledAndMalformedAreDistinctWithoutImplicitRetry() async throws {
        for (site, metadata, failed) in [("failed", "unavailable", true), ("completed", "completed", false),
            ("unavailable", "unavailable", true), ("cancelled", "cancelled", false)] {
            let rpc = FilmSearchRPCFixture(F.snapshot(hasMore: site == "failed", site: site, metadata: metadata))
            let service = filmSearchService(rpc), session = try service.session(for: "原查询")
            await session.drain()
            XCTAssertEqual(session.snapshot.failed, failed)
            XCTAssertEqual(session.snapshot.siteState.rawValue, site)
            XCTAssertNil(session.error)
            XCTAssertTrue(try service.session(for: "原查询") === session)
            await session.drain()
            let calls = await rpc.calls
            XCTAssertEqual(calls.map(\.0), ["search.open"])
            if site == "failed" {
                await rpc.setNext([F.snapshot(2)])
                session.loadMore(); await session.drain()
                XCTAssertEqual(session.snapshot.siteState, .completed)
            }
            await service.close()
        }
        let rpc = FilmSearchRPCFixture(.object([:]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        XCTAssertEqual(session.error?.code, "INVALID_RESPONSE")
        let calls = await rpc.calls
        XCTAssertEqual(calls.count, 1)
        await service.close()
    }

    func testStopAndSameWordResumeKeepSessionAndDoNotOpenOrRepeatSuccessfulPages() async throws {
        let row = F.row(), rpc = FilmSearchRPCFixture(F.snapshot(loading: true, cards: [F.group([F.row()])], raw: [F.row()]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await rpc.awaitObservation()
        await rpc.setNext([F.snapshot(2, site: "cancelled", metadata: "cancelled", cards: [F.group([row])], raw: [row]),
                           F.snapshot(3, hasMore: true, cards: [F.group([row])], raw: [row])])
        session.cancel(); await session.drain()
        XCTAssertEqual(session.snapshot.sessionID, F.id)
        XCTAssertTrue(try service.session(for: "原查询") === session)
        await session.drain()
        XCTAssertEqual(session.snapshot.rawCards.map(\.value), [row])
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.command", "search.command"])
        XCTAssertEqual(calls.dropFirst().map { $0.1.filmObject?["action"] }, [.string("stop"), .string("resume")])
        XCTAssertTrue(calls.dropFirst().allSatisfy { $0.1.filmObject?["sessionId"] == .string(F.id.uuidString.lowercased()) })
        await service.close()
    }

    func testCancelImmediatelyFencesSSEUntilStopReceiptThenResumesSameNodeSession() async throws {
        let stopEntered = expectation(description: "stop accepted"), gate = FilmRuleGate()
        let row = F.row(), late = F.row(.string("late"), key: "late")
        let rpc = FilmSearchRPCFixture(handler: { operation, input in
            if operation == "search.release" { return .object(["released": .bool(true)]) }
            if input.filmObject?["action"] == .string("stop") {
                stopEntered.fulfill(); await gate.wait()
                return F.snapshot(2, site: "cancelled", metadata: "cancelled", cards: [row], raw: [row])
            }
            if input.filmObject?["action"] == .string("resume") { return F.snapshot(4, cards: [row, late], raw: [row, late]) }
            return F.snapshot(loading: true, site: "loading", cards: [row], raw: [row])
        })
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await rpc.awaitObservation()
        let receiver = await rpc.capturedReceiver()
        let oldReceiver = try XCTUnwrap(receiver)
        session.cancel()
        XCTAssertFalse(session.snapshot.loading)
        XCTAssertEqual(session.snapshot.siteState, .cancelled)
        await fulfillment(of: [stopEntered], timeout: 3)
        try await oldReceiver(FilmSearchSnapshot(value: F.snapshot(3, cards: [late], raw: [late])))
        XCTAssertEqual(session.snapshot.cards.map(\.value), [row], "Late SSE cannot publish while stop is in transit")
        XCTAssertEqual(session.snapshot.revision, 1)
        XCTAssertTrue(try service.session(for: "原查询") === session)
        try await oldReceiver(FilmSearchSnapshot(value: F.snapshot(3, cards: [late], raw: [late])))
        XCTAssertEqual(session.snapshot.cards.map(\.value), [row], "Queued resume cannot reauthorize the previous observer")
        await gate.release(); await session.drain()
        XCTAssertEqual(session.snapshot.sessionID, F.id)
        XCTAssertEqual(session.snapshot.cards.map(\.value), [row, late])
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.command", "search.command"])
        XCTAssertEqual(calls.dropFirst().map { $0.1.filmObject?["action"] }, [.string("stop"), .string("resume")])
        await service.close()
    }

    func testCancelFencesAnAcceptedMoreReceiptButStillDrainsItBeforeStopAndRelease() async throws {
        let entered = expectation(description: "more accepted"), gate = FilmRuleGate()
        let row = F.row(), late = F.row(.string("late"), key: "late")
        let rpc = FilmSearchRPCFixture(handler: { operation, input in
            if operation == "search.release" { return .object(["released": .bool(true)]) }
            if input.filmObject?["action"] == .string("more") {
                entered.fulfill(); await gate.wait()
                XCTAssertFalse(Task.isCancelled, "Client stop must not cancel an accepted Node producer")
                return F.snapshot(2, cards: [row, late], raw: [row, late])
            }
            if input.filmObject?["action"] == .string("stop") {
                return F.snapshot(3, site: "cancelled", metadata: "cancelled", cards: [row], raw: [row])
            }
            return F.snapshot(hasMore: true, cards: [row], raw: [row])
        })
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain()
        session.loadMore()
        await fulfillment(of: [entered], timeout: 3)
        session.cancel()
        XCTAssertFalse(session.snapshot.loading)
        let before = await rpc.calls
        XCTAssertEqual(before.count, 2, "Stop waits for the accepted RPC's actual receipt")
        var publications: [[RuntimeValue]] = []
        let subscription = session.$snapshot.sink { publications.append($0.cards.map(\.value)) }
        await gate.release(); await session.drain()
        subscription.cancel()
        XCTAssertTrue(publications.allSatisfy { $0 == [row] }, "A drained old more receipt has no publication authority")
        XCTAssertEqual(session.snapshot.revision, 3)
        XCTAssertEqual(session.snapshot.cards.map(\.value), [row])
        await service.close()
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.command", "search.command", "search.release"])
        XCTAssertEqual(calls[1].1.filmObject?["action"], .string("more"))
        XCTAssertEqual(calls[2].1.filmObject?["action"], .string("stop"))
        XCTAssertTrue(calls.dropFirst().allSatisfy { $0.1.filmObject?["sessionId"] == .string(F.id.uuidString.lowercased()) })
    }

    func testStopReceiptKeepsEveryDisplayedFieldAfterRejectedSSEUntilSameSessionResume() async throws {
        let row = F.row(), late = F.row(.string("late"), key: "late")
        var initial = try F.snapshot(cards: [row], raw: [row]).filmRequiredObject()
        initial["tmdbItems"] = .array([.object(["id": .number(7)])])
        initial["metadataSearch"] = .object(["results": .array([.object(["id": .number(7)])])])
        initial["metadataQuery"] = .string("原元数据查询")
        initial["metadataDetails"] = .object(["movie:7": .object(["overview": .string("旧资料")])])
        var settled = try F.snapshot(3, round: 2, hasMore: true, site: "cancelled", metadata: "cancelled",
            cards: [row, late], raw: [row, late]).filmRequiredObject()
        settled["tmdbItems"] = .array([.object(["id": .number(8)])])
        settled["metadataSearch"] = .object(["results": .array([.object(["id": .number(8)])])])
        settled["metadataQuery"] = .string("晚到元数据查询")
        settled["metadataDetails"] = .object(["movie:8": .object(["overview": .string("晚到资料")])])
        var resumed = settled
        resumed["revision"] = .number(4); resumed["sequence"] = .number(4)
        resumed["siteState"] = .string("completed"); resumed["metadataState"] = .string("completed")
        let initialValue = RuntimeValue.object(initial), stoppedValue = RuntimeValue.object(settled)
        let resumedValue = RuntimeValue.object(resumed), gate = FilmRuleGate()
        let entered = expectation(description: "stop accepted")
        let rpc = FilmSearchRPCFixture(handler: { operation, input in
            if operation == "search.release" { return .object(["released": .bool(true)]) }
            if input.filmObject?["action"] == .string("stop") {
                entered.fulfill(); await gate.wait()
                return stoppedValue
            }
            if input.filmObject?["action"] == .string("resume") { return resumedValue }
            return initialValue
        })
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain(); await rpc.awaitObservation()
        let displayed = session.snapshot, receiver = await rpc.capturedReceiver()
        let oldReceiver = try XCTUnwrap(receiver)
        session.cancel()
        await fulfillment(of: [entered], timeout: 3)
        try await oldReceiver(FilmSearchSnapshot(value: stoppedValue))
        XCTAssertEqual(session.snapshot.cards, displayed.cards)
        XCTAssertEqual(session.snapshot.revision, displayed.revision)
        await gate.release(); await session.drain()
        XCTAssertEqual(session.snapshot.sessionID, F.id)
        XCTAssertEqual(session.snapshot.sequence, 3); XCTAssertEqual(session.snapshot.revision, 3)
        XCTAssertEqual(session.snapshot.round, 2); XCTAssertTrue(session.snapshot.hasMore)
        XCTAssertFalse(session.snapshot.loading); XCTAssertEqual(session.snapshot.siteState, .cancelled)
        XCTAssertEqual(session.snapshot.metadataState, .cancelled)
        XCTAssertEqual(session.snapshot.siteItems, displayed.siteItems)
        XCTAssertEqual(session.snapshot.tmdbItems, displayed.tmdbItems)
        XCTAssertEqual(session.snapshot.cards, displayed.cards); XCTAssertEqual(session.snapshot.rawCards, displayed.rawCards)
        XCTAssertEqual(session.snapshot.metadataSearch, displayed.metadataSearch)
        XCTAssertEqual(session.snapshot.metadataQuery, displayed.metadataQuery)
        XCTAssertEqual(session.snapshot.metadataDetails, displayed.metadataDetails)
        XCTAssertTrue(try service.session(for: "原查询") === session)
        await session.drain()
        XCTAssertEqual(session.snapshot, try FilmSearchSnapshot(value: resumedValue))
        await service.close()
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.command", "search.command", "search.release"])
        XCTAssertEqual(calls[1...2].map { $0.1.filmObject?["action"] }, [.string("stop"), .string("resume")])
        XCTAssertTrue(calls.dropFirst().allSatisfy { $0.1.filmObject?["sessionId"] == .string(F.id.uuidString.lowercased()) })
    }

    func testCancelledAcceptedOpenBindsIdentityWithoutPublishingStopCardsAndResumesThenReleases() async throws {
        let entered = expectation(description: "open accepted"), gate = FilmRuleGate(), late = F.row()
        let rpc = FilmSearchRPCFixture(handler: { operation, input in
            if operation == "search.open" {
                entered.fulfill(); await gate.wait()
                XCTAssertFalse(Task.isCancelled)
                return F.snapshot(2, cards: [late], raw: [late])
            }
            if operation == "search.release" { return .object(["released": .bool(true)]) }
            if input.filmObject?["action"] == .string("stop") {
                return F.snapshot(3, hasMore: true, site: "cancelled", metadata: "cancelled", cards: [late], raw: [late])
            }
            XCTAssertEqual(input.filmObject?["action"], .string("resume"))
            return F.snapshot(4, cards: [late], raw: [late])
        })
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await fulfillment(of: [entered], timeout: 3)
        session.cancel(); await gate.release(); await session.drain()
        XCTAssertEqual(session.snapshot.sessionID, F.id)
        XCTAssertEqual(session.snapshot.sequence, 3); XCTAssertEqual(session.snapshot.siteState, .cancelled)
        XCTAssertFalse(session.snapshot.loading); XCTAssertTrue(session.snapshot.hasMore)
        XCTAssertTrue(session.snapshot.siteItems.isEmpty); XCTAssertTrue(session.snapshot.tmdbItems.isEmpty)
        XCTAssertTrue(session.snapshot.cards.isEmpty); XCTAssertTrue(session.snapshot.rawCards.isEmpty)
        XCTAssertNil(session.snapshot.metadataSearch); XCTAssertTrue(session.snapshot.metadataDetails.isEmpty)
        XCTAssertTrue(try service.session(for: "原查询") === session)
        await session.drain()
        XCTAssertEqual(session.snapshot.sessionID, F.id)
        XCTAssertEqual(session.snapshot.cards.map(\.value), [late])
        XCTAssertEqual(session.snapshot.rawCards.map(\.value), [late])
        await service.close()
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.command", "search.command", "search.release"])
        XCTAssertEqual(calls[1...2].map { $0.1.filmObject?["action"] }, [.string("stop"), .string("resume")])
        XCTAssertTrue(calls.dropFirst().allSatisfy { $0.1.filmObject?["sessionId"] == .string(F.id.uuidString.lowercased()) })
    }

    func testSSEPublishesCompleteNodeCardsWithoutLocalAggregationAndRejectsSequenceRegression() async throws {
        let row = F.row(), second = F.row(.string("0"), key: "site:string-zero")
        let rpc = FilmSearchRPCFixture(F.snapshot(loading: true, site: "loading"))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await rpc.awaitObservation()
        // A deliberately opaque Node decision must not be re-scored/regrouped.
        let new = F.snapshot(9, cards: [F.group([second, row])], raw: [second, row])
        try await rpc.emit(new); await session.drain()
        XCTAssertEqual(session.snapshot.cards.first?.members, [second, row])
        XCTAssertEqual(session.snapshot.rawCards.map(\.value), [second, row])
        try await rpc.emit(F.snapshot(2, cards: []))
        XCTAssertEqual(session.snapshot.revision, 9)
        XCTAssertEqual(session.snapshot.cards.count, 1)
        await service.close()
    }

    func testDisconnectShowsFailureAndExplicitReconnectUsesSnapshotOfSameSession() async throws {
        let row = F.row(), rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.row()]))
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain(); await rpc.awaitObservation()
        await rpc.disconnect()
        let failed = expectation(description: "Observation disconnection is visible")
        let subscription = session.$error.compactMap { $0 }.first().sink { _ in failed.fulfill() }
        await fulfillment(of: [failed], timeout: 3); subscription.cancel()
        XCTAssertEqual(session.error?.code, "OBSERVATION_CLOSED")
        try await session.refresh(); await rpc.awaitObservation()
        XCTAssertNil(session.error); XCTAssertEqual(session.snapshot.cards.map(\.value), [row])
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.snapshot"])
        let observations = await rpc.observationRequests
        XCTAssertEqual(observations.map(\.0), [F.id, F.id])
        XCTAssertEqual(observations.map(\.1), [1, 1])
        await service.close()
    }

    func testCancelFencesALateReconnectFailureWithoutCancellingTheAcceptedRPC() async throws {
        let entered = expectation(description: "snapshot accepted"), gate = FilmRuleGate(), row = F.row()
        let rpc = FilmSearchRPCFixture(handler: { operation, _ in
            if operation == "search.release" { return .object(["released": .bool(true)]) }
            if operation == "search.snapshot" {
                entered.fulfill(); await gate.wait()
                XCTAssertFalse(Task.isCancelled)
                throw FilmPlaybackError(code: "HOST_UNAVAILABLE")
            }
            return F.snapshot(cards: [row], raw: [row])
        })
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain(); await rpc.awaitObservation()
        let disconnected = expectation(description: "disconnection shown")
        let failure = session.$error.compactMap { $0 }.first().sink { _ in disconnected.fulfill() }
        await rpc.disconnect(); await fulfillment(of: [disconnected], timeout: 3); failure.cancel()
        XCTAssertTrue(try service.session(for: "原查询") === session)
        await fulfillment(of: [entered], timeout: 3)
        XCTAssertNil(session.error)
        session.cancel()
        var failures: [String] = []
        let subscription = session.$error.compactMap { $0?.code }.sink { failures.append($0) }
        await gate.release(); await session.drain(); subscription.cancel()
        XCTAssertTrue(failures.isEmpty, "A cancelled reconnect cannot publish its late error")
        XCTAssertEqual(session.snapshot.cards.map(\.value), [row])
        await service.close()
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.snapshot", "search.command", "search.release"])
        XCTAssertEqual(calls[2].1.filmObject?["action"], .string("stop"))
    }

    func testNewWordFencesOldPublishingButKeepsRetainedNavigationIdentity() async throws {
        let nextID = UUID(), row = F.row(), late = F.row(.string("late"), key: "late")
        let rpc = FilmSearchRPCFixture(handler: { operation, input in
            if operation == "search.release" { return .object(["released": .bool(true)]) }
            if operation == "search.open", input.filmObject?["query"] == .string("新词") {
                return F.snapshot(query: "新词", cards: [late], raw: [late], id: nextID)
            }
            return F.snapshot(site: "cancelled", metadata: "cancelled", cards: [row], raw: [row])
        })
        let service = filmSearchService(rpc), first = try service.session(for: "原查询")
        await first.drain(); await rpc.awaitObservation()
        let context = first.context(for: try XCTUnwrap(first.snapshot.cards.first))
        let captured = await rpc.capturedReceiver(), oldReceive = try XCTUnwrap(captured)
        let next = try service.session(for: "新词")
        await next.drain(); await first.drain()
        try await oldReceive(FilmSearchSnapshot(value: F.snapshot(9, cards: [late], raw: [late])))
        XCTAssertEqual(context.snapshot.sessionID, F.id)
        XCTAssertEqual(context.snapshot.cards.map(\.value), [row])
        XCTAssertFalse(first.invalidated, "A different query stops UI work without destroying live navigation")
        XCTAssertEqual(next.snapshot.sessionID, nextID)
        XCTAssertEqual(next.snapshot.query, "新词")
        XCTAssertEqual(next.snapshot.cards.map(\.value), [late])
        let calls = await rpc.calls
        XCTAssertEqual(calls.filter { $0.0 == "search.open" }.count, 2)
        await service.close()
    }

    func testMetadataChangeNeedsExplicitNewSessionWithoutDiscardingRetainedDisplay() async throws {
        let rpc = FilmSearchRPCFixture(F.snapshot(cards: [F.row()]))
        let service = filmSearchService(rpc), first = try service.session(for: "原查询")
        await first.drain()
        service.metadataSettingsChanged()
        XCTAssertTrue(first.invalidated)
        await first.drain()
        let before = await rpc.calls
        XCTAssertFalse(before.contains { $0.0 == "search.open" && $0.1.filmObject?["query"] != .string("原查询") })
        let next = try service.session(for: "原查询", reusing: first)
        XCTAssertFalse(next === first)
        await next.drain()
        XCTAssertEqual(first.snapshot.cards.first?.title, "真实标题")
        await service.close()
    }

    func testClosingDrainsAnAcceptedOpenAndReleasesItsActualIDExactlyOnce() async throws {
        let entered = expectation(description: "open accepted"), gate = FilmRuleGate()
        let rpc = FilmSearchRPCFixture(handler: { operation, _ in
            if operation == "search.open" { entered.fulfill(); await gate.wait(); return F.snapshot() }
            XCTAssertEqual(operation, "search.release")
            return .object(["released": .bool(true)])
        })
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await fulfillment(of: [entered], timeout: 3)
        let closing = Task { await service.close() }
        await gate.release(); await closing.value
        XCTAssertFalse(session.snapshot.loading)
        await service.close()
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.release"])
        XCTAssertEqual(calls.last?.1.filmObject?["sessionId"], .string(F.id.uuidString.lowercased()))
        XCTAssertThrowsError(try service.session(for: "原查询"))
    }

    func testExplicitReleaseSynchronouslyFencesAcceptedOpenAndCloseWaitsForItsOnlyRelease() async throws {
        let entered = expectation(description: "open accepted"), openGate = FilmRuleGate()
        let releaseEntered = expectation(description: "release accepted"), releaseGate = FilmRuleGate()
        let late = F.row(.string("late"), key: "late")
        let rpc = FilmSearchRPCFixture(handler: { operation, _ in
            if operation == "search.open" {
                entered.fulfill(); await openGate.wait()
                XCTAssertFalse(Task.isCancelled, "Final release must drain an accepted producer")
                return F.snapshot(cards: [late], raw: [late])
            }
            XCTAssertEqual(operation, "search.release")
            releaseEntered.fulfill(); await releaseGate.wait()
            return .object(["released": .bool(true)])
        })
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await fulfillment(of: [entered], timeout: 3)
        service.release(session)
        XCTAssertTrue(session.invalidated)
        XCTAssertFalse(session.snapshot.loading)
        session.cancel(); session.loadMore(); service.release(session)
        await openGate.release()
        await fulfillment(of: [releaseEntered], timeout: 3)
        let closeEntered = expectation(description: "close started")
        var closed = false
        let closing = Task {
            closeEntered.fulfill()
            await service.close()
            closed = true
        }
        await fulfillment(of: [closeEntered], timeout: 3)
        XCTAssertFalse(closed, "Close must still await the accepted release receipt")
        XCTAssertTrue(session.snapshot.cards.isEmpty)
        XCTAssertTrue(session.snapshot.rawCards.isEmpty)
        await releaseGate.release(); await closing.value
        await service.close()
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.release"])
        XCTAssertEqual(calls.last?.1.filmObject?["sessionId"], .string(F.id.uuidString.lowercased()))
    }

    func testFinalReleaseSkipsQueuedStopAndLateSSEButDrainsAcceptedMore() async throws {
        let entered = expectation(description: "more accepted"), gate = FilmRuleGate()
        let row = F.row(), late = F.row(.string("late"), key: "late")
        let rpc = FilmSearchRPCFixture(handler: { operation, input in
            if operation == "search.open" { return F.snapshot(hasMore: true, cards: [row], raw: [row]) }
            if operation == "search.command" {
                XCTAssertEqual(input.filmObject?["action"], .string("more"))
                entered.fulfill(); await gate.wait()
                XCTAssertFalse(Task.isCancelled, "Final release must drain an accepted producer")
                return F.snapshot(2, cards: [row, late], raw: [row, late])
            }
            XCTAssertEqual(operation, "search.release")
            return .object(["released": .bool(true)])
        })
        let service = filmSearchService(rpc), session = try service.session(for: "原查询")
        await session.drain(); await rpc.awaitObservation()
        let captured = await rpc.capturedReceiver(), receive = try XCTUnwrap(captured)
        session.loadMore()
        await fulfillment(of: [entered], timeout: 3)
        session.cancel()
        service.release(session)
        XCTAssertTrue(session.invalidated)
        XCTAssertFalse(session.snapshot.loading)
        try await receive(FilmSearchSnapshot(value: F.snapshot(3, cards: [late], raw: [late])))
        XCTAssertEqual(session.snapshot.cards.map(\.value), [row])
        XCTAssertEqual(session.snapshot.rawCards.map(\.value), [row])
        service.release(session)
        await gate.release(); await service.close()
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["search.open", "search.command", "search.release"])
        XCTAssertEqual(calls[1].1.filmObject?["action"], .string("more"))
        XCTAssertEqual(calls.last?.1.filmObject?["sessionId"], .string(F.id.uuidString.lowercased()))
        XCTAssertEqual(session.snapshot.cards.map(\.value), [row])
        XCTAssertEqual(session.snapshot.rawCards.map(\.value), [row])
    }
}
#endif

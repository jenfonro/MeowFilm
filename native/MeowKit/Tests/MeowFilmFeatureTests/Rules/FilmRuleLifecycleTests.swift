import Foundation
import XCTest
@testable import MeowFilmFeature

final class FilmRuleLifecycleTests: XCTestCase {
    func testCallerCancellationSkipsQueuedEngineWorkAndServiceRecovers() async throws {
        let storage = try FilmRuleTestStore()
        let entered = expectation(description: "engine entered")
        let cancelled = expectation(description: "engine cancelled")
        let engine = FilmRuleControlledEngine(entered: entered, cancelled: cancelled)
        let service = FilmRuleService(engine: engine, store: storage.store)
        let running = Task { try await service.normalizeNewMatchRule("first") }
        await fulfillment(of: [entered], timeout: 5)
        let queued = Task { try await service.normalizeNewMatchRule("queued") }
        queued.cancel()
        running.cancel()
        await fulfillment(of: [cancelled], timeout: 5)
        await engine.release.release()
        await filmRuleCancelled { try await running.value }
        await filmRuleCancelled { try await queued.value }
        let next = try await service.normalizeNewMatchRule("next")
        XCTAssertNil(next)
        let calls = await engine.invocations
        XCTAssertEqual(calls, 2, "Cancelled queue work never invokes JS; a later call still works")
        await service.close()
        let closes = await engine.closes
        XCTAssertEqual(closes, 1)
    }

    func testPrepareCancelsAndPhysicallyDrainsBeforeResume() async throws {
        let storage = try FilmRuleTestStore()
        let entered = expectation(description: "engine entered")
        let cancelled = expectation(description: "engine cancelled")
        let engine = FilmRuleControlledEngine(entered: entered, cancelled: cancelled)
        let service = FilmRuleService(engine: engine, store: storage.store)
        let running = Task { try await service.normalizeNewMatchRule("first") }
        await fulfillment(of: [entered], timeout: 5)
        let preparation = Task { await service.prepareShutdown() }
        await fulfillment(of: [cancelled], timeout: 5)
        preparation.cancel()
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.resume() }
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.normalizeNewMatchRule("late") }
        let closesBefore = await engine.closes
        XCTAssertEqual(closesBefore, 0)
        await engine.release.release()
        await preparation.value
        await filmRuleCancelled { try await running.value }
        try await service.resume()
        let result = try await service.normalizeNewMatchRule("resumed")
        XCTAssertNil(result)
        let calls = await engine.invocations
        XCTAssertEqual(calls, 2)
        let closesAfterResume = await engine.closes
        XCTAssertEqual(closesAfterResume, 0, "Resume reuses the same live engine")
        await service.close()
    }

    func testConcurrentPrepareAndCloseShareDrainAndCloseExactlyOnce() async throws {
        let storage = try FilmRuleTestStore()
        let entered = expectation(description: "engine entered")
        let cancelled = expectation(description: "engine cancelled")
        let engine = FilmRuleControlledEngine(entered: entered, cancelled: cancelled)
        let service = FilmRuleService(engine: engine, store: storage.store)
        let running = Task { try await service.normalizeNewMatchRule("first") }
        await fulfillment(of: [entered], timeout: 5)
        let preparation = Task { await service.prepareShutdown() }
        let closing = Task { await service.close() }
        await fulfillment(of: [cancelled], timeout: 5)
        // Await an additional close through the same path without waiting for release.
        let repeated = Task { await service.close() }
        let before = await engine.closes
        XCTAssertEqual(before, 0, "Active invoke must finish before engine release")
        await engine.release.release()
        await preparation.value
        await closing.value
        await repeated.value
        await filmRuleCancelled { try await running.value }
        await service.close()
        await service.prepareShutdown()
        let after = await engine.closes
        XCTAssertEqual(after, 1)
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.resume() }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.normalizeNewMatchRule("late") }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.savedSnapshot() }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.clearData() }
        // A borrowed store remains usable after engine ownership ends.
        try storage.store.write(Data("{}".utf8), key: "unrelated")
        XCTAssertEqual(try storage.store.read(key: "unrelated"), Data("{}".utf8))
    }

    func testMalformedEngineReplyDoesNotPublishOrPersistAndCanRecover() async throws {
        let storage = try FilmRuleTestStore()
        let engine = FilmRuleControlledEngine(entered: XCTestExpectation(description: "unused"),
                                              cancelled: XCTestExpectation(description: "unused"))
        await engine.disableBlocking()
        await engine.setResponse(Data("{}".utf8))
        let service = FilmRuleService(engine: engine, store: storage.store)
        await filmRuleFailure(FilmRuleServiceError.invalidEngineResponse) { try await service.initialize() }
        XCTAssertNil(try storage.store.read(key: FilmRuleService.documentKey))
        await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
        await engine.setResponse(Data("null".utf8))
        let result = try await service.normalizeNewMatchRule("  ")
        XCTAssertNil(result)
        await service.close()
    }

    func testClearCancelsActiveWorkAndPreventsLateDocumentPublication() async throws {
        let storage = try FilmRuleTestStore()
        let entered = expectation(description: "initialization entered")
        let cancelled = expectation(description: "initialization cancelled")
        let engine = FilmRuleControlledEngine(entered: entered, cancelled: cancelled)
        await engine.ignoreCancellation()
        await engine.setResponse(Data(#"{"changed":true,"rules":{"episodeCleanRegexRules":[],"episodeRules":[],"movieRules":[],"aggregateRegexRules":[]},"document":{"episodeCleanRegexRules":[],"episodeRules":[],"movieRules":[],"aggregateRegexRules":[]}}"#.utf8))
        let service = FilmRuleService(engine: engine, store: storage.store)
        let initializing = Task { try await service.initialize() }
        await fulfillment(of: [entered], timeout: 5)
        let clearing = Task { try await service.clearData() }
        await fulfillment(of: [cancelled], timeout: 5)
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.resume() }
        await engine.release.release()
        try await clearing.value
        await filmRuleCancelled { try await initializing.value }
        XCTAssertNil(try storage.store.read(key: FilmRuleService.documentKey))
        await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
        await engine.setResponse(Data("null".utf8))
        let next = try await service.normalizeNewMatchRule("next")
        XCTAssertNil(next, "Clearing reopens admission after old work is drained")
        await service.close()
    }
}

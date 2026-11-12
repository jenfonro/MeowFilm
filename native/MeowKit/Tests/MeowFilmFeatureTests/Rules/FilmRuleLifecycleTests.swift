import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmRuleLifecycleTests: XCTestCase {
    func testCancelledGroupPreviewPhysicallyDrainsAndCannotPublishItsLateReply() async throws {
        let entered = expectation(description: "group preview entered"), cancelled = expectation(description: "group preview cancelled")
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service = rpc.service
        await rpc.ignoreCancellation()
        let preview = try RuntimeJSON.decode(Data(#"{"section":"movie","type":"success","text":"late","inputText":"pending","processedText":"pending","matched":true,"matchedRuleIndex":1,"rewriteApplied":true,"rewrittenText":"late","groupKey":"","trailingDigits":"","errors":[],"previews":["pending"]}"#.utf8))
        await rpc.setResponse(preview)
        let running = Task {
            try await service.process(.init(section: .movie, text: "pending"), group: .movie,
                                      rules: [.init(pattern: "pending", replace: "late")])
        }
        await fulfillment(of: [entered], timeout: 5)
        let preparation = Task { await service.prepareShutdown() }
        await fulfillment(of: [cancelled], timeout: 5)
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.resume() }
        await rpc.release.release(); await preparation.value
        await filmRuleCancelled { try await running.value }
        await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
        try await service.resume()
        let receipt = FilmRuleSet(movieRules: [.init(pattern: "acknowledged")])
        await rpc.setResponse(.object(["rules": try filmRuleValue(receipt), "revision": .string("next")]))
        let saved = try await service.save(.movie, rules: receipt.movieRules)
        XCTAssertEqual(saved, receipt)
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 2)
        await service.close()
    }

    func testCancelledGroupSaveDrainsAndDoesNotPublishLateReceipt() async throws {
        let entered = expectation(description: "group write entered"), cancelled = expectation(description: "group write cancelled")
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service = rpc.service
        await rpc.ignoreCancellation()
        await rpc.setResponse(.object(["rules": try filmRuleValue(FilmRuleSet(movieRules: [.init(pattern: "late")])),
                                      "revision": .string("late")]))
        let running = Task { try await service.save(.movie, rules: [.init(pattern: "pending")]) }
        await fulfillment(of: [entered], timeout: 5)
        let preparation = Task { await service.prepareShutdown() }
        await fulfillment(of: [cancelled], timeout: 5)
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.resume() }
        await rpc.release.release(); await preparation.value
        await filmRuleCancelled { try await running.value }
        await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
        let revision = await service.revision
        XCTAssertNil(revision)
        try await service.resume()
        try await service.initialize()
        await service.close()
    }

    func testCallerCancellationSkipsQueuedRPCAndServiceRecovers() async throws {
        let entered = expectation(description: "RPC entered"), cancelled = expectation(description: "RPC cancelled")
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service: FilmRuleService
        service = rpc.service
        let running = Task { try await service.normalizeNewMatchRule("first") }
        await fulfillment(of: [entered], timeout: 5)
        let queued = Task { try await service.normalizeNewMatchRule("queued") }
        queued.cancel(); running.cancel()
        await fulfillment(of: [cancelled], timeout: 5)
        await rpc.release.release()
        await filmRuleCancelled { try await running.value }
        await filmRuleCancelled { try await queued.value }
        let next = try await service.normalizeNewMatchRule("next")
        XCTAssertNil(next)
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 2)
        await service.close()
    }

    func testPrepareCancelsAndPhysicallyDrainsBeforeResume() async throws {
        let entered = expectation(description: "entered"), cancelled = expectation(description: "cancelled")
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service: FilmRuleService
        service = rpc.service
        let running = Task { try await service.normalizeNewMatchRule("first") }
        await fulfillment(of: [entered], timeout: 5)
        let preparation = Task { await service.prepareShutdown() }
        await fulfillment(of: [cancelled], timeout: 5)
        preparation.cancel()
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.resume() }
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.normalizeNewMatchRule("late") }
        await rpc.release.release(); await preparation.value
        await filmRuleCancelled { try await running.value }
        try await service.resume()
        let next = try await service.normalizeNewMatchRule("resumed")
        XCTAssertNil(next)
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 2)
        await service.close()
    }

    func testConcurrentPrepareAndCloseShareDrainAndLeaveBorrowedRuntimeUsable() async throws {
        let entered = expectation(description: "entered"), cancelled = expectation(description: "cancelled")
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service: FilmRuleService
        service = rpc.service
        let running = Task { try await service.normalizeNewMatchRule("first") }
        await fulfillment(of: [entered], timeout: 5)
        let preparation = Task { await service.prepareShutdown() }, closing = Task { await service.close() }
        await fulfillment(of: [cancelled], timeout: 5)
        let repeated = Task { await service.close() }
        await rpc.release.release(); await preparation.value; await closing.value; await repeated.value
        await filmRuleCancelled { try await running.value }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.resume() }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.normalizeNewMatchRule("late") }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.savedSnapshot() }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.clearData() }
        let borrowed = try await rpc.call("rules.normalize", .object([:]))
        XCTAssertEqual(borrowed, .null)
    }

    func testMalformedReplyDoesNotPublishAndCanRecoverWithoutFallback() async throws {
        let rpc = FilmRuleRPC(), service: FilmRuleService
        service = rpc.service
        await rpc.setResponse(.object([:]))
        await filmRuleFailure(FilmRuleServiceError.invalidResponse) { try await service.initialize() }
        await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
        await rpc.setResponse(nil)
        try await service.initialize()
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["rules.get", "rules.get"], "Only an explicit second call retries")
        await service.close()
    }

    func testClearDrainsOldRPCAndPublishesOnlyAcknowledgedDefaults() async throws {
        let entered = expectation(description: "entered"), cancelled = expectation(description: "cancelled")
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service: FilmRuleService
        service = rpc.service
        await rpc.ignoreCancellation()
        let defaults: RuntimeValue = .object(["rules": try filmRuleValue(FilmRuleSet()), "revision": .string("new")])
        await rpc.setResponse(defaults)
        let initializing = Task { try await service.initialize() }
        await fulfillment(of: [entered], timeout: 5)
        let clearing = Task { try await service.clearData() }
        await fulfillment(of: [cancelled], timeout: 5)
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.resume() }
        await rpc.release.release(); try await clearing.value
        await filmRuleCancelled { try await initializing.value }
        let saved = try await service.savedSnapshot(), revision = await service.revision
        XCTAssertEqual(saved, FilmRuleSet()); XCTAssertEqual(revision, .string("new"))
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 2)
        await service.close()
    }

    func testCloseCancelsAndPhysicallyDrainsAnAcceptedClear() async throws {
        let entered = expectation(description: "clear accepted"), cancelled = expectation(description: "clear cancelled")
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled)
        await rpc.ignoreCancellation()
        await rpc.setResponse(.object(["rules": try filmRuleValue(FilmRuleSet()), "revision": .string("late")]))
        let service = rpc.service
        let clearing = Task { try await service.clearData() }
        await fulfillment(of: [entered], timeout: 3)
        let closing = Task { await service.close() }
        await fulfillment(of: [cancelled], timeout: 3)
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.savedSnapshot() }
        await rpc.release.release()
        await closing.value
        await filmRuleCancelled { try await clearing.value }
        let revision = await service.revision
        XCTAssertNil(revision, "A cancelled clear cannot publish its late receipt")
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 1)
    }

    func testPreparationDuringClearKeepsAdmissionClosedUntilExplicitResume() async throws {
        let entered = expectation(description: "clear accepted"), cancelled = expectation(description: "clear cancelled")
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled)
        await rpc.ignoreCancellation()
        await rpc.setResponse(.object(["rules": try filmRuleValue(FilmRuleSet()), "revision": .string("late")]))
        let service = rpc.service
        let clearing = Task { try await service.clearData() }
        await fulfillment(of: [entered], timeout: 3)
        let preparing = Task { await service.prepareShutdown() }
        await fulfillment(of: [cancelled], timeout: 3)
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.resume() }
        await rpc.release.release(); await preparing.value
        await filmRuleCancelled { try await clearing.value }
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.initialize() }
        let revision = await service.revision
        XCTAssertNil(revision, "Late clear cannot reopen admission or publish a receipt")
        try await service.resume()
        try await service.initialize()
        let rules = try await service.savedSnapshot()
        XCTAssertEqual(rules, FilmRuleSet())
        await service.close()
    }
}

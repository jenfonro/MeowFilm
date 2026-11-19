import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmRuleLifecycleTests: XCTestCase {
    func testHeldPreviewCannotBlockNativeSettingsButCloseStillPhysicallyDrains() async throws {
        let entered = expectation(description: "preview entered"), cancelled = expectation(description: "preview cancelled")
        let localFinished = expectation(description: "native settings completed while preview held")
        let closeFinished = expectation(description: "close waits for physical preview drain")
        let store = try FilmNativeSettingsStore(self, block: .rules)
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service = rpc.service(store.document)
        await rpc.ignoreCancellation(); await rpc.setResponse(try filmRulePreviewValue())
        addTeardownBlock { await rpc.release.release(); await service.close() }
        let preview = Task { try await service.process(.init(section: .movie, text: "held")) }
        await fulfillment(of: [entered], timeout: 3)
        let local = Task {
            defer { localFinished.fulfill() }
            try await service.initialize()
            let initial = try await service.savedSnapshot()
            XCTAssertEqual(initial, FilmRuleSettings.initial)
            _ = try await service.save(.init(movieRules: [.init(pattern: "whole")]))
            let grouped = try await service.save(.movie, rules: [.init(pattern: "  native  ")])
            XCTAssertEqual(grouped.movieRules, [.init(pattern: "native")])
            let restored = try await service.restoreDefaults(.search)
            XCTAssertEqual(restored.movieRules, grouped.movieRules)
            XCTAssertEqual(restored.aggregateRegexRules, FilmRuleSettings.restore.aggregateRegexRules)
            let defaults = try await service.defaults(.episode)
            XCTAssertEqual(defaults, FilmRuleSettings.restore.episodeRules)
            let match = try await service.normalizeNewMatchRule(#"  / \\d+ / gi  "#, replace: " keep ")
            let clean = try await service.normalizeNewCleanRule(#"  / \\s+ / g  "#)
            XCTAssertEqual(match, .init(pattern: #"\d+"#, replace: "keep", flags: "gi"))
            XCTAssertEqual(clean, #"/\s+/g"#)
            try await service.initialize()
            let reread = try await service.savedSnapshot()
            XCTAssertEqual(reread, restored)
            return restored
        }
        await fulfillment(of: [localFinished], timeout: 3)
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 1, "Only the held preview uses the runtime")
        closeFinished.isInverted = true
        let closing = Task { await service.close(); closeFinished.fulfill() }
        await fulfillment(of: [cancelled], timeout: 3)
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.initialize() }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.save(.init()) }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.defaults(.movie) }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.normalizeNewMatchRule("late") }
        await fulfillment(of: [closeFinished], timeout: 0.05)
        await rpc.release.release(); await closing.value
        await filmRuleCancelled { try await preview.value }
        let saved = try await local.value
        XCTAssertEqual(try FilmRuleSettings.read(store.document.readForTest()), saved,
                       "A late preview or close cannot overwrite the native commit")
        XCTAssertEqual(store.notifications, ["film.rules", "film.rules", "film.rules"])
    }

    func testCancelledGroupPreviewPhysicallyDrainsAndCannotPublishItsLateReply() async throws {
        let entered = expectation(description: "group preview entered"), cancelled = expectation(description: "group preview cancelled")
        let store = try FilmNativeSettingsStore(self, block: .rules)
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service = rpc.service(store.document)
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
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        try await service.resume()
        let saved = try await service.save(.movie, rules: [.init(pattern: "acknowledged")])
        XCTAssertEqual(saved.movieRules, [.init(pattern: "acknowledged")])
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 1, "Recovery saves locally without a second RPC")
        XCTAssertEqual(store.notifications, ["film.rules"])
        await service.close()
    }

    func testCancelledNativeGroupSaveDoesNotPublishAndCanRecoverOffline() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document), gate = FilmRuleGate()
        let running = Task {
            await gate.wait()
            return try await service.save(.movie, rules: [.init(pattern: "cancelled")])
        }
        running.cancel(); await gate.release()
        await filmRuleCancelled { try await running.value }
        await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
        let revision = await service.revision
        XCTAssertNil(revision); XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        let saved = try await service.save(.movie, rules: [.init(pattern: "recovered")])
        XCTAssertEqual(saved.movieRules, [.init(pattern: "recovered")])
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(store.notifications, ["film.rules"])
        await service.close()
    }

    func testCallerCancellationSkipsQueuedRPCAndServiceRecovers() async throws {
        let entered = expectation(description: "RPC entered"), cancelled = expectation(description: "RPC cancelled")
        let store = try FilmNativeSettingsStore(self, block: .rules)
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service = rpc.service(store.document)
        await rpc.setResponse(try filmRulePreviewValue())
        let running = Task { try await service.process(.init(section: .movie, text: "first")) }
        await fulfillment(of: [entered], timeout: 5)
        let queued = Task { try await service.process(.init(section: .movie, text: "queued")) }
        queued.cancel(); running.cancel()
        await fulfillment(of: [cancelled], timeout: 5)
        await rpc.release.release()
        await filmRuleCancelled { try await running.value }
        await filmRuleCancelled { try await queued.value }
        let next = try await service.process(.init(section: .movie, text: "next"))
        XCTAssertEqual(next.text.displayString, "preview")
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 2)
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        await service.close()
    }

    func testPrepareCancelsAndPhysicallyDrainsBeforeResume() async throws {
        let entered = expectation(description: "entered"), cancelled = expectation(description: "cancelled")
        let store = try FilmNativeSettingsStore(self, block: .rules)
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service = rpc.service(store.document)
        await rpc.setResponse(try filmRulePreviewValue())
        let running = Task { try await service.process(.init(section: .movie, text: "first")) }
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
        XCTAssertEqual(next, .init(pattern: "resumed"))
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 1, "Pure settings normalization stays offline after resume")
        await service.close()
    }

    func testConcurrentPrepareAndCloseShareDrainAndLeaveBorrowedRuntimeUsable() async throws {
        let entered = expectation(description: "entered"), cancelled = expectation(description: "cancelled")
        let store = try FilmNativeSettingsStore(self, block: .rules)
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service = rpc.service(store.document)
        let response = try filmRulePreviewValue()
        await rpc.setResponse(response)
        let running = Task { try await service.process(.init(section: .movie, text: "first")) }
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
        let borrowed = try await rpc.call("rules.preview", .object([:]))
        XCTAssertEqual(borrowed, response)
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
    }

    func testMalformedNativeDocumentDoesNotPublishAndCanRecoverWithoutFallback() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        try await store.seed(store.envelope(try filmRuleValue(FilmRuleSet(movieRules: [.init(pattern: "saved")])), key: "rules"))
        try await store.damageRules(true)
        let service = rpc.service(store.document)
        await filmRuleFailure(FilmSettingsStorageError.invalidDocument) { try await service.initialize() }
        await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
        try await store.damageRules(false)
        try await store.seed(store.envelope(try filmRuleValue(FilmRuleSet()), key: "rules"))
        try await service.initialize()
        let saved = try await service.savedSnapshot()
        XCTAssertEqual(saved, FilmRuleSet())
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty); XCTAssertTrue(store.notifications.isEmpty)
        await service.close()
    }

    func testClearDrainsOldPreviewAndPublishesOnlyNativeCommittedDefaults() async throws {
        let entered = expectation(description: "entered"), cancelled = expectation(description: "cancelled")
        let store = try FilmNativeSettingsStore(self, block: .rules)
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service = rpc.service(store.document)
        await rpc.ignoreCancellation(); await rpc.setResponse(try filmRulePreviewValue())
        let preview = Task { try await service.process(.init(section: .movie, text: "pending")) }
        await fulfillment(of: [entered], timeout: 5)
        let clearing = Task { try await service.clearData() }
        await fulfillment(of: [cancelled], timeout: 5)
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.resume() }
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        await rpc.release.release(); try await clearing.value
        await filmRuleCancelled { try await preview.value }
        let saved = try await service.savedSnapshot(), revision = await service.revision
        XCTAssertEqual(saved, FilmRuleSettings.initial); XCTAssertEqual(revision?.filmText?.count, 64)
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 1); XCTAssertEqual(store.notifications, ["film.rules"])
        await service.close()
    }

    func testClosePhysicallyDrainsClearWaitingForOldPreviewAndDoesNotCommit() async throws {
        let entered = expectation(description: "preview accepted"), cancelled = expectation(description: "preview cancelled")
        let store = try FilmNativeSettingsStore(self, block: .rules)
        let rpc = FilmRuleControlledRPC(entered: entered, cancelled: cancelled), service = rpc.service(store.document)
        await rpc.ignoreCancellation(); await rpc.setResponse(try filmRulePreviewValue())
        let preview = Task { try await service.process(.init(section: .movie, text: "pending")) }
        await fulfillment(of: [entered], timeout: 3)
        let clearing = Task { try await service.clearData() }
        await fulfillment(of: [cancelled], timeout: 3)
        let closing = Task { await service.close() }
        let deadline = ContinuousClock.now.advanced(by: .seconds(3))
        var closed = false
        while ContinuousClock.now < deadline {
            do { _ = try await service.savedSnapshot() }
            catch FilmRuleServiceError.closed { closed = true; break }
            catch {}
            await Task.yield()
        }
        XCTAssertTrue(closed, "The close boundary must be observed before releasing the old preview")
        await rpc.release.release()
        await closing.value
        await filmRuleCancelled { try await preview.value }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await clearing.value }
        await filmRuleFailure(FilmRuleServiceError.closed) { try await service.savedSnapshot() }
        let revision = await service.revision
        XCTAssertNil(revision); XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        let calls = await rpc.invocations
        XCTAssertEqual(calls, 1)
    }

    func testPreparationAfterClearKeepsAdmissionClosedUntilExplicitResume() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document)
        try await service.clearData()
        let saved = try await service.savedSnapshot(), revision = await service.revision
        XCTAssertEqual(saved, FilmRuleSettings.initial)
        await service.prepareShutdown()
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.initialize() }
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.clearData() }
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.save(.init()) }
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.defaults(.movie) }
        await filmRuleFailure(FilmRuleServiceError.shuttingDown) { try await service.normalizeNewCleanRule("late") }
        let unchanged = await service.revision
        XCTAssertEqual(unchanged, revision); XCTAssertEqual(store.notifications, ["film.rules"])
        try await service.resume()
        try await service.initialize()
        let rules = try await service.savedSnapshot()
        XCTAssertEqual(rules, FilmRuleSettings.initial)
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(store.notifications, ["film.rules"])
        await service.close()
    }
}

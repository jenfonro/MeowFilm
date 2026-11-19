import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmRulePersistenceTests: XCTestCase {
    func testGroupSaveMergesLatestNativeRulesAndPreservesOtherGroups() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        var initial = try filmRuleValue(FilmRuleSet(episodeRules: [.init(pattern: "from another consumer")])).filmRequiredObject()
        try await store.seed(store.envelope(.object(initial), key: "rules"))
        let service = rpc.service(store.document)
        let rows = [FilmMatchRule(pattern: "  raw  ", replace: " preserve ", flags: " g ")]
        let saved = try await service.save(.movie, rules: rows)
        XCTAssertEqual(saved.movieRules, [.init(pattern: "raw", replace: " preserve ", flags: "g")])
        XCTAssertEqual(saved.episodeRules, [.init(pattern: "from another consumer")])
        let document = try XCTUnwrap(store.document.readForTest())
        XCTAssertEqual(document.filmObject?["rules"]?.filmObject?["episodeRules"], initial["episodeRules"])
        let acknowledged = try await service.savedSnapshot()
        XCTAssertEqual(acknowledged, saved)
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(store.notifications, ["film.rules"])
        await service.close()
    }

    func testCleanGroupFailureRetainsSnapshotAndRetryDoesNotReplaceOtherGroups() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document)
        try await service.initialize()
        let original = try await service.savedSnapshot()
        let rows = [FilmMatchRule(pattern: "pending")]
        try store.readOnly(true)
        await filmRuleFailure(FilmSettingsStorageError.storageFailure) { try await service.save(.search, rules: rows) }
        let unchanged = try await service.savedSnapshot()
        XCTAssertEqual(unchanged, original)
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        try store.readOnly(false)
        let saved = try await service.save(.search, rules: [.init(pattern: "  retry  ")])
        XCTAssertEqual(saved.aggregateRegexRules, ["retry"])
        XCTAssertEqual(saved.movieRules, original.movieRules)
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(store.notifications, ["film.rules"])
        await service.close()
    }

    func testNativeDefaultsDoNotCommitOrInitializeSavedSnapshot() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document)
        let rows = try await service.defaults(.episodeClean)
        XCTAssertEqual(rows, FilmRuleSettings.restore.rules(in: .episodeClean))
        let restoredMatch = try await service.defaults(.episode)
        XCTAssertEqual(restoredMatch.count, 3)
        await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        try await service.initialize()
        let initial = try await service.savedSnapshot()
        XCTAssertEqual(initial, FilmRuleSettings.initial); XCTAssertEqual(initial.episodeRules.count, 2)
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testWholeDraftNormalizationIsNativeAndInvalidRegexRemainsAStoredRule() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document)
        let draft = FilmRuleSet(episodeCleanRegexRules: ["  raw  ", "", "\u{feff}\u{0085} wrapped \u{0085}\u{feff}"],
            movieRules: [.init(pattern: "[", replace: " keep invalid "),
                         .init(pattern: #"  \\\\d+  "#, replace: " keep ", flags: " g "), .init(pattern: "  ")],
            aggregateRegexRules: ["\u{feff} padded \u{00a0}"])
        let saved = try await service.save(draft)
        XCTAssertEqual(saved.episodeCleanRegexRules, ["raw", "wrapped"])
        XCTAssertEqual(saved.movieRules, [.init(pattern: "[", replace: " keep invalid "),
                                         .init(pattern: #"\d+"#, replace: " keep ", flags: "g")])
        XCTAssertEqual(saved.aggregateRegexRules, ["padded"])
        let revision = await service.revision
        XCTAssertEqual(revision?.filmText?.count, 64)
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty, "Storage normalization cannot execute a local regex or ask Node")
        XCTAssertEqual(store.notifications, ["film.rules"])
        await service.close()
    }

    func testEmptyGroupsStayEmptyAcrossReopenWithoutDefaultRefill() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let first = rpc.service(store.document)
        let saved = try await first.save(.init())
        XCTAssertEqual(saved, FilmRuleSet())
        await first.close()
        let second = rpc.service(store.makeDocument())
        try await second.initialize()
        let reloaded = try await second.savedSnapshot()
        XCTAssertEqual(reloaded, saved)
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(store.notifications, ["film.rules"])
        await second.close()
    }

    func testMalformedRulesNeverFallbackAndExplicitRecoveryPreservesSnapshot() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document)
        _ = try await service.save(.init(movieRules: [.init(pattern: "saved")]))
        let original = try await service.savedSnapshot(), before = try XCTUnwrap(store.document.readForTest())
        var wrongRow = try filmRuleValue(original).filmRequiredObject()
        wrongRow["movieRules"] = .array([.object(["pattern": .string("x"), "flags": .number(1)])])
        for invalid: RuntimeValue in [.object([:]), .object(["version": .number(2), "rules": try filmRuleValue(original)]),
                                     store.envelope(.object(wrongRow), key: "rules")] {
            await filmRuleFailure(FilmSettingsStorageError.invalidDocument) { try await store.seed(invalid) }
            XCTAssertEqual(try store.document.readForTest(), before, "Malformed proposals cannot change typed rows")
            let unchanged = try await service.savedSnapshot()
            XCTAssertEqual(unchanged, original)
        }
        try await store.damageRules(true)
        await filmRuleFailure(FilmSettingsStorageError.invalidDocument) { try await service.initialize() }
        await filmRuleFailure(FilmSettingsStorageError.invalidDocument) { try await service.save(.init()) }
        let unchanged = try await service.savedSnapshot()
        XCTAssertEqual(unchanged, original)
        try await store.damageRules(false)
        let draft = FilmRuleSet(aggregateRegexRules: [String(repeating: "x", count: 20_000)])
        let saved = try await service.save(draft)
        XCTAssertEqual(saved, draft, "The consumer imposes no arbitrary rule length limit")
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(store.notifications.count, 2)
        await service.close()
    }

    func testRestoreUsesWholeDraftAndOmissionReadsLatestNativeRules() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document)
        let draft = FilmRuleSet(movieRules: [.init(pattern: "other section pending")])
        let first = try await service.restoreDefaults(.episode, draft: draft)
        XCTAssertEqual(first.movieRules, draft.movieRules)
        XCTAssertEqual(first.episodeRules, FilmRuleSettings.restore.episodeRules)
        XCTAssertEqual(first.episodeCleanRegexRules, FilmRuleSettings.restore.episodeCleanRegexRules)
        let second = try await service.restoreDefaults(.search)
        XCTAssertEqual(second.movieRules, draft.movieRules)
        XCTAssertEqual(second.episodeRules, first.episodeRules)
        XCTAssertEqual(second.aggregateRegexRules, FilmRuleSettings.restore.aggregateRegexRules)
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(store.notifications, ["film.rules", "film.rules"])
        await service.close()
    }

    func testConcurrentEditsReceiveEveryCommitAndNewConsumerReadsLastSnapshot() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document)
        let receipts = try await withThrowingTaskGroup(of: FilmRuleSet.self) { group in
            for index in 0..<8 { group.addTask { try await service.save(.init(aggregateRegexRules: ["draft-\(index)"])) } }
            var result: [FilmRuleSet] = []
            for try await item in group { result.append(item) }
            return result
        }
        XCTAssertEqual(receipts.count, 8)
        let saved = try await service.savedSnapshot()
        XCTAssertTrue(receipts.contains(saved))
        await service.close()
        let reopened = rpc.service(store.makeDocument())
        try await reopened.initialize()
        let fetched = try await reopened.savedSnapshot()
        XCTAssertEqual(fetched, saved)
        let calls = await rpc.calls
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(store.notifications.count, 8)
        await reopened.close()
    }

    func testCASRejectsExternalChangeAndNewConsumerReadsItWithoutRollback() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document)
        _ = try await service.save(.init(movieRules: [.init(pattern: "first")]))
        let external = store.envelope(try filmRuleValue(FilmRuleSet(movieRules: [.init(pattern: "external")])), key: "rules")
        try await store.seed(external)
        do { _ = try await service.save(FilmRuleSet()); XCTFail("stale complete rule draft committed") }
        catch { XCTAssertEqual(error as? FilmSettingsStorageError, .conflict) }
        let old = try await service.savedSnapshot()
        XCTAssertEqual(old.movieRules.first?.pattern, "first")
        try await service.initialize()
        let latest = try await service.savedSnapshot()
        XCTAssertEqual(latest.movieRules.first?.pattern, "external")
        XCTAssertEqual(store.notifications, ["film.rules"])
        await service.close()
    }
}

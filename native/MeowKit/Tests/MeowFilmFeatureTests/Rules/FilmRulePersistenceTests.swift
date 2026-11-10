import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmRulePersistenceTests: XCTestCase {
    func testWholeDraftAndNormalizationReceiptAreOwnedByNode() async throws {
        let rpc = FilmRuleRPC(), service: FilmRuleService
        service = rpc.service
        let draft = FilmRuleSet(episodeCleanRegexRules: ["  raw  ", ""],
            movieRules: [.init(pattern: "[", replace: " keep invalid ")], aggregateRegexRules: ["raw"])
        let acknowledged = FilmRuleSet(episodeCleanRegexRules: ["normalized by Node"],
            movieRules: draft.movieRules, aggregateRegexRules: ["raw"])
        await rpc.setResponse(.object(["rules": try filmRuleValue(acknowledged), "revision": .string("opaque")]))
        let saved = try await service.save(draft)
        XCTAssertEqual(saved, acknowledged)
        let calls = await rpc.calls
        XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].0, "rules.set")
        XCTAssertEqual(calls[0].1, .object(["rules": try filmRuleValue(draft)]))
        let revision = await service.revision
        XCTAssertEqual(revision, .string("opaque"))
        await service.close()
    }

    func testEmptyGroupsStayEmptyAcrossConsumersWithoutClientDefaultRefill() async throws {
        let rpc = FilmRuleRPC(), first = rpc.service
        let saved = try await first.save(.init())
        XCTAssertEqual(saved, FilmRuleSet())
        await first.close()
        let second = rpc.service
        try await second.initialize()
        let reloaded = try await second.savedSnapshot()
        XCTAssertEqual(reloaded, saved)
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["rules.set", "rules.get"])
        await second.close()
    }

    func testStorageFailurePreservesAcknowledgedSnapshotAndDraftCanRetry() async throws {
        for code in ["STORAGE_ERROR", "STORAGE_CHANGED", "CORRUPT_STORAGE", "INVALID_RULES"] {
            let rpc = FilmRuleRPC(), service = rpc.service
            try await service.initialize()
            let original = try await service.savedSnapshot()
            let draft = FilmRuleSet(aggregateRegexRules: [String(repeating: "x", count: 20_000)])
            await rpc.setFailure(.remote(code))
            await filmRuleFailure(RuntimeHTTPError.remote(code)) { try await service.save(draft) }
            let unchanged = try await service.savedSnapshot()
            XCTAssertEqual(unchanged, original)
            await rpc.setFailure(nil)
            let saved = try await service.save(draft)
            XCTAssertEqual(saved, draft, "The consumer imposes no rule length limit")
            let calls = await rpc.calls
            XCTAssertEqual(calls.map(\.0), ["rules.get", "rules.set", "rules.set"])
            await service.close()
        }
    }

    func testRestoreSendsWholeDraftAndOmissionUsesNodeSavedRules() async throws {
        let rpc = FilmRuleRPC(), service = rpc.service
        let draft = FilmRuleSet(movieRules: [.init(pattern: "other section pending")])
        _ = try await service.restoreDefaults(.episode, draft: draft)
        _ = try await service.restoreDefaults(.search)
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["rules.restore", "rules.restore"])
        XCTAssertEqual(calls[0].1, .object(["section": .string("episode"), "rules": try filmRuleValue(draft)]))
        XCTAssertEqual(calls[1].1, .object(["section": .string("search")]))
        await service.close()
    }

    func testConcurrentEditsReceiveEveryReceiptAndNewConsumerReadsLastAcknowledgement() async throws {
        let rpc = FilmRuleRPC(), service = rpc.service
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
        let reopened = rpc.service
        try await reopened.initialize()
        let fetched = try await reopened.savedSnapshot()
        XCTAssertEqual(fetched, saved)
        await reopened.close()
    }
}

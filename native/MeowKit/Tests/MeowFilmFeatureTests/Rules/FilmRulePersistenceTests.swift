import Foundation
import XCTest
import MeowStorage
@testable import MeowFilmFeature

final class FilmRulePersistenceTests: XCTestCase {
    func testSaveTrimsCleanRowsInServerOrderWithoutTrimmingMatchingJSONFields() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        // Fixed server/main cases: page JS trim, Go TrimSpace on stored rows,
        // then JS response decoding. In particular, Go does not trim BOM.
        let rows = ["\u{0085}foo\u{0085}", "\u{0085}", "\u{0085}\u{FEFF}bar\u{FEFF}\u{0085}",
                    "a\u{0085}b", "\u{FEFF}\u{0085}baz\u{0085}\u{FEFF}", "\u{200B}keep\u{200B}"]
        let documentRows = ["foo", "\u{FEFF}bar\u{FEFF}", "a\u{0085}b", "baz", "\u{200B}keep\u{200B}"]
        let decodedRows = ["foo", "bar", "a\u{0085}b", "baz", "\u{200B}keep\u{200B}"]
        let matching = [FilmMatchRule(pattern: "\u{0085}movie\u{0085}", replace: "\u{0085}keep\u{0085}")]
        let draft = FilmRuleSet(episodeCleanRegexRules: rows, episodeRules: matching,
                                movieRules: matching, aggregateRegexRules: rows)
        let request = FilmRuleRequest(section: .search, text: "foo")
        do {
            let before = try await service.process(request, draft: draft)
            XCTAssertEqual(before.processedText, FilmRuleText("foo"))
            XCTAssertNil(try storage.store.read(key: FilmRuleService.documentKey))
            let saved = try await service.save(draft)
            XCTAssertEqual(saved.episodeCleanRegexRules, decodedRows)
            XCTAssertEqual(saved.aggregateRegexRules, decodedRows)
            XCTAssertEqual(saved.episodeRules, matching)
            XCTAssertEqual(saved.movieRules, matching)
            let bytes = try XCTUnwrap(storage.store.read(key: FilmRuleService.documentKey))
            // JSONSerialization strips a leading BOM inside a JSON string.
            // Decode the storage representation without changing its contents.
            let document = try JSONDecoder().decode([String: [String]].self, from: bytes)
            for group in ["episodeCleanRegexRules", "aggregateRegexRules"] {
                XCTAssertEqual(document[group], documentRows, group)
            }
            for group in ["episodeRules", "movieRules"] {
                let encoded = try XCTUnwrap(document[group])
                let decoded = try encoded.map { try JSONDecoder().decode([String: String].self, from: Data($0.utf8)) }
                XCTAssertEqual(decoded, [["pattern": "\u{0085}movie\u{0085}", "replace": "\u{0085}keep\u{0085}"]],
                               "Go trims the JSON row, not its fields; empty flags are omitted in storage")
            }
            let after = try await service.process(request)
            XCTAssertEqual(after.processedText, FilmRuleText(""))
            let unsaved = try await service.process(request, draft: draft)
            XCTAssertEqual(unsaved, before, "Processing the original draft must not adopt save normalization")
            XCTAssertEqual(try storage.store.read(key: FilmRuleService.documentKey), bytes)
            await service.close()

            let reopened = try storage.nativeService()
            do {
                try await reopened.initialize()
                let snapshot = try await reopened.savedSnapshot()
                XCTAssertEqual(snapshot, saved)
                let result = try await reopened.process(request)
                XCTAssertEqual(result, after)
                XCTAssertEqual(try storage.store.read(key: FilmRuleService.documentKey), bytes)
                await reopened.close()
            } catch { await reopened.close(); throw error }
        } catch { await service.close(); throw error }
    }

    func testEmptyEpisodeMatchingPreservesCleanedDraftAndSavedResultsWithoutRefilling() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        let draft = FilmRuleSet(episodeCleanRegexRules: [#"^noise\s*"#])
        let request = FilmRuleRequest(section: .episode, text: "noise E2")
        do {
            let before = try await service.process(request, draft: draft)
            XCTAssertNil(try storage.store.read(key: FilmRuleService.documentKey))
            let saved = try await service.save(draft)
            XCTAssertEqual(saved, draft)
            let bytes = try XCTUnwrap(storage.store.read(key: FilmRuleService.documentKey))
            try await service.initialize()
            let snapshot = try await service.savedSnapshot()
            XCTAssertEqual(snapshot, draft, "The same initialized owner must not refill saved empty groups")
            let after = try await service.process(request)
            let explicit = try await service.process(request, draft: saved)
            // Expected fields are from the shared empty-matching server regression.
            for result in [before, after, explicit] {
                XCTAssertEqual(result.section, .episode)
                XCTAssertEqual(result.type, "error")
                XCTAssertEqual(result.text, FilmRuleText("无匹配规则"))
                XCTAssertEqual(result.inputText, FilmRuleText("noise E2"))
                XCTAssertEqual(result.processedText, FilmRuleText("E2"))
                XCTAssertEqual(result.outputText, FilmRuleText("E2"))
                XCTAssertEqual(result.previews, [FilmRuleText("E2")])
                XCTAssertFalse(result.matched)
                XCTAssertNil(result.matchedRuleIndex)
                XCTAssertFalse(result.rewriteApplied)
                XCTAssertNil(result.rewrittenText)
                XCTAssertEqual(result.groupKey, FilmRuleText(""))
                XCTAssertEqual(result.trailingDigits, FilmRuleText(""))
                XCTAssertTrue(result.errors.isEmpty)
            }
            var matchingDraft = draft
            matchingDraft.episodeRules = [FilmMatchRule(pattern: "E2")]
            let control = try await service.process(request, draft: matchingDraft)
            XCTAssertTrue(control.matched)
            XCTAssertEqual(control.processedText, FilmRuleText("E2"))
            XCTAssertEqual(control.outputText, FilmRuleText("E2"))
            XCTAssertEqual(control.previews, [FilmRuleText("E2")])
            let stillSaved = try await service.savedSnapshot()
            XCTAssertEqual(stillSaved, draft)
            XCTAssertEqual(try storage.store.read(key: FilmRuleService.documentKey), bytes)
            await service.close()
        } catch { await service.close(); throw error }
    }

    func testEmptySaveRestartRestoreWholeDraftAndInvalidRegexPersistence() async throws {
        let storage = try FilmRuleTestStore()
        let first = try storage.nativeService()
        do {
            try await first.initialize()
            let initial = try await first.savedSnapshot()
            XCTAssertEqual(initial.episodeRules.count, 2)
            let empty = try await first.save(FilmRuleSet())
            XCTAssertEqual(empty, FilmRuleSet())
            try await first.initialize()
            let stillEmpty = try await first.savedSnapshot()
            XCTAssertEqual(stillEmpty, empty)
            let result = try await first.process(FilmRuleRequest(section: .episode, text: "E1"))
            XCTAssertEqual(result.text, FilmRuleText("无匹配规则"))
            await first.close()
        } catch { await first.close(); throw error }

        let second = try storage.nativeService()
        do {
            try await second.initialize()
            let initial = try await second.savedSnapshot()
            XCTAssertEqual(initial.episodeCleanRegexRules.count, 6)
            XCTAssertEqual(initial.episodeRules.count, 2)
            XCTAssertEqual(initial.movieRules.count, 1)
            XCTAssertEqual(initial.aggregateRegexRules.count, 7)
            let added = try await second.normalizeNewMatchRule(#" /(\d+)/g "#, replace: #" \1 "#)
            XCTAssertEqual(added, FilmMatchRule(pattern: #"(\d+)"#, replace: #"\1"#, flags: "g"))
            let noMatch = try await second.normalizeNewMatchRule("   ")
            XCTAssertNil(noMatch)
            let clean = try await second.normalizeNewCleanRule(" /foo/m ")
            XCTAssertEqual(clean, "/foo/m")
            var draft = initial
            draft.movieRules = [FilmMatchRule(pattern: "[", replace: " keep invalid ")]
            draft.aggregateRegexRules = ["  custom  ", "", "custom"]
            let restored = try await second.restoreDefaults(.episode, draft: draft)
            XCTAssertEqual(restored.episodeRules.count, 3)
            XCTAssertEqual(restored.episodeCleanRegexRules.count, 6)
            XCTAssertEqual(restored.movieRules, draft.movieRules)
            XCTAssertEqual(restored.aggregateRegexRules, ["custom", "custom"])
            draft.movieRules[0].pattern = "changed after saving"
            let snapshot = try await second.savedSnapshot()
            XCTAssertEqual(snapshot, restored, "Draft and saved snapshot have value semantics")
            let bytes = try XCTUnwrap(storage.store.read(key: FilmRuleService.documentKey))
            let document = try XCTUnwrap(JSONSerialization.jsonObject(with: bytes) as? [String: Any])
            XCTAssertNotNil(document["movieRules"] as? [String], "Persistent match rows retain shared encoded string format")
            await second.close()
        } catch { await second.close(); throw error }

        let third = try storage.nativeService()
        do {
            try await third.initialize()
            let snapshot = try await third.savedSnapshot()
            XCTAssertEqual(snapshot.episodeRules.count, 3)
            XCTAssertEqual(snapshot.movieRules.first?.pattern, "[")
            let result = try await third.process(FilmRuleRequest(section: .movie, text: "film"))
            XCTAssertEqual(result.text, FilmRuleText("未命中（#1 正则无效）"))
            XCTAssertEqual(result.errors.first?.ruleIndex, 1)
            await third.close()
        } catch { await third.close(); throw error }
    }

    func testDraftProcessingDoesNotInitializeOrWriteAndMatchesSavedEntry() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        do {
            let draft = FilmRuleSet(movieRules: [FilmMatchRule(pattern: "film", replace: "movie", flags: "g")])
            let request = FilmRuleRequest(section: .movie, text: "film film")
            let before = try await service.process(request, draft: draft)
            XCTAssertEqual(before.outputText, FilmRuleText("movie movie"))
            XCTAssertNil(try storage.store.read(key: FilmRuleService.documentKey))
            await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
            let saved = try await service.save(draft)
            let document = try storage.store.read(key: FilmRuleService.documentKey)
            let after = try await service.process(request)
            let explicit = try await service.process(request, draft: saved)
            XCTAssertEqual(before, after)
            XCTAssertEqual(after, explicit)
            XCTAssertEqual(try storage.store.read(key: FilmRuleService.documentKey), document)
            await service.close()
        } catch { await service.close(); throw error }
    }

    func testCorruptStorageNeverReachesEngineOrOverwritesDocument() async throws {
        for text in ["{", "[]", #"{"episodeCleanRegexRules":[42],"episodeRules":[],"movieRules":[],"aggregateRegexRules":[]}"#] {
            let storage = try FilmRuleTestStore()
            let bytes = Data(text.utf8)
            try storage.store.write(bytes, key: FilmRuleService.documentKey)
            let engine = FilmRuleControlledEngine(entered: XCTestExpectation(description: "unused"), cancelled: XCTestExpectation(description: "unused"))
            // No expectation is registered for an invocation; corruption is rejected before JS.
            await engine.disableBlocking()
            let service = FilmRuleService(engine: engine, store: storage.store)
            await filmRuleFailure(FilmRuleServiceError.corruptDocument) { try await service.initialize() }
            let calls = await engine.invocations
            XCTAssertEqual(calls, 0)
            XCTAssertEqual(try storage.store.read(key: FilmRuleService.documentKey), bytes)
            await service.close()
        }
    }

    func testExternalEditCASConflictPreservesSavedSnapshotAndExternalBytes() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        do {
            try await service.initialize()
            let original = try await service.savedSnapshot()
            let external = Data("external edit".utf8)
            try storage.store.write(external, key: FilmRuleService.documentKey)
            await filmRuleFailure(DocumentStoreError.revisionConflict) { try await service.save(FilmRuleSet()) }
            let snapshot = try await service.savedSnapshot()
            XCTAssertEqual(snapshot, original)
            XCTAssertEqual(try storage.store.read(key: FilmRuleService.documentKey), external)
            await service.close()
        } catch { await service.close(); throw error }
    }

    func testWriteFailureLeavesDraftUnsavedAndOwnerRecoverable() async throws {
        let storage = try FilmRuleTestStore(maximumDocumentBytes: 16_384)
        let service = try storage.nativeService()
        do {
            try await service.initialize()
            let original = try await service.savedSnapshot()
            let originalBytes = try storage.store.read(key: FilmRuleService.documentKey)
            var draft = original
            draft.aggregateRegexRules = [String(repeating: "x", count: 20_000)]
            await filmRuleFailure(DocumentStoreError.documentTooLarge) { try await service.save(draft) }
            let snapshot = try await service.savedSnapshot()
            XCTAssertEqual(snapshot, original)
            XCTAssertEqual(try storage.store.read(key: FilmRuleService.documentKey), originalBytes)
            draft.aggregateRegexRules = ["custom"]
            let saved = try await service.save(draft)
            XCTAssertEqual(saved.aggregateRegexRules, ["custom"])
            await service.close()
        } catch { await service.close(); throw error }
    }

    func testClearDataRemovesDocumentAndReinitializesSameOwner() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        do {
            _ = try await service.save(FilmRuleSet(movieRules: [FilmMatchRule(pattern: "custom")]))
            try await service.clearData()
            XCTAssertNil(try storage.store.read(key: FilmRuleService.documentKey))
            await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
            try await service.initialize()
            let reset = try await service.savedSnapshot()
            XCTAssertEqual(reset.episodeRules.count, 2)
            XCTAssertNotEqual(reset.movieRules.first?.pattern, "custom")
            await service.close()
        } catch { await service.close(); throw error }
    }

    func testConcurrentSavesSerializeWholeTransactionsAndReopenMatchesPublishedSnapshot() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        do {
            try await service.initialize()
            let initial = try await service.savedSnapshot()
            let receipts = try await withThrowingTaskGroup(of: FilmRuleSet.self) { group in
                for index in 0..<8 {
                    var draft = initial
                    draft.aggregateRegexRules = ["draft-\(index)"]
                    let submitted = draft
                    group.addTask { try await service.save(submitted) }
                }
                var values: [FilmRuleSet] = []
                for try await value in group { values.append(value) }
                return values
            }
            XCTAssertEqual(receipts.count, 8, "Each admitted save receives a committed receipt")
            let published = try await service.savedSnapshot()
            XCTAssertTrue(receipts.contains(published))
            await service.close()
            let reopened = try storage.nativeService()
            do {
                try await reopened.initialize()
                let persisted = try await reopened.savedSnapshot()
                XCTAssertEqual(persisted, published, "CAS bytes and published snapshot describe the same last transaction")
                await reopened.close()
            } catch { await reopened.close(); throw error }
        } catch { await service.close(); throw error }
    }
}

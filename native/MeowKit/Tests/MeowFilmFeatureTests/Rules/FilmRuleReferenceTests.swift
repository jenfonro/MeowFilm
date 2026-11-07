import Foundation
import FilmRuleProgram
import XCTest
import MeowJavaScript
@testable import MeowFilmFeature

final class FilmRuleReferenceTests: XCTestCase {
    /// Expectations are the shared, fixed dashboard/server scenarios, not Swift output.
    func testNativeEntryMatchesEverySharedReferenceCaseTwice() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        do {
            XCTAssertEqual(try FilmRuleProgram.load(), try String(
                contentsOf: FilmRuleTestStore.package.appendingPathComponent("film-rules.js"), encoding: .utf8))
            try await service.initialize()
            let initial = try await service.savedSnapshot()
            for section in FilmRuleSection.allCases { _ = try await service.restoreDefaults(section) }
            let restored = try await service.savedSnapshot()
            let fixtureData = try Data(contentsOf: FilmRuleTestStore.package.appendingPathComponent("test/reference-cases.json"))
            let fixtures = try XCTUnwrap(JSONSerialization.jsonObject(with: fixtureData) as? [[String: Any]])
            XCTAssertFalse(fixtures.isEmpty)
            let bytesBefore = try storage.store.read(key: FilmRuleService.documentKey)
            for fixture in fixtures {
                let name = try XCTUnwrap(fixture["name"] as? String)
                let setup = try XCTUnwrap(fixture["setup"] as? String)
                let base: FilmRuleSet
                switch setup {
                case "initial": base = initial
                case "restored": base = restored
                case "empty": base = FilmRuleSet()
                default: XCTFail("Unknown fixture setup: \(setup)"); continue
                }
                var object = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(base)) as? [String: Any])
                if let edits = fixture["rules"] as? [String: Any] {
                    for (key, value) in edits {
                        // Fixed JS fixtures omit optional match fields; public Swift
                        // models express those fields explicitly, with the same defaults.
                        if key == "episodeRules" || key == "movieRules" {
                            let rows = try XCTUnwrap(value as? [[String: Any]])
                            object[key] = rows.map { row -> [String: Any] in
                                var complete: [String: Any] = ["replace": "", "flags": ""]
                                complete.merge(row) { _, new in new }
                                return complete
                            }
                        } else { object[key] = value }
                    }
                }
                let rules = try JSONDecoder().decode(FilmRuleSet.self, from: JSONSerialization.data(withJSONObject: object))
                let requestObject = try XCTUnwrap(fixture["request"])
                let request = try JSONDecoder().decode(FilmRuleRequest.self, from: JSONSerialization.data(withJSONObject: requestObject))
                let expected = try XCTUnwrap(fixture["expected"] as? [String: Any])
                for _ in 0..<2 {
                    let result = try await service.process(request, draft: rules)
                    var actual = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(result)) as? [String: Any])
                    // JSONEncoder omits nil; the shared JSON ABI emits explicit null.
                    if result.matchedRuleIndex == nil { actual["matchedRuleIndex"] = NSNull() }
                    if result.rewrittenText == nil { actual["rewrittenText"] = NSNull() }
                    for (key, value) in expected {
                        let observed = try XCTUnwrap(actual[key], "\(name): \(key)")
                        let expectedBytes = try JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed, .sortedKeys])
                        let actualBytes = try JSONSerialization.data(withJSONObject: observed, options: [.fragmentsAllowed, .sortedKeys])
                        XCTAssertEqual(actualBytes, expectedBytes, "\(name): \(key)")
                    }
                }
            }
            let request = FilmRuleRequest(section: .search, text: "作品2", query: "作品2")
            let savedResult = try await service.process(request)
            let draftResult = try await service.process(request, draft: restored)
            XCTAssertEqual(savedResult, draftResult)
            XCTAssertEqual(try storage.store.read(key: FilmRuleService.documentKey), bytesBefore)
            print("EVIDENCE film-rules shared fixtures=\(fixtures.count); repeated=2; engine=native QuickJS; entry=FilmRuleService.process")
            await service.close()
        } catch { await service.close(); throw error }
    }

    func testNativeCancellationAndTimeoutRecoverOnSameService() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        do {
            try await service.initialize()
            let expensive = FilmRuleSet(movieRules: [FilmMatchRule(pattern: "^(a+)+$", replace: "x")])
            let request = FilmRuleRequest(section: .movie, text: String(repeating: "a", count: 64) + "!")
            let running = Task { try await service.process(request, draft: expensive) }
            try await Task.sleep(nanoseconds: 100_000_000)
            running.cancel()
            await filmRuleCancelled { try await running.value }
            try await assertRecovered(service)
            do {
                _ = try await service.process(request, draft: expensive)
                XCTFail("Expected native execution deadline")
            } catch {
                XCTAssertEqual(error as? NativeJavaScriptError, .timedOut)
            }
            try await assertRecovered(service)
            await service.close()
        } catch { await service.close(); throw error }
    }

    private func assertRecovered(_ service: FilmRuleService) async throws {
        let result = try await service.process(FilmRuleRequest(section: .search, text: "作品2", query: "作品2"))
        XCTAssertEqual(result.type, "success")
        XCTAssertEqual(result.groupKey, FilmRuleText("作品2"))
    }
}

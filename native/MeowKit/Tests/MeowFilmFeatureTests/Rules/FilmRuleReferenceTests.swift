import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmRuleReferenceTests: XCTestCase {
    func testDraftPreviewDoesNotGetSaveOrInitializeAndForwardsExactUnicodeInput() async throws {
        let rpc = FilmRuleRPC(), service = rpc.service(try FilmNativeSettingsStore(self, block: .rules).document)
        let output = try RuntimeJSON.decode(Data(#"{"section":"movie","type":"success","text":[120,57260],"inputText":"🎬","processedText":"🎬","matched":true,"matchedRuleIndex":1,"rewriteApplied":true,"rewrittenText":[120,57260],"groupKey":"","trailingDigits":"","errors":[],"previews":["🎬"]}"#.utf8))
        await rpc.setResponse(output)
        let request = FilmRuleRequest(section: .movie, text: "🎬", query: "ＡK中文"), draft = FilmRuleSet(movieRules: [.init(pattern: "^.", replace: "x")])
        let first = try await service.process(request, draft: draft)
        let second = try await service.process(request)
        let grouped = try await service.process(request, group: .movie, rules: draft.movieRules)
        XCTAssertEqual(first, second)
        XCTAssertEqual(grouped, first)
        XCTAssertEqual(first.outputText.utf16, [0x78, 0xDFAC])
        await filmRuleFailure(FilmRuleServiceError.notInitialized) { try await service.savedSnapshot() }
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["rules.preview", "rules.preview", "rules.group.preview"])
        XCTAssertEqual(calls[0].1, .object(["request": try filmRuleValue(request), "rules": try filmRuleValue(draft)]))
        XCTAssertEqual(calls[1].1, .object(["request": try filmRuleValue(request)]))
        XCTAssertEqual(calls[2].1, .object(["request": try filmRuleValue(request), "group": .string("movie"),
                                           "rules": try filmRuleValue(draft.movieRules)]))
        await service.close()
    }

    func testNewRuleStringNormalizationIsOfflineWhileExecutionErrorsRemainOnline() async throws {
        let store = try FilmNativeSettingsStore(self, block: .rules), rpc = FilmRuleRPC()
        let service = rpc.service(store.document)
        let empty = try await service.normalizeNewMatchRule(" \u{feff} ")
        XCTAssertNil(empty)
        let clean = try await service.normalizeNewCleanRule("  ")
        XCTAssertEqual(clean, "")
        let match = try await service.normalizeNewMatchRule(#"  / \\d+ / gi  "#, replace: " keep ")
        XCTAssertEqual(match, .init(pattern: #"\d+"#, replace: "keep", flags: "gi"))
        let cleanMatch = try await service.normalizeNewCleanRule(#"  / \\s+ / g "#)
        XCTAssertEqual(cleanMatch, #"/\s+/g"#)
        let retainedNEL = try await service.normalizeNewMatchRule("\u{0085}x\u{0085}")
        XCTAssertEqual(retainedNEL?.pattern, "\u{0085}x\u{0085}")
        XCTAssertNil(try store.document.readForTest()); XCTAssertTrue(store.notifications.isEmpty)
        let offlineCalls = await rpc.calls
        XCTAssertTrue(offlineCalls.isEmpty)
        await rpc.setFailure(.remote("CAPABILITY_UNAVAILABLE"))
        await filmRuleFailure(RuntimeHTTPError.remote("CAPABILITY_UNAVAILABLE")) {
            try await service.process(.init(section: .movie, text: "x"))
        }
        await rpc.setFailure(nil); await rpc.setResponse(.object([:]))
        await filmRuleFailure(FilmRuleServiceError.invalidResponse) {
            try await service.process(.init(section: .movie, text: "x"))
        }
        let calls = await rpc.calls
        XCTAssertEqual(calls.map(\.0), ["rules.preview", "rules.preview"], "No local regex engine or retry fallback")
        await service.close()
    }
}

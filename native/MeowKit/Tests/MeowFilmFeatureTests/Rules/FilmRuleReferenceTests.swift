import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmRuleReferenceTests: XCTestCase {
    func testDraftPreviewDoesNotGetSaveOrInitializeAndForwardsExactUnicodeInput() async throws {
        let rpc = FilmRuleRPC(), service = rpc.service
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

    func testNormalizeEmptyIsSuccessWhileUnavailableAndMalformedRemainErrors() async throws {
        let rpc = FilmRuleRPC(), service = rpc.service
        let empty = try await service.normalizeNewMatchRule("   ")
        XCTAssertNil(empty)
        await rpc.setResponse(.string(""))
        let clean = try await service.normalizeNewCleanRule("  ")
        XCTAssertEqual(clean, "")
        await rpc.setFailure(.remote("CAPABILITY_UNAVAILABLE"))
        await filmRuleFailure(RuntimeHTTPError.remote("CAPABILITY_UNAVAILABLE")) { try await service.normalizeNewMatchRule("x") }
        await rpc.setFailure(nil); await rpc.setResponse(.object([:]))
        await filmRuleFailure(FilmRuleServiceError.invalidResponse) { try await service.normalizeNewMatchRule("x") }
        let calls = await rpc.calls
        XCTAssertEqual(calls.count, 4, "No native engine or repeat-request fallback")
        XCTAssertEqual(calls[0].1, .object(["kind": .string("match"), "pattern": .string("   "), "replace": .string("")]))
        await service.close()
    }
}

import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class FilmRuleTextTests: XCTestCase {
    func testTextRoundTripsEveryUTF16UnitAndKeepsDisplaySeparate() throws {
        // Covers every code unit, including isolated high/low surrogates.
        let units = (UInt32(0)...0xFFFF).map(UInt16.init)
        let text = FilmRuleText(utf16: units)
        let encoded = try JSONEncoder().encode(text)
        let decoded = try JSONDecoder().decode(FilmRuleText.self, from: encoded)
        XCTAssertEqual(decoded.utf16, units)
        XCTAssertEqual(text.utf16, units, "Encoding must not repair the business value")
        XCTAssertFalse(text.isEmpty)

        let isolated = FilmRuleText(utf16: [0x78, 0xDFAC])
        XCTAssertEqual(isolated.displayString, "x\u{FFFD}")
        XCTAssertEqual(isolated.utf16, [0x78, 0xDFAC])
        XCTAssertNotEqual(isolated, FilmRuleText(isolated.displayString))
        XCTAssertNotEqual(FilmRuleText("\u{00E9}"), FilmRuleText("e\u{0301}"))
        for value in ["", "中文", "🎬", "e\u{0301}", "\"\\\n\u{0000}"] {
            let normal = FilmRuleText(value)
            XCTAssertEqual(try JSONDecoder().decode(String.self, from: JSONEncoder().encode(normal)), value)
            XCTAssertEqual(try JSONDecoder().decode(FilmRuleText.self, from: JSONEncoder().encode(normal)), normal)
            XCTAssertEqual(normal.isEmpty, value.isEmpty)
        }
    }

    func testMalformedCodeUnitRepresentationIsRejected() throws {
        for json in ["null", "{}", "[65536]", "[-1]", "[1.5]", "[true]", #"["x"]"#] {
            XCTAssertThrowsError(try JSONDecoder().decode(FilmRuleText.self, from: Data(json.utf8)), json)
        }
    }

    func testRPCCodeUnitArraysKeepEveryResultFieldAndNestedError() async throws {
        let json = #"{"section":"search","type":"error","text":[55296],"inputText":[56320],"processedText":[55296,120,57343],"matched":false,"matchedRuleIndex":null,"rewriteApplied":false,"rewrittenText":null,"groupKey":[55296,120,57343],"trailingDigits":"","errors":[{"group":"search","ruleIndex":1,"text":[57343]}],"previews":[[55296],[56320]]}"#
        let rpc = FilmRuleRPC(), service = rpc.service
        await rpc.setResponse(try RuntimeJSON.decode(Data(json.utf8)))
        let result = try await service.process(.init(section: .search, text: "original"))
        XCTAssertEqual(result.text.utf16, [0xD800]); XCTAssertEqual(result.inputText.utf16, [0xDC00])
        XCTAssertEqual(result.groupKey.utf16, [0xD800, 120, 0xDFFF])
        XCTAssertEqual(result.outputText.utf16, result.groupKey.utf16)
        XCTAssertEqual(result.errors.first?.text.utf16, [0xDFFF])
        XCTAssertEqual(result.previews.map(\.utf16), [[0xD800], [0xDC00]])
        XCTAssertEqual(try JSONDecoder().decode(FilmRuleResult.self, from: JSONEncoder().encode(result)), result)
        await service.close()
    }
}

import Foundation
import XCTest
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

    func testNativeMovieResultPreservesUnpairedSurrogateInDraftAndSavedRules() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        do {
            let request = FilmRuleRequest(section: .movie, text: "🎬")
            let rules = FilmRuleSet(movieRules: [.init(pattern: "^.", replace: "x")])
            let draft = try await service.process(request, draft: rules)
            XCTAssertEqual(draft.type, "success")
            XCTAssertTrue(draft.matched)
            XCTAssertEqual(draft.rewrittenText?.utf16, [0x78, 0xDFAC])
            XCTAssertEqual(draft.outputText.utf16, [0x78, 0xDFAC])
            XCTAssertEqual(draft.text.utf16, Array("命中第 1 条：x".utf16) + [0xDFAC])
            XCTAssertEqual(draft.inputText.utf16, [0xD83C, 0xDFAC])
            XCTAssertNil(try storage.store.read(key: FilmRuleService.documentKey))
            _ = try await service.save(rules)
            let document = try storage.store.read(key: FilmRuleService.documentKey)
            let saved = try await service.process(request)
            XCTAssertEqual(saved, draft)
            XCTAssertEqual(try JSONDecoder().decode(FilmRuleResult.self, from: JSONEncoder().encode(saved)), saved)
            let unicode = try await service.process(request,
                draft: .init(movieRules: [.init(pattern: "^.", replace: "x", flags: "u")]))
            XCTAssertEqual(unicode.outputText.utf16, [0x78])
            let chinese = try await service.process(.init(section: .movie, text: "中文"), draft: rules)
            XCTAssertEqual(chinese.outputText.utf16, [0x78, 0x6587])
            XCTAssertEqual(try storage.store.read(key: FilmRuleService.documentKey), document)
            await service.close()
        } catch { await service.close(); throw error }
    }

    func testNativeCleaningPreservesUnpairedSurrogatesInPreviewsAndSearchKey() async throws {
        let storage = try FilmRuleTestStore()
        let service = try storage.nativeService()
        do {
            // Search intentionally strips emoji before custom rules; this
            // supplementary CJK character survives that shared preprocessing.
            let search = try await service.process(.init(section: .search, text: "\u{20000}"), draft:
                .init(aggregateRegexRules: ["^."]))
            XCTAssertEqual(search.type, "success")
            XCTAssertEqual(search.processedText.utf16, [0xDC00])
            XCTAssertEqual(search.groupKey.utf16, [0xDC00])
            XCTAssertEqual(search.previews.map(\.utf16), [[0xDC00]])
            XCTAssertEqual(search.outputText.utf16, [0xDC00])
            let episode = try await service.process(.init(section: .episode, text: "\u{20000}"), draft:
                .init(episodeCleanRegexRules: [".$"], episodeRules: [.init(pattern: ".")]))
            XCTAssertEqual(episode.type, "success")
            XCTAssertEqual(episode.processedText.utf16, [0xD840])
            XCTAssertEqual(episode.previews.map(\.utf16), [[0xD840]])
            XCTAssertNil(episode.rewrittenText)
            XCTAssertEqual(episode.outputText.utf16, [0xD840])
            XCTAssertNil(try storage.store.read(key: FilmRuleService.documentKey))
            await service.close()
        } catch { await service.close(); throw error }
    }
}

import Foundation
import XCTest
import MeowRuntime
@testable import MeowNovelFeature

final class NovelBookPresentationTests: XCTestCase {
    func testMetadataKeepsSourceTextThroughPersistenceAndPartialDetails() throws {
        let source = NovelRuleProcessorTests.source()
        let book = try NovelBook.parse(.object([
            "bookUrl": .string("book"), "name": .string("猫"),
            "kind": .string("奇幻 · 连载"), "wordCount": .string("670.9万"),
            "variable": .object(["cursor": .string("page-2")]),
        ]), source: source)
        let restored = try JSONDecoder().decode(NovelBook.self, from: JSONEncoder().encode(book))
        let detailed = try NovelBook.parse(.object(["intro": .string("新的简介")]), source: source, fallback: restored)
        XCTAssertEqual(detailed.kind, "奇幻 · 连载")
        XCTAssertEqual(detailed.wordCount, "670.9万")
        XCTAssertEqual(detailed.wire["kind"], book.wire["kind"])
        XCTAssertEqual(detailed.wire["wordCount"], book.wire["wordCount"])
        XCTAssertEqual(detailed.variable, book.variable)
        XCTAssertEqual(detailed.intro, "新的简介")
    }

    func testAbsentOrNonTextMetadataDoesNotInventDisplayValuesOrChangeWireData() throws {
        let source = NovelRuleProcessorTests.source()
        let absent = NovelBook(installationId: source.id, bookUrl: "book", name: "猫")
        XCTAssertNil(absent.kind); XCTAssertNil(absent.wordCount)
        let book = try NovelBook.parse(.object([
            "bookUrl": .string("book"), "kind": .null, "wordCount": .number(123),
        ]), source: source)
        XCTAssertNil(book.kind); XCTAssertNil(book.wordCount)
        XCTAssertEqual(book.wire["kind"], .null)
        XCTAssertEqual(book.wire["wordCount"], .number(123))
    }
}

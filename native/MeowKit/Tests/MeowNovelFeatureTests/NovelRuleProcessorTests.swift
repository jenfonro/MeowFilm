import Foundation
import XCTest
import MeowRuntime
@testable import MeowNovelFeature

final class NovelRuleProcessorTests: XCTestCase {
    private var rpc: NovelRulesRPCFixture!
    override func setUp() async throws {
        try await super.setUp()
        rpc = try await NovelRulesRPCFixture.start()
    }
    override func tearDown() async throws {
        await rpc?.close(); rpc = nil
        try await super.tearDown()
    }
    func testFrozenCrossPlatformFixturesThroughPublicRulesRPC() async throws {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let file = root.appendingPathComponent("packages/novel-rules/test/fixtures.json")
        let rows = try XCTUnwrap(RuntimeJSON.decode(Data(contentsOf: file)).array)
        XCTAssertEqual(rows.count, 3)
        let processor = rpc.rules
        for row in rows {
            let input = row["input"], expected = row["expected"]
            var source = Self.source()
            source.sourceUrl = try XCTUnwrap(input["source"]["sourceUrl"].string)
            let book = NovelBook(installationId: source.id, bookUrl: "fixture",
                name: input["book"]["name"].string ?? "", author: input["book"]["author"].string ?? "")
            let original = try NovelContent(title: XCTUnwrap(input["title"].string),
                blocks: input["blocks"].decoding([NovelBlock].self))
            let result = try await processor.transform(original, book: book, source: source,
                rules: input["rules"].decoding([NovelRule].self), conversion: input["conversion"].string ?? "none")
            XCTAssertEqual(result.title, expected["title"].string, row["name"].string ?? "")
            XCTAssertEqual(result.blocks, try expected["blocks"].decoding([NovelBlock].self), row["name"].string ?? "")
        }
        await processor.close()
    }
    static func source(_ id: String = "a") -> NovelSource {
        .init(installationId: id, revision: 1, name: id, sourceUrl: "https://fixture.invalid/" + id, group: "", order: 0,
              enabled: true, enabledExplore: true, protocol: "legado", actions: ["search", "bookInfo", "toc", "content", "exploreKinds", "explore", "payAction", "login"])
    }
    func testSharedMergeKeepsUnknownAuthorsSeparateAndAllCandidateLocators() async throws {
        let processor = rpc.rules
        let a = NovelBook(installationId: "a", bookUrl: "a1", name: "猫", author: "林")
        let b = NovelBook(installationId: "b", bookUrl: "b1", name: "猫", author: "林")
        let unknown = NovelBook(installationId: "b", bookUrl: "b2", name: "猫")
        let other = NovelBook(installationId: "a", bookUrl: "a2", name: "猫", author: "周")
        let merged = try await processor.merge([a, unknown, b, other], sources: [Self.source(), Self.source("b")])
        XCTAssertEqual(merged.count, 3)
        XCTAssertEqual(merged.first?.candidates.map(\.bookUrl), ["a1", "b1"])
        XCTAssertEqual(merged.first { $0.book.author.isEmpty }?.candidates.map(\.bookUrl), ["b2"])
        await processor.close()
    }
    func testUniqueChapterNamesMatchAndAmbiguityNeverUsesOldIndex() async throws {
        let processor = rpc.rules
        let chapters: [NovelChapter] = [.init(name: "卷一", chapterUrl: "v", index: 0, isVolume: true),
            .init(name: "第十二章 猫", chapterUrl: "new", index: 1)]
        let anchor = NovelAnchor(chapterUrl: "old", chapterTitle: "第１２章：猫", chapterIndex: 80)
        let match = try await processor.match(chapters, anchor: anchor)
        XCTAssertEqual(match, 1)
        let ambiguous = try await processor.match(chapters + [.init(name: "第12章 猫", chapterUrl: "duplicate", index: 2)], anchor: anchor)
        XCTAssertNil(ambiguous)
        await processor.close()
    }
    func testPurificationOrderScopeCrossParagraphEmptyReplacementAndConversion() async throws {
        let processor = rpc.rules, source = Self.source()
        let book = NovelBook(installationId: source.id, bookUrl: "book", name: "猫", author: "林")
        let original = NovelContent(title: "广告发", blocks: [.init(text: "广告"), .init(text: "发"), .init(src: "https://fixture.invalid/发.png", alt: "发")])
        var first = NovelRule(); first.pattern = "广告\n"; first.replacement = ""; first.isRegex = false; first.order = 1
        var second = NovelRule(); second.pattern = "广告"; second.replacement = ""; second.scopeTitle = true; second.scopeContent = false; second.order = 2
        second.scope = source.sourceUrl
        let transformed = try await processor.transform(original, book: book, source: source, rules: [second, first], conversion: "traditional")
        XCTAssertEqual(transformed.title, "發")
        XCTAssertEqual(transformed.blocks, [.init(text: "發"), original.blocks[2]])
        second.excludeScope = "猫"
        let excluded = try await processor.transform(original, book: book, source: source, rules: [second], conversion: "none")
        XCTAssertEqual(excluded, original)
        var invalid = first; invalid.pattern = "["; invalid.isRegex = true
        do { try await processor.validate(invalid); XCTFail("Invalid regex must be visible") } catch { XCTAssertTrue(error is NovelError) }
        await processor.close()
    }
    func testRealRegexBudgetInterruptsWithoutReturningPartialTransformation() async throws {
        let processor = rpc.rules, source = Self.source()
        let book = NovelBook(installationId: source.id, bookUrl: "book", name: "猫")
        let original = NovelContent(title: "广告", blocks: [.init(text: String(repeating: "a", count: 100_000) + "!")])
        var first = NovelRule(); first.pattern = "广告"; first.scopeTitle = true; first.scopeContent = false; first.order = 0
        var second = NovelRule(); second.pattern = "^(a+)+$"; second.isRegex = true; second.timeoutMillisecond = 30; second.order = 1
        let start = ContinuousClock.now
        do { _ = try await processor.transform(original, book: book, source: source, rules: [first, second], conversion: "none"); XCTFail("Unbounded regex must be interrupted") }
        catch { XCTAssertEqual(error as? NovelError, .ruleExecution("TIMEOUT")) }
        XCTAssertLessThan(start.duration(to: .now), .seconds(2))
        XCTAssertEqual(original.title, "广告")
        let recovered = try await processor.transform(.init(title: "发", blocks: [.init(text: "发")]), book: book, source: source, rules: [], conversion: "traditional")
        XCTAssertEqual(recovered.title, "發")
        await processor.close()
    }
    func testSourceCandidatesKeepSameNameUnknownAuthorsWithoutMergingDifferentBooks() async throws {
        let processor = rpc.rules
        let current = NovelBook(installationId: "a", bookUrl: "a", name: "猫")
        let known = NovelBook(installationId: "b", bookUrl: "b", name: "猫", author: "林")
        let unknown = NovelBook(installationId: "b", bookUrl: "unknown", name: "《猫》")
        let different = NovelBook(installationId: "b", bookUrl: "other", name: "猫", author: "周")
        let unrelated = NovelBook(installationId: "b", bookUrl: "other-name", name: "夜")
        let rows = try await processor.merge([known, unknown, different, unrelated], sources: [Self.source("b")])
        let fromUnknown = try await processor.sourceCandidates(rows, for: current)
        XCTAssertEqual(fromUnknown.map(\.book.bookUrl), ["b", "unknown", "other"])
        let fromKnown = try await processor.sourceCandidates(rows, for: known)
        XCTAssertEqual(fromKnown.map(\.book.bookUrl), ["b", "unknown"])
        XCTAssertEqual(rows.count, 4, "Showing candidates does not change strict identity merging")
        await processor.close()
    }
}

import Foundation
import XCTest
import MeowRuntime
@testable import MeowNovelFeature

final class NovelSourceClientTests: XCTestCase {
    private let source = NovelRuleProcessorTests.source()
    private let book = NovelBook(installationId: "a", bookUrl: "book", name: "猫")
    private let chapter = NovelChapter(name: "第一章", chapterUrl: "chapter", index: 0)

    private func books(_ client: NovelSourceClient, explore: Bool) async throws -> [NovelBook] {
        if explore { return try await client.explore(source, url: "https://fixture.invalid/explore", page: 2) }
        return try await client.search(source, keyword: "猫", page: 2)
    }
    private func content(_ client: NovelSourceClient) async throws -> NovelContent {
        try await client.content(book, source: source, chapter: chapter, nextChapterURL: "next").content
    }

    func testSearchAndExploreKeepHealthySiblingsAndUseOnlyTheirOwnSourceIdentity() async throws {
        for explore in [false, true] {
            let runtime = NovelSourceResultRuntime(.object(["books": .array([
                .object(["bookUrl": .string("first"), "name": .string("猫"), "installationId": .string("other"),
                         "extra": .object(["nested": .array([.null, .bool(true)])])]),
                .null, .string("bad"), .object(["bookUrl": .number(3)]), .object(["name": .string("缺少地址")]),
                .object(["bookUrl": .string("")]),
                .object(["bookUrl": .string("last"), "name": .string("夜")])
            ]), "count": .object(["unused": .bool(true)])]))
            let result = try await books(NovelSourceClient(runtime: runtime), explore: explore)
            XCTAssertEqual(result.map(\.bookUrl), ["first", "last"])
            XCTAssertEqual(result.map(\.installationId), [source.id, source.id])
            XCTAssertEqual(result.map(\.name), ["猫", "夜"])
            let requests = await runtime.requests, request = try XCTUnwrap(requests.first)
            XCTAssertEqual(requests.count, 1)
            XCTAssertEqual(request["action"], .string(explore ? "explore" : "search"))
            XCTAssertEqual(request["installationId"], .string(source.id))
            XCTAssertEqual(request["revision"], .number(Double(source.revision)))
            XCTAssertEqual(request["payload"]["page"], .number(2))
            XCTAssertEqual(request["payload"][explore ? "url" : "keyword"], .string(explore ? "https://fixture.invalid/explore" : "猫"))
        }
    }

    func testEmptyPagesRemainEmptyButAllBadItemsAndMissingListsFail() async throws {
        let invalid: [RuntimeValue] = [
            .null, .object([:]), .object(["books": .object([:])]),
            .object(["books": .array([.null, .object(["name": .string("缺少地址")])])])
        ]
        for explore in [false, true] {
            let empty = try await books(NovelSourceClient(runtime: NovelSourceResultRuntime(.object(["books": .array([])]))), explore: explore)
            XCTAssertTrue(empty.isEmpty)
            for response in invalid {
                do {
                    _ = try await books(NovelSourceClient(runtime: NovelSourceResultRuntime(response)), explore: explore)
                    XCTFail("An unusable page must not become successful exhaustion")
                } catch { XCTAssertEqual(error as? NovelError, .invalidResponse) }
            }
        }
    }

    func testContentReadsOnlyFieldsUsedByEachBlockType() async throws {
        let blocks: [RuntimeValue] = [
            .object(["type": .string("text"), "text": .string("正文"), "src": .object(["unused": .bool(true)]), "alt": .array([.null])]),
            .object(["type": .string("image"), "src": .string("https://fixture.invalid/image"), "alt": .string("插图"),
                     "text": .object(["unused": .bool(true)])])
        ]
        let runtime = NovelSourceResultRuntime(.object(["title": .string("第一章"), "blocks": .array(blocks)]))
        let result = try await content(NovelSourceClient(runtime: runtime))
        XCTAssertEqual(result, .init(title: "第一章", blocks: [.init(text: "正文"), .init(src: "https://fixture.invalid/image", alt: "插图")]))
        let unusedCaption = NovelSourceResultRuntime(.object(["blocks": .array([
            .object(["type": .string("image"), "src": .string("https://fixture.invalid/image"), "alt": .object(["unused": .bool(true)])])
        ])]))
        let image = try await content(NovelSourceClient(runtime: unusedCaption))
        XCTAssertEqual(image.blocks, [.init(src: "https://fixture.invalid/image")])
        let requests = await runtime.requests, payload = try XCTUnwrap(requests.first)["payload"]
        XCTAssertEqual(payload["book"]["bookUrl"], .string(book.bookUrl))
        XCTAssertEqual(payload["chapter"]["chapterUrl"], .string(chapter.chapterUrl))
        XCTAssertEqual(payload["nextChapterUrl"], .string("next"))
    }

    func testMissingExecutionMaterialsAndUnknownBlockTypesRejectTheWholeChapter() async throws {
        let invalid: [RuntimeValue] = [
            .null, .object(["type": .string("text")]), .object(["type": .string("text"), "text": .object([:])]),
            .object(["type": .string("image")]), .object(["type": .string("image"), "src": .string("")]),
            .object(["type": .string("image"), "src": .number(7)]),
            .object(["type": .string("audio"), "text": .string("不能当正文执行")])
        ]
        for block in invalid {
            let runtime = NovelSourceResultRuntime(.object(["blocks": .array([
                .object(["type": .string("text"), "text": .string("前段")]), block,
                .object(["type": .string("text"), "text": .string("后段")])
            ])]))
            do {
                _ = try await content(NovelSourceClient(runtime: runtime))
                XCTFail("A chapter with missing execution material cannot publish partial blocks")
            } catch {
                XCTAssertTrue(error is DecodingError || error as? NovelError == .invalidResponse, "\(error)")
            }
        }
    }
}

private actor NovelSourceResultRuntime: NovelRuntimeServing {
    private let response: RuntimeValue
    private(set) var requests: [RuntimeValue] = []
    init(_ response: RuntimeValue) { self.response = response }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        XCTAssertEqual(operation, "library.invoke")
        requests.append(parameters); return response
    }
    func image(_ reference: String) async throws -> Data { throw NovelError.invalidResponse }
    func hasActiveCalls() -> Bool { false }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {}
    func close() {}
}

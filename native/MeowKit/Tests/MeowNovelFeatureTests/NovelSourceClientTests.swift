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

    private func managedSources(_ client: NovelSourceClient, operation: String) async throws -> [NovelSource] {
        switch operation {
        case "library.list": return try await client.list()
        case "library.get": return [try await client.definition(source).source]
        case "library.import": return try await client.importSources(text: "synthetic definition").sources
        case "library.save": return [try await client.save(source, definition: "synthetic definition")]
        default: throw NovelError.invalidResponse
        }
    }

    // Consumer-contract perturbations; the current Node producer already normalizes these metrics.
    func testSourceManagementTreatsUnusableMetricsAsAbsentAcrossEveryReceipt() async throws {
        let original = try RuntimeJSON.decode(JSONEncoder().encode(source))
        let unused: [(RuntimeValue?, RuntimeValue?)] = [
            (nil, nil), (.null, .null), (.bool(true), .number(1)), (.string("unused"), .string("true")),
            (.object(["unused": .bool(true)]), .object([:])), (.array([]), .array([])),
        ]
        for (metric, flag) in unused {
            guard case .object(var fields) = original else { return XCTFail("Source fixture must be an object") }
            for key in ["weight", "lastUpdateTime", "respondTime"] { fields[key] = metric }
            fields["hasExplore"] = flag
            let runtime = NovelSourceManagementRuntime(.object(fields))
            let client = NovelSourceClient(runtime: runtime)
            for operation in ["library.list", "library.get", "library.import", "library.save"] {
                let result = try await managedSources(client, operation: operation)
                XCTAssertEqual(result, [source])
            }
            let requests = await runtime.requests
            XCTAssertEqual(requests.map(\.0), ["library.list", "library.get", "library.import", "library.save"])
            XCTAssertEqual(requests[1].1["installationId"], .string(source.id))
            XCTAssertEqual(requests[3].1["installationId"], .string(source.id))
            XCTAssertEqual(requests[3].1["expectedRevision"], .number(Double(source.revision)))
            XCTAssertEqual(requests[3].1["definition"], .string("synthetic definition"))
        }
    }

    func testSourceManagementKeepsNumericMetricsAndBooleanFalse() async throws {
        var expected = source
        expected.weight = 1.25; expected.lastUpdateTime = 1234.5; expected.respondTime = 0; expected.hasExplore = false
        let value = try RuntimeJSON.decode(JSONEncoder().encode(expected))
        let client = NovelSourceClient(runtime: NovelSourceManagementRuntime(value))
        for operation in ["library.list", "library.get", "library.import", "library.save"] {
            let result = try await managedSources(client, operation: operation)
            XCTAssertEqual(result, [expected])
        }
    }

    func testSourceManagementStillRequiresTypedIdentityRevisionEnabledAndActions() async throws {
        let original = try RuntimeJSON.decode(JSONEncoder().encode(source))
        let invalid: [(String, RuntimeValue?)] = [
            ("installationId", nil), ("installationId", .number(0)),
            ("revision", nil), ("revision", .string("1")), ("revision", .number(1.5)),
            ("actions", nil), ("actions", .array([.number(1)])), ("actions", .object([:])),
            ("enabled", .string("true")), ("enabledExplore", .number(1)),
        ]
        for (key, value) in invalid {
            guard case .object(var fields) = original else { return XCTFail("Source fixture must be an object") }
            fields[key] = value
            fields["weight"] = .object(["unused": .bool(true)])
            let client = NovelSourceClient(runtime: NovelSourceManagementRuntime(.object(fields)))
            for operation in ["library.list", "library.get", "library.import", "library.save"] {
                do {
                    _ = try await managedSources(client, operation: operation)
                    XCTFail("\(operation) must consume \(key)")
                } catch { XCTAssertTrue(error is DecodingError, "\(operation): \(error)") }
            }
        }
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

private actor NovelSourceManagementRuntime: NovelRuntimeServing {
    private let source: RuntimeValue
    private(set) var requests: [(String, RuntimeValue)] = []
    init(_ source: RuntimeValue) { self.source = source }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        requests.append((operation, parameters))
        switch operation {
        case "library.list": return .object(["sources": .array([source])])
        case "library.get": return .object(["source": source, "definition": .string("synthetic definition")])
        case "library.import": return .object(["sources": .array([source]), "failures": .array([])])
        case "library.save": return .object(["source": source])
        default: throw NovelError.invalidResponse
        }
    }
    func image(_ reference: String) async throws -> Data { throw NovelError.invalidResponse }
    func hasActiveCalls() -> Bool { false }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {}
    func close() {}
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

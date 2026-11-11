import Foundation
import XCTest
import MeowRuntime
@testable import MeowNovelFeature

@MainActor
final class NovelRuleConsumerTests: XCTestCase {
    func testTransformSendsTheWholeSnapshotAndOnlyPublishesTheCompleteResponse() async throws {
        let runtime = NovelRuleRPCGate()
        let processor = NovelRuleProcessor(runtime: runtime)
        let source = NovelRuleProcessorTests.source()
        let book = NovelBook(installationId: source.id, bookUrl: "book", name: "猫")
        let original = NovelContent(title: "原文", blocks: [.init(text: "原文")], imageStyle: "full")
        var first = NovelRule(); first.pattern = "原文"; first.order = 3; first.timeoutMillisecond = 25_000
        var second = NovelRule(); second.pattern = "广告"; second.order = 1; second.isEnabled = false
        let rules = [first, second]
        let work = Task { try await processor.transform(original, book: book, source: source, rules: rules, conversion: "traditional") }
        await runtime.waitForRequest()
        let requests = await runtime.requests
        XCTAssertEqual(requests.count, 1)
        XCTAssertEqual(requests.first?["request"]["operation"], .string("transform"))
        XCTAssertEqual(requests.first?["request"]["rules"], try .encoding(rules), "Filtering, ordering and budgets belong to Node")
        XCTAssertEqual(requests.first?["request"]["conversion"], .string("traditional"))
        XCTAssertEqual(requests.first?["request"]["blocks"], try .encoding(original.blocks))
        await runtime.release(.object(["title": .string("完成"), "blocks": .array([.object(["type": .string("text"), "text": .string("完整正文")])])]))
        let value = try await work.value
        XCTAssertEqual(value.title, "完成"); XCTAssertEqual(value.blocks, [.init(text: "完整正文")])
        XCTAssertEqual(value.imageStyle, "full"); XCTAssertEqual(original.title, "原文")
        await processor.close()
    }

    func testCancelledRPCDiscardsALateMatchWithoutClosingTheRuntime() async throws {
        let runtime = NovelRuleRPCGate()
        let rules = NovelRuleProcessor(runtime: runtime)
        let work = Task { try await rules.matchesURL("https://fixture.invalid/", pattern: ".*") }
        await runtime.waitForRequest()
        work.cancel()
        await runtime.waitForCancellation()
        await runtime.release(.bool(true))
        do { _ = try await work.value; XCTFail("Cancelled match cannot publish") }
        catch { XCTAssertTrue(error is CancellationError) }
        let requests = await runtime.requests
        XCTAssertEqual(requests.first, .object(["request": .object(["operation": .string("matchUrl"),
            "pattern": .string(".*"), "url": .string("https://fixture.invalid/")])]))
        _ = try await runtime.call("library.list", .object([:]))
        let closed = await runtime.closeCount
        XCTAssertEqual(closed, 0)
        await rules.close()
    }

    func testCloseCancelsAndWaitsForAcceptedRPCButNeverClosesTheBorrowedOwner() async throws {
        let runtime = NovelRuleRPCGate()
        let processor = NovelRuleProcessor(runtime: runtime)
        let work = Task { try await processor.matchesURL("https://fixture.invalid/", pattern: ".*") }
        await runtime.waitForRequest()
        var finished = false
        let closing = Task { await processor.close(); finished = true }
        await runtime.waitForCancellation()
        XCTAssertFalse(finished, "A cancellation-ignoring transport is still accepted work")
        do { _ = try await processor.invoke(["operation": .string("identity")]); XCTFail("Admission must stop") }
        catch { XCTAssertEqual(error as? NovelError, .closed) }
        await runtime.release(.bool(true))
        await closing.value
        do { _ = try await work.value; XCTFail("Late closed reply cannot publish") }
        catch { XCTAssertEqual(error as? NovelError, .closed) }
        await processor.close()
        let closed = await runtime.closeCount
        XCTAssertEqual(closed, 0)
        _ = try await runtime.call("library.list", .object([:]))
    }

    func testRPCFailuresRemainExplicitAndCannotReturnPartialContent() async throws {
        for (failure, expected) in [(RuntimeHTTPError.remote("TIMEOUT"), NovelError.ruleExecution("TIMEOUT")),
                                     (.remote("SOURCE_FAILURE"), .ruleExecution("SOURCE_FAILURE")),
                                     (.invalidResponse, .invalidResponse), (.remote("CLOSED"), .closed)] {
            let runtime = NovelRuleRPCGate()
            let processor = NovelRuleProcessor(runtime: runtime)
            let work = Task { try await processor.matchesURL("https://fixture.invalid/", pattern: ".*") }
            await runtime.waitForRequest(); await runtime.fail(failure)
            do { _ = try await work.value; XCTFail("RPC failure cannot become a successful value") }
            catch { XCTAssertEqual(error as? NovelError, expected) }
            await processor.close()
        }
    }
}

/// Holds the physical request even after cancellation, so tests can distinguish
/// a cancellation signal from a completed drain.
private actor NovelRuleRPCGate: NovelRuntimeServing {
    private(set) var requests: [RuntimeValue] = []
    private(set) var closeCount = 0
    private var response: CheckedContinuation<RuntimeValue, Error>?
    private var entered: CheckedContinuation<Void, Never>?
    private var cancellation: CheckedContinuation<Void, Never>?
    private var cancelled = false
    func waitForRequest() async {
        if response != nil { return }
        await withCheckedContinuation { entered = $0 }
    }
    func waitForCancellation() async {
        if cancelled { return }
        await withCheckedContinuation { cancellation = $0 }
    }
    private func markCancelled() { cancelled = true; cancellation?.resume(); cancellation = nil }
    func release(_ value: RuntimeValue) { response?.resume(returning: value); response = nil }
    func fail(_ error: Error) { response?.resume(throwing: error); response = nil }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        if operation == "library.list" { return .object(["sources": .array([])]) }
        XCTAssertEqual(operation, "rules.process")
        requests.append(parameters)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation {
                response = $0; entered?.resume(); entered = nil
            }
        } onCancel: { Task { await self.markCancelled() } }
    }
    func hasActiveCalls() -> Bool { false }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {}
    func image(_ reference: String) async throws -> Data { throw NovelError.invalidResponse }
    func close() { closeCount += 1 }
}

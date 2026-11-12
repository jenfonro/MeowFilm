import Foundation
import XCTest
import MeowRuntime
@testable import MeowNovelFeature

@MainActor
final class NovelWebBrokerTests: XCTestCase {
    private func wait(_ condition: @escaping @MainActor () -> Bool) async {
        let done = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { condition() } }, evaluatedWith: nil)
        await fulfillment(of: [done], timeout: 3)
        XCTAssertTrue(condition())
    }
    func testIdleDoesNotPollAndCompletedScalarUsesTheCookieEnvelopeUnchanged() async throws {
        let responded = expectation(description: "response reached runtime")
        let runtime = NovelWebRuntime(onResponse: { responded.fulfill() }), broker = NovelWebBroker(runtime: runtime)
        let result: RuntimeValue = .object(["value": .string("1234"), "url": .string("https://fixture.invalid/code"),
            "headers": .object(["set-cookie": .array([.string("fixture=private; HttpOnly; Path=/")])])])
        var presented: [NovelWebRequest] = []
        broker.present = { request in presented.append(request); return result }
        await broker.start()
        let idlePolls = await runtime.polls
        XCTAssertEqual(idlePolls, 0)
        await runtime.activate()
        await wait { presented.count == 1 }
        await fulfillment(of: [responded], timeout: 3)
        await broker.close()
        let receipts = await runtime.responses
        XCTAssertEqual(receipts.first?["result"], result)
        XCTAssertEqual(presented.first?.installationId, "a")
    }
    func testRuntimeWithdrawAndUserCancellationNeverReturnCookieOrLateValue() async throws {
        for withdraw in [true, false] {
            let runtime = NovelWebRuntime(), broker = NovelWebBroker(runtime: runtime)
            var entered = false, cancelled = false
            broker.present = { _ in
                entered = true
                do { try await Task.sleep(nanoseconds: 10_000_000_000) }
                catch { cancelled = true; throw error }
                return .string("must never be returned")
            }
            await broker.start(); await runtime.activate()
            await wait { entered }
            if withdraw { await runtime.withdraw() } else { broker.suspend() }
            await wait { cancelled }
            await broker.close()
            let receipts = await runtime.responses
            XCTAssertTrue(receipts.allSatisfy { $0["cancelled"] == .bool(true) && $0["result"] == .null })
            XCTAssertEqual(receipts.count, 1)
        }
    }
    func testRetirementAndCancellationDuringRulesRPCDrainWithoutReturningCookies() async throws {
        for withdraw in [true, false] {
            let runtime = NovelWebRuntime()
            let owner = NovelWebBroker(runtime: runtime), rules = NovelRuleProcessor(runtime: runtime)
            owner.present = { _ in
                _ = try await rules.matchesURL("https://fixture.invalid/code", pattern: ".*")
                return .object(["value": .bool(true), "url": .string("https://fixture.invalid/code"),
                    "cookieChanges": .array([.object(["url": .string("https://fixture.invalid/code"),
                        "setCookie": .string("fixture=late; Path=/")])])])
            }
            await owner.start(); await runtime.activate(); await runtime.waitForRule()
            if withdraw { await runtime.withdraw() } else { owner.suspend() }
            await runtime.waitForRuleCancellation()
            var closed = false
            let closing = Task { await owner.close(); closed = true }
            let before = await runtime.responses
            XCTAssertTrue(before.isEmpty); XCTAssertFalse(closed)
            await runtime.releaseRule()
            await closing.value; await rules.close()
            let receipts = await runtime.responses
            XCTAssertEqual(receipts.count, 1)
            XCTAssertTrue(receipts.allSatisfy { $0["cancelled"] == .bool(true) && $0["result"] == .null })
        }
    }
}
private actor NovelWebRuntime: NovelRuntimeServing {
    var polls = 0
    var responses: [RuntimeValue] = []
    private var active = false, valid = false
    private var handler: (@Sendable (Bool) -> Void)?
    private var rule: CheckedContinuation<RuntimeValue, Never>?
    private var ruleEntered: CheckedContinuation<Void, Never>?
    private var ruleCancellation: CheckedContinuation<Void, Never>?
    private var ruleCancelled = false
    private let onResponse: @Sendable () -> Void
    init(onResponse: @escaping @Sendable () -> Void = {}) { self.onResponse = onResponse }
    func hasActiveCalls() -> Bool { active }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) { self.handler = handler; handler?(active) }
    func activate() { active = true; valid = true; handler?(true) }
    func withdraw() { valid = false }
    func waitForRule() async { if rule != nil { return }; await withCheckedContinuation { ruleEntered = $0 } }
    func waitForRuleCancellation() async {
        if ruleCancelled { return }; await withCheckedContinuation { ruleCancellation = $0 }
    }
    func releaseRule() { rule?.resume(returning: .bool(true)); rule = nil }
    private func cancelRule() { ruleCancelled = true; ruleCancellation?.resume(); ruleCancellation = nil }
    func image(_ reference: String) async throws -> Data { throw NovelError.invalidResponse }
    func close() { active = false; handler?(false) }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        if operation == "rules.process" {
            return await withTaskCancellationHandler {
                await withCheckedContinuation { rule = $0; ruleEntered?.resume(); ruleEntered = nil }
            } onCancel: { Task { await self.cancelRule() } }
        }
        if operation == "web.pending" {
            polls += 1
            return .object(["requests": .array(valid ? [.object(["id": .string("request"), "installationId": .string("a"),
                "mode": .string("verificationCode"), "request": .object(["url": .string("https://fixture.invalid/code")])])] : [])])
        }
        if operation == "web.respond" {
            responses.append(parameters); valid = false; active = false; handler?(false)
            onResponse()
            return .object(["accepted": .bool(true)])
        }
        throw NovelError.invalidResponse
    }
}

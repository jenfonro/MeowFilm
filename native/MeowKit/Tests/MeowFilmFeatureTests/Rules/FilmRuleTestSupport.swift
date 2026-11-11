import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

func filmRuleValue<T: Encodable>(_ value: T) throws -> RuntimeValue {
    try RuntimeJSON.decode(JSONEncoder().encode(value))
}

func filmRuleFailure<E: Error & Equatable, T>(
    _ expected: E, file: StaticString = #filePath, line: UInt = #line,
    _ operation: () async throws -> T
) async {
    do { _ = try await operation(); XCTFail("Expected \(expected)", file: file, line: line) }
    catch { XCTAssertEqual(error as? E, expected, file: file, line: line) }
}

func filmRuleCancelled<T>(file: StaticString = #filePath, line: UInt = #line,
                          _ operation: () async throws -> T) async {
    do { _ = try await operation(); XCTFail("Expected cancellation", file: file, line: line) }
    catch { XCTAssertTrue(error is CancellationError, "\(error)", file: file, line: line) }
}

actor FilmRuleGate {
    private var released = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        if released { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func release() {
        released = true
        let pending = waiters; waiters.removeAll()
        for waiter in pending { waiter.resume() }
    }
}

/// RPC fixture only. Rule computation is verified on the real Node host/Mobile.
actor FilmRuleRPC {
    var rules = FilmRuleSet(movieRules: [.init(pattern: "saved")])
    var revision = 0
    var failure: RuntimeHTTPError?
    var response: RuntimeValue?
    var calls: [(String, RuntimeValue)] = []
    func setFailure(_ value: RuntimeHTTPError?) { failure = value }
    func setResponse(_ value: RuntimeValue?) { response = value }
    func call(_ operation: String, _ input: RuntimeValue) throws -> RuntimeValue {
        calls.append((operation, input))
        if let failure { throw failure }
        if let response { return response }
        if operation == "rules.normalize" { return .null }
        if let value = input.filmObject?["rules"], operation == "rules.set" {
            rules = try JSONDecoder().decode(FilmRuleSet.self, from: RuntimeJSON.encode(value))
            revision += 1
        }
        if let fields = input.filmObject, case .string(let name)? = fields["group"],
           let group = FilmRuleGroup(rawValue: name), let value = fields["rules"], operation == "rules.group.set" {
            let rows: [FilmMatchRule]
            if group.isClean {
                rows = try JSONDecoder().decode([String].self, from: RuntimeJSON.encode(value)).map { .init(pattern: $0) }
            } else {
                rows = try JSONDecoder().decode([FilmMatchRule].self, from: RuntimeJSON.encode(value))
            }
            rules.replace(group, with: rows); revision += 1
        }
        if operation == "rules.clear" { rules = .init(); revision += 1 }
        return .object(["rules": try filmRuleValue(rules), "revision": .string("receipt-\(revision)")])
    }
    nonisolated var service: FilmRuleService { .init(request: { try await self.call($0, $1) }) }
}

actor FilmRuleControlledRPC {
    let entered: XCTestExpectation, cancelled: XCTestExpectation
    let release = FilmRuleGate()
    private(set) var invocations = 0
    var shouldBlock = true, returnAfterCancellation = false
    var response: RuntimeValue = .null
    init(entered: XCTestExpectation, cancelled: XCTestExpectation) {
        self.entered = entered; self.cancelled = cancelled
    }
    func call(_ operation: String, _ input: RuntimeValue) async throws -> RuntimeValue {
        invocations += 1
        if shouldBlock {
            shouldBlock = false; entered.fulfill()
            do { try await Task.sleep(nanoseconds: 60_000_000_000) }
            catch {
                cancelled.fulfill(); await release.wait()
                if returnAfterCancellation { return response }
                throw CancellationError()
            }
        }
        return response
    }
    func setResponse(_ value: RuntimeValue) { response = value }
    func ignoreCancellation() { returnAfterCancellation = true }
    nonisolated var service: FilmRuleService { .init(request: { try await self.call($0, $1) }) }
}

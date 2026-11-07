import Foundation
import XCTest
import MeowJavaScript
import MeowStorage
@testable import MeowFilmFeature

final class FilmRuleTestStore {
    let root: URL
    let store: DocumentStore

    init(maximumDocumentBytes: Int = 8 * 1_024 * 1_024) throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        store = try DocumentStore(baseURL: root, namespace: FilmRuleService.documentNamespace,
                                  maximumDocumentBytes: maximumDocumentBytes)
    }

    deinit { try? FileManager.default.removeItem(at: root) }

    func nativeService() throws -> FilmRuleService {
        try FilmRuleService(store: store)
    }

    static var package: URL {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<6 { root.deleteLastPathComponent() }
        return root.appendingPathComponent("packages/film-rules")
    }
}

func filmRuleFailure<E: Error & Equatable, T>(
    _ expected: E, file: StaticString = #filePath, line: UInt = #line,
    _ operation: () async throws -> T
) async {
    do {
        _ = try await operation()
        XCTFail("Expected \(expected)", file: file, line: line)
    } catch {
        XCTAssertEqual(error as? E, expected, file: file, line: line)
    }
}

func filmRuleCancelled<T>(file: StaticString = #filePath, line: UInt = #line,
                          _ operation: () async throws -> T) async {
    do {
        _ = try await operation()
        XCTFail("Expected cancellation", file: file, line: line)
    } catch {
        XCTAssertTrue(error is CancellationError, "\(error)", file: file, line: line)
    }
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
        let pending = waiters
        waiters.removeAll()
        for waiter in pending { waiter.resume() }
    }
}

/// Only lifecycle/error tests use this double. Semantic tests use native QuickJS.
actor FilmRuleControlledEngine: JavaScriptJSONEngine {
    let entered: XCTestExpectation
    let cancelled: XCTestExpectation
    let release = FilmRuleGate()
    private(set) var invocations = 0
    private(set) var closes = 0
    var shouldBlock = true
    var returnAfterCancellation = false
    var response = Data("null".utf8)

    init(entered: XCTestExpectation, cancelled: XCTestExpectation) {
        self.entered = entered
        self.cancelled = cancelled
    }

    func invoke(_ request: Data) async throws -> Data {
        invocations += 1
        if shouldBlock {
            shouldBlock = false
            entered.fulfill()
            do {
                try await Task.sleep(nanoseconds: 60_000_000_000)
                throw FilmRuleServiceError.invalidEngineResponse
            } catch {
                cancelled.fulfill()
                await release.wait()
                if returnAfterCancellation { return response }
                throw CancellationError()
            }
        }
        return response
    }

    func setResponse(_ bytes: Data) { response = bytes }
    func disableBlocking() { shouldBlock = false }
    func ignoreCancellation() { returnAfterCancellation = true }
    func close() async { closes += 1 }
}

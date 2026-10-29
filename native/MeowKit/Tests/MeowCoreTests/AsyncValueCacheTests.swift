import XCTest
import MeowContracts

final class AsyncValueCacheTests: XCTestCase {
    private func waitFor(_ predicate: () async -> Bool) async throws {
        for _ in 0..<3000 {
            if await predicate() { return }
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        XCTFail("Cache operation did not reach its expected state")
        throw RequestAdmission.Failure.timedOut
    }
    func testSameKeySingleFlightIndependentCancellationAndCacheHit() async throws {
        let cache = AsyncValueCache<String, Int>(capacity: 2), read = CacheTestRead()
        let events = CacheTestEvents()
        let first = Task { try await cache.value(for: "same", lifetime: 30, observe: events.append) { await read.load() } }
        try await waitFor { await read.count == 1 }
        let second = Task { try await cache.value(for: "same", lifetime: 30, observe: events.append) { await read.load() } }
        try await waitFor { events.values.contains(.coalesced) }
        first.cancel()
        do { _ = try await first.value; XCTFail("Cancelled first waiter completed") }
        catch { XCTAssertTrue(error is CancellationError) }
        await read.finish(0, value: 7)
        let value = try await second.value
        XCTAssertEqual(value, 7)
        let hit = try await cache.value(for: "same", lifetime: 30, observe: events.append) { -1 }
        XCTAssertEqual(hit, 7); XCTAssertEqual(events.values, [.miss, .coalesced, .hit])
        let count = await read.count; XCTAssertEqual(count, 1)
    }
    func testLastCancellationAndInvalidationRejectLateCallbacksWithoutRefilling() async throws {
        for invalidate in [false, true] {
            let cache = AsyncValueCache<String, Int>(capacity: 1), read = CacheTestRead()
            let first = Task { try await cache.value(for: "key", lifetime: 30) { await read.load() } }
            try await waitFor { await read.count == 1 }
            if invalidate { await cache.invalidate("key") } else { first.cancel() }
            do { _ = try await first.value; XCTFail("Revoked result returned") }
            catch { XCTAssertTrue(error is CancellationError) }
            let second = Task { try await cache.value(for: "key", lifetime: 30) { await read.load() } }
            try await waitFor { await read.count == 2 }
            // The loader intentionally ignores cancellation to exercise old IO.
            await read.finish(0, value: 3)
            await read.finish(1, value: 9)
            let accepted = try await second.value
            XCTAssertEqual(accepted, 9)
            let cached = try await cache.value(for: "key", lifetime: 30) { -1 }
            XCTAssertEqual(cached, 9)
        }
    }
    func testCostTTLFailureAndFlightBounds() async throws {
        let clock = CacheTestClock()
        let cache = AsyncValueCache<String, Int>(capacity: 3, maximumCost: 6, maximumFlights: 1,
            cost: { $0 }, clock: { clock.now })
        _ = try await cache.value(for: "a", lifetime: 10) { 4 }
        _ = try await cache.value(for: "b", lifetime: 10) { 4 }
        let evicted = try await cache.value(for: "a", lifetime: 10) { 2 }
        XCTAssertEqual(evicted, 2)
        clock.advance(10)
        let expired = try await cache.value(for: "a", lifetime: 10) { 1 }
        XCTAssertEqual(expired, 1)
        do {
            _ = try await cache.value(for: "fail", lifetime: 10) { throw RequestAdmission.Failure.timedOut }
            XCTFail("Failure cached as a value")
        } catch { XCTAssertEqual(error as? RequestAdmission.Failure, .timedOut) }
        let retry = try await cache.value(for: "fail", lifetime: 10) { 2 }
        XCTAssertEqual(retry, 2)
        _ = try await cache.value(for: "oversize", lifetime: 10) { 7 }
        let notCached = try await cache.value(for: "oversize", lifetime: 10) { 0 }
        XCTAssertEqual(notCached, 0)
        let read = CacheTestRead()
        let held = Task { try await cache.value(for: "held", lifetime: 10) { await read.load() } }
        try await waitFor { await read.count == 1 }
        do {
            _ = try await cache.value(for: "other", lifetime: 10) { 1 }
            XCTFail("Distinct flight budget exceeded")
        } catch { XCTAssertEqual(error as? RequestAdmission.Failure, .queueFull) }
        await read.finish(0, value: 1)
        _ = try await held.value
        let after = try await cache.value(for: "other", lifetime: 10) { 2 }
        XCTAssertEqual(after, 2)
    }
    func testCancelledLoaderThatIgnoresCancellationRemainsChargedUntilItReturns() async throws {
        let cache = AsyncValueCache<String, Int>(capacity: 2, maximumFlights: 1)
        let read = CacheTestRead()
        let first = Task { try await cache.value(for: "first", lifetime: 30) { await read.load() } }
        try await waitFor { await read.count == 1 }
        first.cancel()
        do { _ = try await first.value; XCTFail("cancelled waiter returned") }
        catch { XCTAssertTrue(error is CancellationError) }

        do {
            _ = try await cache.value(for: "second", lifetime: 30) { 2 }
            XCTFail("cancelled physical loader released the concurrency budget early")
        } catch { XCTAssertEqual(error as? RequestAdmission.Failure, .queueFull) }

        await read.finish(0, value: 1)
        try await waitFor {
            do { return try await cache.value(for: "second", lifetime: 30) { 2 } == 2 }
            catch { return false }
        }
    }
}

private actor CacheTestRead {
    private var continuations: [Int: CheckedContinuation<Int, Never>] = [:]
    private(set) var count = 0
    func load() async -> Int {
        let id = count; count += 1
        return await withCheckedContinuation { continuations[id] = $0 }
    }
    func finish(_ id: Int, value: Int) { continuations.removeValue(forKey: id)?.resume(returning: value) }
}
private final class CacheTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date = Date(timeIntervalSince1970: 1000)
    var now: Date { lock.lock(); defer { lock.unlock() }; return date }
    func advance(_ seconds: TimeInterval) { lock.lock(); defer { lock.unlock() }; date.addTimeInterval(seconds) }
}
private final class CacheTestEvents: @unchecked Sendable {
    typealias Event = AsyncValueCache<String, Int>.Access
    private let lock = NSLock()
    private var stored: [Event] = []
    var values: [Event] { lock.lock(); defer { lock.unlock() }; return stored }
    func append(_ event: Event) { lock.lock(); defer { lock.unlock() }; stored.append(event) }
}

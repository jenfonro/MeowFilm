import XCTest
import MeowContracts

final class RequestAdmissionTests: XCTestCase {
    private func waitFor(_ admission: RequestAdmission, waiting: Int) async throws {
        for _ in 0..<3000 {
            if await admission.counts.waiting == waiting { return }
            try await Task.sleep(nanoseconds: 1_000_000)
        }
        XCTFail("Admission did not reach the expected bounded queue state")
        throw RequestAdmission.Failure.timedOut
    }
    private func failure(_ task: Task<UUID, Error>, _ expected: RequestAdmission.Failure) async {
        do { _ = try await task.value; XCTFail("Unexpected lease") }
        catch { XCTAssertEqual(error as? RequestAdmission.Failure, expected) }
    }
    func testHostSaturationDoesNotBlockAnotherHostAndCancellationKeepsPhysicalLease() async throws {
        let admission = RequestAdmission(maximum: 2, perHost: 1)
        let first = try await admission.acquire(host: "a")
        let waiting = Task { try await admission.acquire(host: "a") }
        try await waitFor(admission, waiting: 1)
        let other = try await admission.acquire(host: "b")
        var counts = await admission.counts
        XCTAssertEqual(counts.active, 2); XCTAssertEqual(counts.waiting, 1)
        waiting.cancel()
        do { _ = try await waiting.value; XCTFail("Cancelled waiter received a lease") }
        catch { XCTAssertTrue(error is CancellationError) }
        counts = await admission.counts
        XCTAssertEqual(counts.active, 2); XCTAssertEqual(counts.waiting, 0)
        await admission.release(first); await admission.release(other)
        counts = await admission.counts; XCTAssertEqual(counts.active, 0)
    }
    func testQueueBoundTimeoutAndScopedCloseNeverReleaseOthersPhysicalWork() async throws {
        let admission = RequestAdmission(maximum: 1, waitingLimit: 1, waitSeconds: 10)
        let scope = UUID(), first = try await admission.acquire(host: "a")
        let waiting = Task { try await admission.acquire(host: "a", scope: scope) }
        try await waitFor(admission, waiting: 1)
        await failure(Task { try await admission.acquire(host: "b") }, .queueFull)
        await admission.close(scope: scope)
        await failure(waiting, .closed)
        await failure(Task { try await admission.acquire(host: "a", scope: scope) }, .closed)
        let next = Task { try await admission.acquire(host: "b") }
        try await waitFor(admission, waiting: 1)
        let counts = await admission.counts
        XCTAssertEqual(counts.active, 1)
        await admission.release(first)
        let lease = try await next.value
        await admission.close()
        await admission.release(lease)
        await failure(Task { try await admission.acquire(host: "b") }, .closed)

        let timed = RequestAdmission(maximum: 1, waitSeconds: 0.01)
        let held = try await timed.acquire(host: "a")
        await failure(Task { try await timed.acquire(host: "b") }, .timedOut)
        await timed.release(held)
        let after = await timed.counts
        XCTAssertEqual(after.active, 0); XCTAssertEqual(after.waiting, 0)
    }
    func testForegroundBurstIsBoundedAndOrdinaryReadCannotStarve() async throws {
        let admission = RequestAdmission(maximum: 1)
        let held = try await admission.acquire(host: "a")
        let order = AdmissionTestValues<Int>()
        var tasks: [Task<Void, Error>] = []
        for index in 0..<5 {
            tasks.append(Task {
                let lease = try await admission.acquire(host: "a", priority: index == 0 ? .normal : .foreground)
                order.append(index)
                await admission.release(lease)
            })
            try await waitFor(admission, waiting: index + 1)
        }
        await admission.release(held)
        for task in tasks { try await task.value }
        XCTAssertEqual(order.values, [1, 2, 3, 0, 4])
    }
    func testEvidenceBasedCooldownExpiresWithoutRetryingAnyOperation() async throws {
        let clock = AdmissionTestClock()
        let admission = RequestAdmission(maximum: 1, clock: { clock.now })
        let held = try await admission.acquire(host: "limited")
        let waiting = Task { try await admission.acquire(host: "limited") }
        try await waitFor(admission, waiting: 1)
        await admission.coolDown(host: "limited", seconds: 5)
        await failure(waiting, .coolingDown)
        await admission.release(held)
        await failure(Task { try await admission.acquire(host: "limited") }, .coolingDown)
        let other = try await admission.acquire(host: "other")
        await admission.release(other)
        clock.advance(5)
        let resumed = try await admission.acquire(host: "limited")
        await admission.release(resumed)
        let counts = await admission.counts
        XCTAssertEqual(counts.active, 0); XCTAssertEqual(counts.waiting, 0)
    }
    func testCancellationAtLeaseDeliveryDoesNotLeakCapacity() async throws {
        for _ in 0..<40 {
            let admission = RequestAdmission(maximum: 1)
            let held = try await admission.acquire(host: "a")
            let waiting = Task { try await admission.acquire(host: "a") }
            try await waitFor(admission, waiting: 1)
            await admission.release(held)
            waiting.cancel()
            if let lease = try? await waiting.value { await admission.release(lease) }
            let counts = await admission.counts
            XCTAssertEqual(counts.active, 0); XCTAssertEqual(counts.waiting, 0)
        }
    }
    func testRetryAfterOnlyAcceptsBoundedSecondsOrHTTPDate() {
        let date = Date(timeIntervalSince1970: 1_445_412_480)
        XCTAssertEqual(RequestAdmission.retryAfter("12"), 12)
        XCTAssertEqual(RequestAdmission.retryAfter("999999"), 300)
        XCTAssertEqual(RequestAdmission.retryAfter("0"), 0)
        XCTAssertEqual(RequestAdmission.retryAfter("Wed, 21 Oct 2015 07:28:10 GMT", now: date), 10)
        for value in ["", "-1", "1.5", "nan", "tomorrow", String(repeating: "1", count: 129)] {
            XCTAssertNil(RequestAdmission.retryAfter(value))
        }
    }
}

private final class AdmissionTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date = Date(timeIntervalSince1970: 1000)
    var now: Date { lock.lock(); defer { lock.unlock() }; return date }
    func advance(_ seconds: TimeInterval) { lock.lock(); defer { lock.unlock() }; date.addTimeInterval(seconds) }
}
private final class AdmissionTestValues<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [Value] = []
    var values: [Value] { lock.lock(); defer { lock.unlock() }; return stored }
    func append(_ value: Value) { lock.lock(); defer { lock.unlock() }; stored.append(value) }
}

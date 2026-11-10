import Foundation
import XCTest
import MeowRuntime

actor RuntimeSessionGate {
    private var entered = false
    private var released: Bool
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var waits: [CheckedContinuation<Void, Never>] = []
    init(released: Bool = false) { self.released = released }
    func wait() async {
        entered = true
        starts.forEach { $0.resume() }; starts.removeAll()
        if !released { await withCheckedContinuation { waits.append($0) } }
    }
    func started() async {
        if !entered { await withCheckedContinuation { starts.append($0) } }
    }
    func release() {
        released = true
        waits.forEach { $0.resume() }; waits.removeAll()
    }
}

private actor RuntimeSessionLauncher: RuntimeLaunching {
    let gate = RuntimeSessionGate()
    let exit: RuntimeSessionGate
    let failure: RuntimeLaunchError?
    var calls = 0
    var closes = 0
    var client: RuntimeHTTPClient?
    init(failure: RuntimeLaunchError? = nil, holdExit: Bool = false) {
        self.failure = failure
        exit = RuntimeSessionGate(released: !holdExit)
    }
    func connect() async throws -> RuntimeHTTPClient {
        calls += 1
        // A late executor still belongs to the shared App owner.
        await gate.wait()
        if let failure { throw failure }
        let value = try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!)
        client = value
        return value
    }
    func close() async {
        closes += 1
        await client?.close()
        await exit.wait()
    }
    func waitForExit() async { await exit.wait() }
}

final class RuntimeSessionTests: XCTestCase {
    func testConcurrentBorrowersShareOneLaunchWithoutAConfigurationStep() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeSession(launcher: launcher)
        let first = Task { try await session.connection() }
        await launcher.gate.started()
        let second = Task { try await session.connection() }
        await launcher.gate.release()
        let a = try await first.value, b = try await second.value
        XCTAssertTrue(a === b)
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
        await session.close()
        XCTAssertTrue(a.isClosed)
    }

    func testCancellingABorrowerFinishesWhileSharedStartupIsStillHeld() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeSession(launcher: launcher)
        let cancelled = Task { try await session.connection() }
        await launcher.gate.started()
        let other = Task { try await session.connection() }
        let cancelledDone = expectation(description: "borrower cancels before shared startup finishes")
        cancelled.cancel()
        let checked = Task {
            do { _ = try await cancelled.value; XCTFail("cancelled borrower") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .cancelled) }
            cancelledDone.fulfill()
        }
        await fulfillment(of: [cancelledDone], timeout: 2)
        await launcher.gate.release()
        await checked.value
        let client = try await other.value
        XCTAssertFalse(client.isClosed)
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
        await session.close()
    }

    func testLaunchFailureIsRetainedWithoutRetry() async throws {
        let launcher = RuntimeSessionLauncher(failure: .payloadUnavailable), session = RuntimeSession(launcher: launcher)
        let first = Task { try await session.connection() }
        await launcher.gate.started()
        let second = Task { try await session.connection() }
        await launcher.gate.release()
        for task in [first, second] {
            do { _ = try await task.value; XCTFail("failure") }
            catch { XCTAssertEqual(error as? RuntimeLaunchError, .payloadUnavailable) }
        }
        do { _ = try await session.connection(); XCTFail("retained failure") }
        catch { XCTAssertEqual(error as? RuntimeLaunchError, .payloadUnavailable) }
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
        await session.close()
    }

    func testCloseDisposesALateLaunchAndAllOwnersWaitForIt() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeSession(launcher: launcher)
        let pending = Task { try await session.connection() }
        await launcher.gate.started()
        let closing = Task { await session.close() }
        await launcher.exit.started()
        do { _ = try await pending.value; XCTFail("closed") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        closing.cancel()
        let anotherClose = Task { await session.close() }
        await launcher.gate.release()
        await closing.value; await anotherClose.value
        let client = await launcher.client
        XCTAssertTrue(try XCTUnwrap(client).isClosed)
        let calls = await launcher.calls, closes = await launcher.closes
        XCTAssertEqual(calls, 1); XCTAssertEqual(closes, 1)
        do { _ = try await session.connection(); XCTFail("terminal") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
    }

    func testCloseWaitsForPhysicalExecutorExitAndAClosedClientIsNeverBorrowedAgain() async throws {
        let launcher = RuntimeSessionLauncher(holdExit: true), session = RuntimeSession(launcher: launcher)
        let pending = Task { try await session.connection() }
        await launcher.gate.started(); await launcher.gate.release()
        let client = try await pending.value
        let completion = RuntimeLaunchRecord()
        let closing = Task { await session.close(); completion.set(["finished"]) }
        await launcher.exit.started()
        XCTAssertTrue(client.isClosed)
        XCTAssertNil(completion.get())
        await launcher.exit.release()
        await closing.value
        XCTAssertEqual(completion.get(), ["finished"])
        do { _ = try await session.connection(); XCTFail("closed") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
    }

    func testClosingBeforeAnyBorrowerDoesNotLaunchNode() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeSession(launcher: launcher)
        await session.close()
        let calls = await launcher.calls
        XCTAssertEqual(calls, 0)
        do { _ = try await session.connection(); XCTFail("closed") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
    }

    func testExecutorFailureMakesTheBorrowedClientTerminalWithoutRestart() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeSession(launcher: launcher)
        let pending = Task { try await session.connection() }
        await launcher.gate.started(); await launcher.gate.release()
        let client = try await pending.value
        await client.close()
        do { _ = try await session.connection(); XCTFail("closed client") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
        await session.close()
    }
}

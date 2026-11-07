import Foundation
import XCTest
import MeowJSON
@testable import MeowRuntime

private actor RuntimeSessionGate {
    private var entered = false
    private var released = false
    private var starts: [CheckedContinuation<Void, Never>] = []
    private var waits: [CheckedContinuation<Void, Never>] = []
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

private actor RuntimeSessionLauncher: RuntimeBridgeLaunching {
    let gate = RuntimeSessionGate()
    let failure: RuntimeLaunchError?
    var calls = 0
    var peer: RuntimeBridgeRawPeer?
    var native: RuntimeBridgeConnection?
    init(failure: RuntimeLaunchError? = nil) { self.failure = failure }
    func connect(handlers: [String: RuntimeBridgeConnection.Handler]) async throws -> RuntimeBridgeConnection {
        calls += 1
        // Deliberately ignores cancellation to exercise late launch disposal.
        await gate.wait()
        if let failure { throw failure }
        let (a, b) = try runtimeBridgePair()
        let connection = try RuntimeBridgeConnection(descriptor: a, session: "session-test", handlers: handlers)
        native = connection
        peer = RuntimeBridgeRawPeer(descriptor: b, session: "session-test")
        return connection
    }
    func closePeer() { peer?.close(); peer = nil }
    // This protocol-only fixture owns no executor.
    func waitForExit() {}
}

final class RuntimeBridgeSessionTests: XCTestCase {
    func testConfigurationIsRequiredAndFrozenBeforeLaunch() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeBridgeSession(launcher: launcher)
        do { _ = try await session.connection(); XCTFail("unconfigured") }
        catch { XCTAssertEqual(error as? RuntimeBridgeSessionError, .notConfigured) }
        do { try await session.configure(handlers: ["9invalid": { $0 }]); XCTFail("invalid method") }
        catch { XCTAssertEqual(error as? RuntimeBridgeError, .invalidMessage) }
        try await session.configure(handlers: [:])
        do { try await session.configure(handlers: ["platform.extra": { $0 }]); XCTFail("new permissions") }
        catch { XCTAssertEqual(error as? RuntimeBridgeSessionError, .alreadyConfigured) }
        await session.close()
        do { _ = try await session.connection(); XCTFail("closed") }
        catch { XCTAssertEqual(error as? RuntimeBridgeError, .closed) }
        let calls = await launcher.calls
        XCTAssertEqual(calls, 0)
    }

    func testConcurrentBorrowersShareOneLaunchAndCompleteReverseTable() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeBridgeSession(launcher: launcher)
        try await session.configure(handlers: [
            "platform.music": { $0 }, "platform.other": { _ in .string("other") },
        ])
        let first = Task { try await session.connection() }
        await launcher.gate.started()
        let second = Task { try await session.connection() }
        await launcher.gate.release()
        let a = try await first.value, b = try await second.value
        XCTAssertTrue(a === b)
        let remote = await launcher.peer
        let peer = try XCTUnwrap(remote)
        peer.request(1, "platform.music", "music")
        let music = try await peer.expect()
        XCTAssertEqual(music["value"] as? String, "music")
        peer.request(2, "platform.other")
        let other = try await peer.expect()
        XCTAssertEqual(other["value"] as? String, "other")
        peer.request(3, "platform.unregistered")
        let denied = try await peer.expect()
        XCTAssertEqual(denied["code"] as? String, "METHOD_UNAVAILABLE")
        await session.close()
        XCTAssertTrue(a.isClosed)
        let eof = await peer.next()
        XCTAssertNil(eof)
        await launcher.closePeer()
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
    }

    func testCancelledBorrowerFinishesBeforeLaunchWithoutCancellingOtherBorrowers() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeBridgeSession(launcher: launcher)
        try await session.configure(handlers: [:])
        let cancelled = Task { try await session.connection() }
        await launcher.gate.started()
        let other = Task { try await session.connection() }
        let cancelledDone = expectation(description: "cancelled wait finishes while launch is held")
        cancelled.cancel()
        let check = Task {
            do { _ = try await cancelled.value; XCTFail("cancelled") }
            catch { XCTAssertEqual(error as? RuntimeBridgeError, .cancelled) }
            cancelledDone.fulfill()
        }
        await fulfillment(of: [cancelledDone], timeout: 2)
        await launcher.gate.release()
        await check.value
        let connection = try await other.value
        XCTAssertFalse(connection.isClosed)
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
        await session.close()
        await launcher.closePeer()
    }

    func testLaunchFailureIsRetainedWithoutRetry() async throws {
        let launcher = RuntimeSessionLauncher(failure: .payloadUnavailable)
        let session = RuntimeBridgeSession(launcher: launcher)
        try await session.configure(handlers: [:])
        let first = Task { try await session.connection() }
        await launcher.gate.started()
        let second = Task { try await session.connection() }
        await launcher.gate.release()
        for task in [first, second] {
            do { _ = try await task.value; XCTFail("failed launch") }
            catch { XCTAssertEqual(error as? RuntimeLaunchError, .payloadUnavailable) }
        }
        do { _ = try await session.connection(); XCTFail("terminal launch") }
        catch { XCTAssertEqual(error as? RuntimeLaunchError, .payloadUnavailable) }
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
        await session.close()
    }

    func testCloseRejectsBorrowersAndDrainsALaunchThatReturnsAfterCancellation() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeBridgeSession(launcher: launcher)
        try await session.configure(handlers: [:])
        let borrower = Task { try await session.connection() }
        await launcher.gate.started()
        let closed = expectation(description: "close must wait for the late launch")
        closed.isInverted = true
        let close = Task { await session.close(); closed.fulfill() }
        do { _ = try await borrower.value; XCTFail("closed borrower") }
        catch { XCTAssertEqual(error as? RuntimeBridgeError, .closed) }
        await fulfillment(of: [closed], timeout: 0.05)
        let anotherClose = Task { await session.close() }
        close.cancel() // Cancelling the close caller cannot abandon late disposal.
        await launcher.gate.release()
        await close.value
        await anotherClose.value
        let native = await launcher.native, remote = await launcher.peer
        let connection = try XCTUnwrap(native), peer = try XCTUnwrap(remote)
        XCTAssertTrue(connection.isClosed)
        let eof = await peer.next()
        XCTAssertNil(eof)
        do { _ = try await session.connection(); XCTFail("closed") }
        catch { XCTAssertEqual(error as? RuntimeBridgeError, .closed) }
        await launcher.closePeer()
    }

    func testPeerExitIsFinalAndDoesNotLaunchAgain() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeBridgeSession(launcher: launcher)
        try await session.configure(handlers: [:])
        await launcher.gate.release()
        let connection = try await session.connection()
        let closed = expectation(description: "EOF")
        connection.onClose { closed.fulfill() }
        await launcher.closePeer()
        await fulfillment(of: [closed], timeout: 2)
        do { _ = try await session.connection(); XCTFail("EOF is final") }
        catch { XCTAssertEqual(error as? RuntimeBridgeError, .closed) }
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
        await session.close()
    }

    func testCloseAndWaitDrainsAnAdmittedHandlerEvenIfItIgnoresCancellation() async throws {
        let gate = RuntimeSessionGate(), launcher = RuntimeSessionLauncher()
        let session = RuntimeBridgeSession(launcher: launcher)
        try await session.configure(handlers: ["platform.wait": { _ in await gate.wait(); return .null }])
        await launcher.gate.release()
        let connection = try await session.connection()
        let remote = await launcher.peer
        let peer = try XCTUnwrap(remote)
        peer.request(1, "platform.wait")
        await gate.started()
        let closing = expectation(description: "logical close")
        connection.onClose { closing.fulfill() }
        let drained = expectation(description: "close waits for actual handler completion")
        drained.isInverted = true
        let close = Task { await session.close(); drained.fulfill() }
        await fulfillment(of: [closing], timeout: 2)
        await fulfillment(of: [drained], timeout: 0.05)
        close.cancel()
        await gate.release()
        await close.value
        let eof = await peer.next()
        XCTAssertNil(eof)
        await launcher.closePeer()
    }
}

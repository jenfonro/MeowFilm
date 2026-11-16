import Foundation
import XCTest
@testable import MeowRuntime

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
    private let configuredClient: RuntimeHTTPClient?
    init(failure: RuntimeLaunchError? = nil, holdExit: Bool = false, client: RuntimeHTTPClient? = nil) {
        self.failure = failure
        configuredClient = client
        exit = RuntimeSessionGate(released: !holdExit)
    }
    func connect() async throws -> RuntimeHTTPClient {
        calls += 1
        // A late executor still belongs to the shared App owner.
        await gate.wait()
        if let failure { throw failure }
        let value = try configuredClient ?? RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!)
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
    func testObservationNeverStartsRuntimeAndCancellationDoesNotWaitForStartup() async throws {
        let launcher = RuntimeSessionLauncher(), session = RuntimeSession(launcher: launcher)
        let offline = await session.connectionIfStarted()
        XCTAssertNil(offline)
        let startup = Task { try await session.connection() }
        await launcher.gate.started()
        let observing = Task { await session.connectionIfStarted() }
        let cancelled = expectation(description: "observer retires while physical startup is held")
        observing.cancel()
        let checked = Task {
            let client = await observing.value
            XCTAssertNil(client)
            cancelled.fulfill()
        }
        await fulfillment(of: [cancelled], timeout: 2)
        await launcher.gate.release()
        await checked.value
        let client = try await startup.value
        let active = await session.connectionIfStarted()
        XCTAssertTrue(active === client)
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
        await session.close()
        let closed = await session.connectionIfStarted()
        XCTAssertNil(closed)
    }

    func testSettingsNotificationsDropOfflineChangesAndBorrowOnlyTheCurrentStartup() async throws {
        let seen = SettingsNotificationRequests()
        let notified = expectation(description: "starting and active notifications")
        notified.expectedFulfillmentCount = 2
        SettingsNotificationURLProtocol.install { request, peer in
            XCTAssertEqual(request.url?.path, "/v1/settings/changed")
            let body = peer.body()
            guard case .object(let fields) = try? RuntimeJSON.decode(body),
                  case .string(let block)? = fields["block"], fields.count == 1 else {
                XCTFail("only a block name may enter a notification"); return
            }
            seen.append(block)
            peer.respond()
            notified.fulfill()
        }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [SettingsNotificationURLProtocol.self]
        let client = try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!,
                                          timeout: 3, configuration: configuration)
        let launcher = RuntimeSessionLauncher(client: client), session = RuntimeSession(launcher: launcher)
        session.settingsChanged("offline-before-start")
        let before = await launcher.calls
        XCTAssertEqual(before, 0)
        let startup = Task { try await session.connection() }
        await launcher.gate.started()
        session.settingsChanged("film.rules")
        XCTAssertTrue(seen.values.isEmpty)
        await launcher.gate.release()
        _ = try await startup.value
        session.settingsChanged("tmdb")
        await fulfillment(of: [notified], timeout: 5)
        XCTAssertEqual(Set(seen.values), ["film.rules", "tmdb"])
        await session.close()
        session.settingsChanged("offline-after-close")
        XCTAssertEqual(seen.values.count, 2)
        let calls = await launcher.calls
        XCTAssertEqual(calls, 1)
    }

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

private final class SettingsNotificationRequests: @unchecked Sendable {
    private let lock = NSLock()
    private var blocks: [String] = []
    func append(_ block: String) { lock.lock(); blocks.append(block); lock.unlock() }
    var values: [String] { lock.lock(); defer { lock.unlock() }; return blocks }
}

private final class SettingsNotificationURLProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var handler: (@Sendable (URLRequest, SettingsNotificationURLProtocol) -> Void)?
    static func install(_ handler: @escaping @Sendable (URLRequest, SettingsNotificationURLProtocol) -> Void) {
        lock.lock(); self.handler = handler; lock.unlock()
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); let handler = Self.handler; Self.lock.unlock()
        handler?(request, self)
    }
    override func stopLoading() {}
    func body() -> Data {
        if let bytes = request.httpBody { return bytes }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open(); defer { stream.close() }
        var data = Data(), bytes = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = stream.read(&bytes, maxLength: bytes.count)
            if count <= 0 { return data }
            data.append(contentsOf: bytes.prefix(count))
        }
    }
    func respond() {
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "application/json",
                           "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data("{\"ok\":true,\"contract\":\"runtime.http.v1\",\"value\":{\"notified\":true}}".utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
}

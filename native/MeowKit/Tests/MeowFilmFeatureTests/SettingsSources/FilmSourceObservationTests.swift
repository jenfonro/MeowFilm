import Foundation
import XCTest
import MeowStorage
import MeowStorageTestSupport
@testable import MeowRuntime
@testable import MeowFilmFeature

final class FilmSourceObservationTests: XCTestCase {
    private func fixture(_ connection: @escaping @Sendable () async -> RuntimeHTTPClient?) throws
        -> (FilmSourceService, FilmSettingsDocument, URL) {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try storageTestDatabase(self, root: base)
        let settings = try sourceTestSettings(store)
        let service = FilmSourceService(transport: unavailableCatTransport("MUST_NOT_START"),
            database: store,
            settingsDocument: settings, runtimeSourceRoot: base.appendingPathComponent("cat"),
            observeConnection: connection)
        return (service, settings, base)
    }

    func testNativeSaveReturnsWhileExistingLaunchWaitsAndShutdownDrainsItWithoutStartingRequests() async throws {
        let entered = expectation(description: "borrow existing launch")
        let gate = SourceObservationConnectionGate { entered.fulfill() }
        let (service, settings, base) = try fixture { await gate.connection() }
        let peer = SourceObservationHTTPFixture()
        defer { peer.remove() }
        let client = try peer.connection()
        let saved = try await service.editCat("a", name: "Offline saved")
        XCTAssertEqual(saved.name, "Offline saved")
        storageTestEqual(try await settings.read()?.catField("scripts")?.catArray?.first?.catField("name"), .string("Offline saved"))
        await fulfillment(of: [entered], timeout: 5)
        let drained = expectation(description: "existing launch borrow drained")
        drained.isInverted = true
        let draining = Task { await service.prepareShutdown(); drained.fulfill() }
        await fulfillment(of: [drained], timeout: 0.05)
        await gate.release(client)
        await draining.value
        let snapshot = await service.snapshot
        XCTAssertEqual(snapshot.cats.first?.name, "Offline saved")
        XCTAssertNil(snapshot.runtimeError)
        XCTAssertEqual(peer.requestCount, 0)
        XCTAssertFalse(client.isClosed)
        await service.close()
        await client.close()
    }

    func testOwnerObservesSavedRevisionAndSitesWithoutRefreshAndRejectsOldStatusAndGeneration() async throws {
        let peer = SourceObservationHTTPFixture()
        defer { peer.remove() }
        let client = try peer.connection()
        let (service, settings, base) = try fixture { client }
        try await service.initialize()
        await wait(service) { $0.cats.allSatisfy(\.canDetect) && $0.videos.count == 2 }
        let deleted = FilmSourceKey(installationId: "a", siteKey: "same")
        try await service.deleteVideo(deleted)
        let entered = expectation(description: "old status captured")
        peer.holdNext { entered.fulfill() }
        await fulfillment(of: [entered], timeout: 5)
        let saved = try await service.editCat("a", name: "Latest", sourceURL: "https://example.invalid/latest.js")
        try await service.deleteCat("b")
        XCTAssertEqual(saved.inputRevision, 2)
        XCTAssertEqual(saved.generation, 0)
        let intent = try await settings.read()
        peer.replace([SourcesRuntimeFixture.cat("a", generation: 4)])
        peer.finish()
        // Neither the captured old response nor a later mismatching revision
        // can authorize the old generation or resurrect either deleted row.
        let pending = await service.snapshot
        XCTAssertEqual(pending.cats.map(\.installationId), ["a"])
        XCTAssertEqual(pending.cats.first?.inputRevision, 2)
        XCTAssertTrue(pending.videos.isEmpty)
        guard case .object(var current) = SourcesRuntimeFixture.cat("a", generation: 5) else {
            return XCTFail("Invalid fixture")
        }
        current["name"] = .string("Latest")
        current["sourceUrl"] = .string("https://example.invalid/latest.js")
        current["inputRevision"] = .number(2)
        peer.replace([.object(current)])
        await wait(service) { $0.cats.first?.generation == 5 && $0.cats.first?.canDetect == true }
        let ready = await service.snapshot
        XCTAssertEqual(ready.cats.first?.name, "Latest")
        XCTAssertTrue(ready.videos.isEmpty)
        current["generation"] = .number(3)
        peer.replace([.object(current)])
        let observed = expectation(description: "older generation delivered")
        peer.holdNext { observed.fulfill() }
        await fulfillment(of: [observed], timeout: 5)
        peer.finish()
        let next = expectation(description: "older response processed before next poll")
        peer.holdNext { next.fulfill() }
        await fulfillment(of: [next], timeout: 5)
        await service.prepareShutdown()
        peer.finish()
        let final = await service.snapshot
        XCTAssertEqual(final.cats.first?.generation, 5)
        storageTestEqual(try await settings.read(), intent)
        XCTAssertFalse(client.isClosed, "The source owner must not close its borrowed client")
        await service.close()
        await client.close()
    }

    func testRuntimeObservationFailureDoesNotTurnCommittedSaveIntoFailureOrChangeIntent() async throws {
        let peer = SourceObservationHTTPFixture()
        defer { peer.remove() }
        peer.replace([.object(["invalid": .bool(true)])])
        let client = try peer.connection()
        let (service, settings, base) = try fixture { client }
        let saved = try await service.editCat("a", name: "Committed")
        XCTAssertEqual(saved.name, "Committed")
        let intent = try await settings.read()
        await wait(service) { $0.runtimeError == "INVALID_RESPONSE" }
        storageTestEqual(try await settings.read(), intent)
        await service.close()
        await client.close()
    }

    func testPreSaveStatusAndErrorCannotPublishAcrossDisableEnableEvenWhenInputRevisionIsUnchanged() async throws {
        for malformed in [false, true] {
            let peer = SourceObservationHTTPFixture()
            defer { peer.remove() }
            if malformed { peer.replace([.object(["invalid": .bool(true)])]) }
            let entered = expectation(description: "pre-save status")
            peer.holdNext { entered.fulfill() }
            let client = try peer.connection()
            let (service, settings, base) = try fixture { client }
                try await service.initialize()
            await fulfillment(of: [entered], timeout: 5)
            _ = try await service.enableCat("a", enabled: false)
            let saved = try await service.enableCat("a", enabled: true)
            XCTAssertEqual(saved.inputRevision, 1)
            let intent = try await settings.read()
            peer.replace([SourcesRuntimeFixture.cat("a", generation: 3), SourcesRuntimeFixture.cat("b", generation: 3)])
            peer.finish()
            let next = expectation(description: "old response discarded before next request")
            peer.holdNext { next.fulfill() }
            await fulfillment(of: [next], timeout: 5)
            let pending = await service.snapshot
            XCTAssertTrue(pending.cats.allSatisfy { $0.generation == 0 })
            XCTAssertTrue(pending.videos.isEmpty)
            XCTAssertNil(pending.runtimeError)
            peer.finish()
            await wait(service) { $0.cats.allSatisfy { $0.generation == 3 && $0.canDetect } }
            storageTestEqual(try await settings.read(), intent)
            await service.close()
            await client.close()
        }
    }

    func testShutdownCancelsAndDrainsActualHTTPObservationAndResumeCanBorrowAgain() async throws {
        let peer = SourceObservationHTTPFixture()
        defer { peer.remove() }
        let entered = expectation(description: "status in flight")
        let stopped = expectation(description: "status cancelled")
        peer.holdNext(started: { entered.fulfill() }, stopped: { stopped.fulfill() })
        let client = try peer.connection()
        let (service, settings, base) = try fixture { client }
        try await service.initialize()
        await fulfillment(of: [entered], timeout: 5)
        let intent = try await settings.read()
        await service.prepareShutdown()
        await fulfillment(of: [stopped], timeout: 5)
        let cancelled = await service.snapshot
        XCTAssertTrue(cancelled.videos.isEmpty)
        XCTAssertNil(cancelled.runtimeError)
        XCTAssertFalse(client.isClosed)
        peer.finish()
        try await service.resume()
        await wait(service) { $0.videos.count == 2 }
        await service.close()
        storageTestEqual(try await settings.read(), intent)
        let final = await service.snapshot
        XCTAssertEqual(final.videos.count, 2)
        await client.close()
        do { try await service.resume(); XCTFail("Closed owners cannot resume observation") }
        catch { XCTAssertEqual(error as? FilmSourceError, .init(code: "CLOSED")) }
    }

    private func wait(_ service: FilmSourceService,
                      _ predicate: @escaping @Sendable (FilmSourceSnapshot) -> Bool) async {
        let received = expectation(description: "observed actual runtime state")
        let task = Task {
            for await snapshot in await service.snapshots() {
                if predicate(snapshot) { received.fulfill(); return }
            }
        }
        await fulfillment(of: [received], timeout: 5)
        task.cancel()
        await task.value
        let snapshot = await service.snapshot
        XCTAssertTrue(predicate(snapshot))
    }
}

private actor SourceObservationConnectionGate {
    private let entered: @Sendable () -> Void
    private var waiter: CheckedContinuation<RuntimeHTTPClient?, Never>?
    init(entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func connection() async -> RuntimeHTTPClient? {
        await withCheckedContinuation { waiter = $0; entered() }
    }
    func release(_ client: RuntimeHTTPClient?) { waiter?.resume(returning: client); waiter = nil }
}

private final class SourceObservationHTTPFixture: @unchecked Sendable {
    let baseURL = URL(string: "http://\(UUID().uuidString.lowercased()).runtime.invalid")!
    private let lock = NSLock()
    private var cats = [SourcesRuntimeFixture.cat("a"), SourcesRuntimeFixture.cat("b")]
    private var hold: ((@Sendable () -> Void), (@Sendable () -> Void))?
    private var pending: (SourceObservationHTTPProtocol, RuntimeValue)?
    private var requests = 0
    init() { SourceObservationHTTPProtocol.register(self) }
    func connection() throws -> RuntimeHTTPClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [SourceObservationHTTPProtocol.self]
        return try RuntimeHTTPClient(baseURL: baseURL, configuration: configuration)
    }
    func replace(_ cats: [RuntimeValue]) { lock.lock(); self.cats = cats; lock.unlock() }
    var requestCount: Int { lock.lock(); defer { lock.unlock() }; return requests }
    func holdNext(started: @escaping @Sendable () -> Void, stopped: @escaping @Sendable () -> Void = {}) {
        lock.lock(); hold = (started, stopped); lock.unlock()
    }
    func receive(_ peer: SourceObservationHTTPProtocol) {
        lock.lock()
        requests += 1
        let value: RuntimeValue = .object(["instances": .array(cats)])
        let hold = self.hold; self.hold = nil
        if hold != nil { pending = (peer, value) }
        lock.unlock()
        if let hold { peer.onStop(hold.1); hold.0() } else { peer.respond(value) }
    }
    func finish() {
        lock.lock(); let pending = pending; self.pending = nil; lock.unlock()
        if let pending { pending.0.respond(pending.1) }
    }
    func remove() { SourceObservationHTTPProtocol.remove(self) }
}

private final class SourceObservationHTTPProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [String: SourceObservationHTTPFixture] = [:]
    private let delivery = NSRecursiveLock()
    private var cancelled = false
    private var onStopped: (@Sendable () -> Void)?
    func onStop(_ callback: @escaping @Sendable () -> Void) {
        delivery.lock()
        if cancelled { delivery.unlock(); callback(); return }
        onStopped = callback; delivery.unlock()
    }
    static func register(_ fixture: SourceObservationHTTPFixture) {
        lock.lock(); fixtures[fixture.baseURL.host!] = fixture; lock.unlock()
    }
    static func remove(_ fixture: SourceObservationHTTPFixture) {
        lock.lock(); fixtures[fixture.baseURL.host!] = nil; lock.unlock()
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock()
        let fixture = request.url?.host.flatMap { Self.fixtures[$0] }
        Self.lock.unlock()
        guard request.url?.path == "/v1/cat/status", request.httpMethod == "POST", let fixture else {
            client?.urlProtocol(self, didFailWithError: URLError(.unsupportedURL)); return
        }
        fixture.receive(self)
    }
    override func stopLoading() {
        delivery.lock(); cancelled = true; let stopped = onStopped; onStopped = nil; delivery.unlock()
        stopped?()
    }
    func respond(_ value: RuntimeValue) {
        delivery.lock(); defer { delivery.unlock() }
        guard !cancelled else { return }
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "application/json",
                "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: try! RuntimeJSON.encode(.object([
            "ok": .bool(true), "contract": .string("cat.script.v2"), "value": value
        ])))
        client?.urlProtocolDidFinishLoading(self)
    }
}

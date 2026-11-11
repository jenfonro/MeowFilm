import Foundation
import XCTest
@testable import MeowRuntime
@testable import MeowNovelFeature

final class NovelImageLifetimeTests: XCTestCase {
    func testCancelledAcceptedAcquisitionStillReleasesAndWaitsForTheReleaseReceipt() async throws {
        let acquired = expectation(description: "acquisition accepted"), releasing = expectation(description: "release started")
        let runtime = NovelImageLifetimeFixture(acquired: { acquired.fulfill() }, releasing: { releasing.fulfill() })
        let client = NovelSourceClient(runtime: runtime), source = NovelRuleProcessorTests.source()
        let request = Task { try await client.readImage(source: source,
            book: .init(installationId: source.id, bookUrl: "book", name: "猫"), url: "image", kind: "content") }
        await fulfillment(of: [acquired], timeout: 3)
        request.cancel(); await runtime.acceptAcquisition()
        await fulfillment(of: [releasing], timeout: 3)
        let counts = await runtime.counts
        XCTAssertEqual(counts.images, 0, "Cancellation after acquisition must not start another binary read")
        XCTAssertEqual(counts.releases, 1)
        await runtime.acceptRelease()
        do { _ = try await request.value; XCTFail("Cancelled image cannot succeed") }
        catch { XCTAssertTrue(error is CancellationError) }
    }
    func testReadAndReleaseFailuresAreVisibleRatherThanReturningUnreleasedBytes() async throws {
        let runtime = NovelImageLifetimeFixture(acquired: {}, releasing: {}, immediate: true, failRelease: true)
        let source = NovelRuleProcessorTests.source()
        do {
            _ = try await NovelSourceClient(runtime: runtime).readImage(source: source,
                book: .init(installationId: source.id, bookUrl: "book", name: "猫"), url: "image", kind: "content")
            XCTFail("Failed release cannot return success")
        } catch { XCTAssertEqual(error as? NovelError, .imageReleaseFailed) }
        let counts = await runtime.counts
        XCTAssertEqual(counts.images, 1); XCTAssertEqual(counts.releases, 1)
    }
    func testActualRuntimeOwnerCloseDrainsAcceptedReleaseAndReportsCleanupFailure() async throws {
        for fail in [false, true] {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.protocolClasses = [NovelImageURLProtocol.self]
            let client = try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!,
                configuration: configuration)
            let owner = NovelRuntime(connection: { client })
            let releaseEntered = expectation(description: "owner release reached HTTP")
            let gate = NovelImageHTTPGate()
            NovelImageURLProtocol.fixture.set { request, transport in
                if request.url?.path.hasSuffix("/library.image") == true {
                    transport.respond(.object(["reference": .string("/v1/source/images/accepted")]))
                } else {
                    XCTAssertEqual(request.url?.path, "/v1/source/library.releaseImage")
                    gate.hold(transport); releaseEntered.fulfill()
                }
            }
            let receipt = try await owner.call("library.image", .object([:]))
            XCTAssertEqual(receipt["reference"], .string("/v1/source/images/accepted"))
            let closed = NovelImageCloseFlag()
            let close = Task { () -> Result<Void, Error> in
                do { try await owner.close(); await closed.mark(); return .success(()) }
                catch { await closed.mark(); return .failure(error) }
            }
            await fulfillment(of: [releaseEntered], timeout: 3)
            let early = await closed.value; XCTAssertFalse(early)
            gate.finish(fail: fail)
            let result = await close.value
            if fail {
                guard case .failure(let error) = result else { return XCTFail("Cleanup failure must be reported") }
                XCTAssertEqual(error as? NovelError, .imageReleaseFailed)
            } else { try result.get() }
            await client.close()
        }
    }
    func testClosingOwnerAndLateImageConsumerShareOnePhysicalReleaseReceipt() async throws {
        let configuration = URLSessionConfiguration.ephemeral; configuration.protocolClasses = [NovelImageURLProtocol.self]
        let client = try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!,
            configuration: configuration)
        let owner = NovelRuntime(connection: { client }), gate = NovelImageHTTPGate()
        let entered = expectation(description: "one physical release")
        NovelImageURLProtocol.fixture.set { request, transport in
            if request.url?.path.hasSuffix("/library.image") == true { transport.respond(.object(["reference": .string("/v1/source/images/accepted")])) }
            else { gate.hold(transport); entered.fulfill() }
        }
        _ = try await owner.call("library.image", .object([:]))
        let closing = Task { try await owner.close() }
        await fulfillment(of: [entered], timeout: 3)
        let late = Task { try await owner.call("library.releaseImage", .object(["reference": .string("/v1/source/images/accepted")])) }
        gate.finish(fail: false)
        try await closing.value
        let receipt = try await late.value; XCTAssertEqual(receipt["released"], .bool(true))
        let afterClose = try await owner.call("library.releaseImage", .object(["reference": .string("/v1/source/images/accepted")]))
        XCTAssertEqual(afterClose, receipt); XCTAssertEqual(gate.count, 1)
        await client.close()
    }
}
private actor NovelImageLifetimeFixture: NovelRuntimeServing {
    private let acquired: @Sendable () -> Void, releasing: @Sendable () -> Void
    private let immediate: Bool, failRelease: Bool
    private var acquisition: CheckedContinuation<Void, Never>?, release: CheckedContinuation<Void, Never>?
    private(set) var counts = (images: 0, releases: 0)
    init(acquired: @escaping @Sendable () -> Void, releasing: @escaping @Sendable () -> Void,
         immediate: Bool = false, failRelease: Bool = false) {
        self.acquired = acquired; self.releasing = releasing; self.immediate = immediate; self.failRelease = failRelease
    }
    func setActivityHandler(_ handler: (@Sendable (Bool) -> Void)?) {}
    func hasActiveCalls() -> Bool { false }
    func close() {}
    func acceptAcquisition() { acquisition?.resume(); acquisition = nil }
    func acceptRelease() { release?.resume(); release = nil }
    func image(_ reference: String) async throws -> Data { counts.images += 1; throw NovelError.invalidResponse }
    func call(_ operation: String, _ parameters: RuntimeValue) async throws -> RuntimeValue {
        if operation == "library.image" {
            if !immediate { await withCheckedContinuation { acquisition = $0; acquired() } }
            return .object(["reference": .string("/v1/source/images/accepted")])
        }
        XCTAssertEqual(operation, "library.releaseImage"); counts.releases += 1
        if !immediate { await withCheckedContinuation { release = $0; releasing() } }
        if failRelease { throw NovelError.invalidResponse }
        return .object(["released": .bool(true)])
    }
}
private actor NovelImageCloseFlag {
    private(set) var value = false
    func mark() { value = true }
}
private final class NovelImageHTTPFixture: @unchecked Sendable {
    private let lock = NSLock()
    private var handler: (@Sendable (URLRequest, NovelImageURLProtocol) -> Void)?
    func set(_ handler: @escaping @Sendable (URLRequest, NovelImageURLProtocol) -> Void) { lock.lock(); self.handler = handler; lock.unlock() }
    func handle(_ request: URLRequest, _ transport: NovelImageURLProtocol) { lock.lock(); let handler = handler; lock.unlock(); handler?(request, transport) }
}
private final class NovelImageHTTPGate: @unchecked Sendable {
    private let lock = NSLock()
    private var transport: NovelImageURLProtocol?
    private var started = 0
    var count: Int { lock.lock(); defer { lock.unlock() }; return started }
    func hold(_ transport: NovelImageURLProtocol) { lock.lock(); self.transport = transport; started += 1; lock.unlock() }
    func finish(fail: Bool) {
        lock.lock(); let transport = transport; self.transport = nil; lock.unlock()
        transport?.respond(fail ? nil : .object(["released": .bool(true)]))
    }
}
private final class NovelImageURLProtocol: URLProtocol, @unchecked Sendable {
    static let fixture = NovelImageHTTPFixture()
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() { Self.fixture.handle(request, self) }
    override func stopLoading() {}
    func respond(_ value: RuntimeValue?) {
        let body: RuntimeValue = value.map { .object(["ok": .bool(true), "contract": .string("source.runtime.v2"), "value": $0]) } ??
            .object(["ok": .bool(false), "contract": .string("source.runtime.v2"), "code": .string("SOURCE_FAILURE")])
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: [
            "Content-Type": "application/json", "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: try! RuntimeJSON.encode(body)); client?.urlProtocolDidFinishLoading(self)
    }
}

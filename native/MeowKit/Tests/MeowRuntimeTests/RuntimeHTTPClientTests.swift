import Foundation
import XCTest
@testable import MeowRuntime

private final class RuntimeHTTPFixture: @unchecked Sendable {
    typealias Handler = @Sendable (URLRequest, RuntimeTestURLProtocol) -> Void
    private let lock = NSLock()
    private var handler: Handler?
    func set(_ handler: @escaping Handler) { lock.lock(); self.handler = handler; lock.unlock() }
    func handle(_ request: URLRequest, _ transport: RuntimeTestURLProtocol) {
        lock.lock(); let handler = handler; lock.unlock()
        handler?(request, transport)
    }
}

private final class RuntimeTestURLProtocol: URLProtocol, @unchecked Sendable {
    static let fixture = RuntimeHTTPFixture()
    private let lock = NSLock()
    private var stopped: (@Sendable () -> Void)?
    private var didStop = false
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() { Self.fixture.handle(request, self) }
    override func stopLoading() {
        lock.lock(); didStop = true; let callback = stopped; stopped = nil; lock.unlock()
        callback?()
    }
    func onStop(_ callback: @escaping @Sendable () -> Void) {
        lock.lock()
        if didStop { lock.unlock(); callback(); return }
        stopped = callback; lock.unlock()
    }
    func respond(_ value: RuntimeValue, status: Int = 200, headers: [String: String] = [:]) {
        do { try respond(data: RuntimeJSON.encode(value), status: status, headers: headers) }
        catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    func respond(data: Data, status: Int = 200, headers: [String: String] = [:]) throws {
        var fields = ["Content-Type": "application/json",
                      "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!]
        fields.merge(headers) { $1 }
        let response = try XCTUnwrap(HTTPURLResponse(url: request.url!, statusCode: status,
                                                    httpVersion: "HTTP/1.1", headerFields: fields))
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }
    func fail(_ code: Int) { client?.urlProtocol(self, didFailWithError: NSError(domain: NSURLErrorDomain, code: code)) }

    func body() -> Data {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open(); defer { stream.close() }
        var data = Data(), bytes = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = stream.read(&bytes, maxLength: bytes.count)
            if count <= 0 { return data }
            data.append(contentsOf: bytes.prefix(count))
        }
    }
}

private actor RuntimeHTTPReorderedPeer {
    private var requests: [(RuntimeValue, RuntimeTestURLProtocol)] = []
    private var ids = Set<String>()
    func receive(_ value: RuntimeValue, _ transport: RuntimeTestURLProtocol) {
        ids.insert(transport.request.value(forHTTPHeaderField: "X-Meow-Request-Id")!)
        requests.append((value, transport))
        if requests.count == 2 {
            for (value, transport) in requests.reversed() {
                transport.respond(.object(["ok": .bool(true), "contract": .string("music.script.v3"), "value": value]))
            }
            requests.removeAll()
        }
    }
    var requestCount: Int { ids.count }
}

final class RuntimeHTTPClientTests: XCTestCase {
    private let sessionID = "11111111-1111-4111-8111-111111111111"
    private let key = String(repeating: "a", count: 64)
    private func client(timeout: TimeInterval = 30) throws -> RuntimeHTTPClient {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [RuntimeTestURLProtocol.self]
        return try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!, sessionID: sessionID,
                                     requestKey: key, timeout: timeout, configuration: config)
    }
    private func reply(_ value: RuntimeValue, contract: String = "music.script.v3") -> RuntimeValue {
        .object(["ok": .bool(true), "contract": .string(contract), "value": value])
    }

    func testConcurrentHTTPRepliesStayWithTheirRequestWhenCompletedInReverseOrder() async throws {
        let client = try client(), peer = RuntimeHTTPReorderedPeer()
        RuntimeTestURLProtocol.fixture.set { _, transport in
            let value = try! RuntimeJSON.decode(transport.body())
            Task { await peer.receive(value, transport) }
        }
        async let a = client.call(module: .music, operation: "invoke", parameters: .string("first"))
        async let b = client.call(module: .music, operation: "invoke", parameters: .string("second"))
        let values = try await [a, b]
        XCTAssertEqual(values, [.string("first"), .string("second")])
        let count = await peer.requestCount
        XCTAssertEqual(count, 2)
        await client.close()
    }

    func testDirectHTTPRequestsHaveOnlyHeaderCredentialsAndReturnTheBusinessValue() async throws {
        let client = try client()
        RuntimeTestURLProtocol.fixture.set { [sessionID, key] request, transport in
            XCTAssertEqual(request.url?.absoluteString, "http://127.0.0.1:43210/v1/music/invoke")
            XCTAssertEqual(request.httpMethod, "POST")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer \(key)")
            XCTAssertEqual(request.value(forHTTPHeaderField: "X-Meow-Session"), sessionID)
            XCTAssertNotNil(UUID(uuidString: request.value(forHTTPHeaderField: "X-Meow-Request-Id") ?? ""))
            XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
            XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/json")
            let expected: RuntimeValue = .object(["blob": .string("business"), "token": .string("opaque"), "中文": .null])
            XCTAssertEqual(try? RuntimeJSON.decode(transport.body()), expected)
            transport.respond(.object(["ok": .bool(true), "contract": .string("music.script.v3"),
                                       "value": .array([.string("direct"), .null])]))
        }
        let value = try await client.call(module: .music, operation: "invoke",
            parameters: .object(["blob": .string("business"), "token": .string("opaque"), "中文": .null]))
        XCTAssertEqual(value, .array([.string("direct"), .null]))
        await client.close()
        XCTAssertTrue(client.isClosed)
    }

    func testDescribeUploadAndShutdownUseTheirPublicRoutes() async throws {
        let client = try client()
        let source = Data([0xef, 0xbb, 0xbf, 0, 255, 10])
        RuntimeTestURLProtocol.fixture.set { request, transport in
            switch request.httpMethod {
            case "GET":
                XCTAssertEqual(request.url?.path, "/v1/describe")
                XCTAssertTrue(transport.body().isEmpty)
                transport.respond(.object(["ok": .bool(true), "contract": .string("runtime.http.v1"),
                    "value": .object(["protocol": .string("runtime.http.v1")])]))
            case "PUT":
                XCTAssertEqual(request.url?.absoluteString, "http://127.0.0.1:43210/v1/source/sources/source%2F%E7%8C%AB/7?basename=%E7%8C%AB%20rule.js")
                XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/octet-stream")
                XCTAssertEqual(transport.body(), source)
                transport.respond(.object(["ok": .bool(true), "contract": .string("source.runtime.v2"), "value": .null]))
            default:
                XCTAssertEqual(request.url?.path, "/v1/close")
                XCTAssertEqual(transport.body(), Data("{}".utf8))
                transport.respond(.object(["ok": .bool(true), "contract": .string("runtime.http.v1"),
                    "value": .object(["closed": .bool(true), "cleanupComplete": .bool(true)])]))
            }
        }
        let described = try await client.describe()
        XCTAssertEqual(described, .object(["protocol": .string("runtime.http.v1")]))
        let uploaded = try await client.upload(module: .source, installationID: "source/猫", sourceRevision: 7,
                                               source: source, basename: "猫 rule.js")
        XCTAssertEqual(uploaded, .null)
        try await client.shutdown()
        XCTAssertFalse(client.isClosed, "HTTP close receipt does not stand in for owner shutdown")
        await client.close()
    }

    func testSuccessEnvelopeDepthDoesNotReduceBusinessDepthAndLargeValuesRemainWhole() async throws {
        let client = try client()
        var nested: RuntimeValue = .null
        for _ in 0..<64 { nested = .array([nested]) }
        let depthValue = nested
        RuntimeTestURLProtocol.fixture.set { _, transport in
            // RuntimeJSON.encode checks business depth, so construct the wire
            // envelope around the independently encoded value as the server does.
            let value = try! RuntimeJSON.encode(depthValue)
            var bytes = Data(#"{"ok":true,"contract":"music.script.v3","value":"#.utf8)
            bytes.append(value); bytes.append(125)
            try! transport.respond(data: bytes)
        }
        let deepReply = try await client.call(module: .music, operation: "invoke")
        XCTAssertEqual(deepReply, nested)
        let large = RuntimeValue.string(String(repeating: "x", count: 8 * 1024 * 1024 + 1))
        RuntimeTestURLProtocol.fixture.set { _, transport in
            transport.respond(.object(["ok": .bool(true), "contract": .string("music.script.v3"), "value": large]))
        }
        let largeReply = try await client.call(module: .music, operation: "invoke", parameters: large)
        XCTAssertEqual(largeReply, large)
        await client.close()
    }

    func testEveryBusinessValueKindAndExactUnicodeKeysSurviveTheHTTPEnvelope() async throws {
        let client = try client()
        for value: RuntimeValue in [.null, .bool(true), .number(1.25), .string("中文😀"),
            .array([.string("direct"), .null]),
            .object(["é": .number(1), "e\u{0301}": .number(2), "blob": .string("business"), "token": .null])] {
            RuntimeTestURLProtocol.fixture.set { _, transport in
                XCTAssertEqual(try? RuntimeJSON.decode(transport.body()), value)
                transport.respond(.object(["ok": .bool(true), "contract": .string("music.script.v3"), "value": value]))
            }
            let returned = try await client.call(module: .music, operation: "invoke", parameters: value)
            XCTAssertEqual(returned, value)
        }
        await client.close()
    }

    func testStatusIdentitySchemaContractAndSafeErrorCodesAreValidated() async throws {
        let client = try client()
        let good = reply(.null)
        let invalid: [(RuntimeValue, Int, [String: String])] = [
            (good, 201, [:]), (good, 302, ["Location": "http://127.0.0.1:43210/elsewhere"]),
            (good, 400, [:]), (good, 200, ["Content-Type": "application/json; charset=utf-8"]),
            (good, 200, ["X-Meow-Request-Id": sessionID]),
            (reply(.null, contract: "cat.script.v2"), 200, [:]),
            (.object(["ok": .bool(true), "contract": .string("runtime.http.v1"), "value": .null]), 200, [:]),
            (.object(["ok": .bool(true), "contract": .string("music.script.v3")]), 200, [:]),
            (.object(["ok": .bool(true), "contract": .string("music.script.v3"), "value": .null, "extra": .null]), 200, [:]),
            (.object(["ok": .bool(false), "contract": .string("music.script.v3"), "code": .string("provider secret")]), 200, [:]),
            (.object(["ok": .bool(false), "contract": .string("cat.script.v2"), "code": .string("NOT_FOUND")]), 200, [:])
        ]
        for (value, status, headers) in invalid {
            RuntimeTestURLProtocol.fixture.set { _, transport in transport.respond(value, status: status, headers: headers) }
            do { _ = try await client.call(module: .music, operation: "invoke"); XCTFail("invalid response") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .invalidResponse) }
        }
        for (contract, code, status) in [("music.script.v3", "SOURCE_NOT_FOUND", 200),
                                         ("runtime.http.v1", "PERMISSION_DENIED", 401)] {
            RuntimeTestURLProtocol.fixture.set { _, transport in
                transport.respond(.object(["ok": .bool(false), "contract": .string(contract), "code": .string(code)]), status: status)
            }
            do { _ = try await client.call(module: .music, operation: "invoke"); XCTFail("remote error") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote(code)) }
        }
        await client.close()
    }

    func testMalformedAndTooDeepHTTPBusinessValuesAreRejected() async throws {
        let client = try client()
        let prefix = #"{"ok":true,"contract":"music.script.v3","value":"#
        for body in [
            Data([0xff]),
            Data(#"{"ok":true,"ok":true,"contract":"music.script.v3","value":null}"#.utf8),
            Data((prefix + #"{"same":1,"\u0073ame":2}}"#).utf8),
            Data((prefix + "9007199254740992}").utf8),
            Data((prefix + #""\ud800"}"#).utf8),
            Data((prefix + String(repeating: "[", count: 65) + "null" + String(repeating: "]", count: 65) + "}").utf8)
        ] {
            RuntimeTestURLProtocol.fixture.set { _, transport in try! transport.respond(data: body) }
            do { _ = try await client.call(module: .music, operation: "invoke"); XCTFail("invalid wire") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .invalidResponse) }
        }
        await client.close()
    }

    func testCallerCancellationAndOwnerCloseCancelRealURLSessionTasks() async throws {
        let client = try client()
        let entered = expectation(description: "first HTTP task entered"), stopped = expectation(description: "cancelled HTTP task stopped")
        RuntimeTestURLProtocol.fixture.set { _, transport in entered.fulfill(); transport.onStop { stopped.fulfill() } }
        let pending = Task { try await client.call(module: .music, operation: "invoke") }
        await fulfillment(of: [entered], timeout: 2)
        pending.cancel()
        do { _ = try await pending.value; XCTFail("cancelled") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .cancelled) }
        await fulfillment(of: [stopped], timeout: 2)
        XCTAssertFalse(client.isClosed)
        RuntimeTestURLProtocol.fixture.set { _, transport in
            transport.respond(.object(["ok": .bool(true), "contract": .string("music.script.v3"), "value": .string("available")]))
        }
        let recovered = try await client.call(module: .music, operation: "invoke")
        XCTAssertEqual(recovered, .string("available"))
        let nextEntered = expectation(description: "second HTTP task entered"), nextStopped = expectation(description: "owner stopped task")
        RuntimeTestURLProtocol.fixture.set { _, transport in nextEntered.fulfill(); transport.onStop { nextStopped.fulfill() } }
        let next = Task { try await client.call(module: .cat, operation: "invoke") }
        await fulfillment(of: [nextEntered], timeout: 2)
        async let firstClose: Void = client.close()
        async let secondClose: Void = client.close()
        _ = await (firstClose, secondClose)
        await fulfillment(of: [nextStopped], timeout: 2)
        do { _ = try await next.value; XCTFail("closed") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        do { _ = try await client.describe(); XCTFail("closed") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        XCTAssertTrue(client.isClosed)
    }

    func testTimeoutDoesNotRetryAndPreCancelledCallsNeverSend() async throws {
        let client = try client()
        let called = expectation(description: "one transport attempt")
        called.assertForOverFulfill = true
        RuntimeTestURLProtocol.fixture.set { _, transport in called.fulfill(); transport.fail(NSURLErrorTimedOut) }
        do { _ = try await client.call(module: .music, operation: "invoke"); XCTFail("timeout") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .timeout) }
        await fulfillment(of: [called], timeout: 2)
        let gate = RuntimeSessionGate()
        let task = Task { await gate.wait(); return try await client.describe() }
        await gate.started(); task.cancel(); await gate.release()
        do { _ = try await task.value; XCTFail("cancelled") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .cancelled) }
        await client.close()
    }

    func testRejectsUntrustedEndpointsAndCredentials() throws {
        for address in ["https://127.0.0.1:1", "http://localhost:1", "http://127.0.0.1", "http://127.0.0.1:1/path",
                        "http://user@127.0.0.1:1", "http://127.0.0.1:1?key=value", "http://127.0.0.1:1#fragment"] {
            XCTAssertThrowsError(try RuntimeHTTPClient(baseURL: URL(string: address)!, sessionID: sessionID, requestKey: key))
        }
        XCTAssertThrowsError(try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:1")!, sessionID: "invalid", requestKey: key))
        XCTAssertThrowsError(try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:1")!, sessionID: sessionID, requestKey: "invalid"))
    }
}

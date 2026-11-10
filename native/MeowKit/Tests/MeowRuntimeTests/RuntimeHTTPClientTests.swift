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
        try startResponse(status: status, headers: headers)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }
    func startResponse(status: Int = 200, headers: [String: String] = [:]) throws {
        var fields = ["Content-Type": "application/json",
                      "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!]
        fields.merge(headers) { $1 }
        let response = try XCTUnwrap(HTTPURLResponse(url: request.url!, statusCode: status,
                                                    httpVersion: "HTTP/1.1", headerFields: fields))
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
    }
    func send(_ data: Data) { client?.urlProtocol(self, didLoad: data) }
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

    func testTMDBImagesUseRelativeIdentityAndCurrentSessionHeaders() async throws {
        let client = try client(), bytes = Data([137, 80, 78, 71])
        RuntimeTestURLProtocol.fixture.set { [sessionID, key] request, transport in
            XCTAssertEqual(request.url?.absoluteString, "http://127.0.0.1:43210/v1/tmdb/images/w500/poster.jpg")
            XCTAssertEqual(request.httpMethod, "GET")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer \(key)")
            XCTAssertEqual(request.value(forHTTPHeaderField: "X-Meow-Session"), sessionID)
            XCTAssertNotNil(UUID(uuidString: request.value(forHTTPHeaderField: "X-Meow-Request-Id") ?? ""))
            XCTAssertNil(request.url?.query); XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
            try! transport.respond(data: bytes, headers: ["Content-Type": "image/png", "Content-Length": "4"])
        }
        let relative = try XCTUnwrap(RuntimeHTTPClient.tmdbImageURL("/v1/tmdb/images/w500/poster.jpg"))
        XCTAssertNil(relative.scheme); XCTAssertNil(relative.baseURL)
        let actual = try await client.readTMDBImage(relative)
        XCTAssertEqual(actual, bytes)
        for path in ["https://example.invalid/v1/tmdb/images/w500/a.jpg", "//example.invalid/a.jpg",
                     "/v1/tmdb/images/w500/../a.jpg", "/v1/tmdb/images/w500/%2e%2e/a.jpg",
                     "/v1/tmdb/images/w500/a.jpg?key=x", "/v1/douban/images/a.jpg"] {
            XCTAssertNil(RuntimeHTTPClient.tmdbImageURL(path))
            do { _ = try await client.readTMDBImage(try XCTUnwrap(URL(string: path))); XCTFail(path) }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .invalidRequest) }
        }
        await client.close()
    }

    func testTMDBImageIdentitySurvivesRuntimePortAndSessionChange() async throws {
        let relative = try XCTUnwrap(RuntimeHTTPClient.tmdbImageURL("/v1/tmdb/images/w500/poster.jpg"))
        var requestIDs = Set<String>()
        for (port, id, key) in [(43210, sessionID, key),
                                (43212, "22222222-2222-4222-8222-222222222222", String(repeating: "b", count: 64))] {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.protocolClasses = [RuntimeTestURLProtocol.self]
            let client = try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:\(port)")!,
                sessionID: id, requestKey: key, configuration: configuration)
            let received = RuntimeImageRequestIdentity()
            RuntimeTestURLProtocol.fixture.set { request, transport in
                XCTAssertEqual(request.url?.port, port)
                XCTAssertEqual(request.url?.path, relative.absoluteString)
                XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer \(key)")
                XCTAssertEqual(request.value(forHTTPHeaderField: "X-Meow-Session"), id)
                received.set(request.value(forHTTPHeaderField: "X-Meow-Request-Id") ?? "")
                try! transport.respond(data: Data([137, 80]), headers: ["Content-Type": "image/png"])
            }
            _ = try await client.readTMDBImage(relative)
            XCTAssertTrue(requestIDs.insert(received.value).inserted)
            await client.close()
        }
        XCTAssertEqual(relative.absoluteString, "/v1/tmdb/images/w500/poster.jpg")
    }

    func testTMDBImageErrorsWrongIdentityTruncationAndBudgetNeverReturnBytes() async throws {
        let client = try client()
        let relative = try XCTUnwrap(RuntimeHTTPClient.tmdbImageURL("/v1/tmdb/images/w500/a.jpg"))
        let failure: RuntimeValue = .object(["ok": .bool(false), "contract": .string("tmdb.data.v1"), "code": .string("UPSTREAM_HTTP")])
        for mode in ["remote", "identity", "json", "redirect", "truncated", "length", "stream"] {
            RuntimeTestURLProtocol.fixture.set { _, transport in
                switch mode {
                case "remote": transport.respond(failure, status: 502)
                case "identity":
                    try! transport.respond(data: Data([1]), headers: ["Content-Type": "image/png", "X-Meow-Request-Id": UUID().uuidString])
                case "json": transport.respond(.object(["error": .string("not an image")]))
                case "redirect":
                    try! transport.respond(data: Data(), status: 302,
                        headers: ["Location": "https://external.invalid/poster.png"])
                case "truncated":
                    try! transport.startResponse(headers: ["Content-Type": "image/png", "Content-Length": "100"])
                    transport.send(Data([137, 80])); transport.fail(NSURLErrorNetworkConnectionLost)
                case "length":
                    // Complete the custom URLProtocol response so Foundation delivers
                    // its headers. One byte cannot trigger the body budget; the declared
                    // length must still be rejected before truncated-image validation.
                    try! transport.respond(data: Data([0]),
                        headers: ["Content-Type": "image/png", "Content-Length": "4194305"])
                default:
                    try! transport.startResponse(headers: ["Content-Type": "image/png"])
                    transport.send(Data(repeating: 0, count: 4 * 1_024 * 1_024 + 1))
                }
            }
            do { _ = try await client.readTMDBImage(relative); XCTFail(mode) }
            catch {
                if mode == "remote" { XCTAssertEqual(error as? RuntimeHTTPError, .remote("UPSTREAM_HTTP")) }
                else if mode == "length" || mode == "stream" {
                    XCTAssertEqual((error as? URLError)?.code, .dataLengthExceedsMaximum, mode)
                } else { XCTAssertEqual(error as? RuntimeHTTPError, .invalidResponse) }
            }
        }
        await client.close()
    }

    func testTMDBImageCancellationAndOwnerCloseStopActualURLSessionTasks() async throws {
        for ownerClose in [false, true] {
            let client = try client()
            let entered = expectation(description: "image request accepted"), stopped = expectation(description: "physical image read stopped")
            RuntimeTestURLProtocol.fixture.set { _, transport in
                transport.onStop { stopped.fulfill() }
                try! transport.startResponse(headers: ["Content-Type": "image/png"])
                transport.send(Data([137, 80])); entered.fulfill()
            }
            let read = Task { try await client.readTMDBImage(URL(string: "/v1/tmdb/images/w500/a.jpg")!) }
            await fulfillment(of: [entered], timeout: 3)
            if ownerClose { await client.close() } else { read.cancel() }
            do { _ = try await read.value; XCTFail("Pending bytes cannot succeed") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, ownerClose ? .closed : .cancelled) }
            await fulfillment(of: [stopped], timeout: 3)
            await client.close()
        }
    }

    func testAppDescribeRequiresExactFiveModuleVersionsContractsAndMethods() async throws {
        let client = try client()
        let modules: [RuntimeValue] = [
            .object(["id": .string("music"), "version": .string("0.8.0"), "contract": .string("music.script.v3")]),
            .object(["id": .string("cat"), "version": .string("0.2.0"), "contract": .string("cat.script.v2")]),
            .object(["id": .string("source"), "version": .string("0.2.0"), "contract": .string("source.runtime.v2")]),
            .object(["id": .string("tmdb"), "version": .string("0.1.0"), "contract": .string("tmdb.data.v1"),
                "methods": .array(["request", "settings.get", "settings.update"].map { .string("tmdb.data." + $0) })]),
            .object(["id": .string("netease"), "version": .string("0.1.0"), "contract": .string("netease.token.v1"),
                "methods": .array([.string("netease.token.get")])])
        ]
        var invalid = [Array(modules.prefix(3)), Array(modules.prefix(4)), modules + [modules[4]]]
        for (index, field, value) in [
            (3, "version", RuntimeValue.string("0.2.0")), (3, "methods", .array([])),
            (4, "version", .string("0.2.0")), (4, "contract", .string("music.script.v3")),
            (4, "methods", .array([])), (4, "methods", .array([.string("netease.token.get"), .string("netease.token.get")])),
            (4, "methods", .array([.string("netease.token.other")])), (4, "id", .string("music"))
        ] {
            var supplied = modules
            guard case .object(var fields) = supplied[index] else { return XCTFail("module fixture") }
            fields[field] = value; supplied[index] = .object(fields); invalid.append(supplied)
        }
        for (index, supplied) in ([modules] + invalid).enumerated() {
            RuntimeTestURLProtocol.fixture.set { _, transport in
                transport.respond(.object(["ok": .bool(true), "contract": .string("runtime.http.v1"), "value": .object([
                    "protocol": .string("runtime.http.v1"), "product": .string("aggregate"), "modules": .array(supplied)
                ])]))
            }
            do { try await client.validateAppModules(); XCTAssertEqual(index, 0) }
            catch { XCTAssertNotEqual(index, 0); XCTAssertEqual(error as? RuntimeHTTPError, .invalidResponse) }
        }
        await client.close()
    }

    func testNeteaseTokenRoutingHasEmptyInputAndSixtySecondBudgetWithOrdinaryRequestBudgetPreserved() async throws {
        let client = try client()
        RuntimeTestURLProtocol.fixture.set { request, transport in
            XCTAssertEqual(request.url?.path, "/v1/netease/get")
            XCTAssertEqual(request.httpMethod, "POST")
            XCTAssertEqual(request.timeoutInterval, 60)
            XCTAssertEqual(transport.body(), Data("{}".utf8))
            XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
            transport.respond(.object(["ok": .bool(true), "contract": .string("netease.token.v1"),
                                       "value": .object(["token": .string("synthetic-token")])]))
        }
        let value = try await client.call(module: .netease, operation: "get")
        XCTAssertEqual(value, .object(["token": .string("synthetic-token")]))
        RuntimeTestURLProtocol.fixture.set { request, transport in
            XCTAssertEqual(request.timeoutInterval, 30)
            transport.respond(.object(["ok": .bool(true), "contract": .string("music.script.v3"), "value": .null]))
        }
        _ = try await client.call(module: .music, operation: "invoke")
        RuntimeTestURLProtocol.fixture.set { _, _ in XCTFail("invalid token calls must not reach HTTP") }
        for (operation, parameters) in [("token.get", RuntimeValue.object([:])),
                                        ("get", .object(["cookie": .string("synthetic")])),
                                        ("get", .null)] {
            do { _ = try await client.call(module: .netease, operation: operation, parameters: parameters); XCTFail("invalid input") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .invalidRequest) }
        }
        await client.close()
    }

    func testNeteaseTokenEnvelopeAcceptsOnlyItsContractAndClosedErrorVocabulary() async throws {
        let client = try client()
        for code in ["INVALID_REQUEST", "CLOSED", "CANCELLED", "TIMEOUT", "NETWORK_ERROR",
                     "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "SDK_FAILURE", "SDK_CLEANUP"] {
            RuntimeTestURLProtocol.fixture.set { _, transport in
                transport.respond(.object(["ok": .bool(false), "contract": .string("netease.token.v1"), "code": .string(code)]))
            }
            do { _ = try await client.call(module: .netease, operation: "get"); XCTFail("remote failure") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote(code)) }
        }
        for response in [
            reply(.object(["token": .string("synthetic")])),
            .object(["ok": .bool(false), "contract": .string("netease.token.v1"), "code": .string("SCRIPT_FAILURE")]),
            .object(["ok": .bool(false), "contract": .string("netease.token.v1"), "code": .string("SDK_FAILURE"), "message": .string("unsafe")])
        ] {
            RuntimeTestURLProtocol.fixture.set { _, transport in transport.respond(response) }
            do { _ = try await client.call(module: .netease, operation: "get"); XCTFail("invalid envelope") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .invalidResponse) }
        }
        await client.close()
    }

    func testNeteaseTokenCancellationAndOwnerCloseStopTheActualHTTPTask() async throws {
        for ownerClose in [false, true] {
            let client = try client()
            let entered = expectation(description: "token request entered"), stopped = expectation(description: "token request stopped")
            RuntimeTestURLProtocol.fixture.set { _, transport in
                transport.onStop { stopped.fulfill() }; entered.fulfill()
            }
            let pending = Task { try await client.call(module: .netease, operation: "get") }
            await fulfillment(of: [entered], timeout: 2)
            if ownerClose { await client.close() } else { pending.cancel() }
            do { _ = try await pending.value; XCTFail("pending token cannot succeed") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, ownerClose ? .closed : .cancelled) }
            await fulfillment(of: [stopped], timeout: 2)
            await client.close()
        }
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

private final class RuntimeImageRequestIdentity: @unchecked Sendable {
    private let lock = NSLock()
    private var current = ""
    var value: String { lock.lock(); defer { lock.unlock() }; return current }
    func set(_ value: String) { lock.lock(); current = value; lock.unlock() }
}

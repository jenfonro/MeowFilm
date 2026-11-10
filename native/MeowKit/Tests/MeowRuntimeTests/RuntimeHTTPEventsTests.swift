import Foundation
import XCTest
@testable import MeowRuntime

private final class RuntimeEventsFixture: @unchecked Sendable {
    private let lock = NSLock()
    private var handler: (@Sendable (URLRequest, RuntimeEventsURLProtocol) -> Void)?
    func set(_ handler: @escaping @Sendable (URLRequest, RuntimeEventsURLProtocol) -> Void) {
        lock.lock(); self.handler = handler; lock.unlock()
    }
    func receive(_ request: URLRequest, _ transport: RuntimeEventsURLProtocol) {
        lock.lock(); let handler = handler; lock.unlock()
        handler?(request, transport)
    }
}

private final class RuntimeEventsURLProtocol: URLProtocol, @unchecked Sendable {
    static let fixture = RuntimeEventsFixture()
    private let lock = NSLock()
    private var stopped: (@Sendable () -> Void)?
    private var didStop = false
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() { Self.fixture.receive(request, self) }
    override func stopLoading() {
        lock.lock(); didStop = true; let callback = stopped; stopped = nil; lock.unlock()
        callback?()
    }
    func onStop(_ callback: @escaping @Sendable () -> Void) {
        lock.lock()
        if didStop { lock.unlock(); callback(); return }
        stopped = callback; lock.unlock()
    }
    func headers(status: Int = 200, type: String = "text/event-stream; charset=utf-8", id: String? = nil) {
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": type,
                           "X-Meow-Request-Id": id ?? request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
    }
    func send(_ data: Data) { client?.urlProtocol(self, didLoad: data) }
    func finish() { client?.urlProtocolDidFinishLoading(self) }
}

final class RuntimeHTTPEventsTests: XCTestCase {
    private let businessSession = UUID(uuidString: "22222222-2222-4222-8222-222222222222")!

    private func client() throws -> RuntimeHTTPClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [RuntimeEventsURLProtocol.self]
        return try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!,
            configuration: configuration)
    }

    private func snapshot(_ sequence: Int64, title: String = "中文🎬") -> RuntimeValue {
        .object(["sessionId": .string(businessSession.uuidString.lowercased()),
                 "sequence": .number(Double(sequence)), "target": .object(["title": .string(title)]),
                 "stage": .string("ready")])
    }

    private func frame(_ sequence: Int64, snapshot: RuntimeValue) throws -> Data {
        var bytes = Data("id: \(sequence)\nevent: snapshot\ndata: ".utf8)
        bytes.append(try RuntimeJSON.encode(snapshot)); bytes.append(contentsOf: [10, 10])
        return bytes
    }

    func testUnauthenticatedAfterAndUTF8FragmentsArriveBeforeConnectionCloses() async throws {
        let client = try client(), expected = snapshot(8), bytes = try frame(8, snapshot: expected)
        let stopped = expectation(description: "subscription physically stopped")
        RuntimeEventsURLProtocol.fixture.set { [businessSession] request, transport in
            XCTAssertEqual(request.httpMethod, "GET")
            XCTAssertEqual(request.url?.path, "/v1/film/events/\(businessSession.uuidString.lowercased())")
            XCTAssertEqual(request.url?.query, "after=7")
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
            XCTAssertNil(request.value(forHTTPHeaderField: "X-Meow-Session"))
            XCTAssertNotNil(UUID(uuidString: request.value(forHTTPHeaderField: "X-Meow-Request-Id") ?? ""))
            XCTAssertEqual(request.value(forHTTPHeaderField: "Accept"), "text/event-stream")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Accept-Encoding"), "identity")
            XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
            transport.onStop { stopped.fulfill() }
            transport.headers()
            for byte in bytes { transport.send(Data([byte])) }
            // Do not finish: a first snapshot must be observable on the open response.
        }
        let events = try client.events(moduleID: "film", sessionID: businessSession, after: 7)
        var iterator = events.makeAsyncIterator()
        let received = try await iterator.next()
        XCTAssertEqual(received, expected)
        await events.cancel()
        await fulfillment(of: [stopped], timeout: 2)
        do { _ = try await iterator.next(); XCTFail("cancelled observation cannot return buffered success") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .cancelled) }
        XCTAssertFalse(client.isClosed)
        RuntimeEventsURLProtocol.fixture.set { _, transport in
            transport.headers(type: "application/json")
            transport.send(Data(#"{"ok":true,"contract":"runtime.http.v1","value":{}}"#.utf8))
            transport.finish()
        }
        let described = try await client.describe()
        XCTAssertEqual(described, .object([:]))
        await client.close()
    }

    func testByteParserHandlesCRLFBOMCommentsMultilineDataAndMultipleFrames() throws {
        var parser = RuntimeHTTPEventParser(sessionID: businessSession)
        let first = snapshot(0), second = snapshot(1)
        let json = String(data: try RuntimeJSON.encode(first), encoding: .utf8)!
        let split = json.firstIndex(of: ",")!
        let multiline = "id: 0\r\nevent: snapshot\r\ndata: \(json[...split])\r\ndata: \(json[json.index(after: split)...])\r\n\r\n"
        var wire = Data([0xef, 0xbb, 0xbf])
        wire.append(Data((": " + String(repeating: "x", count: 100_000) + "\r\nunknown: ignored\r\n\r\n" + multiline).utf8))
        wire.append(try frame(1, snapshot: second))
        var values: [RuntimeValue] = []
        for byte in wire { if let value = try parser.feed(byte) { values.append(value) } }
        XCTAssertEqual(values, [first, second])
        XCTAssertFalse(parser.incomplete)
    }

    func testWholeSnapshotLargerThanArtworkBudgetIsNotTruncated() throws {
        let expected = snapshot(42, title: String(repeating: "x", count: 4 * 1024 * 1024 + 1) + "🎬")
        var parser = RuntimeHTTPEventParser(sessionID: businessSession), result: RuntimeValue?
        for byte in try frame(42, snapshot: expected) {
            if let value = try parser.feed(byte) { result = value }
        }
        XCTAssertEqual(result, expected)
    }

    func testSlowObserverRetainsOnlyNewestCompletedSnapshotAndDisconnectIsAnError() async throws {
        let client = try client()
        var wire = Data()
        for sequence in Int64(0)...Int64(99) { wire.append(try frame(sequence, snapshot: snapshot(sequence))) }
        let bytes = wire
        RuntimeEventsURLProtocol.fixture.set { _, transport in
            transport.headers(); transport.send(bytes); transport.finish()
        }
        let events = try client.events(moduleID: "film", sessionID: businessSession)
        await events.connection.waitForDrain()
        var iterator = events.makeAsyncIterator()
        let last = try await iterator.next()
        XCTAssertEqual(last, snapshot(99))
        do { _ = try await iterator.next(); XCTFail("disconnect cannot be normal completion") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        await client.close()
    }

    func testCancellationAndIteratorBreakReleaseOnlySubscriptionAndOwnerCloseDrainsParkedObserver() async throws {
        let client = try client(), bytes = try frame(1, snapshot: snapshot(1))
        let entered = expectation(description: "cancel observation entered"), stopped = expectation(description: "cancel reached transport")
        RuntimeEventsURLProtocol.fixture.set { _, transport in
            transport.onStop { stopped.fulfill() }; transport.headers(); entered.fulfill()
        }
        let events = try client.events(moduleID: "film", sessionID: businessSession)
        let waiting = Task { var iterator = events.makeAsyncIterator(); return try await iterator.next() }
        await fulfillment(of: [entered], timeout: 2)
        waiting.cancel()
        do { _ = try await waiting.value; XCTFail("cancel") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .cancelled) }
        await fulfillment(of: [stopped], timeout: 2)
        XCTAssertFalse(client.isClosed)

        let breakStopped = expectation(description: "break releases iterator even if sequence remains")
        RuntimeEventsURLProtocol.fixture.set { _, transport in
            transport.onStop { breakStopped.fulfill() }; transport.headers(); transport.send(bytes)
        }
        let nextEvents = try client.events(moduleID: "film", sessionID: businessSession)
        let one = Task { for try await value in nextEvents { return value }; return RuntimeValue.null }
        _ = try await one.value
        await fulfillment(of: [breakStopped], timeout: 2)
        await nextEvents.cancel()
        XCTAssertFalse(client.isClosed)

        let ownerEntered = expectation(description: "parked observer entered"), ownerStopped = expectation(description: "owner drained observer")
        RuntimeEventsURLProtocol.fixture.set { _, transport in
            transport.onStop { ownerStopped.fulfill() }; transport.headers(); transport.send(bytes); ownerEntered.fulfill()
        }
        let parked = try client.events(moduleID: "film", sessionID: businessSession)
        var iterator = parked.makeAsyncIterator()
        _ = try await iterator.next()
        await fulfillment(of: [ownerEntered], timeout: 2)
        await client.close()
        await fulfillment(of: [ownerStopped], timeout: 2)
        do { _ = try await iterator.next(); XCTFail("owner close retires parked observation") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        XCTAssertTrue(client.isClosed)
    }

    func testNeteaseCancellationKeepsFilmEventsAliveAndOwnerCloseDrainsBoth() async throws {
        let client = try client()
        let first = snapshot(1), second = snapshot(2)
        let firstFrame = try frame(1, snapshot: first), secondFrame = try frame(2, snapshot: second)
        let tokenEntered = expectation(description: "token request entered beside film observation")
        let tokenStopped = expectation(description: "token cancellation reached its transport")
        let eventsStopped = expectation(description: "owner drained film observation")
        RuntimeEventsURLProtocol.fixture.set { request, transport in
            XCTAssertEqual(request.httpMethod, "GET")
            XCTAssertEqual(request.timeoutInterval, 30)
            transport.onStop { eventsStopped.fulfill() }
            RuntimeEventsURLProtocol.fixture.set { request, token in
                XCTAssertEqual(request.url?.path, "/v1/netease/get")
                XCTAssertEqual(request.timeoutInterval, 60)
                token.onStop {
                    tokenStopped.fulfill()
                    transport.send(secondFrame)
                }
                tokenEntered.fulfill()
            }
            transport.headers(); transport.send(firstFrame)
        }
        let events = try client.events(moduleID: "film", sessionID: businessSession)
        var iterator = events.makeAsyncIterator()
        let receivedFirst = try await iterator.next()
        XCTAssertEqual(receivedFirst, first)
        let token = Task { try await client.call(module: .netease, operation: "get") }
        await fulfillment(of: [tokenEntered], timeout: 2)
        token.cancel()
        do { _ = try await token.value; XCTFail("cancelled token cannot succeed") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .cancelled) }
        await fulfillment(of: [tokenStopped], timeout: 2)
        let receivedSecond = try await iterator.next()
        XCTAssertEqual(receivedSecond, second)
        XCTAssertFalse(client.isClosed)

        let ownerTokenEntered = expectation(description: "new token request entered")
        let ownerTokenStopped = expectation(description: "owner drained token transport")
        RuntimeEventsURLProtocol.fixture.set { request, transport in
            XCTAssertEqual(request.url?.path, "/v1/netease/get")
            transport.onStop { ownerTokenStopped.fulfill() }
            ownerTokenEntered.fulfill()
        }
        let pendingToken = Task { try await client.call(module: .netease, operation: "get") }
        await fulfillment(of: [ownerTokenEntered], timeout: 2)
        await client.close()
        do { _ = try await pendingToken.value; XCTFail("owner close retires token request") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        do { _ = try await iterator.next(); XCTFail("owner close retires film observation") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        await fulfillment(of: [ownerTokenStopped, eventsStopped], timeout: 2)
        XCTAssertTrue(client.isClosed)
    }

    func testMalformedFramesAndTruncatedUTF8FailWithoutEchoingWire() throws {
        let identity = businessSession.uuidString.lowercased()
        for body in [
            "id: 0\nevent: snapshot\ndata: {}\n\n",
            "id: 0\nevent: snapshot\ndata: {\"sessionId\":\"\(identity)\",\"sequence\":1}\n\n",
            "id: 0\nevent: snapshot\ndata: {\"sessionId\":\"11111111-1111-4111-8111-111111111111\",\"sequence\":0}\n\n",
            "id: -1\nevent: snapshot\ndata: {}\n\n",
            "id: 9007199254740992\nevent: snapshot\ndata: {}\n\n",
            "id: 0\nevent: private-wire-text\ndata: {}\n\n",
            "id: 0\nid: 1\nevent: snapshot\ndata: {}\n\n",
            "id: 0\nevent: snapshot\ndata: {invalid-wire}\n\n"
        ] {
            var parser = RuntimeHTTPEventParser(sessionID: businessSession)
            XCTAssertThrowsError(try Data(body.utf8).forEach { _ = try parser.feed($0) }) {
                XCTAssertEqual($0 as? RuntimeHTTPError, .invalidResponse)
            }
        }
        for wire in [Data([0xff]), Data([0xc0, 0xaf]), Data([0xed, 0xa0, 0x80]), Data([0xf4, 0x90])] {
            var parser = RuntimeHTTPEventParser(sessionID: businessSession)
            XCTAssertThrowsError(try wire.forEach { _ = try parser.feed($0) })
        }
        var partial = RuntimeHTTPEventParser(sessionID: businessSession)
        _ = try partial.feed(0xe4)
        XCTAssertTrue(partial.incomplete)
    }

    func testWrongHTTPHeadersEmptyAndPartialStreamsAreExplicitFailures() async throws {
        let client = try client()
        for (status, type, id, body) in [
            (401, "text/event-stream", Optional<String>.none, Data()),
            (302, "text/event-stream", nil, Data()),
            (200, "application/json", nil, Data()),
            (200, "text/event-stream; charset=iso-8859-1", nil, Data()),
            (200, "text/event-stream", "wrong-id", Data()),
            (200, "text/event-stream", nil, Data()),
            (200, "text/event-stream", nil, Data("id: 0\nevent: snapshot\ndata: {".utf8))
        ] {
            RuntimeEventsURLProtocol.fixture.set { _, transport in
                transport.headers(status: status, type: type, id: id); transport.send(body); transport.finish()
            }
            let events = try client.events(moduleID: "film", sessionID: businessSession)
            var iterator = events.makeAsyncIterator()
            do { _ = try await iterator.next(); XCTFail("invalid response") }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .invalidResponse) }
        }
        await client.close()
    }

    func testRouteComponentsAndSequenceCannotInjectOrSendInvalidRequests() async throws {
        let client = try client()
        for module in ["", "../film", "film?key=value", "FILM", "film/events"] {
            XCTAssertThrowsError(try client.events(moduleID: module, sessionID: businessSession)) {
                XCTAssertEqual($0 as? RuntimeHTTPError, .invalidRequest)
            }
        }
        for sequence in [Int64(-1), 9_007_199_254_740_992] {
            XCTAssertThrowsError(try client.events(moduleID: "film", sessionID: businessSession, after: sequence))
        }
        await client.close()
        XCTAssertThrowsError(try client.events(moduleID: "film", sessionID: businessSession)) {
            XCTAssertEqual($0 as? RuntimeHTTPError, .closed)
        }
    }
}

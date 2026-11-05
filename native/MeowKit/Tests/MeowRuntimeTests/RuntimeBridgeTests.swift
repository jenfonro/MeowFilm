import Foundation
import Darwin
import XCTest
import MeowJSON
@testable import MeowRuntime

/// The far end of a socketpair speaking raw protocol-1 frames, as the JS peer does.
final class RuntimeBridgeRawPeer: @unchecked Sendable {
    let descriptor: Int32
    let session: String
    init(descriptor: Int32, session: String) { self.descriptor = descriptor; self.session = session }

    func write(_ object: [String: Any]) {
        let body = try! JSONSerialization.data(withJSONObject: object)
        writeBody(body)
    }
    func writeBody(_ body: Data) {
        var length = UInt32(body.count).bigEndian
        var frame = Data(bytes: &length, count: 4); frame.append(body)
        frame.withUnsafeBytes { raw in
            var offset = 0
            while offset < raw.count {
                let count = Darwin.write(descriptor, raw.baseAddress! + offset, raw.count - offset)
                guard count > 0 else { return }
                offset += count
            }
        }
    }
    /// Blocking read of one frame; nil at EOF.
    func read() -> [String: Any]? {
        func exactly(_ count: Int) -> Data? {
            var data = Data(count: count), offset = 0
            let ok = data.withUnsafeMutableBytes { raw -> Bool in
                while offset < count {
                    let result = Darwin.read(descriptor, raw.baseAddress! + offset, count - offset)
                    guard result > 0 else { return false }
                    offset += result
                }
                return true
            }
            return ok ? data : nil
        }
        guard let header = exactly(4) else { return nil }
        let length = header.reduce(0) { $0 << 8 | Int($1) }
        guard let body = exactly(length) else { return nil }
        return try? JSONSerialization.jsonObject(with: body) as? [String: Any]
    }
    /// The blocking read off the cooperative pool, for async tests.
    func next() async -> [String: Any]? {
        await withCheckedContinuation { continuation in
            DispatchQueue.global().async { continuation.resume(returning: self.read()) }
        }
    }
    func expect(file: StaticString = #filePath, line: UInt = #line) async throws -> [String: Any] {
        let value = await next()
        return try XCTUnwrap(value, file: file, line: line)
    }
    func request(_ id: Int, _ method: String, _ params: Any = NSNull()) {
        write(["version": 1, "session": session, "id": id, "kind": "request", "method": method, "params": params])
    }
    func result(_ id: Int, _ value: Any) {
        write(["version": 1, "session": session, "id": id, "kind": "result", "value": value])
    }
    func close() { _ = Darwin.close(descriptor) }
}

func runtimeBridgePair() throws -> (Int32, Int32) {
    var descriptors: [Int32] = [-1, -1]
    guard socketpair(AF_UNIX, SOCK_STREAM, 0, &descriptors) == 0 else { throw RuntimeBridgeError.closed }
    var enabled: Int32 = 1
    _ = setsockopt(descriptors[1], SOL_SOCKET, SO_NOSIGPIPE, &enabled, socklen_t(MemoryLayout<Int32>.size))
    return (descriptors[0], descriptors[1])
}

final class RuntimeBridgeFrameTests: XCTestCase {
    func testEnvelopeRoundTripKeepsEachKindExact() throws {
        for kind: RuntimeBridgeFrame.Kind in [
            .request(method: "music.script.status", params: .object(["text": .string("中文🎵")])),
            .result(.array([.null, .bool(true), .number(9_007_199_254_740_991)])),
            .error(.methodUnavailable), .cancel,
        ] {
            let frame = RuntimeBridgeFrame(session: "11111111-1111-4111-8111-111111111111", id: 7, kind: kind)
            let data = try frame.encoded()
            let length = data.prefix(4).reduce(0) { $0 << 8 | Int($1) }
            XCTAssertEqual(length, data.count - 4)
            XCTAssertEqual(try RuntimeBridgeFrame.decode(body: data.dropFirst(4)), frame)
        }
    }

    func testDecodingRejectsSchemaDriftDuplicatesAndUnsafeValues() {
        let base = #""version":1,"session":"s","id":1"#
        for text in [
            "{\(base),\"kind\":\"result\"}", "{\(base),\"kind\":\"result\",\"value\":1,\"extra\":1}",
            "{\(base),\"kind\":\"error\",\"code\":\"PROVIDER_TEXT\"}", "{\(base),\"kind\":\"request\",\"params\":null}",
            "{\(base),\"kind\":\"request\",\"method\":\"9bad\",\"params\":null}",
            #"{"version":2,"session":"s","id":1,"kind":"cancel"}"#, #"{"version":1,"session":"s","id":0,"kind":"cancel"}"#,
            #"{"version":1,"session":"s","id":1.5,"kind":"cancel"}"#, #"{"version":1,"session":"bad session","id":1,"kind":"cancel"}"#,
            "{\(base),\"kind\":\"cancel\",\"kind\":\"cancel\"}", "{\(base),\"kind\":\"result\",\"value\":{\"é\":1}}",
            "{\(base),\"kind\":\"result\",\"value\":9007199254740992}", "{\(base),\"kind\":\"unknown\"}",
        ] {
            XCTAssertThrowsError(try RuntimeBridgeFrame.decode(body: Data(text.utf8)), text)
        }
        XCTAssertThrowsError(try RuntimeBridgeFrame.decode(body: Data([0xff])))
        XCTAssertNoThrow(try RuntimeBridgeFrame.decode(body: Data([0xef, 0xbb, 0xbf] + Array("{\(base),\"kind\":\"cancel\"}".utf8))))
    }

    func testEncodingEnforcesThePhysicalFrameBoundAndIdentity() {
        let big = RuntimeBridgeFrame(session: "s", id: 1, kind: .result(.string(String(repeating: "x", count: 1024 * 1024))))
        XCTAssertThrowsError(try big.encoded()) { XCTAssertEqual($0 as? RuntimeBridgeError, .limit) }
        XCTAssertThrowsError(try RuntimeBridgeFrame(session: "", id: 1, kind: .cancel).encoded())
        XCTAssertThrowsError(try RuntimeBridgeFrame(session: "s", id: 0, kind: .cancel).encoded())
        XCTAssertThrowsError(try RuntimeBridgeFrame(session: "s", id: 1, kind: .result(.object(["é": .null]))).encoded())
    }
}

final class RuntimeBridgeConnectionTests: XCTestCase {
    func testConcurrentCallsRemainMonotonicOnTheWire() async throws {
        let (a, b) = try runtimeBridgePair()
        let peer = RuntimeBridgeRawPeer(descriptor: b, session: "concurrent")
        let connection = try RuntimeBridgeConnection(descriptor: a, session: "concurrent")
        defer { connection.close(); peer.close() }
        let responses = Task {
            var lastID = 0
            for _ in 0..<40 {
                let request = try await peer.expect()
                let id = try XCTUnwrap(request["id"] as? Int)
                XCTAssertGreaterThan(id, lastID)
                lastID = id
                peer.result(id, request["params"]!)
            }
        }
        try await withThrowingTaskGroup(of: Void.self) { group in
            for index in 0..<40 {
                group.addTask {
                    let text = String(repeating: "x", count: index.isMultiple(of: 2) ? 50000 : 1)
                    let value = try await connection.call("echo", .string(text))
                    XCTAssertEqual(value, .string(text))
                }
            }
            try await group.waitForAll()
        }
        try await responses.value
    }
    private let session = "22222222-2222-4222-8222-222222222222"

    private func connect(_ handlers: [String: RuntimeBridgeConnection.Handler] = [:],
                         timeout: TimeInterval = 30) throws -> (RuntimeBridgeConnection, RuntimeBridgeRawPeer) {
        let (native, remote) = try runtimeBridgePair()
        let connection = try RuntimeBridgeConnection(descriptor: native, session: session, handlers: handlers, timeout: timeout)
        let peer = RuntimeBridgeRawPeer(descriptor: remote, session: session)
        addTeardownBlock { connection.close(); peer.close() }
        return (connection, peer)
    }

    func testOutboundCallsAreMatchedByIDInAnyOrder() async throws {
        let (connection, peer) = try connect()
        async let first = connection.call("probe.first", .string("a"))
        async let second = connection.call("probe.second", .string("b"))
        var requests: [[String: Any]] = []
        for _ in 0..<2 { requests.append(try await peer.expect()) }
        for request in requests.reversed() {
            peer.result(request["id"] as! Int, ["echo": request["params"] as! String])
        }
        let values = try await [first, second]
        XCTAssertEqual(values, [.object(["echo": .string("a")]), .object(["echo": .string("b")])])
        XCTAssertEqual(Set(requests.map { $0["method"] as! String }), ["probe.first", "probe.second"])
    }

    func testCancellingASentCallSendsOneCancelAndRejectsTheLateResult() async throws {
        let (connection, peer) = try connect()
        let task = Task { try await connection.call("probe.wait") }
        let request = try await peer.expect()
        task.cancel()
        do { _ = try await task.value; XCTFail("cancelled") }
        catch { XCTAssertEqual(error as? RuntimeBridgeError, .cancelled) }
        let cancel = try await peer.expect()
        XCTAssertEqual(cancel["kind"] as? String, "cancel")
        XCTAssertEqual(cancel["id"] as? Int, request["id"] as? Int)
        peer.result(request["id"] as! Int, "late")
        // The connection stays usable; the late value had no receiver.
        async let next = connection.call("probe.next")
        let following = try await peer.expect()
        peer.result(following["id"] as! Int, "next")
        let value = try await next
        XCTAssertEqual(value, .string("next"))
    }

    func testTimeoutFailsTheCallAndCancelsTheRemoteWork() async throws {
        let (connection, peer) = try connect()
        let task = Task { try await connection.call("probe.slow", .null, timeout: 0.2) }
        let request = try await peer.expect()
        do { _ = try await task.value; XCTFail("timeout") }
        catch { XCTAssertEqual(error as? RuntimeBridgeError, .timeout) }
        let cancel = try await peer.expect()
        XCTAssertEqual(cancel["kind"] as? String, "cancel")
        XCTAssertEqual(cancel["id"] as? Int, request["id"] as? Int)
    }

    func testIncomingRequestsUseTheRegisteredTableAndUnknownMethodsAreExplicit() async throws {
        let (_, peer) = try connect(["platform.echo": { $0 }])
        peer.request(1, "platform.echo", ["value": 1])
        let reply = try await peer.expect()
        XCTAssertEqual(reply["kind"] as? String, "result")
        XCTAssertEqual(reply["id"] as? Int, 1)
        XCTAssertEqual(reply["value"] as? [String: Int], ["value": 1])
        peer.request(2, "platform.missing")
        let missing = try await peer.expect()
        XCTAssertEqual(missing["kind"] as? String, "error")
        XCTAssertEqual(missing["code"] as? String, "METHOD_UNAVAILABLE")
    }

    func testRemoteCancelCancelsTheHandlerAndRepliesCancelledOnce() async throws {
        let entered = expectation(description: "handler entered")
        let (_, peer) = try connect(["platform.wait": { _ in
            entered.fulfill()
            try await Task.sleep(nanoseconds: 30_000_000_000)
            return .null
        }])
        peer.request(1, "platform.wait")
        await fulfillment(of: [entered], timeout: 5)
        peer.write(["version": 1, "session": session, "id": 1, "kind": "cancel"])
        let reply = try await peer.expect()
        XCTAssertEqual(reply["kind"] as? String, "error")
        XCTAssertEqual(reply["code"] as? String, "CANCELLED")
    }

    func testProtocolViolationsCloseTheConnectionAndFailPendingCalls() async throws {
        for violation in ["session", "replay", "garbage", "oversized"] {
            let (connection, peer) = try connect(["platform.echo": { $0 }])
            let closed = expectation(description: violation)
            connection.onClose { closed.fulfill() }
            let pending = Task { try await connection.call("probe.pending") }
            _ = try await peer.expect()
            switch violation {
            case "session": peer.write(["version": 1, "session": "other", "id": 1, "kind": "cancel"])
            case "replay":
                peer.request(5, "platform.echo", 1); _ = await peer.next()
                peer.request(5, "platform.echo", 1)
            case "garbage": peer.writeBody(Data("{".utf8))
            default:
                var length = UInt32(1024 * 1024 + 1).bigEndian
                _ = Darwin.write(peer.descriptor, &length, 4)
            }
            await fulfillment(of: [closed], timeout: 5)
            XCTAssertTrue(connection.isClosed)
            do { _ = try await pending.value; XCTFail(violation) }
            catch { XCTAssertEqual(error as? RuntimeBridgeError, .closed, violation) }
            do { _ = try await connection.call("probe.after"); XCTFail(violation) }
            catch { XCTAssertEqual(error as? RuntimeBridgeError, .closed, violation) }
        }
    }

    func testPeerEOFClosesOnceAndLateObserversAreNotified() async throws {
        let (connection, peer) = try connect()
        let closed = expectation(description: "closed"); closed.expectedFulfillmentCount = 1
        connection.onClose { closed.fulfill() }
        peer.close()
        await fulfillment(of: [closed], timeout: 5)
        let late = expectation(description: "late observer")
        connection.onClose { late.fulfill() }
        await fulfillment(of: [late], timeout: 1)
        connection.close()
    }
}

final class RuntimeEmbeddedLauncherTests: XCTestCase {
    func testPayloadRequiresACompleteIOSManifestAndARegularEntry() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("app/ios/entries"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try Data().write(to: root.appendingPathComponent("app/ios/entries/aggregate.mjs"))
        func manifest(_ fields: [String: Any]) throws {
            var value: [String: Any] = ["schemaVersion": 1, "status": "complete", "product": "aggregate",
                                        "entry": "app/ios/entries/aggregate.mjs", "platform": "ios",
                                        "transport": "ios-socketpair-v1"]
            value.merge(fields) { $1 }
            try JSONSerialization.data(withJSONObject: value).write(to: root.appendingPathComponent("runtime-manifest.json"))
        }
        try manifest([:])
        XCTAssertEqual(try RuntimePayload(root: root).entry.lastPathComponent, "aggregate.mjs")
        for fields: [String: Any] in [["status": "platform-pending"], ["platform": "win32"],
                                      ["transport": "windows-named-pipe-v1"], ["entry": "../outside.mjs"],
                                      ["entry": "/tmp/aggregate.mjs"], ["entry": "app/ios/entries/missing.mjs"],
                                      ["entry": "app/ios/entries"], ["schemaVersion": 2]] {
            try manifest(fields)
            XCTAssertThrowsError(try RuntimePayload(root: root), "\(fields)") {
                XCTAssertEqual($0 as? RuntimeLaunchError, .payloadUnavailable)
            }
        }
    }

    func testLauncherStartsOnceOverASocketpairAndAFinishedRuntimeIsFinal() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("app"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try Data().write(to: root.appendingPathComponent("app/entry.mjs"))
        try JSONSerialization.data(withJSONObject: ["schemaVersion": 1, "status": "complete", "product": "aggregate",
            "entry": "app/entry.mjs", "platform": "ios", "transport": "ios-socketpair-v1"])
            .write(to: root.appendingPathComponent("runtime-manifest.json"))
        let recorded = RuntimeLaunchRecord()
        let launcher = RuntimeEmbeddedLauncher(payload: { try RuntimePayload(root: root) },
            dataDirectory: root.appendingPathComponent("data"), start: { arguments in
                recorded.set(arguments)
                // A synthetic runtime: answer describe on the received descriptor, then exit.
                let descriptor = Int32(arguments[3])!
                let peer = RuntimeBridgeRawPeer(descriptor: descriptor, session: arguments[5])
                if let request = peer.read() { peer.result(request["id"] as! Int, ["product": "aggregate"]) }
                _ = peer.read()
                peer.close()
                return 0
            })
        let connection = try await launcher.connect(handlers: [:])
        let closed = expectation(description: "runtime exit closes the connection")
        connection.onClose { closed.fulfill() }
        let described = try await connection.call("runtime.describe")
        XCTAssertEqual(described, .object(["product": .string("aggregate")]))
        let arguments = try XCTUnwrap(recorded.get())
        XCTAssertEqual(arguments.count, 8)
        XCTAssertEqual(Array(arguments.prefix(3)), ["node", root.appendingPathComponent("app/entry.mjs").path, "--meow-fd"])
        XCTAssertEqual(arguments[4], "--meow-session"); XCTAssertEqual(arguments[5], connection.session)
        XCTAssertEqual(arguments[6], "--meow-data"); XCTAssertEqual(arguments[7], root.appendingPathComponent("data").path)
        connection.close()
        await fulfillment(of: [closed], timeout: 5)
        do { _ = try await launcher.connect(handlers: [:]); XCTFail("second start") }
        catch { XCTAssertEqual(error as? RuntimeLaunchError, .alreadyStarted) }
    }

    func testCArgumentsAreContiguousWritableAndTerminated() {
        RuntimeEmbeddedLauncher.withCArguments(["node", "entry.mjs", "中文"]) { count, argv in
            XCTAssertEqual(count, 3)
            XCTAssertEqual(String(cString: argv[0]!), "node")
            XCTAssertEqual(String(cString: argv[2]!), "中文")
            XCTAssertEqual(argv[1]!, argv[0]! + 5)
            XCTAssertNil(argv[3])
            argv[0]![0] = 78 // Writable storage, as node_start requires.
            XCTAssertEqual(String(cString: argv[0]!), "Node")
        }
    }
}

final class RuntimeLaunchRecord: @unchecked Sendable {
    private let lock = NSLock()
    private var value: [String]?
    func set(_ arguments: [String]) { lock.lock(); value = arguments; lock.unlock() }
    func get() -> [String]? { lock.lock(); defer { lock.unlock() }; return value }
}

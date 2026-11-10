import Foundation
import Darwin
import XCTest
import MeowRuntime

final class RuntimeLaunchRecord: @unchecked Sendable {
    private let lock = NSLock()
    private var value: [String]?
    func set(_ value: [String]) { lock.lock(); self.value = value; lock.unlock() }
    func get() -> [String]? { lock.lock(); defer { lock.unlock() }; return value }
}

private struct RuntimeBootstrapPeer {
    let descriptor: Int32
    init(duplicating descriptor: Int32) {
        self.descriptor = Darwin.dup(descriptor)
        XCTAssertGreaterThanOrEqual(self.descriptor, 0)
        XCTAssertNotEqual(self.descriptor, descriptor)
    }

    func readLine() -> Data? {
        var data = Data(), byte: UInt8 = 0
        while true {
            let count = Darwin.read(descriptor, &byte, 1)
            if count < 0, errno == EINTR { continue }
            guard count > 0 else { return nil }
            if byte == 10 { return data }
            data.append(byte)
        }
    }
    func endpoint(_ address: String = "http://127.0.0.1:43210") {
        let data = try! RuntimeJSON.encode(.object(["baseUrl": .string(address)])) + Data([10])
        data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = Darwin.write(descriptor, bytes.baseAddress! + offset, bytes.count - offset)
                if count < 0, errno == EINTR { continue }
                guard count > 0 else { return }
                offset += count
            }
        }
    }
    func close() { _ = Darwin.close(descriptor) }
}

final class RuntimeEmbeddedLauncherTests: XCTestCase {
    private func payload() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("app/ios/entries"), withIntermediateDirectories: true)
        try Data().write(to: root.appendingPathComponent("app/ios/entries/aggregate.mjs"))
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        try manifest(root)
        return root
    }
    private func manifest(_ root: URL, changes: [String: Any] = [:]) throws {
        var value: [String: Any] = ["schemaVersion": 1, "status": "complete", "product": "aggregate",
            "entry": "app/ios/entries/aggregate.mjs", "platform": "ios", "transport": "runtime.http.v1",
            "assemblyProfile": "common-native", "modules": ["music", "cat", "source", "tmdb", "netease"]]
        value.merge(changes) { $1 }
        try JSONSerialization.data(withJSONObject: value).write(to: root.appendingPathComponent("runtime-manifest.json"))
    }

    func testSessionCloseWaitsForNodeStartReturnAfterNativeEOF() async throws {
        let root = try payload()
        let eof = expectation(description: "executor observed owner EOF")
        let permitExit = DispatchSemaphore(value: 0), returned = RuntimeLaunchRecord(), recorded = RuntimeLaunchRecord()
        defer { permitExit.signal() }
        let launcher = RuntimeEmbeddedLauncher(payload: { try RuntimePayload(root: root) }, dataDirectory: root,
            start: { arguments in
                recorded.set(arguments)
                let original = Int32(arguments[3])!
                let peer = RuntimeBootstrapPeer(duplicating: original)
                XCTAssertNotNil(peer.readLine())
                peer.endpoint()
                XCTAssertNil(peer.readLine())
                peer.close()
                XCTAssertGreaterThanOrEqual(Darwin.fcntl(original, F_GETFD), 0)
                eof.fulfill()
                XCTAssertEqual(permitExit.wait(timeout: .now() + 5), .success)
                returned.set(["node_start returned"])
                return 0
            })
        let session = RuntimeSession(launcher: launcher)
        let client = try await session.connection()
        let completion = RuntimeLaunchRecord()
        let closing = Task { await session.close(); completion.set(["closed"]) }
        await fulfillment(of: [eof], timeout: 2)
        XCTAssertTrue(client.isClosed)
        XCTAssertNil(returned.get())
        XCTAssertNil(completion.get())
        permitExit.signal()
        await closing.value
        XCTAssertEqual(returned.get(), ["node_start returned"])
        XCTAssertEqual(completion.get(), ["closed"])
        await launcher.waitForExit()
        await session.close()
        let arguments = try XCTUnwrap(recorded.get())
        let status = Darwin.fcntl(Int32(arguments[3])!, F_GETFD), failure = errno
        XCTAssertEqual(status, -1)
        XCTAssertEqual(failure, EBADF)
    }

    func testLauncherTransfersConfigurationPrivatelyAndStartsOnlyOnce() async throws {
        let root = try payload(), recorded = RuntimeLaunchRecord()
        let launcher = RuntimeEmbeddedLauncher(payload: { try RuntimePayload(root: root) },
            dataDirectory: root.appendingPathComponent("script-runtime"), start: { arguments in
                recorded.set(arguments)
                XCTAssertEqual(arguments.count, 4)
                XCTAssertEqual(arguments[2], "--meow-bootstrap-fd")
                let peer = RuntimeBootstrapPeer(duplicating: Int32(arguments[3])!)
                guard let bytes = peer.readLine(), case .object(let configuration) = try? RuntimeJSON.decode(bytes) else {
                    peer.close(); return 1
                }
                XCTAssertEqual(configuration.count, 3)
                guard case .string(let sessionID)? = configuration["sessionId"],
                      case .string(let key)? = configuration["requestKey"] else { peer.close(); return 1 }
                XCTAssertNotNil(UUID(uuidString: sessionID))
                XCTAssertEqual(key.utf8.count, 64)
                XCTAssertTrue(key.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) })
                XCTAssertFalse(arguments.contains(sessionID)); XCTAssertFalse(arguments.contains(key))
                XCTAssertEqual(configuration["dataRoot"], .string(root.appendingPathComponent("script-runtime").path))
                peer.endpoint()
                XCTAssertNil(peer.readLine())
                peer.close()
                return 0
            })
        let client = try await launcher.connect()
        let arguments = try XCTUnwrap(recorded.get())
        XCTAssertEqual(Array(arguments.prefix(3)), ["node", root.appendingPathComponent("app/ios/entries/aggregate.mjs").path, "--meow-bootstrap-fd"])
        do { _ = try await launcher.connect(); XCTFail("second start") }
        catch { XCTAssertEqual(error as? RuntimeLaunchError, .alreadyStarted) }
        await launcher.close()
        XCTAssertTrue(client.isClosed)
        do { _ = try await launcher.connect(); XCTFail("terminal") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
    }

    func testExecutorReturnBeforeEndpointFailsStartupAndIsNeverRestarted() async throws {
        let root = try payload()
        let launcher = RuntimeEmbeddedLauncher(payload: { try RuntimePayload(root: root) }, dataDirectory: root,
            start: { arguments in
                let peer = RuntimeBootstrapPeer(duplicating: Int32(arguments[3])!)
                _ = peer.readLine()
                peer.close()
                return 1
            })
        do { _ = try await launcher.connect(); XCTFail("early executor exit") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        await launcher.close()
        do { _ = try await launcher.connect(); XCTFail("restart") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
    }

    func testExecutorReturnWithoutTakingBootstrapDescriptorClosesItAndNeverRestarts() async throws {
        let root = try payload(), recorded = RuntimeLaunchRecord()
        let launcher = RuntimeEmbeddedLauncher(payload: { try RuntimePayload(root: root) }, dataDirectory: root,
            start: { arguments in
                recorded.set(arguments)
                return 1
            })
        do { _ = try await launcher.connect(); XCTFail("unclaimed descriptor") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        await launcher.waitForExit()
        await launcher.close()
        let arguments = try XCTUnwrap(recorded.get())
        let status = Darwin.fcntl(Int32(arguments[3])!, F_GETFD), failure = errno
        XCTAssertEqual(status, -1)
        XCTAssertEqual(failure, EBADF)
        await launcher.close()
        do { _ = try await launcher.connect(); XCTFail("restart") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
    }

    func testReusedDescriptorsSurviveExecutorReturnAndRepeatedClose() async throws {
        let root = try payload()
        var descriptors: [Int32] = [-1, -1]
        guard socketpair(AF_UNIX, SOCK_STREAM, 0, &descriptors) == 0 else { XCTFail("sentinel socketpair"); return }
        let sentinel = descriptors[0], sender = descriptors[1]
        defer { _ = Darwin.close(sentinel); _ = Darwin.close(sender) }
        var enabled: Int32 = 1
        _ = setsockopt(sender, SOL_SOCKET, SO_NOSIGPIPE, &enabled, socklen_t(MemoryLayout<Int32>.size))
        let reused = expectation(description: "peer fd atomically replaced by sentinel")
        let permitExit = DispatchSemaphore(value: 0), recorded = RuntimeLaunchRecord()
        defer { permitExit.signal() }
        let launcher = RuntimeEmbeddedLauncher(payload: { try RuntimePayload(root: root) }, dataDirectory: root,
            start: { arguments in
                let original = Int32(arguments[3])!
                let peer = RuntimeBootstrapPeer(duplicating: original)
                XCTAssertNotNil(peer.readLine())
                peer.endpoint()
                XCTAssertNil(peer.readLine())
                // dup2 releases only the copied endpoint and atomically reuses
                // its number. The fixture never closes the original endpoint.
                guard Darwin.dup2(sentinel, peer.descriptor) == peer.descriptor else {
                    peer.close(); XCTFail("reuse peer fd"); return 1
                }
                recorded.set([String(original), String(peer.descriptor)])
                XCTAssertGreaterThanOrEqual(Darwin.fcntl(original, F_GETFD), 0)
                reused.fulfill()
                XCTAssertEqual(permitExit.wait(timeout: .now() + 5), .success)
                return 0
            })
        let session = RuntimeSession(launcher: launcher)
        let client = try await session.connection()
        let completion = RuntimeLaunchRecord()
        let closing = Task { await session.close(); completion.set(["closed"]) }
        await fulfillment(of: [reused], timeout: 2)
        let numbers = try XCTUnwrap(recorded.get())
        let original = Int32(numbers[0])!, copy = Int32(numbers[1])!
        defer { _ = Darwin.close(copy) }
        XCTAssertTrue(client.isClosed)
        XCTAssertNil(completion.get())
        assertSentinel(descriptor: copy, sender: sender, byte: 1)
        permitExit.signal()
        await closing.value
        assertSentinel(descriptor: copy, sender: sender, byte: 2)
        // Allocate the released number atomically, without overwriting any
        // descriptor another thread may have opened in the meantime.
        let reusedOriginal = Darwin.fcntl(sentinel, F_DUPFD, original)
        guard reusedOriginal >= 0 else { XCTFail("reuse original fd"); return }
        defer { _ = Darwin.close(reusedOriginal) }
        XCTAssertEqual(reusedOriginal, original)
        await launcher.close()
        await session.close()
        assertSentinel(descriptor: copy, sender: sender, byte: 3)
        assertSentinel(descriptor: reusedOriginal, sender: sender, byte: 4)
    }

    private func assertSentinel(descriptor: Int32, sender: Int32, byte: UInt8,
                                file: StaticString = #filePath, line: UInt = #line) {
        var sent = byte, received: UInt8 = 0
        XCTAssertEqual(Darwin.send(sender, &sent, 1, 0), 1, file: file, line: line)
        XCTAssertEqual(Darwin.recv(descriptor, &received, 1, MSG_DONTWAIT), 1, file: file, line: line)
        XCTAssertEqual(received, byte, file: file, line: line)
    }

    func testUnexpectedExecutorReturnClosesTheSharedClientAndNeverRestarts() async throws {
        let root = try payload(), permitExit = DispatchSemaphore(value: 0)
        let launcher = RuntimeEmbeddedLauncher(payload: { try RuntimePayload(root: root) }, dataDirectory: root,
            start: { arguments in
                let peer = RuntimeBootstrapPeer(duplicating: Int32(arguments[3])!)
                XCTAssertNotNil(peer.readLine())
                peer.endpoint()
                XCTAssertEqual(permitExit.wait(timeout: .now() + 5), .success)
                peer.close()
                return 1
            })
        let session = RuntimeSession(launcher: launcher)
        let client = try await session.connection()
        XCTAssertFalse(client.isClosed)
        permitExit.signal()
        await launcher.waitForExit()
        XCTAssertTrue(client.isClosed)
        do { _ = try await session.connection(); XCTFail("executor exit is final") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
        await session.close()
    }

    func testPayloadRequiresTheCompleteFiveModuleHTTPAssembly() throws {
        let root = try payload()
        XCTAssertEqual(try RuntimePayload(root: root).entry.lastPathComponent, "aggregate.mjs")
        for fields: [String: Any] in [
            ["status": "platform-pending"], ["platform": "win32"], ["transport": "other"],
            ["assemblyProfile": "other"], ["modules": ["music", "cat"]],
            ["modules": ["music", "cat", "source"]], ["modules": ["music", "cat", "source", "tmdb"]],
            ["modules": ["music", "cat", "source", "tmdb", "douban"]],
            ["modules": ["music", "cat", "source", "tmdb", "netease", "douban"]],
            ["entry": "../outside.mjs"], ["entry": "/tmp/aggregate.mjs"],
            ["entry": "app/ios/entries/missing.mjs"], ["entry": "app/ios/entries"], ["schemaVersion": 2]
        ] {
            try manifest(root, changes: fields)
            XCTAssertThrowsError(try RuntimePayload(root: root)) {
                XCTAssertEqual($0 as? RuntimeLaunchError, .payloadUnavailable)
            }
        }
    }

    func testCArgumentsAreContiguousWritableAndTerminated() {
        RuntimeEmbeddedLauncher.withCArguments(["node", "entry.mjs", "中文"]) { count, argv in
            XCTAssertEqual(count, 3)
            XCTAssertEqual(String(cString: argv[0]!), "node")
            XCTAssertEqual(String(cString: argv[2]!), "中文")
            XCTAssertEqual(argv[1]!, argv[0]! + 5)
            XCTAssertNil(argv[3])
            argv[0]![0] = 78
            XCTAssertEqual(String(cString: argv[0]!), "Node")
        }
    }
}

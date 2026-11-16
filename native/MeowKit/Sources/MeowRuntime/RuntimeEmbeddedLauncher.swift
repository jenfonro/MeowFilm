import Foundation
import Darwin

public enum RuntimeLaunchError: Error, Sendable, Equatable {
    case payloadUnavailable, alreadyStarted, descriptorUnavailable
}

/// Code signing protects the embedded bundle; this checks its HTTP assembly.
public struct RuntimePayload: Sendable, Equatable {
    public let root: URL
    public let entry: URL

    public init(root: URL) throws {
        struct Manifest: Decodable {
            let schemaVersion: Int, status: String, product: String, entry: String, assemblyProfile: String
            let platform: String, transport: String, modules: [String]
        }
        guard let data = try? Data(contentsOf: root.appendingPathComponent("runtime-manifest.json")),
              let manifest = try? JSONDecoder().decode(Manifest.self, from: data),
              manifest.schemaVersion == 1, manifest.status == "complete", manifest.product == "aggregate",
              manifest.platform == "ios", manifest.transport == "runtime.http.v1",
              manifest.assemblyProfile == "common-native", manifest.modules == RuntimeModule.allCases.map(\.rawValue),
              Self.safe(manifest.entry) else {
            throw RuntimeLaunchError.payloadUnavailable
        }
        let entry = root.appendingPathComponent(manifest.entry, isDirectory: false)
        var directory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: entry.path, isDirectory: &directory), !directory.boolValue else {
            throw RuntimeLaunchError.payloadUnavailable
        }
        self.root = root; self.entry = entry
    }

    static func safe(_ path: String) -> Bool {
        let parts = path.split(separator: "/", omittingEmptySubsequences: false)
        return path.hasSuffix(".mjs") && !path.hasPrefix("/") &&
            parts.allSatisfy { !$0.isEmpty && $0 != "." && $0 != ".." }
    }
}

/// A private socketpair transfers trusted startup and native settings frames.
/// EOF requests shutdown; business calls use ordinary HTTP.
public final class RuntimeEmbeddedLauncher: RuntimeLaunching, @unchecked Sendable {
    public typealias Start = @Sendable (_ arguments: [String]) -> Int32
    private let payload: @Sendable () throws -> RuntimePayload
    private let dataDirectory: URL
    private let stackSize: Int
    private let start: Start
    private let settings: RuntimeSettingsAccess?
    private let lock = NSLock()
    private var started = false
    private var closed = false
    private var running = false
    private var channel: RuntimeBootstrapChannel?
    private var client: RuntimeHTTPClient?
    private var endpoint: URL?
    private var exitWaiters: [CheckedContinuation<Void, Never>] = []

    public init(payload: @escaping @Sendable () throws -> RuntimePayload, dataDirectory: URL,
                stackSize: Int = 8 * 1024 * 1024, settings: RuntimeSettingsAccess? = nil, start: @escaping Start) {
        self.payload = payload; self.dataDirectory = dataDirectory
        self.stackSize = stackSize; self.settings = settings; self.start = start
    }

    public func connect() async throws -> RuntimeHTTPClient {
        if let existing = try reserveLaunch() {
            let replacement = try RuntimeHTTPClient(baseURL: existing)
            do {
                try await replacement.validateAppModules()
                guard publish(replacement) else { throw RuntimeHTTPError.closed }
                return replacement
            } catch { await replacement.close(); throw error }
        }
        let payload: RuntimePayload
        do { payload = try self.payload() } catch { throw RuntimeLaunchError.payloadUnavailable }
        var descriptors: [Int32] = [-1, -1]
        guard socketpair(AF_UNIX, SOCK_STREAM, 0, &descriptors) == 0 else { throw RuntimeLaunchError.descriptorUnavailable }
        var enabled: Int32 = 1
        _ = setsockopt(descriptors[0], SOL_SOCKET, SO_NOSIGPIPE, &enabled, socklen_t(MemoryLayout<Int32>.size))
        _ = setsockopt(descriptors[1], SOL_SOCKET, SO_NOSIGPIPE, &enabled, socklen_t(MemoryLayout<Int32>.size))
        let sessionID = UUID().uuidString.lowercased()
        var fields: RuntimeObject = [
            "sessionId": .string(sessionID),
            "dataRoot": .string(dataDirectory.path)
        ]
        if settings != nil { fields["nativeSettings"] = .bool(true) }
        let configuration = try RuntimeJSON.encode(.object(fields))
        let channel = RuntimeBootstrapChannel(descriptor: descriptors[0], settings: settings) { [weak self] in self?.bootstrapEnded() }
        guard admit(channel) else {
            channel.disposeUnstarted()
            _ = Darwin.close(descriptors[1])
            throw RuntimeHTTPError.closed
        }
        // Swift owns the original until Start returns; Node only owns its dup.
        // No data paths enter argv.
        let bootstrapDescriptor = descriptors[1]
        let arguments = ["node", payload.entry.path, "--meow-bootstrap-fd", String(bootstrapDescriptor)]
        let start = start
        let thread = Thread { [self] in
            _ = start(arguments)
            _ = Darwin.close(bootstrapDescriptor)
            channel.executorReturned()
            runtimeExited()
        }
        thread.name = "meowfilm.runtime"
        thread.stackSize = stackSize
        thread.start()
        do {
            let baseURL = try await channel.endpoint(configuration: configuration)
            let client = try RuntimeHTTPClient(baseURL: baseURL)
            guard publish(client, endpoint: baseURL) else { await client.close(); throw RuntimeHTTPError.closed }
            return client
        } catch {
            await close()
            throw error
        }
    }

    public func close() async {
        let (channel, client) = stop()
        client?.invalidate()
        channel?.requestClose()
        await client?.close()
        await waitForExit()
        await channel?.waitForClose()
    }

    /// Only the return from the App's node_start confirms physical exit.
    public func waitForExit() async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if !running { lock.unlock(); continuation.resume(); return }
            exitWaiters.append(continuation)
            lock.unlock()
        }
    }

    private func reserveLaunch() throws -> URL? {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { throw RuntimeHTTPError.closed }
        if started {
            guard running, client?.isClosed == true, let endpoint else { throw RuntimeLaunchError.alreadyStarted }
            return endpoint
        }
        started = true
        return nil
    }

    private func admit(_ channel: RuntimeBootstrapChannel) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { return false }
        self.channel = channel
        running = true
        return true
    }

    private func publish(_ client: RuntimeHTTPClient, endpoint: URL? = nil) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard !closed, running else { return false }
        self.client = client
        if let endpoint { self.endpoint = endpoint }
        return true
    }

    private func stop() -> (RuntimeBootstrapChannel?, RuntimeHTTPClient?) {
        lock.lock(); defer { lock.unlock() }
        closed = true
        return (channel, client)
    }

    private func bootstrapEnded() {
        let (_, client) = stop()
        client?.invalidate()
    }

    private func runtimeExited() {
        lock.lock()
        running = false; closed = true
        let client = client
        let waiters = exitWaiters
        exitWaiters.removeAll()
        lock.unlock()
        client?.invalidate()
        for waiter in waiters { waiter.resume() }
    }

    /// Contiguous, writable C argv storage, valid for the duration of `body`.
    public static func withCArguments<T>(_ arguments: [String],
        _ body: (Int32, UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>) -> T) -> T {
        let lengths = arguments.map { $0.utf8.count + 1 }
        let storage = UnsafeMutablePointer<CChar>.allocate(capacity: max(1, lengths.reduce(0, +)))
        let argv = UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>.allocate(capacity: arguments.count + 1)
        defer { storage.deallocate(); argv.deallocate() }
        var cursor = storage
        for (index, argument) in arguments.enumerated() {
            argv[index] = cursor
            argument.withCString { _ = strcpy(cursor, $0) }
            cursor += lengths[index]
        }
        argv[arguments.count] = nil
        return body(Int32(arguments.count), argv)
    }
}

/// The sole reader retains its descriptor until EOF. Shutdown never closes a
/// descriptor under a blocked read or admits further native settings requests.
private final class RuntimeBootstrapChannel: @unchecked Sendable {
    private static let maximumFrameBytes = 8 * 1024 * 1024
    private let lock = NSLock()
    private var descriptor: Int32
    private var finished = false
    private var closeWaiters: [CheckedContinuation<Void, Never>] = []
    private let onClose: @Sendable () -> Void
    private let settings: RuntimeSettingsAccess?
    private var closing = false

    init(descriptor: Int32, settings: RuntimeSettingsAccess?, onClose: @escaping @Sendable () -> Void) {
        self.descriptor = descriptor; self.settings = settings; self.onClose = onClose
    }

    func endpoint(configuration: Data) async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async { [self] in
                defer { disposeUnstarted(); onClose(); didClose() }
                let descriptor = currentDescriptor()
                var ready = false
                do {
                    var line = configuration
                    line.append(10)
                    try write(line, descriptor: descriptor)
                    var received = Data(), bytes = [UInt8](repeating: 0, count: 4096)
                    while true {
                        let count = Darwin.read(descriptor, &bytes, bytes.count)
                        if count < 0, errno == EINTR { continue }
                        guard count > 0 else {
                            if !ready {
                                continuation.resume(throwing: received.isEmpty ? RuntimeHTTPError.closed : .invalidResponse)
                            }
                            return
                        }
                        received.append(contentsOf: bytes.prefix(count))
                        while let newline = received.firstIndex(of: 10) {
                            guard received.distance(from: received.startIndex, to: newline) <= Self.maximumFrameBytes,
                                  case .object(let reply) = try RuntimeJSON.decode(received.prefix(upTo: newline)) else {
                                throw RuntimeHTTPError.invalidResponse
                            }
                            received.removeSubrange(...newline)
                            if reply.count == 1, case .string(let address)? = reply["baseUrl"],
                               !ready, let url = URL(string: address) {
                                ready = true
                                continuation.resume(returning: url)
                            } else {
                                try respond(reply, descriptor: descriptor)
                            }
                        }
                        guard received.count <= Self.maximumFrameBytes else { throw RuntimeHTTPError.invalidResponse }
                    }
                } catch {
                    requestClose()
                    if !ready { continuation.resume(throwing: error as? RuntimeHTTPError ?? RuntimeHTTPError.invalidResponse) }
                }
            }
        }
    }

    func requestClose() {
        lock.lock(); defer { lock.unlock() }
        closing = true
        if descriptor >= 0 { _ = Darwin.shutdown(descriptor, SHUT_WR) }
    }

    func executorReturned() {
        lock.lock(); defer { lock.unlock() }
        if descriptor >= 0 { _ = Darwin.shutdown(descriptor, SHUT_RDWR) }
    }

    func waitForClose() async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if finished { lock.unlock(); continuation.resume(); return }
            closeWaiters.append(continuation)
            lock.unlock()
        }
    }

    private func didClose() {
        lock.lock()
        finished = true
        let waiters = closeWaiters
        closeWaiters.removeAll()
        lock.unlock()
        for waiter in waiters { waiter.resume() }
    }

    func disposeUnstarted() {
        lock.lock(); defer { lock.unlock() }
        if descriptor >= 0 { _ = Darwin.close(descriptor); descriptor = -1 }
    }

    private func currentDescriptor() -> Int32 {
        lock.lock(); defer { lock.unlock() }
        return descriptor
    }

    private func respond(_ request: RuntimeObject, descriptor: Int32) throws {
        lock.lock()
        let closing = closing
        lock.unlock()
        guard !closing, let settings,
              case .string(let kind)? = request["kind"],
              case .string(let block)? = request["block"], settings.blocks.contains(block),
              case .number(let id)? = request["id"], id >= 1, id <= 9_007_199_254_740_991,
              id.rounded(.towardZero) == id,
              kind == "settings.read" && request.count == 3 ||
                kind == "settings.write" && request.count == 5 && settings.writableBlocks.contains(block),
              kind != "settings.write" || request["document"] != nil && request["expected"] != nil else {
            throw RuntimeHTTPError.invalidResponse
        }
        var reply: RuntimeObject = ["kind": .string("settings.result"), "id": .number(id)]
        do {
            let snapshot: RuntimeSettingsSnapshot
            if kind == "settings.read" { snapshot = try settings.read(block) }
            else {
                guard case .object? = request["document"], case .object? = request["expected"] else {
                    throw RuntimeHTTPError.invalidResponse
                }
                snapshot = try settings.write(block, request["document"]!, request["expected"]!)
            }
            reply["ok"] = .bool(true)
            reply["document"] = snapshot.document ?? .null
            reply["fingerprint"] = snapshot.fingerprint
        } catch let error as RuntimeSettingsAccessError {
            reply["ok"] = .bool(false)
            reply["code"] = .string(error == .conflict ? "STORAGE_CONFLICT" : "STORAGE_ERROR")
        }
        let bytes = try RuntimeJSON.encode(.object(reply))
        guard bytes.count <= Self.maximumFrameBytes else { throw RuntimeHTTPError.invalidResponse }
        try write(bytes + Data([10]), descriptor: descriptor)
    }

    private func write(_ data: Data, descriptor: Int32) throws {
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = Darwin.write(descriptor, bytes.baseAddress! + offset, bytes.count - offset)
                if count < 0, errno == EINTR { continue }
                guard count > 0 else { throw RuntimeHTTPError.closed }
                offset += count
            }
        }
    }
}

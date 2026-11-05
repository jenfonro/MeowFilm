import Foundation
import Darwin

public enum RuntimeLaunchError: Error, Sendable, Equatable {
    case payloadUnavailable, alreadyStarted, descriptorUnavailable
}

/// An App-embedded runtime payload whose manifest is complete for this platform
/// and transport. Code signing protects the bundle; this checks the assembly.
public struct RuntimePayload: Sendable, Equatable {
    public let root: URL
    public let entry: URL

    public init(root: URL, platform: String = "ios", transport: String = "ios-socketpair-v1") throws {
        struct Manifest: Decodable {
            let schemaVersion: Int, status: String, product: String, entry: String
            let platform: String?, transport: String?
        }
        guard let data = try? Data(contentsOf: root.appendingPathComponent("runtime-manifest.json")),
              let manifest = try? JSONDecoder().decode(Manifest.self, from: data),
              manifest.schemaVersion == 1, manifest.status == "complete", manifest.product == "aggregate",
              manifest.platform == platform, manifest.transport == transport, Self.safe(manifest.entry) else {
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

/// Starts one embedded runtime over a fresh socketpair, at most once. `start`
/// runs the payload entry to completion on a dedicated thread: Node Mobile's
/// node_start in the App, a child process in desktop tests.
public final class RuntimeEmbeddedLauncher: RuntimeBridgeLaunching, @unchecked Sendable {
    public typealias Start = @Sendable (_ arguments: [String]) -> Int32
    private let payload: @Sendable () throws -> RuntimePayload
    private let dataDirectory: URL
    private let stackSize: Int
    private let start: Start
    private let lock = NSLock()
    private var started = false

    public init(payload: @escaping @Sendable () throws -> RuntimePayload, dataDirectory: URL,
                stackSize: Int = 8 * 1024 * 1024, start: @escaping Start) {
        self.payload = payload; self.dataDirectory = dataDirectory
        self.stackSize = stackSize; self.start = start
    }

    public func connect(handlers: [String: RuntimeBridgeConnection.Handler]) async throws -> RuntimeBridgeConnection {
        let payload: RuntimePayload
        do { payload = try self.payload() } catch { throw RuntimeLaunchError.payloadUnavailable }
        lock.lock()
        guard !started else { lock.unlock(); throw RuntimeLaunchError.alreadyStarted }
        started = true
        lock.unlock()
        var descriptors: [Int32] = [-1, -1]
        guard socketpair(AF_UNIX, SOCK_STREAM, 0, &descriptors) == 0 else { throw RuntimeLaunchError.descriptorUnavailable }
        var enabled: Int32 = 1
        _ = setsockopt(descriptors[1], SOL_SOCKET, SO_NOSIGPIPE, &enabled, socklen_t(MemoryLayout<Int32>.size))
        let session = UUID().uuidString.lowercased()
        let connection: RuntimeBridgeConnection
        do { connection = try RuntimeBridgeConnection(descriptor: descriptors[0], session: session, handlers: handlers) }
        catch { _ = close(descriptors[0]); _ = close(descriptors[1]); throw error }
        // The runtime takes ownership of its descriptor and closes it on exit.
        let arguments = ["node", payload.entry.path, "--meow-fd", String(descriptors[1]),
                         "--meow-session", session, "--meow-data", dataDirectory.path]
        let start = self.start
        let thread = Thread {
            _ = start(arguments)
            // A finished runtime is final for this process; our end reports it.
            connection.close()
        }
        thread.name = "meowfilm.runtime"
        thread.stackSize = stackSize
        thread.start()
        return connection
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

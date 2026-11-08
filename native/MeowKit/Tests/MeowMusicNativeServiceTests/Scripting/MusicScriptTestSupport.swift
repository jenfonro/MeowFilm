import Foundation
import MeowMusicFeature
import XCTest
@testable import MeowRuntime
@testable import MeowMusicNativeService

struct MusicScriptTestDocuments {
    let root: URL
    init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }
    func remove() { try? FileManager.default.removeItem(at: root) }
}

private final class MusicScriptHTTPRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var runtimes: [String: MusicScriptTestRuntime] = [:]
    func set(_ runtime: MusicScriptTestRuntime?, session: String) {
        lock.lock(); defer { lock.unlock() }; runtimes[session] = runtime
    }
    func runtime(_ session: String) -> MusicScriptTestRuntime? {
        lock.lock(); defer { lock.unlock() }; return runtimes[session]
    }
}

private final class MusicScriptURLProtocol: URLProtocol, @unchecked Sendable {
    static let registry = MusicScriptHTTPRegistry()
    private let lock = NSLock()
    private var loading: Task<Void, Never>?
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        lock.lock(); defer { lock.unlock() }
        loading = Task {
            do {
                let id = request.value(forHTTPHeaderField: "X-Meow-Session") ?? ""
                guard let runtime = Self.registry.runtime(id) else { throw RuntimeHTTPError.closed }
                let response = try await runtime.respond(request)
                try Task.checkCancellation()
                let data = try RuntimeJSON.encode(response)
                let http = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1",
                    headerFields: ["Content-Type": "application/json",
                        "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
                client?.urlProtocol(self, didReceive: http, cacheStoragePolicy: .notAllowed)
                client?.urlProtocol(self, didLoad: data)
                client?.urlProtocolDidFinishLoading(self)
            } catch {
                if !Task.isCancelled { client?.urlProtocol(self, didFailWithError: error) }
            }
        }
    }
    override func stopLoading() {
        lock.lock(); let task = loading; loading = nil; lock.unlock()
        task?.cancel()
    }
}

/// A complete HTTP music peer. Actual module execution/storage is covered by
/// MusicScriptNodeIntegrationTests, independently of this deterministic fixture.
actor MusicScriptTestRuntime: RuntimeLaunching {
    enum ModuleClose: Sendable, Equatable {
        case success, failed, missingClosed, missingCleanup, incomplete, notClosed, disconnected
    }
    struct Upload: Sendable {
        let installationID: String, revision: Int, basename: String
        let source: Data
    }
    var connects = 0
    var calls: [String] = []
    var parameters: [(String, RuntimeValue)] = []
    var uploads: [Upload] = []
    var failure: String?
    var generation = 1
    var enabled = false
    var imported = false
    var networkURL: String?
    var state: MusicScriptSourceItem.State = .disabled
    var content = String(repeating: "a", count: 64)
    var execution = String(repeating: "b", count: 64)
    var checkValue: RuntimeValue = .null
    var lyricValue: RuntimeValue = .object(["lyric": .string("[00:00]line")])
    var native: RuntimeHTTPClient?
    var handshakeValid = true
    var cleanupComplete = true
    var enableFailure: String?
    var loseEnableReceipt = false
    var moduleClose = ModuleClose.success
    var gates: [String: NativeMusicGate] = [:]
    var cancellationObservers: [String: @Sendable () -> Void] = [:]
    var id = "1c33c457-d51f-4f51-a3b7-b0c222e89a01"
    let sessionID = UUID().uuidString.lowercased()
    func fail(_ code: String?) { failure = code }
    func setHandshakeValid(_ valid: Bool) { handshakeValid = valid }
    func setCleanupComplete(_ complete: Bool) { cleanupComplete = complete }
    func setEnableFailure(_ code: String?) { enableFailure = code }
    func setLoseEnableReceipt(_ lost: Bool) { loseEnableReceipt = lost }
    func setModuleClose(_ response: ModuleClose) { moduleClose = response }
    func directory(_ next: MusicScriptSourceItem.State) {
        imported = true; enabled = next != .disabled; state = next
    }
    func replaceContent() {
        content = String(repeating: "c", count: 64); execution = String(repeating: "d", count: 64)
        generation += 1; state = .ready
    }
    func reload() { generation += 1; state = .ready }
    func setLyric(_ value: RuntimeValue) { lyricValue = value }
    func hold(_ method: String, _ gate: NativeMusicGate, onCancel: @escaping @Sendable () -> Void = {}) {
        gates[method] = gate; cancellationObservers[method] = onCancel
    }
    func metrics() -> (connects: Int, calls: [String]) { (connects, calls) }
    func connect() throws -> RuntimeHTTPClient {
        connects += 1
        MusicScriptURLProtocol.registry.set(self, session: sessionID)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [MusicScriptURLProtocol.self]
        let client = try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!,
            sessionID: sessionID, requestKey: String(repeating: "a", count: 64), configuration: configuration)
        native = client
        return client
    }
    func close() async {
        await native?.close()
        MusicScriptURLProtocol.registry.set(nil, session: sessionID)
    }
    func waitForExit() {}
    private func success(_ value: RuntimeValue = .object([:]), contract: String = "music.script.v3") -> RuntimeValue {
        .object(["ok": .bool(true), "contract": .string(contract), "value": value])
    }
    private func failed(_ code: String) -> RuntimeValue {
        .object(["ok": .bool(false), "contract": .string("music.script.v3"), "code": .string(code)])
    }
    private func item() -> RuntimeValue {
        .object([
            "installationId": .string(id), "name": .string("Synthetic source"), "version": .string("2"),
            "author": .string("Fixture"), "description": .string("Test"), "homepage": .string("https://source.invalid"),
            "origin": networkURL.map { .object(["kind": .string("network"), "host": .string("source.invalid"), "url": .string($0)]) }
                ?? .object(["kind": .string("file")]), "enabled": .bool(enabled),
            "state": .string(state.rawValue), "generation": .number(Double(state == .ready ? generation : 0)),
            "contentHash": .string(content), "executionHash": .string(execution),
            "sites": enabled ? .object(["tx": .object([
                "actions": .array([.string("musicUrl"), .string("lyric")]),
                "qualitys": .array([.string("128k"), .string("320k")]),
            ])]) : .null, "check": checkValue, "rollbackAvailable": .bool(false),
        ])
    }
    func respond(_ request: URLRequest) async throws -> RuntimeValue {
        let path = request.url!.path
        let method = path == "/v1/describe" ? "runtime.describe" :
            path == "/v1/cat/status" ? "cat.status" :
            request.httpMethod == "PUT" ? "upload" : request.url!.lastPathComponent
        let bytes: Data
        if let body = request.httpBody { bytes = body }
        else if let stream = request.httpBodyStream {
            stream.open(); defer { stream.close() }
            var data = Data(), buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let count = stream.read(&buffer, maxLength: buffer.count)
                if count <= 0 { break }
                data.append(contentsOf: buffer.prefix(count))
            }
            bytes = data
        } else { bytes = Data() }
        let value: RuntimeValue
        if method == "upload" || bytes.isEmpty { value = .object([:]) }
        else { value = try RuntimeJSON.decode(bytes) }
        calls.append(method); parameters.append((method, value))
        if let gate = gates[method] {
            let observer = cancellationObservers[method] ?? {}
            await withTaskCancellationHandler { await gate.wait() } onCancel: { observer() }
            try Task.checkCancellation()
        }
        if method == "runtime.describe" {
            return success(.object(["protocol": .string("runtime.http.v1"), "modules": .array([
                .object(["id": .string("music"), "version": .string("0.8.0"),
                         "contract": .string(handshakeValid ? "music.script.v3" : "invalid")]),
            ])]), contract: "runtime.http.v1")
        }
        if method == "cat.status" {
            return success(.object(["instances": .array([])]), contract: "cat.script.v2")
        }
        if method == "close" {
            switch moduleClose {
            case .success: return success(.object(["closed": .bool(true), "cleanupComplete": .bool(true)]))
            case .failed: return failed("SCRIPT_CLEANUP")
            case .missingClosed: return success(.object(["cleanupComplete": .bool(true)]))
            case .missingCleanup: return success(.object(["closed": .bool(true)]))
            case .incomplete: return success(.object(["closed": .bool(true), "cleanupComplete": .bool(false)]))
            case .notClosed: return success(.object(["closed": .bool(false), "cleanupComplete": .bool(true)]))
            case .disconnected: throw URLError(.networkConnectionLost)
            }
        }
        if let failure { return failed(failure) }
        switch method {
        case "upload":
            let parts = path.split(separator: "/")
            let installation = String(parts[3]), revision = Int(parts[4])!
            let basename = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)?.queryItems?.first?.value ?? ""
            uploads.append(.init(installationID: installation, revision: revision, basename: basename, source: bytes))
            return success(.object(["installationId": .string(installation), "sourceRevision": .number(Double(revision)),
                "sourceBytes": .number(Double(bytes.count)), "contentHash": .string(content)]))
        case "sources.list": return success(.object(["items": .array(imported ? [item()] : [])]))
        case "sources.add", "sources.download":
            if let installation = value.scriptField("installationId")?.scriptText { id = installation }
            imported = true; enabled = false; state = .disabled
            networkURL = method == "sources.download" ? value.scriptField("url")?.scriptText : nil
            return success(.object(["item": item()]))
        case "sources.update":
            let hasRevision = value.scriptField("sourceRevision") != nil
            guard networkURL == nil ? hasRevision : !hasRevision else { return failed("INVALID_REQUEST") }
            return success(.object(["item": item()]))
        case "sources.edit":
            if let url = value.scriptField("url")?.scriptText { networkURL = url }
            return success(.object(["item": item()]))
        case "sources.check":
            if enabled && (state == .idle || state == .starting) { generation += 1; state = .ready }
            checkValue = .object(["summary": .string("normal"), "checkedAt": .number(1900000000000),
                "sites": .object(["tx": .object(["state": .string("ok")])])])
            return success(.object(["item": item()]))
        case "sources.enable":
            if let enableFailure { return failed(enableFailure) }
            generation += 1; enabled = true; state = .ready
            if loseEnableReceipt { return failed("TIMEOUT") }
            return success(.object(["item": item()]))
        case "sources.disable":
            enabled = false; state = .disabled
            return cleanupComplete ? success(.object(["item": item()])) : failed("SCRIPT_CLEANUP")
        case "sources.remove":
            imported = false; enabled = false; state = .disabled
            return cleanupComplete ? success(.object(["removed": .bool(true)])) : failed("SCRIPT_CLEANUP")
        case "resolve", "invoke":
            if !enabled { return failed("SOURCE_DISABLED") }
            if state == .failed { return failed("SCRIPT_FAILURE") }
            if state != .ready { generation += 1; state = .ready }
            if method == "invoke" { return success(lyricValue) }
            return success(.object([
                "installationId": .string(id), "generation": .number(Double(generation)), "contentHash": .string(content),
                "platform": value.scriptField("platform")!, "quality": value.scriptField("quality")!,
                "url": .string("http://media.invalid/track.mp3"), "headers": .object(["X-Test": .string("public")]),
                "expiresAt": .number(1900000000000),
            ]))
        default: return success(.object(["item": item()]))
        }
    }
}

func scriptExpectFailure<T>(_ expected: MusicServiceError, file: StaticString = #filePath, line: UInt = #line,
                            _ body: () async throws -> T) async {
    do { _ = try await body(); XCTFail("Expected \(expected)", file: file, line: line) }
    catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
}

func scriptAssertCatAvailable(_ connection: RuntimeHTTPClient,
                              file: StaticString = #filePath, line: UInt = #line) async throws {
    XCTAssertFalse(connection.isClosed, file: file, line: line)
    let status = try await connection.call(module: .cat, operation: "status")
    XCTAssertEqual(status.scriptField("instances"), .array([]), file: file, line: line)
    XCTAssertFalse(connection.isClosed, file: file, line: line)
}

let musicScriptFixture = Data("""
/**
 * @name 合成 LX 音源
 * @version 2
 * @author Fixture
 * @description Synthetic test source
 * @homepage https://source.invalid
 */
lx.on(lx.EVENT_NAMES.request, async ({ source, action, info }, context) => {
  const count = ((await context.storage.get('count')) || 0) + 1;
  await context.storage.set('count', count);
  if (action === 'lyric') return { lyric: '[00:00.00]' + source, tlyric: '', rlyric: '' };
  return 'http://media.invalid/' + source + '/' + info.type + '?n=' + count;
});
lx.send(lx.EVENT_NAMES.inited, { sources: {
  tx: { name: 'Fixture', type: 'music', actions: ['musicUrl', 'lyric'], qualitys: ['128k', '320k'] }
} });
""".utf8)

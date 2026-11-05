import Darwin
import Foundation
import MeowJSON
import MeowMusicFeature
import MeowRuntime
import MeowStorage
import XCTest
@testable import MeowMusicNativeService

struct MusicScriptTestDocuments {
    let root: URL
    let store: DocumentStore
    init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        store = try DocumentStore(baseURL: root, namespace: NativeMusicScriptService.namespace,
                                  maximumDocumentBytes: MusicScriptSourceItem.maximumSourceBytes)
    }
    func file(_ key: String) -> URL {
        root.appendingPathComponent(NativeMusicScriptService.namespace).appendingPathComponent(key + ".json")
    }
    func remove() { try? FileManager.default.removeItem(at: root) }
    func owner(_ launcher: any RuntimeBridgeLaunching, download: MusicScriptDownload = .init()) -> NativeMusicScriptService {
        NativeMusicScriptService(storeFactory: { store }, launcher: launcher, download: download,
                                 clock: { NativeMusicFixture.date })
    }
}

/// Protocol peer, not an LX implementation. Tests explicitly control the
/// publication/receipt boundary and may make reverse calls with retired grants.
actor MusicScriptTestRuntime: RuntimeBridgeLaunching {
    enum Load { case success, failed, lostReceipt }
    var mode = Load.success
    var connects = 0
    var calls: [String] = []
    var instances: [String: JSONValue] = [:]
    var bindings: [String: JSONValue] = [:]
    var stages: [String: Data] = [:]
    var generation = 0
    var native: RuntimeBridgeConnection?
    var peer: RuntimeBridgeConnection?
    var invokeGate: NativeMusicGate?

    func configure(_ mode: Load) { self.mode = mode }
    func holdInvoke(_ gate: NativeMusicGate) { invokeGate = gate }
    func metrics() -> (connects: Int, calls: [String]) { (connects, calls) }
    func binding(_ id: String) throws -> JSONValue { try XCTUnwrap(bindings[id]) }
    func connect(handlers: [String: RuntimeBridgeConnection.Handler]) throws -> RuntimeBridgeConnection {
        connects += 1
        var fds: [Int32] = [-1, -1]
        guard socketpair(AF_UNIX, SOCK_STREAM, 0, &fds) == 0 else { throw RuntimeLaunchError.descriptorUnavailable }
        let session = UUID().uuidString.lowercased()
        var methods: [String: RuntimeBridgeConnection.Handler] = [:]
        for name in ["runtime.describe", "stage.begin", "stage.chunk", "stage.abort", "load", "unload", "status", "invoke"] {
            let method = name == "runtime.describe" ? name : "music.script." + name
            methods[method] = { [weak self] value in
                guard let self else { throw RuntimeBridgeError.closed }
                return try await self.respond(name, value)
            }
        }
        peer = try RuntimeBridgeConnection(descriptor: fds[1], session: session, handlers: methods)
        let result = try RuntimeBridgeConnection(descriptor: fds[0], session: session, handlers: handlers)
        native = result
        return result
    }
    func close() { peer?.close(); native?.close() }
    func reverse(_ operation: String, binding: JSONValue, generation: Int? = nil) async throws -> JSONValue {
        guard let peer else { throw RuntimeBridgeError.closed }
        var fields: [String: JSONValue] = [
            "grant": binding["grant"]!, "installationId": binding["installationId"]!,
            "generation": generation.map { .number(Double($0)) } ?? binding["generation"]!,
            "siteId": .string("tx"), "key": .string("count"),
        ]
        if operation == "set" { fields["value"] = .number(7) }
        return try await peer.call("platform.musicScript.storage." + operation, .object(fields))
    }
    private func success(_ fields: [String: JSONValue] = [:]) -> JSONValue {
        .object(fields.merging(["ok": .bool(true), "contract": .string("music.script.v2")]) { _, rhs in rhs })
    }
    private func respond(_ method: String, _ value: JSONValue) async throws -> JSONValue {
        calls.append(method)
        switch method {
        case "runtime.describe":
            return .object(["protocol": .number(1), "modules": .array([
                .object(["id": .string("music"), "version": .string("0.7.0")]),
            ])])
        case "stage.begin":
            let token = UUID().uuidString
            stages[token] = Data()
            return success(["token": .string(token), "chunkBytes": .number(65536)])
        case "stage.chunk":
            let token = try XCTUnwrap(value["token"]?.scriptText)
            stages[token, default: Data()].append(try XCTUnwrap(Data(base64Encoded: value["data"]!.scriptText!)))
            return success(["next": .number(Double(stages[token]!.count))])
        case "stage.abort":
            stages[value["token"]!.scriptText!] = nil
            return success(["released": .bool(true)])
        case "load":
            if mode == .failed { return .object(["ok": .bool(false), "contract": .string("music.script.v2"), "code": .string("SCRIPT_ERROR")]) }
            let id = value["installationId"]!.scriptText!
            generation += 1
            let instance: JSONValue = .object([
                "installationId": .string(id), "revision": value["revision"]!, "generation": .number(Double(generation)),
                "state": .string("ready"), "protocol": .string("lx"), "version": value["version"]!,
                "contentHash": .string(musicScriptSHA256(stages[value["token"]!.scriptText!]!)),
                "capabilities": .object(["tx": .object([
                    "actions": .array([.string("musicUrl"), .string("lyric")]),
                    "qualitys": .array([.string("128k"), .string("320k")]),
                ])]),
            ])
            instances[id] = instance
            bindings[id] = .object(["grant": value["grant"]!, "installationId": .string(id),
                                   "generation": .number(Double(generation))])
            if mode == .lostReceipt { throw RuntimeBridgeError.timeout }
            return success(["instance": instance])
        case "status": return success(["instances": .array(Array(instances.values))])
        case "unload":
            instances[value["installationId"]!.scriptText!] = nil
            return success(["unloaded": .bool(true), "cleanupComplete": .bool(true)])
        case "invoke":
            if let invokeGate { await invokeGate.wait() }
            return success(["value": .string("http://media.invalid/track.mp3")])
        default: throw RuntimeBridgeError.methodUnavailable
        }
    }
}

func scriptExpectFailure<T>(_ expected: MusicServiceError, file: StaticString = #filePath, line: UInt = #line,
                            _ body: () async throws -> T) async {
    do { _ = try await body(); XCTFail("Expected \(expected)", file: file, line: line) }
    catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
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

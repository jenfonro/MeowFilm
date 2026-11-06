import Darwin
import Foundation
import MeowJSON
import MeowMusicFeature
import MeowRuntime
import XCTest
@testable import MeowMusicNativeService

struct MusicScriptTestDocuments {
    let root: URL
    init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }
    func remove() { try? FileManager.default.removeItem(at: root) }
    func owner(_ launcher: any RuntimeBridgeLaunching) -> NativeMusicScriptService {
        NativeMusicScriptService(launcher: launcher)
    }
}

/// A v3 protocol peer. Persistence and script execution are tested against the
/// actual module in MusicScriptNodeIntegrationTests, not simulated here.
actor MusicScriptTestRuntime: RuntimeBridgeLaunching {
    var connects = 0
    var calls: [String] = []
    var parameters: [(String, JSONValue)] = []
    var reverseHandlers: [String] = []
    var failure: String?
    var generation = 1
    var enabled = false
    var imported = false
    var stages: [String: Data] = [:]
    var native: RuntimeBridgeConnection?
    var peer: RuntimeBridgeConnection?
    let id = "1c33c457-d51f-4f51-a3b7-b0c222e89a01"
    func fail(_ code: String?) { failure = code }
    func metrics() -> (connects: Int, calls: [String]) { (connects, calls) }
    func connect(handlers: [String: RuntimeBridgeConnection.Handler]) throws -> RuntimeBridgeConnection {
        connects += 1; reverseHandlers = Array(handlers.keys)
        var fds: [Int32] = [-1, -1]
        guard socketpair(AF_UNIX, SOCK_STREAM, 0, &fds) == 0 else { throw RuntimeLaunchError.descriptorUnavailable }
        let session = UUID().uuidString.lowercased()
        var methods: [String: RuntimeBridgeConnection.Handler] = [:]
        for name in ["runtime.describe", "stage.begin", "stage.chunk", "stage.abort", "sources.list", "sources.detail",
                     "sources.add", "sources.download", "sources.enable", "sources.disable", "sources.edit",
                     "sources.update", "sources.rollback", "sources.check", "sources.remove", "resolve", "invoke"] {
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
    private func success(_ fields: [String: JSONValue] = [:]) -> JSONValue {
        .object(fields.merging(["ok": .bool(true), "contract": .string("music.script.v3")]) { _, rhs in rhs })
    }
    private func item() -> JSONValue {
        .object([
            "installationId": .string(id), "name": .string("Synthetic source"), "version": .string("2"),
            "author": .string("Fixture"), "description": .string("Test"), "homepage": .string("https://source.invalid"),
            "origin": .object(["kind": .string("file")]), "enabled": .bool(enabled),
            "state": .string(enabled ? "ready" : "disabled"), "generation": .number(Double(enabled ? generation : 0)),
            "contentHash": .string(String(repeating: "a", count: 64)), "executionHash": .string(String(repeating: "b", count: 64)),
            "sites": enabled ? .object(["tx": .object([
                "actions": .array([.string("musicUrl"), .string("lyric")]),
                "qualitys": .array([.string("128k"), .string("320k")]),
            ])]) : .null, "check": .null, "rollbackAvailable": .bool(false),
        ])
    }
    private func respond(_ method: String, _ value: JSONValue) async throws -> JSONValue {
        calls.append(method); parameters.append((method, value))
        if method == "runtime.describe" {
            return .object(["protocol": .number(1), "modules": .array([
                .object(["id": .string("music"), "version": .string("0.8.0")]),
            ])])
        }
        if let failure { return .object(["ok": .bool(false), "contract": .string("music.script.v3"), "code": .string(failure)]) }
        switch method {
        case "stage.begin":
            let token = UUID().uuidString; stages[token] = Data()
            return success(["token": .string(token), "chunkBytes": .number(65536)])
        case "stage.chunk":
            let token = try XCTUnwrap(value["token"]?.scriptText)
            stages[token, default: Data()].append(try XCTUnwrap(Data(base64Encoded: value["data"]!.scriptText!)))
            return success(["next": .number(Double(stages[token]!.count))])
        case "stage.abort":
            stages[value["token"]!.scriptText!] = nil
            return success(["released": .bool(true)])
        case "sources.list": return success(["items": .array(imported ? [item()] : [])])
        case "sources.add", "sources.download": imported = true; return success(["item": item()])
        case "sources.enable": generation += 1; enabled = true; return success(["item": item()])
        case "sources.disable": enabled = false; return success(["item": item()])
        case "sources.remove": imported = false; return success(["removed": .bool(true)])
        case "resolve": return success([
            "installationId": .string(id), "generation": .number(Double(generation)),
            "platform": value["platform"]!, "quality": value["quality"]!,
            "url": .string("http://media.invalid/track.mp3"), "headers": .object(["X-Test": .string("public")]),
            "expiresAt": .number(1900000000000),
        ])
        case "invoke": return success([
            "installationId": .string(id), "generation": .number(Double(generation)),
            "value": .object(["lyric": .string("[00:00]line")]),
        ])
        default: return success(["item": item()])
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

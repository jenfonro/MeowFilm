#if os(macOS)
import Darwin
import Foundation
import MeowMusicFeature
import MeowRuntime
import XCTest
@testable import MeowMusicNativeService

/// macOS executor of the actual iOS JS entry, selecting the asmjs backend.
/// Only the designated socket descriptor and stdio cross the spawn boundary.
private actor MusicScriptNodeLauncher: RuntimeBridgeLaunching {
    let node: String
    let root: URL
    let dataRoot: URL
    var exit: Task<Int32, Never>?
    var connection: RuntimeBridgeConnection?
    init(dataRoot: URL) throws {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<6 { root.deleteLastPathComponent() }
        self.root = root; self.dataRoot = dataRoot
        let environment = ProcessInfo.processInfo.environment
        let candidates = environment["MEOWFILM_TEST_NODE"].map { [$0] } ??
            (environment["PATH"] ?? "").split(separator: ":").map { String($0) + "/node" }
        guard let node = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else {
            throw NSError(domain: "Node test executable is required (MEOWFILM_TEST_NODE)", code: 1)
        }
        self.node = node
    }
    func connect(handlers: [String: RuntimeBridgeConnection.Handler]) throws -> RuntimeBridgeConnection {
        var fds: [Int32] = [-1, -1]
        guard socketpair(AF_UNIX, SOCK_STREAM, 0, &fds) == 0 else { throw RuntimeLaunchError.descriptorUnavailable }
        let session = UUID().uuidString.lowercased()
        let preload = "Object.defineProperty(process,'platform',{value:'ios'});Object.defineProperty(globalThis,'WebAssembly',{value:undefined});"
        let arguments = [node, "--import", "data:text/javascript," + preload,
            root.appendingPathComponent("apps/ios/runtime/entries/aggregate.mjs").path,
            "--meow-fd", String(fds[1]), "--meow-session", session, "--meow-data", dataRoot.path]
        var actions: posix_spawn_file_actions_t?, attributes: posix_spawnattr_t?
        posix_spawn_file_actions_init(&actions); posix_spawnattr_init(&attributes)
        defer { posix_spawn_file_actions_destroy(&actions); posix_spawnattr_destroy(&attributes) }
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_CLOEXEC_DEFAULT))
        for fd in [Int32(1), Int32(2), fds[1]] { posix_spawn_file_actions_addinherit_np(&actions, fd) }
        posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0)
        var pid: pid_t = 0
        let environment = ProcessInfo.processInfo.environment.filter { !["NODE_OPTIONS", "NODE_PATH"].contains($0.key) }
            .map { $0.key + "=" + $0.value }
        let code = RuntimeEmbeddedLauncher.withCArguments(arguments) { _, argv in
            RuntimeEmbeddedLauncher.withCArguments(environment) { _, env in
                posix_spawn(&pid, node, &actions, &attributes, argv, env)
            }
        }
        Darwin.close(fds[1])
        guard code == 0 else { Darwin.close(fds[0]); throw NSError(domain: NSPOSIXErrorDomain, code: Int(code)) }
        let child = pid
        exit = Task.detached {
            var status: Int32 = 0
            while waitpid(child, &status, 0) < 0 { if errno != EINTR { return -1 } }
            return status
        }
        let connection = try RuntimeBridgeConnection(descriptor: fds[0], session: session, handlers: handlers, timeout: 30)
        self.connection = connection
        return connection
    }
    func finish() async -> Int32 {
        connection?.close()
        return await exit?.value ?? -1
    }
    func waitForExit() async { _ = await exit?.value }
}

final class MusicScriptNodeIntegrationTests: XCTestCase {
    func testNativeOwnerLoadsInvokesPersistsStorageAndUnloadsThroughTheRealIOSRuntimeEntry() async throws {
        let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
        let launcher = try MusicScriptNodeLauncher(dataRoot: documents.root.appendingPathComponent("runtime"))
        let session = RuntimeBridgeSession(launcher: launcher)
        let service = try await documents.owner(session: session)
        do {
            let item = try await service.importScriptSource(musicScriptFixture, fileName: "fixture.js")
            let enabled = try await service.setScriptSourceEnabled(item.id, enabled: true)
            XCTAssertEqual(enabled.platforms, [.qq]); XCTAssertEqual(enabled.state, .ready)
            let source = try await service.source(installationID: item.id)
            let request = MusicScriptRequest(installationID: item.id, generation: source.generation, siteID: "tx",
                sourceKey: "test", quality: "320k", musicInfo: Data(#"{"songmid":"SyntheticMID"}"#.utf8))
            let audio = try await service.resolve(request: request)
            XCTAssertEqual(audio.url.absoluteString, "https://media.invalid/tx/320k?n=1")
            let lyric = try await service.lyrics(request: request)
            XCTAssertEqual(lyric.text, "[00:00.00]tx")
            _ = try await service.setScriptSourceEnabled(item.id, enabled: false)
            await scriptExpectFailure(.sourceUnavailable) { try await service.resolve(request: request) }
            _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
            let next = try await service.source(installationID: item.id)
            XCTAssertGreaterThan(next.generation, source.generation)
            let audioAfterReload = try await service.resolve(request: .init(installationID: item.id, generation: next.generation,
                siteID: "tx", sourceKey: "test", quality: "128k", musicInfo: Data("{}".utf8)))
            XCTAssertEqual(audioAfterReload.url.absoluteString, "https://media.invalid/tx/128k?n=3")
            try await service.removeScriptSource(item.id)
            XCTAssertNil(try documents.store.read(key: "storage." + item.id))
            let remaining = try await service.sources(); XCTAssertTrue(remaining.isEmpty)
            try await service.close()
            await session.close()
            let exit = await launcher.finish(); XCTAssertEqual(exit, 0)
        } catch {
            do { try await service.close() }
            catch { XCTFail("Music cleanup failed: \(error)") }
            await session.close()
            _ = await launcher.finish()
            throw error
        }
    }
}
#endif

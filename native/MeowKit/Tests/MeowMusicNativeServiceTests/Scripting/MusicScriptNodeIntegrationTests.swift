#if os(macOS)
import Darwin
import Foundation
import MeowMusicFeature
import MeowRuntime
import XCTest
@testable import MeowMusicNativeService

/// Runs the iOS HTTP assembly on macOS with the real native ScriptHost backend.
/// This does not claim iOS framework loading; App host tests cover that boundary.
private actor MusicScriptNodeLauncher: RuntimeLaunching {
    let node: String
    let root: URL
    let dataRoot: URL
    var exit: Task<Int32, Never>?
    var channel: Int32?
    var client: RuntimeHTTPClient?
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
    func connect() async throws -> RuntimeHTTPClient {
        var fds: [Int32] = [-1, -1]
        guard socketpair(AF_UNIX, SOCK_STREAM, 0, &fds) == 0 else { throw RuntimeLaunchError.descriptorUnavailable }
        var enabled: Int32 = 1
        _ = setsockopt(fds[0], SOL_SOCKET, SO_NOSIGPIPE, &enabled, socklen_t(MemoryLayout<Int32>.size))
        let session = UUID().uuidString.lowercased()
        let arguments = [node, root.appendingPathComponent("apps/ios/runtime/entries/aggregate.mjs").path,
                         "--meow-bootstrap-fd", String(fds[1])]
        var actions: posix_spawn_file_actions_t?, attributes: posix_spawnattr_t?
        posix_spawn_file_actions_init(&actions); posix_spawnattr_init(&attributes)
        defer { posix_spawn_file_actions_destroy(&actions); posix_spawnattr_destroy(&attributes) }
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_CLOEXEC_DEFAULT))
        for fd in [Int32(1), Int32(2), fds[1]] { posix_spawn_file_actions_addinherit_np(&actions, fd) }
        posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0)
        var pid: pid_t = 0
        let environment = ProcessInfo.processInfo.environment.filter { !["NODE_OPTIONS", "NODE_PATH"].contains($0.key) }
            .map { $0.key + "=" + $0.value }
        let code = Self.withCArguments(arguments) { argv in
            Self.withCArguments(environment) { env in posix_spawn(&pid, node, &actions, &attributes, argv, env) }
        }
        Darwin.close(fds[1])
        guard code == 0 else { Darwin.close(fds[0]); throw NSError(domain: NSPOSIXErrorDomain, code: Int(code)) }
        let child = pid, descriptor = fds[0]
        channel = descriptor
        exit = Task.detached {
            var status: Int32 = 0
            while waitpid(child, &status, 0) < 0 { if errno != EINTR { return -1 } }
            return status
        }
        do {
            var configuration = try RuntimeJSON.encode(.object([
                "sessionId": .string(session), "dataRoot": .string(dataRoot.path),
            ]))
            configuration.append(10)
            let endpoint = try await Task.detached {
                try configuration.withUnsafeBytes { bytes in
                    var offset = 0
                    while offset < bytes.count {
                        let count = Darwin.write(descriptor, bytes.baseAddress!.advanced(by: offset), bytes.count - offset)
                        if count < 0 && errno == EINTR { continue }
                        guard count > 0 else { throw RuntimeLaunchError.descriptorUnavailable }
                        offset += count
                    }
                }
                var data = Data(), byte: UInt8 = 0
                while true {
                    let count = Darwin.read(descriptor, &byte, 1)
                    if count < 0 && errno == EINTR { continue }
                    guard count == 1 else { throw RuntimeLaunchError.payloadUnavailable }
                    if byte == 10 { return try RuntimeJSON.decode(data) }
                    data.append(byte)
                }
            }.value
            guard case .object(let fields) = endpoint, case .string(let address)? = fields["baseUrl"],
                  let url = URL(string: address) else { throw RuntimeLaunchError.payloadUnavailable }
            let value = try RuntimeHTTPClient(baseURL: url)
            client = value
            return value
        } catch {
            _ = Darwin.shutdown(descriptor, SHUT_WR)
            _ = await exit?.value
            Darwin.close(descriptor); channel = nil
            throw error
        }
    }
    func close() async {
        if let client { try? await client.shutdown(); await client.close() }
        if let channel { _ = Darwin.shutdown(channel, SHUT_WR) }
    }
    func waitForExit() async {
        _ = await exit?.value
        if let channel { Darwin.close(channel); self.channel = nil }
    }
    func exitStatus() async -> Int32 { await exit?.value ?? -1 }
    private static func withCArguments<T>(_ values: [String], _ body: (UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>) -> T) -> T {
        var pointers = values.map { strdup($0) } + [nil]
        defer { pointers.forEach { free($0) } }
        return pointers.withUnsafeMutableBufferPointer { body($0.baseAddress!) }
    }
}

final class MusicScriptNodeIntegrationTests: XCTestCase {
    func testNativeOwnerLoadsInvokesPersistsStorageAndUnloadsThroughTheRealIOSRuntimeEntry() async throws {
        let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
        let launcher = try MusicScriptNodeLauncher(dataRoot: documents.root.appendingPathComponent("runtime"))
        let session = RuntimeSession(launcher: launcher)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        do {
            let item = try await service.importScriptSource(musicScriptFixture, fileName: "fixture.js")
            let enabled = try await service.setScriptSourceEnabled(item.id, enabled: true)
            XCTAssertEqual(enabled.platforms, [.qq]); XCTAssertEqual(enabled.state, .ready)
            let source = try await service.source(installationID: item.id)
            let request = MusicScriptRequest(source: source, siteID: "tx",
                sourceKey: "test", quality: "320k", musicInfo: Data(#"{"songmid":"SyntheticMID"}"#.utf8))
            let audio = try await service.resolve(request: request)
            XCTAssertEqual(audio.value.url.absoluteString, "https://media.invalid/tx/320k?n=1")
            let lyric = try await service.lyrics(request: request)
            XCTAssertEqual(lyric.value.text, "[00:00.00]tx")
            _ = try await service.setScriptSourceEnabled(item.id, enabled: false)
            await scriptExpectFailure(.sourceUnavailable) { try await service.resolve(request: request) }
            _ = try await service.setScriptSourceEnabled(item.id, enabled: true)
            let next = try await service.source(installationID: item.id)
            XCTAssertGreaterThan(next.generation, source.generation)
            let audioAfterReload = try await service.resolve(request: .init(source: next,
                siteID: "tx", sourceKey: "test", quality: "128k", musicInfo: Data("{}".utf8)))
            XCTAssertEqual(audioAfterReload.value.url.absoluteString, "https://media.invalid/tx/128k?n=3")
            try await service.removeScriptSource(item.id)
            XCTAssertFalse(FileManager.default.fileExists(atPath: documents.root
                .appendingPathComponent("runtime/music/data/" + item.id + ".json").path))
            let remaining = try await service.sources(); XCTAssertTrue(remaining.isEmpty)
            let active = try await service.importScriptSource(musicScriptFixture, fileName: "active-at-close.js")
            let activeItem = try await service.setScriptSourceEnabled(active.id, enabled: true)
            XCTAssertTrue(activeItem.enabled); XCTAssertEqual(activeItem.state, .ready)
            let live = try await service.sources()
            XCTAssertEqual(live.map(\.installationID), [active.id])
            XCTAssertGreaterThan(try XCTUnwrap(live.first).generation, 0)
            let connection = try await session.connection()
            try await service.close()
            try await service.close()
            let shared = try await session.connection()
            XCTAssertTrue(connection === shared)
            try await scriptAssertCatAvailable(shared)
            await session.close()
            XCTAssertTrue(connection.isClosed)
            let exit = await launcher.exitStatus(); XCTAssertEqual(exit, 0)
        } catch {
            try? await service.close()
            await session.close()
            throw error
        }
    }
    func testColdStartupKeepsEnabledDirectoryIdleUntilFirstResolveAndReusesItsStoredInstallation() async throws {
        let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
        let dataRoot = documents.root.appendingPathComponent("runtime")
        let firstLauncher = try MusicScriptNodeLauncher(dataRoot: dataRoot)
        let firstSession = RuntimeSession(launcher: firstLauncher)
        let first = NativeMusicScriptService(connection: { try await firstSession.connection() })
        await first.start()
        let installation: String
        do {
            let item = try await first.importScriptSource(musicScriptFixture, fileName: "cold.js")
            installation = item.id
            _ = try await first.setScriptSourceEnabled(item.id, enabled: true)
            try await first.close(); await firstSession.close()
            let status = await firstLauncher.exitStatus(); XCTAssertEqual(status, 0)
        } catch {
            try? await first.close(); await firstSession.close(); throw error
        }
        let launcher = try MusicScriptNodeLauncher(dataRoot: dataRoot)
        let session = RuntimeSession(launcher: launcher)
        let service = NativeMusicScriptService(connection: { try await session.connection() })
        await service.start()
        do {
            let listed = try await service.scriptSources(), sources = try await service.sources()
            XCTAssertEqual(listed.map(\.id), [installation]); XCTAssertEqual(listed.first?.state, .idle)
            let cold = try XCTUnwrap(sources.first)
            XCTAssertEqual(cold.state, .idle); XCTAssertEqual(cold.generation, 0); XCTAssertTrue(cold.isCandidate)
            let stillIdle = try await service.source(installationID: installation)
            XCTAssertEqual(stillIdle, cold)
            let resolved = try await service.resolve(request: .init(source: cold, siteID: "tx", sourceKey: "test",
                quality: "128k", musicInfo: Data("{}".utf8)))
            XCTAssertEqual(resolved.value.url.absoluteString, "https://media.invalid/tx/128k?n=1")
            XCTAssertEqual(resolved.source.state, .ready); XCTAssertGreaterThan(resolved.source.generation, 0)
            XCTAssertEqual(resolved.source.identity, cold.identity)
            let lyrics = try await service.lyrics(request: .init(source: resolved.source, siteID: "tx", sourceKey: "test",
                quality: "", musicInfo: Data("{}".utf8)))
            XCTAssertEqual(lyrics.value.text, "[00:00.00]tx"); XCTAssertEqual(lyrics.source, resolved.source)
            try await service.close(); await session.close()
            let status = await launcher.exitStatus(); XCTAssertEqual(status, 0)
        } catch {
            try? await service.close(); await session.close(); throw error
        }
    }

}
#endif

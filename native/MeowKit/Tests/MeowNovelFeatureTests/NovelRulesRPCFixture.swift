import Foundation
import XCTest
import MeowRuntime
@testable import MeowNovelFeature

/// Native XCTest runs on macOS. Use the public HTTP consumer against the same
/// source module and real Node engine as the product, with an isolated data root.
final class NovelRulesRPCFixture: @unchecked Sendable {
    let rules: NovelRuleProcessor
    private let runtime: NovelRuntime
    private let client: RuntimeHTTPClient
    private let cleanup: @Sendable () async -> Void

    private init(client: RuntimeHTTPClient, cleanup: @escaping @Sendable () async -> Void) {
        self.client = client; self.cleanup = cleanup
        runtime = NovelRuntime(connection: { client })
        rules = NovelRuleProcessor(runtime: runtime)
    }

    static func start() async throws -> NovelRulesRPCFixture {
        #if os(macOS)
        var repository = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { repository.deleteLastPathComponent() }
        let root = repository.appendingPathComponent("tmp/test-results/ios-native/rules-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let process = Process(), input = Pipe(), output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.currentDirectoryURL = repository
        process.arguments = ["node", "--input-type=module", "--eval", """
            import { randomUUID } from 'node:crypto';
            import { createAppRuntime } from './apps/runtime/bootstrap.mjs';
            import { createSourceRuntimeModule } from './apps/runtime/modules/source-runtime.mjs';
            const runtime = createAppRuntime({sessionId:randomUUID(),
              modules:[createSourceRuntimeModule({dataRoot:process.argv[1]})]});
            await runtime.start();
            process.stdout.write(runtime.baseUrl + '\\n');
            process.stdin.resume();
            await new Promise(resolve => process.stdin.once('end', resolve));
            await runtime.close();
            """, root.path]
        process.standardInput = input; process.standardOutput = output
        process.standardError = FileHandle.standardError
        var launched = false
        do {
            try process.run()
            launched = true
            var lines = output.fileHandleForReading.bytes.lines.makeAsyncIterator()
            guard let line = try await lines.next(), let url = URL(string: line) else { throw NovelError.invalidResponse }
            let client = try RuntimeHTTPClient(baseURL: url)
            return NovelRulesRPCFixture(client: client) {
                try? input.fileHandleForWriting.close()
                await Task.detached { process.waitUntilExit() }.value
                XCTAssertEqual(process.terminationStatus, 0, "The real source owner must drain cleanly")
                try? FileManager.default.removeItem(at: root)
            }
        } catch {
            try? input.fileHandleForWriting.close()
            if process.isRunning { process.terminate() }
            if launched { await Task.detached { process.waitUntilExit() }.value }
            try? FileManager.default.removeItem(at: root)
            throw error
        }
        #else
        throw NSError(domain: "NovelRulesRPCFixture requires the macOS native XCTest executor", code: 1)
        #endif
    }

    func close() async {
        await rules.close()
        try? await runtime.close()
        await client.close()
        await cleanup()
    }
}

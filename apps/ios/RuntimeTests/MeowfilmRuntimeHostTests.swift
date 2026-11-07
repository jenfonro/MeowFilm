import XCTest
import UIKit
import MeowMusicFeature
import MeowMusicNativeService
import MeowJSON
import MeowRuntime
@testable import Meowfilm

/// Runs inside the real Meowfilm App process (TEST_HOST): the App's embedded
/// Node Mobile framework, its signed runtime payload and the native script owner.
final class MeowfilmRuntimeHostTests: XCTestCase {
    private static let script = """
    /**
     * @name Runtime host fixture
     * @version 3
     */
    lx.on(lx.EVENT_NAMES.request, async ({ source, action, info }, context) => {
      const count = ((await context.storage.get('count')) || 0) + 1;
      await context.storage.set('count', count);
      if (action === 'lyric') return { lyric: '[00:00.00]' + source, tlyric: '', rlyric: '' };
      return 'http://media.invalid/' + source + '/' + info.type + '?n=' + count;
    });
    lx.send(lx.EVENT_NAMES.inited, { sources: {
      tx: { name: 'Fixture', type: 'music', actions: ['musicUrl', 'lyric'], qualitys: ['128k', '320k'] },
    } });
    """

    func testAppBundleEmbedsTheSignedRuntimeAssembly() throws {
        let root = try XCTUnwrap(Bundle.main.resourceURL).appendingPathComponent("Runtime", isDirectory: true)
        let payload = try RuntimePayload(root: root)
        XCTAssertEqual(payload.entry.lastPathComponent, "aggregate.mjs")
        let data = try Data(contentsOf: root.appendingPathComponent("runtime-manifest.json"))
        let manifest = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(manifest["assemblyProfile"] as? String, "ios-embedded")
        XCTAssertEqual(manifest["modules"] as? [String], ["music", "cat"])
        let files = try XCTUnwrap(manifest["files"] as? [[String: Any]])
        XCTAssertFalse(files.contains { ($0["path"] as? String)?.lowercased().hasSuffix(".wasm") == true })
        let frameworks = try XCTUnwrap(Bundle.main.privateFrameworksURL)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: frameworks.appendingPathComponent("NodeMobile.framework/NodeMobile").path))
        XCTAssertNotNil(Bundle.main.url(forResource: "nodejs-mobile-LICENSE", withExtension: "txt"))
        let nativeLicense = try XCTUnwrap(Bundle.main.url(forResource: "quickjs-native-MIT", withExtension: "txt"))
        XCTAssertTrue(try String(contentsOf: nativeLicense, encoding: .utf8).contains("Fabrice Bellard"))
        let unicodeLicense = try XCTUnwrap(Bundle.main.url(forResource: "Unicode-3.0", withExtension: "txt"))
        XCTAssertTrue(try String(contentsOf: unicodeLicense, encoding: .utf8).contains("1991-2026 Unicode, Inc."))
    }

    @MainActor
    func testEmbeddedNodeServesAnLXScriptThroughTheNativeOwner() async throws {
        let resources = try await hostResources()
        let service = resources.musicScripts
        let connection = try await resources.scriptSession.connection()
        let description = try await connection.call("runtime.describe")
        guard case .object(let fields) = description, case .array(let modules)? = fields["modules"] else {
            return XCTFail("Missing runtime module description")
        }
        XCTAssertTrue(modules.contains {
            guard case .object(let module) = $0 else { return false }
            return module["id"] == .string("music") && module["version"] == .string("0.8.0")
        })

        let imported = try await service.importScriptSource(Data(Self.script.utf8), fileName: "fixture.js")
        addTeardownBlock {
            if try await service.scriptSources().contains(where: { $0.id == imported.id }) {
                try await service.removeScriptSource(imported.id)
            }
        }
        XCTAssertEqual(imported.name, "Runtime host fixture")
        XCTAssertEqual(imported.version, "3")
        XCTAssertEqual(imported.state, .disabled)
        let enabled = try await service.setScriptSourceEnabled(imported.id, enabled: true)
        XCTAssertEqual(enabled.state, .ready)
        XCTAssertEqual(enabled.platforms, [.qq])

        let sources = try await service.sources()
        let source = try XCTUnwrap(sources.first { $0.installationID == imported.id })
        XCTAssertEqual(source.installationID, imported.id)
        XCTAssertEqual(source.capabilities.map(\.siteID), ["tx"])
        let request = MusicScriptRequest(installationID: source.installationID, generation: source.generation,
            siteID: "tx", sourceKey: "fixture", quality: "320k",
            musicInfo: Data(#"{"source":"tx","songmid":"fixture"}"#.utf8))
        // Request-bound storage persists in the shared module between calls.
        let first = try await service.resolve(request: request)
        XCTAssertEqual(first.url.absoluteString, "https://media.invalid/tx/320k?n=1")
        let second = try await service.resolve(request: request)
        XCTAssertEqual(second.url.absoluteString, "https://media.invalid/tx/320k?n=2")
        let lyrics = try await service.lyrics(request: request)
        XCTAssertEqual(lyrics.text, "[00:00.00]tx")
        let disabled = try await service.setScriptSourceEnabled(imported.id, enabled: false)
        XCTAssertEqual(disabled.state, .disabled)
        do {
            _ = try await service.resolve(request: request)
            XCTFail("A disabled script must not execute")
        } catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        try await service.removeScriptSource(imported.id)
        let remaining = try await service.scriptSources()
        XCTAssertFalse(remaining.contains { $0.id == imported.id })
        let shared = try await resources.scriptSession.connection()
        XCTAssertTrue(connection === shared)
        guard case .object(let status) = try await shared.call("cat.script.status", .object([:])) else {
            return XCTFail("Missing Cat status")
        }
        XCTAssertEqual(status["ok"], .bool(true))
        XCTAssertEqual(status["contract"], .string("cat.script.v1"))
    }
}

/// Run in its own xcodebuild invocation: App shutdown is terminal for this process.
@MainActor
final class MeowfilmScriptShutdownHostTests: XCTestCase {
    func testMusicCloseLeavesCatAvailableUntilAppShutdown() async throws {
        guard ProcessInfo.processInfo.environment["MEOWFILM_SCRIPT_SHUTDOWN"] == "1" else {
            throw XCTSkip("Run this terminal App lifecycle scenario in its own process")
        }
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let connection = try await resources.scriptSession.connection()
        _ = try await resources.musicScripts.scriptSources()
        try await resources.musicScripts.close()
        try await resources.musicScripts.close()
        XCTAssertFalse(connection.isClosed)
        guard case .object(let status) = try await connection.call("cat.script.status", .object([:])) else {
            return XCTFail("Missing Cat status after music shutdown")
        }
        XCTAssertEqual(status["ok"], .bool(true))
        XCTAssertEqual(status["contract"], .string("cat.script.v1"))
        assembly.shutdown()
        await assembly.waitForShutdown()
        XCTAssertNil(assembly.shutdownError)
        XCTAssertTrue(connection.isClosed)
        do {
            _ = try await resources.scriptSession.connection()
            XCTFail("App shutdown must close the shared session")
        } catch { XCTAssertEqual(error as? RuntimeBridgeError, .closed) }
    }
}

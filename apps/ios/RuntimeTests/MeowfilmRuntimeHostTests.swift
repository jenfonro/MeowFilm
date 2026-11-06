import XCTest
import MeowMusicFeature
import MeowMusicNativeService
import MeowRuntime
import MeowStorage
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
    }

    func testEmbeddedNodeServesAnLXScriptThroughTheNativeOwner() async throws {
        let service = MeowfilmScriptRuntime.musicScripts
        await service.start() // Idempotent with App assembly; never a second Node host.

        let imported = try await service.importScriptSource(Data(Self.script.utf8), fileName: "fixture.js")
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
    }
}

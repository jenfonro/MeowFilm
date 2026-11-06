import XCTest
import UIKit
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

    @MainActor
    func testEmbeddedNodeServesAnLXScriptThroughTheNativeOwner() async throws {
        let resources = try await hostResources()
        let service = resources.musicScripts
        let directory = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask,
            appropriateFor: nil, create: false).appendingPathComponent("meowfilm/documents")

        let imported = try await service.importScriptSource(Data(Self.script.utf8), fileName: "fixture.js")
        addTeardownBlock {
            if try await service.scriptSources().contains(where: { $0.id == imported.id }) {
                try await service.removeScriptSource(imported.id)
            }
        }
        XCTAssertEqual(imported.name, "Runtime host fixture")
        XCTAssertEqual(imported.version, "3")
        XCTAssertEqual(imported.state, .disabled)
        let documents = directory.appendingPathComponent(NativeMusicScriptService.namespace)
        let catalog = try XCTUnwrap(JSONSerialization.jsonObject(
            with: Data(contentsOf: documents.appendingPathComponent("catalog.json"))) as? [String: Any])
        let installations = try XCTUnwrap(catalog["installations"] as? [[String: Any]])
        XCTAssertTrue(installations.contains { $0["id"] as? String == imported.id })
        let sourceFile = try XCTUnwrap(FileManager.default.contentsOfDirectory(at: documents,
            includingPropertiesForKeys: nil).first {
                $0.lastPathComponent.hasPrefix("source.") && (try? Data(contentsOf: $0)) == Data(Self.script.utf8)
            })
        XCTAssertEqual(try Data(contentsOf: sourceFile), Data(Self.script.utf8))
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
        // Request-bound storage persists through the native owner between calls.
        let first = try await service.resolve(request: request)
        XCTAssertEqual(first.url.absoluteString, "https://media.invalid/tx/320k?n=1")
        let second = try await service.resolve(request: request)
        XCTAssertEqual(second.url.absoluteString, "https://media.invalid/tx/320k?n=2")
        let lyrics = try await service.lyrics(request: request)
        XCTAssertEqual(lyrics.text, "[00:00.00]tx")
        let storageFile = documents.appendingPathComponent("storage." + imported.id + ".json")
        let storage = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: storageFile)) as? [String: Any])
        let sites = try XCTUnwrap(storage["sites"] as? [String: [[String: Any]]])
        XCTAssertEqual(sites["tx"]?.first?["key"] as? String, "count")
        XCTAssertEqual(sites["tx"]?.first?["value"] as? Int, 3)

        let disabled = try await service.setScriptSourceEnabled(imported.id, enabled: false)
        XCTAssertEqual(disabled.state, .disabled)
        do {
            _ = try await service.resolve(request: request)
            XCTFail("A disabled script must not execute")
        } catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        try await service.removeScriptSource(imported.id)
        let remaining = try await service.scriptSources()
        XCTAssertFalse(remaining.contains { $0.id == imported.id })
        XCTAssertFalse(FileManager.default.fileExists(atPath: storageFile.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: sourceFile.path))

    }
}

import XCTest
import UIKit
import MeowMusicFeature
import MeowMusicNativeService
import MeowFilmFeature
import MeowStorage
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
        XCTAssertEqual(manifest["assemblyProfile"] as? String, "common-native")
        XCTAssertEqual(manifest["transport"] as? String, "runtime.http.v1")
        XCTAssertEqual(manifest["modules"] as? [String], ["music", "cat", "source"])
        let files = try XCTUnwrap(manifest["files"] as? [[String: Any]])
        XCTAssertFalse(files.contains { ($0["path"] as? String)?.lowercased().hasSuffix(".wasm") == true })
        let frameworks = try XCTUnwrap(Bundle.main.privateFrameworksURL)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: frameworks.appendingPathComponent("NodeMobile.framework/NodeMobile").path))
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: frameworks.appendingPathComponent("MeowScriptHost.framework/MeowScriptHost").path))
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
        let description = try await connection.describe()
        guard case .object(let fields) = description, case .array(let modules)? = fields["modules"] else {
            return XCTFail("Missing runtime module description")
        }
        XCTAssertEqual(fields["protocol"], .string("runtime.http.v1"))
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
        let request = MusicScriptRequest(source: source, siteID: "tx", sourceKey: "fixture", quality: "320k",
            musicInfo: Data(#"{"source":"tx","songmid":"fixture"}"#.utf8))
        // Request-bound storage persists in the shared module between calls.
        let first = try await service.resolve(request: request)
        XCTAssertEqual(first.value.url.absoluteString, "https://media.invalid/tx/320k?n=1")
        XCTAssertEqual(first.source.identity, source.identity)
        XCTAssertEqual(first.source.generation, source.generation)
        let second = try await service.resolve(request: request)
        XCTAssertEqual(second.value.url.absoluteString, "https://media.invalid/tx/320k?n=2")
        XCTAssertEqual(second.source.generation, first.source.generation)
        let lyrics = try await service.lyrics(request: request)
        XCTAssertEqual(lyrics.value.text, "[00:00.00]tx")
        XCTAssertEqual(lyrics.source.generation, first.source.generation)
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
        guard case .object(let status) = try await shared.call(module: .cat, operation: "status") else {
            return XCTFail("Missing Cat status")
        }
        guard case .array? = status["instances"] else { return XCTFail("Missing direct Cat status value") }
    }

    @MainActor
    func testNativeSourceAndClientRulesCoexistInTheSameApp() async throws {
        let resources = try await hostResources()
        let client = try await resources.scriptSession.connection()
        let sourceID = "ios-source-" + UUID().uuidString.lowercased()
        let source = #"""
        export default {
          async searchContent(keyword) {
            await Promise.resolve();
            return {
              list: [{ vod_id: keyword, vod_name: keyword }],
              digest: CryptoJS.MD5('abc').toString(),
              '\u00e9': 'composed', 'e\u0301': 'decomposed',
              token: 'business-token', blob: { value: 'business-value' }
            };
          }
        };
        """#
        _ = try await client.upload(module: .source, installationID: sourceID,
            sourceRevision: 1, source: Data(source.utf8), basename: "source.js")
        let loaded = try await client.call(module: .source, operation: "load", parameters: .object([
            "installationId": .string(sourceID), "sourceRevision": .number(1),
            "revision": .number(1), "expectedRevision": .number(0), "protocol": .string("cat")
        ]))
        guard case .object(let load) = loaded, case .object(let instance)? = load["instance"],
              case .number(let generation)? = instance["generation"] else {
            _ = try? await client.call(module: .source, operation: "unload",
                parameters: .object(["installationId": .string(sourceID), "revision": .number(1)]))
            return XCTFail("Missing native source identity")
        }
        addTeardownBlock {
            _ = try await client.call(module: .source, operation: "unload", parameters: .object([
                "installationId": .string(sourceID), "revision": .number(1), "generation": .number(generation)
            ]))
        }

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let rules = try FilmRuleService(store: DocumentStore(baseURL: directory,
            namespace: FilmRuleService.documentNamespace))
        do {
            try await rules.initialize()
            // Both locked QuickJS versions execute concurrently inside the same
            // signed App; successful linking alone cannot prove symbol ownership.
            async let rule = rules.process(.init(section: .movie, text: "电影"),
                draft: .init(movieRules: [.init(pattern: "电影", replace: "影片", flags: "u")]))
            async let script = client.call(module: .source, operation: "invoke", parameters: .object([
                "installationId": .string(sourceID), "revision": .number(1), "generation": .number(generation),
                "action": .string("search"), "payload": .object(["keyword": .string("书源中文")])
            ]))
            let (ruleResult, sourceResult) = try await (rule, script)
            XCTAssertEqual(ruleResult.outputText.displayString, "影片")
            guard case .object(let value) = sourceResult else {
                await rules.close()
                return XCTFail("Source result is not an object")
            }
            XCTAssertEqual(value["digest"], .string("900150983cd24fb0d6963f7d28e17f72"))
            XCTAssertEqual(value["\u{00E9}"], .string("composed"))
            XCTAssertEqual(value["e\u{0301}"], .string("decomposed"))
            XCTAssertEqual(value["token"], .string("business-token"))
            XCTAssertEqual(value["blob"], .object(["value": .string("business-value")]))
            XCTAssertEqual(value["list"], .array([.object([
                "vod_id": .string("书源中文"), "vod_name": .string("书源中文")
            ])]))
            await rules.close()
        } catch {
            await rules.close()
            throw error
        }
    }

    @MainActor
    func testEmbeddedReaderReturnsNormalizedBooksWithoutAnotherHost() async throws {
        let resources = try await hostResources()
        let client = try await resources.scriptSession.connection()
        let id = "ios-reader-" + UUID().uuidString.lowercased()
        let definition = RuntimeValue.object([
            "bookSourceName": .string("Reader fixture"),
            "bookSourceUrl": .string("https://books.invalid/"),
            "html": .string("""
            <script>
              async function search(keyword, page) {
                await Promise.resolve();
                return [{name: keyword, author: 'Fixture Author', bookUrl: '/book/' + page}];
              }
            </script>
            """)
        ])
        _ = try await client.upload(module: .source, installationID: id, sourceRevision: 1,
            source: RuntimeJSON.encode(definition), basename: "reader.json")
        let loaded = try await client.call(module: .source, operation: "load", parameters: .object([
            "installationId": .string(id), "sourceRevision": .number(1),
            "revision": .number(1), "expectedRevision": .number(0), "protocol": .string("qysg")
        ]))
        addTeardownBlock {
            _ = try await client.call(module: .source, operation: "unload",
                parameters: .object(["installationId": .string(id), "revision": .number(1)]))
        }
        guard case .object(let root) = loaded, case .object(let instance)? = root["instance"],
              case .number(let generation)? = instance["generation"] else {
            return XCTFail("Missing Reader instance")
        }
        let result = try await client.call(module: .source, operation: "invoke", parameters: .object([
            "installationId": .string(id), "revision": .number(1), "generation": .number(generation),
            "action": .string("search"),
            "payload": .object(["keyword": .string("测试书籍"), "page": .number(2)])
        ]))
        guard case .object(let value) = result, case .array(let books)? = value["books"],
              books.count == 1, case .object(let book) = books[0] else {
            return XCTFail("Reader did not return one normalized book")
        }
        XCTAssertEqual(value["page"], .number(2))
        XCTAssertEqual(book["name"], .string("测试书籍"))
        XCTAssertEqual(book["author"], .string("Fixture Author"))
        XCTAssertEqual(book["bookUrl"], .string("https://books.invalid/book/2"))
        XCTAssertEqual(book["tocUrl"], book["bookUrl"])
        XCTAssertEqual(book["originName"], .string("Reader fixture"))
        let shared = try await resources.scriptSession.connection()
        XCTAssertTrue(client === shared)
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
        guard case .object(let status) = try await connection.call(module: .cat, operation: "status") else {
            return XCTFail("Missing Cat status after music shutdown")
        }
        guard case .array? = status["instances"] else { return XCTFail("Missing direct Cat status value") }
        assembly.shutdown()
        await assembly.waitForShutdown()
        XCTAssertNil(assembly.shutdownError)
        XCTAssertTrue(connection.isClosed)
        do {
            _ = try await resources.scriptSession.connection()
            XCTFail("App shutdown must close the shared session")
        } catch { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
    }
}
